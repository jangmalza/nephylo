using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private StackPanel providerStack;
        private FrameworkElement normalWidgetRoot, compactWidgetRoot;
        private FrameworkElement widgetHeader, widgetFooter, widgetMembershipNotice;
        private bool fitHeightQueued;
        private readonly Dictionary<string, StackPanel> serviceCards = new Dictionary<string, StackPanel>();
        private readonly Dictionary<string, Border> serviceDividers = new Dictionary<string, Border>();
        private readonly Dictionary<string, FrameworkElement> serviceActions = new Dictionary<string, FrameworkElement>();
        private readonly Dictionary<string, ProviderFoldMotion> providerFoldMotions = new Dictionary<string, ProviderFoldMotion>();

        private sealed class FoldPart
        {
            public readonly FrameworkElement Element;
            public readonly Thickness Margin;
            public readonly bool ClipToBounds;
            public AnimationClock Clock;
            public FoldPart(FrameworkElement element)
            { Element = element; Margin = element.Margin; ClipToBounds = element.ClipToBounds; }
        }

        private sealed class ProviderFoldMotion
        {
            public FoldPart Body, Summary;
            public bool Collapsed, Running;
            public int Version;
        }

        private StackPanel AddProviderCard(StackPanel parent, string id, string name, ProviderView view)
        {
            var card = new LocalizedStackPanel();
            var divider = new LocalizedBorder { Height = 3, Background = PaperDetails.Rule(), Margin = new Thickness(0,9,0,9) };
            card.Children.Add(divider); card.Children.Add(CreateCard(name,id,view));
            serviceCards[id] = card; serviceDividers[id] = divider; parent.Children.Add(card);
            return card;
        }

        private ProviderView ViewFor(string id)
        {
            switch (id) {
                case "codex": return codexView;
                case "eleven": return elevenView;
                case "openai": return openAiView;
                case "claude": return claudeView;
                case "gemini": return geminiView;
                case "cursor": return cursorView;
                case "higgsfield": return higgsfieldView;
                case "higgsfield-api": return higgsfieldApiView;
                default: throw new ArgumentException("지원하지 않는 서비스입니다.");
            }
        }

        private void UpdateProviderFold(ProviderView view, bool animate = false)
        {
            if (view.Summary == null) return;
            bool collapsed = settings.CollapsedServices.Contains(view.Id);
            ((System.Windows.Media.RotateTransform)view.Chevron.RenderTransform).Angle = collapsed ? 0 : 90;
            string foldAction = (string)view.FoldButton.Tag + (collapsed ? " 펼치기" : " 접기");
            System.Windows.Automation.AutomationProperties.SetName(view.FoldButton,foldAction);
            System.Windows.Automation.AutomationProperties.SetItemStatus(view.FoldButton,collapsed ? "접힘" : "펼쳐짐");
            view.Summary.Text = view.Snapshot == null || view.Snapshot.Rows == null || !view.Snapshot.Rows.Any(row => row != null)
                ? UsagePresentation.Describe(view,DateTimeOffset.Now).Label :
                String.Join(" · ", view.Snapshot.Rows.Where(row => row != null).Select(row => (row.Label ?? "잔여량").Replace("Codex · ","").Replace(" 한도","") + " " +
                    UsageValue(row)));
            UiL.Preserve(view.Summary,!demo && UsageFreshness.Source(view) == "manual" && view.Id != "higgsfield-api");
            view.Summary.ToolTip = view.Body.ToolTip;
            FrameworkElement actions;
            if (serviceActions.TryGetValue(view.Id,out actions)) actions.Visibility = collapsed || settings.CompactView ? Visibility.Collapsed : Visibility.Visible;

            ProviderFoldMotion motion;
            if (!providerFoldMotions.TryGetValue(view.Id,out motion)) {
                motion = new ProviderFoldMotion { Body = new FoldPart(view.Body), Summary = new FoldPart(view.Summary), Collapsed = collapsed };
                providerFoldMotions[view.Id] = motion;
                var trackedMotion = motion;
                view.Body.IsVisibleChanged += (s,e) => {
                    // Hidden widgets must not retain layout clocks or half-folded content.
                    if (trackedMotion.Running && !MotionAllowed) FinishProviderFold(trackedMotion);
                };
                FinishProviderFold(motion);
                return;
            }
            bool changed = motion.Collapsed != collapsed;
            motion.Collapsed = collapsed;
            if (!MotionAllowed || (!animate && changed)) { FinishProviderFold(motion); return; }
            // Periodic usage renders update the summary without replaying a user's fold.
            if (!changed) return;
            var parent = view.Body.Parent as UIElement;
            if (!animate || parent == null || !parent.IsVisible) { FinishProviderFold(motion); return; }

            int version = ++motion.Version;
            motion.Running = true;
            AnimateFoldPart(motion.Summary,collapsed,null);
            AnimateFoldPart(motion.Body,!collapsed,() => {
                if (version == motion.Version) FinishProviderFold(motion);
            });
        }

        private AnimationClock AnimateFoldPart(FoldPart part, bool show, Action completed)
        {
            var element = part.Element;
            bool visible = element.Visibility == Visibility.Visible;
            double fromHeight = visible ? (Double.IsNaN(element.Height) ? element.ActualHeight : element.Height) : 0;
            double fromOpacity = visible ? element.Opacity : 0.65;
            Thickness fromMargin = visible ? element.Margin : new Thickness(0);
            StopFoldPartClocks(part);
            element.Height = Double.NaN; element.Margin = part.Margin;
            element.Visibility = Visibility.Visible;
            double width = element.ActualWidth > 0 ? element.ActualWidth + part.Margin.Left + part.Margin.Right : Math.Max(1,ActualWidth - 40);
            element.Measure(new Size(width,Double.PositiveInfinity));
            double targetHeight = show ? Math.Max(0,element.DesiredSize.Height - part.Margin.Top - part.Margin.Bottom) : 0;
            element.Height = Math.Max(0,fromHeight); element.Margin = fromMargin;
            element.Opacity = fromOpacity; element.ClipToBounds = true;
            var duration = new Duration(TimeSpan.FromMilliseconds(190));
            var height = new DoubleAnimation(Math.Max(0,fromHeight),targetHeight,duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var heightClock = (AnimationClock)height.CreateClock(true);
            part.Clock = heightClock;
            if (completed != null) heightClock.Completed += (s,e) => { if (Object.ReferenceEquals(part.Clock,heightClock)) completed(); };
            element.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(fromOpacity,show ? 1 : 0.65,duration));
            element.BeginAnimation(FrameworkElement.MarginProperty,new ThicknessAnimation(fromMargin,show ? part.Margin : new Thickness(0),duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            element.ApplyAnimationClock(FrameworkElement.HeightProperty,heightClock);
            return heightClock;
        }

        private void FinishProviderFold(ProviderFoldMotion motion)
        {
            ++motion.Version; motion.Running = false;
            ResetFoldPart(motion.Body,!motion.Collapsed);
            ResetFoldPart(motion.Summary,motion.Collapsed);
            RequestFitHeight();
        }

        private static void ResetFoldPart(FoldPart part, bool show)
        {
            StopFoldPartClocks(part);
            part.Element.Height = Double.NaN; part.Element.Opacity = 1;
            part.Element.Margin = part.Margin; part.Element.ClipToBounds = part.ClipToBounds;
            part.Element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void StopFoldPartClocks(FoldPart part)
        {
            var clock = part.Clock; part.Clock = null;
            if (clock != null) clock.Controller.Remove();
            part.Element.BeginAnimation(FrameworkElement.HeightProperty,null);
            part.Element.BeginAnimation(UIElement.OpacityProperty,null);
            part.Element.BeginAnimation(FrameworkElement.MarginProperty,null);
        }

        private void ToggleProvider(string id)
        {
            var previous = settings.CollapsedServices;
            settings.CollapsedServices = previous.Contains(id) ? previous.Where(x => x != id).ToArray() : previous.Concat(new[] {id}).ToArray();
            try { if (!demo) SettingsStore.Save(settings); }
            catch { settings.CollapsedServices = previous; footer.Text = "접기 설정을 저장하지 못했습니다."; }
            UpdateProviderFold(ViewFor(id),true);
        }

        internal static string UsageValue(UsageRow row)
        {
            return row.DisplayValue ?? (row.RemainingPercent.HasValue ? row.RemainingPercent.Value.ToString("0.#",System.Globalization.CultureInfo.InvariantCulture) + "%" :
                row.Remaining.HasValue ? row.Remaining.Value.ToString("N0") : !String.IsNullOrWhiteSpace(row.Detail) && row.Detail.Contains("무제한") ? "무제한" : "—");
        }

        private void ApplyServiceLayout()
        {
            if (providerStack == null) return;
            var ordered = settings.ServiceOrder.Where(serviceCards.ContainsKey).Select(id => serviceCards[id]).ToArray();
            if (!providerStack.Children.Cast<UIElement>().SequenceEqual(ordered)) {
                providerStack.Children.Clear(); foreach (var card in ordered) providerStack.Children.Add(card);
            }
            bool first = true;
            foreach (var id in settings.ServiceOrder) {
                if (!serviceCards.ContainsKey(id)) continue;
                bool visible = serviceCards[id].Visibility == Visibility.Visible;
                serviceDividers[id].Visibility = visible && !first ? Visibility.Visible : Visibility.Collapsed;
                serviceDividers[id].Margin = new Thickness(0,settings.CompactView ? 7 : 9,0,settings.CompactView ? 7 : 9);
                if (visible) first = false;
                UpdateProviderFold(ViewFor(id));
            }
            RequestFitHeight();
            RefreshCompactBar();
        }

        private void RequestFitHeight()
        {
            if (fitHeightQueued || exiting || widgetGeometryMoving || settings.CompactView || !settings.FitContentHeight || !IsLoaded) return;
            fitHeightQueued = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(() => {
                fitHeightQueued = false;
                if (exiting || widgetGeometryMoving || settings.CompactView || !settings.FitContentHeight || providerStack == null || providerFoldMotions.Values.Any(m => m.Running)) return;
                double width = Math.Max(1,ActualWidth - 39);
                providerStack.Measure(new Size(width,Double.PositiveInfinity));
                var area = SystemParameters.WorkArea;
                var source = System.Windows.PresentationSource.FromVisual(this);
                if (source != null && source.CompositionTarget != null) {
                    var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).WorkingArea;
                    var transform = source.CompositionTarget.TransformFromDevice;
                    area = new Rect(transform.Transform(new Point(screen.Left,screen.Top)),transform.Transform(new Point(screen.Right,screen.Bottom)));
                }
                double notice = widgetMembershipNotice != null && widgetMembershipNotice.Visibility == Visibility.Visible ? widgetMembershipNotice.DesiredSize.Height : 0;
                double desired = CalculateContentHeight(providerStack.DesiredSize.Height + notice,widgetHeader.DesiredSize.Height,widgetFooter.DesiredSize.Height,MinHeight,area.Height);
                if (Math.Abs(Height - desired) > 1) Height = desired;
                if (Top + Height > area.Bottom - 12) Top = Math.Max(area.Top + 12,area.Bottom - Height - 12);
            }));
        }

        internal static double CalculateContentHeight(double content, double header, double footerHeight, double minimum, double workHeight)
        {
            return Math.Max(minimum,Math.Min(Math.Ceiling(content + header + footerHeight + 26),Math.Max(minimum,workHeight - 24)));
        }

        private void SetFitContentHeight(bool enabled)
        {
            bool previous = settings.FitContentHeight;
            settings.FitContentHeight = enabled;
            try { if (!demo) SettingsStore.Save(settings); }
            catch { settings.FitContentHeight = previous; footer.Text = "높이 설정을 저장하지 못했습니다."; return; }
            RequestFitHeight();
        }

        private void SetCompactView(bool compact)
        {
            if (settings.CompactView == compact) return;
            CaptureWidgetGeometry();
            bool previous = settings.CompactView; settings.CompactView = compact;
            try { if (!demo) SettingsStore.Save(settings); }
            catch { settings.CompactView = previous; footer.Text = "보기 설정을 저장하지 못했습니다."; return; }
            ApplyWidgetViewVisibility(); ApplyWidgetGeometry();
            Render(codexView,true); Render(elevenView,false); RefreshLocalExtras(); UpdateFooter(); RequestFitHeight();
        }

        private void ApplyWidgetViewVisibility()
        {
            if (normalWidgetRoot != null) normalWidgetRoot.Visibility = settings.CompactView ? Visibility.Collapsed : Visibility.Visible;
            if (compactWidgetRoot != null) compactWidgetRoot.Visibility = settings.CompactView ? Visibility.Visible : Visibility.Collapsed;
            UpdateRefreshMotion();
        }

        private void BuildOrderControls(StackPanel body, string id)
        {
            var row = new WrapPanel { Margin = new Thickness(0,0,0,12) };
            var position = T("",11,"#647070",FontWeights.Normal,new Thickness(0,7,12,0)); row.Children.Add(position);
            var up = new LocalizedButton { Content = "위로", Padding = new Thickness(10,5,10,5), Margin = new Thickness(0,0,6,0) };
            var down = new LocalizedButton { Content = "아래로", Padding = new Thickness(10,5,10,5) };
            System.Windows.Automation.AutomationProperties.SetName(up,"서비스 표시 순서 위로");
            System.Windows.Automation.AutomationProperties.SetName(down,"서비스 표시 순서 아래로");
            Action update = () => { int n = Array.IndexOf(settings.ServiceOrder,id); position.Text = "표시 순서 " + (n + 1) + " / " + settings.ServiceOrder.Length; up.IsEnabled = n > 0; down.IsEnabled = n >= 0 && n < settings.ServiceOrder.Length - 1; };
            Action<int> move = offset => {
                var previous = settings.ServiceOrder; var next = previous.ToArray(); int n = Array.IndexOf(next,id); int target = n + offset;
                if (n < 0 || target < 0 || target >= next.Length) return;
                next[n] = next[target]; next[target] = id; settings.ServiceOrder = next;
                try { if (!demo) SettingsStore.Save(settings); ApplyServiceLayout(); update(); }
                catch { settings.ServiceOrder = previous; position.Text = "순서 저장 실패"; }
            };
            up.Click += (s,e) => move(-1); down.Click += (s,e) => move(1);
            row.Children.Add(up); row.Children.Add(down); update(); body.Children.Add(row);
            row.IsVisibleChanged += (s,e) => { if (row.IsVisible) update(); };
        }
    }
}
