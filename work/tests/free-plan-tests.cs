using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

// Accountless capabilities, with isolated local storage and no live providers.
// The former single-service restrictions intentionally no longer apply.
internal static class FreePlanTests
{
    private static int checks;
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] Services = { "codex", "eleven", "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" };
    private static void Check(bool value,string message) { checks++; if (!value) throw new Exception(message); }
    private static T Field<T>(WidgetWindow window,string name) { return (T)typeof(WidgetWindow).GetField(name,Hidden).GetValue(window); }
    private static void Set(WidgetWindow window,string name,object value) { typeof(WidgetWindow).GetField(name,Hidden).SetValue(window,value); }
    private static object Call(WidgetWindow window,string name,params object[] args)
    { try { return typeof(WidgetWindow).GetMethod(name,Hidden).Invoke(window,args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
    private static void Reject(Action action,string message)
    { bool failed=false; try { action(); } catch (ArgumentException) { failed=true; } catch (InvalidOperationException) { failed=true; } Check(failed,message); }
    private static IEnumerable<DependencyObject> Elements(DependencyObject node)
    {
        yield return node;
        foreach (object child in LogicalTreeHelper.GetChildren(node)) {
            var dependency = child as DependencyObject;
            if (dependency != null) foreach (var descendant in Elements(dependency)) yield return descendant;
        }
    }
    private static string Text(DependencyObject node)
    { return String.Join("\n",Elements(node).OfType<TextBlock>().Select(item=>item.Text).Concat(Elements(node).OfType<Button>().Select(item=>item.Content as string))); }
    private static void Unlocked(DependencyObject page,string name)
    {
        string text=Text(page);
        Check(!text.Contains("Pro 전용") && !text.Contains("Pro 이용 권한") && !text.Contains("Free / Pro") && !text.Contains("로그인 후"),name+" has no Nephylo account or paid gate");
    }
    [STAThread]
    public static int Main()
    {
        string parent=Path.GetFullPath(Path.GetDirectoryName(typeof(FreePlanTests).Assembly.Location));
        string root=Path.Combine(parent,"free-plan-data-"+Guid.NewGuid().ToString("N"));
        string oldCodexHome=Environment.GetEnvironmentVariable("CODEX_HOME");
        WidgetWindow window=null;
        try {
            string data=Path.Combine(root,"profile");
            SettingsStore.DataDirectoryOverride=data;
            SettingsStore.StartupRegistrySubKeyOverride=@"Software\UsageWidget\Tests\free-accountless-"+Guid.NewGuid().ToString("N");
            ClaudeUsageService.ConfigDirectoryOverride=Path.Combine(root,"claude");
            Environment.SetEnvironmentVariable("CODEX_HOME",Path.Combine(root,"codex"));
            SettingsStore.Save(new WidgetSettings { Theme="light", Language="ko" });
            SettingsStore.SaveApiKey("isolated-eleven-key"); ExtraSettingsStore.SaveOpenAiKey("isolated-openai-key");
            ExtraSettingsStore.Save(new ExtraSettings { ShowGemini=true, GeminiManual=new ManualUsageRecord { Label="daily", RemainingPercent=55, ObservedAt=DateTimeOffset.Now } });
            new ServicePlan(data,false,Services,new[] { "eleven" }).Select("eleven");
            byte[] elevenKey=File.ReadAllBytes(Path.Combine(data,"elevenlabs-key.dat"));
            byte[] openAiKey=File.ReadAllBytes(Path.Combine(data,"openai-admin-key.dat"));
            window=new WidgetWindow(false);
            Field<DispatcherTimer>(window,"timer").Stop(); Field<Forms.NotifyIcon>(window,"tray").Visible=false;
            Check(!window.IsVisible && !window.IsLoaded,"Fixture never starts the loaded handler or provider requests");
            foreach (string id in Services) Check((bool)Call(window,"IsServiceActive",id),id+" is available without Nephylo login");
            Check(!(bool)Call(window,"IsServiceActive","untrusted-service"),"Unlimited services still reject unsupported provider identifiers");
            Check(!WidgetPalette.IsDark && Field<WidgetSettings>(window,"settings").Theme=="light","A saved light theme is honored before any login");
            Check((string)Call(window,"ThemeForSettingsSave","dark")=="dark","Accountless users may save a newly chosen theme");
            Call(window,"ApplyRequestedTheme","dark"); Check(WidgetPalette.IsDark,"Dark theme preview is available");
            Call(window,"ApplyRequestedTheme","light"); Check(!WidgetPalette.IsDark,"Light theme preview is available");
            var journal=Field<UsageJournal>(window,"usageJournal");
            Check(journal!=null && Object.ReferenceEquals(journal,Field<UsageJournal>(window,"persistentUsageJournal")),"Fresh accountless users receive the persistent journal, not memory-only fallback");
            Check(!Field<bool>(window,"usageTrackingMemoryFallback"),"No account fingerprint is required to open local history");
            var cards=Field<Dictionary<string,StackPanel>>(window,"serviceCards");
            Check(cards["eleven"].Visibility==Visibility.Visible && cards["gemini"].Visibility==Visibility.Visible,"Two configured providers are visible together despite a legacy single-service selection");
            Call(window,"SaveUsageOptions",new UsageTrackingOptions { LowEnabled=false, ResetEnabled=false, HistoryEnabled=false, ForecastEnabled=false });
            Check(!journal.Options.HistoryEnabled && !journal.Options.LowEnabled && !journal.Options.ResetEnabled,"Notification and history preferences can be disabled without payment");
            Check((string)Call(window,"WeeklySummaryMode")=="off","Disabled history is explained as a setting, not a paid feature");
            Call(window,"SaveUsageOptions",new UsageTrackingOptions { LowEnabled=true, ResetEnabled=true, HistoryEnabled=true, ForecastEnabled=true });
            Check(journal.Options.HistoryEnabled && journal.Options.ForecastEnabled,"History and forecasting may be enabled without login");
            int before=journal.GetHistory().Count;
            Call(window,"SaveManual","cursor",new ManualUsageRecord { Label="monthly", RemainingPercent=70, ObservedAt=DateTimeOffset.Now });
            Check(cards["cursor"].Visibility==Visibility.Visible && cards["eleven"].Visibility==Visibility.Visible && cards["gemini"].Visibility==Visibility.Visible,"Adding another service does not replace existing connected cards");
            Check(journal.GetHistory().Count>before,"Accountless manual observations contribute local history");
            before=journal.GetHistory().Count;
            Reject(()=>Call(window,"SaveManual","cursor",new ManualUsageRecord { Label="monthly", RemainingPercent=Double.NaN, ObservedAt=DateTimeOffset.Now }),"Unlimited connections still validate manual readings");
            Check(journal.GetHistory().Count==before,"A rejected reading cannot modify history");
            var active=((IEnumerable<string>)Call(window,"ActiveUsageServices")).ToList();
            Check(active.Contains("eleven") && active.Contains("gemini") && active.Contains("cursor"),"All configured sources are eligible for reminders without Nephylo authentication");
            var pending=Field<List<UsageNotice>>(window,"pendingUsageNotices"); pending.Clear();
            foreach (string kind in new[] { "low", "critical", "empty", "forecast" })
                Call(window,"QueueUsageNotices",(object)new[] { new UsageNotice { ServiceId="eleven", Kind=kind, Message="isolated accountless notice", CreatedAt=DateTimeOffset.Now } });
            Check(pending.Count==4,"All alert kinds can be queued for a connected accountless provider");
            Field<DispatcherTimer>(window,"noticeTimer").Stop(); pending.Clear();
            Unlocked((DependencyObject)Call(window,"BuildDashboardHistoryPage",false),"Usage history");
            Unlocked((DependencyObject)Call(window,"BuildDashboardHistoryPage",true),"Remaining balance analysis");
            Unlocked((DependencyObject)Call(window,"BuildDashboardTimelinePage"),"Reset timeline");
            Unlocked((DependencyObject)Call(window,"BuildWeeklySummaryPage"),"Weekly summary");
            Check((string)Call(window,"WeeklySummaryMode")=="ready","Weekly summaries are ready without an account");
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            var notifications=(Window)Call(window,"BuildNotificationSettingsDialog");
            Unlocked(notifications,"Notification settings"); Check(!notifications.IsVisible,"Notification settings are built without showing live UI"); notifications.Close();
            ((Task)Call(window,"FlushUsageTrackingAsync")).GetAwaiter().GetResult();
            Check(File.Exists(Path.Combine(data,"usage-journal.json")) && new UsageJournal(data,false).GetHistory().Count==journal.GetHistory().Count,"Accountless history persists to disk and reloads");
            Check(File.ReadAllBytes(Path.Combine(data,"elevenlabs-key.dat")).SequenceEqual(elevenKey) && File.ReadAllBytes(Path.Combine(data,"openai-admin-key.dat")).SequenceEqual(openAiKey),"Free conversion does not rewrite provider credentials");
            Check(!File.Exists(Path.Combine(data,"membership-session.dat")),"Using all local features never creates a Nephylo login session");
            Call(window,"ClearUsageHistory"); Check(journal.GetHistory().Count==0,"History deletion remains a deliberate local action available without login");
            ((Task)Call(window,"FlushUsageTrackingAsync")).GetAwaiter().GetResult();
            Console.WriteLine("Accountless free capability tests passed: "+checks+" checks. No network, visible UI, or live account changes."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if (window!=null) {
                Field<DispatcherTimer>(window,"timer").Stop(); Field<DispatcherTimer>(window,"noticeTimer").Stop();
                ((Task)Call(window,"FlushUsageTrackingAsync")).GetAwaiter().GetResult(); Set(window,"shutdownFlushed",true); window.Close();
                Field<CodexService>(window,"codex").Dispose(); Field<Forms.NotifyIcon>(window,"tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride=null; SettingsStore.StartupRegistrySubKeyOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null;
            Environment.SetEnvironmentVariable("CODEX_HOME",oldCodexHome);
            string full=Path.GetFullPath(root);
            if (full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("free-plan-data-",StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
}
