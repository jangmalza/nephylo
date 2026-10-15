using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private bool DashboardMotionAllowed {
            get { return settings.AnimationsEnabled && SystemParameters.ClientAreaAnimation && dashboardWindow != null && dashboardWindow.IsVisible && !exiting; }
        }

        private void AnimateDashboardPage()
        {
            var page = dashboardContent == null ? null : dashboardContent.Content as FrameworkElement;
            if (page == null) return;
            page.BeginAnimation(UIElement.OpacityProperty,null); page.Opacity = 1;
            var move = new TranslateTransform(); page.RenderTransform = move;
            if (!DashboardMotionAllowed) return;
            var duration = TimeSpan.FromMilliseconds(180);
            // Only explicit navigation/initial opening calls this method. Polling
            // never fades content out or animates a number through invented values.
            page.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0.6,1,duration) { FillBehavior = FillBehavior.Stop },HandoffBehavior.SnapshotAndReplace);
            move.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(5,0,duration) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            },HandoffBehavior.SnapshotAndReplace);
        }

        private void AttachDashboardHover(Button button,FrameworkElement surface)
        {
            var move = new TranslateTransform(); surface.RenderTransform = move;
            Action<bool> animate = active => {
                double from = move.Y;
                move.BeginAnimation(TranslateTransform.YProperty,null); move.Y = 0;
                if (!DashboardMotionAllowed || !button.IsVisible) return;
                move.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(from,active ? -1 : 0,TimeSpan.FromMilliseconds(110)) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
                },HandoffBehavior.SnapshotAndReplace);
                move.Y = active ? -1 : 0;
            };
            button.MouseEnter += (s,e) => animate(true);
            button.MouseLeave += (s,e) => animate(false);
            button.Unloaded += (s,e) => { move.BeginAnimation(TranslateTransform.YProperty,null); move.Y = 0; };
        }

        private void RestoreDashboardFocus(string id)
        {
            if (String.IsNullOrEmpty(id) || dashboardWindow == null || !dashboardWindow.IsVisible) return;
            var content = dashboardContent.Content as DependencyObject;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(() => {
                if (dashboardWindow == null || !dashboardWindow.IsVisible || dashboardContent.Content != content || exiting) return;
                var target = FindDashboardFocus(dashboardRoot,id);
                if (target != null && target.IsEnabled && target.IsVisible) target.Focus();
            }));
        }

        private static UIElement FindDashboardFocus(DependencyObject root,string id)
        {
            if (root == null) return null;
            if (AutomationProperties.GetAutomationId(root) == id) return root as UIElement;
            foreach (object value in LogicalTreeHelper.GetChildren(root)) {
                var found = FindDashboardFocus(value as DependencyObject,id);
                if (found != null) return found;
            }
            return null;
        }
    }
}
