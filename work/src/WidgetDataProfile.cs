using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace UsageWidget
{
    public sealed class ProfileInitializationException : InvalidOperationException
    {
        public string Reason { get; private set; }
        public string EnglishMessage { get; private set; }
        internal ProfileInitializationException(string reason, string message, string englishMessage) : base(message) { Reason = reason; EnglishMessage = englishMessage; }
    }

    // AppData can be virtualized by the parent MSIX application. Keep account data
    // outside AppData; install/update payloads deliberately retain their old path.
    public static class WidgetDataProfile
    {
        internal const string MarkerName = ".profile-version";
        internal const string MarkerContents = "Nephylo profile 1\n";
        internal const string RecoveryNoticeName = ".profile-recovery";
        private const int MaximumFiles = 256;
        private const long MaximumCohortBytes = 160L * 1024 * 1024;
        public static string DataDirectory
        {
            get
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (String.IsNullOrWhiteSpace(profile) || !Path.IsPathRooted(profile)) throw Failure("InvalidLocation");
                return Path.Combine(profile, ".nephylo");
            }
        }
        public static string LegacyInstallationDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageWidget"); } }

        public static void Initialize()
        {
            InitializeForTests(DataDirectory, LegacyInstallationDirectory, ResolvePhysicalPath, null);
        }

        // Only fixed categories and counts are persisted here, never source paths,
        // workspace identifiers, credential contents, or exception messages.
        public static string ReadRecoveryNotice(bool english = false)
        {
            return ReadRecoveryNoticeFrom(DataDirectory, english);
        }

        internal static string ReadRecoveryNoticeFrom(string directory, bool english = false)
        {
            try
            {
                string path = Path.Combine(directory, RecoveryNoticeName);
                ValidateNoReparse(path);
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length == 0 || input.Length > 128) return null;
                    string[] lines;
                    using (var reader = new StreamReader(input, new UTF8Encoding(false, true))) lines = reader.ReadToEnd().Split('\n');
                    int cacheCount, historyCount;
                    if (lines.Length != 4 || lines[0] != "Nephylo recovery 1" || lines[3] != "" ||
                        !Int32.TryParse(lines[1], out cacheCount) || !Int32.TryParse(lines[2], out historyCount) ||
                        cacheCount < 0 || cacheCount > MaximumFiles || historyCount < 0 || historyCount > 1 || cacheCount + historyCount == 0) return null;
                    if (historyCount != 0)
                        return english ? "Some saved history could not be read, so new history will start here. The original files are preserved in the previous settings folder. Service connections were migrated safely. Contact Nephylo support to recover the previous history." :
                            "일부 사용 이력을 읽을 수 없어 새 이력 기록을 시작합니다. 원본은 이전 설정 폴더에 보존되어 있으며, 서비스 연결 정보는 안전하게 이전했습니다. 이전 이력 복구가 필요하면 Nephylo 지원에 문의해 주세요.";
                    return english ? "Some saved usage or update cache could not be read. Usage will refresh automatically; if Claude Code needs reconnection, open service settings. Service connections were migrated safely, and the original files are preserved in the previous settings folder." :
                        "일부 사용량 또는 업데이트 캐시를 읽을 수 없어 자동 조회로 다시 가져옵니다. Claude Code 재연결이 필요하면 서비스 설정을 열어 주세요. 서비스 연결 정보는 안전하게 이전했으며 원본은 이전 설정 폴더에 보존했습니다.";
                }
            }
            catch { return null; }
        }

        public static void AcknowledgeRecoveryNotice()
        {
            AcknowledgeRecoveryNoticeAt(DataDirectory);
        }

        internal static void AcknowledgeRecoveryNoticeAt(string directory)
        {
            string path = Path.Combine(directory, RecoveryNoticeName);
            ValidateNoReparse(path);
            if (File.Exists(path)) File.Delete(path);
        }

        // Explicit fixture roots only; production does not use an environment or command-line override.
        internal static void InitializeForTests(string destination, string legacy, Func<string, string> resolvePhysicalPath, Action<string> beforeCopy)
        {
            try
            {
                destination = Normalize(destination); legacy = Normalize(legacy);
                if (String.Equals(destination, legacy, StringComparison.OrdinalIgnoreCase) || IsBelow(destination, legacy) || IsBelow(legacy, destination))
                    throw Failure("InvalidLocation");
                string gateName;
                using (var hash = SHA256.Create()) gateName = "Local\\Nephylo.Profile.v1." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(destination.ToUpperInvariant()))).Replace("-", "");
                using (var gate = new Mutex(false, gateName))
                {
                    bool held = false;
                    try
                    {
                        try { held = gate.WaitOne(TimeSpan.FromSeconds(20)); } catch (AbandonedMutexException) { held = true; }
                        if (!held) throw Failure("ProfileBusy");
                        InitializeLocked(destination, legacy, resolvePhysicalPath ?? ResolvePhysicalPath, beforeCopy);
                    }
                    finally { if (held) gate.ReleaseMutex(); }
                }
            }
            catch (ProfileInitializationException) { throw; }
            // Never put token content, account names, or filesystem exception paths in the UI.
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException) &&
                    !(error is ArgumentException) && !(error is NotSupportedException)) throw;
                throw Failure("MigrationFailed");
            }
        }

        private static void InitializeLocked(string destination, string legacy, Func<string,string> resolve, Action<string> beforeCopy)
        {
            ValidateUnredirected(destination, resolve, "UnsafeProfilePath");
            FileAttributes destinationAttributes;
            if (TryAttributes(destination, out destinationAttributes))
            {
                if ((destinationAttributes & FileAttributes.Directory) == 0) throw Failure("UninitializedProfile");
                ValidateMarker(destination);
                return; // This profile is authoritative, including intentionally deleted credentials after logout.
            }
            if (legacy.IndexOf(@"\AppData\Local\Packages\", StringComparison.OrdinalIgnoreCase) >= 0) throw Failure("RedirectedLegacyProfile");
            ValidateUnredirected(legacy, resolve, "RedirectedLegacyProfile");
            FileAttributes legacyAttributes;
            bool hasLegacy = TryAttributes(legacy, out legacyAttributes);
            if (hasLegacy && (legacyAttributes & FileAttributes.Directory) == 0) throw Failure("MigrationFailed");
            string parent = Path.GetDirectoryName(destination);
            if (!Directory.Exists(parent)) throw Failure("InvalidLocation");
            string stage = destination + ".migrating-" + Guid.NewGuid().ToString("N");
            var sources = new List<SourceFile>();
            var createdFiles = new List<string>();
            try
            {
                List<string> names = hasLegacy ? FindFiles(legacy) : new List<string>();
                long total = 0;
                int recoveredCaches = 0, recoveredHistory = 0;
                foreach (string relative in names)
                {
                    string sourcePath = Path.Combine(legacy, relative);
                    ValidateUnredirected(sourcePath, resolve, "UnsafeLegacyPath");
                    var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    try
                    {
                        // Validate the actual open handle, not just its earlier path observation.
                        if (!String.Equals(Normalize(ResolveHandle(source.SafeFileHandle)), Normalize(resolve(sourcePath)), StringComparison.OrdinalIgnoreCase)) throw Failure("UnsafeLegacyPath");
                        // Corrupt derived files must not prevent configured services from starting.
                        // Retain their locked original in the legacy folder; never copy unbounded
                        // content or let a new history writer replace the only surviving original.
                        bool recover = IsRecoverableFile(relative) && !IsValidDerivedObject(source, SizeLimit(relative));
                        if (recover)
                        {
                            if (String.Equals(relative, "usage-journal.json", StringComparison.OrdinalIgnoreCase)) recoveredHistory++;
                            else recoveredCaches++;
                        }
                        else
                        {
                            if (source.Length == 0 || source.Length > SizeLimit(relative)) throw Failure("InvalidLegacyFile");
                            total += source.Length;
                            if (total > MaximumCohortBytes) throw Failure("InvalidLegacyFile");
                        }
                        sources.Add(new SourceFile(relative, source, !recover)); source = null;
                    }
                    finally { if (source != null) source.Dispose(); }
                }
                Directory.CreateDirectory(stage);
                ValidateUnredirected(stage, resolve, "UnsafeProfilePath");
                foreach (SourceFile source in sources)
                {
                    if (!source.Copy) continue;
                    if (beforeCopy != null) beforeCopy(source.RelativeName);
                    string output = Path.Combine(stage, source.RelativeName);
                    if (source.RelativeName.IndexOf(Path.DirectorySeparatorChar) >= 0) Directory.CreateDirectory(Path.GetDirectoryName(output));
                    ValidateUnredirected(output, resolve, "UnsafeProfilePath");
                    createdFiles.Add(output);
                    using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                    {
                        source.Stream.CopyTo(target); target.Flush(true);
                        if (target.Length != source.Stream.Length) throw Failure("MigrationFailed");
                    }
                }
                // Fail the whole cohort if another old process added/removed profile files during migration.
                FileAttributes finalLegacyAttributes;
                if (!hasLegacy && TryAttributes(legacy, out finalLegacyAttributes)) throw Failure("ProfileBusy");
                List<string> currentNames = hasLegacy ? FindFiles(legacy) : new List<string>();
                if (!SameNames(names, currentNames)) throw Failure("ProfileBusy");
                ValidateUnredirected(legacy, resolve, "RedirectedLegacyProfile");
                ValidateUnredirected(destination, resolve, "UnsafeProfilePath");
                if (recoveredCaches + recoveredHistory != 0)
                {
                    string recovery = Path.Combine(stage, RecoveryNoticeName);
                    createdFiles.Add(recovery);
                    WriteDurable(recovery, "Nephylo recovery 1\n" + recoveredCaches.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + recoveredHistory.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                }
                string marker = Path.Combine(stage, MarkerName);
                createdFiles.Add(marker);
                WriteDurable(marker, MarkerContents);
                // No destination exists before the entire cohort and marker are durable.
                Directory.Move(stage, destination);
            }
            finally
            {
                foreach (SourceFile source in sources) source.Stream.Dispose();
                CleanupStage(stage, destination, createdFiles);
            }
        }

        private static void WriteDurable(string path, string contents)
        {
            using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(contents); target.Write(bytes, 0, bytes.Length); target.Flush(true);
            }
        }

        private static bool IsRecoverableFile(string name)
        {
            return String.Equals(name, "update-attempt.json", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "usage-journal.json", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "claude-usage.json", StringComparison.OrdinalIgnoreCase) ||
                (SizeLimit(name) > 0 && (name.StartsWith("claude-usage-", StringComparison.Ordinal) || name.StartsWith("claude-hook-", StringComparison.Ordinal)));
        }

        private static bool IsValidDerivedObject(FileStream source, long limit)
        {
            if (source.Length == 0 || source.Length > limit) return false;
            try
            {
                string json;
                using (var reader = new StreamReader(source, new UTF8Encoding(false, true), true, 4096, true)) json = reader.ReadToEnd();
                return new JavaScriptSerializer { MaxJsonLength = (int)limit, RecursionLimit = 32 }.DeserializeObject(json) is Dictionary<string, object>;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            finally { source.Position = 0; }
        }

        private static void ValidateMarker(string directory)
        {
            string marker = Path.Combine(directory, MarkerName);
            ValidateNoReparse(marker);
            FileAttributes attributes;
            if (!TryAttributes(marker, out attributes) || (attributes & FileAttributes.Directory) != 0) throw Failure("UninitializedProfile");
            using (var input = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != Encoding.UTF8.GetByteCount(MarkerContents)) throw Failure("UninitializedProfile");
                using (var reader = new StreamReader(input, new UTF8Encoding(false, true))) if (reader.ReadToEnd() != MarkerContents) throw Failure("UninitializedProfile");
            }
        }

        private static List<string> FindFiles(string directory)
        {
            var files = new List<string>();
            int scanned = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++scanned > 4096) throw Failure("InvalidLegacyFile");
                string name = Path.GetFileName(entry);
                if (SizeLimit(name) == 0) continue;
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) throw Failure("InvalidLegacyFile");
                files.Add(name);
                if (files.Count > MaximumFiles) throw Failure("InvalidLegacyFile");
            }
            string tool = Path.Combine("tools", "higgsfield.exe");
            FileAttributes attributes;
            if (TryAttributes(Path.Combine(directory, tool), out attributes))
            {
                if ((attributes & FileAttributes.Directory) != 0) throw Failure("InvalidLegacyFile");
                files.Add(tool);
                if (files.Count > MaximumFiles) throw Failure("InvalidLegacyFile");
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        private static long SizeLimit(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "settings.json": case "extra-settings.json": return 65536;
                case "higgsfield-settings.json": return 8192;
                case "plan-settings.json": case "update-attempt.json": return 4096;
                // Retired Nephylo authentication is not part of the active profile
                // cohort. Do not open or copy these files: locked or malformed old
                // sessions cannot block accountless startup. Originals stay intact.
                case "membership-session.dat": case "membership-reauth-required.json": return 0;
                case "elevenlabs-key.dat": case "openai-admin-key.dat": return 32768;
                case "usage-journal.json": return 16777216;
                case "claude-usage.json": return 16384;
            }
            if (String.Equals(name, Path.Combine("tools", "higgsfield.exe"), StringComparison.OrdinalIgnoreCase)) return 128L * 1024 * 1024;
            bool hook = name.StartsWith("claude-hook-", StringComparison.Ordinal);
            if ((hook || name.StartsWith("claude-usage-", StringComparison.Ordinal)) && name.EndsWith(".json", StringComparison.Ordinal))
            {
                int prefix = hook ? 12 : 13;
                string hash = name.Substring(prefix, name.Length - prefix - 5);
                if (!hook && hash.StartsWith("win-", StringComparison.Ordinal)) hash = hash.Substring(4);
                if (hash.Length != 64) return 0;
                for (int i = 0; i < hash.Length; i++) if (!((hash[i] >= 'a' && hash[i] <= 'f') || (hash[i] >= '0' && hash[i] <= '9'))) return 0;
                return 16384;
            }
            return 0;
        }

        private static bool SameNames(List<string> first, List<string> second)
        {
            if (first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++) if (!String.Equals(first[i], second[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static void ValidateUnredirected(string path, Func<string,string> resolve, string reason)
        {
            ValidateNoReparse(path);
            if (!String.Equals(Normalize(path), Normalize(resolve(path)), StringComparison.OrdinalIgnoreCase)) throw Failure(reason);
        }

        private static void ValidateNoReparse(string path)
        {
            string current = Normalize(path);
            while (!String.IsNullOrEmpty(current))
            {
                FileAttributes attributes;
                if (TryAttributes(current, out attributes) && (attributes & FileAttributes.ReparsePoint) != 0) throw Failure("UnsafeProfilePath");
                string parent = Path.GetDirectoryName(current);
                if (String.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }

        private static bool TryAttributes(string path, out FileAttributes attributes)
        {
            try { attributes = File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { attributes = 0; return false; }
            catch (DirectoryNotFoundException) { attributes = 0; return false; }
        }

        internal static string ResolvePhysicalPath(string path)
        {
            string ancestor = Normalize(path);
            var missing = new Stack<string>();
            FileAttributes attributes;
            while (!TryAttributes(ancestor, out attributes))
            {
                missing.Push(Path.GetFileName(ancestor));
                string parent = Path.GetDirectoryName(ancestor);
                if (String.IsNullOrEmpty(parent) || parent == ancestor) throw Failure("InvalidLocation");
                ancestor = parent;
            }
            string result;
            using (SafeFileHandle handle = CreateFile(ancestor, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new IOException("Cannot resolve profile location.");
                result = ResolveHandle(handle);
            }
            while (missing.Count > 0) result = Path.Combine(result, missing.Pop());
            return result;
        }

        private static string ResolveHandle(SafeFileHandle handle)
        {
            var result = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(handle, result, (uint)result.Capacity, 0);
            if (length == 0 || length >= result.Capacity) throw new IOException("Cannot resolve profile location.");
            string value = result.ToString();
            if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + value.Substring(8);
            return value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value.Substring(4) : value;
        }

        private static string Normalize(string path)
        {
            string full = Path.GetFullPath(path);
            return String.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        private static bool IsBelow(string path, string parent) { return path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }

        private static void CleanupStage(string stage, string destination, List<string> createdFiles)
        {
            // Delete only known files under this call's random staging directory. Never recursively delete a profile.
            try
            {
                if (!stage.StartsWith(destination + ".migrating-", StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(stage) != Path.GetDirectoryName(destination)) return;
                ValidateNoReparse(stage);
                if (!Directory.Exists(stage)) return;
                foreach (string file in createdFiles)
                {
                    if (!IsBelow(file, stage)) return;
                    ValidateNoReparse(file); if (File.Exists(file)) File.Delete(file);
                }
                string tools = Path.Combine(stage, "tools");
                ValidateNoReparse(tools); if (Directory.Exists(tools)) Directory.Delete(tools, false);
                Directory.Delete(stage, false);
            }
            catch { /* A failed cleanup never makes a partial profile authoritative. */ }
        }

        private static ProfileInitializationException Failure(string reason)
        {
            string message, english;
            if (reason == "RedirectedLegacyProfile")
            {
                message = "실행 환경의 설정 폴더가 분리되어 있습니다. Windows 시작 메뉴에서 설치된 Nephylo를 한 번 실행해 로그인 정보를 안전하게 이전해 주세요. 기존 정보는 변경하지 않았습니다.";
                english = "This launch environment uses a separate settings folder. Open the installed Nephylo from the Windows Start menu once to migrate your sign-in safely. Your existing data was not changed.";
            }
            else if (reason == "UninitializedProfile")
            {
                message = "저장된 프로필을 확인할 수 없어 시작을 중단했습니다. 기존 로그인과 연결 정보는 보존되어 있습니다. Nephylo 지원에 문의해 주세요.";
                english = "Nephylo stopped because the saved profile could not be verified. Your existing sign-in and connections have been preserved. Please contact Nephylo support.";
            }
            else if (reason == "ProfileBusy")
            {
                message = "다른 Nephylo 작업이 로그인 정보를 사용 중입니다. 잠시 후 다시 실행해 주세요. 기존 정보는 변경하지 않았습니다.";
                english = "Another Nephylo task is using your sign-in data. Please try again shortly. Your existing data was not changed.";
            }
            else
            {
                message = "로그인과 연결 정보를 안전하게 불러오지 못해 시작을 중단했습니다. Windows 시작 메뉴에서 다시 실행해 주세요. 문제가 계속되면 Nephylo 지원에 문의해 주세요. 기존 정보는 변경하지 않았습니다.";
                english = "Nephylo stopped because your sign-in and connections could not be loaded safely. Open it from the Windows Start menu and contact Nephylo support if the issue continues. Your existing data was not changed.";
            }
            return new ProfileInitializationException(reason, message, english);
        }

        private sealed class SourceFile
        {
            internal readonly string RelativeName;
            internal readonly FileStream Stream;
            internal readonly bool Copy;
            internal SourceFile(string name, FileStream stream, bool copy) { RelativeName = name; Stream = stream; Copy = copy; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    }
}
