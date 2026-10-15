using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static class UsageAnalysisIntegrationTests
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool condition,string message) { checks++; if(!condition) throw new Exception(message); }
    private static object Get(object target,string name) { return target.GetType().GetField(name,Flags).GetValue(target); }
    private static void Set(object target,string name,object value) { target.GetType().GetField(name,Flags).SetValue(target,value); }
    private static object Call(object target,string name,params object[] args)
    { try { return target.GetType().GetMethod(name,Flags).Invoke(target,args); } catch(TargetInvocationException error) { throw error.InnerException; } }
    private static UsageNotice Notice(string kind)
    { return new UsageNotice { Kind=kind,ServiceId="codex",Label="weekly",Source="api",Message="synthetic "+kind,CreatedAt=DateTimeOffset.UtcNow }; }

    private static void NoticePolicies()
    {
        foreach(bool low in new[]{false,true}) foreach(bool reset in new[]{false,true})
        foreach(bool history in new[]{false,true}) foreach(bool forecast in new[]{false,true}) {
            var options=new UsageTrackingOptions { LowEnabled=low,ResetEnabled=reset,HistoryEnabled=history,ForecastEnabled=forecast };
            foreach(string kind in new[]{"low","critical","empty","forecast","reset","reset-late","reset-confirmed","unknown"}) {
                bool expected=kind=="low"||kind=="critical"||kind=="empty" ? low : kind=="forecast" ? forecast&&history
                    : kind=="reset"||kind=="reset-late"||kind=="reset-confirmed" ? reset : false;
                Check(WidgetWindow.UsageNoticeEnabled(Notice(kind),options)==expected,"Notice option policy for "+kind+" must be independent of unrelated toggles.");
            }
        }
        Check(!WidgetWindow.UsageNoticeEnabled(null,new UsageTrackingOptions()),"A missing queued notice is never eligible.");
        var criticalDisabled=new UsageTrackingOptions { LowEnabled=true,CriticalThresholdPercent=0 };
        Check(!WidgetWindow.UsageNoticeEnabled(Notice("critical"),criticalDisabled),"A zero critical threshold disables critical notices that are already pending.");
        Check(WidgetWindow.UsageNoticeEnabled(Notice("empty"),criticalDisabled),"Disabling the critical stage still allows depletion notices.");
        foreach(string kind in new[]{"low","critical","empty","forecast"}) Check(WidgetWindow.UsageNoticeWarning(kind),kind+" gets warning severity.");
        foreach(string kind in new[]{"reset","reset-late","reset-confirmed","unknown"}) Check(!WidgetWindow.UsageNoticeWarning(kind),kind+" does not get depletion-warning severity.");
        var titles=new[]{"low","critical","empty","forecast","reset","reset-confirmed"}.Select(WidgetWindow.UsageNoticeTitle).ToArray();
        Check(titles.All(title=>!String.IsNullOrWhiteSpace(title)),"Every supported notice kind has a usable tray title.");
        Check(titles.Distinct().Count()==titles.Length,"Low, critical, empty, forecast, scheduled-reset and confirmed-reset titles remain distinguishable.");
        Check(WidgetWindow.UsageNoticeTitle("forecast")!=WidgetWindow.UsageNoticeTitle("reset"),"Forecast cannot be mislabeled as a scheduled reset.");
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        { throw new InvalidOperationException("Analysis integration tests forbid network."); }
    }

    private sealed class AnalysisFixture : IDisposable
    {
        internal readonly WidgetWindow Window;
        internal readonly MembershipClient Member;
        internal readonly UsageJournal Journal;
        internal readonly ProviderView View;
        internal readonly List<UsageNotice> Pending;
        internal AnalysisFixture(string root)
        {
            SettingsStore.DataDirectoryOverride=root;
            ClaudeUsageService.ConfigDirectoryOverride=Path.Combine(root,"claude");
            Member=new MembershipClient(root,false,MembershipConfiguration.FromValues("https://fixture.supabase.co","sb_publishable_fixture_only",true),new NoNetwork(),()=>0L);
            Window=new WidgetWindow(true,Member);
            ((DispatcherTimer)Get(Window,"timer")).Stop(); ((Forms.NotifyIcon)Get(Window,"tray")).Visible=false;
            Set(Window,"demo",false);
            Set(Member,"userId","fixture-user"); Set(Member,"verifiedPro",true); Set(Member,"entitlementDeadline",100000L); Set(Member,"subscriptionDeadline",100000L);
            Journal=new UsageJournal(null,true); Journal.SetAnalysisOwner(Member.AnalysisOwner);
            Set(Window,"usageJournal",Journal); Set(Window,"persistentUsageJournal",Journal);
            View=(ProviderView)Get(Window,"codexView"); View.Error=null;
            Pending=(List<UsageNotice>)Get(Window,"pendingUsageNotices");
        }
        internal void Queue(params UsageNotice[] notices)
        { Call(Window,"QueueUsageNotices",(object)notices); ((DispatcherTimer)Get(Window,"noticeTimer")).Stop(); }
        public void Dispose()
        {
            Set(Window,"demo",true); ((DispatcherTimer)Get(Window,"timer")).Stop(); ((DispatcherTimer)Get(Window,"noticeTimer")).Stop();
            Window.Close(); Member.Dispose(); SettingsStore.DataDirectoryOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null;
        }
    }

    private static UsageSnapshot Weekly(double remaining,DateTimeOffset reset)
    { return new UsageSnapshot { Plan="fixture",Rows=new List<UsageRow> { new UsageRow { Label="weekly",RemainingPercent=remaining,ResetsAt=reset } } }; }

    private static void ObservationTimeSafety(string root)
    {
        using(var fixture=new AnalysisFixture(root)) {
            var observed=(Dictionary<string,DateTimeOffset>)Get(fixture.Window,"usageObservedAt");
            DateTimeOffset now=DateTimeOffset.UtcNow;
            fixture.View.Snapshot=Weekly(70,now.AddDays(4)); fixture.View.LastSuccess=now.AddHours(1);
            Call(fixture.Window,"ObserveUsage",fixture.View,"api");
            Check(fixture.Journal.GetHistory().Count==0&&!observed.ContainsKey("codex|api"),"Rejected future observations cannot poison the widget's duplicate-observation cache.");
            fixture.View.LastSuccess=now.AddMinutes(-10); Call(fixture.Window,"ObserveUsage",fixture.View,"api");
            Check(fixture.Journal.GetHistory().Count==0&&!observed.ContainsKey("codex|api"),"Rejected stale observations cannot become the widget's accepted baseline.");
            fixture.View.LastSuccess=now; Call(fixture.Window,"ObserveUsage",fixture.View,"api");
            Check(fixture.Journal.GetHistory().Count==1&&observed["codex|api"]==now,"A valid observation is recorded immediately after rejected timestamps.");
            Call(fixture.Window,"ObserveUsage",fixture.View,"api");
            Check(fixture.Journal.GetHistory().Count==1,"A cached accepted observation still cannot duplicate history.");
            fixture.View.Error="synthetic provider failure"; fixture.View.LastSuccess=now.AddSeconds(1);
            Call(fixture.Window,"ObserveUsage",fixture.View,"api");
            Check(observed["codex|api"]==now,"Failed provider reads cannot advance the accepted observation boundary.");
        }
    }

    private static bool HasNoForecast(UsageAnalysis value)
    { return !value.PredictedEmptyAt.HasValue&&!value.EarliestEmptyAt.HasValue&&!value.LatestEmptyAt.HasValue&&value.RiskLevel==0; }

    private static void AnalysisLifecycleSafety(string root)
    {
        using(var fixture=new AnalysisFixture(root)) {
            DateTimeOffset now=DateTimeOffset.UtcNow,reset=now.AddDays(4);
            for(int hour=0;hour<=96;hour++) {
                DateTimeOffset at=now.AddHours(hour-96);
                fixture.Journal.Observe("codex",Weekly(100-.8*hour,reset),at,"api",at);
            }
            var baseline=fixture.Journal.GetAnalysis("codex","weekly","api",now);
            Check(baseline.TrendPerDay.HasValue&&baseline.RecommendedPerDay.HasValue&&baseline.PredictedEmptyAt.HasValue,"Integration fixture contains actual trend, daily guidance and forecast evidence.");
            var stale=fixture.Journal.GetAnalysis("codex","weekly","api",now.AddHours(1));
            Check(HasNoForecast(stale)&&!stale.RecommendedPerDay.HasValue,"An old automatic balance cannot expose an exhaustion range or daily allowance.");
            var future=fixture.Journal.GetAnalysis("codex","weekly","api",now.AddMinutes(-10));
            Check(HasNoForecast(future)&&!future.RecommendedPerDay.HasValue,"A backwards clock cannot use a future balance as current guidance.");
            fixture.Queue(Notice("forecast"));
            Call(fixture.Window,"SaveUsageOptions",new UsageTrackingOptions { HistoryEnabled=false,ForecastEnabled=true });
            var disabled=fixture.Journal.GetAnalysis("codex","weekly","api",now);
            Check(HasNoForecast(disabled)&&!disabled.AveragePerDay.HasValue&&!disabled.TrendPerDay.HasValue&&!disabled.RecommendedPerDay.HasValue,"History disabled hides every new analytics field immediately.");
            Check(!fixture.Pending.Any(n=>n.Kind=="forecast"),"History disabled invalidates queued forecasts from the trained series.");
            Call(fixture.Window,"SaveUsageOptions",new UsageTrackingOptions { HistoryEnabled=true,ForecastEnabled=true });
            Check(HasNoForecast(fixture.Journal.GetAnalysis("codex","weekly","api",now)),"Re-enabling history does not revive a forecast before a new accepted baseline.");
            fixture.Journal.Observe("eleven",new UsageSnapshot { Rows=new List<UsageRow> { new UsageRow { Label="credits",Remaining=500,Limit=1000,ResetsAt=reset } } },now,"api",now);
            int historyCount=fixture.Journal.GetHistory().Count;
            fixture.Queue(Notice("forecast"));
            ((Dictionary<string,DateTimeOffset>)Get(fixture.Window,"usageObservedAt"))["codex|api"]=now;
            Call(fixture.Window,"ForgetUsageService","codex");
            var disconnected=fixture.Journal.GetAnalysis("codex","weekly","api",now);
            Check(HasNoForecast(disconnected)&&!disconnected.TrendPerDay.HasValue&&!disconnected.RecommendedPerDay.HasValue,"A connection change clears the old account's complete analytics result.");
            Check(!fixture.Pending.Any(n=>n.ServiceId=="codex")&&!((Dictionary<string,DateTimeOffset>)Get(fixture.Window,"usageObservedAt")).ContainsKey("codex|api"),"Disconnect clears pending forecasts and widget observation deduplication together.");
            Check(fixture.Journal.GetHistory().Count==historyCount&&fixture.Journal.GetAnalysis("eleven","credits","api",now).Unit=="credits","Disconnect retains raw history and the other service's derived state.");
            fixture.View.Snapshot=Weekly(85,reset); fixture.View.LastSuccess=now;
            Call(fixture.Window,"ObserveUsage",fixture.View,"api");
            var reconnected=fixture.Journal.GetAnalysis("codex","weekly","api",now);
            Check(!reconnected.AveragePerDay.HasValue&&!reconnected.TrendPerDay.HasValue&&HasNoForecast(reconnected),"A fresh connection cannot reuse the former account's forecast training.");
            fixture.Queue(Notice("forecast"));
            Set(fixture.Member,"userId","fixture-other-user"); Call(fixture.Window,"ApplyMembershipState");
            Check(fixture.Pending.Any(n=>n.Kind=="forecast")&&fixture.Journal.GetAnalysis("codex","weekly","api",now).Unit!=null,"Retired Nephylo account changes cannot discard local alerts or analysis.");
            Check(fixture.Journal.GetHistory().Count==historyCount+1,"A membership owner switch preserves raw historical samples.");
            Set(fixture.Member,"verifiedPro",false); Call(fixture.Window,"ApplyMembershipState");
            Check(Call(fixture.Window,"BuildUsageAnalysisPanel","codex","weekly","api",now,false)!=null,"Accountless use exposes retained analysis.");
        }
    }

    private static void CapabilityAndOptions(string root)
    {
        WidgetWindow window=null;
        MembershipClient member=null;
        try {
            SettingsStore.DataDirectoryOverride=root;
            ClaudeUsageService.ConfigDirectoryOverride=Path.Combine(root,"claude");
            member=new MembershipClient(root,false,MembershipConfiguration.FromValues("https://fixture.supabase.co","sb_publishable_fixture_only",true),new NoNetwork(),()=>0L);
            window=new WidgetWindow(true,member);
            ((DispatcherTimer)Get(window,"timer")).Stop(); ((Forms.NotifyIcon)Get(window,"tray")).Visible=false;
            Set(window,"demo",false);
            Set(member,"userId","fixture-user"); Set(member,"verifiedPro",true); Set(member,"entitlementDeadline",100000L); Set(member,"subscriptionDeadline",100000L);
            var journal=new UsageJournal(null,true);
            Set(window,"usageJournal",journal); Set(window,"persistentUsageJournal",journal);
            DateTimeOffset now=DateTimeOffset.UtcNow;
            journal.Observe("codex",new UsageSnapshot { Rows=new List<UsageRow> { new UsageRow { Label="weekly",RemainingPercent=75,ResetsAt=now.AddDays(4) } } },now,"api",now);
            var pending=(List<UsageNotice>)Get(window,"pendingUsageNotices");
            Check(!window.IsVisible&&!window.IsLoaded,"Capability fixture stays hidden with no startup request or actual account.");
            Call(window,"QueueUsageNotices",(object)new[]{Notice("forecast"),Notice("critical")});
            ((DispatcherTimer)Get(window,"noticeTimer")).Stop();
            Check(pending.Any(n=>n.Kind=="forecast")&&pending.Any(n=>n.Kind=="critical"),"Verified Pro fixture can queue both forecast and low-threshold notices.");
            Call(window,"QueueUsageNotices",(object)new[]{Notice("empty")});
            Call(window,"SaveUsageOptions",new UsageTrackingOptions { LowEnabled=true,ResetEnabled=true,HistoryEnabled=true,ForecastEnabled=true,CriticalThresholdPercent=0 });
            Check(!pending.Any(n=>n.Kind=="critical")&&pending.Any(n=>n.Kind=="empty"),"Turning off the critical stage removes pending critical notices while preserving depletion notices.");
            Call(window,"SaveUsageOptions",new UsageTrackingOptions { LowEnabled=true,ResetEnabled=true,HistoryEnabled=true,ForecastEnabled=true,CriticalThresholdPercent=10 });
            Call(window,"QueueUsageNotices",(object)new[]{Notice("critical")}); ((DispatcherTimer)Get(window,"noticeTimer")).Stop();
            Call(window,"SaveUsageOptions",new UsageTrackingOptions { LowEnabled=true,ResetEnabled=true,HistoryEnabled=false,ForecastEnabled=true });
            Check(!pending.Any(n=>n.Kind=="forecast")&&pending.Any(n=>n.Kind=="critical"),"Turning history off drops queued forecasts without dropping eligible threshold notices.");
            Check(journal.GetAnalysis("codex","weekly","api",now).Status=="이력 기록 꺼짐","Disabled history immediately makes new analysis unavailable.");
            Call(window,"SaveUsageOptions",new UsageTrackingOptions { LowEnabled=true,ResetEnabled=true,HistoryEnabled=true,ForecastEnabled=true });
            Call(window,"QueueUsageNotices",(object)new[]{Notice("forecast")}); ((DispatcherTimer)Get(window,"noticeTimer")).Stop();
            Check(pending.Any(n=>n.Kind=="forecast"),"Enabled history restores forecast eligibility for new events.");
            Call(window,"ClearUsageHistory");
            Check(journal.GetHistory().Count==0&&journal.GetAnalysis("codex","weekly","api",now).Unit==null,"History deletion clears its derived analysis alongside raw samples.");
            Check(!pending.Any(n=>n.Kind=="forecast")&&pending.Any(n=>n.Kind=="critical"),"History deletion invalidates pending forecast only, preserving independent threshold alerts.");
            Call(window,"QueueUsageNotices",(object)new[]{Notice("forecast"),Notice("empty")}); ((DispatcherTimer)Get(window,"noticeTimer")).Stop();
            Set(member,"verifiedPro",false); Call(window,"ApplyMembershipState");
            Check(pending.Any(n=>n.Kind=="empty"),"Legacy expiry cannot discard local alerts.");
            foreach(string kind in new[]{"critical","empty","forecast"}) Call(window,"QueueUsageNotices",(object)new[]{Notice(kind)});
            Check(pending.Any(n=>n.Kind=="critical")&&pending.Any(n=>n.Kind=="forecast"),"All alert kinds remain available without Pro.");
            Call(window,"SaveUsageOptions",new UsageTrackingOptions { LowEnabled=false,ResetEnabled=false,HistoryEnabled=false,ForecastEnabled=false });
            Check(pending.Count==0,"Disabling notifications explicitly discards pending alerts.");
        }
        finally {
            if(window!=null) { Set(window,"demo",true); ((DispatcherTimer)Get(window,"timer")).Stop(); ((DispatcherTimer)Get(window,"noticeTimer")).Stop(); window.Close(); }
            if(member!=null) member.Dispose();
            SettingsStore.DataDirectoryOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null;
        }
    }

    [STAThread]
    public static int Main()
    {
        string parent=Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        string root=Path.Combine(parent,"usage-analysis-integration-"+Guid.NewGuid().ToString("N"));
        string prior=Environment.GetEnvironmentVariable("CODEX_HOME");
        try {
            Environment.SetEnvironmentVariable("CODEX_HOME",Path.Combine(root,"codex"));
            NoticePolicies(); CapabilityAndOptions(Path.Combine(root,"options"));
            ObservationTimeSafety(Path.Combine(root,"timestamps")); AnalysisLifecycleSafety(Path.Combine(root,"lifecycle"));
            Console.WriteLine("Usage analysis integration tests passed: "+checks+" assertions. Isolated hidden fixtures only; no network, live accounts or notifications."); return 0;
        } catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            Environment.SetEnvironmentVariable("CODEX_HOME",prior);
            string full=Path.GetFullPath(root);
            if(full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                &&Path.GetFileName(full).StartsWith("usage-analysis-integration-",StringComparison.Ordinal)&&Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
}
