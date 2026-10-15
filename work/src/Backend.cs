using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    public sealed class UsageRow
    {
        public string Label;
        public double? RemainingPercent;
        public double? Remaining;
        public double? Limit;
        public DateTimeOffset? ResetsAt;
        public string Detail;
        public string DisplayValue;
        public string DisplaySubtitle;
    }

    public sealed class UsageSnapshot
    {
        public string Plan;
        public List<UsageRow> Rows = new List<UsageRow>();
        public string Note;
    }

    internal static class UsageJson
    {
        internal static Dictionary<string, object> Read(string json)
        {
            var result = new JavaScriptSerializer { MaxJsonLength = 2097152 }.DeserializeObject(json) as Dictionary<string, object>;
            if (result == null) throw new InvalidOperationException("사용량 응답 형식을 확인할 수 없습니다.");
            var wrapped = Object(result, "result");
            return wrapped ?? result;
        }

        internal static Dictionary<string, object> Object(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        internal static string String(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as string : null;
        }

        internal static double? Number(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value) || value == null || value is bool) return null;
            double number;
            if (!Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || Double.IsNaN(number) || Double.IsInfinity(number)) return null;
            return number;
        }

        internal static bool? Boolean(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) && value is bool ? (bool?)value : null;
        }

        internal static DateTimeOffset? Epoch(Dictionary<string, object> obj, string key)
        {
            var value = Number(obj, key);
            if (!value.HasValue) return null;
            try { return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(value.Value); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        internal static double Clamp(double value) { return Math.Max(0, Math.Min(100, value)); }
        internal static string Count(double value) { return value.ToString("N0", CultureInfo.GetCultureInfo("ko-KR")); }
    }

    public sealed class ElevenLabsService
    {
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 2097152 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("UsageWidget/1.0");
            return client;
        }

        public async Task<UsageSnapshot> ReadAsync(string apiKey)
        {
            if (String.IsNullOrWhiteSpace(apiKey)) throw new ConnectionException(ConnectionDiagnostics.MissingKey("eleven"));
            apiKey = apiKey.Trim();
            if (apiKey.Length > 4096 || apiKey.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ConnectionException(ConnectionDiagnostics.InvalidKeyFormat("eleven"));
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/user/subscription"))
                {
                    request.Headers.Add("xi-api-key", apiKey);
                    using (var response = await Client.SendAsync(request).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode) throw new ConnectionException(ConnectionDiagnostics.Http("eleven", (int)response.StatusCode, body),ConnectionDiagnostics.RetryAfter(response));
                        return Parse(body);
                    }
                }
            }
            catch (TaskCanceledException) { throw new ConnectionException(ConnectionDiagnostics.Timeout("eleven")); }
            catch (HttpRequestException) { throw new ConnectionException(ConnectionDiagnostics.Network("eleven")); }
            catch (FormatException) { throw new ConnectionException(ConnectionDiagnostics.InvalidKeyFormat("eleven")); }
        }

        public static string ErrorMessage(int statusCode, string json)
        {
            return ConnectionDiagnostics.Http("eleven", statusCode, json).Message;
        }

        public static UsageSnapshot Parse(string json)
        {
            Dictionary<string, object> root;
            try { root = UsageJson.Read(json); }
            catch (Exception e) { if (e is OutOfMemoryException) throw; throw new ConnectionException(ConnectionDiagnostics.Malformed("eleven")); }
            var used = UsageJson.Number(root, "character_count");
            var limit = UsageJson.Number(root, "character_limit");
            if (used < 0) used = null;
            if (limit < 0) limit = null;
            double? remaining = used.HasValue && limit.HasValue ? (double?)Math.Max(0, limit.Value - used.Value) : null;
            double? percent = remaining.HasValue && limit > 0 ? (double?)UsageJson.Clamp(remaining.Value / limit.Value * 100) : null;
            var result = new UsageSnapshot { Plan = UsageJson.String(root, "tier") ?? "ElevenLabs", Note = "계정 구독 크레딧 기준 · 웹과 API 사용량 포함" };
            var detail = used.HasValue && limit.HasValue ? "사용 " + UsageJson.Count(used.Value) + " / " + UsageJson.Count(limit.Value) + " 크레딧" : "크레딧 사용량 또는 한도가 제공되지 않았습니다.";
            if (used.HasValue && limit.HasValue && used.Value > limit.Value) detail += " · 포함량 초과 " + UsageJson.Count(used.Value - limit.Value);
            result.Rows.Add(new UsageRow { Label = "구독 크레딧", Remaining = remaining, RemainingPercent = percent, Limit = limit, ResetsAt = UsageJson.Epoch(root, "next_character_count_reset_unix"), Detail = detail });
            var status = UsageJson.String(root, "status");
            if (!String.IsNullOrEmpty(status) && status != "active" && status != "free") result.Note += " · 구독 상태: " + status;
            return result;
        }
    }

    // Login notifications can follow the RPC response before its awaiting continuation runs.
    // Keep a bounded set until the response supplies the attempt ID; never accept another login.
    internal sealed class CodexLoginAttempt
    {
        private readonly object gate = new object();
        private readonly Dictionary<string,bool> early = new Dictionary<string,bool>(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private string loginId;
        internal Task<bool> Completion { get { return completion.Task; } }
        internal void Bind(string id)
        {
            Guid parsed;
            if (!Guid.TryParse(id,out parsed) || parsed == Guid.Empty) throw new ConnectionException(ConnectionDiagnostics.CodexUnsupported());
            lock (gate) { loginId = id; bool success; if (early.TryGetValue(id,out success)) completion.TrySetResult(success); early.Clear(); }
        }
        internal void Notify(Dictionary<string,object> parameters)
        {
            string id = UsageJson.String(parameters,"loginId");
            bool? success = UsageJson.Boolean(parameters,"success");
            if (String.IsNullOrEmpty(id) || id.Length > 128 || !success.HasValue) return;
            lock (gate) {
                if (loginId != null) { if (id == loginId) completion.TrySetResult(success.Value); }
                else if (early.Count < 4) early[id] = success.Value;
            }
        }
        internal void Disconnected() { completion.TrySetException(new ConnectionException(ConnectionDiagnostics.CodexDisconnected())); }
    }

    // The short-lived login destination is never persisted or included in diagnostics.
    public sealed class CodexBrowserRecovery
    {
        private string url;
        public bool Available { get { return url != null; } }
        public static bool IsAllowed(string value)
        {
            Uri parsed;
            if (!Uri.TryCreate(value,UriKind.Absolute,out parsed) || parsed.Scheme != Uri.UriSchemeHttps || parsed.UserInfo.Length != 0 || !parsed.IsDefaultPort) return false;
            foreach (string domain in new[] { "openai.com", "chatgpt.com" })
                if (parsed.Host.Equals(domain,StringComparison.OrdinalIgnoreCase) || parsed.Host.EndsWith("." + domain,StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        public void Set(string value)
        {
            Clear();
            if (!IsAllowed(value)) throw new ConnectionException(ConnectionDiagnostics.CodexUnsupported());
            url = value;
        }
        public void Use(Action<string> action) { if (url == null) return; action(url); }
        public void Clear() { url = null; }
    }

    public sealed class CodexService : IDisposable
    {
        private readonly SemaphoreSlim operationGate = new SemaphoreSlim(1, 1);
        private readonly object stateLock = new object();
        private readonly object writeLock = new object();
        private readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private Process process;
        private string processPath;
        private int nextId;
        private bool disposed;
        private const int RequestTimeoutMs = 30000;
        internal static int LoginTimeoutMs = 180000;
        private CodexLoginAttempt activeLogin;

        public Task<UsageSnapshot> ReadAsync(string executableOverride)
        {
            return ReadAsync(executableOverride, "");
        }

        public async Task<UsageSnapshot> ReadAsync(string executableOverride, string wslDistribution)
        {
            await operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureStartedAsync(executableOverride, wslDistribution, CancellationToken.None).ConfigureAwait(false);
                return await ReadCurrentAsync().ConfigureAwait(false);
            }
            finally { operationGate.Release(); }
        }

        private async Task<UsageSnapshot> ReadCurrentAsync()
        {
                var accountResult = await RequestAsync("account/read", new { refreshToken = false }).ConfigureAwait(false);
                var account = UsageJson.Object(accountResult, "account");
                if (account == null) throw new ConnectionException(ConnectionDiagnostics.CodexLogin());
                if (UsageJson.String(account, "type") != "chatgpt") throw new ConnectionException(ConnectionDiagnostics.CodexLogin());
                var result = await RequestAsync("account/rateLimits/read", new { excludeResetCreditDetails = true }).ConfigureAwait(false);
                var snapshot = Parse(new JavaScriptSerializer().Serialize(result));
                if (String.IsNullOrEmpty(snapshot.Plan)) snapshot.Plan = UsageJson.String(account, "planType") ?? "ChatGPT";
                return snapshot;
        }

        public Task<UsageSnapshot> LoginAsync(string executableOverride, Action<string> openBrowser, CancellationToken cancellationToken)
        {
            return LoginAsync(executableOverride, "", openBrowser, cancellationToken);
        }

        public async Task<UsageSnapshot> LoginAsync(string executableOverride, string wslDistribution, Action<string> openBrowser, CancellationToken cancellationToken)
        {
            if (openBrowser == null) throw new ArgumentNullException("openBrowser");
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            CodexLoginAttempt attempt = null;
            int timedOut = 0;
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(LoginTimeoutMs);
                    using (deadline.Token.Register(() => { if (!cancellationToken.IsCancellationRequested) Interlocked.Exchange(ref timedOut,1); StopProcess(); }))
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        await EnsureStartedAsync(executableOverride, wslDistribution, deadline.Token).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        attempt = new CodexLoginAttempt();
                        lock (stateLock) activeLogin = attempt;
                        var result = await RequestAsync("account/login/start", new { type = "chatgpt" }).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        attempt.Bind(UsageJson.String(result, "loginId"));
                        var authUrl = UsageJson.String(result, "authUrl");
                        if (!CodexBrowserRecovery.IsAllowed(authUrl))
                            throw new ConnectionException(ConnectionDiagnostics.CodexUnsupported());
                        openBrowser(authUrl);
                        if (!await attempt.Completion.ConfigureAwait(false)) throw new ConnectionException(ConnectionDiagnostics.CodexLoginFailed());
                        deadline.Token.ThrowIfCancellationRequested();
                        return await ReadCurrentAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception)
            {
                StopProcess();
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref timedOut) != 0) throw new TimeoutException("Codex 로그인이 시간 내 완료되지 않았습니다. 다시 로그인해 주세요.");
                throw;
            }
            finally { lock (stateLock) if (Object.ReferenceEquals(activeLogin,attempt)) activeLogin = null; operationGate.Release(); }
        }

        private async Task EnsureStartedAsync(string executableOverride, string wslDistribution, CancellationToken cancellationToken)
        {
            if (disposed) throw new ObjectDisposedException("CodexService");
            bool useWsl = !String.IsNullOrEmpty(wslDistribution);
            if (useWsl && !WslRuntime.IsValidDistributionName(wslDistribution)) throw WslRuntime.Problem("distribution");
            var path = useWsl ? "wsl:" + wslDistribution : "windows:" + FindExecutable(executableOverride);
            lock (stateLock)
            {
                if (process != null && !process.HasExited && String.Equals(path, processPath, useWsl ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) return;
            }
            if (useWsl)
            {
                var distribution = await WslRuntime.ProbeAsync(wslDistribution,cancellationToken).ConfigureAwait(false);
                if (String.IsNullOrEmpty(distribution.CodexPath)) throw WslRuntime.Problem("codex");
            }
            cancellationToken.ThrowIfCancellationRequested();
            StopProcess();
            var info = useWsl ? WslRuntime.CreateCodexStartInfo(wslDistribution) : new ProcessStartInfo(path.Substring("windows:".Length), "app-server")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetTempPath()
            };
            // The widget never supplies an API token. Authentication is managed by Codex itself.
            info.EnvironmentVariables.Remove("OPENAI_API_KEY");
            info.EnvironmentVariables.Remove("CODEX_API_KEY");
            info.EnvironmentVariables["RUST_LOG"] = "error";
            Process child;
            try { child = Process.Start(info); }
            catch (Exception e)
            {
                if (e is OutOfMemoryException) throw;
                throw useWsl ? WslRuntime.Problem("launch") : new ConnectionException(ConnectionDiagnostics.CodexMissing(true));
            }
            lock (stateLock)
            {
                if (disposed) { try { child.Kill(); } catch { } child.Dispose(); throw new ObjectDisposedException("CodexService"); }
                process = child;
                processPath = path;
            }
            StartReaderTasks(child);
            try
            {
                await RequestAsync("initialize", new { clientInfo = new { name = "codex_elevenlabs_usage_widget", title = "Nephylo", version = "1.0.0" } }).ConfigureAwait(false);
                Write(new { method = "initialized" });
            }
            catch { StopProcess(); throw; }
        }

        private void StartReaderTasks(Process child)
        {
            // Readers deliberately outlive initialization and remain active through browser login.
            Task.Run(() => ReadOutput(child));
            Task.Run(() => DrainErrors(child));
        }

        private static void DrainErrors(Process child)
        {
            // Do not persist stderr: authentication and account details can appear in CLI diagnostics.
            try { while (child.StandardError.ReadLine() != null) { } } catch { }
        }

        private void ReadOutput(Process child)
        {
            try
            {
                string line;
                while ((line = child.StandardOutput.ReadLine()) != null)
                {
                    Dictionary<string, object> message;
                    try { message = new JavaScriptSerializer { MaxJsonLength = 2097152 }.DeserializeObject(line) as Dictionary<string, object>; }
                    catch { continue; }
                    if (message == null) continue;
                    if (!message.ContainsKey("id") && UsageJson.String(message,"method") == "account/login/completed")
                    {
                        lock (stateLock) {
                            if (!Object.ReferenceEquals(process,child)) return;
                            if (activeLogin != null) activeLogin.Notify(UsageJson.Object(message,"params"));
                        }
                        continue;
                    }
                    var numericId = UsageJson.Number(message, "id");
                    if (!numericId.HasValue || numericId.Value < Int32.MinValue || numericId.Value > Int32.MaxValue) continue;
                    int id = (int)numericId.Value;
                    TaskCompletionSource<Dictionary<string, object>> completion = null;
                    lock (stateLock)
                    {
                        if (!Object.ReferenceEquals(process, child)) return;
                        if (message.ContainsKey("method")) continue;
                        if (pending.TryGetValue(id, out completion)) pending.Remove(id);
                    }
                    if (completion == null) continue;
                    var error = UsageJson.Object(message, "error");
                    if (error != null) completion.TrySetException(new ConnectionException(ConnectionDiagnostics.CodexRpc(error)));
                    else
                    {
                        var result = UsageJson.Object(message, "result");
                        if (result == null) completion.TrySetException(new ConnectionException(ConnectionDiagnostics.Malformed("codex")));
                        else completion.TrySetResult(result);
                    }
                }
            }
            catch { }
            finally
            {
                List<TaskCompletionSource<Dictionary<string, object>>> abandoned = null;
                lock (stateLock)
                {
                    if (Object.ReferenceEquals(process, child)) { abandoned = pending.Values.ToList(); pending.Clear(); if (activeLogin != null) activeLogin.Disconnected(); }
                }
                if (abandoned != null) foreach (var completion in abandoned) completion.TrySetException(new ConnectionException(ConnectionDiagnostics.CodexDisconnected()));
            }
        }

        private async Task<Dictionary<string, object>> RequestAsync(string method, object parameters)
        {
            var completion = new TaskCompletionSource<Dictionary<string, object>>();
            int id;
            lock (stateLock)
            {
                if (disposed) throw new ObjectDisposedException("CodexService");
                id = ++nextId;
                pending.Add(id, completion);
            }
            try
            {
                Write(new { id = id, method = method, @params = parameters });
                var winner = await Task.WhenAny(completion.Task, Task.Delay(RequestTimeoutMs)).ConfigureAwait(false);
                if (!Object.ReferenceEquals(winner, completion.Task))
                {
                    StopProcess();
                    throw new ConnectionException(ConnectionDiagnostics.Timeout("codex"));
                }
                return await completion.Task.ConfigureAwait(false);
            }
            finally { lock (stateLock) pending.Remove(id); }
        }

        private void Write(object message)
        {
            var json = new JavaScriptSerializer().Serialize(message);
            try
            {
                lock (writeLock)
                {
                    Process child;
                    lock (stateLock) child = process;
                    if (child == null || child.HasExited) throw new InvalidOperationException();
                    child.StandardInput.WriteLine(json);
                    child.StandardInput.Flush();
                }
            }
            catch { throw new ConnectionException(ConnectionDiagnostics.CodexDisconnected()); }
        }

        private void StopProcess()
        {
            Process child;
            List<TaskCompletionSource<Dictionary<string, object>>> abandoned;
            lock (stateLock)
            {
                child = process;
                process = null;
                processPath = null;
                abandoned = pending.Values.ToList();
                pending.Clear();
                if (activeLogin != null) activeLogin.Disconnected();
            }
            foreach (var completion in abandoned) completion.TrySetException(new ConnectionException(ConnectionDiagnostics.CodexDisconnected()));
            if (child != null)
            {
                try { child.StandardInput.Close(); } catch { }
                try { if (!child.HasExited) child.Kill(); } catch { }
                child.Dispose();
            }
        }

        public void Dispose()
        {
            lock (stateLock) disposed = true;
            StopProcess();
        }

        public static string FindExecutable(string executableOverride)
        {
            try { return FindExecutableCore(executableOverride); }
            catch (ArgumentException) { throw new ConnectionException(ConnectionDiagnostics.CodexMissing(!String.IsNullOrWhiteSpace(executableOverride))); }
            catch (NotSupportedException) { throw new ConnectionException(ConnectionDiagnostics.CodexMissing(!String.IsNullOrWhiteSpace(executableOverride))); }
            catch (IOException) { throw new ConnectionException(ConnectionDiagnostics.CodexMissing(!String.IsNullOrWhiteSpace(executableOverride))); }
            catch (UnauthorizedAccessException) { throw new ConnectionException(ConnectionDiagnostics.CodexMissing(!String.IsNullOrWhiteSpace(executableOverride))); }
            catch (System.Security.SecurityException) { throw new ConnectionException(ConnectionDiagnostics.CodexMissing(!String.IsNullOrWhiteSpace(executableOverride))); }
        }

        private static string FindExecutableCore(string executableOverride)
        {
            if (!String.IsNullOrWhiteSpace(executableOverride))
            {
                var supplied = Environment.ExpandEnvironmentVariables(executableOverride.Trim().Trim('"'));
                if (!Path.IsPathRooted(supplied) || !File.Exists(supplied) || !Path.GetExtension(supplied).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new ConnectionException(ConnectionDiagnostics.CodexMissing(true));
                return Path.GetFullPath(supplied);
            }
            var candidates = new List<string>();
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            AddNpmCandidates(candidates, Path.Combine(appData, "npm", "node_modules", "@openai", "codex"));
            AddNpmCandidates(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node_modules", "@openai", "codex"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "codex.exe"));
            foreach (var pathEntry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var directory = pathEntry.Trim().Trim('"');
                if (String.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    candidates.Add(Path.Combine(directory, "codex.exe"));
                    AddNpmCandidates(candidates, Path.Combine(directory, "node_modules", "@openai", "codex"));
                }
                catch (ArgumentException) { }
            }
            foreach (var candidate in candidates) if (File.Exists(candidate)) return candidate;
            var registeredExecutable = CodexInstallationDiscovery.FindRegisteredExecutable();
            if (!String.IsNullOrEmpty(registeredExecutable)) return registeredExecutable;
            // Store installs may deny listing WindowsApps; explicit CLI path remains available.
            try
            {
                var storeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                foreach (var package in Directory.GetDirectories(storeRoot, "OpenAI.Codex_*", SearchOption.TopDirectoryOnly).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var relative in new[] { "app\\resources\\codex.exe", "resources\\codex.exe", "codex.exe" })
                    {
                        var candidate = Path.Combine(package, relative);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            throw new ConnectionException(ConnectionDiagnostics.CodexMissing());
        }

        private static void AddNpmCandidates(List<string> candidates, string root)
        {
            foreach (var architecture in new[] { "x64", "arm64" })
            {
                var triple = architecture == "x64" ? "x86_64-pc-windows-msvc" : "aarch64-pc-windows-msvc";
                candidates.Add(Path.Combine(root, "node_modules", "@openai", "codex-win32-" + architecture, "vendor", triple, "bin", "codex.exe"));
                candidates.Add(Path.Combine(root, "vendor", triple, "codex", "codex.exe"));
                candidates.Add(Path.Combine(root, "vendor", triple, "bin", "codex.exe"));
            }
        }

        public static UsageSnapshot Parse(string json)
        {
            Dictionary<string, object> root;
            try { root = UsageJson.Read(json); }
            catch (Exception e) { if (e is OutOfMemoryException) throw; throw new ConnectionException(ConnectionDiagnostics.Malformed("codex")); }
            var result = new UsageSnapshot { Note = "Codex 계정 전체 사용량 · 잔여 메시지 수와 다를 수 있음" };
            var map = UsageJson.Object(root, "rateLimitsByLimitId");
            if (map != null && map.Count > 0)
            {
                foreach (var entry in map.OrderBy(p => p.Key == "codex" ? 0 : 1).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var bucket = entry.Value as Dictionary<string, object>;
                    if (bucket != null) AddBucket(result, bucket, entry.Key);
                    else result.Rows.Add(new UsageRow { Label = entry.Key, Detail = "이 한도의 정보가 제공되지 않았습니다." });
                }
            }
            else
            {
                var bucket = UsageJson.Object(root, "rateLimits");
                if (bucket != null) AddBucket(result, bucket, "codex");
            }
            if (result.Rows.Count == 0) result.Rows.Add(new UsageRow { Label = "사용 한도", Detail = "계정에서 사용량 정보가 제공되지 않았습니다." });
            if (UsageJson.Boolean(root, "ordinaryUsageAllowed") == false) result.Note += " · 포함 사용량 이용 제한 상태";
            return result;
        }

        private static void AddBucket(UsageSnapshot snapshot, Dictionary<string, object> bucket, string key)
        {
            var name = UsageJson.String(bucket, "limitName") ?? (key == "codex" ? "Codex" : key);
            var plan = UsageJson.String(bucket, "planType");
            if (String.IsNullOrEmpty(snapshot.Plan) && !String.IsNullOrEmpty(plan)) snapshot.Plan = plan;
            var primary = UsageJson.Object(bucket, "primary");
            var secondary = UsageJson.Object(bucket, "secondary");
            if (primary != null) AddWindow(snapshot, primary, name, "기본 한도");
            if (secondary != null) AddWindow(snapshot, secondary, name, "추가 한도");
            if (primary == null && secondary == null) snapshot.Rows.Add(new UsageRow { Label = name, Detail = "시간별 한도 정보가 제공되지 않았습니다." });
            var individual = UsageJson.Object(bucket, "individualLimit");
            if (individual != null)
            {
                var remainingPercent = UsageJson.Number(individual, "remainingPercent");
                snapshot.Rows.Add(new UsageRow { Label = name + " · 개인 지출 한도", RemainingPercent = remainingPercent.HasValue ? (double?)UsageJson.Clamp(remainingPercent.Value) : null, ResetsAt = UsageJson.Epoch(individual, "resetsAt"), Detail = "계정의 개인 지출 한도 기준" });
            }
            var credits = UsageJson.Object(bucket, "credits");
            if (credits != null && (UsageJson.Boolean(credits, "hasCredits") == true || UsageJson.Boolean(credits, "unlimited") == true))
            {
                var unlimited = UsageJson.Boolean(credits, "unlimited") == true;
                snapshot.Rows.Add(new UsageRow { Label = name + " · 추가 크레딧", Remaining = unlimited ? null : UsageJson.Number(credits, "balance"), Detail = unlimited ? "무제한 크레딧" : "별도 크레딧 잔액 · 구독 한도와 별개" });
            }
            if (UsageJson.Boolean(bucket, "spendControlReached") == true) snapshot.Note += " · " + name + " 지출 한도 도달";
        }

        private static void AddWindow(UsageSnapshot snapshot, Dictionary<string, object> window, string name, string fallback)
        {
            var used = UsageJson.Number(window, "usedPercent");
            var minutes = UsageJson.Number(window, "windowDurationMins");
            string period = fallback;
            if (minutes.HasValue && minutes > 0)
            {
                if (minutes == 10080) period = "주간";
                else if (minutes.Value % 1440 == 0) period = (minutes.Value / 1440).ToString("0", CultureInfo.InvariantCulture) + "일";
                else if (minutes.Value % 60 == 0) period = (minutes.Value / 60).ToString("0", CultureInfo.InvariantCulture) + "시간";
                else period = minutes.Value.ToString("0", CultureInfo.InvariantCulture) + "분";
            }
            snapshot.Rows.Add(new UsageRow
            {
                Label = name + " · " + period,
                RemainingPercent = used.HasValue ? (double?)UsageJson.Clamp(100 - used.Value) : null,
                ResetsAt = UsageJson.Epoch(window, "resetsAt"),
                Detail = used.HasValue ? "현재 한도에서 " + UsageJson.Clamp(used.Value).ToString("0.#", CultureInfo.InvariantCulture) + "% 사용" : "사용 비율이 제공되지 않았습니다."
            });
        }
    }
}
