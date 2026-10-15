using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;

internal static class CompactAutoIntegrationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static readonly string[] All = { "codex", "eleven", "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" };
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name,Private).GetValue(value); }
    private static void Set(object value, string name, object next) { value.GetType().GetField(name,Private).SetValue(value,next); }
    private static object Call(WidgetWindow window, string name, params object[] args)
    {
        try { return typeof(WidgetWindow).GetMethod(name,Private).Invoke(window,args); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("This fixture must never send a network request."); }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Layout(WidgetWindow window)
    {
        var root = Field<Grid>(window,"compactBarRoot");
        root.Visibility = Visibility.Visible;
        root.Measure(new Size(window.Width,44)); root.Arrange(new Rect(0,0,window.Width,44)); root.UpdateLayout();
    }

    private static void Select(WidgetWindow window, params string[] ids)
    {
        var cards = Field<Dictionary<string,StackPanel>>(window,"serviceCards");
        foreach (var pair in cards) pair.Value.Visibility = ids.Contains(pair.Key) ? Visibility.Visible : Visibility.Collapsed;
        Call(window,"ApplyServiceLayout"); Pump(); Layout(window);
        Check(((string[])Call(window,"CompactServiceIds")).SequenceEqual(All.Where(ids.Contains)),"Visible service identities follow the active card set, not all configured services");
    }

    private static void CheckAllocation(WidgetWindow window, int total, string scenario)
    {
        var strip = Field<StackPanel>(window,"compactServiceStrip");
        var overflow = Field<Button>(window,"compactOverflowButton");
        var empty = Field<Button>(window,"compactEmptyButton");
        var hidden = Field<List<string>>(window,"compactOverflowIds");
        int shown = strip.Children.Cast<UIElement>().Count(item => item != overflow && item != empty);
        Check(shown + hidden.Count == total,scenario + ": every active service is visible or reachable in overflow");
        Check(strip.Children.Contains(overflow) == (hidden.Count > 0),scenario + ": +N exists only for hidden services");
        Check(strip.Children.Contains(empty) == (total == 0),scenario + ": empty connection action replaces all service cells");
        var root = Field<Grid>(window,"compactBarRoot");
        var controlGroup = Field<Grid>(window,"compactControlGroup");
        double available = root.ActualWidth - 12 - Math.Max(controlGroup.ActualWidth,controlGroup.DesiredSize.Width);
        Check(strip.Children.Cast<FrameworkElement>().Sum(item => item.ActualWidth) <= available + 1,scenario + ": service cells and +N do not overlap fixed controls");
        Check(window.Height == 44,scenario + ": auto width never changes the 44-DIP height");
    }

    private static void CheckChangingServiceCount(WidgetWindow window)
    {
        var settings = Field<WidgetSettings>(window,"settings");
        settings.CompactView = true; settings.CompactAutoWidth = true;
        Call(window,"ApplyWidgetViewVisibility");
        window.Left = 40; window.Top = 40;
        double savedWidth = settings.Width, savedHeight = settings.Height;
        Select(window,"codex"); double one = window.Width; CheckAllocation(window,1,"One service");
        Select(window,"codex","eleven"); double two = window.Width; CheckAllocation(window,2,"Two services");
        Select(window,"codex","eleven","openai","claude"); double four = window.Width; CheckAllocation(window,4,"Four services");
        Check(two > one && four >= two,"Connecting more services expands the live compact window automatically up to its monitor cap");
        Select(window,"codex");
        Check(window.Width < four && Math.Abs(window.Width - one) <= 1,"Removing services shrinks back to the single-service width");
        Select(window);
        Check(window.Width == 240,"No connected service uses the minimum compact width"); CheckAllocation(window,0,"Empty services");
        Check(settings.Width == savedWidth && settings.Height == savedHeight,"Automatic compact changes preserve saved normal geometry");
    }

    private static void CheckScreenCap(WidgetWindow window)
    {
        Select(window,All);
        var area = new Rect(-800,20,800,600);
        window.Left = -30; window.Top = 55;
        Call(window,"ApplyWidgetWorkArea",area); Layout(window);
        Check(window.Width == 640,"Eight services respect the 80-percent logical monitor width cap");
        Check(area.Contains(new Rect(window.Left,window.Top,window.Width,window.Height)),"A growing minibar stays entirely inside a negative-origin monitor");
        CheckAllocation(window,8,"Capped eight services");
        var hidden = Field<List<string>>(window,"compactOverflowIds");
        Check(hidden.Count > 0 && Field<Button>(window,"compactOverflowButton").Content.ToString().StartsWith("+" + hidden.Count),"The overflow number describes exactly the services exceeding the monitor cap");
        var pending = Field<DispatcherOperation>(window,"widgetGeometryRefresh"); if (pending != null) pending.Abort();
        Set(window,"widgetGeometryRefresh",null);
        Call(window,"ApplyWidgetWorkArea",new Rect(0,0,1920,1040)); Layout(window);
        Check(window.Width > 640 && window.Width <= 1536,"A larger monitor gives the same service set more automatic width");
        CheckAllocation(window,8,"Larger monitor services");
    }

    private static void CheckStableNumbersAndReorder(WidgetWindow window)
    {
        Select(window,"codex");
        var codex = Field<ProviderView>(window,"codexView");
        codex.Snapshot = new UsageSnapshot { Rows = new List<UsageRow> {
            new UsageRow { Label = "주간 한도", RemainingPercent = 100, ResetsAt = DateTimeOffset.Now.AddDays(5) }
        }};
        Call(window,"RefreshCompactBar"); Pump(); Layout(window);
        double largest = window.Width;
        foreach (double value in new double[] { 9,0,55.5,100,2 }) {
            codex.Snapshot.Rows[0].RemainingPercent = value;
            Call(window,"RefreshCompactBar"); Pump(); Layout(window);
            Check(window.Width >= largest,"Ordinary remaining-value refresh does not shrink the minibar at " + value + "%");
            largest = window.Width;
        }
        codex.Snapshot.Rows[0].ResetsAt = DateTimeOffset.Now.AddHours(1);
        Call(window,"RefreshCompactBar"); Pump(); Layout(window);
        Check(window.Width >= largest,"Reset date changing to a short local time does not shrink the current service set");
        Select(window,"codex","eleven");
        double pairWidth = window.Width;
        var settings = Field<WidgetSettings>(window,"settings");
        settings.ServiceOrder = new[] { "eleven", "codex", "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" };
        Call(window,"ApplyServiceLayout"); Pump(); Layout(window);
        Check(Math.Abs(window.Width - pairWidth) <= 1,"Reordering an unchanged service set preserves its fitted width");
        Check(((string[])Call(window,"CompactServiceIds"))[0] == "eleven","Stable width does not prevent the requested service order change");
        settings.ServiceOrder = All; Call(window,"ApplyServiceLayout"); Pump();
    }

    private static void CheckManualAndRestore(WidgetWindow window)
    {
        var settings = Field<WidgetSettings>(window,"settings");
        Select(window,"codex");
        Call(window,"SetCompactAutoWidth",false);
        window.Width = 600; Layout(window);
        Select(window,"codex","eleven","openai","claude");
        Check(!settings.CompactAutoWidth && window.Width == 600,"Manual width remains fixed when new services connect");
        Select(window,"codex");
        Check(window.Width == 600,"Manual width does not shrink when a service is removed");
        Call(window,"SetCompactAutoWidth",true); Pump(); Layout(window);
        Check(settings.CompactAutoWidth && window.Width < 600,"Re-enabling automatic width immediately refits the current single service");
        CheckAllocation(window,1,"Auto restored");
        var row = Field<ProviderView>(window,"codexView").Snapshot.Rows[0];
        row.DisplayValue = "123,456,789,012,345,678";
        Call(window,"RefreshCompactBar"); Pump(); Layout(window);
        double longWidth = window.Width;
        row.DisplayValue = "9%";
        Call(window,"RefreshCompactBar"); Pump(); Layout(window);
        Check(window.Width == longWidth,"Shortening a transient large display value keeps the fitted service width stable");
        Call(window,"SetCompactAutoWidth",false); Call(window,"SetCompactAutoWidth",true); Pump(); Layout(window);
        Check(window.Width < longWidth,"Explicitly re-enabling automatic width clears the previous high-water measurement and fits current content");
        row.DisplayValue = null;
        Call(window,"RefreshCompactBar"); Call(window,"RefreshCompactBar");
        var scheduled = Field<DispatcherOperation>(window,"widgetGeometryRefresh");
        Call(window,"QueueCompactAutoWidth");
        Check(Object.ReferenceEquals(scheduled,Field<DispatcherOperation>(window,"widgetGeometryRefresh")),"Repeated data notifications coalesce into a single geometry reconciliation");
        Pump();
        Check(Field<DispatcherOperation>(window,"widgetGeometryRefresh") == null,"Automatic width reconciliation settles without an endless refresh loop");
    }

    private static void CheckRealVisibilityPolicy(WidgetWindow window, MembershipClient membership)
    {
        var settings = Field<WidgetSettings>(window,"settings");
        // Only pure visibility is invoked while leaving preview mode. The fixture
        // retains in-memory credentials and snapshots; nothing restores or refreshes.
        settings.CodexWslDistribution = "fixture-distribution";
        Set(window,"secret","fixture-eleven-key");
        Set(window,"openAiKey","fixture-openai-key");
        Set(membership,"userId","fixture-user");
        Set(membership,"entitlementDeadline",Int64.MaxValue);
        Set(membership,"subscriptionDeadline",Int64.MaxValue);
        Set(membership,"verifiedPro",true);
        Set(window,"demo",false);
        try {
            Call(window,"UpdateServiceVisibility"); Pump(); Layout(window);
            var proIds = (string[])Call(window,"CompactServiceIds");
            double proWidth = window.Width;
            Check(proIds.Length > 1,"Verified test Pro visibility makes multiple configured services available to auto width");
            CheckAllocation(window,proIds.Length,"Pro active visibility");
            Set(membership,"verifiedPro",false);
            Call(window,"UpdateServiceVisibility"); Pump(); Layout(window);
            var freeIds = (string[])Call(window,"CompactServiceIds");
            Check(freeIds.SequenceEqual(proIds),"Accountless width includes the same configured services after legacy entitlement changes");
            Check(Math.Abs(window.Width-proWidth)<1,"Legacy entitlement changes cannot shrink an accountless minibar");
            CheckAllocation(window,freeIds.Length,"Accountless active visibility");
        } finally { Set(window,"demo",true); }
    }

    [STAThread]
    private static int Main()
    {
        string isolated = Path.Combine(Path.GetTempPath(),"nephylo-compact-auto-" + Guid.NewGuid().ToString("N"));
        string previousDirectory = SettingsStore.DataDirectoryOverride;
        string previousClaude = ClaudeUsageService.ConfigDirectoryOverride;
        string previousStartup = SettingsStore.StartupRegistrySubKeyOverride;
        WidgetWindow window = null;
        var handler = new RejectNetwork();
        var membership = new MembershipClient(isolated,false,
            MembershipConfiguration.FromValues("https://fixture.invalid","sb_publishable_fixtureonly"),handler,() => 0);
        try {
            SettingsStore.DataDirectoryOverride = isolated;
            ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated,"claude");
            SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\compact-auto-" + Guid.NewGuid().ToString("N");
            window = new WidgetWindow(true,membership);
            Field<DispatcherTimer>(window,"timer").Stop(); Field<System.Windows.Forms.NotifyIcon>(window,"tray").Visible = false;
            Call(window,"ShowDemo");
            Check(!window.IsLoaded && !window.IsVisible,"The integration fixture never creates a visible application window");
            CheckChangingServiceCount(window);
            CheckScreenCap(window);
            CheckStableNumbersAndReorder(window);
            CheckManualAndRestore(window);
            CheckRealVisibilityPolicy(window,membership);
            Check(handler.Calls == 0,"No live or mock authentication/provider network requests were sent");
            Check(!Directory.Exists(isolated),"Auto sizing and policy transitions do not create settings, credentials, or usage files");
            Console.WriteLine("Compact auto integration tests passed: " + checks + " assertions. Hidden in-memory WPF fixture; no network, real settings, credentials or visible windows.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine("Compact auto integration tests failed: " + ex); return 1; }
        finally {
            if (window != null) { Set(window,"demo",true); Field<DispatcherTimer>(window,"timer").Stop(); window.Close(); }
            else membership.Dispose();
            SettingsStore.DataDirectoryOverride = previousDirectory; ClaudeUsageService.ConfigDirectoryOverride = previousClaude;
            SettingsStore.StartupRegistrySubKeyOverride = previousStartup;
            L.Language = "ko";
        }
    }
}
