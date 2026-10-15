using System;
using System.Linq;
using UsageWidget;

public static class BackendTests
{
    static int count;
    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        count++;
    }
    public static int Main(string[] args)
    {
        try
        {
            var fallback = CodexService.Parse("{\"rateLimits\":{\"planType\":\"plus\",\"primary\":{\"usedPercent\":30,\"windowDurationMins\":300,\"resetsAt\":1800000000},\"secondary\":{\"usedPercent\":80,\"windowDurationMins\":10080}}}");
            Check(fallback.Rows.Count == 2, "both legacy windows retained");
            Check(fallback.Rows[0].RemainingPercent == 70, "used vs remaining semantics");
            Check(fallback.Rows[1].RemainingPercent == 20, "weekly remaining");
            Check(fallback.Rows[0].Label.Contains("5시간"), "duration format");
            Check(fallback.Rows[0].ResetsAt.Value.ToUnixTimeSecondsCompat() == 1800000000, "unix seconds timezone");
            var multiple = CodexService.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":99}},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":20}},\"review\":{\"primary\":{\"usedPercent\":130},\"secondary\":{\"usedPercent\":-2}}}}");
            Check(multiple.Rows.Count == 3, "all map buckets preferred without legacy duplication");
            Check(multiple.Rows[0].RemainingPercent == 80, "map precedence");
            Check(multiple.Rows[1].RemainingPercent == 0 && multiple.Rows[2].RemainingPercent == 100, "percent clamp");
            var unknown = CodexService.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":null,\"resetsAt\":null}}}");
            Check(!unknown.Rows[0].RemainingPercent.HasValue, "unknown is not zero");
            Check(!unknown.Rows[0].ResetsAt.HasValue, "unknown reset remains unknown");
            var free = CodexService.Parse("{\"rateLimits\":{\"primary\":null,\"secondary\":null}}");
            Check(free.Rows.Count == 1 && !free.Rows[0].RemainingPercent.HasValue, "missing windows not 100 percent");
            var eleven = ElevenLabsService.Parse("{\"tier\":\"creator\",\"character_count\":2000,\"character_limit\":10000,\"next_character_count_reset_unix\":1800000000}");
            Check(eleven.Rows[0].Remaining == 8000 && eleven.Rows[0].RemainingPercent == 80, "Eleven credits");
            var overage = ElevenLabsService.Parse("{\"character_count\":12000,\"character_limit\":10000}");
            Check(overage.Rows[0].Remaining == 0 && overage.Rows[0].Detail.Contains("2,000"), "overage separate and clamped");
            var zero = ElevenLabsService.Parse("{\"character_count\":0,\"character_limit\":0}");
            Check(zero.Rows[0].Remaining == 0 && !zero.Rows[0].RemainingPercent.HasValue, "zero limit has no fabricated percent");
            var missing = ElevenLabsService.Parse("{\"character_count\":null,\"character_limit\":10000}");
            Check(!missing.Rows[0].Remaining.HasValue && !missing.Rows[0].RemainingPercent.HasValue, "missing count remains unknown");
            var invalid = ElevenLabsService.Parse("{\"character_count\":-1,\"character_limit\":10000}");
            Check(!invalid.Rows[0].Remaining.HasValue, "negative count invalid");
            var legacyPermission = ElevenLabsService.ErrorMessage(401, "{\"detail\":{\"status\":\"missing_permissions\",\"message\":\"PRIVATE_API_KEY\"}}");
            Check(legacyPermission.Contains("user_read") && !legacyPermission.Contains("PRIVATE_API_KEY"), "401 permission errors distinguished without raw message");
            var livePermission = ElevenLabsService.ErrorMessage(401, "{\"detail\":{\"code\":\"unauthorized\",\"status\":\"missing_permissions\",\"message\":\"PRIVATE_API_KEY\"}}");
            Check(livePermission.Contains("user_read") && !livePermission.Contains("401") && !livePermission.Contains("PRIVATE_API_KEY"), "real subscription schema retains specific permission cause from legacy status");
            var modernPermission = ElevenLabsService.ErrorMessage(403, "{\"detail\":{\"code\":\"insufficient_permissions\",\"status\":\"invalid_api_key\"}}");
            Check(modernPermission.Contains("user_read"), "modern error code preferred over legacy status");
            var invalidKey = ElevenLabsService.ErrorMessage(401, "{\"detail\":{\"code\":\"invalid_api_key\"}}");
            Check(invalidKey.Contains("만료·삭제") && !invalidKey.Contains("user_read"), "invalid key separate from missing scope");
            var unknownCode = ElevenLabsService.ErrorMessage(401, "{\"detail\":{\"code\":\"PRIVATE_API_KEY\",\"message\":\"OTHER_SECRET\"}}");
            Check(!unknownCode.Contains("PRIVATE_API_KEY") && !unknownCode.Contains("OTHER_SECRET") && unknownCode.Contains("401"), "unknown provider strings never displayed");
            Check(ElevenLabsService.ErrorMessage(403, "<html>PRIVATE_API_KEY</html>").Contains("IP 제한"), "non-JSON permission response safe fallback");
            Check(ElevenLabsService.ErrorMessage(429, null).Contains("일시 제한"), "missing body rate limit fallback");
            if (args.Length > 0 && args[0] == "--live")
            {
                using (var service = new CodexService())
                {
                    var live = service.ReadAsync(null).GetAwaiter().GetResult();
                    Check(live.Rows.Count > 0, "live query has rows");
                    Console.WriteLine("Live Codex query succeeded; rows=" + live.Rows.Count + "; known percentages=" + live.Rows.Count(row => row.RemainingPercent.HasValue));
                }
            }
            Console.WriteLine("PASS " + count + " backend checks");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
    }
    static long ToUnixTimeSecondsCompat(this DateTimeOffset date) { return (long)(date.ToUniversalTime() - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds; }
}
