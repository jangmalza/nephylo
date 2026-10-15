using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
using UsageWidget;

internal static class WidgetStartupTests
{
    private static int checks;
    private static void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
    private static void Reject(Action action, string label) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, label); }
    private static ProfileInitializationException Error(string reason) { return new ProfileInitializationException(reason, "safe", "safe"); }
    private static IDictionary<string,object> Receipt(string root) { return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root, ".nephylo-startup.json"))); }

    private static int Main(string[] args)
    {
        // This copied fixture writes only beside itself; it never starts the real
        // app, migrates a user profile, or touches a login or network credential.
        if (args.Length == 2 && args[0] == "--normal-profile-start")
        {
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "probe-ready.txt"), new[] { args[1], WindowsIdentity.GetCurrent().User.Value, Process.GetCurrentProcess().SessionId.ToString() });
            return 0;
        }
        string root = Path.Combine(Path.GetTempPath(), "NephyloStartupTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Run(root); if (Array.IndexOf(args, "--wmi-probe") >= 0) WmiProbe(root); Console.WriteLine("Widget startup tests passed: " + checks); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Run(string root)
    {
        Check(WidgetStartup.ShouldHandoff(new string[0], Error("RedirectedLegacyProfile")), "redirected first start is recoverable");
        Check(WidgetStartup.ShouldHandoff(new string[0], Error("UnsafeLegacyPath")), "redirected legacy file can hand off");
        foreach (string reason in new[] { "InvalidLegacyFile", "UnsafeProfilePath", "MigrationFailed", "ProfileBusy", "UninitializedProfile", "InvalidLocation" })
            Check(!WidgetStartup.ShouldHandoff(new string[0], Error(reason)), "unrelated error not retried: " + reason);
        Check(!WidgetStartup.ShouldHandoff(null, Error("RedirectedLegacyProfile")), "null args rejected");
        Check(!WidgetStartup.ShouldHandoff(new string[0], null), "null error rejected");
        Check(!WidgetStartup.ShouldHandoff(new[] { "--normal-profile-start" }, Error("RedirectedLegacyProfile")), "malformed flag still blocks repeated handoff");
        Check(!WidgetStartup.ShouldHandoff(new[] { "--demo" }, Error("RedirectedLegacyProfile")), "demo never migrates");
        string nonce = Guid.NewGuid().ToString("N");
        Check(WidgetStartup.ReadNonce(new[] { "--update-ready", nonce.ToUpperInvariant() }, "--update-ready") == nonce, "nonce normalized");
        foreach (string invalid in new[] { "", null, Guid.Empty.ToString("N"), nonce + " extra", "../file", "\" --install", Guid.NewGuid().ToString("D") })
            Check(WidgetStartup.ReadNonce(new[] { "--update-ready", invalid }, "--update-ready") == null, "invalid nonce rejected");
        Check(WidgetStartup.ReadNonce(new[] { "--update-ready", nonce, "--update-ready", nonce }, "--update-ready") == null, "duplicate nonce rejected");
        Check(WidgetStartup.ReadNonce(new[] { "--update-ready" }, "--update-ready") == null, "missing nonce rejected");

        string app = Path.Combine(root, "App"); Directory.CreateDirectory(app);
        string executable = Path.Combine(app, "UsageWidget.exe"); File.Copy(Assembly.GetExecutingAssembly().Location, executable);
        string capturedExe = null, capturedArgs = null; int launched = 0;
        Func<string,string,bool> launcher = (exe, arguments) => { launched++; capturedExe = exe; capturedArgs = arguments; return true; };
        Check(WidgetStartup.TryHandoffForTests(new[] { "--update-ready", nonce, "--unknown", "untrusted" }, executable, executable, root, "1.25.8.0", launcher), "verified self launch succeeds");
        Check(launched == 1 && capturedExe == executable, "only exact fixed executable launched");
        string[] forwarded = capturedArgs.Split(' ');
        Check(forwarded.Length == 4 && forwarded[0] == "--normal-profile-start" && forwarded[2] == "--update-ready" && forwarded[3] == nonce, "only validated handoff and ready args forwarded");
        Check(WidgetStartup.ReadNonce(forwarded, "--normal-profile-start") != null, "fresh handoff nonce");
        Check((string)Receipt(root)["status"] == "handoff", "handoff status saved before launch");
        Check(!WidgetStartup.TryHandoffForTests(forwarded, executable, executable, root, "1.25.8.0", launcher) && launched == 1, "cannot loop handoff");
        Check(!WidgetStartup.TryHandoffForTests(new[] { "--demo" }, executable, executable, root, "1.25.8.0", launcher), "direct demo handoff rejected");
        Check(!WidgetStartup.TryHandoffForTests(new string[0], executable, executable + ".other", root, "1.25.8.0", launcher) && launched == 1, "arbitrary expected executable rejected");
        Check(!WidgetStartup.TryHandoffForTests(new string[0], executable, executable, root, "1.25.8.0", (e,a) => false), "launch rejection propagated");
        Check((string)Receipt(root)["stage"] == "handoff" && (string)Receipt(root)["status"] == "failed", "failed handoff receipt");
        Check(!WidgetStartup.TryHandoffForTests(new string[0], executable, executable, root, "1.25.8.0", (e,a) => { throw new IOException("secret path"); }), "launch exceptions safe");
        Check(!File.ReadAllText(Path.Combine(root, ".nephylo-startup.json")).Contains("secret path"), "exception details not persisted");
        Reject(() => WidgetStartup.ValidateExecutable("relative.exe", "relative.exe"), "relative executable rejected");
        Reject(() => WidgetStartup.ValidateExecutable(Path.Combine(root, "cmd.exe"), Path.Combine(root, "cmd.exe")), "other file name rejected");
        string missing = Path.Combine(root, "missing", "UsageWidget.exe");
        Reject(() => WidgetStartup.ValidateExecutable(missing, missing), "missing executable rejected");
        string packaged = Path.Combine(root, "AppData", "Local", "Packages", "Package", "UsageWidget.exe");
        Reject(() => WidgetStartup.ValidateExecutable(packaged, packaged), "package executable never handed off");
        Reject(() => WidgetStartup.ValidateExecutable(@"\\server\share\UsageWidget.exe", @"\\server\share\UsageWidget.exe"), "remote executable rejected");

        using (var request = WidgetStartup.CreateReadyRequest("1.25.8.0"))
        {
            Check(!request.Wait(TimeSpan.Zero), "new ready request is not signalled");
            WidgetStartup.MarkReadyForTests(new[] { "--update-ready", request.Nonce }, root, "1.25.7.0");
            Check(!request.Wait(TimeSpan.Zero), "wrong version cannot signal readiness");
            WidgetStartup.MarkReadyForTests(new[] { "--update-ready", Guid.NewGuid().ToString("N") }, root, "1.25.8.0");
            Check(!request.Wait(TimeSpan.Zero), "stale nonce cannot signal readiness");
            WidgetStartup.MarkReadyForTests(new[] { "--update-ready", request.Nonce }, root, "1.25.8.0");
            Check(request.Wait(TimeSpan.FromSeconds(1)), "matching final window signals readiness");
            Check((string)Receipt(root)["nonce"] == request.Nonce && (string)Receipt(root)["status"] == "ready", "ready receipt matches request");
            Reject(() => { using (var duplicate = new WidgetStartup.StartupReadyRequest("1.25.8.0", request.Nonce)) { } }, "existing named request rejected");
        }
        Reject(() => WidgetStartup.CreateReadyRequest("invalid"), "invalid version rejected");
        using (var one = WidgetStartup.CreateReadyRequest("1.25.8.0"))
        using (var two = WidgetStartup.CreateReadyRequest("1.25.8.0"))
        {
            Check(one.Nonce != two.Nonce, "requests have distinct nonce");
            WidgetStartup.MarkReadyForTests(new[] { "--normal-profile-start", one.Nonce, "--update-ready", two.Nonce }, root, "1.25.8.0");
            Check(one.Wait(TimeSpan.Zero) && two.Wait(TimeSpan.Zero), "final window wakes both handoff parent and updater");
        }
        TimeSpan waited = TimeSpan.Zero;
        Check(!WidgetStartup.TryHandoffForTests(new string[0], executable, executable, root, "1.25.8.0", launcher, nonce, timeout => { waited = timeout; return false; }), "process creation alone is not readiness");
        Check(waited == TimeSpan.FromSeconds(45), "handoff wait is bounded");
        Check((string)Receipt(root)["status"] == "failed", "no ready signal persists failed handoff");
        Check(WidgetStartup.TryHandoffForTests(new string[0], executable, executable, root, "1.25.8.0", launcher, nonce, timeout => true), "confirmed final window completes handoff");
        Check(!WidgetStartup.TryHandoffForTests(new string[0], executable, executable, root, "1.25.8.0", launcher, "bad nonce", timeout => true), "invalid internal handoff request rejected");
        WidgetStartup.MarkReadyForTests(new string[0], root, "1.25.8.0");
        Check((string)Receipt(root)["nonce"] == "" && (string)Receipt(root)["status"] == "ready", "normal first run writes bounded status");
        WidgetStartup.WriteStatus(root, "1.25.8.0", "failed", "secret@example.test token", "secret-token", new DateTime(2026,10,14,0,0,0,DateTimeKind.Utc));
        var record = Receipt(root);
        Check((string)record["stage"] == "startup" && (string)record["nonce"] == "", "diagnostic allowlists omit secret strings");
        Check(record.Count == 5 && (string)record["timestampUtc"] == "2026-10-14T00:00:00.0000000Z", "bounded diagnostic contract and clock");
        Check(new FileInfo(Path.Combine(root, ".nephylo-startup.json")).Length < 1024, "diagnostic size bounded");
        Check(Directory.GetFiles(root, ".nephylo-startup-*.tmp").Length == 0, "no temporary receipts retained");
        string before = File.ReadAllText(Path.Combine(root, ".nephylo-startup.json"));
        WidgetStartup.WriteStatus(root, "1.25.8.0", "arbitrary", "window", nonce, DateTime.UtcNow);
        Check(File.ReadAllText(Path.Combine(root, ".nephylo-startup.json")) == before, "unknown status never persisted");
        WidgetStartup.WriteStatus(Path.Combine(root, "not-created"), "1.25.8.0", "ready", "window", nonce, DateTime.UtcNow);
        Check(!Directory.Exists(Path.Combine(root, "not-created")), "diagnostics do not create unknown parent");
        WidgetStartup.MarkReadyForTests(new[] { "--update-ready", Guid.NewGuid().ToString("N") }, Path.Combine(root, "missing"), "1.25.8.0");
        Check(true, "missing waiter and unwritable diagnostic do not prevent startup");
    }

    private static void WmiProbe(string root)
    {
        string directory = Path.Combine(root, "WmiProbe"); Directory.CreateDirectory(directory);
        string exe = Path.Combine(directory, "UsageWidget.exe"); File.Copy(Assembly.GetExecutingAssembly().Location, exe);
        MethodInfo method = typeof(WidgetStartup).GetMethod("LaunchNormalContext", BindingFlags.NonPublic | BindingFlags.Static);
        Check(WidgetStartup.TryHandoffForTests(new string[0], exe, exe, root, "1.25.8.0", (e,a) => (bool)method.Invoke(null, new object[] { e, a })), "isolated real local WMI process creation");
        string result = Path.Combine(directory, "probe-ready.txt"); var elapsed = Stopwatch.StartNew();
        while (!File.Exists(result) && elapsed.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(50);
        Check(File.Exists(result), "isolated WMI child reached fixture main");
        string[] lines = File.ReadAllLines(result);
        Check(WidgetStartup.ReadNonce(new[] { "--normal-profile-start", lines[0] }, "--normal-profile-start") != null, "WMI forwarded validated nonce");
        Check(lines[1] == WindowsIdentity.GetCurrent().User.Value, "local WMI runs as same Windows user");
        Check(lines[2] == Process.GetCurrentProcess().SessionId.ToString(), "local WMI runs in same desktop session");
    }
}
