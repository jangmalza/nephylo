using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UsageWidget
{
    // Original vector details inspired by printed technical manuals.
    internal static class PaperDetails
    {
        internal static DrawingImage Wordmark()
        {
            string[] glyphs = {
                "10001110011100110101100111001110001", // N
                "11111100001000011110100001000011111", // E
                "11110100011000111110100001000010000", // P
                "10001100011000111111100011000110001", // H
                "10001100010101000100001000010000100", // Y
                "10000100001000010000100001000011111", // L
                "01110100011000110001100011000101110"  // O
            };
            var group = new DrawingGroup();
            var brush = WidgetPalette.Brush("#2448F5");
            for (int letter = 0; letter < glyphs.Length; letter++)
                for (int cell = 0; cell < 35; cell++)
                    if (glyphs[letter][cell] == '1')
                        group.Children.Add(new GeometryDrawing(brush,null,new RectangleGeometry(new Rect((letter*6+cell%5)*2,(cell/5)*2,2,2))));
            return new DrawingImage(group);
        }

        internal static Brush Rule()
        {
            var dots = new GeometryGroup();
            dots.Children.Add(new RectangleGeometry(new Rect(0,0,1,1)));
            dots.Children.Add(new RectangleGeometry(new Rect(2,2,1,1)));
            var brush = new DrawingBrush(new GeometryDrawing(WidgetPalette.Brush("#9FABB2"),null,dots)) {
                TileMode = TileMode.Tile, Viewport = new Rect(0,0,4,4), ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0,0,4,4), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None
            };
            return brush;
        }
    }

    internal sealed class PaperMeter : FrameworkElement
    {
        internal static readonly DependencyProperty DisplayedPercentageProperty = DependencyProperty.Register(
            "DisplayedPercentage",typeof(double),typeof(PaperMeter),
            new FrameworkPropertyMetadata(0.0,FrameworkPropertyMetadataOptions.AffectsRender));
        private readonly Brush ink = WidgetPalette.Brush("#2448F5");
        private int animationVersion;
        private AnimationClock activeAnimation;
        private static readonly Brush Track = WidgetPalette.Brush("#E2E7F5");
        private static readonly Brush Gap = WidgetPalette.Brush("#FAFAF7");

        internal PaperMeter(double percentage)
        {
            Height = 5; Margin = new Thickness(0,6,0,5); SnapsToDevicePixels = true;
            SetPercentage(percentage,false);
        }

        internal double DisplayedPercentage { get { return (double)GetValue(DisplayedPercentageProperty); } }
        internal double TargetPercentage { get; private set; }

        internal void SetPercentage(double value, bool animate)
        {
            double target = Double.IsNaN(value) ? 0 : Math.Max(0,Math.Min(100,value));
            // An ordinary refresh can rebuild its surrounding row while the meter
            // is moving. Keeping the same target must not restart that movement.
            if (animate && target == TargetPercentage) return;
            double current = DisplayedPercentage;
            int version = ++animationVersion;
            TargetPercentage = target;
            System.Windows.Automation.AutomationProperties.SetName(this,"남은 사용량 " + target.ToString("0.#") + "%");
            // Store the final value first so clearing or stopping the clock always
            // reveals the actual quota, including exact zero and one hundred.
            SetValue(DisplayedPercentageProperty,target);
            if (!animate || current == target) {
                BeginAnimation(DisplayedPercentageProperty,null);
                activeAnimation = null;
                return;
            }
            var animation = new DoubleAnimation(current,target,TimeSpan.FromMilliseconds(320)) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            activeAnimation = (AnimationClock)animation.CreateClock(true);
            activeAnimation.Completed += delegate {
                if (version == animationVersion) {
                    BeginAnimation(DisplayedPercentageProperty,null);
                    activeAnimation = null;
                }
            };
            ApplyAnimationClock(DisplayedPercentageProperty,activeAnimation,HandoffBehavior.SnapshotAndReplace);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double width = ActualWidth;
            if (width <= 0) return;
            const int cells = 16;
            double step = width / cells;
            dc.DrawRectangle(Track,null,new Rect(0,0,width,5));
            // Clip to the exact quota; a tiny positive amount never becomes a full segment.
            dc.PushClip(new RectangleGeometry(new Rect(0,0,width * DisplayedPercentage / 100,5)));
            dc.DrawRectangle(ink,null,new Rect(0,0,width,5)); dc.Pop();
            dc.PushOpacity(0.45);
            for (int i = 1; i < cells; i++) dc.DrawRectangle(Gap,null,new Rect(i*step-0.5,0,1,5));
            dc.Pop();
        }
    }
}
