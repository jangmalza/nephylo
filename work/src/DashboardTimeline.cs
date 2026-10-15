using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UsageWidget
{
    internal sealed class DashboardResetItem
    {
        internal string ServiceId, Metric, DisplayValue;
        internal double? RemainingPercent, Remaining;
        internal DateTimeOffset ResetsAt, LocalReset;
        internal bool Manual;
        internal int RowIndex;
    }

    internal sealed class DashboardResetSchedule
    {
        internal readonly List<DashboardResetItem> Items = new List<DashboardResetItem>();
        internal DateTime LocalToday;
        internal DateTimeOffset LocalStart, LocalEnd;
        internal int UnavailableRows, MissingScheduleRows, LaterRows, ServiceCount;
    }

    // Project existing observations only. No timers, provider calls, journal
    // writes, or arithmetic across different allowance units belong here.
    internal static class DashboardResetTimeline
    {
        internal static DashboardResetSchedule Project(IEnumerable<ProviderView> views, DateTimeOffset now, TimeZoneInfo zone)
        {
            zone = zone ?? TimeZoneInfo.Local;
            var result = new DashboardResetSchedule();
            result.LocalStart = TimeZoneInfo.ConvertTime(now,zone);
            result.LocalToday = result.LocalStart.Date;
            // Use the same 168 hours as the remaining-time card, across midnight and DST.
            var end = now.AddDays(7);
            result.LocalEnd = TimeZoneInfo.ConvertTime(end,zone);
            foreach (var view in views ?? Enumerable.Empty<ProviderView>()) {
                if (view == null) continue;
                result.ServiceCount++;
                var rows = view.Snapshot == null || view.Snapshot.Rows == null ? new List<UsageRow>() : view.Snapshot.Rows;
                if (rows.Count == 0) { result.UnavailableRows++; continue; }
                for (int index = 0; index < rows.Count; index++) {
                    var row = rows[index];
                    if (!DashboardAssessment.IsUsableRow(view,row,now)) { result.UnavailableRows++; continue; }
                    if (!row.ResetsAt.HasValue) { result.MissingScheduleRows++; continue; }
                    var local = TimeZoneInfo.ConvertTime(row.ResetsAt.Value,zone);
                    if (row.ResetsAt.Value > end) { result.LaterRows++; continue; }
                    result.Items.Add(new DashboardResetItem { ServiceId = view.Id, Metric = row.Label,
                        DisplayValue = row.DisplayValue, RemainingPercent = row.RemainingPercent, Remaining = row.Remaining,
                        ResetsAt = row.ResetsAt.Value, LocalReset = local, RowIndex = index, Manual = UsageFreshness.Source(view) == "manual" });
                }
            }
            result.Items.Sort((left,right) => {
                int byTime = left.ResetsAt.CompareTo(right.ResetsAt); if (byTime != 0) return byTime;
                int byService = StringComparer.Ordinal.Compare(left.ServiceId,right.ServiceId);
                return byService != 0 ? byService : left.RowIndex.CompareTo(right.RowIndex);
            });
            return result;
        }
    }

    public partial class WidgetWindow
    {
        private FrameworkElement BuildDashboardTimelinePage()
        {
            if (!ProFeaturesAvailable) return BuildDashboardProGate();
            var page = new StackPanel();
            var header = DashboardPageHeader("초기화 일정",demo
                ? "미리보기 · 예시 데이터입니다. 실제 사용량이 아니며 PC에 저장하지 않습니다."
                : "서비스별 다음 초기화를 날짜순으로 확인하세요.");
            header.Margin = new Thickness(0,0,0,16); page.Children.Add(header);
            var now = DateTimeOffset.Now;
            var schedule = DashboardResetTimeline.Project(DashboardViews(),now,TimeZoneInfo.Local);
            var meta = DashboardText(L.Text("현재부터 7일 · 현지 시간") + " · " + schedule.LocalStart.ToString("MM/dd HH:mm",CultureInfo.InvariantCulture)
                + " – " + schedule.LocalEnd.ToString("MM/dd HH:mm",CultureInfo.InvariantCulture),13,"#647070",FontWeights.Normal,new Thickness(0,0,0,16));
            UiL.Raw(meta); meta.ToolTip = TimeZoneInfo.Local.DisplayName + "\n" + L.Text("실제 초기화는 새 사용량을 확인한 뒤 반영됩니다."); page.Children.Add(meta);
            if (schedule.Items.Count == 0) page.Children.Add(DashboardTimelineEmpty(schedule));
            foreach (var group in schedule.Items.GroupBy(item => item.LocalReset.Date)) {
                string day = group.Key == schedule.LocalToday ? L.Text("오늘")
                    : group.Key == schedule.LocalToday.AddDays(1) ? L.Text("내일")
                    : group.Key.ToString(L.Language == "en" ? "MMM d, ddd" : "M월 d일 ddd",L.Language == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("ko-KR"));
                if (group.Key == schedule.LocalToday || group.Key == schedule.LocalToday.AddDays(1)) day += " · " + group.Key.ToString("MM/dd",CultureInfo.InvariantCulture);
                var title = DashboardText(day,16,"#252827",FontWeights.SemiBold,new Thickness(0,12,0,12)); UiL.Raw(title); page.Children.Add(title);
                page.Children.Add(BuildDashboardTimelineList(group,now,false));
            }
            if (schedule.Items.Count > 0 && schedule.UnavailableRows > 0) page.Children.Add(DashboardTimelineAttention());
            else if (schedule.Items.Count > 0 && schedule.MissingScheduleRows > 0)
                page.Children.Add(DashboardText("초기화 시각이 없는 항목은 제외됩니다.",13,"#647070",FontWeights.Normal,new Thickness(0,8,0,0)));
            return page;
        }

        private FrameworkElement BuildDashboardUpcomingResetPreview()
        {
            if (!ProFeaturesAvailable) return new Border { Visibility = Visibility.Collapsed };
            var now = DateTimeOffset.Now;
            var schedule = DashboardResetTimeline.Project(DashboardViews(),now,TimeZoneInfo.Local);
            var body = new StackPanel { Margin = new Thickness(0,8,0,0) };
            var heading = new DockPanel { Margin = new Thickness(0,0,0,10) };
            var all = ManagementAction("전체 보기",() => {
                if (!ProFeaturesAvailable) { RefreshDashboardIfOpen(); return; }
                NavigateDashboard("timeline");
            });
            all.Margin = new Thickness(12,0,0,0); all.MinHeight = 36; all.FontSize = 13; all.Padding = new Thickness(12,7,12,7);
            AutomationProperties.SetAutomationId(all,"dashboard-timeline:all"); DockPanel.SetDock(all,Dock.Right); heading.Children.Add(all);
            var label = DashboardText("다가오는 초기화",16,"#252827",FontWeights.SemiBold); label.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(label); body.Children.Add(heading);
            if (schedule.Items.Count == 0) body.Children.Add(DashboardTimelineEmpty(schedule));
            else body.Children.Add(BuildDashboardTimelineList(schedule.Items.Take(3),now,true));
            return body;
        }

        private FrameworkElement DashboardTimelineEmpty(DashboardResetSchedule schedule)
        {
            var empty = new StackPanel();
            empty.Children.Add(DashboardText(schedule.ServiceCount == 0 ? "연결된 서비스가 없습니다." : "7일 안에 확인된 초기화 일정이 없습니다.",16,"#252827",FontWeights.SemiBold));
            empty.Children.Add(DashboardText(schedule.ServiceCount == 0 ? "서비스를 연결하면 확인된 초기화 일정이 여기에 표시됩니다."
                : schedule.UnavailableRows > 0 ? "새 사용량을 확인하면 초기화 일정을 다시 표시합니다."
                : schedule.LaterRows > 0 ? "다음 초기화는 표시 기간 이후에 예정되어 있습니다."
                : "서비스가 초기화 시각을 제공하지 않거나 직접 입력한 일정이 없습니다.",14,"#647070",FontWeights.Normal,new Thickness(0,10,0,0)));
            var actions = new WrapPanel { Margin = new Thickness(0,12,0,0) };
            if (schedule.UnavailableRows > 0) actions.Children.Add(DashboardTimelineRefreshButton());
            actions.Children.Add(DashboardTimelineManageButton(schedule.ServiceCount == 0)); empty.Children.Add(actions);
            var card = DashboardCard(empty); card.Padding = new Thickness(20); return card;
        }

        private FrameworkElement DashboardTimelineAttention()
        {
            var panel = new StackPanel { Margin = new Thickness(0,6,0,0) };
            panel.Children.Add(DashboardText("일부 일정은 새 사용량 확인이 필요합니다.",14,"#475256",FontWeights.Normal));
            var actions = new WrapPanel { Margin = new Thickness(0,8,0,0) };
            actions.Children.Add(DashboardTimelineRefreshButton()); actions.Children.Add(DashboardTimelineManageButton(false)); panel.Children.Add(actions);
            return panel;
        }

        private Button DashboardTimelineRefreshButton()
        {
            var button = ManagementAction("새로고침",async () => {
                if (!ProFeaturesAvailable || refreshing) { RefreshDashboardIfOpen(); return; }
                if (!demo) await RefreshAsync(); else RefreshDashboardIfOpen();
            });
            button.IsEnabled = !refreshing; button.MinHeight = 36; button.FontSize = 13; button.Padding = new Thickness(12,7,12,7); button.Margin = new Thickness(0,0,10,8);
            AutomationProperties.SetAutomationId(button,"dashboard-timeline-refresh"); return button;
        }

        private Button DashboardTimelineManageButton(bool connect)
        {
            var button = ManagementAction(connect ? "서비스 연결" : "서비스 관리",() => {
                if (!ProFeaturesAvailable) { RefreshDashboardIfOpen(); return; }
                if (connect) OpenServices(CurrentServiceSelection()); else NavigateDashboard("services");
            });
            button.MinHeight = 36; button.FontSize = 13; button.Padding = new Thickness(12,7,12,7); button.Margin = new Thickness(0,0,0,8);
            AutomationProperties.SetAutomationId(button,"dashboard-timeline-manage"); return button;
        }

        private FrameworkElement BuildDashboardTimelineList(IEnumerable<DashboardResetItem> items,DateTimeOffset now,bool compact)
        {
            var list = new StackPanel();
            foreach (var item in items) {
                if (list.Children.Count > 0) list.Children.Add(new Border { Height = 1, Margin = new Thickness(16,0,16,0), Background = BrushOf("#C8CECF") });
                list.Children.Add(BuildDashboardTimelineRow(item,now,compact));
            }
            var card = DashboardCard(list); card.Padding = new Thickness(0); card.Margin = new Thickness(0,0,0,16); return card;
        }

        private FrameworkElement BuildDashboardTimelineRow(DashboardResetItem item,DateTimeOffset now,bool compact)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var time = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (compact) {
                string day = item.LocalReset.Date == now.ToLocalTime().Date ? L.Text("오늘") : item.LocalReset.ToString("MM/dd",CultureInfo.InvariantCulture);
                var date = DashboardText(day,13,"#647070",FontWeights.Normal,new Thickness(0,0,0,4)); UiL.Raw(date); time.Children.Add(date);
            }
            var stamp = DashboardText(item.LocalReset.ToString("HH:mm",CultureInfo.InvariantCulture),16,"#252827",FontWeights.SemiBold); UiL.Raw(stamp); time.Children.Add(stamp); line.Children.Add(time);
            var icon = new Image { Source = ServiceIcons.Get(item.ServiceId), Width = 22, Height = 22, Margin = new Thickness(0,0,12,0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(icon,1); line.Children.Add(icon);
            var info = new StackPanel { Margin = new Thickness(0,0,12,0), VerticalAlignment = VerticalAlignment.Center };
            var name = DashboardText(UsageServiceName(item.ServiceId),16,"#252827",FontWeights.SemiBold);
            name.ToolTip = L.Text(UsageServiceName(item.ServiceId)); info.Children.Add(name);
            string metric = !demo && item.Manual ? item.Metric ?? "" : L.Text(item.Metric ?? "사용 한도");
            // This built-in provider label already carries the source suffix;
            // show it once as the more specific schedule label below.
            if (item.Manual && item.Metric == "API 달러 잔액 · 수동") metric = L.Text("API 달러 잔액");
            if (item.Manual) metric += " · " + L.Text("수동 입력 일정");
            var detail = DashboardText(metric,13,"#647070",FontWeights.Normal,new Thickness(0,4,0,0)); UiL.Raw(detail);
            detail.ToolTip = metric; info.Children.Add(detail);
            Grid.SetColumn(info,2); line.Children.Add(info);
            var value = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170 };
            var current = new UsageRow { RemainingPercent = item.RemainingPercent, Remaining = item.Remaining, DisplayValue = item.DisplayValue };
            string balance = CompactUsageSummary.ValueText(current);
            if (!item.RemainingPercent.HasValue && item.Remaining.HasValue && String.IsNullOrEmpty(item.DisplayValue))
                balance = item.ServiceId == "openai" || item.ServiceId == "higgsfield-api" ? "$" + item.Remaining.Value.ToString("0.##",CultureInfo.InvariantCulture) : balance + " cr";
            var remaining = DashboardText(balance,24,"#252827",FontWeights.SemiBold); UiL.Raw(remaining); remaining.TextAlignment = TextAlignment.Right;
            remaining.TextWrapping = TextWrapping.NoWrap; remaining.TextTrimming = TextTrimming.CharacterEllipsis; remaining.ToolTip = L.Text("잔량") + " " + balance; value.Children.Add(remaining);
            var countdown = DashboardText(ResetText(item.ResetsAt),13,"#647070",FontWeights.Normal,new Thickness(0,4,0,0));
            countdown.TextAlignment = TextAlignment.Right; value.Children.Add(countdown);
            Grid.SetColumn(value,3); line.Children.Add(value);
            line.SizeChanged += (s,e) => ArrangeDashboardTimelineRow(line,value,remaining,countdown);
            var card = new Border { Child = line, Padding = new Thickness(16,14,16,14), Background = System.Windows.Media.Brushes.Transparent };
            card.ToolTip = item.LocalReset.ToString("yyyy-MM-dd HH:mm zzz",CultureInfo.InvariantCulture)
                + " · " + L.Text(ResetText(item.ResetsAt)) + (item.Manual ? " · " + L.Text("수동 입력 일정") : "");
            string id = item.ServiceId;
            var button = DashboardSurfaceButton(card,() => OpenDashboardTimelineService(id));
            AutomationProperties.SetAutomationId(button,"dashboard-timeline:" + id + ":" + item.RowIndex.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(button,L.Text(UsageServiceName(id)) + " · " + metric + " · " + L.Text("초기화 예정") + " " + item.LocalReset.ToString("MM/dd HH:mm",CultureInfo.InvariantCulture) + " · " + L.Text("잔량") + " " + balance);
            AutomationProperties.SetHelpText(button,L.Text("자세히 보기") + " · " + L.Text(ResetText(item.ResetsAt))); return button;
        }

        private static void ArrangeDashboardTimelineRow(Grid line,StackPanel value,TextBlock remaining,TextBlock countdown)
        {
            bool narrow = line.ActualWidth > 0 && line.ActualWidth < 520;
            if (line.Tag is bool && (bool)line.Tag == narrow) return;
            line.Tag = narrow;
            line.RowDefinitions.Clear(); line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (narrow) line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            line.ColumnDefinitions[3].Width = narrow ? new GridLength(0) : GridLength.Auto;
            Grid.SetRow(value,narrow ? 1 : 0); Grid.SetColumn(value,narrow ? 2 : 3);
            Grid.SetColumnSpan(value,narrow ? 2 : 1);
            value.Margin = new Thickness(0,narrow ? 10 : 0,0,0);
            value.MaxWidth = narrow ? Double.PositiveInfinity : 170;
            value.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            remaining.TextAlignment = countdown.TextAlignment = narrow ? TextAlignment.Left : TextAlignment.Right;
        }

        private void OpenDashboardTimelineService(string id)
        {
            // A visible button may outlive a disconnect or entitlement change.
            if (!ProFeaturesAvailable || !DashboardViews().Any(view => view.Id == id)) { RefreshDashboardIfOpen(); return; }
            OpenDashboardService(id);
        }
    }
}
