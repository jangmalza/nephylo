using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static class AutoUpdateUiTests
{
    private static int checks;
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static T Field<T>(WidgetWindow window, string name) { return (T)typeof(WidgetWindow).GetField(name, Private).GetValue(window); }
    private static void Set(WidgetWindow window, string name, object value) { typeof(WidgetWindow).GetField(name, Private).SetValue(window, value); }
    private static object Call(WidgetWindow window, string name, params object[] values)
    {
        try { return typeof(WidgetWindow).GetMethod(name, Private).Invoke(window, values); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root)) {
            var next = child as DependencyObject;
            if (next != null) foreach (var descendant in Elements(next)) yield return descendant;
        }
    }
    private static void StopBackground(WidgetWindow window)
    {
        Field<DispatcherTimer>(window,"timer").Stop();
        Field<Forms.NotifyIcon>(window,"tray").Visible = false;
        window.ShowActivated = false;
    }
    private static void CloseWindow(WidgetWindow window)
    {
        if (window == null) return;
        Set(window,"shutdownFlushed",true);
        Field<DispatcherTimer>(window,"timer").Stop();
        window.Close();
    }
    [STAThread]
    public static int Main()
    {
        string parent = Path.GetFullPath(Path.GetDirectoryName(typeof(AutoUpdateUiTests).Assembly.Location));
        string directory = Path.Combine(parent,"auto-update-ui-isolated-" + Guid.NewGuid().ToString("N"));
        string registry = @"Software\UsageWidget\Tests\auto-update-" + Guid.NewGuid().ToString("N");
        string previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        WidgetWindow demo = null, window = null;
        SettingsStore.DataDirectoryOverride = directory;
        SettingsStore.StartupRegistrySubKeyOverride = registry;
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(directory,"claude");
        Environment.SetEnvironmentVariable("CODEX_HOME",Path.Combine(directory,"codex"));
        try
        {
            Check(new WidgetSettings().AutoUpdateEnabled,"new installations enable automatic updates by default");
            Check(SettingsStore.Load().AutoUpdateEnabled,"missing settings enable automatic updates");
            demo = new WidgetWindow(true); StopBackground(demo);
            Call(demo,"InitializeAutoUpdates");
            Check(!Field<bool>(demo,"updateInitialized") && Field<DateTime>(demo,"nextUpdateCheckUtc") == DateTime.MaxValue,"preview never schedules a network update");
            Task demoCheck = (Task)Call(demo,"CheckForUpdateAsync",true);
            Check(demoCheck.IsCompleted && !Field<bool>(demo,"updateBusy"),"manual preview check returns synchronously without opening transport");
            Check(((Task)Call(demo,"PollAutoUpdateAsync")).IsCompleted,"preview polling is a no-op");
            Check(!Directory.Exists(directory),"preview update checks persist nothing");
            CloseWindow(demo); demo = null;

            Directory.CreateDirectory(directory);
            string settingsPath = Path.Combine(directory,"settings.json");
            File.WriteAllText(settingsPath,"{\"Theme\":\"dark\",\"RefreshSeconds\":300}");
            Check(SettingsStore.Load().AutoUpdateEnabled,"existing pre-updater settings migrate to enabled");
            File.WriteAllText(settingsPath,"{\"AutoUpdateEnabled\":\"false\"}");
            Check(SettingsStore.Load().AutoUpdateEnabled,"invalid string cannot silently alter boolean policy");
            var options = new WidgetSettings { AutoUpdateEnabled = false, RefreshSeconds = 300, Language = "ko", CodexWslDistribution = "Ubuntu", ClaudeWslDistribution = "kali-linux", CompactView = true };
            SettingsStore.Save(options);
            options = SettingsStore.Load();
            Check(!options.AutoUpdateEnabled && options.RefreshSeconds == 300 && options.CompactView,"disabled update preference round-trips with other settings");
            Check(options.CodexWslDistribution == "Ubuntu" && options.ClaudeWslDistribution == "kali-linux","update preference preserves environment selections");
            options.AutoUpdateEnabled = true; SettingsStore.Save(options);
            Check(SettingsStore.Load().AutoUpdateEnabled,"enabled preference round-trips");
            options.AutoUpdateEnabled = false; options.CodexWslDistribution = ""; options.ClaudeWslDistribution = ""; SettingsStore.Save(options);

            DateTime now = DateTime.UtcNow;
            var attempt = new AutoUpdateAttempt { Version = "1.22.0", AttemptUtc = now.AddHours(-1) };
            attempt.Save();
            var loaded = AutoUpdateAttempt.Load();
            Check(loaded.Version == attempt.Version && loaded.IsCoolingDown("1.22.0",now),"failed install cooldown survives restart");
            Check(!loaded.IsCoolingDown("1.23.0",now),"different release is not suppressed by an older failure");
            Check(!loaded.IsCoolingDown("1.22.0",now.AddHours(24)),"same release becomes retryable after a day");
            loaded.AttemptUtc = now.AddYears(1);
            Check(!loaded.IsCoolingDown("1.22.0",now),"clock rollback or invalid future state cannot suppress updates indefinitely");
            File.WriteAllText(Path.Combine(directory,"update-attempt.json"),"invalid");
            Check(!AutoUpdateAttempt.Load().IsCoolingDown("1.22.0",now),"corrupt attempt state safely recovers");
            File.WriteAllText(Path.Combine(directory,"update-attempt.json"),new string('x',4097));
            Check(!AutoUpdateAttempt.Load().IsCoolingDown("1.22.0",now),"attempt state read is bounded");

            window = new WidgetWindow(false); StopBackground(window);
            Check((bool)typeof(WidgetWindow).GetProperty("ProFeaturesAvailable",Private).GetValue(window,null),"all features available without membership");
            Check((bool)Call(window,"UpdateCanRestart",false),"Free member can satisfy update restart policy");
            Set(window,"nextUpdateCheckUtc",DateTime.MinValue);
            var beforeStatus = Field<string>(window,"updateStatus");
            Check(((Task)Call(window,"PollAutoUpdateAsync")).IsCompleted && !Field<bool>(window,"updateBusy") && Field<string>(window,"updateStatus") == beforeStatus,"off preference blocks due automatic checks before network work");
            var prepared = new PreparedUpdate(); Set(window,"preparedUpdate",prepared);
            Check(((Task)Call(window,"PollAutoUpdateAsync")).IsCompleted && Object.ReferenceEquals(Field<PreparedUpdate>(window,"preparedUpdate"),prepared),"disabled automatic updates retain prepared package without installing");
            Set(window,"preparedUpdate",null);
            foreach (string busy in new[] { "exiting", "refreshing", "claudeConnectionBusy", "loginPending" })
            {
                Set(window,busy,true);
                Check(!(bool)Call(window,"UpdateCanRestart",false) && !(bool)Call(window,"UpdateCanRestart",true),busy + " defers automatic and manual restart");
                Set(window,busy,false);
            }
            using (var operation = new CancellationTokenSource())
            {
                foreach (string field in new[] { "codexLoginOperation", "higgsfieldOperation" }) {
                    Set(window,field,operation);
                    Check(!(bool)Call(window,"UpdateCanRestart",true),field + " cannot be interrupted by update");
                    Set(window,field,null);
                }
            }
            Set(window,"updateBusy",true);
            Check(((Task)Call(window,"CheckForUpdateAsync",true)).IsCompleted && Field<string>(window,"updateStatus") == beforeStatus,"duplicate manual check returns before transport");
            Set(window,"updateBusy",false);

            new WindowInteropHelper(window).EnsureHandle();
            var release = new Window { Owner = window }; Set(window,"releaseWindow",release);
            Check(!(bool)Call(window,"UpdateCanRestart",false),"open update page defers unattended restart");
            Check((bool)Call(window,"UpdateCanRestart",true),"explicit install may close only its update page");
            var editor = new Window { Owner = window };
            Check(!(bool)Call(window,"UpdateCanRestart",true),"a second settings/login editor defers manual restart");
            editor.Close(); release.Close(); Set(window,"releaseWindow",null);
            Check((bool)Call(window,"UpdateCanRestart",false),"closing all management windows restores eligibility");

            Call(window,"OpenSettings");
            var settingsWindow = Field<Window>(window,"settingsWindow"); settingsWindow.Hide();
            Elements(settingsWindow).OfType<Button>().Single(button => Object.Equals(button.Content,"저장")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<Window>(window,"settingsWindow") == null,"real settings save handler completed");
            Check(!SettingsStore.Load().AutoUpdateEnabled && !Field<WidgetSettings>(window,"settings").AutoUpdateEnabled,"saving appearance cannot accidentally re-enable updates");

            Call(window,"OpenReleaseInfo");
            var releaseWindow = Field<Window>(window,"releaseWindow"); releaseWindow.Hide();
            var toggle = Elements(releaseWindow).OfType<CheckBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "자동으로 최신 버전 유지");
            Check(toggle.IsEnabled && (bool)typeof(WidgetWindow).GetProperty("ProFeaturesAvailable",Private).GetValue(window,null),"automatic update controls are available to Free users");
            toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(SettingsStore.Load().AutoUpdateEnabled && Field<DateTime>(window,"nextUpdateCheckUtc") <= DateTime.UtcNow,"enable toggle persists and schedules a check");
            toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!SettingsStore.Load().AutoUpdateEnabled,"disable toggle persists immediately");
            Set(window,"preparedUpdate",prepared); Call(window,"SetUpdateStatus","test-ready");
            Check(Field<Button>(window,"updateInstallButton").Visibility == Visibility.Visible && Field<Button>(window,"updateInstallButton").IsEnabled,"prepared package offers explicit install even with automatic mode off");
            Set(window,"updateBusy",true); Call(window,"SetUpdateStatus","test-busy");
            Check(!Field<Button>(window,"updateInstallButton").IsEnabled && !Field<Button>(window,"updateCheckButton").IsEnabled,"busy state disables duplicate install/check controls");
            Set(window,"updateBusy",false); Set(window,"preparedUpdate",null);
            releaseWindow.Close();
            Check(Field<Button>(window,"updateInstallButton") == null && Field<TextBlock>(window,"updateStatusText") == null,"closing update page releases stale UI references");
            Check(!AutoUpdateInstaller.CanAutoInstall(),"isolated test executable cannot install into the real application");
            Call(window,"StopAutoUpdates");
            Check(Field<CancellationTokenSource>(window,"updateLifetime").IsCancellationRequested,"shutdown cancels updater lifetime");
            Console.WriteLine("Auto-update UI tests passed: " + checks + ". Isolated settings, no network or installation.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            CloseWindow(demo); CloseWindow(window);
            Environment.SetEnvironmentVariable("CODEX_HOME",previousCodexHome);
            SettingsStore.DataDirectoryOverride = null;
            SettingsStore.StartupRegistrySubKeyOverride = null;
            ClaudeUsageService.ConfigDirectoryOverride = null;
            Registry.CurrentUser.DeleteSubKeyTree(registry,false);
            string full = Path.GetFullPath(directory);
            if (full.StartsWith(parent + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("auto-update-ui-isolated-",StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
}
