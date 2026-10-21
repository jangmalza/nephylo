using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using UsageWidget;

internal static class ClaudeQuotaUiFixture
{
    internal static void Install()
    {
        // Existing connection fixtures validate receipt age/profile selection; they
        // never launch a real CLI. Protocol/schema coverage is in claude-quota-tests.
        ClaudeQuotaService.ReadOverride = (distribution, directory, token) => {
            var result = ClaudeUsageService.ReadLatest(distribution, directory);
            if (!result.HasQuotaData && !result.HasExpiredWindows) throw ClaudeQuotaService.Problem("unavailable");
            if (result.HasExpiredWindows || DateTimeOffset.UtcNow - result.ObservedAt > TimeSpan.FromMinutes(30)) throw ClaudeQuotaService.Problem("response");
            var windows = new Dictionary<string,object>();
            for(int i=0;i<Math.Min(2,result.Snapshot.Rows.Count);i++) {
                var row=result.Snapshot.Rows[i]; if(!row.RemainingPercent.HasValue) continue;
                var value=new Dictionary<string,object>{{"used_percentage",100-row.RemainingPercent.Value}};
                if(row.ResetsAt.HasValue)value["resets_at"]=(long)(row.ResetsAt.Value-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds;
                windows[i==0?"five_hour":"seven_day"]=value;
            }
            result.QuotaJson=new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"rate_limits",windows}});
            result.AccountFingerprint=new string('a',64);result.IsDirectQuery=true;
            return Task.FromResult(result);
        };
    }
}
