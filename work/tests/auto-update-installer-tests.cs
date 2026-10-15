using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.CSharp;
using UsageWidget;

namespace UsageWidget { internal static class SettingsStore { internal static string InstallDirectory { get { return DataDirectory; } } internal static string DataDirectory { get { return Path.Combine(Path.GetTempPath(), "unused-nephylo-updater-test"); } } } }
namespace UsageWidget { internal static class AutoUpdateInstallerTestPaths { internal static string DataDirectory = SettingsStore.DataDirectory; internal static string InstallMutex = @"Local\Nephylo.Update.WorkerTest." + Guid.NewGuid().ToString("N"); internal static TimeSpan StartupTimeout = TimeSpan.FromMilliseconds(100); } }
// Unit seam only; the silent-installer flow compiles and executes WidgetStartup itself.
namespace UsageWidget { internal static class WidgetStartup { internal static int Calls; internal static bool Ready; internal static bool RestartInstalledAndWait(string executable, TimeSpan timeout) { Calls++; return Ready; } } }

internal static class AutoUpdateInstallerTests
{
    private static int assertions;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
    private static void Reject(Action action, string message) { try { action(); } catch { assertions++; return; } throw new Exception(message); }
    private static string Digest(string path) { using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(stream)); }
    private static void Fixture(string path, string product = "Nephylo", string version = "1.0.0.0")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var compiler = new CSharpCodeProvider()) {
            var parameters = new CompilerParameters(new[] { "System.dll" }, path, false) { GenerateExecutable = true, CompilerOptions = "/target:winexe" };
            var result = compiler.CompileAssemblyFromSource(parameters, "using System.Reflection; [assembly:AssemblyProduct(\"" + product + "\")] [assembly:AssemblyVersion(\"" + version + "\")] public static class Fixture { public static void Main() { System.Threading.Thread.Sleep(1200); } }");
            if (result.Errors.HasErrors) throw new Exception("Fixture did not compile");
        }
    }
    public static int Main()
    {
        string root = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "update-worker-isolated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            string updates = Path.Combine(root, "Updates"), stage = Path.Combine(updates, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            Check(AutoUpdateInstaller.ValidateStage(stage, updates) == stage, "Owned GUID stage accepted");
            Reject(() => AutoUpdateInstaller.ValidateStage(Path.Combine(root, Guid.NewGuid().ToString("N")), updates), "Sibling stage rejected");
            Reject(() => AutoUpdateInstaller.ValidateStage(updates, updates), "Root is not a stage");
            Reject(() => AutoUpdateInstaller.ValidateStage(Path.Combine(updates, "not-a-guid"), updates), "Non GUID rejected");
            Reject(() => AutoUpdateInstaller.ValidateStage(Path.Combine(stage, Guid.NewGuid().ToString("N")), updates), "Nested stage rejected");
            Reject(() => AutoUpdateInstaller.ValidateStage(Path.Combine(updates, Guid.NewGuid().ToString("N")), updates), "Missing stage rejected");
            Check(!AutoUpdateInstaller.CanAutoInstall(), "Portable test process cannot auto install");
            Reject(() => AutoUpdateInstaller.StartPreparedUpdate(null), "Portable cannot start worker");
            Check(!AutoUpdateInstaller.TryRunWorker(new string[0]), "Ordinary app launch untouched");
            Check(AutoUpdateInstaller.TryRunWorker(new[] { "--apply-update" }), "Missing worker arguments handled without app launch");
            Check(AutoUpdateInstaller.TryRunWorker(new[] { "--apply-update", "-1", "1" }), "Negative worker PID rejected");
            Check(AutoUpdateInstaller.TryRunWorker(new[] { "--apply-update", "1", "1", "extra" }), "Extra worker arguments rejected");
            Check(AutoUpdateInstaller.TryRunWorker(new[] { "--apply-update", "1", "1" }), "Worker invocation from non-helper executable rejected");
            var start = AutoUpdateInstaller.CreateInstallerStartInfo(Path.Combine(stage, "Nephylo-Setup-2.0.0.exe"), Path.Combine(stage, "setup.log"));
            foreach (string option in new[] { "/SP-", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCLOSEAPPLICATIONS", "/NOFORCECLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS" }) Check(start.Arguments.Contains(option), "Installer flag: " + option);
            Check(!start.UseShellExecute && start.CreateNoWindow && start.WindowStyle == ProcessWindowStyle.Hidden, "No shell and hidden installer");
            Check(!start.Arguments.Contains("/DIR="), "No remote directory override");
            Reject(() => AutoUpdateInstaller.CreateInstallerStartInfo("evil\"file", "log"), "Quoted installer rejected");
            Reject(() => AutoUpdateInstaller.CreateInstallerStartInfo("file", "evil\" /DIR=other"), "Quoted log rejected");
            using (var reservation = AutoUpdateInstaller.TryReserveInstaller()) {
                Check(reservation != null, "Updater reserves isolated installer mutex");
                Check(AutoUpdateInstaller.TryReserveInstaller() == null, "Existing Setup mutex cannot be reserved again");
                bool acquired = false;
                var thread = new Thread(() => { using (var mutex = new Mutex(false, AutoUpdateInstallerTestPaths.InstallMutex)) { acquired = mutex.WaitOne(0); if (acquired) mutex.ReleaseMutex(); } });
                thread.Start(); thread.Join();
                Check(!acquired, "Reservation blocks legacy ZIP installer ownership too");
            }
            using (var reservation = AutoUpdateInstaller.TryReserveInstaller()) Check(reservation != null, "Reservation release permits subsequent installation");
            Check(!AutoUpdateInstaller.ShouldRollbackPayload(false, -1), "No rollback before installer starts");
            foreach (int code in new[] { 1, 2, 3, 7, 8 }) Check(!AutoUpdateInstaller.ShouldRollbackPayload(true, code), "Pre-install refusal does not undo concurrent installer: " + code);
            foreach (int code in new[] { 0, 4, 5, 6, 99 }) Check(AutoUpdateInstaller.ShouldRollbackPayload(true, code), "Potential partial installation is eligible for rollback: " + code);
            string app = Path.Combine(root, "data", "App"), executable = Path.Combine(app, "UsageWidget.exe");
            Fixture(executable);
            File.WriteAllText(executable + ".config", "<configuration />");
            File.WriteAllText(Path.Combine(app, "README.txt"), "original readme");
            File.WriteAllText(Path.Combine(app, "unknown-user-file.dat"), "preserve unknown");
            string userData = Path.Combine(root, "data", "settings.json"); File.WriteAllText(userData, "preserve settings");
            Check(AutoUpdateInstaller.ValidateExecutable(executable) == new Version(1, 0, 0, 0), "Recognizes Nephylo assembly and version");
            string foreign = Path.Combine(root, "foreign", "UsageWidget.exe"); Fixture(foreign, "Other App");
            Reject(() => AutoUpdateInstaller.ValidateExecutable(foreign), "Foreign product rejected");
            string hash = Digest(executable);
            Dictionary<string, string> backup = AutoUpdateInstaller.BackupPayload(stage, app);
            Check(backup.Count == 6 && backup["widget.ico"] == null, "Backup records only known six files including absent optional files");
            Check(!File.Exists(Path.Combine(stage, "previous", "unknown-user-file.dat")), "Unknown file excluded from backup");
            Reject(() => AutoUpdateInstaller.BackupPayload(stage, app), "Existing backup cannot be overwritten");
            File.WriteAllText(executable, "partial installer write"); File.WriteAllText(Path.Combine(app, "widget.ico"), "new optional file");
            File.WriteAllText(Path.Combine(app, "README.txt"), "changed readme");
            AutoUpdateInstaller.RestorePayload(stage, app, backup);
            Check(Digest(executable) == hash, "Rollback restores exact old executable");
            Check(File.ReadAllText(Path.Combine(app, "README.txt")) == "original readme", "Rollback restores existing readme");
            Check(!File.Exists(Path.Combine(app, "widget.ico")), "Rollback removes only newly installed known file");
            Check(File.ReadAllText(Path.Combine(app, "unknown-user-file.dat")) == "preserve unknown", "Rollback preserves unknown App file");
            Check(File.ReadAllText(userData) == "preserve settings", "Rollback preserves parent settings");
            File.WriteAllText(Path.Combine(stage, "previous", "README.txt"), "tampered backup");
            Reject(() => AutoUpdateInstaller.RestorePayload(stage, app, backup), "Tampered rollback backup rejected");
            using (var parent = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })) {
                AutoUpdateInstaller.ValidateParent(parent, parent.StartTime.ToUniversalTime().Ticks, executable); assertions++;
                Reject(() => AutoUpdateInstaller.ValidateParent(parent, parent.StartTime.ToUniversalTime().Ticks + 1, executable), "Parent process start time mismatch rejected");
                Reject(() => AutoUpdateInstaller.ValidateParent(parent, parent.StartTime.ToUniversalTime().Ticks, foreign), "Parent executable mismatch rejected");
                long ticks = parent.StartTime.ToUniversalTime().Ticks;
                parent.WaitForExit(5000);
                Reject(() => AutoUpdateInstaller.ValidateParent(parent, ticks, executable), "Exited parent rejected");
            }
            using (var self = Process.GetCurrentProcess()) Reject(() => AutoUpdateInstaller.ValidateParent(self, self.StartTime.ToUniversalTime().Ticks, self.MainModule.FileName), "Self PID rejected as parent");
            string startupRoot = Path.Combine(root, "startup"), startupExecutable = Path.Combine(startupRoot, "App", "UsageWidget.exe");
            Fixture(startupExecutable, "Nephylo", "1.25.8.0");
            AutoUpdateInstallerTestPaths.DataDirectory = startupRoot;
            WidgetStartup.Ready = false;
            Reject(() => AutoUpdateInstaller.RestartInstalled(), "Modern app missing readiness is a failed restart");
            Check(WidgetStartup.Calls == 1, "Modern restart uses readiness protocol");
            WidgetStartup.Ready = true; AutoUpdateInstaller.RestartInstalled();
            Check(WidgetStartup.Calls == 2, "Modern restart accepts confirmed readiness");
            string status = Path.Combine(root, "result.json");
            Check(AutoUpdateInstaller.ReadResult(status) == null, "Missing result is harmless");
            File.WriteAllText(status, "{\"version\":\"2.0.0\",\"code\":\"installed\",\"success\":true,\"timestampUtc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\"}");
            var resultStatus = AutoUpdateInstaller.ReadResult(status);
            Check(resultStatus != null && resultStatus.Success && resultStatus.Version == "2.0.0" && resultStatus.Code == "installed", "Bounded result parses successfully");
            File.WriteAllText(status, new string('x', 2049)); Check(AutoUpdateInstaller.ReadResult(status) == null, "Oversized result ignored");
            File.WriteAllText(status, "{broken}"); Check(AutoUpdateInstaller.ReadResult(status) == null, "Malformed result ignored");
            File.WriteAllText(status, "{\"version\":\"2.0.0\",\"code\":\"installed\",\"success\":\"true\",\"timestampUtc\":\"2026-01-01\"}"); Check(AutoUpdateInstaller.ReadResult(status) == null, "Wrong result types rejected");
            string cleanRoot = Path.Combine(root, "cleanup"); Directory.CreateDirectory(cleanRoot);
            string cleanStage = Path.Combine(cleanRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(cleanStage);
            File.WriteAllText(Path.Combine(cleanStage, "Nephylo-Setup-2.0.0.exe"), "abandoned download");
            Directory.SetLastWriteTimeUtc(cleanStage, DateTime.UtcNow.AddDays(-9));
            string recent = Path.Combine(cleanRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(recent);
            File.WriteAllText(Path.Combine(recent, "update.json"), "recent stage");
            string recovery = Path.Combine(cleanRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(recovery);
            File.WriteAllText(Path.Combine(recovery, "result.json"), "{\"version\":\"2.0.0\",\"code\":\"rollback-failed\",\"success\":false,\"timestampUtc\":\"" + DateTime.UtcNow.AddDays(-9).ToString("o", CultureInfo.InvariantCulture) + "\"}");
            Directory.SetLastWriteTimeUtc(recovery, DateTime.UtcNow.AddDays(-9));
            string startupRecovery = Path.Combine(cleanRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(startupRecovery);
            File.WriteAllText(Path.Combine(startupRecovery, "result.json"), "{\"version\":\"1.25.8\",\"code\":\"startup-failed\",\"success\":false,\"timestampUtc\":\"" + DateTime.UtcNow.AddDays(-9).ToString("o", CultureInfo.InvariantCulture) + "\"}");
            Directory.SetLastWriteTimeUtc(startupRecovery, DateTime.UtcNow.AddDays(-9));
            string incompleteStartup = Path.Combine(cleanRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(incompleteStartup);
            File.WriteAllText(Path.Combine(incompleteStartup, "result.json"), "{\"version\":\"1.25.8\",\"code\":\"installation-complete\",\"success\":false,\"timestampUtc\":\"" + DateTime.UtcNow.AddDays(-9).ToString("o", CultureInfo.InvariantCulture) + "\"}");
            Directory.SetLastWriteTimeUtc(incompleteStartup, DateTime.UtcNow.AddDays(-9));
            string unknown = Path.Combine(cleanRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(unknown, "my-own-file.txt"), "retain unknown"); Directory.SetLastWriteTimeUtc(unknown, DateTime.UtcNow.AddDays(-9));
            string complete = Path.Combine(cleanRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(complete, "previous"));
            File.WriteAllText(Path.Combine(complete, "previous", "UsageWidget.exe"), "old exe");
            File.WriteAllText(Path.Combine(complete, "NephyloUpdater.exe"), "old helper");
            File.WriteAllText(Path.Combine(complete, "result.json"), "{\"version\":\"2.0.0\",\"code\":\"installed\",\"success\":true,\"timestampUtc\":\"" + DateTime.UtcNow.AddDays(-9).ToString("o", CultureInfo.InvariantCulture) + "\"}");
            Directory.SetLastWriteTimeUtc(complete, DateTime.UtcNow.AddDays(-9));
            AutoUpdateInstaller.CleanupOldStages(cleanRoot, DateTime.UtcNow);
            Check(!Directory.Exists(cleanStage), "Old abandoned download removed");
            Check(!Directory.Exists(complete), "Old completed helper and allowlisted backup removed");
            Check(Directory.Exists(recent), "Recent stage retained");
            Check(Directory.Exists(recovery), "Recovery-needed backup retained");
            Check(Directory.Exists(startupRecovery) && Directory.Exists(incompleteStartup), "Startup failure or interrupted readiness keeps recovery evidence");
            Check(File.Exists(Path.Combine(unknown, "my-own-file.txt")), "Cleanup leaves unknown stage file untouched");
            Console.WriteLine("Auto update installer: " + assertions + " assertions passed."); return 0;
        } catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally {
            string full = Path.GetFullPath(root);
            string tests = Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            if (Path.GetDirectoryName(full) == tests && Path.GetFileName(full).StartsWith("update-worker-isolated-", StringComparison.Ordinal)) Directory.Delete(full, true);
        }
    }
}
