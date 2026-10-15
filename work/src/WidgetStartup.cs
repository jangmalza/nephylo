using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // Startup state is deliberately outside AppData: an MSIX parent may redirect
    // AppData before the first profile migration has taken place.
    public static class WidgetStartup
    {
        private const string HandoffFlag = "--normal-profile-start";
        private const string ReadyFlag = "--update-ready";
        private static string CurrentVersion { get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); } }
        private static string StatusRoot { get {
#if NEPHYLO_UPDATE_INSTALLER_TESTS
            return AutoUpdateInstallerTestPaths.DataDirectory;
#else
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
#endif
        } }
        private static string FixedExecutable { get {
#if NEPHYLO_UPDATE_INSTALLER_TESTS
            return Path.Combine(AutoUpdateInstallerTestPaths.DataDirectory, "App", "UsageWidget.exe");
#else
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageWidget", "App", "UsageWidget.exe");
#endif
        } }

        public static bool ShouldHandoff(string[] args, ProfileInitializationException error)
        {
            if (error == null || args == null || Array.IndexOf(args, HandoffFlag) >= 0 || Array.IndexOf(args, "--demo") >= 0) return false;
            return error.Reason == "RedirectedLegacyProfile" || error.Reason == "UnsafeLegacyPath";
        }

        // The caller must release the single-instance mutex before handing off.
        // Only the current installed binary can be launched, never a CLI path.
        public static bool TryHandoff(string[] args)
        {
            try
            {
                using (var ready = CreateReadyRequest(CurrentVersion))
                    return TryHandoffForTests(args, Assembly.GetExecutingAssembly().Location, FixedExecutable, StatusRoot, CurrentVersion, LaunchNormalContext, ready.Nonce, ready.Wait);
            }
            catch { return false; }
        }

        internal static bool TryHandoffForTests(string[] args, string executable, string expectedExecutable, string root, string version, Func<string, string, bool> launch, string handoffNonce = null, Func<TimeSpan, bool> waitReady = null)
        {
            if (args == null || Array.IndexOf(args, HandoffFlag) >= 0 || Array.IndexOf(args, "--demo") >= 0) return false;
            string nonce = handoffNonce == null ? Guid.NewGuid().ToString("N") : ReadNonce(new[] { HandoffFlag, handoffNonce }, HandoffFlag), ready = ReadNonce(args, ReadyFlag);
            if (nonce == null) return false;
            try
            {
                ValidateExecutable(executable, expectedExecutable);
                string arguments = HandoffFlag + " " + nonce + (ready == null ? "" : " " + ReadyFlag + " " + ready);
                WriteStatus(root, version, "handoff", "profile", ready ?? nonce, DateTime.UtcNow);
                if (launch(executable, arguments) && (waitReady == null || waitReady(TimeSpan.FromSeconds(45)))) return true;
            }
            catch { /* Keep the original, localized profile error as the visible fallback. */ }
            WriteStatus(root, version, "failed", "handoff", ready ?? nonce, DateTime.UtcNow);
            return false;
        }

        public static void MarkReady(string[] args)
        {
            try { MarkReadyForTests(args, StatusRoot, CurrentVersion); } catch { }
        }

        internal static void MarkReadyForTests(string[] args, string root, string version)
        {
            string nonce = ReadNonce(args, ReadyFlag), handoff = ReadNonce(args, HandoffFlag);
            WriteStatus(root, version, "ready", "window", nonce ?? handoff, DateTime.UtcNow);
            SignalReady(version, nonce);
            if (handoff != nonce) SignalReady(version, handoff);
        }

        private static void SignalReady(string version, string nonce)
        {
            if (nonce == null) return;
            try { using (var ready = EventWaitHandle.OpenExisting(EventName(version, nonce))) ready.Set(); }
            catch { /* A stale or inaccessible waiter must not break the window. */ }
        }

        public static void MarkFailed(string[] args, string stage)
        {
            try { WriteStatus(StatusRoot, CurrentVersion, "failed", SafeStage(stage), ReadNonce(args, ReadyFlag) ?? ReadNonce(args, HandoffFlag), DateTime.UtcNow); } catch { }
        }

        // Used by the new updater after it has verified and installed its fixed
        // payload. Older updaters still benefit from the app's self-handoff.
        internal static bool RestartInstalledAndWait(string executable, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(3)) throw new ArgumentOutOfRangeException("timeout");
            ValidateExecutable(executable, FixedExecutable);
            string version = FileVersionInfo.GetVersionInfo(executable).FileVersion;
            Version parsed;
            if (!Version.TryParse(version, out parsed) || parsed < new Version(1, 25, 8, 0)) throw new InvalidDataException("Unexpected restart version.");
            using (var request = CreateReadyRequest(parsed.ToString()))
            {
                var start = new ProcessStartInfo(executable, request.Arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable) };
                using (var process = Process.Start(start))
                {
                    if (process == null) return false;
                    // The first child can exit after WMI handoff. Readiness comes
                    // from the final window, so child exit alone is not success.
                    return request.Wait(timeout);
                }
            }
        }

        internal static StartupReadyRequest CreateReadyRequest(string version)
        {
            return new StartupReadyRequest(version, Guid.NewGuid().ToString("N"));
        }

        internal sealed class StartupReadyRequest : IDisposable
        {
            private readonly EventWaitHandle ready;
            internal string Arguments { get; private set; }
            internal string Nonce { get; private set; }
            internal StartupReadyRequest(string version, string nonce)
            {
                Nonce = nonce;
                bool created;
                ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(version, nonce), out created);
                if (!created) { ready.Dispose(); throw new IOException("Startup request already exists."); }
                Arguments = ReadyFlag + " " + nonce;
            }
            internal bool Wait(TimeSpan timeout) { return ready.WaitOne(timeout); }
            public void Dispose() { ready.Dispose(); }
        }

        internal static string ReadNonce(string[] args, string flag)
        {
            if (args == null) return null;
            int index = Array.IndexOf(args, flag);
            if (index < 0 || index + 1 >= args.Length || Array.IndexOf(args, flag, index + 1) >= 0) return null;
            Guid value;
            string raw = args[index + 1];
            return raw != null && raw.Length == 32 && Guid.TryParseExact(raw, "N", out value) && value != Guid.Empty ? value.ToString("N") : null;
        }

        private static string EventName(string version, string nonce)
        {
            Version parsed;
            if (!Version.TryParse(version, out parsed) || ReadNonce(new[] { ReadyFlag, nonce }, ReadyFlag) == null) throw new ArgumentException("Invalid startup request.");
            return @"Local\Nephylo.StartupReady.v1." + parsed.ToString() + "." + nonce;
        }

        internal static void ValidateExecutable(string executable, string expected)
        {
            if (String.IsNullOrWhiteSpace(executable) || String.IsNullOrWhiteSpace(expected) || !Path.IsPathRooted(executable) || !Path.IsPathRooted(expected)) throw new InvalidDataException("Invalid installed executable.");
            string full = Path.GetFullPath(executable), fixedPath = Path.GetFullPath(expected);
            if (!String.Equals(full, fixedPath, StringComparison.OrdinalIgnoreCase) || !String.Equals(Path.GetFileName(full), "UsageWidget.exe", StringComparison.OrdinalIgnoreCase) || full.IndexOf('"') >= 0 || full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(@"\AppData\Local\Packages\", StringComparison.OrdinalIgnoreCase) >= 0) throw new InvalidDataException("Only the installed application can be restarted.");
            ValidatePath(full);
            if (!File.Exists(full)) throw new FileNotFoundException("Installed application not found.");
        }

        private static void ValidatePath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe startup path.");
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }

        private static string SafeStage(string stage)
        {
            return stage == "profile" || stage == "window" || stage == "handoff" || stage == "restart" || stage == "initialization" ? stage : "startup";
        }

        internal static void WriteStatus(string root, string version, string status, string stage, string nonce, DateTime now)
        {
            string temporary = null;
            try
            {
                Version parsed;
                if (!Version.TryParse(version, out parsed) || (status != "ready" && status != "failed" && status != "handoff") || String.IsNullOrEmpty(root) || !Path.IsPathRooted(root) || !Directory.Exists(root)) return;
                root = Path.GetFullPath(root);
                if (root.StartsWith(@"\\", StringComparison.Ordinal)) return;
                string path = Path.Combine(root, ".nephylo-startup.json");
                ValidatePath(path);
                var receipt = new Dictionary<string, object> {
                    { "version", parsed.ToString() }, { "status", status }, { "stage", SafeStage(stage) },
                    { "nonce", ReadNonce(new[] { ReadyFlag, nonce }, ReadyFlag) ?? "" }, { "timestampUtc", now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) }
                };
                string json = new JavaScriptSerializer().Serialize(receipt);
                if (json.Length > 1024) return;
                temporary = Path.Combine(root, ".nephylo-startup-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                ValidatePath(path);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                temporary = null;
            }
            catch { /* Diagnostics must never prevent startup or disclose account data. */ }
            finally { if (temporary != null) { try { File.Delete(temporary); } catch { } } }
        }

        // Local WMI starts outside the MSIX parent's virtualized filesystem.
        // COM late binding keeps the framework-only build free of new references.
        private static bool LaunchNormalContext(string executable, string arguments)
        {
            var objects = new List<object>();
            try
            {
                object locator = Track(objects, Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)));
                object service = Track(objects, Call(locator, "ConnectServer", ".", @"root\cimv2"));
                object process = Track(objects, Call(service, "Get", "Win32_Process"));
                object methods = Track(objects, Get(process, "Methods_"));
                object create = Track(objects, Call(methods, "Item", "Create"));
                object parameters = Track(objects, Get(create, "InParameters"));
                object input = Track(objects, Call(parameters, "SpawnInstance_"));
                SetWmi(objects, input, "CommandLine", "\"" + executable + "\" " + arguments);
                SetWmi(objects, input, "CurrentDirectory", Path.GetDirectoryName(executable));
                object startupClass = Track(objects, Call(service, "Get", "Win32_ProcessStartup"));
                object startup = Track(objects, Call(startupClass, "SpawnInstance_"));
                SetWmi(objects, startup, "ShowWindow", (ushort)0);
                SetWmi(objects, input, "ProcessStartupInformation", startup);
                object output = Track(objects, Call(process, "ExecMethod_", "Create", input));
                object properties = Track(objects, Get(output, "Properties_"));
                object result = Track(objects, Call(properties, "Item", "ReturnValue"));
                return Convert.ToUInt32(Get(result, "Value"), CultureInfo.InvariantCulture) == 0;
            }
            finally { for (int i = objects.Count - 1; i >= 0; i--) { try { if (Marshal.IsComObject(objects[i])) Marshal.FinalReleaseComObject(objects[i]); } catch { } } }
        }
        private static object Track(List<object> list, object item) { if (item != null) list.Add(item); return item; }
        private static object Call(object target, string member, params object[] args) { return target.GetType().InvokeMember(member, BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance | BindingFlags.OptionalParamBinding, null, target, args, CultureInfo.InvariantCulture); }
        private static object Get(object target, string member) { return target.GetType().InvokeMember(member, BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, target, null, CultureInfo.InvariantCulture); }
        private static void SetWmi(List<object> objects, object target, string name, object value)
        {
            object properties = Track(objects, Get(target, "Properties_"));
            object property = Track(objects, Call(properties, "Item", name));
            property.GetType().InvokeMember("Value", BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance, null, property, new[] { value }, CultureInfo.InvariantCulture);
        }
    }
}
