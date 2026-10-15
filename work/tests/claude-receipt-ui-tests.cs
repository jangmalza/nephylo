using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static class ClaudeReceiptUiTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static object Get(object target, string name) { return target.GetType().GetField(name,Flags).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name,Flags).SetValue(target,value); }
    private static object Call(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name,Flags).Invoke(target,args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        T found=parent as T; if (found != null) yield return found;
        foreach (object child in LogicalTreeHelper.GetChildren(parent)) {
            var element=child as DependencyObject; if (element == null) continue;
            foreach (T item in Descendants<T>(element)) yield return item;
        }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { throw new InvalidOperationException("This fixture forbids network requests."); }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly WidgetWindow Window;
        internal readonly WidgetSettings Settings;
        internal readonly ProviderView View;
        internal readonly List<UsageNotice> Pending;
        internal readonly MembershipClient Member;
        internal readonly string Root, Config;
        internal Fixture(string root, bool pro)
        {
            Root=root; Config=Path.Combine(root,"claude-config");
            SettingsStore.DataDirectoryOverride=root; ClaudeUsageService.ConfigDirectoryOverride=Config;
            Window=new WidgetWindow(true);
            ((DispatcherTimer)Get(Window,"timer")).Stop(); ((Forms.NotifyIcon)Get(Window,"tray")).Visible=false;
            Member=new MembershipClient(root,false,MembershipConfiguration.FromValues("https://fixture.supabase.co","sb_publishable_fixture_only",true),new NoNetwork(),()=>0L);
            if (pro) { Set(Member,"userId","fixture-user"); Set(Member,"verifiedPro",true); Set(Member,"entitlementDeadline",100000L); Set(Member,"subscriptionDeadline",100000L); }
            Set(Window,"demo",false);
            var plan=new ServicePlan(root,true,null,null); plan.Select("cursor"); Set(Window,"servicePlan",plan);
            Settings=(WidgetSettings)Get(Window,"settings"); View=(ProviderView)Get(Window,"claudeView"); Pending=(List<UsageNotice>)Get(Window,"pendingUsageNotices");
            Check(!Window.IsVisible && !Window.IsLoaded,"Fixture stays hidden and never starts normal startup requests");
            Check(Member.HasPro == pro,"Fixture entitlement is entirely synthetic");
        }
        internal void Render()
        {
            Call(Window,"RenderClaude");
            ((DispatcherTimer)Get(Window,"noticeTimer")).Stop();
        }
        public void Dispose()
        {
            ((DispatcherTimer)Get(Window,"timer")).Stop(); ((DispatcherTimer)Get(Window,"noticeTimer")).Stop();
            ((CodexService)Get(Window,"codex")).Dispose(); Set(Window,"demo",true); Window.Close();
            ((Forms.NotifyIcon)Get(Window,"tray")).Dispose(); Member.Dispose();
        }
    }
    private static string Quota(DateTimeOffset deadline)
    {
        long reset=(long)(deadline.ToUniversalTime()-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds;
        return "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":95,\"resets_at\":"+reset+"}}}";
    }
    private static string History(UsageJournal journal) { return new JavaScriptSerializer().Serialize(journal.GetHistory()); }
    private static ManualUsageRecord SetManual(Fixture fixture, DateTimeOffset observedAt, DateTimeOffset deadline)
    {
        var record=new ManualUsageRecord { Label="manual fixture quota", RemainingPercent=4, ObservedAt=observedAt, ResetsAt=deadline };
        ((ExtraSettings)Get(fixture.Window,"extras")).ClaudeManual=record;
        return record;
    }
    private static UsageSnapshot ManualSnapshot(ManualUsageRecord record)
    {
        return new UsageSnapshot { Rows=new List<UsageRow> { new UsageRow { Label=record.Label, RemainingPercent=record.RemainingPercent, ResetsAt=record.ResetsAt } } };
    }
    private static UsageJournal Track(Fixture fixture)
    {
        ClaudeUsageService.InstallStatusLine(Assembly.GetExecutingAssembly().Location);
        Set(fixture.Window,"claudeConfigured",true);
        var journal=new UsageJournal(fixture.Root,false,true); Set(fixture.Window,"usageJournal",journal);
        journal.SaveOptions(new UsageTrackingOptions { LowEnabled=true,ResetEnabled=true,HistoryEnabled=true });
        return journal;
    }
    private static void OldManualFallbackClearsOnlyAutomaticState(string root)
    {
        using (var fixture=new Fixture(root,true)) {
            var journal=Track(fixture);
            DateTimeOffset now=DateTimeOffset.UtcNow.AddMinutes(-1), deadline=DateTimeOffset.UtcNow.AddMinutes(2);
            var manual=SetManual(fixture,now,deadline.AddMinutes(1));
            fixture.Render();
            Check(fixture.Pending.Count(item=>item.ServiceId == "claude" && item.Label == manual.Label) == 1 && fixture.Pending.Any(item=>item.Label == manual.Label && item.Kind == "critical"),"Old manual baseline produces only its strongest initial critical notice");
            fixture.Pending.Clear(); // Its real first notification has already been delivered.
            ClaudeUsageService.CaptureJson(Quota(deadline),now.AddSeconds(10)); fixture.Render();
            Check(fixture.View.Source == "code" && fixture.Pending.Any(item=>item.ServiceId == "claude" && item.Label != manual.Label),"Newer Code quota replaces old manual display and schedules an automatic notice");
            string before=History(journal);
            ClaudeUsageService.CaptureJson("{}",now.AddSeconds(20)); fixture.Render(); journal.FlushAsync().GetAwaiter().GetResult();
            Check(fixture.View.Source == "manual" && fixture.View.Snapshot.Rows[0].Label == manual.Label,"A missing-quota receipt still permits the older manual display fallback");
            Check(!fixture.Pending.Any(item=>item.ServiceId == "claude" && item.Label != manual.Label),"Old manual fallback must not leave the previous Code notice queued");
            Check(!fixture.Pending.Any(item=>item.ServiceId == "claude" && item.Label == manual.Label),"Showing a cached old manual record must not repeat its delivered critical notice");
            Check(History(journal) == before,"Old manual fallback preserves Code and manual history without duplicating either");
            var reload=new UsageJournal(root,false);
            Check(reload.CheckResetReminders(deadline.AddMinutes(2),new[] { "claude" }).Count == 0,"Automatic reset is cleared and an obsolete cached manual schedule is not recreated");
            long revision=(long)Get(journal,"revision");
            fixture.Render(); fixture.Render(); journal.FlushAsync().GetAwaiter().GetResult();
            Check((long)Get(journal,"revision") == revision,"Repeated old manual fallback does not rewrite the journal");
        }
    }
    private static void NewManualSurvivesMissingQuotaReceipts(string root)
    {
        using (var fixture=new Fixture(root,true)) {
            var journal=Track(fixture);
            DateTimeOffset now=DateTimeOffset.UtcNow.AddMinutes(-1), codeDeadline=DateTimeOffset.UtcNow.AddMinutes(2), manualDeadline=codeDeadline.AddMinutes(2);
            ClaudeUsageService.CaptureJson(Quota(codeDeadline),now); fixture.Render();
            ClaudeUsageService.CaptureJson("{}",now.AddSeconds(10)); fixture.Render();
            var manual=SetManual(fixture,now.AddSeconds(20),manualDeadline); fixture.Render();
            Check(fixture.Pending.Count(item=>item.ServiceId == "claude" && item.Label == manual.Label) == 1 && fixture.Pending.Any(item=>item.Label == manual.Label && item.Kind == "critical"),"A newly entered manual quota produces exactly one strongest-stage critical notice");
            // Build a genuine manual reset notice through the journal as well,
            // without consuming the live fixture's still-upcoming schedule.
            var noticeFixture=new UsageJournal(null,true);
            noticeFixture.SaveOptions(journal.Options);
            noticeFixture.Observe("claude",ManualSnapshot(manual),manual.ObservedAt.Value,"manual",DateTimeOffset.UtcNow);
            var manualReset=noticeFixture.CheckResetReminders(manualDeadline.AddSeconds(1),new[] { "claude" });
            Check(manualReset.Count == 1,"Isolated manual reset fixture produces a genuine source-tagged reset notice");
            fixture.Pending.AddRange(manualReset);
            string before=History(journal); long revision=(long)Get(journal,"revision");
            ClaudeUsageService.CaptureJson("{}",now.AddSeconds(30)); fixture.Render();
            ClaudeUsageService.CaptureJson("{\"rate_limits\":null}",now.AddSeconds(40)); fixture.Render();
            journal.FlushAsync().GetAwaiter().GetResult();
            Check(fixture.View.Source == "manual","Later unknown Code receipts preserve an explicitly entered manual display");
            Check(fixture.Pending.Count(item=>item.ServiceId == "claude" && item.Label == manual.Label && item.Kind == "critical") == 1,"Later unknown receipts neither erase nor repeat a pending manual critical notice");
            Check(fixture.Pending.Count(item=>item.ServiceId == "claude" && item.Label == manual.Label && item.Kind == "reset") == 1,"Later unknown receipts preserve a genuine pending manual reset notice");
            Check(!fixture.Pending.Any(item=>item.ServiceId == "claude" && item.Label != manual.Label),"Later unknown receipts cannot resurrect the earlier automatic notice");
            Check(History(journal) == before,"Repeated missing-quota receipts do not duplicate or erase manual history");
            Check((long)Get(journal,"revision") == revision,"Unknown Code receipts do not rewrite an exclusively manual journal state");
            var reload=new UsageJournal(root,false);
            Check(reload.CheckResetReminders(codeDeadline.AddSeconds(1),new[] { "claude" }).Count == 0,"The removed Code schedule stays removed while the manual deadline is still pending");
            var reminders=reload.CheckResetReminders(manualDeadline.AddSeconds(1),new[] { "claude" });
            Check(reminders.Count == 1 && reminders[0].Label == manual.Label && reminders[0].Message.Contains("수동"),"The manual reset schedule survives persisted reload and fires once");
            fixture.Pending.Clear();
            ClaudeUsageService.CaptureJson("{}",now.AddSeconds(50)); fixture.Render();
            Check(!fixture.Pending.Any(item=>item.ServiceId == "claude"),"Fresh missing-quota receipt does not re-alert an already delivered manual observation");
            Check(History(journal) == before,"Delivered manual alerts do not change history on subsequent receipt renders");
        }
    }
    private static void SavedManualSurvivesStartupAndProRestoration(string root, bool startFree)
    {
        using (var fixture=new Fixture(root,!startFree)) {
            ((ServicePlan)Get(fixture.Window,"servicePlan")).Select("claude");
            ClaudeUsageService.InstallStatusLine(Assembly.GetExecutingAssembly().Location);
            Set(fixture.Window,"claudeConfigured",true);
            DateTimeOffset now=DateTimeOffset.UtcNow.AddMinutes(-1), deadline=DateTimeOffset.UtcNow.AddMinutes(2);
            var manual=SetManual(fixture,now,deadline);
            var saved=new UsageJournal(root,false);
            saved.SaveOptions(new UsageTrackingOptions { LowEnabled=true,ResetEnabled=true,HistoryEnabled=true });
            saved.Observe("claude",ManualSnapshot(manual),now,"manual",DateTimeOffset.UtcNow);
            string historyBefore=History(saved), journalPath=Path.Combine(root,"usage-journal.json");
            byte[] before=File.ReadAllBytes(journalPath);
            ClaudeUsageService.CaptureJson("{}",now.AddSeconds(10));
            Set(fixture.Window,"persistentUsageJournal",null);
            Call(fixture.Window,"InitializeUsageTracking");
            fixture.Render();
            Check(File.ReadAllBytes(journalPath).SequenceEqual(before),"Accountless manual fallback preserves the persisted journal without replaying alerts");
            var journal=(UsageJournal)Get(fixture.Window,"usageJournal"); journal.FlushAsync().GetAwaiter().GetResult();
            Check(fixture.View.Source == "manual",(startFree ? "Pro restoration" : "Startup")+" retains the saved manual display");
            Check(!fixture.Pending.Any(item=>item.ServiceId == "claude"),(startFree ? "Pro restoration" : "Startup")+" does not repeat a persisted manual critical notice");
            Check(History(journal) == historyBefore,(startFree ? "Pro restoration" : "Startup")+" preserves exact manual history");
            var reload=new UsageJournal(root,false);
            var reminders=reload.CheckResetReminders(deadline.AddSeconds(1),new[] { "claude" });
            Check(reminders.Count == 1 && reminders[0].Label == manual.Label,(startFree ? "Pro restoration" : "Startup")+" preserves the persisted manual reset schedule");
        }
    }
    private static void MissingQuotaClearsPendingState(string root)
    {
        using (var fixture=new Fixture(root,true)) {
            ClaudeUsageService.InstallStatusLine(Assembly.GetExecutingAssembly().Location);
            Set(fixture.Window,"claudeConfigured",true);
            var journal=new UsageJournal(root,false,true); Set(fixture.Window,"usageJournal",journal);
            journal.SaveOptions(new UsageTrackingOptions { LowEnabled=true,ResetEnabled=true,HistoryEnabled=true });
            DateTimeOffset now=DateTimeOffset.UtcNow.AddSeconds(-10), deadline=DateTimeOffset.UtcNow.AddMinutes(2);
            ClaudeUsageService.CaptureJson(Quota(deadline),now);
            fixture.Render(); journal.FlushAsync().GetAwaiter().GetResult();
            Check(fixture.Pending.Any(item=>item.ServiceId == "claude" && item.Kind == "critical"),"Initial 5 percent quota schedules a real pending Claude critical notice");
            string historyBefore=History(journal); int historyCount=journal.GetHistory().Count;
            Check(historyCount > 0,"Initial quota has persisted historical measurements");
            var beforeReload=new UsageJournal(root,false);
            Check(beforeReload.CheckResetReminders(deadline.AddSeconds(1),new[] { "claude" }).Count == 1,"Persisted baseline contains an upcoming Claude reset schedule");
            // The independent reload above consumes its own state. Persist the live
            // journal again so the receipt cleanup must remove the actual schedule.
            journal.SaveOptions(journal.Options); journal.FlushAsync().GetAwaiter().GetResult();
            fixture.Pending.Add(new UsageNotice { ServiceId="eleven",Kind="low",Message="other fixture provider" });
            fixture.View.Source="manual";
            ClaudeUsageService.CaptureJson("{}",now.AddSeconds(1));
            fixture.Render(); journal.FlushAsync().GetAwaiter().GetResult();
            Check(fixture.View.Snapshot == null && fixture.View.Source == "code","A new receipt without quota removes stale numbers and stale manual source labeling");
            Check(fixture.View.Badge.Text.Contains("한도 미제공"),"The card distinguishes receipt arrival from quota availability");
            Check(!fixture.Pending.Any(item=>item.ServiceId == "claude"),"Previously queued Claude critical/reset notifications are cleared");
            Check(fixture.Pending.Any(item=>item.ServiceId == "eleven"),"Receipt cleanup leaves another provider's pending notices intact");
            Check(History(journal) == historyBefore,"Unknown receipt preserves prior historical measurements exactly");
            var afterReload=new UsageJournal(root,false);
            Check(afterReload.CheckResetReminders(deadline.AddSeconds(1),new[] { "claude" }).Count == 0,"Cleared reset schedules stay cleared after a journal reload");
            Check(History(afterReload) == historyBefore,"Persisted history survives receipt cleanup");
            long revision=(long)Get(journal,"revision");
            fixture.Render(); fixture.Render(); journal.FlushAsync().GetAwaiter().GetResult();
            Check((long)Get(journal,"revision") == revision,"Rendering the same unknown receipt does not repeatedly persist cleanup");

            ClaudeUsageService.CaptureJson(Quota(deadline),now.AddSeconds(2)); fixture.Render();
            Check(fixture.View.Snapshot != null && fixture.Pending.Any(item=>item.ServiceId == "claude"),"A fresh quota receipt resumes display and starts a new notification baseline");
            string resumedHistory=History(journal);
            ClaudeUsageService.CaptureJson("{\"rate_limits\":null}",now.AddSeconds(3)); fixture.Render(); journal.FlushAsync().GetAwaiter().GetResult();
            Check(!fixture.Pending.Any(item=>item.ServiceId == "claude") && fixture.View.Snapshot == null,"A later missing-quota transition clears its newly created state too");
            Check(History(journal) == resumedHistory,"Repeated quota-to-unknown transitions retain all prior history");

            // Same receipt time under a different explicitly selected Windows
            // source must not be mistaken for the already processed default source.
            fixture.Settings.ClaudeWindowsConfigDirectory=fixture.Config;
            string newSource=ClaudeUsageService.GetWindowsSourceId(fixture.Config);
            Check(newSource.StartsWith("win:",StringComparison.Ordinal),"Explicit fixture configuration has a separate source identity");
            ClaudeUsageService.CaptureJson("{}",now.AddSeconds(3),newSource);
            fixture.Pending.Add(new UsageNotice { ServiceId="claude",Kind="reset",Message="new fixture source" });
            revision=(long)Get(journal,"revision"); fixture.Render(); journal.FlushAsync().GetAwaiter().GetResult();
            Check((long)Get(journal,"revision") == revision && !fixture.Pending.Any(item=>item.ServiceId == "claude"),"Source change clears source-specific pending notices without unnecessary journal persistence");
        }
    }
    private static void ExpiredForwardingCannotOptIn(string root)
    {
        using (var fixture=new Fixture(root,false)) {
            Directory.CreateDirectory(fixture.Config);
            string config=Path.Combine(fixture.Config,"settings.json");
            const string custom="{\"statusLine\":{\"type\":\"command\",\"command\":\"user-owned-statusline\"}}";
            File.WriteAllText(config,custom);
            ClaudeUsageService.CaptureJson(Quota(DateTimeOffset.UtcNow.AddHours(1)),DateTimeOffset.UtcNow.AddMinutes(-31));
            var editor=new StackPanel(); Call(fixture.Window,"BuildClaudeSettings",editor);
            var existing=Descendants<CheckBox>(editor).Single(item=>Convert.ToString(item.Content) == "기존 상태 표시줄에서 수신");
            var check=Descendants<Button>(editor).Single(item=>Convert.ToString(item.Content) == "수신 확인");
            existing.IsChecked=true;
            Check(Descendants<TextBlock>(editor).Any(item=>item.Text.Contains("이전 수신값")),"Expired forwarding receipt is labeled stale before opt-in");
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!fixture.Settings.ClaudeUseExistingStatusLine,"Expired forwarding receipt cannot save a new custom-hook opt-in");
            Check(((ServicePlan)Get(fixture.Window,"servicePlan")).ActiveServiceId == "cursor","Rejected stale receipt preserves the active Free service");
            Check(!(bool)Call(fixture.Window,"IsExtraReady","claude"),"Rejected stale forwarding does not advertise Claude as connected");
            Check(File.ReadAllText(config) == custom && !File.Exists(Path.Combine(root,"settings.json")),"Rejected opt-in changes neither custom script nor widget settings");
            var stale=(Task<UsageSnapshot>)Call(fixture.Window,"VerifyServiceForSelection","claude");
            Check(stale.IsCompleted && stale.Result == null,"Top-level service switching cannot bypass the rejected custom receipt");
            ClaudeUsageService.CaptureJson(Quota(DateTimeOffset.UtcNow.AddHours(1)),DateTimeOffset.UtcNow);
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(fixture.Settings.ClaudeUseExistingStatusLine && ((ServicePlan)Get(fixture.Window,"servicePlan")).ActiveServiceId == "cursor" && (bool)Call(fixture.Window,"IsServiceActive","claude"),"A fresh quota receipt enables Claude without replacing the saved Cursor preference");
            Check(File.ReadAllText(config) == custom,"Successful forwarding setup also preserves the user-owned script");
            var fresh=(Task<UsageSnapshot>)Call(fixture.Window,"VerifyServiceForSelection","claude");
            Check(fresh.IsCompleted && fresh.Result != null,"Top-level verification recognizes saved custom forwarding with fresh quota");
        }
    }
    private static void FreeReceiptThenProRestore(string root)
    {
        using (var fixture=new Fixture(root,false)) {
            ((ServicePlan)Get(fixture.Window,"servicePlan")).Select("claude");
            ClaudeUsageService.InstallStatusLine(Assembly.GetExecutingAssembly().Location);
            Set(fixture.Window,"claudeConfigured",true);
            DateTimeOffset now=DateTimeOffset.UtcNow.AddSeconds(-10), deadline=DateTimeOffset.UtcNow.AddMinutes(2);
            ClaudeUsageService.CaptureJson(Quota(deadline),now);
            var saved=new UsageJournal(root,false);
            saved.SaveOptions(new UsageTrackingOptions { LowEnabled=true,ResetEnabled=true,HistoryEnabled=true });
            saved.Observe("claude",ClaudeUsageService.ReadLatest().Snapshot,now,"code",now);
            string historyBefore=History(saved), journalPath=Path.Combine(root,"usage-journal.json");
            Set(fixture.Window,"persistentUsageJournal",null);
            Call(fixture.Window,"InitializeUsageTracking");
            ClaudeUsageService.CaptureJson("{}",now.AddSeconds(1));
            fixture.Render();
            Check(fixture.View.Badge.Text.Contains("한도 미제공"),"Accountless startup renders an unknown receipt without waiting for entitlement restoration");
            var restored=(UsageJournal)Get(fixture.Window,"usageJournal"); restored.FlushAsync().GetAwaiter().GetResult();
            Check(History(restored) == historyBefore,"Pro restoration retains historical samples after the unknown receipt");
            var reload=new UsageJournal(root,false);
            Check(reload.CheckResetReminders(deadline.AddSeconds(1),new[] { "claude" }).Count == 0,"A receipt already seen while Free still clears restored Pro reset state");
        }
    }
    [STAThread]
    public static int Main()
    {
        ClaudeUsageService.BridgeProbeOverride = (info, token) => System.Threading.Tasks.Task.FromResult("ok");
        string parent=Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        string root=Path.Combine(parent,"claude-receipt-test-"+Guid.NewGuid().ToString("N"));
        string oldHome=Environment.GetEnvironmentVariable("CODEX_HOME");
        try {
            Environment.SetEnvironmentVariable("CODEX_HOME",Path.Combine(root,"codex-home"));
            OldManualFallbackClearsOnlyAutomaticState(Path.Combine(root,"old-manual"));
            NewManualSurvivesMissingQuotaReceipts(Path.Combine(root,"new-manual"));
            SavedManualSurvivesStartupAndProRestoration(Path.Combine(root,"manual-startup"),false);
            SavedManualSurvivesStartupAndProRestoration(Path.Combine(root,"manual-restore"),true);
            MissingQuotaClearsPendingState(Path.Combine(root,"quota"));
            ExpiredForwardingCannotOptIn(Path.Combine(root,"forwarding"));
            FreeReceiptThenProRestore(Path.Combine(root,"restore"));
            Console.WriteLine("Claude receipt UI tests passed: "+checks+" assertions. Hidden fixtures and isolated journal/config only; no accounts, network or visible notifications."); return 0;
        } catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            SettingsStore.DataDirectoryOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null;
            Environment.SetEnvironmentVariable("CODEX_HOME",oldHome);
            string full=Path.GetFullPath(root);
            if (full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("claude-receipt-test-",StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
}
