using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using UsageWidget;

namespace UsageWidget
{
    public static class SettingsStore { public static string DataDirectory; public static string InstallDirectory; }
}

public static class AutoUpdateTests
{
    private static int checks;
    private static RSACryptoServiceProvider signingKey;
    private static string publicKey;
    private static byte[] package;
    private static string validEnvelope;
    private static JavaScriptSerializer json = new JavaScriptSerializer();

    private static void Check(bool result, string description)
    {
        if (!result) throw new Exception(description);
        checks++;
    }
    private static void Reject(Action action, string description)
    {
        bool failed = false;
        try { action(); } catch { failed = true; }
        Check(failed, description);
    }
    private static async Task RejectAsync(Func<Task> action, string description)
    {
        bool failed = false;
        try { await action(); } catch { failed = true; }
        Check(failed, description);
    }
    private static Dictionary<string, object> Payload()
    {
        using (SHA256 sha = SHA256.Create()) return new Dictionary<string, object> {
            {"schemaVersion", 1}, {"version", "1.22.0"}, {"fileName", "Nephylo-Setup-1.22.0.exe"},
            {"bytes", package.Length}, {"sha256", AutoUpdateManifest.Hex(sha.ComputeHash(package))}
        };
    }
    private static string Sign(Dictionary<string, object> payload)
    {
        return SignBytes(Encoding.UTF8.GetBytes(json.Serialize(payload)));
    }
    private static string SignBytes(byte[] bytes)
    {
        return json.Serialize(new Dictionary<string, object> {
            { "payloadBase64", Convert.ToBase64String(bytes) },
            { "signatureBase64", Convert.ToBase64String(signingKey.SignData(bytes, CryptoConfig.MapNameToOID("SHA256"))) }
        });
    }
    private static AutoUpdateClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
    {
        return new AutoUpdateClient(new Handler(callback), publicKey, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }
    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
    }
    private static int Stages()
    {
        return Directory.Exists(AutoUpdateClient.StagingRoot) ? Directory.GetDirectories(AutoUpdateClient.StagingRoot).Length : 0;
    }
    public static int Main()
    {
        string isolated = Path.Combine(Path.GetTempPath(), "nephylo-update-tests-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectory = Path.Combine(isolated, "profile");
        SettingsStore.InstallDirectory = Path.Combine(isolated, "installation");
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        try
        {
            signingKey = new RSACryptoServiceProvider(2048, new CspParameters { ProviderType = 24 });
            signingKey.PersistKeyInCsp = false;
            publicKey = signingKey.ToXmlString(false);
            package = new byte[8193];
            package[0] = (byte)'M'; package[1] = (byte)'Z';
            for (int i = 2; i < package.Length; i++) package[i] = (byte)(i % 251);
            validEnvelope = Sign(Payload());
            ManifestTests();
            Check(AutoUpdateClient.StagingRoot == Path.Combine(SettingsStore.InstallDirectory, "Updates"), "Update staging remains in the installation root after profile migration");
            Check(!AutoUpdateClient.StagingRoot.StartsWith(SettingsStore.DataDirectory, StringComparison.OrdinalIgnoreCase), "Signed update files do not share the account profile directory");
            NetworkTests().GetAwaiter().GetResult();
            Console.WriteLine("Auto-update tests passed: " + checks);
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            if (signingKey != null) signingKey.Dispose();
            // A test-created, absolute, GUID-scoped temporary folder only.
            string full = Path.GetFullPath(isolated);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("nephylo-update-tests-", StringComparison.Ordinal) && Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }

    private static void ManifestTests()
    {
        AutoUpdateManifest valid = AutoUpdateManifest.ParseAndVerify(validEnvelope, publicKey);
        Check(valid.VersionText == "1.22.0" && valid.Version == new Version(1, 22, 0, 0), "canonical signed version parsed");
        Check(valid.InstallerUri.AbsoluteUri == "https://nephylo.com/downloads/Nephylo-Setup-1.22.0.exe", "URL fixed by canonical version");
        Check(!valid.IsNewerThan(new Version(1, 22, 0)) && !valid.IsNewerThan(new Version(1, 22, 0, 0)), "same three/four part versions never update");
        Check(valid.IsNewerThan(new Version(1, 21, 0, 0)), "newer version accepted");
        Check(!valid.IsNewerThan(new Version(2, 0, 0, 0)), "downgrade rejected");
        using (RSACryptoServiceProvider otherKey = new RSACryptoServiceProvider(2048))
        {
            otherKey.PersistKeyInCsp = false;
            Reject(delegate { AutoUpdateManifest.ParseAndVerify(validEnvelope, otherKey.ToXmlString(false)); }, "untrusted signing key rejected");
        }
        var outer = json.Deserialize<Dictionary<string, object>>(validEnvelope);
        byte[] mutated = Convert.FromBase64String((string)outer["payloadBase64"]); mutated[10] ^= 1;
        outer["payloadBase64"] = Convert.ToBase64String(mutated);
        Reject(delegate { AutoUpdateManifest.ParseAndVerify(json.Serialize(outer), publicKey); }, "tampered signed payload rejected");
        outer = json.Deserialize<Dictionary<string, object>>(validEnvelope);
        outer["signatureBase64"] = Convert.ToBase64String(new byte[256]);
        Reject(delegate { AutoUpdateManifest.ParseAndVerify(json.Serialize(outer), publicKey); }, "tampered signature rejected");
        foreach (string invalid in new[] { "", "[]", "{}", new string('x', 16385), "{\"payloadBase64\":\"!!\",\"signatureBase64\":\"!!\"}" })
            Reject(delegate { AutoUpdateManifest.ParseAndVerify(invalid, publicKey); }, "malformed/bounded envelope rejected");
        foreach (string invalidVersion in new[] { "1.2", "1.2.3.4", "01.2.3", "1.2.-3", "65536.2.3", "../x", "1.22.0\n" })
        {
            var values = Payload(); values["version"] = invalidVersion;
            Reject(delegate { AutoUpdateManifest.ParseAndVerify(Sign(values), publicKey); }, "invalid canonical version rejected");
        }
        foreach (object size in new object[] { 0, 63, 104857601L, "8193", 8193.5, true })
        {
            var values = Payload(); values["bytes"] = size;
            Reject(delegate { AutoUpdateManifest.ParseAndVerify(Sign(values), publicKey); }, "invalid installer length rejected");
        }
        foreach (string filename in new[] { "../Nephylo-Setup-1.22.0.exe", "C:\\other.exe", "https://evil.test/a.exe", "Nephylo-Setup-1.21.0.exe", "Nephylo-Setup-1.22.0.exe:stream" })
        {
            var values = Payload(); values["fileName"] = filename;
            Reject(delegate { AutoUpdateManifest.ParseAndVerify(Sign(values), publicKey); }, "filename cannot redirect or traverse");
        }
        var badHash = Payload(); badHash["sha256"] = new string('g', 64);
        Reject(delegate { AutoUpdateManifest.ParseAndVerify(Sign(badHash), publicKey); }, "non SHA256 hash rejected");
        var futureSchema = Payload(); futureSchema["schemaVersion"] = 2;
        Reject(delegate { AutoUpdateManifest.ParseAndVerify(Sign(futureSchema), publicKey); }, "unknown manifest schema rejected");
        var url = Payload(); url["url"] = "https://evil.test/";
        Reject(delegate { AutoUpdateManifest.ParseAndVerify(Sign(url), publicKey); }, "extra remote fields rejected");
        Reject(delegate { AutoUpdateManifest.ParseAndVerify(SignBytes(new byte[] { 255, 254, 253 }), publicKey); }, "invalid signed UTF8 rejected");
    }

    private static async Task NetworkTests()
    {
        AutoUpdateManifest manifest = AutoUpdateManifest.ParseAndVerify(validEnvelope, publicKey);
        using (var client = Client((request, token) => Task.FromResult(Response(request, Encoding.UTF8.GetBytes(validEnvelope)))))
        {
            Check(await client.CheckAsync(new Version(1, 21, 0), CancellationToken.None) != null, "new signed manifest returned");
            Check(await client.CheckAsync(new Version(1, 22, 0, 0), CancellationToken.None) == null, "same version no update");
            Check(await client.CheckAsync(new Version(2, 0, 0), CancellationToken.None) == null, "older server release no update");
        }
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Redirect, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PartialContent, HttpStatusCode.NotFound })
            using (var client = Client((request, token) => Task.FromResult(new HttpResponseMessage(status) {
                RequestMessage = request, Content = new StringContent(validEnvelope), Headers = { Location = new Uri("https://other.example/download") }
            })))
                await RejectAsync(async delegate { await client.CheckAsync(new Version(1, 0), CancellationToken.None); }, "non-200 and redirects rejected");
        using (var client = Client((request, token) => Task.FromResult(Response(new HttpRequestMessage(HttpMethod.Get, "https://other.example/update.json"), Encoding.UTF8.GetBytes(validEnvelope)))))
            await RejectAsync(async delegate { await client.CheckAsync(new Version(1, 0), CancellationToken.None); }, "unexpected final response URL rejected");
        using (var client = Client((request, token) => { var response = Response(request, Encoding.UTF8.GetBytes(validEnvelope)); response.Content.Headers.ContentEncoding.Add("gzip"); return Task.FromResult(response); }))
            await RejectAsync(async delegate { await client.CheckAsync(new Version(1, 0), CancellationToken.None); }, "encoded manifest rejected");
        using (var client = Client((request, token) => Task.FromResult(Response(request, new byte[16385]))))
            await RejectAsync(async delegate { await client.CheckAsync(new Version(1, 0), CancellationToken.None); }, "oversized manifest rejected");
        using (var client = Client((request, token) => { var response = Response(request, new byte[16385]); response.Content.Headers.ContentLength = null; return Task.FromResult(response); }))
            await RejectAsync(async delegate { await client.CheckAsync(new Version(1, 0), CancellationToken.None); }, "oversized chunked manifest rejected");
        using (var client = Client(async (request, token) => { await Task.Delay(5000, token); return Response(request, package); }))
        using (var cancellation = new CancellationTokenSource(30))
            await RejectAsync(async delegate { await client.CheckAsync(new Version(1, 0), cancellation.Token); }, "manifest request cancellable");

        using (var client = Client((request, token) => Task.FromResult(Response(request, package))))
        {
            PreparedUpdate first = await client.DownloadAsync(manifest, CancellationToken.None);
            Check(File.Exists(first.InstallerPath) && File.Exists(first.ManifestJsonPath), "verified installer and signed manifest staged");
            manifest.VerifyInstaller(first.InstallerPath); checks++;
            Check(File.ReadAllText(first.ManifestJsonPath) == validEnvelope, "helper receives original signed envelope");
            PreparedUpdate second = await client.DownloadAsync(manifest, CancellationToken.None);
            Check(first.DirectoryPath != second.DirectoryPath && Stages() == 2, "retries/concurrent downloads use distinct staging folders");
            File.WriteAllBytes(first.InstallerPath, new byte[package.Length]);
            Reject(delegate { manifest.VerifyInstaller(first.InstallerPath); }, "staged installer tampering rejected on recheck");
            string outside = Path.Combine(SettingsStore.DataDirectory, "settings.json"); File.WriteAllText(outside, "KEEP");
            AutoUpdateClient.CleanupStagingDirectory(SettingsStore.DataDirectory);
            Check(File.ReadAllText(outside) == "KEEP", "cleanup cannot remove user settings");
            AutoUpdateClient.CleanupStagingDirectory(first.DirectoryPath);
            AutoUpdateClient.CleanupStagingDirectory(second.DirectoryPath);
            Check(Stages() == 0, "known staging folders cleaned");
        }
        byte[] corrupt = (byte[])package.Clone(); corrupt[10] ^= 1;
        foreach (byte[] invalid in new[] { corrupt, new byte[20], new byte[package.Length + 1] })
            using (var client = Client((request, token) => Task.FromResult(Response(request, invalid))))
            {
                await RejectAsync(async delegate { await client.DownloadAsync(manifest, CancellationToken.None); }, "wrong hash/truncated/oversized installer rejected");
                Check(Stages() == 0, "failed download leaves no stage");
            }
        using (var client = Client((request, token) => { var response = Response(request, new byte[package.Length + 1]); response.Content.Headers.ContentLength = null; return Task.FromResult(response); }))
        {
            await RejectAsync(async delegate { await client.DownloadAsync(manifest, CancellationToken.None); }, "stream enforces signed length without Content-Length");
            Check(Stages() == 0, "chunked oversized cleanup");
        }
        using (var client = Client(async (request, token) => { await Task.Delay(5000, token); return Response(request, package); }))
        using (var cancellation = new CancellationTokenSource(30))
        {
            await RejectAsync(async delegate { await client.DownloadAsync(manifest, cancellation.Token); }, "installer request cancellable");
            Check(Stages() == 0, "cancelled installer cleanup");
        }
        using (var client = new AutoUpdateClient(new Handler(async (request, token) => { await Task.Delay(5000, token); return Response(request, package); }), publicKey, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30)))
        {
            await RejectAsync(async delegate { await client.CheckAsync(new Version(1, 0), CancellationToken.None); }, "manifest deadline enforced");
            await RejectAsync(async delegate { await client.DownloadAsync(manifest, CancellationToken.None); }, "installer deadline enforced");
            Check(Stages() == 0, "timed out installer cleanup");
        }
        using (var client = new AutoUpdateClient(new Handler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            RequestMessage = request, Content = new StreamContent(new StalledStream())
        })), publicKey, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)))
        {
            var watch = Stopwatch.StartNew();
            await RejectAsync(async delegate { await client.CheckAsync(new Version(1, 0), CancellationToken.None); }, "stalled manifest body aborted after headers");
            await RejectAsync(async delegate { await client.DownloadAsync(manifest, CancellationToken.None); }, "stalled installer body aborted after headers");
            Check(watch.Elapsed < TimeSpan.FromSeconds(2), "deadline disposes stalled streams even if ReadAsync ignores its cancellation token");
            Check(Stages() == 0, "stalled stream stage cleanup");
        }
        using (var client = Client((request, token) => Task.FromResult(Response(request, package))))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await RejectAsync(async delegate { await client.DownloadAsync(manifest, cancellation.Token); }, "pre-cancelled download rejected");
            Check(Stages() == 0, "pre-cancel does not create staging folder");
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) { this.callback = callback; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { return callback(request, cancellationToken); }
    }

    // Models the .NET Framework network stream which may ignore ReadAsync cancellation.
    private sealed class StalledStream : Stream
    {
        private readonly TaskCompletionSource<int> pending = new TaskCompletionSource<int>();
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { return pending.Task; }
        protected override void Dispose(bool disposing) { pending.TrySetException(new IOException("Test stream disposed")); base.Dispose(disposing); }
        public override void Flush() { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long length) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
