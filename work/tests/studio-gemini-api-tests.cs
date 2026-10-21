using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using UsageWidget;

#if STUDIO_GEMINI_API_TESTS
namespace UsageWidget
{
    internal static class CodexBrowserRecovery
    {
        internal static bool IsAllowed(string url){throw new NotSupportedException("Browser login is outside isolated API tests.");}
    }
}
#endif

internal static class StudioGeminiApiTests
{
    const string Key="Nephylo_TEST_ONLY_Gemini_Key_000001",Key2="Nephylo_TEST_ONLY_Gemini_Key_000002",Model="gemini-test-text";
    static string root,project;static int count;
    static void Check(bool good,string name){if(!good)throw new Exception(name);count++;}
    static void Fail(Action run,StudioFailureKind kind,string name){try{run();throw new Exception("Expected failure: "+name);}catch(StudioFailure e){Check(e.Kind==kind,name);}}
    static async Task FailAsync(Func<Task> run,StudioFailureKind kind,string name){try{await run();throw new Exception("Expected failure: "+name);}catch(StudioFailure e){Check(e.Kind==kind,name);}}
    static string Json(object value){return new JavaScriptSerializer().Serialize(value);}
    static object ModelRow(string name,string method){return new{name="models/"+name,displayName="Test "+name,supportedGenerationMethods=new[]{method},inputTokenLimit=200000,outputTokenLimit=16000};}
    static string Catalog(){return Json(new{models=new[]{ModelRow(Model,"generateContent")}});}
    static string Chunk(string text,string finish){return Json(new{candidates=new[]{new{index=0,content=new{role="model",parts=new[]{new{text=text}}},finishReason=finish}}});}
    static string Sse(string text,string finish){return "data: "+Chunk(text,finish)+"\n\n";}
    static HttpResponseMessage Response(string body,string media="application/json",int code=200){return new HttpResponseMessage((HttpStatusCode)code){Content=new StringContent(body,Encoding.UTF8,media)};}
    static HttpResponseMessage Streaming(Stream stream){var message=new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(stream)};message.Content.Headers.ContentType=new MediaTypeHeaderValue("text/event-stream");return message;}
    sealed class Capture {internal string Url,Method,Key,Body;}
    sealed class Handler:HttpMessageHandler
    {
        internal readonly Queue<Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>> Queue=new Queue<Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>>();
        internal readonly List<Capture> Requests=new List<Capture>();
        internal void Add(HttpResponseMessage response){Queue.Enqueue((r,c)=>Task.FromResult(response));}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
            IEnumerable<string> values;Requests.Add(new Capture{Url=request.RequestUri.AbsoluteUri,Method=request.Method.Method,Key=request.Headers.TryGetValues("x-goog-api-key",out values)?values.Single():null,Body=request.Content==null?null:await request.Content.ReadAsStringAsync()});
            Check(request.RequestUri.Scheme=="https"&&request.RequestUri.Host=="generativelanguage.googleapis.com"&&request.RequestUri.Port==443,"fixed HTTPS origin");
            Check(!request.RequestUri.Query.Contains("key="),"key absent from URL");Check(request.Headers.Authorization==null,"no ambient OAuth authorization");
            if(Queue.Count==0)throw new Exception("Unexpected HTTP request");return await Queue.Dequeue()(request,token);
        }
    }
    sealed class Fixture:IDisposable
    {
        internal readonly Handler Handler=new Handler();internal readonly StudioGeminiSecrets Secrets;internal readonly StudioGeminiApiClient Client;internal string Id;
        internal Fixture(bool key=true){Secrets=new StudioGeminiSecrets(Path.Combine(root,Guid.NewGuid().ToString("N")));if(key)Secrets.Save(Key);Client=new StudioGeminiApiClient(Handler,Secrets);Client.ConnectAsync("","").GetAwaiter().GetResult();}
        internal async Task Ready(){Handler.Add(Response(Catalog()));await Client.AccountAsync();var models=await Client.ModelsAsync();Check(models.Count==1&&models[0].Id==Model,"catalog loaded");Id=await Client.ThreadAsync(null,project,Model);}
        internal Task<string> Run(string prompt="current prompt"){return Client.RunBoundAsync(Id,project,Model,prompt,Client.AccountIdentity.Fingerprint);}
        public void Dispose(){Client.Dispose();}
    }
    sealed class TinyStream:MemoryStream
    {
        internal TinyStream(string text):base(Encoding.UTF8.GetBytes(text)){}
        public override Task<int> ReadAsync(byte[] bytes,int offset,int length,CancellationToken token){return base.ReadAsync(bytes,offset,Math.Min(length,1),token);}
    }
    sealed class StallStream:Stream
    {
        readonly byte[] prefix;int position;readonly TaskCompletionSource<int> stalled=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Waiting=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StallStream(string text){prefix=Encoding.UTF8.GetBytes(text);}
        public override Task<int> ReadAsync(byte[] data,int offset,int length,CancellationToken token){if(position<prefix.Length){int n=Math.Min(length,prefix.Length-position);Buffer.BlockCopy(prefix,position,data,offset,n);position+=n;return Task.FromResult(n);}Waiting.TrySetResult(true);token.Register(()=>stalled.TrySetCanceled());return stalled.Task;}
        protected override void Dispose(bool disposing){stalled.TrySetException(new ObjectDisposedException("test stream"));base.Dispose(disposing);}
        public override bool CanRead{get{return true;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return false;}}public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
        public override void Flush(){}public override int Read(byte[] b,int o,int c){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}public override void SetLength(long n){throw new NotSupportedException();}public override void Write(byte[] b,int o,int c){throw new NotSupportedException();}
    }
    static List<StudioMessage> History(string prefix="past")
    {return new List<StudioMessage>{new StudioMessage{Id="u1",ExecutionId="e1",Provider="gemini-api",Model=Model,Role="user",State="completed",Text=prefix+" question"},new StudioMessage{Id="a1",ExecutionId="e1",Provider="gemini-api",Model=Model,Role="assistant",State="completed",Text=prefix+" answer"}};}
    static async Task RunTests()
    {
        var secrets=new StudioGeminiSecrets(Path.Combine(root,"secrets"));Check(secrets.Load()==null&&!secrets.HasKey,"missing secret is unauthenticated");secrets.Save(Key);Check(secrets.HasKey&&secrets.Load()==Key,"secret roundtrip");
        var raw=File.ReadAllBytes(secrets.FilePath);Check(!Encoding.UTF8.GetString(raw).Contains(Key),"secret encrypted on disk");secrets.Save(Key2);Check(secrets.Load()==Key2,"atomic replacement");Check(Directory.GetFiles(Path.GetDirectoryName(secrets.FilePath)).Length==1,"no stale plaintext or backup files");
        Fail(()=>secrets.Save(Key+"\r\n"),StudioFailureKind.Authentication,"reject header injection");Check(secrets.Load()==Key2,"invalid save preserves existing secret");
        File.WriteAllBytes(secrets.FilePath,new byte[]{1,2,3});Fail(()=>secrets.Load(),StudioFailureKind.Storage,"corrupt secret fails closed");Fail(()=>secrets.Save(Key),StudioFailureKind.Storage,"corrupt secret not overwritten");Check(File.ReadAllBytes(secrets.FilePath).Length==3,"corrupt file preserved");secrets.Clear();Check(!secrets.HasKey,"explicit clear repairs secret state");
        Directory.CreateDirectory(secrets.FilePath);Fail(()=>secrets.Load(),StudioFailureKind.Storage,"directory at secret path rejected");Directory.Delete(secrets.FilePath);
        Check(StudioAccountIdentity.FromGeminiApiKey(Key).Fingerprint!=StudioAccountIdentity.FromGeminiApiKey(Key2).Fingerprint,"key identity isolated");Check(StudioAccountIdentity.FromGeminiApiKey(Key).Fingerprint!=StudioAccountIdentity.Hash("key",Key),"provider fingerprint namespace");
        using(var production=(HttpClientHandler)typeof(StudioGeminiApiClient).GetMethod("CreateHandler",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null)){Check(!production.AllowAutoRedirect&&!production.UseCookies&&!production.UseProxy&&!production.UseDefaultCredentials,"production transport rejects redirects proxies cookies credentials");}
        using(var f=new Fixture(false)){var account=await f.Client.AccountAsync();Check(account["account"]==null&&!f.Client.AccountIdentity.IsAuthenticated,"missing API key account");await FailAsync(async()=>{await f.Client.ModelsAsync();},StudioFailureKind.Authentication,"missing key no list request");Check(f.Handler.Requests.Count==0,"missing key uses zero HTTP");}
        using(var f=new Fixture()) {
            f.Handler.Add(Response(Json(new {models=new[]{ModelRow(Model,"generateContent"),ModelRow("gemini-test-image","generateContent"),ModelRow("gemini-embedding-test","embedContent"),ModelRow("gemini-../evil","generateContent"),ModelRow("gemini-test-live","generateContent"),ModelRow("other-test","generateContent")},nextPageToken="a&b/next"})));
            f.Handler.Add(Response(Json(new {models=new[]{ModelRow("gemini-next-preview","generateContent"),ModelRow(Model,"generateContent")}})));
            var models=await f.Client.ModelsAsync();Check(models.Count==2,"catalog excludes non-text and unsafe paths, deduplicates");Check(models.Any(m=>m.Id=="gemini-next-preview"),"supported preview not hardcoded out");Check(f.Handler.Requests[1].Url.Contains("pageToken=a%26b%2Fnext"),"page token URI encoded");Check(f.Handler.Requests.All(r=>r.Key==Key),"key header only");
            await FailAsync(async()=>{await f.Client.ThreadAsync(null,project,"gemini-unknown");},StudioFailureKind.Model,"unknown model blocked");await FailAsync(async()=>{await f.Client.ThreadAsync(null,Path.Combine(root,"missing"),Model);},StudioFailureKind.Project,"missing project blocked");
        }
        using(var f=new Fixture()){string duplicate=Json(new{models=new object[0],nextPageToken="repeat"});f.Handler.Add(Response(duplicate));f.Handler.Add(Response(duplicate));await FailAsync(async()=>{await f.Client.ModelsAsync();},StudioFailureKind.Compatibility,"repeating page token bounded");Check(f.Handler.Requests.Count==2,"paging loop stopped");}
        foreach(int status in new[]{301,400,401,403,429,500})using(var f=new Fixture()){f.Handler.Add(Response("DO NOT DISPLAY SECRET OR ERROR DETAILS","application/json",status));await FailAsync(async()=>{await f.Client.ModelsAsync();},status==301?StudioFailureKind.Compatibility:status==429?StudioFailureKind.Quota:status==500?StudioFailureKind.Network:StudioFailureKind.Authentication,"list HTTP sanitized "+status);Check(f.Handler.Requests.Count==1,"HTTP errors not retried");}
        using(var f=new Fixture()) {
            await f.Ready();string shown="";int finals=0;f.Client.Message+=(id,text,final)=>{shown=final==Boolean.TrueString?text:shown+text;if(final==Boolean.TrueString)finals++;};
            var multiline=Chunk("안녕 ",null).Replace("\"content\":","\n\"content\":").Split('\n');
            string payload="\uFEFF: comment\r\nretry: 2000\r\n"+String.Join("\r\n",multiline.Select(x=>"data: "+x))+"\r\n\r\n"+"data: "+Json(new{candidates=new[]{new{content=new{parts=new[]{new{text="private thought",thought=true}}}}}})+"\n\n"+Sse("세계","STOP");
            f.Handler.Add(Streaming(new TinyStream(payload)));Check(await f.Run()=="completed","SSE final completion");Check(shown=="안녕 세계"&&finals==1,"UTF8 split, multiline SSE, thoughts ignored and final dedup");
            var request=f.Handler.Requests.Last();Check(request.Method=="POST"&&request.Url.EndsWith("/models/"+Model+":streamGenerateContent?alt=sse"),"official stream endpoint");
            Check(!request.Body.Contains(Key)&&!request.Body.Contains("tools")&&!request.Body.Contains("systemInstruction")&&!request.Body.Contains(project),"no key tools project or ambient system context in body");
            Check(request.Body.Split(new[]{"current prompt"},StringSplitOptions.None).Length==2,"current prompt exactly once");
        }
        using(var f=new Fixture()) {
            await f.Ready();var history=History();f.Client.PrepareHistory(f.Id,history);history[0].Text="mutated after snapshot";history.Clear();f.Handler.Add(Response(Sse("answer","STOP"),"text/event-stream"));await f.Run();string body=f.Handler.Requests.Last().Body;
            Check(body.Contains("past question")&&body.Contains("past answer")&&!body.Contains("mutated"),"history snapshot cloned");Check(body.IndexOf("past question",StringComparison.Ordinal)<body.IndexOf("past answer",StringComparison.Ordinal)&&body.IndexOf("past answer",StringComparison.Ordinal)<body.IndexOf("current prompt",StringComparison.Ordinal),"ordered transcript and current request");
            string second=await f.Client.ThreadAsync(null,project,Model);f.Id=second;f.Handler.Add(Response(Sse("isolated","STOP"),"text/event-stream"));await f.Run();Check(!f.Handler.Requests.Last().Body.Contains("past question"),"new thread history isolation");
            foreach(string change in new[]{"role","state","provider","execution","model","id"}){var invalid=History();if(change=="role")invalid[0].Role="assistant";if(change=="state")invalid[1].State="interrupted";if(change=="provider")invalid[0].Provider="claude";if(change=="execution")invalid[1].ExecutionId="other";if(change=="model")invalid[1].Model="other";if(change=="id")invalid[1].Id="u1";Fail(()=>f.Client.PrepareHistory(f.Id,invalid),StudioFailureKind.Context,"reject unsafe history "+change);}
            Fail(()=>f.Client.PrepareHistory(f.Id,History().Take(1).ToList()),StudioFailureKind.Context,"reject orphan history");var huge=History();huge[0].Text=new string('x',129000);Fail(()=>f.Client.PrepareHistory(f.Id,huge),StudioFailureKind.Context,"reject oversized history without truncation");
            string resumed=Guid.NewGuid().ToString("N");f.Id=await f.Client.ThreadAsync(resumed,project,Model);await FailAsync(async()=>{await f.Run();},StudioFailureKind.Context,"resumed thread requires explicit reconstruction");f.Client.PrepareHistory(f.Id,History("restored"));f.Handler.Add(Response(Sse("restored answer","STOP"),"text/event-stream"));await f.Run();Check(f.Handler.Requests.Last().Body.Contains("restored question"),"restart reconstruction sent");
        }
        using(var f=new Fixture()){await f.Ready();f.Secrets.Save(Key2);await FailAsync(async()=>{await f.Run();},StudioFailureKind.Authentication,"rotated key blocked before send");await FailAsync(async()=>{await f.Client.RunBoundAsync(f.Id,project,Model,"question",StudioAccountIdentity.FromGeminiApiKey(Key2).Fingerprint);},StudioFailureKind.Authentication,"caller cannot substitute key identity with stale catalog");Check(f.Handler.Requests.Count==1,"key mismatch sends no inference");await f.Client.AccountAsync();await FailAsync(async()=>{await f.Client.ThreadAsync(null,project,Model);},StudioFailureKind.Authentication,"key change invalidates model catalog");}
        using(var f=new Fixture()){await f.Ready();f.Handler.Add(Response("","application/json",403));await FailAsync(async()=>{await f.Client.ModelsAsync();},StudioFailureKind.Authentication,"failed refresh rejected");await FailAsync(async()=>{await f.Client.ThreadAsync(null,project,Model);},StudioFailureKind.Authentication,"failed refresh clears stale model catalog");}
        foreach(string broken in new[]{Sse("partial",null),Sse("partial",null)+"data: {bad}\n\n",Sse("partial",null)+"data: "+new string('x',1024*1024+1)+"\n\n"})using(var f=new Fixture()){await f.Ready();string shown="";f.Client.Message+=(id,text,final)=>shown+=text;f.Handler.Add(Response(broken,"text/event-stream"));await FailAsync(async()=>{await f.Run();},StudioFailureKind.Disconnected,"incomplete stream uncertain");Check(shown=="partial","partial answer survives failure");}
        using(var f=new Fixture()){await f.Ready();f.Handler.Add(Response("{}","application/json"));await FailAsync(async()=>{await f.Run();},StudioFailureKind.Disconnected,"non-SSE response uncertain");}
        using(var f=new Fixture()){await f.Ready();f.Handler.Add(Response("data: "+Json(new{promptFeedback=new{blockReason="SAFETY"}})+"\n\n","text/event-stream"));await FailAsync(async()=>{await f.Run();},StudioFailureKind.Permission,"confirmed prompt block");}
        using(var f=new Fixture()){await f.Ready();f.Handler.Add(Response(Sse("partial","SAFETY"),"text/event-stream"));await FailAsync(async()=>{await f.Run();},StudioFailureKind.Permission,"confirmed safety terminal");}
        using(var f=new Fixture()){await f.Ready();f.Handler.Add(Response("data: "+Json(new{error=new{code=429,message="KEY SHOULD NEVER DISPLAY"}})+"\n\n","text/event-stream"));await FailAsync(async()=>{await f.Run();},StudioFailureKind.Quota,"confirmed upstream error sanitized");}
        using(var f=new Fixture()){await f.Ready();string notice=null;f.Client.Notice+=(kind,text)=>notice=text;f.Handler.Add(Response(Sse("limit","MAX_TOKENS"),"text/event-stream"));Check(await f.Run()=="completed"&&notice=="output-limit","confirmed token limit completion notice");}
        using(var f=new Fixture()){await f.Ready();var stalled=new StallStream(Sse("partial",null));f.Handler.Add(Streaming(stalled));string shown="";f.Client.Message+=(id,text,final)=>shown+=text;Task pending=f.Run();await stalled.Waiting.Task;await f.Client.InterruptAsync();await FailAsync(()=>pending,StudioFailureKind.Disconnected,"REST cancel stays uncertain");Check(shown=="partial","cancel preserves partial response");Check(f.Handler.Requests.Count==2,"cancel never auto retries");}
        using(var f=new Fixture()){await f.Ready();var stalled=new StallStream("");f.Handler.Add(Streaming(stalled));Task pending=f.Run();await stalled.Waiting.Task;f.Client.Dispose();await FailAsync(()=>pending,StudioFailureKind.Disconnected,"dispose aborts only owned request");Check(!f.Client.Connected,"disposed connection false");}
        using(var f=new Fixture()){await f.Ready();await FailAsync(async()=>{await f.Run(new string('x',32001));},StudioFailureKind.Context,"oversized prompt rejected before HTTP");Check(f.Handler.Requests.Count==1,"oversized prompt no inference");}
    }
    public static int Main()
    {
        root=Path.GetFullPath(Path.Combine("outputs","studio-gemini-api-tests","fixture-"+Guid.NewGuid().ToString("N")));project=Path.Combine(root,"project");Directory.CreateDirectory(project);
        try{RunTests().GetAwaiter().GetResult();Console.WriteLine("PASS Gemini API: "+count+" isolated checks (no network, no real key). Fixture: "+root);return 0;}
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
