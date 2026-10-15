using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

// Hidden UI fixtures only: no user configuration, CLI, account or network access.
internal static class ClaudeEnvironmentUiTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static int assertions;

    private static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new Exception(message);
    }

    private static T Field<T>(WidgetWindow window, string name)
    {
        return (T)typeof(WidgetWindow).GetField(name, Private).GetValue(window);
    }

    private static void SetField(WidgetWindow window, string name, object value)
    {
        typeof(WidgetWindow).GetField(name, Private).SetValue(window, value);
    }

    private static object Call(WidgetWindow window, string name, params object[] args)
    {
        try { return typeof(WidgetWindow).GetMethod(name, Private).Invoke(window, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        var match = parent as T;
        if (match != null) yield return match;
        foreach (object child in LogicalTreeHelper.GetChildren(parent)) {
            var node = child as DependencyObject;
            if (node == null) continue;
            foreach (var found in Descendants<T>(node)) yield return found;
        }
    }

    private static Button Button(StackPanel editor, string content)
    {
        return Descendants<Button>(editor).Single(item => Convert.ToString(item.Content) == content);
    }

    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); }

    private static void CheckInheritedDirectory(string isolated)
    {
        string custom = Path.Combine(isolated, "shell-claude-config");
        string source = null;
        WidgetWindow window = null;
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", custom);
        try {
            // Construct in preview mode so startup cannot load real providers. Only
            // after every timer is stopped do isolated handlers use normal behavior.
            window = new WidgetWindow(true);
            Field<DispatcherTimer>(window, "timer").Stop();
            Field<DispatcherTimer>(window, "noticeTimer").Stop();
            Field<Forms.NotifyIcon>(window, "tray").Visible = false;
            Check(!window.IsLoaded && !window.IsVisible, "Fixture never loads or displays a live widget");
            SetField(window, "demo", false);
            SetField(window, "servicePlan", new ServicePlan(isolated, false, null, null));
            var settings = Field<WidgetSettings>(window, "settings");
            settings.ClaudeWindowsConfigDirectory = "";
            settings.ClaudeWslDistribution = "";
            settings.ClaudeUseExistingStatusLine = false;
            settings.CodexPath = Path.Combine(isolated, "missing-codex.exe");
            var editor = new StackPanel();
            Call(window, "BuildClaudeSettings", editor);
            var directoryInput = Descendants<TextBox>(editor).Single();
            Check(directoryInput.Text == "", "User starts with an inherited directory rather than an explicit selection");
            source = ClaudeUsageService.GetWindowsSourceId("");
            Check(source.StartsWith("win:", StringComparison.Ordinal), "Inherited custom directory gets its own source identity");

            Click(Button(editor, "Claude Code 연결"));
            Check(!Field<bool>(window, "claudeConnectionBusy"), "Windows connection completes without a child process or dispatcher wait");
            Check(settings.ClaudeWindowsConfigDirectory == custom, "Connecting pins the inherited custom folder in memory");
            Check(SettingsStore.Load().ClaudeWindowsConfigDirectory == custom, "Connecting pins the inherited folder on disk");
            Check(directoryInput.Text == custom, "The editor shows the persisted absolute folder");
            Check(File.Exists(Path.Combine(custom, "settings.json")), "Hook is written only to the isolated inherited directory");
            string command = ClaudeUsageService.GetWindowsStatusLineCommand(typeof(ClaudeEnvironmentUiTests).Assembly.Location, custom);
            Check(command.Contains("--claude-source " + source), "Installed hook includes the source-specific receiver argument");
            Check(ClaudeUsageService.IsStatusLineInstalledWindows(typeof(ClaudeEnvironmentUiTests).Assembly.Location, custom), "Installed hook exactly matches the selected folder receiver");
            Check(Field<ServicePlan>(window, "servicePlan").IsActive("codex"), "Installing the hook does not replace the active service before receipt");

            string quota = "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":25,\"resets_at\":253402300000}}}";
            ClaudeUsageService.CaptureJson(quota, DateTimeOffset.UtcNow, source);
            Click(Button(editor, "수신 확인"));
            Check(Field<ServicePlan>(window, "servicePlan").IsActive("codex") && (bool)Call(window,"IsServiceActive","claude"), "Receipt verification makes Claude usable alongside Codex");
            Check(Field<ProviderView>(window, "claudeView").Snapshot.Rows[0].RemainingPercent == 75, "Verified UI shows the isolated source quota");

            // Simulate Windows startup where the launching shell's variable is gone.
            // The explicit persisted path must avoid touching the default .claude.
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            var restored = SettingsStore.Load();
            Check(restored.ClaudeWindowsConfigDirectory == custom, "Saved folder survives a restart without the shell environment");
            Check(ClaudeUsageService.GetWindowsSourceId(restored.ClaudeWindowsConfigDirectory) == source, "Restart retains the exact cache source identity");
            Check(ClaudeUsageService.IsStatusLineInstalledWindows(typeof(ClaudeEnvironmentUiTests).Assembly.Location, restored.ClaudeWindowsConfigDirectory), "Restart finds the same hook after the environment variable is gone");
            SetField(window, "settings", restored);
            Call(window, "RefreshLocalExtras");
            Check(Field<ProviderView>(window, "claudeView").Snapshot.Rows[0].RemainingPercent == 75, "Restarted settings read the same custom-folder cache");
            Check((string)Call(window, "ClaudeUsageSource") == "code:" + source, "History identity remains separated from default Windows Claude");
            Check(!window.IsLoaded && !window.IsVisible, "No browser, WSL discovery, or live polling was started");
        }
        finally {
            if (window != null) {
                Field<DispatcherTimer>(window, "timer").Stop();
                Field<DispatcherTimer>(window, "noticeTimer").Stop();
                SetField(window, "demo", true);
                window.Close();
                Field<CodexService>(window, "codex").Dispose();
                Field<Forms.NotifyIcon>(window, "tray").Dispose();
            }
        }
    }

    [STAThread]
    public static int Main()
    {
        ClaudeUsageService.BridgeProbeOverride = (info, token) => System.Threading.Tasks.Task.FromResult("ok");
        string parent = Path.GetFullPath(Path.GetDirectoryName(typeof(ClaudeEnvironmentUiTests).Assembly.Location));
        string isolated = Path.Combine(parent, "claude-environment-ui-test-data-" + Guid.NewGuid().ToString("N"));
        string priorClaude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string priorCodex = Environment.GetEnvironmentVariable("CODEX_HOME");
        try {
            SettingsStore.DataDirectoryOverride = isolated;
            SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\claude-environment-" + Guid.NewGuid().ToString("N");
            ClaudeUsageService.ConfigDirectoryOverride = null;
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(isolated, "empty-codex-home"));
            CheckInheritedDirectory(isolated);
            Console.WriteLine("Claude environment UI tests passed: " + assertions + " assertions. Isolated hidden fixtures only.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("Claude environment UI test failed: " + error); return 1; }
        finally {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", priorClaude);
            Environment.SetEnvironmentVariable("CODEX_HOME", priorCodex);
            SettingsStore.DataDirectoryOverride = null;
            SettingsStore.StartupRegistrySubKeyOverride = null;
            ClaudeUsageService.ConfigDirectoryOverride = null;
            string full = Path.GetFullPath(isolated);
            if (full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("claude-environment-ui-test-data-", StringComparison.Ordinal) && Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }
}
