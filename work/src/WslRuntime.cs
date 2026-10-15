using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UsageWidget
{
    public sealed class WslDistribution
    {
        public string Name = "";
        public string HomePath = "";
        public string CodexPath = "";
        public string ClaudePath = "";
        public string Error = "";
    }

    public sealed class WslCommandResult
    {
        public int ExitCode;
        public string Output = "";
    }

    public static class WslRuntime
    {
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string,WslDistribution> Cache = new Dictionary<string,WslDistribution>(StringComparer.Ordinal);
        // Tests substitute only the process boundary; production never reads a runner from settings or environment.
        internal static Func<ProcessStartInfo,string,int,CancellationToken,Task<WslCommandResult>> RunnerOverride = null;
        internal static Func<string,ProcessStartInfo> CodexStartInfoOverride = null;
        internal const int OutputLimit = 2097152;
        private const int TimeoutMs = 12000;

        // No login/interactive shell or user profile is evaluated. Ignore inherited Windows PATH entries.
        // Native and explicit standard install locations win over nvm fallback copies.
        // Probe nvm versions numerically without sourcing a login profile or nvm.sh.
        internal const string LinuxPathScript = @"PATH=""$HOME/.local/bin:$HOME/.npm-global/bin:$HOME/.npm/bin:$HOME/.volta/bin:$HOME/.bun/bin:$HOME/.local/share/mise/shims:$HOME/.asdf/shims:$HOME/bin:/usr/local/bin:/usr/bin:/bin:/usr/local/sbin:/usr/sbin:/sbin""
nephylo_nvm_versions=$(
  for nephylo_dir in ""$HOME""/.nvm/versions/node/v*/bin; do
    [ -d ""$nephylo_dir"" ] || continue
    nephylo_version=${nephylo_dir%/bin}; nephylo_version=${nephylo_version##*/}
    nephylo_numbers=${nephylo_version#v}
    case ""$nephylo_numbers"" in ''|*[!0-9.]*|.*|*.|*..*) continue ;; esac
    case ""$nephylo_numbers"" in *.*.*.*) continue ;; *.*.*) printf '%s\n' ""$nephylo_version"" ;; esac
  done | LC_ALL=C sort -Vr
)
for nephylo_version in $nephylo_nvm_versions; do
  PATH=""$PATH:$HOME/.nvm/versions/node/$nephylo_version/bin""
done
export PATH
find_tool() {
  nephylo_tool=$(command -v ""$1"" 2>/dev/null) || return 0
  case ""$nephylo_tool"" in /*) [ ! -f ""$nephylo_tool"" ] || [ ! -x ""$nephylo_tool"" ] || printf '%s' ""$nephylo_tool"" ;; esac
}
";
        internal const string LinuxCodexScript = LinuxPathScript + @"unset OPENAI_API_KEY CODEX_API_KEY
export RUST_LOG=error
cd ""$HOME"" || exit 126
nephylo_codex=$(find_tool codex)
[ -n ""$nephylo_codex"" ] || exit 127
# An npm executable under nvm must use the node runtime from that same install.
case ""$nephylo_codex"" in ""$HOME""/.nvm/versions/node/*/bin/codex) PATH=""${nephylo_codex%/*}:$PATH""; export PATH ;; esac
exec ""$nephylo_codex"" app-server
";
        private const string ProbeScript = LinuxPathScript + "printf 'NEPHYLO_WSL_V1\\000%s\\000%s\\000%s\\000' \"$HOME\" \"$(find_tool codex)\" \"$(find_tool claude)\"";

        public static bool IsValidDistributionName(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim()
                && !value.Any(c => Char.IsControl(c) || "\\/:*?\"<>|".IndexOf(c) >= 0);
        }

        private static void ValidateDistributionName(string value)
        {
            if (!IsValidDistributionName(value)) throw Problem("distribution");
        }

        internal static ConnectionException Problem(string kind)
        {
            string title = "WSL 연결을 확인해 주세요";
            string message = "WSL 배포판을 실행할 수 없습니다. Windows 터미널에서 해당 배포판이 정상적으로 열리는지 확인한 뒤 다시 탐색해 주세요.";
            if (kind == "missing") message = "WSL을 찾지 못했습니다. WSL과 Linux 배포판을 설치한 뒤 다시 탐색해 주세요.";
            else if (kind == "distribution") message = "선택한 WSL 배포판을 찾지 못했습니다. WSL 다시 탐색을 누르고 설치된 배포판을 선택해 주세요.";
            else if (kind == "timeout") message = "WSL 응답 시간이 초과되었습니다. 해당 배포판을 Windows 터미널에서 한 번 연 뒤 다시 시도해 주세요.";
            else if (kind == "response") message = "WSL 연결 정보를 읽지 못했습니다. 해당 배포판과 설치된 프로그램을 확인한 뒤 다시 탐색해 주세요.";
            else if (kind == "codex") { title = "WSL에 Codex를 설치해 주세요"; message = "선택한 WSL 배포판에서 Codex CLI를 찾지 못했습니다. 해당 배포판에 Codex를 설치한 뒤 WSL 다시 탐색을 눌러 주세요."; }
            return new ConnectionException(new ConnectionIssue { Title = title, Message = message, ActionLabel = "WSL 안내 열기", ActionUrl = "https://learn.microsoft.com/windows/wsl/basic-commands" });
        }

        private static string ExecutablePath()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string path = Path.Combine(windows, Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32", "wsl.exe");
            if (RunnerOverride == null && !File.Exists(path)) throw Problem("missing");
            return path;
        }

        internal static string QuoteArgument(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid process argument.");
            // wsl.exe parses switches before its normal argv conversion and does not recognize quoted switches.
            if (value.Length > 0 && !value.Any(c => Char.IsWhiteSpace(c) || c == '"')) return value;
            var quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { quoted.Append('\\', slashes * 2 + 1); quoted.Append(c); }
                else { quoted.Append('\\', slashes); quoted.Append(c); }
                slashes = 0;
            }
            quoted.Append('\\', slashes * 2); quoted.Append('"');
            return quoted.ToString();
        }

        private static ProcessStartInfo StartInfo(IEnumerable<string> args)
        {
            var result = new ProcessStartInfo(ExecutablePath(), String.Join(" ", args.Select(QuoteArgument))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetTempPath()
            };
            result.EnvironmentVariables.Remove("OPENAI_API_KEY");
            result.EnvironmentVariables.Remove("CODEX_API_KEY");
            return result;
        }

        public static ProcessStartInfo CreateScriptStartInfo(string distribution, string fixedScript, params string[] arguments)
        {
            ValidateDistributionName(distribution);
            if (fixedScript == null) throw new ArgumentNullException("fixedScript");
            var args = new List<string> { "--distribution", distribution, "--exec", "/bin/sh", "-c", fixedScript.Replace("\r\n","\n").Replace('\r','\n'), "nephylo" };
            if (arguments != null) args.AddRange(arguments);
            return StartInfo(args);
        }

        public static ProcessStartInfo CreateCodexStartInfo(string distribution)
        {
            ValidateDistributionName(distribution);
            var factory = CodexStartInfoOverride;
            if (factory != null) return factory(distribution);
            return CreateScriptStartInfo(distribution, LinuxCodexScript);
        }

        public static Task<WslCommandResult> ExecuteAsync(string distribution, string fixedScript, string[] arguments, string standardInput = null, int timeoutMs = TimeoutMs, CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunAsync(CreateScriptStartInfo(distribution, fixedScript, arguments), standardInput, timeoutMs, cancellationToken);
        }

        public static async Task<List<WslDistribution>> DiscoverAsync()
        {
            using (var discoveryDeadline = new CancellationTokenSource(15000))
            {
                List<string> names;
                try { names = await ListAsync(discoveryDeadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw Problem("timeout"); }
                lock (CacheLock) foreach (var previous in Cache.Keys.Where(n => !names.Contains(n,StringComparer.Ordinal)).ToArray()) Cache.Remove(previous);
                // Limit both simultaneous starts and the total scan duration, even with many registered distributions.
                using (var gate = new SemaphoreSlim(3))
                {
                    var tasks = names.Select(async name => {
                        bool acquired = false;
                        try {
                            await gate.WaitAsync(discoveryDeadline.Token).ConfigureAwait(false); acquired = true;
                            return await ProbeKnownAsync(name,discoveryDeadline.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { lock (CacheLock) Cache.Remove(name); return new WslDistribution { Name = name, Error = Problem("timeout").Message }; }
                        finally { if (acquired) gate.Release(); }
                    }).ToArray();
                    return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
                }
            }
        }

        public static async Task<WslDistribution> ProbeAsync(string distribution, CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateDistributionName(distribution);
            var names = await ListAsync(cancellationToken).ConfigureAwait(false);
            if (!names.Contains(distribution, StringComparer.Ordinal)) { lock (CacheLock) Cache.Remove(distribution); throw Problem("distribution"); }
            var result = await ProbeKnownAsync(distribution,cancellationToken).ConfigureAwait(false);
            if (!String.IsNullOrEmpty(result.Error)) throw new ConnectionException(new ConnectionIssue { Title = "WSL 연결을 확인해 주세요", Message = result.Error });
            return result;
        }

        public static bool TryGetCachedDistribution(string distribution, out WslDistribution result)
        {
            lock (CacheLock) return Cache.TryGetValue(distribution ?? "", out result);
        }

        private static async Task<List<string>> ListAsync(CancellationToken cancellationToken)
        {
            var result = await RunAsync(StartInfo(new[] { "--list", "--quiet" }),null,TimeoutMs,cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) throw Problem("missing");
            return ParseDistributionList(result.Output);
        }

        internal static List<string> ParseDistributionList(string text)
        {
            var names = new List<string>();
            foreach (string raw in (text ?? "").TrimStart('\uFEFF').Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = raw.Trim();
                if (!IsValidDistributionName(name)) throw Problem("response");
                if (!names.Contains(name,StringComparer.Ordinal)) names.Add(name);
                if (names.Count > 64) throw Problem("response");
            }
            return names;
        }

        private static async Task<WslDistribution> ProbeKnownAsync(string distribution, CancellationToken cancellationToken)
        {
            try
            {
                var result = await ExecuteAsync(distribution,ProbeScript,new string[0],null,TimeoutMs,cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0) throw Problem("launch");
                var parsed = ParseProbe(distribution,result.Output);
                lock (CacheLock) Cache[distribution] = parsed;
                return parsed;
            }
            catch (ConnectionException error) { lock (CacheLock) Cache.Remove(distribution); return new WslDistribution { Name = distribution, Error = error.Message }; }
        }

        internal static WslDistribution ParseProbe(string distribution,string output)
        {
            var fields = (output ?? "").Split('\0');
            if (fields.Length != 5 || fields[0] != "NEPHYLO_WSL_V1" || fields[4] != "" || !IsLinuxPath(fields[1],false) || !IsLinuxPath(fields[2],true) || !IsLinuxPath(fields[3],true)) throw Problem("response");
            return new WslDistribution { Name = distribution, HomePath = fields[1], CodexPath = fields[2], ClaudePath = fields[3] };
        }

        private static bool IsLinuxPath(string path, bool allowEmpty)
        {
            return (allowEmpty && path == "") || (path != null && path.Length > 0 && path.Length <= 4096 && path[0] == '/' && !path.Any(Char.IsControl));
        }

        public static async Task<string> WindowsToLinuxPathAsync(string distribution, string windowsPath)
        {
            if (String.IsNullOrEmpty(windowsPath) || !Path.IsPathRooted(windowsPath) || windowsPath.Any(Char.IsControl)) throw new ArgumentException("An absolute Windows path is required.");
            var result = await ExecuteAsync(distribution,"exec /usr/bin/wslpath -a -u \"$1\"",new[] { windowsPath }).ConfigureAwait(false);
            var path = result.Output.TrimEnd('\r','\n');
            if (result.ExitCode != 0 || !IsLinuxPath(path,false)) throw Problem("response");
            return path;
        }

        public static string ToUncPath(string distribution,string linuxPath)
        {
            ValidateDistributionName(distribution);
            if (!IsLinuxPath(linuxPath,false) || linuxPath.Split('/').Any(p => p == ".." || p == "." || p.IndexOfAny(new[] { '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)) throw new ArgumentException("Invalid Linux path.");
            return "\\\\wsl$\\" + distribution + linuxPath.Replace('/','\\');
        }

        private static Task<WslCommandResult> RunAsync(ProcessStartInfo info,string input,int timeoutMs,CancellationToken cancellationToken)
        {
            if (timeoutMs < 1 || timeoutMs > 30000) throw new ArgumentOutOfRangeException("timeoutMs");
            if (input != null && input.Length > OutputLimit * 2) throw Problem("response");
            var runner = RunnerOverride;
            return runner != null ? runner(info,input,timeoutMs,cancellationToken) : RunProcessAsync(info,input,timeoutMs,cancellationToken);
        }

        internal static async Task<WslCommandResult> RunProcessAsync(ProcessStartInfo info,string input,int timeoutMs,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var process = new Process { StartInfo = info })
            {
                try { if (!process.Start()) throw Problem("launch"); }
                catch (ConnectionException) { throw; }
                catch (Exception error) { if (error is OutOfMemoryException) throw; throw Problem("launch"); }
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(timeoutMs);
                    var output = ReadBoundedAsync(process.StandardOutput.BaseStream,true);
                    var errors = ReadBoundedAsync(process.StandardError.BaseStream,false);
                    var write = Task.Run(async () => {
                        if (input != null) { var bytes = new UTF8Encoding(false).GetBytes(input); await process.StandardInput.BaseStream.WriteAsync(bytes,0,bytes.Length).ConfigureAwait(false); }
                        process.StandardInput.Close();
                    });
                    var exited = Task.Run(() => process.WaitForExit());
                    var all = Task.WhenAll(output,errors,write,exited);
                    var canceled = new TaskCompletionSource<bool>();
                    using (deadline.Token.Register(() => canceled.TrySetResult(true)))
                    {
                        var winner = await Task.WhenAny(all,canceled.Task).ConfigureAwait(false);
                        if (!Object.ReferenceEquals(winner,all))
                        {
                            TryKill(process);
                            Observe(all);
                            cancellationToken.ThrowIfCancellationRequested();
                            throw Problem("timeout");
                        }
                        try { await all.ConfigureAwait(false); }
                        catch { TryKill(process); throw Problem("response"); }
                        string decoded;
                        try {
                            var bytes = await output.ConfigureAwait(false);
                            decoded = info.Arguments == "--list --quiet" ? Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF') : DecodeOutput(bytes);
                        }
                        catch (DecoderFallbackException) { throw Problem("response"); }
                        return new WslCommandResult { ExitCode = process.ExitCode, Output = decoded };
                    }
                }
            }
        }

        private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(); } catch { } }
        private static void Observe(Task task) { task.ContinueWith(t => { var ignored = t.Exception; },TaskContinuationOptions.OnlyOnFaulted); }
        private static async Task<byte[]> ReadBoundedAsync(Stream stream,bool retain)
        {
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[4096]; int total = 0, count;
                while ((count = await stream.ReadAsync(buffer,0,buffer.Length).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > OutputLimit) throw new InvalidDataException("WSL output exceeded its limit.");
                    if (retain) memory.Write(buffer,0,count);
                }
                return memory.ToArray();
            }
        }

        internal static string DecodeOutput(byte[] bytes)
        {
            // wsl --list is UTF-16LE, while Linux process output is UTF-8. Detect only the list form.
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] != 0 && bytes[1] == 0 && bytes.Length >= 4 && bytes[3] == 0)))
                return Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF');
            return new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');
        }
    }
}
