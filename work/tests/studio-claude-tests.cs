using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using UsageWidget;

// This executable is also the fake CLI. Normal tests never start the installed CLI,
// contact a model, inspect credentials, or request login. --probe is metadata-only.
internal static class StudioClaudeTests
{
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=2*1024*1024};
    static int checks;
    static string Str(Dictionary<string,object> obj,string key){return StudioCodexClient.Str(obj,key);}
    static Dictionary<string,object> Obj(Dictionary<string,object> obj,string key){return StudioCodexClient.Obj(obj,key);}
    static void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
    static void Emit(object value){Console.WriteLine(Json.Serialize(value));Console.Out.Flush();}
    static void EmitAtomic(object value){var bytes=new UTF8Encoding(false).GetBytes(Json.Serialize(value)+"\n");var output=Console.OpenStandardOutput();output.Write(bytes,0,bytes.Length);output.Flush();}
    static void Log(string file,object value){File.AppendAllText(file,Json.Serialize(value)+"\n",new UTF8Encoding(false));}
    static string Mode(){return File.Exists("mode.txt")?File.ReadAllText("mode.txt"):"normal";}
    static string Option(string[] args,string name){int i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
    static void Result(string session){Emit(new{type="result",subtype="success",is_error=false,session_id=session,result="한글 응답"});}
    static void Init(string session,string mode)
    {
        if(mode=="missing-tools"){Emit(new{type="system",subtype="init",session_id=session});return;}
        if(mode=="malformed-tools"){Emit(new{type="system",subtype="init",session_id=session,tools=false,mcp_servers=new string[0]});return;}
        Emit(new{type="system",subtype="init",session_id=session,tools=mode=="tools"?new[]{"Read"}:new string[0],mcp_servers=mode=="mcp"?new[]{"unexpected"}:new string[0]});
    }
    static void Delta(string session,string value){Emit(new{type="stream_event",session_id=session,parent_tool_use_id=(string)null,@event=new{type="content_block_delta",delta=new{type="text_delta",text=value}}});}
    static void Answer(string session)
    {
        Emit(new{type="stream_event",session_id=session,parent_tool_use_id="unexpected-subagent",@event=new{type="content_block_delta",delta=new{type="text_delta",text="IGNORED_CHILD"}}});
        Emit(new{type="stream_event",session_id=session,@event=new{type="content_block_delta",delta=new{type="thinking_delta",thinking="IGNORED_THINKING"}}});
        Delta(session,"한글 ");Delta(session,"응답");
        Emit(new{type="assistant",session_id=session,parent_tool_use_id=(string)null,message=new{role="assistant",content=new[]{new{type="text",text="한글 응답"}}}});
        Result(session);
    }
    static int Fake(string[] args)
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=new UTF8Encoding(false);
        if(args.Contains("--version")){Console.WriteLine(Mode()=="old"?"2.1.258 (Claude Code)":File.Exists("usage-supported.txt")?"2.1.304 (Claude Code)":"2.1.296 (Claude Code)");return 0;}
        if(args.Contains("--help")){Console.WriteLine(String.Join(" ",StudioClaudeClient.BuildArguments(Guid.NewGuid().ToString("D"),"sonnet",true))+" --resume"+(Mode()=="usage-no-flag"?"":" --no-session-persistence"));return 0;}
        if(args.Contains("auth")){
            if(File.Exists("delay-auth.txt"))Thread.Sleep(500);
            Console.WriteLine(File.ReadAllText("account.json"));return 0;
        }
        if(!args.Contains("--print"))throw new Exception("Unknown fixture invocation");
        Log("argv.jsonl",args);Log("cwd.jsonl",Environment.CurrentDirectory);
        string session=Option(args,"--session-id")??Option(args,"--resume"),mode=Mode();
        if(mode=="missing-resume"){Emit(new{type="result",subtype="error_during_execution",is_error=true,session_id=session,errors=new[]{"No conversation found SECRET_PROMPT"}});return 1;}
        string line;
        while((line=Console.ReadLine())!=null){
            Log("stdin.jsonl",Json.DeserializeObject(line));var message=Json.Deserialize<Dictionary<string,object>>(line);var type=Str(message,"type");
            if(type=="control_request"){
                var request=Obj(message,"request");var subtype=Str(request,"subtype");
                if(subtype=="initialize"){
                    if(mode=="init-disconnect")return 0;
                    if(mode=="large-init")EmitAtomic(new{type="control_response",response=new{subtype="success",request_id=Str(message,"request_id"),response=new{current_permission_mode="dontAsk",metadata=new String('x',6000)}}});
                    else Emit(new{type="control_response",response=new{subtype="success",request_id=Str(message,"request_id"),response=new{current_permission_mode=mode=="permission"?"bypassPermissions":"dontAsk",account=new{email=mode=="usage-child-mismatch"?"different@example.invalid":Str(Json.Deserialize<Dictionary<string,object>>(File.ReadAllText("account.json")),"email"),apiProvider=mode=="usage-bedrock"?"bedrock":"firstParty"},models=new[]{new{value="sonnet"},new{value="opus"},new{value="haiku"}}}}});
                    if(mode=="block-stdin"){Thread.Sleep(15000);return 0;}
                }else if(subtype=="get_usage"){
                    File.WriteAllText("usage-started.txt","");
                    if(mode=="usage-hold"||mode=="usage-timeout"){
                        var until=DateTime.UtcNow.AddSeconds(20);while(!File.Exists("release-usage.txt")&&DateTime.UtcNow<until)Thread.Sleep(10);
                    }
                    if(mode=="usage-malformed"){Console.WriteLine("PRIVATE malformed quota");Console.Out.Flush();continue;}
                    if(mode=="usage-huge"){Console.WriteLine(new String('x',1024*1024+1));Console.Out.Flush();continue;}
                    if(mode=="usage-control"){Emit(new{type="control_request",request_id="unexpected",request=new{subtype="can_use_tool",tool_name="Bash"}});continue;}
                    if(File.Exists("account-after.json"))File.Copy("account-after.json","account.json",true);
                    Emit(new{type="control_response",session_id=mode=="usage-foreign"?Guid.NewGuid().ToString("D"):session,response=new{subtype=mode=="usage-unsupported"?"error":"success",request_id=mode=="usage-wrong-id"?"unrelated":Str(message,"request_id"),response=mode=="usage-bad-body"?(object)false:Json.DeserializeObject(File.ReadAllText("quota.json"))}});
                }else if(subtype=="interrupt"){
                    if(mode=="wait-eof")return 0;
                    if(mode=="wait-malformed"){Console.WriteLine("PRIVATE malformed output");Console.Out.Flush();return 0;}
                    if(mode=="wait-completed"){Answer(session);continue;}
                    if(mode=="wait-quota"){Emit(new{type="result",subtype="success",is_error=true,session_id=session,result="billing_error SECRET_TOKEN"});continue;}
                    if(mode!="wait-timeout")Emit(new{type="result",subtype="error_during_execution",is_error=true,session_id=session,terminal_reason="aborted_streaming",errors=new[]{"Interrupted by user"}});
                }
                continue;
            }
            if(type=="control_response"){
                if(mode=="deny"){Log("deny.jsonl",message);mode="unsupported";Emit(new{type="control_request",request_id="unsupported-request",request=new{subtype="unknown"}});}
                else {Log("unsupported.jsonl",message);Answer(session);}
                continue;
            }
            if(type!="user")continue;
            if(mode=="drop-before-init")return 0;
            Init(session,mode);
            if(mode=="tools"||mode=="mcp")continue;
            if(mode.StartsWith("wait",StringComparison.Ordinal))continue;
            if(mode=="long-stream"){
                var text="시작 "+new String('x',6000)+" 끝";
                EmitAtomic(new{type="stream_event",session_id=session,parent_tool_use_id=(string)null,@event=new{type="content_block_delta",delta=new{type="text_delta",text=text}}});
                var until=DateTime.UtcNow.AddSeconds(10);while(!File.Exists("release.txt")&&DateTime.UtcNow<until)Thread.Sleep(10);
                Emit(new{type="result",subtype="success",is_error=false,session_id=session,result=text});continue;
            }
            if(mode=="deny"){Emit(new{type="control_request",request_id="tool-request",request=new{subtype="can_use_tool",tool_name="Bash",input=new{command="SECRET_DO_NOT_EXECUTE"}}});continue;}
            if(mode=="foreign-session"){Delta(Guid.NewGuid().ToString("D"),"PRIVATE_FOREIGN");continue;}
            if(mode=="missing-session"){Emit(new{type="result",subtype="success",is_error=false,result="PRIVATE_RESULT"});continue;}
            if(mode=="missing-session-delta"){Emit(new{type="stream_event",@event=new{type="content_block_delta",delta=new{type="text_delta",text="PRIVATE_RESULT"}}});continue;}
            if(mode=="malformed-result"){Emit(new{type="result",subtype="success",session_id=session,result="PRIVATE_RESULT"});continue;}
            if(mode=="malformed-result-text"){Emit(new{type="result",subtype="success",session_id=session,is_error=false,result=42});continue;}
            if(mode=="tool-stream"){Emit(new{type="stream_event",session_id=session,@event=new{type="content_block_start",content_block=new{type="tool_use",name="Read"}}});continue;}
            if(mode=="tool-assistant"){Emit(new{type="assistant",session_id=session,message=new{content=new[]{new{type="tool_use",name="Read"}}}});continue;}
            if(mode=="malformed"){Console.WriteLine("PRIVATE malformed output");Console.Out.Flush();continue;}
            if(mode=="huge-line"){Console.WriteLine(new String('x',1024*1024+1));Console.Out.Flush();continue;}
            if(mode=="huge-answer"){Delta(session,new String('x',512001));continue;}
            if(mode=="huge-output"){for(int i=0;i<9;i++)Emit(new{type="ignored",payload=new String('x',500000)});continue;}
            if(mode.StartsWith("error:",StringComparison.Ordinal)){Emit(new{type="result",subtype="success",is_error=true,session_id=session,result=mode.Substring(6)+" SECRET_PROMPT PRIVATE_TOKEN"});continue;}
            if(mode=="duplicate-result"){Answer(session);Result(session);continue;}
            Answer(session);
        }
        return 0;
    }
    sealed class Fixture:IDisposable
    {
        internal readonly string Home,Project;internal readonly StudioClaudeClient Client=new StudioClaudeClient();
        internal Fixture(string root,string name){Home=Path.Combine(root,name);Project=Path.Combine(root,"project");Directory.CreateDirectory(Home);Directory.CreateDirectory(Project);Account("oauth",true,"fixture@example.invalid");Mode("normal");}
        internal void Account(string method,bool logged,string email){File.WriteAllText(Path.Combine(Home,"account.json"),Json.Serialize(new{loggedIn=logged,authMethod=method,email=email}),new UTF8Encoding(false));}
        internal void Mode(string mode){File.WriteAllText(Path.Combine(Home,"mode.txt"),mode);}
        internal void Usage(){File.WriteAllText(Path.Combine(Home,"usage-supported.txt"),"");WriteQuota(UsagePayload());}
        internal void WriteQuota(object value){File.WriteAllText(Path.Combine(Home,"quota.json"),Json.Serialize(value),new UTF8Encoding(false));}
        internal async Task Open(){await Client.ConnectAsync(Assembly.GetExecutingAssembly().Location,Home);await Client.AccountAsync();}
        internal Task<string> NewThread(){return Client.ThreadAsync(null,Project,"sonnet");}
        internal Task<string> Run(string thread,string prompt){return Client.RunBoundAsync(thread,Project,"sonnet",prompt,Client.AccountIdentity.Fingerprint);}
        internal List<string[]> Args(){var path=Path.Combine(Home,"argv.jsonl");return File.Exists(path)?File.ReadAllLines(path).Select(x=>Json.Deserialize<string[]>(x)).ToList():new List<string[]>();}
        internal List<Dictionary<string,object>> Inputs(){var path=Path.Combine(Home,"stdin.jsonl");return File.Exists(path)?File.ReadAllLines(path).Select(x=>Json.Deserialize<Dictionary<string,object>>(x)).ToList():new List<Dictionary<string,object>>();}
        public void Dispose(){Client.Dispose();}
    }
    static async Task Failure(Func<Task> action,StudioFailureKind kind,string label)
    {
        StudioFailure caught=null;try{await action();}catch(StudioFailure e){caught=e;}
        Check(caught!=null&&caught.Kind==kind,label);Check(!caught.ToString().Contains("PRIVATE")&&!caught.ToString().Contains("SECRET"),label+" sanitized");
    }
    static async Task Basic(string root)
    {
        using(var f=new Fixture(root,"basic")){
            await f.Open();Check(f.Client.Connected&&f.Client.Provider=="claude","official-provider adapter connects");
            var account=f.Client.AccountIdentity;Check(account.Known&&account.IsAuthenticated&&account.Fingerprint.Length==64,"known account hashed");
            Check(!Json.Serialize(await f.Client.AccountAsync()).Contains("fixture@example.invalid"),"raw email not exposed");
            var models=await f.Client.ModelsAsync();Check(String.Join(",",models.Select(m=>m.Id))=="sonnet,opus,haiku","explicit model aliases");
            var quota=await f.Client.QuotaAsync();Check(!quota.Available&&quota.State(DateTime.UtcNow,account,f.Client.ConnectionId)==StudioQuotaState.Unknown,"unknown quota not fabricated");
            string thread=await f.NewThread(),display="";int finals=0,deltas=0;
            f.Client.Message+=(id,text,final)=>{if(final==Boolean.TrueString){display=text;finals++;}else{display+=text;deltas++;}};
            string prompt="SECRET_PROMPT\n/command @private-file 한글 \\\"";
            Check(await f.Run(thread,prompt)=="completed","stream completed");Check(display=="한글 응답"&&deltas==2&&finals==1,"UTF8 deltas and single authoritative final without duplication");
            var args=f.Args().Single();Check(args.Contains("--session-id")&&!args.Contains("--resume"),"new thread uses explicit fresh ID");
            Check(!String.Join(" ",args).Contains("SECRET_PROMPT")&&!args.Contains(f.Project),"prompt and project absent from process arguments");
            Check(File.ReadAllLines(Path.Combine(f.Home,"cwd.jsonl")).All(x=>Json.Deserialize<string>(x)==f.Home),"CLI runs only in sterile ADE directory");
            Check(Option(args,"--tools")==""&&Option(args,"--permission-mode")=="dontAsk"&&Option(args,"--permission-prompts")=="none"&&args.Contains("--restricted")&&args.Contains("--safe-mode"),"chat-only permissions fixed");
            Check(Option(args,"--setting-sources")==""&&Option(args,"--settings").Contains("disableAllHooks")&&args.Contains("--strict-mcp-config")&&Option(args,"--mcp-config")=="{\"mcpServers\":{}}"&&args.Contains("--no-chrome")&&args.Contains("--disable-slash-commands"),"customization and integration flags fixed");
            var input=f.Inputs();Check(Str(Obj(input.First(),"request"),"subtype")=="initialize","control initialize precedes prompt");
            var user=input.Single(x=>Str(x,"type")=="user");Check(Object.Equals(user["client_composed"],true)&&Str(user,"session_id")==thread,"verbatim user message bound to session");
            Check(Str(StudioCodexClient.Items(Obj(user,"message"),"content").Single(),"text")==prompt,"private prompt sent intact only over stdin");
            Check(await f.Run(thread,"second")=="completed"&&f.Args().Last().Contains("--resume"),"completed session resumes exact ID");
            f.Mode("duplicate-result");finals=0;Check(await f.Run(thread,"third")=="completed"&&finals==1,"duplicate terminal frames ignored");
            f.Mode("deny");Check(await f.Run(thread,"permission fixture")=="completed","tool requests denied without hanging");
            var denial=Json.Deserialize<Dictionary<string,object>>(File.ReadAllLines(Path.Combine(f.Home,"deny.jsonl")).Single());Check(Str(Obj(Obj(denial,"response"),"response"),"behavior")=="deny","can_use_tool is explicitly denied");
            var unsupported=Json.Deserialize<Dictionary<string,object>>(File.ReadAllLines(Path.Combine(f.Home,"unsupported.jsonl")).Single());Check(Str(Obj(unsupported,"response"),"subtype")=="error","unsupported control request receives error");
        }
    }
    static async Task Identity(string root)
    {
        using(var f=new Fixture(root,"identity")){
            f.Account("oauth",true,null);await f.Open();Check(f.Client.AccountIdentity.IsAuthenticated&&!f.Client.AccountIdentity.Known,"authenticated status can remain unidentified");
            string id=await f.NewThread();Check(await f.Run(id,"unknown-account")=="completed"&&!f.Client.AccountIdentity.Known,"unchanged unknown identity allowed on owned connection without fabricated fingerprint");
            f.Account("api_key",true,null);await Failure(()=>f.Run(id,"changed method"),StudioFailureKind.Authentication,"unknown auth-method change blocks preflight");
            Check(f.Args().Count==1,"identity change prevents inference child");
            await Failure(()=>f.Client.RunBoundAsync(id,f.Project,"sonnet","known expected","known-fingerprint"),StudioFailureKind.Authentication,"known expected identity never matches unknown actual");
            f.Account("oauth",false,null);await f.Client.AccountAsync();await Failure(()=>f.Run(id,"logged out"),StudioFailureKind.Authentication,"unauthenticated account blocked");
        }
        using(var f=new Fixture(root,"known-mismatch")){
            await f.Open();string id=await f.NewThread();var expected=f.Client.AccountIdentity.Fingerprint;
            await Failure(()=>f.Client.RunBoundAsync(id,f.Project,"sonnet","missing expected",null),StudioFailureKind.Authentication,"known account requires expected identity");
            f.Account("oauth",true,"changed@example.invalid");await Failure(()=>f.Client.RunBoundAsync(id,f.Project,"sonnet","changed known",expected),StudioFailureKind.Authentication,"known account mismatch blocked across preflight");
            Check(f.Args().Count==0,"known mismatch never starts inference child");
        }
    }
    static async Task ParserFailures(string root)
    {
        // Every malformed/over-limit frame here follows a sent prompt but no terminal
        // provider result, so delivery stays unconfirmed rather than becoming retryable.
        var cases=new[]{new{mode="tools",kind=StudioFailureKind.Disconnected},new{mode="mcp",kind=StudioFailureKind.Disconnected},new{mode="missing-tools",kind=StudioFailureKind.Disconnected},new{mode="malformed-tools",kind=StudioFailureKind.Disconnected},new{mode="foreign-session",kind=StudioFailureKind.Disconnected},new{mode="missing-session",kind=StudioFailureKind.Disconnected},new{mode="missing-session-delta",kind=StudioFailureKind.Disconnected},new{mode="malformed-result",kind=StudioFailureKind.Disconnected},new{mode="malformed-result-text",kind=StudioFailureKind.Disconnected},new{mode="tool-stream",kind=StudioFailureKind.Disconnected},new{mode="tool-assistant",kind=StudioFailureKind.Disconnected},new{mode="malformed",kind=StudioFailureKind.Disconnected},new{mode="huge-line",kind=StudioFailureKind.Disconnected},new{mode="huge-answer",kind=StudioFailureKind.Disconnected},new{mode="huge-output",kind=StudioFailureKind.Disconnected},new{mode="error:not logged in",kind=StudioFailureKind.Authentication},new{mode="error:billing_error",kind=StudioFailureKind.Quota},new{mode="error:model_not_found",kind=StudioFailureKind.Model},new{mode="error:network unavailable",kind=StudioFailureKind.Network},new{mode="error:unclassified",kind=StudioFailureKind.Unknown}};
        int index=0;foreach(var item in cases){using(var f=new Fixture(root,"parser-"+(index++))){await f.Open();f.Mode(item.mode);var id=await f.NewThread();await Failure(()=>f.Run(id,"fixture"),item.kind,"parser: "+item.mode);}}
    }
    static async Task SessionFailures(string root)
    {
        using(var f=new Fixture(root,"preflight")){
            await f.Open();var id=await f.NewThread();f.Mode("permission");await Failure(()=>f.Run(id,"not sent"),StudioFailureKind.Permission,"initialization permission downgrade blocked");
            Check(!f.Inputs().Any(x=>Str(x,"type")=="user"),"failed initialization never sends prompt");f.Mode("normal");Check(await f.Run(id,"retry")=="completed"&&f.Args().All(a=>a.Contains("--session-id")),"failure before prompt preserves fresh session");
        }
        using(var f=new Fixture(root,"ambiguous-delivery")){
            await f.Open();var id=await f.NewThread();f.Mode("drop-before-init");await Failure(()=>f.Run(id,"possibly delivered"),StudioFailureKind.Disconnected,"disconnect after prompt is unconfirmed");
            f.Mode("normal");Check(await f.Run(id,"explicit retry")=="completed"&&f.Args().Last().Contains("--resume"),"attempted delivery consumes fresh ID even before system/init");
        }
        using(var f=new Fixture(root,"unknown-resume")){
            await f.Open();var id=Guid.NewGuid().ToString("D");await f.Client.ThreadAsync(id,f.Project,"sonnet");f.Mode("missing-resume");await Failure(()=>f.Run(id,"resume"),StudioFailureKind.Context,"unknown resume returns context failure");
            Check(f.Args().Count==1&&f.Args().Single().Contains("--resume")&&!f.Args().Single().Contains("--session-id"),"unknown resume never falls back to new session");
        }
        using(var f=new Fixture(root,"old-version")){
            f.Mode("old");await Failure(()=>f.Open(),StudioFailureKind.Compatibility,"older CLI version blocked");Check(!f.Client.Connected,"unsupported CLI is not connected");
        }
    }
    static async Task Cancellation(string root)
    {
        foreach(var mode in new[]{"wait-confirmed","wait-completed","wait-quota","wait-eof","wait-malformed","wait-timeout"}){
            using(var f=new Fixture(root,mode)){
                await f.Open();f.Mode(mode);var started=new TaskCompletionSource<bool>();f.Client.Notice+=(kind,value)=>{if(kind=="working")started.TrySetResult(true);};
                var id=await f.NewThread();var running=f.Run(id,"wait");Check(await Task.WhenAny(started.Task,Task.Delay(10000))==started.Task,"cancellation fixture started");
                if(mode=="wait-confirmed"){await f.Client.InterruptAsync();Check(await running=="interrupted","provider result confirms interruption");}
                else if(mode=="wait-completed"){string final=null;f.Client.Message+=(answer,text,last)=>{if(last==Boolean.TrueString)final=text;};await f.Client.InterruptAsync();Check(await running=="completed"&&final=="한글 응답","Stop racing with success retains completed result and final text");}
                else if(mode=="wait-quota"){await Failure(()=>f.Client.InterruptAsync(),StudioFailureKind.Quota,"Stop racing with provider failure retains quota category");await Failure(()=>running,StudioFailureKind.Quota,"provider failure is not labelled interruption");}
                else {var kind=mode=="wait-timeout"?StudioFailureKind.Timeout:StudioFailureKind.Disconnected;await Failure(()=>f.Client.InterruptAsync(),kind,"interrupt failure remains unconfirmed: "+mode);await Failure(()=>running,kind,"run failure remains unconfirmed: "+mode);}
            }
        }
        using(var f=new Fixture(root,"cancel-before-prompt")){
            await f.Open();File.WriteAllText(Path.Combine(f.Home,"delay-auth.txt"),"");var id=await f.NewThread();var running=f.Run(id,"not sent");
            await Failure(()=>f.Client.InterruptAsync(),StudioFailureKind.Disconnected,"preflight cancellation is not provider-confirmed");await Failure(()=>running,StudioFailureKind.Disconnected,"preflight cancellation stops run");
            Check(f.Args().Count==0,"cancel before preflight completes starts no inference child");File.Delete(Path.Combine(f.Home,"delay-auth.txt"));Check(await f.Run(id,"retry")=="completed"&&f.Args().Single().Contains("--session-id"),"preflight cancellation retains fresh ID");
        }
        using(var f=new Fixture(root,"dispose")){
            await f.Open();f.Mode("wait-timeout");var started=new TaskCompletionSource<bool>();f.Client.Notice+=(kind,value)=>{if(kind=="working")started.TrySetResult(true);};var id=await f.NewThread();var running=f.Run(id,"wait");await started.Task;f.Client.Dispose();await Failure(()=>running,StudioFailureKind.Disconnected,"dispose unblocks active run");
        }
        using(var f=new Fixture(root,"blocked-stdin")){
            await f.Open();f.Mode("block-stdin");var id=await f.NewThread();var running=f.Run(id,new String('한',32000));
            await Task.Delay(300);var stopping=f.Client.InterruptAsync();
            Check(await Task.WhenAny(stopping,Task.Delay(7000))==stopping,"interrupt does not block behind a full stdin pipe");
            Check(await Task.WhenAny(running,Task.Delay(7000))==running,"prompt write has a bounded deadline");
            StudioFailure runFailure=null,stopFailure=null;try{await running;}catch(StudioFailure error){runFailure=error;}try{await stopping;}catch(StudioFailure error){stopFailure=error;}
            Check(runFailure!=null&&(runFailure.Kind==StudioFailureKind.Timeout||runFailure.Kind==StudioFailureKind.Disconnected),"blocked prompt delivery remains unconfirmed");
            Check(stopFailure!=null&&(stopFailure.Kind==StudioFailureKind.Timeout||stopFailure.Kind==StudioFailureKind.Disconnected),"blocked interrupt cannot manufacture confirmation");
        }
    }
    static async Task Probe(string root)
    {
        using(var client=new StudioClaudeClient()){
            await client.ConnectAsync(StudioClaudeClient.FindExecutable(),Path.Combine(root,"official-metadata"));await client.AccountAsync();
            var start=typeof(StudioClaudeClient).GetMethod("Start",BindingFlags.NonPublic|BindingFlags.Instance);
            using(var process=(Process)start.Invoke(client,new object[]{StudioClaudeClient.BuildArguments(Guid.NewGuid().ToString("D"),"sonnet",true)})){
                var stderr=Task.Run(()=>{try{process.StandardError.ReadToEnd();}catch{}});
                try{
                    // Initialization only: never send a user frame or text prompt.
                    process.StandardInput.WriteLine(Json.Serialize(new{type="control_request",request_id="metadata-only",request=new{subtype="initialize",hooks=new{},skills=new string[0]}}));process.StandardInput.Flush();
                    // Official CLI may emit lifecycle notices before the correlated reply.
                    var read=Task.Run(()=>{try{for(int i=0;i<32;i++){var line=process.StandardOutput.ReadLine();if(line==null||line.Length>1024*1024)return null;var frame=Json.Deserialize<Dictionary<string,object>>(line);if(Str(frame,"type")=="control_response"&&Str(Obj(frame,"response"),"request_id")=="metadata-only")return frame;}return null;}catch{return null;}});
                    Check(await Task.WhenAny(read,Task.Delay(15000))==read,"official initialization responds without inference");
                    var message=await read;var response=Obj(message,"response");
                    Check(Str(response,"request_id")=="metadata-only"&&Str(response,"subtype")=="success"&&Str(Obj(response,"response"),"current_permission_mode")=="dontAsk","official initialization confirms dontAsk");
                    Console.WriteLine(Json.Serialize(new{officialCliConnected=client.Connected,authenticated=client.AccountIdentity.IsAuthenticated,accountIdentityKnown=client.AccountIdentity.Known,initializationVerified=true,inferenceRequested=false,loginRequested=false,credentialFilesRead=false}));
                }finally{try{process.StandardInput.Close();}catch{}try{if(!process.WaitForExit(1000))process.Kill();}catch{}try{stderr.Wait(3000);}catch{}}
            }
        }
    }
    static async Task FrameReadiness(string root)
    {
        using(var f=new Fixture(root,"init-large")){
            await f.Open();f.Mode("large-init");var id=await f.NewThread();var running=f.Run(id,"synthetic initialization framing check");
            bool ready=await Task.WhenAny(running,Task.Delay(8000))==running;
            if(!ready){f.Client.Dispose();try{await running;}catch(StudioFailure){}}
            Check(ready,"a complete initialization line larger than 4096 chars must not wait for another frame or EOF");
            Check(await running=="completed","large initialization permits one normal question");
            Check(f.Inputs().Count(x=>Str(x,"type")=="user")==1,"large initialization never duplicates the user request");
        }
        using(var f=new Fixture(root,"stream-large")){
            await f.Open();f.Mode("long-stream");var received=new TaskCompletionSource<string>();int finals=0;
            f.Client.Message+=(id,text,final)=>{if(final==Boolean.TrueString)finals++;else received.TrySetResult(text);};
            var thread=await f.NewThread();var running=f.Run(thread,"synthetic large streamed line check");bool streamed;
            try{streamed=await Task.WhenAny(received.Task,Task.Delay(6000))==received.Task;}
            finally{File.WriteAllText(Path.Combine(f.Home,"release.txt"),"release synthetic final");}
            Check(streamed,"large text delta must be delivered while the child is waiting for permission to emit its final frame");
            Check((await received.Task).StartsWith("시작 ")&&(await received.Task).EndsWith(" 끝"),"streamed line preserves UTF8 boundaries and tail text");
            Check(await running=="completed"&&finals==1,"large streamed line completes with one authoritative final");
        }
    }
    static Dictionary<string,object> UsagePayload()
    {
        return new Dictionary<string,object>{{"rate_limits_available",true},{"subscription_type","max"},{"rate_limits",new Dictionary<string,object>{{"five_hour",UsageWindow(1,2)},{"seven_day",UsageWindow(3,24)},{"seven_day_opus",UsageWindow(85,24)},{"seven_day_sonnet",null},{"model_scoped",new[]{new{display_name="unrecognized scope",utilization=100,resets_at=DateTime.UtcNow.AddDays(1).ToString("o")}}},{"extra_usage",new{is_enabled=true,used_credits=0,monthly_limit=10000}}}}};
    }
    static Dictionary<string,object> UsageWindow(object used,double hours)
    { return new Dictionary<string,object>{{"utilization",used},{"resets_at",DateTime.UtcNow.AddHours(hours).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",System.Globalization.CultureInfo.InvariantCulture)}}; }
    static void UsageParser()
    {
        var identity=StudioAccountIdentity.FromClaudeStatus(new Dictionary<string,object>{{"loggedIn",true},{"authMethod","claude.ai"},{"email","fixture@example.invalid"},{"accountId","fixture-id"}});
        Check(identity.AccountIdFingerprint==identity.Fingerprint&&identity.EmailFingerprint==StudioAccountIdentity.ClaudeEmailFingerprint(" FIXTURE@example.invalid "),"email binding retained when official account ID takes precedence");
        var now=DateTime.UtcNow;var data=UsagePayload();var parsed=StudioClaudeQuotaParser.Parse(data,identity,"fixture",now);
        Check(parsed.State(now,identity,"fixture")==StudioQuotaState.Confirmed&&parsed.Buckets.Count==2,"common and exact supported-model quota parsed: "+parsed.State(now,identity,"fixture")+" / "+parsed.Buckets.Count);
        Check(parsed.Buckets[0].Primary.RemainingPercent==99&&parsed.Buckets[0].Secondary.RemainingPercent==97,"usage percent is subtracted from 100, never treated as fraction");
        Check(parsed.Buckets[1].ModelScope=="opus"&&parsed.Buckets[1].Secondary.RemainingPercent==15,"Opus weekly window keeps exact alias scope");
        foreach(var invalid in new object[]{null,"1",true,-1,100.01,Double.NaN,Double.PositiveInfinity}){
            data=UsagePayload();Obj(Obj(data,"rate_limits"),"five_hour")["utilization"]=invalid;
            Check(!StudioClaudeQuotaParser.Parse(data,identity,"fixture",now).Available,"malformed used percent stays unknown: "+Convert.ToString(invalid));
        }
        foreach(var invalid in new object[]{null,"bad","2026-10-20",DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss"),DateTime.UtcNow.AddDays(10).ToString("o"),DateTime.UtcNow.AddSeconds(-1).ToString("o")}){
            data=UsagePayload();Obj(Obj(data,"rate_limits"),"five_hour")["resets_at"]=invalid;
            Check(!StudioClaudeQuotaParser.Parse(data,identity,"fixture",now).Available,"invalid, expired or implausible reset stays unknown");
        }
        foreach(var available in new object[]{false,null,"true"}){data=UsagePayload();data["rate_limits_available"]=available;Check(!StudioClaudeQuotaParser.Parse(data,identity,"fixture",now).Available,"availability must be an explicit boolean");}
        foreach(var subscription in new object[]{null,"api","future-plan"}){data=UsagePayload();data["subscription_type"]=subscription;Check(!StudioClaudeQuotaParser.Parse(data,identity,"fixture",now).Available,"non-subscription/unknown entitlement never supplies quota");}
        data=UsagePayload();Obj(data,"rate_limits").Remove("seven_day");Check(!StudioClaudeQuotaParser.Parse(data,identity,"fixture",now).Available,"partial common quota cannot confirm capacity");
        data=UsagePayload();Obj(Obj(data,"rate_limits"),"seven_day_opus")["utilization"]="invalid";Check(!StudioClaudeQuotaParser.Parse(data,identity,"fixture",now).Available,"malformed known model-specific constraint never ignored");
        data=UsagePayload();Obj(Obj(data,"rate_limits"),"five_hour")["utilization"]=100;parsed=StudioClaudeQuotaParser.Parse(data,identity,"fixture",now);
        Check(parsed.Available&&parsed.Buckets[0].Primary.RemainingPercent==0&&parsed.OrdinaryUsageAllowed!=true,"exhaustion preserved without enabling extra paid usage");
        Check(parsed.State(now.AddMinutes(5),identity,"fixture")==StudioQuotaState.Stale&&parsed.State(now,identity,"foreign")==StudioQuotaState.Mismatch,"Claude observations retain shared freshness/connection checks");
    }
    static async Task WaitForFile(string path)
    { var until=DateTime.UtcNow.AddSeconds(8);while(!File.Exists(path)&&DateTime.UtcNow<until)await Task.Delay(10);Check(File.Exists(path),"quota child reached held response"); }
    static async Task UsageQueries(string root)
    {
        UsageParser();
        using(var f=new Fixture(root,"usage-success")){
            f.Usage();await f.Open();var pending=Enumerable.Range(0,4).Select(i=>f.Client.QuotaAsync()).ToArray();var results=await Task.WhenAll(pending);
            Check(results.All(q=>q.Available&&q.Buckets[0].Secondary.RemainingPercent==97)&&f.Args().Count==1,"concurrent quota readers share one official control-only child");
            Check((await f.Client.QuotaAsync()).Available&&f.Args().Count==1,"fresh cache reused after account recheck");
            var args=f.Args().Single();Check(args.Contains("--no-session-persistence")&&args.Contains("--session-id")&&Option(args,"--tools")==""&&args.Contains("--restricted")&&Option(args,"--setting-sources")=="","quota child remains sterile, chat-only and nonpersistent");
            Check(f.Inputs().Count==2&&f.Inputs().All(m=>Str(m,"type")=="control_request")&&Object.Equals(Obj(f.Inputs().Last(),"request")["skip_behaviors"],true),"quota collection sends no user prompt and requests no transcript behavior scan");
            var old=f.Client.Quota;typeof(StudioClaudeClient).GetField("quota",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(f.Client,new StudioQuotaSnapshot(DateTime.UtcNow.AddSeconds(-61),old.ConnectionId,old.AccountFingerprint,null,old.Buckets,null,true));
            Check((await f.Client.QuotaAsync()).Available&&f.Args().Count==2,"cache expires after one minute");
            f.Account("oauth",true,"next@example.invalid");Check((await f.Client.QuotaAsync()).AccountFingerprint==f.Client.AccountIdentity.Fingerprint&&f.Args().Count==3,"cache invalidated on official account change");
        }
        foreach(var mode in new[]{"usage-child-mismatch","usage-bedrock","permission","usage-unsupported","usage-wrong-id","usage-bad-body","usage-malformed","usage-huge","usage-foreign","usage-control","usage-timeout"}){
            using(var f=new Fixture(root,mode)){
                f.Usage();f.Mode(mode);await f.Open();var watch=Stopwatch.StartNew();var quota=await f.Client.QuotaAsync();
                Check(!quota.Available&&f.Client.Connected,"optional usage failure stays unknown without disconnecting chat: "+mode);
                Check(!f.Inputs().Any(m=>Str(m,"type")=="user"),"failed usage never falls back to model question: "+mode);
                if(mode=="usage-child-mismatch"||mode=="usage-bedrock"||mode=="permission")Check(f.Inputs().Count==1,"untrusted child account/permission blocked before get_usage");
                if(mode=="usage-timeout")Check(watch.Elapsed<TimeSpan.FromSeconds(18),"hung official usage has a bounded deadline");
            }
        }
        using(var f=new Fixture(root,"usage-account-switch")){
            f.Usage();await f.Open();File.WriteAllText(Path.Combine(f.Home,"account-after.json"),Json.Serialize(new{loggedIn=true,authMethod="oauth",email="changed@example.invalid"}));
            Check(!(await f.Client.QuotaAsync()).Available,"account change after query rejects observation");
        }
        using(var f=new Fixture(root,"usage-aba")){
            f.Usage();f.Mode("usage-hold");await f.Open();var pending=f.Client.QuotaAsync();await WaitForFile(Path.Combine(f.Home,"usage-started.txt"));
            f.Account("oauth",true,"other@example.invalid");await f.Client.AccountAsync();f.Account("oauth",true,"fixture@example.invalid");await f.Client.AccountAsync();File.WriteAllText(Path.Combine(f.Home,"release-usage.txt"),"");
            Check(!(await pending).Available&&f.Client.Quota==null,"A-to-B-to-A login change cannot publish in-flight old quota");
            f.Mode("normal");Check((await f.Client.QuotaAsync()).Available,"fresh query recovers after identity invalidation");
        }
        foreach(var mode in new[]{"usage-api","usage-no-email","usage-no-flag"}){
            using(var f=new Fixture(root,mode)){
                f.Usage();f.Mode(mode);if(mode=="usage-api")f.Account("api_key",true,"fixture@example.invalid");if(mode=="usage-no-email")f.Account("oauth",true,null);await f.Open();
                Check(!(await f.Client.QuotaAsync()).Available&&f.Args().Count==0,"unsupported usage prerequisites start no child: "+mode);
            }
        }
        using(var f=new Fixture(root,"usage-dispose")){
            f.Usage();f.Mode("usage-hold");await f.Open();var pending=f.Client.QuotaAsync();await WaitForFile(Path.Combine(f.Home,"usage-started.txt"));f.Client.Dispose();
            Check(await Task.WhenAny(pending,Task.Delay(5000))==pending&&!(await pending).Available,"disposing owns and stops its quota process, returning unknown");
        }
    }
    static async Task Tests(string root){await UsageQueries(root);await FrameReadiness(root);await Basic(root);await Identity(root);await ParserFailures(root);await SessionFailures(root);await Cancellation(root);}
    [STAThread] static int Main(string[] args)
    {
        try{
            if(args.Contains("--version")||args.Contains("--help")||args.Contains("auth")||args.Contains("--print"))return Fake(args);
            string root=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"studio-claude-output");Directory.CreateDirectory(root);root=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            if(args.Contains("--probe"))Probe(root).GetAwaiter().GetResult();else Tests(root).GetAwaiter().GetResult();
            Console.WriteLine("PASS: "+checks+" Claude adapter checks; "+(args.Contains("--probe")?"metadata-only native probe":"fake CLI only, no inference")+".");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
