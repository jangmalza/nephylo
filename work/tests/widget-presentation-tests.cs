using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static class WidgetPresentationTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); checks++; }
    private static object Get(object target, string field) { return target.GetType().GetField(field,Flags).GetValue(target); }
    private static void Set(object target, string field, object value) { target.GetType().GetField(field,Flags).SetValue(target,value); }
    private static object Call(object target, string method, params object[] args) { return target.GetType().GetMethod(method,Flags).Invoke(target,args); }
    private static string PlanText(TextBlock label) { return String.Concat(label.Inlines.OfType<Run>().Select(run=>run.Text)); }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        { throw new InvalidOperationException("Presentation tests forbid network."); }
    }
    // Observe requests to open without creating a popup or changing foreground
    // input. This catches the former focus handler even in a hidden fixture.
    private sealed class HiddenTooltip : ToolTip
    {
        internal bool RequestedOpen;
        static HiddenTooltip()
        {
            IsOpenProperty.OverrideMetadata(typeof(HiddenTooltip),new FrameworkPropertyMetadata(false,null,(target,value) => {
                ((HiddenTooltip)target).RequestedOpen = (bool)value; return false;
            }));
        }
    }
    [STAThread]
    public static int Main()
    {
        string parent=Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        string root=Path.Combine(parent,"widget-presentation-fixture-"+Guid.NewGuid().ToString("N"));
        WidgetWindow window=null; MembershipClient member=null;
        try {
            SettingsStore.DataDirectoryOverride=root; ClaudeUsageService.ConfigDirectoryOverride=Path.Combine(root,"claude");
            member=new MembershipClient(root,false,MembershipConfiguration.FromValues("https://fixture.supabase.co","sb_publishable_fixture_only",true),new NoNetwork(),()=>0L);
            window=new WidgetWindow(true,member);
            ((DispatcherTimer)Get(window,"timer")).Stop(); ((Forms.NotifyIcon)Get(window,"tray")).Visible=false;
            Set(window,"demo",false);
            Check(!window.IsVisible && !window.IsLoaded,"Fixture remains hidden without startup side effects.");
            WidgetPalette.SetTheme("dark");
            var plan=(TextBlock)Call(window,"CreatePlanLabel");
            Check(PlanText(plan)=="모든 기능 무료" && plan.Foreground is SolidColorBrush && plan.Inlines.OfType<Run>().All(run=>run.Foreground is SolidColorBrush),"Accountless access has one monochrome free-access label.");
            Check(plan.Focusable && plan.ToolTip is ToolTip,"Plan information supports keyboard focus and tooltip access.");
            Set(member,"userId","fixture-user"); Set(member,"verifiedPro",true); Set(member,"entitlementDeadline",100000L); Set(member,"subscriptionDeadline",100000L);
            Call(window,"UpdatePlanLabel",plan);
            var dark=(SolidColorBrush)plan.Inlines.OfType<Run>().Last().Foreground;
            Check(PlanText(plan)=="모든 기능 무료" && !dark.HasAnimatedProperties,"A legacy entitlement cannot reintroduce a paid label or gradient.");
            L.Language="en"; Call(window,"UpdatePlanLabel",plan);
            Check(PlanText(plan)=="All features free","The free-access label localizes to English.");
            L.Language="ko"; Call(window,"UpdatePlanLabel",plan);
            Check(plan.Inlines.OfType<Run>().Count()==1 && PlanText(plan)=="모든 기능 무료","Language round trips preserve the single free-access inline.");
            plan.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var darkColor=dark.Color;
            WidgetPalette.SetTheme("light");
            var light=(SolidColorBrush)plan.Inlines.OfType<Run>().Last().Foreground;
            Check(light.Color!=darkColor,"An open free-access label follows light-theme preview.");
            plan.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            var detachedInline=plan.Inlines.OfType<Run>().Single();
            WidgetPalette.SetTheme("dark");
            Check(Object.ReferenceEquals(detachedInline,plan.Inlines.OfType<Run>().Single()),"Detached labels unsubscribe from palette callbacks while shared brushes remain theme-aware.");
            var area=(Grid)Call(window,"BuildWidgetStatusFooter");
            var warning=(TextBlock)Get(window,"footer");
            area.Measure(new Size(300,Double.PositiveInfinity));
            Check(area.Children.Count==1 && area.Visibility==Visibility.Collapsed && area.DesiredSize.Height==0,
                "An empty widget footer contains no plan label and reserves no height or top margin.");
            Call(window,"ApplyLanguage","en"); Call(window,"ApplyLanguage","ko");
            area.Measure(new Size(300,Double.PositiveInfinity));
            Check(area.Visibility==Visibility.Collapsed && area.DesiredSize.Height==0,
                "Language refresh does not recreate branding or empty footer space.");
            warning.Text="설정 저장 실패 · 폴더 쓰기 권한을 확인해 주세요";
            var refresh=(Button)Get(window,"refreshButton");
            Check(warning.Visibility==Visibility.Visible && area.Visibility==Visibility.Visible && warning.Margin.Left==0
                && Grid.GetColumn(warning)==0 && (string)refresh.Content=="\uE7BA","Direct failures show the full-width footer immediately and update the warning indicator.");
            var compactRefresh=(Button)Get(window,"compactRefreshButton");
            Check((string)compactRefresh.Content=="\uE7BA" && AutomationProperties.GetHelpText(compactRefresh).Contains(warning.Text),"Direct warning writes immediately reach the compact indicator and its details.");
            var popup=(Popup)Call(window,"CreateRefreshDetailsPopup",refresh);
            var popupPanel=(StackPanel)((Border)popup.Child).Child;
            Check(!popup.IsOpen && ((TextBlock)((ScrollViewer)popupPanel.Children[0]).Content).Text.Contains(warning.Text)
                && ((Button)popupPanel.Children[1]).Content.ToString()=="새로고침","Warning details retain the failure and a separate refresh action without opening a live popup.");
            warning.Text="";
            area.Measure(new Size(300,Double.PositiveInfinity));
            Check(warning.Visibility==Visibility.Collapsed && area.Visibility==Visibility.Collapsed && area.DesiredSize.Height==0,
                "Clearing a warning also removes its container spacing.");
            var cards=(Dictionary<string,StackPanel>)Get(window,"serviceCards");
            foreach(var pair in cards) pair.Value.Visibility=new[]{"codex","eleven","claude","gemini"}.Contains(pair.Key)?Visibility.Visible:Visibility.Collapsed;
            DateTimeOffset now=DateTimeOffset.UtcNow;
            var codex=(ProviderView)Get(window,"codexView"); codex.LastSuccess=now.AddMinutes(-2); codex.Error="연결 확인 필요"; codex.RefreshSchedule.Failed(now,60,now.AddMinutes(5));
            var eleven=(ProviderView)Get(window,"elevenView"); eleven.LastSuccess=now;
            var claude=(ProviderView)Get(window,"claudeView"); claude.Source="code"; claude.ReceiptState=ProviderReceiptState.QuotaUnavailable; claude.ReceiptObservedAt=now.AddMinutes(-1);
            var manual=(ProviderView)Get(window,"geminiView"); manual.Source="manual"; manual.LastSuccess=now.AddHours(-1);
            Set(window,"planStorageError","선택 저장 확인"); Set(window,"usageTrackingError","이력 저장 확인"); Set(member,"lastError","인증 서버에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요.");
            string tooltip=(string)Call(window,"StatusTooltip");
            Check(tooltip.Contains("Codex · CLI") && tooltip.Contains("ElevenLabs · API"),"Automatic sources identify CLI and API separately.");
            Check(tooltip.Contains("Claude 응답 시 갱신") && tooltip.Contains("마지막 수신") && tooltip.Contains("수동 기록 · 자동 갱신 안 됨"),"Receipt and manual sources explain their own update behavior.");
            Check(tooltip.Contains("마지막 확인:") && tooltip.Contains("다음 재시도"),"Tooltip retains last check and server-respecting retry times.");
            Check(tooltip.Contains("선택 저장 확인") && tooltip.Contains("이력 저장 확인") && !tooltip.Contains(member.LastError),"Storage failures remain visible while obsolete account errors cannot affect free use.");
            Call(window,"UpdateRefreshIndicator");
            Check(AutomationProperties.GetHelpText(refresh)==tooltip && ToolTipService.GetShowOnDisabled(refresh),"Refresh exposes status to assistive technology and while busy-disabled.");
            CheckTooltipFocusLifecycle(new FrameworkElement[] { codex.FoldButton,refresh,compactRefresh,plan });
            ((WidgetSettings)Get(window,"settings")).AnimationsEnabled=false;
            Set(window,"refreshing",true); Call(window,"UpdateRefreshIndicator");
            Check((string)refresh.Content=="\uE72C" && !((RotateTransform)refresh.RenderTransform).HasAnimatedProperties,"Busy status uses the refresh glyph and respects disabled motion.");
            Set(window,"refreshing",false);
            L.Language="en";
            tooltip=(string)Call(window,"StatusTooltip");
            Check(tooltip.Contains("Updates after Claude responses") && tooltip.Contains("Manual · No automatic updates") && tooltip.Contains("Next retry at"),"Source modes and retry details localize to English.");
            L.Language="ko";
            CheckReceiptTransitions(window);
            Set(member,"verifiedPro",false); Call(window,"UpdatePlanLabel",plan);
            Check(PlanText(plan)=="모든 기능 무료" && plan.Foreground is SolidColorBrush && plan.Inlines.OfType<Run>().All(run=>run.Foreground is SolidColorBrush),"Expired legacy entitlement leaves the same free access presentation.");
            Console.WriteLine("Widget presentation tests passed: "+checks+" assertions. Hidden fixture only; no network or user settings accessed.");
            return 0;
        } catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if(window!=null) { Set(window,"demo",true); ((DispatcherTimer)Get(window,"timer")).Stop(); window.Close(); }
            if(member!=null) member.Dispose();
            SettingsStore.DataDirectoryOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null; L.Language="ko";
            string full=Path.GetFullPath(root);
            if(full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("widget-presentation-fixture-",StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full,true);
        }
    }

    private static void CheckTooltipFocusLifecycle(IEnumerable<FrameworkElement> controls)
    {
        foreach (var control in controls) {
            object original=control.ToolTip;
            try {
                var probe=new HiddenTooltip { Content="Fixture status" }; control.ToolTip=probe;
                control.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,Environment.TickCount,null,control) {
                    RoutedEvent=Keyboard.GotKeyboardFocusEvent });
                Check(!probe.RequestedOpen && !probe.IsOpen,"Acquiring focus alone never pins a manually opened tooltip: "+control.GetType().Name);
                Check(ToolTipService.GetShowsToolTipOnKeyboardFocus(control)==true,"WPF retains keyboard-navigation tooltip access: "+control.GetType().Name);
                Check(ToolTipService.GetInitialShowDelay(control)>0,"Pointer hover retains a positive delay instead of opening on focus: "+control.GetType().Name);
                Check(!String.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(control)),"Status help remains accessible without opening the tooltip: "+control.GetType().Name);
                control.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,Environment.TickCount,control,null) {
                    RoutedEvent=Keyboard.LostKeyboardFocusEvent });
                Check(!probe.RequestedOpen,"Moving focus away dismisses the tooltip: "+control.GetType().Name);
            }
            finally { control.ToolTip=original; }
        }
    }

    private static string Quota(DateTimeOffset reset)
    {
        long epoch=(long)(reset.ToUniversalTime()-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds;
        return "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":25,\"resets_at\":"+epoch+"}}}";
    }

    private static void CheckReceiptTransitions(WidgetWindow window)
    {
        var view=(ProviderView)Get(window,"claudeView");
        var extras=(ExtraSettings)Get(window,"extras"); extras.ClaudeManual=new ManualUsageRecord();
        var now=DateTimeOffset.UtcNow;
        Set(window,"claudeConfigured",true);
        Call(window,"RenderClaude");
        var waiting=UsagePresentation.Describe(view,now);
        Check(view.ReceiptState==ProviderReceiptState.Waiting && waiting.Label=="연결 대기" && !waiting.ObservedAt.HasValue,"A missing receipt explicitly waits without inheriting an earlier received timestamp.");
        ClaudeUsageService.CaptureJson("{}",now);
        Call(window,"RenderClaude");
        var unavailable=UsagePresentation.Describe(view,now);
        Check(view.Snapshot==null && view.Error==null && view.ReceiptState==ProviderReceiptState.QuotaUnavailable
            && unavailable.Warning && unavailable.Label=="수신됨 · 한도 미제공" && unavailable.ObservedAt==now,"A valid empty receipt stays distinct from both connection errors and first-receipt waiting.");
        Check(((string)Call(window,"StatusTooltip")).Contains("한도 미제공"),"Global status explains unavailable quota even though snapshot and error are empty.");
        extras.ClaudeManual=new ManualUsageRecord { Label="수동 기록", RemainingPercent=60, ObservedAt=now.AddHours(-4), ResetsAt=now.AddHours(2) };
        Call(window,"RenderClaude");
        var manual=UsagePresentation.Describe(view,now);
        Check(view.Source=="manual" && view.ReceiptState==ProviderReceiptState.None && !view.ReceiptObservedAt.HasValue
            && !manual.Warning && !manual.Stale && manual.ObservedAt==extras.ClaudeManual.ObservedAt,"Manual fallback clears receipt metadata and an older manual entry is not mislabeled as stale automatic usage.");
        extras.ClaudeManual.ResetsAt=now.AddMinutes(-1); Call(window,"RenderClaude");
        manual=UsagePresentation.Describe(view,now);
        Check(manual.Warning && manual.Stale && manual.Label=="수동 · 기한 경과","Manual reset expiry has its own warning instead of a Claude reception warning.");
        extras.ClaudeManual=new ManualUsageRecord(); Set(window,"claudeConfigured",false);
        Call(window,"RenderClaude");
        Check(view.ReceiptState==ProviderReceiptState.Waiting && !UsagePresentation.Describe(view,now).ObservedAt.HasValue,"Disconnect-to-empty removes the old manual or receipt time.");
        Set(window,"claudeConfigured",true);
        ClaudeUsageService.CaptureJson(Quota(now.AddHours(1)),now); Call(window,"RenderClaude");
        var received=UsagePresentation.Describe(view,now);
        Check(view.ReceiptState==ProviderReceiptState.Received && !received.Warning && !received.Stale && received.Label=="CODE 연결","Fresh quota clears unavailable and waiting states.");
        ClaudeUsageService.CaptureJson(Quota(now.AddHours(1)),now.AddMinutes(-31)); Call(window,"RenderClaude");
        var old=UsagePresentation.Describe(view,now);
        Check(old.Warning && old.Stale && old.Label=="이전 수신" && view.Badge.Text==old.Label,"Normal and shared status agree that a Claude receipt older than 30 minutes is stale.");
        ClaudeUsageService.CaptureJson(Quota(now.AddSeconds(-1)),now.AddMinutes(-1)); Call(window,"RenderClaude");
        var reset=UsagePresentation.Describe(view,now);
        Check(view.ReceiptState==ProviderReceiptState.ResetWaiting && reset.Warning && reset.Stale && reset.Label=="초기화 후 수신 대기"
            && !reset.Message.Contains("Free"),"Expired Claude limits remain reset-waiting rather than unsupported-plan or empty-connection claims.");
        ClaudeUsageService.CaptureJson(Quota(now.AddHours(1)),now); Call(window,"RenderClaude");
        Check(!UsagePresentation.Describe(view,now).Warning,"A later fresh response removes reset-waiting warnings.");
        view.Snapshot.Note="구독 제공량 이용 제한";
        var restricted=UsagePresentation.Describe(view,now);
        Check(restricted.Warning && restricted.Message.Contains("이용 제한") && ((string)Call(window,"StatusTooltip")).Contains("이용 제한"),"Claude restriction notes remain visible in shared global status even on a fresh receipt.");
        ((DispatcherTimer)Get(window,"noticeTimer")).Stop();
    }
}
