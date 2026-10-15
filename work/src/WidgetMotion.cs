using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UsageWidget
{
    internal sealed class UsageRowVisual
    {
        internal TextBlock Number;
        internal PaperMeter Meter;
        internal string Quantity;
        internal int MotionVersion;
    }

    public partial class WidgetWindow
    {
        private bool MotionAllowed { get { return settings.AnimationsEnabled && SystemParameters.ClientAreaAnimation && IsVisible && !exiting; } }

        private TextBlock UsageNumber(ProviderView view, string key, string quantity, double fontSize)
        {
            UsageRowVisual visual;
            if (!view.RowVisuals.TryGetValue(key,out visual)) {
                visual = new UsageRowVisual { Number = T("",fontSize,"#2448F5",FontWeights.SemiBold,new Thickness(10,0,0,0)) };
                System.Windows.Documents.Typography.SetNumeralAlignment(visual.Number,FontNumeralAlignment.Tabular);
                visual.Number.RenderTransform = new TranslateTransform();
                view.RowVisuals.Add(key,visual);
            }
            Detach(visual.Number);
            visual.Number.FontSize = fontSize;
            bool changed = visual.Quantity != quantity;
            visual.Number.Text = quantity; // Always display the actual fetched value.
            visual.Quantity = quantity;
            if (!MotionAllowed || !view.Body.IsVisible) StopNumberMotion(visual);
            else if (changed) {
                StopNumberMotion(visual);
                int version = visual.MotionVersion;
                var duration = TimeSpan.FromMilliseconds(220);
                var fade = new DoubleAnimation(0.65,1,duration) { FillBehavior = FillBehavior.Stop };
                var slide = new DoubleAnimation(2,0,duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
                fade.Completed += (s,e) => { if (visual.MotionVersion == version) StopNumberMotion(visual); };
                visual.Number.BeginAnimation(UIElement.OpacityProperty,fade,HandoffBehavior.SnapshotAndReplace);
                ((TranslateTransform)visual.Number.RenderTransform).BeginAnimation(TranslateTransform.YProperty,slide,HandoffBehavior.SnapshotAndReplace);
            }
            return visual.Number;
        }

        private PaperMeter UsageMeter(ProviderView view, string key, double percentage)
        {
            var visual = view.RowVisuals[key];
            bool animate = MotionAllowed && view.Body.IsVisible;
            if (visual.Meter == null) visual.Meter = new PaperMeter(animate ? 0 : percentage);
            Detach(visual.Meter);
            visual.Meter.SetPercentage(percentage,animate);
            return visual.Meter;
        }

        private static void Detach(FrameworkElement element)
        {
            var parent = element.Parent as Panel;
            if (parent != null) parent.Children.Remove(element);
        }

        private static void HideUsageMeter(ProviderView view, string key, double? percentage)
        {
            var visual = view.RowVisuals[key];
            if (visual.Meter == null) return;
            visual.Meter.SetPercentage(percentage ?? visual.Meter.TargetPercentage,false);
            Detach(visual.Meter);
            if (!percentage.HasValue) visual.Meter = null;
        }

        private static void StopNumberMotion(UsageRowVisual visual)
        {
            visual.MotionVersion++;
            visual.Number.BeginAnimation(UIElement.OpacityProperty,null); visual.Number.Opacity = 1;
            var slide = visual.Number.RenderTransform as TranslateTransform;
            if (slide != null) { slide.BeginAnimation(TranslateTransform.YProperty,null); slide.Y = 0; }
        }

        private static void PruneUsageVisuals(ProviderView view, HashSet<string> keys)
        {
            foreach (string key in view.RowVisuals.Keys.Where(key => !keys.Contains(key)).ToArray()) {
                var visual = view.RowVisuals[key]; StopNumberMotion(visual);
                if (visual.Meter != null) visual.Meter.SetPercentage(visual.Meter.TargetPercentage,false);
                view.RowVisuals.Remove(key);
            }
        }

        private void UpdateRefreshMotion()
        {
            foreach (var button in new[] { refreshButton, compactRefreshButton }) {
                if (button == null) continue;
                var rotation = button.RenderTransform as RotateTransform;
                if (rotation == null) { rotation = new RotateTransform(); button.RenderTransform = rotation; button.RenderTransformOrigin = new Point(0.5,0.5); }
                if (refreshing && MotionAllowed && button.IsVisible) {
                    if (!rotation.HasAnimatedProperties) rotation.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromMilliseconds(950)) { RepeatBehavior = RepeatBehavior.Forever });
                } else { rotation.BeginAnimation(RotateTransform.AngleProperty,null); rotation.Angle = 0; }
            }
        }

        private void StopWidgetMotion()
        {
            UpdateRefreshMotion();
            foreach (var view in new[] { codexView,elevenView,openAiView,claudeView,geminiView,cursorView,higgsfieldView,higgsfieldApiView }) {
                foreach (var visual in view.RowVisuals.Values) {
                    StopNumberMotion(visual);
                    if (visual.Meter != null) visual.Meter.SetPercentage(visual.Meter.TargetPercentage,false);
                }
                UpdateProviderFold(view);
            }
        }

        private void MotionPreferenceChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != "ClientAreaAnimation" || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() => { if (!MotionAllowed) StopWidgetMotion(); else UpdateRefreshMotion(); }));
        }
    }
}
