using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UsageWidget
{
    public static partial class ClaudeUsageService
    {
        // All configuration I/O happens in a bounded WSL child, never through a blocking
        // UNC call on the dispatcher. Normal rendering reads only this local manifest.
        private static readonly SemaphoreSlim WslHookGate = new SemaphoreSlim(1, 1);
        internal static Func<string, string, string[], string, Task<WslCommandResult>> WslExecuteOverride = null;
        internal static Func<string, Task<WslDistribution>> WslProbeOverride = null;
        internal static Func<string, string, Task<string>> WslPathOverride = null;

        internal const string WslReadConfigScript =
            "set -eu\n" +
            "p=$1\n" +
            "if [ -L \"$p\" ]; then exit 41; fi\n" +
            "if [ ! -e \"$p\" ]; then printf 'MISSING'; exit 0; fi\n" +
            "[ -f \"$p\" ] && [ \"$(wc -c < \"$p\")\" -lt 1048576 ] || exit 42\n" +
            "printf 'DATA:'; base64 < \"$p\"\n";

        internal const string WslWriteConfigScript =
            "set -eu\n" +
            "p=$1; d=${p%/*}; backup=$2; expected=''; updated=''; legacy=\"$p.nephylo-lock\"; lock=\"$p.nephylo-flock\"\n" +
            "umask 077\n" +
            "[ ! -L \"$p\" ] || exit 41\n" +
            "mkdir -p -- \"$d\"\n" +
            "command -v flock >/dev/null 2>&1 || exit 46\n" +
            "[ ! -L \"$lock\" ] && { [ ! -e \"$lock\" ] || [ -f \"$lock\" ]; } || exit 41\n" +
            "exec 9>>\"$lock\"\n" +
            "flock -x -w 3 9 || exit 43\n" +
            // A permanent marker blocks old mkdir-based writers. Never delete or claim
            // an existing unowned legacy lock: it may still belong to a running old widget.
            "[ ! -L \"$legacy\" ] || exit 45\n" +
            "if [ -e \"$legacy\" ]; then [ -f \"$legacy\" ] && [ \"$(wc -c < \"$legacy\")\" -eq 17 ] && [ \"$(cat -- \"$legacy\")\" = NEPHYLO_FLOCK_V1 ] || exit 45; else (set -C; printf 'NEPHYLO_FLOCK_V1\\n' > \"$legacy\") 2>/dev/null || exit 45; fi\n" +
            "cleanup() { [ -z \"$expected\" ] || rm -f -- \"$expected\"; [ -z \"$updated\" ] || rm -f -- \"$updated\"; }\n" +
            "trap cleanup EXIT; trap 'exit 130' HUP INT TERM\n" +
            "IFS= read -r before; IFS= read -r after\n" +
            "expected=$(mktemp \"$d/.nephylo-before-XXXXXX\")\n" +
            "updated=$(mktemp \"$d/.nephylo-after-XXXXXX\")\n" +
            "if [ \"$before\" != MISSING ]; then printf '%s' \"$before\" | base64 -d > \"$expected\"; fi\n" +
            "printf '%s' \"$after\" | base64 -d > \"$updated\"\n" +
            "[ \"$(wc -c < \"$updated\")\" -lt 1048576 ] || exit 42\n" +
            "if [ \"$before\" = MISSING ]; then [ ! -e \"$p\" ] && [ ! -L \"$p\" ] || exit 44; else [ -f \"$p\" ] && [ ! -L \"$p\" ] && cmp -s -- \"$expected\" \"$p\" || exit 44; fi\n" +
            "if [ \"$before\" != MISSING ]; then cp -p -- \"$p\" \"$backup\"; chmod --reference=\"$p\" -- \"$updated\"; cmp -s -- \"$expected\" \"$backup\" && cmp -s -- \"$expected\" \"$p\" || exit 44; fi\n" +
            "mv -f -- \"$updated\" \"$p\"; updated=''\n" +
            "printf 'OK'\n";

        private static string WslManifestPath(string distribution)
        {
            return Path.Combine(SettingsStore.DataDirectory, "claude-hook-" + GetSourceId(distribution).Substring(4) + ".json");
        }

        private static Dictionary<string, object> ReadWslManifest(string distribution)
        {
            try {
                string path = WslManifestPath(distribution);
                if (!File.Exists(path)) return null;
                var manifest = ReadObject(ReadBoundedFile(path, CacheLimit));
                object source;
                return manifest.TryGetValue("source", out source) && Object.Equals(source, GetSourceId(distribution)) ? manifest : null;
            } catch { return null; }
        }

        private static string ManifestString(Dictionary<string, object> manifest, string key)
        {
            object value;
            return manifest != null && manifest.TryGetValue(key, out value) ? value as string : null;
        }

        private static void SaveWslManifest(string distribution, string executable, string command, string state)
        {
            var manifest = new Dictionary<string, object> {
                { "source", GetSourceId(distribution) }, { "executable", Path.GetFullPath(executable) },
                { "command", command }, { "state", state }, { "checkedAt", DateTimeOffset.UtcNow.ToString("o") }
            };
            AtomicWrite(WslManifestPath(distribution), Serializer(CacheLimit).Serialize(manifest), false);
        }

        public static bool IsStatusLineInstalled(string widgetExePath, string distribution, string windowsConfigDirectory = null)
        {
            if (String.IsNullOrEmpty(distribution)) return IsStatusLineInstalledWindows(widgetExePath, windowsConfigDirectory);
            var manifest = ReadWslManifest(distribution);
            return ManifestString(manifest, "state") == "installed" && String.Equals(ManifestString(manifest, "executable"), Path.GetFullPath(widgetExePath), StringComparison.OrdinalIgnoreCase);
        }

        public static string StatusLineStatus(string widgetExePath, string distribution, string windowsConfigDirectory = null)
        {
            if (String.IsNullOrEmpty(distribution)) return StatusLineStatusWindows(widgetExePath, windowsConfigDirectory);
            var manifest = ReadWslManifest(distribution);
            string state = ManifestString(manifest, "state");
            if (state == "installed") return IsStatusLineInstalled(widgetExePath, distribution) ? "Claude Code 연결됨 · 다음 응답 대기" : "위젯 위치 변경 · 연결 복구 필요";
            if (state == "repair") return "위젯 위치 변경 · 연결 복구 필요";
            if (state == "custom") return "기존 Claude 상태 표시줄 사용 중";
            return "Claude Code 연결 전";
        }

        public static bool HasCustomStatusLine(string widgetExePath, string distribution, string windowsConfigDirectory = null)
        {
            if (String.IsNullOrEmpty(distribution)) return HasCustomStatusLineWindows(widgetExePath, windowsConfigDirectory);
            return ManifestString(ReadWslManifest(distribution), "state") == "custom";
        }

        public static Task<string> GetStatusLineCommandAsync(string widgetExePath, string distribution, string windowsConfigDirectory = null)
        {
            return String.IsNullOrEmpty(distribution) ? Task.FromResult(GetWindowsStatusLineCommand(widgetExePath, windowsConfigDirectory)) : MakeWslCommand(widgetExePath, distribution);
        }

        private static Task<WslCommandResult> ExecuteWsl(string distribution, string script, string[] arguments, string input)
        {
            return WslExecuteOverride != null ? WslExecuteOverride(distribution, script, arguments, input) : WslRuntime.ExecuteAsync(distribution, script, arguments, input);
        }

        private static async Task<WslDistribution> ProbeClaudeWsl(string distribution, bool requireClaude)
        {
            GetSourceId(distribution);
            var probe = await (WslProbeOverride != null ? WslProbeOverride(distribution) : WslRuntime.ProbeAsync(distribution)).ConfigureAwait(false);
            if (probe == null || probe.Name != distribution || !String.IsNullOrEmpty(probe.Error) || String.IsNullOrEmpty(probe.HomePath) || !probe.HomePath.StartsWith("/", StringComparison.Ordinal)) throw WslClaudeProblem("선택한 WSL 배포판을 확인하지 못했습니다. WSL을 실행한 뒤 다시 확인해 주세요.");
            if (requireClaude && String.IsNullOrEmpty(probe.ClaudePath)) throw WslClaudeProblem("선택한 WSL 배포판에 Claude Code를 설치한 뒤 다시 연결해 주세요.");
            return probe;
        }

        private static async Task<string> MakeWslCommand(string widgetExePath, string distribution)
        {
            string fullPath = Path.GetFullPath(widgetExePath);
            if (!File.Exists(fullPath)) throw new InvalidOperationException("위젯 실행 파일을 찾지 못했습니다.");
            string linuxPath = await (WslPathOverride != null ? WslPathOverride(distribution, fullPath) : WslRuntime.WindowsToLinuxPathAsync(distribution, fullPath)).ConfigureAwait(false);
            return MakeWslCommandFromLinuxPath(linuxPath, GetSourceId(distribution));
        }

        internal static string MakeWslCommandFromLinuxPath(string linuxPath, string sourceId)
        {
            SourceCachePath(sourceId);
            if (String.IsNullOrEmpty(sourceId) || String.IsNullOrEmpty(linuxPath) || !linuxPath.StartsWith("/", StringComparison.Ordinal) || linuxPath.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) throw new InvalidOperationException("위젯 경로가 올바르지 않습니다.");
            return QuotePosix(linuxPath) + " --claude-statusline --claude-source " + QuotePosix(sourceId);
        }

        private static string QuotePosix(string value) { return "'" + value.Replace("'", "'\"'\"'") + "'"; }

        private static async Task<byte[]> ReadWslConfig(string distribution, string configPath)
        {
            var result = await ExecuteWsl(distribution, WslReadConfigScript, new[] { configPath }, null).ConfigureAwait(false);
            CheckWslConfigResult(result);
            if (result.Output.Trim() == "MISSING") return null;
            if (!result.Output.StartsWith("DATA:", StringComparison.Ordinal)) throw new InvalidOperationException("Claude 설정 파일을 확인해 주세요");
            byte[] bytes = Convert.FromBase64String(result.Output.Substring(5));
            if (bytes.Length >= InputLimit) throw new InvalidOperationException("설정 파일이 너무 커서 변경하지 않았습니다.");
            return bytes;
        }

        private static Dictionary<string, object> ParseWslConfig(byte[] bytes)
        {
            if (bytes == null) return new Dictionary<string, object>();
            try {
                string json = new UTF8Encoding(false, true).GetString(bytes);
                if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
                return ReadObject(json);
            } catch (Exception ex) {
                if (!(ex is ArgumentException || ex is InvalidOperationException)) throw;
                throw new InvalidOperationException("Claude 설정 JSON 형식이 올바르지 않아 변경하지 않았습니다.");
            }
        }

        private static void CheckWslConfigResult(WslCommandResult result)
        {
            if (result == null || result.ExitCode != 0) {
                if (result != null && result.ExitCode == 43) throw new InvalidOperationException("Claude 설정을 다른 작업에서 변경 중입니다. 잠시 후 다시 연결해 주세요.");
                if (result != null && result.ExitCode == 44) throw new InvalidOperationException("Claude 설정이 변경되어 연결을 중단했습니다. 다시 시도해 주세요.");
                if (result != null && result.ExitCode == 45) throw WslClaudeProblem("이전 버전의 Claude 설정 잠금이 남아 있습니다. 이전 위젯과 연결 작업을 종료한 뒤 해당 배포판의 settings.json.nephylo-lock을 확인해 주세요. 기존 잠금과 설정은 변경하지 않았습니다.");
                if (result != null && result.ExitCode == 46) throw WslClaudeProblem("WSL 설정 잠금 기능을 사용할 수 없습니다. 배포판의 util-linux(flock) 설치 상태를 확인해 주세요.");
                if (result != null && result.ExitCode == 41) throw WslClaudeProblem("WSL Claude 설정이 심볼릭 링크여서 자동으로 변경하지 않았습니다.");
                throw WslClaudeProblem("WSL Claude 설정에 접근하지 못했습니다. 배포판과 파일 권한을 확인해 주세요.");
            }
        }

        private static ConnectionException WslClaudeProblem(string message)
        {
            return new ConnectionException(new ConnectionIssue { Title = "WSL Claude Code 연결을 확인해 주세요", Message = message, ActionLabel = "WSL 안내 열기", ActionUrl = "https://learn.microsoft.com/windows/wsl/filesystems" });
        }

        private static ConnectionException WslCustomHookProblem()
        {
            return new ConnectionException(new ConnectionIssue { Title = "기존 상태 표시줄을 유지했어요", Message = "선택한 WSL 배포판의 기존 상태 표시줄은 변경하지 않았습니다. Claude Code의 상태 표시줄 설정을 확인한 뒤 다시 연결해 주세요.", ActionLabel = "상태 표시줄 안내 열기", ActionUrl = "https://code.claude.com/docs/en/statusline" });
        }

        private static bool IsRecordedWslHook(object hook, Dictionary<string, object> manifest)
        {
            var record = hook as Dictionary<string, object>;
            string command = ManifestString(manifest, "command");
            return record != null && record.Count == 2 && !String.IsNullOrEmpty(command) && IsOurHook(hook, command);
        }

        private static async Task WriteWslConfig(string distribution, string path, byte[] original, Dictionary<string, object> config)
        {
            byte[] updated = new UTF8Encoding(false).GetBytes(Serializer(InputLimit).Serialize(config));
            if (updated.Length >= InputLimit) throw new InvalidOperationException("설정 파일이 너무 커서 변경하지 않았습니다.");
            string input = (original == null ? "MISSING" : Convert.ToBase64String(original)) + "\n" + Convert.ToBase64String(updated) + "\n";
            string backup = path + ".usage-widget-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".bak";
            var result = await ExecuteWsl(distribution, WslWriteConfigScript, new[] { path, backup }, input).ConfigureAwait(false);
            CheckWslConfigResult(result);
            if (result.Output.Trim() != "OK") throw WslClaudeProblem("WSL Claude 설정에 접근하지 못했습니다. 배포판과 파일 권한을 확인해 주세요.");
        }

        public static async Task<string> RefreshStatusLineAsync(string widgetExePath, string distribution, string windowsConfigDirectory = null)
        {
            if (String.IsNullOrEmpty(distribution)) return StatusLineStatusWindows(widgetExePath, windowsConfigDirectory);
            await WslHookGate.WaitAsync().ConfigureAwait(false);
            try {
                var probe = await ProbeClaudeWsl(distribution, false).ConfigureAwait(false);
                string command = await MakeWslCommand(widgetExePath, distribution).ConfigureAwait(false);
                var previous = ReadWslManifest(distribution);
                var config = ParseWslConfig(await ReadWslConfig(distribution, probe.HomePath.TrimEnd('/') + "/.claude/settings.json").ConfigureAwait(false));
                object hook;
                string state = !config.TryGetValue("statusLine", out hook) || hook == null ? "missing" : IsOurHook(hook, command) ? "installed" : IsRecordedWslHook(hook, previous) ? "repair" : "custom";
                // Keep the recognized old command until the user chooses Repair/Disconnect.
                SaveWslManifest(distribution, widgetExePath, state == "repair" ? ManifestString(previous, "command") : command, state);
                return StatusLineStatus(widgetExePath, distribution);
            } finally { WslHookGate.Release(); }
        }

        public static async Task InstallStatusLineAsync(string widgetExePath, string distribution, string windowsConfigDirectory = null)
        {
            if (String.IsNullOrEmpty(distribution)) { InstallStatusLineWindows(widgetExePath, windowsConfigDirectory); return; }
            await WslHookGate.WaitAsync().ConfigureAwait(false);
            try {
                var probe = await ProbeClaudeWsl(distribution, true).ConfigureAwait(false);
                string command = await MakeWslCommand(widgetExePath, distribution).ConfigureAwait(false);
                string linuxExecutable = await (WslPathOverride != null ? WslPathOverride(distribution, Path.GetFullPath(widgetExePath)) : WslRuntime.WindowsToLinuxPathAsync(distribution, Path.GetFullPath(widgetExePath))).ConfigureAwait(false);
                var interop = await ExecuteWsl(distribution, "exec \"$1\" --claude-bridge-check", new[] { linuxExecutable }, null).ConfigureAwait(false);
                if (interop.ExitCode == 3 && interop.Output.Trim() == "nephylo-claude-bridge-storage") throw BridgeProblem("storage");
                if (interop.ExitCode != 0 || interop.Output.Trim() != "nephylo-claude-bridge-v1") throw WslClaudeProblem("WSL에서 Windows 앱을 실행할 수 없습니다. WSL 상호 운용성을 켠 뒤 다시 연결해 주세요.");
                string path = probe.HomePath.TrimEnd('/') + "/.claude/settings.json";
                byte[] original = await ReadWslConfig(distribution, path).ConfigureAwait(false);
                var config = ParseWslConfig(original);
                object existing;
                if (config.TryGetValue("statusLine", out existing) && existing != null && !IsOurHook(existing, command) && !IsRecordedWslHook(existing, ReadWslManifest(distribution))) {
                    SaveWslManifest(distribution, widgetExePath, command, "custom");
                    throw WslCustomHookProblem();
                }
                if (existing == null || !IsOurHook(existing, command)) {
                    config["statusLine"] = new Dictionary<string, object> { { "type", "command" }, { "command", command } };
                    await WriteWslConfig(distribution, path, original, config).ConfigureAwait(false);
                }
                SaveWslManifest(distribution, widgetExePath, command, "installed");
            } finally { WslHookGate.Release(); }
        }

        public static async Task RemoveStatusLineAsync(string widgetExePath, string distribution, string windowsConfigDirectory = null)
        {
            if (String.IsNullOrEmpty(distribution)) { RemoveStatusLineWindows(widgetExePath, windowsConfigDirectory); return; }
            await WslHookGate.WaitAsync().ConfigureAwait(false);
            try {
                var probe = await ProbeClaudeWsl(distribution, false).ConfigureAwait(false);
                string path = probe.HomePath.TrimEnd('/') + "/.claude/settings.json";
                byte[] original = await ReadWslConfig(distribution, path).ConfigureAwait(false);
                var config = ParseWslConfig(original);
                object hook;
                if (config.TryGetValue("statusLine", out hook) && hook != null) {
                    string command = await MakeWslCommand(widgetExePath, distribution).ConfigureAwait(false);
                    if (!IsOurHook(hook, command) && !IsRecordedWslHook(hook, ReadWslManifest(distribution))) {
                        SaveWslManifest(distribution, widgetExePath, command, "custom");
                        throw WslCustomHookProblem();
                    }
                    config.Remove("statusLine");
                    await WriteWslConfig(distribution, path, original, config).ConfigureAwait(false);
                }
                SaveWslManifest(distribution, widgetExePath, "", "missing");
            } finally { WslHookGate.Release(); }
        }
    }
}
