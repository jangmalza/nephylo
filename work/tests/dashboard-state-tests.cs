using System;
using System.Collections.Generic;
using System.Linq;
using UsageWidget;

internal static class DashboardStateTests
{
    private static int assertions;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026,10,12,12,0,0,TimeSpan.FromHours(9));
    private static void Check(bool value,string message) { assertions++; if (!value) throw new Exception(message); }
    private static ProviderView View(string id, params UsageRow[] rows)
    {
        return new ProviderView { Id = id, Source = id == "claude" ? "code" : "api", LastSuccess = Now,
            ReceiptObservedAt = id == "claude" ? (DateTimeOffset?)Now : null, ReceiptState = ProviderReceiptState.Received,
            Snapshot = new UsageSnapshot { Plan = "PRO", Rows = rows.ToList() } };
    }
    private static DashboardAssessmentState Assess(ProviderView view) { return DashboardAssessment.Assess(view,new UsageTrackingOptions(),Now); }
    public static int Main()
    {
        try {
            CheckMissingAndInvalidData(); CheckSeparateWindows(); CheckCodeFreshness(); CheckAutomaticFreshness(); CheckLowThresholds(); CheckManualValues(); CheckResetSchedule(); CheckReadOnly();
            Console.WriteLine("Dashboard state tests passed: " + assertions + " assertions. No accounts, storage, provider requests or notifications used."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckMissingAndInvalidData()
    {
        foreach (var view in new[] { (ProviderView)null, new ProviderView { Id = "codex" }, View("codex"), View("codex",(UsageRow)null) }) {
            var state = Assess(view);
            Check(state.HasUnknown && state.UnknownCount == 1 && state.UsableCount == 0 && !state.HasLow,"A provider with no confirmed metric contributes one explicit unknown, never a healthy zero-low result");
        }
        foreach (double? invalid in new double?[] { null,Double.NaN,Double.NegativeInfinity,Double.PositiveInfinity,-1,100.1 }) {
            var view = View("codex",new UsageRow { Label = "주간 한도", RemainingPercent = invalid });
            var state = Assess(view);
            Check(state.UnknownCount == 1 && state.UsableCount == 0 && !state.HasLow && state.StatusLabel == "확인 불가","Absent or invalid percentages are unknown instead of a reassuring plan label: " + invalid);
        }
        foreach (double invalid in new[] { Double.NaN,Double.NegativeInfinity,Double.PositiveInfinity,-1 }) {
            var state = Assess(View("higgsfield",new UsageRow { Label = "크레딧 잔량", Remaining = invalid }));
            Check(state.UnknownCount == 1 && !state.HasLow,"Invalid absolute balances cannot enter a low-credit count: " + invalid);
        }
        var explanatory = Assess(View("claude",new UsageRow { Label = "주간 한도 · 수신 대기", DisplayValue = "—" }));
        Check(explanatory.HasUnknown && !explanatory.HasLow,"A display-only placeholder never becomes zero usage");
    }

    private static void CheckSeparateWindows()
    {
        var shortWindow = new UsageRow { Label = "5시간 한도", RemainingPercent = 2, ResetsAt = Now.AddMinutes(-1) };
        var weekly = new UsageRow { Label = "주간 한도", RemainingPercent = 3, ResetsAt = Now.AddDays(3) };
        var view = View("claude",shortWindow,weekly); view.ReceiptState = ProviderReceiptState.ResetWaiting;
        var state = Assess(view);
        Check(state.LowCount == 1 && state.UnknownCount == 1 && state.UsableCount == 1,"An expired short quota and current weekly quota retain separate meanings");
        Check(state.Stale && state.HasLow && state.HasUnknown,"Mixed quota windows can simultaneously need a fresh receipt and have a confirmed low balance");
        Check(!DashboardAssessment.IsUsableRow(view,shortWindow,Now) && DashboardAssessment.IsUsableRow(view,weekly,Now),"Provider-wide ResetWaiting does not suppress a valid weekly allowance");
        Check(state.NextReset == weekly.ResetsAt && state.ResetCount == 0,"Expired reset deadlines do not enter the upcoming reset summary");
        view.Error = "fixture read failed"; state = Assess(view);
        Check(state.UnknownCount == 2 && state.LowCount == 0 && state.UsableCount == 0 && state.Stale,"A failed fetch marks both remembered windows unknown rather than current low balances");
        Check(!state.NextReset.HasValue && state.ResetCount == 0,"A failed response's old reset schedule is not promoted as a current forecast");
        view.Error = null; weekly.Label = "주간 한도 · 수신 대기";
        Check(Assess(view).UnknownCount == 2,"A row awaiting a new quota receipt remains unknown even if numeric cache fields remain");
    }

    private static void CheckCodeFreshness()
    {
        var view = View("claude",new UsageRow { Label = "주간 한도", RemainingPercent = 10, ResetsAt = Now.AddDays(1) });
        foreach (var receipt in new[] { ProviderReceiptState.Waiting,ProviderReceiptState.QuotaUnavailable }) {
            view.ReceiptState = receipt; var state = Assess(view);
            Check(state.UnknownCount == 1 && state.LowCount == 0 && state.UsableCount == 0,"Cached numbers cannot bypass Claude receipt availability: " + receipt);
        }
        view.ReceiptState = ProviderReceiptState.Received;
        view.ReceiptObservedAt = Now.AddMinutes(-30);
        Check(Assess(view).LowCount == 1,"The exact 30-minute freshness boundary still permits the last confirmed receipt");
        view.ReceiptObservedAt = Now.AddMinutes(-30).AddTicks(-1); var old = Assess(view);
        Check(old.UnknownCount == 1 && old.LowCount == 0 && old.Stale,"A Code receipt older than 30 minutes is unknown and explicitly stale");
        view.ReceiptObservedAt = null; view.LastSuccess = Now;
        Check(Assess(view).LowCount == 1,"A confirmed last-success timestamp is used when the explicit receipt timestamp is absent");
        view.LastSuccess = null;
        Check(Assess(view).HasUnknown && !Assess(view).HasLow,"An undated Code balance is not represented as a current allowance");
        view.Source = "code:win:" + new string('a',64); view.LastSuccess = Now.AddHours(-1);
        Check(Assess(view).HasUnknown && !Assess(view).HasLow,"A namespaced local Code source retains the same freshness requirement");
        view.LastSuccess = Now.AddSeconds(1);
        Check(Assess(view).HasUnknown && Assess(view).Stale,"A Code receipt from a future clock time is not current evidence");
    }

    private static void CheckAutomaticFreshness()
    {
        var view = View("codex",new UsageRow { Label = "주간 한도", RemainingPercent = 10, ResetsAt = Now.AddHours(1) });
        foreach (string source in new[] { "api", "cli" }) {
            view.Source = source; view.LastSuccess = Now.AddMinutes(-10);
            Check(Assess(view).HasLow && !Assess(view).HasUnknown,"An automatic snapshot at exactly ten minutes is still current: " + source);
            view.LastSuccess = Now.AddMinutes(-10).AddTicks(-1); var stale = Assess(view);
            Check(stale.UnknownCount == 1 && stale.LowCount == 0 && stale.Stale && stale.StatusLabel == "이전 값","An automatic snapshot older than ten minutes is explicitly a previous value: " + source);
            Check(stale.ResetCount == 0 && !stale.NextReset.HasValue,"An old automatic snapshot's reset deadline is not advertised as freshly confirmed: " + source);
            view.LastSuccess = Now.AddTicks(1);
            Check(Assess(view).HasUnknown && Assess(view).Stale,"A future automatic observation timestamp needs confirmation: " + source);
            view.LastSuccess = null;
            Check(Assess(view).HasLow && !Assess(view).HasUnknown,"Undated legacy API snapshots remain readable without inventing or changing timestamps: " + source);
        }
    }

    private static void CheckLowThresholds()
    {
        var options = new UsageTrackingOptions { LowThresholdPercent = 20, HiggsfieldCreditsThreshold = 15, HiggsfieldApiUsdThreshold = 3.5 };
        var row = new UsageRow { Label = "구독 잔량", RemainingPercent = 20 };
        var view = View("codex",row);
        Check(DashboardAssessment.IsLowRow(view,row,options,Now),"The percent threshold is inclusive");
        row.RemainingPercent = 20.01;
        Check(!DashboardAssessment.IsLowRow(view,row,options,Now),"Low state is based on the actual percentage rather than a rounded label");
        row.RemainingPercent = 0;
        Check(DashboardAssessment.IsLowRow(view,row,options,Now),"A confirmed exhausted allowance is low");
        row.RemainingPercent = null; row.Remaining = 15;
        view = View("higgsfield",row);
        Check(DashboardAssessment.IsLowRow(view,row,options,Now),"Higgsfield credit balances use the configured absolute credit threshold");
        row.Remaining = 15.01;
        Check(!DashboardAssessment.IsLowRow(view,row,options,Now),"Credits above their own threshold are not low");
        row.Remaining = 3.5; view.Id = "higgsfield-api";
        Check(DashboardAssessment.IsLowRow(view,row,options,Now),"Higgsfield API balances use the configured USD threshold");
        row.Remaining = 3.51;
        Check(!DashboardAssessment.IsLowRow(view,row,options,Now),"USD balances are not compared against the credit threshold");
        row.Remaining = 1; view.Id = "openai";
        Check(!DashboardAssessment.IsLowRow(view,row,options,Now) && DashboardAssessment.IsUsableRow(view,row,Now),"An absolute balance without a configured threshold stays readable without inventing a low threshold");
        row.RemainingPercent = 80; row.Remaining = 1; view.Id = "higgsfield-api";
        Check(!DashboardAssessment.IsLowRow(view,row,options,Now),"When percentage and amount coexist, the percentage threshold takes precedence exactly as in the journal");
        row.RemainingPercent = 10; options.LowEnabled = false;
        Check(DashboardAssessment.IsLowRow(view,row,options,Now),"Disabling alert delivery does not hide a genuinely low dashboard balance");
        options.HiggsfieldApiUsdThreshold = null; row.RemainingPercent = null;
        Check(!DashboardAssessment.IsLowRow(view,row,options,Now) && DashboardAssessment.IsUsableRow(view,row,Now),"An unset absolute threshold disables low classification without making valid data unknown");
        row.RemainingPercent = 10; options.LowThresholdPercent = 5;
        Check(!DashboardAssessment.IsLowRow(view,row,options,Now),"Changing the configured threshold immediately changes dashboard assessment");
    }

    private static void CheckManualValues()
    {
        var row = new UsageRow { Label = "API 잔액", Remaining = 2, ResetsAt = Now.AddDays(2) };
        var view = View("higgsfield-api",row); view.Source = "manual"; view.LastSuccess = Now.AddDays(-20);
        var state = Assess(view);
        Check(state.LowCount == 1 && !state.HasUnknown && state.StatusLabel == "수동","A valid manual amount remains explicitly manual and may use its configured low threshold");
        row.ResetsAt = Now; state = Assess(view);
        Check(state.UnknownCount == 1 && state.LowCount == 0 && state.Stale && state.StatusLabel.Contains("기한 경과"),"An expired manual cycle needs reconfirmation; its cached low amount is not current");
    }

    private static void CheckResetSchedule()
    {
        var past = new UsageRow { Label = "지난 초기화", RemainingPercent = 5, ResetsAt = Now.AddTicks(-1) };
        var due = new UsageRow { Label = "현재 초기화", RemainingPercent = 5, ResetsAt = Now };
        var soon = new UsageRow { Label = "다음 초기화", RemainingPercent = 50, ResetsAt = Now.AddHours(1) };
        var day = new UsageRow { Label = "24시간", RemainingPercent = 50, ResetsAt = Now.AddHours(24) };
        var later = new UsageRow { Label = "주간", RemainingPercent = 50, ResetsAt = Now.AddHours(24).AddTicks(1) };
        var view = View("codex",past,due,soon,day,later);
        var state = Assess(view);
        Check(state.ResetCount == 2 && state.NextReset == soon.ResetsAt,"Upcoming reset counts use the precise (now, now+24h] window and the earliest confirmed deadline");
        Check(state.UnknownCount == 2 && state.UsableCount == 3,"Already elapsed deadlines remain unknown until a provider confirms a new cycle");
        soon.RemainingPercent = null;
        state = Assess(view);
        Check(state.ResetCount == 1 && state.NextReset == day.ResetsAt,"A deadline attached to an unavailable measurement cannot make the overview appear up to date");
    }

    private static void CheckReadOnly()
    {
        var row = new UsageRow { Label = "주간 한도", RemainingPercent = 10, ResetsAt = Now.AddDays(2) };
        var view = View("codex",row); var snapshot = view.Snapshot;
        var options = new UsageTrackingOptions { LowThresholdPercent = 0 };
        DashboardAssessment.Assess(view,options,Now);
        Check(Object.ReferenceEquals(view.Snapshot,snapshot) && Object.ReferenceEquals(snapshot.Rows.Single(),row)
            && row.RemainingPercent == 10 && row.ResetsAt == Now.AddDays(2) && view.LastSuccess == Now,"Assessment is read-only and never changes live snapshots, rows, or observation timestamps");
        Check(options.LowThresholdPercent == 0,"Threshold normalization for display never mutates persisted alert preferences");
    }
}
