using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        // Keep these elements alive during polling so a focused disclosure and
        // the user's reading position do not disappear when a value changes.
        private sealed class DashboardAnalysisCard
        {
            internal Border Card;
            internal TextBlock Identity, Conclusion, Basis, Description, Average, AverageUnit, Recommended, RecommendedUnit, Reset, ResetUnit, Support;
            internal Expander Disclosure;
        }

        private readonly Dictionary<string,DashboardAnalysisCard> dashboardAnalysisCards = new Dictionary<string,DashboardAnalysisCard>();

        private DashboardAnalysisCard CreateDashboardAnalysisCard(string key)
        {
            var item = new DashboardAnalysisCard();
            var body = new LocalizedStackPanel();
            item.Identity = DashboardText("",14,"#647070",FontWeights.Normal); UiL.Raw(item.Identity); body.Children.Add(item.Identity);
            item.Conclusion = DashboardText("",22,"#252827",FontWeights.SemiBold,new Thickness(0,10,0,0)); body.Children.Add(item.Conclusion);
            item.Basis = DashboardText("",13,"#647070",FontWeights.Normal,new Thickness(0,8,0,0)); UiL.Raw(item.Basis); body.Children.Add(item.Basis);
            var metrics = new Grid { Margin = new Thickness(0,22,0,20) };
            for (int column = 0; column < 3; column++) metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
            item.Average = AddDashboardAnalysisMetric(metrics,0,"하루 평균 소모량",out item.AverageUnit);
            item.Recommended = AddDashboardAnalysisMetric(metrics,1,"하루 권장 소모량",out item.RecommendedUnit);
            TextBlock resetUnit;
            item.Reset = AddDashboardAnalysisMetric(metrics,2,"초기화 예정",out resetUnit); item.Reset.FontSize = 18; item.Reset.LineHeight = 27;
            item.ResetUnit=resetUnit;
            resetUnit.Text = "현지 시간";
            metrics.SizeChanged += (s,e) => ArrangeDashboardAnalysisMetrics(metrics);
            body.Children.Add(metrics);
            item.Description = DashboardText("",14,"#647070",FontWeights.Normal); UiL.Raw(item.Description); body.Children.Add(item.Description);
            item.Support = DashboardText("",13,"#647070",FontWeights.Normal,new Thickness(0,12,0,0)); UiL.Raw(item.Support);
            item.Disclosure = new LocalizedExpander { Header = "관측·계산 안내",Content = item.Support,FontSize = 13,
                Foreground = Muted,Margin = new Thickness(0,16,0,0),IsExpanded = false };
            bool expanded;
            if (dashboardHistoryDisclosureStates.TryGetValue(key,out expanded)) item.Disclosure.IsExpanded = expanded;
            body.Children.Add(item.Disclosure);
            item.Card = DashboardCard(body); item.Card.Tag = key; item.Card.Padding = new Thickness(20);
            return item;
        }

        private static void ArrangeDashboardAnalysisMetrics(Grid metrics)
        {
            bool narrow=metrics.ActualWidth>0 && metrics.ActualWidth<560;
            if(metrics.Tag is bool && (bool)metrics.Tag==narrow) return;
            metrics.Tag=narrow;
            metrics.RowDefinitions.Clear(); metrics.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            if(narrow) metrics.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            metrics.ColumnDefinitions[2].Width=narrow ? new GridLength(0) : new GridLength(1,GridUnitType.Star);
            for(int index=0;index<metrics.Children.Count;index++) {
                var child=metrics.Children[index] as FrameworkElement; if(child==null) continue;
                bool secondRow=narrow && index==2;
                Grid.SetRow(child,secondRow ? 1 : 0); Grid.SetColumn(child,secondRow ? 0 : index); Grid.SetColumnSpan(child,secondRow ? 3 : 1);
                child.Margin=new Thickness(index==0 || secondRow ? 0 : 20,secondRow ? 20 : 0,0,0);
            }
        }

        private TextBlock AddDashboardAnalysisMetric(Grid metrics, int column, string title, out TextBlock unit)
        {
            var panel = new LocalizedStackPanel { Margin = new Thickness(column == 0 ? 0 : 20,0,0,0) };
            panel.Children.Add(DashboardText(title,13,"#647070",FontWeights.Normal));
            var value = DashboardText("—",30,"#252827",FontWeights.SemiBold,new Thickness(0,8,0,0)); UiL.Raw(value);
            System.Windows.Documents.Typography.SetNumeralAlignment(value,System.Windows.FontNumeralAlignment.Tabular);
            panel.Children.Add(value);
            unit = DashboardText("",13,"#647070",FontWeights.Normal,new Thickness(0,4,0,0)); panel.Children.Add(unit);
            Grid.SetColumn(panel,column); metrics.Children.Add(panel); return value;
        }

        private void UpdateDashboardAnalysisCards(List<HistoryMetricChoice> metrics,List<UsageHistoryPoint> points,HashSet<string> connected,DateTimeOffset now)
        {
            if (!ProFeaturesAvailable) { LockDashboardHistory(); return; }
            var keys = new HashSet<string>(metrics.Select(DashboardMetricKey),StringComparer.Ordinal);
            foreach (string key in dashboardAnalysisCards.Keys.Where(key => !keys.Contains(key)).ToList()) {
                dashboardHistoryContent.Children.Remove(dashboardAnalysisCards[key].Card); dashboardAnalysisCards.Remove(key);
            }
            if (metrics.Count == 0) {
                dashboardHistoryContent.Children.Clear();
                dashboardHistoryContent.Children.Add(DashboardCard(DashboardText("서비스와 항목을 하나 선택하면 분석을 표시합니다.",14,"#647070",FontWeights.Normal)));
                return;
            }
            if (dashboardAnalysisCards.Count == 0) dashboardHistoryContent.Children.Clear();
            for (int index = 0; index < metrics.Count; index++) {
                var metric = metrics[index]; string key = DashboardMetricKey(metric);
                DashboardAnalysisCard card;
                if (!dashboardAnalysisCards.TryGetValue(key,out card)) {
                    card = CreateDashboardAnalysisCard(key); dashboardAnalysisCards.Add(key,card);
                    dashboardHistoryContent.Children.Insert(Math.Min(index,dashboardHistoryContent.Children.Count),card.Card);
                }
                var latest = points.LastOrDefault(point => point.ServiceId == metric.ServiceId && point.Label == metric.Label);
                string source = connected.Contains(metric.ServiceId) ? CurrentAnalysisSource(metric.ServiceId) : latest == null ? null : latest.Source;
                latest = points.LastOrDefault(point => point.ServiceId == metric.ServiceId && point.Label == metric.Label
                    && (source == null || point.Source == source));
                UsageAnalysis analysis = null;
                try { analysis = usageJournal.GetAnalysis(metric.ServiceId,metric.Label,source,now); }
                catch { }
                var view = connected.Contains(metric.ServiceId) ? ViewFor(metric.ServiceId) : null;
                bool current = DashboardAnalysisCurrent(view,metric.Label,analysis,now);
                UpdateDashboardAnalysisCard(card,metric,source,analysis,latest,current,now);
            }
        }

        internal static bool DashboardAnalysisCurrent(ProviderView view,string label,UsageAnalysis analysis,DateTimeOffset now)
        {
            if (view == null || view.Snapshot == null || view.Snapshot.Rows == null || !String.IsNullOrEmpty(view.Error)) return false;
            var rows = view.Snapshot.Rows.Where(item => item != null && item.Label == label).ToList();
            if (rows.Count != 1) return false;
            var row = rows[0];
            if (!DashboardAssessment.IsUsableRow(view,row,now)) return false;
            if (UsageFreshness.Source(view) == "manual") return true; // Manual analysis never produces a forecast.
            string source = UsageFreshness.Source(view);
            bool code = source == "code" || source.StartsWith("code:",StringComparison.Ordinal);
            var observed = code ? view.ReceiptObservedAt ?? view.LastSuccess : view.LastSuccess;
            if (!observed.HasValue || observed > now || now-observed.Value > TimeSpan.FromMinutes(code ? 30 : 10)) return false;
            if (row.ResetsAt.HasValue && row.ResetsAt <= now || analysis != null && analysis.ResetAt.HasValue && analysis.ResetAt != row.ResetsAt) return false;
            return true;
        }

        private void UpdateDashboardAnalysisCard(DashboardAnalysisCard card,HistoryMetricChoice metric,string source,UsageAnalysis analysis,UsageHistoryPoint latest,bool current,DateTimeOffset now)
        {
            card.Identity.Text = UsageMetricTitle(metric.ServiceId,metric.Label,metric.Manual);
            card.Basis.Text = latest == null ? L.Text("아직 확인된 사용량이 없습니다.")
                : String.Format(CultureInfo.CurrentCulture,L.Text("마지막 확인 · {0}"),latest.ObservedAt.ToLocalTime().ToString("MM/dd HH:mm",CultureInfo.InvariantCulture));
            card.Average.Text = analysis != null && analysis.AveragePerDay.HasValue ? AnalysisNumber(analysis.AveragePerDay.Value) : "—";
            card.AverageUnit.Text = analysis != null && analysis.AveragePerDay.HasValue ? AnalysisUnit(analysis.Unit) : L.Text("계산 가능한 기록 없음");
            card.Recommended.Text = current && analysis != null && analysis.RecommendedPerDay.HasValue ? AnalysisNumber(analysis.RecommendedPerDay.Value) : "—";
            card.RecommendedUnit.Text = current && analysis != null && analysis.RecommendedPerDay.HasValue ? AnalysisUnit(analysis.Unit) + " · " + L.Text("24시간 환산") : L.Text(current ? "계산 대기" : "최근 잔량 확인 필요");
            card.Reset.Text = current && analysis != null && analysis.ResetAt.HasValue ? AnalysisDate(analysis.ResetAt.Value) : "—";
            card.ResetUnit.Text=current && analysis != null && analysis.ResetAt.HasValue ? L.Text("현지 시간") : L.Text(current ? "초기화 일정 없음" : "최근 잔량 확인 필요");
            if (analysis == null) {
                card.Conclusion.Text = "분석을 불러오지 못했습니다. 잠시 뒤 다시 확인해 주세요."; card.Description.Text = ""; card.Support.Text = "";
                return;
            }
            string insight = current ? InlineUsageInsightText(analysis,now) : null;
            card.Conclusion.Text = !current ? "최근 잔량 확인 필요" : !String.IsNullOrEmpty(insight) ? insight
                : analysis.Status == "분석 가능" ? "최근 7일 분석" : analysis.Status;
            var descriptions = new List<string>();
            if (!current) descriptions.Add(L.Text("마지막 확인 기록으로 평균만 표시합니다. 새 잔량을 확인하면 예측을 다시 표시합니다."));
            else {
                foreach (string line in AnalysisDisplayLines(analysis)) {
                    // The primary quantities already have dedicated tiles. The
                    // remaining explanation is useful without a wall of text.
                    if (line.StartsWith(L.Text("하루 평균 소모량 · {0} {1}").Split('{')[0],StringComparison.Ordinal)
                        || line.StartsWith(L.Text("하루 권장 소모량 · {0} {1} 이하 (24시간 환산)").Split('{')[0],StringComparison.Ordinal)
                        || line.StartsWith(L.Text("초기화 예정") + " · ",StringComparison.Ordinal)) continue;
                    descriptions.Add(line);
                }
            }
            descriptions=descriptions.Distinct().ToList();
            card.Description.Text = String.Join("\n",descriptions.Take(2)); card.Description.Visibility = descriptions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var support = new List<string> { L.Text(HistorySourceName(source)) };
            support.AddRange(descriptions.Skip(2));
            support.AddRange(AnalysisSupportLines(analysis,current));
            support.Add(L.Text("관측한 사용 추세에 따른 참고값입니다. 사용 패턴에 따라 달라질 수 있습니다."));
            card.Support.Text = String.Join("\n",support.Distinct());
        }
    }
}
