using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using UsageWidget;

public static class OpenAiTests
{
    private static int checks;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static long Day(int day) { return (long)(new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero) - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds; }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
    private static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
    private static object Amount(decimal value, string currency) { return new { @object = "organization.costs.result", amount = new { value = value, currency = currency } }; }
    private static string Page(int day, bool more, string cursor, params object[] results)
    {
        return Json(new { @object = "page", has_more = more, next_page = cursor, data = new[] { new { @object = "bucket", start_time = Day(day), end_time = Day(day) + 86400, results = results } } });
    }
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) { return new HttpResponseMessage(status) { Content = new StringContent(body) }; }

    private sealed class Handler : HttpMessageHandler
    {
        internal readonly List<Uri> Uris = new List<Uri>();
        internal readonly List<string> Auth = new List<string>();
        internal Func<int, CancellationToken, Task<HttpResponseMessage>> Next;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uris.Add(request.RequestUri);
            Auth.Add(request.Headers.Authorization == null ? null : request.Headers.Authorization.ToString());
            Check(request.Method == HttpMethod.Get, "read only costs request");
            return Next(Uris.Count - 1, token);
        }
    }

    private static Handler Sequence(params string[] pages)
    {
        return new Handler { Next = (index, token) => { if (index >= pages.Length) throw new Exception("Unexpected extra request"); return Task.FromResult(Response(pages[index])); } };
    }
    private static UsageSnapshot Read(Handler handler, double? budget = null, int deadlineMs = 1000, DateTimeOffset? now = null)
    {
        using (var client = new HttpClient(handler))
            return new OpenAiUsageService(client, () => now ?? Now, deadlineMs).ReadAsync("test-admin-key", budget).GetAwaiter().GetResult();
    }
    private static string Fails(Handler handler, double? budget = null, int deadlineMs = 1000)
    {
        try { Read(handler, budget, deadlineMs); }
        catch (InvalidOperationException error) { checks++; return error.Message; }
        throw new Exception("Expected safe failure");
    }

    public static int Main()
    {
        try
        {
            var pages = Sequence(Page(1, true, "cursor+/&?=1", Amount(0.1m, "usd"), Amount(0.2m, "usd")), Page(2, false, null, Amount(12m, "usd")));
            var budget = Read(pages, 100);
            var row = budget.Rows[0];
            Check(row.DisplayValue == "$87.70" && row.Remaining == 87.7, "all pages and decimal costs included in remaining user budget");
            Check(Math.Abs(row.RemainingPercent.Value - 87.7) < 0.0001, "budget percentage agrees with amount");
            Check(row.DisplaySubtitle.Contains("$12.30") && row.DisplaySubtitle.Contains("$100.00"), "cost and manually configured budget labelled");
            Check(budget.Note.Contains("선불 잔액이 아닙니다") && row.Detail.Contains("직접 설정한"), "not represented as API prepaid credit");
            Check(pages.Uris.Count == 2 && pages.Uris[1].AbsoluteUri.Contains("page=" + Uri.EscapeDataString("cursor+/&?=1")), "opaque cursor encoded as query data");
            Check(pages.Uris[0].Query.Contains("start_time=" + Day(1)) && pages.Uris[0].Query.Contains("limit=31") && !pages.Uris[0].Query.Contains("group_by"), "calendar-month organization query without filters");
            Check(pages.Auth.TrueForAll(x => x == "Bearer test-admin-key"), "credential stays in Authorization header");
            Check(pages.Uris.TrueForAll(x => x.Scheme == "https" && x.Host == "api.openai.com" && x.AbsolutePath == "/v1/organization/costs"), "fixed secure origin for every page");
            Check(row.ResetsAt.Value == new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), "next UTC calendar month reset");

            var noBudget = Read(Sequence(Page(1, false, null, Amount(12.34m, "usd"))));
            Check(noBudget.Rows[0].DisplayValue == "$12.34" && noBudget.Rows[0].Label == "이번 달 비용" && !noBudget.Rows[0].RemainingPercent.HasValue && !noBudget.Rows[0].Remaining.HasValue, "no inferred budget or fake remaining amount");
            var zero = Read(Sequence("{\"object\":\"page\",\"data\":[],\"has_more\":false,\"next_page\":null}"));
            Check(zero.Rows[0].DisplayValue == "$0.00", "valid empty response means zero cost");
            var tiny = Read(Sequence(Page(1, false, null, Amount(0.0003m, "usd"))));
            Check(tiny.Rows[0].DisplayValue == "<$0.01", "sub-cent nonzero cost remains visible");
            var over = Read(Sequence(Page(1, false, null, Amount(125m, "usd"))), 100);
            Check(over.Rows[0].Remaining == 0 && over.Rows[0].RemainingPercent == 0 && over.Rows[0].DisplaySubtitle.Contains("예산 초과 $25.00"), "budget overage visible and remaining not negative");
            var credit = Read(Sequence(Page(1, false, null, Amount(10m, "usd"), Amount(-2m, "usd"))), 100);
            Check(credit.Rows[0].Remaining == 92 && credit.Rows[0].Detail.Contains("차감·조정"), "signed adjustments summed accurately");
            var negative = Read(Sequence(Page(1, false, null, Amount(-2m, "usd"))), 100);
            Check(negative.Rows[0].DisplayValue == "-$2.00" && !negative.Rows[0].Remaining.HasValue && !negative.Rows[0].RemainingPercent.HasValue, "net negative charges do not inflate remaining budget");
            Check(Fails(Sequence(Page(1, false, null, Amount(4m, "eur")))).Contains("USD"), "unknown currency not added as dollars");
            Fails(Sequence(Page(1, false, null, new { @object = "organization.costs.result", amount = new { currency = "usd" } })));
            Fails(Sequence(Page(1, false, null, new { @object = "organization.costs.result", amount = new { value = "10", currency = "usd" } })));
            Fails(Sequence(Page(1, false, null, new { @object = "organization.usage.completions.result", amount = new { value = 10, currency = "usd" } })));
            Fails(Sequence("{\"object\":\"page\",\"data\":[],\"next_page\":null}"));
            Fails(Sequence("{\"object\":\"page\",\"has_more\":false}"));
            Check(Fails(Sequence(Page(1, true, null, Amount(5m, "usd")))).Contains("완전히"), "missing continuation fails rather than returning partial total");
            Fails(Sequence(Page(1, true, "same", Amount(1m, "usd")), Page(2, true, "same", Amount(2m, "usd"))));
            Fails(Sequence(Page(1, true, "next", Amount(1m, "usd")), Page(1, false, null, Amount(1m, "usd"))));
            Fails(Sequence(Page(1, false, "contradiction", Amount(1m, "usd"))));
            Fails(Sequence("{\"object\":\"page\",\"has_more\":true,\"next_page\":\"x\",\"data\":[]}"));
            Fails(Sequence(Page(11, false, null, Amount(1m, "usd"))));
            Fails(Sequence(Page(1, false, null, Amount(1m, "usd")).Replace("\"start_time\":" + Day(1), "\"start_time\":" + (Day(1) - 86400))));
            Fails(Sequence(Page(1, false, null, Amount(1m, "usd")).Replace("\"end_time\":" + (Day(1) + 86400), "\"end_time\":" + (Day(1) + 86401))));

            var keys = Sequence(Page(1, false, null, Amount(1m, "usd")));
            using (var client = new HttpClient(keys))
            {
                try { new OpenAiUsageService(client, () => Now, 1000).ReadAsync("bad\r\nHeader:key", null).GetAwaiter().GetResult(); throw new Exception("header injection accepted"); }
                catch (InvalidOperationException) { Check(keys.Uris.Count == 0, "invalid key rejected before network"); }
            }
            var wrongBudget = Sequence();
            Fails(wrongBudget, Double.NaN);
            Check(wrongBudget.Uris.Count == 0, "invalid budget makes no request");
            Fails(Sequence(), 0);
            Fails(Sequence(), -1);
            Fails(Sequence(), 1000000001);
            var underflowBudget = Sequence();
            Check(Fails(underflowBudget, 1e-30).Contains("0.01"), "positive double underflow rejected with minimum budget guidance");
            Check(underflowBudget.Uris.Count == 0, "underflow budget rejected before transport");
            Fails(Sequence(), 0.009);
            var minimumBudget = Read(Sequence(Page(1, false, null, Amount(0m, "usd"))), 0.01);
            Check(minimumBudget.Rows[0].Remaining == 0.01 && minimumBudget.Rows[0].RemainingPercent == 100, "minimum valid one-cent budget accepted");

            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, (HttpStatusCode)429, HttpStatusCode.Redirect, HttpStatusCode.InternalServerError })
            {
                var http = new Handler { Next = (index, token) => Task.FromResult(Response("secret-key-and-server-details", status)) };
                var error = Fails(http);
                Check(!error.Contains("secret-key") && http.Uris.Count == 1, "HTTP errors sanitized without retries or redirect request");
            }
            var afterPartial = new Handler { Next = (index, token) => Task.FromResult(index == 0 ? Response(Page(1, true, "page2", Amount(10m, "usd"))) : Response("secret", HttpStatusCode.Forbidden)) };
            Check(Fails(afterPartial).Contains("권한"), "later page failure invalidates whole total");
            var slow = new Handler { Next = async (index, token) => { await Task.Delay(10000, token); return Response("{}"); } };
            Check(Fails(slow, null, 30).Contains("시간이 초과"), "overall deadline cancels network operation");
            var maliciousCursor = "https://untrusted.example/steal?token=other";
            var cursorData = Sequence(Page(1, true, maliciousCursor, Amount(1m, "usd")), Page(2, false, null, Amount(1m, "usd")));
            Read(cursorData);
            Check(cursorData.Uris[1].Host == "api.openai.com" && cursorData.Uris[1].Query.Contains(Uri.EscapeDataString(maliciousCursor)), "cursor cannot replace destination origin");
            var koreaMonthBoundary = new DateTimeOffset(2026, 11, 1, 8, 59, 0, TimeSpan.FromHours(9));
            var boundary = Sequence(Page(1, false, null, Amount(1m, "usd")));
            Read(boundary, null, 1000, koreaMonthBoundary);
            Check(boundary.Uris[0].Query.Contains("start_time=" + Day(1)), "UTC calendar month not local time month");
            Console.WriteLine("PASS " + checks.ToString(CultureInfo.InvariantCulture) + " OpenAI cost checks (mock transport; no live credentials)");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
    }
}
