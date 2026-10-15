using System;
using System.Collections.Generic;
using System.Linq;

namespace UsageWidget
{
    // Inputs are defensive journal snapshots, never provider responses or account
    // identities. Callers decide entitlement and which currently connected rows
    // to include; this model performs no I/O and never starts collection.
    internal sealed class WeeklyUsageSeries
    {
        internal string ServiceId, Label, Source;
        internal UsageAnalysis Basis;
        internal UsageObservedHistory Observed;
        internal IEnumerable<UsageHistoryPoint> Points;
    }

    internal sealed class WeeklyUsageReport
    {
        internal DateTimeOffset CurrentStart, CurrentEnd, PreviousStart;
        internal DateTime LocalStart, LocalEndExclusive;
        internal int LowThresholdPercent;
        internal readonly List<WeeklyUsageItem> Items = new List<WeeklyUsageItem>();
    }

    internal sealed class WeeklyUsageItem
    {
        internal string ServiceId, Label, Source, Unit, Reason;
        internal bool Comparable, BasisChanged;
        internal double? DifferencePercent;
        internal readonly WeeklyUsagePeriod Current = new WeeklyUsagePeriod();
        internal readonly WeeklyUsagePeriod Previous = new WeeklyUsagePeriod();
    }

    internal sealed class WeeklyUsagePeriod
    {
        internal double Consumption, CoveredHours;
        internal int PointCount, ObservedDays, IntervalCount, ConsumptionDays, LowDays, LowSampleDays, UnassignedDayBuckets;
        internal DateTime? PeakDay;
        internal double? PeakConsumption;
        internal bool HasConsumption { get { return IntervalCount > 0; } }
        internal bool LowDaysAvailable { get { return LowSampleDays > 0; } }
        internal readonly HashSet<DateTime> ObservedDates = new HashSet<DateTime>();
        internal readonly HashSet<DateTime> LowSampleDates = new HashSet<DateTime>();
        internal readonly HashSet<DateTime> LowDates = new HashSet<DateTime>();
        internal readonly Dictionary<DateTime, double> DailyConsumption = new Dictionary<DateTime, double>();
    }

    internal static class WeeklyUsageSummary
    {
        // Completed local calendar days, not the current partial day or a rolling
        // 168-hour window. Actual elapsed hours can differ around DST transitions.
        internal static WeeklyUsageReport Build(IEnumerable<WeeklyUsageSeries> series, DateTimeOffset now,
            TimeZoneInfo zone = null, int lowThresholdPercent = 20)
        {
            zone = zone ?? TimeZoneInfo.Local;
            DateTime today = TimeZoneInfo.ConvertTime(now, zone).Date;
            var report = new WeeklyUsageReport {
                LocalStart = today.AddDays(-7), LocalEndExclusive = today,
                CurrentStart = DayStart(today.AddDays(-7), zone), CurrentEnd = DayStart(today, zone),
                PreviousStart = DayStart(today.AddDays(-14), zone),
                LowThresholdPercent = Math.Max(1, Math.Min(100, lowThresholdPercent))
            };
            // A duplicate caller selection must not make the same service appear
            // twice. Different labels are independent; no cross-unit grand total.
            var selections = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in series ?? Enumerable.Empty<WeeklyUsageSeries>()) {
                if (input == null || String.IsNullOrEmpty(input.ServiceId) || String.IsNullOrEmpty(input.Label)
                    || !selections.Add(input.ServiceId + "\n" + input.Label)) continue;
                report.Items.Add(Summarize(input, report, now, zone));
            }
            return report;
        }

        private static WeeklyUsageItem Summarize(WeeklyUsageSeries input, WeeklyUsageReport report,
            DateTimeOffset now, TimeZoneInfo zone)
        {
            var item = new WeeklyUsageItem { ServiceId = input.ServiceId, Label = input.Label, Source = input.Source };
            if (input.Source == "manual") {
                item.Reason = "수동 기록은 주간 관측 소모량을 계산하지 않습니다."; return item;
            }
            if (!UsageSourceIdentity.IsValid(input.Source)) {
                item.Reason = "자료 부족 · 자동 조회 출처를 확인할 수 없습니다."; return item;
            }
            // The chart intentionally retains old accounts/connections. Only the
            // active analysis epoch may authorize their use here. Do not infer a
            // fresh epoch from the newest chart point after logout/reconnection.
            if (input.Basis == null || input.Basis.Source != input.Source
                || !Finite(input.Basis.CollectionHours) || input.Basis.CollectionHours <= 0
                || input.Basis.CollectionHours > (now - DateTimeOffset.MinValue).TotalHours) {
                item.Reason = "자료 부족 · 현재 연결 기준의 기록이 쌓이면 요약합니다."; return item;
            }
            DateTimeOffset since = now.AddHours(-input.Basis.CollectionHours);
            var observed = input.Observed;
            if (observed != null) {
                if (observed.Source != input.Source || observed.Since < since.AddMilliseconds(-1) || observed.Since > now) {
                    item.Reason = "자료 부족 · 현재 연결 기준의 기록이 쌓이면 요약합니다."; return item;
                }
                since = observed.Since;
            }
            var points = (input.Points ?? Enumerable.Empty<UsageHistoryPoint>()).Where(point => point != null
                && point.ServiceId == input.ServiceId && point.Label == input.Label && point.ObservedAt <= now)
                .OrderBy(point => point.ObservedAt).ToList();
            item.BasisChanged = points.Any(point => point.ObservedAt < since.AddMilliseconds(-1));
            points = points.Where(point => point.ObservedAt >= since.AddMilliseconds(-1)).ToList();
            var newest = points.LastOrDefault();
            if (newest != null && newest.Source != input.Source) {
                item.Reason = "자료 부족 · 현재 연결 기준의 기록이 쌓이면 요약합니다."; return item;
            }
            if (newest != null && !ValidPoint(newest)) {
                item.Reason = "자료 부족 · 비교할 수치 기록이 아직 없습니다."; return item;
            }
            // An input obtained during a connection/source/capacity transition is
            // restricted to its newest contiguous basis. Returning A -> B -> A
            // cannot resurrect the original A observations or aggregate buckets.
            if (newest != null) {
                for (int index = points.Count - 1; index >= 0; --index) {
                    if (points[index].Source != newest.Source || points[index].Limit != newest.Limit
                        || NumericUnit(points[index]) != NumericUnit(newest)) {
                        since = points[index + 1].ObservedAt;
                        points = points.Skip(index + 1).ToList(); item.BasisChanged = true; break;
                    }
                }
            }
            item.Unit = observed != null && ValidUnit(observed.Unit) ? observed.Unit
                : newest == null ? null : NumericUnit(newest);
            if (!ValidUnit(item.Unit)) {
                item.Reason = "자료 부족 · 비교할 수치 기록이 아직 없습니다."; return item;
            }
            if (newest != null && NumericUnit(newest) != item.Unit) {
                item.Reason = "자료 부족 · 단위가 바뀐 기록은 합산하지 않습니다."; return item;
            }
            // Never choose an arbitrary winner for duplicate measurement times.
            var duplicated = new HashSet<DateTimeOffset>(points.GroupBy(point => point.ObservedAt)
                .Where(group => group.Count() > 1).Select(group => group.Key));
            foreach (var point in points) {
                if (duplicated.Contains(point.ObservedAt) || !ValidPoint(point) || NumericUnit(point) != item.Unit) continue;
                var period = PointPeriod(point.ObservedAt, report, item);
                if (period == null) continue;
                DateTime date = TimeZoneInfo.ConvertTime(point.ObservedAt, zone).Date;
                ++period.PointCount; period.ObservedDates.Add(date);
                double? percent = RemainingPercent(point);
                if (percent.HasValue) {
                    period.LowSampleDates.Add(date);
                    if (percent.Value <= report.LowThresholdPercent) period.LowDates.Add(date);
                }
            }
            // Only original accepted intervals have exact sums. Compacted chart
            // endpoints cannot establish unobserved consumption, reset crossings,
            // or coverage; older journals get useful point/low-day information
            // without fabricating a weekly total.
            if (observed != null && observed.Unit == item.Unit) {
                var buckets = (observed.Buckets ?? new List<UsageObservedBucket>()).Where(bucket => bucket != null && ValidBucket(bucket, since, now))
                    .OrderBy(bucket => bucket.Start).ThenBy(bucket => bucket.End).ToList();
                var overlaps = OverlappingBuckets(buckets);
                for (int index = 0; index < buckets.Count; ++index) {
                    var bucket = buckets[index];
                    // Overlapping aggregates are ambiguous, including the first
                    // member; exclude every overlap instead of order-dependent sums.
                    if (overlaps[index]) continue;
                    var period = IntervalPeriod(bucket.Start, bucket.End, report, item);
                    if (period == null) continue;
                    if (!Finite(period.Consumption + bucket.Consumption) || !Finite(period.CoveredHours + bucket.Hours)
                        || period.IntervalCount > Int32.MaxValue - bucket.Intervals) {
                        item.Reason = "자료 부족 · 수치 범위를 확인할 수 없습니다.";
                        return WithoutConsumption(item);
                    }
                    period.Consumption += bucket.Consumption; period.CoveredHours += bucket.Hours;
                    period.IntervalCount += bucket.Intervals;
                    DateTime day = TimeZoneInfo.ConvertTime(bucket.Start, zone).Date;
                    DateTime lastDay = TimeZoneInfo.ConvertTime(bucket.End.AddTicks(-1), zone).Date;
                    if (day == lastDay) {
                        double total; period.DailyConsumption.TryGetValue(day, out total);
                        period.DailyConsumption[day] = total + bucket.Consumption;
                    } else ++period.UnassignedDayBuckets;
                }
            }
            FinishPeriod(item.Current); FinishPeriod(item.Previous);
            double currentHours = (report.CurrentEnd - report.CurrentStart).TotalHours;
            double previousHours = (report.CurrentStart - report.PreviousStart).TotalHours;
            if (!item.Current.HasConsumption) {
                item.Reason = item.Current.PointCount == 0
                    ? "자료 부족 · 완료된 최근 7일의 관측 기록이 없습니다."
                    : "자료 부족 · 잔량 기록은 있지만 계산 가능한 관측 구간이 없습니다.";
            } else if (item.Current.IntervalCount < 3 || item.Previous.IntervalCount < 3
                || item.Current.CoveredHours + 0.000001 < currentHours * 0.25
                || item.Previous.CoveredHours + 0.000001 < previousHours * 0.25) {
                item.Reason = "비교 자료 부족 · 두 주 모두 25% 이상 관측하고 유효 구간이 3개 이상이면 비교합니다.";
            } else if (Math.Min(item.Current.CoveredHours, item.Previous.CoveredHours)
                / Math.Max(item.Current.CoveredHours, item.Previous.CoveredHours) < 0.8) {
                item.Reason = "관측 시간의 차이가 커서 주간 증감률을 비교하지 않습니다.";
            } else {
                item.Comparable = true;
                if (item.Previous.Consumption > 0) {
                    double difference = (item.Current.Consumption / item.Previous.Consumption - 1) * 100;
                    if (Finite(difference)) item.DifferencePercent = difference;
                    else item.Comparable = false;
                }
                item.Reason = item.Comparable ? "전체 사용량이 아닌 기록된 구간 기준입니다."
                    : "자료 부족 · 수치 범위를 확인할 수 없습니다.";
            }
            return item;
        }

        private static void FinishPeriod(WeeklyUsagePeriod period)
        {
            period.ObservedDays = period.ObservedDates.Count;
            period.LowSampleDays = period.LowSampleDates.Count; period.LowDays = period.LowDates.Count;
            period.ConsumptionDays = period.DailyConsumption.Count;
            // Zero observations are not evidence of a "most used" day.
            var positive = period.DailyConsumption.Where(pair => pair.Value > 0).ToList();
            if (positive.Count > 0) {
                double maximum = positive.Max(pair => pair.Value);
                // Subtracting balances and summing many intervals can differ by
                // a few floating-point bits for otherwise equal daily totals.
                // Select the earliest numerical tie without rounding the stored
                // totals or hiding a meaningful (even small) observed difference.
                double tolerance = maximum * 0.000000000001;
                var peak = positive.Where(pair => maximum - pair.Value <= tolerance)
                    .OrderBy(pair => pair.Key).First();
                period.PeakDay = peak.Key; period.PeakConsumption = peak.Value;
            }
        }

        private static WeeklyUsageItem WithoutConsumption(WeeklyUsageItem item)
        {
            foreach (var period in new[] { item.Current, item.Previous }) {
                period.Consumption = period.CoveredHours = 0; period.IntervalCount = 0;
                period.DailyConsumption.Clear(); period.UnassignedDayBuckets = 0; FinishPeriod(period);
            }
            return item;
        }

        private static WeeklyUsagePeriod PointPeriod(DateTimeOffset at, WeeklyUsageReport report, WeeklyUsageItem item)
        {
            return at >= report.CurrentStart && at < report.CurrentEnd ? item.Current
                : at >= report.PreviousStart && at < report.CurrentStart ? item.Previous : null;
        }

        private static WeeklyUsagePeriod IntervalPeriod(DateTimeOffset start, DateTimeOffset end, WeeklyUsageReport report, WeeklyUsageItem item)
        {
            return start >= report.CurrentStart && end <= report.CurrentEnd ? item.Current
                : start >= report.PreviousStart && end <= report.CurrentStart ? item.Previous : null;
        }

        private static bool[] OverlappingBuckets(List<UsageObservedBucket> buckets)
        {
            var result = new bool[buckets.Count];
            for (int index = 0; index < buckets.Count;) {
                int endIndex = index + 1;
                DateTimeOffset end = buckets[index].End;
                while (endIndex < buckets.Count && buckets[endIndex].Start < end) {
                    if (buckets[endIndex].End > end) end = buckets[endIndex].End;
                    ++endIndex;
                }
                if (endIndex - index > 1) for (int member = index; member < endIndex; ++member) result[member] = true;
                index = endIndex;
            }
            return result;
        }

        private static bool ValidBucket(UsageObservedBucket bucket, DateTimeOffset since, DateTimeOffset now)
        {
            return bucket.Start >= since && bucket.End > bucket.Start && bucket.End <= now
                && bucket.Intervals > 0 && bucket.Intervals <= 10000000
                && Finite(bucket.Consumption) && bucket.Consumption >= 0
                && Finite(bucket.Hours) && bucket.Hours > 0 && bucket.Hours <= (bucket.End - bucket.Start).TotalHours + 0.000001
                && Finite(bucket.MaximumGapMinutes) && bucket.MaximumGapMinutes > 0 && bucket.MaximumGapMinutes <= 30
                && bucket.Hours * 60 <= bucket.Intervals * bucket.MaximumGapMinutes + 0.000001;
        }

        private static bool ValidPoint(UsageHistoryPoint point)
        {
            return point.Source != "manual" && UsageSourceIdentity.IsValid(point.Source)
                && UsageHistoryChart.MetricValue(point).HasValue
                && (!point.ResetsAt.HasValue || point.ResetsAt.Value > point.ObservedAt)
                && (!point.Limit.HasValue || Finite(point.Limit.Value) && point.Limit.Value >= 0);
        }

        private static double? RemainingPercent(UsageHistoryPoint point)
        {
            if (point.RemainingPercent.HasValue) return point.RemainingPercent;
            if (point.Remaining.HasValue && point.Limit.HasValue && point.Limit > 0
                && point.Remaining <= point.Limit) {
                double value = point.Remaining.Value / point.Limit.Value * 100;
                if (Finite(value)) return value;
            }
            return null;
        }

        private static string NumericUnit(UsageHistoryPoint point)
        {
            string unit = UsageHistoryChart.MetricUnit(point);
            return unit == "%" ? "%p" : unit;
        }

        private static bool ValidUnit(string value) { return value == "%p" || value == "USD" || value == "크레딧"; }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }

        private static DateTimeOffset DayStart(DateTime date, TimeZoneInfo zone)
        {
            date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            // A local date can start inside a DST gap. Use its first real instant;
            // repeated midnight uses the earlier occurrence, keeping days adjacent.
            while (zone.IsInvalidTime(date)) date = date.AddMinutes(1);
            TimeSpan offset = zone.IsAmbiguousTime(date) ? zone.GetAmbiguousTimeOffsets(date).Max() : zone.GetUtcOffset(date);
            return new DateTimeOffset(date, offset);
        }
    }
}
