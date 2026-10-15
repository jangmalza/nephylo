using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageWidget;

internal static class DashboardTimelineTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int assertions;
    private static string renderDirectory;
    private static readonly TimeZoneInfo Seoul = TimeZoneInfo.CreateCustomTimeZone("Test Seoul",TimeSpan.FromHours(9),"Test Seoul","Test Seoul");
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026,10,13,22,30,0,TimeSpan.FromHours(9));
    private static void Check(bool value,string message) { assertions++; if (!value) throw new Exception(message); }
    private static UsageRow Row(string label,DateTimeOffset? reset,double? balance) { return new UsageRow { Label = label, ResetsAt = reset, RemainingPercent = balance }; }
    private static ProviderView View(string id,params UsageRow[] rows) { return new ProviderView { Id = id, Source = "api", LastSuccess = Now, Snapshot = new UsageSnapshot { Rows = rows.ToList() } }; }
    private static DashboardResetSchedule Project(params ProviderView[] views) { return DashboardResetTimeline.Project(views,Now,Seoul); }
    private static T Field<T>(object value,string name) { return (T)value.GetType().GetField(name,Private).GetValue(value); }
    private static void Set(object value,string name,object next) { value.GetType().GetField(name,Private).SetValue(value,next); }
    private static object Call(object owner,string name,params object[] arguments)
    {
        try { return owner.GetType().GetMethod(name,Private).Invoke(owner,arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject value)
    {
        yield return value;
        foreach (var child in LogicalTreeHelper.GetChildren(value).OfType<DependencyObject>()) foreach (var nested in Walk(child)) yield return nested;
    }
    private static string Text(DependencyObject value) { return String.Join("\n",Walk(value).OfType<TextBlock>().Select(item => item.Text)); }

    [STAThread]
    public static int Main(string[] arguments)
    {
        try {
            renderDirectory = arguments.Length == 0 ? null : Path.GetFullPath(arguments[0]);
            CheckRollingWindow(); CheckFreshness(); CheckAmounts(); CheckSortAndManual(); CheckZones(); CheckProjectionIsolation(); CheckUi();
            Console.WriteLine("Dashboard timeline tests passed: " + assertions + " assertions. Isolated fixture; no provider requests, real accounts, or settings writes."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckRollingWindow()
    {
        var startOfEighth = new DateTimeOffset(2026,10,20,0,0,0,TimeSpan.FromHours(9));
        var view = View("codex",Row("past",Now.AddTicks(-1),50),Row("now",Now,50),Row("future",Now.AddTicks(1),0),
            Row("tomorrow",Now.AddHours(2),50),Row("seventh",startOfEighth.AddTicks(-1),50),Row("eighth",startOfEighth,50),
            Row("six days 23 hours",Now.AddHours(167),50),Row("exactly seven days",Now.AddDays(7),50),
            Row("after seven days",Now.AddDays(7).AddTicks(1),50),Row("undated",null,50));
        var schedule = Project(view);
        Check(schedule.Items.Count == 6,"The next 168 hours include the eighth local calendar date when it is within seven days");
        Check(schedule.Items.Select(item => item.Metric).SequenceEqual(new[] { "future","tomorrow","seventh","eighth","six days 23 hours","exactly seven days" }),"Exactly seven days is included but exact now and times beyond the endpoint are excluded");
        Check(schedule.LocalToday == new DateTime(2026,10,13) && schedule.LocalStart == Now && schedule.LocalEnd == Now.AddDays(7),"Displayed range preserves start and end time rather than truncating to midnight");
        Check(schedule.UnavailableRows == 2 && schedule.MissingScheduleRows == 1 && schedule.LaterRows == 1,"Expired, unavailable schedules and later deadlines remain distinguishable");
        Check(schedule.ServiceCount == 1 && schedule.Items.First().RemainingPercent == 0,"An actually exhausted allowance remains valid evidence for a future reset");
        var empty = DashboardResetTimeline.Project(null,Now,Seoul);
        Check(empty.ServiceCount == 0 && empty.Items.Count == 0,"No connection yields a truly empty schedule without invented resets");
        Check(Project(null,new ProviderView { Id = "empty" }).UnavailableRows == 1,"Missing provider rows are unavailable without throwing");
        var reproducedAt = new DateTimeOffset(2026,10,14,21,35,0,TimeSpan.FromHours(9));
        var reported = View("codex",Row("Codex · 주간",new DateTimeOffset(2026,10,21,12,12,0,TimeSpan.FromHours(9)),91));
        reported.LastSuccess = reproducedAt;
        Check(DashboardResetTimeline.Project(new[] { reported },reproducedAt,Seoul).Items.Count == 1,
            "The actual QA case of 6 days 14 hours is shown instead of claiming no reset within seven days");
    }

    private static void CheckFreshness()
    {
        var view = View("codex",Row("current",Now.AddHours(1),25));
        view.Error = "fixture failed"; Check(Project(view).Items.Count == 0,"Cached dates behind a provider failure are not promoted as current schedules");
        view.Error = null; view.LastSuccess = Now.AddMinutes(-11);
        Check(Project(view).UnavailableRows == 1 && Project(view).Items.Count == 0,"Old automatic responses need fresh confirmation");
        view.LastSuccess = Now.AddMinutes(-10); Check(Project(view).Items.Count == 1,"The existing API freshness boundary is preserved");
        view.LastSuccess = Now.AddTicks(1); Check(Project(view).Items.Count == 0,"A future observation timestamp cannot establish freshness");
        view.LastSuccess = Now; view.Source = "code:win:fixture"; view.ReceiptObservedAt = Now; view.ReceiptState = ProviderReceiptState.Received;
        Check(Project(view).Items.Count == 1,"A fresh namespaced Claude Code receipt is available");
        foreach (var state in new[] { ProviderReceiptState.Waiting,ProviderReceiptState.QuotaUnavailable }) {
            view.ReceiptState = state; Check(Project(view).Items.Count == 0,"Unavailable quota receipt is excluded: " + state);
        }
        view.ReceiptState = ProviderReceiptState.ResetWaiting;
        view.Snapshot.Rows.Add(Row("expired short",Now.AddSeconds(-1),40));
        Check(Project(view).Items.Count == 1 && Project(view).UnavailableRows == 1,"A provider-level reset wait does not discard a still-current independent weekly window");
        view.ReceiptObservedAt = Now.AddMinutes(-31); Check(Project(view).Items.Count == 0,"Old Code receipts cannot seed reset predictions");
        view.ReceiptObservedAt = Now; view.Snapshot.Rows[0].Label = "주간 · 수신 대기";
        Check(Project(view).Items.Count == 0,"Row-level receipt waiting placeholders remain unavailable");
    }

    private static void CheckAmounts()
    {
        foreach (double? invalid in new double?[] { null,Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity,-1,101 })
            Check(Project(View("codex",Row("invalid",Now.AddHours(1),invalid))).Items.Count == 0,"Unavailable or invalid percentage is never treated as a current quota: " + invalid);
        var credits = Row("credits",Now.AddDays(1),null); credits.Remaining = 12;
        var dollars = Row("dollars",Now.AddDays(1),null); dollars.Remaining = 3.5;
        var schedule = Project(View("higgsfield",credits),View("higgsfield-api",dollars));
        Check(schedule.Items.Count == 2 && schedule.Items.Sum(item => item.RemainingPercent ?? 0) == 0,"Absolute-unit windows are represented independently without invented percentages");
        Check(schedule.Items.Single(item => item.ServiceId == "higgsfield").Remaining == 12 && schedule.Items.Single(item => item.ServiceId == "higgsfield-api").Remaining == 3.5,"Credits and USD preserve each provider's original unit and value");
        foreach (double invalid in new[] { Double.NaN,Double.NegativeInfinity,Double.PositiveInfinity,-0.1 }) {
            credits.Remaining = invalid; Check(Project(View("higgsfield",credits)).Items.Count == 0,"Invalid absolute balance is excluded: " + invalid);
        }
    }

    private static void CheckSortAndManual()
    {
        var late = View("codex",Row("weekly",Now.AddDays(3),50),Row("short",Now.AddHours(1),60));
        var same = View("claude",Row("second",Now.AddHours(1),70),Row("third",Now.AddHours(1),80));
        var schedule = Project(late,same);
        Check(String.Join(",",schedule.Items.Select(item => item.ServiceId + ":" + item.Metric)) == "claude:second,claude:third,codex:short,codex:weekly","Resets sort by actual instant, service, then stable metric order");
        Check(schedule.Items.Count == 4,"Multiple allowances are never merged into one service deadline");
        late.Source = "manual"; late.LastSuccess = Now.AddDays(-20);
        schedule = Project(late); Check(schedule.Items.Count == 2 && schedule.Items.All(item => item.Manual),"Manual schedules remain explicitly manual even when the entry was recorded long ago");
        late.Snapshot.Rows[0].ResetsAt = Now.AddDays(-1); Check(Project(late).Items.Count == 1,"Expired manual schedules do not silently roll forward into a new billing cycle");
    }

    private static void CheckZones()
    {
        var reset = new DateTimeOffset(2026,10,13,15,15,0,TimeSpan.Zero);
        var view = View("codex",Row("zone",reset,50));
        var local = Project(view); var utc = DashboardResetTimeline.Project(new[] { view },Now,TimeZoneInfo.Utc);
        Check(local.Items.Single().LocalReset.Date == new DateTime(2026,10,14) && utc.Items.Single().LocalReset.Date == new DateTime(2026,10,13),"Grouping follows the requested time zone rather than the reset timestamp's original offset");
        Check(local.Items.Single().ResetsAt == utc.Items.Single().ResetsAt,"Time zone presentation never changes the actual scheduled instant");
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var beforeFallBack = new DateTimeOffset(2026,11,1,4,30,0,TimeSpan.Zero);
        var fall = View("codex",Row("first 01:30",beforeFallBack.AddHours(1),50),Row("second 01:30",beforeFallBack.AddHours(2),50)); fall.LastSuccess = beforeFallBack;
        var repeated = DashboardResetTimeline.Project(new[] { fall },beforeFallBack,eastern);
        Check(repeated.Items.Count == 2 && repeated.Items[0].LocalReset.Hour == 1 && repeated.Items[1].LocalReset.Hour == 1,"Repeated clock hours at DST fall-back retain both distinct reset instants");
        Check(repeated.Items[0].LocalReset.Offset != repeated.Items[1].LocalReset.Offset && repeated.Items[0].ResetsAt < repeated.Items[1].ResetsAt,"DST ambiguity is preserved through UTC ordering and offset-bearing timestamps");
        var beforeSpring = new DateTimeOffset(2026,3,8,6,30,0,TimeSpan.Zero);
        fall.LastSuccess = beforeSpring; fall.Snapshot.Rows = new List<UsageRow> { Row("spring",beforeSpring.AddHours(1),50) };
        var spring = DashboardResetTimeline.Project(new[] { fall },beforeSpring,eastern);
        Check(spring.Items.Single().LocalReset.Hour == 3 && spring.LocalToday == new DateTime(2026,3,8),"A skipped DST hour does not create an impossible local reset time");
        foreach (var start in new[] { beforeFallBack,beforeSpring }) {
            fall.LastSuccess = start;
            fall.Snapshot.Rows = new List<UsageRow> { Row("endpoint",start.AddHours(168),50),Row("outside",start.AddHours(168).AddTicks(1),50) };
            var dstWindow = DashboardResetTimeline.Project(new[] { fall },start,eastern);
            Check(dstWindow.Items.Count == 1 && (dstWindow.LocalEnd-dstWindow.LocalStart).TotalHours == 168,
                "DST changes local clock labels without shortening or lengthening the seven-day window");
        }
    }

    private static void CheckProjectionIsolation()
    {
        var row = Row("weekly",Now.AddDays(2),42); var view = View("codex",row); var snapshot = view.Snapshot;
        var projected = Project(view); row.Label = "new label"; row.RemainingPercent = 5;
        Check(projected.Items[0].Metric == "weekly" && projected.Items[0].RemainingPercent == 42,"Projected UI values are immutable snapshots of observations rather than mutable provider-row references");
        Check(Object.ReferenceEquals(view.Snapshot,snapshot) && view.LastSuccess == Now && row.ResetsAt == Now.AddDays(2),"Projection does not replace, retime, persist or modify provider data");
    }

    private static void CheckUi()
    {
        string isolated = Path.Combine(Path.GetTempPath(),"nephylo-timeline-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\timeline-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated,"claude");
        WidgetWindow owner = null;
        try {
            L.Language = "ko"; owner = new WidgetWindow(true);
            Field<DispatcherTimer>(owner,"timer").Stop(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Visible = false;
            Call(owner,"ShowDemo");
            var manual = Field<ProviderView>(owner,"higgsfieldApiView"); manual.Source = "manual";
            manual.Snapshot.Rows[0].ResetsAt = DateTimeOffset.Now.AddDays(1);
            var page = (FrameworkElement)Call(owner,"BuildDashboardTimelinePage");
            var buttons = Walk(page).OfType<Button>().Where(button => AutomationProperties.GetAutomationId(button).StartsWith("dashboard-timeline:",StringComparison.Ordinal)).ToList();
            Check(buttons.Count > 1 && Text(page).Contains("초기화 일정") && !Text(page).Contains("초기화 타임라인"),"The schedule title matches its navigation while retaining multiple actual reset windows");
            Check(buttons.All(button => AutomationProperties.GetName(button).Contains("초기화 예정")) && Text(page).Contains("수동 입력 일정"),"Accessible schedule entries identify future plans while manually supplied dates remain visibly distinct");
            var preview = (FrameworkElement)Call(owner,"BuildDashboardUpcomingResetPreview");
            int count = Walk(preview).OfType<Button>().Count(button => AutomationProperties.GetAutomationId(button).StartsWith("dashboard-timeline:",StringComparison.Ordinal) && AutomationProperties.GetAutomationId(button) != "dashboard-timeline:all");
            Check(count <= 3 && count > 0,"Overview preview shows at most three imminent metrics");
            CheckTimelineActions(owner);
            CheckResponsiveRender(owner);
            string before = Field<string>(owner,"dashboardSelectedService");
            Call(owner,"OpenDashboardTimelineService","not-connected");
            Check(Field<string>(owner,"dashboardSelectedService") == before,"An action captured before a disconnect cannot open unavailable service details");
            Set(owner,"demo",false);
            try {
                page = (FrameworkElement)Call(owner,"BuildDashboardTimelinePage");
                Check(Text(page).Contains("초기화 일정") && Text(page).Contains("Codex"),"Accountless reset schedules show connected provider windows");
                Check(((FrameworkElement)Call(owner,"BuildDashboardUpcomingResetPreview")).Visibility != Visibility.Collapsed,"Accountless use retains its upcoming-reset preview");
                var actionPage = (FrameworkElement)Call(owner,"DashboardTimelineEmpty",new DashboardResetSchedule { ServiceCount = 2, UnavailableRows = 1 });
                var manage = Walk(actionPage).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button)=="dashboard-timeline-manage");
                manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Field<string>(owner,"dashboardPage") == "services" && !Field<bool>(owner,"refreshing"),"Accountless schedule recovery opens service management without starting network work");
            } finally { Set(owner,"demo",true); }
            Check(!owner.IsLoaded && !owner.IsVisible && !Field<DispatcherTimer>(owner,"timer").IsEnabled,"Timeline components never load the actual widget or start a polling loop");
            Check(!Directory.Exists(isolated),"Read-only rendering writes no settings, account or usage files");
        } finally {
            if (owner != null) {
                Set(owner,"demo",true); Field<DispatcherTimer>(owner,"timer").Stop(); owner.Close();
                Field<CodexService>(owner,"codex").Dispose(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null; SettingsStore.StartupRegistrySubKeyOverride = null;
            ClaudeUsageService.ConfigDirectoryOverride = null; L.Language = "ko";
        }
    }

    private static void CheckTimelineActions(WidgetWindow owner)
    {
        string previousPage = Field<string>(owner,"dashboardPage");
        var noServices = (FrameworkElement)Call(owner,"DashboardTimelineEmpty",new DashboardResetSchedule());
        var manage = Walk(noServices).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "dashboard-timeline-manage");
        Check((string)manage.Content == "서비스 연결" && !Walk(noServices).OfType<Button>().Any(button => AutomationProperties.GetAutomationId(button) == "dashboard-timeline-refresh"),"An unconnected empty state offers connection rather than an ineffective refresh");
        var unavailable = (FrameworkElement)Call(owner,"DashboardTimelineEmpty",new DashboardResetSchedule { ServiceCount = 2, UnavailableRows = 2 });
        var refresh = Walk(unavailable).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "dashboard-timeline-refresh");
        Check(refresh.IsEnabled,"An unavailable schedule offers an actionable refresh");
        DateTimeOffset next = Field<DateTimeOffset>(owner,"nextRefresh"); refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!Field<bool>(owner,"refreshing") && Field<DateTimeOffset>(owner,"nextRefresh") == next,"Preview refresh never makes a real provider request or changes polling");
        var providerMissing = (FrameworkElement)Call(owner,"DashboardTimelineEmpty",new DashboardResetSchedule { ServiceCount = 1, MissingScheduleRows = 1 });
        Check(!Walk(providerMissing).OfType<Button>().Any(button => AutomationProperties.GetAutomationId(button) == "dashboard-timeline-refresh"),"A provider that does not offer reset times gets management guidance instead of futile refresh advice");
        Walk(providerMissing).OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Field<string>(owner,"dashboardPage") == "services","The management action leads directly to the existing service manager");
        Set(owner,"dashboardPage","timeline");
        Call(owner,"OpenDashboardTimelineService","codex");
        Check(Field<string>(owner,"dashboardPage") == "detail" && Field<string>(owner,"dashboardReturnPage") == "timeline","Opening a schedule row preserves the schedule as the detail return destination");
        Call(owner,"ReturnFromDashboardService");
        Check(Field<string>(owner,"dashboardPage") == "timeline","Returning from service detail restores the schedule rather than losing context in the service list");
        Set(owner,"dashboardPage",previousPage);
        Set(owner,"refreshing",true);
        try { Check(!((Button)Call(owner,"DashboardTimelineRefreshButton")).IsEnabled,"Refresh is disabled while the existing fetch is in flight"); }
        finally { Set(owner,"refreshing",false); }
        var codex = Field<ProviderView>(owner,"codexView"); string oldError = codex.Error;
        try {
            codex.Error = "fixture failed";
            var partial = (FrameworkElement)Call(owner,"BuildDashboardTimelinePage");
            Check(Text(partial).Contains("일부 일정은 새 사용량 확인이 필요합니다.") && Walk(partial).OfType<Button>().Any(button => AutomationProperties.GetAutomationId(button) == "dashboard-timeline-refresh"),"Partial provider failure remains actionable even while other current schedules are shown");
        } finally { codex.Error = oldError; }
    }

    private static void CheckResponsiveRender(WidgetWindow owner)
    {
        if (renderDirectory != null) Directory.CreateDirectory(renderDirectory);
        foreach (string language in new[] { "ko","en" }) foreach (string theme in new[] { "dark","light" }) {
            L.Language = language;
            Call(owner,"ApplyRequestedTheme",theme);
            foreach (int width in new[] { 500,900 }) {
                var page = (FrameworkElement)Call(owner,"BuildDashboardTimelinePage");
                page.Width = width; page.Resources = WidgetWindow.MakeResources();
                page.Measure(new Size(width,Double.PositiveInfinity));
                page.Arrange(new Rect(0,0,width,page.DesiredSize.Height)); page.UpdateLayout();
                Check(page.DesiredSize.Width <= width && Walk(page).OfType<Button>().All(button => button.ActualWidth <= width),"Timeline rows fit the available width in " + theme + " mode at " + width + " DIP");
                Check(Walk(page).OfType<TextBlock>().All(text => !Double.IsNaN(text.ActualWidth) && !Double.IsNaN(text.ActualHeight)),"Text layout stays finite at " + width + " DIP in " + theme + " mode");
                var entries = Walk(page).OfType<Button>().Where(button => AutomationProperties.GetAutomationId(button).StartsWith("dashboard-timeline:",StringComparison.Ordinal)).ToList();
                Check(entries.All(button => button.ActualHeight >= 64 && ((Border)button.Content).CornerRadius.TopLeft == 0),"Readable schedule rows keep generous hit targets inside shared day groups");
                Check(entries.SelectMany(button => Walk(button).OfType<TextBlock>()).All(text => text.FontSize >= 13
                    && text.ActualHeight + text.Margin.Top + text.Margin.Bottom + 0.5 >= text.DesiredSize.Height),
                    "Schedule text uses the readable scale without vertical clipping in " + language + " at " + width + " DIP");
                Check(entries.All(button => {
                    var line=(Grid)((Border)button.Content).Child;
                    var value=line.Children.OfType<StackPanel>().Last();
                    return Grid.GetRow(value)==(width==500 ? 1 : 0);
                }),"Narrow schedule rows put the balance beneath the identity instead of squeezing long service labels");
                if (language == "en") Check(!System.Text.RegularExpressions.Regex.IsMatch(Text(page),"[가-힣]"),"English timeline contains no untranslated Korean fixture labels");
                if (renderDirectory == null) continue;
                int height = (int)Math.Ceiling(page.ActualHeight);
                var bitmap = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(theme == "dark" ? Color.FromRgb(16,17,18) : Color.FromRgb(248,249,246)),null,new Rect(0,0,width,height));
                bitmap.Render(background); bitmap.Render(page);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(renderDirectory,"timeline-" + language + "-" + theme + "-" + width + ".png"))) encoder.Save(stream);
            }
        }
        L.Language = "ko";
    }
}
