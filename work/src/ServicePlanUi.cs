using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        // Compatibility name for existing feature boundaries. All local features
        // are free, including offline use; this never reads an entitlement.
        private bool ProFeaturesAvailable { get { return true; } }
        private string EffectiveTheme(string requested) { return requested; }
        private string ThemeForSettingsSave(string requested) { return requested; }
        private void ApplyRequestedTheme(string requested) { WidgetPalette.SetTheme(EffectiveTheme(requested)); RefreshDashboardIfOpen(); }
        private ServicePlan servicePlan;
        private string planStorageError;
        private Window planWindow;
        private Action refreshPlanControls;
        private bool IsServiceActive(string id) { return ServicePlan.IsSupported(id); }

        private bool HasCodexConnection()
        {
            if (!String.IsNullOrEmpty(settings.CodexWslDistribution)) return true;
            if (demo || (codexView.Snapshot != null && codexView.LastSuccess.HasValue)) return true;
            string home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return File.Exists(Path.Combine(home,"auth.json"));
        }

        private string codexCandidatePath, codexCandidateDistribution;
        private DateTimeOffset codexCandidateExpires;
        private bool codexCandidateAvailable;

        // An installed app can keep its login outside auth.json. Keep installation
        // discovery separate from connected-card visibility and off the UI thread.
        private bool HasCurrentCodexCandidate(DateTimeOffset now)
        {
            return now < codexCandidateExpires &&
                String.Equals(codexCandidatePath,settings.CodexPath,StringComparison.Ordinal) &&
                String.Equals(codexCandidateDistribution,settings.CodexWslDistribution,StringComparison.Ordinal);
        }

        private bool ShouldAttemptCodexRead(DateTimeOffset now, bool manual)
        {
            return HasCodexConnection() || manual ||
                !HasCurrentCodexCandidate(now) || codexCandidateAvailable;
        }

        private async Task<bool> CanReadCodexCandidateAsync(bool manual)
        {
            if (HasCodexConnection()) return true;
            if (!manual && HasCurrentCodexCandidate(DateTimeOffset.UtcNow)) return codexCandidateAvailable;
            string path = settings.CodexPath, distribution = settings.CodexWslDistribution;
            bool available = await Task.Run(() => {
                try { return !String.IsNullOrEmpty(CodexService.FindExecutable(path)); }
                catch (ConnectionException) { return false; }
            });
            codexCandidatePath = path; codexCandidateDistribution = distribution;
            codexCandidateAvailable = available;
            codexCandidateExpires = DateTimeOffset.UtcNow.AddMinutes(5);
            return available;
        }

        private void InitializeServicePlan()
        {
            var configured = new List<string>();
            if (!demo && HasCodexConnection()) configured.Add("codex");
            if (!String.IsNullOrWhiteSpace(secret)) configured.Add("eleven");
            foreach (string id in new[] { "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" }) if (IsExtraShown(id) && IsExtraReady(id)) configured.Add(id);
            servicePlan = new ServicePlan(SettingsStore.DataDirectory, demo, settings.ServiceOrder, configured);
            // Keep old single-service selection without rewriting the profile.
        }

        private string InactiveServiceMessage { get { return "지원하지 않는 서비스입니다."; } }
        private bool RequireActiveService(string id, ConnectionFeedback feedback)
        {
            if (IsServiceActive(id)) return true;
            Feedback(feedback,"보관된 서비스 · 현재 사용 안 함",InactiveServiceMessage,false); return false;
        }

        private bool RequireServiceConnection(string id, ConnectionFeedback feedback)
        {
            if (ServicePlan.IsSupported(id)) return true;
            Feedback(feedback,"연결을 확인해 주세요","지원하지 않는 서비스입니다.",true); return false;
        }

        private void SelectFreeService(string id)
        {
            if (IsServiceConnectionPending(id) || ViewFor(id).Busy) throw new InvalidOperationException("조회 또는 로그인을 마친 뒤 전환해 주세요.");
            CommitVerifiedServiceSelection(id);
        }

        // Called only after an explicit connection check or validated manual save succeeds.
        // Polling and visibility continue to use IsServiceActive throughout verification.
        private void CommitVerifiedServiceSelection(string id)
        {
            if (demo || IsServiceActive(id)) return;
            servicePlan.Select(id);
            planStorageError = null;
            pendingUsageNotices.RemoveAll(item => item.ServiceId != id);
            UpdateServiceVisibility();
            if (refreshPlanControls != null) refreshPlanControls();
            RefreshConnectionEditor(id);
            nextRefresh = DateTimeOffset.MinValue;
        }

        private async Task<UsageSnapshot> VerifyServiceForSelection(string id)
        {
            if (id == "codex") return await codex.ReadAsync(settings.CodexPath,settings.CodexWslDistribution);
            if (id == "eleven") return await eleven.ReadAsync(secret);
            if (id == "openai") return await openAi.ReadAsync(openAiKey,extras.OpenAiMonthlyBudgetUsd);
            if (id == "higgsfield") {
                if (!higgsfieldSettings.WebConnected) return null;
                return await higgsfield.ReadWebAsync(higgsfieldSettings.CliPath,higgsfieldSettings.WorkspaceId,higgsfieldLifetime.Token);
            }
            if (id == "higgsfield-api") return higgsfieldSettings.ApiBalanceUsd.HasValue && higgsfieldSettings.ApiObservedAt.HasValue ? HiggsfieldUsageService.ReadApiManual(higgsfieldSettings) : null;
            if (id == "claude" && IsSavedClaudeConfigured()) {
                try {
                    var received = ClaudeUsageService.ReadLatest(settings.ClaudeWslDistribution,settings.ClaudeWindowsConfigDirectory);
                    if (received.HasQuotaData && !received.HasExpiredWindows && DateTimeOffset.Now - received.ObservedAt <= TimeSpan.FromMinutes(30) && received.Snapshot.Rows.Any(row => row.RemainingPercent.HasValue)) return received.Snapshot;
                } catch { }
            }
            var record = GetManual(id);
            if (!record.ObservedAt.HasValue || !record.RemainingPercent.HasValue) return null;
            return new UsageSnapshot { Plan = "수동", Rows = new List<UsageRow> { new UsageRow { Label = record.Label,RemainingPercent = record.RemainingPercent,ResetsAt = record.ResetsAt } } };
        }

        private void BuildServicePlanControls(StackPanel top, Func<string> selected)
        {
            var panel = new LocalizedStackPanel();
            var title = T("",12,"#252827",FontWeights.SemiBold);
            var detail = T("",12,"#647070",FontWeights.Normal,new Thickness(0,7,0,7));
            panel.Children.Add(title); panel.Children.Add(detail);
            top.Children.Add(new LocalizedBorder { Child = panel, Background = BrushOf("#FBFBF8"), BorderBrush = BrushOf("#C8CECF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(16), Margin = new Thickness(0,0,0,14) });
            refreshPlanControls = () => {
                title.Text = demo ? "전체 서비스 미리보기" : "지원 서비스 등록 무제한";
                detail.Text = demo ? "미리보기 · 예시 데이터입니다. 실제 사용량이 아니며 PC에 저장하지 않습니다." : "Nephylo 로그인 없이 모든 기능을 무료로 이용합니다. 각 AI 서비스의 연결은 별도로 필요합니다.";
            };
            refreshPlanControls();
        }

        private void OpenSponsorship()
        {
            OpenOfficialPage("https://github.com/sponsors/jangmalza");
        }

        private void OpenPlan()
        {
            if (planWindow != null) { planWindow.Activate(); return; }
            var dialog = CreateManagementDialog("무료 이용 안내",530,620);
            planWindow = dialog;
            StackPanel footer;
            var body = ManagementBody(dialog,"모든 기능 무료","Nephylo 회원가입이나 로그인 없이 이용할 수 있습니다.",out footer);
            AddPlanCard(body,"Nephylo","무료","지원 서비스 등록 무제한\n잔량·초기화 알림 · 하루 평균 소모량 · 소진 예상\n주간 요약 · 30일 이력 · CSV 내보내기\n다크·라이트 테마");
            body.Children.Add(T("연결 정보, 설정과 사용 이력은 이 PC에 보관합니다.",12,"#252827",FontWeights.Normal,new Thickness(0,2,0,10)));
            body.Children.Add(T("무제한은 지원 서비스 수 기준이며 같은 서비스의 여러 계정을 뜻하지 않습니다. 각 AI 서비스의 구독·API 비용은 별도입니다.",11,"#647070",FontWeights.Normal,new Thickness(0,0,0,10)));
            body.Children.Add(SmallAction("사이트에서 무료 이용 안내 보기 ↗",() => OpenOfficialPage("https://nephylo.com/#pricing")));
            body.Children.Add(T("후원은 선택입니다. 후원 여부와 관계없이 모든 위젯 기능을 무료로 사용할 수 있습니다.",11,"#647070",FontWeights.Normal,new Thickness(0,16,0,4)));
            body.Children.Add(SmallAction("후원하기 ↗",OpenSponsorship));
            footer.Children.Add(ManagementAction("닫기",() => dialog.Close()));
            dialog.Closed += (s,e) => planWindow = null; dialog.Show();
        }
        private void AddPlanCard(StackPanel body, string title, string price, string detail)
        {
            var panel = ManagementSection(body,title);
            panel.Children.Add(T(price,25,"#252827",FontWeights.SemiBold,new Thickness(0,0,0,12)));
            panel.Children.Add(T(detail,12,"#647070",FontWeights.Normal));
        }
    }
}
