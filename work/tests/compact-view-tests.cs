using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UsageWidget;

internal static class CompactViewTests
{
    private static int checks;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026,10,12,14,0,0,TimeSpan.FromHours(9));

    private static void Check(bool condition, string message)
    { ++checks; if (!condition) throw new Exception(message); }

    private static ProviderView View(string id, params UsageRow[] rows)
    { return new ProviderView { Id = id, Snapshot = new UsageSnapshot { Rows = rows.ToList() }, LastSuccess = Now }; }

    [STAThread]
    public static int Main()
    {
        try {
            CheckSelection(); CheckMissingAndStale(); CheckUnitsAndSources(); CheckDatesAndOverflow(); CheckHiddenWindowPresentation();
            Console.WriteLine("Compact view tests passed: " + checks + " assertions. No live accounts, provider calls, settings writes or visible application windows.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckSelection()
    {
        var shortWindow = new UsageRow { Label = "Codex · 5시간", RemainingPercent = 75, ResetsAt = Now.AddHours(2) };
        var week = new UsageRow { Label = "Codex · 주간", RemainingPercent = 12, ResetsAt = Now.AddDays(4) };
        var credits = new UsageRow { Label = "Codex · 추가 크레딧", Remaining = 9999, RemainingPercent = 0 };
        var spending = new UsageRow { Label = "Codex · 개인 지출 한도", RemainingPercent = 1 };
        var view = View("codex",shortWindow,week,credits,spending);
        var result = CompactUsageSummary.From(view,Now);
        Check(Object.ReferenceEquals(result.Row,week),"Select the most constrained subscription window, excluding credit and spending pools");
        Check(result.Metric == "주간" && result.Value == "12%","The representative value identifies its selected window");
        Check(result.Reset == CompactUsageSummary.ResetText(week.ResetsAt.Value,Now),"Reset date belongs to exactly the chosen quota window");
        Check(view.Snapshot.Rows.Count == 4 && credits.Remaining == 9999 && shortWindow.RemainingPercent == 75,"Projecting compact data never mutates source snapshots");
        week.RemainingPercent = 75;
        Check(Object.ReferenceEquals(CompactUsageSummary.From(view,Now).Row,shortWindow),"Equal quotas preserve provider order rather than moving on each render");
        shortWindow.RemainingPercent = 0;
        Check(CompactUsageSummary.From(view,Now).Value == "0%","Zero quota is meaningful, never treated as missing");
        foreach (string id in new[] { "codex","claude" }) {
            var standard = View(id,new UsageRow { Label = "5시간 한도", RemainingPercent = 50 },new UsageRow { Label = "주간 한도",RemainingPercent = 20 });
            Check(CompactUsageSummary.From(standard,Now).Metric == "주간",id + " canonical labels select weekly quota");
        }
        foreach (var pair in new[] { new[] { "Codex · 5시간","5시간" }, new[] { "5시간 한도","5시간" }, new[] { "주간 한도","주간" }, new[] { "Codex · 2일","2일" }, new[] { "GPT · 90분","90분" }, new[] { "Codex · 기본 한도","한도" } })
            Check(CompactUsageSummary.Period(pair[0]) == pair[1],"Known server period: " + pair[0]);
        foreach (string label in new[] { "추가 크레딧","Codex · 개인 지출 한도","월 예산","신청 5시간 남음","","구독 크레딧" })
            Check(CompactUsageSummary.Period(label) == "","Unrelated labels are not subscription windows: " + label);
    }

    private static void CheckMissingAndStale()
    {
        var empty = new ProviderView { Id = "codex" };
        var result = CompactUsageSummary.From(empty,Now);
        Check(result.Value == "—" && result.Reset == "—" && result.Row == null,"Unobserved values do not become zero or invented reset dates");
        empty.Error = "Connection unavailable";
        Check(CompactUsageSummary.From(empty,Now).Warning,"First connection failure retains a visible warning");
        var view = View("codex",new UsageRow { Label = "Codex · 5시간",RemainingPercent = 33,ResetsAt = Now.AddHours(-1) });
        view.Error = "Offline";
        result = CompactUsageSummary.From(view,Now);
        Check(result.Value == "33%" && result.Warning,"Last known data remains visibly stale after a refresh failure");
        Check(result.Reset == "확인 중","Passing a reset date never fabricates fresh 100 percent quota");
        Check(view.Snapshot.Rows[0].RemainingPercent == 33,"Expired display does not write a synthetic reset into history");
        var claude = View("claude",new UsageRow { Label = "5시간 한도 · 수신 대기" },new UsageRow { Label = "주간 한도",RemainingPercent = 42,ResetsAt = Now.AddDays(1) });
        result = CompactUsageSummary.From(claude,Now);
        Check(result.Value == "42%" && result.Metric == "주간" && result.Warning,"A valid Claude window does not hide another window awaiting receipt");
        var codex = View("codex",new UsageRow { Label = "Codex · 5시간",RemainingPercent = 80,ResetsAt = Now.AddMinutes(-1) },new UsageRow { Label = "Codex · 주간",RemainingPercent = 42,ResetsAt = Now.AddDays(1) });
        result = CompactUsageSummary.From(codex,Now);
        Check(result.Value == "42%" && result.Metric == "주간" && result.Warning && result.Reset == CompactUsageSummary.ResetText(Now.AddDays(1),Now),"An expired nonrepresentative Codex window keeps its warning without changing the selected reset date");
        var restriction = View("codex",new UsageRow { Label = "주간",RemainingPercent = 78 }); restriction.Snapshot.Note = "포함 사용량 이용 제한 상태";
        Check(CompactUsageSummary.From(restriction,Now).Warning,"Provider restriction remains visible despite a positive quota value");
        foreach (double value in new[] { Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity,-1,101 }) {
            var invalid = View("codex",new UsageRow { Label = "5시간",RemainingPercent = value },new UsageRow { Label = "주간",RemainingPercent = 44 });
            Check(CompactUsageSummary.From(invalid,Now).Value == "44%","Invalid percentage cannot win quota selection");
            Check(CompactUsageSummary.ValueText(new UsageRow { RemainingPercent = value }) == "—","Invalid standalone percentages are missing rather than displayed");
        }
        var unavailable = new ProviderView { Id = "claude", Source = "code", ReceiptState = ProviderReceiptState.QuotaUnavailable, ReceiptObservedAt = Now, ReceiptMessage = "Free / API: no subscription quota" };
        result = CompactUsageSummary.From(unavailable,Now);
        Check(result.Warning && result.Value == "한도 미제공" && result.StateLabel == "수신됨 · 한도 미제공" && result.StateMessage.Contains("no subscription quota") && result.ObservedAt == Now,"A received unsupported quota retains its visible reason and receipt time without pretending the connection failed");
        unavailable.Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label = "5시간 한도",RemainingPercent = 61,ResetsAt = Now.AddHours(2) } } };
        unavailable.ReceiptState = ProviderReceiptState.Received; unavailable.ReceiptMessage = ""; unavailable.LastSuccess = Now;
        Check(!CompactUsageSummary.From(unavailable,Now).Warning,"A fresh valid receipt clears the previous unavailable warning");
        unavailable.ReceiptObservedAt = Now.AddMinutes(-31);
        Check(CompactUsageSummary.From(unavailable,Now).Warning && CompactUsageSummary.From(unavailable,Now).StateLabel == "이전 수신","Old Claude data remains visibly distinguishable before its reset deadline");
    }

    private static void CheckUnitsAndSources()
    {
        var budget = View("openai",new UsageRow { Label = "설정 예산 잔여",DisplayValue = "$38.25",RemainingPercent = 76.5 });
        var result = CompactUsageSummary.From(budget,Now);
        Check(result.Value == "$38.25" && result.Metric == "예산","A configured dollar budget keeps dollars and budget meaning");
        var costs = View("openai",new UsageRow { Label = "이번 달 비용",DisplayValue = "$11.75" });
        Check(CompactUsageSummary.From(costs,Now).Metric == "비용","Actual cost is never presented as a remaining balance");
        var higgs = View("higgsfield",new UsageRow { Label = "웹 잔여 크레딧",DisplayValue = "120.5",Remaining = 120.5 });
        Check(CompactUsageSummary.From(higgs,Now).Value == "120.5 cr","Higgsfield credits retain amount and unit");
        var dollars = View("higgsfield-api",new UsageRow { Label = "API 달러 잔액 · 수동",DisplayValue = "$16.25",Remaining = 16.25 });
        result = CompactUsageSummary.From(dollars,Now);
        Check(result.Value == "$16.25" && result.Metric == "수동","Manual API balance retains dollars and manual marker");
        var manual = View("claude",new UsageRow { Label = "내 기록",RemainingPercent = 51 }); manual.Source = "manual";
        result = CompactUsageSummary.From(manual,Now);
        Check(result.Value == "51%" && result.Metric == "수동","Claude fallback manual data cannot look like a live response");
        Check(result.Reset == "—","Missing manually-entered reset is left unknown");
        var eleven = View("eleven",new UsageRow { Label = "구독 크레딧",RemainingPercent = 93.6,Remaining = 36614 });
        Check(CompactUsageSummary.From(eleven,Now).Value == "93.6%","Credit subscriptions with provided percentage retain that percentage");
        Check(CompactUsageSummary.ValueText(new UsageRow { Detail = "무제한 크레딧" }) == "무제한","Unlimited remains explicit");
        var hugeBalance = new UsageRow { Label = "API 달러 잔액 · 수동",Remaining = 123456789,DisplayValue = "$123,456,789.00" };
        Check(CompactUsageSummary.NarrowValue("higgsfield-api",hugeBalance,hugeBalance.DisplayValue) == "$123.5M","Narrow balances retain currency while their full amount remains in the source");
        var hugeCredits = new UsageRow { Label = "웹 잔여 크레딧",Remaining = 123456789,DisplayValue = "123456789" };
        Check(CompactUsageSummary.NarrowValue("higgsfield",hugeCredits,"123456789 cr") == "123.5M cr","Credit abbreviations retain their unit");
        var custom = new UsageRow { Label = "Custom provider status",DisplayValue = "123456789 transactions pending",Remaining = 123456789 };
        Check(CompactUsageSummary.NarrowValue("custom",custom,custom.DisplayValue) == custom.DisplayValue,"Custom display strings are not parsed or replaced with an unrelated amount");
    }

    private static void CheckDatesAndOverflow()
    {
        Check(CompactUsageSummary.ResetText(Now,Now) == "확인 중","Reset exactly now awaits a provider confirmation");
        Check(CompactUsageSummary.ResetText(Now.AddMinutes(-1),Now) == "확인 중","Past dates are not negative countdowns");
        var localNow = DateTimeOffset.Now;
        var sameDay = new DateTimeOffset(localNow.ToLocalTime().Date.AddHours(23).AddMinutes(59),localNow.ToLocalTime().Offset);
        if (sameDay > localNow) Check(CompactUsageSummary.ResetText(sameDay,localNow) == "23:59","Today's reset is a local short time");
        var tomorrow = Now.AddDays(1);
        Check(CompactUsageSummary.ResetText(tomorrow,Now) == tomorrow.ToLocalTime().ToString("MM/dd",CultureInfo.InvariantCulture),"Later reset shows local month and day");
        var nextYear = Now.AddYears(1);
        Check(CompactUsageSummary.ResetText(nextYear,Now) == nextYear.ToLocalTime().ToString("yy/MM/dd",CultureInfo.InvariantCulture),"A reset next year retains year context");
        Check(CompactUsageSummary.VisibleCount(new double[0],100,40) == 0,"An empty service list has no phantom service");
        Check(CompactUsageSummary.VisibleCount(new[] { 120.0,130.0 },250,40) == 2,"Exactly fitting services do not reserve unnecessary overflow width");
        Check(CompactUsageSummary.VisibleCount(new[] { 120.0,130.0,150.0 },300,40) == 2,"Overflow retains ordered leading cells while reserving +N");
        Check(CompactUsageSummary.VisibleCount(new[] { 120.0,130.0 },150,40) == 1,"Narrow widths keep one reduced service plus the overflow control");
        Check(CompactUsageSummary.VisibleCount(new[] { 120.0,130.0 },160,40) == 1,"Exact cell-plus-overflow boundary is stable");
        Check(CompactUsageSummary.VisibleCount(new[] { 120.0 },0,40) == 0,"No measured width falls back safely to overflow");
    }

    private static T Field<T>(WidgetWindow window, string name)
    { return (T)typeof(WidgetWindow).GetField(name,BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window); }

    private static object Invoke(WidgetWindow window, string name, params object[] args)
    {
        try { return typeof(WidgetWindow).GetMethod(name,BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window,args); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    private static IEnumerable<TextBlock> TextBlocks(DependencyObject parent)
    {
        var text = parent as TextBlock; if (text != null) yield return text;
        foreach (var item in LogicalTreeHelper.GetChildren(parent)) {
            var child = item as DependencyObject; if (child == null) continue;
            foreach (var found in TextBlocks(child)) yield return found;
        }
    }

    private static void ArrangeBar(WidgetWindow window, Grid root, double width)
    {
        root.Measure(new Size(width,44)); root.Arrange(new Rect(0,0,width,44));
        Invoke(window,"RefreshCompactBar");
        root.Measure(new Size(width,44)); root.Arrange(new Rect(0,0,width,44));
        root.UpdateLayout();
    }

    private static double AvailableServiceWidth(WidgetWindow window, Grid root)
    {
        var group = Field<Grid>(window,"compactControlGroup");
        var grip = root.Children.Cast<FrameworkElement>().Single(element => Grid.GetColumn(element) == 0);
        return root.ActualWidth - grip.ActualWidth - Math.Max(group.ActualWidth,group.DesiredSize.Width);
    }

    private static Rect BoundsIn(FrameworkElement element, FrameworkElement parent)
    { return element.TransformToAncestor(parent).TransformBounds(new Rect(0,0,element.ActualWidth,element.ActualHeight)); }

    private static bool PresentedIn(FrameworkElement element, FrameworkElement parent)
    {
        for (DependencyObject current = element; current != null; current = VisualTreeHelper.GetParent(current)) {
            var framework = current as FrameworkElement;
            if (framework != null && framework.Visibility != Visibility.Visible) return false;
            if (Object.ReferenceEquals(current,parent)) return true;
        }
        return false;
    }

    private static void CheckResponsiveLayout(WidgetWindow window, Grid root)
    {
        Invoke(window,"ShowDemo");
        var settings = Field<WidgetSettings>(window,"settings");
        settings.ServiceOrder = new[] { "codex","eleven","openai","claude","gemini","cursor","higgsfield","higgsfield-api" };
        var cards = Field<Dictionary<string,StackPanel>>(window,"serviceCards");
        foreach (var card in cards.Values) card.Visibility = Visibility.Visible;
        var strip = Field<StackPanel>(window,"compactServiceStrip");
        var group = Field<Grid>(window,"compactControlGroup");
        var controls = Field<StackPanel>(window,"compactControls");
        var overflow = Field<Button>(window,"compactOverflowButton");
        var grip = root.Children.Cast<FrameworkElement>().Single(element => Grid.GetColumn(element) == 0);
        double margins = root.Margin.Left + root.Margin.Right;
        // The responsive breakpoint is the inner bar width. Preserve its outside
        // margins so 329/330/331 exercise both sides of the actual breakpoint.
        double[] widths = { 240,280,329 + margins,330 + margins,331 + margins,420,900,331 + margins,330 + margins,329 + margins,280,240 };
        foreach (string language in new[] { "ko","en" }) {
            L.Language = language; UiL.Refresh();
            double previousWidth = 0; int previousVisible = 0;
            var visibleAtWidth = new Dictionary<double,int>();
            foreach (double width in widths) {
                ArrangeBar(window,root,width);
                string scenario = language + " outer=" + width + " inner=" + root.ActualWidth;
                Rect controlBounds = BoundsIn(group,root);
                Check(Math.Abs(root.ActualWidth - (width-margins)) <= 1 && Math.Abs(root.ActualHeight - 44) <= 0.01,
                    scenario + " remains a single fixed-height bar at the requested width");
                Check(Math.Abs(group.DesiredSize.Width - group.ActualWidth) <= 1
                    && Math.Abs(controls.ActualWidth + controls.Margin.Left + controls.Margin.Right - group.ActualWidth) <= 1,
                    scenario + " reserves the measured action group without stale dimensions after resizing");
                Check(controlBounds.Left >= -0.5 && controlBounds.Right <= root.ActualWidth + 0.5
                    && controlBounds.Top >= -0.5 && controlBounds.Bottom <= 44.5,
                    scenario + " keeps every persistent action inside the minibar");
                var actionButtons = controls.Children.OfType<Button>().ToArray();
                Check(actionButtons.Length == 3 && actionButtons.All(button => button.ActualWidth > 0 && button.ActualHeight > 0
                    && BoundsIn(button,root).Left >= controlBounds.Left - 0.5 && BoundsIn(button,root).Right <= controlBounds.Right + 0.5
                    && BoundsIn(button,root).Top >= -0.5 && BoundsIn(button,root).Bottom <= 44.5),
                    scenario + " keeps refresh, full-view return and more actions reachable");
                var serviceChildren = strip.Children.Cast<FrameworkElement>().ToArray();
                Check(serviceChildren.Length > 0 && !Object.ReferenceEquals(serviceChildren[0],overflow)
                    && serviceChildren.Sum(element => element.ActualWidth) <= AvailableServiceWidth(window,root) + 1,
                    scenario + " retains a real first service and reserves actual grip and control space");
                Check(serviceChildren.All(element => BoundsIn(element,root).Left >= BoundsIn(grip,root).Right - 0.5
                    && BoundsIn(element,root).Right <= controlBounds.Left + 0.5 && BoundsIn(element,root).Top >= -0.5
                    && BoundsIn(element,root).Bottom <= 44.5),
                    scenario + " service and overflow hit targets do not overlap the grip or actions");
                Check(serviceChildren.SelectMany(TextBlocks).Where(text => PresentedIn(text,root)).All(text =>
                    BoundsIn(text,root).Right <= controlBounds.Left + 0.5 && BoundsIn(text,root).Left >= BoundsIn(grip,root).Right - 0.5),
                    scenario + " rendered labels and amounts cannot spill over the persistent actions: controls=" + controlBounds
                    + ", texts=" + String.Join("; ",serviceChildren.SelectMany(TextBlocks).Where(text => PresentedIn(text,root)).Select(text => text.Text + "=" + BoundsIn(text,root))));
                int hidden = Field<List<string>>(window,"compactOverflowIds").Count;
                int visible = serviceChildren.Count(element => !Object.ReferenceEquals(element,overflow));
                Check(visible + hidden == settings.ServiceOrder.Length
                    && (hidden == 0 || serviceChildren.Contains(overflow)),
                    scenario + " accounts for all eight demo services through cells or reachable overflow");
                if (width > previousWidth)
                    Check(visible >= previousVisible,scenario + " widening the bar never hides a previously visible service");
                if (visibleAtWidth.ContainsKey(width))
                    Check(visible == visibleAtWidth[width],scenario + " returning to a prior width restores the same service allocation");
                visibleAtWidth[width] = visible; previousWidth = width; previousVisible = visible;
            }
        }
        CheckResponsiveShortPair(window,root);
    }

    private static void CheckResponsiveShortPair(WidgetWindow window, Grid root)
    {
        // Two short readings fit just below the toolbar breakpoint. Widening
        // the toolbar must not turn this into one reading and a +1 overflow.
        var cards = Field<Dictionary<string,StackPanel>>(window,"serviceCards");
        foreach (var pair in cards) pair.Value.Visibility = pair.Key == "codex" || pair.Key == "eleven" ? Visibility.Visible : Visibility.Collapsed;
        Field<ProviderView>(window,"codexView").Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label = "구독", RemainingPercent = 20 } } };
        Field<ProviderView>(window,"elevenView").Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label = "구독", RemainingPercent = 40 } } };
        var strip = Field<StackPanel>(window,"compactServiceStrip");
        var overflow = Field<Button>(window,"compactOverflowButton");
        var group = Field<Grid>(window,"compactControlGroup");
        double margins = root.Margin.Left + root.Margin.Right;
        foreach (string language in new[] { "ko","en" }) {
            L.Language = language; UiL.Refresh();
            foreach (double innerWidth in new double[] { 329,330,331,335,336,337,336,335,331,330,329 }) {
                ArrangeBar(window,root,innerWidth+margins);
                string scenario = language + " short pair inner=" + innerWidth;
                Check(strip.Children.Count == 2 && !strip.Children.Contains(overflow),
                    scenario + " preserves both already-fitting readings across the action-size breakpoint");
                Check(strip.Children.Cast<FrameworkElement>().All(element => BoundsIn(element,root).Right <= BoundsIn(group,root).Left + 0.5)
                    && strip.Children.Cast<FrameworkElement>().Sum(element => element.ActualWidth) <= AvailableServiceWidth(window,root) + 1,
                    scenario + " reserves the current measured controls through expansion and contraction");
            }
        }
    }

    private static void CheckHiddenWindowPresentation()
    {
        string isolated = Path.Combine(Path.GetTempPath(),"nephylo-compact-tests-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\compact-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated,"claude");
        WidgetWindow window = null;
        try {
            window = new WidgetWindow(true);
            Field<DispatcherTimer>(window,"timer").Stop();
            Field<System.Windows.Forms.NotifyIcon>(window,"tray").Visible = false;
            Invoke(window,"ShowDemo");
            Check(!window.IsLoaded && !window.IsVisible,"Compact presentation fixture never loads or shows a real window");
            var root = Field<Grid>(window,"compactBarRoot"); root.Visibility = Visibility.Visible;
            root.Measure(new Size(420,44)); root.Arrange(new Rect(0,0,420,44));
            Invoke(window,"RefreshCompactBar");
            Check(root.Height == 44 && root.ActualHeight == 44,"The compact renderer measures as one 44-DIP bar");
            var strip = Field<StackPanel>(window,"compactServiceStrip");
            Check(strip.Orientation == Orientation.Horizontal,"The bar uses one horizontal sequence without wrapping");
            var overflow = Field<Button>(window,"compactOverflowButton");
            Check(strip.Children.Contains(overflow),"Multiple connected example services expose an overflow button at constrained width");
            Check(overflow.Content.ToString().StartsWith("+"),"Overflow communicates the number of hidden services");
            Check(AutomationProperties.GetName(overflow).Length > 0,"Overflow has a discoverable keyboard and assistive name");
            var refresh = Field<Button>(window,"compactRefreshButton");
            Check(refresh.ToolTip is ToolTip && AutomationProperties.GetHelpText(refresh).Length > 0,"Refresh details support actual tooltips and keyboard help");
            var footer = Field<TextBlock>(window,"footer"); footer.Text = "선택 저장 확인";
            Invoke(window,"RefreshCompactBar");
            Check(refresh.Content.ToString() == "\uE7BA","A global storage failure is shown through a warning glyph, not color alone");
            Check(AutomationProperties.GetHelpText(refresh).Contains("선택 저장 확인"),"Compact status preserves otherwise hidden storage warnings");
            footer.Text = "";
            foreach (string id in new[] { "codex","eleven","openai","claude","gemini","cursor","higgsfield","higgsfield-api" })
                Field<Dictionary<string,StackPanel>>(window,"serviceCards")[id].Visibility = Visibility.Collapsed;
            Invoke(window,"RefreshCompactBar");
            Check(strip.Children.Count == 1 && Object.ReferenceEquals(strip.Children[0],Field<Button>(window,"compactEmptyButton")),"No connected services exposes a clear connection action");
            var cards = Field<Dictionary<string,StackPanel>>(window,"serviceCards");
            cards["codex"].Visibility = cards["claude"].Visibility = Visibility.Visible;
            var settings = Field<WidgetSettings>(window,"settings"); settings.ServiceOrder = new[] { "codex","claude","eleven","openai","gemini","cursor","higgsfield","higgsfield-api" };
            var codex = Field<ProviderView>(window,"codexView"); codex.Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label = "Codex · 주간",RemainingPercent = 0,ResetsAt = DateTimeOffset.Now.AddDays(3) } } }; codex.LastSuccess = DateTimeOffset.Now;
            var claude = Field<ProviderView>(window,"claudeView"); claude.Source = "code"; claude.Snapshot = null; claude.Error = null; claude.LastSuccess = null;
            claude.ReceiptState = ProviderReceiptState.QuotaUnavailable; claude.ReceiptObservedAt = DateTimeOffset.Now; claude.ReceiptMessage = "한도 미제공 · 수동 기록을 이용하세요.";
            ArrangeBar(window,root,240);
            Check(strip.Children.Count == 2 && !Object.ReferenceEquals(strip.Children[0],overflow) && Object.ReferenceEquals(strip.Children[1],overflow),"A 240-DIP bar retains its first real service plus reachable overflow");
            Check(TextBlocks(strip.Children[0]).Any(text => text.Text == "0%"),"Zero remaining quota stays readable when optional date and period are removed");
            Check(overflow.Content.ToString().Contains("!") && refresh.Content.ToString() == "\uE7BA","A hidden unsupported quota marks both overflow and the global status control");
            var overflowText = new TextBlock { Text = "+7 !", FontFamily = overflow.FontFamily, FontSize = overflow.FontSize }; overflowText.Measure(new Size(Double.PositiveInfinity,32));
            Check(overflowText.DesiredSize.Width + overflow.Padding.Left + overflow.Padding.Right + overflow.BorderThickness.Left + overflow.BorderThickness.Right <= overflow.Width,"The overflow count and warning mark fit together even for seven hidden services");
            Check(AutomationProperties.GetHelpText(overflow).Contains("수신됨 · 한도 미제공"),"The hidden warning's meaning is available without opening each service");
            Check(strip.Children.Cast<FrameworkElement>().Sum(element => element.ActualWidth) <= AvailableServiceWidth(window,root) + 1,"The first service and overflow do not overlap persistent controls at minimum width: root=" + root.ActualWidth + ", strip=" + strip.ActualWidth + ", children=" + String.Join(",",strip.Children.Cast<FrameworkElement>().Select(element => element.ActualWidth.ToString() + "/" + element.Width.ToString() + "/" + element.DesiredSize.Width.ToString())));
            var details = (FrameworkElement)Invoke(window,"BuildCompactDetails","claude",DateTimeOffset.Now);
            var detailTexts = TextBlocks(details).Select(text => text.Text).ToArray();
            Check(detailTexts.Contains("연결 상태") && detailTexts.Contains("사용량") && detailTexts.Any(text => text.Contains("초기화")) && detailTexts.Contains("마지막 수신"),"Clicked details are grouped into state, usage, reset and receipt sections");
            Check(detailTexts.Any(text => text.Contains("수동 기록을 이용하세요")) && !detailTexts.Contains("아직 확인된 사용량이 없습니다."),"Quota-unavailable details retain support guidance and the observed receipt rather than claim nothing arrived");
            settings.ServiceOrder = new[] { "claude","codex","eleven","openai","gemini","cursor","higgsfield","higgsfield-api" };
            foreach (string language in new[] { "ko","en" }) {
                L.Language = language; UiL.Refresh(); ArrangeBar(window,root,240);
                Check(TextBlocks(strip.Children[0]).Any(text => text.Text == (language == "ko" ? "한도 미제공" : "Unavailable")),language + " unsupported Claude quota is distinguished at a glance at minimum width");
            }
            L.Language = "ko"; UiL.Refresh(); settings.ServiceOrder = new[] { "codex","claude","eleven","openai","gemini","cursor","higgsfield","higgsfield-api" };
            claude.Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label = "5시간 한도",RemainingPercent = 80,ResetsAt = DateTimeOffset.Now.AddHours(3) } } }; claude.LastSuccess = DateTimeOffset.Now;
            claude.ReceiptState = ProviderReceiptState.Received; claude.ReceiptObservedAt = DateTimeOffset.Now; claude.ReceiptMessage = "";
            ArrangeBar(window,root,240);
            Check(!overflow.Content.ToString().Contains("!") && refresh.Content.ToString() == "\uE72C","A fresh valid receipt clears hidden and global warning markers immediately");
            cards["claude"].Visibility = Visibility.Collapsed;
            ArrangeBar(window,root,240);
            Check(strip.Children.Count == 1 && !Object.ReferenceEquals(strip.Children[0],overflow) && TextBlocks(strip.Children[0]).Any(text => text.Text == "0%"),"A single service never turns into a bare +1 button at 240 DIP");
            cards["codex"].Visibility = Visibility.Collapsed; cards["higgsfield"].Visibility = Visibility.Visible; cards["claude"].Visibility = Visibility.Visible;
            settings.ServiceOrder = new[] { "higgsfield","claude","codex","eleven","openai","gemini","cursor","higgsfield-api" };
            var huge = Field<ProviderView>(window,"higgsfieldView"); huge.LastSuccess = DateTimeOffset.Now;
            huge.Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label = "웹 잔여 크레딧",Remaining = 123456789.123456789,DisplayValue = "123456789.123456789012345678901234567890123456789" } } };
            foreach (string language in new[] { "ko","en" }) {
                L.Language = language; UiL.Refresh(); ArrangeBar(window,root,240);
                var first = (FrameworkElement)strip.Children[0];
                Check(!Object.ReferenceEquals(first,overflow) && TextBlocks(first).Any(text => text.Text == "123.5M cr"),language + " very long credit values remain visible with their unit at minimum width: width=" + first.Width + "/" + first.ActualWidth + ", root=" + root.ActualWidth + " " + String.Join(" / ",TextBlocks(first).Select(text => text.Text + ":" + text.DesiredSize.Width + ":" + text.Visibility)));
                Check(AutomationProperties.GetHelpText(first).Contains("123456789.123456789012345678901234567890123456789"),language + " full precise values remain available through accessible details");
                Check(TextBlocks(first).Where(text => text.Visibility == Visibility.Visible).All(text => text.FontSize >= 12),language + " compact support text remains at least 12 DIP");
                Check(strip.Children.Cast<FrameworkElement>().Sum(element => element.ActualWidth) <= AvailableServiceWidth(window,root) + 1,language + " long values do not displace overflow or persistent controls");
            }
            cards["higgsfield"].Visibility = Visibility.Collapsed; cards["openai"].Visibility = Visibility.Visible;
            settings.ServiceOrder = new[] { "openai","claude","codex","eleven","gemini","cursor","higgsfield","higgsfield-api" };
            var openai = Field<ProviderView>(window,"openAiView"); openai.LastSuccess = DateTimeOffset.Now;
            openai.Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label = "이번 달 순비용",DisplayValue = "$-999.99" } } };
            L.Language = "en"; UiL.Refresh(); ArrangeBar(window,root,240);
            var negativeAmount = TextBlocks(strip.Children[0]).First(text => text.Text == "$-999.99");
            var naturalAmount = new FormattedText(negativeAmount.Text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(negativeAmount.FontFamily,negativeAmount.FontStyle,negativeAmount.FontWeight,negativeAmount.FontStretch),negativeAmount.FontSize,Brushes.Black,VisualTreeHelper.GetDpi(negativeAmount).PixelsPerDip);
            Check(negativeAmount.ActualWidth + 1 >= naturalAmount.WidthIncludingTrailingWhitespace && TextBlocks(strip.Children[0]).Any(text => text.Text == "Cost"),"English negative costs below abbreviation threshold retain the full currency value and cost meaning");
            L.Language = "ko"; UiL.Refresh();
            CheckResponsiveLayout(window,root);
            Check(!Directory.Exists(isolated),"Rendering and calculating compact state does not create settings or credentials");
        }
        finally {
            if (window != null) {
                Field<DispatcherTimer>(window,"timer").Stop(); window.Close();
                Field<CodexService>(window,"codex").Dispose();
                Field<System.Windows.Forms.NotifyIcon>(window,"tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null; SettingsStore.StartupRegistrySubKeyOverride = null; ClaudeUsageService.ConfigDirectoryOverride = null;
            L.Language = "ko";
        }
    }
}
