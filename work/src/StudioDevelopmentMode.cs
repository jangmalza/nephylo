using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;

namespace UsageWidget
{
    internal sealed class StudioDevelopmentOptions
    {
        internal string Profile = "default";
        internal string Theme, Language;

        internal static StudioDevelopmentOptions Parse(string[] args)
        {
            if (args == null) throw new ArgumentException("Missing ADE development options.");
            var result = new StudioDevelopmentOptions();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool enabled = false;
            for (int i = 0; i < args.Length; i++) {
                string flag = args[i];
                if (flag == null || !seen.Add(flag)) throw new ArgumentException("Duplicate ADE development option.");
                if (flag == "--ade-dev") { enabled = true; continue; }
                if (flag != "--ade-dev-profile" && flag != "--ade-dev-theme" && flag != "--ade-dev-language")
                    throw new ArgumentException("Unsupported ADE development option.");
                if (++i >= args.Length || String.IsNullOrEmpty(args[i])) throw new ArgumentException("Missing ADE development option value.");
                string value = args[i];
                if (flag == "--ade-dev-profile") result.Profile = StudioDevelopmentProfile.ValidateName(value);
                else if (flag == "--ade-dev-theme") {
                    if (value != "dark" && value != "light") throw new ArgumentException("Invalid development theme.");
                    result.Theme = value;
                } else {
                    if (value != "ko" && value != "en") throw new ArgumentException("Invalid development language.");
                    result.Language = value;
                }
            }
            if (!enabled) throw new ArgumentException("--ade-dev is required.");
            return result;
        }
    }

    internal sealed class StudioDevelopmentSettings
    {
        public int SchemaVersion { get; set; }
        public string Theme { get; set; }
        public string Language { get; set; }
        public StudioDevelopmentSettings() { SchemaVersion = 1; Theme = "dark"; Language = "ko"; }
    }

    // A named child of a fixed development root, never a general data-directory override.
    // Production widget migration, settings, startup registration and updates stay untouched.
    internal sealed class StudioDevelopmentProfile
    {
        internal readonly string Name, DirectoryPath;
        internal string StudioDirectory { get { return Path.Combine(DirectoryPath,"studio"); } }
        internal string SettingsFile { get { return Path.Combine(DirectoryPath,"development.json"); } }
        internal string MutexName { get { return "Local\\Nephylo.ADE.Development." + Identity; } }
        internal string WakeName { get { return MutexName + ".Show"; } }
        string Identity { get {
            using (var sha = SHA256.Create()) return String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(DirectoryPath.ToUpperInvariant())).Select(b => b.ToString("x2",CultureInfo.InvariantCulture)));
        } }

        internal StudioDevelopmentProfile(string userProfile, string name)
        {
            Name = ValidateName(name);
            if (String.IsNullOrWhiteSpace(userProfile) || !Path.IsPathRooted(userProfile)) throw new ArgumentException("Invalid user profile.");
            DirectoryPath = Path.GetFullPath(Path.Combine(userProfile,".nephylo-ade-dev","profile-" + Name));
        }
        internal static string ValidateName(string name)
        {
            if (name == null || !Regex.IsMatch(name,"\\A[a-z0-9][a-z0-9_-]{0,39}\\z",RegexOptions.CultureInvariant)) throw new ArgumentException("Use 1-40 lowercase letters, digits, underscores or hyphens for the development profile.");
            return name;
        }
        internal void EnsureDirectory()
        {
            ValidatePath(DirectoryPath);
            Directory.CreateDirectory(DirectoryPath);
            ValidatePath(DirectoryPath);
        }
        internal static void ValidatePath(string value)
        {
            for (string current = Path.GetFullPath(value); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) {
                if (!File.Exists(current) && !Directory.Exists(current)) continue;
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked development storage is not supported.");
                if (current != value && (attributes & FileAttributes.Directory) == 0) throw new IOException("A development storage parent is not a directory.");
            }
        }
        internal StudioDevelopmentSettings LoadSettings()
        {
            EnsureDirectory();
            ValidatePath(SettingsFile);
            if (!File.Exists(SettingsFile)) return new StudioDevelopmentSettings();
            if (new FileInfo(SettingsFile).Length > 4096) throw new InvalidDataException("Development settings are too large; the original file is preserved.");
            var result = new JavaScriptSerializer { MaxJsonLength = 4096 }.Deserialize<StudioDevelopmentSettings>(File.ReadAllText(SettingsFile,Encoding.UTF8));
            ValidateSettings(result);
            return result;
        }
        static void ValidateSettings(StudioDevelopmentSettings settings)
        {
            if (settings == null || settings.SchemaVersion != 1 || settings.Theme != "dark" && settings.Theme != "light" || settings.Language != "ko" && settings.Language != "en")
                throw new InvalidDataException("Development settings could not be read; the original file is preserved.");
        }
        internal void SaveSettings(StudioDevelopmentSettings settings)
        {
            ValidateSettings(settings);EnsureDirectory();ValidatePath(SettingsFile);
            // Do not silently overwrite a corrupt or future-version settings file.
            if (File.Exists(SettingsFile)) LoadSettings();
            string temporary = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(settings));
                using (var output = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) output.Write(bytes,0,bytes.Length);
                if (File.Exists(SettingsFile)) File.Replace(temporary,SettingsFile,null); else File.Move(temporary,SettingsFile);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal static class StudioDevelopmentMode
    {
        internal static bool IsRequested(string[] args)
        {
            // A malformed development flag must fail here, never fall through to live widget startup.
            return args != null && args.Any(value => value != null && value.StartsWith("--ade-dev",StringComparison.OrdinalIgnoreCase));
        }
        internal static int Run(string[] args)
        {
            try {
                var options = StudioDevelopmentOptions.Parse(args);
                var profile = new StudioDevelopmentProfile(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),options.Profile);
                return Run(options,profile,null);
            } catch {
                MessageBox.Show("ADE 개발 모드를 시작하지 못했습니다. 실행 옵션과 개발 저장 폴더 권한을 확인하세요. 기존 대화·설정과 손상된 개발 파일은 보존됩니다.\n\nUse --ade-dev with an optional --ade-dev-profile name, --ade-dev-theme dark|light or --ade-dev-language ko|en. Check development storage permissions. Original files are preserved.","Nephylo ADE · Development",MessageBoxButton.OK,MessageBoxImage.Warning);
                return 3;
            }
        }
        internal static StudioWindow CreateWindow(StudioDevelopmentOptions options, StudioDevelopmentProfile profile)
        {
            if (options == null || profile == null || options.Profile != profile.Name) throw new ArgumentException("Mismatched development profile.");
            var settings = profile.LoadSettings();
            if (options.Theme != null) settings.Theme = options.Theme;
            if (options.Language != null) settings.Language = options.Language;
            if (!File.Exists(profile.SettingsFile) || options.Theme != null || options.Language != null) profile.SaveSettings(settings);
            L.Language = settings.Language;
            WidgetPalette.SetTheme(settings.Theme);
            var resources = WidgetWindow.MakeResources();
            WidgetWindow.ApplyManagementReadingStyles(resources);
            var window = new StudioWindow("",false,resources,WidgetWindow.Back,WidgetWindow.Fore,new FontFamily("Segoe UI, Malgun Gothic"),profile);
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WidgetWindow.ApplyDialogTheme(window);
            return window;
        }
        // Explicit fixture profiles are an in-assembly test seam, not a CLI/environment redirect.
        internal static int Run(StudioDevelopmentOptions options, StudioDevelopmentProfile profile, Action<StudioWindow> configure)
        {
            bool first;
            using (var mutex = new Mutex(true,profile.MutexName,out first))
            using (var wake = new EventWaitHandle(false,EventResetMode.AutoReset,profile.WakeName)) {
                if (!first) { wake.Set(); return 0; }
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (sender,e) => {
                    MessageBox.Show(L.English?"Could not complete this development action. Check the development storage and connection.":"개발 작업을 완료하지 못했습니다. 개발용 저장 폴더와 연결 상태를 확인해 주세요.","Nephylo ADE · Development",MessageBoxButton.OK,MessageBoxImage.Warning);
                    e.Handled = true;
                };
                var window = CreateWindow(options,profile);
                app.MainWindow = window;
                if (configure != null) configure(window);
                var thread = new Thread(() => {
                    while (true) {
                        wake.WaitOne();
                        if (app.Dispatcher.HasShutdownStarted) return;
                        try { app.Dispatcher.BeginInvoke(new Action(() => { if (!window.IsLoaded) return;window.Show();window.WindowState=WindowState.Normal;window.Activate(); })); }
                        catch (InvalidOperationException) { return; }
                    }
                }) { IsBackground = true };
                thread.Start();
                try { app.Run(window); }
                finally { wake.Set();thread.Join(1000); }
                return 0;
            }
        }
    }
}
