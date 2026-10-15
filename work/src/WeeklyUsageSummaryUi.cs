using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private StackPanel weeklyPageRoot, weeklyCardsRoot;
        private TextBlock weeklyRange, weeklyComparisonRange, weeklyHighlights, weeklyCopyStatus, weeklyCollectingHint;
        private Button weeklyCopy;
        private string weeklyPageMode;
        private readonly Dictionary<string,WeeklySummaryCard> weeklyCards = new Dictionary<string,WeeklySummaryCard>();

        private sealed class WeeklySummaryCard
        {
            internal Border Root;
            internal TextBlock Identity, Consumption, Coverage, Comparison, Low, Peak, Detail;
        }

        private void OpenWeeklySummary()
        {
            if (exiting) return;
            OpenDashboard(); NavigateDashboard("weekly");
            RestoreDashboardFocus("dashboard-nav:weekly");
        }

        private void ResetWeeklySummaryState()
        {
            weeklyPageRoot = weeklyCardsRoot = null;
            weeklyRange = weeklyComparisonRange = weeklyHighlights = weeklyCopyStatus = weeklyCollectingHint = null; weeklyCopy = null;
            weeklyPageMode = null; weeklyCards.Clear();
        }

        private void LockWeeklySummary()
        {
            // The old tree can still be referenced by an accessibility client.
            // Remove values before replacing it, including a retained copy button.
            if (weeklyCopy != null) weeklyCopy.IsEnabled = false;
            foreach (var card in weeklyCards.Values)
                foreach (var text in new[] { card.Identity, card.Consumption, card.Coverage, card.Comparison, card.Low, card.Peak, card.Detail }) text.Text = "";
            foreach (var text in new[] { weeklyRange, weeklyComparisonRange, weeklyHighlights, weeklyCopyStatus, weeklyCollectingHint }) if (text != null) text.Text = "";
            if (weeklyPageRoot != null) weeklyPageRoot.Children.Clear();
            ResetWeeklySummaryState();
        }

        private string WeeklySummaryMode()
        {
            return usageJournal == null || !usageJournal.Options.HistoryEnabled ? "off" : "ready";
        }

        private FrameworkElement BuildWeeklySummaryPage()
        {
            ResetWeeklySummaryState();
            var page = new LocalizedStackPanel(); weeklyPageRoot = page;
            weeklyPageMode = WeeklySummaryMode();
            page.Children.Add(DashboardPageHeader("주간 요약",demo
                ? "미리보기 · 예시 데이터입니다. 실제 사용량이 아니며 PC에 저장하지 않습니다."
                : "오늘을 제외한 최근 7일의 관측 기록을 정리합니다."));
            if (weeklyPageMode == "off") {
                var off = new LocalizedStackPanel(); off.Children.Add(T("이력 기록이 꺼져 있습니다.",17,"#252827",FontWeights.SemiBold));
                off.Children.Add(WeeklyLabel("이력 기록을 다시 켜면 이후 관측값으로 요약을 만듭니다. 기존 기록은 사용 이력에서 확인할 수 있습니다.",14,new Thickness(0,10,0,16)));
                var offActions = new WrapPanel(); offActions.Children.Add(WeeklyAction("알림 · 이력 설정",OpenWeeklyTrackingSettings,"weekly-enable-history"));
                offActions.Children.Add(WeeklyAction("사용 이력",() => NavigateDashboard("history"),"weekly-existing-history"));
                off.Children.Add(offActions); page.Children.Add(DashboardCard(off)); return page;
            }

            weeklyRange = WeeklyText("",14,FontWeights.SemiBold,new Thickness(0));
            page.Children.Add(weeklyRange);
            weeklyComparisonRange = WeeklyText("",13,FontWeights.Normal,new Thickness(0,6,0,18));
            page.Children.Add(weeklyComparisonRange);
            weeklyHighlights = WeeklyText("",16,FontWeights.SemiBold,new Thickness(0));
            var intro = new LocalizedStackPanel(); intro.Children.Add(weeklyHighlights);
            weeklyCollectingHint = WeeklyLabel("서비스를 연결하고 위젯을 실행해 두세요. 기록이 쌓이면 완료된 날짜부터 요약합니다.",14,new Thickness(0,10,0,0)); intro.Children.Add(weeklyCollectingHint);
            intro.Children.Add(WeeklyLabel("관측 기록 기준 · 오늘 제외 · 전체 합계 없음",13,new Thickness(0,10,0,0)));
            page.Children.Add(DashboardCard(intro));
            var actions = new WrapPanel { Margin = new Thickness(0,0,0,6) };
            weeklyCopy = WeeklyAction("요약 복사",CopyWeeklySummary,"weekly-copy"); actions.Children.Add(weeklyCopy);
            actions.Children.Add(WeeklyAction("잔량 분석",() => NavigateDashboard("analysis"),"weekly-analysis"));
            actions.Children.Add(WeeklyAction("알림 · 이력 설정",OpenWeeklyTrackingSettings,"weekly-settings"));
            page.Children.Add(actions);
            weeklyCopyStatus = WeeklyText("",13,FontWeights.Normal,new Thickness(0,0,0,10));
            AutomationProperties.SetLiveSetting(weeklyCopyStatus,AutomationLiveSetting.Polite); page.Children.Add(weeklyCopyStatus);
            weeklyCardsRoot = new LocalizedStackPanel(); page.Children.Add(weeklyCardsRoot);
            var notes = new LocalizedStackPanel();
            notes.Children.Add(WeeklyLabel("관측한 구간의 소모량입니다. 앱을 끈 시간과 누락 구간은 추정하지 않으며 실제 총사용량과 다를 수 있습니다.",13,new Thickness(0,12,0,0)));
            notes.Children.Add(WeeklyLabel("서비스·한도마다 단위가 달라 전체 합계는 표시하지 않습니다.",13,new Thickness(0,10,0,0)));
            notes.Children.Add(WeeklyLabel("날짜는 이 PC의 현지 시간을 기준으로 합니다. 오늘 진행 중인 사용량은 다음 날 요약에 포함됩니다.",13,new Thickness(0,10,0,0)));
            notes.Children.Add(WeeklyLabel("관측일은 기록이 있는 날이며 하루 전체를 관측했다는 뜻은 아닙니다. 저잔량 관측일은 한도 부족을 확인한 날의 수이며 소진 횟수가 아닙니다.",13,new Thickness(0,10,0,0)));
            page.Children.Add(new LocalizedExpander { Header = "관측·계산 안내", Content = notes, FontSize = 13, Foreground = BrushOf("#475256"), Margin = new Thickness(0,8,0,0) });
            RefreshWeeklySummary(); return page;
        }

        private void OpenWeeklyTrackingSettings()
        { OpenNotificationSettings(); }

        private Button WeeklyAction(string title,Action action,string id)
        {
            var button = ManagementAction(title,action); button.FontSize = 14; button.MinHeight = 36;
            button.Padding = new Thickness(12,7,12,7); button.Margin = new Thickness(0,0,8,8);
            button.HorizontalAlignment = HorizontalAlignment.Left; AutomationProperties.SetAutomationId(button,id); return button;
        }

        private TextBlock WeeklyText(string text,double size,FontWeight weight,Thickness margin)
        {
            var block = T(text,size,weight == FontWeights.SemiBold ? "#252827" : "#475256",weight,margin); UiL.Raw(block); block.LineHeight = size * 1.5; return block;
        }

        private TextBlock WeeklyLabel(string text,double size,Thickness margin)
        {
            var block = T(text,size,"#475256",FontWeights.Normal,margin); block.LineHeight = size * 1.5; return block;
        }

        private WeeklyUsageReport ReadWeeklySummary(DateTimeOffset now)
        {
            // Keep the result tied to this local journal and window lifecycle.
            if (usageJournal == null || !usageJournal.Options.HistoryEnabled) return null;
            var journal = usageJournal;
            string owner = ServiceReadingOwner;
            var points = journal.GetHistory().Where(point => point != null && point.ObservedAt <= now).ToList();
            var views = DashboardViews();
            var identities = points.Select(point => new HistoryMetricChoice { ServiceId = point.ServiceId, Label = point.Label }).ToList();
            foreach (var view in views.Where(view => view.Snapshot != null && view.Snapshot.Rows != null))
                identities.AddRange(view.Snapshot.Rows.Where(row => row != null && !String.IsNullOrEmpty(row.Label)).Select(row => new HistoryMetricChoice { ServiceId = view.Id, Label = row.Label }));
            var series = new List<WeeklyUsageSeries>();
            foreach (var identity in identities.Where(item => !String.IsNullOrEmpty(item.ServiceId) && !String.IsNullOrEmpty(item.Label))
                .GroupBy(DashboardMetricKey).Select(group => group.Last()).OrderBy(item => HistoryServiceName(item.ServiceId)).ThenBy(item => AnalysisPeriodPriority(item.Label)).ThenBy(item => item.Label)) {
                var latest = points.Where(point => point.ServiceId == identity.ServiceId && point.Label == identity.Label).OrderBy(point => point.ObservedAt).LastOrDefault();
                string source = views.Any(view => view.Id == identity.ServiceId) ? CurrentAnalysisSource(identity.ServiceId) : latest == null ? null : latest.Source;
                series.Add(new WeeklyUsageSeries { ServiceId = identity.ServiceId, Label = identity.Label, Source = source,
                    Basis = journal.GetAnalysis(identity.ServiceId,identity.Label,source,now),
                    Observed = journal.GetObservedHistory(identity.ServiceId,identity.Label,source,now),
                    Points = points.Where(point => point.ServiceId == identity.ServiceId && point.Label == identity.Label).ToList() });
            }
            var report = WeeklyUsageSummary.Build(series,now,TimeZoneInfo.Local,journal.Options.LowThresholdPercent);
            return WeeklyAccessMatches(journal,owner) ? report : null;
        }

        private bool WeeklyAccessMatches(UsageJournal journal,string owner)
        {
            return !exiting && Object.ReferenceEquals(journal,usageJournal)
                && String.Equals(owner,ServiceReadingOwner,StringComparison.Ordinal)
                && journal != null && journal.Options.HistoryEnabled;
        }

        private void RefreshWeeklySummary()
        {
            if (weeklyPageRoot == null) return;
            string mode = WeeklySummaryMode();
            if (mode != weeklyPageMode) {
                var target = weeklyPageRoot;
                LockWeeklySummary();
                var replacement = BuildWeeklySummaryPage() as StackPanel;
                while (replacement.Children.Count > 0) { var child = replacement.Children[0]; replacement.Children.RemoveAt(0); target.Children.Add(child); }
                weeklyPageRoot = target; return;
            }
            if (mode != "ready") return;
            try {
                var report = ReadWeeklySummary(DateTimeOffset.Now);
                if (report == null) return;
                weeklyRange.Text = report.LocalStart.ToString("yyyy.MM.dd",CultureInfo.InvariantCulture) + " – " + report.LocalEndExclusive.AddDays(-1).ToString("yyyy.MM.dd",CultureInfo.InvariantCulture);
                weeklyComparisonRange.Text = String.Format(CultureInfo.CurrentCulture,L.Text("비교 기간 · {0} – {1}"),report.LocalStart.AddDays(-7).ToString("yyyy.MM.dd",CultureInfo.InvariantCulture),report.LocalStart.AddDays(-1).ToString("yyyy.MM.dd",CultureInfo.InvariantCulture));
                weeklyHighlights.Text = WeeklyHighlightsText(report,demo);
                weeklyCollectingHint.Visibility = report.Items.Any(item => item.Current.HasConsumption) ? Visibility.Collapsed : Visibility.Visible;
                weeklyCopy.IsEnabled = report.Items.Any(item => item.Current.PointCount > 0 || item.Current.HasConsumption);
                var keys = new HashSet<string>(report.Items.Select(WeeklyKey),StringComparer.Ordinal);
                foreach (string key in weeklyCards.Keys.Where(key => !keys.Contains(key)).ToList()) { weeklyCardsRoot.Children.Remove(weeklyCards[key].Root); weeklyCards.Remove(key); }
                if (report.Items.Count == 0) {
                    if (weeklyCardsRoot.Children.Count == 0) {
                        var empty = new LocalizedStackPanel(); empty.Children.Add(T("아직 요약할 기록이 없습니다.",16,"#252827",FontWeights.SemiBold));
                        empty.Children.Add(WeeklyLabel("서비스를 연결하고 위젯을 실행해 두세요. 기록이 쌓이면 완료된 날짜부터 요약합니다.",14,new Thickness(0,10,0,18)));
                        empty.Children.Add(WeeklyAction("서비스 추가",() => OpenServices(CurrentServiceSelection()),"weekly-connect")); weeklyCardsRoot.Children.Add(DashboardCard(empty));
                    }
                    return;
                }
                if (weeklyCards.Count == 0) weeklyCardsRoot.Children.Clear();
                for (int index = 0; index < report.Items.Count; index++) {
                    var item = report.Items[index]; string key = WeeklyKey(item); WeeklySummaryCard card;
                    if (!weeklyCards.TryGetValue(key,out card)) {
                        card = CreateWeeklySummaryCard(item); weeklyCards.Add(key,card); weeklyCardsRoot.Children.Insert(Math.Min(index,weeklyCardsRoot.Children.Count),card.Root);
                    }
                    UpdateWeeklySummaryCard(card,item,report.LowThresholdPercent);
                }
            }
            catch {
                // Never leave plausible-looking figures after a failed read.
                foreach (var card in weeklyCards.Values)
                    foreach (var text in new[] { card.Identity, card.Consumption, card.Coverage, card.Comparison, card.Low, card.Peak, card.Detail }) text.Text = "";
                weeklyCardsRoot.Children.Clear(); weeklyCards.Clear(); weeklyCopy.IsEnabled = false;
                weeklyHighlights.Text = L.Text("이력을 불러오지 못했습니다. 잠시 뒤 다시 확인해 주세요.");
            }
        }

        private WeeklySummaryCard CreateWeeklySummaryCard(WeeklyUsageItem item)
        {
            var card = new WeeklySummaryCard(); var body = new LocalizedStackPanel();
            var heading = new DockPanel();
            var icon = new Image { Source = ServiceIcons.Get(item.ServiceId), Width = 20, Height = 20, Margin = new Thickness(0,2,10,0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(icon,Dock.Left); heading.Children.Add(icon);
            card.Identity = WeeklyText("",16,FontWeights.SemiBold,new Thickness(0)); heading.Children.Add(card.Identity); body.Children.Add(heading);
            body.Children.Add(WeeklyLabel("관측 소모량",13,new Thickness(0,18,0,2)));
            card.Consumption = WeeklyText("",32,FontWeights.SemiBold,new Thickness(0)); card.Consumption.LineHeight = 42;
            System.Windows.Documents.Typography.SetNumeralAlignment(card.Consumption,FontNumeralAlignment.Tabular); body.Children.Add(card.Consumption);
            card.Coverage = WeeklyText("",13,FontWeights.Normal,new Thickness(0,4,0,0)); body.Children.Add(card.Coverage);
            body.Children.Add(new Border { BorderBrush = BrushOf("#D6D9D7"), BorderThickness = new Thickness(0,1,0,0), Margin = new Thickness(0,16,0,16) });
            card.Comparison = WeeklyText("",14,FontWeights.SemiBold,new Thickness(0)); body.Children.Add(card.Comparison);
            card.Detail = WeeklyText("",13,FontWeights.Normal,new Thickness(0,6,0,0)); body.Children.Add(card.Detail);
            body.Children.Add(new Border { BorderBrush = BrushOf("#D6D9D7"), BorderThickness = new Thickness(0,1,0,0), Margin = new Thickness(0,16,0,16) });
            card.Low = WeeklyText("",14,FontWeights.Normal,new Thickness(0)); body.Children.Add(card.Low);
            card.Peak = WeeklyText("",14,FontWeights.Normal,new Thickness(0,10,0,0)); body.Children.Add(card.Peak);
            string service = item.ServiceId, label = item.Label;
            var history = WeeklyAction("해당 이력 보기",() => {
                SetDashboardHistoryService(service); dashboardHistoryMetricKey = service + "\n" + label; dashboardHistoryDays = 7; NavigateDashboard("history");
            },"weekly-history:" + WeeklyKey(item)); history.Margin = new Thickness(0,18,0,0); body.Children.Add(history);
            card.Root = DashboardCard(body); return card;
        }

        private void UpdateWeeklySummaryCard(WeeklySummaryCard card,WeeklyUsageItem item,int lowThreshold)
        {
            card.Identity.Text = WeeklyIdentity(item,demo); card.Consumption.Text = WeeklyConsumption(item.Current,item.Unit);
            card.Coverage.Text = String.Format(CultureInfo.CurrentCulture,L.Text("관측 {0}/7일 · 확정 구간 {1}시간"),item.Current.ObservedDays,WeeklyNumber(item.Current.CoveredHours));
            card.Comparison.Text = WeeklyComparison(item);
            card.Low.Text = WeeklyLow(item,lowThreshold); card.Peak.Text = WeeklyPeak(item);
            card.Detail.Text = String.Format(CultureInfo.CurrentCulture,L.Text("이전 7일 · 관측 {0}/7일 · 확정 구간 {1}시간"),item.Previous.ObservedDays,WeeklyNumber(item.Previous.CoveredHours));
            if (item.Current.UnassignedDayBuckets > 0) card.Detail.Text += "\n" + L.Text("날짜 경계를 넘은 구간은 주간 합계에만 포함하며 일별 최다 계산에서는 제외합니다.");
        }

        private static string WeeklyKey(WeeklyUsageItem item) { return item.ServiceId + "\n" + item.Label; }
        private static string WeeklyNumber(double value) { return value.ToString("#,0.##",CultureInfo.CurrentCulture); }
        private static string WeeklyIdentity(WeeklyUsageItem item,bool example = false)
        { return UsageMetricTitle(item.ServiceId,item.Label,item.Source == "manual" && !example); }
        private static string WeeklyRangeText(WeeklyUsageReport report)
        {
            return report.LocalStart.ToString("yyyy.MM.dd",CultureInfo.InvariantCulture) + " – " + report.LocalEndExclusive.AddDays(-1).ToString("yyyy.MM.dd",CultureInfo.InvariantCulture) + " · " + L.Text("현지 시간 · 오늘 제외")
                + "\n" + String.Format(CultureInfo.CurrentCulture,L.Text("비교 기간 · {0} – {1}"),report.LocalStart.AddDays(-7).ToString("yyyy.MM.dd",CultureInfo.InvariantCulture),report.LocalStart.AddDays(-1).ToString("yyyy.MM.dd",CultureInfo.InvariantCulture));
        }
        private static string WeeklyConsumption(WeeklyUsagePeriod period,string unit)
        { return period.HasConsumption ? WeeklyNumber(period.Consumption) + (String.IsNullOrEmpty(unit) ? "" : " " + L.Text(unit)) : "—"; }
        private static string WeeklyComparison(WeeklyUsageItem item)
        {
            if (item.Comparable && !item.DifferencePercent.HasValue)
                return String.Format(CultureInfo.CurrentCulture,L.Text("이전 7일 · {0} → {1} · 이전 소모량이 0이라 증감률은 표시하지 않습니다."),WeeklyConsumption(item.Previous,item.Unit),WeeklyConsumption(item.Current,item.Unit));
            if (!item.Comparable || !item.DifferencePercent.HasValue)
                return L.Text(String.IsNullOrEmpty(item.Reason) ? "이전 7일과 비교할 기록이 부족합니다." : item.Reason);
            return String.Format(CultureInfo.CurrentCulture,L.Text("이전 7일 대비 {0}% · {1} → {2}"),
                item.DifferencePercent.Value.ToString("+0.#;-0.#;0",CultureInfo.CurrentCulture),WeeklyConsumption(item.Previous,item.Unit),WeeklyConsumption(item.Current,item.Unit));
        }
        private static string WeeklyLow(WeeklyUsageItem item,int threshold)
        {
            return item.Current.LowDaysAvailable ? String.Format(CultureInfo.CurrentCulture,L.Text("저잔량 관측 {0}일 · {1}% 이하 · 비율 확인 {2}일"),item.Current.LowDays,threshold,item.Current.LowSampleDays)
                : L.Text("저잔량 관측일 · 비율 정보 없음");
        }
        private static string WeeklyPeak(WeeklyUsageItem item)
        {
            return item.Current.PeakDay.HasValue && item.Current.PeakConsumption.HasValue
                ? String.Format(CultureInfo.CurrentCulture,L.Text("일별 관측 소모 최다 · {0} · {1} {2}"),item.Current.PeakDay.Value.ToString("MM.dd",CultureInfo.InvariantCulture),WeeklyNumber(item.Current.PeakConsumption.Value),L.Text(item.Unit ?? ""))
                : L.Text(item.Current.HasConsumption && item.Current.Consumption == 0 ? "일별 관측 소모 최다 · 관측된 소모 없음" : "일별 관측 소모 최다 · 기록 부족");
        }
        private static string WeeklyHighlightsText(WeeklyUsageReport report,bool example)
        {
            var changed = report.Items.Where(item => item.Comparable && item.DifferencePercent.HasValue).OrderByDescending(item => Math.Abs(item.DifferencePercent.Value)).FirstOrDefault();
            if (changed != null) return WeeklyIdentity(changed,example) + " · " + String.Format(CultureInfo.CurrentCulture,L.Text("이전 7일 대비 {0}%"),changed.DifferencePercent.Value.ToString("+0.#;-0.#;0",CultureInfo.CurrentCulture));
            int count = report.Items.Count(item => item.Current.HasConsumption);
            return count > 0 ? String.Format(CultureInfo.CurrentCulture,L.Text("{0}개 항목의 관측 소모량을 정리했습니다."),count)
                : L.Text("기록을 모으고 있습니다. 관측하지 못한 사용량을 0으로 표시하지 않습니다.");
        }

        private void CopyWeeklySummary()
        {
            if (usageJournal == null || !usageJournal.Options.HistoryEnabled) { RefreshWeeklySummary(); return; }
            try {
                var journal = usageJournal; string owner = ServiceReadingOwner;
                var report = ReadWeeklySummary(DateTimeOffset.Now);
                if (report == null || !report.Items.Any(item => item.Current.PointCount > 0 || item.Current.HasConsumption)) return;
                string text = WeeklySummaryText(report,demo);
                if (!WeeklyAccessMatches(journal,owner)) { RefreshWeeklySummary(); return; }
                Clipboard.SetText(text);
                if (weeklyCopyStatus != null) weeklyCopyStatus.Text = L.Text("주간 요약을 복사했습니다.");
            }
            catch { if (weeklyCopyStatus != null) weeklyCopyStatus.Text = L.Text("복사하지 못했습니다. 잠시 뒤 다시 시도해 주세요."); }
        }

        internal static string WeeklySummaryText(WeeklyUsageReport report,bool example = false)
        {
            var text = new StringBuilder(); text.AppendLine("Nephylo · " + L.Text("주간 요약")); text.AppendLine(WeeklyRangeText(report));
            text.AppendLine(L.Text("관측한 구간의 소모량입니다. 앱을 끈 시간과 누락 구간은 추정하지 않으며 실제 총사용량과 다를 수 있습니다."));
            foreach (var item in report.Items) {
                text.AppendLine(); text.AppendLine(WeeklyIdentity(item,example)); text.AppendLine(L.Text("관측 소모량") + " · " + WeeklyConsumption(item.Current,item.Unit));
                text.AppendLine(String.Format(CultureInfo.CurrentCulture,L.Text("관측 {0}/7일 · 확정 구간 {1}시간"),item.Current.ObservedDays,WeeklyNumber(item.Current.CoveredHours)));
                text.AppendLine(WeeklyComparison(item)); text.AppendLine(WeeklyLow(item,report.LowThresholdPercent)); text.AppendLine(WeeklyPeak(item));
                text.AppendLine(String.Format(CultureInfo.CurrentCulture,L.Text("이전 7일 · 관측 {0}/7일 · 확정 구간 {1}시간"),item.Previous.ObservedDays,WeeklyNumber(item.Previous.CoveredHours)));
                if (item.Current.UnassignedDayBuckets > 0) text.AppendLine(L.Text("날짜 경계를 넘은 구간은 주간 합계에만 포함하며 일별 최다 계산에서는 제외합니다."));
            }
            return text.ToString().TrimEnd();
        }
    }
}
