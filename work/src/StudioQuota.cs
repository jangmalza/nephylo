using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UsageWidget
{
    internal enum StudioQuotaState { Confirmed, Stale, ResetPending, Unknown, Mismatch }

    // Account metadata is used only to bind observations to ADE's own live connection.
    // Never retain a raw email, an auth token, or an API key in a quota snapshot.
    internal sealed class StudioAccountIdentity
    {
        internal readonly string Type, Fingerprint, AccountIdFingerprint, EmailFingerprint;
        internal readonly bool IsAuthenticated;
        internal bool Known { get { return !String.IsNullOrEmpty(Fingerprint); } }
        StudioAccountIdentity(string type,string fingerprint,string accountIdFingerprint,bool authenticated,string emailFingerprint=null)
        { Type=type;Fingerprint=fingerprint;AccountIdFingerprint=accountIdFingerprint;IsAuthenticated=authenticated;EmailFingerprint=emailFingerprint; }
        internal static StudioAccountIdentity FromResponse(Dictionary<string,object> response)
        {
            var account=StudioCodexClient.Obj(response,"account");
            if(account==null)return new StudioAccountIdentity("",null,null,false);
            var type=StudioQuotaParser.SafeText(StudioCodexClient.Str(account,"type"),40)??"";
            var id=StudioQuotaParser.SafeText(StudioCodexClient.Str(account,"accountId")??StudioCodexClient.Str(account,"chatgptAccountId"),256);
            var email=StudioQuotaParser.SafeText(StudioCodexClient.Str(account,"email"),320);
            var idHash=Hash("account-id",id);
            return new StudioAccountIdentity(type,idHash??Hash("email",email==null?null:email.Trim().ToLowerInvariant()),idHash,!String.IsNullOrEmpty(type));
        }
        internal static string Hash(string kind,string value)
        {
            if(String.IsNullOrWhiteSpace(value))return null;
            using(var sha=SHA256.Create())return String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes("codex\n"+kind+"\n"+value)).Select(b=>b.ToString("x2",CultureInfo.InvariantCulture)));
        }
        internal static StudioAccountIdentity FromClaudeStatus(Dictionary<string,object> response)
        {
            object logged;bool authenticated=response!=null&&response.TryGetValue("loggedIn",out logged)&&Object.Equals(logged,true);
            var method=StudioQuotaParser.SafeText(StudioCodexClient.Str(response,"authMethod"),64)??"unknown";
            var id=StudioQuotaParser.SafeText(StudioCodexClient.Str(response,"accountId")??StudioCodexClient.Str(response,"userId"),256);
            var email=StudioQuotaParser.SafeText(StudioCodexClient.Str(response,"email"),320);
            string identity=null,idHash=null;
            using(var sha=SHA256.Create()){
                if(id!=null)idHash=String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes("claude\naccount-id\n"+id)).Select(b=>b.ToString("x2",CultureInfo.InvariantCulture)));
                identity=idHash;
                if(identity==null&&email!=null)identity=String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes("claude\nemail\n"+email.Trim().ToLowerInvariant())).Select(b=>b.ToString("x2",CultureInfo.InvariantCulture)));
            }
            return new StudioAccountIdentity("claude:"+method,authenticated?identity:null,authenticated?idHash:null,authenticated,authenticated?ClaudeEmailFingerprint(email):null);
        }
        internal static string ClaudeEmailFingerprint(string email)
        { return ProviderHash("claude","email",StudioQuotaParser.SafeText(email,320)==null?null:email.Trim().ToLowerInvariant()); }
        internal static StudioAccountIdentity FromGeminiApiKey(string key)
        {
            bool present=!String.IsNullOrWhiteSpace(key);var fingerprint=present?ProviderHash("gemini-api","key",key):null;
            return new StudioAccountIdentity("gemini-api:key",fingerprint,fingerprint,present);
        }
        internal static StudioAccountIdentity FromGeminiCliStatus(bool authenticated,string opaqueAccountId)
        {
            var id=StudioQuotaParser.SafeText(opaqueAccountId,320);var fingerprint=authenticated?ProviderHash("gemini-cli","account",id):null;
            return new StudioAccountIdentity("gemini-cli:google",fingerprint,fingerprint,authenticated);
        }
        static string ProviderHash(string provider,string kind,string value)
        {
            if(String.IsNullOrWhiteSpace(value))return null;
            using(var sha=SHA256.Create())return String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(provider+"\n"+kind+"\n"+value)).Select(b=>b.ToString("x2",CultureInfo.InvariantCulture)));
        }
    }

    internal sealed class StudioQuotaWindow
    {
        internal readonly string Role;
        internal readonly double? RemainingPercent;
        internal readonly long? WindowMinutes;
        internal readonly DateTime? ResetUtc;
        internal StudioQuotaWindow(string role,double? remaining,long? minutes,DateTime? reset)
        { Role=role;RemainingPercent=remaining;WindowMinutes=minutes;ResetUtc=reset; }
        internal StudioQuotaState State(DateTime nowUtc,StudioQuotaState snapshotState)
        {
            if(snapshotState!=StudioQuotaState.Confirmed)return snapshotState;
            if(!RemainingPercent.HasValue)return StudioQuotaState.Unknown;
            if(ResetUtc.HasValue&&nowUtc.ToUniversalTime()>=ResetUtc.Value)return StudioQuotaState.ResetPending;
            return StudioQuotaState.Confirmed;
        }
    }

    internal sealed class StudioQuotaBucket
    {
        internal readonly string Id,Name,ModelScope,ReachedType;
        internal readonly bool? SpendControlReached;
        internal readonly StudioQuotaWindow Primary,Secondary;
        internal StudioQuotaBucket(string id,string name,string model,string reached,bool? spend,StudioQuotaWindow primary,StudioQuotaWindow secondary)
        { Id=id;Name=name;ModelScope=model;ReachedType=reached;SpendControlReached=spend;Primary=primary;Secondary=secondary; }
    }

    internal sealed class StudioQuotaSnapshot
    {
        internal static readonly TimeSpan Freshness=TimeSpan.FromMinutes(5);
        internal readonly DateTime ObservedUtc;
        internal readonly string ConnectionId,AccountFingerprint,SourceAccountFingerprint;
        internal readonly ReadOnlyCollection<StudioQuotaBucket> Buckets;
        internal readonly bool? OrdinaryUsageAllowed;
        internal readonly bool Available;
        internal StudioQuotaSnapshot(DateTime observed,string connection,string account,string sourceAccount,IEnumerable<StudioQuotaBucket> buckets,bool? ordinary,bool available)
        { ObservedUtc=observed.ToUniversalTime();ConnectionId=connection;AccountFingerprint=account;SourceAccountFingerprint=sourceAccount;Buckets=new List<StudioQuotaBucket>(buckets).AsReadOnly();OrdinaryUsageAllowed=ordinary;Available=available; }
        internal static StudioQuotaSnapshot Unknown(string connection,StudioAccountIdentity identity)
        { return new StudioQuotaSnapshot(DateTime.UtcNow,connection,identity==null?null:identity.Fingerprint,null,new StudioQuotaBucket[0],null,false); }
        internal bool IsCurrentObservation(DateTime nowUtc,StudioAccountIdentity identity,string connectionId)
        {
            var age=nowUtc.ToUniversalTime()-ObservedUtc;
            return Available&&identity!=null&&identity.IsAuthenticated&&identity.Known&&!String.IsNullOrEmpty(connectionId)&&ConnectionId==connectionId&&AccountFingerprint==identity.Fingerprint&&(identity.AccountIdFingerprint==null||SourceAccountFingerprint==null||identity.AccountIdFingerprint==SourceAccountFingerprint)&&age>=TimeSpan.Zero&&age<Freshness;
        }
        internal StudioQuotaState State(DateTime nowUtc,StudioAccountIdentity identity,string connectionId)
        {
            if(String.IsNullOrEmpty(connectionId)||ConnectionId!=connectionId)return StudioQuotaState.Mismatch;
            if(identity==null||!identity.Known||String.IsNullOrEmpty(AccountFingerprint))return StudioQuotaState.Unknown;
            if(identity.Fingerprint!=AccountFingerprint)return StudioQuotaState.Mismatch;
            if(identity.AccountIdFingerprint!=null&&SourceAccountFingerprint!=null&&identity.AccountIdFingerprint!=SourceAccountFingerprint)return StudioQuotaState.Mismatch;
            if(!Available||Buckets.Count==0)return StudioQuotaState.Unknown;
            var age=nowUtc.ToUniversalTime()-ObservedUtc;
            if(age<TimeSpan.Zero||age>=Freshness)return StudioQuotaState.Stale;
            return StudioQuotaState.Confirmed;
        }
    }

    internal static class StudioQuotaParser
    {
        internal static string SafeText(string value,int max)
        { return String.IsNullOrWhiteSpace(value)||value.Length>max||value.Any(Char.IsControl)?null:value.Trim(); }
        static double? Number(Dictionary<string,object> row,string key)
        {
            object value;if(row==null||!row.TryGetValue(key,out value)||value==null)return null;
            // Reject textual numbers and booleans: a malformed payload is unknown, not zero.
            if(!(value is byte||value is short||value is int||value is long||value is float||value is double||value is decimal))return null;
            double result;try{result=Convert.ToDouble(value,CultureInfo.InvariantCulture);}catch{return null;}
            return Double.IsNaN(result)||Double.IsInfinity(result)?(double?)null:result;
        }
        static long? Integer(Dictionary<string,object> row,string key,long min,long max)
        { var value=Number(row,key);return value.HasValue&&value.Value>=min&&value.Value<=max&&Math.Truncate(value.Value)==value.Value?(long?)value.Value:null; }
        static bool? Boolean(Dictionary<string,object> row,string key)
        { object v;return row!=null&&row.TryGetValue(key,out v)&&v is bool?(bool?)v:null; }
        static StudioQuotaWindow Window(Dictionary<string,object> value,string role)
        {
            if(value==null)return null;
            var used=Number(value,"usedPercent");double? remaining=used.HasValue&&used.Value>=0&&used.Value<=100?(double?)(100-used.Value):null;
            var reset=Integer(value,"resetsAt",0,253402300799L);
            DateTime? resetUtc=reset.HasValue?(DateTime?)new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(reset.Value):null;
            return new StudioQuotaWindow(role,remaining,Integer(value,"windowDurationMins",1,52560000),resetUtc);
        }
        static StudioQuotaBucket Bucket(Dictionary<string,object> row,string mapId)
        {
            var id=SafeText(mapId,128)??SafeText(StudioCodexClient.Str(row,"limitId"),128)??"codex";
            return new StudioQuotaBucket(id,SafeText(StudioCodexClient.Str(row,"limitName"),128)??id,SafeText(StudioCodexClient.Str(row,"normalModelSlug"),128),SafeText(StudioCodexClient.Str(row,"rateLimitReachedType"),128),Boolean(row,"spendControlReached"),Window(StudioCodexClient.Obj(row,"primary"),"primary"),Window(StudioCodexClient.Obj(row,"secondary"),"secondary"));
        }
        internal static StudioQuotaSnapshot Parse(Dictionary<string,object> response,StudioAccountIdentity identity,string connectionId,DateTime observedUtc)
        {
            var buckets=new List<StudioQuotaBucket>();var map=StudioCodexClient.Obj(response,"rateLimitsByLimitId");
            if(map!=null)foreach(var entry in map.OrderBy(v=>v.Key,StringComparer.Ordinal).Take(32)){
                var row=entry.Value as Dictionary<string,object>;if(row!=null)buckets.Add(Bucket(row,entry.Key));
            }
            if(buckets.Count==0){var single=StudioCodexClient.Obj(response,"rateLimits");if(single!=null)buckets.Add(Bucket(single,null));}
            var accountId=SafeText(StudioCodexClient.Str(response,"accountId"),256);
            var ordinary=Boolean(response,"ordinaryUsageAllowed");
            var observed=ordinary.HasValue||buckets.Any(b=>(b.Primary!=null&&b.Primary.RemainingPercent.HasValue)||(b.Secondary!=null&&b.Secondary.RemainingPercent.HasValue)||b.SpendControlReached.HasValue||!String.IsNullOrEmpty(b.ReachedType));
            return new StudioQuotaSnapshot(observedUtc,connectionId,identity==null?null:identity.Fingerprint,StudioAccountIdentity.Hash("account-id",accountId),buckets,ordinary,response!=null&&observed);
        }
    }
}
