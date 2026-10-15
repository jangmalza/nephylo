using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    public sealed class HiggsfieldSettings
    {
        public bool WebConnected;
        public string CliPath = String.Empty;
        public string WorkspaceId = String.Empty;
        public double? ApiBalanceUsd;
        public DateTimeOffset? ApiObservedAt;
    }

    // Stores display preferences only. OAuth remains entirely inside the official CLI.
    public static class HiggsfieldSettingsStore
    {
        private static readonly object Gate = new object();
        private const int MaxBytes = 8192;
        public static HiggsfieldSettings Load()
        {
            lock (Gate)
            {
                try
                {
                    string path = Path.Combine(SettingsStore.DataDirectory, "higgsfield-settings.json");
                    if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return new HiggsfieldSettings();
                    var values = Serializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                    if (values == null) return new HiggsfieldSettings();
                    object value;
                    string cli = values.TryGetValue("CliPath", out value) ? value as string : String.Empty;
                    if (!ValidCliPreference(cli)) return new HiggsfieldSettings();
                    string workspace = values.TryGetValue("WorkspaceId", out value) ? value as string : String.Empty;
                    if (!ValidWorkspaceId(workspace)) return new HiggsfieldSettings();
                    double? amount = values.TryGetValue("ApiBalanceUsd", out value) ? Numeric(value, 1000000000) : null;
                    DateTimeOffset observed;
                    DateTimeOffset? date = values.TryGetValue("ApiObservedAt", out value) && value is string && DateTimeOffset.TryParseExact((string)value, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out observed) ? (DateTimeOffset?)observed : null;
                    return new HiggsfieldSettings {
                        WebConnected = values.TryGetValue("WebConnected", out value) && value is bool && (bool)value,
                        CliPath = cli ?? String.Empty,
                        WorkspaceId = String.IsNullOrWhiteSpace(workspace) ? String.Empty : workspace.Trim().ToLowerInvariant(),
                        ApiBalanceUsd = amount.HasValue && date.HasValue ? amount : null,
                        ApiObservedAt = amount.HasValue && date.HasValue ? date : null
                    };
                }
                catch (IOException) { return new HiggsfieldSettings(); }
                catch (UnauthorizedAccessException) { return new HiggsfieldSettings(); }
                catch (ArgumentException) { return new HiggsfieldSettings(); }
                catch (InvalidOperationException) { return new HiggsfieldSettings(); }
            }
        }

        public static void Save(HiggsfieldSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (!ValidCliPreference(settings.CliPath)) throw new ArgumentException("Higgsfield 실행 파일 경로를 확인해 주세요.");
            if (!ValidWorkspaceId(settings.WorkspaceId)) throw new ArgumentException("Higgsfield 작업 공간 ID를 UUID 형식으로 입력해 주세요.");
            if (settings.ApiBalanceUsd.HasValue && (!Numeric(settings.ApiBalanceUsd.Value, 1000000000).HasValue || !settings.ApiObservedAt.HasValue))
                throw new ArgumentException("API 잔액과 입력 시간을 확인해 주세요.");
            var data = new Dictionary<string, object> {
                { "WebConnected", settings.WebConnected }, { "CliPath", settings.CliPath ?? String.Empty },
                { "WorkspaceId", String.IsNullOrWhiteSpace(settings.WorkspaceId) ? String.Empty : settings.WorkspaceId.Trim().ToLowerInvariant() },
                { "ApiBalanceUsd", settings.ApiBalanceUsd },
                { "ApiObservedAt", settings.ApiBalanceUsd.HasValue ? settings.ApiObservedAt.Value.ToString("o", CultureInfo.InvariantCulture) : null }
            };
            byte[] bytes = new UTF8Encoding(false).GetBytes(Serializer().Serialize(data));
            if (bytes.Length > MaxBytes) throw new ArgumentException("Higgsfield 설정이 너무 큽니다.");
            lock (Gate)
            {
                string path = Path.Combine(SettingsStore.DataDirectory, "higgsfield-settings.json");
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                    if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        internal static double? Numeric(object value, double maximum)
        {
            if (!(value is int || value is long || value is decimal || value is double || value is float)) return null;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return !Double.IsNaN(number) && !Double.IsInfinity(number) && number >= 0 && number <= maximum ? (double?)number : null;
        }
        internal static bool ValidCliPreference(string value)
        {
            if (String.IsNullOrEmpty(value)) return true;
            if (value.Length > 1024 || value.IndexOfAny(new[] { '\r', '\n', '"', '\0' }) >= 0) return false;
            try { return FullyQualifiedPath(value) && String.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
        }
        internal static bool FullyQualifiedPath(string value)
        {
            if (!Path.IsPathRooted(value)) return false;
            string root = Path.GetPathRoot(value);
            return root.Length > 1 && !root.EndsWith(":", StringComparison.Ordinal);
        }
        internal static bool ValidWorkspaceId(string value)
        {
            Guid id;
            return String.IsNullOrWhiteSpace(value) || (value.Length <= 80 && Guid.TryParseExact(value.Trim(), "D", out id) && id != Guid.Empty);
        }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 8 }; }
    }

    public sealed class HiggsfieldWorkspace
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string PlanType { get; private set; }
        public bool IsSelected { get; private set; }

        internal HiggsfieldWorkspace(string id, string name, string planType, bool isSelected)
        {
            Id = id; Name = name; PlanType = planType; IsSelected = isSelected;
        }
    }

    public sealed class HiggsfieldWebConnection
    {
        public UsageSnapshot Snapshot { get; private set; }
        public string WorkspaceId { get; private set; }

        internal HiggsfieldWebConnection(UsageSnapshot snapshot, string workspaceId)
        {
            Snapshot = snapshot; WorkspaceId = workspaceId;
        }
    }

    public sealed class HiggsfieldWorkspaceSelectionRequiredException : InvalidOperationException
    {
        public ReadOnlyCollection<HiggsfieldWorkspace> Workspaces { get; private set; }

        internal HiggsfieldWorkspaceSelectionRequiredException(ReadOnlyCollection<HiggsfieldWorkspace> workspaces)
            : base("Higgsfield 작업 공간을 선택해 주세요. 연결할 작업 공간이 여러 개입니다.")
        {
            Workspaces = workspaces;
        }
    }

    public sealed class HiggsfieldUsageService
    {
        private const int OutputLimit = 65536;
        internal static int StatusTimeoutMilliseconds = 20000;
        internal static int LoginTimeoutMilliseconds = 180000;

        public static string FindCliPath()
        {
            var candidates = new List<string>();
            candidates.Add(Path.Combine(SettingsStore.DataDirectory, "tools", "higgsfield.exe"));
            candidates.Add(Path.Combine(SettingsStore.InstallDirectory, "tools", "higgsfield.exe"));
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "higgsfield.exe"));
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            candidates.Add(Path.Combine(roaming, "npm", "node_modules", "@higgsfield", "cli", "vendor", "hf.exe"));
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? String.Empty).Split(Path.PathSeparator))
            {
                string directory = entry.Trim().Trim('"');
                try
                {
                    if (String.IsNullOrEmpty(directory) || !HiggsfieldSettingsStore.FullyQualifiedPath(directory)) continue;
                    candidates.Add(Path.Combine(directory, "higgsfield.exe"));
                    // Official npm's Windows shim launches this exact vendor binary. Never execute a .cmd shim or ambiguous hf.exe from PATH.
                    candidates.Add(Path.Combine(directory, "node_modules", "@higgsfield", "cli", "vendor", "hf.exe"));
                }
                catch (ArgumentException) { }
            }
            foreach (string path in candidates) if (File.Exists(path)) return Path.GetFullPath(path);
            return null;
        }

        public Task<UsageSnapshot> ReadWebAsync(CancellationToken cancellationToken) { return ReadWebAsync(null, null, cancellationToken); }
        public Task<UsageSnapshot> ReadWebAsync(string cliPath, CancellationToken cancellationToken) { return ReadWebAsync(cliPath, null, cancellationToken); }
        public async Task<UsageSnapshot> ReadWebAsync(string cliPath, string workspaceId, CancellationToken cancellationToken)
        {
            return (await ReadWebConnectionAsync(cliPath, workspaceId, cancellationToken).ConfigureAwait(false)).Snapshot;
        }

        public async Task<HiggsfieldWebConnection> ReadWebConnectionAsync(string cliPath, string workspaceId, CancellationToken cancellationToken)
        {
            string path = ResolveCli(cliPath);
            string json = null;
            bool needsWorkspace = false;
            string resolvedWorkspaceId = String.IsNullOrWhiteSpace(workspaceId) ? String.Empty : workspaceId.Trim().ToLowerInvariant();
            try
            {
                json = await RunCliAsync(path, "account status --json --no-color", resolvedWorkspaceId, StatusTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (NoWorkspaceSelectedException)
            {
                // Only a missing default may be recovered. An explicit ID, expired login,
                // permission error or arbitrary CLI failure must never switch workspaces.
                if (resolvedWorkspaceId.Length != 0) throw;
                needsWorkspace = true;
            }
            if (needsWorkspace)
            {
                var workspaces = await ListWorkspacesAsync(path, cancellationToken).ConfigureAwait(false);
                if (workspaces.Count == 0)
                    throw new InvalidOperationException("Higgsfield 계정에서 조회할 작업 공간을 찾지 못했습니다. 로그인한 계정을 확인하거나 Higgsfield 지원팀에 문의해 주세요.");
                HiggsfieldWorkspace selected = null;
                int selectedCount = 0;
                foreach (var workspace in workspaces)
                    if (workspace.IsSelected) { selected = workspace; selectedCount++; }
                if (selectedCount != 1)
                {
                    if (workspaces.Count != 1) throw new HiggsfieldWorkspaceSelectionRequiredException(workspaces);
                    selected = workspaces[0];
                }
                resolvedWorkspaceId = selected.Id;
                cancellationToken.ThrowIfCancellationRequested();
                json = await RunCliAsync(path, "account status --json --no-color", resolvedWorkspaceId, StatusTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new HiggsfieldWebConnection(ParseWebStatus(json), resolvedWorkspaceId);
        }
        public Task<UsageSnapshot> LoginAsync(CancellationToken cancellationToken) { return LoginAsync(null, null, cancellationToken); }
        public Task<UsageSnapshot> LoginAsync(string cliPath, CancellationToken cancellationToken) { return LoginAsync(cliPath, null, cancellationToken); }
        public async Task<UsageSnapshot> LoginAsync(string cliPath, string workspaceId, CancellationToken cancellationToken)
        {
            return (await LoginConnectionAsync(cliPath, workspaceId, cancellationToken).ConfigureAwait(false)).Snapshot;
        }

        public async Task<HiggsfieldWebConnection> LoginConnectionAsync(string cliPath, string workspaceId, CancellationToken cancellationToken)
        {
            string path = ResolveCli(cliPath);
            await RunCliAsync(path, "auth login --no-color", workspaceId, LoginTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await ReadWebConnectionAsync(path, workspaceId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ReadOnlyCollection<HiggsfieldWorkspace>> ListWorkspacesAsync(string cliPath, CancellationToken cancellationToken)
        {
            string path = ResolveCli(cliPath);
            string json = await RunCliAsync(path, "workspace list --json --no-color", null, StatusTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ParseWorkspaces(json);
        }

        // CLI 1.1.26 returns a root JSON array. Retain only display names, plan labels
        // and validated UUIDs; no token, role, email or balance is stored in choices.
        internal static ReadOnlyCollection<HiggsfieldWorkspace> ParseWorkspaces(string json)
        {
            if (String.IsNullOrWhiteSpace(json) || json.Length > OutputLimit) throw WorkspaceSchemaError();
            object[] values;
            try { values = new JavaScriptSerializer { MaxJsonLength = OutputLimit, RecursionLimit = 16 }.DeserializeObject(json) as object[]; }
            catch (ArgumentException) { throw WorkspaceSchemaError(); }
            catch (InvalidOperationException) { throw WorkspaceSchemaError(); }
            if (values == null || values.Length > 100) throw WorkspaceSchemaError();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var workspaces = new List<HiggsfieldWorkspace>();
            foreach (object value in values)
            {
                var workspace = value as Dictionary<string, object>;
                object item;
                string id = workspace != null && workspace.TryGetValue("id", out item) ? item as string : null;
                object nameValue;
                if (workspace == null || !workspace.TryGetValue("name", out nameValue)) nameValue = null;
                if (nameValue != null && !(nameValue is string)) throw WorkspaceSchemaError();
                string name = nameValue as string;
                string plan = workspace != null && workspace.TryGetValue("plan_type", out item) ? item as string : null;
                Guid parsed;
                if (id == null || !Guid.TryParseExact(id, "D", out parsed) || parsed == Guid.Empty ||
                    (name != null && name.Length > 160) ||
                    plan == null || String.IsNullOrWhiteSpace(plan) || plan.Length > 48 ||
                    workspace == null || !workspace.TryGetValue("is_selected", out item) || !(item is bool)) throw WorkspaceSchemaError();
                if (name != null) foreach (char c in name) if (Char.IsControl(c)) throw WorkspaceSchemaError();
                foreach (char c in plan) if (!Char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != ' ') throw WorkspaceSchemaError();
                id = parsed.ToString("D");
                if (!ids.Add(id)) throw WorkspaceSchemaError();
                // Real CLI responses may have no name. Label only the validated ID;
                // do not infer a personal/team workspace or copy unrelated fields.
                name = String.IsNullOrWhiteSpace(name) ? "Workspace " + id.Substring(0, 8) : name.Trim();
                workspaces.Add(new HiggsfieldWorkspace(id, name, plan.Trim(), (bool)item));
            }
            return workspaces.AsReadOnly();
        }

        // v1.1.26's official Windows binary exposes its Go AccountStatus schema as
        // { credits: float32, email: *string, subscription_plan_type: enum }. Only numeric credits
        // and a safe plan label are retained. No quota denominator or renewal date is supplied.
        internal static UsageSnapshot ParseWebStatus(string json)
        {
            if (String.IsNullOrWhiteSpace(json) || json.Length > OutputLimit) throw SchemaError();
            Dictionary<string, object> values;
            try { values = new JavaScriptSerializer { MaxJsonLength = OutputLimit, RecursionLimit = 16 }.DeserializeObject(json) as Dictionary<string, object>; }
            catch (ArgumentException) { throw SchemaError(); }
            catch (InvalidOperationException) { throw SchemaError(); }
            object value;
            double? credits = values != null && values.TryGetValue("credits", out value) ? HiggsfieldSettingsStore.Numeric(value, 1000000000000) : null;
            if (!credits.HasValue) throw SchemaError();
            string plan = values.TryGetValue("subscription_plan_type", out value) ? value as string : null;
            if (String.IsNullOrEmpty(plan) || plan.Length > 48) plan = "웹 구독";
            else foreach (char c in plan) if (!Char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != ' ') { plan = "웹 구독"; break; }
            return new UsageSnapshot {
                Plan = plan,
                Rows = new List<UsageRow> { new UsageRow {
                    Label = "웹 잔여 크레딧", Remaining = credits,
                    DisplayValue = credits.Value.ToString("N2", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.'),
                    DisplaySubtitle = "크레딧 · 공식 CLI 조회"
                } },
                Note = "웹사이트 구독 크레딧 · 개발자 API 잔액과 별도"
            };
        }

        public static UsageSnapshot ReadApiManual(HiggsfieldSettings settings)
        {
            if (settings == null || !settings.ApiBalanceUsd.HasValue || !settings.ApiObservedAt.HasValue || !HiggsfieldSettingsStore.Numeric(settings.ApiBalanceUsd.Value, 1000000000).HasValue)
                throw new InvalidOperationException("API 콘솔 Billing에서 달러 잔액을 확인한 뒤 직접 입력해 주세요.");
            return new UsageSnapshot {
                Plan = "수동 입력",
                Rows = new List<UsageRow> { new UsageRow {
                    Label = "API 달러 잔액 · 수동", Remaining = settings.ApiBalanceUsd,
                    DisplayValue = "$" + settings.ApiBalanceUsd.Value.ToString("N2", CultureInfo.InvariantCulture),
                    DisplaySubtitle = "API 선불 잔액 · USD"
                } },
                Note = "웹 크레딧과 별도 · API 콘솔에서 확인한 금액"
            };
        }

        private static InvalidOperationException SchemaError() { return new InvalidOperationException("Higgsfield 응답에서 잔여 크레딧을 확인하지 못했습니다. 공식 CLI를 업데이트하고 다시 연결해 주세요."); }
        private static InvalidOperationException WorkspaceSchemaError() { return new InvalidOperationException("Higgsfield 작업 공간 목록을 확인하지 못했습니다. 공식 CLI를 업데이트하고 다시 연결해 주세요."); }
        private sealed class NoWorkspaceSelectedException : InvalidOperationException
        {
            internal NoWorkspaceSelectedException() : base("Higgsfield 작업 공간 선택이 필요합니다. 로그인 후 터미널에서 higgsfield workspace list로 확인한 ID를 위젯의 ‘작업 공간 ID’에 입력하고 다시 연결해 주세요.") { }
        }
        private static string ResolveCli(string cliPath)
        {
            if (String.IsNullOrEmpty(cliPath)) cliPath = FindCliPath();
            if (String.IsNullOrEmpty(cliPath)) throw new InvalidOperationException("Higgsfield 공식 CLI가 필요합니다. 공식 설치 안내에 따라 설치하거나 실행 파일을 선택해 주세요.");
            if (!HiggsfieldSettingsStore.ValidCliPreference(cliPath) || !File.Exists(cliPath)) throw new InvalidOperationException("Higgsfield 실행 파일을 찾지 못했습니다. 설치 경로에서 .exe 파일을 다시 선택해 주세요.");
            return Path.GetFullPath(cliPath);
        }

        private sealed class CapturedOutput { internal int TooLarge; }
        private static Task<string> RunCliAsync(string path, string arguments, string workspaceId, int timeout, CancellationToken cancellationToken)
        {
            if (!HiggsfieldSettingsStore.ValidWorkspaceId(workspaceId)) throw new ArgumentException("Higgsfield 작업 공간 ID를 UUID 형식으로 입력해 주세요.");
            return Task.Run(() => {
                cancellationToken.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo {
                    FileName = path, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(path)
                };
                // Runtime-derived from the official v1.1.26 binary, not a publicly documented stable API.
                // This optional override is scoped to this child only; never change global env or CLI config.
                if (!String.IsNullOrWhiteSpace(workspaceId)) start.EnvironmentVariables["HIGGSFIELD_WORKSPACE_ID"] = workspaceId.Trim().ToLowerInvariant();
                using (var process = new Process { StartInfo = start })
                {
                    try { if (!process.Start()) throw new InvalidOperationException(); }
                    catch (Exception ex) {
                        if (!(ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException)) throw;
                        throw new InvalidOperationException("Higgsfield CLI를 실행하지 못했습니다. Windows용 공식 실행 파일인지 확인해 주세요.");
                    }
                    var output = new CapturedOutput();
                    Task<string> stdout = ReadBoundedAsync(process.StandardOutput, output);
                    Task<string> stderr = ReadBoundedAsync(process.StandardError, output);
                    var elapsed = Stopwatch.StartNew();
                    try
                    {
                        while (!process.WaitForExit(100))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (Volatile.Read(ref output.TooLarge) != 0) throw SchemaError();
                            if (elapsed.ElapsedMilliseconds >= timeout) throw new InvalidOperationException(arguments.StartsWith("auth ", StringComparison.Ordinal) ? "Higgsfield 로그인이 시간 내 완료되지 않았습니다. 다시 로그인해 주세요." : "Higgsfield 조회 시간이 초과되었습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.");
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        // Both streams are drained concurrently to avoid pipe deadlocks.
                        try { if (!Task.WaitAll(new Task[] { stdout, stderr }, 2000)) throw SchemaError(); }
                        catch (AggregateException) { throw SchemaError(); }
                        if (Volatile.Read(ref output.TooLarge) != 0) throw SchemaError();
                        if (process.ExitCode != 0)
                        {
                            string diagnosis = (stdout.Result + " " + stderr.Result).ToLowerInvariant();
                            if (diagnosis.Contains("workspace_membership_required"))
                                throw new InvalidOperationException("이 Higgsfield 계정에 작업 공간 조회 권한이 없습니다. workspace list에서 본인 작업 공간 ID를 확인하고, 같은 문제가 계속되면 Higgsfield 지원팀에 문의해 주세요.");
                            bool missingWorkspace = diagnosis.Contains("no workspace selected");
                            // A rejected/expired account cannot be recovered by selecting another
                            // workspace. The phrase "auth login" alone may merely be a help hint
                            // accompanying a real missing-default error, so keep that case recoverable.
                            if (diagnosis.Contains("not authenticated") || diagnosis.Contains("session expired") || diagnosis.Contains("unauthorized") ||
                                (!missingWorkspace && diagnosis.Contains("auth login")))
                                throw new InvalidOperationException("Higgsfield 로그인이 필요하거나 만료되었습니다. ‘힉스필드 로그인’을 눌러 다시 연결해 주세요.");
                            if (missingWorkspace) throw new NoWorkspaceSelectedException();
                            throw new InvalidOperationException("Higgsfield 사용량을 확인하지 못했습니다. CLI 로그인과 인터넷 연결을 확인하고 다시 시도해 주세요.");
                        }
                        return stdout.Result;
                    }
                    finally
                    {
                        // Cancellation and timeouts only stop this CLI process, never a user's browser.
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        try { process.WaitForExit(2000); } catch { }
                    }
                }
            }, cancellationToken);
        }
        private static async Task<string> ReadBoundedAsync(StreamReader reader, CapturedOutput output)
        {
            var text = new StringBuilder();
            var buffer = new char[1024];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                if (text.Length + read > OutputLimit) { Interlocked.Exchange(ref output.TooLarge, 1); return String.Empty; }
                text.Append(buffer, 0, read);
            }
            return text.ToString();
        }
    }
}
