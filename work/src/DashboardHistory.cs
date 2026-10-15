using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        // The dashboard owns its selection and controls. Opening the legacy
        // history dialog must never replace these references (or vice versa).
        private ComboBox dashboardHistoryService, dashboardHistoryMetric, dashboardHistoryRange;
        private StackPanel dashboardHistoryContent, dashboardHistoryPageRoot;
        private TextBlock dashboardHistoryStatus;
        private Button dashboardHistoryExport;
        private string dashboardHistoryServiceId, dashboardHistoryMetricKey;
        private int dashboardHistoryDays = 7;
        private bool dashboardHistoryUpdating, dashboardHistoryAnalysisOnly;
        private bool dashboardHistoryRefreshPending;
        private UsageHistoryChart dashboardHistoryChart;
        private TextBlock dashboardHistoryChartCaption, dashboardHistoryChartRange, dashboardHistoryRecordsCaption;
        private StackPanel dashboardHistoryRecords;
        private string dashboardHistoryRecordsKey;
        private List<UsageHistoryPoint> dashboardHistoryFiltered = new List<UsageHistoryPoint>();
        private readonly Dictionary<string, bool> dashboardHistoryDisclosureStates = new Dictionary<string, bool>();

        private void ResetDashboardHistoryState()
        {
            dashboardHistoryService = dashboardHistoryMetric = dashboardHistoryRange = null;
            dashboardHistoryContent = dashboardHistoryPageRoot = null; dashboardHistoryStatus = null; dashboardHistoryExport = null;
            dashboardHistoryServiceId = dashboardHistoryMetricKey = null; dashboardHistoryDays = 7;
            dashboardHistoryFiltered.Clear(); dashboardHistoryDisclosureStates.Clear(); dashboardHistoryUpdating = false;
            ResetDashboardHistoryVisuals();
        }

        private void ResetDashboardHistoryVisuals()
        {
            dashboardHistoryChart = null; dashboardHistoryChartCaption = dashboardHistoryRecordsCaption = null;
            dashboardHistoryChartRange = null;
            dashboardHistoryRecords = null; dashboardHistoryRecordsKey = null; dashboardHistoryRefreshPending = false;
            dashboardHistoryInsights = null;
            dashboardAnalysisCards.Clear();
        }

        private void SetDashboardHistoryService(string id)
        {
            dashboardHistoryServiceId = id == "elevenlabs" ? "eleven" : id;
            dashboardHistoryMetricKey = null;
        }

        private FrameworkElement BuildDashboardHistoryPage(bool analysisOnly)
        {
            CaptureDashboardAnalysisDisclosures();
            ResetDashboardHistoryVisuals();
            dashboardHistoryAnalysisOnly = analysisOnly;
            var page = new LocalizedStackPanel();
            dashboardHistoryPageRoot = page;
            if (!ProFeaturesAvailable) { LockDashboardHistory(); return page; }
            var header = DashboardPageHeader(analysisOnly ? "잔량 분석" : "사용 이력",
                demo ? "미리보기 · 예시 데이터입니다. 실제 사용량이 아니며 PC에 저장하지 않습니다."
                : analysisOnly ? "최근 7일의 관측 기록으로 하루 평균과 소진 가능성을 확인합니다."
                : "이 PC에서 확인한 값을 기록합니다. 과거 사용량을 소급해서 가져오지는 않습니다.");
            header.Margin = new Thickness(0,0,0,16); page.Children.Add(header);

            var filterBody = new LocalizedStackPanel();
            var filters = new LocalizedGrid();
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
            if (!analysisOnly) filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
            dashboardHistoryService = DashboardHistoryFilter(filters, "서비스", 0);
            dashboardHistoryMetric = DashboardHistoryFilter(filters, "항목", 1);
            dashboardHistoryRange = analysisOnly ? null : DashboardHistoryFilter(filters, "기간", 2);
            filters.SizeChanged += (s,e) => ArrangeDashboardHistoryFilters(filters,analysisOnly);
            if (dashboardHistoryRange != null) {
                foreach (int days in new[] { 1, 7, 30 }) dashboardHistoryRange.Items.Add(new LocalizedComboBoxItem {
                    Content = days == 1 ? "24시간" : days == 7 ? "7일" : "30일", Tag = days });
                dashboardHistoryRange.SelectedItem = dashboardHistoryRange.Items.Cast<ComboBoxItem>().First(item => (int)item.Tag == dashboardHistoryDays);
            }
            filterBody.Children.Add(filters);
            dashboardHistoryStatus = DashboardText("", 13, "#647070", FontWeights.Normal, new Thickness(0, 0, 0, 16));
            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            dashboardHistoryExport = null;
            if (!analysisOnly) {
                dashboardHistoryExport = ManagementAction("CSV 내보내기", ExportDashboardHistory);
                dashboardHistoryExport.IsEnabled = false;
                dashboardHistoryExport.Margin = new Thickness(0,0,10,8); dashboardHistoryExport.MinHeight = 36;
                dashboardHistoryExport.FontSize = 13; dashboardHistoryExport.Padding = new Thickness(12,7,12,7);
                actions.Children.Add(dashboardHistoryExport);
            }
            var settingsAction = ManagementAction("알림 · 이력 설정", () => {
                if (!ProFeaturesAvailable) { RefreshDashboardIfOpen(); return; }
                OpenNotificationSettings();
            });
            settingsAction.Margin = new Thickness(0,0,0,8); settingsAction.MinHeight = 36;
            settingsAction.FontSize = 13; settingsAction.Padding = new Thickness(12,7,12,7);
            actions.Children.Add(settingsAction);
            var filterCard = DashboardCard(filterBody); filterCard.Padding = new Thickness(20); filterCard.Margin = new Thickness(0,0,0,16); page.Children.Add(filterCard);
            dashboardHistoryContent = new LocalizedStackPanel(); page.Children.Add(dashboardHistoryContent);
            // Export and preferences are secondary to the selected balance
            // chart. Keep them reachable without crowding the filter row.
            page.Children.Add(actions);
            page.Children.Add(dashboardHistoryStatus);
            dashboardHistoryService.SelectionChanged += (s, e) => {
                if (dashboardHistoryUpdating) return;
                var selected = dashboardHistoryService.SelectedItem as ComboBoxItem;
                dashboardHistoryServiceId = selected == null ? "" : selected.Tag as string;
                dashboardHistoryMetricKey = null;
                UpdateDashboardHistoryData(true);
            };
            dashboardHistoryMetric.SelectionChanged += (s, e) => {
                if (dashboardHistoryUpdating) return;
                dashboardHistoryMetricKey = DashboardMetricKey(dashboardHistoryMetric.SelectedItem as HistoryMetricChoice);
                UpdateDashboardHistoryData(false);
            };
            if (dashboardHistoryRange != null) dashboardHistoryRange.SelectionChanged += (s, e) => {
                if (dashboardHistoryUpdating) return;
                var selected = dashboardHistoryRange.SelectedItem as ComboBoxItem;
                if (selected != null) dashboardHistoryDays = (int)selected.Tag;
                UpdateDashboardHistoryData(false);
            };
            UpdateDashboardHistoryData(false);
            return page;
        }

        private ComboBox DashboardHistoryFilter(Grid grid, string label, int column)
        {
            var panel = new LocalizedStackPanel { Margin = new Thickness(column == 0 ? 0 : 10, 0, 0, 0) };
            panel.Children.Add(DashboardText(label, 13, "#647070", FontWeights.Normal, new Thickness(0, 0, 0, 6)));
            var combo = new LocalizedComboBox { FontSize = 14, MinHeight = 38, HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Resources[typeof(ComboBox)], ItemContainerStyle = (Style)Resources[typeof(ComboBoxItem)] };
            AutomationProperties.SetName(combo, L.Text("사용 이력") + " · " + L.Text(label));
            AutomationProperties.SetAutomationId(combo,"dashboard-history-filter-" + column);
            combo.DropDownClosed += (s,e) => {
                if (!dashboardHistoryRefreshPending || !Object.ReferenceEquals(combo,dashboardHistoryService)
                    && !Object.ReferenceEquals(combo,dashboardHistoryMetric) && !Object.ReferenceEquals(combo,dashboardHistoryRange)) return;
                if (!DashboardHistoryPopupOpen()) { dashboardHistoryRefreshPending = false; UpdateDashboardHistoryData(false); }
            };
            panel.Children.Add(combo); Grid.SetColumn(panel, column); grid.Children.Add(panel);
            return combo;
        }

        private static void ArrangeDashboardHistoryFilters(Grid filters,bool analysisOnly)
        {
            bool narrow = filters.ActualWidth > 0 && filters.ActualWidth < (analysisOnly ? 420 : 540);
            if(filters.Tag is bool && (bool)filters.Tag==narrow) return;
            filters.Tag=narrow;
            filters.RowDefinitions.Clear();
            filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (narrow) filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int index=0;index<filters.Children.Count;index++) {
                var child=filters.Children[index] as FrameworkElement; if(child==null) continue;
                bool secondRow = narrow && index == filters.Children.Count-1;
                Grid.SetRow(child,secondRow ? 1 : 0); Grid.SetColumn(child,secondRow ? 0 : index);
                Grid.SetColumnSpan(child,secondRow ? filters.ColumnDefinitions.Count : 1);
                child.Margin = new Thickness(index==0 || secondRow ? 0 : 10,secondRow ? 10 : 0,0,0);
            }
            if(!analysisOnly) filters.ColumnDefinitions[2].Width=narrow ? new GridLength(0) : new GridLength(0.7,GridUnitType.Star);
            else filters.ColumnDefinitions[1].Width=narrow ? new GridLength(0) : new GridLength(1.35,GridUnitType.Star);
        }

        private static string DashboardMetricKey(HistoryMetricChoice metric)
        { return metric == null || metric.Label == null ? "" : metric.ServiceId + "\n" + metric.Label; }

        private void CaptureDashboardAnalysisDisclosures()
        {
            if (dashboardHistoryContent == null) return;
            foreach (var card in dashboardHistoryContent.Children.OfType<Border>()) {
                var body = card.Child as StackPanel;
                var key = card.Tag as string;
                var disclosure = card.Child as Expander;
                if (disclosure == null && body != null) disclosure = body.Children.OfType<Expander>().FirstOrDefault();
                if (key != null && disclosure != null) dashboardHistoryDisclosureStates[key] = disclosure.IsExpanded;
            }
        }

        private void RefreshDashboardHistoryIfOpen()
        {
            if (dashboardWindow == null || (dashboardPage != "history" && dashboardPage != "analysis")) return;
            if (!ProFeaturesAvailable) { LockDashboardHistory(); return; }
            if (dashboardHistoryService == null && dashboardHistoryPageRoot != null) {
                var target = dashboardHistoryPageRoot;
                var replacement = BuildDashboardHistoryPage(dashboardPage == "analysis") as StackPanel;
                if (replacement != null) {
                    target.Children.Clear();
                    while (replacement.Children.Count > 0) {
                        var child = replacement.Children[0]; replacement.Children.RemoveAt(0); target.Children.Add(child);
                    }
                    dashboardHistoryPageRoot = target;
                }
                return;
            }
            // Preserve a popup's current option; catch up immediately when it
            // closes. Ordinary keyboard focus must never suspend observations.
            if (DashboardHistoryPopupOpen()) { dashboardHistoryRefreshPending = true; return; }
            if (dashboardHistoryService != null) UpdateDashboardHistoryData(false);
        }

        private bool DashboardHistoryPopupOpen()
        {
            return dashboardHistoryService != null && dashboardHistoryService.IsDropDownOpen
                || dashboardHistoryMetric != null && dashboardHistoryMetric.IsDropDownOpen
                || dashboardHistoryRange != null && dashboardHistoryRange.IsDropDownOpen;
        }

        private void LockDashboardHistory()
        {
            dashboardHistoryUpdating = true;
            try {
                dashboardHistoryFiltered.Clear();
                dashboardHistoryDisclosureStates.Clear();
                foreach (var filter in new[] { dashboardHistoryService, dashboardHistoryMetric, dashboardHistoryRange })
                    if (filter != null) { filter.IsEnabled = false; filter.Items.Clear(); }
                if (dashboardHistoryContent != null) dashboardHistoryContent.Children.Clear();
                if (dashboardHistoryStatus != null) dashboardHistoryStatus.Text = "";
                if (dashboardHistoryExport != null) dashboardHistoryExport.IsEnabled = false;
                if (dashboardHistoryPageRoot != null) {
                    dashboardHistoryPageRoot.Children.Clear(); dashboardHistoryPageRoot.Children.Add(BuildDashboardProGate());
                }
                dashboardHistoryService = dashboardHistoryMetric = dashboardHistoryRange = null;
                dashboardHistoryContent = null; dashboardHistoryStatus = null; dashboardHistoryExport = null;
                ResetDashboardHistoryVisuals();
            }
            finally { dashboardHistoryUpdating = false; }
        }

        private void UpdateDashboardHistoryData(bool resetMetric)
        {
            if (dashboardHistoryUpdating || dashboardHistoryContent == null) return;
            if (!ProFeaturesAvailable) { LockDashboardHistory(); return; }
            dashboardHistoryUpdating = true;
            try {
                var now = DateTimeOffset.Now;
                var points = usageJournal.GetHistory().Where(point => point.ObservedAt <= now).OrderBy(point => point.ObservedAt).ToList();
                var knownIds = new[] { "codex", "eleven", "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" };
                var connected = new HashSet<string>(DashboardViews().Select(view => view.Id), StringComparer.Ordinal);
                var ids = knownIds.Where(id => connected.Contains(id) || points.Any(point => point.ServiceId == id)).OrderBy(HistoryServiceName).ToList();
                if (dashboardHistoryServiceId == null) dashboardHistoryServiceId = ids.FirstOrDefault(id => connected.Contains(id) && points.Any(point => point.ServiceId == id))
                    ?? ids.FirstOrDefault(id => connected.Contains(id)) ?? ids.FirstOrDefault() ?? "";
                if (!String.IsNullOrEmpty(dashboardHistoryServiceId) && !ids.Contains(dashboardHistoryServiceId)) dashboardHistoryServiceId = "";
                if (!ids.SequenceEqual(dashboardHistoryService.Items.OfType<ComboBoxItem>().Skip(1).Select(item => (string)item.Tag))) {
                    dashboardHistoryService.Items.Clear();
                    dashboardHistoryService.Items.Add(new LocalizedComboBoxItem { Content = "모든 서비스", Tag = "" });
                    foreach (string id in ids) dashboardHistoryService.Items.Add(new LocalizedComboBoxItem { Content = HistoryServiceName(id), Tag = id });
                }
                if (dashboardHistoryService.Items.Count == 0) dashboardHistoryService.Items.Add(new LocalizedComboBoxItem { Content = "모든 서비스", Tag = "" });
                dashboardHistoryService.SelectedItem = dashboardHistoryService.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == dashboardHistoryServiceId);
                var servicePoints = String.IsNullOrEmpty(dashboardHistoryServiceId) ? points : points.Where(point => point.ServiceId == dashboardHistoryServiceId).ToList();
                var metrics = servicePoints.Select(point => new HistoryMetricChoice { ServiceId = point.ServiceId, Label = point.Label, Manual = !demo && point.Source == "manual", IncludeService = String.IsNullOrEmpty(dashboardHistoryServiceId) }).ToList();
                // A just-connected service needs an honest collecting state even
                // before its first journal observation has been written.
                foreach (string id in ids.Where(id => connected.Contains(id) && (String.IsNullOrEmpty(dashboardHistoryServiceId) || id == dashboardHistoryServiceId))) {
                    var view = ViewFor(id);
                    if (view.Snapshot == null || view.Snapshot.Rows == null) continue;
                    foreach (var row in view.Snapshot.Rows.Where(row => row != null && !String.IsNullOrEmpty(row.Label))) metrics.Add(new HistoryMetricChoice {
                        ServiceId = id, Label = row.Label, Manual = !demo && UsageFreshness.Source(view) == "manual", IncludeService = String.IsNullOrEmpty(dashboardHistoryServiceId) });
                }
                metrics = metrics.Where(metric => !String.IsNullOrEmpty(metric.Label)).GroupBy(DashboardMetricKey).Select(group => group.Last())
                    .OrderBy(metric => HistoryServiceName(metric.ServiceId)).ThenBy(metric => metric.Manual ? 0 : AnalysisPeriodPriority(metric.Label)).ThenBy(metric => metric.Label).ToList();
                if (resetMetric || dashboardHistoryMetricKey == null) dashboardHistoryMetricKey = !String.IsNullOrEmpty(dashboardHistoryServiceId) && metrics.Count > 0 ? DashboardMetricKey(metrics[0]) : "";
                if (!String.IsNullOrEmpty(dashboardHistoryMetricKey) && !metrics.Any(metric => DashboardMetricKey(metric) == dashboardHistoryMetricKey)) dashboardHistoryMetricKey = "";
                if (dashboardHistoryMetric.Items.Count == 0 || !metrics.Select(metric => DashboardMetricKey(metric) + "\n" + metric.Manual).SequenceEqual(dashboardHistoryMetric.Items.OfType<HistoryMetricChoice>().Skip(1).Select(metric => DashboardMetricKey(metric) + "\n" + metric.Manual))
                    || dashboardHistoryMetric.Items.OfType<HistoryMetricChoice>().Skip(1).Any(metric => metric.IncludeService != String.IsNullOrEmpty(dashboardHistoryServiceId))) {
                    dashboardHistoryMetric.Items.Clear(); dashboardHistoryMetric.Items.Add(new HistoryMetricChoice());
                    foreach (var metric in metrics) dashboardHistoryMetric.Items.Add(metric);
                }
                dashboardHistoryMetric.SelectedItem = dashboardHistoryMetric.Items.OfType<HistoryMetricChoice>().First(metric => DashboardMetricKey(metric) == dashboardHistoryMetricKey);
                var selectedMetric = dashboardHistoryMetric.SelectedItem as HistoryMetricChoice;
                var start = now.AddDays(-dashboardHistoryDays);
                dashboardHistoryFiltered = servicePoints.Where(point => point.ObservedAt >= start && (selectedMetric == null || selectedMetric.Label == null || point.ServiceId == selectedMetric.ServiceId && point.Label == selectedMetric.Label)).ToList();
                CaptureDashboardAnalysisDisclosures();
                UpdateDashboardHistoryStatus(points.Count);
                if (dashboardHistoryExport != null) dashboardHistoryExport.IsEnabled = dashboardHistoryFiltered.Count > 0;
                if (dashboardHistoryAnalysisOnly) {
                    var chosen = String.IsNullOrEmpty(dashboardHistoryMetricKey) ? metrics : metrics.Where(metric => DashboardMetricKey(metric) == dashboardHistoryMetricKey).ToList();
                    UpdateDashboardAnalysisCards(chosen,points,connected,now);
                } else BuildDashboardHistoryRecords(selectedMetric, start, now);
            }
            catch {
                dashboardHistoryFiltered.Clear(); dashboardHistoryContent.Children.Clear(); ResetDashboardHistoryVisuals();
                if (dashboardHistoryExport != null) dashboardHistoryExport.IsEnabled = false;
                dashboardHistoryStatus.Text = "이력을 불러오지 못했습니다. 잠시 뒤 다시 확인해 주세요.";
            }
            finally { dashboardHistoryUpdating = false; }
        }

        private void UpdateDashboardHistoryStatus(int total)
        {
            var messages = new List<string>();
            if (!String.IsNullOrWhiteSpace(usageTrackingError)) messages.Add(usageTrackingError);
            if (!usageJournal.Options.HistoryEnabled) messages.Add("이력 기록이 꺼져 있습니다. 기존 기록만 표시합니다.");
            if (total == 0) messages.Add("기록은 지금부터 쌓입니다. 서비스가 새로 확인되거나 수동 기록을 저장하면 표시되며, 과거 사용량은 복원하지 않습니다.");
            else if (dashboardHistoryAnalysisOnly) messages.Add("관측한 사용 추세에 따른 참고값입니다. 사용 패턴에 따라 달라질 수 있습니다.");
            else messages.Add("확인 시각은 이 PC의 현지 시간입니다. CSV에는 현재 필터의 전체 기록을 담습니다.");
            dashboardHistoryStatus.Text = String.Join("\n", messages);
        }

        private void BuildDashboardHistoryRecords(HistoryMetricChoice metric, DateTimeOffset start, DateTimeOffset now)
        {
            var numeric = dashboardHistoryFiltered.Where(point => UsageHistoryChart.MetricValue(point).HasValue).ToList();
            int series = dashboardHistoryFiltered.Select(point => point.ServiceId + "\n" + point.Label).Distinct().Count();
            string message = dashboardHistoryFiltered.Count == 0 ? "이 기간에 확인한 기록이 없습니다."
                : series > 1 ? "항목을 하나 선택하면 잔량 추이를 표시합니다."
                : numeric.Count == 0 ? "그래프로 표시할 수치가 없습니다. 아래 기록을 확인하세요."
                : numeric.Select(UsageHistoryChart.MetricUnit).Distinct().Count() > 1 ? "기록의 단위가 변경되었습니다. 아래에서 개별 수치를 확인하세요." : null;
            if (dashboardHistoryChart == null) {
                dashboardHistoryContent.Children.Clear();
                var chartBody = new LocalizedStackPanel();
                dashboardHistoryChartCaption = DashboardText("잔량 추이", 16, "#252827", FontWeights.SemiBold, new Thickness(0, 0, 0, 6));
                chartBody.Children.Add(dashboardHistoryChartCaption);
                dashboardHistoryChartRange = DashboardText("",13,"#647070",FontWeights.Normal,new Thickness(0,0,0,12));
                UiL.Raw(dashboardHistoryChartRange); chartBody.Children.Add(dashboardHistoryChartRange);
                dashboardHistoryChart = new UsageHistoryChart { Height = 238, ReadableDashboardText = true, HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(dashboardHistoryChart, L.Text("기록된 잔량 추이 · 항목별 백분율, 크레딧 또는 USD"));
                chartBody.Children.Add(dashboardHistoryChart);
                chartBody.Children.Add(DashboardText("점은 확인된 값입니다. 수동 기록, 초기화 시각, 수집 간 공백은 선으로 연결하지 않습니다.", 13, "#647070", FontWeights.Normal, new Thickness(0, 12, 0, 0)));
                var chartCard = DashboardCard(chartBody); chartCard.Padding = new Thickness(20); chartCard.Margin = new Thickness(0,0,0,16);
                dashboardHistoryContent.Children.Add(chartCard);
                dashboardHistoryInsights = CreateDashboardHistoryInsights();
                dashboardHistoryContent.Children.Add(dashboardHistoryInsights.Card);
                dashboardHistoryRecordsCaption = DashboardText("최근 기록", 16, "#252827", FontWeights.SemiBold);
                dashboardHistoryRecords = new LocalizedStackPanel { Margin = new Thickness(0,14,0,0) };
                var disclosure = new LocalizedExpander { Header = dashboardHistoryRecordsCaption, Content = dashboardHistoryRecords,
                    IsExpanded = false, Foreground = Fore, FontSize = 14 };
                AutomationProperties.SetAutomationId(disclosure,"dashboard-history-records");
                // Like comparison methodology, this is a display preference for
                // the dashboard session, shared across service/period filters.
                bool expanded;
                if (dashboardHistoryDisclosureStates.TryGetValue("history:records",out expanded)) disclosure.IsExpanded = expanded;
                var recordsCard = DashboardCard(disclosure); recordsCard.Padding = new Thickness(20); recordsCard.Margin = new Thickness(0,0,0,16);
                recordsCard.Tag = "history:records";
                dashboardHistoryContent.Children.Add(recordsCard);
            }
            dashboardHistoryChartCaption.Text = L.Text("잔량 추이");
            if (metric != null && metric.Label != null) {
                dashboardHistoryChartCaption.Text = (metric.Manual ? metric.Label : L.Text(metric.Label)) + " · " + L.Text("잔량 추이");
            }
            UiL.Raw(dashboardHistoryChartCaption);
            dashboardHistoryChartRange.Text = DashboardObservedDateRange(start,now) + " · " + L.Text("현지 시간");
            dashboardHistoryChart.AnimationsEnabled = settings.AnimationsEnabled && SystemParameters.ClientAreaAnimation;
            dashboardHistoryChart.SetPoints(message == null ? dashboardHistoryFiltered : new List<UsageHistoryPoint>(), start, now, Math.Max(30, settings.RefreshSeconds / 60.0 * 3), message);
            UpdateDashboardHistoryInsights(metric, now);
            string count = L.Text("최근 기록") + " · " + dashboardHistoryFiltered.Count.ToString("N0", CultureInfo.CurrentCulture);
            if (dashboardHistoryFiltered.Count > 100) count += " · " + L.Text("최근 100개 표시");
            dashboardHistoryRecordsCaption.Text = count; UiL.Raw(dashboardHistoryRecordsCaption);
            var recent = dashboardHistoryFiltered.AsEnumerable().Reverse().Take(100).ToList();
            string recordsKey = count + "|" + L.Language + "|" + String.Join("\n",recent.Select(point => point.ServiceId + "|" + point.Label + "|" + point.Source + "|"
                + point.ObservedAt.ToString("o",CultureInfo.InvariantCulture) + "|" + point.RemainingPercent + "|" + point.Remaining + "|" + point.Limit + "|" + point.ResetsAt + "|" + point.DisplayValue + "|" + point.ResolutionMinutes));
            if (dashboardHistoryRecordsKey == recordsKey) return;
            dashboardHistoryRecordsKey = recordsKey; dashboardHistoryRecords.Children.Clear();
            if (dashboardHistoryFiltered.Count == 0) dashboardHistoryRecords.Children.Add(DashboardText("이 기간에 확인한 기록이 없습니다.", 14, "#647070", FontWeights.Normal));
            foreach (var point in recent) dashboardHistoryRecords.Children.Add(DashboardHistoryRow(point));
            if (dashboardHistoryFiltered.Any(point => point.ResolutionMinutes > 0)) dashboardHistoryRecords.Children.Add(DashboardText("묶음 기록은 해당 시간대의 마지막 실제 확인값입니다. 평균이나 추정 사용량이 아닙니다.", 13, "#647070", FontWeights.Normal, new Thickness(0, 16, 0, 0)));
        }

        private FrameworkElement DashboardHistoryRow(UsageHistoryPoint point)
        {
            // The dashboard has its own reading scale. The standalone history
            // window keeps its existing density and layout.
            var grid = new LocalizedGrid { Margin = new Thickness(0,14,0,14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.65,GridUnitType.Star) });
            var details = new LocalizedStackPanel { Margin = new Thickness(0,0,20,0) };
            var title = DashboardText(HistoryServiceName(point.ServiceId) + " · " + point.Label,14,"#252827",FontWeights.SemiBold);
            if (!demo && point.Source == "manual") { title.Text = L.Text(HistoryServiceName(point.ServiceId)) + " · " + point.Label; UiL.Raw(title); }
            details.Children.Add(title);
            details.Children.Add(DashboardText(point.ObservedAt.ToLocalTime().ToString("MM/dd HH:mm:ss") + "  ·  " + HistorySourceName(point.Source)
                + (point.ResolutionMinutes > 0 ? " · " + point.ResolutionMinutes + "분 묶음" : ""),13,"#647070",FontWeights.Normal,new Thickness(0,6,0,0)));
            grid.Children.Add(details);
            var value = DashboardText(HistoryValue(point),16,"#252827",FontWeights.SemiBold);
            value.HorizontalAlignment = HorizontalAlignment.Right; value.VerticalAlignment = VerticalAlignment.Center;
            value.TextAlignment = TextAlignment.Right; Grid.SetColumn(value,1); grid.Children.Add(value);
            return new LocalizedBorder { Child = grid, BorderBrush = BrushOf("#D6D9D7"), BorderThickness = new Thickness(0,0,0,1) };
        }

        private void ExportDashboardHistory()
        {
            if (!ProFeaturesAvailable) { LockDashboardHistory(); return; }
            if (dashboardWindow == null || dashboardHistoryFiltered.Count == 0 || exiting) return;
            var journal = usageJournal;
            string account = ServiceReadingOwner;
            var points = dashboardHistoryFiltered.ToList();
            var owner = dashboardWindow;
            var save = new SaveFileDialog { Title = L.Text("현재 필터의 사용 이력 내보내기"), Filter = L.Text("CSV 파일 (*.csv)|*.csv"), DefaultExt = ".csv", AddExtension = true,
                FileName = "Nephylo-" + (demo ? "Sample-" : "") + "History-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv", OverwritePrompt = true };
            if (save.ShowDialog(owner) != true) return;
            // A modal save dialog pumps messages; local journal/lifecycle changes
            // during it must not export a stale captured snapshot.
            if (!ProFeaturesAvailable || exiting || owner != dashboardWindow || !Object.ReferenceEquals(journal, usageJournal)
                || !String.Equals(account, ServiceReadingOwner, StringComparison.Ordinal)) return;
            try {
                var output = new StringBuilder();
                output.AppendLine(L.Text("서비스,항목,출처,확인시각(시간대포함),남은비율(%),잔량,한도,초기화예정시각(시간대포함),표시값,단위,묶음간격(분)"));
                foreach (var point in points) {
                    string[] values = { L.Text(HistoryServiceName(point.ServiceId)), point.Source == "manual" ? point.Label : L.Text(point.Label), L.Text((demo ? "예시 · " : "") + HistorySourceName(point.Source)),
                        point.ObservedAt.ToLocalTime().ToString("o", CultureInfo.InvariantCulture),
                        point.RemainingPercent.HasValue ? point.RemainingPercent.Value.ToString("R", CultureInfo.InvariantCulture) : "",
                        point.Remaining.HasValue ? point.Remaining.Value.ToString("R", CultureInfo.InvariantCulture) : "",
                        point.Limit.HasValue ? point.Limit.Value.ToString("R", CultureInfo.InvariantCulture) : "",
                        point.ResetsAt.HasValue ? point.ResetsAt.Value.ToLocalTime().ToString("o", CultureInfo.InvariantCulture) : "",
                        L.Text(HistoryValue(point)), L.Text(UsageHistoryChart.MetricUnit(point)), point.ResolutionMinutes.ToString(CultureInfo.InvariantCulture) };
                    output.AppendLine(String.Join(",", values.Select(EscapeHistoryCsv)));
                }
                File.WriteAllText(save.FileName, output.ToString(), new UTF8Encoding(true));
                if (dashboardHistoryStatus != null) dashboardHistoryStatus.Text = (demo ? "예시 데이터 " : "") + points.Count.ToString("N0") + "개를 CSV로 저장했습니다.";
            }
            catch { if (dashboardHistoryStatus != null) dashboardHistoryStatus.Text = "CSV를 저장하지 못했습니다. 파일이 열려 있으면 닫거나 다른 저장 위치를 선택해 주세요."; }
        }
    }
}
