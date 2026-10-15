using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // The server supplies data, never a command, executable path, or arbitrary download URL.
    public sealed class AutoUpdateManifest
    {
        public const int MaximumManifestBytes = 16384;
        public const long MaximumInstallerBytes = 100L * 1024L * 1024L;
        public Version Version { get; private set; }
        public string VersionText { get; private set; }
        public string FileName { get; private set; }
        public long Bytes { get; private set; }
        public string Sha256 { get; private set; }
        public string SignedEnvelope { get; private set; }
        public Uri InstallerUri { get { return new Uri(AutoUpdateClient.DownloadBase + FileName); } }

        private AutoUpdateManifest() { }

        public static AutoUpdateManifest ParseAndVerify(string envelope)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsageWidget.UpdateSigningPublicKey.xml"))
            {
                if (stream == null) throw new InvalidDataException("The update verification key is unavailable.");
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    return ParseAndVerify(envelope, reader.ReadToEnd());
            }
        }

        internal static AutoUpdateManifest ParseAndVerify(string envelope, string publicKeyXml)
        {
            if (String.IsNullOrEmpty(envelope) || Encoding.UTF8.GetByteCount(envelope) > MaximumManifestBytes)
                throw InvalidManifest();
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = MaximumManifestBytes, RecursionLimit = 8 };
            Dictionary<string, object> outer;
            try { outer = serializer.Deserialize<Dictionary<string, object>>(envelope); }
            catch (Exception exception)
            {
                if (!(exception is ArgumentException) && !(exception is InvalidOperationException)) throw;
                throw InvalidManifest();
            }
            string payloadText = ReadString(outer, "payloadBase64");
            string signatureText = ReadString(outer, "signatureBase64");
            if (outer.Count != 2 || payloadText.Length > 8192 || signatureText.Length > 1024) throw InvalidManifest();
            byte[] payload, signature;
            try { payload = Convert.FromBase64String(payloadText); signature = Convert.FromBase64String(signatureText); }
            catch (FormatException) { throw InvalidManifest(); }
            if (payload.Length == 0 || payload.Length > 4096 || signature.Length != 256) throw InvalidManifest();
            try
            {
                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(new CspParameters { ProviderType = 24 }))
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(publicKeyXml);
                    if (rsa.KeySize != 2048 || !rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature))
                        throw InvalidManifest();
                }
            }
            catch (CryptographicException) { throw InvalidManifest(); }
            Dictionary<string, object> values;
            try { values = serializer.Deserialize<Dictionary<string, object>>(new UTF8Encoding(false, true).GetString(payload)); }
            catch (Exception exception)
            {
                if (!(exception is ArgumentException) && !(exception is InvalidOperationException)) throw;
                throw InvalidManifest();
            }
            if (values == null || values.Count != 5 || ReadInteger(values, "schemaVersion") != 1) throw InvalidManifest();
            string versionText = ReadString(values, "version");
            Version parsedVersion;
            if (!Regex.IsMatch(versionText, @"\A(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\z", RegexOptions.CultureInvariant)
                || !System.Version.TryParse(versionText, out parsedVersion)
                || parsedVersion.Major > 65535 || parsedVersion.Minor > 65535 || parsedVersion.Build > 65535)
                throw InvalidManifest();
            string expectedFile = "Nephylo-Setup-" + versionText + ".exe";
            string file = ReadString(values, "fileName");
            long bytes = ReadInteger(values, "bytes");
            string hash = ReadString(values, "sha256");
            if (file != expectedFile || bytes < 64 || bytes > MaximumInstallerBytes
                || !Regex.IsMatch(hash, @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant)) throw InvalidManifest();
            return new AutoUpdateManifest {
                Version = NormalizeVersion(parsedVersion), VersionText = versionText, FileName = file,
                Bytes = bytes, Sha256 = hash, SignedEnvelope = envelope
            };
        }

        public bool IsNewerThan(Version currentVersion)
        {
            if (currentVersion == null) throw new ArgumentNullException("currentVersion");
            return Version.CompareTo(NormalizeVersion(currentVersion)) > 0;
        }

        internal static Version NormalizeVersion(Version value)
        {
            return new Version(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
        }

        // The installer helper calls this again immediately before execution.
        public void VerifyInstaller(string installerPath)
        {
            if (!String.Equals(Path.GetFileName(installerPath), FileName, StringComparison.Ordinal)) throw InvalidManifest();
            if ((File.GetAttributes(installerPath) & FileAttributes.ReparsePoint) != 0) throw InvalidManifest();
            using (FileStream stream = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length != Bytes || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw InvalidManifest();
                stream.Position = 0;
                using (SHA256 sha = SHA256.Create())
                    if (!String.Equals(Hex(sha.ComputeHash(stream)), Sha256, StringComparison.Ordinal)) throw InvalidManifest();
            }
        }

        internal static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        private static InvalidDataException InvalidManifest() { return new InvalidDataException("The update package could not be verified."); }
        private static string ReadString(Dictionary<string, object> values, string name)
        {
            object value;
            if (values == null || !values.TryGetValue(name, out value) || !(value is string)) throw InvalidManifest();
            return (string)value;
        }
        private static long ReadInteger(Dictionary<string, object> values, string name)
        {
            object value;
            if (values == null || !values.TryGetValue(name, out value) || (!(value is int) && !(value is long))) throw InvalidManifest();
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
    }

    public sealed class PreparedUpdate
    {
        public AutoUpdateManifest Manifest { get; internal set; }
        public string DirectoryPath { get; internal set; }
        public string InstallerPath { get; internal set; }
        public string ManifestJsonPath { get; internal set; }
    }

    public sealed class AutoUpdateClient : IDisposable
    {
        public const string DownloadBase = "https://nephylo.com/downloads/";
        public static readonly Uri ManifestUri = new Uri(DownloadBase + "update.json");
        private readonly HttpClient client;
        private readonly string publicKeyForTests;
        private readonly TimeSpan manifestTimeout;
        private readonly TimeSpan downloadTimeout;

        public AutoUpdateClient() : this(new HttpClientHandler {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false, CheckCertificateRevocationList = true
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(3)) { }

        // Only test code in the same assembly can replace transport, key, or timeout policy.
        internal AutoUpdateClient(HttpMessageHandler handler, string publicKeyXml, TimeSpan checkTimeout, TimeSpan fileTimeout)
        {
            client = new HttpClient(handler, true);
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Nephylo-Updater/1.0");
            publicKeyForTests = publicKeyXml;
            manifestTimeout = checkTimeout;
            downloadTimeout = fileTimeout;
        }

        public async Task<AutoUpdateManifest> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
        {
            if (currentVersion == null) throw new ArgumentNullException("currentVersion");
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(manifestTimeout);
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, ManifestUri))
                {
                    request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
                    using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    using (deadline.Token.Register(response.Dispose))
                    {
                        CheckResponse(response, ManifestUri, AutoUpdateManifest.MaximumManifestBytes);
                        using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (MemoryStream bytes = new MemoryStream())
                        {
                            await CopyBoundedAsync(stream, bytes, AutoUpdateManifest.MaximumManifestBytes, deadline.Token).ConfigureAwait(false);
                            deadline.Token.ThrowIfCancellationRequested();
                            string text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
                            AutoUpdateManifest manifest = publicKeyForTests == null ? AutoUpdateManifest.ParseAndVerify(text) : AutoUpdateManifest.ParseAndVerify(text, publicKeyForTests);
                            return manifest.IsNewerThan(currentVersion) ? manifest : null;
                        }
                    }
                }
            }
        }

        public async Task<PreparedUpdate> DownloadAsync(AutoUpdateManifest manifest, CancellationToken cancellationToken)
        {
            if (manifest == null) throw new ArgumentNullException("manifest");
            cancellationToken.ThrowIfCancellationRequested();
            string directory = CreateStagingDirectory();
            bool completed = false;
            try
            {
                string installer = Path.Combine(directory, manifest.FileName);
                using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(downloadTimeout);
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, manifest.InstallerUri))
                    using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    using (deadline.Token.Register(response.Dispose))
                    {
                        CheckResponse(response, manifest.InstallerUri, manifest.Bytes);
                        if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != manifest.Bytes)
                            throw new InvalidDataException("The update download has an unexpected size.");
                        using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (FileStream output = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        {
                            long written = await CopyBoundedAsync(input, output, manifest.Bytes, deadline.Token).ConfigureAwait(false);
                            if (written != manifest.Bytes) throw new InvalidDataException("The update download is incomplete.");
                            await output.FlushAsync(deadline.Token).ConfigureAwait(false);
                        }
                    }
                    deadline.Token.ThrowIfCancellationRequested();
                    // Bound file size keeps this final disk hash small; never expose an unverified executable.
                    manifest.VerifyInstaller(installer);
                    deadline.Token.ThrowIfCancellationRequested();
                    string manifestFile = Path.Combine(directory, "update.json");
                    File.WriteAllText(manifestFile, manifest.SignedEnvelope, new UTF8Encoding(false));
                    PreparedUpdate result = new PreparedUpdate { Manifest = manifest, DirectoryPath = directory, InstallerPath = installer, ManifestJsonPath = manifestFile };
                    completed = true;
                    return result;
                }
            }
            finally { if (!completed) CleanupStagingDirectory(directory); }
        }

        private static void CheckResponse(HttpResponseMessage response, Uri expected, long limit)
        {
            if (response.StatusCode != HttpStatusCode.OK || response.Content == null
                || response.RequestMessage == null || response.RequestMessage.RequestUri != expected
                || response.Content.Headers.ContentEncoding.Count != 0
                || (response.Content.Headers.ContentLength.HasValue && (response.Content.Headers.ContentLength.Value < 0 || response.Content.Headers.ContentLength.Value > limit)))
                throw new InvalidDataException("The update server returned an invalid response.");
        }

        private static async Task<long> CopyBoundedAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[65536];
            long total = 0;
            for (;;)
            {
                int received = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (received == 0) return total;
                total += received;
                if (total > limit) throw new InvalidDataException("The update download exceeds its expected size.");
                await target.WriteAsync(buffer, 0, received, cancellationToken).ConfigureAwait(false);
            }
        }

        internal static string StagingRoot { get { return Path.Combine(SettingsStore.InstallDirectory, "Updates"); } }

        private static string CreateStagingDirectory()
        {
            Directory.CreateDirectory(SettingsStore.InstallDirectory);
            RejectReparsePoint(SettingsStore.InstallDirectory);
            Directory.CreateDirectory(StagingRoot);
            RejectReparsePoint(StagingRoot);
            string result = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(result);
            RejectReparsePoint(result);
            return result;
        }

        internal static void RejectReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The update staging location is invalid.");
        }

        public static void CleanupStagingDirectory(string directory)
        {
            // Never recursively remove a caller-provided path or follow a junction.
            try
            {
                string full = Path.GetFullPath(directory);
                if (!String.Equals(Path.GetDirectoryName(full), Path.GetFullPath(StagingRoot), StringComparison.OrdinalIgnoreCase)
                    || !Regex.IsMatch(Path.GetFileName(full), @"\A[a-f0-9]{32}\z", RegexOptions.CultureInvariant)
                    || !Directory.Exists(full)) return;
                RejectReparsePoint(SettingsStore.InstallDirectory);
                RejectReparsePoint(StagingRoot);
                RejectReparsePoint(full);
                if (Directory.GetDirectories(full).Length != 0) return;
                string[] files = Directory.GetFiles(full);
                foreach (string file in files) RejectReparsePoint(file);
                foreach (string file in files) File.Delete(file);
                Directory.Delete(full, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }

        public void Dispose() { client.Dispose(); }
    }
}
