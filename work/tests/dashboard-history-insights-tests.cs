using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using UsageWidget;

internal static class DashboardHistoryInsightsTests
{
    private static int count;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026,10,13,12,0,0,TimeSpan.FromHours(9));
    private static void Check(bool value, string message) { ++count; if (!value) throw new Exception(message); }
    private static bool Near(double left, double right) { return Math.Abs(left-right) < 0.00001; }
    private static UsageHistoryPoint Point(DateTimeOffset at, double? value)
    { return new UsageHistoryPoint { ServiceId="codex", Label="weekly", Source="api", ObservedAt=at, RemainingPercent=value, ResetsAt=Now.AddDays(3) }; }
    private static List<UsageHistoryPoint> Samples(double currentHours, double previousHours, double currentDrop, double previousDrop)
    {
        var result = new List<UsageHistoryPoint>();
        for (int i=0; i<=previousHours*2; ++i) result.Add(Point(Now.AddHours(-24-previousHours+i*0.5), 90-i*previousDrop));
        for (int i=0; i<=currentHours*2; ++i) result.Add(Point(Now.AddHours(-currentHours+i*0.5), 60-i*currentDrop));
        return result;
    }
    private static DashboardHistoryComparison Compare(List<UsageHistoryPoint> points)
    { return DashboardHistoryInsights.Compare(points,"codex","weekly",Now,1,30); }
    [STAThread]
    public static int Main()
    {
        try {
            var baseline = Samples(6,6,1,0.5); var result=Compare(baseline);
            Check(result.Comparable,"Equal 25% observation spans are comparable");
            Check(result.Unit=="%p", "Percent remaining differences use percentage points");
            Check(Near(result.Current.Consumption,12) && Near(result.Previous.Consumption,6),"Only observed decreasing intervals are summed");
            Check(Near(result.Current.CoveredHours,6) && Near(result.Previous.CoveredHours,6),"Unobserved hours are not extrapolated");
            Check(result.Current.PointCount==14 && result.Current.Latest==Now,"Quality includes the actual start-boundary reading and last observation");
            Check(Near(result.DifferencePercent.Value,100),"Comparable observed totals calculate percentage increase");
            result=Compare(Samples(5.5,6,1,0.5));
            Check(!result.Comparable && result.Current.HasConsumption && result.Reason.Contains("25%"),"Insufficient current coverage retains observed total but does not compare");
            result=Compare(Samples(6,12,1,0.5));
            Check(!result.Comparable && result.Reason.Contains("차이가"),"Unequal observation durations do not imply false trend");
            result=Compare(Samples(6,6,0,0));
            Check(result.Comparable && result.Current.HasConsumption && Near(result.Current.Consumption,0),"Actual flat observations are legitimate zero usage");
            Check(!result.DifferencePercent.HasValue,"Zero versus zero does not divide by zero");
            result=Compare(Samples(6,6,1,0));
            Check(result.Comparable && !result.DifferencePercent.HasValue && result.Current.Consumption>0,"Increase from zero has no infinite percentage");
            result=Compare(Samples(6,6,0,1));
            Check(result.Comparable && Near(result.DifferencePercent.Value,-100),"Observed zero against positive prior total is a valid decrease");
            result=Compare(new List<UsageHistoryPoint>());
            Check(!result.Current.HasConsumption && !result.Comparable,"Empty period is unavailable, not zero usage");
            result=Compare(new List<UsageHistoryPoint>{Point(Now,50)});
            Check(result.Current.PointCount==1 && !result.Current.HasConsumption,"One reading has no consumption interval");
            result=DashboardHistoryInsights.Compare(baseline,"",null,Now,1,30);
            Check(result.Unit==null && result.Reason.Contains("하나"),"All-service selection does not combine unlike metrics");
            result=Compare(new List<UsageHistoryPoint>{Point(Now.AddMinutes(-10),70),Point(Now.AddMinutes(-5),null),Point(Now,60)});
            Check(!result.Current.HasConsumption,"Missing reading splits both adjacent intervals");
            result=Compare(new List<UsageHistoryPoint>{Point(Now.AddMinutes(-31),70),Point(Now,60)});
            Check(!result.Current.HasConsumption,"An unexplained gap longer than chart threshold is excluded");
            var compacted = new List<UsageHistoryPoint>{Point(Now.AddHours(-1),70),Point(Now,60)}; compacted.ForEach(point=>point.ResolutionMinutes=60);
            result=Compare(compacted);
            Check(!result.Current.HasConsumption && Near(result.Current.CoveredHours,0),"Compaction metadata does not expand observed coverage beyond the strict gap limit");
            compacted[0].ObservedAt=Now.AddMinutes(-91); result=Compare(compacted);
            Check(!result.Current.HasConsumption,"Compacted points still have a bounded gap tolerance");
            compacted[0].ObservedAt=Now.AddMinutes(-80); compacted.ForEach(point=>point.ResolutionMinutes=60); result=Compare(compacted);
            Check(!result.Current.HasConsumption && Near(result.Current.CoveredHours,0),"An 80-minute compacted gap cannot masquerade as 80 minutes of observations");
            compacted[0].ObservedAt=Now.AddHours(-23); compacted.ForEach(point=>point.ResolutionMinutes=1440); result=Compare(compacted);
            Check(!result.Current.HasConsumption && Near(result.Current.CoveredHours,0),"Daily compaction cannot imply a whole day of coverage");
            result=Compare(new List<UsageHistoryPoint>{Point(Now.AddMinutes(-10),60),Point(Now.AddMinutes(-5),61),Point(Now,59)});
            Check(Near(result.Current.Consumption,2) && result.Current.IntervalCount==1,"Even a small top-up is not absorbed as usage");
            var reset=new List<UsageHistoryPoint>{Point(Now.AddMinutes(-10),90),Point(Now.AddMinutes(-5),85),Point(Now,80)};
            reset[1].ResetsAt=reset[2].ResetsAt=Now.AddDays(7); result=Compare(reset);
            Check(Near(result.Current.Consumption,5) && result.Current.IntervalCount==1,"Different quota reset windows never connect");
            reset.ForEach(point=>point.ResetsAt=Now.AddMinutes(-7)); result=Compare(reset);
            Check(!result.Current.HasConsumption && result.Current.PointCount==1,"Expired readings do not contribute current intervals");
            var manual=Samples(6,6,1,0.5); manual.ForEach(point=>point.Source="manual"); result=Compare(manual);
            Check(result.Unit==null && result.Reason.Contains("수동"),"Manual records are not consumption inference");
            var source=Samples(6,6,1,0.5); source[source.Count-1].Source="cli"; result=Compare(source);
            Check(result.BasisChanged && result.Current.PointCount==1 && !result.Previous.HasConsumption,"A new source starts a new comparison basis");
            source=Samples(6,6,1,0.5); source[source.Count-3].Source="cli"; result=Compare(source);
            Check(result.BasisChanged && result.Current.PointCount==2 && Near(result.Current.Consumption,1),"Returning A to B to A does not resurrect the earlier A history");
            source=Samples(6,6,1,0.5); source[source.Count-1].Source="untrusted"; result=Compare(source);
            Check(result.Unit==null && result.Reason.Contains("출처"),"Unknown source is unsupported");
            var capacity=Samples(6,6,1,0.5); capacity[capacity.Count-1].Limit=100; result=Compare(capacity);
            Check(result.BasisChanged && !result.Current.HasConsumption,"Changed capacity resets comparison basis");
            var mixed=Samples(6,6,1,0.5); mixed[0].RemainingPercent=null; mixed[0].Remaining=10; result=Compare(mixed);
            Check(result.Unit==null && result.Reason.Contains("단위"),"Changed units refuse aggregation instead of mixing amount and percent");
            var currency=new List<UsageHistoryPoint>{Point(Now.AddMinutes(-10),null),Point(Now,null)};
            currency[0].Remaining=10; currency[1].Remaining=9; currency.ForEach(point=>point.ServiceId="openai");
            result=DashboardHistoryInsights.Compare(currency,"openai","weekly",Now,1,30);
            Check(result.Unit=="USD" && Near(result.Current.Consumption,1),"Dollar balances retain USD units");
            currency.ForEach(point=>point.ServiceId="eleven"); result=DashboardHistoryInsights.Compare(currency,"eleven","weekly",Now,1,30);
            Check(result.Unit=="크레딧" && Near(result.Current.Consumption,1),"Absolute credits retain credit units");
            var duplicates=new List<UsageHistoryPoint>{Point(Now.AddMinutes(-10),90),Point(Now.AddMinutes(-5),85),Point(Now.AddMinutes(-5),84),Point(Now,80)};
            result=Compare(duplicates);
            Check(!result.Current.HasConsumption && result.Current.PointCount==2,"Duplicate timestamps cannot pick an arbitrary consumption reading");
            result=Compare(new List<UsageHistoryPoint>{Point(Now.AddHours(-24).AddMinutes(-5),90),Point(Now.AddHours(-24).AddMinutes(5),80)});
            Check(!result.Current.HasConsumption && !result.Previous.HasConsumption,"An interval crossing period boundary is not interpolated or assigned wholly to either side");
            var boundary=new List<UsageHistoryPoint>{Point(Now.AddHours(-24).AddMinutes(-5),90),Point(Now.AddHours(-24),85),Point(Now.AddHours(-24).AddMinutes(5),80)};
            result=Compare(boundary);
            Check(Near(result.Previous.Consumption,5) && Near(result.Current.Consumption,5),"Exact boundary measurement divides observed intervals without double counting");
            var future=Samples(6,6,1,0.5); future.Add(Point(Now.AddMinutes(1),0)); result=Compare(future);
            Check(result.Current.Latest==Now && Near(result.Current.Consumption,12),"Future readings cannot affect period summary");
            result=DashboardHistoryInsights.Compare(baseline,"codex","weekly",Now,30,30);
            Check(!result.Comparable && result.Reason.Contains("이전 30일"),"30-day retention explicitly cannot supply the preceding 30-day comparison");
            var invalid=new List<UsageHistoryPoint>{Point(Now.AddMinutes(-10),Double.NaN),Point(Now.AddMinutes(-5),Double.PositiveInfinity),Point(Now,101)};
            result=Compare(invalid);
            Check(result.Unit==null && !result.Current.HasConsumption,"Invalid and out-of-range measurements do not appear as zero or consumption");
            CheckKnownBasis(); CheckUi();
            Console.WriteLine("Dashboard history insights tests passed: "+count+" assertions. Isolated calculations, journal and hidden WPF fixtures."); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckKnownBasis()
    {
        var points=Samples(6,6,1,0.5);
        var result=DashboardHistoryInsights.CompareKnownBasis(points,"codex","weekly","api",null,Now,1,30);
        Check(!result.Current.HasConsumption && result.Reason.Contains("연결"),"Retained history cannot bypass a cleared account or connection analysis baseline");
        result=DashboardHistoryInsights.CompareKnownBasis(points,"codex","weekly","api",new UsageAnalysis {Source="api",CollectionHours=6},Now,1,30);
        Check(result.Current.PointCount==13 && Near(result.Current.Consumption,12) && !result.Previous.HasConsumption,"A new account epoch keeps only observations since its first confirmed reading");
        Check(result.BasisChanged && !result.Comparable,"Old-account retained records never fill missing comparison coverage");
        result=DashboardHistoryInsights.CompareKnownBasis(points,"codex","weekly","manual",new UsageAnalysis {Source="api",CollectionHours=100},Now,1,30);
        Check(!result.Current.HasConsumption && result.Reason.Contains("수동"),"New manual connection cannot borrow old automatic observations before its first reading");
        result=DashboardHistoryInsights.CompareKnownBasis(points,"codex","weekly","cli",new UsageAnalysis {Source="api",CollectionHours=100},Now,1,30);
        Check(!result.Current.HasConsumption,"Analysis baseline must match the current source");
        result=DashboardHistoryInsights.CompareKnownBasis(points,"codex","weekly","cli",new UsageAnalysis {Source="cli",CollectionHours=100},Now,1,30);
        Check(!result.Current.HasConsumption,"Retained latest history from another source cannot borrow the current source's epoch");
        result=DashboardHistoryInsights.CompareKnownBasis(points,"codex","weekly","api",new UsageAnalysis {Source="api",CollectionHours=48},Now,1,30);
        Check(result.Comparable && Near(result.Current.Consumption,12),"An established current epoch preserves an otherwise valid comparison");
        var journal=new UsageJournal(null,true); journal.SetAnalysisOwner(new String('a',64));
        // GetHistory prunes relative to the real clock; keep this integration
        // fixture safely in its past while pure arithmetic cases use Now.
        var journalNow=DateTimeOffset.Now.AddHours(-1);
        foreach(var point in points) {
            var at=journalNow+(point.ObservedAt-Now);
            journal.Observe("codex",new UsageSnapshot { Rows=new List<UsageRow> {
                new UsageRow { Label="weekly",RemainingPercent=point.RemainingPercent,ResetsAt=journalNow.AddDays(3) }
            } },at,"api",at);
        }
        int retained=journal.GetHistory().Count; journal.SetAnalysisOwner(new String('b',64));
        Check(journal.GetHistory().Count==retained,"Account migration fixture actually retains chart history");
        result=DashboardHistoryInsights.CompareKnownBasis(journal.GetHistory(),"codex","weekly","api",journal.GetAnalysis("codex","weekly","api",journalNow),journalNow,1,30);
        Check(!result.Current.HasConsumption,"A real SetAnalysisOwner reset cannot reuse retained records as new-account consumption");
        for(int index=1;index<=2;++index) {
            var at=journalNow.AddMinutes(index*5);
            journal.Observe("codex",new UsageSnapshot { Rows=new List<UsageRow> {
                new UsageRow {Label="weekly",RemainingPercent=40-index,ResetsAt=journalNow.AddDays(3)}
            } },at,"api",at);
        }
        var after=journalNow.AddMinutes(10);
        result=DashboardHistoryInsights.CompareKnownBasis(journal.GetHistory(),"codex","weekly","api",journal.GetAnalysis("codex","weekly","api",after),after,1,30);
        Check(result.Current.PointCount==2 && Near(result.Current.Consumption,1) && !result.Previous.HasConsumption,"Only fresh account observations contribute after an actual journal epoch reset");
        journal.ForgetService("codex");
        result=DashboardHistoryInsights.CompareKnownBasis(journal.GetHistory(),"codex","weekly","api",journal.GetAnalysis("codex","weekly","api",after),after,1,30);
        Check(!result.Current.HasConsumption,"An explicit reconnect clears inference while retaining old chart records");
    }

    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object value,string name) { return (T)value.GetType().GetField(name,Private).GetValue(value); }
    private static void Set(object value,string name,object data) { value.GetType().GetField(name,Private).SetValue(value,data); }
    private static object Call(object value,string name,params object[] args)
    { try { return value.GetType().GetMethod(name,Private).Invoke(value,args); } catch(TargetInvocationException error) { throw error.InnerException??error; } }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach(object child in LogicalTreeHelper.GetChildren(root)) if(child is DependencyObject)
            foreach(var descendant in Walk((DependencyObject)child)) yield return descendant;
    }
    private static void CheckUi()
    {
        string isolated=Path.Combine(Path.GetTempPath(),"nephylo-history-insights-"+Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride=isolated;
        SettingsStore.StartupRegistrySubKeyOverride=@"Software\UsageWidget\Tests\history-insights-"+Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride=Path.Combine(isolated,"claude");
        WidgetWindow owner=null;
        try {
            L.Language="ko"; owner=new WidgetWindow(true);
            Field<DispatcherTimer>(owner,"timer").Stop(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Visible=false;
            Call(owner,"ShowDemo"); new WindowInteropHelper(owner).EnsureHandle(); Call(owner,"BuildDashboardWindow");
            Call(owner,"SetDashboardHistoryService","codex"); Call(owner,"NavigateDashboard","history");
            var card=Field<object>(owner,"dashboardHistoryInsights");
            Check(card!=null,"History builds the observed-consumption panel");
            var chart=Field<UsageHistoryChart>(owner,"dashboardHistoryChart");
            var root=Field<StackPanel>(owner,"dashboardHistoryContent");
            var disclosure=Walk(root).OfType<Expander>().Single(item => System.Windows.Automation.AutomationProperties.GetAutomationId(item)=="dashboard-history-records"); disclosure.IsExpanded=true;
            Call(owner,"RefreshDashboardHistoryIfOpen");
            Check(Object.ReferenceEquals(card,Field<object>(owner,"dashboardHistoryInsights")),"Polling preserves the insights panel instance");
            Check(Object.ReferenceEquals(chart,Field<UsageHistoryChart>(owner,"dashboardHistoryChart")) && disclosure.IsExpanded,"Insights refresh preserves graph selection and existing record disclosure");
            Field<ComboBox>(owner,"dashboardHistoryMetric").SelectedIndex=0;
            Check(Field<Grid>(card,"Metrics").Visibility==Visibility.Collapsed && Field<TextBlock>(card,"Reason").Text.Contains("하나"),"All-metric selection replaces misleading totals with an explanation");
            Set(owner,"demo",false); Call(owner,"RefreshDashboardHistoryIfOpen");
            Check(Field<object>(owner,"dashboardHistoryInsights")!=null && Field<UsageHistoryChart>(owner,"dashboardHistoryChart")!=null,"Accountless mode keeps graph and insights available");
            Call(owner,"LockDashboardHistory");
            Check(Field<object>(owner,"dashboardHistoryInsights")==null && Field<UsageHistoryChart>(owner,"dashboardHistoryChart")==null,"Explicit history invalidation clears graph and insights references together");
            Set(owner,"demo",true);
            Check(!owner.IsLoaded && !owner.IsVisible && !Directory.Exists(isolated),"Hidden fixture uses no real service queries, settings or account writes");
        }
        finally {
            if(owner!=null) { Set(owner,"demo",true); Call(owner,"CloseDashboard"); Field<DispatcherTimer>(owner,"timer").Stop(); owner.Close();
                Field<CodexService>(owner,"codex").Dispose(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Dispose(); }
            SettingsStore.DataDirectoryOverride=null; SettingsStore.StartupRegistrySubKeyOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null;
        }
    }
}
