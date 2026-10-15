using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

// Migration regression: old sessions must be inert. Feature access and existing
// local history must survive offline boots without a Nephylo authentication call.
internal static class MembershipUiTests
{
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    private const string FixtureId="19bd679d-8d7e-4865-9657-2400c3503e38";
    private static int checks;
    private static T Field<T>(object owner,string name) { return (T)owner.GetType().GetField(name,Hidden).GetValue(owner); }
    private static void Set(object owner,string name,object value) { owner.GetType().GetField(name,Hidden).SetValue(owner,value); }
    private static object Call(object owner,string name,params object[] args)
    { try { return owner.GetType().GetMethod(name,Hidden).Invoke(owner,args); } catch(TargetInvocationException error) { throw error.InnerException??error; } }
    private static void Check(bool value,string message) { checks++; if(!value) throw new Exception(message); }
    private sealed class OfflineServer:HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        { Requests++; throw new HttpRequestException("No network is permitted in accountless migration tests."); }
    }
    private static MembershipClient Client(string data,OfflineServer server)
    { return new MembershipClient(data,false,MembershipConfiguration.FromValues("https://test.supabase.co","sb_publishable_fixture_only",true,true),server,()=>1000L); }
    private static void LegacyState(MembershipClient member,bool pro,bool expired)
    {
        Set(member,"userId",FixtureId); Set(member,"email","migration-fixture@example.com"); Set(member,"refreshToken","isolated-refresh-token");
        Set(member,"verifiedPro",pro); Set(member,"entitlementDeadline",expired?0L:100000L); Set(member,"subscriptionDeadline",expired?0L:100000L);
    }
    private static void Lifecycle(WidgetWindow window)
    {
        foreach(string method in new[] { "RestoreMembershipAsync", "CheckMembershipAsync", "RefreshMembershipOnActivationAsync", "RecoverMembershipAfterNetworkAsync" })
            ((Task)Call(window,method)).GetAwaiter().GetResult();
        Call(window,"ApplyMembershipState"); Call(window,"RefreshMembershipStatusNotice");
    }
    private static void Stop(WidgetWindow window)
    {
        if(window==null) return;
        Field<DispatcherTimer>(window,"timer").Stop(); Field<DispatcherTimer>(window,"noticeTimer").Stop();
        ((Task)Call(window,"FlushUsageTrackingAsync")).GetAwaiter().GetResult(); Set(window,"shutdownFlushed",true); window.Close();
        Field<CodexService>(window,"codex").Dispose(); Field<Forms.NotifyIcon>(window,"tray").Dispose();
    }
    private static WidgetWindow Create(string data,MembershipClient member)
    {
        SettingsStore.DataDirectoryOverride=data;
        var window=new WidgetWindow(false,member); Field<DispatcherTimer>(window,"timer").Stop(); Field<Forms.NotifyIcon>(window,"tray").Visible=false;
        Check(!window.IsLoaded && !window.IsVisible,"Migration fixture never starts live provider lookup or opens a window"); return window;
    }
    private static void CheckProfile(string root,string scenario)
    {
        string data=Path.Combine(root,scenario); Directory.CreateDirectory(data); SettingsStore.DataDirectoryOverride=data;
        SettingsStore.Save(new WidgetSettings { Theme="light",Language="ko" }); SettingsStore.SaveApiKey("isolated-preserved-key");
        byte[] key=File.ReadAllBytes(Path.Combine(data,"elevenlabs-key.dat"));
        string sessionPath=Path.Combine(data,"membership-session.dat");
        var server=new OfflineServer(); var member=Client(data,server); WidgetWindow window=null;
        try {
            if(scenario=="corrupt-session") File.WriteAllBytes(sessionPath,new byte[] { 0,255,6,8,12 });
            if(scenario=="saved-session" || scenario=="previous-pro" || scenario=="expired-pro") {
                LegacyState(member,true,scenario=="expired-pro"); Call(member,"PersistSession",0);
                Check(File.Exists(sessionPath) && new FileInfo(sessionPath).Length>0,"Legacy session fixture uses the actual protected storage format");
            }
            if(scenario=="previous-free") { LegacyState(member,false,false); Call(member,"PersistSession",0); }
            byte[] session=File.Exists(sessionPath)?File.ReadAllBytes(sessionPath):null;
            if(scenario=="saved-session") { member.Dispose(); member=Client(data,server=new OfflineServer()); }
            window=Create(data,member); Lifecycle(window);
            Check(server.Requests==0,scenario+": construction, restore, ticks, focus and network signals never call membership HTTP");
            Check(!WidgetPalette.IsDark,scenario+": offline account state cannot force a saved light theme to dark");
            foreach(string id in new[] { "codex","eleven","openai","claude","gemini","cursor","higgsfield","higgsfield-api" })
                Check((bool)Call(window,"IsServiceActive",id),scenario+": "+id+" remains available");
            Check(!Field<bool>(window,"usageTrackingMemoryFallback") && Object.ReferenceEquals(Field<UsageJournal>(window,"usageJournal"),Field<UsageJournal>(window,"persistentUsageJournal")),scenario+": history opens on disk without a valid login");
            Check(Field<Border>(window,"membershipStatusNotice").Visibility==Visibility.Collapsed,scenario+": no obsolete login expiry, verification, or Pro warning is displayed");
            Check(File.ReadAllBytes(Path.Combine(data,"elevenlabs-key.dat")).SequenceEqual(key),scenario+": provider credentials remain byte-for-byte intact");
            Check(session==null?!File.Exists(sessionPath):File.Exists(sessionPath)&&File.ReadAllBytes(sessionPath).SequenceEqual(session),scenario+": legacy session is neither required, erased, nor rewritten");
            var journal=Field<UsageJournal>(window,"usageJournal");
            Call(window,"SaveUsageOptions",new UsageTrackingOptions { HistoryEnabled=false,LowEnabled=false,ResetEnabled=false,ForecastEnabled=false });
            LegacyState(member,false,true); Lifecycle(window);
            Check(Object.ReferenceEquals(journal,Field<UsageJournal>(window,"usageJournal")) && !journal.Options.HistoryEnabled && !journal.Options.LowEnabled,scenario+": changing obsolete entitlement state never replaces the journal or resets preferences");
            Check(server.Requests==0,scenario+": feature changes do not trigger authentication");
        }
        finally { Stop(window); member.Dispose(); }
    }
    private static UsageSnapshot Snapshot(double remaining,DateTimeOffset reset)
    { return new UsageSnapshot { Rows=new List<UsageRow> { new UsageRow { Label="weekly",RemainingPercent=remaining,ResetsAt=reset } } }; }
    private static void CheckOwnedJournal(string root)
    {
        string data=Path.Combine(root,"owned-journal"); Directory.CreateDirectory(data); SettingsStore.DataDirectoryOverride=data;
        SettingsStore.Save(new WidgetSettings { Theme="light",Language="ko" }); SettingsStore.SaveApiKey("isolated-owned-key");
        var seed=new UsageJournal(data,false); seed.SetAnalysisOwner(new string('a',64));
        seed.SaveOptions(new UsageTrackingOptions { HistoryEnabled=true,LowEnabled=true,ResetEnabled=true,ForecastEnabled=false,LowThresholdPercent=25,CriticalThresholdPercent=5 });
        var now=DateTimeOffset.Now; var reset=now.AddMinutes(60);
        seed.Observe("codex",Snapshot(8,reset),now.AddMinutes(-2),"api",now.AddMinutes(-2));
        seed.Observe("codex",Snapshot(7,reset),now.AddMinutes(-1),"api",now.AddMinutes(-1));
        seed.Observe("eleven",new UsageSnapshot { Rows=new List<UsageRow> { new UsageRow { Label="credits",Remaining=600,Limit=1000,ResetsAt=reset } } },now.AddMinutes(-1),"api",now.AddMinutes(-1));
        string path=Path.Combine(data,"usage-journal.json"); byte[] before=File.ReadAllBytes(path);
        var json=new JavaScriptSerializer(); string history=json.Serialize(seed.GetHistory()), notices=json.Serialize(seed.GetRecentNotices()), options=json.Serialize(seed.Options);
        string analysis=json.Serialize(seed.GetAnalysis("codex","weekly","api",now)), observed=json.Serialize(seed.GetObservedHistory("codex","weekly","api",now));
        Check(seed.GetAnalysis("codex","weekly","api",now).Unit!=null && seed.GetRecentNotices().Any(),"Prior Pro journal contains a live analysis baseline and alert dedupe state");
        var server=new OfflineServer(); var member=Client(data,server); WidgetWindow window=null;
        try {
            window=Create(data,member); Lifecycle(window); var journal=Field<UsageJournal>(window,"usageJournal");
            Check(File.ReadAllBytes(path).SequenceEqual(before),"Accountless startup leaves the owned journal byte-for-byte unchanged");
            Check(json.Serialize(journal.GetHistory())==history && json.Serialize(journal.GetRecentNotices())==notices && json.Serialize(journal.Options)==options,"Raw history, notification history and custom preferences survive the accountless migration");
            Check(json.Serialize(journal.GetAnalysis("codex","weekly","api",now))==analysis && json.Serialize(journal.GetObservedHistory("codex","weekly","api",now))==observed,"Derived analysis and observed buckets survive; accountless adoption does not call destructive owner switching");
            var pending=Field<List<UsageNotice>>(window,"pendingUsageNotices"); pending.Add(new UsageNotice { ServiceId="eleven",Kind="low",CreatedAt=now,Message="isolated queued notice" });
            LegacyState(member,true,false); Lifecycle(window); LegacyState(member,false,true); Lifecycle(window);
            Check(pending.Count==1 && Object.ReferenceEquals(journal,Field<UsageJournal>(window,"usageJournal")),"Legacy sign-in or expiry cannot clear queued notifications or replace a local journal"); pending.Clear();
            Check(File.ReadAllBytes(path).SequenceEqual(before) && server.Requests==0,"Legacy identity changes neither rewrite the owned journal nor contact the server");
            var repeated=journal.Observe("codex",Snapshot(7,reset),now,"api",now);
            Check(!repeated.Any(item=>item.Kind=="low" || item.Kind=="critical" || item.Kind=="empty"),"Previously delivered shortage alerts are not replayed on free migration");
            var resetNotices=journal.CheckResetReminders(reset.AddSeconds(1),new[] { "codex" });
            Check(resetNotices.Count==1 && resetNotices[0].Kind=="reset","A registered reset reminder survives free migration exactly once");
            Check(journal.CheckResetReminders(reset.AddMinutes(1),new[] { "codex" }).Count==0,"The preserved reset reminder is deduplicated after delivery");
            ((Task)Call(window,"FlushUsageTrackingAsync")).GetAwaiter().GetResult();
            var reloaded=new UsageJournal(data,false);
            Check(reloaded.GetHistory().Count>=seed.GetHistory().Count && reloaded.CheckResetReminders(reset.AddMinutes(2),new[] { "codex" }).Count==0,"History and reset dedupe survive another accountless restart");
            Call(window,"ForgetUsageService","codex");
            Check(journal.GetAnalysis("codex","weekly","api",now).Unit==null && journal.GetAnalysis("eleven","credits","api",now).Unit!=null,"Actual provider changes still invalidate only that provider's derived baseline");
        }
        finally { Stop(window); member.Dispose(); }
    }
    private static void CheckRecoveryNotice(string root)
    {
        string data=Path.Combine(root,"recovery"); Directory.CreateDirectory(data); SettingsStore.DataDirectoryOverride=data;
        var server=new OfflineServer(); var member=Client(data,server); WidgetWindow window=null;
        try {
            window=Create(data,member);
            string legacy=Path.Combine(root,"legacy-corrupt-cache"), recovered=Path.Combine(root,"generated-recovery");
            Directory.CreateDirectory(legacy); File.WriteAllBytes(Path.Combine(legacy,"update-attempt.json"),new byte[0]);
            WidgetDataProfile.InitializeForTests(recovered,legacy,p=>Path.GetFullPath(p),null);
            string notice=Path.Combine(data,WidgetDataProfile.RecoveryNoticeName);
            File.Copy(Path.Combine(recovered,WidgetDataProfile.RecoveryNoticeName),notice); Call(window,"RefreshMembershipStatusNotice");
            Check(Field<Border>(window,"membershipStatusNotice").Visibility==Visibility.Visible && Field<bool>(window,"profileRecoveryNoticeVisible"),"Real profile migration guidance stays visible without a membership client");
            Check(Field<TextBlock>(window,"membershipStatusTitle").Text=="일부 기록 복구 안내" && Object.Equals(Field<Button>(window,"membershipStatusRetry").Content,"확인"),"Recovery notice has a local acknowledgment, not a sign-in action");
            L.Language="en"; Call(window,"RefreshMembershipStatusNotice"); Check(Field<TextBlock>(window,"membershipStatusDetail").Text.StartsWith("Some saved usage"),"Local recovery advice respects the English language setting"); L.Language="ko";
            using(var locked=new FileStream(notice,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                ((Task)Call(window,"RecheckMembershipFromNoticeAsync")).GetAwaiter().GetResult();
                Check(File.Exists(notice) && Field<Border>(window,"membershipStatusNotice").Visibility==Visibility.Visible,"A locked notice remains visible instead of silently discarding recovery advice");
            }
            ((Task)Call(window,"RecheckMembershipFromNoticeAsync")).GetAwaiter().GetResult();
            Check(!File.Exists(notice) && Field<Border>(window,"membershipStatusNotice").Visibility==Visibility.Collapsed && server.Requests==0,"Acknowledgment removes only local advice with zero authentication calls");
            Check(File.Exists(Path.Combine(legacy,"update-attempt.json")),"Recovery acknowledgment preserves the original corrupt file for support");
        }
        finally { Stop(window); member.Dispose(); }
    }
    [STAThread]
    public static int Main()
    {
        string parent=Path.GetFullPath(Path.GetDirectoryName(typeof(MembershipUiTests).Assembly.Location));
        string root=Path.Combine(parent,"membership-ui-data-"+Guid.NewGuid().ToString("N"));
        string previous=Environment.GetEnvironmentVariable("CODEX_HOME");
        try {
            SettingsStore.StartupRegistrySubKeyOverride=@"Software\UsageWidget\Tests\accountless-migration-"+Guid.NewGuid().ToString("N");
            ClaudeUsageService.ConfigDirectoryOverride=Path.Combine(root,"claude"); Environment.SetEnvironmentVariable("CODEX_HOME",Path.Combine(root,"codex"));
            foreach(string scenario in new[] { "fresh","corrupt-session","saved-session","previous-free","previous-pro","expired-pro" }) CheckProfile(root,scenario);
            CheckOwnedJournal(root); CheckRecoveryNotice(root);
            Console.WriteLine("Accountless migration UI tests passed: "+checks+" checks. Zero membership HTTP requests; protected fixture sessions and local history preserved."); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            L.Language="ko"; SettingsStore.DataDirectoryOverride=null; SettingsStore.StartupRegistrySubKeyOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null;
            Environment.SetEnvironmentVariable("CODEX_HOME",previous);
            string full=Path.GetFullPath(root);
            if(full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("membership-ui-data-",StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
}
