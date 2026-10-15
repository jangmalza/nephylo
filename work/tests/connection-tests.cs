using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UsageWidget;

public static class ConnectionTests
{
    private static int checks;
    private const string Secret = "sk-private-do-not-render";
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    private static string Text(ConnectionIssue issue) { return issue.Title + " " + issue.Message + " " + issue.ActionLabel + " " + issue.ActionUrl; }
    private static ConnectionIssue Fails(Action action)
    {
        try { action(); }
        catch (ConnectionException error) { checks++; return error.Issue; }
        throw new Exception("Expected structured connection error");
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Status = 401;
        internal bool Cancel;
        internal bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Cancel) throw new TaskCanceledException(Secret);
            if (Offline) throw new HttpRequestException(Secret);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(Secret) });
        }
    }

    public static int Main()
    {
        try
        {
            using (var response = new HttpResponseMessage((HttpStatusCode)429)) {
                Check(!ConnectionDiagnostics.RetryAfter(response).HasValue,"Missing Retry-After leaves the scheduler to choose its backoff");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(4));
                var deadline = ConnectionDiagnostics.RetryAfter(response);
                Check(deadline.HasValue && Math.Abs((deadline.Value - DateTimeOffset.UtcNow).TotalSeconds - 240) < 2,"Relative Retry-After is converted to an absolute retry deadline");
                var date = DateTimeOffset.UtcNow.AddHours(1);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(date);
                Check(ConnectionDiagnostics.RetryAfter(response) == date,"Absolute Retry-After is preserved");
                var scheduled = new ConnectionException(ConnectionDiagnostics.Http("eleven",429),date);
                Check(scheduled.RetryNotBefore == date && !scheduled.Message.Contains(date.ToString()),"Retry metadata is carried separately from safe user-facing diagnostics");
            }
            var permission = ConnectionDiagnostics.Http("eleven", 401, "{\"detail\":{\"code\":\"unauthorized\",\"status\":\"missing_permissions\",\"message\":\"" + Secret + "\"}}");
            Check(permission.Title.Contains("권한") && permission.Message.Contains("사용자 → 읽기") && permission.Message.Contains("user_read"), "real Eleven 401 permissions gets Korean precise permission remedy");
            Check(permission.ActionUrl == "https://elevenlabs.io/app/developers/api-keys", "permission fix opens official key settings");
            var invalid = ConnectionDiagnostics.Http("elevenlabs", 401, "{\"detail\":{\"code\":\"invalid_api_key\"}}");
            Check(invalid.Title.Contains("인증") && !invalid.Message.Contains("user_read"), "invalid Eleven key is not mislabeled as missing scope");
            Check(ConnectionDiagnostics.Http("eleven", 403, "<html>" + Secret + "</html>").Message.Contains("IP 제한"), "non JSON 403 retains useful access guidance");
            Check(ConnectionDiagnostics.Http("eleven", 401, null).Message.Contains("401"), "ambiguous 401 does not assert invalid key specifically");
            Check(ConnectionDiagnostics.Http("openai", 401).Message.Contains("일반 프로젝트 키가 아닌"), "OpenAI authentication guidance distinguishes Admin and project keys");
            Check(ConnectionDiagnostics.Http("openai", 403).Message.Contains("권한"), "OpenAI 403 explains organization permission");
            Check(ConnectionDiagnostics.Http("codex", 429).Message.Contains("기다"), "throttle recommends waiting not changing login");
            Check(ConnectionDiagnostics.Http("openai", 503).Title.Contains("잠시"), "server errors describe temporary service failure");
            Check(ConnectionDiagnostics.For("eleven", new TimeoutException(Secret)).Title.Contains("초과"), "timeout classified without exception payload");
            Check(ConnectionDiagnostics.For("openai", new TaskCanceledException(Secret)).Message.Contains("시간이 초과"), "cancelled request explains timeout");
            Check(ConnectionDiagnostics.For("openai", new WebException(Secret, WebExceptionStatus.Timeout)).Title.Contains("초과"), "WebException timeout distinct from offline");
            Check(ConnectionDiagnostics.For("eleven", new HttpRequestException(Secret)).Message.Contains("인터넷"), "network failures offer connectivity checks");
            Check(ConnectionDiagnostics.For("codex", new Exception(Secret)).Title.Contains("연결"), "unknown errors remain safe with actionable default");
            var wrapped = new ConnectionException(permission);
            Check(Object.ReferenceEquals(ConnectionDiagnostics.For("eleven", new AggregateException(wrapped)), permission), "single aggregate preserves precise underlying issue");
            Check(wrapped is InvalidOperationException, "existing exception catches remain compatible");
            var rpc = new Dictionary<string, object> { { "code", -32601 }, { "message", Secret } };
            Check(ConnectionDiagnostics.CodexRpc(rpc).Title.Contains("업데이트"), "Codex unsupported RPC offers update guide");
            rpc["code"] = 401; rpc["message"] = "expired token " + Secret;
            Check(ConnectionDiagnostics.CodexRpc(rpc).Message.Contains("브라우저로 로그인"), "expired Codex login offers exact local UI action");
            rpc["message"] = Secret;
            Check(!Text(ConnectionDiagnostics.CodexRpc(rpc)).Contains(Secret), "unknown Codex RPC never reflects raw provider message");
            rpc["code"] = 429;
            Check(ConnectionDiagnostics.CodexRpc(rpc).Title.Contains("제한"), "Codex numeric throttling code does not require English provider text");
            Check(Fails(() => CodexService.FindExecutable("relative-codex.exe")).Message.Contains("자동 검색"), "invalid Codex executable path has usable recovery guidance");
            Check(!Text(Fails(() => CodexService.FindExecutable("C:\\" + Secret + "\0.exe"))).Contains(Secret), "invalid Codex path characters cannot escape as path-bearing exceptions");
            var claudeConflict = ConnectionDiagnostics.For("claude", new InvalidOperationException("기존 Claude 상태 표시줄을 유지했습니다. 기존 스크립트에서 위젯의 --claude-statusline 명령에 같은 JSON 입력을 전달하도록 연결해 주세요."));
            Check(claudeConflict.Title.Contains("유지") && claudeConflict.Message.Contains("직접 기록") && claudeConflict.Message.Contains("변경하지 않았"), "existing Claude statusline kept with usable manual fallback");
            var claudeJson = ConnectionDiagnostics.For("claude", new InvalidOperationException("Claude 설정 JSON 형식이 올바르지 않아 변경하지 않았습니다."));
            Check(claudeJson.Message.Contains("백업") && claudeJson.Message.Contains("문법") && claudeJson.Message.Contains("변경하지 않았"), "invalid Claude JSON guides repair without overwriting");
            foreach (string text in new[] { "Claude 설정을 다른 작업에서 변경 중입니다. 잠시 후 다시 연결해 주세요.", "Claude 설정을 다른 작업에서 변경 중입니다. 잠시 후 다시 시도해 주세요.", "Claude 설정이 변경되어 연결을 중단했습니다. 다시 시도해 주세요.", "Claude 설정이 변경되어 연결 해제를 중단했습니다. 다시 시도해 주세요." })
                Check(ConnectionDiagnostics.For("claude", new InvalidOperationException(text)).Message.Contains("다른 설정 작업을 마친"), "concurrent Claude settings change recommends waiting and retry");
            var claudeWaiting = ConnectionDiagnostics.For("claude", new InvalidOperationException("Claude Code 연결 후 대화를 시작하면 사용량이 표시됩니다."));
            Check(claudeWaiting.Title.Contains("기다") && claudeWaiting.Message.Contains("다음 응답을 받은 뒤") && claudeWaiting.Message.Contains("즉시 표시되지는"), "missing Claude cache explains next ordinary response instead of immediate data");
            var claudeUnknown = ConnectionDiagnostics.For("claude", new InvalidOperationException("기존 Claude 상태 표시줄 " + Secret));
            Check(!claudeUnknown.Title.Contains("유지") && !Text(claudeUnknown).Contains(Secret) && claudeUnknown.Message.Contains("다음 응답을 받은 뒤"), "unknown Claude text is not trusted from substring and never echoed");
            Check(Fails(() => ElevenLabsService.Parse("not-json " + Secret)).Title.Contains("응답"), "malformed Eleven response is structured");
            Check(Fails(() => CodexService.Parse("not-json " + Secret)).Title.Contains("응답"), "malformed Codex response is structured");
            Check(Fails(() => new ElevenLabsService().ReadAsync("bad\r\nkey").GetAwaiter().GetResult()).Title.Contains("형식"), "bad Eleven header key rejected before sending");
            foreach (int status in new[] { 200, 401, 403, 429, 503, 302 })
            {
                using (var client = new HttpClient(new Handler { Status = status }))
                {
                    var issue = Fails(() => new OpenAiUsageService(client, () => DateTimeOffset.UtcNow, 2000).ReadAsync("test-key", null).GetAwaiter().GetResult());
                    Check(!Text(issue).Contains(Secret), "actual HTTP pipeline sanitizes failure body " + status);
                    Check(!String.IsNullOrWhiteSpace(issue.Title) && !String.IsNullOrWhiteSpace(issue.Message), "actual HTTP failure carries diagnostic " + status);
                }
            }
            foreach (bool cancel in new[] { true, false })
            {
                using (var client = new HttpClient(new Handler { Cancel = cancel, Offline = !cancel }))
                {
                    var issue = Fails(() => new OpenAiUsageService(client, () => DateTimeOffset.UtcNow, 2000).ReadAsync("test-key", null).GetAwaiter().GetResult());
                    Check(cancel ? issue.Title.Contains("초과") : issue.Title.Contains("인터넷"), "actual transport failures preserve actionable category");
                }
            }
            foreach (string service in new[] { "eleven", "elevenlabs", "openai", "codex", "claude", Secret })
            {
                var issue = ConnectionDiagnostics.For(service, new InvalidOperationException(Secret));
                Check(!Text(issue).Contains(Secret), "fallback does not reflect service ID or exception text");
                foreach (int status in new[] { 401, 403, 429, 500, 302 })
                {
                    issue = ConnectionDiagnostics.Http(service, status, "{\"detail\":{\"code\":\"" + Secret + "\",\"message\":\"" + Secret + "\",\"url\":\"https://evil.invalid/" + Secret + "\"}}");
                    Check(!Text(issue).Contains(Secret) && !Text(issue).Contains("evil.invalid"), "unknown code and malicious link never reach UI");
                    if (issue.ActionUrl != null)
                    {
                        var url = new Uri(issue.ActionUrl);
                        Check(url.Scheme == "https" && (url.Host == "elevenlabs.io" || url.Host == "platform.openai.com" || url.Host == "developers.openai.com" || url.Host == "code.claude.com"), "action destination comes only from fixed official origins");
                    }
                }
            }
            Console.WriteLine("PASS " + checks + " connection diagnostics checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
