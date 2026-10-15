using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;

namespace UsageWidget
{
    internal sealed class WidgetInstallResult
    {
        internal string ExecutablePath;
        internal bool AlreadyInstalled, StartupUpdated;
    }

    public static class WidgetInstallation
    {
        private static readonly string[] Files = { "UsageWidget.exe", "UsageWidget.exe.config", "widget.ico", "widget.png", "README.txt", "THIRD-PARTY-NOTICES.txt" };
        public static string InstalledExecutable { get { return Path.Combine(SettingsStore.InstallDirectory,"App","UsageWidget.exe"); } }

        // Explicit user action only. Source is this running distribution, never a URL or
        // downloaded script. Accounts, keys, history and preferences live outside App.
        public static void InstallInteractive(Window owner, bool demo)
        {
            if (owner == null) L.Language = SettingsStore.Load().Language;
            if (demo) { Show(owner,"미리보기에서는 파일을 설치하거나 시작프로그램을 변경하지 않습니다.",MessageBoxImage.Information); return; }
            try {
                string source = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Nephylo.lnk");
                string legacyShortcut = Path.Combine(Path.GetDirectoryName(shortcut),"Milos.lnk");
                string olderShortcut = Path.Combine(Path.GetDirectoryName(shortcut),"Usage Widget.lnk");
                var result = Install(source,SettingsStore.InstallDirectory,shortcut,IsRunning,WriteShortcut,SettingsStore.ReadStartupCommand,SettingsStore.RestoreStartupCommand,legacyShortcut,null,new[] { olderShortcut });
                Show(owner,result.AlreadyInstalled ? "이미 이 PC의 설치 폴더에서 실행 중입니다. 업데이트하려면 최신 Nephylo-Setup.exe를 실행하세요. ZIP의 Install.cmd도 사용할 수 있습니다. 종료 안내가 나오면 위젯을 트레이에서 종료해 주세요."
                    : "설치가 완료되었습니다. 다음부터 시작 메뉴의 Nephylo를 실행하세요.\n\n" + result.ExecutablePath + (result.StartupUpdated ? "\n기존 Windows 자동 실행도 설치 경로로 변경했습니다." : "") + "\n기존 연결 정보·설정·사용 이력은 유지됩니다.\n다음 업데이트는 최신 Nephylo-Setup.exe를 실행하면 됩니다.",MessageBoxImage.Information);
            } catch (Exception ex) {
                string message = ex is InvalidOperationException ? ex.Message : "설치하지 못했습니다. 실행 중인 위젯을 트레이에서 종료한 뒤 다시 설치하세요. 계속 실패하면 설치 폴더의 쓰기 권한과 남은 저장 공간을 확인해 주세요.";
                Show(owner,message,MessageBoxImage.Warning);
            }
        }

        private static void Show(Window owner, string message, MessageBoxImage icon)
        {
            if (owner == null) MessageBox.Show(L.Text(message),L.Text("Nephylo · 설치"),MessageBoxButton.OK,icon);
            else MessageBox.Show(owner,L.Text(message),L.Text("Nephylo · 설치"),MessageBoxButton.OK,icon);
        }

        internal static WidgetInstallResult Install(string sourceDirectory, string dataDirectory, string shortcutPath,
            Func<string,bool> isRunning, Action<string,string> writeShortcut, Func<string> readStartup, Action<string> writeStartup,
            string legacyShortcutPath = null, Func<string,string,bool> isOwnedShortcut = null, string[] additionalLegacyShortcutPaths = null)
        {
            string source = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string data = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string destination = Path.Combine(data,"App");
            string executable = Path.Combine(destination,"UsageWidget.exe");
            if (String.Equals(source,destination,StringComparison.OrdinalIgnoreCase)) return new WidgetInstallResult { AlreadyInstalled = true, ExecutablePath = executable };
            RejectReparsePath(data); RejectReparsePath(source); RejectReparsePath(destination); RejectReparsePath(shortcutPath);
            var legacyShortcutPaths = new[] { legacyShortcutPath }.Concat(additionalLegacyShortcutPaths ?? new string[0])
                .Where(path => !String.IsNullOrEmpty(path)).Select(Path.GetFullPath)
                .Where(path => !String.Equals(path,Path.GetFullPath(shortcutPath),StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string legacy in legacyShortcutPaths) RejectReparsePath(legacy);
            if (isOwnedShortcut == null) isOwnedShortcut = IsOwnedShortcut;
            if (isRunning(executable)) throw RunningError();
            using (var mutex = new Mutex(false,@"Local\UsageWidget.Install.v1")) {
                bool held = false;
                try {
                    try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new InvalidOperationException("다른 설치 작업이 진행 중입니다. 설치가 끝난 뒤 다시 실행해 주세요.");
                    Directory.CreateDirectory(data);
                    string stage = Path.Combine(data,".install-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stage);
                    bool keepBackup = false;
                    var changed = new List<string>();
                    var existed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string priorStartup = null;
                    bool startupAttempted = false, shortcutAttempted = false, shortcutExisted = false;
                    var legacyBackups = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                    var removedLegacyShortcuts = new List<string>();
                    string backup = Path.Combine(stage,"previous"); Directory.CreateDirectory(backup);
                    try {
                        var files = new List<string>();
                        foreach (string name in Files) {
                            string original = Path.Combine(source,name), staged = Path.Combine(stage,name);
                            if (!File.Exists(original)) {
                                if (name == "UsageWidget.exe" || name == "UsageWidget.exe.config") throw new InvalidOperationException("실행 파일과 설정 파일이 모두 필요합니다. 배포 ZIP 전체를 압축 해제한 뒤 설치하세요.");
                                continue;
                            }
                            RejectReparsePath(original);
                            if (new FileInfo(original).Length > 67108864) throw new InvalidOperationException("배포 파일 크기를 확인하지 못했습니다. 공식 배포 ZIP을 다시 받아 주세요.");
                            File.Copy(original,staged,false);
                            if (Digest(original) != Digest(staged)) throw new IOException("Installation copy verification failed.");
                            files.Add(name);
                        }
                        Version incoming = ValidateExecutable(Path.Combine(stage,"UsageWidget.exe"));
                        if (File.Exists(executable)) {
                            Version installed = ValidateExecutable(executable);
                            if (installed > incoming) throw new InvalidOperationException("설치된 위젯이 더 최신 버전입니다. 최신 배포 ZIP을 사용해 주세요.");
                        }
                        // Back up only the known distribution files. Never enumerate or delete
                        // account/config files in the parent data directory or unknown App files.
                        foreach (string name in files) {
                            string target = Path.Combine(destination,name); RejectReparsePath(target);
                            if (Directory.Exists(target)) throw new IOException("Installation target is not a file.");
                            if (File.Exists(target)) { File.Copy(target,Path.Combine(backup,name),false); existed.Add(name); }
                        }
                        shortcutExisted = File.Exists(shortcutPath);
                        if (shortcutExisted && !isOwnedShortcut(shortcutPath,executable))
                            throw new InvalidOperationException("시작 메뉴에 다른 앱의 Nephylo 바로가기가 있습니다. 해당 바로가기의 이름을 바꾼 뒤 다시 설치해 주세요.");
                        if (shortcutExisted) File.Copy(shortcutPath,Path.Combine(backup,"start-menu.lnk"),false);
                        foreach (string legacy in legacyShortcutPaths) {
                            if (!File.Exists(legacy) || !isOwnedShortcut(legacy,executable)) continue;
                            string legacyBackup = Path.Combine(backup,"legacy-start-menu-" + legacyBackups.Count + ".lnk");
                            File.Copy(legacy,legacyBackup,false); legacyBackups.Add(legacy,legacyBackup);
                        }
                        priorStartup = readStartup();
                        if (isRunning(executable)) throw RunningError();
                        Directory.CreateDirectory(destination);
                        foreach (string name in files) {
                            Replace(Path.Combine(stage,name),Path.Combine(destination,name)); changed.Add(name);
                        }
                        shortcutAttempted = true;
                        writeShortcut(shortcutPath,executable);
                        bool startupUpdated = IsWidgetStartup(priorStartup);
                        if (startupUpdated) { startupAttempted = true; writeStartup("\"" + executable + "\""); }
                        foreach (string legacy in legacyShortcutPaths) {
                            if (!legacyBackups.ContainsKey(legacy)) continue;
                            // Only replace our exact installed target with no custom arguments.
                            // A same-name shortcut belonging to another app stays untouched.
                            File.Delete(legacy); removedLegacyShortcuts.Add(legacy);
                        }
                        return new WidgetInstallResult { ExecutablePath = executable, StartupUpdated = startupUpdated };
                    } catch {
                        Action<Action> undo = action => { try { action(); } catch { keepBackup = true; } };
                        foreach (string legacy in removedLegacyShortcuts.AsEnumerable().Reverse()) {
                            string savedLegacy = legacy;
                            undo(() => Restore(legacyBackups[savedLegacy],savedLegacy));
                        }
                        if (startupAttempted) undo(() => writeStartup(priorStartup));
                        if (shortcutAttempted) undo(() => {
                                if (shortcutExisted) Restore(Path.Combine(backup,"start-menu.lnk"),shortcutPath);
                                else if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
                            });
                        foreach (string name in changed.AsEnumerable().Reverse()) {
                            string savedName = name;
                            undo(() => {
                                string target = Path.Combine(destination,savedName);
                                if (existed.Contains(savedName)) Restore(Path.Combine(backup,savedName),target);
                                else if (File.Exists(target)) File.Delete(target);
                            });
                        }
                        if (keepBackup) throw new InvalidOperationException("설치를 마치지 못해 이전 파일 백업을 보관했습니다. 위젯을 종료한 뒤 다시 설치하세요. 백업 위치: " + backup);
                        throw;
                    } finally {
                        if (!keepBackup) DeleteStage(stage,data);
                    }
                } finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        private static InvalidOperationException RunningError()
        {
            return new InvalidOperationException("설치된 위젯이 실행 중입니다. 작업 표시줄 트레이의 Nephylo를 종료한 뒤 Install.cmd를 다시 실행해 주세요. 실행 중인 위젯은 강제로 종료하지 않았습니다.");
        }
        private static Version ValidateExecutable(string path)
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            AssemblyName identity;
            try { identity = AssemblyName.GetAssemblyName(path); }
            catch { throw new InvalidOperationException("Nephylo 배포 파일을 확인하지 못했습니다. 공식 배포 ZIP을 다시 받아 주세요."); }
            if (identity.Name != "UsageWidget" || (version.ProductName != "Nephylo" && version.ProductName != "Milos" && version.ProductName != "Usage Widget")) throw new InvalidOperationException("Nephylo 배포 파일이 아닙니다. 공식 배포 ZIP을 사용해 주세요.");
            return identity.Version;
        }
        private static bool IsWidgetStartup(string command)
        {
            if (String.IsNullOrEmpty(command) || command.Length < 3 || command[0] != '"' || command[command.Length-1] != '"') return false;
            string path = command.Substring(1,command.Length-2);
            try { return path.IndexOf('"') < 0 && Path.IsPathRooted(path) && String.Equals(Path.GetFileName(path),"UsageWidget.exe",StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        private static bool IsRunning(string target)
        {
            foreach (var process in Process.GetProcessesByName("UsageWidget")) using (process) {
                try { if (!process.HasExited && String.Equals(Path.GetFullPath(process.MainModule.FileName),target,StringComparison.OrdinalIgnoreCase)) return true; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { return true; }
            }
            return false;
        }
        private static string Digest(string path)
        {
            using (var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(file));
        }
        private static void Replace(string staged, string target)
        {
            for (int attempt = 0; ; attempt++) {
                try {
                    if (File.Exists(target)) File.Replace(staged,target,null);
                    else File.Move(staged,target);
                    return;
                } catch (IOException error) {
                    int code = error.HResult & 0xffff;
                    // A scanner may briefly deny delete sharing. Retain the atomic
                    // operation and its source; permanent failures still roll back.
                    if (attempt >= 4 || (code != 32 && code != 33 && code != 1175) || !File.Exists(staged)) throw;
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
        }
        private static void Restore(string previous, string target)
        {
            string temporary = target + ".restore-" + Guid.NewGuid().ToString("N");
            try { File.Copy(previous,temporary,false); Replace(temporary,target); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static void RejectReparsePath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current)) {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("설치 경로에 다른 폴더로 연결된 링크가 있습니다. 일반 폴더에 배포 ZIP을 압축 해제한 뒤 설치하세요.");
                current = Path.GetDirectoryName(current);
            }
        }
        private static void DeleteStage(string stage, string data)
        {
            try {
                string full = Path.GetFullPath(stage);
                if (Path.GetDirectoryName(full) == data && Path.GetFileName(full).StartsWith(".install-",StringComparison.Ordinal) && Directory.Exists(full)) {
                    RejectReparsePath(full); Directory.Delete(full,true);
                }
            } catch { /* Keep a recoverable staging folder if cleanup is unavailable. */ }
        }
        private static void WriteShortcut(string path, string executable)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".lnk";
            object shell = null, shortcut = null;
            try {
                Type type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) throw new InvalidOperationException("시작 메뉴 바로가기를 만들 수 없습니다. Windows Script Host 설정을 확인해 주세요.");
                shell = Activator.CreateInstance(type);
                shortcut = type.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[] { temporary });
                Type linkType = shortcut.GetType();
                linkType.InvokeMember("TargetPath",BindingFlags.SetProperty,null,shortcut,new object[] { executable });
                linkType.InvokeMember("WorkingDirectory",BindingFlags.SetProperty,null,shortcut,new object[] { Path.GetDirectoryName(executable) });
                linkType.InvokeMember("Description",BindingFlags.SetProperty,null,shortcut,new object[] { "Nephylo" });
                linkType.InvokeMember("Save",BindingFlags.InvokeMethod,null,shortcut,null);
                Replace(temporary,path);
            } finally {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static bool IsOwnedShortcut(string path, string executable)
        {
            object shell = null, shortcut = null;
            try {
                if (!File.Exists(path)) return false;
                Type type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return false;
                shell = Activator.CreateInstance(type);
                shortcut = type.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[] { path });
                Type linkType = shortcut.GetType();
                string target = Convert.ToString(linkType.InvokeMember("TargetPath",BindingFlags.GetProperty,null,shortcut,null));
                string arguments = Convert.ToString(linkType.InvokeMember("Arguments",BindingFlags.GetProperty,null,shortcut,null));
                return String.IsNullOrWhiteSpace(arguments) && Path.IsPathRooted(target)
                    && String.Equals(Path.GetFullPath(target),Path.GetFullPath(executable),StringComparison.OrdinalIgnoreCase);
            } catch { return false; }
            finally {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
