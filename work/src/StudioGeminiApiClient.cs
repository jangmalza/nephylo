using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // Explicit user-owned Developer API key, text-only requests, no tools or ambient credentials.
    internal sealed class StudioGeminiApiClient : IStudioAgentClient, IStudioConversationClient
    {
        const string Origin="https://generativelanguage.googleapis.com/v1beta/";
        const int MaxDocument=2*1024*1024,MaxLine=1024*1024,MaxAnswer=512000,MaxHistory=128000;
        readonly object gate=new object();
        readonly HttpClient http;
        readonly string connectionId=Guid.NewGuid().ToString("N");
        readonly Dictionary<string,Session> sessions=new Dictionary<string,Session>(StringComparer.Ordinal);
        Dictionary<string,ModelSpec> catalog=new Dictionary<string,ModelSpec>(StringComparer.Ordinal);
        StudioGeminiSecrets secrets;
        StudioAccountIdentity identity;
        string catalogIdentity;
        bool connected,disposed,busy;
        CancellationTokenSource activeCancellation;
        sealed class ModelSpec { internal string Id,Label;internal int InputLimit,OutputLimit; }
        sealed class Session { internal string Model,Project;internal List<Dictionary<string,object>> History; }
        public string Provider { get { return "gemini-api"; } }
        public string Profile { get { return "ade-gemini-api"; } }
        public string ConnectionId { get { return connectionId; } }
        public bool Connected { get { lock(gate)return connected&&!disposed; } }
        public StudioAccountIdentity AccountIdentity { get { lock(gate)return identity; } }
        public event Action<string,string> Notice;
        public event Action<string,string,string> Message;
        internal StudioGeminiApiClient() : this(CreateHandler(),null) { }
        internal StudioGeminiApiClient(HttpMessageHandler handler,StudioGeminiSecrets secrets)
        { if(handler==null)throw new ArgumentNullException("handler");http=new HttpClient(handler,true){Timeout=Timeout.InfiniteTimeSpan};this.secrets=secrets; }
        static HttpClientHandler CreateHandler()
        { return new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false,UseProxy=false,AutomaticDecompression=DecompressionMethods.None }; }
        static JavaScriptSerializer Json() { return new JavaScriptSerializer {MaxJsonLength=MaxDocument,RecursionLimit=48}; }
        static Dictionary<string,object> Obj(Dictionary<string,object> row,string name) { return StudioCodexClient.Obj(row,name); }
        static string Str(Dictionary<string,object> row,string name) { return StudioCodexClient.Str(row,name); }
        static IEnumerable<object> ArrayAt(Dictionary<string,object> row,string name)
        { object value;if(row==null||!row.TryGetValue(name,out value)||value==null)return new object[0];var items=value as IEnumerable;return items==null||value is string?new object[0]:items.Cast<object>(); }
        static int Number(Dictionary<string,object> row,string name)
        { object value;int result;return row!=null&&row.TryGetValue(name,out value)&&Int32.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),out result)&&result>0?result:0; }
        static bool True(Dictionary<string,object> row,string name) { object value;return row!=null&&row.TryGetValue(name,out value)&&Object.Equals(value,true); }
        void Tell(string kind,string text) { var h=Notice;if(h!=null)h(kind,text); }
        void Emit(string text,bool final) { var h=Message;if(h!=null)h("gemini-answer",text,final.ToString()); }
        void RequireConnected() { if(!Connected)throw new StudioFailure(StudioFailureKind.Disconnected); }
        public Task ConnectAsync(string unused,string home)
        {
            lock(gate){if(disposed)throw new ObjectDisposedException("StudioGeminiApiClient");if(connected)throw new InvalidOperationException();if(secrets==null)secrets=new StudioGeminiSecrets(home);secrets.Load();connected=true;}
            return Task.FromResult(0);
        }
        public Task<Dictionary<string,object>> AccountAsync()
        {
            RequireConnected();var next=StudioAccountIdentity.FromGeminiApiKey(secrets.Load());bool changed;
            lock(gate){changed=identity!=null&&(identity.Fingerprint!=next.Fingerprint||identity.IsAuthenticated!=next.IsAuthenticated);identity=next;if(changed){catalog.Clear();catalogIdentity=null;}}
            if(changed)Tell("account","changed");
            return Task.FromResult(new Dictionary<string,object>{{"account",next.IsAuthenticated?(object)new Dictionary<string,object>{{"type",next.Type},{"accountId",next.Fingerprint}}:null}});
        }
        public Task<StudioQuotaSnapshot> QuotaAsync() { return Task.FromResult(StudioQuotaSnapshot.Unknown(connectionId,AccountIdentity)); }
        string CurrentKey(string expected)
        {
            var key=secrets.Load();var check=StudioAccountIdentity.FromGeminiApiKey(key);
            if(!check.IsAuthenticated||String.IsNullOrEmpty(expected)||check.Fingerprint!=expected)throw new StudioFailure(StudioFailureKind.Authentication);
            return key;
        }
        public async Task<List<StudioModel>> ModelsAsync()
        {
            RequireConnected();await AccountAsync();var expected=AccountIdentity.Fingerprint;var key=CurrentKey(expected);
            lock(gate){catalog.Clear();catalogIdentity=null;}
            var found=new Dictionary<string,ModelSpec>(StringComparer.Ordinal);var tokens=new HashSet<string>(StringComparer.Ordinal);string page=null;
            using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40))) {
                try {
                    for(int index=0;index<20;index++) {
                        string path="models?pageSize=100"+(page==null?"":"&pageToken="+Uri.EscapeDataString(page));
                        using(var request=Request(HttpMethod.Get,path,key))
                        using(var result=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false)) {
                            CheckStatus(result.StatusCode,false);
                            var body=Parse(await ReadDocument(result.Content,timeout.Token).ConfigureAwait(false));
                            foreach(var item in ArrayAt(body,"models")) {
                                var row=item as Dictionary<string,object>;var name=Str(row,"name");
                                if(name==null||!name.StartsWith("models/",StringComparison.Ordinal))continue;
                                var id=name.Substring(7);
                                if(!AllowedModel(id)||!ArrayAt(row,"supportedGenerationMethods").Any(x=>Object.Equals(x,"generateContent")))continue;
                                var label=StudioQuotaParser.SafeText(Str(row,"displayName"),120)??id;
                                found[id]=new ModelSpec {Id=id,Label=label,InputLimit=Number(row,"inputTokenLimit"),OutputLimit=Number(row,"outputTokenLimit")};
                                if(found.Count>500)throw new StudioFailure(StudioFailureKind.Compatibility);
                            }
                            page=Str(body,"nextPageToken");
                            if(String.IsNullOrEmpty(page))break;
                            if(page.Length>2048||page.Any(Char.IsControl)||!tokens.Add(page)||index==19)throw new StudioFailure(StudioFailureKind.Compatibility);
                        }
                    }
                    CurrentKey(expected);
                    lock(gate){if(disposed)throw new StudioFailure(StudioFailureKind.Disconnected);catalog=found;catalogIdentity=expected;}
                    return found.Values.OrderBy(x=>x.Label,StringComparer.OrdinalIgnoreCase).Select(x=>new StudioModel{Id=x.Id,Label=x.Label}).ToList();
                } catch(StudioFailure){throw;}
                catch(OperationCanceledException){throw new StudioFailure(StudioFailureKind.Timeout);}
                catch {throw new StudioFailure(timeout.IsCancellationRequested?StudioFailureKind.Timeout:StudioFailureKind.Network);}
            }
        }
        static bool AllowedModel(string id)
        {
            if(id==null||id.Length>128||!Regex.IsMatch(id,@"\Agemini-[a-zA-Z0-9][a-zA-Z0-9._-]*\z"))return false;
            string lower=id.ToLowerInvariant();
            return !new[]{"embedding","-image","-audio","-tts","-live","-robotics","-computer-use","-aqa"}.Any(lower.Contains);
        }
        ModelSpec Model(string id)
        {
            lock(gate){ModelSpec result;if(identity==null||!identity.IsAuthenticated||catalogIdentity!=identity.Fingerprint)throw new StudioFailure(StudioFailureKind.Authentication);if(!AllowedModel(id)||!catalog.TryGetValue(id,out result))throw new StudioFailure(StudioFailureKind.Model);return result;}
        }
        static string Project(string project)
        {
            try { if(String.IsNullOrWhiteSpace(project)||!Path.IsPathRooted(project)||!Directory.Exists(project))throw new IOException();return Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar); }
            catch {throw new StudioFailure(StudioFailureKind.Project);}
        }
        static void SessionId(string id) { Guid parsed;if(id==null||!Guid.TryParseExact(id,"N",out parsed))throw new StudioFailure(StudioFailureKind.Context); }
        public Task<string> ThreadAsync(string id,string project,string model)
        {
            RequireConnected();Model(model);project=Project(project);bool fresh=String.IsNullOrEmpty(id);if(fresh)id=Guid.NewGuid().ToString("N");SessionId(id);
            lock(gate){Session saved;if(sessions.TryGetValue(id,out saved)){if(saved.Model!=model||!String.Equals(saved.Project,project,StringComparison.OrdinalIgnoreCase))throw new StudioFailure(StudioFailureKind.Context);}
                else sessions[id]=new Session{Model=model,Project=project,History=fresh?new List<Dictionary<string,object>>():null};}
            return Task.FromResult(id);
        }
        static Dictionary<string,object> Content(string role,string text)
        { return new Dictionary<string,object>{{"role",role},{"parts",new object[]{new Dictionary<string,object>{{"text",text}}}}}; }
        public void PrepareHistory(string threadId,IList<StudioMessage> messages)
        {
            RequireConnected();SessionId(threadId);if(messages==null||messages.Count>200||messages.Count%2!=0)throw new StudioFailure(StudioFailureKind.Context);
            Session session;lock(gate){if(busy||!sessions.TryGetValue(threadId,out session))throw new StudioFailure(StudioFailureKind.Context);}
            var contents=new List<Dictionary<string,object>>();string execution=null;int total=0;var ids=new HashSet<string>(StringComparer.Ordinal);
            for(int i=0;i<messages.Count;i++) {
                var m=messages[i];if(m==null||m.State!="completed"||m.Provider!=Provider||m.Model!=session.Model||String.IsNullOrWhiteSpace(m.ExecutionId)||String.IsNullOrWhiteSpace(m.Id)||!ids.Add(m.Id)||m.Role!=(i%2==0?"user":"assistant")||String.IsNullOrWhiteSpace(m.Text))throw new StudioFailure(StudioFailureKind.Context);
                if(execution==null)execution=m.ExecutionId;else if(execution!=m.ExecutionId)throw new StudioFailure(StudioFailureKind.Context);
                total+=Encoding.UTF8.GetByteCount(m.Text);if(total>MaxHistory)throw new StudioFailure(StudioFailureKind.Context);
                contents.Add(Content(i%2==0?"user":"model",m.Text));
            }
            lock(gate){if(busy)throw new StudioFailure(StudioFailureKind.Context);session.History=contents;}
        }
        public async Task<string> RunBoundAsync(string threadId,string project,string model,string prompt,string expectedAccountFingerprint)
        {
            RequireConnected();SessionId(threadId);var spec=Model(model);project=Project(project);
            if(String.IsNullOrWhiteSpace(prompt)||prompt.Length>32000)throw new StudioFailure(StudioFailureKind.Context);
            var key=CurrentKey(expectedAccountFingerprint);var rows=new List<Dictionary<string,object>>();CancellationTokenSource cancellation;
            lock(gate){Session session;if(busy)throw new InvalidOperationException();if(identity==null||identity.Fingerprint!=expectedAccountFingerprint||catalogIdentity!=expectedAccountFingerprint)throw new StudioFailure(StudioFailureKind.Authentication);if(!sessions.TryGetValue(threadId,out session)||session.Model!=model||!String.Equals(session.Project,project,StringComparison.OrdinalIgnoreCase)||session.History==null)throw new StudioFailure(StudioFailureKind.Context);
                rows.AddRange(session.History);rows.Add(Content("user",prompt));busy=true;activeCancellation=cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(4));}
            bool sent=false,terminal=false;var answer=new StringBuilder();
            try {
                var payload=Json().Serialize(new {contents=rows,generationConfig=new {candidateCount=1,maxOutputTokens=spec.OutputLimit==0?8192:Math.Min(8192,spec.OutputLimit)}});
                int bytes=Encoding.UTF8.GetByteCount(payload);
                // Bytes are a conservative upper bound for text tokens; never silently truncate history.
                if(bytes>512000||spec.InputLimit>0&&bytes>spec.InputLimit)throw new StudioFailure(StudioFailureKind.Context);
                CurrentKey(expectedAccountFingerprint);
                using(var request=Request(HttpMethod.Post,"models/"+model+":streamGenerateContent?alt=sse",key)) {
                    request.Content=new StringContent(payload,Encoding.UTF8,"application/json");
                    sent=true;
                    using(var result=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation.Token).ConfigureAwait(false)) {
                        if(!result.IsSuccessStatusCode){terminal=true;CheckStatus(result.StatusCode,true);}
                        var media=result.Content.Headers.ContentType;
                        if(media==null||!String.Equals(media.MediaType,"text/event-stream",StringComparison.OrdinalIgnoreCase))throw new StudioFailure(StudioFailureKind.Compatibility);
                        using(var stream=await result.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using(cancellation.Token.Register(()=>{try{stream.Dispose();}catch{}})) {
                            var reader=new SseReader(stream,cancellation.Token);
                            while(true) {
                                string data=await reader.Next().ConfigureAwait(false);if(data==null)throw new StudioFailure(StudioFailureKind.Disconnected);
                                var chunk=Parse(data);var error=Obj(chunk,"error");
                                if(error!=null){terminal=true;throw ApiError(error);}
                                var feedback=Obj(chunk,"promptFeedback");
                                if(!String.IsNullOrEmpty(Str(feedback,"blockReason"))){terminal=true;throw new StudioFailure(StudioFailureKind.Permission);}
                                var candidates=ArrayAt(chunk,"candidates").ToList();
                                if(candidates.Count==0)continue;if(candidates.Count!=1)throw new StudioFailure(StudioFailureKind.Compatibility);
                                var candidate=candidates[0] as Dictionary<string,object>;if(candidate==null||Number(candidate,"index")>0)throw new StudioFailure(StudioFailureKind.Compatibility);
                                var content=Obj(candidate,"content");
                                foreach(var item in ArrayAt(content,"parts")) {
                                    var part=item as Dictionary<string,object>;if(part==null)throw new StudioFailure(StudioFailureKind.Compatibility);
                                    if(Obj(part,"functionCall")!=null||Obj(part,"executableCode")!=null||Obj(part,"inlineData")!=null)throw new StudioFailure(StudioFailureKind.Compatibility);
                                    if(True(part,"thought"))continue;
                                    var delta=Str(part,"text");if(delta==null)continue;
                                    if(answer.Length+delta.Length>MaxAnswer)throw new StudioFailure(StudioFailureKind.Context);
                                    answer.Append(delta);Emit(delta,false);
                                }
                                string reason=Str(candidate,"finishReason");
                                if(String.IsNullOrEmpty(reason)||reason=="FINISH_REASON_UNSPECIFIED")continue;
                                terminal=true;
                                if(reason!="STOP"&&reason!="MAX_TOKENS")throw new StudioFailure(reason=="SAFETY"||reason=="RECITATION"||reason=="BLOCKLIST"||reason=="PROHIBITED_CONTENT"||reason=="SPII"||reason=="MALFORMED_FUNCTION_CALL"||reason=="UNEXPECTED_TOOL_CALL"?StudioFailureKind.Permission:StudioFailureKind.Compatibility);
                                if(answer.Length==0)throw new StudioFailure(StudioFailureKind.Compatibility);
                                Emit(answer.ToString(),true);if(reason=="MAX_TOKENS")Tell("response","output-limit");
                                return "completed";
                            }
                        }
                    }
                }
            } catch(Exception error) {
                if(sent&&!terminal)throw new StudioFailure(StudioFailureKind.Disconnected);
                var known=error as StudioFailure;if(known!=null)throw known;
                throw new StudioFailure(error is OperationCanceledException?StudioFailureKind.Timeout:StudioFailureKind.Network);
            } finally { lock(gate){busy=false;if(activeCancellation==cancellation)activeCancellation=null;}cancellation.Dispose(); }
        }
        public Task InterruptAsync()
        { lock(gate){if(activeCancellation!=null)activeCancellation.Cancel();}return Task.FromResult(0); }
        static HttpRequestMessage Request(HttpMethod method,string path,string key)
        {
            var request=new HttpRequestMessage(method,new Uri(Origin+path));request.Headers.Add("x-goog-api-key",key);request.Headers.TryAddWithoutValidation("Accept",method==HttpMethod.Get?"application/json":"text/event-stream");return request;
        }
        static void CheckStatus(HttpStatusCode status,bool inference)
        {
            int code=(int)status;if(code>=200&&code<300)return;
            if(code==401||code==403)throw new StudioFailure(StudioFailureKind.Authentication);
            if(code==429)throw new StudioFailure(StudioFailureKind.Quota);
            if(code==404)throw new StudioFailure(StudioFailureKind.Model);
            if(code==400)throw new StudioFailure(inference?StudioFailureKind.Context:StudioFailureKind.Authentication);
            throw new StudioFailure(code>=500?StudioFailureKind.Network:StudioFailureKind.Compatibility);
        }
        static StudioFailure ApiError(Dictionary<string,object> row)
        {
            int code=Number(row,"code");
            if(code==401||code==403)return new StudioFailure(StudioFailureKind.Authentication);
            if(code==429)return new StudioFailure(StudioFailureKind.Quota);
            if(code==400)return new StudioFailure(StudioFailureKind.Context);
            if(code==404)return new StudioFailure(StudioFailureKind.Model);
            return new StudioFailure(code>=500?StudioFailureKind.Network:StudioFailureKind.Unknown);
        }
        static Dictionary<string,object> Parse(string text)
        { try {var result=Json().DeserializeObject(text) as Dictionary<string,object>;if(result==null)throw new InvalidDataException();return result;}catch{throw new StudioFailure(StudioFailureKind.Compatibility);} }
        static async Task<string> ReadDocument(HttpContent content,CancellationToken token)
        {
            if(content.Headers.ContentLength>MaxDocument)throw new StudioFailure(StudioFailureKind.Compatibility);
            using(var stream=await content.ReadAsStreamAsync().ConfigureAwait(false))
            using(token.Register(()=>{try{stream.Dispose();}catch{}}))
            using(var buffer=new MemoryStream()) {
                var bytes=new byte[8192];int count;
                while((count=await stream.ReadAsync(bytes,0,bytes.Length,token).ConfigureAwait(false))>0){if(buffer.Length+count>MaxDocument)throw new StudioFailure(StudioFailureKind.Compatibility);buffer.Write(bytes,0,count);}
                return new UTF8Encoding(false,true).GetString(buffer.ToArray());
            }
        }
        sealed class SseReader
        {
            readonly Stream stream;readonly CancellationToken token;readonly Decoder decoder=new UTF8Encoding(false,true).GetDecoder();
            readonly byte[] bytes=new byte[4096];readonly char[] chars=new char[4096];readonly StringBuilder line=new StringBuilder(),data=new StringBuilder();
            int index,count,total;bool previousCr,eof,first=true;
            internal SseReader(Stream stream,CancellationToken token){this.stream=stream;this.token=token;}
            internal async Task<string> Next()
            {
                while(true) {
                    if(index==count) {
                        if(eof)return null;
                        int read=await stream.ReadAsync(bytes,0,bytes.Length,token).ConfigureAwait(false);total+=read;
                        if(total>4*1024*1024)throw new InvalidDataException();
                        count=decoder.GetChars(bytes,0,read,chars,0,read==0);index=0;
                        if(read==0){eof=true;return null;}if(count==0)continue;
                    }
                    char c=chars[index++];if(first){first=false;if(c=='\uFEFF')continue;}
                    if(previousCr){previousCr=false;if(c=='\n')continue;}
                    if(c=='\r'||c=='\n') {
                        previousCr=c=='\r';string value=line.ToString();line.Length=0;
                        if(value.Length==0&&data.Length>0){string payload=data.ToString(0,data.Length-1);data.Length=0;return payload;}
                        if(value.StartsWith("data:",StringComparison.Ordinal)){value=value.Substring(5);if(value.StartsWith(" ",StringComparison.Ordinal))value=value.Substring(1);if(data.Length+value.Length>MaxLine)throw new InvalidDataException();data.Append(value).Append('\n');}
                        continue;
                    }
                    if(line.Length>=MaxLine)throw new InvalidDataException();line.Append(c);
                }
            }
        }
        public void Dispose()
        {
            lock(gate){if(disposed)return;disposed=true;connected=false;if(activeCancellation!=null)activeCancellation.Cancel();catalog.Clear();sessions.Clear();identity=null;catalogIdentity=null;}
            http.Dispose();
        }
    }
}
