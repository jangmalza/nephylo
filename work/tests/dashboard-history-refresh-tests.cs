using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using UsageWidget;

internal static class DashboardHistoryRefreshTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int count;
    private static void Check(bool value,string message) { ++count; if (!value) throw new Exception(message); }
    private static T Field<T>(object item,string name) { return (T)item.GetType().GetField(name,Private).GetValue(item); }
    private static void Set(object item,string name,object value) { item.GetType().GetField(name,Private).SetValue(item,value); }
    private static object Call(object item,string name,params object[] values)
    { try { return item.GetType().GetMethod(name,Private).Invoke(item,values); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (object item in LogicalTreeHelper.GetChildren(root)) {
            var child = item as DependencyObject;
            if (child != null) foreach (var descendant in Walk(child)) yield return descendant;
        }
    }
    private static string Text(DependencyObject root) { return String.Join("\n",Walk(root).OfType<TextBlock>().Select(item => item.Text)); }
    [STAThread]
    public static int Main()
    {
        string isolated = Path.Combine(Path.GetTempPath(),"nephylo-history-refresh-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\history-refresh-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated,"claude");
        WidgetWindow owner = null;
        try {
            owner = new WidgetWindow(true); L.Language = "ko";
            Field<DispatcherTimer>(owner,"timer").Stop(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Visible = false;
            Call(owner,"ShowDemo"); new WindowInteropHelper(owner).EnsureHandle(); Call(owner,"BuildDashboardWindow");
            Check(!owner.IsLoaded && !owner.IsVisible,"Isolated fixtures never initialize real providers");
            CheckInitialHistorySelection(owner); CheckHistoryRefresh(owner); CheckRecordsDisclosureState(owner); CheckAnalysisRefresh(owner); CheckCurrentGuards(); CheckStableActionTargets(owner); CheckNavigationContext(owner);
            Check(!Directory.Exists(isolated),"Dashboard reads do not persist demo observations or account data");
            Console.WriteLine("Dashboard history refresh tests passed: " + count + " assertions. Isolated, hidden demo fixture.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if (owner != null) {
                Set(owner,"demo",true); Call(owner,"CloseDashboard"); Field<DispatcherTimer>(owner,"timer").Stop(); owner.Close();
                Field<CodexService>(owner,"codex").Dispose(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null; SettingsStore.StartupRegistrySubKeyOverride = null; ClaudeUsageService.ConfigDirectoryOverride = null;
        }
    }
    private static void CheckInitialHistorySelection(WidgetWindow owner)
    {
        var journal = Field<UsageJournal>(owner,"usageJournal");
        var codex = Field<ProviderView>(owner,"codexView");
        var claude = Field<ProviderView>(owner,"claudeView"); var previous = claude.Snapshot;
        try {
            var isolated = new UsageJournal(null,true); var now = DateTimeOffset.Now;
            isolated.Observe("codex",codex.Snapshot,now,"api",now);
            Set(owner,"usageJournal",isolated); claude.Snapshot = new UsageSnapshot { Rows = new List<UsageRow>() };
            Call(owner,"ResetDashboardHistoryState"); Call(owner,"BuildDashboardHistoryPage",false);
            Check(Field<string>(owner,"dashboardHistoryServiceId") == "codex","First history visit chooses a connected service with records before an alphabetically earlier empty service");
            Call(owner,"SetDashboardHistoryService","claude"); Call(owner,"BuildDashboardHistoryPage",false);
            Check(Field<string>(owner,"dashboardHistoryServiceId") == "claude","An explicit selection with no records remains selected instead of jumping to a populated service");
        }
        finally { Set(owner,"usageJournal",journal); claude.Snapshot = previous; Call(owner,"ResetDashboardHistoryState"); }
    }
    private static void CheckHistoryRefresh(WidgetWindow owner)
    {
        Call(owner,"SetDashboardHistoryService","codex"); Call(owner,"NavigateDashboard","history");
        var chart = Field<UsageHistoryChart>(owner,"dashboardHistoryChart");
        var root = Field<StackPanel>(owner,"dashboardHistoryContent");
        var disclosure = Walk(root).OfType<Expander>().Single(item => AutomationProperties.GetAutomationId(item) == "dashboard-history-records"); disclosure.IsExpanded = true;
        int journalCount = Field<UsageJournal>(owner,"usageJournal").GetHistory().Count;
        Call(owner,"RefreshDashboardHistoryIfOpen");
        Check(Object.ReferenceEquals(chart,Field<UsageHistoryChart>(owner,"dashboardHistoryChart")),"A poll preserves the interactive chart instance");
        Check(Object.ReferenceEquals(disclosure,Walk(root).OfType<Expander>().Single(item => AutomationProperties.GetAutomationId(item) == "dashboard-history-records")) && disclosure.IsExpanded,"Refreshing history preserves the recent-records disclosure and its expanded state");
        var service = Field<ComboBox>(owner,"dashboardHistoryService");
        // Hidden WPF controls coerce IsDropDownOpen to false. Supply the pending
        // state and raise the production close event without showing a popup.
        Set(owner,"dashboardHistoryRefreshPending",true);
        typeof(ComboBox).GetMethod("OnDropDownClosed",Private).Invoke(service,new object[] { EventArgs.Empty });
        Check(!Field<bool>(owner,"dashboardHistoryRefreshPending"),"Closing a filter catches up immediately instead of waiting for the next poll");
        Check(journalCount == Field<UsageJournal>(owner,"usageJournal").GetHistory().Count,"History refresh never creates extra observations");
        var now = DateTimeOffset.Now;
        Set(owner,"dashboardHistoryFiltered",new List<UsageHistoryPoint> {
            new UsageHistoryPoint { ServiceId = "codex",Label = "주간 한도",Source = "api",ObservedAt = now.AddMinutes(-6),RemainingPercent = 60 },
            new UsageHistoryPoint { ServiceId = "codex",Label = "주간 한도",Source = "api",ObservedAt = now.AddMinutes(-4) },
            new UsageHistoryPoint { ServiceId = "codex",Label = "주간 한도",Source = "api",ObservedAt = now.AddMinutes(-2),RemainingPercent = 50 }
        });
        Call(owner,"BuildDashboardHistoryRecords",null,now.AddDays(-1),now);
        Check(Field<List<UsageHistoryPoint>>(chart,"points").Count == 3,"Dashboard preserves the missing observation when supplying the chart");
        Check(Field<System.Collections.IList>(chart,"segments").Count == 2,"A missing middle value prevents a misleading line between two known balances");
    }
    private static Expander RecordsDisclosure(WidgetWindow owner)
    {
        return Walk(Field<StackPanel>(owner,"dashboardHistoryContent")).OfType<Expander>()
            .Single(item => AutomationProperties.GetAutomationId(item)=="dashboard-history-records");
    }
    private static void CheckRecordsDisclosureState(WidgetWindow owner)
    {
        Call(owner,"ResetDashboardHistoryState"); Call(owner,"SetDashboardHistoryService","codex");
        Call(owner,"NavigateDashboard","history");
        Check(!RecordsDisclosure(owner).IsExpanded,"Recent records starts collapsed for a new dashboard session");
        RecordsDisclosure(owner).IsExpanded=true;
        Call(owner,"NavigateDashboard","overview"); Call(owner,"NavigateDashboard","history");
        Check(RecordsDisclosure(owner).IsExpanded,"Returning from overview preserves the expanded recent-records choice");
        var previous=RecordsDisclosure(owner); Call(owner,"RenderDashboard",true);
        Check(!Object.ReferenceEquals(previous,RecordsDisclosure(owner)) && RecordsDisclosure(owner).IsExpanded,"A forced rebuild restores expansion on the new recent-records control");
        var range=Field<ComboBox>(owner,"dashboardHistoryRange");
        range.SelectedItem=range.Items.OfType<ComboBoxItem>().Single(item => (int)item.Tag==30);
        var service=Field<ComboBox>(owner,"dashboardHistoryService");
        service.SelectedItem=service.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag=="eleven");
        Check(RecordsDisclosure(owner).IsExpanded,"Service and period filters share the display preference rather than collapsing records");
        Call(owner,"ApplyLanguage","en"); Call(owner,"ApplyRequestedTheme","light"); Call(owner,"RenderDashboard",true);
        Check(RecordsDisclosure(owner).IsExpanded,"Language and theme recreation preserve expanded recent records");
        RecordsDisclosure(owner).IsExpanded=false;
        Call(owner,"NavigateDashboard","overview"); Call(owner,"NavigateDashboard","history");
        Check(!RecordsDisclosure(owner).IsExpanded,"An explicit collapse replaces the earlier expanded preference");
        RecordsDisclosure(owner).IsExpanded=true;
        Call(owner,"CaptureDashboardAnalysisDisclosures");
        Check(Field<Dictionary<string,bool>>(owner,"dashboardHistoryDisclosureStates").ContainsKey("history:records"),"Recent records participates in the existing disclosure-state lifecycle");
        Call(owner,"LockDashboardHistory");
        Check(Field<Dictionary<string,bool>>(owner,"dashboardHistoryDisclosureStates").Count==0,"Access revocation clears disclosure state with protected history controls");
        Call(owner,"NavigateDashboard","history");
        Check(!RecordsDisclosure(owner).IsExpanded,"Restored access does not resurrect a previous protected disclosure");
        Call(owner,"ApplyLanguage","ko"); Call(owner,"ApplyRequestedTheme","dark");
        Call(owner,"ResetDashboardHistoryState");
    }
    private static void CheckAnalysisRefresh(WidgetWindow owner)
    {
        Call(owner,"SetDashboardHistoryService","codex"); Call(owner,"NavigateDashboard","analysis");
        var root = Field<StackPanel>(owner,"dashboardHistoryContent");
        var disclosure = Walk(root).OfType<Expander>().Single();
        Check(!disclosure.IsExpanded,"Methodology starts collapsed so the conclusion and quantities are visible first");
        disclosure.IsExpanded = true;
        string initial = Text(root);
        var view = Field<ProviderView>(owner,"codexView");
        var average = Field<UsageJournal>(owner,"usageJournal").GetAnalysis("codex","주간 한도","api",DateTimeOffset.Now).AveragePerDay;
        Check(average.HasValue && initial.Contains("하루 평균 소모량"),"The analysis card exposes the existing observed average");
        view.Error = "Fixture connection failure"; Call(owner,"RefreshDashboardHistoryIfOpen");
        Check(Object.ReferenceEquals(disclosure,Walk(root).OfType<Expander>().Single()) && disclosure.IsExpanded,"A provider error updates a stable, expanded methodology control without replacing its focus target");
        string changed = Text(root);
        Check(changed.Contains("최근 잔량 확인 필요") && changed.Contains("평균만 표시"),"A recent fetch failure is visible immediately even with an expanded analysis disclosure");
        Check(!changed.Contains("현 추세라면 초기화까지 여유") && !changed.Contains("소진 참고 범위 ·"),"An old journal forecast is not presented as current while its provider fails");
        Check(changed.Contains(average.Value.ToString("#,0.##")),"Historical observed average remains available during a provider failure");
        view.Error = null; Call(owner,"RefreshDashboardHistoryIfOpen");
        Check(Text(root).Contains("현 추세라면 초기화까지 여유"),"A new valid provider state restores the existing forecast without rebuilding the disclosure");
        Check(Object.ReferenceEquals(disclosure,Walk(root).OfType<Expander>().Single()),"Recovery preserves the same analysis controls");
    }
    private static void CheckCurrentGuards()
    {
        var now = DateTimeOffset.Now;
        var row = new UsageRow { Label = "주간 한도",RemainingPercent = 40,ResetsAt = now.AddDays(2) };
        var view = new ProviderView { Id = "codex",Source = "api",LastSuccess = now,Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { row } } };
        var analysis = new UsageAnalysis { ResetAt = row.ResetsAt };
        Check(WidgetWindow.DashboardAnalysisCurrent(view,row.Label,analysis,now),"A fresh, matching quota is eligible for current-balance analysis");
        view.Error = "failed";
        Check(!WidgetWindow.DashboardAnalysisCurrent(view,row.Label,analysis,now),"Errors suppress current predictions despite a recent retained snapshot");
        view.Error = null; view.LastSuccess = now.AddMinutes(-11);
        Check(!WidgetWindow.DashboardAnalysisCurrent(view,row.Label,analysis,now),"An old automatic snapshot cannot support a current forecast");
        view.LastSuccess = now; analysis.ResetAt = now.AddDays(3);
        Check(!WidgetWindow.DashboardAnalysisCurrent(view,row.Label,analysis,now),"A journal for another reset window cannot lend a forecast to the current quota");
        analysis.ResetAt = null;
        Check(WidgetWindow.DashboardAnalysisCurrent(view,row.Label,analysis,now),"A fresh service still shows collecting state before an analysis epoch exists");
        view.Snapshot.Rows.Add(row);
        Check(!WidgetWindow.DashboardAnalysisCurrent(view,row.Label,analysis,now),"Duplicate labels do not select an ambiguous quota");
        view.Snapshot.Rows.RemoveAt(1); view.Source = "code"; view.ReceiptState = ProviderReceiptState.QuotaUnavailable;
        Check(!WidgetWindow.DashboardAnalysisCurrent(view,row.Label,analysis,now),"A missing subscription quota cannot inherit a retained forecast");
        view.ReceiptState = ProviderReceiptState.Received; view.ReceiptObservedAt = now.AddMinutes(-31);
        Check(!WidgetWindow.DashboardAnalysisCurrent(view,row.Label,analysis,now),"An old code receipt cannot support a current forecast");
        view.ReceiptObservedAt = now; view.ReceiptState = ProviderReceiptState.ResetWaiting;
        view.Snapshot.Rows.Add(new UsageRow { Label = "5시간 한도",RemainingPercent = 2,ResetsAt = now.AddMinutes(-1) });
        Check(WidgetWindow.DashboardAnalysisCurrent(view,row.Label,analysis,now),"An expired short window does not suppress the fresh weekly analysis in the same response");
    }
    private static void CheckStableActionTargets(WidgetWindow owner)
    {
        var view = Field<ProviderView>(owner,"codexView");
        Set(owner,"dashboardSelectedService","codex");
        var finder = typeof(WidgetWindow).GetMethod("FindDashboardFocus",BindingFlags.NonPublic | BindingFlags.Static);
        foreach (string page in new[] { "detail","settings" }) {
            Call(owner,"NavigateDashboard",page);
            var oldRoot = Field<ContentControl>(owner,"dashboardContent").Content as DependencyObject;
            var actions = Walk(oldRoot).OfType<Button>().ToList();
            Check(actions.Count == (page == "detail" ? 5 : 4),"Action target fixture covers every " + page + " button without an obsolete account entry");
            var ids = actions.Select(AutomationProperties.GetAutomationId).ToList();
            Check(ids.All(id => !String.IsNullOrEmpty(id)) && ids.Distinct().Count() == actions.Count,"Every " + page + " action has a unique, stable focus identity");
            bool busy = view.Busy;
            try { view.Busy = !busy; Call(owner,"RenderDashboard",false); }
            finally { view.Busy = busy; }
            var newRoot = Field<ContentControl>(owner,"dashboardContent").Content as DependencyObject;
            Check(!Object.ReferenceEquals(oldRoot,newRoot),"A changed provider triggers the real " + page + " redraw path");
            for (int index = 0; index < ids.Count; index++) {
                var replacement = finder.Invoke(null,new object[] { newRoot,ids[index] }) as Button;
                Check(replacement != null && !Object.ReferenceEquals(replacement,actions[index]),"Focus restoration locates the replacement action after a poll: " + ids[index]);
            }
        }
        Call(owner,"NavigateDashboard","overview");
        var overview = Field<ContentControl>(owner,"dashboardContent").Content as DependencyObject;
        var service = Walk(overview).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "dashboard-service:codex");
        Check(AutomationProperties.GetName(service).Contains("Codex") && !AutomationProperties.GetName(service).Contains("dashboard-service:"),"Service cards announce a human-readable name and value, keeping implementation IDs separate");
        var filter = Walk(overview).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "dashboard-filter:low");
        filter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var filtered = Field<ContentControl>(owner,"dashboardContent").Content as DependencyObject;
        Check(!Object.ReferenceEquals(overview,filtered) && finder.Invoke(null,new object[] { filtered,"dashboard-filter:low" }) is Button,
            "The explicit filter action exposes a replacement target for restoring keyboard focus after rebuilding the overview");
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
    private static void Layout(WidgetWindow owner)
    {
        var root = Field<Grid>(owner,"dashboardRoot"); root.Width = 1100; root.Height = 500;
        root.Measure(new Size(1100,500)); root.Arrange(new Rect(0,0,1100,500)); root.UpdateLayout(); Drain();
    }
    private static void CheckNavigationContext(WidgetWindow owner)
    {
        Set(owner,"dashboardFilter","all"); Set(owner,"dashboardFavoritesOnly",false);
        Call(owner,"NavigateDashboard","overview"); Layout(owner);
        var scroll = Field<ScrollViewer>(owner,"dashboardScroll"); scroll.ScrollToVerticalOffset(130); Layout(owner);
        double offset = scroll.VerticalOffset; Check(offset > 0,"Navigation fixture has a genuinely scrolled overview");
        Call(owner,"OpenDashboardService","codex"); Layout(owner);
        Check(Field<string>(owner,"dashboardPage") == "detail" && (string)Call(owner,"DashboardReturnLabel") == "← 개요","Overview service opens detail with an origin-aware back action");
        Call(owner,"ReturnFromDashboardService"); Layout(owner);
        Check(Field<string>(owner,"dashboardPage") == "overview" && Math.Abs(scroll.VerticalOffset-offset) < 1,"Returning restores the overview scroll position after layout");
        Set(owner,"dashboardFilter","reset"); Set(owner,"dashboardFavoritesOnly",true);
        Call(owner,"NavigateDashboard","overview"); Call(owner,"OpenDashboardService","codex");
        Call(owner,"ReturnFromDashboardService"); Layout(owner);
        Check(Field<string>(owner,"dashboardFilter") == "reset" && Field<bool>(owner,"dashboardFavoritesOnly"),"Detail navigation preserves combined overview filters");
        var root = Field<ContentControl>(owner,"dashboardContent").Content as DependencyObject;
        var clear = Walk(root).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "dashboard-clear-filters");
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(owner);
        Check(Field<string>(owner,"dashboardFilter") == "all" && !Field<bool>(owner,"dashboardFavoritesOnly"),"Clear filters removes both summary and favorites filters");
        Check(Field<System.Windows.Controls.Primitives.UniformGrid>(owner,"dashboardOverviewGrid").Children.Count == 8,"Clearing an empty combination restores authorized service cards");
        Call(owner,"NavigateDashboard","timeline"); Call(owner,"OpenDashboardService","codex");
        Check((string)Call(owner,"DashboardReturnLabel") == "← 초기화 일정","Schedule detail has a schedule-specific back label");
        Call(owner,"ReturnFromDashboardService"); Layout(owner);
        Check(Field<string>(owner,"dashboardPage") == "timeline","Schedule detail returns to the schedule, not the management page");
        Call(owner,"OpenDashboardService","not-connected");
        Check(Field<string>(owner,"dashboardPage") == "timeline","An unauthorized service cannot be opened through a stale navigation action");
        Call(owner,"OpenDashboardServiceFromMenu","codex","dashboard-toggle-sidebar");
        Check(Field<string>(owner,"dashboardReturnFocus") == "dashboard-toggle-sidebar","Compact-menu entry preserves the persistent menu opener as its return focus");
        Call(owner,"ReturnFromDashboardService"); Layout(owner);
        Call(owner,"NavigateDashboard","overview");
        var window = Field<Window>(owner,"dashboardWindow"); double width = window.Width;
        var settings = Field<WidgetSettings>(owner,"settings"); bool collapsed = settings.DashboardSidebarCollapsed;
        try {
            window.Width = 850; settings.DashboardSidebarCollapsed = false; Call(owner,"UpdateDashboardSidebar");
            Check(Field<System.Windows.Controls.Primitives.UniformGrid>(owner,"dashboardOverviewGrid").Columns == 1,"Expanded sidebar uses one readable card column near the breakpoint");
            settings.DashboardSidebarCollapsed = true; Call(owner,"UpdateDashboardSidebar");
            Check(Field<System.Windows.Controls.Primitives.UniformGrid>(owner,"dashboardOverviewGrid").Columns == 2,"Collapsing the sidebar reflows cards without a window resize");
            settings.DashboardSidebarCollapsed = false; Call(owner,"UpdateDashboardSidebar");
            Check(Field<System.Windows.Controls.Primitives.UniformGrid>(owner,"dashboardOverviewGrid").Columns == 1,"Expanding the sidebar immediately restores a readable single column");
        } finally { window.Width = width; settings.DashboardSidebarCollapsed = collapsed; Call(owner,"UpdateDashboardSidebar"); }
        var view = Field<ProviderView>(owner,"codexView"); string error = view.Error; bool busy = view.Busy;
        try {
            view.Error = "fixture";
            string card = Text((DependencyObject)Call(owner,"BuildDashboardOverviewCard",view));
            Check(card.Contains("일정 확인 필요") && !card.Contains("초기화까지"),"A failed reading does not present a cached deadline as a confirmed countdown");
            view.Error = null; view.Busy = true;
            Check(Text((DependencyObject)Call(owner,"BuildDashboardOverviewCard",view)).Contains("갱신 중"),"In-flight refresh is visible without replacing the prior balance with zero");
        } finally { view.Error = error; view.Busy = busy; }
        var nav = Field<StackPanel>(owner,"dashboardNavigation");
        var ids = Walk(nav).OfType<Button>().Select(AutomationProperties.GetAutomationId).ToArray();
        Check(ids.All(id => !String.IsNullOrEmpty(id)) && ids.Distinct().Count() == ids.Length,"Navigation actions have unique stable focus targets");
    }
}
