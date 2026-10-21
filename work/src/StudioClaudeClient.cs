using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // Official native CLI only. Auth belongs to Claude Code; ADE never opens its credential files.
    // Each turn uses its exact session ID in a sterile directory, with tools/customizations disabled.
    internal sealed partial class StudioClaudeClient : IStudioAgentClient
    {
        public string Provider { get { return "claude"; } }
        public string Profile { get { return "ade-claude-code"; } }
        const int MaxLine=1024*1024, MaxOutput=4*1024*1024, MaxAnswer=512000;
        readonly object gate=new object(), writer=new object();
        readonly HashSet<Process> probes=new HashSet<Process>();
        readonly HashSet<string> freshSessions=new HashSet<string>(StringComparer.Ordinal);
        readonly string connectionId=Guid.NewGuid().ToString("N");
        string executable, home;
        bool disposed, connected, busy, stopping;
        Process child;
        StudioAccountIdentity identity;
        TaskCompletionSource<string> completion;
        TaskCompletionSource<Dictionary<string,object>> initialization;
        string initializeId, activeSession;
        bool sawInit, sentPrompt, sawResult;
        int answerLength;
        string lastFinalText;
        public event Action<string,string> Notice;
        public event Action<string,string,string> Message;
        public bool Connected { get { lock(gate)return connected&&!disposed; } }
        public string ConnectionId { get { return connectionId; } }
        public StudioAccountIdentity AccountIdentity { get { lock(gate)return identity; } }
        static string Str(Dictionary<string,object> value,string key) { return StudioCodexClient.Str(value,key); }
        static Dictionary<string,object> Obj(Dictionary<string,object> value,string key) { return StudioCodexClient.Obj(value,key); }
        static bool IsTrue(Dictionary<string,object> value,string key) { object v;return value!=null&&value.TryGetValue(key,out v)&&Object.Equals(v,true); }
        void Tell(string kind,string value) { var h=Notice;if(h!=null)h(kind,value); }
        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength=MaxLine, RecursionLimit=64 }; }
        internal static string FindExecutable()
        {
            var candidates=new List<string>{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".local","bin","claude.exe")};
            foreach(var part in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))if(!String.IsNullOrWhiteSpace(part)){try{if(Path.IsPathRooted(part.Trim('"')))candidates.Add(Path.Combine(part.Trim('"'),"claude.exe"));}catch{}}
            foreach(var path in candidates){try{if(File.Exists(path))return Path.GetFullPath(path);}catch{}}
            throw new StudioFailure(StudioFailureKind.Installation);
        }

        public async Task ConnectAsync(string path,string directory)
        {
            if(String.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path)||!String.Equals(Path.GetExtension(path),".exe",StringComparison.OrdinalIgnoreCase)||!File.Exists(path))throw new StudioFailure(StudioFailureKind.Installation);
            try {
                directory=Path.GetFullPath(directory);
                for(string p=directory;!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
                    if(Directory.Exists(p)&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException();
                Directory.CreateDirectory(directory);
            } catch { throw new StudioFailure(StudioFailureKind.Storage); }
            lock(gate){if(disposed)throw new ObjectDisposedException("StudioClaudeClient");if(executable!=null)throw new InvalidOperationException();executable=Path.GetFullPath(path);home=directory;}
            var version=await Probe(new[]{"--version"});
            Match match=Regex.Match(version.Text??"",@"^(\d+)\.(\d+)\.(\d+)");
            Version parsed;
            if(version.ExitCode!=0||!match.Success||!Version.TryParse(match.Value,out parsed)||parsed<new Version(2,1,259))throw new StudioFailure(StudioFailureKind.Compatibility);
            var help=await Probe(new[]{"--help"});
            foreach(var flag in new[]{"--safe-mode","--restricted","--tools","--permission-mode","--permission-prompts","--strict-mcp-config","--setting-sources","--settings","--disable-slash-commands","--no-chrome","--input-format","--output-format","--include-partial-messages","--session-id","--resume"})
                if(help.ExitCode!=0||help.Text==null||!help.Text.Contains(flag))throw new StudioFailure(StudioFailureKind.Compatibility);
            lock(gate){if(disposed)throw new ObjectDisposedException("StudioClaudeClient");usageSupported=parsed>=new Version(2,1,304)&&help.Text.Contains("--no-session-persistence");connected=true;}
        }
        public async Task<Dictionary<string,object>> AccountAsync()
        {
            RequireConnected();
            var result=await Probe(new[]{"--safe-mode","--setting-sources","","auth","status","--json"});
            Dictionary<string,object> data;
            try{data=Json().DeserializeObject(result.Text) as Dictionary<string,object>;}catch{throw new StudioFailure(StudioFailureKind.Compatibility);}
            if(data==null)throw new StudioFailure(StudioFailureKind.Compatibility);
            if(result.ExitCode!=0&&IsTrue(data,"loggedIn"))throw new StudioFailure(StudioFailureKind.Authentication);
            var next=StudioAccountIdentity.FromClaudeStatus(data);
            bool changed;
            lock(gate){if(disposed)throw new ObjectDisposedException("StudioClaudeClient");changed=identity!=null&&(identity.Fingerprint!=next.Fingerprint||identity.Type!=next.Type||identity.IsAuthenticated!=next.IsAuthenticated||identity.EmailFingerprint!=next.EmailFingerprint);if(identity==null||changed){accountRevision++;quota=null;}identity=next;}
            // No raw email/account identifier leaves this adapter.
            var safe=new Dictionary<string,object>();
            safe["account"]=next.IsAuthenticated?(object)new Dictionary<string,object>{{"type",next.Type},{"accountId",next.Fingerprint}}:null;
            if(changed)Tell("account","changed");
            return safe;
        }
        public Task<List<StudioModel>> ModelsAsync()
        {
            RequireConnected();
            // CLI aliases are explicit requests, not an assertion of account entitlement.
            return Task.FromResult(new List<StudioModel>{new StudioModel{Id="sonnet",Label="Claude Sonnet"},new StudioModel{Id="opus",Label="Claude Opus"},new StudioModel{Id="haiku",Label="Claude Haiku"}});
        }
        internal Task<string> LoginAsync() { throw new StudioFailure(StudioFailureKind.Authentication); }
        public Task<string> ThreadAsync(string id,string project,string model)
        {
            RequireConnected();ValidateProject(project);ValidateModel(model);
            if(String.IsNullOrEmpty(id)){id=Guid.NewGuid().ToString("D");lock(gate)freshSessions.Add(id);}
            else ValidateSession(id);
            return Task.FromResult(id);
        }
        internal Task<string> RunAsync(string thread,string project,string model,string prompt)
        { return RunBoundAsync(thread,project,model,prompt,AccountIdentity==null?null:AccountIdentity.Fingerprint); }
        public async Task<string> RunBoundAsync(string thread,string project,string model,string prompt,string expectedAccountFingerprint)
        {
            RequireConnected();ValidateSession(thread);ValidateProject(project);ValidateModel(model);
            if(String.IsNullOrWhiteSpace(prompt)||prompt.Length>32000)throw new StudioFailure(StudioFailureKind.Context);
            TaskCompletionSource<string> done;string expectedType;
            lock(gate){if(busy)throw new InvalidOperationException();ValidateIdentity(expectedAccountFingerprint,null);expectedType=identity.Type;busy=true;stopping=false;sentPrompt=false;sawInit=false;sawResult=false;answerLength=0;lastFinalText=null;activeSession=thread;completion=done=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);}
            Process owned=null;Task outputTask=null,errorTask=null;
            try {
                await AccountAsync();
                lock(gate){ValidateIdentity(expectedAccountFingerprint,expectedType);if(stopping)throw new StudioFailure(StudioFailureKind.Disconnected);}
                bool fresh;lock(gate)fresh=freshSessions.Contains(thread);
                var args=BuildArguments(thread,model,fresh);
                owned=Start(args);lock(gate){if(disposed){KillOwned(owned);throw new ObjectDisposedException("StudioClaudeClient");}child=owned;initializeId=Guid.NewGuid().ToString("N");initialization=new TaskCompletionSource<Dictionary<string,object>>(TaskCreationOptions.RunContinuationsAsynchronously);}
                var stdout=owned.StandardOutput;var stderr=owned.StandardError;
                outputTask=Task.Run(()=>ReadOutput(stdout,owned,done));
                errorTask=Task.Run(()=>Drain(stderr));
                await WriteAsync(new{type="control_request",request_id=initializeId,request=new{subtype="initialize",hooks=new{},skills=new string[0]}},owned);
                Task<Dictionary<string,object>> init;lock(gate)init=initialization.Task;
                if(await Task.WhenAny(init,done.Task,Task.Delay(15000))!=init){if(done.Task.IsCompleted)return await done.Task;throw new StudioFailure(StudioFailureKind.Timeout);}
                var initialized=await init;
                if(Str(initialized,"current_permission_mode")!="dontAsk")throw new StudioFailure(StudioFailureKind.Permission);
                lock(gate){ValidateIdentity(expectedAccountFingerprint,expectedType);if(stopping)throw new StudioFailure(StudioFailureKind.Disconnected);sentPrompt=true;freshSessions.Remove(thread);}
                // client_composed prevents @file/slash-command expansion by the official host.
                // Once a write is attempted delivery is ambiguous on failure: only resume this ID.
                await WriteAsync(new{type="user",session_id=thread,parent_tool_use_id=(object)null,client_composed=true,message=new{role="user",content=new[]{new{type="text",text=prompt}}}},owned);
                if(await Task.WhenAny(done.Task,Task.Delay(TimeSpan.FromMinutes(15)))!=done.Task)throw new StudioFailure(StudioFailureKind.Timeout);
                return await done.Task;
            } catch(Exception error) {
                var failure=error as StudioFailure??new StudioFailure(error is IOException||error is ObjectDisposedException?StudioFailureKind.Disconnected:StudioFailureKind.Compatibility);
                done.TrySetException(failure);
                // A preflight failure may precede the normal await of the completion task.
                var observed=done.Task.Exception;
                throw failure;
            } finally {
                // Only the process instance created by this adapter is ever stopped.
                StopOwned(owned,outputTask,errorTask);
                lock(gate){if(initialization!=null&&initialization.Task.IsFaulted){var observed=initialization.Task.Exception;}if(child==owned)child=null;accountRevision++;quota=null;busy=false;completion=null;initialization=null;activeSession=null;}
            }
        }
        public async Task InterruptAsync()
        {
            Process owned;TaskCompletionSource<string> pending;
            lock(gate){stopping=true;owned=child;pending=completion;}
            if(pending==null)return;
            var waiting=pending.Task;
            if(owned!=null){try{await WriteAsync(new{type="control_request",request_id=Guid.NewGuid().ToString("N"),request=new{subtype="interrupt"}},owned);}catch{}}
            if(await Task.WhenAny(waiting,Task.Delay(3000))!=waiting){pending.TrySetException(new StudioFailure(StudioFailureKind.Timeout));KillOwned(owned);}
            await waiting;
        }
        void RequireConnected(){if(!Connected)throw new StudioFailure(StudioFailureKind.Disconnected);}
        void ValidateIdentity(string expected,string expectedType)
        {
            if(disposed)throw new ObjectDisposedException("StudioClaudeClient");
            if(identity==null||!identity.IsAuthenticated||expected!=identity.Fingerprint||String.IsNullOrEmpty(expected)&&identity.Known||expectedType!=null&&expectedType!=identity.Type)throw new StudioFailure(StudioFailureKind.Authentication);
            // A CLI can confirm authentication without exposing an account identifier. Such an
            // owned connection stays unknown; it never receives an invented fingerprint.
        }
        static void ValidateSession(string id){Guid guid;if(!Guid.TryParseExact(id,"D",out guid))throw new StudioFailure(StudioFailureKind.Context);}
        static void ValidateProject(string path){if(String.IsNullOrWhiteSpace(path)||!Directory.Exists(path))throw new StudioFailure(StudioFailureKind.Project);}
        static void ValidateModel(string model){if(model!="sonnet"&&model!="opus"&&model!="haiku")throw new StudioFailure(StudioFailureKind.Model);}
        internal static string[] BuildArguments(string session,string model,bool fresh)
        {
            ValidateSession(session);ValidateModel(model);
            return new[]{"--print","--input-format","stream-json","--output-format","stream-json","--verbose","--include-partial-messages","--safe-mode","--restricted","--tools","","--disallowedTools","*","--permission-mode","dontAsk","--permission-prompts","none","--strict-mcp-config","--mcp-config","{\"mcpServers\":{}}","--setting-sources","","--settings","{\"disableAllHooks\":true,\"autoMemoryEnabled\":false}","--disable-slash-commands","--no-chrome","--model",model,fresh?"--session-id":"--resume",session};
        }
        internal static string Quote(string value)
        {
            // CommandLineToArgvW quoting. Always quote, including empty arguments.
            if(value==null)value="";var result=new StringBuilder("\"");int slashes=0;
            foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='\"'){result.Append('\\',slashes*2+1);result.Append(c);}else{result.Append('\\',slashes);result.Append(c);}slashes=0;}
            result.Append('\\',slashes*2);return result.Append('"').ToString();
        }
        Process Start(IEnumerable<string> arguments)
        {
            var info=new ProcessStartInfo(executable,String.Join(" ",arguments.Select(Quote))) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=home };
            // Do not inherit another host's SDK transport or automatically enabled integrations.
            foreach(var name in new[]{"CLAUDE_CODE_SDK_URL","CLAUDE_CODE_SESSION_ID","CLAUDE_CODE_ENTRYPOINT","CLAUDE_CODE_SYNC_PLUGIN_INSTALL","CLAUDE_CODE_REMOTE","CLAUDE_CODE_REMOTE_ENVIRONMENT_TYPE","CLAUDE_CODE_USE_BEDROCK","CLAUDE_CODE_USE_VERTEX","CLAUDE_CODE_USE_FOUNDRY","ANTHROPIC_BASE_URL"})info.EnvironmentVariables.Remove(name);
            info.EnvironmentVariables["CLAUDE_CODE_SAFE_MODE"]="1";
            try{var process=Process.Start(info);process.StandardInput.AutoFlush=true;return process;}catch{throw new StudioFailure(StudioFailureKind.Installation);}
        }
        sealed class ProbeResult { internal int ExitCode;internal string Text; }
        sealed class ProbeText { internal string Text;internal StudioFailure Failure; }
        Task<ProbeResult> Probe(string[] arguments)
        {
            return Task.Run(()=>{
                Process process=null;Task<ProbeText> output=null;Task error=null;
                try{
                    lock(gate){if(disposed)throw new ObjectDisposedException("StudioClaudeClient");process=Start(arguments);probes.Add(process);}
                    process.StandardInput.Close();var stdout=process.StandardOutput;var stderr=process.StandardError;
                    output=Task.Run(()=>{try{return new ProbeText{Text=ReadBounded(stdout,MaxLine)};}catch(Exception failure){return new ProbeText{Failure=failure as StudioFailure??new StudioFailure(StudioFailureKind.Compatibility)};}});
                    error=Task.Run(()=>Drain(stderr));
                    if(!process.WaitForExit(15000)){KillOwned(process);throw new StudioFailure(StudioFailureKind.Timeout);}
                    if(!Task.WaitAll(new Task[]{output,error},3000))throw new StudioFailure(StudioFailureKind.Timeout);
                    if(output.Result.Failure!=null)throw output.Result.Failure;
                    return new ProbeResult{ExitCode=process.ExitCode,Text=output.Result.Text};
                }catch(AggregateException){throw new StudioFailure(StudioFailureKind.Compatibility);}
                finally{if(process!=null){StopOwned(process,output,error);lock(gate)probes.Remove(process);}}
            });
        }
        static string ReadBounded(StreamReader reader,int limit)
        {
            var text=new StringBuilder();var buffer=new char[4096];int n;
            while((n=reader.Read(buffer,0,buffer.Length))>0){if(text.Length+n>limit)throw new StudioFailure(StudioFailureKind.Compatibility);text.Append(buffer,0,n);}return text.ToString();
        }
        static void Drain(StreamReader reader){try{var buffer=new char[2048];while(reader.Read(buffer,0,buffer.Length)>0){}}catch{}}
        void Write(object value,Process owned)
        {
            var bytes=new UTF8Encoding(false).GetBytes(Json().Serialize(value)+"\n");
            lock(writer){owned.StandardInput.BaseStream.Write(bytes,0,bytes.Length);owned.StandardInput.BaseStream.Flush();}
        }
        async Task WriteAsync(object value,Process owned)
        {
            // Anonymous pipe writes can block when a child stops reading. Keep the UI and
            // cancellation path off that write, bound it, and observe late write failures.
            var writing=Task.Run(()=>{try{Write(value,owned);return (StudioFailure)null;}catch{return new StudioFailure(StudioFailureKind.Disconnected);}});
            if(await Task.WhenAny(writing,Task.Delay(3000))!=writing){KillOwned(owned);throw new StudioFailure(StudioFailureKind.Timeout);}
            var failure=await writing;if(failure!=null)throw failure;
        }
        void ReadOutput(StreamReader stdout,Process owned,TaskCompletionSource<string> done)
        {
            try {
                // StreamReader.Read(char[], count) can wait to fill the caller's
                // buffer even after a complete JSON line has arrived on a live
                // pipe. The child may be waiting for our next request meanwhile.
                // Read from StreamReader's own buffer one character at a time,
                // dispatch at newline, and retain the pre-allocation limits.
                var line=new StringBuilder();int next,total=0;
                while((next=stdout.Read())>=0){
                    if(++total>MaxOutput)throw new StudioFailure(StudioFailureKind.Context);
                    if(next=='\n'){if(line.Length>0)Receive(Json().DeserializeObject(line.ToString()) as Dictionary<string,object>,owned,done);line.Clear();}
                    else if(next!='\r'){if(line.Length>=MaxLine)throw new StudioFailure(StudioFailureKind.Compatibility);line.Append((char)next);}
                }
                if(line.Length>0)Receive(Json().DeserializeObject(line.ToString()) as Dictionary<string,object>,owned,done);
                done.TrySetException(new StudioFailure(StudioFailureKind.Disconnected));
            }catch(Exception error){
                var failure=error as StudioFailure??new StudioFailure(error is IOException||error is ObjectDisposedException?StudioFailureKind.Disconnected:StudioFailureKind.Compatibility);
                // Without a terminal provider result, post-delivery parse/permission/size
                // failures do not prove that the provider rejected or stopped the request.
                lock(gate){if(!sawResult&&(stopping||sentPrompt))failure=new StudioFailure(StudioFailureKind.Disconnected);}
                done.TrySetException(failure);
            }
        }
        static bool IsEmptyList(Dictionary<string,object> value,string key)
        { object list;return value!=null&&value.TryGetValue(key,out list)&&list is IList&&((IList)list).Count==0; }
        void Receive(Dictionary<string,object> message,Process owned,TaskCompletionSource<string> done)
        {
            if(message==null)throw new StudioFailure(StudioFailureKind.Compatibility);
            lock(gate){if(completion!=done||disposed||sawResult)return;}
            string type=Str(message,"type");
            if(type=="control_response"){
                var response=Obj(message,"response");
                lock(gate){if(initialization!=null&&Str(response,"request_id")==initializeId){if(Str(response,"subtype")=="success")initialization.TrySetResult(Obj(response,"response")??new Dictionary<string,object>());else initialization.TrySetException(new StudioFailure(StudioFailureKind.Compatibility));}}
                return;
            }
            if(type=="control_request"){
                var request=Obj(message,"request");var requestId=Str(message,"request_id");
                if(String.IsNullOrEmpty(requestId))throw new StudioFailure(StudioFailureKind.Compatibility);
                if(Str(request,"subtype")=="can_use_tool")WriteAsync(new{type="control_response",response=new{subtype="success",request_id=requestId,response=new{behavior="deny",message="Tools are disabled in Nephylo ADE chat mode."}}},owned).GetAwaiter().GetResult();
                else WriteAsync(new{type="control_response",response=new{subtype="error",request_id=requestId,error="Unsupported request"}},owned).GetAwaiter().GetResult();
                Tell("denied","tool");return;
            }
            var session=Str(message,"session_id");
            lock(gate){if(session!=null&&session!=activeSession||session==null&&(type=="stream_event"||type=="assistant"||type=="result"))throw new StudioFailure(StudioFailureKind.Compatibility);}
            if(type=="system"&&Str(message,"subtype")=="init"){
                if(session==null||!IsEmptyList(message,"tools")||!IsEmptyList(message,"mcp_servers"))throw new StudioFailure(StudioFailureKind.Permission);
                lock(gate){if(!sentPrompt)throw new StudioFailure(StudioFailureKind.Compatibility);sawInit=true;}Tell("working","answer");return;
            }
            if(type=="system"&&Str(message,"subtype")=="permission_denied"){Tell("denied","tool");return;}
            if(type=="system"&&Str(message,"subtype")=="api_retry"){Tell("working","retry");return;}
            if(type=="stream_event"){
                if(Str(message,"parent_tool_use_id")!=null)return;
                var ev=Obj(message,"event");var delta=Obj(ev,"delta");
                if(Str(Obj(ev,"content_block"),"type")=="tool_use")throw new StudioFailure(StudioFailureKind.Permission);
                if(Str(ev,"type")=="content_block_delta"&&Str(delta,"type")=="text_delta")EmitText(Str(delta,"text")??"",false);
                return;
            }
            if(type=="assistant"){
                if(Str(message,"parent_tool_use_id")!=null)return;
                if(message.ContainsKey("error")){var error=Str(message,"error");if(!String.IsNullOrEmpty(error))throw Classify(error);}
                var blocks=StudioCodexClient.Items(Obj(message,"message"),"content").ToList();
                if(blocks.Any(x=>Str(x,"type")=="tool_use"))throw new StudioFailure(StudioFailureKind.Permission);
                var text=String.Concat(blocks.Where(x=>Str(x,"type")=="text").Select(x=>Str(x,"text")??""));
                if(text.Length>0)EmitText(text,true);return;
            }
            if(type=="result"){
                object isError;var subtype=Str(message,"subtype");
                if(!message.TryGetValue("is_error",out isError)||!(isError is bool)||String.IsNullOrEmpty(subtype))throw new StudioFailure(StudioFailureKind.Compatibility);
                var terminalReason=Str(message,"terminal_reason");
                // The official SDK defines these terminal reasons as cancellation. A
                // concurrent Stop does not turn an ordinary success or error into a cancel.
                lock(gate){if(sawInit&&sentPrompt&&(terminalReason=="aborted_streaming"||terminalReason=="aborted_tools")){sawResult=true;done.TrySetResult("interrupted");return;}}
                if((bool)isError||subtype!="success"){lock(gate)sawResult=true;throw Classify(Json().Serialize(message));}
                lock(gate){if(!sawInit||!sentPrompt)throw new StudioFailure(StudioFailureKind.Compatibility);}
                object resultValue;if(message.TryGetValue("result",out resultValue)&&resultValue!=null&&!(resultValue is string))throw new StudioFailure(StudioFailureKind.Compatibility);
                var final=Str(message,"result");if(final!=null)EmitText(final,true);
                lock(gate){sawResult=true;done.TrySetResult("completed");}return;
            }
        }
        static StudioFailure Classify(string value)
        {
            value=(value??"").ToLowerInvariant();
            if(value.Contains("not logged in")||value.Contains("invalid_api_key")||value.Contains("authentication_failed")||value.Contains("oauth_org_not_allowed"))return new StudioFailure(StudioFailureKind.Authentication);
            if(value.Contains("billing_error")||value.Contains("rate_limit")||value.Contains("usage limit"))return new StudioFailure(StudioFailureKind.Quota);
            if(value.Contains("no conversation found")||value.Contains("session not found"))return new StudioFailure(StudioFailureKind.Context);
            return StudioFailure.FromServer(new Dictionary<string,object>{{"message",value}});
        }
        void EmitText(string value,bool final)
        {
            lock(gate){if(!sawInit||!sentPrompt)throw new StudioFailure(StudioFailureKind.Compatibility);answerLength=final?value.Length:answerLength+value.Length;if(answerLength>MaxAnswer)throw new StudioFailure(StudioFailureKind.Context);if(final&&lastFinalText==value)return;lastFinalText=final?value:null;}
            var h=Message;if(h!=null)h("answer",value,final?Boolean.TrueString:Boolean.FalseString);
        }
        static void KillOwned(Process process){if(process==null)return;try{if(!process.HasExited)process.Kill();}catch{}}
        static void StopOwned(Process process,params Task[] readers)
        {
            if(process==null)return;
            try{process.StandardInput.Close();}catch{}
            KillOwned(process);
            try{Task.WaitAll(readers.Where(t=>t!=null).ToArray(),3000);}catch(AggregateException){}
            process.Dispose();
        }
        public void Dispose()
        {
            Process owned;Process[] active;
            lock(gate){if(disposed)return;disposed=true;connected=false;owned=child;active=probes.ToArray();if(completion!=null)completion.TrySetException(new StudioFailure(StudioFailureKind.Disconnected));if(initialization!=null)initialization.TrySetException(new StudioFailure(StudioFailureKind.Disconnected));}
            KillOwned(owned);foreach(var process in active)KillOwned(process);
        }
    }
}
