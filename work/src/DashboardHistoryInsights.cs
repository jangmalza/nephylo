using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace UsageWidget
{
    internal sealed class DashboardObservedPeriod
    {
        internal double Consumption, CoveredHours;
        internal int PointCount, IntervalCount;
        internal DateTimeOffset? Latest;
        internal bool HasConsumption { get { return IntervalCount > 0; } }
    }

    internal sealed class DashboardHistoryComparison
    {
        internal readonly DashboardObservedPeriod Current = new DashboardObservedPeriod();
        internal readonly DashboardObservedPeriod Previous = new DashboardObservedPeriod();
        internal string Unit, Reason;
        internal bool Comparable, BasisChanged;
        internal double? DifferencePercent;
        internal double DurationHours;
    }

    internal static class DashboardHistoryInsights
    {
        internal static DashboardHistoryComparison CompareKnownBasis(IEnumerable<UsageHistoryPoint> observations,
            string service, string label, string source, UsageAnalysis basis, DateTimeOffset now, int days, double gapMinutes,
            UsageObservedHistory observed = null)
        {
            if (String.IsNullOrEmpty(service) || String.IsNullOrEmpty(label))
                return Compare(observations, service, label, now, days, gapMinutes);
            if (source == "manual") return new DashboardHistoryComparison { DurationHours = Math.Max(1,days)*24.0,
                Reason = "수동 기록은 관측 소모량을 계산하지 않습니다." };
            if (basis == null || basis.Source != source || basis.CollectionHours <= 0 || Double.IsNaN(basis.CollectionHours)
                || Double.IsInfinity(basis.CollectionHours) || basis.CollectionHours > (now-DateTimeOffset.MinValue).TotalHours)
                return new DashboardHistoryComparison { DurationHours = Math.Max(1,days)*24.0,
                    Reason = "현재 연결 기준의 기록이 쌓이면 비교합니다. 이전 연결 기록은 합산하지 않습니다." };
            // CollectionHours is derived from the current analysis epoch, which
            // resets on the Nephylo account, explicit connection, source, plan,
            // or capacity change. Historical chart records do not carry an
            // account identity and must not bypass that established boundary.
            // A provider-account switch outside the app, with otherwise
            // identical metadata, remains undetectable until reconnection.
            DateTimeOffset first = now.AddHours(-basis.CollectionHours);
            // Persisted interval sums start only after this feature first saw
            // original observations. Never reconstruct missing aggregates
            // from older, already compacted chart endpoints.
            if(observed!=null) {
                if(observed.Source!=source || observed.Since<first.AddMilliseconds(-1) || observed.Since>now)
                    return new DashboardHistoryComparison { DurationHours=Math.Max(1,days)*24.0,
                        Reason="현재 연결 기준의 기록이 쌓이면 비교합니다. 이전 연결 기록은 합산하지 않습니다." };
                first=observed.Since;
            }
            var points = (observations ?? Enumerable.Empty<UsageHistoryPoint>()).Where(point => point != null).ToList();
            var newest = points.Where(point => point.ServiceId == service && point.Label == label && point.ObservedAt <= now)
                .OrderBy(point => point.ObservedAt).LastOrDefault();
            if (newest != null && newest.Source != source)
                return new DashboardHistoryComparison { DurationHours = Math.Max(1,days)*24.0,
                    Reason = "현재 연결 기준의 기록이 쌓이면 비교합니다. 이전 연결 기록은 합산하지 않습니다." };
            bool excluded = points.Any(point => point.ServiceId == service && point.Label == label && point.ObservedAt < first.AddMilliseconds(-1));
            var result = Compare(points.Where(point => point.ObservedAt >= first.AddMilliseconds(-1)), service, label, now, days, gapMinutes, observed);
            if (excluded) {
                result.BasisChanged = true;
                if (result.Unit != null && !result.Comparable && days != 30)
                    result.Reason = "현재 연결 기준 이후의 기록만 사용합니다. 이전 기간 비교 자료가 부족합니다.";
            }
            return result;
        }

        // These are observed decreases, never estimates of the unobserved part
        // of a day. A boundary-crossing interval is deliberately not prorated.
        internal static DashboardHistoryComparison Compare(IEnumerable<UsageHistoryPoint> observations,
            string service, string label, DateTimeOffset now, int days, double gapMinutes, UsageObservedHistory observed = null)
        {
            var result = new DashboardHistoryComparison { DurationHours = Math.Max(1, days) * 24.0 };
            if (String.IsNullOrEmpty(service) || String.IsNullOrEmpty(label)) {
                result.Reason = "서비스와 항목을 하나 선택하면 기간별 관측 소모량을 비교합니다."; return result;
            }
            DateTimeOffset start = now.AddHours(-result.DurationHours), previousStart = start.AddHours(-result.DurationHours);
            var points = (observations ?? Enumerable.Empty<UsageHistoryPoint>()).Where(point => point != null
                && point.ServiceId == service && point.Label == label && point.ObservedAt <= now
                && point.ObservedAt >= previousStart).OrderBy(point => point.ObservedAt).ToList();
            if (points.Count == 0) { result.Reason = "비교할 관측 기록이 아직 없습니다."; return result; }
            var latest = points[points.Count - 1];
            if (latest.Source == "manual") { result.Reason = "수동 기록은 관측 소모량을 계산하지 않습니다."; return result; }
            if (!UsageSourceIdentity.IsValid(latest.Source)) { result.Reason = "기록의 출처를 확인할 수 없어 비교하지 않습니다."; return result; }
            var numeric = points.Where(point => UsageHistoryChart.MetricValue(point).HasValue).ToList();
            if (numeric.Select(UsageHistoryChart.MetricUnit).Distinct().Count() > 1) {
                result.Reason = "단위가 바뀐 기록은 합산하거나 비교하지 않습니다."; return result;
            }
            var lastNumeric = numeric.LastOrDefault();
            if (lastNumeric == null) { result.Reason = "비교할 수치 기록이 아직 없습니다."; return result; }
            result.Unit = UsageHistoryChart.MetricUnit(lastNumeric) == "%" ? "%p" : UsageHistoryChart.MetricUnit(lastNumeric);

            // Only the latest contiguous source/capacity basis may contribute.
            // Switching A -> B -> A must not resurrect the first A's usage.
            int basisStart = 0;
            for (int index = points.Count - 1; index >= 0; index--) {
                if (points[index].Source != latest.Source || points[index].Limit != latest.Limit) {
                    basisStart = index + 1; result.BasisChanged = true; break;
                }
            }
            var basis = points.Skip(basisStart).ToList();
            var duplicates = new HashSet<DateTimeOffset>(basis.GroupBy(point => point.ObservedAt).Where(group => group.Count() > 1).Select(group => group.Key));
            CountPoints(result.Current, basis, start, now, duplicates);
            CountPoints(result.Previous, basis, previousStart, start, duplicates);
            for (int index = 1; observed==null && index < basis.Count; index++) {
                var before = basis[index - 1]; var after = basis[index];
                if (!ValidInterval(before, after, gapMinutes, duplicates)) continue;
                DashboardObservedPeriod period = before.ObservedAt >= start ? result.Current
                    : after.ObservedAt <= start && before.ObservedAt >= previousStart ? result.Previous : null;
                if (period == null) continue;
                double decrease = UsageHistoryChart.MetricValue(before).Value - UsageHistoryChart.MetricValue(after).Value;
                if (Double.IsInfinity(period.Consumption + decrease))
                    return new DashboardHistoryComparison { DurationHours = result.DurationHours, Reason = "수치 범위를 확인할 수 없어 비교하지 않습니다." };
                period.Consumption += decrease;
                period.CoveredHours += (after.ObservedAt - before.ObservedAt).TotalHours;
                ++period.IntervalCount;
            }
            if(observed!=null && observed.Source==latest.Source && observed.Unit==result.Unit) {
                DateTimeOffset previousEnd=DateTimeOffset.MinValue;
                foreach(var bucket in (observed.Buckets ?? new List<UsageObservedBucket>()).Where(item=>item!=null).OrderBy(item=>item.Start)) {
                    if(bucket.Start<previousEnd || bucket.Start<observed.Since || bucket.End<=bucket.Start || bucket.End>now
                        || bucket.Intervals<1 || bucket.Hours<=0 || Double.IsNaN(bucket.Hours) || Double.IsInfinity(bucket.Hours)
                        || bucket.Hours>(bucket.End-bucket.Start).TotalHours+0.000001 || bucket.Consumption<0
                        || Double.IsNaN(bucket.Consumption) || Double.IsInfinity(bucket.Consumption)
                        || bucket.MaximumGapMinutes<=0 || Double.IsNaN(bucket.MaximumGapMinutes) || bucket.MaximumGapMinutes>Math.Max(30,gapMinutes)) continue;
                    previousEnd=bucket.End;
                    DashboardObservedPeriod period=bucket.Start>=start ? result.Current
                        : bucket.End<=start && bucket.Start>=previousStart ? result.Previous : null;
                    if(period==null) continue;
                    if(Double.IsInfinity(period.Consumption+bucket.Consumption))
                        return new DashboardHistoryComparison {DurationHours=result.DurationHours,Reason="수치 범위를 확인할 수 없어 비교하지 않습니다."};
                    period.Consumption+=bucket.Consumption; period.CoveredHours+=bucket.Hours; period.IntervalCount+=bucket.Intervals;
                }
            }
            double minimumHours = result.DurationHours * 0.25;
            if (result.Current.IntervalCount < 3 || result.Previous.IntervalCount < 3
                || result.Current.CoveredHours + 0.000001 < minimumHours || result.Previous.CoveredHours + 0.000001 < minimumHours) {
                result.Reason = days == 30 ? "30일 비교에는 이전 30일 기록도 필요합니다. 현재 보관 범위에서는 비교 자료가 부족합니다."
                    : result.BasisChanged ? "출처 또는 한도가 바뀐 이후의 기록만 사용합니다. 이전 기간 비교 자료가 부족합니다."
                    : "두 기간 모두 25% 이상 관측하고 유효 구간이 3개 이상 쌓이면 비교합니다.";
                return result;
            }
            double balance = Math.Min(result.Current.CoveredHours, result.Previous.CoveredHours) / Math.Max(result.Current.CoveredHours, result.Previous.CoveredHours);
            if (balance < 0.8) { result.Reason = "관측 시간의 차이가 커서 증감률을 비교하지 않습니다."; return result; }
            result.Comparable = true;
            if (result.Previous.Consumption > 0) {
                double difference = (result.Current.Consumption / result.Previous.Consumption - 1) * 100;
                if (Double.IsInfinity(difference) || Double.IsNaN(difference)) {
                    result.Comparable = false; result.Reason = "수치 범위를 확인할 수 없어 비교하지 않습니다."; return result;
                }
                result.DifferencePercent = difference;
            }
            result.Reason = result.BasisChanged ? "출처 또는 한도가 바뀐 이후의 기록만 비교합니다." : "전체 사용량이 아닌 기록된 구간 기준입니다.";
            return result;
        }

        private static void CountPoints(DashboardObservedPeriod period, List<UsageHistoryPoint> points,
            DateTimeOffset start, DateTimeOffset end, HashSet<DateTimeOffset> duplicates)
        {
            foreach (var point in points.Where(point => point.ObservedAt >= start && point.ObservedAt <= end
                && ValidPoint(point) && !duplicates.Contains(point.ObservedAt))) {
                ++period.PointCount; period.Latest = point.ObservedAt;
            }
        }

        private static bool ValidPoint(UsageHistoryPoint point)
        {
            return UsageHistoryChart.MetricValue(point).HasValue && point.Source != "manual" && UsageSourceIdentity.IsValid(point.Source)
                && (!point.ResetsAt.HasValue || point.ResetsAt > point.ObservedAt)
                && (!point.Limit.HasValue || !Double.IsNaN(point.Limit.Value) && !Double.IsInfinity(point.Limit.Value) && point.Limit.Value >= 0);
        }

        private static bool ValidInterval(UsageHistoryPoint before, UsageHistoryPoint after, double gapMinutes, HashSet<DateTimeOffset> duplicates)
        {
            return ValidPoint(before) && ValidPoint(after) && !duplicates.Contains(before.ObservedAt) && !duplicates.Contains(after.ObservedAt)
                && UsageHistoryChart.Connects(before, after, gapMinutes)
                // Chart compaction tolerance is only a visual continuity aid.
                // A bucket's last reading cannot prove the whole bucket was
                // observed; never use its resolution to expand coverage.
                && (after.ObservedAt-before.ObservedAt).TotalMinutes <= Math.Max(30,gapMinutes)
                // The chart tolerates small visual corrections; consumption
                // must not silently absorb even a small top-up or correction.
                && UsageHistoryChart.MetricValue(after).Value <= UsageHistoryChart.MetricValue(before).Value;
        }
    }

    public partial class WidgetWindow
    {
        private sealed class DashboardHistoryInsightCard
        {
            internal Border Card;
            internal Grid Metrics;
            internal TextBlock Current, Previous, CurrentRange, PreviousRange, CurrentCoverage, PreviousCoverage, Comparison, Quality, Reason, Methodology;
            internal Expander Disclosure;
        }
        private DashboardHistoryInsightCard dashboardHistoryInsights;

        private DashboardHistoryInsightCard CreateDashboardHistoryInsights()
        {
            var item = new DashboardHistoryInsightCard();
            var body = new LocalizedStackPanel();
            body.Children.Add(DashboardText("관측 소모량 비교", 16, "#252827", FontWeights.SemiBold));
            item.Metrics = new Grid { Margin = new Thickness(0,18,0,16) };
            item.Metrics.ColumnDefinitions.Add(new ColumnDefinition()); item.Metrics.ColumnDefinitions.Add(new ColumnDefinition());
            item.Current = AddDashboardAnalysisMetric(item.Metrics, 0, "선택 기간", out item.CurrentCoverage);
            item.Previous = AddDashboardAnalysisMetric(item.Metrics, 1, "직전 같은 기간", out item.PreviousCoverage);
            item.CurrentRange=AddDashboardObservedDateRange(item.Current);
            item.PreviousRange=AddDashboardObservedDateRange(item.Previous);
            UiL.Raw(item.CurrentCoverage); UiL.Raw(item.PreviousCoverage); body.Children.Add(item.Metrics);
            item.Comparison = DashboardText("", 14, "#252827", FontWeights.SemiBold); UiL.Raw(item.Comparison); body.Children.Add(item.Comparison);
            item.Quality = DashboardText("", 13, "#647070", FontWeights.Normal, new Thickness(0,8,0,0)); UiL.Raw(item.Quality); body.Children.Add(item.Quality);
            item.Reason = DashboardText("", 14, "#647070", FontWeights.Normal, new Thickness(0,8,0,0)); UiL.Raw(item.Reason); body.Children.Add(item.Reason);
            item.Methodology = DashboardText("",13,"#647070",FontWeights.Normal,new Thickness(0,12,0,0)); UiL.Raw(item.Methodology);
            item.Disclosure = new LocalizedExpander { Header="기록·비교 기준",Content=item.Methodology,FontSize=13,
                Foreground=Muted,Margin=new Thickness(0,16,0,0),IsExpanded=false };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item.Disclosure,"dashboard-history-methodology");
            bool expanded;
            if(dashboardHistoryDisclosureStates.TryGetValue("history:insights",out expanded)) item.Disclosure.IsExpanded=expanded;
            body.Children.Add(item.Disclosure);
            item.Card = DashboardCard(body); item.Card.Padding = new Thickness(20); item.Card.Margin = new Thickness(0,0,0,16);
            item.Card.Tag="history:insights";
            return item;
        }

        private static TextBlock AddDashboardObservedDateRange(TextBlock value)
        {
            var parent=(StackPanel)value.Parent;
            var range=DashboardText("",13,"#647070",FontWeights.Normal,new Thickness(0,6,0,0)); UiL.Raw(range);
            parent.Children.Insert(1,range); return range;
        }

        private void UpdateDashboardHistoryInsights(HistoryMetricChoice metric, DateTimeOffset now)
        {
            if (dashboardHistoryInsights == null) return;
            string service = metric == null ? null : metric.ServiceId, label = metric == null ? null : metric.Label;
            var history = usageJournal.GetHistory();
            var latest = history.LastOrDefault(point => point.ServiceId == service && point.Label == label);
            string source = service != null && DashboardViews().Any(view => view.Id == service) ? CurrentAnalysisSource(service) : latest == null ? null : latest.Source;
            UsageAnalysis basis = service == null || label == null ? null : usageJournal.GetAnalysis(service,label,source,now);
            var summary = DashboardHistoryInsights.CompareKnownBasis(history, service, label, source, basis,
                now, dashboardHistoryDays, Math.Max(30, settings.RefreshSeconds / 60.0 * 3),
                service==null || label==null ? null : usageJournal.GetObservedHistory(service,label,source,now));
            var item = dashboardHistoryInsights;
            DateTimeOffset start=now.AddHours(-summary.DurationHours);
            item.CurrentRange.Text=DashboardObservedDateRange(start,now);
            item.PreviousRange.Text=DashboardObservedDateRange(start.AddHours(-summary.DurationHours),start);
            item.Current.Text = ObservedConsumption(summary.Current, summary.Unit);
            item.Previous.Text = ObservedConsumption(summary.Previous, summary.Unit);
            item.CurrentCoverage.Text = ObservedCoverage(summary.Current, summary.DurationHours);
            item.PreviousCoverage.Text = ObservedCoverage(summary.Previous, summary.DurationHours);
            item.Metrics.Visibility = summary.Unit == null ? Visibility.Collapsed : Visibility.Visible;
            string comparison = "";
            if (summary.Comparable) {
                if (summary.DifferencePercent.HasValue) {
                    double change = summary.DifferencePercent.Value;
                    comparison = Math.Abs(change) < 0.05 ? L.Text("관측 소모량 변화 없음")
                        : String.Format(CultureInfo.CurrentCulture, L.Text(change > 0 ? "직전 기간보다 {0}% 증가" : "직전 기간보다 {0}% 감소"), Math.Abs(change).ToString("0.#",CultureInfo.CurrentCulture));
                } else comparison = L.Text(summary.Current.Consumption > 0 ? "직전 기간은 관측 소모량 0 · 이번 기간 소모 확인" : "두 기간 모두 관측 소모량 0");
            }
            item.Comparison.Text = comparison; item.Comparison.Visibility = comparison.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            item.Quality.Text = summary.Current.Latest.HasValue ? String.Format(CultureInfo.CurrentCulture,
                L.Text("선택 기간 {0}개 기록 · 마지막 확인 {1}"), summary.Current.PointCount.ToString("N0",CultureInfo.CurrentCulture),
                summary.Current.Latest.Value.ToLocalTime().ToString("MM/dd HH:mm",CultureInfo.InvariantCulture)) : "";
            item.Quality.Visibility = item.Quality.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            item.Reason.Text = L.Text(DashboardComparisonShortReason(summary));
            var methodology=new List<string> { L.Text(summary.Reason) };
            if(summary.Reason!="전체 사용량이 아닌 기록된 구간 기준입니다.") methodology.Add(L.Text("전체 사용량이 아닌 기록된 구간 기준입니다."));
            methodology.Add(L.Text("두 기간 모두 25% 이상 관측하고 유효 구간이 3개 이상 쌓이면 비교합니다."));
            methodology.Add(L.Text("관측 시간 차이가 20% 이내일 때만 증감률을 표시하며, 기록되지 않은 시간은 추정하지 않습니다."));
            methodology.Add(L.Text("기록 공백과 초기화·충전 구간은 소모량 계산에서 제외합니다."));
            methodology.Add(L.Text("관측 구간의 소모량과 시간을 별도로 보관합니다. 기간 경계에 걸친 집계는 나누어 추정하지 않고 제외합니다."));
            item.Methodology.Text=String.Join("\n",methodology.Distinct());
            item.Disclosure.Visibility=summary.Unit==null ? Visibility.Collapsed : Visibility.Visible;
        }

        internal static string DashboardObservedDateRange(DateTimeOffset start,DateTimeOffset end)
        {
            var localStart=start.ToLocalTime(); var localEnd=end.ToLocalTime();
            string format=localStart.Year==localEnd.Year ? "MM/dd HH:mm" : "yyyy/MM/dd HH:mm";
            return localStart.ToString(format,CultureInfo.InvariantCulture) + " – " + localEnd.ToString(format,CultureInfo.InvariantCulture);
        }

        private static string DashboardComparisonShortReason(DashboardHistoryComparison summary)
        {
            if(summary.Unit==null) return summary.Reason;
            if(summary.Comparable) return "전체 사용량이 아닌 기록된 구간 기준입니다.";
            if(summary.DurationHours>=720) return "이전 30일 기록이 부족하여 비교를 표시하지 않습니다.";
            if(summary.Reason=="관측 시간의 차이가 커서 증감률을 비교하지 않습니다.") return summary.Reason;
            if(summary.Reason=="수치 범위를 확인할 수 없어 비교하지 않습니다.") return summary.Reason;
            if(!summary.Current.HasConsumption && summary.Current.PointCount==0) return "선택 기간의 기록이 없습니다. 새 잔량을 확인하면 기록이 쌓입니다.";
            if(!summary.Previous.HasConsumption) return "이전 기간의 비교 기록이 부족합니다. 현재 관측 소모량만 표시합니다.";
            return "비교 자료 수집 중 · 기록이 충분해지면 증감률을 표시합니다.";
        }

        private static string ObservedConsumption(DashboardObservedPeriod period, string unit)
        { return period.HasConsumption ? period.Consumption.ToString("#,0.##",CultureInfo.CurrentCulture) + " " + L.Text(unit) : "—"; }

        private static string ObservedCoverage(DashboardObservedPeriod period, double durationHours)
        {
            if(!period.HasConsumption) return period.PointCount==0 ? L.Text("이 기간의 기록 없음")
                : String.Format(CultureInfo.CurrentCulture,L.Text("기록 {0}개 · 소모량 계산 대기"),period.PointCount.ToString("N0",CultureInfo.CurrentCulture));
            return String.Format(CultureInfo.CurrentCulture, L.Text("관측 {0}시간 · 기간의 {1}%"),
                period.CoveredHours.ToString("0.#",CultureInfo.CurrentCulture), (period.CoveredHours / durationHours * 100).ToString("0.#",CultureInfo.CurrentCulture));
        }
    }
}
