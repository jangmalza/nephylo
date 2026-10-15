using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private UsageJournal usageJournal;
        private UsageJournal persistentUsageJournal;
        private string usageTrackingError = "";
        private bool usageTrackingMemoryFallback;
        private readonly Dictionary<string, DateTimeOffset> usageObservedAt = new Dictionary<string, DateTimeOffset>();
        private readonly List<UsageNotice> pendingUsageNotices = new List<UsageNotice>();
        private readonly DispatcherTimer noticeTimer = new DispatcherTimer();
        private bool noticeTimerInitialized;

        private string CodexUsageSource() { return UsageSourceIdentity.ForWsl("api", settings.CodexWslDistribution); }
        private string ClaudeUsageSource() {
            if (!String.IsNullOrEmpty(settings.ClaudeWslDistribution)) return UsageSourceIdentity.ForWsl("code", settings.ClaudeWslDistribution);
            string source = ClaudeUsageService.GetWindowsSourceId(settings.ClaudeWindowsConfigDirectory);
            return String.IsNullOrEmpty(source) ? "code" : "code:" + source;
        }

        private void InitializeUsageTracking()
        {
            usageTrackingMemoryFallback = false;
            usageTrackingError = "";
            if (!noticeTimerInitialized) {
                noticeTimer.Interval = TimeSpan.FromSeconds(2);
                noticeTimer.Tick += (s,e) => FlushUsageNotices();
                noticeTimerInitialized = true;
            }
            try {
                if (persistentUsageJournal == null) {
                    persistentUsageJournal = new UsageJournal(SettingsStore.DataDirectory, demo, true);
                    persistentUsageJournal.PersistenceChanged += UsagePersistenceChanged;
                }
                usageJournal = persistentUsageJournal;
                // Retain the local journal's legacy opaque owner, analysis and
                // alert state. Free use does not depend on a Nephylo account.
                usageTrackingError = usageJournal.LastSaveError ?? "";
            }
            catch {
                usageJournal = new UsageJournal(null, true);
                usageTrackingMemoryFallback = true;
                usageTrackingError = "이력 저장 폴더를 열지 못했습니다. 이번 실행의 기록만 표시합니다. 폴더 권한을 확인한 뒤 다시 실행해 주세요.";
            }
        }

        private void UsagePersistenceChanged(object sender, EventArgs args)
        {
            if (exiting || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() => {
                if (exiting || !ProFeaturesAvailable || !Object.ReferenceEquals(sender, usageJournal)) return;
                if (!usageTrackingMemoryFallback) usageTrackingError = usageJournal.LastSaveError ?? "";
                UpdateFooter();
            }));
        }

        private Task FlushUsageTrackingAsync()
        {
            return persistentUsageJournal == null ? Task.FromResult(0) : persistentUsageJournal.FlushAsync();
        }

        private void ObserveUsage(ProviderView view, string source)
        {
            if (!ProFeaturesAvailable || !IsServiceActive(view.Id) || exiting || usageJournal == null || view.Snapshot == null || !view.LastSuccess.HasValue || !String.IsNullOrEmpty(view.Error)) return;
            var now = DateTimeOffset.Now;
            // Rejected receipts must not advance the integration watermark: a
            // corrupt future timestamp would otherwise block valid later reads.
            if (view.LastSuccess.Value > now.AddMinutes(1) || view.LastSuccess.Value < now.AddMinutes(-5)) return;
            string observationKey = view.Id + "|" + source;
            DateTimeOffset previous;
            if (usageObservedAt.TryGetValue(observationKey, out previous) && previous >= view.LastSuccess.Value) return;
            try {
                QueueUsageNotices(usageJournal.Observe(view.Id, view.Snapshot, view.LastSuccess.Value, source, now));
                usageObservedAt[observationKey] = view.LastSuccess.Value;
                if (!usageTrackingMemoryFallback) usageTrackingError = usageJournal.LastSaveError ?? "";
                RefreshUsageHistoryIfOpen();
            } catch {
                usageTrackingError = "사용 이력을 저장하지 못했습니다. 저장 공간과 폴더 쓰기 권한을 확인해 주세요.";
            }
        }

        private void ForgetUsageService(string id)
        {
            ForgetUsageServiceCore(id, false);
        }

        private void ForgetAutomaticUsageService(string id)
        {
            ForgetUsageServiceCore(id, true);
        }

        private void ForgetUsageServiceCore(string id, bool preserveManual)
        {
            if (exiting) return;
            pendingUsageNotices.RemoveAll(item => item.ServiceId == id && (!preserveManual || item.Source != "manual"));
            foreach (string key in usageObservedAt.Keys.Where(key => key.StartsWith(id + "|", StringComparison.Ordinal) && (!preserveManual || key != id + "|manual")).ToList()) usageObservedAt.Remove(key);
            // Automatic no-quota receipts while Free must not open or rewrite
            // the previous Pro journal. Explicit connection changes still
            // invalidate its baseline before a later Pro restoration.
            if (preserveManual && !ProFeaturesAvailable) return;
            try {
                // Changing a connection while Free still invalidates the old Pro
                // baseline. Existing history and preferences are retained.
                var journal = ProFeaturesAvailable ? usageJournal : persistentUsageJournal;
                if (journal == null && !demo && File.Exists(Path.Combine(SettingsStore.DataDirectory,"usage-journal.json"))) {
                    persistentUsageJournal = new UsageJournal(SettingsStore.DataDirectory,false,true);
                    persistentUsageJournal.PersistenceChanged += UsagePersistenceChanged;
                    journal = persistentUsageJournal;
                }
                if (journal != null) {
                    if (preserveManual) journal.ForgetAutomaticService(id);
                    else journal.ForgetService(id);
                }
                CloseUsageAnalysisPopups();
            }
            catch { usageTrackingError = "연결 변경 후 알림 상태를 저장하지 못했습니다. 위젯을 다시 실행해 주세요."; }
            RefreshDashboardIfOpen();
        }

        private IEnumerable<string> ActiveUsageServices()
        {
            if (!ProFeaturesAvailable) yield break;
            if (IsServiceActive("codex") && !loginPending) yield return "codex";
            if (IsServiceActive("eleven") && !String.IsNullOrWhiteSpace(secret)) yield return "eleven";
            if (IsServiceActive("openai") && IsExtraShown("openai") && !String.IsNullOrWhiteSpace(openAiKey)) yield return "openai";
            if (IsServiceActive("claude") && IsExtraShown("claude") && (claudeConfigured || HasManualUsage(extras.ClaudeManual))) yield return "claude";
            if (IsServiceActive("gemini") && IsExtraShown("gemini") && HasManualUsage(extras.GeminiManual)) yield return "gemini";
            if (IsServiceActive("cursor") && IsExtraShown("cursor") && HasManualUsage(extras.CursorManual)) yield return "cursor";
            if (IsServiceActive("higgsfield") && higgsfieldSettings.WebConnected) yield return "higgsfield";
            if (IsServiceActive("higgsfield-api") && IsExtraReady("higgsfield-api")) yield return "higgsfield-api";
        }

        private void CheckUsageResetReminders()
        {
            if (!ProFeaturesAvailable || demo || exiting || usageJournal == null) return;
            try {
                var notices = usageJournal.CheckResetReminders(DateTimeOffset.Now, ActiveUsageServices());
                QueueUsageNotices(notices);
                if (notices.Count > 0) RefreshUsageHistoryIfOpen();
            }
            catch { usageTrackingError = "알림 상태를 저장하지 못했습니다. 저장 공간과 폴더 쓰기 권한을 확인해 주세요."; }
        }

        private void QueueUsageNotices(IEnumerable<UsageNotice> notices)
        {
            if (!ProFeaturesAvailable || demo || exiting) return;
            var active = new HashSet<string>(ActiveUsageServices());
            foreach (var notice in notices) {
                if (!active.Contains(notice.ServiceId)) continue;
                if (pendingUsageNotices.Count < UsageJournal.MaximumNotices) pendingUsageNotices.Add(notice);
            }
            if (pendingUsageNotices.Count > 0 && !noticeTimer.IsEnabled) noticeTimer.Start();
        }

        private void FlushUsageNotices()
        {
            noticeTimer.Stop();
            var active = new HashSet<string>(ActiveUsageServices());
            var notices = pendingUsageNotices.Where(n => active.Contains(n.ServiceId)).ToList();
            pendingUsageNotices.Clear();
            if (!ProFeaturesAvailable || exiting || demo || tray == null || notices.Count == 0) return;
            var options = usageJournal.Options;
            notices = notices.Where(n => UsageNoticeEnabled(n,options)).ToList();
            if (notices.Count == 0) return;
            string title = notices.All(n => n.Kind == "reset-late") ? "Nephylo · 놓친 초기화 안내" : notices.Count == 1 ? UsageServiceName(notices[0].ServiceId) + " · " + UsageNoticeTitle(notices[0].Kind) : "Nephylo · 잔여량 알림 " + notices.Count + "개";
            string body = UsageNoticeSummary(notices);
            title = L.Text(title); body = L.Text(body);
            if (body.Length > 230) body = body.Substring(0, 227) + "…";
            try { tray.ShowBalloonTip(8000, title, body, notices.Any(n => UsageNoticeWarning(n.Kind)) ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info); }
            catch { usageTrackingError = "Windows 알림을 표시하지 못했습니다. 위젯의 사용 이력에서 현재 기록을 확인해 주세요."; }
        }

        internal static bool UsageNoticeEnabled(UsageNotice notice, UsageTrackingOptions options)
        {
            if (notice == null || options == null) return false;
            switch (notice.Kind) {
                case "low": case "empty": return options.LowEnabled;
                case "critical": return options.LowEnabled && options.CriticalThresholdPercent > 0;
                case "forecast": return options.ForecastEnabled && options.HistoryEnabled;
                case "reset": case "reset-late": case "reset-confirmed": return options.ResetEnabled;
                default: return false;
            }
        }

        internal static bool UsageNoticeWarning(string kind)
        { return kind == "low" || kind == "critical" || kind == "empty" || kind == "forecast"; }

        internal static string UsageNoticeTitle(string kind)
        {
            switch (kind) {
                case "low": return "잔량 부족";
                case "critical": return "잔량 매우 부족";
                case "empty": return "잔량 소진";
                case "forecast": return "초기화 전 소진 예상";
                case "reset-late": return "늦게 확인한 초기화 일정";
                case "reset-confirmed": return "초기화 확인";
                case "reset": return "초기화 예정 시각";
                default: return "잔여량 알림";
            }
        }

        private void CloseUsageAnalysisPopups()
        {
            ClearInlineUsageInsights();
            foreach (var cell in compactCells.Values) {
                CloseCompactTip(cell.Button);
                if (cell.Button.ContextMenu != null) cell.Button.ContextMenu.IsOpen = false;
            }
            if (compactOverflowButton != null && compactOverflowButton.ContextMenu != null) compactOverflowButton.ContextMenu.IsOpen = false;
        }

        private void PauseUsageAnalysis()
        {
            CloseUsageAnalysisPopups();
            try { if (persistentUsageJournal != null) persistentUsageJournal.PauseAnalysis(); }
            catch { usageTrackingError = "분석 상태를 저장하지 못했습니다. 저장 공간과 폴더 쓰기 권한을 확인해 주세요."; }
        }

        internal static string UsageNoticeSummary(IList<UsageNotice> notices)
        {
            var late = notices.Where(n => n.Kind == "reset-late").ToList();
            if (late.Count > 0) {
                // One catch-up balloon for all overdue schedules, rather than a
                // series of stale individual alarms after sleep or app restart.
                string summary = "늦게 확인한 초기화 일정 " + late.Count + "개입니다. 실제 잔량은 새로고침으로 확인하세요.";
                summary += "\n" + String.Join(", ", late.Select(n => L.Text(UsageServiceName(n.ServiceId))).Distinct().Take(4));
                int other = notices.Count - late.Count;
                if (other > 0) summary += "\n외 " + other + "개 · 클릭하여 이력 확인";
                else summary += "\n전체 알림은 사용 이력에서 확인하세요.";
                return summary;
            }
            string body = String.Join("\n", notices.Take(3).Select(n => UsageServiceName(n.ServiceId) + " · " + n.Message).ToArray());
            if (notices.Count > 3) body += "\n외 " + (notices.Count - 3) + "개 · 클릭하여 이력 확인";
            return body;
        }

        private string TestUsageNotification()
        {
            if (demo) return "미리보기에서는 실제 Windows 알림을 보내지 않습니다.";
            try {
                tray.ShowBalloonTip(8000, L.Text("Nephylo · 알림 테스트"), L.Text("잔량 부족·초기화 예정 시각을 이 알림으로 알려드립니다. 실제 사용량 변경은 없습니다."), Forms.ToolTipIcon.Info);
                return "Windows에 테스트 알림을 보냈습니다. 보이지 않으면 Windows 알림·방해 금지 설정을 확인해 주세요.";
            } catch { return "알림을 표시하지 못했습니다. Windows 알림 설정을 확인해 주세요."; }
        }

        private static string UsageServiceName(string id)
        {
            switch (id) {
                case "codex": return "Codex"; case "eleven": return "ElevenLabs"; case "openai": return "OpenAI API";
                case "claude": return "Claude"; case "gemini": return "Gemini API"; case "cursor": return "Cursor";
                case "higgsfield": return "Higgsfield 웹"; case "higgsfield-api": return "Higgsfield API";
                default: return "서비스";
            }
        }

        private void SeedDemoUsageHistory()
        {
            if (!demo || usageJournal.GetHistory().Count != 0) return;
            var now = DateTimeOffset.Now;
            // Demo-only observations follow a regular cadence so the preview
            // illustrates a continuous history without bridging missing data.
            for (int sample = 360; sample >= 0; sample--) {
                double hour = sample / 3.0;
                var observed = now.AddMinutes(-sample * 20);
                usageJournal.Observe("codex", new UsageSnapshot { Plan = "PLUS", Rows = new List<UsageRow> {
                    new UsageRow { Label = "주간 한도", RemainingPercent = 86 + hour * (14.0/120), ResetsAt = codexView.Snapshot.Rows[1].ResetsAt }
                }}, observed, "api", observed);
                usageJournal.Observe("eleven", new UsageSnapshot { Plan = "CREATOR", Rows = new List<UsageRow> {
                    new UsageRow { Label = "기본 제공 크레딧", RemainingPercent = 64.2 + hour * (35.8/120), Remaining = 64200 + hour * (35800.0/120), Limit = 100000, ResetsAt = elevenView.Snapshot.Rows[0].ResetsAt }
                }}, observed, "api", observed);
            }
        }
    }
}
