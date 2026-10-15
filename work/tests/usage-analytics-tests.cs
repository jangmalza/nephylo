using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using UsageWidget;

internal static class UsageAnalyticsTests
{
    private static int checks;
    // Restart fixtures also advance nineteen hours past End. Keep every poll
    // in the past so morning runs do not exercise the future-record rejection.
    private static readonly DateTimeOffset End = new DateTimeOffset(DateTime.Today.AddDays(-1));
    private static readonly DateTimeOffset Start = End.AddDays(-4);
    private static readonly DateTimeOffset Reset = End.AddDays(3);
    private static void Check(bool test, string message) { checks++; if (!test) throw new Exception(message); }
    private static void Near(double? actual, double expected, string message) { Check(actual.HasValue && Math.Abs(actual.Value - expected) < .00001,message + ": " + actual); }
    private static List<UsageNotice> Put(UsageJournal journal, DateTimeOffset at, double value, DateTimeOffset? reset, string source = "api", string label = "주간 한도", string service = "codex", double? limit = null, string plan = "Pro")
    {
        bool amount = service == "eleven" || service == "higgsfield" || service == "higgsfield-api" || service == "openai";
        var row = new UsageRow { Label = label, RemainingPercent = amount ? null : (double?)value, Remaining = amount ? (double?)value : null, Limit = limit, ResetsAt = reset };
        return journal.Observe(service,new UsageSnapshot { Plan = plan, Rows = new List<UsageRow> { row } },at,source,at);
    }
    private static List<UsageNotice> Train(UsageJournal journal, string source = "api", string label = "주간 한도", string service = "codex", double? limit = null)
    {
        var notices = new List<UsageNotice>();
        for (int hour = 0; hour <= 96; hour++) notices.AddRange(Put(journal,Start.AddHours(hour),100-hour,Reset,source,label,service,limit));
        return notices;
    }
    private static UsageAnalysis Read(UsageJournal journal) { return journal.GetAnalysis("codex","주간 한도","api",End); }
    private static UsageJournal.AnalysisStateDocument Series(UsageJournal journal, string path)
    {
        journal.WriteRecoveryBackup(path);
        return new JavaScriptSerializer().Deserialize<UsageJournal.JournalDocument>(File.ReadAllText(path)).Analyses.Single();
    }

    private static void Accuracy()
    {
        var journal = new UsageJournal(null,true); var notices = Train(journal); var result = Read(journal);
        Near(result.AveragePerDay,24,"Four full days produce actual daily consumption");
        Check(result.SampleDays == 4 && Math.Abs(result.ObservedHours-96)<.001,"Local completed dates and observed hours are reported");
        Check(result.Unit == "%p" && result.Status == "분석 가능" && result.BeforeReset,"Percentage points stay separate from credit units");
        Check(result.PredictedEmptyAt == End.AddHours(4),"Remaining/rate predicts four hours until exhaustion");
        Check(notices.Count(n => n.Kind == "forecast") == 2,"A known risk and its later 24-hour escalation each warn once after cooldown");
        Check(notices.Count(n => n.Kind == "low") == 1 && notices.Count(n => n.Kind == "critical") == 1,"Crossing distinct stages generates only their own notices");
        Check(Put(journal,End.AddMinutes(1),0,Reset).Single().Kind == "empty","Zero balance has a distinct one-time exhausted notice");
        Check(Put(journal,End.AddMinutes(2),0,Reset).Count == 0,"Repeated zero remains quiet");
        Check(journal.GetAnalysis("codex","주간 한도","api",End.AddHours(1)).Status == "오래된 기록","Stale balances never project an exhaustion time");
        Check(journal.GetAnalysis("codex","주간 한도","code",End).AveragePerDay == null,"Another source cannot read the trained series");
        journal = new UsageJournal(null,true);
        // Only twelve hours per date are observed. Unknown overnight hours are
        // excluded, and a working-time rate must not be multiplied by 24.
        for (int day=0;day<4;day++) for (int hour=0;hour<=12;hour++) Put(journal,Start.AddDays(day).AddHours(hour),100-day*13-hour,Reset);
        Put(journal,End,48,Reset);
        result=Read(journal); Near(result.AveragePerDay,12,"Half-day coverage uses observed daily consumption, not 24-hour extrapolation");
        Check(result.SampleDays==4 && result.ObservedHours==48,"Long overnight gaps add neither usage nor covered time");
        journal = new UsageJournal(null,true);
        for(int day=0;day<=4;day++) Put(journal,Start.AddDays(day),100-day*10,Reset);
        Check(Read(journal).AveragePerDay==null && Read(journal).Status=="기록 부족","Daily isolated snapshots do not fabricate 24-hour coverage");
        journal = new UsageJournal(null,true);
        for(int hour=0;hour<=48;hour++) Put(journal,End.AddHours(-48+hour),90-hour,Reset);
        Check(Read(journal).AveragePerDay.HasValue && !Read(journal).PredictedEmptyAt.HasValue,"A two-day average may display while forecasts still collect evidence");
    }

    private static void Boundaries(string directory)
    {
        var journal=new UsageJournal(null,true); Train(journal); int count=journal.GetHistory().Count;
        journal.ForgetService("codex");
        Check(!Read(journal).AveragePerDay.HasValue && journal.GetHistory().Count==count,"Disconnect keeps history but starts a separate analytics epoch");
        Put(journal,End.AddMinutes(1),99,Reset);
        Check(!journal.GetAnalysis("codex","주간 한도","api",End.AddMinutes(1)).AveragePerDay.HasValue,"New connection never bridges old readings");
        journal=new UsageJournal(null,true); Train(journal);
        Put(journal,End.AddMinutes(1),3,Reset,"api","주간 한도","codex",null,"Max");
        Check(!Read(journal).AveragePerDay.HasValue,"A plan change separates even percentage-only quotas");
        journal=new UsageJournal(null,true); Train(journal,"api","구독 크레딧","eleven",100);
        Put(journal,End.AddMinutes(1),3,Reset,"api","구독 크레딧","eleven",200);
        Check(!journal.GetAnalysis("eleven","구독 크레딧","api",End.AddMinutes(1)).AveragePerDay.HasValue,"A changed absolute limit starts a new series");
        journal=new UsageJournal(null,true); Train(journal); journal.PauseAnalysis();
        Check(Read(journal).AveragePerDay.HasValue && !Read(journal).PredictedEmptyAt.HasValue,"Pausing retains the daily totals but suspends forecasts");
        var before=Series(journal,Path.Combine(directory,"paused-before.json"));
        Put(journal,End.AddMinutes(30),1,Reset);
        var after=Series(journal,Path.Combine(directory,"paused-after.json"));
        Near(after.Days.Sum(d=>d.Consumed),before.Days.Sum(d=>d.Consumed),"The first post-pause observation cannot consume through the inactive period");
        journal.ClearHistory(); Check(!Read(journal).AveragePerDay.HasValue && journal.GetHistory().Count==0,"Clearing history also clears analytics");
        journal=new UsageJournal(null,true); journal.SetAnalysisOwner(new string('a',64)); Train(journal); count=journal.GetHistory().Count;
        journal.SetAnalysisOwner(new string('a',64)); Check(Read(journal).AveragePerDay.HasValue,"Same owner preserves accumulated analysis");
        journal.SetAnalysisOwner(new string('b',64)); Check(!Read(journal).AveragePerDay.HasValue && journal.GetHistory().Count==count,"Different Nephylo account clears analysis only");
        Check(journal.CheckResetReminders(Reset,new[] { "codex" }).Count==0,"Owner changes cannot deliver previous owner's pending schedules");
        bool invalid=false; try { journal.SetAnalysisOwner("person@example.com"); } catch(ArgumentException) { invalid=true; }
        Check(invalid,"Owner boundary accepts only opaque canonical hashes");
        journal=new UsageJournal(null,true); Put(journal,End.AddMinutes(-1),10,Reset);
        journal.SetAnalysisOwner(new string('a',64));
        Check(Put(journal,End,9,Reset).Count==0,"First owner adoption preserves legacy stage deduplication");
        Check(journal.CheckResetReminders(Reset,new[] { "codex" }).Count==1,"First owner adoption preserves the legacy scheduled reset");
        journal=new UsageJournal(null,true);
        for(int hour=0;hour<=96;hour++) Put(journal,Start.AddHours(hour),hour<36?100-hour:120-hour,Reset);
        var analysis=Read(journal); Check(analysis.SampleDays==3,"A refill excludes its uncertain day from the average");
        Near(analysis.AveragePerDay,24,"Refills are not negative consumption or counted as free usage");
        journal=new UsageJournal(null,true);
        DateTimeOffset reset=Start.AddHours(36);
        for(int hour=0;hour<=96;hour++) Put(journal,Start.AddHours(hour),hour<36?100-hour:136-hour,hour<36?reset:Reset);
        analysis=Read(journal); Check(analysis.SampleDays==3,"Reset transition day is excluded while other cycles remain usable");
        Near(analysis.AveragePerDay,24,"Reset replenishment is never added to consumption");
    }

    private static void Eligibility()
    {
        var journal=new UsageJournal(null,true); Train(journal,"manual");
        Check(journal.GetAnalysis("codex","주간 한도","manual",End).Status=="수동 기록" && journal.GetRecentNotices().All(n=>n.Kind!="forecast"),"Manual data may summarize but cannot trigger forecast notifications");
        journal=new UsageJournal(null,true); Train(journal,"code","5시간 한도","claude");
        Check(journal.GetAnalysis("claude","5시간 한도","code",End).Status=="짧은 한도" && journal.GetRecentNotices().All(n=>n.Kind!="forecast"),"Short rolling limits are not extrapolated from daily totals");
        journal=new UsageJournal(null,true); Train(journal,"code","90분 한도","claude");
        Check(journal.GetAnalysis("claude","90분 한도","code",End).Status=="짧은 한도","Minute-based short windows also suppress daily-rate forecasts");
        journal=new UsageJournal(null,true); Train(journal,"api","구독 크레딧","eleven",100);
        var analysis=journal.GetAnalysis("eleven","구독 크레딧","api",End); Near(analysis.AveragePerDay,24,"Credit-based quotas preserve exact credit units"); Check(analysis.Unit=="credits","Credit units are explicit");
        journal=new UsageJournal(null,true); Train(journal,"api","설정 예산 잔여","openai",100);
        analysis=journal.GetAnalysis("openai","설정 예산 잔여","api",End); Check(analysis.Unit=="USD" && analysis.BeforeReset,"Only structured OpenAI budget remaining supports budget exhaustion");
        Put(journal,End.AddMinutes(1),4,Reset,"api","이번 달 비용","openai");
        Check(journal.GetAnalysis("openai","이번 달 비용","api",End.AddMinutes(1)).Status=="지원하지 않는 항목","Spend is not mistaken for a spendable wallet balance");
        journal=new UsageJournal(null,true); var options=journal.Options; options.ForecastEnabled=false; options.CriticalThresholdPercent=0; journal.SaveOptions(options);
        var notices=Train(journal); Check(notices.All(n=>n.Kind!="forecast" && n.Kind!="critical") && Read(journal).BeforeReset,"Alert toggles do not hide the forecast calculation");
        options.HistoryEnabled=false; journal.SaveOptions(options); Put(journal,End.AddMinutes(1),3,Reset);
        Check(Read(journal).Status=="이력 기록 꺼짐" && !Read(journal).AveragePerDay.HasValue,"History-off mode performs no analysis presentation");
        options.HistoryEnabled=true; journal.SaveOptions(options); Check(Read(journal).AveragePerDay.HasValue && !Read(journal).PredictedEmptyAt.HasValue,"Re-enabling retains prior totals but requires a new baseline");
        options.CriticalThresholdPercent=50; options.LowThresholdPercent=20; journal.SaveOptions(options); Check(journal.Options.CriticalThresholdPercent==19,"Critical threshold cannot overtake the low threshold");
        journal=new UsageJournal(null,true);
        for(int hour=0;hour<=96;hour++) Put(journal,Start.AddHours(hour),80,Reset);
        Check(Read(journal).AveragePerDay==0 && Read(journal).Status=="소비 변화 없음" && !Read(journal).PredictedEmptyAt.HasValue,"Zero consumption has no infinite or invented exhaustion date");
    }

    private static void Persistence(string directory)
    {
        string path=Path.Combine(directory,"persist"); var journal=new UsageJournal(path,false,true); journal.SetAnalysisOwner(new string('a',64));
        Train(journal); journal.FlushAsync().GetAwaiter().GetResult(); journal=new UsageJournal(path,false);
        Near(Read(journal).AveragePerDay,24,"Daily aggregates survive restart independently of chart compaction");
        Check(Put(journal,End.AddMinutes(1),3,Reset).All(n=>n.Kind!="forecast" && n.Kind!="critical"),"Forecast and stage dedup survive restart");
        journal.SetAnalysisOwner(new string('a',64)); Check(Read(journal).AveragePerDay.HasValue,"Persistent owner is stable after restart");
        string file=Path.Combine(path,"usage-journal.json"); var document=new JavaScriptSerializer().Deserialize<UsageJournal.JournalDocument>(File.ReadAllText(file));
        Check(document.Version==1 && document.Analyses.Single().Days.Count<=9,"Version-one format remains readable with bounded additive fields");
        document.Analyses=null; document.AnalysisOwner=null; File.WriteAllText(file,new JavaScriptSerializer().Serialize(document));
        journal=new UsageJournal(path,false); Check(journal.GetHistory().Count>0 && !Read(journal).AveragePerDay.HasValue,"Legacy raw history is retained without pretending it is complete analytics evidence");
        File.WriteAllText(file,"{\"Version\":1,\"Options\":{\"LowThresholdPercent\":20,\"LowEnabled\":true,\"HistoryEnabled\":true},\"Points\":[],\"States\":[]}");
        journal=new UsageJournal(path,false); Check(journal.Options.ForecastEnabled && journal.Options.CriticalThresholdPercent==10,"Old settings acquire forecast and critical defaults");
        Train(journal); double? previous=Read(journal).AveragePerDay;
        using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
            bool failed=false; try { journal.ClearHistory(); } catch(IOException) { failed=true; }
            Check(failed && Read(journal).AveragePerDay==previous,"Failed atomic mutation rolls back daily aggregates with the existing journal");
        }
        journal=new UsageJournal(null,true); var at=End.AddHours(-2); var deadline=at.AddMinutes(1);
        Put(journal,at,50,deadline); var notices=Put(journal,deadline.AddSeconds(1),100,Reset);
        Check(notices.Count(n=>n.Kind=="reset")==1 && notices.Count(n=>n.Kind=="reset-confirmed")==1,"Schedule and actual reset confirmation remain distinct");
        Check(Put(journal,deadline.AddMinutes(1),99,Reset).All(n=>n.Kind!="reset-confirmed"),"Repeated post-reset readings cannot repeat confirmation");
        journal=new UsageJournal(null,true); Put(journal,at,50,deadline); notices=Put(journal,deadline.AddSeconds(1),49,Reset);
        Check(notices.All(n=>n.Kind!="reset-confirmed"),"A new deadline without replenished usage is not falsely confirmed");
        journal=new UsageJournal(null,true); Put(journal,at,50,deadline);
        Put(journal,deadline.AddSeconds(1),50,deadline);
        Check(Put(journal,deadline.AddMinutes(1),100,Reset).Count(n=>n.Kind=="reset-confirmed")==1,"A delayed fresh API response still confirms reset after an expired old snapshot");
        journal=new UsageJournal(null,true); Train(journal);
        journal.Observe("codex",new UsageSnapshot { Rows=new List<UsageRow>() },End.AddMinutes(1),"api",End.AddMinutes(1));
        Check(journal.GetAnalysis("codex","주간 한도","api",End.AddMinutes(1)).AveragePerDay.HasValue && !journal.GetAnalysis("codex","주간 한도","api",End.AddMinutes(1)).PredictedEmptyAt.HasValue,"A disappeared row keeps its history but cannot project an old balance");
    }

    private static List<UsageNotice> Workdays(UsageJournal journal, double[] consumed, int hours, double remaining)
    {
        var notices = new List<UsageNotice>();
        double balance = remaining+consumed.Sum();
        for(int day=0;day<consumed.Length;day++) {
            for(int hour=0;hour<=hours;hour++)
                notices.AddRange(Put(journal,End.AddDays(day-consumed.Length).AddHours(hour),balance-consumed[day]*hour/hours,Reset,"api","구독 크레딧","eleven",1000));
            balance -= consumed[day];
        }
        notices.AddRange(Credit(journal,End,remaining));
        return notices;
    }
    private static List<UsageNotice> Credit(UsageJournal journal, DateTimeOffset at, double remaining)
    { return Put(journal,at,remaining,Reset,"api","구독 크레딧","eleven",1000); }
    private static UsageAnalysis Credits(UsageJournal journal, DateTimeOffset at)
    { return journal.GetAnalysis("eleven","구독 크레딧","api",at); }

    private static void WorkdayCoverage()
    {
        var journal = new UsageJournal(null,true);
        Workdays(journal,new double[] { 6,6,6,6 },6,30);
        var analysis = Credits(journal,End);
        Near(analysis.AveragePerDay,6,"Six-hour laptop days produce a real observed daily mean");
        Check(analysis.ForecastSampleDays==4 && analysis.ForecastObservedHours==24 && analysis.PredictedEmptyAt.HasValue,"Four workdays qualify without keeping a laptop on for twelve hours");
        Check(analysis.RequiredSampleDays==3 && analysis.RequiredDailyHours==4 && analysis.RequiredObservedHours==12 && analysis.RequiredCollectionHours==72,"UI receives all forecast prerequisites");
        Near(analysis.RecommendedPerDay,10,"Reset budget divides the fresh remaining balance by remaining calendar days");
        Check(analysis.CollectionHours==96 && analysis.ObservedHours==24,"Elapsed collection time is distinct from observed coverage");
        journal = new UsageJournal(null,true);
        Workdays(journal,new double[] { 3,3,3,3 },1,30);
        analysis=Credits(journal,End);
        Near(analysis.AveragePerDay,3,"A one-hour completed day provides a limited summary without extrapolation");
        Check(analysis.SampleDays==4 && analysis.ForecastSampleDays==0 && !analysis.PredictedEmptyAt.HasValue,"Short workdays summarize but do not qualify as forecast evidence");
        Near(analysis.RecommendedPerDay,10,"Arithmetic reset budget remains available while history is insufficient");
        journal = new UsageJournal(null,true);
        Workdays(journal,new double[] { 6,6,6,6 },6,100);
        for(int hour=1;hour<=4;hour++) Credit(journal,End.AddHours(hour),100-hour*5);
        analysis=Credits(journal,End.AddHours(4));
        Near(analysis.AveragePerDay,6,"Today's incomplete date cannot lower or raise the completed-day average");
        Near(analysis.TrendPerDay,20,"Today's already-observed higher consumption raises mature trend without 24-hour extrapolation");
        Check(analysis.TodaySpike && analysis.TodayObservedHours==4 && analysis.TodayConsumed==20 && analysis.ObservedHours==28,"Coverage progress includes real observations today");
        Check(analysis.ForecastSampleDays==4 && analysis.ForecastObservedHours==24,"Today cannot satisfy a completed-day prerequisite");
        journal = new UsageJournal(null,true);
        Workdays(journal,new double[] { 4,8,12,16 },6,30);
        analysis=Credits(journal,End);
        Near(analysis.AveragePerDay,10,"Summary remains an understandable equally weighted observed-day mean");
        Near(analysis.TrendPerDay,240.0/22,"Forecast gives more weight to recent completed dates");
        Check(analysis.EarliestEmptyAt<analysis.PredictedEmptyAt && analysis.LatestEmptyAt>analysis.PredictedEmptyAt && analysis.RiskLevel==1,"Variable days expose a reference range and possible-before-reset risk");
        Check(analysis.EarliestEmptyAt<Reset && analysis.LatestEmptyAt>Reset,"A range crossing reset cannot be described as wholly before reset");
        Check(!Credits(journal,End.AddMinutes(-1)).RecommendedPerDay.HasValue && !Credits(journal,End.AddMinutes(-1)).PredictedEmptyAt.HasValue,"Future cached observations cannot produce budgets or predictions for an earlier clock");
        Check(!Credits(journal,End.AddHours(1)).RecommendedPerDay.HasValue,"Stale data cannot suggest a daily budget");
        journal.PauseAnalysis(); Check(!Credits(journal,End).RecommendedPerDay.HasValue,"Paused ownership or history cannot suggest an old budget");
        journal=new UsageJournal(null,true); Put(journal,End,50,Reset,"manual");
        Check(!Read(journal).RecommendedPerDay.HasValue,"Manual values have no automated recommended allowance");
        journal=new UsageJournal(null,true); Put(journal,End,50,Reset,"api","5시간 한도");
        Check(!journal.GetAnalysis("codex","5시간 한도","api",End).RecommendedPerDay.HasValue,"Short rolling limits do not expose misleading daily allowances");
        journal=new UsageJournal(null,true);
        Check(journal.GetAnalysis("codex","5시간 한도","api",End).Status=="짧은 한도","Unsupported short windows explain themselves before the first sample");
        Check(journal.GetAnalysis("higgsfield","크레딧","manual",End).Status=="수동 기록","Manual entries explain their lack of forecasts before collection");
        // Three four-hour days are enough even if a provider only produces a
        // fresh measurement every two hours. Unknown fourth date is not zero.
        double balance=100;
        foreach(int day in new[] { -4,-2,-1 }) {
            foreach(int hour in new[] { 0,2,4 }) Put(journal,End.AddDays(day).AddHours(hour),balance-hour,Reset);
            balance-=4;
        }
        Put(journal,End,balance,Reset);
        analysis=Read(journal);
        Check(analysis.ForecastSampleDays==3 && analysis.ForecastObservedHours==12 && analysis.PredictedEmptyAt.HasValue,"Exactly three qualifying workdays and twelve observed hours can forecast");
        Near(analysis.AveragePerDay,4,"A missing date is neither zero usage nor a fabricated observation");
        journal=new UsageJournal(null,true); Workdays(journal,new double[] { 6,6,6,6 },6,100);
        Credit(journal,End.AddHours(1),50);
        Check(!Credits(journal,End.AddHours(1)).TodaySpike && Credits(journal,End.AddHours(1)).TrendPerDay==6,"A one-hour burst does not extrapolate a mature daily trend");
        journal=new UsageJournal(null,true); Workdays(journal,new double[] { 0,0,0,40 },6,30);
        analysis=Credits(journal,End);
        Check(analysis.EarliestEmptyAt.HasValue && !analysis.LatestEmptyAt.HasValue && analysis.RiskLevel==1,"High variation permits an open-ended latest estimate rather than inventing certainty");
    }

    private static void ForecastTransitions(string directory)
    {
        var journal = new UsageJournal(null,true);
        var options=journal.Options; options.ForecastEnabled=false; journal.SaveOptions(options);
        Workdays(journal,new double[] { 4,8,12,16 },6,30);
        options.ForecastEnabled=true; journal.SaveOptions(options);
        Check(Credit(journal,End.AddMinutes(1),30).Count(n=>n.Kind=="forecast")==1,"The initial possible-risk state generates one forecast notice");
        Check(Credit(journal,End.AddHours(1),15).All(n=>n.Kind!="forecast"),"A stronger risk within six hours respects cooldown");
        Check(Credits(journal,End.AddHours(1)).RiskLevel==2,"The entire range before reset is a distinct higher risk");
        Check(Credit(journal,End.AddHours(6).AddMinutes(1),14).Count(n=>n.Kind=="forecast")==1,"Persistent stronger risk is delivered once cooldown ends");
        Check(Credit(journal,End.AddHours(7),4).All(n=>n.Kind!="forecast") && Credits(journal,End.AddHours(7)).RiskLevel==3,"A whole range within 24 hours is urgent but still respects cooldown");
        Check(Credit(journal,End.AddHours(12).AddMinutes(1),3).Count(n=>n.Kind=="forecast")==1,"An urgent escalation can notify after another six hours");
        Check(Credit(journal,End.AddHours(13),2).All(n=>n.Kind!="forecast"),"Repeated same-level urgent readings stay quiet");
        Credit(journal,End.AddHours(14),200);
        Check(Credits(journal,End.AddHours(14)).RiskLevel==0,"A refill can produce an observed genuine recovery");
        Check(Credit(journal,End.AddHours(15),3).All(n=>n.Kind!="forecast"),"Recovery does not bypass cooldown");
        Check(Credit(journal,End.AddHours(18).AddMinutes(1),2).Count(n=>n.Kind=="forecast")==1,"Recovery followed by renewed risk can notify even at the same former severity");
        Check(Credit(journal,End.AddHours(19),0).All(n=>n.Kind!="forecast"),"An exhausted balance uses its empty notice instead of predicting depletion");

        string path=Path.Combine(directory,"forecast-policy");
        journal=new UsageJournal(path,false); options=journal.Options; options.ForecastEnabled=false; journal.SaveOptions(options);
        Workdays(journal,new double[] { 4,8,12,16 },6,30);
        options.ForecastEnabled=true; journal.SaveOptions(options); Credit(journal,End.AddMinutes(1),30);
        journal=new UsageJournal(path,false);
        Check(Credit(journal,End.AddHours(1),15).All(n=>n.Kind!="forecast"),"Restart preserves cooldown against a stronger risk");
        Check(Credit(journal,End.AddHours(6).AddMinutes(1),14).Count(n=>n.Kind=="forecast")==1,"Restart preserves eligibility for escalation after cooldown");
        string file=Path.Combine(path,"usage-journal.json");
        var serializer=new JavaScriptSerializer(); var document=serializer.Deserialize<UsageJournal.JournalDocument>(File.ReadAllText(file));
        var state=document.Analyses.Single(); state.ForecastPolicyVersion=0; state.ForecastAlerted=true; state.ForecastAlertedRisk=0; state.ForecastLastAlertAt=null; state.ForecastRecovered=false;
        File.WriteAllText(file,serializer.Serialize(document)); journal=new UsageJournal(path,false);
        Check(Credit(journal,End.AddHours(13),3).All(n=>n.Kind!="forecast"),"A legacy once-per-cycle warning migrates as strongest prior risk and never replays on upgrade");
        var migrated=Series(journal,Path.Combine(directory,"migrated.json"));
        Check(migrated.ForecastPolicyVersion==2 && migrated.ForecastAlertedRisk==3 && migrated.ForecastLastAlertAt!=null,"Migrated warning state is persisted in the new versioned additive policy fields");

        path=Path.Combine(directory,"delayed-dispatch");
        journal=new UsageJournal(path,false); options=journal.Options; options.ForecastEnabled=false; journal.SaveOptions(options);
        Workdays(journal,new double[] { 4,8,12,16 },6,30);
        options.ForecastEnabled=true; journal.SaveOptions(options);
        var snapshot=new UsageSnapshot { Plan="Pro", Rows=new List<UsageRow> { new UsageRow { Label="구독 크레딧", Remaining=30, Limit=1000, ResetsAt=Reset } } };
        Check(journal.Observe("eleven",snapshot,End.AddMinutes(1),"api",End.AddMinutes(4)).Count(n=>n.Kind=="forecast")==1,"A delayed fresh provider receipt dispatches with the actual later clock");
        journal=new UsageJournal(path,false);
        var dispatch=Series(journal,Path.Combine(directory,"restored-dispatch.json"));
        Check(DateTimeOffset.Parse(dispatch.ForecastLastAlertAt,CultureInfo.InvariantCulture)==End.AddMinutes(4),"Restart preserves dispatch time even when later than the source observation");
        Check(Credit(journal,End.AddHours(6).AddMinutes(1),15).All(n=>n.Kind!="forecast"),"Restart cannot shorten cooldown by the provider's delivery lag");
        Check(Credit(journal,End.AddHours(6).AddMinutes(4),14).Count(n=>n.Kind=="forecast")==1,"Escalation becomes available exactly six hours after actual dispatch");
        journal.PauseAnalysis();
        Check(Credit(journal,End.AddHours(13),13).All(n=>n.Kind!="forecast"),"An unknown disconnected interval is not a recovery that rearms the same risk");
        Credit(journal,End.AddHours(14),200); journal=new UsageJournal(path,false);
        Check(Credit(journal,End.AddHours(15),12).Count(n=>n.Kind=="forecast")==1,"A real recovery survives restart and permits renewed danger after cooldown");
    }

    public static int Main()
    {
        string directory=Path.Combine(Path.GetTempPath(),"Nephylo-AnalyticsTests-"+Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(directory); Accuracy(); Boundaries(directory); Eligibility(); Persistence(directory); WorkdayCoverage(); ForecastTransitions(directory); Console.WriteLine("PASS: "+checks+" analytics checks (daily consumption, gaps, resets, epochs, stages, persistence, eligibility, coverage, weighted range, budgets, forecast transitions)."); return 0; }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
    }
}
