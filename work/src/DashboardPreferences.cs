using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private ComboBox dashboardPreferenceMetric;
        private bool dashboardPreferenceRefreshPending;
        private sealed class DashboardPreferenceFeedback
        {
            internal string Message;
            internal DateTimeOffset? ExpiresAt;
        }
        private readonly Dictionary<string,DashboardPreferenceFeedback> dashboardPreferenceFeedback = new Dictionary<string,DashboardPreferenceFeedback>(StringComparer.Ordinal);

        internal static void CopyDashboardPreferences(WidgetSettings source,WidgetSettings destination)
        {
            if (source == null || destination == null) return;
            destination.DashboardFavorites = SettingsStore.NormalizeDashboardFavorites(source.DashboardFavorites);
            destination.DashboardPreferredMetrics = SettingsStore.NormalizeDashboardPreferredMetrics(source.DashboardPreferredMetrics);
        }

        private bool DashboardFavorite(string id)
        { return settings.DashboardFavorites != null && settings.DashboardFavorites.Contains(id,StringComparer.Ordinal); }

        private string DashboardPreferredMetric(string id)
        {
            string label;
            return id != null && settings.DashboardPreferredMetrics != null && settings.DashboardPreferredMetrics.TryGetValue(id,out label)
                && SettingsStore.ValidDashboardMetricLabel(label) ? label : null;
        }

        private UsageRow DashboardOverviewRow(ProviderView view)
        {
            if (view == null || view.Snapshot == null || view.Snapshot.Rows == null) return null;
            var rows = view.Snapshot.Rows.Where(row => row != null).ToList();
            string preferred = DashboardPreferredMetric(view.Id);
            var matching = preferred == null ? new List<UsageRow>() : rows.Where(row => String.Equals(row.Label,preferred,StringComparison.Ordinal)).ToList();
            var now = DateTimeOffset.Now;
            if (matching.Count == 1 && DashboardAssessment.IsUsableRow(view,matching[0],now)) return matching[0];
            return rows.Where(row => DashboardAssessment.IsUsableRow(view,row,now)).OrderBy(row => row.RemainingPercent ?? Double.MaxValue).FirstOrDefault()
                ?? rows.Where(row => CompactUsageSummary.ValidPercent(row.RemainingPercent)).OrderBy(row => row.RemainingPercent.Value).FirstOrDefault()
                ?? rows.FirstOrDefault();
        }

        // The renderer may defer a provider-driven detail replacement while a user
        // chooses a metric. Navigation and account changes must still render immediately.
        private bool DashboardPreferencePopupOpen()
        {
            if (dashboardPreferenceMetric == null || !dashboardPreferenceMetric.IsDropDownOpen) return false;
            dashboardPreferenceRefreshPending = true;
            return true;
        }

        private void ResetDashboardPreferenceState()
        {
            dashboardPreferenceMetric = null;
            dashboardPreferenceRefreshPending = false;
        }

        private void DashboardPreferenceDropDownClosed(object sender,EventArgs args)
        {
            if (!Object.ReferenceEquals(sender,dashboardPreferenceMetric) || !dashboardPreferenceRefreshPending) return;
            dashboardPreferenceRefreshPending = false;
            RefreshDashboardIfOpen();
        }

        private void ShowDashboardPreferenceFeedback(string id,TextBlock text)
        {
            DashboardPreferenceFeedback feedback;
            if (!dashboardPreferenceFeedback.TryGetValue(id,out feedback) || feedback.ExpiresAt.HasValue && feedback.ExpiresAt.Value <= DateTimeOffset.Now) {
                dashboardPreferenceFeedback.Remove(id); text.Text = String.Empty;
            } else text.Text = L.Text(feedback.Message);
            bool error = feedback != null && !feedback.ExpiresAt.HasValue;
            Grid.SetRow(text,error ? 1 : 0); Grid.SetColumn(text,error ? 0 : 1); Grid.SetColumnSpan(text,error ? 2 : 1);
            text.Margin = error ? new Thickness(0,8,0,0) : new Thickness(16,0,0,0);
            text.Visibility = text.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private bool SaveDashboardPreferenceChange(string id,Action change,TextBlock feedback)
        {
            var before = new WidgetSettings(); CopyDashboardPreferences(settings,before);
            try {
                // Never mutate collections held by another open settings transaction.
                CopyDashboardPreferences(settings,settings);
                change();
                if (!demo) SettingsStore.Save(settings);
                dashboardPreferenceFeedback[id] = new DashboardPreferenceFeedback {
                    Message = demo ? "미리보기에 적용됨" : "저장됨", ExpiresAt = DateTimeOffset.Now.AddSeconds(8) };
                ShowDashboardPreferenceFeedback(id,feedback);
            }
            catch {
                CopyDashboardPreferences(before,settings);
                dashboardPreferenceFeedback[id] = new DashboardPreferenceFeedback {
                    Message = "저장하지 못했습니다. 폴더 쓰기 권한을 확인한 뒤 다시 선택해 주세요." };
                ShowDashboardPreferenceFeedback(id,feedback);
                return false;
            }
            dashboardSignature = dashboardNavigationKey = null;
            if (!DashboardPreferencePopupOpen()) RefreshDashboardIfOpen();
            return true;
        }

        private void UpdateDashboardFavoriteButton(Button favorite,string id)
        {
            bool active = DashboardFavorite(id);
            favorite.Content = L.Text(active ? "★ 즐겨찾기" : "☆ 즐겨찾기");
            favorite.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            favorite.BorderBrush = active ? Fore : BrushOf("#C8CECF");
            favorite.Background = BrushOf(active ? "#E9EDFF" : "#FFFFFF");
            favorite.ToolTip = L.Text(active ? "개요와 사이드바에서 먼저 표시합니다. 클릭하면 해제합니다." : "개요와 사이드바에서 먼저 표시하도록 고정합니다.");
            AutomationProperties.SetName(favorite,L.Text(active ? "즐겨찾기 해제" : "즐겨찾기 추가") + " · " + L.Text(UsageServiceName(id)));
            AutomationProperties.SetItemStatus(favorite,L.Text(active ? "즐겨찾기에 추가됨" : "즐겨찾기에 없음"));
        }

        private string DashboardPreferencePreview(ProviderView view)
        {
            var row = DashboardOverviewRow(view);
            if (row == null) return L.Text("개요에 표시할 잔량이 없습니다.");
            string label = !demo && UsageFreshness.Source(view) == "manual" ? row.Label : L.Text(row.Label ?? "사용 한도");
            string reading = CompactUsageSummary.ValueText(row);
            bool current = DashboardAssessment.IsUsableRow(view,row,DateTimeOffset.Now);
            return String.Format(CultureInfo.CurrentCulture,L.Text(current ? "개요 표시 · {0} · {1}" : "개요의 이전 값 · {0} · {1}"),label,reading);
        }

        private FrameworkElement BuildDashboardPreferencesControls(ProviderView view)
        {
            var body = new StackPanel();
            if (view == null) return body;
            string id = view.Id;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var favorite = ManagementAction("☆ 즐겨찾기",() => {});
            favorite.Margin = new Thickness(0,0,16,0); favorite.VerticalAlignment = VerticalAlignment.Bottom;
            favorite.MinWidth = 108; favorite.MinHeight = 38; favorite.FontSize = 14;
            favorite.Padding = new Thickness(12,8,12,8);
            AutomationProperties.SetAutomationId(favorite,"favorite:" + id);
            UpdateDashboardFavoriteButton(favorite,id);
            row.Children.Add(favorite);
            var metricBody = new StackPanel(); Grid.SetColumn(metricBody,1); row.Children.Add(metricBody);
            metricBody.Children.Add(DashboardText("개요에 표시할 한도",13,"#647070",FontWeights.Normal,new Thickness(0,0,0,8)));
            var combo = new LocalizedComboBox { FontSize = 14, MinHeight = 38, HorizontalAlignment = HorizontalAlignment.Stretch,
                IsTextSearchEnabled = true, MaxDropDownHeight = 280,
                Style = (Style)Resources[typeof(ComboBox)], ItemContainerStyle = (Style)Resources[typeof(ComboBoxItem)] };
            dashboardPreferenceMetric = combo; dashboardPreferenceRefreshPending = false;
            AutomationProperties.SetAutomationId(combo,"metric:" + id);
            AutomationProperties.SetName(combo,L.Text("개요에 표시할 한도") + " · " + L.Text(UsageServiceName(id)));
            AutomationProperties.SetHelpText(combo,L.Text("Alt+아래쪽 방향키로 목록을 열고 방향키로 선택하세요. 선택한 한도를 확인할 수 없으면 다른 잔량을 표시합니다."));
            combo.ToolTip = L.Text("자동 선택은 확인 가능한 한도 중 잔여 비율이 가장 낮은 항목을 표시합니다.");
            var automatic = new LocalizedComboBoxItem { Content = "자동 · 낮은 잔량 우선", Tag = String.Empty };
            combo.Items.Add(automatic);
            var rows = view.Snapshot == null || view.Snapshot.Rows == null ? new List<UsageRow>() : view.Snapshot.Rows.Where(value => value != null).ToList();
            foreach (var group in rows.Where(value => SettingsStore.ValidDashboardMetricLabel(value.Label)).GroupBy(value => value.Label,StringComparer.Ordinal)) {
                if (group.Count() != 1) continue;
                string label = group.Key;
                // Raw user-defined labels are never keys in the localization dictionary.
                ComboBoxItem item = !demo && UsageFreshness.Source(view) == "manual"
                    ? new ComboBoxItem { Content = label, Tag = label }
                    : new LocalizedComboBoxItem { Content = label, Tag = label };
                combo.Items.Add(item);
            }
            string preferred = DashboardPreferredMetric(id);
            var selected = preferred == null ? automatic : combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => String.Equals(item.Tag as string,preferred,StringComparison.Ordinal));
            if (selected == null) {
                string unavailableLabel = !demo && UsageFreshness.Source(view) == "manual" ? preferred : L.Text(preferred);
                selected = new ComboBoxItem { Content = unavailableLabel + " · " + L.Text("현재 확인 불가"), Tag = preferred, IsEnabled = false };
                combo.Items.Add(selected);
            }
            combo.SelectedItem = selected; metricBody.Children.Add(combo); body.Children.Add(row);
            var footer = new Grid { Margin = new Thickness(0,12,0,0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var preview = DashboardText(DashboardPreferencePreview(view),13,"#647070",FontWeights.Normal);
            UiL.Raw(preview); preview.TextWrapping = TextWrapping.Wrap; preview.TextTrimming = TextTrimming.None;
            preview.ToolTip = preview.Text; AutomationProperties.SetAutomationId(preview,"preference-preview:" + id); footer.Children.Add(preview);
            var feedback = DashboardText("",13,"#475256",FontWeights.Normal);
            UiL.Raw(feedback); AutomationProperties.SetAutomationId(feedback,"preference-feedback:" + id);
            AutomationProperties.SetLiveSetting(feedback,AutomationLiveSetting.Polite);
            ShowDashboardPreferenceFeedback(id,feedback); footer.Children.Add(feedback);
            var selectedRows = preferred == null ? new List<UsageRow>() : rows.Where(value => String.Equals(value.Label,preferred,StringComparison.Ordinal)).ToList();
            if (preferred != null && (selectedRows.Count != 1 || !DashboardAssessment.IsUsableRow(view,selectedRows[0],DateTimeOffset.Now))) {
                var fallback = new DockPanel { Margin = new Thickness(0,12,0,0), LastChildFill = true };
                var automaticAction = ManagementAction("자동 선택으로 변경",() => {
                    combo.Focus(); combo.SelectedItem = automatic;
                });
                automaticAction.Margin = new Thickness(16,0,0,0); automaticAction.Padding = new Thickness(10,7,10,7);
                automaticAction.FontSize = 13;
                AutomationProperties.SetAutomationId(automaticAction,"preference-auto:" + id);
                DockPanel.SetDock(automaticAction,Dock.Right); fallback.Children.Add(automaticAction);
                fallback.Children.Add(DashboardText("선택한 한도 확인 대기 · 선택은 유지됩니다.",13,"#647070",FontWeights.Normal));
                body.Children.Add(fallback);
            }
            body.Children.Add(footer);
            favorite.Click += (s,e) => {
                bool remove = DashboardFavorite(id);
                if (SaveDashboardPreferenceChange(id,() => settings.DashboardFavorites = remove
                    ? settings.DashboardFavorites.Where(value => value != id).ToArray()
                    : SettingsStore.NormalizeDashboardFavorites(settings.DashboardFavorites.Concat(new[] { id })),feedback)) UpdateDashboardFavoriteButton(favorite,id);
            };
            bool restoring = false;
            ComboBoxItem previous = selected;
            combo.SelectionChanged += (s,e) => {
                if (restoring) return;
                var item = combo.SelectedItem as ComboBoxItem;
                if (item == null || !item.IsEnabled) return;
                string label = item.Tag as string;
                if (SaveDashboardPreferenceChange(id,() => {
                    if (String.IsNullOrEmpty(label)) settings.DashboardPreferredMetrics.Remove(id);
                    else settings.DashboardPreferredMetrics[id] = label;
                },feedback)) {
                    previous = item; preview.Text = DashboardPreferencePreview(view); preview.ToolTip = preview.Text;
                }
                else { restoring = true; combo.SelectedItem = previous; restoring = false; }
            };
            combo.DropDownClosed += DashboardPreferenceDropDownClosed;
            var card = DashboardCard(body); card.Padding = new Thickness(18,16,18,16);
            return card;
        }
    }
}
