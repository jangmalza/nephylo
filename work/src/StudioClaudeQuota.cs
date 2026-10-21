using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace UsageWidget
{
    // Official Agent SDK 0.3.304 GetUsage contract (experimental). No SDK dependency,
    // OAuth HTTP requests, credential reads, model prompts, or global statusLine hooks.
    internal static class StudioClaudeQuotaParser
    {
        static StudioQuotaWindow Window(Dictionary<string,object> row,string role,long minutes,DateTime now)
        {
            object used;if(row==null||!row.TryGetValue("utilization",out used)||!(used is byte||used is short||used is int||used is long||used is float||used is double||used is decimal))return null;
            double percent;try{percent=Convert.ToDouble(used,CultureInfo.InvariantCulture);}catch{return null;}
            if(Double.IsNaN(percent)||Double.IsInfinity(percent)||percent<0||percent>100)return null;
            var reset=StudioCodexClient.Str(row,"resets_at");DateTimeOffset parsed;
            // Require an explicit timezone. Do not interpret a locale/date-only value as UTC.
            if(reset==null||reset.Length>64||!Regex.IsMatch(reset,@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")||!DateTimeOffset.TryParse(reset,CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed))return null;
            var utc=parsed.UtcDateTime;
            if(utc<=now.ToUniversalTime()||utc>now.ToUniversalTime().AddMinutes(minutes).AddMinutes(5))return null;
            return new StudioQuotaWindow(role,100-percent,minutes,utc);
        }
        internal static StudioQuotaSnapshot Parse(Dictionary<string,object> response,StudioAccountIdentity identity,string connection,DateTime observed)
        {
            var unknown=StudioQuotaSnapshot.Unknown(connection,identity);object available;
            if(identity==null||!identity.IsAuthenticated||!identity.Known||response==null||!response.TryGetValue("rate_limits_available",out available)||!Object.Equals(available,true))return unknown;
            if(!new[]{"pro","max","team","enterprise"}.Contains(StudioCodexClient.Str(response,"subscription_type")))return unknown;
            var rates=StudioCodexClient.Obj(response,"rate_limits");
            var primary=Window(StudioCodexClient.Obj(rates,"five_hour"),"primary",300,observed);
            var secondary=Window(StudioCodexClient.Obj(rates,"seven_day"),"secondary",10080,observed);
            // A partial common limit is not evidence of sufficient allowance.
            if(primary==null||secondary==null)return unknown;
            var buckets=new List<StudioQuotaBucket>{new StudioQuotaBucket("claude-common","Claude",null,null,null,primary,secondary)};
            foreach(var alias in new[]{"opus","sonnet"}){
                var row=StudioCodexClient.Obj(rates,"seven_day_"+alias);object raw;
                if(rates.TryGetValue("seven_day_"+alias,out raw)&&raw!=null){
                    var window=Window(row,"secondary",10080,observed);if(window==null)return unknown;
                    buckets.Add(new StudioQuotaBucket("claude-"+alias,"Claude "+alias,alias,null,null,null,window));
                }
            }
            // Surface-only / unknown model scopes and extra paid usage are not common quota.
            return new StudioQuotaSnapshot(observed,connection,identity.Fingerprint,null,buckets,null,true);
        }
    }

    internal sealed partial class StudioClaudeClient
    {
        bool usageSupported;
        long accountRevision,quotaPendingRevision;
        StudioQuotaSnapshot quota;
        TaskCompletionSource<StudioQuotaSnapshot> quotaPending;
        internal StudioQuotaSnapshot Quota { get { lock(gate)return quota; } }
        static bool SameQuotaIdentity(StudioAccountIdentity a,StudioAccountIdentity b)
        { return a!=null&&b!=null&&a.IsAuthenticated&&b.IsAuthenticated&&a.Known&&a.Fingerprint==b.Fingerprint&&a.Type==b.Type&&a.EmailFingerprint==b.EmailFingerprint; }
        public async Task<StudioQuotaSnapshot> QuotaAsync()
        {
            // Recheck the official account even on a cache hit. A connection can outlive a login.
            try{RequireConnected();await AccountAsync();}catch{lock(gate){quota=null;return StudioQuotaSnapshot.Unknown(connectionId,identity);}}
            TaskCompletionSource<StudioQuotaSnapshot> pending;StudioAccountIdentity expected;long revision;bool collect=false;
            lock(gate){
                expected=identity;revision=accountRevision;
                if(disposed||!usageSupported||expected==null||!expected.IsAuthenticated||!expected.Known||expected.EmailFingerprint==null||!(expected.Type=="claude:claude.ai"||expected.Type=="claude:oauth"))return StudioQuotaSnapshot.Unknown(connectionId,expected);
                var now=DateTime.UtcNow;
                if(quota!=null&&quota.AccountFingerprint==expected.Fingerprint&&now>=quota.ObservedUtc&&now-quota.ObservedUtc<TimeSpan.FromSeconds(60)&&quota.Buckets.All(b=>new[]{b.Primary,b.Secondary}.All(w=>w==null||!w.ResetUtc.HasValue||w.ResetUtc.Value>now)))return quota;
                if(quotaPending!=null&&quotaPendingRevision==revision)pending=quotaPending;
                else{pending=new TaskCompletionSource<StudioQuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);quotaPending=pending;quotaPendingRevision=revision;collect=true;}
            }
            if(collect){var collecting=CollectQuotaAsync(pending,expected,revision);}
            return await pending.Task;
        }
        async Task CollectQuotaAsync(TaskCompletionSource<StudioQuotaSnapshot> pending,StudioAccountIdentity expected,long revision)
        {
            StudioQuotaSnapshot result=null;
            try{
                var response=await QueryUsageAsync(expected);var observed=DateTime.UtcNow;
                await AccountAsync();
                lock(gate)if(!disposed&&accountRevision==revision&&SameQuotaIdentity(expected,identity))result=StudioClaudeQuotaParser.Parse(response,expected,connectionId,observed);
            }catch{ /* Optional telemetry failure never disconnects the chat adapter. */ }
            lock(gate){
                bool current=!disposed&&accountRevision==revision&&SameQuotaIdentity(expected,identity);
                if(!current||result==null)result=StudioQuotaSnapshot.Unknown(connectionId,identity);
                if(quotaPending==pending){quotaPending=null;if(current)quota=result;}
            }
            pending.TrySetResult(result);
        }
        async Task<Dictionary<string,object>> QueryUsageAsync(StudioAccountIdentity expected)
        {
            Process process=null;Task errors=null;Task<Dictionary<string,object>> reading=null;
            string session=Guid.NewGuid().ToString("D");
            try{
                lock(gate){if(disposed)throw new ObjectDisposedException("StudioClaudeClient");process=Start(BuildArguments(session,"haiku",true).Concat(new[]{"--no-session-persistence"}));probes.Add(process);}
                var stdout=process.StandardOutput;var stderr=process.StandardError;errors=Task.Run(()=>Drain(stderr));
                string init=Guid.NewGuid().ToString("N");
                await WriteAsync(new{type="control_request",request_id=init,request=new{subtype="initialize",hooks=new{},skills=new string[0]}},process);
                reading=Task.Run(()=>ReadUsageReply(stdout,init,session));
                var initialized=await UsageReplyAsync(reading);var account=Obj(initialized,"account");
                if(Str(initialized,"current_permission_mode")!="dontAsk"||Str(account,"apiProvider")!="firstParty"||StudioAccountIdentity.ClaudeEmailFingerprint(Str(account,"email"))!=expected.EmailFingerprint)throw new StudioFailure(StudioFailureKind.Authentication);
                string usage=Guid.NewGuid().ToString("N");
                await WriteAsync(new{type="control_request",request_id=usage,request=new{subtype="get_usage",skip_behaviors=true}},process);
                reading=Task.Run(()=>ReadUsageReply(stdout,usage,session));
                return await UsageReplyAsync(reading);
            }finally{
                if(process!=null){StopOwned(process,reading,errors);lock(gate)probes.Remove(process);}
            }
        }
        static async Task<Dictionary<string,object>> UsageReplyAsync(Task<Dictionary<string,object>> reading)
        {
            if(await Task.WhenAny(reading,Task.Delay(12000))!=reading)throw new StudioFailure(StudioFailureKind.Timeout);
            return await reading;
        }
        static Dictionary<string,object> ReadUsageReply(StreamReader stdout,string id,string session)
        {
            int total=0;
            for(int frames=0;frames<32;frames++){
                var line=new StringBuilder();int c;
                while((c=stdout.Read())>=0&&c!='\n'){if(++total>MaxLine)throw new StudioFailure(StudioFailureKind.Compatibility);line.Append((char)c);}
                if(c<0)throw new StudioFailure(StudioFailureKind.Disconnected);
                var frame=Json().DeserializeObject(line.ToString()) as Dictionary<string,object>;
                var sid=Str(frame,"session_id");if(sid!=null&&sid!=session)throw new StudioFailure(StudioFailureKind.Compatibility);
                var type=Str(frame,"type");
                if(type=="system"&&Str(frame,"subtype")!="init")continue;
                if(type!="control_response")throw new StudioFailure(StudioFailureKind.Compatibility);
                var response=Obj(frame,"response");
                if(Str(response,"request_id")!=id||Str(response,"subtype")!="success"||Obj(response,"response")==null)throw new StudioFailure(StudioFailureKind.Compatibility);
                return Obj(response,"response");
            }
            throw new StudioFailure(StudioFailureKind.Compatibility);
        }
    }
}
