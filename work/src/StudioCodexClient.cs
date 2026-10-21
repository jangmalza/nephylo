using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    internal sealed class StudioModel
    {
        internal string Id; internal string Label;
        public override string ToString() { return Label; }
    }
    // Independent from the quota poller. Only this child owns Studio's official Codex auth and threads.
    internal sealed class StudioCodexClient : IStudioAgentClient
    {
        public string Provider { get { return "codex"; } }
        public string Profile { get { return "ade-codex"; } }
        readonly object gate=new object(), writer=new object();
        readonly Dictionary<int,TaskCompletionSource<Dictionary<string,object>>> pending=new Dictionary<int,TaskCompletionSource<Dictionary<string,object>>>();
        Process child; bool disposed,ended; int nextId; string thread, turn; bool stopping;
        readonly string connectionId=Guid.NewGuid().ToString("N");
        StudioAccountIdentity accountIdentity;
        StudioQuotaSnapshot quota;
        Task<StudioQuotaSnapshot> quotaFlight;
        int accountRevision,quotaRevision;
        sealed class AccountBinding
        {
            internal int Revision;
            internal string Fingerprint;
        }
        TaskCompletionSource<string> completion;
        public event Action<string,string> Notice;
        public event Action<string,string,string> Message;
        public bool Connected { get { lock(gate)return child!=null&&!disposed&&!ended; } }
        public string ConnectionId { get { return connectionId; } }
        public StudioAccountIdentity AccountIdentity { get { lock(gate)return accountIdentity; } }
        internal StudioQuotaSnapshot Quota { get { lock(gate)return quota; } }
        internal static string Str(Dictionary<string,object> d,string key) { object v;return d!=null&&d.TryGetValue(key,out v)?v as string:null; }
        internal static Dictionary<string,object> Obj(Dictionary<string,object> d,string key) { object v;return d!=null&&d.TryGetValue(key,out v)?v as Dictionary<string,object>:null; }
        internal static IEnumerable<Dictionary<string,object>> Items(Dictionary<string,object> d,string key) { object v;if(d==null||!d.TryGetValue(key,out v)||!(v is IEnumerable))yield break;foreach(var x in (IEnumerable)v){var row=x as Dictionary<string,object>;if(row!=null)yield return row;} }
        void Tell(string kind,string value) { var h=Notice;if(h!=null)h(kind,value); }
        public async Task ConnectAsync(string executable,string home)
        {
            try{Directory.CreateDirectory(home);if((File.GetAttributes(home)&FileAttributes.ReparsePoint)!=0)throw new IOException();}
            catch{throw new StudioFailure(StudioFailureKind.Storage);}
            var info=new ProcessStartInfo(executable,"app-server") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=home };
            info.EnvironmentVariables["CODEX_HOME"]=home;
            foreach(var name in new[]{"OPENAI_API_KEY","CODEX_API_KEY","CODEX_ACCESS_TOKEN","OPENAI_BASE_URL"})info.EnvironmentVariables.Remove(name);
            info.EnvironmentVariables["RUST_LOG"]="error";
            lock(gate){if(disposed)throw new ObjectDisposedException("StudioCodexClient");if(child!=null)throw new InvalidOperationException();try{child=Process.Start(info);child.StandardInput.AutoFlush=true;}catch(System.ComponentModel.Win32Exception){throw new StudioFailure(StudioFailureKind.Installation);}}
            StartReaders();
            await Request("initialize",new { clientInfo=new { name="nephylo_studio",title="Nephylo ADE",version="0.1.0" } });
            Write(new {method="initialized"});
        }
        public async Task<Dictionary<string,object>> AccountAsync()
        {
            int revision;lock(gate)revision=accountRevision;
            var result=await Request("account/read",new {refreshToken=false});
            var identity=StudioAccountIdentity.FromResponse(result);
            lock(gate){
                if(revision==accountRevision&&!disposed&&!ended){
                    if(accountIdentity==null||identity.Fingerprint!=accountIdentity.Fingerprint||identity.Type!=accountIdentity.Type)quota=null;
                    accountIdentity=identity;
                }
            }
            return result;
        }
        // Quota is optional and is read from this exact ADE process/profile only.
        // Unsupported or slow quota RPCs never tear down a working chat connection.
        public Task<StudioQuotaSnapshot> QuotaAsync()
        {
            lock(gate){
                if(quotaFlight!=null)return quotaFlight;
                var identity=accountIdentity;var revision=accountRevision;var observationRevision=quotaRevision;
                if(disposed||ended||child==null||identity==null||!identity.IsAuthenticated)return Task.FromResult(StudioQuotaSnapshot.Unknown(connectionId,identity));
                quotaFlight=Task.Run(()=>ReadQuotaAsync(identity,revision,observationRevision));return quotaFlight;
            }
        }
        async Task<StudioQuotaSnapshot> ReadQuotaAsync(StudioAccountIdentity identity,int revision,int observationRevision)
        {
            StudioQuotaSnapshot result;
            try{
                var response=await RequestCore("account/rateLimits/read",new{},5000,false);
                result=StudioQuotaParser.Parse(response,identity,connectionId,DateTime.UtcNow);
            }catch{result=StudioQuotaSnapshot.Unknown(connectionId,identity);}
            lock(gate){
                quotaFlight=null;
                if(!disposed&&!ended&&revision==accountRevision&&observationRevision==quotaRevision&&accountIdentity==identity)quota=result;
                else result=StudioQuotaSnapshot.Unknown(connectionId,null);
            }
            return result;
        }
        void StartReaders() { Task.Run(()=>Read()); Task.Run(()=>{try{while(child.StandardError.ReadLine()!=null){}}catch{}}); }
        internal async Task<string> LoginAsync()
        {
            var r=await Request("account/login/start",new{type="chatgpt"});var url=Str(r,"authUrl");
            if(!CodexBrowserRecovery.IsAllowed(url))throw new StudioFailure(StudioFailureKind.Compatibility);return url;
        }
        public async Task<List<StudioModel>> ModelsAsync()
        {
            var result=new List<StudioModel>();string cursor=null;
            for(int page=0;page<10;page++){
                var r=await Request("model/list",new{limit=100,includeHidden=false,cursor=cursor});
                foreach(var row in Items(r,"data")){var id=Str(row,"model")??Str(row,"id");if(!String.IsNullOrEmpty(id)&&!result.Any(m=>m.Id==id))result.Add(new StudioModel{Id=id,Label=Str(row,"displayName")??id});}
                var next=Str(r,"nextCursor");if(String.IsNullOrEmpty(next)||next==cursor)break;cursor=next;
            }return result;
        }
        public async Task<string> ThreadAsync(string id,string project,string model)
        {
            if(String.IsNullOrWhiteSpace(project)||!Directory.Exists(project))throw new StudioFailure(StudioFailureKind.Project);
            var parameters=new Dictionary<string,object>{{"cwd",project},{"model",model},{"approvalPolicy","untrusted"},{"approvalsReviewer","user"},{"sandbox","read-only"}};
            if(!String.IsNullOrEmpty(id)){parameters["threadId"]=id;parameters["excludeTurns"]=true;}
            var r=await Request(String.IsNullOrEmpty(id)?"thread/start":"thread/resume",parameters);
            var sandbox=Obj(r,"sandbox");object network;
            if(Str(sandbox,"type")!="readOnly"||(sandbox.TryGetValue("networkAccess",out network)&&Object.Equals(network,true))||Str(r,"approvalPolicy")!="untrusted"||Str(r,"approvalsReviewer")!="user")throw new StudioFailure(StudioFailureKind.Permission);
            var value=Str(Obj(r,"thread"),"id");if(String.IsNullOrEmpty(value))throw new InvalidDataException();return value;
        }
        internal Task<string> RunAsync(string threadId,string project,string model,string prompt)
        { return RunCoreAsync(threadId,project,model,prompt,null); }
        public async Task<string> RunBoundAsync(string threadId,string project,string model,string prompt,string expectedAccountFingerprint)
        {
            AccountBinding binding;
            lock(gate){binding=new AccountBinding{Revision=accountRevision,Fingerprint=expectedAccountFingerprint};ValidateAccountBinding(binding);}
            return await RunCoreAsync(threadId,project,model,prompt,binding);
        }
        void ValidateAccountBinding(AccountBinding binding)
        {
            if(binding!=null&&(binding.Revision!=accountRevision||accountIdentity==null||!accountIdentity.IsAuthenticated||!String.Equals(binding.Fingerprint,accountIdentity.Fingerprint,StringComparison.Ordinal)))throw new StudioFailure(StudioFailureKind.Authentication);
        }
        async Task<string> RunCoreAsync(string threadId,string project,string model,string prompt,AccountBinding binding)
        {
            TaskCompletionSource<string> done;
            lock(gate){if(completion!=null)throw new InvalidOperationException();thread=threadId;turn=null;stopping=false;completion=done=new TaskCompletionSource<string>();}
            try {
                var r=await RequestCore("turn/start",new {threadId=threadId,cwd=project,model=model,approvalPolicy="untrusted",approvalsReviewer="user",sandboxPolicy=new{type="readOnly",networkAccess=false},input=new[]{new{type="text",text=prompt}}},30000,true,binding);
                lock(gate){if(turn==null)turn=Str(Obj(r,"turn"),"id");}
                if(stopping)await InterruptAsync();
                if(await Task.WhenAny(done.Task,Task.Delay(TimeSpan.FromMinutes(15)))!=done.Task){Dispose();throw new TimeoutException();}
                return await done.Task;
            } finally {lock(gate){if(completion==done){completion=null;thread=null;turn=null;}}}
        }
        public async Task InterruptAsync()
        {
            string t,u;lock(gate){stopping=true;t=thread;u=turn;}
            if(t==null||u==null)return;
            await Request("turn/interrupt",new{threadId=t,turnId=u});
            Task<string> waiting;lock(gate)waiting=completion==null?null:completion.Task;
            if(waiting!=null&&await Task.WhenAny(waiting,Task.Delay(10000))!=waiting)Dispose();
        }
        internal Task<Dictionary<string,object>> Request(string method,object parameters)
        { return RequestCore(method,parameters,30000,true); }
        async Task<Dictionary<string,object>> RequestCore(string method,object parameters,int timeoutMilliseconds,bool disconnectOnTimeout,AccountBinding binding=null)
        {
            int id;var tcs=new TaskCompletionSource<Dictionary<string,object>>();
            lock(gate){if(disposed)throw new ObjectDisposedException("StudioCodexClient");id=++nextId;pending[id]=tcs;}
            try{Write(new{id=id,method=method,@params=parameters},binding);if(await Task.WhenAny(tcs.Task,Task.Delay(timeoutMilliseconds))!=tcs.Task){if(disconnectOnTimeout)Dispose();throw new TimeoutException();}return await tcs.Task;}
            finally{lock(gate)pending.Remove(id);}
        }
        void Write(object value,AccountBinding binding=null)
        {
            var bytes=new UTF8Encoding(false).GetBytes(new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.Serialize(value)+"\n");
            // Always acquire gate before writer, including account-change notifications.
            // Check again at the actual transport write: a UI notice may still be queued.
            lock(gate){lock(writer){if(disposed||ended||child==null||child.HasExited)throw new IOException();ValidateAccountBinding(binding);child.StandardInput.BaseStream.Write(bytes,0,bytes.Length);child.StandardInput.BaseStream.Flush();}}
        }
        void Read()
        {
            try{string line;while((line=child.StandardOutput.ReadLine())!=null){if(line.Length>4*1024*1024)throw new InvalidDataException();var msg=new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.DeserializeObject(line) as Dictionary<string,object>;if(msg!=null)Receive(msg);}}
            catch{}finally{lock(gate)ended=true;FailPending();Tell("disconnected","");try{child.Dispose();}catch{}}
        }
        internal void Receive(Dictionary<string,object> msg)
        {
            object id;var method=Str(msg,"method");var p=Obj(msg,"params");
            if(msg.TryGetValue("id",out id)){
                if(method!=null){ReplyDenied(id,method);Tell("denied",method);return;}
                int n;if(!Int32.TryParse(Convert.ToString(id),out n))return;
                TaskCompletionSource<Dictionary<string,object>> tcs;lock(gate){if(!pending.TryGetValue(n,out tcs))return;}
                if(msg.ContainsKey("error"))tcs.TrySetException(StudioFailure.FromServer(Obj(msg,"error")));else tcs.TrySetResult(Obj(msg,"result")??new Dictionary<string,object>());return;
            }
            if(method=="account/login/completed"){Tell("login",p!=null&&p.ContainsKey("success")&&Object.Equals(p["success"],true)?"success":"failed");return;}
            if(method=="account/updated"){
                lock(gate){accountRevision++;quotaRevision++;accountIdentity=null;quota=null;}
                Tell("account","changed");return;
            }
            if(method=="account/rateLimits/updated"){
                // Rolling notifications are sparse and may not contain account identity.
                // Refetch rather than combining a new percentage with old account metadata.
                lock(gate){quotaRevision++;quota=null;}
                Tell("quota","changed");return;
            }
            lock(gate){if(completion==null||Str(p,"threadId")!=thread)return;}
            var t=Obj(p,"turn");
            if(method=="turn/started"){lock(gate)turn=Str(t,"id");return;}
            var eventTurn=Str(p,"turnId");lock(gate){if(turn!=null&&eventTurn!=null&&turn!=eventTurn)return;}
            if(method=="item/agentMessage/delta"){var h=Message;if(h!=null)h(Str(p,"itemId")??"answer",Str(p,"delta")??"",false.ToString());}
            else if(method=="item/completed"){
                var item=Obj(p,"item");if(Str(item,"type")=="agentMessage"){var h=Message;if(h!=null)h(Str(item,"id")??"answer",Str(item,"text")??"",true.ToString());}
            }else if(method=="turn/completed"){
                lock(gate){if(turn!=null&&Str(t,"id")!=turn)return;var state=Str(t,"status");if(state=="failed")completion.TrySetException(StudioFailure.FromServer(Obj(t,"error")));else completion.TrySetResult(state??"failed");}
            }else if(method=="item/started")Tell("working",Str(Obj(p,"item"),"type")??"");
        }
        void ReplyDenied(object id,string method)
        {
            // Phase 1 never approves writes, commands, external tools or permission escalation.
            if(method=="item/commandExecution/requestApproval"||method=="item/fileChange/requestApproval")Write(new{id=id,result=new{decision="decline"}});
            else if(method=="item/permissions/requestApproval")Write(new{id=id,result=new{permissions=new{},scope="turn"}});
            else if(method=="item/tool/requestUserInput")Write(new{id=id,result=new{answers=new{}}});
            else if(method=="mcpServer/elicitation/request")Write(new{id=id,result=new{action="decline",content=(object)null}});
            else Write(new{id=id,error=new{code=-32601,message="Not supported by Nephylo Studio phase 1"}});
        }
        void FailPending()
        {
            lock(gate){foreach(var p in pending.Values)p.TrySetException(new IOException("Codex disconnected."));if(completion!=null)completion.TrySetException(new IOException("Codex disconnected."));}
        }
        public void Dispose()
        {
            lock(gate){if(disposed)return;disposed=true;}
            FailPending();try{if(child!=null&&!child.HasExited){child.StandardInput.Close();if(!child.WaitForExit(500))child.Kill();}}catch{}
        }
    }
}
