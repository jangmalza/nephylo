using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // Unmodified official Gemini CLI, ACP over private stdio. Google credentials stay
    // in the CLI-owned ADE profile. No authenticate request or auth-file reads here.
    internal sealed class StudioGeminiCliClient : IStudioAgentClient,IStudioReplayClient
    {
        const int MaxLine=1024*1024, MaxOutput=4*1024*1024, MaxAnswer=512000;
        readonly object gate=new object(), writer=new object();
        readonly Dictionary<int,TaskCompletionSource<Dictionary<string,object>>> pending=new Dictionary<int,TaskCompletionSource<Dictionary<string,object>>>();
        readonly Dictionary<string,string> loaded=new Dictionary<string,string>(StringComparer.Ordinal);
        readonly string connectionId=Guid.NewGuid().ToString("N");
        readonly Func<string,string,bool,ProcessStartInfo> fixtureLauncher;
        readonly int timeoutMs;
        string entry,home,work,node,acpFlag,childModel,activeSession,probeSession;
        Process child;Task outputTask,errorTask;bool disposed,connected,busy,ending,authProbe,stopping;
        int nextId,answerLength;Task<Dictionary<string,object>> turn;
        StudioAccountIdentity identity=StudioAccountIdentity.FromGeminiCliStatus(false,null);
        List<StudioModel> models=Aliases();
        string preparedReplayId,replayId;List<StudioMessage> preparedReplay,replayMessages;
        int replayIndex,replayOffset,outputLength;bool replayInvalid;
        TaskCompletionSource<bool> replayDone;
        public string Provider {get{return "gemini-cli";}}
        public string Profile {get{return "ade-gemini-cli";}}
        public string ConnectionId {get{return connectionId;}}
        public bool Connected {get{lock(gate)return connected&&!disposed;}}
        public StudioAccountIdentity AccountIdentity {get{lock(gate)return identity;}}
        public event Action<string,string> Notice;
        public event Action<string,string,string> Message;
        internal StudioGeminiCliClient():this(null,60000){}
        // Isolated process harness only; production never uses an environment override.
        internal StudioGeminiCliClient(Func<string,string,bool,ProcessStartInfo> launcher,int timeout)
        {fixtureLauncher=launcher;timeoutMs=timeout;}
        static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=MaxLine,RecursionLimit=48};}
        static string Str(Dictionary<string,object> obj,string key){return StudioCodexClient.Str(obj,key);}
        static Dictionary<string,object> Obj(Dictionary<string,object> obj,string key){return StudioCodexClient.Obj(obj,key);}
        static object Value(Dictionary<string,object> obj,string key){object value;return obj!=null&&obj.TryGetValue(key,out value)?value:null;}
        static List<StudioModel> Aliases(){return new List<StudioModel>{new StudioModel{Id="pro",Label="Gemini Pro (CLI alias)"},new StudioModel{Id="flash",Label="Gemini Flash (CLI alias)"},new StudioModel{Id="flash-lite",Label="Gemini Flash Lite (CLI alias)"}};}
        internal static string FindExecutable()
        {
            var candidates=new List<string>{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"npm","node_modules","@google","gemini-cli","dist","index.js")};
            foreach(var path in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))try{if(Path.IsPathRooted(path.Trim('"')))candidates.Add(Path.Combine(path.Trim('"'),"node_modules","@google","gemini-cli","dist","index.js"));}catch{}
            foreach(var candidate in candidates)try{if(File.Exists(candidate)){ValidatePackage(candidate);return Path.GetFullPath(candidate);}}catch(StudioFailure){}
            throw new StudioFailure(StudioFailureKind.Installation);
        }
        static string FindNode()
        {
            var candidates=new List<string>{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"nodejs","node.exe")};
            foreach(var path in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))try{if(Path.IsPathRooted(path.Trim('"')))candidates.Add(Path.Combine(path.Trim('"'),"node.exe"));}catch{}
            foreach(var path in candidates)if(File.Exists(path))return Path.GetFullPath(path);
            throw new StudioFailure(StudioFailureKind.Installation);
        }
        static string ValidatePackage(string path)
        {
            if(String.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path)||!File.Exists(path)||Path.GetFileName(path)!="index.js"||Path.GetFileName(Path.GetDirectoryName(path))!="dist")throw new StudioFailure(StudioFailureKind.Installation);
            try{
                string root=Path.GetDirectoryName(Path.GetDirectoryName(path));
                var package=Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,"package.json")));
                Version version;if(Str(package,"name")!="@google/gemini-cli"||!Version.TryParse((Str(package,"version")??"").Split('-')[0],out version)||version<new Version(0,31,0))throw new StudioFailure(StudioFailureKind.Compatibility);
                var config=File.ReadAllText(Path.Combine(root,"dist","src","config","config.js"));
                var schema=File.ReadAllText(Path.Combine(root,"dist","src","config","settingsSchema.js"));
                foreach(var setting in new[]{"coreTools:","enableHooks:","extensionsEnabled","mcpEnabled","adminSkillsEnabled"})if(!config.Contains(setting))throw new StudioFailure(StudioFailureKind.Compatibility);
                foreach(var setting in new[]{"hooksConfig:","disableYoloMode:","overrides:","fileName:"})if(!schema.Contains(setting))throw new StudioFailure(StudioFailureKind.Compatibility);
                if(config.Contains(".option('acp'"))return "--acp";
                if(config.Contains(".option('experimental-acp'"))return "--experimental-acp";
                throw new StudioFailure(StudioFailureKind.Compatibility);
            }catch(StudioFailure){throw;}catch{throw new StudioFailure(StudioFailureKind.Compatibility);}
        }
        internal static string PrepareHome(string directory,bool interactive=false)
        {
            try{
                directory=Path.GetFullPath(directory);
                for(string path=directory;!String.IsNullOrEmpty(path);path=Path.GetDirectoryName(path))if(Directory.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException();
                Directory.CreateDirectory(directory);string workspace=Path.Combine(directory,"workspace");Directory.CreateDirectory(workspace);
                foreach(var path in new[]{workspace,Path.Combine(workspace,".gemini"),Path.Combine(directory,".gemini"),Path.Combine(directory,".gemini","settings.json"),Path.Combine(workspace,".gemini","settings.json"),Path.Combine(directory,"system-settings.json"),Path.Combine(directory,"system-defaults.json"),Path.Combine(workspace,".env"),Path.Combine(workspace,".gemini",".env"),Path.Combine(directory,"login-requested")})if((File.Exists(path)||Directory.Exists(path))&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException();
                Directory.CreateDirectory(Path.Combine(workspace,".gemini"));
                ValidateProfileSettings(Path.Combine(directory,".gemini","settings.json"));
                ValidateProfileSettings(Path.Combine(workspace,".gemini","settings.json"));
                foreach(var path in new[]{Path.Combine(workspace,".env"),Path.Combine(workspace,".gemini",".env")})if(File.Exists(path)&&new FileInfo(path).Length>0)throw new StudioFailure(StudioFailureKind.Compatibility);
                // System settings have the highest precedence. This profile is dedicated to
                // text conversations; its empty .env stops inherited parent .env discovery.
                bool requested=interactive||File.Exists(Path.Combine(directory,"login-requested"));
                string contextFile="nephylo-no-context-"+Guid.NewGuid().ToString("N")+".md";
                for(string ancestor=workspace;!String.IsNullOrEmpty(ancestor);ancestor=Path.GetDirectoryName(ancestor))if(File.Exists(Path.Combine(ancestor,contextFile)))throw new StudioFailure(StudioFailureKind.Storage);
                if(File.Exists(Path.Combine(directory,".gemini",contextFile)))throw new StudioFailure(StudioFailureKind.Storage);
                var settings=SafeSettings.Replace("NEPHYLO_CONTEXT_SENTINEL",contextFile);
                File.WriteAllText(Path.Combine(directory,"system-settings.json"),requested?settings.Replace("\"selectedType\":null","\"selectedType\":\"oauth-personal\""):settings,new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(directory,"system-defaults.json"),"{}",new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(workspace,".env"),"",new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(workspace,".gemini",".env"),"",new UTF8Encoding(false));
                return directory;
            }catch(StudioFailure){throw;}catch{throw new StudioFailure(StudioFailureKind.Storage);}
        }
        static void ValidateProfileSettings(string path)
        {
            if(!File.Exists(path))return;
            try{if(new FileInfo(path).Length>65536)throw new InvalidDataException();var settings=Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));if(settings==null||!AllowedSetting(settings,""))throw new InvalidDataException();}
            catch{throw new StudioFailure(StudioFailureKind.Compatibility);}
        }
        static bool AllowedSetting(Dictionary<string,object> settings,string prefix)
        {
            foreach(var pair in settings){string name=prefix+pair.Key;var nested=pair.Value as Dictionary<string,object>;if(nested!=null){if(name!="security"&&name!="security.auth"&&name!="ui")return false;if(!AllowedSetting(nested,name+"."))return false;}
                else if(name=="ui.theme"){var theme=pair.Value as string;if(theme==null||theme.Length>80||theme.Any(Char.IsControl))return false;}
                else if(name!="security.auth.selectedType"||pair.Value!=null&&!Object.Equals(pair.Value,"oauth-personal"))return false;}
            return true;
        }
        internal const string SafeSettings="{\"general\":{\"enableAutoUpdate\":false,\"enableAutoUpdateNotification\":false,\"checkpointing\":{\"enabled\":false}},\"security\":{\"disableYoloMode\":true,\"auth\":{\"selectedType\":null,\"useExternal\":true}},\"tools\":{\"core\":[],\"allowed\":[],\"discoveryCommand\":null},\"hooksConfig\":{\"enabled\":false},\"hooks\":{},\"skills\":{\"enabled\":false},\"ide\":{\"enabled\":false},\"mcp\":{\"serverCommand\":null},\"mcpServers\":{},\"context\":{\"fileName\":\"NEPHYLO_CONTEXT_SENTINEL\",\"includeDirectories\":[],\"includeDirectoryTree\":false,\"loadMemoryFromIncludeDirectories\":false},\"agents\":{\"overrides\":{\"codebase_investigator\":{\"enabled\":false},\"cli_help\":{\"enabled\":false}}},\"experimental\":{\"enableAgents\":false,\"jitContext\":false,\"plan\":false},\"telemetry\":{\"enabled\":false,\"logPrompts\":false}}";
        static string[] GuardArguments(){return new[]{"--approval-mode","default","--extensions","none","--allowed-mcp-server-names","nephylo-deny-"+Guid.NewGuid().ToString("N")};}
        static ProcessStartInfo StartInfo(string entryPath,string nodePath,string directory,string[] arguments,bool interactive)
        {
            var info=new ProcessStartInfo{FileName=nodePath,Arguments=String.Join(" ",new[]{entryPath}.Concat(arguments).Select(Quote)),WorkingDirectory=Path.Combine(directory,"workspace"),UseShellExecute=false,CreateNoWindow=!interactive,WindowStyle=interactive?ProcessWindowStyle.Normal:ProcessWindowStyle.Hidden,RedirectStandardInput=!interactive,RedirectStandardOutput=!interactive,RedirectStandardError=!interactive};
            if(!interactive){info.StandardOutputEncoding=Encoding.UTF8;info.StandardErrorEncoding=Encoding.UTF8;}
            var removed=new HashSet<string>(new[]{"NO_BROWSER","DEBUG","HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","NO_PROXY","CI","GITHUB_ACTIONS"},StringComparer.OrdinalIgnoreCase);
            foreach(string key in info.EnvironmentVariables.Keys.Cast<string>().ToArray())if(key.StartsWith("GEMINI",StringComparison.OrdinalIgnoreCase)||key.StartsWith("GOOGLE",StringComparison.OrdinalIgnoreCase)||key.StartsWith("GCLOUD",StringComparison.OrdinalIgnoreCase)||key.StartsWith("CLOUD_SHELL",StringComparison.OrdinalIgnoreCase)||key.StartsWith("NODE_",StringComparison.OrdinalIgnoreCase)||key.StartsWith("OAUTH_CALLBACK_",StringComparison.OrdinalIgnoreCase)||removed.Contains(key))info.EnvironmentVariables.Remove(key);
            info.EnvironmentVariables["GEMINI_CLI_HOME"]=directory;
            info.EnvironmentVariables["GEMINI_CLI_NO_RELAUNCH"]="true";
            info.EnvironmentVariables["GEMINI_CLI_SYSTEM_SETTINGS_PATH"]=Path.Combine(directory,"system-settings.json");
            info.EnvironmentVariables["GEMINI_CLI_SYSTEM_DEFAULTS_PATH"]=Path.Combine(directory,"system-defaults.json");
            return info;
        }
        internal static ProcessStartInfo CreateLoginStartInfo(string directory)
        {
            string entryPath=FindExecutable(),nodePath=FindNode();directory=PrepareHome(directory,true);
            // This permits a later auth probe only. It NEVER means login succeeded.
            File.WriteAllText(Path.Combine(directory,"login-requested"),"1",new UTF8Encoding(false));
            return StartInfo(entryPath,nodePath,directory,GuardArguments(),true);
        }
        internal static string Quote(string value)
        {
            if(value==null)value="";var result=new StringBuilder("\"");int slashes=0;
            foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"')result.Append('\\',slashes*2+1).Append(c);else result.Append('\\',slashes).Append(c);slashes=0;}
            return result.Append('\\',slashes*2).Append('"').ToString();
        }
        public async Task ConnectAsync(string executable,string directory)
        {
            lock(gate){if(disposed)throw new ObjectDisposedException("StudioGeminiCliClient");if(entry!=null)throw new InvalidOperationException();}
            acpFlag=fixtureLauncher==null?ValidatePackage(executable):"--experimental-acp";
            entry=executable;node=fixtureLauncher==null?FindNode():null;home=PrepareHome(directory);work=Path.Combine(home,"workspace");
            authProbe=File.Exists(Path.Combine(home,"login-requested"));
            try{await BeginChild("pro");lock(gate)connected=true;}finally{authProbe=false;}
        }
        async Task BeginChild(string model)
        {
            StopChild();
            lock(gate){if(disposed)throw new ObjectDisposedException("StudioGeminiCliClient");ending=false;}
            var info=fixtureLauncher==null?StartInfo(entry,node,home,new[]{acpFlag,"--model",model}.Concat(GuardArguments()).ToArray(),false):fixtureLauncher(home,model,false);
            var process=new Process{StartInfo=info};try{if(!process.Start())throw new IOException();}catch{process.Dispose();throw new StudioFailure(StudioFailureKind.Installation);}
            lock(gate){child=process;childModel=model;loaded.Clear();probeSession=null;outputLength=0;}
            outputTask=Task.Run(()=>ReadOutput(process));errorTask=Task.Run(()=>Drain(process));
            try{
                var initialized=await Request("initialize",new{protocolVersion=1,clientCapabilities=new{fs=new{readTextFile=false,writeTextFile=false},terminal=false},clientInfo=new{name="nephylo-ade",version="1"}},timeoutMs);
                object version;if(!initialized.TryGetValue("protocolVersion",out version)||Convert.ToString(version)!="1")throw new StudioFailure(StudioFailureKind.Compatibility);
                if(!Object.Equals(Value(Obj(initialized,"agentCapabilities"),"loadSession"),true))throw new StudioFailure(StudioFailureKind.Compatibility);
            }catch{StopChild();throw;}
        }
        public async Task<Dictionary<string,object>> AccountAsync()
        {
            RequireConnected();
            if(!File.Exists(Path.Combine(home,"login-requested"))){SetIdentity(false);return AccountResult();}
            lock(gate){if(busy)return AccountResult();authProbe=true;}
            try{
                if(child==null||child.HasExited)await BeginChild("pro");
                if(probeSession==null){var result=await Request("session/new",new{cwd=work,mcpServers=new object[0]},timeoutMs);probeSession=ValidateSession(Str(result,"sessionId"));ValidateMode(result);AcceptModels(result);loaded[probeSession]=childModel;}
                SetIdentity(true);
            }catch(StudioFailure failure){if(failure.Kind!=StudioFailureKind.Authentication)throw;SetIdentity(false);}
            finally{lock(gate)authProbe=false;}
            return AccountResult();
        }
        Dictionary<string,object> AccountResult(){return new Dictionary<string,object>{{"account",identity.IsAuthenticated?(object)new Dictionary<string,object>{{"type",identity.Type},{"accountId",identity.Fingerprint}}:null}};}
        void SetIdentity(bool authenticated){bool changed;lock(gate){changed=identity.IsAuthenticated!=authenticated;identity=StudioAccountIdentity.FromGeminiCliStatus(authenticated,null);}if(changed){var h=Notice;if(h!=null)h("account","changed");}}
        public Task<List<StudioModel>> ModelsAsync(){RequireConnected();return Task.FromResult(models.Select(m=>new StudioModel{Id=m.Id,Label=m.Label}).ToList());}
        public Task<StudioQuotaSnapshot> QuotaAsync(){return Task.FromResult(StudioQuotaSnapshot.Unknown(connectionId,AccountIdentity));}
        void AcceptModels(Dictionary<string,object> response)
        {
            var catalogue=Obj(response,"models");if(catalogue==null)return;var found=new List<StudioModel>();
            foreach(var row in StudioCodexClient.Items(catalogue,"availableModels")){var obj=row as Dictionary<string,object>;var id=Str(obj,"modelId");if(!ValidModel(id))throw new StudioFailure(StudioFailureKind.Compatibility);found.Add(new StudioModel{Id=id,Label=StudioQuotaParser.SafeText(Str(obj,"name"),120)??id});}
            if(found.Count==0||found.Count>100)throw new StudioFailure(StudioFailureKind.Compatibility);models=found;
        }
        static bool ValidModel(string model){return model!=null&&Regex.IsMatch(model,@"^[a-zA-Z0-9][a-zA-Z0-9._/-]{0,127}$")&&model!="auto";}
        static string ValidateSession(string value){Guid id;if(!Guid.TryParseExact(value,"D",out id))throw new StudioFailure(StudioFailureKind.Context);return value;}
        static void ValidateMode(Dictionary<string,object> result){var modes=Obj(result,"modes");if(modes==null||Str(modes,"currentModeId")!="default")throw new StudioFailure(StudioFailureKind.Permission);}
        public void PrepareReplay(string threadId,IList<StudioMessage> messages)
        {
            ValidateSession(threadId);if(messages==null||messages.Count>400)throw new StudioFailure(StudioFailureKind.Context);
            var copy=new List<StudioMessage>();int length=0;foreach(var message in messages){if(message==null||message.Role!="user"&&message.Role!="assistant"||message.Text==null)throw new StudioFailure(StudioFailureKind.Context);length+=message.Text.Length;if(length>2*1024*1024)throw new StudioFailure(StudioFailureKind.Context);if(!String.IsNullOrWhiteSpace(message.Text))copy.Add(new StudioMessage{Role=message.Role,Text=message.Text});}
            lock(gate){if(busy)throw new InvalidOperationException();preparedReplayId=threadId;preparedReplay=copy;}
        }
        public async Task<string> ThreadAsync(string id,string project,string model)
        {
            RequireConnected();if(String.IsNullOrWhiteSpace(project)||!Directory.Exists(project))throw new StudioFailure(StudioFailureKind.Project);
            if(!ValidModel(model)||!models.Any(m=>m.Id==model))throw new StudioFailure(StudioFailureKind.Model);
            if(!identity.IsAuthenticated)throw new StudioFailure(StudioFailureKind.Authentication);
            lock(gate)if(busy)throw new InvalidOperationException();
            if(childModel!=model||child==null||child.HasExited)await BeginChild(model);
            if(!String.IsNullOrEmpty(id)){ValidateSession(id);string bound;if(loaded.TryGetValue(id,out bound)){if(bound!=model)throw new StudioFailure(StudioFailureKind.Model);return id;}}
            else if(probeSession!=null&&childModel==model){id=probeSession;probeSession=null;return id;}
            bool resume=!String.IsNullOrEmpty(id);
            if(resume){lock(gate){if(preparedReplayId!=id||preparedReplay==null)throw new StudioFailure(StudioFailureKind.Context);replayId=id;replayMessages=preparedReplay;replayIndex=0;replayOffset=0;replayInvalid=false;replayDone=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);if(replayMessages.Count==0)replayDone.TrySetResult(true);}}
            var result=await Request(resume?"session/load":"session/new",resume?(object)new{sessionId=id,cwd=work,mcpServers=new object[0]}:new{cwd=work,mcpServers=new object[0]},timeoutMs);
            if(resume){var replay=replayDone.Task;if(await Task.WhenAny(replay,Task.Delay(timeoutMs))!=replay){replayInvalid=true;throw new StudioFailure(StudioFailureKind.Context);}await replay;}
            ValidateMode(result);AcceptModels(result);if(String.IsNullOrEmpty(id))id=ValidateSession(Str(result,"sessionId"));else if(Str(result,"sessionId")!=null&&Str(result,"sessionId")!=id)throw new StudioFailure(StudioFailureKind.Context);
            loaded[id]=model;return id;
        }
        public async Task<string> RunBoundAsync(string threadId,string project,string model,string prompt,string expectedAccountFingerprint)
        {
            RequireConnected();ValidateSession(threadId);if(String.IsNullOrWhiteSpace(project)||!Directory.Exists(project))throw new StudioFailure(StudioFailureKind.Project);
            if(String.IsNullOrWhiteSpace(prompt)||prompt.Length>32000)throw new StudioFailure(StudioFailureKind.Context);
            string bound;if(!loaded.TryGetValue(threadId,out bound)||bound!=model||model!=childModel)throw new StudioFailure(StudioFailureKind.Model);
            lock(gate){if(busy)throw new InvalidOperationException();if(replayInvalid)throw new StudioFailure(StudioFailureKind.Context);if(!identity.IsAuthenticated||identity.Fingerprint!=expectedAccountFingerprint)throw new StudioFailure(StudioFailureKind.Authentication);busy=true;stopping=false;activeSession=threadId;answerLength=0;outputLength=0;replayId=null;replayMessages=null;replayDone=null;}
            try{
                // Text blocks only: ACP resource blocks can cause local file expansion.
                lock(gate){if(stopping)throw new StudioFailure(StudioFailureKind.Disconnected);turn=Request("session/prompt",new{sessionId=threadId,prompt=new[]{new{type="text",text=prompt}}},15*60*1000);}
                var result=await turn;string reason=Str(result,"stopReason");
                if(reason=="end_turn")return "completed";
                if(reason=="cancelled")return "interrupted";
                if(reason=="max_tokens"||reason=="max_turn_requests")throw new StudioFailure(StudioFailureKind.Context);
                if(reason=="refusal")throw new StudioFailure(StudioFailureKind.Permission);
                throw new StudioFailure(StudioFailureKind.Disconnected);
            }finally{lock(gate){busy=false;activeSession=null;turn=null;}}
        }
        public async Task InterruptAsync()
        {
            Task<Dictionary<string,object>> current;string session;lock(gate){stopping=true;current=turn;session=activeSession;}if(current==null)return;
            try{await WriteAsync(new{jsonrpc="2.0",method="session/cancel",@params=new{sessionId=session}});}catch{Fail(new StudioFailure(StudioFailureKind.Disconnected));}
            if(await Task.WhenAny(current,Task.Delay(Math.Min(3000,timeoutMs)))!=current){Fail(new StudioFailure(StudioFailureKind.Timeout));StopChild();}
            await current;
        }
        async Task<Dictionary<string,object>> Request(string method,object parameters,int timeout)
        {
            int id;TaskCompletionSource<Dictionary<string,object>> waiter=new TaskCompletionSource<Dictionary<string,object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock(gate){if(disposed||child==null)throw new StudioFailure(StudioFailureKind.Disconnected);id=++nextId;pending.Add(id,waiter);}
            try{await WriteAsync(new{jsonrpc="2.0",id,method,@params=parameters});if(await Task.WhenAny(waiter.Task,Task.Delay(timeout))!=waiter.Task){Fail(new StudioFailure(StudioFailureKind.Timeout));StopChild();}return await waiter.Task;}
            finally{lock(gate)pending.Remove(id);if(waiter.Task.IsFaulted){var ignored=waiter.Task.Exception;GC.KeepAlive(ignored);}}
        }
        void Write(object value,Process owner){string text=Json().Serialize(value);if(text.Length>MaxLine)throw new StudioFailure(StudioFailureKind.Context);lock(writer){Process process;lock(gate)process=child;if(process==null||process!=owner)throw new StudioFailure(StudioFailureKind.Disconnected);var bytes=Encoding.UTF8.GetBytes(text+"\n");process.StandardInput.BaseStream.Write(bytes,0,bytes.Length);process.StandardInput.BaseStream.Flush();}}
        async Task WriteAsync(object value,Process owner=null)
        {
            lock(gate)if(owner==null)owner=child;
            var task=Task.Run(()=>Write(value,owner));
            if(await Task.WhenAny(task,Task.Delay(timeoutMs))!=task){Fail(new StudioFailure(StudioFailureKind.Timeout),owner);StopChild(owner);var observer=task.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);GC.KeepAlive(observer);throw new StudioFailure(StudioFailureKind.Timeout);}
            try{await task;}catch(StudioFailure){throw;}catch{throw new StudioFailure(StudioFailureKind.Disconnected);}
        }
        void ReadOutput(Process process)
        {
            try{while(true){var line=ReadLine(process.StandardOutput);if(line==null)break;lock(gate){outputLength+=line.Length;if(outputLength>MaxOutput)throw new StudioFailure(StudioFailureKind.Compatibility);}if(String.IsNullOrWhiteSpace(line))continue;Dictionary<string,object> message;try{message=Json().Deserialize<Dictionary<string,object>>(line);}catch{throw new StudioFailure(authProbe?StudioFailureKind.Authentication:StudioFailureKind.Compatibility);}if(message==null||Str(message,"jsonrpc")!="2.0")throw new StudioFailure(StudioFailureKind.Compatibility);Handle(message,process);}}
            catch(StudioFailure error){Fail(error,process);}catch{Fail(new StudioFailure(StudioFailureKind.Disconnected),process);}
            finally{bool own;lock(gate)own=child==process&&!ending;if(own)Fail(new StudioFailure(StudioFailureKind.Disconnected),process);}
        }
        string ReadLine(StreamReader reader){var value=new StringBuilder();bool started=false;int next;while((next=reader.Read())>=0){if(next=='\n')return value.ToString();if(!started&&!Char.IsWhiteSpace((char)next)){if(next!='{')throw new StudioFailure(authProbe?StudioFailureKind.Authentication:StudioFailureKind.Compatibility);started=true;}if(value.Length>=MaxLine)throw new StudioFailure(StudioFailureKind.Compatibility);if(next!='\r')value.Append((char)next);}return value.Length==0?null:value.ToString();}
        void Handle(Dictionary<string,object> message,Process process)
        {
            lock(gate)if(child!=process||ending)return;
            object rawId;string method=Str(message,"method");
            if(method!=null){
                if(message.TryGetValue("id",out rawId)){
                    // No filesystem, terminal or permission RPC may execute in ADE.
                    if(method=="session/request_permission")WriteAsync(new{jsonrpc="2.0",id=rawId,result=new{outcome=new{outcome="cancelled"}}},process).GetAwaiter().GetResult();
                    else WriteAsync(new{jsonrpc="2.0",id=rawId,error=new{code=-32601,message="Unsupported client capability"}},process).GetAwaiter().GetResult();
                    return;
                }
                if(method!="session/update")return;var data=Obj(message,"params");var update=Obj(data,"update");string session=Str(data,"sessionId");
                string kind=Str(update,"sessionUpdate");
                lock(gate){if(session==replayId&&replayMessages!=null){ReplayUpdate(kind,Obj(update,"content"));return;}if(!busy||session!=activeSession)return;}
                if(kind=="user_message_chunk")throw new StudioFailure(StudioFailureKind.Context);
                if(kind=="tool_call"||kind=="tool_call_update")throw new StudioFailure(StudioFailureKind.Permission);
                if(kind!="agent_message_chunk")return;var content=Obj(update,"content");if(Str(content,"type")!="text")throw new StudioFailure(StudioFailureKind.Compatibility);string text=Str(content,"text");if(text==null)throw new StudioFailure(StudioFailureKind.Compatibility);
                lock(gate){answerLength+=text.Length;if(answerLength>MaxAnswer)throw new StudioFailure(StudioFailureKind.Compatibility);}var h=Message;if(h!=null)h("answer",text,Boolean.FalseString);return;
            }
            if(!message.TryGetValue("id",out rawId))throw new StudioFailure(StudioFailureKind.Compatibility);int id;if(!Int32.TryParse(Convert.ToString(rawId),out id))throw new StudioFailure(StudioFailureKind.Compatibility);
            TaskCompletionSource<Dictionary<string,object>> waiter;lock(gate){if(!pending.TryGetValue(id,out waiter))return;}
            var error=Obj(message,"error");if(error!=null){string code=Convert.ToString(Value(error,"code"));var failure=code=="401"||code=="-32000"?new StudioFailure(StudioFailureKind.Authentication):code=="429"?new StudioFailure(StudioFailureKind.Quota):StudioFailure.FromServer(error);waiter.TrySetException(failure);return;}
            var result=Obj(message,"result");if(result==null){waiter.TrySetException(new StudioFailure(StudioFailureKind.Compatibility));return;}waiter.TrySetResult(result);
        }
        void ReplayUpdate(string kind,Dictionary<string,object> content)
        {
            if(kind=="tool_call"||kind=="tool_call_update")throw new StudioFailure(StudioFailureKind.Permission);
            if(kind!="user_message_chunk"&&kind!="agent_message_chunk")return;
            string role=kind=="user_message_chunk"?"user":"assistant",text=Str(content,"text");
            if(replayIndex>=replayMessages.Count||Str(content,"type")!="text"||text==null||replayMessages[replayIndex].Role!=role||!replayMessages[replayIndex].Text.Substring(replayOffset).StartsWith(text,StringComparison.Ordinal)){
                replayInvalid=true;replayDone.TrySetException(new StudioFailure(StudioFailureKind.Context));throw new StudioFailure(StudioFailureKind.Context);
            }
            replayOffset+=text.Length;if(replayOffset==replayMessages[replayIndex].Text.Length){replayIndex++;replayOffset=0;}if(replayIndex==replayMessages.Count)replayDone.TrySetResult(true);
        }
        void Drain(Process process){try{char[] buffer=new char[1024];int total=0,count;while((count=process.StandardError.Read(buffer,0,buffer.Length))>0){total+=count;if(total>MaxOutput){Fail(new StudioFailure(StudioFailureKind.Compatibility),process);break;}}}catch{}}
        void RequireConnected(){if(!Connected)throw new StudioFailure(StudioFailureKind.Disconnected);}
        void Fail(StudioFailure failure,Process owner=null)
        {
            TaskCompletionSource<Dictionary<string,object>>[] requests;Process process;lock(gate){if(owner!=null&&(child!=owner||ending))return;if(busy&&failure.Kind!=StudioFailureKind.Timeout&&failure.Kind!=StudioFailureKind.Disconnected)failure=new StudioFailure(StudioFailureKind.Disconnected);requests=pending.Values.ToArray();process=child;}
            foreach(var request in requests)request.TrySetException(failure);lock(gate)if(replayDone!=null)replayDone.TrySetException(failure);
            try{if(process!=null&&!process.HasExited)process.Kill();}catch{}
        }
        void StopChild(Process owner=null)
        {
            Process process;Task output,error;lock(gate){if(owner!=null&&child!=owner)return;ending=true;process=child;child=null;output=outputTask;error=errorTask;outputTask=null;errorTask=null;loaded.Clear();probeSession=null;}
            // Kill the owned process before touching a possibly blocked stdin writer.
            // GEMINI_CLI_NO_RELAUNCH keeps the official ACP runtime in this exact process.
            if(process!=null){try{if(!process.HasExited)process.Kill();}catch{}try{process.WaitForExit(1000);}catch{}try{process.StandardInput.Close();}catch{}}
            foreach(var task in new[]{output,error})if(task!=null)try{task.Wait(1000);}catch{}
            if(process!=null)process.Dispose();
        }
        public void Dispose(){lock(gate){if(disposed)return;disposed=true;connected=false;}Fail(new StudioFailure(StudioFailureKind.Disconnected));StopChild();}
    }
}
