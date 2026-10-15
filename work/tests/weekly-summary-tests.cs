using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UsageWidget;

internal static class WeeklySummaryTests
{
    private static int count;
    private static readonly TimeZoneInfo Korea = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(9));
    private static readonly DateTimeOffset End = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.FromHours(9));
    private static void Check(bool condition, string message) { ++count; if (!condition) throw new Exception(message); }
    private static bool Near(double a, double b) { return Math.Abs(a - b) < 0.000001; }
    private static UsageHistoryPoint Point(DateTimeOffset at, double? value = 50, string source = "api")
    { return new UsageHistoryPoint { ServiceId = "codex", Label = "weekly", Source = source, ObservedAt = at,
        RemainingPercent = value, ResetsAt = at.AddDays(7), Limit = 100 }; }
    private static UsageObservedBucket Bucket(DateTimeOffset start, double hours, double consumption)
    { return new UsageObservedBucket { Start = start, End = start.AddHours(hours), Hours = hours, Consumption = consumption,
        Intervals = (int)Math.Ceiling(hours * 2), MaximumGapMinutes = 30 }; }
    private static WeeklyUsageSeries Samples(double current = 2, double previous = 1, int hours = 6)
    {
        var points = new List<UsageHistoryPoint>(); var buckets = new List<UsageObservedBucket>();
        for (int days = -14; days < 0; ++days) {
            DateTimeOffset start = End.AddDays(days).AddHours(9);
            for (int step = 0; step <= hours * 2; ++step) {
                points.Add(Point(start.AddMinutes(step * 30), 90 - step));
                if (step > 0) buckets.Add(Bucket(start.AddMinutes((step - 1) * 30), 0.5, days < -7 ? previous : current));
            }
        }
        return new WeeklyUsageSeries { ServiceId = "codex", Label = "weekly", Source = "api", Points = points,
            Basis = new UsageAnalysis { ServiceId = "codex", Label = "weekly", Source = "api", CollectionHours = 30 * 24 },
            Observed = new UsageObservedHistory { Source = "api", Unit = "%p", Since = End.AddDays(-14), Buckets = buckets } };
    }
    private static WeeklyUsageReport Build(WeeklyUsageSeries sample, int threshold = 20)
    { return WeeklyUsageSummary.Build(new[] { sample }, Now, Korea, threshold); }
    private static WeeklyUsageItem Read(WeeklyUsageSeries sample) { return Build(sample).Items.Single(); }

    public static int Main()
    {
        try {
            Baseline(); FloatingPointPeakTies(); Boundaries(); InvalidData(); Epochs(); UnitsAndLowDays(); JournalIntegration();
            Console.WriteLine("Weekly summary tests passed: " + count + " assertions. Offline journal/model fixtures only."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Baseline()
    {
        var series = Samples(); var report = Build(series); var item = report.Items.Single();
        Check(report.CurrentStart == End.AddDays(-7) && report.CurrentEnd == End && report.PreviousStart == End.AddDays(-14), "The report uses completed local days, excluding today");
        Check(report.LocalStart == new DateTime(2026,10,7) && report.LocalEndExclusive == new DateTime(2026,10,14), "Display dates are local calendar dates");
        Check(item.Comparable && Near(item.DifferencePercent.Value,100), "Equal observed windows can show an observed 100 percent increase");
        Check(Near(item.Current.Consumption,168) && Near(item.Previous.Consumption,84), "Exact interval sums remain separate by week");
        Check(Near(item.Current.CoveredHours,42) && Near(item.Previous.CoveredHours,42), "Coverage never extrapolates to full days");
        Check(item.Current.ObservedDays == 7 && item.Current.ConsumptionDays == 7 && item.Current.PointCount == 91, "Actual point days, interval days and point counts are distinct");
        Check(item.Current.PeakDay == End.AddDays(-7).Date && Near(item.Current.PeakConsumption.Value,24), "A tied peak deterministically uses the first observed day");
        Check(item.Unit == "%p", "Percentage remaining consumption uses percentage points");
        Check(item.Current.LowDaysAvailable && item.Current.LowSampleDays == 7 && item.Current.LowDays == 0, "Zero low days is valid when percentage observations exist");
        Check(((List<UsageHistoryPoint>)series.Points).Count == 182 && series.Observed.Buckets.Count == 168, "The model does not mutate input records");
        Check(WeeklyUsageSummary.Build(new[] {series,series},Now,Korea).Items.Count == 1, "Duplicate row selections do not double count");
        Check(WeeklyUsageSummary.Build(null,Now,Korea).Items.Count == 0, "Empty report makes no zero-usage service claim");
        item = Read(Samples(0,0));
        Check(item.Comparable && !item.DifferencePercent.HasValue && item.Current.HasConsumption && item.Current.Consumption == 0, "Observed flat balances are valid zero, not missing data");
        Check(!item.Current.PeakDay.HasValue, "Zero observed consumption does not invent a busiest day");
        item = Read(Samples(2,0)); Check(item.Comparable && !item.DifferencePercent.HasValue, "A zero previous total never divides by zero");
        item = Read(Samples(0,1)); Check(item.Comparable && Near(item.DifferencePercent.Value,-100), "Observed zero against a nonzero prior week supports a decrease");
        item = Read(Samples(2,1,5)); Check(!item.Comparable && item.Current.HasConsumption && item.Reason.Contains("25%"), "Less than 25 percent coverage retains totals without a trend");
        series = Samples();
        for (int day = -14; day < -7; ++day) series.Observed.Buckets.Add(Bucket(End.AddDays(day).AddHours(16),2,1));
        item = Read(series); Check(!item.Comparable && item.Reason.Contains("차이가"), "Unbalanced observation time prevents trend claims");
        series = Samples(); series.Observed = null; item = Read(series);
        Check(item.Current.PointCount > 0 && item.Current.LowDaysAvailable && !item.Current.HasConsumption && item.Reason.Contains("자료 부족"), "Legacy chart records provide observations but cannot backfill consumption");
        series.Points = new UsageHistoryPoint[0]; item = Read(series);
        Check(!item.Current.HasConsumption && item.Current.PointCount == 0 && item.Reason.Contains("자료 부족"), "Missing observations are unavailable rather than zero usage");
    }

    private static void Boundaries()
    {
        var series = Samples(); var points = (List<UsageHistoryPoint>)series.Points;
        points.Add(Point(End,0)); points.Add(Point(Now.AddMinutes(1),0));
        series.Observed.Buckets.Add(Bucket(End,0.5,100));
        var item = Read(series);
        Check(Near(item.Current.Consumption,168) && item.Current.LowDays == 0 && item.Current.PointCount == 91, "Current-day and future observations cannot affect completed-week totals");
        series.Observed.Buckets.Add(Bucket(End.AddDays(-7).AddMinutes(-5),1.0/6,100));
        item = Read(series); Check(Near(item.Current.Consumption,168) && Near(item.Previous.Consumption,84), "Week-crossing buckets are omitted instead of prorated");
        series = Samples(); series.Observed.Buckets.Add(Bucket(End.AddDays(-7),0.5,3));
        item = Read(series); Check(Near(item.Current.Consumption,171) && Near(item.Previous.Consumption,84), "A bucket starting exactly at the week boundary belongs only to the new week");
        series = Samples(); series.Observed.Buckets.Add(Bucket(End.AddDays(-3).AddMinutes(-5),1.0/6,10));
        item = Read(series);
        Check(Near(item.Current.Consumption,178) && item.Current.UnassignedDayBuckets == 1 && Near(item.Current.PeakConsumption.Value,24), "A midnight-crossing bucket contributes only to the weekly total, not an invented daily allocation");
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var report = WeeklyUsageSummary.Build(null,new DateTimeOffset(2026,3,9,12,0,0,TimeSpan.FromHours(-4)),eastern);
        Check(Near((report.CurrentEnd-report.CurrentStart).TotalHours,167), "Spring DST week has 167 elapsed hours");
        report = WeeklyUsageSummary.Build(null,new DateTimeOffset(2026,11,2,12,0,0,TimeSpan.FromHours(-5)),eastern);
        Check(Near((report.CurrentEnd-report.CurrentStart).TotalHours,169), "Autumn DST week has 169 elapsed hours");
        report = WeeklyUsageSummary.Build(null,new DateTimeOffset(2027,1,3,12,0,0,TimeSpan.FromHours(9)),Korea);
        Check(report.LocalStart == new DateTime(2026,12,27) && report.PreviousStart.Date == new DateTime(2026,12,20), "Year boundaries keep exact seven-day calendar periods");
    }

    private static void FloatingPointPeakTies()
    {
        var series = Samples(0.06,0.03,24);
        foreach (var point in series.Points) point.ObservedAt = point.ObservedAt.AddHours(-9);
        foreach (var bucket in series.Observed.Buckets) { bucket.Start = bucket.Start.AddHours(-9); bucket.End = bucket.End.AddHours(-9); }
        // Real balance subtraction and bucket summation can introduce a few
        // last-bit differences even when every displayed daily total is 2.88.
        var later = series.Observed.Buckets.First(bucket => bucket.Start >= End.AddDays(-4));
        later.Consumption += 0.00000000000001;
        var item = Read(series);
        Check(item.Current.PeakDay == End.AddDays(-7).Date,
            "Round-off-only peak ties use the earliest observed day instead of a later floating-point winner");
        double originalTotal = series.Observed.Buckets.Where(bucket => bucket.Start >= End.AddDays(-7)
            && bucket.End <= End).Sum(bucket => bucket.Consumption);
        Check(item.Current.Consumption == originalTotal,
            "Peak tie handling never rounds or changes exact weekly consumption");
        later.Consumption += 0.000001;
        item = Read(series);
        Check(item.Current.PeakDay == End.AddDays(-4).Date,
            "A genuine small consumption difference larger than numerical tolerance retains its actual peak day");
    }

    private static void InvalidData()
    {
        var series = Samples(); var baseBucket = series.Observed.Buckets[84];
        series.Observed.Buckets.Add(baseBucket);
        var item = Read(series); Check(Near(item.Current.Consumption,166), "Duplicated aggregate instances are excluded rather than added twice");
        series = Samples(); baseBucket = series.Observed.Buckets[84];
        series.Observed.Buckets.Add(Bucket(baseBucket.Start.AddMinutes(5),1.0/6,100));
        item = Read(series); Check(Near(item.Current.Consumption,166), "Both members of overlapping aggregate ranges are excluded");
        foreach (double value in new[] {Double.NaN,Double.PositiveInfinity,-1}) {
            series = Samples(); series.Observed.Buckets[84].Consumption = value; item = Read(series);
            Check(Near(item.Current.Consumption,166), "Invalid aggregate consumption is excluded: " + value);
        }
        series = Samples(); series.Observed.Buckets[84].MaximumGapMinutes = 31;
        Check(Near(Read(series).Current.Consumption,166), "An unexplained observation gap over 30 minutes cannot claim coverage");
        series = Samples(); series.Observed.Buckets[84].Hours = 1;
        Check(Near(Read(series).Current.Consumption,166), "Coverage cannot exceed aggregate elapsed time");
        series = Samples(); series.Observed.Buckets[84].Intervals = 0;
        Check(Near(Read(series).Current.Consumption,166), "A bucket with no accepted intervals is not measured usage");
        series = Samples(); var points = (List<UsageHistoryPoint>)series.Points;
        var low = Point(End.AddDays(-2).AddHours(10),0); points.Add(low);
        item = Read(series); Check(item.Current.LowDays == 0 && item.Current.PointCount == 90, "Duplicate point timestamps never pick an arbitrary low-balance reading");
        series = Samples(); points = (List<UsageHistoryPoint>)series.Points;
        points.Last().Limit = Double.NaN;
        item = Read(series); Check(!item.Current.HasConsumption, "Invalid newest capacity cannot throw or infer a valid epoch");
        series = Samples(); series.Basis.CollectionHours = Double.PositiveInfinity;
        Check(!Read(series).Current.HasConsumption, "Invalid epoch duration is rejected");
        series = Samples(); series.Observed.Buckets[84].Consumption = Double.MaxValue;
        series.Observed.Buckets[85].Consumption = Double.MaxValue;
        item = Read(series); Check(!item.Current.HasConsumption && !item.Previous.HasConsumption && item.Reason.Contains("수치"), "Overflow invalidates totals instead of showing infinity or partial sums");
    }

    private static void Epochs()
    {
        var series = Samples(); series.Basis = null;
        Check(!Read(series).Current.HasConsumption && Read(series).Current.PointCount == 0, "Retained old-account chart records cannot bypass a missing active epoch");
        series = Samples(); series.Basis.Source = "cli";
        Check(!Read(series).Current.HasConsumption, "Analysis and selected source must agree");
        series = Samples(); series.Source = "manual";
        Check(!Read(series).Current.HasConsumption && Read(series).Reason.Contains("수동"), "Manual records do not imply consumption");
        series = Samples(); series.Source = "unknown";
        Check(!Read(series).Current.HasConsumption, "Unknown sources are rejected");
        series = Samples(); series.Observed.Since = Now.AddMinutes(1);
        Check(!Read(series).Current.HasConsumption, "Future aggregation epochs are rejected");
        series = Samples(); series.Basis.CollectionHours = 48;
        Check(!Read(series).Current.HasConsumption, "An old observed epoch cannot bypass a newer connection epoch");
        series = Samples(); var points = (List<UsageHistoryPoint>)series.Points;
        points[points.Count-3].Source = "cli";
        var item = Read(series);
        Check(item.BasisChanged && item.Current.PointCount == 2 && !item.Previous.HasConsumption && Near(item.Current.Consumption,2), "A to B to A source transition uses only the latest contiguous A basis");
        series = Samples(); points = (List<UsageHistoryPoint>)series.Points;
        points[points.Count-2].Limit = points[points.Count-1].Limit = 200;
        item = Read(series); Check(item.BasisChanged && item.Current.PointCount == 2 && Near(item.Current.Consumption,2), "Capacity changes exclude earlier points and aggregate ranges");
        series = Samples(); points = (List<UsageHistoryPoint>)series.Points;
        points.Last().Source = "cli";
        Check(!Read(series).Current.HasConsumption, "Newest source disagreement suppresses a stale caller snapshot");
    }

    private static void UnitsAndLowDays()
    {
        var series = Samples(); var points = (List<UsageHistoryPoint>)series.Points;
        points.Where(point => point.ObservedAt.Date == End.AddDays(-2).Date).ToList().ForEach(point => point.RemainingPercent = 20);
        points.Where(point => point.ObservedAt.Date == End.AddDays(-3).Date).ToList().ForEach(point => point.RemainingPercent = 5);
        var item = Read(series);
        Check(item.Current.LowDays == 2 && item.Current.LowSampleDays == 7, "Repeated low points count two observed days, not alerts or depletion events");
        Check(Build(series,10).Items.Single().Current.LowDays == 1, "Caller-specified threshold has a precise percent meaning");
        Check(Build(series,0).LowThresholdPercent == 1 && Build(series,200).LowThresholdPercent == 100, "Invalid threshold is clamped to a meaningful percent range");
        foreach (var point in points) { point.Remaining = point.RemainingPercent; point.RemainingPercent = null; point.ServiceId = "higgsfield-api"; }
        series.ServiceId = "higgsfield-api"; series.Observed.Unit = "USD";
        item = Read(series);
        Check(item.Unit == "USD" && item.Current.LowDays == 2, "Absolute remaining against known capacity can support percent low-day counts");
        points.ForEach(point => point.Limit = null); item = Read(series);
        Check(item.Unit == "USD" && item.Current.HasConsumption && !item.Current.LowDaysAvailable, "Unknown USD capacity does not imply zero low days");
        var credits = Samples(); credits.ServiceId = "higgsfield"; credits.Observed.Unit = "크레딧";
        foreach (var point in credits.Points) { point.ServiceId = "higgsfield"; point.Remaining = point.RemainingPercent; point.RemainingPercent = null; point.Limit = null; }
        var report = WeeklyUsageSummary.Build(new[] { series,credits,Samples() },Now,Korea);
        Check(report.Items.Count == 3 && report.Items.Select(row => row.Unit).Distinct().Count() == 3, "Currency, credits and percentage-point rows stay separate without a combined total");
        series = Samples(); series.Observed.Unit = "USD";
        item = Read(series); Check(item.Current.PointCount == 0 && !item.Current.HasConsumption, "Percent points cannot be counted as currency observations or permit stale-unit aggregates");
    }

    private static void JournalIntegration()
    {
        var journal = new UsageJournal(null,true);
        DateTimeOffset start = End.AddDays(-3).AddHours(9);
        Action<DateTimeOffset,double,DateTimeOffset> put = (at,value,reset) => journal.Observe("eleven",
            new UsageSnapshot { Plan = "Starter", Rows = new List<UsageRow> { new UsageRow {
                Label = "monthly", Remaining = value, Limit = 1000, ResetsAt = reset } } },at,"api",at);
        DateTimeOffset cycle = start.AddMinutes(12);
        put(start,100,cycle); put(start.AddMinutes(5),95,cycle);
        put(start.AddMinutes(10),99,cycle); // Top-up is not a consumed negative interval.
        put(start.AddMinutes(15),1000,start.AddDays(7)); // Reset crossing is excluded.
        put(start.AddMinutes(20),990,start.AddDays(7));
        put(start.AddMinutes(60),900,start.AddDays(7)); // Unobserved long gap is excluded.
        put(start.AddMinutes(65),898,start.AddDays(7));
        var input = new WeeklyUsageSeries { ServiceId = "eleven", Label = "monthly", Source = "api",
            Basis = journal.GetAnalysis("eleven","monthly","api",Now),
            Observed = journal.GetObservedHistory("eleven","monthly","api",Now), Points = journal.GetHistory() };
        var item = Read(input);
        Check(Near(item.Current.Consumption,17) && Near(item.Current.CoveredHours,0.25), "Real journal excludes top-ups, quota resets and unobserved gaps while retaining exact accepted decreases");
        Check(!item.Comparable, "A few real observations never become a fabricated weekly trend");
        journal.SetAnalysisOwner(new String('a',64));
        input.Basis = journal.GetAnalysis("eleven","monthly","api",Now);
        input.Observed = journal.GetObservedHistory("eleven","monthly","api",Now);
        item = Read(input);
        Check(!item.Current.HasConsumption && item.Current.PointCount == 0, "Real account change excludes all previous-account history from the summary");

        journal = new UsageJournal(null,true);
        double remaining = 10000;
        for (int day = -14; day < 0; ++day) {
            for (int step = 0; step <= 12; ++step) {
                DateTimeOffset at = End.AddDays(day).AddHours(9).AddMinutes(step * 30);
                if (step > 0) remaining -= day < -7 ? 1 : 2;
                journal.Observe("eleven", new UsageSnapshot { Plan = "Starter", Rows = new List<UsageRow> {
                    new UsageRow { Label = "monthly", Remaining = remaining, Limit = 10000, ResetsAt = End.AddDays(7) }
                } }, at, "api", at);
            }
        }
        input = new WeeklyUsageSeries { ServiceId = "eleven", Label = "monthly", Source = "api",
            Basis = journal.GetAnalysis("eleven","monthly","api",Now),
            Observed = journal.GetObservedHistory("eleven","monthly","api",Now), Points = journal.GetHistory() };
        item = Read(input);
        Check(input.Points.Count() < 182, "A realistic fourteen-day journal actually compacts chart samples");
        Check(item.Comparable && Near(item.Current.Consumption,168) && Near(item.Previous.Consumption,84)
            && Near(item.DifferencePercent.Value,100), "Real fourteen-day journal aggregates support exact weekly comparisons after chart compaction");
        Check(item.Current.ObservedDays == 7 && item.Current.CoveredHours == 42, "Compacted chart point days remain separate from exact observed interval hours");
    }
}
