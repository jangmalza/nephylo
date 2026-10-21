using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static class WidgetIsolationTests
{
    static int count;
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
    static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Private).GetValue(target); }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--retired-entry") {
            // Demo remains a data-safe fallback even if this entry guard regresses.
            Program.Main(new[] { "--ade-dev", "--demo" }); return Environment.ExitCode;
        }
        string root = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "profile");
        WidgetWindow window = null;
        try {
            Check(args.Length == 1 && File.Exists(args[0]), "An independent built Widget executable is required");
            var artifact = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Check(!artifact.GetTypes().Any(type => type.FullName.StartsWith("UsageWidget.Studio", StringComparison.Ordinal) || type.Name == "IStudioAgentClient"), "The built Widget has no ADE runtime types");
            foreach (string name in new[] { "WidgetWindow", "SettingsStore", "WidgetDataProfile", "CodexService", "ClaudeUsageService", "ClaudeQuotaService", "UsageJournal", "WidgetInstallation", "AutoUpdateInstaller" })
                Check(artifact.GetType("UsageWidget." + name) != null, "Widget capability retained: " + name);
            Check(typeof(WidgetWindow).GetMethod("OpenStudio", Private) == null && typeof(WidgetWindow).GetField("studioWindow", Private) == null, "Widget lifetime no longer owns an ADE window");
            Check(!Program.IsWidgetInvocation(new[] { "--ade-dev" }) && !Program.IsWidgetInvocation(new[] { "--ade-dev-profile", "old" }), "Retired ADE launch flags are rejected");
            Check(!Program.IsWidgetInvocation(new[] { "--ade-dev-invalid" }) && !Program.IsWidgetInvocation(new[] { "--ade-devmalformed" }), "Malformed retired flags cannot fall through into the Widget profile");
            foreach (var flags in new[] { new string[0], new[] { "--demo" }, new[] { "--install" }, new[] { "--claude-statusline" }, new[] { "--claude-bridge-check" } })
                Check(Program.IsWidgetInvocation(flags), "Existing Widget launch modes remain valid");
            using (var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--retired-entry") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })) {
                bool exited = child.WaitForExit(8000);
                if (!exited) { child.Kill(); child.WaitForExit(1500); }
                Check(exited && child.ExitCode == 2, "A retired launcher exits before creating any Widget window or profile");
            }

            Directory.CreateDirectory(root);
            SettingsStore.DataDirectoryOverride = root;
            SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\product-" + Guid.NewGuid().ToString("N");
            ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(root, "claude-config");
            string opaqueData = Path.Combine(root, "studio-preserved-data.json");
            File.WriteAllText(opaqueData, "{\"unrelated\":\"preserve verbatim\"}");
            var settings = new WidgetSettings { RefreshSeconds = 60, CompactAutoWidth = true, CompactWidth = 520, ServiceOrder = new[] { "claude", "codex", "eleven" }, ClaudeWindowsConfigDirectory = Path.Combine(root, "claude-config"), Language = "ko", Theme = "dark" };
            SettingsStore.Save(settings);
            SettingsStore.SaveApiKey("isolated-placeholder-key");
            var restored = SettingsStore.Load();
            Check(restored.RefreshSeconds == 60 && restored.CompactAutoWidth && restored.CompactWidth == 520, "Refresh and minibar settings retain their existing format");
            Check(restored.ServiceOrder[0] == "claude" && restored.ClaudeWindowsConfigDirectory == settings.ClaudeWindowsConfigDirectory, "Service order and Claude connection are retained");
            Check(SettingsStore.LoadApiKey() == "isolated-placeholder-key", "Existing CurrentUser DPAPI key storage remains usable");
            Check(File.ReadAllText(opaqueData) == "{\"unrelated\":\"preserve verbatim\"}", "Widget writes never clean up data owned by the other product");
            Check(WidgetDataProfile.DataDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nephylo"), "The legacy Widget data directory remains stable");

            window = new WidgetWindow(true);
            Field<DispatcherTimer>(window, "timer").Stop(); Field<DispatcherTimer>(window, "noticeTimer").Stop();
            var tray = Field<Forms.NotifyIcon>(window, "tray"); tray.Visible = false;
            Check(!window.IsVisible && !window.IsLoaded, "The fixture never shows or starts a live Widget");
            var menu = (ContextMenu)typeof(WidgetWindow).GetMethod("CreateMoreMenu", Private).Invoke(window, new object[] { new Button() });
            Check(!menu.IsOpen, "Inspecting the menu does not open a popup");
            var labels = menu.Items.OfType<MenuItem>().Select(item => Convert.ToString(item.Header)).ToArray();
            Check(!labels.Any(text => text.Contains("ADE")), "The Widget menu has no ADE action");
            foreach (string label in new[] { "대시보드 열기", "사용 이력", "주간 요약", "알림 · 이력 설정", "후원하기 ↗", "버전 · 설치 · 업데이트", "간략 보기", "종료" })
                Check(labels.Contains(label), "Widget menu action retained: " + label);
            var trayLabels = tray.ContextMenuStrip.Items.Cast<Forms.ToolStripItem>().Select(item => Convert.ToString(item.Tag)).ToArray();
            Check(!trayLabels.Any(text => text.Contains("ADE")), "The tray menu has no ADE action");
            foreach (string label in new[] { "위젯 열기", "대시보드 열기", "새로고침", "서비스 연결 · 관리", "위젯 설정", "종료" })
                Check(trayLabels.Contains(label), "Tray action retained: " + label);
            Console.WriteLine("Widget product isolation tests passed: " + count + ". Independent assembly, menus, settings, DPAPI and retired launch modes; no account or network.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if (window != null) {
                Field<DispatcherTimer>(window, "timer").Stop(); Field<DispatcherTimer>(window, "noticeTimer").Stop();
                window.Close(); Field<CodexService>(window, "codex").Dispose(); Field<Forms.NotifyIcon>(window, "tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null; SettingsStore.StartupRegistrySubKeyOverride = null; ClaudeUsageService.ConfigDirectoryOverride = null;
        }
    }
}
