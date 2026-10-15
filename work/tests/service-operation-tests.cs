using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

// Runs the real window/coordinator in isolated storage without showing a window.
// The membership handler must never receive a request; no real credentials,
// providers, browser, operating-system network settings or user data are accessed.
internal static class ServiceOperationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string MemberId = "c8c48918-6d18-43c3-9eee-e23f7fd19246";
    private const string OtherMemberId = "86c30f0c-0ab2-4fc1-9c5f-c51d43eecfd1";
    private static int checks;
    private static T Field<T>(WidgetWindow window, string name) { return (T)typeof(WidgetWindow).GetField(name, Hidden).GetValue(window); }
    private static void Set(WidgetWindow window, string name, object value) { typeof(WidgetWindow).GetField(name, Hidden).SetValue(window, value); }
    private static T Property<T>(WidgetWindow window, string name) { return (T)typeof(WidgetWindow).GetProperty(name, Hidden).GetValue(window, null); }
    private static object Call(WidgetWindow window, string name, params object[] args)
    {
        try { return typeof(WidgetWindow).GetMethod(name, Hidden).Invoke(window, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static Exception Rejected(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException error) { checks++; return error; }
        catch (OperationCanceledException error) { checks++; return error; }
        throw new Exception(message);
    }
    private static bool Reserve(WidgetWindow window, string id, out IDisposable lease)
    {
        object[] args = { id, null };
        bool acquired = (bool)Call(window, "TryReserveServiceConnection", args);
        lease = args[1] as IDisposable;
        return acquired;
    }
    private static void Validate(WidgetWindow window, string id, IDisposable lease) { Call(window, "ThrowIfServiceConnectionChanged", id, lease); }
    private static bool Pending(WidgetWindow window, string id) { return (bool)Call(window, "IsServiceConnectionPending", id); }
    private static Task Wait(WidgetWindow window, string id, CancellationToken token) { return (Task)Call(window, "WaitForServiceRefreshAsync", id, token); }
    private static void PumpUntil(Func<bool> complete, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!complete() && elapsed.Elapsed < TimeSpan.FromSeconds(4)) {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Check(complete(), message);
    }
    private sealed class FixtureServer : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Requests++;
            throw new InvalidOperationException("No membership requests are allowed in accountless coordinator fixtures.");
        }
    }
    private static void CheckReservations(WidgetWindow window)
    {
        IDisposable codex, eleven, duplicate;
        Check(!Property<bool>(window, "HasServiceConnectionWork"), "An idle window has no connection work");
        Check(Reserve(window, "codex", out codex) && codex != null, "Codex connection reserves its provider");
        Check(Pending(window, "codex") && Property<bool>(window, "HasServiceConnectionWork"), "A reserved operation remains visible before its first await");
        Check(!Reserve(window, "codex", out duplicate), "A second click cannot start a duplicate operation for the same provider");
        Check(Reserve(window, "eleven", out eleven), "An unrelated provider can connect while Codex is pending");
        Validate(window, "codex", codex); Validate(window, "eleven", eleven);
        Check(Pending(window, "codex") && Pending(window, "eleven"), "Independent reservations remain active together");
        codex.Dispose(); codex.Dispose();
        Check(!Pending(window, "codex") && Pending(window, "eleven"), "Disposing one lease twice never releases another provider");
        Check(Reserve(window, "codex", out duplicate), "Completed work releases its provider for a later connection");
        duplicate.Dispose(); eleven.Dispose();
        Check(!Property<bool>(window, "HasServiceConnectionWork"), "All completed work leaves the coordinator idle");
    }
    private static void CheckWaits(WidgetWindow window)
    {
        var codexView = Field<ProviderView>(window, "codexView");
        var elevenView = Field<ProviderView>(window, "elevenView");
        IDisposable codex, eleven;
        Check(Reserve(window, "codex", out codex), "Same-provider wait reserves the next connection before waiting");
        codexView.Busy = true; Set(window, "refreshing", true);
        Task codexWait = Wait(window, "codex", CancellationToken.None);
        Check(!codexWait.IsCompleted, "A connection waits for its own outstanding automatic read");
        Check(Reserve(window, "eleven", out eleven), "A global refresh does not reject an unrelated connection");
        elevenView.Busy = false;
        Task elevenWait = Wait(window, "eleven", CancellationToken.None);
        PumpUntil(() => elevenWait.IsCompleted, "An idle unrelated provider proceeds while Codex is still busy");
        elevenWait.GetAwaiter().GetResult();
        Check(codexView.Busy && !codexWait.IsCompleted && Field<bool>(window, "refreshing"), "Unrelated connection never completes or clears the running automatic refresh");
        codexView.Busy = false;
        PumpUntil(() => codexWait.IsCompleted, "A queued connection proceeds after its provider's automatic read completes");
        codexWait.GetAwaiter().GetResult(); Validate(window, "codex", codex);
        codex.Dispose(); eleven.Dispose(); Set(window, "refreshing", false);

        Check(Reserve(window, "codex", out codex), "A cancellable queued operation can be reserved");
        codexView.Busy = true;
        using (var cancellation = new CancellationTokenSource()) {
            Task canceled = Wait(window, "codex", cancellation.Token);
            cancellation.Cancel();
            PumpUntil(() => canceled.IsCompleted, "Explicit cancellation interrupts a same-provider wait");
            Check(Rejected(() => canceled.GetAwaiter().GetResult(), "Canceled wait unexpectedly succeeded") is OperationCanceledException,
                "Cancellation is identified as cancellation rather than provider failure");
        }
        Check(codexView.Busy, "Canceling queued connection does not falsify the existing automatic read's busy state");
        codex.Dispose(); codexView.Busy = false;
        Check(!Pending(window, "codex"), "Canceled operation releases its reservation through normal disposal");
    }
    private static void CheckSelectionFence(WidgetWindow window)
    {
        var plan = Field<ServicePlan>(window, "servicePlan");
        plan.Select("codex");
        IDisposable eleven = null, higgsfield = null;
        Check(Reserve(window, "eleven", out eleven) && Reserve(window, "higgsfield", out higgsfield), "Two accountless providers may verify in parallel");
        Validate(window, "eleven", eleven);
        plan.Select("eleven"); // Simulates the first successful candidate's verified commit.
        Validate(window, "higgsfield", higgsfield);
        Check(plan.IsActive("eleven") && Pending(window,"higgsfield"), "Legacy selection changes do not invalidate an unrelated accountless connection");
        eleven.Dispose(); higgsfield.Dispose();
        Check(Reserve(window, "higgsfield", out higgsfield), "A new explicit attempt captures its own local lifecycle");
        Validate(window, "higgsfield", higgsfield); higgsfield.Dispose();
        plan.Select("codex");
    }
    private static void CheckAccountFence(WidgetWindow window, MembershipClient member, FixtureServer server)
    {
        IDisposable lease;
        Check(Reserve(window, "eleven", out lease), "Accountless operation can begin verification");
        string epoch=Property<string>(window,"ServiceReadingOwner");
        typeof(MembershipClient).GetField("userId",Hidden).SetValue(member,MemberId);
        typeof(MembershipClient).GetField("verifiedPro",Hidden).SetValue(member,true);
        Call(window,"ApplyMembershipState"); Validate(window,"eleven",lease);
        Field<ServicePlan>(window, "servicePlan").Select("higgsfield");
        Validate(window, "eleven", lease);
        typeof(MembershipClient).GetField("userId",Hidden).SetValue(member,OtherMemberId);
        typeof(MembershipClient).GetField("verifiedPro",Hidden).SetValue(member,false);
        Call(window,"ApplyMembershipState"); Validate(window,"eleven",lease);
        Check(Property<string>(window,"ServiceReadingOwner")==epoch && Pending(window,"eleven") && server.Requests==0,
            "Obsolete membership changes cannot cancel a local provider connection or contact authentication");
        Set(window,"localReadingEpoch",Guid.NewGuid().ToString("N"));
        Rejected(()=>Validate(window,"eleven",lease),"An operation from a previous local lifecycle was allowed to save");
        Check(!(bool)Call(window,"CanApplyServiceReading","eleven",epoch),"A stale local lifecycle cannot publish a provider result");
        lease.Dispose();
        Check(Reserve(window,"eleven",out lease),"A new operation captures the current local lifecycle");
        Validate(window,"eleven",lease); lease.Dispose();
        Rejected(()=>Validate(window,"eleven",lease),"A disposed connection lease was accepted again");
        IDisposable unsupported;
        Check(!Reserve(window,"untrusted-service",out unsupported),"Unlimited providers still reject unsupported service identifiers");
    }
    private static void CheckShutdown(WidgetWindow window)
    {
        IDisposable lease;
        Check(Reserve(window, "codex", out lease), "Shutdown fixture has a queued connection");
        Field<ProviderView>(window, "codexView").Busy = true;
        Task waiting = Wait(window, "codex", Property<CancellationToken>(window, "ServiceConnectionsToken"));
        Set(window, "exiting", true);
        PumpUntil(() => waiting.IsCompleted, "App exit interrupts a queued wait rather than leaving it alive");
        Check(Rejected(() => waiting.GetAwaiter().GetResult(), "Wait outlived app shutdown") is OperationCanceledException, "App-exit wait uses the cancellation path");
        Check(Rejected(() => Validate(window, "codex", lease), "Shutdown operation was allowed to save") is OperationCanceledException, "Shutdown cannot commit saved connection state");
        lease.Dispose(); Field<ProviderView>(window, "codexView").Busy = false; Set(window, "exiting", false);
        Check(Reserve(window, "codex", out lease), "An aborted close can retry connection after the exit flag is reset");
        Validate(window, "codex", lease); lease.Dispose();
    }
    [STAThread]
    public static int Main()
    {
        string parent = Path.GetFullPath(Path.GetDirectoryName(typeof(ServiceOperationTests).Assembly.Location));
        string isolated = Path.Combine(parent, "service-operation-data-" + Guid.NewGuid().ToString("N"));
        string previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        SynchronizationContext previousContext = SynchronizationContext.Current;
        WidgetWindow window = null;
        try {
            SettingsStore.DataDirectoryOverride = Path.Combine(isolated, "data");
            SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\service-operation-" + Guid.NewGuid().ToString("N");
            ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated, "claude");
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(isolated, "codex"));
            var server = new FixtureServer();
            var member = new MembershipClient(SettingsStore.DataDirectoryOverride, false,
                MembershipConfiguration.FromValues("https://test.supabase.co", "sb_publishable_fixture_only", false, true), server, () => 0);
            window = new WidgetWindow(false, member);
            Field<DispatcherTimer>(window, "timer").Stop(); Field<Forms.NotifyIcon>(window, "tray").Visible = false;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Check(!window.IsLoaded && !window.IsVisible && server.Requests == 0, "Fixture window starts without any live refresh or visible UI");
            CheckReservations(window); CheckWaits(window); CheckSelectionFence(window);
            CheckAccountFence(window, member, server); CheckShutdown(window);
            Check(!Property<bool>(window, "HasServiceConnectionWork"), "All regression scenarios leave no pending connection");
            Check(!window.IsLoaded && !window.IsVisible && server.Requests == 0, "Accountless fixture never requests membership or shows real UI");
            Console.WriteLine("Service operation tests passed: " + checks + " assertions. Isolated accountless WPF coordinator; zero membership requests.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if (window != null) {
                Field<DispatcherTimer>(window, "timer").Stop(); Set(window, "shutdownFlushed", true); window.Close();
                Field<CodexService>(window, "codex").Dispose(); Field<Forms.NotifyIcon>(window, "tray").Dispose();
            }
            SynchronizationContext.SetSynchronizationContext(previousContext);
            SettingsStore.DataDirectoryOverride = null; SettingsStore.StartupRegistrySubKeyOverride = null; ClaudeUsageService.ConfigDirectoryOverride = null;
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
            string full = Path.GetFullPath(isolated);
            if (full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("service-operation-data-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
