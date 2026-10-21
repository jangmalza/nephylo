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

// No real credentials, Google login or inference. This executable is the ACP fixture.
internal static class StudioGeminiCliTests
{
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=3*1024*1024};
    static int checks;static string root;
    static void Check(bool ok,string label){checks++;if(!ok)throw new Exception(label);}
    static string Str(Dictionary<string,object> value,string key){return StudioCodexClient.Str(value,key);}
    static Dictionary<string,object> Obj(Dictionary<string,object> value,string key){return StudioCodexClient.Obj(value,key);}
    static void Emit(object value){Console.WriteLine(Json.Serialize(value));Console.Out.Flush();}
    static void Result(object id,object value){Emit(new{jsonrpc="2.0",id,result=value});}
    static void Error(object id,int code,string message){Emit(new{jsonrpc="2.0",id,error=new{code,message}});}
    static void Update(string session,string text){Emit(new{jsonrpc="2.0",method="session/update",@params=new{sessionId=session,update=new{sessionUpdate="agent_message_chunk",content=new{type="text",text}}}});}
    static void Replay(string session,string role,string text){Emit(new{jsonrpc="2.0",method="session/update",@params=new{sessionId=session,update=new{sessionUpdate=role=="user"?"user_message_chunk":"agent_message_chunk",content=new{type="text",text}}}});}
    static string Mode(){return File.Exists("mode.txt")?File.ReadAllText("mode.txt"):"normal";}
    static object Modes(){return new{currentModeId=Mode()=="permission"?"yolo":"default",availableModes=new[]{new{id="default",name="Default"}}};}
    static void Log(string name,object value){File.AppendAllText(name,Json.Serialize(value)+"\n",new UTF8Encoding(false));}
    static int Fake(string[] args)
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=new UTF8Encoding(false);
        Log("launch.jsonl",new{args,cwd=Environment.CurrentDirectory});string session=null;object promptId=null;
        string line;while((line=Console.ReadLine())!=null){var message=Json.Deserialize<Dictionary<string,object>>(line);Log("stdin.jsonl",message);object id;message.TryGetValue("id",out id);string method=Str(message,"method");var data=Obj(message,"params");string mode=Mode();
            if(method=="initialize"){
                if(mode=="init-eof")return 0;
                Result(777,new{unexpected=true});
                Result(id,new{protocolVersion=mode=="version"?99:1,agentCapabilities=new{loadSession=mode!="no-resume"},authMethods=new[]{new{id="oauth-personal",name="Google"}}});continue;
            }
            if(method=="session/new"||method=="session/load"){
                if(mode=="auth"){Error(id,401,"Authentication required PRIVATE_TOKEN");continue;}
                if(mode=="consent"){Console.Write("Opening authentication page in your browser. Do you want to continue?");Console.Out.Flush();continue;}
                if(mode=="missing"&&method=="session/load"){Error(id,404,"Session not found PRIVATE_PATH");continue;}
                session=method=="session/new"?Guid.NewGuid().ToString("D"):Str(data,"sessionId");
                if(method=="session/load"){
                    Result(id,new{sessionId=session,modes=Modes()});
                    if(mode=="missing-replay")continue;
                    Thread.Sleep(100);
                    var replay=Json.Deserialize<List<StudioMessage>>(File.ReadAllText("replay.json"));
                    foreach(var item in replay){string text=item.Text;if(mode=="bad-replay")text="mismatch";int midpoint=text.Length/2;Replay(session,item.Role,text.Substring(0,midpoint));Thread.Sleep(10);Replay(session,item.Role,text.Substring(midpoint));}
                    continue;
                }
                if(mode=="bad-session")session="../../not-a-session";
                if(mode=="catalog")Result(id,new{sessionId=session,modes=Modes(),models=new{availableModels=new[]{new{modelId="gemini-test-custom",name="Fixture custom"}},currentModelId="gemini-test-custom"}});
                else Result(id,new{sessionId=session,modes=Modes()});if(mode=="block-stdin")Thread.Sleep(15000);continue;
            }
            if(method=="session/prompt"){
                promptId=id;
                if(mode=="eof")return 0;
                if(mode=="quota"){Error(id,429,"Quota PRIVATE_KEY");continue;}
                if(mode=="malformed"){Console.WriteLine("PRIVATE malformed");Console.Out.Flush();continue;}
                if(mode=="huge-line"){Console.WriteLine(new String('x',1024*1024+2));Console.Out.Flush();continue;}
                if(mode=="huge-answer"){Update(session,new String('x',512001));continue;}
                if(mode=="tool-event"){Emit(new{jsonrpc="2.0",method="session/update",@params=new{sessionId=session,update=new{sessionUpdate="tool_call",toolCallId="disallowed",title="No tools"}}});continue;}
                if(mode=="deny"){Emit(new{jsonrpc="2.0",id="permission",method="session/request_permission",@params=new{sessionId=session,options=new[]{new{optionId="allow",kind="allow_always"}}}});continue;}
                if(mode.StartsWith("wait",StringComparison.Ordinal)){Update(session,"Partial ");continue;}
                if(mode=="unknown-stop"){Result(id,new{stopReason="unknown"});continue;}
                Update(Guid.NewGuid().ToString("D"),"IGNORED_FOREIGN");
                Emit(new{jsonrpc="2.0",method="session/update",@params=new{sessionId=session,update=new{sessionUpdate="agent_thought_chunk",content=new{type="text",text="IGNORED_THOUGHT"}}}});
                Update(session,"한글 ");Update(session,"답변");Result(id,new{stopReason="end_turn"});continue;
            }
            if(method=="session/cancel"){
                if(mode=="wait-eof")return 0;
                if(mode=="wait-timeout")continue;
                Result(promptId,new{stopReason=mode=="wait-completed"?"end_turn":"cancelled"});continue;
            }
            if(method==null&&Convert.ToString(id)=="permission"){
                Log("permission.jsonl",message);
                Emit(new{jsonrpc="2.0",id="fs",method="fs/read_text_file",@params=new{path="PRIVATE_PATH",sessionId=session}});continue;
            }
            if(method==null&&Convert.ToString(id)=="fs"){
                Log("fs.jsonl",message);Emit(new{jsonrpc="2.0",id="terminal",method="terminal/create",@params=new{command="DO_NOT_RUN"}});continue;
            }
            if(method==null&&Convert.ToString(id)=="terminal"){Log("terminal.jsonl",message);Update(session,"Denied safely");Result(promptId,new{stopReason="end_turn"});continue;}
        }return 0;
    }
    sealed class Fixture:IDisposable
    {
        internal string Home,Work;internal StudioGeminiCliClient Client;
        internal Fixture(string mode,bool requested=true)
        {
            Home=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Home);Work=Path.Combine(Home,"workspace");Directory.CreateDirectory(Work);File.WriteAllText(Path.Combine(Work,"mode.txt"),mode);
            if(requested)File.WriteAllText(Path.Combine(Home,"login-requested"),"1");
            Client=new StudioGeminiCliClient((home,model,interactive)=>new ProcessStartInfo{FileName=Assembly.GetExecutingAssembly().Location,Arguments="--fake "+StudioGeminiCliClient.Quote(model),WorkingDirectory=Path.Combine(home,"workspace"),UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8},1200);
        }
        internal async Task Connect(){await Client.ConnectAsync("fixture",Home);await Client.AccountAsync();}
        internal List<Dictionary<string,object>> Log(){return File.Exists(Path.Combine(Work,"stdin.jsonl"))?File.ReadAllLines(Path.Combine(Work,"stdin.jsonl")).Where(l=>!String.IsNullOrEmpty(l)).Select(l=>Json.Deserialize<Dictionary<string,object>>(l)).ToList():new List<Dictionary<string,object>>();}
        public void Dispose(){Client.Dispose();}
    }
    static async Task Throws(Func<Task> action,StudioFailureKind kind,string label)
    {try{await action();throw new Exception("Missing failure: "+label);}catch(StudioFailure error){Check(error.Kind==kind,label+" kind "+error.Kind);Check(!error.ToString().Contains("PRIVATE"),label+" sanitized");}}
    static async Task Run()
    {
        root=Path.GetFullPath(Path.Combine("outputs","studio-gemini-cli-tests",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        using(var f=new Fixture("normal",false)){
            await f.Connect();Check(f.Client.Connected,"initialize connected");Check(!f.Client.AccountIdentity.IsAuthenticated,"no marker never authenticated");Check(f.Log().Count==1&&Str(f.Log()[0],"method")=="initialize","no login request or session creation without marker");Check(f.Client.Provider=="gemini-cli"&&f.Client.Profile=="ade-gemini-cli","separate profile");Check(f.Client.AccountIdentity.Fingerprint==null,"no fabricated identity");
            Check((await f.Client.QuotaAsync()).State(DateTime.UtcNow,f.Client.AccountIdentity,f.Client.ConnectionId)==StudioQuotaState.Unknown,"quota unknown");
            await Throws(()=>f.Client.ThreadAsync(null,root,"pro"),StudioFailureKind.Authentication,"unauth thread");
        }
        using(var f=new Fixture("auth")){await f.Connect();Check(!f.Client.AccountIdentity.IsAuthenticated,"auth denied cleanly");Check(f.Log().All(x=>Str(x,"method")!="authenticate"),"never authenticates automatically");}
        using(var f=new Fixture("consent")){await f.Connect();Check(!f.Client.AccountIdentity.IsAuthenticated,"consent text fails auth without approval");}
        foreach(var mode in new[]{"version","no-resume"})using(var f=new Fixture(mode)){await Throws(()=>f.Client.ConnectAsync("fixture",f.Home),StudioFailureKind.Compatibility,mode);}
        using(var f=new Fixture("init-eof")){await Throws(()=>f.Client.ConnectAsync("fixture",f.Home),StudioFailureKind.Disconnected,"init EOF");}
        using(var f=new Fixture("permission")){await Throws(()=>f.Connect(),StudioFailureKind.Permission,"unsafe mode");}
        using(var f=new Fixture("bad-session")){await Throws(()=>f.Connect(),StudioFailureKind.Context,"malformed session");}
        using(var f=new Fixture("normal")){
            await f.Connect();Check(f.Client.AccountIdentity.IsAuthenticated&&!f.Client.AccountIdentity.Known,"authenticated unknown identity");var models=await f.Client.ModelsAsync();Check(models.Select(x=>x.Id).SequenceEqual(new[]{"pro","flash","flash-lite"}),"explicit aliases only");
            string session=await f.Client.ThreadAsync(null,root,"pro"),answer="";f.Client.Message+=(kind,text,final)=>answer+=text;
            string prompt="Synthetic @private.txt /not-a-command ` ; 한글";Check(await f.Client.RunBoundAsync(session,root,"pro",prompt,null)=="completed","turn completed");Check(answer=="한글 답변","stream exact only active answer");
            var sent=f.Log().Single(m=>Str(m,"method")=="session/prompt");var parameters=Obj(sent,"params");var blocks=StudioCodexClient.Items(parameters,"prompt").ToList();Check(blocks.Count==1&&Str(blocks[0] as Dictionary<string,object>,"type")=="text"&&Str(blocks[0] as Dictionary<string,object>,"text")==prompt,"exact text no resource expansion");
            Check(f.Log().Where(m=>Str(m,"method")=="session/new").All(m=>Str(Obj(m,"params"),"cwd")==f.Work),"sterile folder not selected project");
            await Throws(()=>f.Client.RunBoundAsync(session,root,"pro","test","wrong-account"),StudioFailureKind.Authentication,"mismatch before write");
            await Throws(()=>f.Client.ThreadAsync(null,root,"auto"),StudioFailureKind.Model,"no automatic alias");
            await Throws(()=>f.Client.ThreadAsync("../../evil",root,"pro"),StudioFailureKind.Context,"no path session");
            string flash=await f.Client.ThreadAsync(null,root,"flash");Check(flash!=session,"model change new session");Check(File.ReadAllLines(Path.Combine(f.Work,"launch.jsonl")).Last().Contains("flash"),"requested alias process argument");
            var history=new List<StudioMessage>{new StudioMessage{Role="user",Text=prompt},new StudioMessage{Role="assistant",Text="한글 답변"}};File.WriteAllText(Path.Combine(f.Work,"replay.json"),Json.Serialize(history),new UTF8Encoding(false));f.Client.PrepareReplay(session,history);
            Check(await f.Client.ThreadAsync(session,root,"pro")==session,"resume exact original id");Check(f.Log().Any(m=>Str(m,"method")=="session/load"&&Str(Obj(m,"params"),"sessionId")==session),"explicit load no new-session fallback");
            Check(answer=="한글 답변","load history not duplicate streamed answer");
        }
        using(var f=new Fixture("catalog")){await f.Connect();Check((await f.Client.ModelsAsync()).Single().Id=="gemini-test-custom","official catalogue replaces aliases");}
        foreach(string mode in new[]{"bad-replay","missing-replay","missing"})using(var f=new Fixture("normal")){
            await f.Connect();string prior=Guid.NewGuid().ToString("D");var history=new List<StudioMessage>{new StudioMessage{Role="user",Text="prior question"},new StudioMessage{Role="assistant",Text="prior answer"}};
            File.WriteAllText(Path.Combine(f.Work,"replay.json"),Json.Serialize(history));File.WriteAllText(Path.Combine(f.Work,"mode.txt"),mode);f.Client.PrepareReplay(prior,history);
            try{await f.Client.ThreadAsync(prior,root,"flash");throw new Exception("Unexpected replay acceptance");}catch(StudioFailure error){Check(error.Kind==StudioFailureKind.Context||mode=="missing"&&error.Kind==StudioFailureKind.Unknown,mode+" rejected");}
            Check(f.Log().All(m=>Str(m,"method")!="session/prompt"),mode+" sends no question");Check(f.Log().Count(m=>Str(m,"method")=="session/new")==1,mode+" no fallback new session");
        }
        using(var f=new Fixture("block-stdin")){
            await f.Connect();string session=await f.Client.ThreadAsync(null,root,"pro");var watch=Stopwatch.StartNew();
            await Throws(()=>f.Client.RunBoundAsync(session,root,"pro",new String('q',32000),null),StudioFailureKind.Timeout,"blocked stdin bounded");Check(watch.ElapsedMilliseconds<6000,"blocked writer does not hang cleanup");
        }
        using(var f=new Fixture("normal",false)){
            Directory.CreateDirectory(Path.Combine(f.Home,".gemini"));string settings=Path.Combine(f.Home,".gemini","settings.json"),custom="{\"context\":{\"includeDirectories\":[\"PRIVATE_PATH\"]}}";File.WriteAllText(settings,custom);
            await Throws(()=>f.Client.ConnectAsync("fixture",f.Home),StudioFailureKind.Compatibility,"custom profile refused");Check(File.ReadAllText(settings)==custom,"custom settings preserved");
        }
        using(var f=new Fixture("normal",false)){
            Directory.CreateDirectory(Path.Combine(f.Home,".gemini"));File.WriteAllText(Path.Combine(f.Home,".gemini","settings.json"),"{\"security\":{\"auth\":{\"selectedType\":\"oauth-personal\"}},\"ui\":{\"theme\":\"Default Dark\"}}");
            await f.Connect();Check(!f.Client.AccountIdentity.IsAuthenticated,"benign official auth/theme preferences allowed without marker bypass");
            var effective=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(f.Home,"system-settings.json")));
            Check(Obj(Obj(effective,"security"),"auth")["selectedType"]==null,"initialization cannot trigger Google OAuth");Check(Str(Obj(effective,"context"),"fileName").StartsWith("nephylo-no-context-"),"unique context sentinel materialized");
        }
        using(var f=new Fixture("deny")){
            await f.Connect();var session=await f.Client.ThreadAsync(null,root,"pro");Check(await f.Client.RunBoundAsync(session,root,"pro","test",null)=="completed","permission refusal can complete chat");
            Check(File.ReadAllText(Path.Combine(f.Work,"permission.jsonl")).Contains("cancelled"),"permission cancelled");Check(File.ReadAllText(Path.Combine(f.Work,"fs.jsonl")).Contains("-32601"),"filesystem unavailable");Check(File.ReadAllText(Path.Combine(f.Work,"terminal.jsonl")).Contains("-32601"),"terminal unavailable");
        }
        foreach(var mode in new[]{"eof","quota","malformed","huge-line","huge-answer","tool-event","unknown-stop"})using(var f=new Fixture(mode)){
            await f.Connect();string session=await f.Client.ThreadAsync(null,root,"pro");var kind=mode=="quota"?StudioFailureKind.Quota:StudioFailureKind.Disconnected;
            await Throws(()=>f.Client.RunBoundAsync(session,root,"pro","test",null),kind,mode);
        }
        foreach(var mode in new[]{"wait","wait-eof","wait-timeout","wait-completed"})using(var f=new Fixture(mode)){
            await f.Connect();string session=await f.Client.ThreadAsync(null,root,"pro");var begun=new TaskCompletionSource<bool>();f.Client.Message+=(k,t,d)=>begun.TrySetResult(true);var running=f.Client.RunBoundAsync(session,root,"pro","test",null);await begun.Task;
            if(mode=="wait"||mode=="wait-completed"){await f.Client.InterruptAsync();Check(await running==(mode=="wait"?"interrupted":"completed"),mode+" provider confirmed outcome");}
            else{var kind=mode=="wait-eof"?StudioFailureKind.Disconnected:StudioFailureKind.Timeout;await Throws(()=>f.Client.InterruptAsync(),kind,mode+" interrupt uncertainty");await Throws(()=>running,kind,mode+" turn uncertainty");}
        }
        Check(StudioGeminiCliClient.Quote("a b\\")=="\"a b\\\\\"","trailing backslash quote");Check(StudioGeminiCliClient.Quote("a\"b")=="\"a\\\"b\"","embedded quote");
        var envNames=new[]{"no_browser","https_proxy","NODE_OPTIONS","GEMINI_API_KEY","oauth_callback_host"};var previous=envNames.ToDictionary(x=>x,Environment.GetEnvironmentVariable);
        try{
            foreach(var name in envNames)Environment.SetEnvironmentVariable(name,"synthetic-fixture-value");
            var info=(ProcessStartInfo)typeof(StudioGeminiCliClient).GetMethod("StartInfo",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{"official-entry.js","node.exe",root,new[]{"--experimental-acp"},false});
            foreach(var name in envNames)Check(!info.EnvironmentVariables.ContainsKey(name),"case-insensitive ambient environment removal "+name);
            Check(info.EnvironmentVariables["GEMINI_CLI_HOME"]==root&&info.EnvironmentVariables["GEMINI_CLI_NO_RELAUNCH"]=="true","owned profile and exact child lifetime");
        }finally{foreach(var pair in previous)Environment.SetEnvironmentVariable(pair.Key,pair.Value);}
        var config=Json.Deserialize<Dictionary<string,object>>(StudioGeminiCliClient.SafeSettings);
        Check(StudioCodexClient.Items(Obj(config,"tools"),"core").Count()==0,"core tool registry empty");Check(Object.Equals(Obj(config,"hooksConfig")["enabled"],false),"hooks disabled");Check(Obj(config,"mcp")["serverCommand"]==null,"MCP command unavailable");Check(Object.Equals(Obj(config,"skills")["enabled"],false),"skills disabled");Check(Str(Obj(config,"context"),"fileName")=="NEPHYLO_CONTEXT_SENTINEL"&&Object.Equals(Obj(config,"context")["includeDirectoryTree"],false),"no default context or directory tree");
        Console.WriteLine("PASS Gemini CLI adapter: "+checks+" checks");Console.WriteLine("Fixtures: "+root);
    }
    static async Task Probe()
    {
        root=Path.GetFullPath(Path.Combine("outputs","studio-gemini-cli-probe",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        using(var client=new StudioGeminiCliClient()){
            await client.ConnectAsync(StudioGeminiCliClient.FindExecutable(),root);await client.AccountAsync();
            if(!client.Connected||client.AccountIdentity.IsAuthenticated)throw new Exception("Unexpected probe state");
            Console.WriteLine("PASS official Gemini CLI ACP initialize; unauthenticated; no session/prompt/authenticate sent; owned isolated profile.");
        }
    }
    static int Main(string[] args)
    {
        if(args.Contains("--fake"))return Fake(args);
        try{if(args.Contains("--probe"))Probe().GetAwaiter().GetResult();else Run().GetAwaiter().GetResult();return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
