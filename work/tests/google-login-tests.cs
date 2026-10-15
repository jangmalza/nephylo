using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using UsageWidget;

internal static class GoogleLoginTests
{
    private const string Id = "19bd679d-8d7e-4865-9657-2400c3503e38";
    private const string OtherId = "109bb8de-d519-42a6-bb2e-e626d64182aa";
    private const string Code = "ce7373b0-dbb6-411f-97bc-7a6b12d29180";
    private const string Address = "member@example.com";
    private static int checks, dirs;
    private static string root;
    public static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "UsageWidget-Google-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + checks + " Google login checks"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static string NewDirectory() { string path = Path.Combine(root, (++dirs).ToString()); Directory.CreateDirectory(path); return path; }
    private static MembershipConfiguration Config(bool enabled) { return MembershipConfiguration.FromValues("https://test.supabase.co/", "sb_publishable_public_test_key", enabled); }
    private static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>();
        foreach (string pair in uri.Query.TrimStart('?').Split('&')) { int position = pair.IndexOf('='); result[Uri.UnescapeDataString(pair.Substring(0, position))] = Uri.UnescapeDataString(pair.Substring(position + 1)); }
        return result;
    }
    private static Uri Redirect(Uri authorize) { return new Uri(Query(authorize)["redirect_to"]); }
    private static Uri Callback(Uri authorize) { var redirect = Redirect(authorize); return new Uri(redirect.AbsoluteUri + "&code=" + Code); }
    private static string Challenge(string verifier) { using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
    private static HttpResponseMessage Json(object value) { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JavaScriptSerializer().Serialize(value), Encoding.UTF8, "application/json") }; }
    private static object Session() { return new { access_token = "access-only-in-memory", refresh_token = "refresh-dpapi-secret", expires_in = 3600, provider_token = "google-provider-secret", user = new { id = Id, email = Address } }; }
    private static object User(string id, string confirmed) { return new { id = id, email = Address, email_confirmed_at = confirmed }; }
    private static object Membership(string plan) { return new { user_id = Id, email = Address, plan = plan, status = plan == "pro" ? "active" : "free", expires_at = plan == "pro" ? "2026-10-12T00:00:00Z" : null, server_time = "2026-10-11T00:00:00Z", is_admin = false }; }
    private static async Task Failure(Func<Task> action, string expected)
    {
        try { await action(); throw new Exception("Expected failure"); }
        catch (InvalidOperationException error) { Check(error.Message.Contains(expected) && !error.Message.Contains("secret") && !error.Message.Contains(Code), "safe translated failure: " + expected); }
    }
    private static async Task<string> Send(Uri callback, string target = null, string host = null, string verb = "GET")
    {
        using (var client = new TcpClient()) {
            await client.ConnectAsync(IPAddress.Loopback, callback.Port);
            using (var stream = client.GetStream()) {
                byte[] request = Encoding.ASCII.GetBytes(verb + " " + (target ?? callback.PathAndQuery) + " HTTP/1.1\r\nHost: " + (host ?? callback.Authority) + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(request, 0, request.Length);
                using (var data = new MemoryStream()) {
                    byte[] buffer = new byte[1024]; int count;
                    try { while ((count = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0) data.Write(buffer, 0, count); }
                    catch (IOException) { }
                    return Encoding.UTF8.GetString(data.ToArray());
                }
            }
        }
    }
    private static async Task Run()
    {
        Check(!MembershipConfiguration.FromValues("https://test.supabase.co/", "sb_publishable_public_test_key").GoogleEnabled, "backward-compatible config leaves Google disabled");
        Check(Config(true).GoogleEnabled && Config(true).IsConfigured, "explicit Google flag accepted with safe public config");
        Check(!MembershipConfiguration.FromValues("", "", true).GoogleEnabled, "flag cannot configure missing server");
        foreach (bool demo in new[] { false, true }) {
            var handler = new FakeHandler(); int opens = 0;
            using (var client = new MembershipClient(NewDirectory(), demo, Config(demo), handler, () => 0)) {
                await Failure(() => client.SignInWithGoogleAsync(uri => opens++, TimeSpan.FromSeconds(3)), "설정");
                Check(!client.IsGoogleConfigured && opens == 0 && handler.Calls == 0, "disabled or preview never opens provider");
            }
        }

        // A real loopback socket validates redirect, PKCE, request provenance and bounded parsing.
        Uri firstAuthorize = null;
        using (var flow = new MembershipGoogleFlow(new Uri("https://test.supabase.co/"))) {
            firstAuthorize = flow.AuthorizationUri; var query = Query(firstAuthorize); var callback = Callback(firstAuthorize);
            Check(firstAuthorize.Scheme == "https" && firstAuthorize.Host == "test.supabase.co" && firstAuthorize.AbsolutePath == "/auth/v1/authorize", "only configured HTTPS authority is opened");
            Check(query["provider"] == "google" && query["code_challenge_method"] == "s256", "Google OAuth uses S256 PKCE");
            Check(query["scopes"] == "openid email profile", "only profile identity scopes requested");
            Check(flow.Verifier.Length == 64 && query["code_challenge"] == Challenge(flow.Verifier), "cryptographic verifier and S256 challenge match");
            Check(callback.Host == "127.0.0.1" && callback.Scheme == "http" && callback.Port > 0 && callback.AbsolutePath.StartsWith("/auth/google/"), "callback binds loopback ephemeral port and random path");
            Check(!firstAuthorize.AbsoluteUri.Contains(flow.Verifier) && !firstAuthorize.AbsoluteUri.Contains("secret"), "browser URL has no verifier or secret");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10))) {
                Task<string> receive = flow.ReceiveCodeAsync(uri => { }, timeout.Token);
                foreach (string target in new[] { "/favicon.ico", callback.PathAndQuery.Replace("state=", "state=wrong"), callback.PathAndQuery + "&state=duplicate", callback.PathAndQuery + "&code=" + Code, callback.AbsoluteUri, callback.PathAndQuery + "&access_token=secret", callback.PathAndQuery.Replace(Code, "not-a-valid-code") }) {
                    string response = await Send(callback, target);
                    Check(response.StartsWith("HTTP/1.1 400") && !receive.IsCompleted, "invalid callback cannot consume pending request");
                    Check(!response.Contains(Code) && !response.Contains("secret"), "callback values never echoed");
                }
                Check((await Send(callback, null, "attacker.example")).StartsWith("HTTP/1.1 400") && !receive.IsCompleted, "Host binding prevents foreign origin callback");
                Check((await Send(callback, null, null, "POST")).StartsWith("HTTP/1.1 400") && !receive.IsCompleted, "only browser GET allowed");
                await Send(callback, "/" + new string('a', 9000));
                Check(!receive.IsCompleted, "oversized headers cannot consume pending callback");
                string success = await Send(callback);
                Check(await receive == Code, "valid exact state/path callback yields one code");
                Check(success.Contains("Cache-Control: no-store") && success.Contains("Referrer-Policy: no-referrer") && success.Contains("frame-ancestors 'none'"), "callback response prevents caching and framing");
                Check(success.Contains("history.replaceState") && !success.Contains(Code) && !success.Contains(Query(callback)["state"]), "callback address cleaned without reflecting credentials");
            }
        }
        using (var next = new MembershipGoogleFlow(new Uri("https://test.supabase.co/"))) {
            Check(Query(next.AuthorizationUri)["code_challenge"] != Query(firstAuthorize)["code_challenge"] && Redirect(next.AuthorizationUri).AbsolutePath != Redirect(firstAuthorize).AbsolutePath, "each attempt has unique verifier and callback path");
        }

        var http = new FakeHandler(); string directory = NewDirectory(); Uri opened = null; Task<string> browserReply = null;
        http.Add(async request => {
            var payload = MembershipClient.ParseJson(await request.Content.ReadAsStringAsync());
            Check(request.Method == HttpMethod.Post && request.RequestUri.PathAndQuery == "/auth/v1/token?grant_type=pkce", "code exchange uses documented PKCE grant");
            Check((string)payload["auth_code"] == Code && Challenge((string)payload["code_verifier"]) == Query(opened)["code_challenge"], "server receives exact callback code and private matching verifier");
            Check(request.Headers.Authorization == null && request.Headers.Contains("apikey"), "code exchange uses only public project key");
            return Json(Session());
        });
        http.Add(request => {
            Check(request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/auth/v1/user" && request.Content == null, "GET user validates returned access token");
            Check(request.Headers.Authorization.Parameter == "access-only-in-memory", "identity check authenticates Supabase access token");
            return Task.FromResult(Json(User(Id, "2026-10-11T00:00:00Z")));
        });
        http.Add(request => {
            Check(request.RequestUri.AbsolutePath == "/rest/v1/rpc/get_my_membership", "membership checked independently after identity");
            return Task.FromResult(Json(Membership("free")));
        });
        using (var client = new MembershipClient(directory, false, Config(true), http, () => 0)) {
            await client.SignInWithGoogleAsync(uri => { opened = uri; browserReply = Send(Callback(uri)); }, TimeSpan.FromSeconds(10));
            await browserReply;
            Check(client.IsSignedIn && client.Email == Address && !client.HasPro && !client.IsGoogleSignInPending, "Google identity alone remains Free");
            byte[] protectedFile = File.ReadAllBytes(Path.Combine(directory, "membership-session.dat"));
            Check(!Encoding.UTF8.GetString(protectedFile).Contains("refresh-dpapi-secret"), "refresh token is encrypted on disk");
            string saved = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedFile, Encoding.UTF8.GetBytes("UsageWidget.Membership.v1|https://test.supabase.co/"), DataProtectionScope.CurrentUser));
            Check(saved.Contains("refresh-dpapi-secret") && !saved.Contains("access-only-in-memory") && !saved.Contains("google-provider-secret") && !saved.Contains("code_verifier") && !saved.Contains("plan"), "only existing DPAPI refresh session persists; no OAuth/provider/plan data");
            http.Reply(Membership("pro")); await client.RefreshAsync();
            Check(client.HasPro, "only server membership can grant Pro after Google login");
        }

        foreach (object identity in new[] { User(OtherId, "2026-10-11T00:00:00Z"), User(Id, null), User(Id, "not-a-date") }) {
            http = new FakeHandler(); directory = NewDirectory(); http.Reply(Session()); http.Reply(identity);
            using (var client = new MembershipClient(directory, false, Config(true), http, () => 0)) {
                await Failure(() => client.SignInWithGoogleAsync(uri => { browserReply = Send(Callback(uri)); }, TimeSpan.FromSeconds(10)), "완료하지");
                await browserReply;
                Check(!client.IsSignedIn && !client.HasPro && http.Calls == 2 && Directory.GetFiles(directory).Length == 0, "mismatched or unconfirmed identity never persists or fetches entitlement");
            }
        }

        http = new FakeHandler();
        using (var client = new MembershipClient(NewDirectory(), false, Config(true), http, () => 0)) {
            await Failure(() => client.SignInWithGoogleAsync(uri => { browserReply = Send(new Uri(Redirect(uri).AbsoluteUri + "&error=access_denied&error_description=secret-provider-text")); }, TimeSpan.FromSeconds(10)), "취소");
            Check(!(await browserReply).Contains("secret-provider-text") && http.Calls == 0, "provider denial is translated without token request or raw provider text");
            await Failure(() => client.SignInWithGoogleAsync(uri => { throw new Exception("secret-browser-error"); }, TimeSpan.FromSeconds(10)), "브라우저");
            Check(!client.IsGoogleSignInPending && !client.IsSignedIn, "browser launch failure cleans pending flow");
            await Failure(() => client.SignInWithGoogleAsync(uri => { }, TimeSpan.FromMilliseconds(150)), "만료");
            Check(!client.IsGoogleSignInPending && !client.IsSignedIn && http.Calls == 0, "deadline closes unused flow without network or session");
        }

        // Pending browser and in-flight token exchange must be invalidated by cancellation/logout/disposal.
        foreach (string end in new[] { "cancel", "logout", "dispose" }) {
            http = new FakeHandler(); directory = NewDirectory(); Uri pendingCallback = null;
            var client = new MembershipClient(directory, false, Config(true), http, () => 0);
            Task signIn = client.SignInWithGoogleAsync(uri => pendingCallback = Callback(uri), TimeSpan.FromSeconds(10));
            Check(client.IsGoogleSignInPending && pendingCallback != null, "pending state exposed for Cancel UI");
            if (end == "cancel") client.CancelGoogleSignIn(); else if (end == "logout") await client.SignOutAsync(); else client.Dispose();
            await signIn;
            Check(!client.IsSignedIn && !client.HasPro && !client.IsGoogleSignInPending && http.Calls == 0 && Directory.GetFiles(directory).Length == 0, end + " rejects late browser identity");
            bool closed = false;
            try { await Send(pendingCallback); } catch (SocketException) { closed = true; }
            Check(closed, end + " closes loopback listening socket");
            client.Dispose();
        }
        http = new FakeHandler(); directory = NewDirectory();
        var exchangeStarted = new TaskCompletionSource<bool>(); var exchangeReply = new TaskCompletionSource<HttpResponseMessage>();
        http.Add(request => { exchangeStarted.TrySetResult(true); return exchangeReply.Task; });
        using (var client = new MembershipClient(directory, false, Config(true), http, () => 0)) {
            Uri usedCallback = null;
            Task signIn = client.SignInWithGoogleAsync(uri => { usedCallback = Callback(uri); browserReply = Send(usedCallback); }, TimeSpan.FromSeconds(10));
            await exchangeStarted.Task;
            bool closed = false;
            try { await Send(usedCallback); } catch (SocketException) { closed = true; }
            Check(closed, "listener closes immediately after callback, before token exchange finishes");
            await client.SignOutAsync(); exchangeReply.SetResult(Json(Session())); await signIn; await browserReply;
            Check(!client.IsSignedIn && !client.HasPro && Directory.GetFiles(directory).Length == 0, "late token exchange cannot revive signed-out account");
            Check(http.Calls == 1, "canceled exchange does not validate user or query membership");
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        internal int Calls;
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> replies = new Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>();
        internal void Add(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) { replies.Enqueue(callback); }
        internal void Reply(object value) { Add(request => Task.FromResult(Json(value))); }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Calls++;
            if (replies.Count == 0) throw new Exception("Unexpected request: " + request.RequestUri.AbsolutePath);
            return replies.Dequeue()(request);
        }
    }
}
