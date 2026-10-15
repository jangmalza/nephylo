using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private Window historyWindow, notificationWindow;
        private ComboBox historyService, historyMetric, historyRange;
        private TextBlock historyInfo, historyCount, historyChartCaption;
        private StackPanel historyRows;
        private StackPanel recentNoticeRows;
        private TextBlock recentNoticeCount;
        private Expander recentNoticesExpander;
        private UsageHistoryChart historyChart;
        private Button historyExport;
        private bool historyUpdating;
        private List<UsageHistoryPoint> historyFiltered = new List<UsageHistoryPoint>();

        private static TextBlock HistoryText(string text, double size, string color, FontWeight weight, Thickness? margin = null)
        {
            var block = T(text, Math.Max(13, size), color, weight, margin);
            block.LineHeight = Math.Ceiling(block.FontSize * 1.45);
            block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            return block;
        }

        private sealed class HistoryMetricChoice
        {
            public string ServiceId, Label;
            public bool IncludeService, Manual;
            public override string ToString() { return Label == null ? L.Text("모든 항목") : (IncludeService ? L.Text(HistoryServiceName(ServiceId)) + " · " : "") + (Manual ? Label : L.Text(Label)); }
        }

        private static string HistoryServiceName(string id)
        {
            switch (id) {
                case "codex": return "Codex";
                case "elevenlabs": case "eleven": return "ElevenLabs";
                case "openai": return "OpenAI API";
                case "claude": return "Claude";
                case "gemini": return "Gemini API";
                case "cursor": return "Cursor";
                case "higgsfield": return "Higgsfield 웹";
                case "higgsfield-api": return "Higgsfield API";
                default: return String.IsNullOrWhiteSpace(id) ? "서비스" : id;
            }
        }

        internal static string UsageMetricTitle(string serviceId, string label, bool manual)
        {
            string service = L.Text(HistoryServiceName(serviceId));
            string metric = manual ? label ?? "" : L.Text(label ?? "");
            // Display only: keep persisted identities and user-entered labels intact.
            if (!manual && (String.Equals(metric,service,StringComparison.OrdinalIgnoreCase)
                || metric.StartsWith(service + " · ",StringComparison.OrdinalIgnoreCase))) return metric;
            return service + " · " + metric;
        }

        private static string HistorySourceName(string source)
        {
            if (UsageSourceIdentity.IsWindowsConfig(source)) return L.Text("CODE · 수신") + " · Windows " + source.Substring(source.Length - 64,8);
            if (UsageSourceIdentity.IsWsl(source)) {
                string kind = source.StartsWith("api:", StringComparison.Ordinal) ? "api" : "code";
                return L.Text(HistorySourceName(kind)) + " · WSL " + source.Substring(source.Length - 64, 8);
            }
            switch (source) {
                case "api": return "API · 자동 조회";
                case "code": return "CODE · 수신";
                case "cli": return "CLI · 자동 조회";
                case "manual": return "수동 기록";
                default: return "수집 기록";
            }
        }

        private void SaveUsageOptions(UsageTrackingOptions options)
        {
            if (exiting) throw new InvalidOperationException("종료를 준비하고 있습니다. 잠시 기다려 주세요.");
            usageJournal.SaveOptions(options);
            var savedOptions = usageJournal.Options;
            pendingUsageNotices.RemoveAll(notice => notice.Kind == "forecast" || !UsageNoticeEnabled(notice,savedOptions));
            CloseUsageAnalysisPopups(); RefreshUsageHistoryIfOpen();
        }

        private void ClearUsageHistory()
        {
            if (exiting) throw new InvalidOperationException("종료를 준비하고 있습니다. 잠시 기다려 주세요.");
            usageJournal.ClearHistory();
            pendingUsageNotices.RemoveAll(notice => notice.Kind == "forecast");
            CloseUsageAnalysisPopups(); RefreshUsageHistoryIfOpen();
        }

        private void OpenUsageHistory()
        {
            if (historyWindow != null) { historyWindow.Activate(); return; }
            var dialog = CreateManagementDialog(demo ? "사용 이력 · 예시 데이터" : "사용 이력", 800, 780, 620);
            historyWindow = dialog;
            StackPanel actions;
            var body = ManagementBody(dialog, "사용 이력", demo ? "미리보기 · 예시 데이터입니다. 실제 사용량이 아니며 PC에 저장하지 않습니다." : "이 PC에서 확인한 값을 기록합니다. 과거 사용량을 소급해서 가져오지는 않습니다.", out actions);
            var clear = ManagementAction("기록 지우기", null);
            clear.Margin = new Thickness(0, 0, 16, 0);
            clear.Click += async (s, e) => {
                if (LocalizedMessageBox.Show(dialog, "이 PC에 저장된 모든 서비스의 사용 이력을 지울까요?\n지운 기록은 복구할 수 없습니다. 서비스 연결과 알림 설정은 유지됩니다.",
                    "사용 이력 지우기", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                clear.IsEnabled = false;
                try { ClearUsageHistory(); await usageJournal.FlushAsync(); RefreshUsageHistoryIfOpen(); }
                catch { if (historyInfo != null) historyInfo.Text = "기록 삭제를 파일에 저장하지 못했습니다. 저장 폴더 권한을 확인한 뒤 다시 시도해 주세요."; }
                finally { clear.IsEnabled = true; }
            };
            actions.Children.Add(clear);
            var close = ManagementAction("닫기", () => dialog.Close()); close.IsCancel = true;
            historyExport = ManagementAction("CSV 내보내기", null, true); historyExport.IsEnabled = false;
            historyExport.Click += (s, e) => ExportUsageHistory();
            actions.Children.Add(historyExport); actions.Children.Add(close);
            var noticeSection = ManagementSection(body, null);
            recentNoticeCount = HistoryText("최근 알림", 16, "#252827", FontWeights.SemiBold);
            recentNoticeRows = new LocalizedStackPanel();
            var noticeContent = new LocalizedStackPanel();
            noticeContent.Children.Add(HistoryText("모든 서비스의 최근 30일 알림을 최대 100개 보관합니다. Windows에서 실제로 표시되었는지는 확인할 수 없습니다.", 12, "#566369", FontWeights.Normal, new Thickness(0, 10, 0, 8)));
            noticeContent.Children.Add(new ScrollViewer { Content = recentNoticeRows, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var clearNotices = ManagementAction("알림 기록 지우기", null);
            clearNotices.HorizontalAlignment = HorizontalAlignment.Left; clearNotices.Margin = new Thickness(0, 8, 0, 0);
            clearNotices.Click += async (s, e) => {
                if (exiting) return;
                if (LocalizedMessageBox.Show(dialog, "알림 기록을 지울까요? 사용 이력과 알림 설정은 유지됩니다.", "알림 기록 지우기", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                clearNotices.IsEnabled = false;
                try { usageJournal.ClearNotices(); await usageJournal.FlushAsync(); RefreshRecentUsageNotices(); }
                catch { if (historyInfo != null) historyInfo.Text = "알림 기록 삭제를 저장하지 못했습니다. 저장 공간과 폴더 권한을 확인해 주세요."; }
                finally { clearNotices.IsEnabled = true; }
            };
            noticeContent.Children.Add(clearNotices);
            recentNoticesExpander = new LocalizedExpander { Header = recentNoticeCount, Content = noticeContent };
            noticeSection.Children.Add(recentNoticesExpander);
            var filterSection = ManagementSection(body, "기록 필터");
            var filters = new LocalizedGrid();
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star) });
            filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            historyService = HistoryFilter(filters, "서비스", 0);
            historyMetric = HistoryFilter(filters, "항목", 1);
            historyRange = HistoryFilter(filters, "기간", 2);
            historyRange.Items.Add(new LocalizedComboBoxItem { Content = "24시간", Tag = 1 });
            historyRange.Items.Add(new LocalizedComboBoxItem { Content = "7일", Tag = 7 });
            historyRange.Items.Add(new LocalizedComboBoxItem { Content = "30일", Tag = 30 });
            historyRange.SelectedIndex = 1; filterSection.Children.Add(filters);
            filters.SizeChanged += (s, e) => ArrangeHistoryFilters(filters);
            historyAnalysis = ManagementSection(body,null);
            var chartSection = ManagementSection(body, null);
            historyChartCaption = HistoryText("잔량 추이", 16, "#252827", FontWeights.SemiBold, new Thickness(0, 0, 0, 8));
            chartSection.Children.Add(historyChartCaption);
            historyChart = new UsageHistoryChart { Height = 220, HorizontalAlignment = HorizontalAlignment.Stretch, AnimationsEnabled = settings.AnimationsEnabled, ReadableDashboardText = true };
            AutomationProperties.SetName(historyChart, "기록된 잔량 추이 · 항목별 백분율, 크레딧 또는 USD");
            chartSection.Children.Add(new LocalizedBorder { Child = historyChart, Background = BrushOf("#F2F3EF"), CornerRadius = new CornerRadius(3), Padding = new Thickness(8) });
            chartSection.Children.Add(HistoryText("점은 확인된 값입니다. 수동 기록, 초기화 시각, 수집 간 공백은 선으로 연결하지 않습니다.", 10, "#647070", FontWeights.Normal, new Thickness(0, 8, 0, 0)));
            var recordSection = ManagementSection(body, null);
            historyCount = HistoryText("최근 기록", 16, "#252827", FontWeights.SemiBold, new Thickness(0, 0, 0, 9)); recordSection.Children.Add(historyCount);
            historyInfo = HistoryText("", 11, "#647070", FontWeights.Normal, new Thickness(0, 0, 0, 12)); recordSection.Children.Add(historyInfo);
            historyRows = new LocalizedStackPanel(); recordSection.Children.Add(historyRows);
            body.Children.Add(HistoryText("최근 24시간은 상세 기록, 이전 29일은 시간대별 마지막 확인값을 보관합니다. 기록이 많으면 간격을 넓혀 30일 범위를 유지합니다.", 11, "#647070", FontWeights.Normal, new Thickness(0, 0, 0, 8)));
            historyService.SelectionChanged += (s, e) => { if (!historyUpdating) UpdateHistoryView(true); };
            historyMetric.SelectionChanged += (s, e) => { if (!historyUpdating) UpdateHistoryView(false); };
            historyRange.SelectionChanged += (s, e) => { if (!historyUpdating) UpdateHistoryView(false); };
            dialog.Closed += (s, e) => {
                historyWindow = null; historyService = null; historyMetric = null; historyRange = null;
                historyRows = null; historyChart = null; historyExport = null; historyInfo = null;
                historyCount = null; historyChartCaption = null; historyFiltered.Clear();
                historyAnalysis = null;
                recentNoticeRows = null; recentNoticeCount = null; recentNoticesExpander = null;
            };
            UpdateHistoryView(true); dialog.Show();
        }

        private ComboBox HistoryFilter(Grid grid, string label, int column)
        {
            var panel = new LocalizedStackPanel { Margin = new Thickness(column == 0 ? 0 : 10, 0, 0, 0) };
            panel.Children.Add(HistoryText(label, 11, "#647070", FontWeights.Normal, new Thickness(0, 0, 0, 6)));
            var combo = new LocalizedComboBox { FontSize = 14, MinHeight = 38, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(combo, "이력 " + label);
            panel.Children.Add(combo); Grid.SetColumn(panel, column); grid.Children.Add(panel);
            return combo;
        }

        private static void ArrangeHistoryFilters(Grid filters)
        {
            bool narrow = filters.ActualWidth < 600;
            filters.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            filters.ColumnDefinitions[1].Width = new GridLength(1.35, GridUnitType.Star);
            filters.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(0.75, GridUnitType.Star);
            for (int index = 0; index < filters.Children.Count; index++) {
                var panel = (FrameworkElement)filters.Children[index];
                Grid.SetRow(panel, narrow && index == 2 ? 1 : 0);
                Grid.SetColumn(panel, narrow && index == 2 ? 0 : index);
                Grid.SetColumnSpan(panel, narrow && index == 2 ? 2 : 1);
                panel.Margin = new Thickness(index == 1 || index == 2 && !narrow ? 10 : 0, narrow && index == 2 ? 12 : 0, 0, 0);
            }
        }

        private void RefreshUsageHistoryIfOpen()
        {
            RefreshDashboardIfOpen();
            if (historyWindow != null) UpdateHistoryView(false);
        }

        private void UpdateHistoryView(bool resetMetric)
        {
            if (historyWindow == null || historyUpdating) return;
            historyUpdating = true;
            try {
                historyChart.AnimationsEnabled = settings.AnimationsEnabled;
                RefreshRecentUsageNotices();
                var points = usageJournal.GetHistory().OrderBy(p => p.ObservedAt).ToList();
                var selectedService = historyService.SelectedItem as ComboBoxItem;
                string service = selectedService == null ? null : selectedService.Tag as string;
                var ids = points.Select(p => p.ServiceId).Distinct().OrderBy(HistoryServiceName).ToList();
                if (historyService.Items.Count == 0 || !ids.SequenceEqual(historyService.Items.Cast<ComboBoxItem>().Skip(1).Select(item => (string)item.Tag))) {
                    historyService.Items.Clear(); historyService.Items.Add(new LocalizedComboBoxItem { Content = "모든 서비스", Tag = "" });
                    foreach (var id in ids) historyService.Items.Add(new LocalizedComboBoxItem { Content = HistoryServiceName(id), Tag = id });
                    if (service == null) service = points.Count > 0 ? points[points.Count - 1].ServiceId : "";
                    historyService.SelectedItem = historyService.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == service) ?? historyService.Items[0];
                    service = (string)((ComboBoxItem)historyService.SelectedItem).Tag;
                }
                var servicePoints = String.IsNullOrEmpty(service) ? points : points.Where(p => p.ServiceId == service).ToList();
                var selectedMetric = historyMetric.SelectedItem as HistoryMetricChoice;
                var metrics = servicePoints.Select(p => new HistoryMetricChoice { ServiceId = p.ServiceId, Label = p.Label, Manual = !demo && p.Source == "manual", IncludeService = String.IsNullOrEmpty(service) }).GroupBy(p => p.ServiceId + "\n" + p.Label).Select(g => g.First()).OrderBy(p => p.Label).ToList();
                string metricKey = selectedMetric == null || selectedMetric.Label == null ? null : selectedMetric.ServiceId + "\n" + selectedMetric.Label;
                bool metricsChanged = !metrics.Select(m => m.ServiceId + "\n" + m.Label).SequenceEqual(historyMetric.Items.Cast<HistoryMetricChoice>().Skip(1).Select(m => m.ServiceId + "\n" + m.Label));
                if (resetMetric || historyMetric.Items.Count == 0 || metricsChanged) {
                    historyMetric.Items.Clear(); historyMetric.Items.Add(new HistoryMetricChoice());
                    foreach (var metric in metrics) historyMetric.Items.Add(metric);
                    var previous = metrics.FirstOrDefault(m => m.ServiceId + "\n" + m.Label == metricKey);
                    historyMetric.SelectedItem = previous != null ? previous : resetMetric && metrics.Count > 0 && !String.IsNullOrEmpty(service) ? metrics[0] : historyMetric.Items[0];
                }
                selectedMetric = historyMetric.SelectedItem as HistoryMetricChoice;
                var now = DateTimeOffset.Now;
                RefreshHistoryAnalysis(selectedMetric,points,now);
                int days = (int)((ComboBoxItem)historyRange.SelectedItem).Tag;
                var start = now.AddDays(-days);
                historyFiltered = servicePoints.Where(p => p.ObservedAt >= start && p.ObservedAt <= now && (selectedMetric == null || selectedMetric.Label == null || (p.ServiceId == selectedMetric.ServiceId && p.Label == selectedMetric.Label))).ToList();
                var numericPoints = historyFiltered.Where(p => UsageHistoryChart.MetricValue(p).HasValue).ToList();
                int seriesCount = historyFiltered.Select(p => p.ServiceId + "\n" + p.Label).Distinct().Count();
                string chartMessage = historyFiltered.Count == 0 ? "이 기간에 확인한 기록이 없습니다." : seriesCount > 1 ? "항목을 하나 선택하면 잔량 추이를 표시합니다." : numericPoints.Count == 0 ? "그래프로 표시할 수치가 없습니다. 아래 기록을 확인하세요." : numericPoints.Select(UsageHistoryChart.MetricUnit).Distinct().Count() > 1 ? "기록의 단위가 변경되었습니다. 아래에서 개별 수치를 확인하세요." : null;
                historyChart.SetPoints(chartMessage == null ? historyFiltered : new List<UsageHistoryPoint>(), start, now, Math.Max(30, settings.RefreshSeconds / 60.0 * 3), chartMessage);
                string name = selectedMetric != null && selectedMetric.Label != null ? selectedMetric.Label : "잔량";
                historyChartCaption.Text = name + " 추이";
                if (chartMessage == null && numericPoints.Count > 0) {
                    var latest = numericPoints[numericPoints.Count - 1];
                    historyChartCaption.Text = UiL.Source(historyChartCaption,TextBlock.TextProperty) + " · " + UsageHistoryChart.MetricUnit(latest) + " · 최근 " + UsageHistoryChart.MetricValue(latest).Value.ToString("0.##", CultureInfo.CurrentCulture);
                }
                var info = new List<string>();
                if (!String.IsNullOrWhiteSpace(usageTrackingError)) info.Add(usageTrackingError);
                if (!usageJournal.Options.HistoryEnabled) info.Add("이력 기록이 꺼져 있습니다. 기존 기록만 표시합니다.");
                if (historyFiltered.Any(p => p.ResolutionMinutes > 0)) info.Add("묶음 기록은 해당 시간대의 마지막 실제 확인값입니다. 평균이나 추정 사용량이 아닙니다.");
                if (points.Count == 0) info.Add("기록은 지금부터 쌓입니다. 서비스가 새로 확인되거나 수동 기록을 저장하면 표시되며, 과거 사용량은 복원하지 않습니다.");
                else if (historyFiltered.Count == 0) info.Add("서비스·항목·기간을 바꾸면 다른 기록을 확인할 수 있습니다.");
                else info.Add("확인 시각은 이 PC의 현지 시간입니다. CSV에는 현재 필터의 전체 기록을 담습니다.");
                historyInfo.Text = String.Join("\n", info);
                historyExport.IsEnabled = historyFiltered.Count > 0;
                historyCount.Text = "최근 기록 · " + historyFiltered.Count.ToString("N0") + "개" + (historyFiltered.Count > 150 ? " 중 최근 150개 표시" : "");
                historyRows.Children.Clear();
                foreach (var point in historyFiltered.AsEnumerable().Reverse().Take(150)) historyRows.Children.Add(HistoryRow(point));
            }
            catch {
                if (historyAnalysis != null) historyAnalysis.Children.Clear();
                historyFiltered.Clear(); historyExport.IsEnabled = false; historyRows.Children.Clear();
                historyInfo.Text = "사용 이력을 읽지 못했습니다. 잠시 뒤 다시 열어 주세요. 저장된 기록은 변경하지 않았습니다.";
                historyChart.SetPoints(new List<UsageHistoryPoint>(), DateTimeOffset.Now.AddDays(-7), DateTimeOffset.Now, 30, "사용 이력을 불러오지 못했습니다.");
            }
            finally { historyUpdating = false; }
        }

        private void RefreshRecentUsageNotices()
        {
            if (recentNoticeRows == null || usageJournal == null) return;
            var notices = usageJournal.GetRecentNotices();
            recentNoticeCount.Text = "최근 알림 · " + notices.Count + "개";
            recentNoticeRows.Children.Clear();
            if (notices.Count == 0) recentNoticeRows.Children.Add(HistoryText("아직 알림 기록이 없습니다.", 12, "#566369", FontWeights.Normal, new Thickness(0, 8, 0, 8)));
            foreach (var notice in notices) {
                var row = new LocalizedStackPanel { Margin = new Thickness(0, 8, 4, 10) };
                string kind = UsageNoticeTitle(notice.Kind);
                row.Children.Add(HistoryText(HistoryServiceName(notice.ServiceId) + " · " + kind, 14, "#252827", FontWeights.SemiBold));
                row.Children.Add(HistoryText(notice.Message, 14, "#566369", FontWeights.Normal, new Thickness(0, 4, 0, 0)));
                string time = notice.CreatedAt.ToLocalTime().ToString("MM/dd HH:mm") + " · " + L.Text("알림 확인 시각");
                if (notice.ScheduledAt.HasValue) time += " / " + notice.ScheduledAt.Value.ToLocalTime().ToString("MM/dd HH:mm") + " · " + L.Text("예정 시각");
                row.Children.Add(HistoryText(time, 11, "#647070", FontWeights.Normal, new Thickness(0, 5, 0, 0)));
                recentNoticeRows.Children.Add(row);
            }
        }

        private void OpenRecentUsageNotices()
        {
            OpenUsageHistory();
            if (recentNoticesExpander != null) { recentNoticesExpander.IsExpanded = true; recentNoticesExpander.BringIntoView(); }
        }

        private FrameworkElement HistoryRow(UsageHistoryPoint point)
        {
            var grid = new LocalizedGrid { Margin = new Thickness(0, 14, 0, 14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var details = new LocalizedStackPanel { Margin = new Thickness(0, 0, 16, 0) };
            var title = HistoryText(HistoryServiceName(point.ServiceId) + " · " + point.Label, 14, "#252827", FontWeights.SemiBold);
            if (!demo && point.Source == "manual") { title.Text = L.Text(HistoryServiceName(point.ServiceId)) + " · " + point.Label; UiL.Raw(title); }
            details.Children.Add(title);
            details.Children.Add(HistoryText(point.ObservedAt.ToLocalTime().ToString("MM/dd HH:mm:ss") + "  ·  " + HistorySourceName(point.Source) + (point.ResolutionMinutes > 0 ? " · " + point.ResolutionMinutes + "분 묶음" : ""), 10, "#647070", FontWeights.Normal, new Thickness(0, 5, 0, 0)));
            grid.Children.Add(details);
            var value = HistoryText(HistoryValue(point), 16, "#252827", FontWeights.SemiBold);
            value.HorizontalAlignment = HorizontalAlignment.Right; value.VerticalAlignment = VerticalAlignment.Center;
            value.TextAlignment = TextAlignment.Right; Grid.SetColumn(value, 1); grid.Children.Add(value);
            grid.SizeChanged += (s, e) => {
                bool narrow = grid.ActualWidth < 500;
                Grid.SetColumnSpan(details, narrow ? 2 : 1);
                details.Margin = new Thickness(0, 0, narrow ? 0 : 16, 0);
                Grid.SetRow(value, narrow ? 1 : 0); Grid.SetColumn(value, narrow ? 0 : 1);
                Grid.SetColumnSpan(value, narrow ? 2 : 1);
                value.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                value.TextAlignment = narrow ? TextAlignment.Left : TextAlignment.Right;
                value.Margin = new Thickness(0, narrow ? 8 : 0, 0, 0);
            };
            return new LocalizedBorder { Child = grid, BorderBrush = BrushOf("#D6D9D7"), BorderThickness = new Thickness(0, 0, 0, 1) };
        }

        private static string HistoryValue(UsageHistoryPoint point)
        {
            if (!String.IsNullOrWhiteSpace(point.DisplayValue)) return point.DisplayValue + (point.ServiceId == "higgsfield" ? " 크레딧" : point.ServiceId == "higgsfield-api" ? " USD" : "");
            if (point.RemainingPercent.HasValue) return point.RemainingPercent.Value.ToString("0.#", CultureInfo.CurrentCulture) + "%";
            if (point.Remaining.HasValue) return point.Remaining.Value.ToString("N2", CultureInfo.CurrentCulture);
            return "—";
        }

        // Quoted CSV alone does not neutralize spreadsheet formulas, including
        // formulas preceded by whitespace. Prefix those text cells with a quote.
        internal static string EscapeHistoryCsv(string value)
        {
            string text = value ?? "";
            string trimmed = text.TrimStart();
            if ((trimmed.Length > 0 && "=+-@".IndexOf(trimmed[0]) >= 0) || (text.Length > 0 && (text[0] == '\t' || text[0] == '\r' || text[0] == '\n'))) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private void ExportUsageHistory()
        {
            if (historyWindow == null || historyFiltered.Count == 0) return;
            var save = new SaveFileDialog { Title = L.Text("현재 필터의 사용 이력 내보내기"), Filter = L.Text("CSV 파일 (*.csv)|*.csv"), DefaultExt = ".csv", AddExtension = true,
                FileName = "Nephylo-" + (demo ? "Sample-" : "") + "History-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv", OverwritePrompt = true };
            if (save.ShowDialog(historyWindow) != true) return;
            try {
                var output = new StringBuilder();
                output.AppendLine(L.Text("서비스,항목,출처,확인시각(시간대포함),남은비율(%),잔량,한도,초기화예정시각(시간대포함),표시값,단위,묶음간격(분)"));
                foreach (var point in historyFiltered) {
                    string[] values = { L.Text(HistoryServiceName(point.ServiceId)), point.Source == "manual" ? point.Label : L.Text(point.Label), L.Text((demo ? "예시 · " : "") + HistorySourceName(point.Source)),
                        point.ObservedAt.ToLocalTime().ToString("o", CultureInfo.InvariantCulture),
                        point.RemainingPercent.HasValue ? point.RemainingPercent.Value.ToString("R", CultureInfo.InvariantCulture) : "",
                        point.Remaining.HasValue ? point.Remaining.Value.ToString("R", CultureInfo.InvariantCulture) : "",
                        point.Limit.HasValue ? point.Limit.Value.ToString("R", CultureInfo.InvariantCulture) : "",
                        point.ResetsAt.HasValue ? point.ResetsAt.Value.ToLocalTime().ToString("o", CultureInfo.InvariantCulture) : "", L.Text(HistoryValue(point)), L.Text(UsageHistoryChart.MetricUnit(point)), point.ResolutionMinutes.ToString(CultureInfo.InvariantCulture) };
                    output.AppendLine(String.Join(",", values.Select(EscapeHistoryCsv)));
                }
                File.WriteAllText(save.FileName, output.ToString(), new UTF8Encoding(true));
                historyInfo.Text = (demo ? "예시 데이터 " : "") + historyFiltered.Count.ToString("N0") + "개를 CSV로 저장했습니다.";
            }
            catch { historyInfo.Text = "CSV를 저장하지 못했습니다. 파일이 열려 있으면 닫거나 다른 저장 위치를 선택해 주세요."; }
        }

        private void OpenNotificationSettings()
        {
            if (notificationWindow != null) { notificationWindow.Activate(); return; }
            BuildNotificationSettingsDialog().Show();
        }

        private Window BuildNotificationSettingsDialog()
        {
            var dialog = CreateManagementDialog("알림 · 이력 설정", 560, 760);
            notificationWindow = dialog;
            StackPanel actions;
            var body = ManagementBody(dialog, "알림 · 이력", "필요한 순간만 알려드리고, 확인한 사용량을 보관합니다.", out actions);
            var cancel = ManagementAction("취소", () => dialog.Close()); cancel.IsCancel = true;
            var save = ManagementAction("저장", null, true);
            actions.Children.Add(cancel); actions.Children.Add(save);
            var options = usageJournal.Options;
            var lowSection = ManagementSection(body, "잔량 알림");
            var low = new LocalizedCheckBox { IsChecked = options.LowEnabled };
            AutomationProperties.SetName(low, "잔량 부족 알림");
            ManagementRow(lowSection, "잔량 부족 알림", "같은 부족 상태를 반복해서 알리지 않습니다.", low);
            var threshold = new LocalizedComboBox { Width = 180, FontSize = 14, MinHeight = 38, IsEnabled = options.LowEnabled };
            foreach (int n in new[] { 10, 20, 30, options.LowThresholdPercent }.Distinct().OrderBy(n => n)) threshold.Items.Add(new LocalizedComboBoxItem { Content = "잔량 " + n + "% 이하", Tag = n });
            threshold.SelectedItem = threshold.Items.Cast<ComboBoxItem>().First(item => (int)item.Tag == options.LowThresholdPercent);
            AutomationProperties.SetName(threshold, "잔량 부족 알림 기준");
            low.Checked += (s, e) => threshold.IsEnabled = true; low.Unchecked += (s, e) => threshold.IsEnabled = false;
            ManagementRow(lowSection, "알림 기준", "백분율이 있는 항목에 적용합니다.", threshold);
            var critical = new LocalizedComboBox { Width = 180, FontSize = 14, MinHeight = 38, IsEnabled = options.LowEnabled };
            AutomationProperties.SetName(critical,"긴급 잔량 알림 기준");
            Action updateCritical = () => {
                var selected = critical.SelectedItem as ComboBoxItem;
                int previous = selected == null ? options.CriticalThresholdPercent : (int)selected.Tag;
                int lowValue = (int)((ComboBoxItem)threshold.SelectedItem).Tag;
                critical.Items.Clear();
                foreach (int n in new[] { 0, 5, 10, 15, 20, previous }.Where(n => n >= 0 && n < lowValue).Distinct().OrderBy(n => n))
                    critical.Items.Add(new LocalizedComboBoxItem { Content = n == 0 ? "긴급 단계 끄기" : "잔량 " + n + "% 이하", Tag = n });
                critical.SelectedItem = critical.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (int)item.Tag == previous)
                    ?? critical.Items.Cast<ComboBoxItem>().Last();
            };
            threshold.SelectionChanged += (s,e) => updateCritical(); updateCritical();
            low.Checked += (s,e) => critical.IsEnabled = true; low.Unchecked += (s,e) => critical.IsEnabled = false;
            ManagementRow(lowSection,"긴급 기준","부족 알림 이후 더 낮아지면 한 번 더 알립니다. 0% 소진 알림은 별도입니다.",critical);
            var absoluteSection = new LocalizedStackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var advanced = new LocalizedExpander { Header = "서비스별 알림 기준", Content = absoluteSection,
                IsExpanded = options.HiggsfieldCreditsThreshold.HasValue || options.HiggsfieldApiUsdThreshold.HasValue };
            lowSection.Children.Add(advanced);
            var creditEnabled = new LocalizedCheckBox { Content = "Higgsfield 웹 · 크레딧 기준", IsChecked = options.HiggsfieldCreditsThreshold.HasValue };
            var creditThreshold = AbsoluteThresholdEditor(absoluteSection, creditEnabled, options.HiggsfieldCreditsThreshold, "크레딧 이하");
            var usdEnabled = new LocalizedCheckBox { Content = "Higgsfield API · 달러 기준", IsChecked = options.HiggsfieldApiUsdThreshold.HasValue };
            var usdThreshold = AbsoluteThresholdEditor(absoluteSection, usdEnabled, options.HiggsfieldApiUsdThreshold, "USD 이하");
            Action updateAbsolute = () => {
                creditEnabled.IsEnabled = usdEnabled.IsEnabled = low.IsChecked == true;
                creditThreshold.IsEnabled = low.IsChecked == true && creditEnabled.IsChecked == true;
                usdThreshold.IsEnabled = low.IsChecked == true && usdEnabled.IsChecked == true;
            };
            low.Checked += (s,e) => updateAbsolute(); low.Unchecked += (s,e) => updateAbsolute();
            creditEnabled.Checked += (s,e) => updateAbsolute(); creditEnabled.Unchecked += (s,e) => updateAbsolute();
            usdEnabled.Checked += (s,e) => updateAbsolute(); usdEnabled.Unchecked += (s,e) => updateAbsolute(); updateAbsolute();
            absoluteSection.Children.Add(HistoryText("API 달러 잔액은 새로 저장한 수동 입력값으로만 알립니다. 자동 사용 감지는 지원하지 않습니다.", 11, "#647070", FontWeights.Normal, new Thickness(0, 0, 0, 4)));
            var resetSection = ManagementSection(body, "초기화 알림");
            var reset = new LocalizedCheckBox { IsChecked = options.ResetEnabled };
            AutomationProperties.SetName(reset, "초기화 예정 시각 알림");
            ManagementRow(resetSection, "초기화 예정 시각 알림", "서비스가 알려준 예정 시각이 지나면 알려드립니다. 실제 한도 충전 여부는 새로고침 후 확인하세요.", reset);
            resetSection.Children.Add(HistoryText("절전·종료 중 놓친 일정은 다시 실행할 때 한 번에 안내합니다. 이미 알고 있던 최근 30일 일정만 대상으로 합니다.", 12, "#566369", FontWeights.Normal, new Thickness(0, 0, 0, 8)));
            var historySection = ManagementSection(body, "사용 이력");
            var history = new LocalizedCheckBox { IsChecked = options.HistoryEnabled };
            AutomationProperties.SetName(history, "사용 이력 기록");
            ManagementRow(historySection, "사용 이력 기록", "최근 30일의 확인값을 이 PC에 보관합니다. 끄면 새 기록을 남기지 않습니다.", history);
            var forecast = new LocalizedCheckBox { IsChecked = options.ForecastEnabled, IsEnabled = options.HistoryEnabled };
            AutomationProperties.SetName(forecast,"소진 예상 알림");
            ManagementRow(historySection,"소진 예상 알림","최근 7일의 관측 추세에서 초기화 전에 소진될 것으로 예상하면 알립니다. 이력 기록이 필요합니다.",forecast);
            historySection.Children.Add(HistoryText("위험이 커지거나 회복 후 다시 부족해질 때만 재알림합니다. 소진 예상 알림 사이에는 최소 6시간 간격을 둡니다.",11,"#647070",FontWeights.Normal,new Thickness(0,0,0,8)));
            history.Checked += (s,e) => forecast.IsEnabled = true; history.Unchecked += (s,e) => forecast.IsEnabled = false;
            historySection.Children.Add(SmallAction("사용 이력 보기", OpenUsageHistory));
            historySection.Children.Add(SmallAction("최근 알림 보기", OpenRecentUsageNotices));
            var testSection = ManagementSection(body, "알림 확인", "알림은 위젯이 실행 중일 때만 작동합니다. 트레이에 숨겨도 받을 수 있으며, Windows 알림 설정·방해 금지에 따라 표시되지 않을 수 있습니다.");
            var test = ManagementAction("알림 테스트", null); test.HorizontalAlignment = HorizontalAlignment.Left; test.Margin = new Thickness(0);
            testSection.Children.Add(test);
            var status = HistoryText(demo ? "미리보기 모드의 설정은 PC에 저장하지 않습니다." : "", 11, "#647070", FontWeights.Normal, new Thickness(0, 12, 0, 0)); body.Children.Add(status);
            test.Click += (s, e) => { try { status.Text = TestUsageNotification(); } catch { status.Text = "테스트 알림을 보내지 못했습니다. Windows 알림 설정을 확인해 주세요."; } };
            save.Click += async (s, e) => {
                save.IsEnabled = false;
                try {
                    int lowValue = (int)((ComboBoxItem)threshold.SelectedItem).Tag;
                    int criticalValue = (int)((ComboBoxItem)critical.SelectedItem).Tag;
                    ValidateUsageAlertThresholds(lowValue,criticalValue);
                    SaveUsageOptions(new UsageTrackingOptions { LowEnabled = low.IsChecked == true, ResetEnabled = reset.IsChecked == true,
                        HistoryEnabled = history.IsChecked == true, ForecastEnabled = forecast.IsChecked == true,
                        LowThresholdPercent = lowValue, CriticalThresholdPercent = criticalValue,
                        HiggsfieldCreditsThreshold = creditEnabled.IsChecked == true ? ParseAbsoluteThreshold(creditThreshold.Text, "웹 크레딧") : (double?)null,
                        HiggsfieldApiUsdThreshold = usdEnabled.IsChecked == true ? ParseAbsoluteThreshold(usdThreshold.Text, "API USD") : (double?)null });
                    await usageJournal.FlushAsync();
                    RefreshUsageHistoryIfOpen(); dialog.Close();
                }
                catch (ArgumentException ex) { status.Text = ex.Message; status.BringIntoView(); }
                catch { status.Text = "설정을 저장하지 못했습니다. 저장 폴더의 쓰기 권한을 확인하고 다시 시도해 주세요."; status.BringIntoView(); }
                finally { save.IsEnabled = true; }
            };
            dialog.Closed += (s, e) => notificationWindow = null;
            return dialog;
        }

        private TextBox AbsoluteThresholdEditor(StackPanel body, CheckBox enabled, double? value, string unit)
        {
            body.Children.Add(enabled);
            var line = new LocalizedStackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 0, 0, 12) };
            var input = new LocalizedTextBox { Text = value.GetValueOrDefault(unit == "USD 이하" ? 5 : 20).ToString("0.##", CultureInfo.InvariantCulture), Width = 110 };
            AutomationProperties.SetName(input, enabled.Content + " 알림 기준");
            line.Children.Add(input); line.Children.Add(HistoryText(unit, 12, "#475256", FontWeights.Normal, new Thickness(10, 9, 0, 0)));
            body.Children.Add(line); return input;
        }

        internal static double ParseAbsoluteThreshold(string text, string label)
        {
            double value;
            if (!Double.TryParse((text ?? "").Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) || Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 || value > 1000000000)
                throw new ArgumentException(label + " 기준은 0부터 1,000,000,000 사이의 숫자로 입력해 주세요. 소수점은 마침표(.)를 사용합니다.");
            return value;
        }
    }

    internal sealed class UsageHistoryChart : FrameworkElement
    {
        private List<UsageHistoryPoint> points = new List<UsageHistoryPoint>();
        private readonly List<List<int>> segments = new List<List<int>>();
        private readonly List<int> numericIndexes = new List<int>();
        private DateTimeOffset start, end;
        private double maxGapMinutes;
        private string message;
        private string unit = "%";
        private double maximum = 100;
        private int selectedIndex = -1;
        private bool animationsEnabled, paletteSubscribed, revealPending = true;
        private bool readableDashboardText;
        private string seriesIdentity;
        private static readonly DependencyProperty RevealProperty = DependencyProperty.Register("Reveal", typeof(double), typeof(UsageHistoryChart),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool AnimationsEnabled {
            get { return animationsEnabled; }
            set { animationsEnabled = value; if (!value) StopReveal(); }
        }

        // Reading surfaces opt into larger labels. The default retains the
        // original plot geometry for embedded consumers and chart-only fixtures.
        public bool ReadableDashboardText {
            get { return readableDashboardText; }
            set { if (readableDashboardText == value) return; readableDashboardText = value; InvalidateVisual(); }
        }

        public UsageHistoryChart()
        {
            Focusable = true; Cursor = System.Windows.Input.Cursors.Cross;
            AutomationProperties.SetName(this, L.Text("잔량 추이 그래프"));
            AutomationProperties.SetHelpText(this, L.Text("방향키로 실제 관측값을 확인합니다."));
            Loaded += (s, e) => {
                if (!paletteSubscribed) { WidgetPalette.Changed += PaletteChanged; SystemParameters.StaticPropertyChanged += MotionPreferenceChanged; paletteSubscribed = true; }
                TryReveal();
            };
            Unloaded += (s, e) => {
                if (paletteSubscribed) { WidgetPalette.Changed -= PaletteChanged; SystemParameters.StaticPropertyChanged -= MotionPreferenceChanged; paletteSubscribed = false; }
                StopReveal();
            };
            IsVisibleChanged += (s, e) => { if (!IsVisible) StopReveal(); };
        }
        private void PaletteChanged(object sender, EventArgs args) { InvalidateVisual(); }
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        {
            return new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);
        }
        private void MotionPreferenceChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName != "ClientAreaAnimation" || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() => { if (!SystemParameters.ClientAreaAnimation) StopReveal(); }));
        }
        private void StopReveal() { BeginAnimation(RevealProperty, null); SetValue(RevealProperty, 1.0); }
        private void TryReveal()
        {
            if (!revealPending || !IsLoaded || !IsVisible) return;
            revealPending = false; StopReveal();
            if (!AnimationsEnabled || !SystemParameters.ClientAreaAnimation || numericIndexes.Count == 0 || !String.IsNullOrEmpty(message)) return;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(250)) {
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            BeginAnimation(RevealProperty, fade);
        }

        public void SetPoints(List<UsageHistoryPoint> observations, DateTimeOffset from, DateTimeOffset to, double gapMinutes, string emptyMessage)
        {
            var selected = selectedIndex >= 0 && selectedIndex < points.Count ? points[selectedIndex] : null;
            // Keep a snapshot: subsequent journal changes must not silently alter the rendered path.
            points = (observations ?? new List<UsageHistoryPoint>()).Where(p => p != null).OrderBy(p => p.ObservedAt).Select(p => new UsageHistoryPoint {
                ServiceId = p.ServiceId, Label = p.Label, Source = p.Source, ObservedAt = p.ObservedAt, RemainingPercent = p.RemainingPercent,
                Remaining = p.Remaining, Limit = p.Limit, ResetsAt = p.ResetsAt, ResolutionMinutes = p.ResolutionMinutes, DisplayValue = p.DisplayValue
            }).ToList();
            start = from; end = to > from ? to : from == DateTimeOffset.MaxValue ? from : from.AddSeconds(1);
            if (end == start) start = end.AddSeconds(-1);
            maxGapMinutes = Math.Max(0, gapMinutes); message = emptyMessage;
            AutomationProperties.SetName(this, L.Text("잔량 추이 그래프"));
            AutomationProperties.SetHelpText(this, L.Text("방향키로 실제 관측값을 확인합니다."));
            numericIndexes.Clear(); segments.Clear();
            var visible = points.Where(p => p.ObservedAt >= start && p.ObservedAt <= end && MetricValue(p).HasValue).ToList();
            unit = visible.Count == 0 ? "%" : MetricUnit(visible[0]);
            if (String.IsNullOrEmpty(message) && visible.Select(MetricUnit).Distinct().Count() > 1)
                message = "기록의 단위가 변경되었습니다. 아래에서 개별 수치를 확인하세요.";
            if (String.IsNullOrEmpty(message) && visible.Select(p => p.ServiceId + "\n" + p.Label).Distinct().Count() > 1)
                message = "항목을 하나 선택하면 잔량 추이를 표시합니다.";
            if (String.IsNullOrEmpty(message) && visible.Count == 0) message = "이 기간에 확인한 기록이 없습니다.";
            double largest = visible.Select(MetricValue).Where(v => v.HasValue).Select(v => v.Value).DefaultIfEmpty(0).Max();
            maximum = unit == "%" ? 100 : NiceMaximum(largest);
            int previous = -1;
            for (int i = 0; i < points.Count; i++) {
                var point = points[i];
                if (point.ObservedAt < start || point.ObservedAt > end || !MetricValue(point).HasValue) { previous = -1; continue; }
                numericIndexes.Add(i);
                if (previous < 0 || !Connects(points[previous], point, maxGapMinutes)) segments.Add(new List<int>());
                segments[segments.Count - 1].Add(i); previous = i;
            }
            selectedIndex = selected == null ? -1 : numericIndexes.Where(i => points[i].ObservedAt == selected.ObservedAt && points[i].ServiceId == selected.ServiceId && points[i].Label == selected.Label).DefaultIfEmpty(-1).First();
            string identity = (visible.Count == 0 ? "" : visible[0].ServiceId + "\n" + visible[0].Label) + "\n" + unit + "\n" + Math.Round((end - start).TotalHours).ToString(CultureInfo.InvariantCulture);
            if (identity != seriesIdentity) { seriesIdentity = identity; revealPending = true; }
            if (!String.IsNullOrEmpty(message)) selectedIndex = -1;
            TryReveal(); UpdateAccessibleValue();
            InvalidateVisual();
        }

        private Rect PlotBounds { get {
            return readableDashboardText ? new Rect(70,20,Math.Max(1,ActualWidth-88),Math.Max(1,ActualHeight-65))
                : new Rect(56,16,Math.Max(1,ActualWidth-74),Math.Max(1,ActualHeight-51));
        } }
        private Point Position(int index, Rect plot)
        {
            double fraction = (points[index].ObservedAt - start).TotalSeconds / Math.Max(1, (end - start).TotalSeconds);
            return new Point(plot.Left + fraction * plot.Width, plot.Bottom - Math.Max(0, Math.Min(maximum, MetricValue(points[index]).Value)) / maximum * plot.Height);
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            if (ActualWidth < 120 || ActualHeight < 80) return;
            drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            Rect plot = PlotBounds;
            var gridPen = new Pen(WidgetPalette.Brush("#D6D9D7"), 1);
            gridPen.DashStyle = new DashStyle(new double[] { 2, 4 }, 0);
            foreach (double value in new[] { 0, maximum / 2, maximum }) {
                double y = plot.Bottom - plot.Height * value / maximum;
                drawing.PushOpacity(0.55); drawing.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y)); drawing.Pop();
                DrawText(drawing, AxisLabel(value) + (unit == "%" ? "%" : ""), 10, WidgetPalette.Brush("#647070"), 0, y - (readableDashboardText ? 9 : 7), readableDashboardText ? 64 : 49);
            }
            double totalSeconds = Math.Max(1, (end - start).TotalSeconds);
            for (int i = 0; i < 3; i++) {
                if ((i == 1 && plot.Width < 190) || (i == 2 && plot.Width < 90)) continue;
                var time = start.AddSeconds(totalSeconds * i / 2.0).ToLocalTime();
                string label = totalSeconds <= 86401 ? time.ToString("HH:mm") : time.ToString("MM/dd");
                double width = readableDashboardText ? 56 : 42;
                double x = plot.Left + plot.Width * i / 2.0 - (i == 0 ? 0 : i == 2 ? width : width / 2);
                DrawText(drawing, label, 10, WidgetPalette.Brush("#647070"), x, plot.Bottom + 14, width);
            }
            if (!String.IsNullOrEmpty(message)) {
                var text = Formatted(message, 12, WidgetPalette.Brush("#647070")); text.MaxTextWidth = Math.Max(40, plot.Width - 24);
                text.TextAlignment = TextAlignment.Center;
                drawing.DrawText(text, new Point(plot.Left + 12, plot.Top + (plot.Height - text.Height) / 2)); return;
            }
            var ink = WidgetPalette.Brush("#252827");
            var stroke = new Pen(ink, 1.7) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            drawing.PushOpacity((double)GetValue(RevealProperty));
            drawing.PushClip(new RectangleGeometry(new Rect(plot.Left - 7, plot.Top - 7, plot.Width + 14, plot.Height + 14)));
            double isolatedRadius = segments.Count(segment => segment.Count == 1) > Math.Max(12, plot.Width / 18) ? 1.4 : 2.1;
            foreach (var segment in segments) {
                if (segment.Count == 1) { drawing.DrawEllipse(ink, null, Position(segment[0], plot), isolatedRadius, isolatedRadius); continue; }
                var line = new StreamGeometry();
                using (var context = line.Open()) {
                    context.BeginFigure(Position(segment[0], plot), false, false);
                    for (int i = 1; i < segment.Count; i++) context.LineTo(Position(segment[i], plot), true, false);
                }
                line.Freeze();
                var area = new StreamGeometry();
                using (var context = area.Open()) {
                    Point first = Position(segment[0], plot), last = Position(segment[segment.Count - 1], plot);
                    context.BeginFigure(new Point(first.X, plot.Bottom), true, true); context.LineTo(first, true, false);
                    for (int i = 1; i < segment.Count; i++) context.LineTo(Position(segment[i], plot), true, false);
                    context.LineTo(new Point(last.X, plot.Bottom), true, false);
                }
                area.Freeze();
                drawing.PushOpacity(0.055); drawing.DrawGeometry(ink, null, area); drawing.Pop();
                drawing.DrawGeometry(null, stroke, line);
            }
            if (numericIndexes.Count > 0) {
                Point last = Position(numericIndexes[numericIndexes.Count - 1], plot);
                drawing.PushOpacity(0.10); drawing.DrawEllipse(ink, null, last, 7, 7); drawing.Pop();
                drawing.DrawEllipse(ink, new Pen(WidgetPalette.Brush("#FAFAF7"), 1.5), last, 3.8, 3.8);
            }
            drawing.Pop(); drawing.Pop();
            if (selectedIndex >= 0) DrawSelection(drawing, plot, ink);
        }

        private void DrawSelection(DrawingContext drawing, Rect plot, Brush ink)
        {
            Point at = Position(selectedIndex, plot);
            var crosshair = new Pen(WidgetPalette.Brush("#647070"), 1) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };
            drawing.PushOpacity(0.65); drawing.DrawLine(crosshair, new Point(at.X, plot.Top), new Point(at.X, plot.Bottom)); drawing.Pop();
            drawing.DrawEllipse(WidgetPalette.Brush("#FAFAF7"), new Pen(ink, 1.5), at, 4, 4);
            var point = points[selectedIndex];
            string value = ValueLabel(point);
            string timestamp = point.ObservedAt.ToLocalTime().ToString("MM/dd HH:mm:ss", CultureInfo.CurrentCulture);
            var valueText = Formatted(value, 13, ink);
            var timeText = Formatted(timestamp, 10, WidgetPalette.Brush("#647070"));
            double width = Math.Min(Math.Max(118, Math.Max(valueText.Width, timeText.Width) + 24), Math.Max(80, ActualWidth - 16));
            valueText.MaxTextWidth = width - 24; valueText.Trimming = TextTrimming.CharacterEllipsis;
            double timestampTop = readableDashboardText ? 8 + valueText.Height + 6 : 30;
            double manualTop = readableDashboardText ? timestampTop + timeText.Height + 6 : 47;
            double height = readableDashboardText ? (point.Source == "manual" ? manualTop + Formatted("수동 입력",10,ink).Height : timestampTop + timeText.Height) + 10
                : point.Source == "manual" ? 71 : 54;
            double x = Math.Max(8, Math.Min(ActualWidth - width - 8, at.X - width / 2));
            double y = at.Y - height - 12;
            if (y < plot.Top) y = Math.Min(plot.Bottom - height, at.Y + 14);
            y = Math.Max(4, Math.Min(ActualHeight - height - 4, y));
            drawing.DrawRoundedRectangle(WidgetPalette.Brush("#FAFAF7"), new Pen(WidgetPalette.Brush("#D6D9D7"), 1), new Rect(x, y, width, height), 7, 7);
            drawing.DrawText(valueText, new Point(x + 12, y + 8));
            drawing.DrawText(timeText, new Point(x + 12, y + timestampTop));
            if (point.Source == "manual") DrawText(drawing, "수동 입력", 10, WidgetPalette.Brush("#647070"), x + 12, y + manualTop, width - 24);
        }

        internal int FindClosestPointIndex(double fraction)
        {
            if (numericIndexes.Count == 0 || !String.IsNullOrEmpty(message) || Double.IsNaN(fraction)) return -1;
            double seconds = Math.Max(0, Math.Min(1, fraction)) * (end - start).TotalSeconds;
            int low = 0, high = numericIndexes.Count - 1;
            while (low < high) {
                int middle = low + (high - low) / 2;
                if ((points[numericIndexes[middle]].ObservedAt - start).TotalSeconds < seconds) low = middle + 1; else high = middle;
            }
            if (low > 0 && Math.Abs((points[numericIndexes[low - 1]].ObservedAt - start).TotalSeconds - seconds) <= Math.Abs((points[numericIndexes[low]].ObservedAt - start).TotalSeconds - seconds)) low--;
            return numericIndexes[low];
        }
        internal bool MoveSelection(int direction)
        {
            if (numericIndexes.Count == 0 || !String.IsNullOrEmpty(message)) return false;
            int current = numericIndexes.IndexOf(selectedIndex);
            current = current < 0 ? (direction < -1 ? 0 : numericIndexes.Count - 1) : Math.Max(0, Math.Min(numericIndexes.Count - 1, current + direction));
            SelectPoint(numericIndexes[current]); return true;
        }
        private void SelectPoint(int index)
        {
            if (index == selectedIndex) return;
            selectedIndex = index; UpdateAccessibleValue(); InvalidateVisual();
        }
        private string ValueLabel(UsageHistoryPoint point)
        {
            return MetricValue(point).Value.ToString("#,0.##", CultureInfo.CurrentCulture) + (MetricUnit(point) == "%" ? "%" : " " + L.Text(MetricUnit(point)));
        }
        private void UpdateAccessibleValue()
        {
            AutomationProperties.SetItemStatus(this, selectedIndex < 0 ? "" : points[selectedIndex].ObservedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + " · " + ValueLabel(points[selectedIndex]));
        }
        protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseMove(e); Rect plot = PlotBounds; Point at = e.GetPosition(this);
            if (plot.Contains(at)) SelectPoint(FindClosestPointIndex((at.X - plot.Left) / plot.Width));
            else if (!IsKeyboardFocused) SelectPoint(-1);
        }
        protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e) { base.OnMouseLeave(e); if (!IsKeyboardFocused) SelectPoint(-1); }
        protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e); Focus();
            // ScrollViewer otherwise handles the bubbled click and takes focus back,
            // preventing arrow keys from reaching the selected chart observation.
            e.Handled = true;
        }
        protected override void OnGotKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); if (selectedIndex < 0) MoveSelection(0); }
        protected override void OnLostKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); if (!IsMouseOver) SelectPoint(-1); }
        protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == System.Windows.Input.Key.Left) e.Handled = MoveSelection(-1);
            else if (e.Key == System.Windows.Input.Key.Right) e.Handled = MoveSelection(1);
            else if (e.Key == System.Windows.Input.Key.Home) e.Handled = MoveSelection(-numericIndexes.Count);
            else if (e.Key == System.Windows.Input.Key.End) e.Handled = MoveSelection(numericIndexes.Count);
            else if (e.Key == System.Windows.Input.Key.Escape) { SelectPoint(-1); e.Handled = true; }
        }

        internal static bool Connects(UsageHistoryPoint previous, UsageHistoryPoint point, double gapMinutes)
        {
            return previous != null && point != null && MetricValue(point).HasValue && MetricValue(previous).HasValue
                && point.ServiceId == previous.ServiceId && point.Label == previous.Label
                && MetricUnit(point) == MetricUnit(previous)
                && point.Source != "manual" && previous.Source == point.Source && point.ObservedAt > previous.ObservedAt
                && (point.ObservedAt - previous.ObservedAt).TotalMinutes <= Math.Max(gapMinutes, Math.Max(point.ResolutionMinutes, previous.ResolutionMinutes) * 1.5)
                && (!previous.ResetsAt.HasValue || previous.ResetsAt.Value > point.ObservedAt)
                && point.ResetsAt == previous.ResetsAt && point.Limit == previous.Limit
                && MetricValue(point).Value <= MetricValue(previous).Value + (point.RemainingPercent.HasValue ? 3 : 0);
        }

        internal static double? MetricValue(UsageHistoryPoint point)
        {
            if (point == null) return null;
            double? value = point.RemainingPercent ?? point.Remaining;
            return value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) && value.Value >= 0 && (!point.RemainingPercent.HasValue || value.Value <= 100) ? value : null;
        }

        internal static string MetricUnit(UsageHistoryPoint point)
        {
            if (point == null || !MetricValue(point).HasValue) return "";
            if (point.RemainingPercent.HasValue) return "%";
            return point.ServiceId == "higgsfield-api" || point.ServiceId == "openai" ? "USD" : "크레딧";
        }

        internal static double NiceMaximum(double largest)
        {
            if (Double.IsNaN(largest) || Double.IsInfinity(largest) || largest <= 0) return 1;
            double scale = Math.Pow(10, Math.Floor(Math.Log10(largest)));
            if (scale == 0) return largest;
            double result = Math.Ceiling(largest / scale * 1.1) * scale;
            return Double.IsInfinity(result) || Double.IsNaN(result) ? largest : result;
        }

        private static string AxisLabel(double value)
        {
            if (value >= 1000000) return (value / 1000000).ToString("0.#", CultureInfo.CurrentCulture) + "M";
            if (value >= 10000) return (value / 1000).ToString("0.#", CultureInfo.CurrentCulture) + "k";
            if (value > 0 && value < 0.01) return value.ToString("0.##E+0", CultureInfo.CurrentCulture);
            return value.ToString("0.##", CultureInfo.CurrentCulture);
        }
        private FormattedText Formatted(string value, double size, Brush brush)
        {
            if (readableDashboardText) size = size <= 10 ? 13 : size <= 12 ? 14 : 16;
            return new FormattedText(L.Text(value), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI, Malgun Gothic"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
        private void DrawText(DrawingContext drawing, string text, double size, Brush brush, double x, double y, double width)
        {
            var formatted = Formatted(text, size, brush); formatted.MaxTextWidth = Math.Max(1, width);
            drawing.DrawText(formatted, new Point(x, y));
        }
    }
}
