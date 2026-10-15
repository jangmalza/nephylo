using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UsageWidget;

internal static class DashboardUiTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int count;
    private static void Check(bool value,string message) { count++; if (!value) throw new Exception(message); }
    private static T Field<T>(object item,string name) { return (T)item.GetType().GetField(name,Private).GetValue(item); }
    private static void Set(object item,string name,object value) { item.GetType().GetField(name,Private).SetValue(item,value); }
    private static object Call(object item,string name,params object[] arguments)
    {
        var method = item.GetType().GetMethod(name,Private);
        if (method == null) throw new MissingMethodException(item.GetType().FullName,name);
        try { return method.Invoke(item,arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        if (root == null) yield break;
        yield return root;
        foreach (object value in LogicalTreeHelper.GetChildren(root)) {
            var child = value as DependencyObject;
            if (child != null) foreach (var nested in Walk(child)) yield return nested;
        }
    }
    private static string Text(DependencyObject root)
    {
        return String.Join("\n",Walk(root).OfType<TextBlock>().Select(value => value.Text)
            .Concat(Walk(root).OfType<ContentControl>().Where(value => value.Content is string).Select(value => (string)value.Content)));
    }
    private static IEnumerable<DependencyObject> VisualWalk(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in VisualWalk(VisualTreeHelper.GetChild(root,index))) yield return child;
    }
    private static string SelectedVisualText(ComboBox combo)
    {
        combo.ApplyTemplate(); combo.Measure(new Size(320,42)); combo.Arrange(new Rect(0,0,320,42)); combo.UpdateLayout();
        return String.Join("\n",VisualWalk(combo).OfType<TextBlock>().Select(item => item.Text));
    }
    private static FrameworkElement Page(WidgetWindow owner,string method,params object[] arguments)
    {
        var result = Call(owner,method,arguments) as FrameworkElement;
        Check(result != null,method + " returns usable WPF content");
        return result;
    }

    [STAThread]
    public static int Main()
    {
        string isolated = Path.Combine(Path.GetTempPath(),"nephylo-dashboard-ui-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\dashboard-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated,"claude");
        WidgetWindow owner = null;
        try {
            L.Language = "ko";
            owner = new WidgetWindow(true);
            Field<DispatcherTimer>(owner,"timer").Stop();
            Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Visible = false;
            Call(owner,"ShowDemo");
            Check(!owner.IsLoaded && !owner.IsVisible,"Dashboard fixtures remain unloaded: no provider initialization or real account use");
            CheckSnapshotReuse(owner);
            CheckObservationMeaning(owner);
            CheckProviderStatusConsistency(owner);
            CheckLowBalanceCounting();
            CheckOverviewFilters(owner);
            CheckServiceSelection(owner);
            CheckHistoryIsolation(owner);
            CheckHistoryLifecycle(owner);
            CheckShellLifecycle(owner);
            CheckLanguage(owner);
            Check(!Field<DispatcherTimer>(owner,"timer").IsEnabled,"Constructing dashboard pages never starts another refresh cycle");
            Check(!Directory.Exists(isolated),"Read-only dashboard pages never create account, settings or history files");
            Console.WriteLine("Dashboard UI tests passed: " + count + " assertions. Hidden, isolated demo fixture; no real accounts or API requests.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if (owner != null) {
                Set(owner,"demo",true);
                Field<DispatcherTimer>(owner,"timer").Stop();
                owner.Close();
                Field<CodexService>(owner,"codex").Dispose();
                Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null;
            SettingsStore.StartupRegistrySubKeyOverride = null;
            ClaudeUsageService.ConfigDirectoryOverride = null;
            L.Language = "ko";
        }
    }

    private static void CheckSnapshotReuse(WidgetWindow owner)
    {
        var codex = Field<ProviderView>(owner,"codexView");
        var eleven = Field<ProviderView>(owner,"elevenView");
        var snapshot = codex.Snapshot;
        var rows = snapshot.Rows.ToArray();
        var last = codex.LastSuccess;
        var journal = Field<UsageJournal>(owner,"usageJournal");
        int historyCount = journal.GetHistory().Count;
        var nextRefresh = Field<DateTimeOffset>(owner,"nextRefresh");
        var overview = Page(owner,"BuildDashboardOverview");
        string text = Text(overview);
        Check(text.Contains("Codex") && text.Contains("ElevenLabs"),"Overview presents the already-connected demo services");
        var detail = Page(owner,"BuildDashboardServiceDetail","codex");
        text = Text(detail);
        Check(text.Contains("72%") && text.Contains("86%"),"Service details preserve independent short and weekly balances");
        Page(owner,"BuildDashboardHistoryPage",false);
        Page(owner,"BuildDashboardHistoryPage",true);
        Check(Object.ReferenceEquals(codex.Snapshot,snapshot) && rows.SequenceEqual(snapshot.Rows),"Page construction never replaces provider snapshots or mutates metric objects");
        Check(codex.LastSuccess == last && Field<UsageJournal>(owner,"usageJournal").GetHistory().Count == historyCount,"Opening or switching dashboard pages neither invents observations nor duplicates history");
        Check(!Field<bool>(owner,"refreshing") && Field<DateTimeOffset>(owner,"nextRefresh") == nextRefresh && !codex.Busy && !eleven.Busy,"Dashboard rendering does not start provider refreshes or alter their schedule");
        var timerFields = typeof(WidgetWindow).GetFields(Private).Where(field => field.FieldType == typeof(DispatcherTimer));
        Check(timerFields.Where(field => field.Name.IndexOf("dashboard",StringComparison.OrdinalIgnoreCase) >= 0)
            .All(field => field.GetValue(owner) == null || !((DispatcherTimer)field.GetValue(owner)).IsEnabled),"Hidden dashboard pages do not create a running polling timer");
    }

    private static void CheckObservationMeaning(WidgetWindow owner)
    {
        var view = Field<ProviderView>(owner,"codexView");
        var snapshot = view.Snapshot;
        string error = view.Error, source = view.Source;
        var observed = view.LastSuccess;
        try {
            view.Error = "Dashboard fixture: connection interrupted";
            var page = Page(owner,"BuildDashboardServiceDetail","codex");
            string text = Text(page);
            Check(text.Contains("86%") && (text.Contains("이전") || text.Contains("마지막")),"A failed refresh keeps the last balance explicitly marked as a previous observation");
            Check(text.Contains(view.Error),"A connection failure has a visible reason rather than a plausible-looking current balance alone");
            view.Error = null; view.Source = "manual";
            text = Text(Page(owner,"BuildDashboardServiceDetail","codex"));
            Check(text.Contains("수동"),"Manual values remain labeled as manual in service details");
            view.Snapshot = new UsageSnapshot { Rows = new List<UsageRow>() }; view.LastSuccess = null; view.Source = source;
            text = Text(Page(owner,"BuildDashboardServiceDetail","codex"));
            Check(!text.Contains("86%") && !text.Contains("100%"),"No observation never inherits an old percentage or fabricates a full allowance");
        }
        finally { view.Snapshot = snapshot; view.Error = error; view.Source = source; view.LastSuccess = observed; }

        var claude = Field<ProviderView>(owner,"claudeView");
        var claudeSnapshot = claude.Snapshot;
        var receipt = claude.ReceiptState;
        var receiptTime = claude.ReceiptObservedAt;
        string claudeSource = claude.Source, receiptMessage = claude.ReceiptMessage, claudeError = claude.Error;
        try {
            claude.Source = "code"; claude.Error = null;
            claude.ReceiptState = ProviderReceiptState.QuotaUnavailable;
            claude.ReceiptObservedAt = DateTimeOffset.Now;
            claude.ReceiptMessage = "Claude Code 데이터는 도착했지만 구독 한도는 없습니다.";
            claude.Snapshot = new UsageSnapshot { Rows = new List<UsageRow>() };
            string text = Text(Page(owner,"BuildDashboardServiceDetail","claude"));
            Check(text.Contains("한도") && (text.Contains("미제공") || text.Contains("없습니다")),"Claude receipts without quotas explain missing provider data");
            Check(!text.Contains("100%") && !text.Contains("0%"),"An unavailable Claude quota is never represented as either full or empty");
        }
        finally {
            claude.Source = claudeSource; claude.Error = claudeError; claude.Snapshot = claudeSnapshot;
            claude.ReceiptState = receipt; claude.ReceiptObservedAt = receiptTime; claude.ReceiptMessage = receiptMessage;
        }
    }

    private static void CheckServiceSelection(WidgetWindow owner)
    {
        var plan = Field<ServicePlan>(owner,"servicePlan");
        string previous = plan.ActiveServiceId;
        string previousSecret = Field<string>(owner,"secret");
        Set(owner,"secret","dashboard-fixture-not-sent");
        Set(owner,"demo",false);
        try {
            plan.Select("codex");
            string text = Text(Page(owner,"BuildDashboardOverview"));
            Check(text.Contains("Codex") && text.Contains("ElevenLabs"),"Accountless overview includes all connected services even with an old single-service selection");
            plan.Select("eleven");
            text = Text(Page(owner,"BuildDashboardOverview"));
            Check(text.Contains("ElevenLabs") && text.Contains("Codex"),"Changing a legacy selected service cannot hide another connected service");
            text = Text(Page(owner,"BuildDashboardServiceDetail","codex"));
            Check(text.Contains("86%") && text.Contains("72%"),"Accountless service details retain all independent quota windows");
            foreach (bool analysisOnly in new[] { false,true }) {
                var page = Page(owner,"BuildDashboardHistoryPage",analysisOnly);
                Check(Walk(page).OfType<ComboBox>().Any() && !Text(page).Contains("Pro에서"),"History and analysis controls remain available without a Nephylo account");
            }
        }
        finally { plan.Select(previous); Set(owner,"demo",true); Set(owner,"secret",previousSecret); }
    }

    private static void CheckProviderStatusConsistency(WidgetWindow owner)
    {
        var view = Field<ProviderView>(owner,"codexView");
        var snapshot = view.Snapshot; var observed = view.LastSuccess;
        string source = view.Source, error = view.Error;
        try {
            view.Source = "api"; view.Error = null; view.LastSuccess = DateTimeOffset.Now;
            view.Snapshot = new UsageSnapshot { Plan = "PRO", Rows = new List<UsageRow> {
                new UsageRow { Label = "주간 한도", RemainingPercent = 50, ResetsAt = DateTimeOffset.Now.AddDays(2) }
            } };
            string text = Text(Page(owner,"DashboardProviderStatus",view));
            Check(text.Contains("PRO · 자동 조회") && !text.Contains("이전 값"),"A healthy provider retains its plan and automatic-reading status");
            view.LastSuccess = DateTimeOffset.Now.AddMinutes(-11);
            text = Text(Page(owner,"DashboardProviderStatus",view));
            Check(text.Contains("이전 값 · 자동 조회") && !text.Contains("PRO · 자동 조회"),"An old API snapshot is marked as a previous value in the service header as well as individual rows");
            var header = Walk(Page(owner,"DashboardProviderStatus",view)).OfType<TextBlock>().First();
            Check(header.FontWeight == FontWeights.SemiBold,"An uncertain service header uses the same visible warning treatment as failed provider reads");
            view.LastSuccess = DateTimeOffset.Now;
            view.Snapshot.Rows.Add(new UsageRow { Label = "5시간 한도", RemainingPercent = 1, ResetsAt = DateTimeOffset.Now.AddMinutes(-1) });
            text = Text(Page(owner,"DashboardProviderStatus",view));
            Check(text.Contains("일부 항목 확인 필요 · 자동 조회"),"A mixed current-weekly/expired-short response identifies partial uncertainty rather than invalidating every limit");
            L.Language = "en";
            text = Text(Page(owner,"DashboardProviderStatus",view));
            Check(text.Contains("Some limits need attention") && !text.Any(value => value >= '\uAC00' && value <= '\uD7AF'),"The partial-uncertainty header and existing reason remain fully localized in English");
            view.Snapshot.Rows.RemoveAt(1); view.LastSuccess = DateTimeOffset.Now.AddMinutes(-11);
            text = Text(Page(owner,"DashboardProviderStatus",view));
            Check(text.Contains("Previous value · Automatic") && !text.Any(value => value >= '\uAC00' && value <= '\uD7AF'),"The old-API header remains fully localized in English");
        }
        finally { view.Snapshot = snapshot; view.LastSuccess = observed; view.Source = source; view.Error = error; L.Language = "ko"; }
    }

    private static void CheckLowBalanceCounting()
    {
        var now = DateTimeOffset.Now;
        var shortQuota = new UsageRow { Label = "5시간 한도", RemainingPercent = 2, ResetsAt = now.AddMinutes(-1) };
        var weekly = new UsageRow { Label = "주간 한도", RemainingPercent = 3, ResetsAt = now.AddDays(3) };
        var view = new ProviderView { Id = "claude", Source = "code", ReceiptState = ProviderReceiptState.ResetWaiting,
            ReceiptObservedAt = now, LastSuccess = now, Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { shortQuota,weekly } } };
        Check(!WidgetWindow.IsDashboardLowRow(view,shortQuota,now,20),"An expired short quota cannot be counted as a current low balance");
        Check(WidgetWindow.IsDashboardLowRow(view,weekly,now,20),"A reset-pending short quota does not hide the same provider's valid 3% weekly allowance");
        Check(view.Snapshot.Rows.Count(row => WidgetWindow.IsDashboardLowRow(view,row,now,20)) == 1,"Mixed reset windows produce one low-balance item, not zero or two");
        view.Error = "fixture connection error";
        Check(!WidgetWindow.IsDashboardLowRow(view,weekly,now,20),"A failed provider read prevents previous balances from entering the current low count");
        view.Error = null;
        foreach (var state in new[] { ProviderReceiptState.Waiting,ProviderReceiptState.QuotaUnavailable }) {
            view.ReceiptState = state;
            Check(!WidgetWindow.IsDashboardLowRow(view,weekly,now,20),"A receipt without confirmed quota data is excluded: " + state);
        }
        view.ReceiptState = ProviderReceiptState.Received;
        view.ReceiptObservedAt = now.AddMinutes(-31);
        Check(!WidgetWindow.IsDashboardLowRow(view,weekly,now,20),"Claude readings older than 30 minutes are excluded from the current summary");
        view.ReceiptObservedAt = null; view.LastSuccess = null;
        Check(!WidgetWindow.IsDashboardLowRow(view,weekly,now,20),"A Claude balance without an observation time is not treated as fresh");
        view.ReceiptObservedAt = now; weekly.Label = "주간 한도 · 수신 대기";
        Check(!WidgetWindow.IsDashboardLowRow(view,weekly,now,20),"A row explicitly awaiting a new receipt stays outside the low count");
        weekly.Label = "주간 한도";
        foreach (double? invalid in new double?[] { null,Double.NaN,Double.PositiveInfinity,-1,101 }) {
            weekly.RemainingPercent = invalid;
            Check(!WidgetWindow.IsDashboardLowRow(view,weekly,now,20),"An unavailable or invalid percentage cannot contribute a low balance: " + invalid);
        }
        weekly.RemainingPercent = 20;
        Check(WidgetWindow.IsDashboardLowRow(view,weekly,now,20),"The selected low-balance threshold includes a balance exactly at that percentage");
        weekly.RemainingPercent = 20.1;
        Check(!WidgetWindow.IsDashboardLowRow(view,weekly,now,20),"A balance above the threshold is excluded without rounding it down");
        weekly.RemainingPercent = 0;
        Check(WidgetWindow.IsDashboardLowRow(view,weekly,now,20),"A genuinely empty, current allowance remains a low-balance item");
    }

    private static void CheckOverviewFilters(WidgetWindow owner)
    {
        var views = (List<ProviderView>)Call(owner,"DashboardViews");
        var snapshots = views.ToDictionary(view => view,view => view.Snapshot);
        var errors = views.ToDictionary(view => view,view => view.Error);
        string filter = Field<string>(owner,"dashboardFilter");
        try {
            foreach (var view in views) {
                view.Error = null;
                view.Snapshot = new UsageSnapshot { Plan = "fixture", Rows = new List<UsageRow> {
                    new UsageRow { Label = "주간 한도", RemainingPercent = 100, ResetsAt = DateTimeOffset.Now.AddDays(7) }
                } };
            }
            var codex = views.Single(view => view.Id == "codex");
            var eleven = views.Single(view => view.Id == "eleven");
            var higgsfield = views.Single(view => view.Id == "higgsfield");
            codex.Snapshot.Rows[0].RemainingPercent = 5; codex.Snapshot.Rows[0].ResetsAt = DateTimeOffset.Now.AddHours(1);
            eleven.Error = "fixture: last fetch failed";
            higgsfield.Snapshot.Rows[0].RemainingPercent = null; higgsfield.Snapshot.Rows[0].Remaining = 1;
            Set(owner,"dashboardFilter","all");
            var page = Page(owner,"BuildDashboardOverview");
            Check(Field<Panel>(owner,"dashboardOverviewGrid").Children.Count == views.Count,"The unfiltered overview contains every authorized connected service once");
            var low = Walk(page).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "dashboard-filter:low");
            Check(Text(low).Contains("2"),"The low summary includes both current percentages and configured absolute credits");
            low.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<string>(owner,"dashboardFilter") == "low","The low summary is an actionable filter rather than a decorative count");
            Page(owner,"BuildDashboardOverview");
            string cards = Text(Field<Panel>(owner,"dashboardOverviewGrid"));
            Check(Field<Panel>(owner,"dashboardOverviewGrid").Children.Count == 2 && cards.Contains("Codex") && cards.Contains("Higgsfield") && !cards.Contains("ElevenLabs"),"Low filtering excludes a failed fetch's cached values and shows both supported allowance units");
            Set(owner,"dashboardFilter","attention"); Page(owner,"BuildDashboardOverview");
            Check(Field<Panel>(owner,"dashboardOverviewGrid").Children.Count == 1 && Text(Field<Panel>(owner,"dashboardOverviewGrid")).Contains("ElevenLabs"),"Connection or freshness uncertainty has its own filter, separate from low balance");
            Set(owner,"dashboardFilter","reset"); Page(owner,"BuildDashboardOverview");
            Check(Field<Panel>(owner,"dashboardOverviewGrid").Children.Count == 1 && Text(Field<Panel>(owner,"dashboardOverviewGrid")).Contains("Codex"),"The reset filter only contains services with confirmed deadlines inside 24 hours");
            foreach (var view in views) view.Error = "fixture: unavailable";
            Set(owner,"dashboardFilter","all"); page = Page(owner,"BuildDashboardOverview");
            low = Walk(page).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "dashboard-filter:low");
            Check(Text(low).Contains("—") && !Text(low).Contains("0"),"When no balance can be assessed, the low summary shows unknown rather than a reassuring zero");
            low.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); page = Page(owner,"BuildDashboardOverview");
            Check(Text(page).Contains("현재 잔량을 판단할 수 없습니다"),"An empty low filter explains unavailable data instead of implying every service is healthy");
        }
        finally {
            foreach (var view in views) { view.Snapshot = snapshots[view]; view.Error = errors[view]; }
            Set(owner,"dashboardFilter",filter);
        }
    }

    private static void CheckHistoryIsolation(WidgetWindow owner)
    {
        var legacyService = new ComboBox(); var legacyMetric = new ComboBox(); var legacyAnalysis = new StackPanel();
        Set(owner,"historyService",legacyService); Set(owner,"historyMetric",legacyMetric); Set(owner,"historyAnalysis",legacyAnalysis);
        try {
            var first = Page(owner,"BuildDashboardHistoryPage",false);
            var second = Page(owner,"BuildDashboardHistoryPage",false);
            var firstElements = new HashSet<UIElement>(Walk(first).OfType<UIElement>());
            Check(!Walk(second).OfType<UIElement>().Any(firstElements.Contains),"Independent dashboard history views never reuse parented WPF elements");
            var host = new StackPanel(); host.Children.Add(first); host.Children.Add(second);
            Check(host.Children.Count == 2,"Two history views can be parented simultaneously without a WPF parent collision");
            Check(Object.ReferenceEquals(Field<ComboBox>(owner,"historyService"),legacyService)
                && Object.ReferenceEquals(Field<ComboBox>(owner,"historyMetric"),legacyMetric)
                && Object.ReferenceEquals(Field<StackPanel>(owner,"historyAnalysis"),legacyAnalysis),"Dashboard history does not overwrite the independent legacy history window's controls");
            var analysis = Page(owner,"BuildDashboardHistoryPage",true);
            Check(!Walk(analysis).OfType<UIElement>().Any(firstElements.Contains),"Analysis and history use independent visual trees while sharing observations");
        }
        finally { Set(owner,"historyService",null); Set(owner,"historyMetric",null); Set(owner,"historyAnalysis",null); }
    }

    private static void CheckLanguage(WidgetWindow owner)
    {
        L.Language = "en";
        try {
            string text = Text(Page(owner,"BuildDashboardOverview")) + Text(Page(owner,"BuildDashboardServiceDetail","codex"))
                + Text(Page(owner,"BuildDashboardHistoryPage",false)) + Text(Page(owner,"BuildDashboardHistoryPage",true))
                + Text(Page(owner,"BuildDashboardSettings"));
            Check(!text.Any(value => value >= '\uAC00' && value <= '\uD7AF'),"English dashboard pages localize all user-facing labels: " + text);
        }
        finally { L.Language = "ko"; }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void CheckShellLifecycle(WidgetWindow owner)
    {
        new WindowInteropHelper(owner).EnsureHandle();
        var dashboard = (Window)Call(owner,"BuildDashboardWindow");
        try {
            Check(!dashboard.IsVisible && !owner.IsVisible,"The production dashboard builder supports hidden verification without showing a widget");
            Check(Object.ReferenceEquals(dashboard,Call(owner,"BuildDashboardWindow")) && Object.ReferenceEquals(Field<Window>(owner,"dashboardWindow"),dashboard),"Opening the dashboard repeatedly reuses one owned window");
            Check(Object.ReferenceEquals(dashboard.Owner,owner) && dashboard.ShowInTaskbar && !dashboard.Topmost,"The dashboard uses an independent taskbar window without forcing itself above the user's work");
            Call(owner,"NavigateDashboard","history");
            var content = Field<ContentControl>(owner,"dashboardContent");
            Check(Walk(content.Content as DependencyObject).OfType<ComboBox>().Any(),"The real dashboard shell renders its accountless history page");
            Call(owner,"SetDashboardHistoryService","codex"); Call(owner,"UpdateDashboardHistoryData",false);
            var metricSelector = Field<ComboBox>(owner,"dashboardHistoryMetric");
            var rangeSelector = Field<ComboBox>(owner,"dashboardHistoryRange");
            rangeSelector.SelectedItem = rangeSelector.Items.OfType<ComboBoxItem>().Single(item => (int)item.Tag == 30);
            string selectedMetric = metricSelector.SelectedItem.ToString();
            var codex = Field<ProviderView>(owner,"codexView"); string previousError = codex.Error;
            try {
                codex.Error = "Dashboard fixture changed observation";
                Call(owner,"RenderDashboard",false);
                Check(Object.ReferenceEquals(metricSelector,Field<ComboBox>(owner,"dashboardHistoryMetric"))
                    && metricSelector.SelectedItem.ToString() == selectedMetric && (int)((ComboBoxItem)rangeSelector.SelectedItem).Tag == 30,
                    "A provider update preserves the open history selectors and the user's metric and range selection");
            }
            finally { codex.Error = previousError; }
            string beforeLanguage = SelectedVisualText(metricSelector);
            Check(beforeLanguage.Contains("주간"),"The hidden WPF metric selector renders a Korean weekly label before language switching: " + beforeLanguage);
            try {
                Call(owner,"ApplyLanguage","en"); DrainDispatcher();
                string afterLanguage = SelectedVisualText(Field<ComboBox>(owner,"dashboardHistoryMetric"));
                Check(afterLanguage.Contains("Weekly") && !afterLanguage.Any(value => value >= '\uAC00' && value <= '\uD7AF'),
                    "An already-open metric selector updates its rendered selection when switching to English: " + afterLanguage);
            }
            finally { Call(owner,"ApplyLanguage","ko"); DrainDispatcher(); }
            Call(owner,"RefreshDashboardIfOpen");
            var scheduled = Field<DispatcherOperation>(owner,"dashboardRefreshOperation");
            Call(owner,"RefreshDashboardIfOpen");
            Check(scheduled != null && Object.ReferenceEquals(scheduled,Field<DispatcherOperation>(owner,"dashboardRefreshOperation")),"Multiple provider updates coalesce into one pending dashboard render");
            DrainDispatcher();
            Check(Field<DispatcherOperation>(owner,"dashboardRefreshOperation") == null,"The coalesced dashboard refresh completes without retaining a queued operation");

            Set(owner,"demo",false);
            Call(owner,"RefreshDashboardAccess");
            Check(content.Content == null && Field<StackPanel>(owner,"dashboardNavigation").Children.Count == 0,"Resetting dashboard data removes previous page content synchronously before rebuilding");
            Check(Field<List<UsageHistoryPoint>>(owner,"dashboardHistoryFiltered").Count == 0,"Dashboard invalidation also clears cached history export rows");
            DrainDispatcher();
            Check(Walk(content.Content as DependencyObject).OfType<ComboBox>().Any(),"The queued refresh rebuilds history without requiring an account or entitlement");
            Set(owner,"demo",true);
            Call(owner,"RefreshDashboardAccess"); DrainDispatcher();
            Check(Walk(content.Content as DependencyObject).OfType<ComboBox>().Any(),"Switching to preview rebuilds the existing dashboard without a second window");

            var beforeAccountChange = content.Content;
            Call(owner,"RefreshDashboardAccess");
            Check(content.Content == null,"Explicit data invalidation clears the previous page before rebuilding");
            DrainDispatcher();
            Check(!Object.ReferenceEquals(beforeAccountChange,content.Content),"The rebuilt data view does not retain stale controls");
        }
        finally {
            Set(owner,"demo",true);
            Call(owner,"CloseDashboard");
        }
        Check(Field<Window>(owner,"dashboardWindow") == null && !Field<bool>(owner,"exiting"),"Closing the dashboard releases its window but leaves the widget running");
        Call(owner,"RefreshDashboardIfOpen");
        Check(Field<DispatcherOperation>(owner,"dashboardRefreshOperation") == null,"A closed dashboard does not schedule background UI work");
    }

    private static void CheckHistoryLifecycle(WidgetWindow owner)
    {
        Call(owner,"SetDashboardHistoryService","codex");
        var page = Page(owner,"BuildDashboardHistoryPage",false);
        var service = Field<ComboBox>(owner,"dashboardHistoryService");
        var metric = Field<ComboBox>(owner,"dashboardHistoryMetric");
        var export = Field<Button>(owner,"dashboardHistoryExport");
        Check(service.Items.Count > 1 && metric.Items.Count > 1 && export.IsEnabled,"The history fixture initially contains selectable recorded observations");
        Set(owner,"demo",false);
        try {
            Call(owner,"LockDashboardHistory");
            Check(service.Items.Count == 0 && metric.Items.Count == 0 && !export.IsEnabled,"History invalidation clears stale selector values and disables the retained export button");
            Check(!Walk(page).OfType<ComboBox>().Any() && !Text(page).Contains("Pro에서"),"The invalidated history view contains no stale controls or paid upsell");
            Check(Field<List<UsageHistoryPoint>>(owner,"dashboardHistoryFiltered").Count == 0,"History invalidation drops the filtered snapshot that an export action could otherwise retain");
        }
        finally { Set(owner,"demo",true); }
        var restored = Page(owner,"BuildDashboardHistoryPage",false);
        Check(Walk(restored).OfType<ComboBox>().Any() && Field<UsageJournal>(owner,"usageJournal").GetHistory().Count > 0,"Rebuilding history restores controls without erasing stored observations");
    }
}
