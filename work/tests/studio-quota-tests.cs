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

#if STUDIO_QUOTA_TESTS
// Quota protocol tests do not include the widget or any real login/browser path.
// Accidental browser sign-in in this isolated suite fails immediately.
namespace UsageWidget
{
    internal static class CodexBrowserRecovery
    {
        internal static bool IsAllowed(string url){throw new NotSupportedException("Browser login is outside isolated quota tests.");}
    }
}
#endif

internal static class StudioQuotaTests
{
    static int count;static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static void Check(bool ok,string name){count++;if(!ok)throw new Exception(name);}
    static Dictionary<string,object> D(object value){return Json.Deserialize<Dictionary<string,object>>(Json.Serialize(value));}
    static StudioAccountIdentity Account(string email){return StudioAccountIdentity.FromResponse(D(new{account=new{type="chatgpt",email=email}}));}
    static object Payload(){return new{rateLimits=new{limitId="codex",primary=new{usedPercent=24,windowDurationMins=300,resetsAt=2000000000L},secondary=new{usedPercent=61,windowDurationMins=10080,resetsAt=2000500000L}},ordinaryUsageAllowed=(bool?)true};}
    static void Emit(object value){Console.WriteLine(Json.Serialize(value));Console.Out.Flush();}
    static int Server()
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=new UTF8Encoding(false);
        string line,mode="valid";int reads=0,turns=0;
        while((line=Console.ReadLine())!=null){
            var message=Json.Deserialize<Dictionary<string,object>>(line);object id;message.TryGetValue("id",out id);var method=StudioCodexClient.Str(message,"method");var p=StudioCodexClient.Obj(message,"params");
            if(method=="initialized")continue;
            object result=new{};
            if(method=="account/read")result=new{account=new{type="chatgpt",email="quota-fixture@example.invalid",planType="plus"}};
            if(method=="account/read"&&mode=="unknownIdentity")result=new{account=new{type="chatgpt",email=(string)null,planType="plus"}};
            if(method=="fixture/mode"){mode=StudioCodexClient.Str(p,"value");result=new{readCount=reads};}
            if(method=="fixture/readCount")result=new{readCount=reads};
            if(method=="fixture/turnCount")result=new{turnCount=turns};
            if(method=="fixture/quotaChanged")Emit(new{method="account/rateLimits/updated",@params=new{rateLimits=new{primary=new{usedPercent=75}}}});
            if(method=="fixture/accountChanged")Emit(new{method="account/updated",@params=new{authMode="chatgpt",planType="plus"}});
            if(method=="account/rateLimits/read"){
                reads++;
                if(mode=="unsupported"){Emit(new{id=id,error=new{code=-32601,message="unsupported SECRET"}});continue;}
                if(mode=="timeout")continue;
                if(mode=="accountRace")Emit(new{method="account/updated",@params=new{authMode="chatgpt"}});
                if(mode=="quotaRace")Emit(new{method="account/rateLimits/updated",@params=new{rateLimits=new{primary=new{usedPercent=90}}}});
                if(mode=="delay")Thread.Sleep(250);
                result=Payload();
            }
            if(method=="model/list")result=new{data=new[]{new{model="fixture-model",displayName="Fixture model"}}};
            if(method=="thread/start"){
                if(mode=="accountBeforeThread")Emit(new{method="account/updated",@params=new{authMode="chatgpt"}});
                result=new{thread=new{id="quota-thread"},sandbox=new{type="readOnly",networkAccess=false},approvalPolicy="untrusted",approvalsReviewer="user"};
            }
            if(method=="turn/start"){
                turns++;Emit(new{id=id,result=new{turn=new{id="quota-turn",status="inProgress"}}});
                Emit(new{method="turn/started",@params=new{threadId="quota-thread",turn=new{id="quota-turn"}}});
                Emit(new{method="turn/completed",@params=new{threadId="quota-thread",turn=new{id="quota-turn",status="completed"}}});continue;
            }
            Emit(new{id=id,result=result});
        }
        return 0;
    }
    static void Parser()
    {
        var now=new DateTime(2026,10,16,0,0,0,DateTimeKind.Utc);var account=Account("quota-fixture@example.invalid");
        Check(account.IsAuthenticated&&account.Known,"account identity from official metadata");
        Check(account.Fingerprint.Length==64&&!account.Fingerprint.Contains("fixture"),"raw account email not retained");
        Check(account.Fingerprint==Account(" QUOTA-FIXTURE@example.invalid ").Fingerprint,"email comparison normalized");
        Check(!StudioAccountIdentity.FromResponse(D(new{account=(object)null})).IsAuthenticated,"logged out remains unauthenticated");
        var unknownAccount=StudioAccountIdentity.FromResponse(D(new{account=new{type="chatgpt"}}));
        Check(unknownAccount.IsAuthenticated&&!unknownAccount.Known,"missing metadata is unknown identity");
        var snapshot=StudioQuotaParser.Parse(D(Payload()),account,"connection-a",now);
        Check(snapshot.State(now,account,"connection-a")==StudioQuotaState.Confirmed,"dedicated active connection observation confirmed");
        Check(snapshot.State(now,account,"connection-b")==StudioQuotaState.Mismatch,"another process connection quota rejected");
        Check(snapshot.State(now,Account("other@example.invalid"),"connection-a")==StudioQuotaState.Mismatch,"another account quota rejected");
        Check(snapshot.State(now,unknownAccount,"connection-a")==StudioQuotaState.Unknown,"unknown identity cannot recommend");
        Check(snapshot.State(now.AddMinutes(5),account,"connection-a")==StudioQuotaState.Stale,"observation stale at five minutes");
        Check(snapshot.State(now.AddSeconds(-1),account,"connection-a")==StudioQuotaState.Stale,"backward clock not considered fresh");
        Check(snapshot.Buckets.Single().Primary.RemainingPercent==76&&snapshot.Buckets.Single().Secondary.RemainingPercent==39,"separate primary and secondary allowances");
        Check(snapshot.Buckets.Single().ModelScope==null,"no invented model-specific quota scope");
        Check(snapshot.Buckets.Single().Primary.WindowMinutes==300&&snapshot.Buckets.Single().Secondary.WindowMinutes==10080,"window lengths retained independently");
        Check(snapshot.Buckets.Single().Primary.State(new DateTime(2040,1,1,0,0,0,DateTimeKind.Utc),StudioQuotaState.Confirmed)==StudioQuotaState.ResetPending,"past reset needs refresh rather than restoring percentage");
        Check(snapshot.Buckets.Single().Primary.RemainingPercent==76,"reset crossing never modifies measured quota");
        var map=D(new{rateLimits=new{primary=new{usedPercent=99}},rateLimitsByLimitId=new{alpha=new{limitName="Alpha pool",normalModelSlug="fixture-model",primary=new{usedPercent=5},spendControlReached=true,rateLimitReachedType="workspace_owner_usage_limit_reached"},beta=new{secondary=new{usedPercent=95}}},ordinaryUsageAllowed=false});
        snapshot=StudioQuotaParser.Parse(map,account,"connection-a",now);
        Check(snapshot.Buckets.Count==2,"multi-bucket map does not double count legacy view");
        Check(snapshot.Buckets[0].Primary.RemainingPercent==95&&snapshot.Buckets[1].Secondary.RemainingPercent==5,"independent buckets never averaged");
        Check(snapshot.Buckets[0].ModelScope=="fixture-model","explicit model scope retained");
        Check(snapshot.OrdinaryUsageAllowed==false&&snapshot.Buckets[0].SpendControlReached==true&&snapshot.Buckets[0].ReachedType=="workspace_owner_usage_limit_reached","server blockers not inferred from percentages");
        Check(snapshot.Buckets[0].Secondary==null&&snapshot.Buckets[1].Primary==null,"absent windows remain absent");
        foreach(var bad in new object[]{-1,101,Double.NaN,Double.PositiveInfinity,"25",true,null}){
            var window=new Dictionary<string,object>{{"usedPercent",bad}};
            var row=new Dictionary<string,object>{{"rateLimits",new Dictionary<string,object>{{"primary",window}}}};
            var parsed=StudioQuotaParser.Parse(row,account,"connection-a",now).Buckets.Single().Primary;
            Check(parsed.RemainingPercent==null,"invalid percentage is unknown: "+Convert.ToString(bad));
            Check(parsed.State(now,StudioQuotaState.Confirmed)==StudioQuotaState.Unknown,"invalid percentage excluded from recommendation");
        }
        foreach(var used in new[]{0,100}){
            var p=StudioQuotaParser.Parse(D(new{rateLimits=new{primary=new{usedPercent=used}}}),account,"connection-a",now).Buckets.Single().Primary;
            Check(p.RemainingPercent==100-used,"valid boundary value: "+used);
        }
        snapshot=StudioQuotaParser.Parse(D(new{rateLimits=new{primary=new{usedPercent=10,resetsAt=Int64.MaxValue,windowDurationMins=-1}}}),account,"connection-a",now);
        Check(snapshot.Buckets[0].Primary.ResetUtc==null&&snapshot.Buckets[0].Primary.WindowMinutes==null,"malformed durations unknown without overflow");
        Check(!StudioQuotaParser.Parse(null,account,"connection-a",now).Available,"missing response unavailable");
        Check(!StudioQuotaParser.Parse(D(new{rateLimits=new{}}),account,"connection-a",now).Available,"empty bucket does not claim observed allowance");
        Check(StudioQuotaSnapshot.Unknown("connection-a",account).State(now,account,"connection-a")==StudioQuotaState.Unknown,"failed read is unknown not zero");
        var idAccount=StudioAccountIdentity.FromResponse(D(new{account=new{type="chatgpt",accountId="workspace-a",email="quota-fixture@example.invalid"}}));
        snapshot=StudioQuotaParser.Parse(D(new{accountId="workspace-b",rateLimits=new{primary=new{usedPercent=10}}}),idAccount,"connection-a",now);
        Check(snapshot.State(now,idAccount,"connection-a")==StudioQuotaState.Mismatch,"explicit mismatched workspace IDs rejected");
        snapshot=StudioQuotaParser.Parse(D(new{rateLimits=new{limitName="bad\nlabel",normalModelSlug="bad\tmodel",primary=new{usedPercent=10}}}),account,"connection-a",now);
        Check(snapshot.Buckets[0].Name=="codex"&&snapshot.Buckets[0].ModelScope==null,"untrusted controls not rendered in metadata");
    }
    static async Task Protocol(string root)
    {
        using(var client=new StudioCodexClient()){
            await client.ConnectAsync(Assembly.GetExecutingAssembly().Location,root);
            Check(!(await client.QuotaAsync()).Available,"quota not requested before account is read");
            var before=await client.Request("fixture/readCount",new{});Check(Convert.ToInt32(before["readCount"])==0,"unknown authentication sends no quota request");
            await client.AccountAsync();
            var quota=await client.QuotaAsync();Check(quota.State(DateTime.UtcNow,client.AccountIdentity,client.ConnectionId)==StudioQuotaState.Confirmed,"official response bound to current account and process");
            Check(Object.ReferenceEquals(quota,client.Quota),"client retains immutable snapshot");
            await client.Request("fixture/mode",new{value="delay"});
            before=await client.Request("fixture/readCount",new{});var flightA=client.QuotaAsync();var flightB=client.QuotaAsync();
            Check(Object.ReferenceEquals(flightA,flightB),"concurrent quota requests coalesced");await Task.WhenAll(flightA,flightB);
            var after=await client.Request("fixture/readCount",new{});Check(Convert.ToInt32(after["readCount"])==Convert.ToInt32(before["readCount"])+1,"no duplicate quota poller request");
            await client.Request("fixture/mode",new{value="unsupported"});Check(!(await client.QuotaAsync()).Available&&client.Connected,"unsupported quota method does not break connection");
            Check(client.AccountIdentity.Known&&(await client.ModelsAsync()).Count==1,"optional failure preserves auth and normal requests");
            await client.Request("fixture/mode",new{value="timeout"});var watch=Stopwatch.StartNew();
            Check(!(await client.QuotaAsync()).Available&&client.Connected,"quota timeout does not dispose chat child");Check(watch.ElapsedMilliseconds>=4500&&watch.ElapsedMilliseconds<10000,"optional quota has bounded timeout");
            Check((await client.ModelsAsync()).Count==1,"chat RPC remains usable after quota timeout");
            await client.Request("fixture/mode",new{value="valid"});await client.QuotaAsync();
            int notices=0;client.Notice+=(kind,value)=>{if(kind=="quota"&&value=="changed")notices++;};
            await client.Request("fixture/quotaChanged",new{});Check(client.Quota==null&&notices==1,"sparse notification invalidates instead of incorrectly merging");
            await client.QuotaAsync();Check(client.Quota!=null,"refetch after sparse notification succeeds");
            await client.Request("fixture/mode",new{value="quotaRace"});Check(!(await client.QuotaAsync()).Available&&client.Quota==null,"quota event rejects pre-event response race");
            await client.Request("fixture/mode",new{value="accountRace"});Check(!(await client.QuotaAsync()).Available&&client.AccountIdentity==null&&client.Quota==null,"account change rejects in-flight old account quota");
            await client.Request("fixture/mode",new{value="valid"});await client.AccountAsync();Check((await client.QuotaAsync()).Available,"explicit account reread restores fresh observation");
            await client.Request("fixture/accountChanged",new{});Check(client.AccountIdentity==null&&client.Quota==null,"account notification clears identity and observation immediately");
            await client.AccountAsync();var expected=client.AccountIdentity.Fingerprint;
            await client.Request("fixture/mode",new{value="accountBeforeThread"});var thread=await client.ThreadAsync(null,root,"fixture-model");
            bool blocked=false;try{await client.RunBoundAsync(thread,root,"fixture-model","never send on changed account",expected);}catch(StudioFailure error){blocked=error.Kind==StudioFailureKind.Authentication;}
            Check(blocked,"account change during thread/start blocks bound turn before queued UI notice");
            Check(Convert.ToInt32((await client.Request("fixture/turnCount",new{}))["turnCount"])==0,"changed account receives zero turn/start requests");
            await client.Request("fixture/mode",new{value="valid"});await client.AccountAsync();
            blocked=false;try{await client.RunBoundAsync(thread,root,"fixture-model","wrong expected account","another-fingerprint");}catch(StudioFailure error){blocked=error.Kind==StudioFailureKind.Authentication;}
            Check(blocked,"mismatched preflight fingerprint blocks bound turn");
            // Reproduce a switch after the initial binding check but before transport Write.
            // This regression deliberately exercises the second check, not only RunBoundAsync's guard.
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;var type=typeof(StudioCodexClient);var bindingType=type.GetNestedType("AccountBinding",BindingFlags.NonPublic);var binding=Activator.CreateInstance(bindingType,true);
            bindingType.GetField("Revision",flags).SetValue(binding,type.GetField("accountRevision",flags).GetValue(client));
            bindingType.GetField("Fingerprint",flags).SetValue(binding,client.AccountIdentity.Fingerprint);
            await client.Request("fixture/accountChanged",new{});
            var guarded=(Task<Dictionary<string,object>>)type.GetMethod("RequestCore",flags).Invoke(client,new object[]{"turn/start",new{threadId=thread},30000,true,binding});
            blocked=false;try{await guarded;}catch(StudioFailure error){blocked=error.Kind==StudioFailureKind.Authentication;}
            Check(blocked,"transport guard rechecks account revision immediately before writing");
            Check(Convert.ToInt32((await client.Request("fixture/turnCount",new{}))["turnCount"])==0,"transport rejection writes no turn to child process");
            await client.AccountAsync();Check(await client.RunBoundAsync(thread,root,"fixture-model","valid bound turn",client.AccountIdentity.Fingerprint)=="completed","unchanged known account supports bound inference");
            await client.Request("fixture/mode",new{value="unknownIdentity"});await client.AccountAsync();
            Check(await client.RunBoundAsync(thread,root,"fixture-model","unknown identity but active connection",null)=="completed","unknown fingerprint still binds authenticated live account revision");
        }
    }
    [STAThread]static int Main(string[] args)
    {
        if(args.Length>0&&args[0]=="app-server")return Server();
        try{
            var root=Path.GetFullPath(Path.Combine("outputs","studio-quota-tests",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
            Parser();Protocol(root).GetAwaiter().GetResult();Console.WriteLine("Studio quota tests passed: "+count);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
