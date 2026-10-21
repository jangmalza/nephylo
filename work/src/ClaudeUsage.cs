using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    public sealed class ClaudeUsageResult
    {
        public UsageSnapshot Snapshot;
        public DateTimeOffset ObservedAt;
        public bool HasExpiredWindows;
        public bool HasQuotaData;
        public bool IsDirectQuery;
        internal string AccountFingerprint, QuotaJson;
    }

    // Uses Claude Code's documented statusLine input, never browser cookies or OAuth credentials.
    public static partial class ClaudeUsageService
    {
        private const int InputLimit = 1048576;
        private const int CacheLimit = 16384;
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        internal static string ConfigDirectoryOverride = null;
        internal static Func<ProcessStartInfo, CancellationToken, Task<string>> BridgeProbeOverride = null;

        // Fixed categories only: never persist command lines, child output, paths or input JSON.
        internal static void RecordBridgeDiagnostic(string sourceId, string phase, string code)
        {
            try {
                if (phase != "probe" && phase != "receive") return;
                if (code != "ok" && code != "launch" && code != "timeout" && code != "response" && code != "storage" && code != "input") return;
                string path = SourceCachePath(sourceId) + "." + phase + ".diagnostic.json";
                AtomicWrite(path, Serializer(1024).Serialize(new Dictionary<string, object> {
                    { "schemaVersion", 1 }, { "checkedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "code", code }
                }), false);
            } catch { /* Diagnostic failure must never overwrite the quota or block Claude. */ }
        }

        internal static ConnectionException BridgeProblem(string code)
        {
            string title = code == "storage" ? "Claude 기록에 접근하지 못했습니다" : code == "input" ? "Claude 수신 데이터를 확인해 주세요" : "Claude 연결 명령 실행을 확인하지 못했습니다";
            string message = code == "storage" ? "Nephylo 데이터 폴더의 쓰기 권한과 보안 프로그램의 차단 기록을 확인해 주세요. 기존 사용량은 유지했습니다." :
                code == "input" ? "Claude에서 전달된 데이터 형식을 처리하지 못했습니다. Claude Code를 업데이트한 뒤 다음 응답에서 다시 확인해 주세요." :
                code == "timeout" ? "연결 명령이 제한 시간 안에 응답하지 않았습니다. 보안 프로그램의 차단 기록과 PowerShell 실행 정책을 확인해 주세요." :
                "연결 명령을 시작하지 못했거나 정상 응답을 받지 못했습니다. 설치 파일 누락, 실행 권한 또는 보안 프로그램 차단 여부를 확인해 주세요. 백신 차단으로 확정된 것은 아닙니다.";
            return new ConnectionException(new ConnectionIssue { Title = title, Message = message, ActionLabel = "상태 표시줄 안내 열기", ActionUrl = "https://code.claude.com/docs/en/statusline" });
        }

        private static void CheckReceiveDiagnostic(string sourceId)
        {
            string code = null;
            try {
                string path = SourceCachePath(sourceId) + ".receive.diagnostic.json";
                if (!File.Exists(path)) return;
                var record = ReadObject(ReadBoundedFile(path, 1024)); object raw, when;
                DateTimeOffset time;
                if (!record.TryGetValue("code", out raw) || !record.TryGetValue("checkedAt", out when) || !(when is string) ||
                    !DateTimeOffset.TryParseExact((string)when, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out time) ||
                    time > DateTimeOffset.UtcNow || time < DateTimeOffset.UtcNow.AddMinutes(-10)) return;
                if (Object.Equals(raw, "storage") || Object.Equals(raw, "input")) code = (string)raw;
            } catch { return; }
            if (code != null) throw BridgeProblem(code);
        }

        // Explicit connection/check only; never execute this probe during automatic polling.
        public static async Task VerifyWindowsBridgeAsync(string executable, string directory, CancellationToken cancellationToken)
        {
            string sourceId = GetWindowsSourceId(directory);
            string command = MakeCommand(executable, "", "--claude-bridge-check");
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), command.Substring("powershell.exe ".Length)) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))
            };
            cancellationToken.ThrowIfCancellationRequested();
            string code = await (BridgeProbeOverride != null ? BridgeProbeOverride(info, cancellationToken) : RunBridgeProbeAsync(info, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (code != "ok" && code != "timeout" && code != "launch" && code != "storage") code = "response";
            RecordBridgeDiagnostic(sourceId, "probe", code);
            if (code != "ok") throw BridgeProblem(code);
        }

        public static void VerifyCaptureStorage()
        {
            string path = Path.Combine(SettingsStore.DataDirectory, ".claude-check-" + Guid.NewGuid().ToString("N") + ".tmp");
            try {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.WriteByte(0); stream.Flush(true); }
            } finally { if (File.Exists(path)) File.Delete(path); }
        }

        private static async Task<string> ReadProbeOutputAsync(Stream stream)
        {
            using (var buffer = new MemoryStream()) {
                var bytes = new byte[512]; int count;
                while ((count = await stream.ReadAsync(bytes, 0, bytes.Length).ConfigureAwait(false)) > 0) {
                    if (buffer.Length + count > 4096) throw new InvalidDataException();
                    buffer.Write(bytes, 0, count);
                }
                return new UTF8Encoding(false, true).GetString(buffer.ToArray());
            }
        }

        private static async Task<string> RunBridgeProbeAsync(ProcessStartInfo info, CancellationToken cancellationToken)
        {
            using (var process = new Process { StartInfo = info }) {
                try { if (!process.Start()) return "launch"; }
                catch (System.ComponentModel.Win32Exception) { return "launch"; }
                catch (UnauthorizedAccessException) { return "launch"; }
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
                    deadline.CancelAfter(8000);
                    var output = ReadProbeOutputAsync(process.StandardOutput.BaseStream);
                    var errors = ReadProbeOutputAsync(process.StandardError.BaseStream);
                    process.StandardInput.Close();
                    var exit = Task.Run(() => process.WaitForExit());
                    var all = Task.WhenAll(output, errors, exit);
                    var canceled = new TaskCompletionSource<bool>();
                    using (deadline.Token.Register(() => canceled.TrySetResult(true))) {
                        await Task.WhenAny(all, canceled.Task).ConfigureAwait(false);
                        if (!all.IsCompleted) {
                            try { if (!process.HasExited) process.Kill(); } catch { }
                            ObserveProbe(all);
                            cancellationToken.ThrowIfCancellationRequested();
                            return "timeout";
                        }
                        try { await all.ConfigureAwait(false); }
                        catch { return "response"; }
                        // A GUI-subsystem child may not propagate its exit code through PowerShell.
                        if (output.Result.Trim() == "nephylo-claude-bridge-storage") return "storage";
                        return process.ExitCode == 0 && output.Result.Trim() == "nephylo-claude-bridge-v1" ? "ok" : "response";
                    }
                }
            }
        }

        private static void ObserveProbe(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static string CachePath { get { return Path.Combine(SettingsStore.DataDirectory, "claude-usage.json"); } }
        public static string ResolveWindowsConfigDirectory(string configDirectory)
        {
            string directory = ConfigDirectoryOverride;
            if (String.IsNullOrWhiteSpace(directory)) directory = configDirectory;
            if (String.IsNullOrWhiteSpace(directory)) directory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (String.IsNullOrWhiteSpace(directory)) directory = DefaultWindowsConfigDirectory;
            try {
                directory = Environment.ExpandEnvironmentVariables(directory.Trim());
                if (directory.Length > 2048 || directory.IndexOfAny(new[] { '"', '\0', '\r', '\n', '*', '?' }) >= 0) throw new ArgumentException();
                foreach (char character in directory) if (Char.IsControl(character)) throw new ArgumentException();
                string drive = Path.GetPathRoot(directory);
                if (drive == null || drive.Length != 3 || drive[1] != ':' || (drive[2] != '\\' && drive[2] != '/')) throw new ArgumentException();
                if (directory.IndexOf(':', 2) >= 0) throw new ArgumentException();
                string full = Path.GetFullPath(directory);
                return full.Length == 3 ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            } catch (Exception error) {
                if (!(error is ArgumentException || error is NotSupportedException || error is IOException || error is System.Security.SecurityException)) throw;
                throw new InvalidOperationException("Claude 설정 폴더는 이 PC의 전체 경로로 입력해 주세요.");
            }
        }

        private static string DefaultWindowsConfigDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"); } }
        private static string WindowsConfigPath(string directory) { return Path.Combine(ResolveWindowsConfigDirectory(directory), "settings.json"); }

        public static string GetWindowsSourceId(string configDirectory)
        {
            string directory = ResolveWindowsConfigDirectory(configDirectory);
            if (!String.IsNullOrEmpty(ConfigDirectoryOverride) && String.IsNullOrWhiteSpace(configDirectory)) return "";
            if (String.Equals(directory, Path.GetFullPath(DefaultWindowsConfigDirectory), StringComparison.OrdinalIgnoreCase)) return "";
            using (var hash = SHA256.Create()) return "win:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()))).Replace("-", "").ToLowerInvariant();
        }

        public static ClaudeUsageResult ReadLatest()
        {
            return ReadLatest("");
        }

        public static ClaudeUsageResult ReadLatest(string distribution)
        {
            return ReadLatest(distribution, null);
        }

        public static ClaudeUsageResult ReadLatest(string distribution, string windowsConfigDirectory)
        {
            string sourceId = String.IsNullOrEmpty(distribution) ? GetWindowsSourceId(windowsConfigDirectory) : GetSourceId(distribution);
            string cachePath = SourceCachePath(sourceId);
            CheckReceiveDiagnostic(sourceId);
            if (!File.Exists(cachePath)) throw new InvalidOperationException("Claude Code 연결 후 대화를 시작하면 사용량이 표시됩니다.");
            try
            {
                string json = ReadBoundedFile(cachePath, CacheLimit);
                object source;
                bool hasSource = ReadObject(json).TryGetValue("source", out source);
                if (String.IsNullOrEmpty(sourceId) ? hasSource : !hasSource || !Object.Equals(source, sourceId)) throw new InvalidOperationException("Unexpected Claude source.");
                return ParseCache(json, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException || ex is FormatException)) throw;
                if (ex is IOException || ex is UnauthorizedAccessException) throw BridgeProblem("storage");
                throw new InvalidOperationException("Claude Code 사용량 기록을 읽지 못했습니다. 다음 응답 후 다시 확인해 주세요.");
            }
        }

        public static string StatusLineStatus(string widgetExePath)
        {
            return StatusLineStatusWindows(widgetExePath, null);
        }

        public static string StatusLineStatusWindows(string widgetExePath, string configDirectory)
        {
            try
            {
                string configPath = WindowsConfigPath(configDirectory);
                if (!File.Exists(configPath)) return "Claude Code 연결 전";
                Dictionary<string, object> config = ReadObject(ReadBoundedFile(configPath, InputLimit));
                object value;
                if (!config.TryGetValue("statusLine", out value) || value == null) return "Claude Code 연결 전";
                return IsOurHook(value, GetWindowsStatusLineCommand(widgetExePath, configDirectory)) ? "Claude Code 연결됨 · 다음 응답 대기" : IsPreviousWidgetHook(value) ? "위젯 위치 변경 · 연결 복구 필요" : "기존 Claude 상태 표시줄 사용 중";
            }
            catch { return "Claude 설정 파일을 확인해 주세요"; }
        }

        public static bool IsStatusLineInstalled(string widgetExePath)
        {
            return IsStatusLineInstalledWindows(widgetExePath, null);
        }

        public static bool IsStatusLineInstalledWindows(string widgetExePath, string configDirectory)
        {
            try {
                string configPath = WindowsConfigPath(configDirectory);
                if (!File.Exists(configPath)) return false;
                var config = ReadObject(ReadBoundedFile(configPath, InputLimit));
                object hook;
                return config.TryGetValue("statusLine", out hook) && IsOurHook(hook, GetWindowsStatusLineCommand(widgetExePath, configDirectory));
            } catch { return false; }
        }

        public static bool HasCustomStatusLineWindows(string widgetExePath, string configDirectory)
        {
            try {
                string configPath = WindowsConfigPath(configDirectory);
                if (!File.Exists(configPath)) return false;
                var config = ReadObject(ReadBoundedFile(configPath, InputLimit));
                object raw, type, command;
                if (!config.TryGetValue("statusLine", out raw)) return false;
                var hook = raw as Dictionary<string, object>;
                return hook != null && hook.TryGetValue("type", out type) && Object.Equals(type, "command") &&
                    hook.TryGetValue("command", out command) && command is string && !String.IsNullOrWhiteSpace((string)command) &&
                    !IsOurHook(hook, GetWindowsStatusLineCommand(widgetExePath, configDirectory)) && !IsPreviousWidgetHook(hook);
            } catch { return false; }
        }

        public static string GetWindowsStatusLineCommand(string widgetExePath, string configDirectory = null)
        {
            return MakeCommand(widgetExePath, GetWindowsSourceId(configDirectory));
        }

        // Called only by the user's explicit Connect button. Does not launch Claude or a model request.
        public static void InstallStatusLine(string widgetExePath)
        {
            InstallStatusLineWindows(widgetExePath, null);
        }

        public static void InstallStatusLineWindows(string widgetExePath, string configDirectory)
        {
            string configPath = WindowsConfigPath(configDirectory);
            string command = GetWindowsStatusLineCommand(widgetExePath, configDirectory);
            if (!File.Exists(Path.GetFullPath(widgetExePath))) throw new InvalidOperationException("위젯 실행 파일을 찾지 못했습니다.");
            using (var mutex = new Mutex(false, @"Local\UsageWidget.ClaudeInstall.v1"))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(3000); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new InvalidOperationException("Claude 설정을 다른 작업에서 변경 중입니다. 잠시 후 다시 연결해 주세요.");
                    string original = File.Exists(configPath) ? ReadBoundedFile(configPath, InputLimit) : null;
                    Dictionary<string, object> config;
                    try { config = original == null ? new Dictionary<string, object>() : ReadObject(original); }
                    catch (Exception ex)
                    {
                        if (!(ex is ArgumentException || ex is InvalidOperationException)) throw;
                        throw new InvalidOperationException("Claude 설정 JSON 형식이 올바르지 않아 변경하지 않았습니다.");
                    }
                    object existing;
                    if (config.TryGetValue("statusLine", out existing) && existing != null)
                    {
                        if (IsOurHook(existing, command)) return;
                        if (!IsPreviousWidgetHook(existing))
                            throw new InvalidOperationException("기존 Claude 상태 표시줄을 유지했습니다. 기존 스크립트에서 위젯의 --claude-statusline 명령에 같은 JSON 입력을 전달하도록 연결해 주세요.");
                    }
                    config["statusLine"] = new Dictionary<string, object> { { "type", "command" }, { "command", command } };
                    string updated = Serializer(InputLimit).Serialize(config);
                    // Refuse to replace a file changed since it was read; retain the exact original as a backup.
                    if ((original == null && File.Exists(configPath)) || (original != null && (!File.Exists(configPath) || ReadBoundedFile(configPath, InputLimit) != original)))
                        throw new InvalidOperationException("Claude 설정이 변경되어 연결을 중단했습니다. 다시 시도해 주세요.");
                    AtomicWrite(configPath, updated, original != null);
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        // Only our exact command is removed. A changed/custom status line is left untouched.
        public static void RemoveStatusLine(string widgetExePath)
        {
            RemoveStatusLineWindows(widgetExePath, null);
        }

        public static void RemoveStatusLineWindows(string widgetExePath, string configDirectory)
        {
            string configPath = WindowsConfigPath(configDirectory);
            string command = GetWindowsStatusLineCommand(widgetExePath, configDirectory);
            using (var mutex = new Mutex(false, @"Local\UsageWidget.ClaudeInstall.v1"))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(3000); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new InvalidOperationException("Claude 설정을 다른 작업에서 변경 중입니다. 잠시 후 다시 시도해 주세요.");
                    if (!File.Exists(configPath)) return;
                    string original = ReadBoundedFile(configPath, InputLimit);
                    Dictionary<string, object> config;
                    try { config = ReadObject(original); }
                    catch (Exception ex)
                    {
                        if (!(ex is ArgumentException || ex is InvalidOperationException)) throw;
                        throw new InvalidOperationException("Claude 설정 JSON 형식이 올바르지 않아 변경하지 않았습니다.");
                    }
                    object hook;
                    if (!config.TryGetValue("statusLine", out hook) || hook == null) return;
                    if (!IsOurHook(hook, command) && !IsPreviousWidgetHook(hook)) throw new InvalidOperationException("기존 Claude 상태 표시줄이 위젯 연결과 달라 변경하지 않았습니다.");
                    config.Remove("statusLine");
                    if (!File.Exists(configPath) || ReadBoundedFile(configPath, InputLimit) != original) throw new InvalidOperationException("Claude 설정이 변경되어 연결 해제를 중단했습니다. 다시 시도해 주세요.");
                    AtomicWrite(configPath, Serializer(InputLimit).Serialize(config), true);
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        public static void CaptureFromStandardInput()
        {
            CaptureFromStandardInput("");
        }

        public static void CaptureFromStandardInput(string sourceId)
        {
            // A status line must never expose input, paths, credentials, or exception messages.
            try
            {
                // Keep only the latest sanitized local snapshot. This helper has no membership,
                // network or history access; the widget checks its verified entitlement before use.
                string json;
                using (Stream input = Console.OpenStandardInput()) json = ReadBounded(input, InputLimit);
                if (CaptureJson(json, DateTimeOffset.UtcNow, sourceId)) Console.WriteLine("Claude usage received");
                else Console.WriteLine("Claude usage waiting");
            }
            catch (Exception error) {
                RecordBridgeDiagnostic(sourceId, "receive", error is IOException || error is UnauthorizedAccessException ? "storage" : "input");
                Environment.ExitCode = 1;
                try { Console.WriteLine("Claude usage unavailable"); } catch { }
            }
        }

        internal static bool CaptureJson(string json, DateTimeOffset observedAt)
        {
            return CaptureJson(json, observedAt, "");
        }

        internal static bool CaptureJson(string json, DateTimeOffset observedAt, string sourceId, string queryAccount = null)
        {
            if (queryAccount != null && (queryAccount.Length != 64 || queryAccount.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))) throw new ArgumentException("Invalid quota account fingerprint.");
            string cachePath = SourceCachePath(sourceId);
            if (json == null || Encoding.UTF8.GetByteCount(json) >= InputLimit) throw new InvalidOperationException("Status line input too large.");
            Dictionary<string, object> input = ReadObject(json);
            object raw;
            input.TryGetValue("rate_limits", out raw);
            var rateLimits = raw == null ? new Dictionary<string, object>() : raw as Dictionary<string, object>;
            if (rateLimits == null) throw new InvalidOperationException("Invalid rate limits.");
            Dictionary<string, object> clean = CleanWindows(rateLimits);
            var cache = new Dictionary<string, object> {
                { "schemaVersion", 1 },
                { "observedAt", observedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) },
                { "rate_limits", clean }
            };
            if (!String.IsNullOrEmpty(sourceId)) cache["source"] = sourceId;
            if (queryAccount != null) cache["queryAccount"] = queryAccount;
            using (var mutex = new Mutex(false, @"Local\UsageWidget.ClaudeUsage.v1" + (String.IsNullOrEmpty(sourceId) ? "" : "." + (sourceId.StartsWith("win:", StringComparison.Ordinal) ? "win." : "") + sourceId.Substring(4))))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(2000); } catch (AbandonedMutexException) { held = true; }
                    if (!held) return false;
                    AtomicWrite(cachePath, Serializer(CacheLimit).Serialize(cache), false);
                    RecordBridgeDiagnostic(sourceId, "receive", "ok");
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
            return true;
        }

        public static string GetSourceId(string distribution)
        {
            if (String.IsNullOrEmpty(distribution)) return "";
            // Names never form paths or shell arguments here. Hash exact Unicode names so
            // two distros (or Windows) cannot overwrite one another's quota snapshot.
            if (!WslRuntime.IsValidDistributionName(distribution)) throw new ArgumentException("Invalid WSL distribution.");
            using (var hash = SHA256.Create()) return "wsl:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(distribution))).Replace("-", "").ToLowerInvariant();
        }

        private static string SourceCachePath(string sourceId)
        {
            if (String.IsNullOrEmpty(sourceId)) return CachePath;
            if (sourceId.Length != 68 || !(sourceId.StartsWith("wsl:", StringComparison.Ordinal) || sourceId.StartsWith("win:", StringComparison.Ordinal))) throw new ArgumentException("Invalid Claude source.");
            for (int i = 4; i < sourceId.Length; i++) if (!((sourceId[i] >= '0' && sourceId[i] <= '9') || (sourceId[i] >= 'a' && sourceId[i] <= 'f'))) throw new ArgumentException("Invalid Claude source.");
            return Path.Combine(SettingsStore.DataDirectory, "claude-usage-" + (sourceId.StartsWith("win:", StringComparison.Ordinal) ? "win-" : "") + sourceId.Substring(4) + ".json");
        }

        internal static ClaudeUsageResult ParseCache(string json, DateTimeOffset now)
        {
            Dictionary<string, object> cache = ReadObject(json);
            DateTimeOffset observed;
            object rawObserved;
            if (!cache.TryGetValue("observedAt", out rawObserved) || !(rawObserved is string) ||
                !DateTimeOffset.TryParseExact((string)rawObserved, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out observed) || observed > now.AddMinutes(5))
                throw new InvalidOperationException("Invalid observation time.");
            object raw;
            if (!cache.TryGetValue("rate_limits", out raw) || !(raw is Dictionary<string, object>)) throw new InvalidOperationException("Invalid quota cache.");
            var clean = CleanWindows((Dictionary<string, object>)raw);
            var result = new ClaudeUsageResult {
                ObservedAt = observed,
                Snapshot = new UsageSnapshot { Plan = "CODE 수신", Note = "Claude Code 마지막 응답에서 받은 구독 한도입니다. 다른 기기에서 사용한 내용은 다음 응답 후 반영됩니다. 마지막 수신: " + observed.ToLocalTime().ToString("MM/dd HH:mm") }
            };
            object queryAccount;
            if (cache.TryGetValue("queryAccount", out queryAccount)) {
                string fingerprint = queryAccount as string;
                if (fingerprint == null || fingerprint.Length != 64 || fingerprint.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f'))) throw new InvalidOperationException("Invalid quota account fingerprint.");
                result.IsDirectQuery = true; result.AccountFingerprint = fingerprint;
                result.Snapshot.Plan = "CODE 조회";
                result.Snapshot.Note = "Claude Code에서 대화 없이 조회한 계정 구독 한도입니다. 마지막 확인: " + observed.ToLocalTime().ToString("MM/dd HH:mm");
            }
            AddRow(result, clean, "five_hour", "5시간 한도", now);
            AddRow(result, clean, "seven_day", "주간 한도", now);
            foreach (var row in result.Snapshot.Rows) if (row.RemainingPercent.HasValue) result.HasQuotaData = true;
            if (!result.HasQuotaData && !result.HasExpiredWindows) result.Snapshot.Note = "Claude Code 응답은 받았지만 구독 한도 정보가 포함되지 않았습니다. 현재 계정이나 사용 환경에서 한도가 제공되는지 확인해 주세요.";
            return result;
        }

        private static void AddRow(ClaudeUsageResult result, Dictionary<string, object> windows, string key, string label, DateTimeOffset now)
        {
            object value;
            var row = new UsageRow { Label = label, Detail = "Claude Code가 이 한도 정보를 아직 제공하지 않았습니다." };
            if (windows.TryGetValue(key, out value))
            {
                var window = (Dictionary<string, object>)value;
                object used, reset;
                if (window.TryGetValue("used_percentage", out used)) row.RemainingPercent = 100 - Convert.ToDouble(used, CultureInfo.InvariantCulture);
                if (window.TryGetValue("resets_at", out reset)) row.ResetsAt = Epoch.AddSeconds(Convert.ToDouble(reset, CultureInfo.InvariantCulture));
                row.Detail = "Claude Code 마지막 수신: " + result.ObservedAt.ToLocalTime().ToString("MM/dd HH:mm");
                if (row.ResetsAt.HasValue && row.ResetsAt.Value <= now)
                {
                    result.HasExpiredWindows = true;
                    row.RemainingPercent = null;
                    row.Label += " · 수신 대기";
                    row.Detail += result.IsDirectQuery ? " · 초기화 시간이 지났습니다. 새로고침하여 새 한도를 확인하세요." : " · 초기화 시간이 지났습니다. 다음 Claude Code 응답 후 새 한도가 표시됩니다.";
                    row.ResetsAt = null;
                }
            }
            result.Snapshot.Rows.Add(row);
        }

        private static Dictionary<string, object> CleanWindows(Dictionary<string, object> input)
        {
            var clean = new Dictionary<string, object>();
            foreach (string name in new[] { "five_hour", "seven_day" })
            {
                object raw;
                if (!input.TryGetValue(name, out raw) || raw == null) continue;
                var window = raw as Dictionary<string, object>;
                if (window == null) throw new InvalidOperationException("Invalid rate limit window.");
                var output = new Dictionary<string, object>();
                CopyNumber(window, output, "used_percentage", 0, 100, false);
                CopyNumber(window, output, "resets_at", 0, 253402300799, true);
                clean[name] = output;
            }
            return clean;
        }

        private static void CopyNumber(Dictionary<string, object> source, Dictionary<string, object> target, string name, double min, double max, bool integral)
        {
            object raw;
            if (!source.TryGetValue(name, out raw) || raw == null) return;
            if (!(raw is int || raw is long || raw is double || raw is decimal || raw is float)) throw new InvalidOperationException("Invalid numeric quota field.");
            double value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < min || value > max || (integral && Math.Floor(value) != value)) throw new InvalidOperationException("Invalid numeric quota range.");
            target[name] = value;
        }

        private static string MakeCommand(string executable, string sourceId = "", string operation = "--claude-statusline")
        {
            if (String.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("위젯 경로가 올바르지 않습니다.");
            string path = Path.GetFullPath(executable);
            if (path.IndexOfAny(new[] { '"', '\r', '\n', '$', '`' }) >= 0) throw new InvalidOperationException("위젯 경로에 상태 표시줄에서 지원하지 않는 문자가 있습니다.");
            // The documented Windows host is Git Bash OR PowerShell. The explicit PowerShell
            // wrapper works in both. Reject characters expanded by the outer double quotes;
            // the inner single-quoted literal preserves spaces, apostrophes and metacharacters.
            SourceCachePath(sourceId);
            if (operation != "--claude-statusline" && operation != "--claude-bridge-check") throw new ArgumentException("Invalid bridge operation.");
            string script = "& '" + path.Replace('\\', '/').Replace("'", "''") + "' " + operation + (String.IsNullOrEmpty(sourceId) ? "" : " --claude-source " + sourceId);
            return "powershell.exe -NoProfile -NonInteractive -Command \"" + script + "\"";
        }

        private static bool IsOurHook(object raw, string command)
        {
            var hook = raw as Dictionary<string, object>;
            object type, current;
            return hook != null && hook.TryGetValue("type", out type) && Object.Equals(type, "command") &&
                hook.TryGetValue("command", out current) && Object.Equals(current, command);
        }

        // Recognize only the exact wrapper emitted by this app and its original executable
        // filename. Custom wrappers, extra arguments/fields and other programs are preserved.
        private static bool IsPreviousWidgetHook(object raw)
        {
            var hook = raw as Dictionary<string,object>;
            object type, value;
            if (hook == null || hook.Count != 2 || !hook.TryGetValue("type",out type) || !Object.Equals(type,"command") || !hook.TryGetValue("command",out value)) return false;
            string command = value as string;
            const string prefix = "powershell.exe -NoProfile -NonInteractive -Command \"& '";
            const string marker = "' --claude-statusline";
            if (command == null || !command.StartsWith(prefix,StringComparison.Ordinal)) return false;
            int markerAt = command.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerAt <= prefix.Length) return false;
            string suffix = command.Substring(markerAt + marker.Length), sourceId = "";
            if (suffix != "\"") {
                const string sourcePrefix = " --claude-source ";
                if (!suffix.StartsWith(sourcePrefix, StringComparison.Ordinal) || !suffix.EndsWith("\"", StringComparison.Ordinal)) return false;
                sourceId = suffix.Substring(sourcePrefix.Length, suffix.Length - sourcePrefix.Length - 1);
                if (!sourceId.StartsWith("win:", StringComparison.Ordinal)) return false;
            }
            string path = command.Substring(prefix.Length,markerAt-prefix.Length).Replace("''","'");
            try {
                if (!Path.IsPathRooted(path) || !String.Equals(Path.GetFileName(path),"UsageWidget.exe",StringComparison.OrdinalIgnoreCase) || MakeCommand(path,sourceId) != command) return false;
                // A moved/deleted old copy is recoverable. If it exists, ensure it actually
                // identifies as this app's current or legacy brand without executing
                // or loading that assembly. The executable name remains compatible.
                if (!File.Exists(path)) return true;
                string product = FileVersionInfo.GetVersionInfo(path).ProductName;
                return product == "Usage Widget" || product == "Milos" || product == "Nephylo";
            } catch { return false; }
        }

        private static JavaScriptSerializer Serializer(int limit) { return new JavaScriptSerializer { MaxJsonLength = limit, RecursionLimit = 32 }; }
        private static Dictionary<string, object> ReadObject(string json)
        {
            var result = Serializer(InputLimit).DeserializeObject(json) as Dictionary<string, object>;
            if (result == null) throw new InvalidOperationException("Invalid JSON object.");
            return result;
        }

        private static string ReadBoundedFile(string path, int limit)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return ReadBounded(stream, limit);
        }

        private static string ReadBounded(Stream stream, int limit)
        {
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[4096];
                int count;
                while ((count = stream.Read(chunk, 0, Math.Min(chunk.Length, limit - (int)buffer.Length))) > 0)
                {
                    buffer.Write(chunk, 0, count);
                    if (buffer.Length >= limit) throw new InvalidOperationException("Input too large.");
                }
                string value = new UTF8Encoding(false, true).GetString(buffer.ToArray());
                return value.Length > 0 && value[0] == '\uFEFF' ? value.Substring(1) : value;
            }
        }

        private static void AtomicWrite(string path, string json, bool backup)
        {
            if (Encoding.UTF8.GetByteCount(json) >= InputLimit) throw new InvalidOperationException("설정 파일이 너무 커서 변경하지 않았습니다.");
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".usage-widget-" + Guid.NewGuid().ToString("N") + ".tmp");
            string backupPath = backup ? path + ".usage-widget-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".bak" : null;
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) {
                    // Antivirus/indexing may briefly hold a newly written local cache.
                    // Retry only errors where Windows leaves the destination untouched;
                    // never fall back to delete-then-move and lose the last good snapshot.
                    for (int attempt = 0; ; attempt++) {
                        try { File.Replace(temporary, path, backupPath); break; }
                        catch (IOException ex) {
                            int code = ex.HResult & 0xFFFF;
                            if (attempt >= 3 || (code != 32 && code != 33 && code != 1175)) throw;
                            Thread.Sleep(25 * (attempt + 1));
                        }
                    }
                }
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
