using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // Costs are organization-wide net charges, not prepaid credits or ChatGPT limits.
    public sealed class OpenAiUsageService
    {
        private static readonly HttpClient SharedClient = CreateClient();
        private readonly HttpClient client;
        private readonly Func<DateTimeOffset> clock;
        private readonly int timeoutMs;
        private const int MaxPages = 32;
        private const int MaxBodyLength = 2097152;
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public OpenAiUsageService() : this(SharedClient, () => DateTimeOffset.UtcNow, 30000) { }

        internal OpenAiUsageService(HttpClient httpClient, Func<DateTimeOffset> utcClock, int deadlineMs)
        {
            client = httpClient;
            clock = utcClock;
            timeoutMs = deadlineMs;
        }

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var result = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,
                MaxResponseContentBufferSize = MaxBodyLength
            };
            result.DefaultRequestHeaders.UserAgent.ParseAdd("UsageWidget/1.2");
            return result;
        }

        public async Task<UsageSnapshot> ReadAsync(string adminApiKey, double? monthlyBudgetUsd)
        {
            if (String.IsNullOrWhiteSpace(adminApiKey)) throw new ConnectionException(ConnectionDiagnostics.MissingKey("openai"));
            adminApiKey = adminApiKey.Trim();
            if (adminApiKey.Length > 4096) throw new ConnectionException(ConnectionDiagnostics.InvalidKeyFormat("openai"));
            foreach (char c in adminApiKey)
                if (c < 33 || c > 126) throw new ConnectionException(ConnectionDiagnostics.InvalidKeyFormat("openai"));
            decimal? budget = ValidateBudget(monthlyBudgetUsd);
            var now = clock().ToUniversalTime();
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            long start = (long)(monthStart - Epoch).TotalSeconds;
            long end = Math.Max(start + 1, (long)(now - Epoch).TotalSeconds);
            string cursor = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            var buckets = new HashSet<long>();
            decimal total = 0;
            bool hasAdjustments = false;
            try
            {
                using (var deadline = new CancellationTokenSource(timeoutMs))
                {
                    for (int pageNumber = 0; pageNumber < MaxPages; pageNumber++)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        var address = "https://api.openai.com/v1/organization/costs?start_time=" + start.ToString(CultureInfo.InvariantCulture)
                            + "&end_time=" + end.ToString(CultureInfo.InvariantCulture) + "&bucket_width=1d&limit=31";
                        if (cursor != null) address += "&page=" + Uri.EscapeDataString(cursor);
                        using (var request = new HttpRequestMessage(HttpMethod.Get, address))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminApiKey);
                            using (var response = await client.SendAsync(request, deadline.Token).ConfigureAwait(false))
                            {
                                if (!response.IsSuccessStatusCode) throw new ConnectionException(ConnectionDiagnostics.Http("openai", (int)response.StatusCode), ConnectionDiagnostics.RetryAfter(response));
                                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                var page = ParsePage(body, start, end, buckets);
                                total = checked(total + page.Total);
                                hasAdjustments |= page.HasAdjustments;
                                if (!page.HasMore) return MakeSnapshot(total, budget, monthStart.AddMonths(1), hasAdjustments);
                                if (!cursors.Add(page.NextPage)) throw Incomplete();
                                cursor = page.NextPage;
                            }
                        }
                    }
                }
                throw Incomplete();
            }
            catch (OperationCanceledException) { throw new ConnectionException(ConnectionDiagnostics.Timeout("openai")); }
            catch (HttpRequestException) { throw new ConnectionException(ConnectionDiagnostics.Network("openai")); }
            catch (OverflowException) { throw InvalidResponse(); }
            catch (FormatException) { throw InvalidResponse(); }
        }

        private sealed class CostsPage
        {
            internal decimal Total;
            internal bool HasAdjustments;
            internal bool HasMore;
            internal string NextPage;
        }

        private static CostsPage ParsePage(string json, long monthStart, long queryEnd, HashSet<long> seenBuckets)
        {
            Dictionary<string, object> root;
            try
            {
                if (String.IsNullOrWhiteSpace(json) || json.Length > MaxBodyLength) throw InvalidResponse();
                root = new JavaScriptSerializer { MaxJsonLength = MaxBodyLength, RecursionLimit = 32 }.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (ArgumentException) { throw InvalidResponse(); }
            catch (InvalidOperationException) { throw InvalidResponse(); }
            if (root == null || UsageJson.String(root, "object") != "page") throw InvalidResponse();
            var hasMore = UsageJson.Boolean(root, "has_more");
            var data = Array(root, "data");
            if (!hasMore.HasValue || data == null) throw InvalidResponse();
            var page = new CostsPage { HasMore = hasMore.Value, NextPage = UsageJson.String(root, "next_page") };
            if (page.HasMore && (String.IsNullOrWhiteSpace(page.NextPage) || page.NextPage.Length > 4096)) throw Incomplete();
            if (page.HasMore && data.Length == 0) throw Incomplete();
            if (!page.HasMore && !String.IsNullOrEmpty(page.NextPage)) throw Incomplete();
            foreach (var item in data)
            {
                var bucket = item as Dictionary<string, object>;
                if (bucket == null || UsageJson.String(bucket, "object") != "bucket") throw InvalidResponse();
                var start = Integer(bucket, "start_time");
                var end = Integer(bucket, "end_time");
                // Daily responses may finish the current UTC day beyond queryEnd.
                if (start < monthStart || start >= queryEnd || end <= start || end - start > 86400 || (start - monthStart) % 86400 != 0 || !seenBuckets.Add(start)) throw Incomplete();
                var results = Array(bucket, "results");
                if (results == null) throw InvalidResponse();
                foreach (var resultItem in results)
                {
                    var result = resultItem as Dictionary<string, object>;
                    if (result == null || UsageJson.String(result, "object") != "organization.costs.result") throw InvalidResponse();
                    var amount = UsageJson.Object(result, "amount");
                    if (!String.Equals(UsageJson.String(amount, "currency"), "usd", StringComparison.OrdinalIgnoreCase))
                        throw new ConnectionException(ConnectionDiagnostics.Malformed("openai", "OpenAI 응답에 USD 이외 통화 또는 확인할 수 없는 통화가 있어 비용을 합산하지 않았습니다. 잠시 후 다시 연결을 확인해 주세요."));
                    var value = Number(amount, "value");
                    page.Total = checked(page.Total + value);
                    page.HasAdjustments |= value < 0;
                }
            }
            return page;
        }

        private static object[] Array(Dictionary<string, object> value, string name)
        {
            object result;
            return value != null && value.TryGetValue(name, out result) ? result as object[] : null;
        }

        private static decimal Number(Dictionary<string, object> value, string name)
        {
            object raw;
            decimal result;
            if (value == null || !value.TryGetValue(name, out raw) || raw == null || raw is bool || raw is string
                || !Decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result)) throw InvalidResponse();
            return result;
        }

        private static long Integer(Dictionary<string, object> value, string name)
        {
            decimal result = Number(value, name);
            if (Decimal.Truncate(result) != result || result < 0 || result > Int64.MaxValue) throw InvalidResponse();
            return (long)result;
        }

        private static decimal? ValidateBudget(double? value)
        {
            if (!value.HasValue) return null;
            if (Double.IsNaN(value.Value) || Double.IsInfinity(value.Value) || value.Value < 0.01 || value.Value > 1000000000)
                throw new InvalidOperationException("월 예산은 0.01~1,000,000,000 USD 사이로 입력해 주세요.");
            return (decimal)value.Value;
        }

        private static UsageSnapshot MakeSnapshot(decimal total, decimal? budget, DateTimeOffset nextMonth, bool adjustments)
        {
            string cost = Money(total);
            var snapshot = new UsageSnapshot { Plan = "API · USD", Note = "조직 전체 · UTC 달력 월 · 비용 반영 지연 가능 · 선불 잔액이 아닙니다." };
            var row = new UsageRow { Label = "이번 달 비용", DisplayValue = cost, DisplaySubtitle = "조직 전체 · USD", ResetsAt = nextMonth };
            row.Detail = "UTC 기준 이번 달 조직 전체 비용: " + cost + ". 구독·선불 크레딧 잔액 조회가 아닙니다.";
            if (budget.HasValue && total >= 0)
            {
                decimal remaining = Math.Max(0, budget.Value - total);
                row.Label = "설정 예산 잔여";
                row.DisplayValue = Money(remaining);
                row.DisplaySubtitle = "사용 " + cost + " / 예산 " + Money(budget.Value);
                row.Remaining = (double)remaining;
                row.Limit = (double)budget.Value;
                row.RemainingPercent = UsageJson.Clamp((double)(remaining / budget.Value * 100));
                row.Detail += " 직접 설정한 월 예산 " + Money(budget.Value) + "에서 비용을 뺀 참고값이며 API 지출 제한을 변경하지 않습니다.";
                if (total > budget.Value) row.DisplaySubtitle += " · 예산 초과 " + Money(total - budget.Value);
            }
            else if (total < 0)
            {
                row.Label = "이번 달 순비용";
                row.DisplaySubtitle = "음수 비용 조정 · 잔여 예산 계산 제외";
                row.Detail += " 음수 비용 조정이 포함되어 잔여 예산으로 환산하지 않았습니다.";
            }
            if (adjustments) row.Detail += " 차감·조정 항목 포함.";
            snapshot.Rows.Add(row);
            return snapshot;
        }

        private static string Money(decimal value)
        {
            // Preserve small nonzero costs instead of rounding them to a false $0.00.
            if (value > 0 && value < 0.01m) return "<$0.01";
            if (value < 0 && value > -0.01m) return ">-$0.01";
            return (value < 0 ? "-$" : "$") + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture);
        }

        public static string ErrorMessage(int status)
        {
            return ConnectionDiagnostics.Http("openai", status).Message;
        }

        private static InvalidOperationException InvalidResponse() { return new ConnectionException(ConnectionDiagnostics.Malformed("openai", "OpenAI 비용 응답을 확인할 수 없습니다. 금액을 합산하지 않았습니다. 잠시 후 다시 연결을 확인해 주세요.")); }
        private static InvalidOperationException Incomplete() { return new ConnectionException(ConnectionDiagnostics.Malformed("openai", "OpenAI 비용 데이터를 완전히 확인하지 못했습니다. 일부 금액만 표시하지 않고 다음 갱신 때 다시 조회합니다.")); }
    }
}
