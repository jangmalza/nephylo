using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

// In-process clocks only: no shown windows, real accounts, network or settings.
internal static class FoldMotionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static int checks;
    private static void Check(bool valid, string message) { ++checks; if (!valid) throw new Exception(message); }
    private static T Field<T>(WidgetWindow window, string name) { return (T)typeof(WidgetWindow).GetField(name,Private).GetValue(window); }
    private static object Call(WidgetWindow window, string name, params object[] args)
    {
        try { return typeof(WidgetWindow).GetMethod(name,Private).Invoke(window,args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (sender,args) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Measure(FrameworkElement element)
    {
        element.Measure(new Size(250,Double.PositiveInfinity));
        element.Arrange(new Rect(0,0,250,element.DesiredSize.Height));
    }
    private static void CheckReset(ProviderView view, bool collapsed)
    {
        Check(!view.Body.HasAnimatedProperties && !view.Summary.HasAnimatedProperties,"Finishing removes body and summary animation clocks");
        Check(Double.IsNaN(view.Body.Height) && Double.IsNaN(view.Summary.Height),"Finishing restores natural content height");
        Check(view.Body.Opacity == 1 && view.Summary.Opacity == 1,"Finishing restores readable text opacity");
        Check(!view.Body.ClipToBounds && !view.Summary.ClipToBounds,"Finishing restores unclipped content");
        Check(view.Body.Visibility == (collapsed ? Visibility.Collapsed : Visibility.Visible)
            && view.Summary.Visibility == (collapsed ? Visibility.Visible : Visibility.Collapsed),"Finishing leaves the requested detail or summary visible");
        Check(view.Body.Margin.Top == 4 && view.Summary.Margin.Top == 5,"Finishing restores the original layout margins");
    }
    [STAThread]
    public static int Main()
    {
        string isolated = Path.Combine(Path.GetTempPath(),"usage-widget-fold-test-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\fold-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated,"claude-config");
        WidgetWindow window = null;
        try {
            window = new WidgetWindow(true);
            Field<DispatcherTimer>(window,"timer").Stop();
            Field<Forms.NotifyIcon>(window,"tray").Visible = false;
            Call(window,"ShowDemo");
            var view = Field<ProviderView>(window,"codexView");
            var settings = Field<WidgetSettings>(window,"settings");
            var motion = ((IDictionary)typeof(WidgetWindow).GetField("providerFoldMotions",Private).GetValue(window))["codex"];
            var part = motion.GetType().GetField("Body").GetValue(motion);
            Check(!window.IsVisible && !window.IsLoaded,"Fixture never displays a desktop window");
            settings.AnimationsEnabled = true;
            Call(window,"ToggleProvider","codex"); CheckReset(view,true);
            Call(window,"ToggleProvider","codex"); CheckReset(view,false);
            Measure(view.Body);
            double originalHeight = view.Body.ActualHeight;
            int originalChildren = view.Body.Children.Count;
            int firstCompleted = 0, reversedCompleted = 0;
            var firstClock = (AnimationClock)Call(window,"AnimateFoldPart",part,false,new Action(() => ++firstCompleted));
            firstClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(65),TimeSeekOrigin.BeginTime);
            double midway = view.Body.Height;
            Check(midway > 0 && midway < originalHeight,"Collapse advances through an intermediate size (" + originalHeight + " -> " + midway + ")");
            var reversedClock = (AnimationClock)Call(window,"AnimateFoldPart",part,true,new Action(() => { ++reversedCompleted; Call(window,"FinishProviderFold",motion); }));
            Check(Math.Abs(view.Body.Height - midway) < 1,"A rapid reversal starts at the current animated height");
            reversedClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(220),TimeSeekOrigin.BeginTime);
            Pump(30);
            Check(firstCompleted == 0 && reversedCompleted == 1,"Replacing a fold cancels the stale completion and completes only its replacement");
            CheckReset(view,false);
            Check(view.Body.Children.Count == originalChildren,"Rapid reversals preserve every usage detail child");

            var interruptedClock = (AnimationClock)Call(window,"AnimateFoldPart",part,false,new Action(() => ++firstCompleted));
            interruptedClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(40),TimeSeekOrigin.BeginTime);
            Call(window,"Render",view,true);
            CheckReset(view,false);
            Pump(220);
            Check(firstCompleted == 0,"A hidden-widget render snaps the fold and cancels its pending completion");
            Check(view.Body.Children.Count == originalChildren,"Routine rendering during a fold preserves usage details");

            settings.AnimationsEnabled = false;
            Call(window,"ToggleProvider","codex"); CheckReset(view,true);
            Call(window,"Render",view,true); CheckReset(view,true);
            Check(view.Summary.Text.Contains("%"),"Collapsed refresh keeps actual quota in the summary");
            Call(window,"ToggleProvider","codex"); CheckReset(view,false);
            Check(!Directory.Exists(isolated),"The animation fixture performs no settings or account writes");
            Console.WriteLine("Fold motion tests passed: " + checks + " assertions. No desktop windows or live account data were used.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if (window != null) {
                Field<DispatcherTimer>(window,"timer").Stop(); window.Close();
                Field<CodexService>(window,"codex").Dispose(); Field<Forms.NotifyIcon>(window,"tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null;
            SettingsStore.StartupRegistrySubKeyOverride = null;
            ClaudeUsageService.ConfigDirectoryOverride = null;
        }
    }
}
