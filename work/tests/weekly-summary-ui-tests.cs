using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageWidget;

internal static class WeeklySummaryUiTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int assertions;
    private static void Check(bool value,string message) { ++assertions; if (!value) throw new Exception(message); }
    private static T Field<T>(object owner,string name) { return (T)owner.GetType().GetField(name,Private).GetValue(owner); }
    private static void Set(object owner,string name,object value) { owner.GetType().GetField(name,Private).SetValue(owner,value); }
    private static object Call(object owner,string name,params object[] args)
    { try { return owner.GetType().GetMethod(name,Private).Invoke(owner,args); } catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; } }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        if (root == null) yield break;
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var node in Walk(child)) yield return node;
    }
    private static string Text(DependencyObject root)
    { return String.Join("\n",Walk(root).OfType<TextBlock>().Select(block => block.Text).Concat(Walk(root).OfType<ContentControl>().Where(control => control.Content is string).Select(control => (string)control.Content))); }
    private static Button Button(DependencyObject root,string id)
    { return Walk(root).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == id); }
    private static void Drain()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        string isolated = Path.Combine(Path.GetTempPath(),"nephylo-weekly-ui-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\weekly-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated,"claude");
        WidgetWindow owner = null;
        try {
            L.Language = "ko"; owner = new WidgetWindow(true);
            Field<DispatcherTimer>(owner,"timer").Stop(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Visible = false;
            Call(owner,"ShowDemo"); new WindowInteropHelper(owner).EnsureHandle(); Call(owner,"BuildDashboardWindow");
            Check(!owner.IsLoaded && !owner.IsVisible,"Hidden demo fixture never initializes actual providers or identities");
            var journal = SeedJournal((string)Call(owner,"CurrentAnalysisSource","codex")); Set(owner,"usageJournal",journal);
            if (args.Length == 2 && args[0] == "--preview") { SavePreview(owner,args[1]); return 0; }
            CheckReadingAndNavigation(owner,journal); CheckRefresh(owner,journal); CheckAccess(owner,journal); CheckEmptyStates(owner,journal); CheckTextMeaning(); CheckLanguages(owner,journal); CheckMetricTitles();
            Check(!Field<DispatcherTimer>(owner,"timer").IsEnabled && !Field<bool>(owner,"refreshing"),"Summary construction and navigation never start provider polling");
            Check(!Directory.Exists(isolated),"Read-only summary fixtures never create profile, account or journal files");
            Console.WriteLine("Weekly summary UI tests passed: " + assertions + " assertions. Hidden isolated WPF, no real accounts, network or clipboard writes."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally {
            if (owner != null) {
                Set(owner,"demo",true); Call(owner,"CloseDashboard"); Field<DispatcherTimer>(owner,"timer").Stop(); owner.Close();
                Field<CodexService>(owner,"codex").Dispose(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null; SettingsStore.StartupRegistrySubKeyOverride = null; ClaudeUsageService.ConfigDirectoryOverride = null; L.Language = "ko";
        }
    }

    private static void CheckMetricTitles()
    {
        var identity = typeof(WidgetWindow).GetMethod("WeeklyIdentity",BindingFlags.Static|BindingFlags.NonPublic);
        string language = L.Language;
        try {
            foreach (string locale in new[] { "ko","en" }) {
                L.Language = locale;
                var item = new WeeklyUsageItem { ServiceId="codex",Label="Codex · 주간",Source="api" };
                string title = (string)identity.Invoke(null,new object[] { item,false });
                Check(title == L.Text(item.Label) && !title.Contains("Codex · Codex"),"Weekly summaries and copied titles show the real provider prefix once: " + locale);
                Check(item.Label == "Codex · 주간","Weekly display formatting leaves journal identity unchanged");
                item.Source="manual";
                Check((string)identity.Invoke(null,new object[] { item,false }) == "Codex · " + item.Label,"User-authored weekly labels remain verbatim");
            }
        } finally { L.Language = language; }
    }

    private static void SavePreview(WidgetWindow owner,string destination)
    {
        // Render only a synthetic dashboard tree; do not capture the desktop or
        // display a live window, and keep the example-data header visible.
        foreach (string field in new[] { "claudeView","elevenView","openAiView","geminiView","cursorView","higgsfieldView","higgsfieldApiView" })
            Field<ProviderView>(owner,field).Snapshot = null;
        Field<ProviderView>(owner,"codexView").Snapshot.Rows = new List<UsageRow> { new UsageRow { Label = "주간 한도",RemainingPercent = 15 } };
        Call(owner,"NavigateDashboard","weekly");
        var root = Field<Grid>(owner,"dashboardRoot");
        root.Measure(new Size(1100,800)); root.Arrange(new Rect(0,0,1100,800)); root.UpdateLayout(); Drain();
        var bitmap = new RenderTargetBitmap(1100,800,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(destination)) encoder.Save(stream);
        Console.WriteLine("Synthetic weekly-summary preview saved: " + destination);
    }

    private static UsageJournal SeedJournal(string source)
    {
        var journal = new UsageJournal(null,true); journal.SetAnalysisOwner(new String('a',64));
        DateTime today = DateTime.Today; double remaining = 25;
        for (int day = -14; day < 0; ++day) for (int step = 0; step <= 36; ++step) {
            var at = new DateTimeOffset(today.AddDays(day).AddHours(8).AddMinutes(step * 10));
            remaining -= 0.02;
            journal.Observe("codex",new UsageSnapshot { Plan = "Pro", Rows = new List<UsageRow> {
                new UsageRow { Label = "주간 한도",RemainingPercent = remaining,ResetsAt = new DateTimeOffset(today.AddDays(7)) }
            } },at,source,at);
        }
        return journal;
    }

    private static void CheckReadingAndNavigation(WidgetWindow owner,UsageJournal journal)
    {
        int before = journal.GetHistory().Count;
        Call(owner,"NavigateDashboard","weekly"); var page = Field<StackPanel>(owner,"weeklyPageRoot");
        var report = (WeeklyUsageReport)Call(owner,"ReadWeeklySummary",DateTimeOffset.Now);
        var codex = report.Items.Single(item => item.ServiceId == "codex" && item.Label == "주간 한도");
        Check(codex.Current.HasConsumption && codex.Current.ObservedDays == 7,"Real journal observations feed completed days into the weekly summary");
        Check(codex.Current.Consumption > 0 && codex.Current.CoveredHours >= 41.9,"Summary uses exact recorded intervals, not compacted chart differences");
        string text = Text(page);
        Check(text.Contains("오늘 제외") && text.Contains("비교 기간") && text.Contains(DateTime.Today.AddDays(-7).ToString("yyyy.MM.dd")),"Both completed current and comparison date ranges are explicit");
        Check(text.Contains("관측 소모량") && text.Contains("총사용량과 다를") && text.Contains("전체 합계는 표시하지"),"Observed totals and cross-unit restrictions are visible");
        Check(Button(page,"weekly-copy").IsEnabled,"A report with observations has an explicit copy action");
        Check(journal.GetHistory().Count == before,"Rendering summaries never appends or changes history records");
        Check(Walk(Field<StackPanel>(owner,"dashboardNavigation")).OfType<Button>().Any(button => AutomationProperties.GetAutomationId(button) == "dashboard-nav:weekly"),"Weekly page is reachable from normal dashboard navigation");
        var link = Button(page,"weekly-history:codex\n주간 한도"); link.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(Field<string>(owner,"dashboardPage") == "history" && Field<string>(owner,"dashboardHistoryServiceId") == "codex","Per-row history action navigates to the matching service");
        Check(Field<string>(owner,"dashboardHistoryMetricKey") == "codex\n주간 한도","Per-row history preserves the exact metric rather than selecting an unrelated quota");
        Call(owner,"NavigateDashboard","weekly");
    }

    private static void CheckRefresh(WidgetWindow owner,UsageJournal journal)
    {
        var page = Field<StackPanel>(owner,"weeklyPageRoot");
        var copy = Button(page,"weekly-copy"); var history = Button(page,"weekly-history:codex\n주간 한도");
        var disclosure = Walk(page).OfType<Expander>().Single(); disclosure.IsExpanded = true;
        var card = (Border)((StackPanel)Field<StackPanel>(owner,"weeklyCardsRoot")).Children.OfType<Border>().First();
        var scroll = Field<ScrollViewer>(owner,"dashboardScroll"); scroll.Measure(new Size(550,350)); scroll.Arrange(new Rect(0,0,550,350)); scroll.UpdateLayout();
        scroll.ScrollToVerticalOffset(150); Drain(); double offset = scroll.VerticalOffset;
        for (int i = 0; i < 3; ++i) Call(owner,"RefreshWeeklySummary");
        Check(Object.ReferenceEquals(page,Field<StackPanel>(owner,"weeklyPageRoot")),"Polling preserves the page root and scroll owner");
        Check(Object.ReferenceEquals(copy,Button(page,"weekly-copy")) && Object.ReferenceEquals(history,Button(page,"weekly-history:codex\n주간 한도")),"Polling preserves actionable controls and keyboard focus targets");
        Check(Object.ReferenceEquals(card,Field<StackPanel>(owner,"weeklyCardsRoot").Children.OfType<Border>().First()),"Polling updates text in place instead of replacing service cards");
        Check(disclosure.IsExpanded && Object.ReferenceEquals(disclosure,Walk(page).OfType<Expander>().Single()),"Polling preserves an expanded calculation explanation");
        Check(Math.Abs(scroll.VerticalOffset-offset) < 1,"Background summary refresh does not jump the reading position");
        var view = Field<ProviderView>(owner,"codexView"); string error = view.Error;
        try { view.Error = "isolated refresh error"; Call(owner,"RenderDashboard",false); }
        finally { view.Error = error; }
        Check(Object.ReferenceEquals(copy,Field<Button>(owner,"weeklyCopy")),"The real dashboard render path also preserves weekly action controls on provider updates");
    }

    private static void CheckAccess(WidgetWindow owner,UsageJournal journal)
    {
        var page = Field<StackPanel>(owner,"weeklyPageRoot"); var copy = Button(page,"weekly-copy");
        Set(owner,"demo",false);
        try {
            Check(Call(owner,"ReadWeeklySummary",DateTimeOffset.Now) != null,"Accountless use can summarize the existing local journal");
            Call(owner,"RefreshWeeklySummary");
            Check(copy.IsEnabled && Text(page).Contains("Codex"),"Accountless summary retains its existing values and copy control");
            string readingOwner = (string)typeof(WidgetWindow).GetProperty("ServiceReadingOwner",Private).GetValue(owner,null);
            Check((bool)Call(owner,"WeeklyAccessMatches",journal,readingOwner),"The summary accepts its current local journal and window epoch");
            Check(!(bool)Call(owner,"WeeklyAccessMatches",new UsageJournal(null,true),readingOwner),"A stale journal cannot supply a retained summary action");
            Check(!(bool)Call(owner,"WeeklyAccessMatches",journal,"stale-window"),"A stale window epoch cannot supply a retained summary action");
            Set(owner,"exiting",true);
            try { Check(!(bool)Call(owner,"WeeklyAccessMatches",journal,readingOwner),"Closing the widget invalidates retained summary actions"); }
            finally { Set(owner,"exiting",false); }
        }
        finally { Set(owner,"demo",true); }
        Call(owner,"RefreshWeeklySummary");
        Check(Field<Button>(owner,"weeklyCopy") != null,"Returning to preview keeps the report usable without restarting the dashboard");
        Call(owner,"RefreshDashboardAccess");
        Check(Field<StackPanel>(owner,"weeklyPageRoot") == null,"Data invalidation removes cached weekly references synchronously");
        Drain(); Check(Field<StackPanel>(owner,"weeklyPageRoot") != null,"Queued rendering rebuilds the current local journal view");
        int before = journal.GetHistory().Count; journal.SetAnalysisOwner(new String('b',64)); Call(owner,"RefreshWeeklySummary");
        var report = (WeeklyUsageReport)Call(owner,"ReadWeeklySummary",DateTimeOffset.Now);
        Check(report.Items.All(item => !item.Current.HasConsumption && item.Current.LowDays == 0),"An explicitly changed analysis epoch does not combine observations from the previous epoch");
        Check(journal.GetHistory().Count == before,"Analysis epoch isolation does not erase historical chart data");
    }

    private static void CheckEmptyStates(WidgetWindow owner,UsageJournal journal)
    {
        var options = journal.Options; options.HistoryEnabled = false; journal.SaveOptions(options); Call(owner,"RefreshWeeklySummary");
        var page = Field<StackPanel>(owner,"weeklyPageRoot");
        Check(Text(page).Contains("꺼져") && Text(page).Contains("기존 기록은 사용 이력"),"History-off state explains both future collection and existing history access");
        Check(Field<Button>(owner,"weeklyCopy") == null && Button(page,"weekly-enable-history") != null,"History-off state offers settings instead of stale figures or export");
        options.HistoryEnabled = true; journal.SaveOptions(options); Call(owner,"RefreshWeeklySummary");
        Check(Field<Button>(owner,"weeklyCopy") != null && !Field<Button>(owner,"weeklyCopy").IsEnabled,"No current-epoch observations give a disabled copy state rather than a fabricated report");
        journal.ClearHistory(); Call(owner,"RefreshWeeklySummary");
        Check(!Field<Button>(owner,"weeklyCopy").IsEnabled,"Clearing history immediately disables copy");
        Check(Text(Field<StackPanel>(owner,"weeklyPageRoot")).Contains("0으로 표시하지"),"Missing data is explained rather than displayed as zero consumption");
    }

    private static void CheckTextMeaning()
    {
        var report = new WeeklyUsageReport { LocalStart = new DateTime(2026,10,7),LocalEndExclusive = new DateTime(2026,10,14),LowThresholdPercent = 20 };
        var item = new WeeklyUsageItem { ServiceId = "codex",Label = "주간 한도",Source = "api",Unit = "%p",Comparable = true };
        item.Current.IntervalCount = item.Previous.IntervalCount = 4; item.Current.CoveredHours = item.Previous.CoveredHours = 50;
        report.Items.Add(item); string text = WidgetWindow.WeeklySummaryText(report);
        Check(text.Contains("이전 소모량이 0") && !text.Contains("Infinity") && !text.Contains("NaN"),"Valid zero baseline does not imply an infinite growth rate");
        Check(text.Contains("관측된 소모 없음") && !text.Contains("소모 최다 · 기록 부족"),"Actual observed zero is distinct from missing observations");
        Check(text.Contains("비율 정보 없음") && !text.Contains("저잔량 관측 0일"),"No percentage evidence cannot look like zero low-balance days");
        item.Current.UnassignedDayBuckets = 1; text = WidgetWindow.WeeklySummaryText(report);
        Check(text.Contains("날짜 경계를 넘은 구간"),"Copied report retains the cross-midnight attribution limitation");
        item.Current.IntervalCount = 0; item.Comparable = false; item.Reason = "자료 부족 · 완료된 최근 7일의 관측 기록이 없습니다.";
        Check(WidgetWindow.WeeklySummaryText(report).Contains("관측 소모량 · —"),"Unavailable consumption is copied as unavailable, not a numeric zero");
    }

    private static void CheckLanguages(WidgetWindow owner,UsageJournal journal)
    {
        L.Language = "en";
        try {
            Call(owner,"RenderDashboard",true); string text = Text(Field<StackPanel>(owner,"weeklyPageRoot"));
            Check(!text.Any(c => c >= '\uAC00' && c <= '\uD7AF'),"Weekly English page has no untranslated product copy: " + text);
            var options = journal.Options; options.HistoryEnabled = false; journal.SaveOptions(options); Call(owner,"RefreshWeeklySummary");
            text = Text(Field<StackPanel>(owner,"weeklyPageRoot"));
            Check(!text.Any(c => c >= '\uAC00' && c <= '\uD7AF'),"History-off recovery actions are localized");
            options.HistoryEnabled = true; journal.SaveOptions(options);
            Set(owner,"demo",false);
            try {
                Call(owner,"RefreshWeeklySummary"); text = Text(Field<StackPanel>(owner,"weeklyPageRoot"));
                foreach (var view in (List<ProviderView>)Call(owner,"DashboardViews")) {
                    if ((string)Call(owner,"CurrentAnalysisSource",view.Id) != "manual" || view.Snapshot == null) continue;
                    foreach (var row in view.Snapshot.Rows) {
                        Check(text.Contains(row.Label),"Accountless summaries preserve user-authored manual metric labels verbatim");
                        text = text.Replace(row.Label,"");
                    }
                }
                Check(!text.Any(c => c >= '\uAC00' && c <= '\uD7AF'),"Accountless summary product copy is localized while manual labels remain verbatim: " + text);
            }
            finally { Set(owner,"demo",true); }
        }
        finally { L.Language = "ko"; }
    }
}
