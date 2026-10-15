using System;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UsageWidget;

// Seek controllable WPF clocks on an STA thread without creating a window,
// touching settings, or contacting a service. This also avoids timing-sensitive tests.
internal static class MeterAnimationTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static AnimationClock Clock(PaperMeter meter)
    {
        return (AnimationClock)typeof(PaperMeter).GetField("activeAnimation",BindingFlags.NonPublic | BindingFlags.Instance).GetValue(meter);
    }

    private static void Seek(AnimationClock clock, int milliseconds)
    {
        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds),TimeSeekOrigin.BeginTime);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [STAThread]
    public static int Main()
    {
        try {
            var bounded = new PaperMeter(Double.NaN);
            Check(bounded.DisplayedPercentage == 0 && bounded.TargetPercentage == 0 && !bounded.HasAnimatedProperties,
                "Construction sanitizes NaN and never animates");
            double[] inputs = { -1, 101, Double.NegativeInfinity, Double.PositiveInfinity, Double.NaN, 0, 100, 17.25 };
            double[] expected = { 0, 100, 0, 100, 0, 0, 100, 17.25 };
            for (int i = 0; i < inputs.Length; i++) {
                bounded.SetPercentage(inputs[i],false);
                Check(bounded.DisplayedPercentage == expected[i] && bounded.TargetPercentage == expected[i]
                    && !bounded.HasAnimatedProperties,"Immediate updates clamp finite and non-finite values: " + i);
            }
            var meter = new PaperMeter(80);
            meter.SetPercentage(20,true);
            Check(meter.TargetPercentage == 20 && (double)meter.GetAnimationBaseValue(PaperMeter.DisplayedPercentageProperty) == 20,
                "The actual quota is the base value while a transition is active");
            Check(AutomationProperties.GetName(meter) == "남은 사용량 20%", "Assistive text reports the final quota immediately");
            Check(meter.HasAnimatedProperties, "Changed values create an animation clock");
            AnimationClock original = Clock(meter);
            Seek(original,90);
            double first = meter.DisplayedPercentage;
            Check(first > 20 && first < 80, "A transition interpolates instead of jumping to its target");
            meter.SetPercentage(20,true);
            Check(Object.ReferenceEquals(Clock(meter),original), "An identical target preserves the active animation instead of restarting it");
            Seek(original,150);
            Check(meter.DisplayedPercentage < first, "The existing transition keeps progressing after an identical refresh");
            double beforeRetarget = meter.DisplayedPercentage;
            meter.SetPercentage(100,true);
            AnimationClock replacement = Clock(meter);
            Seek(replacement,0);
            Check(Math.Abs(meter.DisplayedPercentage - beforeRetarget) < 0.0001,
                "A replacement transition begins at the currently displayed value");
            Check(meter.TargetPercentage == 100 && AutomationProperties.GetName(meter) == "남은 사용량 100%",
                "Retargeting publishes the new actual quota immediately");
            Seek(replacement,321);
            Pump();
            Check(meter.DisplayedPercentage == 100 && !meter.HasAnimatedProperties,
                "Completed transitions release their clocks and retain exact final values");
            meter.SetPercentage(0,true);
            AnimationClock stopped = Clock(meter);
            Seek(stopped,90);
            Check(meter.DisplayedPercentage > 0 && meter.DisplayedPercentage < 100, "A transition can interpolate toward zero");
            meter.SetPercentage(44,false);
            Check(meter.DisplayedPercentage == 44 && !meter.HasAnimatedProperties && Clock(meter) == null,
                "Disabling animation immediately removes an active clock and displays the actual quota");
            Seek(stopped,321);
            Pump();
            Check(meter.DisplayedPercentage == 44 && !meter.HasAnimatedProperties,
                "A stopped transition cannot overwrite a newer immediate value");
            meter.SetPercentage(0,false);
            Check(meter.DisplayedPercentage == 0 && !meter.HasAnimatedProperties, "Zero never gains an artificial minimum fill");
            meter.SetPercentage(0,true);
            Check(!meter.HasAnimatedProperties, "An unchanged value at rest does not create an idle clock");
            Console.WriteLine("Meter animation tests passed: " + assertions + " assertions. No windows, settings, accounts or APIs used.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
