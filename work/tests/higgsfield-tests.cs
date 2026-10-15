using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using UsageWidget;

public static class HiggsfieldTests
{
    private static int count;
    private const string AutoWorkspace = "926d3ed5-92d7-4e57-b6e7-4ae84f9d6414";
    private const string SecondWorkspace = "29e3ee8a-33c8-4b65-95a3-bbf4d0cb9f5c";
    private static string WorkspaceJson(string id, string name, bool selected)
    {
        return "{\"id\":\"" + id + "\",\"name\":" + (name == null ? "null" : "\"" + name + "\"") + ",\"plan_type\":\"starter\",\"credits\":120.5,\"is_selected\":" + (selected ? "true" : "false") + ",\"user_role\":\"owner\"}";
    }
    private static void Check(bool condition, string description) { if (!condition) throw new Exception(description); count++; }
    private static void Reject(Action action, string description) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, description); }
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        // Real subprocess fixtures exercise timeouts, cancellation, large output and error redaction.
        if (args.Length > 0 && (args[0] == "account" || args[0] == "auth" || args[0] == "workspace"))
        {
            string pidPath = Environment.GetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_PID");
            if (!String.IsNullOrEmpty(pidPath)) File.WriteAllText(pidPath, Process.GetCurrentProcess().Id.ToString());
            string mode = Environment.GetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE");
            string tracePath = Environment.GetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_TRACE");
            if (!String.IsNullOrEmpty(tracePath)) File.AppendAllText(tracePath, args[0] + "|" + Environment.GetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID") + Environment.NewLine);
            if (mode == "wait") { Thread.Sleep(20000); return 0; }
            if (mode == "large") { Console.WriteLine(new String('x', 70000)); Thread.Sleep(20000); return 0; }
            if (mode == "error") { Console.Error.WriteLine("PRIVATE_TOKEN PRIVATE_EMAIL not authenticated; use auth login"); return 1; }
            if (mode == "workspace") { Console.Error.WriteLine("Error: No workspace selected. Hint: Run: hf workspace set <workspace_id>"); return 4; }
            if (mode == "membership") { Console.Error.WriteLine("PRIVATE_TOKEN PRIVATE_EMAIL workspace_membership_required"); return 1; }
            if (mode != null && mode.StartsWith("mixed-", StringComparison.Ordinal))
            {
                Console.WriteLine("No workspace selected. Hint: run auth login first.");
                Console.Error.WriteLine("PRIVATE_TOKEN PRIVATE_EMAIL " + (mode == "mixed-membership" ? "workspace_membership_required" : mode == "mixed-expired" ? "session expired" : mode == "mixed-unauthorized" ? "unauthorized" : "not authenticated"));
                return 1;
            }
            if (mode == "env") { Console.WriteLine("{\"credits\":1,\"subscription_plan_type\":\"" + Environment.GetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID") + "\"}"); return 0; }
            if (args[0] == "auth") { Console.WriteLine("Private auth result is discarded"); return 0; }
            if (args[0] == "workspace")
            {
                if (mode == "auto-list-wait") {
                    string readyName = Environment.GetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_READY_EVENT");
                    if (!String.IsNullOrEmpty(readyName)) using (var ready = EventWaitHandle.OpenExisting(readyName)) ready.Set();
                    Thread.Sleep(20000); return 0;
                }
                if (mode == "auto-list-large") { Console.WriteLine(new String('x', 70000)); Thread.Sleep(20000); return 0; }
                if (mode == "auto-list-auth") { Console.Error.WriteLine("PRIVATE_TOKEN not authenticated"); return 1; }
                if (mode == "auto-empty") { Console.WriteLine("[]"); return 0; }
                if (mode == "auto-invalid") { Console.WriteLine("[{}]"); return 0; }
                if (mode == "auto-multi" || mode == "auto-selected" || mode == "auto-both-selected")
                {
                    Console.WriteLine("[" + WorkspaceJson(AutoWorkspace, "Primary", mode == "auto-both-selected") + "," + WorkspaceJson(SecondWorkspace, "Second", mode == "auto-selected" || mode == "auto-both-selected") + "]"); return 0;
                }
                Console.WriteLine("[" + WorkspaceJson(AutoWorkspace, mode == "auto-null-name" ? null : "Primary", false) + "]"); return 0;
            }
            if (mode != null && mode.StartsWith("auto", StringComparison.Ordinal))
            {
                string workspace = Environment.GetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID");
                string expected = mode == "auto-selected" ? SecondWorkspace : AutoWorkspace;
                if (workspace != expected) { Console.Error.WriteLine(mode == "auto-login-hint" ? "No workspace selected. Hint: run auth login if needed." : "No workspace selected"); return 4; }
                if (mode == "auto-retry-membership") { Console.Error.WriteLine("workspace_membership_required"); return 1; }
            }
            Console.WriteLine("{\"credits\":120.5,\"subscription_plan_type\":\"pro\",\"email\":\"PRIVATE_EMAIL\"}"); return 0;
        }
        string root = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "higgsfield-isolated-" + Guid.NewGuid().ToString("N"));
        string priorPath = Environment.GetEnvironmentVariable("PATH");
        string priorWorkspace = Environment.GetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID");
        string priorTrace = Environment.GetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_TRACE");
        string priorReady = Environment.GetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_READY_EVENT");
        bool passed = false;
        try
        {
            Directory.CreateDirectory(root);
            SettingsStore.DataDirectoryOverride = Path.Combine(root, "data");
            var service = new HiggsfieldUsageService();
            // Fixture field names were confirmed in the official v1.1.26 Windows Go binary;
            // this is not a recorded live user's account response.
            var result = HiggsfieldUsageService.ParseWebStatus("{\"credits\":120.5,\"subscription_plan_type\":\"pro\",\"email\":\"PRIVATE_EMAIL\",\"api_balance\":999}");
            Check(result.Plan == "pro" && result.Rows.Single().Remaining == 120.5, "official account schema parsed");
            Check(!result.Rows[0].RemainingPercent.HasValue && !result.Rows[0].Limit.HasValue && !result.Rows[0].ResetsAt.HasValue, "no inferred percent limit or renewal");
            Check(result.Rows[0].DisplayValue == "120.5", "credits value formatting");
            Check(!result.Note.Contains("PRIVATE") && !result.Rows[0].DisplaySubtitle.Contains("PRIVATE"), "email not retained");
            Check(HiggsfieldUsageService.ParseWebStatus("{\"credits\":0}").Rows[0].Remaining == 0, "explicit zero remains valid");
            Check(HiggsfieldUsageService.ParseWebStatus("{\"credits\":1,\"subscription_plan_type\":\"bad\\nplan\"}").Plan == "웹 구독", "plan controls sanitized");
            foreach (string invalid in new[] { "{}", "[]", "null", "bad", "{\"balance\":10}", "{\"credits\":\"12\"}", "{\"credits\":true}", "{\"credits\":null}", "{\"credits\":-1}", "{\"credits\":1000000000001}", "{\"result\":{\"credits\":5}}", new String('x', 65537) })
                Reject(() => HiggsfieldUsageService.ParseWebStatus(invalid), "unknown or invalid schema fails rather than zero");

            var workspaceChoices = HiggsfieldUsageService.ParseWorkspaces("[" + WorkspaceJson(AutoWorkspace.ToUpperInvariant(), "Primary", false) + "]");
            Check(workspaceChoices.Count == 1 && workspaceChoices[0].Id == AutoWorkspace && workspaceChoices[0].Name == "Primary" && workspaceChoices[0].PlanType == "starter" && !workspaceChoices[0].IsSelected, "confirmed root array schema retains normalized safe choice fields");
            Check(HiggsfieldUsageService.ParseWorkspaces("[]").Count == 0, "empty workspace list is distinguished from malformed schema");
            Reject(() => ((System.Collections.Generic.IList<HiggsfieldWorkspace>)workspaceChoices).Clear(), "workspace choices are read only");
            foreach (string name in new[] { (string)null, "", "    " })
            {
                workspaceChoices = HiggsfieldUsageService.ParseWorkspaces("[" + WorkspaceJson(AutoWorkspace.ToUpperInvariant(), name, false) + "]");
                Check(workspaceChoices.Count == 1 && workspaceChoices[0].Id == AutoWorkspace && workspaceChoices[0].Name == "Workspace " + AutoWorkspace.Substring(0, 8), "null, empty and blank workspace names use only a normalized ID label");
            }
            workspaceChoices = HiggsfieldUsageService.ParseWorkspaces("[" + WorkspaceJson(AutoWorkspace, null, false).Replace("\"name\":null,", "") + "]");
            Check(workspaceChoices.Count == 1 && workspaceChoices[0].Name == "Workspace " + AutoWorkspace.Substring(0, 8), "missing optional workspace name uses an ID label");
            foreach (string invalid in new[] {
                "{}", "null", "bad", "[null]", "[{}]", "{\"workspaces\":[]}",
                "[" + WorkspaceJson("not-a-uuid", "Primary", false) + "]",
                "[" + WorkspaceJson("00000000-0000-0000-0000-000000000000", "Primary", false) + "]",
                "[" + WorkspaceJson(AutoWorkspace.Replace("-", ""), "Primary", false) + "]",
                "[" + WorkspaceJson(AutoWorkspace, "Primary", false) + "," + WorkspaceJson(AutoWorkspace.ToUpperInvariant(), "Duplicate", false) + "]",
                "[" + WorkspaceJson(AutoWorkspace, null, false).Replace("\"name\":null", "\"name\":123") + "]",
                "[" + WorkspaceJson(AutoWorkspace, null, false).Replace("\"name\":null", "\"name\":false") + "]",
                "[" + WorkspaceJson(AutoWorkspace, null, false).Replace("\"name\":null", "\"name\":{}") + "]",
                "[" + WorkspaceJson(AutoWorkspace, new String('n', 161), false) + "]",
                "[" + WorkspaceJson(AutoWorkspace, "Bad\\nName", false) + "]",
                "[" + WorkspaceJson(AutoWorkspace, " \\n ", false) + "]",
                "[" + WorkspaceJson(AutoWorkspace, "Primary", false).Replace("\"is_selected\":false", "\"is_selected\":\"false\"") + "]",
                "[" + WorkspaceJson(AutoWorkspace, "Primary", false).Replace("\"plan_type\":\"starter\"", "\"plan_type\":\"bad\\nplan\"") + "]",
                "[" + WorkspaceJson(AutoWorkspace, "Primary", false).Replace("\"id\":\"" + AutoWorkspace + "\"", "\"id\":123") + "]",
                "[" + String.Join(",", Enumerable.Repeat(WorkspaceJson(AutoWorkspace, "Primary", false), 101)) + "]", new String('x', 65537)
            }) Reject(() => HiggsfieldUsageService.ParseWorkspaces(invalid), "invalid workspace schema is rejected without partial choices");

            var settings = HiggsfieldSettingsStore.Load();
            Check(!settings.WebConnected && !settings.ApiBalanceUsd.HasValue, "fresh install disconnected");
            settings.WebConnected = true; settings.CliPath = Assembly.GetExecutingAssembly().Location;
            settings.WorkspaceId = "926d3ed5-92d7-4e57-b6e7-4ae84f9d6414";
            settings.ApiBalanceUsd = 35.12; settings.ApiObservedAt = DateTimeOffset.UtcNow.AddHours(-4);
            HiggsfieldSettingsStore.Save(settings);
            var loaded = HiggsfieldSettingsStore.Load();
            Check(loaded.WebConnected && loaded.CliPath == settings.CliPath && loaded.ApiBalanceUsd == 35.12 && loaded.ApiObservedAt == settings.ApiObservedAt, "settings roundtrip");
            Check(loaded.WorkspaceId == settings.WorkspaceId, "optional workspace persists");
            string settingsFile = Path.Combine(SettingsStore.DataDirectory, "higgsfield-settings.json");
            Check(!File.ReadAllText(settingsFile).Contains("token"), "no credential field saved");
            result = HiggsfieldUsageService.ReadApiManual(loaded);
            Check(result.Plan == "수동 입력" && result.Rows[0].DisplayValue == "$35.12", "API dollars clearly manual");
            Check(result.Rows[0].DisplaySubtitle == "API 선불 잔액 · USD", "manual unit explicit; UI renders original observation timestamp separately");
            Check(!result.Rows[0].RemainingPercent.HasValue && !result.Rows[0].ResetsAt.HasValue, "API no fabricated quota");
            HiggsfieldUsageService.ReadApiManual(loaded);
            Check(loaded.ApiObservedAt == settings.ApiObservedAt, "manual reads preserve original timestamp");
            settings.ApiBalanceUsd = 0; HiggsfieldSettingsStore.Save(settings);
            Check(HiggsfieldSettingsStore.Load().ApiBalanceUsd == 0, "manual zero persists");
            settings.ApiBalanceUsd = Double.NaN; Reject(() => HiggsfieldSettingsStore.Save(settings), "nan rejected");
            Check(HiggsfieldSettingsStore.Load().ApiBalanceUsd == 0, "invalid save preserves file");
            settings.ApiBalanceUsd = -1; Reject(() => HiggsfieldSettingsStore.Save(settings), "negative dollars rejected");
            settings.ApiBalanceUsd = Double.PositiveInfinity; Reject(() => HiggsfieldSettingsStore.Save(settings), "infinite dollars rejected");
            settings.ApiBalanceUsd = 1; settings.ApiObservedAt = null; Reject(() => HiggsfieldSettingsStore.Save(settings), "manual date required");
            settings.ApiBalanceUsd = null; HiggsfieldSettingsStore.Save(settings);
            Check(!HiggsfieldSettingsStore.Load().ApiObservedAt.HasValue, "remove clears observation");
            Reject(() => HiggsfieldUsageService.ReadApiManual(settings), "no API balance reports setup needed");
            Check(!Directory.GetFiles(SettingsStore.DataDirectory, "*.tmp").Any(), "atomic temp files cleaned");
            foreach (string invalid in new[] { "hf.exe", "C:relative.exe", "\\root-relative.exe", "C:\\higgsfield.cmd", "C:\\tool.exe\" --spend", "C:\\x\nhf.exe" })
            {
                settings.CliPath = invalid; Reject(() => HiggsfieldSettingsStore.Save(settings), "unsafe path rejected");
            }
            settings.CliPath = Assembly.GetExecutingAssembly().Location;
            foreach (string invalid in new[] { "not-a-uuid", "00000000-0000-0000-0000-000000000000", "926d3ed592d74e57b6e74ae84f9d6414", "926d3ed5-92d7-4e57-b6e7-4ae84f9d6414\nARGUMENT" })
            {
                settings.WorkspaceId = invalid;
                Reject(() => HiggsfieldSettingsStore.Save(settings), "workspace must be nonempty UUID or blank");
            }
            File.WriteAllText(settingsFile, "{\"WebConnected\":\"true\",\"ApiBalanceUsd\":\"10\"}");
            Check(!HiggsfieldSettingsStore.Load().WebConnected && !HiggsfieldSettingsStore.Load().ApiBalanceUsd.HasValue, "storage strict types");
            File.WriteAllText(settingsFile, "{broken"); Check(!HiggsfieldSettingsStore.Load().WebConnected, "malformed storage defaults disconnected");
            File.WriteAllText(settingsFile, new String('x', 9000)); Check(!HiggsfieldSettingsStore.Load().WebConnected, "oversized storage bounded");

            string managed = Path.Combine(SettingsStore.DataDirectory, "tools", "higgsfield.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(managed)); File.Copy(Assembly.GetExecutingAssembly().Location, managed);
            Check(HiggsfieldUsageService.FindCliPath() == managed, "managed CLI found first");
            string pidFile = Path.Combine(root, "pid.txt");
            string traceFile = Path.Combine(root, "trace.txt");
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_PID", pidFile);
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_TRACE", traceFile);
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "ok");
            result = service.ReadWebAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Rows[0].Remaining == 120.5, "real hidden subprocess status output parsed");
            Check(File.ReadAllLines(traceFile).Length == 1 && File.ReadAllLines(traceFile)[0].StartsWith("account|", StringComparison.Ordinal), "existing CLI default succeeds without querying or changing workspaces");
            var defaultConnection = service.ReadWebConnectionAsync(managed, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(defaultConnection.WorkspaceId == String.Empty && defaultConnection.Snapshot.Rows[0].Remaining == 120.5, "successful existing default does not invent a stored workspace ID");
            result = service.LoginAsync(managed, CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Rows[0].Remaining == 120.5, "login returns verified account status");
            const string defaultWorkspace = "29e3ee8a-33c8-4b65-95a3-bbf4d0cb9f5c";
            const string explicitWorkspace = "926d3ed5-92d7-4e57-b6e7-4ae84f9d6414";
            Environment.SetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID", defaultWorkspace);
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "env");
            result = service.ReadWebAsync(managed, explicitWorkspace, CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Plan == explicitWorkspace, "workspace override reaches child environment");
            Check(Environment.GetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID") == defaultWorkspace, "workspace override leaves parent untouched");
            result = service.ReadWebAsync(managed, CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Plan == defaultWorkspace, "no override preserves CLI default environment");
            int beforeInvalid = File.ReadAllLines(traceFile).Length;
            Reject(() => service.ReadWebAsync(managed, "invalid;command", CancellationToken.None).GetAwaiter().GetResult(), "invalid workspace rejected before spawn");
            Reject(() => service.LoginConnectionAsync(managed, "invalid;command", CancellationToken.None).GetAwaiter().GetResult(), "invalid login workspace rejected before spawn");
            Check(File.ReadAllLines(traceFile).Length == beforeInvalid, "invalid workspace never starts auth or lookup subprocesses");

            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "auto"); File.WriteAllText(traceFile, "");
            var automatic = service.ReadWebConnectionAsync(managed, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(automatic.WorkspaceId == AutoWorkspace && automatic.Snapshot.Rows[0].Remaining == 120.5, "a single unselected workspace automatically resolves to a verified connection");
            var calls = File.ReadAllLines(traceFile);
            Check(calls.Length == 3 && calls[0].StartsWith("account|", StringComparison.Ordinal) && calls[1].StartsWith("workspace|", StringComparison.Ordinal) && calls[2] == "account|" + AutoWorkspace, "missing workspace causes exactly one list and one child-scoped retry");
            Check(Environment.GetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID") == defaultWorkspace, "automatic resolution never changes the parent workspace");
            File.WriteAllText(traceFile, "");
            automatic = service.LoginConnectionAsync(managed, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(automatic.WorkspaceId == AutoWorkspace && File.ReadAllLines(traceFile).Length == 4 && File.ReadAllLines(traceFile)[0].StartsWith("auth|", StringComparison.Ordinal), "login success resolves the missing workspace and returns its ID for persistence");
            File.WriteAllText(traceFile, "");
            automatic = service.ReadWebConnectionAsync(managed, explicitWorkspace.ToUpperInvariant(), CancellationToken.None).GetAwaiter().GetResult();
            Check(automatic.WorkspaceId == explicitWorkspace && File.ReadAllLines(traceFile).Length == 1, "a valid explicit workspace is normalized and never enumerates other spaces");

            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "auto-null-name"); File.WriteAllText(traceFile, "");
            automatic = service.ReadWebConnectionAsync(managed, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(automatic.WorkspaceId == AutoWorkspace && automatic.Snapshot.Rows[0].Remaining == 120.5 && File.ReadAllLines(traceFile).Length == 3, "real nullable-name schema permits single-workspace automatic connection");
            File.WriteAllText(traceFile, "");
            automatic = service.LoginConnectionAsync(managed, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(automatic.WorkspaceId == AutoWorkspace && File.ReadAllLines(traceFile).Length == 4 && File.ReadAllLines(traceFile)[0].StartsWith("auth|", StringComparison.Ordinal), "login automatically resolves an unnamed workspace and returns the verified ID");
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "auto-login-hint"); File.WriteAllText(traceFile, "");
            automatic = service.ReadWebConnectionAsync(managed, String.Empty, CancellationToken.None).GetAwaiter().GetResult();
            Check(automatic.WorkspaceId == AutoWorkspace && automatic.Snapshot.Rows[0].Remaining == 120.5 && File.ReadAllLines(traceFile).Length == 3, "auth login help wording alone does not block a valid missing-workspace recovery");

            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "auto-selected"); File.WriteAllText(traceFile, "");
            Environment.SetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID", "");
            automatic = service.ReadWebConnectionAsync(managed, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(automatic.WorkspaceId == SecondWorkspace && File.ReadAllLines(traceFile).Length == 3, "exactly one selected workspace wins over list ordering");
            Environment.SetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID", defaultWorkspace);
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "auto-multi"); File.WriteAllText(traceFile, "");
            HiggsfieldWorkspaceSelectionRequiredException selection = null;
            try { service.ReadWebConnectionAsync(managed, null, CancellationToken.None).GetAwaiter().GetResult(); } catch (HiggsfieldWorkspaceSelectionRequiredException ex) { selection = ex; }
            Check(selection != null && selection.Workspaces.Count == 2 && selection.Workspaces[0].Id == AutoWorkspace && selection.Workspaces[1].Id == SecondWorkspace, "multiple unselected workspaces expose immutable choices rather than selecting an arbitrary one");
            Check(File.ReadAllLines(traceFile).Length == 2, "ambiguous workspace list never sends a status request for an arbitrary workspace");
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "auto-both-selected"); File.WriteAllText(traceFile, "");
            selection = null;
            try { service.ReadWebConnectionAsync(managed, null, CancellationToken.None).GetAwaiter().GetResult(); } catch (HiggsfieldWorkspaceSelectionRequiredException ex) { selection = ex; }
            Check(selection != null && selection.Workspaces.Count == 2 && File.ReadAllLines(traceFile).Length == 2, "inconsistent multiple selected flags require an explicit choice rather than choosing the last flag");

            foreach (string mode in new[] { "auto-empty", "auto-invalid", "auto-list-auth", "auto-retry-membership" })
            {
                Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", mode); File.WriteAllText(traceFile, "");
                string failure = null;
                try { service.ReadWebConnectionAsync(managed, null, CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidOperationException ex) { failure = ex.Message; }
                Check(failure != null && !failure.Contains("PRIVATE"), "empty, invalid, auth and denied retry failures are safely reported: " + mode);
                Check(File.ReadAllLines(traceFile).Length == (mode == "auto-retry-membership" ? 3 : 2), "workspace recovery is bounded without looping: " + mode);
                Check(Environment.GetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID") == defaultWorkspace && File.ReadAllText(settingsFile) == new String('x', 9000), "failed workspace recovery leaves parent env and saved settings unchanged: " + mode);
            }

            CheckObserverSharing(root);
            // An event after fixture initialization replaces a reader racing the
            // child's trace writer. Retain the actual process handle, not a PID
            // looked up after termination (which may already have been reused).
            for (int attempt = 1; attempt <= 10; attempt++) CheckWorkspaceCancellation(service,managed,pidFile,traceFile,attempt);
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "auto-list-wait"); File.WriteAllText(traceFile, "");
            HiggsfieldUsageService.StatusTimeoutMilliseconds = 1000;
            Reject(() => service.ReadWebConnectionAsync(managed, null, CancellationToken.None).GetAwaiter().GetResult(), "workspace enumeration timeout is bounded");
            Check(File.ReadAllLines(traceFile).Length == 2 && Exited(pidFile), "enumeration timeout kills the list process without retrying account status");
            HiggsfieldUsageService.StatusTimeoutMilliseconds = 3000;
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "auto-list-large"); File.WriteAllText(traceFile, "");
            Reject(() => service.ReadWebConnectionAsync(managed, null, CancellationToken.None).GetAwaiter().GetResult(), "workspace enumeration output is bounded");
            Check(File.ReadAllLines(traceFile).Length == 2 && Exited(pidFile), "oversized enumeration kills the owned list process");
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "error");
            File.WriteAllText(traceFile, "");
            string message = null;
            try { service.ReadWebAsync(managed, CancellationToken.None).GetAwaiter().GetResult(); } catch (Exception ex) { message = ex.Message; }
            Check(message != null && message.Contains("로그인") && !message.Contains("PRIVATE"), "error gives login guidance without raw token/email");
            Check(File.ReadAllLines(traceFile).Length == 1, "authentication errors do not enumerate or switch workspaces");
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "membership"); File.WriteAllText(traceFile, "");
            Reject(() => service.ReadWebConnectionAsync(managed, null, CancellationToken.None).GetAwaiter().GetResult(), "workspace permission errors remain errors");
            Check(File.ReadAllLines(traceFile).Length == 1, "permission errors do not switch to another workspace");
            foreach (string mode in new[] { "mixed-auth", "mixed-expired", "mixed-unauthorized", "mixed-membership" })
            {
                Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", mode); File.WriteAllText(traceFile, "");
                message = null;
                try { service.ReadWebConnectionAsync(managed, String.Empty, CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidOperationException ex) { message = ex.Message; }
                Check(message != null && message.Contains(mode == "mixed-membership" ? "조회 권한" : "필요하거나 만료") && !message.Contains("PRIVATE"), "explicit authentication or permission failures win over missing-workspace hints: " + mode);
                Check(File.ReadAllLines(traceFile).Length == 1 && File.ReadAllLines(traceFile)[0] == "account|" + defaultWorkspace, "mixed failures never enumerate or send a workspace override: " + mode);
                Check(Environment.GetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID") == defaultWorkspace && File.ReadAllText(settingsFile) == new String('x', 9000), "mixed failures leave existing environment and settings unchanged: " + mode);
            }
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "workspace");
            File.WriteAllText(traceFile, "");
            message = null;
            try { service.ReadWebAsync(managed, explicitWorkspace, CancellationToken.None).GetAwaiter().GetResult(); } catch (Exception ex) { message = ex.Message; }
            Check(message != null && message.Contains("작업 공간") && message.Contains("workspace list"), "official workspace prerequisite explained");
            Check(File.ReadAllLines(traceFile).Length == 1, "an explicit missing workspace never falls back to another workspace");
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "wait");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.CancelAfter(200);
                bool cancelled = false;
                try { service.ReadWebAsync(managed, cancellation.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "read cancellation propagated");
            }
            Check(Exited(pidFile), "cancelled subprocess killed");
            HiggsfieldUsageService.StatusTimeoutMilliseconds = 200;
            Reject(() => service.ReadWebAsync(managed, CancellationToken.None).GetAwaiter().GetResult(), "read timeout bounded");
            Check(Exited(pidFile), "timed out subprocess killed");
            HiggsfieldUsageService.LoginTimeoutMilliseconds = 200;
            Reject(() => service.LoginAsync(managed, CancellationToken.None).GetAwaiter().GetResult(), "login timeout bounded");
            Check(Exited(pidFile), "timed out login killed");
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", "large");
            HiggsfieldUsageService.StatusTimeoutMilliseconds = 3000;
            Reject(() => service.ReadWebAsync(managed, CancellationToken.None).GetAwaiter().GetResult(), "large output bounded");
            Check(Exited(pidFile), "large output subprocess killed");
            Reject(() => service.ReadWebAsync("C:\\not-installed-higgsfield.exe", CancellationToken.None).GetAwaiter().GetResult(), "missing custom CLI fails with guidance");
            passed = true;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); }
        finally
        {
            SettingsStore.DataDirectoryOverride = null;
            Environment.SetEnvironmentVariable("PATH", priorPath);
            Environment.SetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID", priorWorkspace);
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_PID", null);
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE", null);
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_TRACE", priorTrace);
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_READY_EVENT", priorReady);
            // Preserve the original failure and identify cleanup independently.
            try {
                string expected = Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)) + Path.DirectorySeparatorChar + "higgsfield-isolated-";
                if (!Path.GetFullPath(root).StartsWith(expected,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture cleanup root");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            } catch (Exception cleanup) { Console.Error.WriteLine("Fixture cleanup failed: " + cleanup); passed = false; }
        }
        if (passed) Console.WriteLine("Higgsfield tests passed: " + count);
        return passed ? 0 : 1;
    }

    private static void CheckObserverSharing(string root)
    {
        string trace = Path.Combine(root,"observer-sharing.txt"); File.WriteAllText(trace,"");
        bool blocked = false;
        using (var observer = File.OpenRead(trace)) {
            try { File.AppendAllText(trace,"workspace"); } catch (IOException) { blocked = true; }
        }
        Check(blocked,"A trace reader with default sharing can prevent the child's readiness write; use a separate event");
    }

    private static void CheckWorkspaceCancellation(HiggsfieldUsageService service,string managed,string pidFile,string traceFile,int attempt)
    {
        Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_MODE","auto-list-wait"); File.WriteAllText(traceFile,"");
        string name = @"Local\Nephylo.Higgsfield.Test." + Guid.NewGuid().ToString("N");
        using (var ready = new EventWaitHandle(false,EventResetMode.ManualReset,name))
        using (var cancellation = new CancellationTokenSource()) {
            Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_READY_EVENT",name);
            Process owned = null; bool started = false, cancelled = false; Exception operationError = null;
            var elapsed = Stopwatch.StartNew();
            var watcher = System.Threading.Tasks.Task.Factory.StartNew(() => {
                try {
                    started = ready.WaitOne(3000);
                    if (started) {
                        owned = Process.GetProcessById(Int32.Parse(File.ReadAllText(pidFile)));
                        var handle = owned.Handle;
                        if (!String.Equals(owned.MainModule.FileName,managed,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Not the owned fixture process");
                    }
                } finally { cancellation.Cancel(); }
            },CancellationToken.None,System.Threading.Tasks.TaskCreationOptions.LongRunning,System.Threading.Tasks.TaskScheduler.Default);
            try {
                try { service.ReadWebConnectionAsync(managed,null,cancellation.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex) { operationError = ex; }
                watcher.GetAwaiter().GetResult();
                bool exited = owned != null && owned.HasExited;
                Console.WriteLine("Cancellation attempt " + attempt + ": ready=" + started + ", cancelled=" + cancelled + ", exited=" + exited + ", elapsedMs=" + elapsed.ElapsedMilliseconds);
                if (operationError != null) throw operationError;
                Check(started,"Workspace subprocess signals readiness before cancellation");
                Check(cancelled,"Cancellation propagates after workspace enumeration begins");
                Check(exited,"The captured owned process has exited when the cancelled operation returns");
                Check(File.ReadAllLines(traceFile).Length == 2,"Cancelled enumeration never starts a status retry");
            } finally {
                cancellation.Cancel();
                try { watcher.GetAwaiter().GetResult(); } catch { }
                if (owned != null) {
                    try {
                        if (!owned.HasExited && String.Equals(owned.MainModule.FileName,managed,StringComparison.OrdinalIgnoreCase)) { owned.Kill(); owned.WaitForExit(2000); }
                    } finally { owned.Dispose(); }
                }
                Environment.SetEnvironmentVariable("USAGE_WIDGET_HIGGSFIELD_READY_EVENT",null);
            }
        }
    }
    private static bool Exited(string path)
    {
        if (!File.Exists(path)) return false;
        int pid = Int32.Parse(File.ReadAllText(path));
        try { using (var process = Process.GetProcessById(pid)) return process.HasExited; } catch (ArgumentException) { return true; }
    }
}
