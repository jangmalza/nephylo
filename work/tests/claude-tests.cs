using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using UsageWidget;

[assembly: AssemblyProduct("Usage Widget")]

public static class ClaudeTests
{
    static int count;
    static void Check(bool condition, string description) { if (!condition) throw new Exception(description); count++; }
    static void Reject(Action action, string description) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, description); }
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--claude-bridge-check") {
            if (Environment.GetEnvironmentVariable("NEPHYLO_CLAUDE_TEST_PROBE_STATUS") == "storage") { Console.WriteLine("nephylo-claude-bridge-storage"); return 3; }
            Console.WriteLine("nephylo-claude-bridge-v1"); return 0;
        }
        if (args.Length > 0 && args[0] == "--claude-statusline")
        {
            // This test-only entrypoint is isolated even when launched through the actual hook command.
            SettingsStore.DataDirectoryOverride = Environment.GetEnvironmentVariable("USAGE_WIDGET_CLAUDE_TEST_DIR");
            int testDirectoryIndex = Array.IndexOf(args, "--test-local-data");
            if (testDirectoryIndex >= 0 && testDirectoryIndex + 1 < args.Length) {
                string testDirectory = Path.GetFullPath(args[testDirectoryIndex + 1]);
                string testParent = Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)) + Path.DirectorySeparatorChar;
                if (!testDirectory.StartsWith(testParent, StringComparison.OrdinalIgnoreCase)) return 2;
                SettingsStore.DataDirectoryOverride = testDirectory;
            }
            if (String.IsNullOrEmpty(SettingsStore.DataDirectoryOverride)) return 2;
            int sourceIndex = Array.IndexOf(args, "--claude-source");
            ClaudeUsageService.CaptureFromStandardInput(sourceIndex >= 0 && sourceIndex + 1 < args.Length ? args[sourceIndex + 1] : "");
            return 0;
        }
        string root = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "claude-isolated-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            SettingsStore.DataDirectoryOverride = Path.Combine(root, "data");
            ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(root, "claude");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string valid = "{\"cwd\":\"PRIVATE_PATH\",\"secret\":\"PRIVATE_KEY\",\"rate_limits\":{\"five_hour\":{\"used_percentage\":23.5,\"resets_at\":253402300000,\"other\":\"PRIVATE_DETAIL\"},\"seven_day\":{\"used_percentage\":100}}}";
            Check(ClaudeUsageService.CaptureJson(valid, now), "quota captured");
            var result = ClaudeUsageService.ReadLatest();
            Check(result.Snapshot.Rows.Count == 2 && result.Snapshot.Rows[0].RemainingPercent == 76.5 && result.Snapshot.Rows[1].RemainingPercent == 0, "used-to-remaining semantics");
            Check(result.ObservedAt == now && ClaudeUsageService.ReadLatest().ObservedAt == now, "reads preserve observation timestamp");
            string cachePath = Path.Combine(SettingsStore.DataDirectory, "claude-usage.json");
            string clean = File.ReadAllText(cachePath);
            Check(!clean.Contains("PRIVATE") && !clean.Contains("cwd") && !clean.Contains("secret"), "cache whitelists numeric quotas only");
            Check(result.HasQuotaData, "received numerical quota is explicitly identified");
            Check(ClaudeUsageService.CaptureJson("{\"model\":{\"id\":\"test\"}}", now.AddSeconds(1)), "valid input without rate limits records a receipt");
            result = ClaudeUsageService.ReadLatest();
            Check(!result.HasQuotaData && result.ObservedAt == now.AddSeconds(1) && result.Snapshot.Rows.All(row => !row.RemainingPercent.HasValue), "missing rate limits replaces prior account quota with a fresh unknown receipt");
            Check(!result.Snapshot.Note.Contains("Free") && !result.Snapshot.Note.Contains("API"), "missing quota never infers an account plan or login method");
            clean = File.ReadAllText(cachePath);
            Check(!clean.Contains("model") && !clean.Contains("test"), "quota-unavailable receipt retains no input metadata");
            foreach (string bad in new[] { "not json", "[]", "{\"rate_limits\":false}", "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":\"23\"}}}", "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":-1}}}", "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":101}}}", "{\"rate_limits\":{\"five_hour\":{\"resets_at\":1.5}}}", new string('x', 1048576) })
                Reject(delegate { ClaudeUsageService.CaptureJson(bad, now); }, "reject invalid quota input");
            Check(File.ReadAllText(cachePath) == clean, "malformed input never replaces cache");
            ClaudeUsageService.CaptureJson("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":10,\"resets_at\":1},\"seven_day\":null}}", now);
            result = ClaudeUsageService.ReadLatest();
            Check(result.HasExpiredWindows && !result.Snapshot.Rows[0].RemainingPercent.HasValue && result.Snapshot.Rows[0].Label.Contains("수신 대기"), "expired quota is unknown instead of fake reset");
            Check(!result.Snapshot.Rows[1].RemainingPercent.HasValue && !result.Snapshot.Rows[1].ResetsAt.HasValue, "missing weekly is unknown");
            ClaudeUsageService.CaptureJson("{\"rate_limits\":{}}", now);
            Check(!ClaudeUsageService.ReadLatest().HasQuotaData && ClaudeUsageService.ReadLatest().Snapshot.Rows.All(r => !r.RemainingPercent.HasValue), "empty windows are a received event without quota");
            Check(ClaudeUsageService.CaptureJson("{\"rate_limits\":null}", now.AddSeconds(2)) && !ClaudeUsageService.ReadLatest().HasQuotaData, "null rate limits are a received event without inferred quota");
            Check(ClaudeUsageService.CaptureJson("{}", now.AddSeconds(3)) && ClaudeUsageService.ReadLatest().ObservedAt == now.AddSeconds(3), "a valid minimal status event records its observation time");

            string executable = Assembly.GetExecutingAssembly().Location;
            Reject(delegate { ClaudeUsageService.InstallStatusLine(Path.Combine(root, "$(unsafe).exe")); }, "shell expansion path rejected");
            Reject(delegate { ClaudeUsageService.InstallStatusLine(Path.Combine(root, "`unsafe.exe")); }, "shell backtick path rejected");
            string configPath = Path.Combine(ClaudeUsageService.ConfigDirectoryOverride, "settings.json");
            Directory.CreateDirectory(ClaudeUsageService.ConfigDirectoryOverride);
            File.WriteAllText(configPath, "{\"permissions\":{\"deny\":[\"Bash(rm*)\"]},\"theme\":\"dark\"}", new UTF8Encoding(false));
            string original = File.ReadAllText(configPath);
            ClaudeUsageService.InstallStatusLine(executable);
            string installed = File.ReadAllText(configPath);
            var config = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(installed);
            Check(config.ContainsKey("permissions") && Object.Equals(config["theme"], "dark"), "install preserves unrelated settings");
            Check(Directory.GetFiles(ClaudeUsageService.ConfigDirectoryOverride, "*.bak").Any(p => File.ReadAllText(p) == original), "exact original backup retained");
            Check(ClaudeUsageService.StatusLineStatus(executable).Contains("연결됨"), "hook detected");
            ClaudeUsageService.InstallStatusLine(executable);
            Check(File.ReadAllText(configPath) == installed, "install idempotent");

            // Paths with spaces and apostrophes survive the inner PowerShell literal quoting.
            string literalExecutable = Path.Combine(root, "widget space 'quoted' & literal.exe");
            File.Copy(executable, literalExecutable);

            // Execute the installed wrapper against a WinExe, with bounded fixture JSON on stdin.
            var hook = (Dictionary<string, object>)config["statusLine"];
            string command = (string)hook["command"];
            string childData = Path.Combine(root, "child-capture");
            new ServicePlan(childData,false,null,null).Select("claude");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), command.Substring("powershell.exe ".Length)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.EnvironmentVariables["USAGE_WIDGET_CLAUDE_TEST_DIR"] = childData;
            using (var child = Process.Start(start))
            {
                child.StandardInput.WriteLine(valid); child.StandardInput.Close();
                string output = child.StandardOutput.ReadToEnd(); string error = child.StandardError.ReadToEnd();
                Check(child.WaitForExit(10000), "WinExe hook exits");
                Check(child.ExitCode == 0 && File.Exists(Path.Combine(childData, "claude-usage.json")), "WinExe receives redirected JSON through installed wrapper");
                Check(!output.Contains("PRIVATE") && !error.Contains("PRIVATE"), "hook does not expose raw input");
            }
            string beforeDormant = File.ReadAllText(Path.Combine(childData,"claude-usage.json"));
            new ServicePlan(childData,false,null,null).Select("eleven");
            using (var child = Process.Start(start)) {
                child.StandardInput.WriteLine(valid); child.StandardInput.Close(); child.StandardOutput.ReadToEnd(); child.StandardError.ReadToEnd();
                Check(child.WaitForExit(10000) && child.ExitCode == 0,"Unselected Claude hook exits cleanly without an entitlement file");
                string latestCache = File.ReadAllText(Path.Combine(childData,"claude-usage.json"));
                Check(latestCache != beforeDormant && !latestCache.Contains("PRIVATE") && !File.Exists(Path.Combine(childData,"usage-journal.json")),"Hook updates only the latest sanitized cache; widget entitlement gates its use, never journal history");
            }
            ClaudeUsageService.RemoveStatusLine(executable);
            config = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
            Check(!config.ContainsKey("statusLine") && config.ContainsKey("permissions"), "disconnect removes only hook");
            ClaudeUsageService.InstallStatusLine(literalExecutable);
            config = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
            command = (string)((Dictionary<string, object>)config["statusLine"])["command"];
            start.Arguments = command.Substring("powershell.exe ".Length);
            string literalChildData = Path.Combine(root, "literal-child-capture");
            new ServicePlan(literalChildData,false,null,null).Select("claude");
            start.EnvironmentVariables["USAGE_WIDGET_CLAUDE_TEST_DIR"] = literalChildData;
            using (var child = Process.Start(start))
            {
                child.StandardInput.WriteLine(valid); child.StandardInput.Close();
                child.StandardOutput.ReadToEnd(); child.StandardError.ReadToEnd();
                Check(child.WaitForExit(10000) && child.ExitCode == 0 && File.Exists(Path.Combine(literalChildData, "claude-usage.json")), "spaces apostrophes ampersands are literal path characters");
            }
            ClaudeUsageService.RemoveStatusLine(literalExecutable);
            string oldExecutable = Path.Combine(root,"previous-install","UsageWidget.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(oldExecutable)); File.Copy(executable,oldExecutable);
            ClaudeUsageService.InstallStatusLine(oldExecutable);
            string oldConfig = File.ReadAllText(configPath);
            Check(ClaudeUsageService.StatusLineStatus(executable).Contains("복구"),"old widget path offers repair instead of reporting custom statusline");
            ClaudeUsageService.InstallStatusLine(executable);
            Check(ClaudeUsageService.IsStatusLineInstalled(executable),"repair points hook at current executable");
            Check(Directory.GetFiles(ClaudeUsageService.ConfigDirectoryOverride,"*.bak").Any(p => File.ReadAllText(p) == oldConfig),"repair keeps exact old configuration backup");
            ClaudeUsageService.RemoveStatusLine(executable);
            ClaudeUsageService.InstallStatusLine(oldExecutable);
            File.Delete(oldExecutable);
            Check(ClaudeUsageService.StatusLineStatus(executable).Contains("복구"),"missing previous executable remains recognizable");
            ClaudeUsageService.InstallStatusLine(executable);
            Check(ClaudeUsageService.IsStatusLineInstalled(executable),"removed previous installation repaired safely");
            ClaudeUsageService.RemoveStatusLine(executable);
            config = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(oldConfig);
            var previousHook = (Dictionary<string,object>)config["statusLine"];
            string originalOldCommand = (string)previousHook["command"];
            previousHook["command"] = originalOldCommand + " ; Write-Output 'user customization'";
            File.WriteAllText(configPath,new JavaScriptSerializer().Serialize(config));
            string modifiedWrapper = File.ReadAllText(configPath);
            Reject(delegate { ClaudeUsageService.InstallStatusLine(executable); },"custom wrapper suffix never migrated");
            Check(File.ReadAllText(configPath) == modifiedWrapper,"custom wrapper exact bytes preserved");
            previousHook["command"] = originalOldCommand; previousHook["padding"] = 3;
            File.WriteAllText(configPath,new JavaScriptSerializer().Serialize(config));
            Reject(delegate { ClaudeUsageService.InstallStatusLine(executable); },"customized old hook fields never discarded by migration");
            previousHook.Remove("padding"); File.WriteAllText(configPath,new JavaScriptSerializer().Serialize(config));
            File.WriteAllText(oldExecutable,"not a Usage Widget executable");
            Reject(delegate { ClaudeUsageService.InstallStatusLine(executable); },"existing non-widget executable not identified by filename alone");
            File.Delete(oldExecutable);
            ClaudeUsageService.RemoveStatusLine(executable);
            Check(!new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(configPath)).ContainsKey("statusLine"),"old known widget hook can also be explicitly disconnected");
            File.WriteAllText(configPath, "{\"statusLine\":{\"type\":\"command\",\"command\":\"custom\"},\"theme\":\"dark\"}");
            string custom = File.ReadAllText(configPath);
            Check(ClaudeUsageService.HasCustomStatusLineWindows(executable,null) && !ClaudeUsageService.IsStatusLineInstalled(executable), "custom forwarding opt-in can be offered without treating the hook as ours");
            Reject(delegate { ClaudeUsageService.InstallStatusLine(executable); }, "install refuses custom hook");
            Reject(delegate { ClaudeUsageService.RemoveStatusLine(executable); }, "remove refuses custom hook");
            Check(File.ReadAllText(configPath) == custom, "custom hook unchanged");
            File.WriteAllText(configPath, "{ malformed");
            Reject(delegate { ClaudeUsageService.InstallStatusLine(executable); }, "malformed config refused");
            Check(File.ReadAllText(configPath) == "{ malformed", "malformed config retained");
            TestWindowsConfigurationDirectory(root, executable, valid, now);
            TestBridgeDiagnostics(executable, valid);
            TestWslBridge(root, executable, valid, now);
            string liveDistribution = Environment.GetEnvironmentVariable("NEPHYLO_CLAUDE_TEST_WSL");
            if (!String.IsNullOrEmpty(liveDistribution)) TestLiveWsl(liveDistribution, executable);
            Console.WriteLine("Claude bridge tests passed: " + count);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        finally
        {
            SettingsStore.DataDirectoryOverride = null;
            ClaudeUsageService.ConfigDirectoryOverride = null;
            ClaudeUsageService.WslExecuteOverride = null;
            ClaudeUsageService.WslProbeOverride = null;
            ClaudeUsageService.WslPathOverride = null;
            ClaudeUsageService.BridgeProbeOverride = null;
            // Only remove the isolated directory created by this test, under the test executable's folder.
            string expectedParent = Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void TestBridgeDiagnostics(string executable, string valid)
    {
        string config = Path.Combine(ClaudeUsageService.ConfigDirectoryOverride, "settings.json");
        string original = File.ReadAllText(config);
        foreach (string code in new[] { "launch", "timeout", "response", "storage", "PRIVATE_SECRET" }) {
            ClaudeUsageService.BridgeProbeOverride = (info, token) => {
                Check(Path.IsPathRooted(info.FileName) && info.FileName.EndsWith("powershell.exe") && info.Arguments.Contains("--claude-bridge-check") && !info.Arguments.Contains("--claude-statusline"), "Probe uses a fixed PowerShell path and a non-receiving operation");
                Check(info.CreateNoWindow && !info.UseShellExecute && !info.Arguments.Contains("ExecutionPolicy"), "Probe does not bypass security policy");
                return Task.FromResult(code);
            };
            Exception failure = null;
            try { ClaudeUsageService.VerifyWindowsBridgeAsync(executable, null, System.Threading.CancellationToken.None).GetAwaiter().GetResult(); } catch (Exception error) { failure = error; }
            Check(failure is ConnectionException && !failure.Message.Contains("PRIVATE_SECRET"), "Probe failure is classified without raw child output");
            Check(File.ReadAllText(config) == original, "Probe failure leaves the existing Claude configuration unchanged");
            var diagnostic = File.ReadAllText(Path.Combine(SettingsStore.DataDirectory, "claude-usage.json.probe.diagnostic.json"));
            Check(!diagnostic.Contains("PRIVATE") && !diagnostic.Contains(executable) && !diagnostic.Contains("command"), "Probe diagnostic contains only fixed category and timestamp");
        }
        int calls = 0;
        ClaudeUsageService.BridgeProbeOverride = (info, token) => { calls++; return Task.FromResult("ok"); };
        var canceled = new System.Threading.CancellationToken(true);
        Reject(() => ClaudeUsageService.VerifyWindowsBridgeAsync(executable, null, canceled).GetAwaiter().GetResult(), "Canceled checks are not executed");
        Check(calls == 0, "Canceled checks never launch a process");
        ClaudeUsageService.CaptureJson(valid, DateTimeOffset.UtcNow);
        string cache = Path.Combine(SettingsStore.DataDirectory, "claude-usage.json");
        string saved = File.ReadAllText(cache);
        ClaudeUsageService.RecordBridgeDiagnostic("", "receive", "storage");
        Exception readFailure = null;
        try { ClaudeUsageService.ReadLatest(); } catch (Exception error) { readFailure = error; }
        Check(readFailure is ConnectionException && readFailure.Message.Contains("쓰기 권한"), "Recent write failure is distinguished from no receipt");
        Check(File.ReadAllText(cache) == saved, "Receive failure preserves previous quota bytes");
        ClaudeUsageService.RecordBridgeDiagnostic("", "receive", "input");
        Reject(() => ClaudeUsageService.ReadLatest(), "Invalid input failure is surfaced");
        ClaudeUsageService.CaptureJson(valid, DateTimeOffset.UtcNow);
        Check(ClaudeUsageService.ReadLatest().HasQuotaData, "Successful receipt clears a prior diagnostic failure");
        using (var locked = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.None)) {
            Exception blocked = null;
            try { ClaudeUsageService.ReadLatest(); } catch (Exception error) { blocked = error; }
            Check(blocked is ConnectionException && blocked.Message.Contains("쓰기 권한"), "Locked cache is an access problem, not first-receipt waiting");
        }
        ClaudeUsageService.VerifyCaptureStorage();
        Check(!Directory.GetFiles(SettingsStore.DataDirectory, ".claude-check-*.tmp").Any(), "Storage probe cleans up its own test file");
        ClaudeUsageService.BridgeProbeOverride = null;
        // Uses only this test executable's constant handshake; no profile, account or network.
        ClaudeUsageService.VerifyWindowsBridgeAsync(executable, null, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
        Check(File.ReadAllText(config) == original, "Real PowerShell handshake leaves Claude settings untouched");
        string priorMode = Environment.GetEnvironmentVariable("NEPHYLO_CLAUDE_TEST_PROBE_STATUS");
        try {
            Environment.SetEnvironmentVariable("NEPHYLO_CLAUDE_TEST_PROBE_STATUS", "storage");
            Exception error = null;
            try { ClaudeUsageService.VerifyWindowsBridgeAsync(executable, null, System.Threading.CancellationToken.None).GetAwaiter().GetResult(); } catch (Exception failure) { error = failure; }
            Check(error is ConnectionException && error.Message.Contains("쓰기 권한"), "Native storage failure survives PowerShell exit-code normalization");
        } finally { Environment.SetEnvironmentVariable("NEPHYLO_CLAUDE_TEST_PROBE_STATUS", priorMode); }
    }

    private static void TestWindowsConfigurationDirectory(string root, string executable, string valid, DateTimeOffset now)
    {
        string previousOverride = ClaudeUsageService.ConfigDirectoryOverride;
        string previousEnvironment = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try {
            string environmentDirectory = Path.Combine(root, "environment config");
            string explicitDirectory = Path.Combine(root, "explicit config");
            string defaultDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            ClaudeUsageService.ConfigDirectoryOverride = null;
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            Check(ClaudeUsageService.ResolveWindowsConfigDirectory(null) == defaultDirectory && ClaudeUsageService.GetWindowsSourceId(null) == "", "default Windows directory retains the legacy cache identity without opening real settings");
            string legacyCommand = ClaudeUsageService.GetWindowsStatusLineCommand(executable);
            Check(!legacyCommand.Contains("--claude-source"), "default Windows hook command remains compatible");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", environmentDirectory);
            Check(ClaudeUsageService.ResolveWindowsConfigDirectory(null) == environmentDirectory, "CLAUDE_CONFIG_DIR is respected when no explicit directory is supplied");
            Check(ClaudeUsageService.ResolveWindowsConfigDirectory(explicitDirectory) == explicitDirectory, "explicit directory takes precedence over CLAUDE_CONFIG_DIR");
            string explicitSource = ClaudeUsageService.GetWindowsSourceId(explicitDirectory);
            string environmentSource = ClaudeUsageService.GetWindowsSourceId(null);
            Check(explicitSource.StartsWith("win:") && explicitSource.Length == 68 && explicitSource != environmentSource, "different Windows config roots have distinct bounded cache identities");
            Check(ClaudeUsageService.GetWindowsSourceId(explicitDirectory.ToUpperInvariant() + Path.DirectorySeparatorChar) == explicitSource, "case and trailing separators do not split the same Windows source");
            Check(ClaudeUsageService.GetWindowsSourceId(defaultDirectory) == "", "explicit default directory also uses legacy source identity");
            ClaudeUsageService.InstallStatusLineWindows(executable, explicitDirectory);
            string configPath = Path.Combine(explicitDirectory, "settings.json");
            Check(File.Exists(configPath) && !Directory.Exists(environmentDirectory), "explicit install touches only its isolated selected directory");
            Check(ClaudeUsageService.IsStatusLineInstalledWindows(executable, explicitDirectory) && !ClaudeUsageService.HasCustomStatusLineWindows(executable, explicitDirectory), "own hook is strict and distinct from custom forwarding");
            var config = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(configPath));
            var hook = (Dictionary<string,object>)config["statusLine"];
            string command = ClaudeUsageService.GetWindowsStatusLineCommand(executable, explicitDirectory);
            Check(Object.Equals(hook["command"], command) && command.Contains("--claude-source " + explicitSource), "selected config hook forwards its own hashed source to the capture helper");
            ClaudeUsageService.CaptureJson(valid, now, explicitSource);
            Reject(delegate { ClaudeUsageService.ReadLatest("", null); }, "a new selected environment cannot consume another Windows directory's cached quota");
            ClaudeUsageService.CaptureJson("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":5}}}", now, environmentSource);
            Check(ClaudeUsageService.ReadLatest("", explicitDirectory).Snapshot.Rows[0].RemainingPercent == 76.5 && ClaudeUsageService.ReadLatest("", null).Snapshot.Rows[0].RemainingPercent == 95, "two Windows config sources retain separate quota snapshots");
            Check(!ClaudeUsageService.ReadLatest("", defaultDirectory).HasQuotaData, "legacy default source does not inherit either custom directory's quota");
            string cachePath = Path.Combine(SettingsStore.DataDirectory, "claude-usage-win-" + explicitSource.Substring(4) + ".json");
            string cache = File.ReadAllText(cachePath);
            File.WriteAllText(cachePath, cache.Replace(explicitSource, environmentSource));
            Reject(delegate { ClaudeUsageService.ReadLatest("", explicitDirectory); }, "Windows cache metadata must match the requested directory source");
            File.WriteAllText(cachePath, cache);
            ClaudeUsageService.CaptureJson(valid, now, "wsl:" + explicitSource.Substring(4));
            Check(File.Exists(cachePath) && File.Exists(Path.Combine(SettingsStore.DataDirectory, "claude-usage-" + explicitSource.Substring(4) + ".json")), "Windows and WSL cache names remain disjoint even for the same hash");

            // Run the generated source-specific wrapper with fixture JSON, never Claude.
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), command.Substring("powershell.exe ".Length)) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            string childData = Path.Combine(root, "source-specific-child");
            start.EnvironmentVariables["USAGE_WIDGET_CLAUDE_TEST_DIR"] = childData;
            using (var child = Process.Start(start)) {
                child.StandardInput.WriteLine(valid); child.StandardInput.Close();
                var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
                Check(child.WaitForExit(10000), "source-specific helper exits within the fixture deadline");
                Task.WaitAll(output, error);
                Check(child.ExitCode == 0 && File.Exists(Path.Combine(childData, Path.GetFileName(cachePath))) && !File.Exists(Path.Combine(childData, "claude-usage.json")), "source-specific Windows command writes only its own cache");
            }
            hook["command"] = command + " ; Write-Output 'custom status'";
            File.WriteAllText(configPath, new JavaScriptSerializer().Serialize(config));
            string custom = File.ReadAllText(configPath);
            Check(ClaudeUsageService.HasCustomStatusLineWindows(executable, explicitDirectory) && !ClaudeUsageService.IsStatusLineInstalledWindows(executable, explicitDirectory), "manual forwarding is detectable without weakening own-hook matching");
            Check(ClaudeUsageService.ReadLatest("", explicitDirectory).HasQuotaData, "custom forwarding quota is available for explicit UI opt-in");
            Reject(delegate { ClaudeUsageService.RemoveStatusLineWindows(executable, explicitDirectory); }, "custom forwarding command is not removed by own-hook disconnect");
            Reject(delegate { ClaudeUsageService.InstallStatusLineWindows(executable, explicitDirectory); }, "custom forwarding command is not overwritten by own-hook install");
            Check(File.ReadAllText(configPath) == custom, "custom forwarding remains byte-for-byte unchanged");
            string repairDirectory = Path.Combine(root, "repair custom config");
            string previousExecutable = Path.Combine(root, "previous-custom-install", "UsageWidget.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(previousExecutable)); File.Copy(executable, previousExecutable);
            ClaudeUsageService.InstallStatusLineWindows(previousExecutable, repairDirectory);
            string repairConfig = Path.Combine(repairDirectory, "settings.json");
            string beforeRepair = File.ReadAllText(repairConfig);
            Check(ClaudeUsageService.StatusLineStatusWindows(executable, repairDirectory).Contains("복구") && !ClaudeUsageService.HasCustomStatusLineWindows(executable, repairDirectory), "a moved own hook with Windows source is recognized as repairable, not custom");
            ClaudeUsageService.InstallStatusLineWindows(executable, repairDirectory);
            Check(ClaudeUsageService.IsStatusLineInstalledWindows(executable, repairDirectory) && Directory.GetFiles(repairDirectory, "*.bak").Any(file => File.ReadAllText(file) == beforeRepair), "repair preserves the custom source identity and exact old settings backup");
            ClaudeUsageService.RemoveStatusLineWindows(executable, repairDirectory);
            Check(!ClaudeUsageService.IsStatusLineInstalledWindows(executable, repairDirectory), "source-specific own hook still disconnects without weakening custom-hook protection");
            foreach (string source in new[] { "win:../cache", "win:" + new string('G',64), "win:" + new string('a',63), "win:" + new string('a',65) })
                Reject(delegate { ClaudeUsageService.CaptureJson(valid, now, source); }, "malformed Windows source cannot select a file path");
            foreach (string directory in new[] { "relative", @"C:relative", @"\rooted", @"\\server\share", "C:\\invalid\npath", "C:\\wild*card", "C:\\alternate:stream", "C:\\" + new string('x',2048) })
                Reject(delegate { ClaudeUsageService.ResolveWindowsConfigDirectory(directory); }, "invalid config path is rejected without file access");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", "relative");
            Reject(delegate { ClaudeUsageService.ResolveWindowsConfigDirectory(null); }, "invalid environment override is not silently replaced by the default folder");
            Check(ClaudeUsageService.ResolveWindowsConfigDirectory(explicitDirectory) == explicitDirectory, "explicit valid directory can recover from invalid environment setting");
            ClaudeUsageService.ConfigDirectoryOverride = previousOverride;
            Check(ClaudeUsageService.ResolveWindowsConfigDirectory(explicitDirectory) == previousOverride && ClaudeUsageService.GetWindowsSourceId(null) == "", "test-only override wins and preserves isolated legacy fixtures");
        } finally {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousEnvironment);
            ClaudeUsageService.ConfigDirectoryOverride = previousOverride;
        }
    }

    private static void TestWslBridge(string root, string executable, string valid, DateTimeOffset now)
    {
        string first = "Fixture Ubuntu", second = "Fixture Debian";
        string firstSource = ClaudeUsageService.GetSourceId(first), secondSource = ClaudeUsageService.GetSourceId(second);
        Check(firstSource != secondSource && firstSource.Length == 68, "different distros have distinct bounded source identities");
        ClaudeUsageService.CaptureJson(valid, now);
        ClaudeUsageService.CaptureJson("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":5}}}", now, firstSource);
        ClaudeUsageService.CaptureJson("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":60}}}", now, secondSource);
        Check(ClaudeUsageService.ReadLatest().Snapshot.Rows[0].RemainingPercent == 76.5 && ClaudeUsageService.ReadLatest(first).Snapshot.Rows[0].RemainingPercent == 95 && ClaudeUsageService.ReadLatest(second).Snapshot.Rows[0].RemainingPercent == 40, "Windows and each WSL distro keep separate quota snapshots");
        foreach (string invalid in new[] { "../windows", "wsl:../config", "wsl:" + new string('G', 64), "wsl:" + new string('a', 65) }) Reject(delegate { ClaudeUsageService.CaptureJson(valid, now, invalid); }, "invalid source cannot choose arbitrary storage path");
        string firstCache = Path.Combine(SettingsStore.DataDirectory, "claude-usage-" + firstSource.Substring(4) + ".json");
        File.WriteAllText(firstCache, File.ReadAllText(firstCache).Replace(firstSource, secondSource));
        Reject(delegate { ClaudeUsageService.ReadLatest(first); }, "mismatched source metadata is rejected");
        string command = ClaudeUsageService.MakeWslCommandFromLinuxPath("/mnt/c/widget 'quote' $(touch BAD) &/UsageWidget.exe", firstSource);
        Check(command.StartsWith("'/mnt/c/widget '\"'\"'quote'\"'\"' $(touch BAD) &/UsageWidget.exe' --claude-statusline", StringComparison.Ordinal) && command.EndsWith("'" + firstSource + "'", StringComparison.Ordinal), "Linux command shell-quotes path and explicit source as literals");
        Reject(delegate { ClaudeUsageService.MakeWslCommandFromLinuxPath("/mnt/c/evil\ncommand", firstSource); }, "control characters rejected from hook path");

        byte[] settings = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"permissions\":{\"deny\":[\"Bash(rm*)\"]},\"theme\":\"dark\"}")).ToArray();
        byte[] original = settings.ToArray();
        byte[] backup = null;
        int calls = 0, writes = 0, writeExit = 0;
        bool interop = true, concurrentChange = false, storageFailure = false;
        ClaudeUsageService.WslProbeOverride = d => Task.FromResult(new WslDistribution { Name = d, HomePath = "/home/fixture user", ClaudePath = "/home/fixture user/.local/bin/claude" });
        ClaudeUsageService.WslPathOverride = (d, p) => Task.FromResult("/mnt/c/widget 'quote'/UsageWidget.exe");
        ClaudeUsageService.WslExecuteOverride = (d, script, argv, input) => {
            calls++;
            Check(d == first, "WSL hook operation stays in exactly the selected distro");
            if (script == "exec \"$1\" --claude-bridge-check") return Task.FromResult(new WslCommandResult { ExitCode = storageFailure ? 3 : interop ? 0 : 126, Output = storageFailure ? "nephylo-claude-bridge-storage" : interop ? "nephylo-claude-bridge-v1\n" : "" });
            Check(argv[0] == "/home/fixture user/.claude/settings.json", "settings path is a positional literal in the default user's home");
            if (script == ClaudeUsageService.WslReadConfigScript) return Task.FromResult(new WslCommandResult { Output = settings == null ? "MISSING" : "DATA:" + Convert.ToBase64String(settings) });
            Check(script == ClaudeUsageService.WslWriteConfigScript, "only reviewed configuration mutation script executes");
            string[] lines = input.Split('\n');
            Check(lines[0] == (settings == null ? "MISSING" : Convert.ToBase64String(settings)), "original config compared byte for byte, including UTF8 BOM");
            if (writeExit != 0) return Task.FromResult(new WslCommandResult { ExitCode = writeExit });
            if (concurrentChange) return Task.FromResult(new WslCommandResult { ExitCode = 44 });
            backup = settings == null ? null : settings.ToArray();
            settings = Convert.FromBase64String(lines[1]); writes++;
            return Task.FromResult(new WslCommandResult { Output = "OK" });
        };
        storageFailure = true;
        Exception storageError = null;
        try { ClaudeUsageService.InstallStatusLineAsync(executable, first).GetAwaiter().GetResult(); } catch (Exception error) { storageError = error; }
        Check(storageError is ConnectionException && storageError.Message.Contains("쓰기 권한") && writes == 0 && settings.SequenceEqual(original), "WSL storage failure is diagnosed before changing settings");
        storageFailure = false;
        ClaudeUsageService.InstallStatusLineAsync(executable, first).GetAwaiter().GetResult();
        Check(backup.SequenceEqual(original), "WSL install passes exact original bytes for backup");
        var parsed = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(settings));
        Check(parsed.ContainsKey("permissions") && Object.Equals(parsed["theme"], "dark"), "WSL install preserves unrelated configuration");
        int cachedCalls = calls;
        Check(ClaudeUsageService.IsStatusLineInstalled(executable, first) && ClaudeUsageService.StatusLineStatus(executable, first).Contains("연결됨") && calls == cachedCalls, "cached WSL status performs no WSL call or UNC access");
        Check(!ClaudeUsageService.HasCustomStatusLine(executable, first) && calls == cachedCalls, "owned WSL hook is not reported as a custom chain and cached status stays nonblocking");
        Check(ClaudeUsageService.GetStatusLineCommandAsync(executable, first, "Z:\\ignored-Windows-config").GetAwaiter().GetResult() == ClaudeUsageService.MakeWslCommandFromLinuxPath("/mnt/c/widget 'quote'/UsageWidget.exe", firstSource), "public WSL command preserves its source and ignores Windows-only directory override");
        ClaudeUsageService.InstallStatusLineAsync(executable, first).GetAwaiter().GetResult();
        Check(writes == 1, "WSL install is idempotent");
        ClaudeUsageService.RemoveStatusLineAsync(executable, first).GetAwaiter().GetResult();
        Check(!ClaudeUsageService.IsStatusLineInstalled(executable, first) && !Encoding.UTF8.GetString(settings).Contains("statusLine"), "WSL disconnect removes only exact hook and updates local manifest");
        settings = Encoding.UTF8.GetBytes("{\"statusLine\":{\"type\":\"command\",\"command\":\"custom\"},\"theme\":\"dark\"}");
        original = settings.ToArray();
        Reject(delegate { ClaudeUsageService.InstallStatusLineAsync(executable, first).GetAwaiter().GetResult(); }, "WSL install preserves custom hook");
        Check(ClaudeUsageService.HasCustomStatusLine(executable, first), "rejected automatic install records discovered custom hook for explicit UI opt-in");
        Reject(delegate { ClaudeUsageService.RemoveStatusLineAsync(executable, first).GetAwaiter().GetResult(); }, "WSL disconnect preserves custom hook");
        Check(settings.SequenceEqual(original), "custom WSL hook remains byte-identical");
        Check(ClaudeUsageService.RefreshStatusLineAsync(executable, first).GetAwaiter().GetResult().Contains("기존"), "explicit WSL refresh detects custom settings");
        cachedCalls = calls;
        Check(ClaudeUsageService.HasCustomStatusLine(executable, first, "Z:\\ignored-Windows-config") && calls == cachedCalls, "custom WSL hook lookup uses only the local verified manifest");
        settings = Encoding.UTF8.GetBytes("{ invalid JSON"); original = settings.ToArray();
        Reject(delegate { ClaudeUsageService.InstallStatusLineAsync(executable, first).GetAwaiter().GetResult(); }, "malformed WSL settings JSON is rejected");
        Check(settings.SequenceEqual(original), "malformed WSL settings remain byte-identical");
        settings = new byte[] { 0xFF, 0xFE, 0xFD }; original = settings.ToArray();
        Reject(delegate { ClaudeUsageService.InstallStatusLineAsync(executable, first).GetAwaiter().GetResult(); }, "invalid UTF8 WSL settings are rejected");
        Check(settings.SequenceEqual(original), "invalid UTF8 settings remain byte-identical");
        settings = Encoding.UTF8.GetBytes("{}"); original = settings.ToArray(); interop = false;
        Reject(delegate { ClaudeUsageService.InstallStatusLineAsync(executable, first).GetAwaiter().GetResult(); }, "disabled WSL interop blocks installation before mutation");
        Check(settings.SequenceEqual(original), "interop failure preserves settings");
        interop = true; concurrentChange = true;
        Reject(delegate { ClaudeUsageService.InstallStatusLineAsync(executable, first).GetAwaiter().GetResult(); }, "concurrent WSL settings mutation rejects stale update");
        Check(settings.SequenceEqual(original), "concurrent-write conflict preserves current settings");
        concurrentChange = false; writeExit = 45;
        string diagnostic = "";
        try { ClaudeUsageService.InstallStatusLineAsync(executable, first).GetAwaiter().GetResult(); } catch (Exception error) { diagnostic = error.Message; }
        Check(diagnostic.Contains("이전 버전") && diagnostic.Contains("nephylo-lock") && settings.SequenceEqual(original), "unowned legacy lock gets explicit recovery guidance without changing settings");
        writeExit = 46; diagnostic = "";
        try { ClaudeUsageService.InstallStatusLineAsync(executable, first).GetAwaiter().GetResult(); } catch (Exception error) { diagnostic = error.Message; }
        Check(diagnostic.Contains("flock") && settings.SequenceEqual(original), "missing lifetime lock utility gets actionable guidance without changing settings");
        ClaudeUsageService.WslExecuteOverride = null; ClaudeUsageService.WslProbeOverride = null; ClaudeUsageService.WslPathOverride = null;
    }

    private static void TestLiveWsl(string distribution, string executable)
    {
        var created = WslRuntime.ExecuteAsync(distribution, "mktemp -d /tmp/nephylo-claude-test-XXXXXX", new string[0]).GetAwaiter().GetResult();
        string directory = created.Output.Trim();
        Check(created.ExitCode == 0 && directory.StartsWith("/tmp/nephylo-claude-test-", StringComparison.Ordinal) && directory.IndexOfAny(new[] { '\n', '\r', '\0' }) < 0, "isolated Linux fixture created");
        string path = directory + "/config 'quoted' $(literal).json", backup = directory + "/backup.json";
        try {
            byte[] before = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"theme\":\"dark\"}\n")).ToArray();
            byte[] after = Encoding.UTF8.GetBytes("{\"theme\":\"light\"}");
            var result = WslRuntime.ExecuteAsync(distribution, "umask 077; base64 -d > \"$1\"", new[] { path }, Convert.ToBase64String(before)).GetAwaiter().GetResult();
            Check(result.ExitCode == 0, "Linux fixture bytes written as stdin");
            result = WslRuntime.ExecuteAsync(distribution, ClaudeUsageService.WslReadConfigScript, new[] { path }).GetAwaiter().GetResult();
            Check(result.ExitCode == 0 && Convert.FromBase64String(result.Output.Substring(5)).SequenceEqual(before), "live Linux bounded reader preserves original BOM and bytes");
            string input = Convert.ToBase64String(before) + "\n" + Convert.ToBase64String(after) + "\n";
            result = WslRuntime.ExecuteAsync(distribution, ClaudeUsageService.WslWriteConfigScript, new[] { path, backup }, input).GetAwaiter().GetResult();
            Check(result.ExitCode == 0 && result.Output.Trim() == "OK", "live Linux atomic writer accepts literal unusual path");
            result = WslRuntime.ExecuteAsync(distribution, "base64 < \"$1\"; printf '\\nMODE:'; stat -c %a -- \"$2\"; find \"$3\" -name '.nephylo-*'", new[] { backup, path, directory }).GetAwaiter().GetResult();
            string[] split = result.Output.Split(new[] { "MODE:" }, StringSplitOptions.None);
            Check(Convert.FromBase64String(split[0]).SequenceEqual(before) && split[1].Trim() == "600", "live backup preserves exact bytes, file mode retained, temporary copies cleaned");
            result = WslRuntime.ExecuteAsync(distribution, ClaudeUsageService.WslWriteConfigScript, new[] { path, directory + "/stale-backup.json" }, input).GetAwaiter().GetResult();
            Check(result.ExitCode == 44, "live compare-and-replace rejects stale original");
            result = WslRuntime.ExecuteAsync(distribution, ClaudeUsageService.WslReadConfigScript, new[] { path }).GetAwaiter().GetResult();
            Check(result.ExitCode == 0 && Convert.FromBase64String(result.Output.Substring(5)).SequenceEqual(after), "live conflict preserves existing destination bytes");
            result = WslRuntime.ExecuteAsync(distribution, "find \"$1\" -name '.nephylo-*'", new[] { directory }).GetAwaiter().GetResult();
            Check(result.ExitCode == 0 && String.IsNullOrWhiteSpace(result.Output), "failed live update cleans temporary files while lifetime-lock files remain reusable");
            result = WslRuntime.ExecuteAsync(distribution, "ln -s -- \"$1\" \"$2\"", new[] { path, directory + "/link.json" }).GetAwaiter().GetResult();
            Check(result.ExitCode == 0, "Linux symlink fixture created");
            result = WslRuntime.ExecuteAsync(distribution, ClaudeUsageService.WslReadConfigScript, new[] { directory + "/link.json" }).GetAwaiter().GetResult();
            Check(result.ExitCode == 41, "live symlink settings rejected");
            string linuxExe = WslRuntime.WindowsToLinuxPathAsync(distribution, executable).GetAwaiter().GetResult();
            result = WslRuntime.ExecuteAsync(distribution, "exec \"$1\" --claude-bridge-check", new[] { linuxExe }).GetAwaiter().GetResult();
            Check(result.ExitCode == 0 && result.Output.Trim() == "nephylo-claude-bridge-v1", "live WSL interop invokes the Windows helper without normal UI");
            string sourceId = ClaudeUsageService.GetSourceId(distribution);
            string isolatedData = SettingsStore.DataDirectory;
            result = WslRuntime.ExecuteAsync(distribution, "exec \"$1\" --claude-statusline --claude-source \"$2\" --test-local-data \"$3\"", new[] { linuxExe, sourceId, isolatedData }, "{\"secret\":\"PRIVATE_WSL_INPUT\",\"rate_limits\":{\"five_hour\":{\"used_percentage\":12}}}").GetAwaiter().GetResult();
            Check(result.ExitCode == 0 && ClaudeUsageService.ReadLatest(distribution).Snapshot.Rows[0].RemainingPercent == 88, "live WSL stdin reaches Windows helper and selected source cache");
            Check(!result.Output.Contains("PRIVATE") && !File.ReadAllText(Path.Combine(isolatedData, "claude-usage-" + sourceId.Substring(4) + ".json")).Contains("PRIVATE"), "live bridge exposes no unrelated input in stdout or cache");
        } finally {
            var clean = WslRuntime.ExecuteAsync(distribution, "case \"$1\" in /tmp/nephylo-claude-test-*) [ -d \"$1\" ] && [ ! -L \"$1\" ] && rm -rf -- \"$1\" ;; *) exit 1 ;; esac", new[] { directory }).GetAwaiter().GetResult();
            Check(clean.ExitCode == 0, "only isolated Linux fixture removed");
        }
    }
}
