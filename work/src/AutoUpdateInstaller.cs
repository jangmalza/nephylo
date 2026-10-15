using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    internal sealed class AutoUpdateInstallResult
    {
        internal string Version, Code;
        internal bool Success;
        internal DateTime TimestampUtc;
    }

    // This helper runs as the same Windows user, from a copy outside App. It never
    // accepts an installation path, download URL or command supplied on its command line.
    internal static class AutoUpdateInstaller
    {
        private const string WorkerName = "NephyloUpdater.exe";
        private static readonly string[] PayloadFiles = { "UsageWidget.exe", "UsageWidget.exe.config", "widget.ico", "widget.png", "README.txt", "THIRD-PARTY-NOTICES.txt" };
        private static string FixedDataDirectory { get {
#if NEPHYLO_UPDATE_INSTALLER_TESTS
            // Compile-time isolated fixture only. Release builds contain no override.
            return AutoUpdateInstallerTestPaths.DataDirectory;
#else
            return SettingsStore.InstallDirectory;
#endif
        } }
        private static string FixedExecutable { get { return Path.Combine(FixedDataDirectory, "App", "UsageWidget.exe"); } }
        private static string UpdateRoot { get { return Path.Combine(FixedDataDirectory, "Updates"); } }
        private static string InstallerMutexName { get {
#if NEPHYLO_UPDATE_INSTALLER_TESTS
            return AutoUpdateInstallerTestPaths.InstallMutex;
#else
            return @"Local\UsageWidget.Install.v1";
#endif
        } }

        internal static bool CanAutoInstall()
        {
            try {
                string running = Assembly.GetExecutingAssembly().Location;
                RejectReparsePath(running);
                return SamePath(running, FixedExecutable) && ValidateExecutable(running) != null;
            } catch { return false; }
        }

        // Invoke on a background thread after settings/history have been flushed.
        // Return only after the child has independently verified itself and is ready.
        internal static void StartPreparedUpdate(PreparedUpdate update)
        {
            if (!CanAutoInstall()) throw new InvalidOperationException("Automatic installation requires the installed Nephylo application.");
            if (update == null) throw new ArgumentNullException("update");
            string stage = ValidateStage(update.DirectoryPath, UpdateRoot);
            if (!SamePath(update.ManifestJsonPath, Path.Combine(stage, "update.json"))) throw new InvalidDataException("Invalid update manifest path.");
            AutoUpdateManifest manifest = ReadManifest(stage);
            if (!SamePath(update.InstallerPath, Path.Combine(stage, manifest.FileName))) throw new InvalidDataException("Invalid installer path.");
            if (!manifest.IsNewerThan(ValidateExecutable(FixedExecutable))) throw new InvalidOperationException("The update is not newer than the installed version.");
            manifest.VerifyInstaller(update.InstallerPath);
            string helper = Path.Combine(stage, WorkerName);
            RejectReparsePath(helper);
            RejectRunningHelper(helper);
            CopyHelperFile(FixedExecutable, helper);
            string config = FixedExecutable + ".config";
            RejectReparsePath(config);
            CopyHelperFile(config, helper + ".config");
            if (!String.Equals(Digest(FixedExecutable), Digest(helper), StringComparison.Ordinal)) throw new IOException("Updater copy verification failed.");
            using (var parent = Process.GetCurrentProcess())
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEvent(stage))) {
                ready.Reset();
                var start = new ProcessStartInfo(helper, "--apply-update " + parent.Id.ToString(CultureInfo.InvariantCulture) + " " + parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)) {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = stage
                };
                using (var worker = Process.Start(start)) {
                    if (worker == null) throw new IOException("Updater could not be started.");
                    if (!ready.WaitOne(TimeSpan.FromSeconds(8)) || worker.HasExited) throw new IOException("Updater did not become ready. Nephylo will remain open.");
                }
            }
        }

        internal static bool TryRunWorker(string[] args)
        {
            if (args == null || Array.IndexOf(args, "--apply-update") < 0) return false;
            int parentId; long parentTicks;
            if (args.Length != 3 || args[0] != "--apply-update" || !Int32.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out parentId)
                || !Int64.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out parentTicks) || parentId <= 0 || parentTicks <= 0) return true;
            string stage = null;
            AutoUpdateManifest manifest = null;
            bool parentExited = false, installerStillRunning = false, backupReady = false, rollbackFailed = false, newerInstallationPreserved = false, resultWritten = false;
            Version backupVersion = null;
            bool installerStarted = false;
            int installerExitCode = -1;
            Dictionary<string, string> backup = null;
            try {
                string self = Assembly.GetExecutingAssembly().Location;
                if (Path.GetFileName(self) != WorkerName) throw new InvalidDataException("Invalid updater executable.");
                stage = ValidateStage(Path.GetDirectoryName(self), UpdateRoot);
                RejectReparsePath(self); RejectReparsePath(FixedExecutable);
                manifest = ReadManifest(stage);
                string installer = Path.Combine(stage, manifest.FileName);
                manifest.VerifyInstaller(installer);
                using (var gate = new Mutex(false, @"Local\UsageWidget.AutoUpdate.v1")) {
                    bool held = false;
                    try {
                        try { held = gate.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                        if (!held) throw new IOException("An update is already running.");
                        using (var parent = Process.GetProcessById(parentId)) {
                            ValidateParent(parent, parentTicks, FixedExecutable);
                            if (!String.Equals(Digest(self), Digest(FixedExecutable), StringComparison.Ordinal)) throw new InvalidDataException("Updater identity did not match its parent.");
                            if (!manifest.IsNewerThan(ValidateExecutable(FixedExecutable))) throw new InvalidDataException("Update would not advance the installed version.");
                            using (var ready = EventWaitHandle.OpenExisting(ReadyEvent(stage))) ready.Set();
                            if (!parent.WaitForExit(120000)) { WriteResult(stage, manifest.VersionText, false, "parent-still-running"); return true; }
                            parentExited = true;
                        }
                        // Keep the verified installer open without write/delete sharing for
                        // the entire launch, so its bytes cannot be replaced after hashing.
                        RejectReparsePath(installer);
                        using (var lockedInstaller = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                            manifest.VerifyInstaller(installer);
                            // Inno's SetupMutex is an existence check. Creating this same
                            // object excludes manual Setup while taking the snapshot; release
                            // it before launching our own Setup, which performs the same check.
                            using (var reservation = TryReserveInstaller()) {
                                if (reservation == null) { installerStillRunning = true; throw new IOException("Another installer is active."); }
                                backupVersion = ValidateExecutable(FixedExecutable);
                                if (backupVersion >= manifest.Version) {
                                    RecordInstalledAndRestart(stage, manifest.VersionText, "already-installed");
                                    return true;
                                }
                                backup = BackupPayload(stage, Path.GetDirectoryName(FixedExecutable));
                                backupReady = true;
                            }
                            int exitCode;
                            using (var setup = Process.Start(CreateInstallerStartInfo(installer, Path.Combine(stage, "setup.log")))) {
                                if (setup == null) throw new IOException("Installer could not be started.");
                                installerStarted = true;
                                if (!setup.WaitForExit(600000)) {
                                    installerStillRunning = true;
                                    WriteResult(stage, manifest.VersionText, false, "installer-timeout");
                                    return true; // Never overwrite files while Setup may still use them.
                                }
                                exitCode = setup.ExitCode;
                                installerExitCode = exitCode;
                            }
                            if (exitCode != 0) throw new IOException("Installer returned an unsuccessful exit code.");
                            using (var reservation = TryReserveInstaller()) {
                                if (reservation == null) { installerStillRunning = true; throw new IOException("Another installer is active."); }
                                if (ValidateExecutable(FixedExecutable) != manifest.Version) throw new InvalidDataException("Installed version did not match the signed release.");
                                RecordInstalledAndRestart(stage, manifest.VersionText, "installed");
                            }
                        }
                    } finally { if (held) gate.ReleaseMutex(); }
                }
            } catch {
                if (backupReady && !installerStillRunning) {
                    try {
                        using (var reservation = TryReserveInstaller()) {
                            if (reservation == null) installerStillRunning = true;
                            else {
                                Version current = null;
                                try { current = ValidateExecutable(FixedExecutable); } catch { }
                                // A pre-install abort cannot have changed payload files. A
                                // different manual Setup may have completed during handoff.
                                newerInstallationPreserved = current != null && backupVersion != null && current > backupVersion
                                    && (!ShouldRollbackPayload(installerStarted, installerExitCode) || current > manifest.Version);
                                if (!newerInstallationPreserved && ShouldRollbackPayload(installerStarted, installerExitCode))
                                    RestorePayload(stage, Path.GetDirectoryName(FixedExecutable), backup);
                                WriteResult(stage, manifest == null ? "" : manifest.VersionText, false, newerInstallationPreserved ? "newer-installation-preserved" : "install-failed");
                                resultWritten = true;
                                try { RestartInstalled(); } catch { }
                            }
                        }
                    }
                    catch { rollbackFailed = true; }
                }
                if (stage != null && !resultWritten) WriteResult(stage, manifest == null ? "" : manifest.VersionText, false, installerStillRunning ? "another-installer-running" : rollbackFailed ? "rollback-failed" : newerInstallationPreserved ? "newer-installation-preserved" : "install-failed");
                if (parentExited && !backupReady && !installerStillRunning && !rollbackFailed) {
                    try { using (var reservation = TryReserveInstaller()) { if (reservation != null) RestartInstalled(); } } catch { }
                }
            }
            return true;
        }

        internal static ProcessStartInfo CreateInstallerStartInfo(string installer, string log)
        {
            if (installer.IndexOf('"') >= 0 || log.IndexOf('"') >= 0) throw new InvalidDataException("Invalid installer path.");
            return new ProcessStartInfo(installer, "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS /NOFORCECLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /LOG=\"" + log + "\"") {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(installer)
            };
        }

        internal sealed class InstallerReservation : IDisposable
        {
            private Mutex mutex;
            internal InstallerReservation(Mutex value) { mutex = value; }
            public void Dispose() {
                if (mutex == null) return;
                try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); mutex = null; }
            }
        }

        internal static InstallerReservation TryReserveInstaller()
        {
            bool created;
            // Ownership also excludes the legacy ZIP installer, which uses WaitOne.
            var reservation = new Mutex(true, InstallerMutexName, out created);
            if (created) return new InstallerReservation(reservation);
            reservation.Dispose();
            return null;
        }

        internal static bool ShouldRollbackPayload(bool installerStarted, int exitCode)
        {
            // Inno: 1 initialization, 2 cancel before install, 3 pre-install fatal,
            // 7/8 preparing-to-install refusal. 4/5/6 or unknown may have changed files.
            // Exit 0 can still require rollback if our post-install validation failed.
            return installerStarted && exitCode != 1 && exitCode != 2 && exitCode != 3 && exitCode != 7 && exitCode != 8;
        }

        internal static void ValidateParent(Process parent, long startTicks, string installedExecutable)
        {
            if (parent.HasExited || parent.Id == Process.GetCurrentProcess().Id || parent.StartTime.ToUniversalTime().Ticks != startTicks
                || !SamePath(parent.MainModule.FileName, installedExecutable)) throw new InvalidDataException("Updater parent identity was invalid.");
        }

        private static void RecordInstalledAndRestart(string stage, string version, string readyCode)
        {
            // Installing bytes is not proof that profile migration and the first
            // window succeeded. Retain recovery evidence when readiness fails.
            WriteResult(stage, version, false, "installation-complete");
            try {
                RestartInstalled();
                WriteResult(stage, version, true, readyCode);
            }
            catch { WriteResult(stage, version, false, "startup-failed"); }
        }

        internal static void RestartInstalled()
        {
            RejectReparsePath(FixedExecutable);
            Version version = ValidateExecutable(FixedExecutable);
            if (version >= new Version(1, 25, 8, 0)) {
#if NEPHYLO_UPDATE_INSTALLER_TESTS
                TimeSpan timeout = AutoUpdateInstallerTestPaths.StartupTimeout;
#else
                // A redirected first launch may wait for profile migration and
                // a normal-context handoff before its final window is ready.
                TimeSpan timeout = TimeSpan.FromSeconds(75);
#endif
                if (!WidgetStartup.RestartInstalledAndWait(FixedExecutable, timeout)) throw new IOException("Nephylo did not confirm startup readiness.");
                return;
            }
            // Rollback versions predate the readiness protocol. Starting those
            // cannot produce the new ready event and must retain legacy behavior.
            using (var restarted = Process.Start(new ProcessStartInfo(FixedExecutable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(FixedExecutable) })) {
                if (restarted == null) throw new IOException("Nephylo could not be restarted.");
            }
        }

        private static AutoUpdateManifest ReadManifest(string stage)
        {
            string file = Path.Combine(stage, "update.json");
            RejectReparsePath(file);
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (stream.Length < 1 || stream.Length > 16384) throw new InvalidDataException("Invalid update manifest size.");
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true))) return AutoUpdateManifest.ParseAndVerify(reader.ReadToEnd());
            }
        }

        internal static string ValidateStage(string stage, string root)
        {
            if (String.IsNullOrEmpty(stage) || String.IsNullOrEmpty(root)) throw new InvalidDataException("Missing update staging path.");
            string full = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar);
            string expectedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            Guid id;
            if (!SamePath(Path.GetDirectoryName(full), expectedRoot) || !Guid.TryParseExact(Path.GetFileName(full), "N", out id)) throw new InvalidDataException("Update stage is not an owned directory.");
            RejectReparsePath(full);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Update stage is missing.");
            return full;
        }

        internal static Dictionary<string, string> BackupPayload(string stage, string app)
        {
            RejectReparsePath(stage); RejectReparsePath(app);
            string backup = Path.Combine(stage, "previous");
            RejectReparsePath(backup);
            if (Directory.Exists(backup)) throw new IOException("Update backup already exists.");
            Directory.CreateDirectory(backup);
            var originals = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in PayloadFiles) {
                string source = Path.Combine(app, name), saved = Path.Combine(backup, name);
                RejectReparsePath(source);
                if (Directory.Exists(source)) throw new InvalidDataException("A payload path is not a file.");
                if (!File.Exists(source)) {
                    if (name == "UsageWidget.exe" || name == "UsageWidget.exe.config") throw new InvalidDataException("Required installed file is missing.");
                    originals.Add(name, null); continue;
                }
                if (new FileInfo(source).Length > 67108864) throw new InvalidDataException("Installed payload file is too large.");
                File.Copy(source, saved, false);
                string digest = Digest(source);
                if (digest != Digest(saved)) throw new IOException("Backup verification failed.");
                originals.Add(name, digest);
            }
            return originals;
        }

        internal static void RestorePayload(string stage, string app, Dictionary<string, string> originals)
        {
            RejectReparsePath(stage); RejectReparsePath(app);
            foreach (string name in PayloadFiles) {
                string digest;
                if (originals == null || !originals.TryGetValue(name, out digest)) throw new InvalidDataException("Incomplete update backup.");
                string target = Path.Combine(app, name), saved = Path.Combine(stage, "previous", name);
                RejectReparsePath(target); RejectReparsePath(saved);
                if (digest == null) { if (File.Exists(target)) File.Delete(target); continue; }
                if (Digest(saved) != digest) throw new InvalidDataException("Update backup was changed.");
                if (File.Exists(target) && Digest(target) == digest) continue;
                string temporary = Path.Combine(app, ".update-restore-" + Guid.NewGuid().ToString("N"));
                try {
                    File.Copy(saved, temporary, false);
                    if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
                    if (Digest(target) != digest) throw new IOException("Restored update file failed verification.");
                } finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        internal static Version ValidateExecutable(string path)
        {
            RejectReparsePath(path);
            var identity = AssemblyName.GetAssemblyName(path);
            var info = FileVersionInfo.GetVersionInfo(path);
            if (identity.Name != "UsageWidget" || info.ProductName != "Nephylo" || identity.Version == null) throw new InvalidDataException("Installed Nephylo identity is invalid.");
            return identity.Version;
        }

        internal static AutoUpdateInstallResult ReadLastResult()
        {
            return ReadResult(Path.Combine(UpdateRoot, "last-result.json"));
        }

        internal static void CleanupOldStages()
        {
            try {
                bool created;
                using (var gate = new Mutex(true, @"Local\UsageWidget.AutoUpdate.v1", out created)) {
                    if (!created) return;
                    try { CleanupOldStages(UpdateRoot, DateTime.UtcNow); }
                    finally { gate.ReleaseMutex(); }
                }
            } catch { /* Best-effort housekeeping never blocks updates or app startup. */ }
        }

        internal static void CleanupOldStages(string root, DateTime utcNow)
        {
            if (!Directory.Exists(root)) return;
            RejectReparsePath(root);
            int inspected = 0;
            foreach (string candidate in Directory.EnumerateDirectories(root)) {
                if (++inspected > 64) break;
                try {
                    string stage = ValidateStage(candidate, root);
                    if (Directory.GetLastWriteTimeUtc(stage) > utcNow.AddDays(-7)) continue;
                    RejectRunningHelper(Path.Combine(stage, WorkerName));
                    var result = ReadResult(Path.Combine(stage, "result.json"));
                    if (result != null && result.Code != "installed" && result.Code != "already-installed" && result.Code != "install-failed" && result.Code != "parent-still-running" && result.Code != "restart-failed") continue;
                    string[] directories = Directory.GetDirectories(stage);
                    if (directories.Length > 1 || (directories.Length == 1 && Path.GetFileName(directories[0]) != "previous")) continue;
                    if (result == null && (directories.Length != 0 || File.Exists(Path.Combine(stage, WorkerName)))) continue;
                    var files = new List<string>(Directory.GetFiles(stage));
                    bool owned = files.Count <= 12;
                    foreach (string file in files) {
                        RejectReparsePath(file);
                        string name = Path.GetFileName(file);
                        if (name != WorkerName && name != WorkerName + ".config" && name != "update.json" && name != "result.json" && name != "setup.log"
                            && !Regex.IsMatch(name, @"\ANephylo-Setup-[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}\.exe\z", RegexOptions.CultureInvariant)) owned = false;
                    }
                    if (directories.Length == 1) {
                        string backup = directories[0];
                        RejectReparsePath(backup);
                        if (Directory.GetDirectories(backup).Length != 0) owned = false;
                        foreach (string file in Directory.GetFiles(backup)) {
                            RejectReparsePath(file);
                            if (Array.IndexOf(PayloadFiles, Path.GetFileName(file)) < 0) owned = false;
                            files.Add(file);
                        }
                    }
                    if (!owned) continue;
                    // No recursive delete: only the prevalidated, fixed-name files and
                    // then empty directories are removed. Recovery-needed stages remain.
                    foreach (string file in files) File.Delete(file);
                    if (directories.Length == 1) Directory.Delete(directories[0], false);
                    Directory.Delete(stage, false);
                } catch { /* In-use, linked or unfamiliar stages are retained. */ }
            }
        }

        internal static AutoUpdateInstallResult ReadResult(string path)
        {
            try {
                RejectReparsePath(path);
                if (!File.Exists(path) || new FileInfo(path).Length > 2048) return null;
                var serializer = new JavaScriptSerializer { MaxJsonLength = 2048, RecursionLimit = 3 };
                var value = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                object version, code, success, timestamp;
                DateTime utc;
                if (!value.TryGetValue("version", out version) || !value.TryGetValue("code", out code) || !value.TryGetValue("success", out success) || !value.TryGetValue("timestampUtc", out timestamp)
                    || !(version is string) || !(code is string) || !(success is bool) || !(timestamp is string)
                    || ((string)version).Length > 40 || ((string)code).Length > 60
                    || !DateTime.TryParseExact((string)timestamp, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out utc) || utc.Kind != DateTimeKind.Utc) return null;
                return new AutoUpdateInstallResult { Version = (string)version, Code = (string)code, Success = (bool)success, TimestampUtc = utc };
            } catch { return null; }
        }

        private static void WriteResult(string stage, string version, bool success, string code)
        {
            try {
                ValidateStage(stage, UpdateRoot);
                string json = new JavaScriptSerializer().Serialize(new { version = version, code = code, success = success, timestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
                WriteResultFile(Path.Combine(stage, "result.json"), json);
                WriteResultFile(Path.Combine(UpdateRoot, "last-result.json"), json);
            } catch { /* An update failure must never create an unhandled helper dialog. */ }
        }

        private static void WriteResultFile(string target, string json)
        {
            RejectReparsePath(target);
            string temporary = target + "." + Guid.NewGuid().ToString("N");
            try {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string ReadyEvent(string stage) { return @"Local\UsageWidget.Update.Ready." + Path.GetFileName(stage); }
        private static void CopyHelperFile(string source, string destination)
        {
            RejectReparsePath(source); RejectReparsePath(destination);
            if (File.Exists(destination)) {
                if (Digest(source) != Digest(destination)) throw new InvalidDataException("Existing updater helper did not match this installation.");
            } else File.Copy(source, destination, false);
        }
        private static void RejectRunningHelper(string helper)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WorkerName))) using (process) {
                if (process.HasExited) continue;
                try { if (SamePath(process.MainModule.FileName, helper)) throw new IOException("This update helper is already running."); }
                catch (System.ComponentModel.Win32Exception) { throw new IOException("An update helper could not be checked."); }
            }
        }
        private static bool SamePath(string left, string right) { return !String.IsNullOrEmpty(left) && !String.IsNullOrEmpty(right) && String.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        internal static void RejectReparsePath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current)) {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Update path contains a symbolic link or junction.");
                string parent = Path.GetDirectoryName(current);
                if (String.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }
        private static string Digest(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(stream));
        }
    }
}
