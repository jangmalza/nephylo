using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

// In-process WPF behavior checks. The window is never shown and all reads/writes
// are redirected to test storage. No account, network, login or live UI is used.
internal static class WidgetUiTests
{
    private static int assertions;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static T Field<T>(WidgetWindow window, string name)
    {
        return (T)typeof(WidgetWindow).GetField(name, PrivateInstance).GetValue(window);
    }

    private static void SetField(WidgetWindow window, string name, object value)
    {
        typeof(WidgetWindow).GetField(name, PrivateInstance).SetValue(window, value);
    }

    private static object Call(WidgetWindow window, string name, params object[] args)
    {
        try { return typeof(WidgetWindow).GetMethod(name, PrivateInstance).Invoke(window, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    private static IEnumerable<TextBlock> TextBlocks(DependencyObject parent)
    {
        var text = parent as TextBlock;
        if (text != null) yield return text;
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            var item = child as DependencyObject;
            if (item == null) continue;
            foreach (var found in TextBlocks(item)) yield return found;
        }
    }

    [STAThread]
    public static int Main()
    {
        string parent = Path.GetFullPath(Path.GetDirectoryName(typeof(WidgetUiTests).Assembly.Location));
        string isolated = Path.Combine(parent, "widget-ui-test-data-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\ui-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated, "claude-config");
        WidgetWindow window = null;
        try
        {
            window = new WidgetWindow(true);
            Field<DispatcherTimer>(window, "timer").Stop();
            Field<Forms.NotifyIcon>(window, "tray").Visible = false;
            Call(window, "ShowDemo");
            Check(!window.IsLoaded && !window.IsVisible, "Regression fixture never shows or loads a real window");
            CheckHeaderLayout(window);
            CheckLoginIsolation(window);
            CheckFoldAndCompact(window);
            CheckServiceOrder(window);
            CheckClaudeConnectionState(window);
            CheckAnimatedRowReuse(window);
            CheckFreshnessAndHeight();
            CheckReleaseMetadata();
            Check(!Directory.Exists(isolated), "Demo behavior tests never persist secrets, usage or settings");
            Console.WriteLine("Widget UI tests passed: " + assertions + " assertions. No live accounts, APIs, settings or application windows were used.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Widget UI test failed: " + ex);
            return 1;
        }
        finally
        {
            if (window != null)
            {
                Field<DispatcherTimer>(window, "timer").Stop();
                window.Close();
                Field<CodexService>(window, "codex").Dispose();
                Field<Forms.NotifyIcon>(window, "tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null;
            SettingsStore.StartupRegistrySubKeyOverride = null;
            ClaudeUsageService.ConfigDirectoryOverride = null;
            string full = Path.GetFullPath(isolated);
            if (full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("widget-ui-test-data-", StringComparison.Ordinal) && Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }

    private static void CheckReleaseMetadata()
    {
        string hash = new String('a',64);
        var value = WidgetRelease.Parse("{\"version\":\"1.14.0\",\"file\":\"UsageWidget-v1.14.0-Windows11.zip\",\"sha256\":\"" + hash + "\"}");
        Check(value.version == "1.14.0","Valid deployment metadata reports the published version");
        foreach (string json in new[] { "<html>Sign in</html>", "null", "{\"version\":\"1.14.0\",\"file\":\"https://example.com/other.zip\",\"sha256\":\"" + hash + "\"}", "{\"version\":\"bad\"}" }) {
            bool rejected = false; try { WidgetRelease.Parse(json); } catch { rejected = true; }
            Check(rejected,"Invalid, redirected or cross-origin release data is rejected");
        }
    }

    private static void CheckHeaderLayout(WidgetWindow window)
    {
        Check(window.Title == "Nephylo — 미리보기" && Field<Forms.NotifyIcon>(window,"tray").Text == "Nephylo · AI 사용량","Window and tray expose the new brand");
        var header = Field<Grid>(window,"widgetHeader");
        var title = (Grid)header.Children[0];
        var subtitle = title.Children.OfType<TextBlock>().Single();
        string previousText = subtitle.Text; subtitle.Text = "Preview with a long localized description";
        header.Measure(new Size(242,Double.PositiveInfinity)); header.Arrange(new Rect(0,0,242,header.DesiredSize.Height));
        var toolbar = (Panel)header.Children[1];
        Check(title.Background != null && title.ActualWidth > 0 && title.ActualHeight == toolbar.ActualHeight,"The title area remains a full-height drag surface beside the toolbar");
        Check(toolbar.Children.OfType<Button>().Count() == 4 && title.ActualWidth + toolbar.ActualWidth <= 242.01 && title.ClipToBounds && subtitle.TextWrapping == TextWrapping.NoWrap,"Minimum widget width keeps four toolbar actions accessible without overlapping preview text");
        Check(System.Windows.Automation.AutomationProperties.GetName(title) == "Nephylo 남은 사용량 미리보기","Accessible header carries the new brand");
        subtitle.Text = previousText;
    }

    private static void CheckFreshnessAndHeight()
    {
        var now = DateTimeOffset.Now;
        var manual = new ProviderView { Id = "higgsfield-api", LastSuccess = now.AddDays(-4) };
        string text = UsageFreshness.Footer(new[] { manual },60);
        Check(text.Contains("수동 기록") && text.Contains("자동 갱신 안 됨") && !text.Contains("1분"),"Manual-only usage never advertises scheduled polling");
        var api = new ProviderView { Id = "eleven", LastSuccess = now };
        text = UsageFreshness.Footer(new[] { api,manual },60);
        Check(text.Contains("자동 조회 1분") && text.Contains("수동 기록 1개") && !text.Contains(manual.LastSuccess.Value.ToString("HH:mm")),"An old manual value cannot overwrite automatic freshness");
        var claude = new ProviderView { Id = "claude", Source = "manual", LastSuccess = now };
        Check(UsageFreshness.Footer(new[] { claude },60).Contains("자동 갱신 안 됨"),"Claude manual fallback is not an automatic Code connection");
        claude.Source = "code";
        Check(UsageFreshness.Footer(new[] { claude },60).Contains("응답 시 갱신"),"Claude receipt-based updates have an explicit cadence");
        api.Error = "synthetic failure";
        Check(UsageFreshness.Footer(new[] { api },120).Contains("일부 갱신 실패"),"Failure remains visible alongside schedule");
        Check(UsageFreshness.Stamp(now.AddDays(-1),now).Contains("/"),"Old values include a date, not only a clock time");
        Check(!UsageFreshness.Stamp(now,now).Contains("/"),"Today's card timestamp stays compact");
        Check(WidgetWindow.CalculateContentHeight(120,40,35,260,900)==260,"One short card shrinks to the window minimum");
        Check(WidgetWindow.CalculateContentHeight(450,40,35,260,900)==551,"Multiple cards receive their natural height");
        Check(WidgetWindow.CalculateContentHeight(1800,40,35,260,900)==876,"Many cards are bounded by the monitor work area");
    }

    private static void CheckLoginIsolation(WidgetWindow window)
    {
        var codex = Field<ProviderView>(window, "codexView");
        var eleven = Field<ProviderView>(window, "elevenView");
        var originalCodex = codex.Snapshot;
        var originalCodexTime = codex.LastSuccess;
        Check(eleven.Snapshot != null, "Login regression starts with a previous ElevenLabs result");
        eleven.Error = "previous synthetic failure";
        SetField(window, "loginPending", true);
        SetField(window, "secret", "");
        SetField(window, "openAiKey", "");
        SetField(window, "nextRefresh", DateTimeOffset.MinValue);
        var before = DateTimeOffset.Now;
        var task = (Task)Call(window, "RefreshAsync");
        Check(task.IsCompleted, "Pending login and blank keys complete refresh without external I/O");
        task.GetAwaiter().GetResult();
        Check(eleven.Snapshot == null && eleven.LastSuccess == null && eleven.Error == null && !eleven.Busy,
            "Codex login does not skip ElevenLabs refresh and disconnected state cleanup");
        Check(Object.ReferenceEquals(codex.Snapshot, originalCodex) && codex.LastSuccess == originalCodexTime && !codex.Busy,
            "Pending Codex login retains its previous data without requesting usage");
        Check(Field<bool>(window, "loginPending"), "Other-service refresh cannot clear pending Codex login");
        double scheduledIn = (Field<DateTimeOffset>(window, "nextRefresh") - before).TotalSeconds;
        Check(scheduledIn > 0 && scheduledIn <= 16, "Scheduler continues checking other providers while Codex login is pending");
        Check(!Field<bool>(window, "refreshing") && Field<Button>(window, "refreshButton").IsEnabled,
            "Refresh always releases its busy state and button");
        SetField(window, "loginPending", false);
    }

    private static void CheckFoldAndCompact(WidgetWindow window)
    {
        var view = Field<ProviderView>(window, "codexView");
        var settings = Field<WidgetSettings>(window, "settings");
        view.Snapshot = new UsageSnapshot { Rows = new List<UsageRow> {
            new UsageRow { Label = "주간", RemainingPercent = 0, ResetsAt = DateTimeOffset.Now.AddHours(1) },
            new UsageRow { Label = "추가 크레딧", Remaining = 0 },
            new UsageRow { Label = "알 수 없음" },
            new UsageRow { Label = "예외 한도", Detail = "무제한" },
            new UsageRow { Label = "예산", DisplayValue = "$38.25", RemainingPercent = 76.5, DisplaySubtitle = "synthetic budget details" }
        }};
        Call(window, "Render", view, true);
        Call(window, "ToggleProvider", "codex");
        Check(settings.CollapsedServices.Contains("codex") && view.Body.Visibility == Visibility.Collapsed
            && view.Summary.Visibility == Visibility.Visible, "Folding hides detail and presents the remaining usage summary");
        Check(view.Summary.Text.Contains("주간 0%") && view.Summary.Text.Contains("추가 크레딧 0"), "Collapsed usage preserves actual zero quota and zero credit balance");
        Check(view.Summary.Text.Contains("알 수 없음 —"), "Collapsed unknown usage is not displayed as zero");
        Check(view.Summary.Text.Contains("예외 무제한"), "Collapsed unlimited usage stays distinct from unknown");
        Check(view.Summary.Text.Contains("예산 $38.25"), "Collapsed currency value takes precedence over budget percentage");
        Call(window, "ToggleProvider", "codex");
        Check(!settings.CollapsedServices.Contains("codex") && view.Body.Visibility == Visibility.Visible
            && view.Summary.Visibility == Visibility.Collapsed, "Unfolding restores the detailed usage view");
        Check(TextBlocks(view.Body).Any(text => text.Text == "synthetic budget details"), "Detailed mode includes supplementary usage information");
        Call(window, "SetCompactView", true);
        var compact = TextBlocks(view.Body).Select(text => text.Text).ToArray();
        Check(settings.CompactView && compact.Contains("0%") && compact.Contains("0") && compact.Contains("—") && compact.Contains("무제한") && compact.Contains("$38.25"),
            "Compact mode preserves zero, unknown, unlimited and formatted usage quantities");
        Check(compact.Any(text => text.Contains("후 초기화")) && !compact.Contains("synthetic budget details"), "Compact view retains reset information while omitting supplementary details");
        Call(window, "SetCompactView", false);
        Check(TextBlocks(view.Body).Any(text => text.Text == "synthetic budget details"), "Leaving compact mode restores supplementary details");
    }

    private static void CheckServiceOrder(WidgetWindow window)
    {
        var settings = Field<WidgetSettings>(window, "settings");
        var extras = Field<ExtraSettings>(window, "extras");
        var cards = Field<Dictionary<string, StackPanel>>(window, "serviceCards");
        var dividers = Field<Dictionary<string, Border>>(window, "serviceDividers");
        var stack = Field<StackPanel>(window, "providerStack");
        settings.ServiceOrder = new[] { "openai", "cursor", "eleven", "codex", "claude", "gemini" };
        extras.ShowOpenAi = false;
        Call(window, "UpdateServiceVisibility");
        Check(Object.ReferenceEquals(stack.Children[0], cards["openai"]) && Object.ReferenceEquals(stack.Children[1], cards["cursor"])
            && Object.ReferenceEquals(stack.Children[3], cards["codex"]), "Card order follows the saved preference across services");
        Check(cards["openai"].Visibility == Visibility.Collapsed && dividers["openai"].Visibility == Visibility.Collapsed,
            "A hidden first service leaves no stray separator");
        Check(dividers["cursor"].Visibility == Visibility.Collapsed && dividers["eleven"].Visibility == Visibility.Visible,
            "Separator starts after the first visible reordered service");
        extras.ShowOpenAi = true;
        Call(window, "UpdateServiceVisibility");
        Check(cards["openai"].Visibility == Visibility.Visible && dividers["openai"].Visibility == Visibility.Collapsed
            && dividers["cursor"].Visibility == Visibility.Visible, "Revealing the first service repositions separators correctly");
    }

    private static void CheckAnimatedRowReuse(WidgetWindow window)
    {
        var view = Field<ProviderView>(window,"codexView");
        var settings = Field<WidgetSettings>(window,"settings");
        settings.CompactView = false; settings.CollapsedServices = new string[0]; settings.AnimationsEnabled = false;
        view.Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label = "주간", RemainingPercent = 75 } } };
        Call(window,"Render",view,true);
        var first = view.RowVisuals.Values.Single();
        var meter = first.Meter;
        Call(window,"Render",view,true);
        Check(Object.ReferenceEquals(first,view.RowVisuals.Values.Single()) && Object.ReferenceEquals(meter,first.Meter),
            "Routine refresh reuses quota visuals instead of replaying their arrival");
        Check(first.Number.Text == "75%" && meter.DisplayedPercentage == 75 && !meter.HasAnimatedProperties,
            "Disabled animation shows the exact fetched value immediately");
        settings.CompactView = true; view.Snapshot.Rows[0].RemainingPercent = 0;
        Call(window,"Render",view,true);
        Check(first.Number.Text == "0%" && meter.TargetPercentage == 0 && meter.Parent == null,
            "Compact refresh keeps a hidden meter current without retaining its old row");
        settings.CompactView = false;
        Call(window,"Render",view,true);
        Check(meter.DisplayedPercentage == 0 && meter.Parent != null && TextBlocks(view.Body).Count(text => text.Text == "0%") == 1,
            "Expanding a refreshed compact view preserves zero and does not duplicate text");
        view.Snapshot = null;
        Call(window,"Render",view,true);
        Check(view.RowVisuals.Count == 0 && !first.Number.HasAnimatedProperties && !meter.HasAnimatedProperties,
            "Removing a snapshot releases cached rows and their animation clocks");
    }

    private static void CheckClaudeConnectionState(WidgetWindow window)
    {
        var view = Field<ProviderView>(window, "claudeView");
        var result = new ClaudeUsageResult {
            ObservedAt = DateTimeOffset.Now,
            Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label = "주간 한도", RemainingPercent = 72 } } }
        };
        SetField(window, "claudeConfigured", true);
        Call(window, "RenderClaudeRecord", result);
        Check(view.Badge.Text == "CODE 연결" && view.Body.Opacity == 1, "Recent Claude record reflects a currently attached connection");
        SetField(window, "claudeConfigured", false);
        Call(window, "RenderClaudeRecord", result);
        Check(view.Badge.Text == "연결 해제" && view.Body.Opacity == 1, "Disconnected Claude is explicitly labelled while its history remains readable");
        Check(Object.ReferenceEquals(view.Snapshot, result.Snapshot) && view.LastSuccess == result.ObservedAt
            && TextBlocks(view.Body).Any(text => text.Text.Contains("마지막 수신 기록")), "Disconnect preserves the previous quota while labelling it as history");
        SetField(window, "claudeConfigured", true);
        result.HasExpiredWindows = true;
        Call(window, "RenderClaudeRecord", result);
        Check(view.Badge.Text == "초기화 후 수신 대기" && view.Body.Opacity == 1
            && TextBlocks(view.Body).Any(text => text.Text.Contains("새 응답 후 갱신")), "Expired Claude quota remains readable and distinguishes awaiting reset data from an older reading");
        result.HasExpiredWindows = false;
        result.ObservedAt = DateTimeOffset.Now.AddMinutes(-31);
        Call(window, "RenderClaudeRecord", result);
        Check(view.Badge.Text == "이전 수신", "A Claude record older than thirty minutes is visibly stale");
    }
}
