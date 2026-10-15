using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static class CompactAutoGeometryTests
{
    private static int assertions;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static T Field<T>(WidgetWindow window, string name)
    { return (T)typeof(WidgetWindow).GetField(name, Private).GetValue(window); }
    private static void SetField(WidgetWindow window, string name, object value)
    { typeof(WidgetWindow).GetField(name, Private).SetValue(window, value); }
    private static object Call(WidgetWindow window, string name, params object[] args)
    {
        try { return typeof(WidgetWindow).GetMethod(name, Private).Invoke(window, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }
    private static void Message(WidgetWindow window, int code, int edge)
    { Call(window, "WidgetGeometryMessage", IntPtr.Zero, code, new IntPtr(edge), IntPtr.Zero, false); }

    [STAThread]
    private static int Main()
    {
        try {
            var desktop = new Rect(-1920, 0, 1920, 1040);
            Check(WidgetWindow.CalculateCompactAutoWidth(520.3, desktop) == 521, "Natural content width rounds up without clipping");
            Check(WidgetWindow.CalculateCompactAutoWidth(5000, desktop) == 1536, "Automatic width stops at eighty percent of its work area");
            Check(WidgetWindow.CalculateCompactAutoWidth(100, desktop) == 240, "Automatic mode retains its minimum control width");
            foreach (double invalid in new double[] { Double.NaN, Double.PositiveInfinity, -10 })
                Check(WidgetWindow.CalculateCompactAutoWidth(invalid, desktop) == 240, "Invalid measurements use a safe minimum");
            Check(WidgetWindow.CalculateCompactAutoWidth(900, new Rect(0, 0, 220, 200)) == 220, "Tiny desktops remain fully reachable");
            Check(WidgetWindow.CalculateCompactAutoWidth(900, new Rect(0, 0, 260, 200)) == 240, "Controls retain minimum width when eighty percent is too narrow");
            Check(WidgetWindow.CalculateCompactAutoWidth(900, Rect.Empty) == 819.2, "Unavailable work areas use the safe desktop fallback");
            foreach (double dpi in new double[] { 1, 1.25, 1.5, 2 }) {
                var logical = new Rect(-1920 / dpi, 0, 1920 / dpi, 1040 / dpi);
                Check(Math.Abs(WidgetWindow.CalculateCompactAutoWidth(5000, logical) - logical.Width * .8) < .001,
                    "Automatic cap is expressed in logical units at DPI scale " + dpi);
            }
            CheckWindowIntegration();
            Console.WriteLine("Compact auto geometry tests passed: " + assertions + " assertions. Hidden in-memory window; no user settings or provider calls.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void CheckWindowIntegration()
    {
        string parent = Path.GetFullPath(Path.GetDirectoryName(typeof(CompactAutoGeometryTests).Assembly.Location));
        string isolated = Path.Combine(parent, "compact-auto-test-data-" + Guid.NewGuid().ToString("N"));
        string previous = SettingsStore.DataDirectoryOverride;
        WidgetWindow window = null;
        SettingsStore.DataDirectoryOverride = isolated;
        try {
            window = new WidgetWindow(true);
            Field<DispatcherTimer>(window, "timer").Stop();
            Field<Forms.NotifyIcon>(window, "tray").Visible = false;
            var settings = Field<WidgetSettings>(window, "settings");
            settings.CompactView = true; settings.CompactAutoWidth = true;
            settings.CompactWidth = 240; settings.CompactLeft = 20; settings.CompactTop = 30;
            SetField(window, "compactPreferredWidth", 680.0);
            Call(window, "ApplyWidgetGeometry");
            var actualArea = (Rect)Call(window, "GetWidgetWorkArea", settings.CompactLeft, settings.CompactTop);
            Check(window.Width == WidgetWindow.CalculateCompactAutoWidth(680, actualArea), "Startup ignores a previously saved narrow width in automatic mode");
            Check(window.Height == 44 && window.MinHeight == 44 && window.MaxHeight == 44, "Automatic growth keeps fixed compact height");
            Check(settings.CompactWidth == 240, "Applying automatic geometry does not repeatedly write a saved manual width");

            var large = new Rect(-1920, 0, 1920, 1040);
            var small = new Rect(0, 0, 800, 600);
            SetField(window, "compactPreferredWidth", 1200.0);
            Call(window, "ApplyWidgetWorkArea", small);
            Check(window.Width == 640 && small.Contains(new Rect(window.Left, window.Top, window.Width, window.Height)), "Many services shrink to the new monitor and stay visible");
            Call(window, "ApplyWidgetWorkArea", large);
            Check(window.Width == 1200 && large.Contains(new Rect(window.Left, window.Top, window.Width, window.Height)), "Moving to a larger work area recovers full content width automatically");
            SetField(window, "compactPreferredWidth", 330.0);
            Call(window, "ApplyWidgetWorkArea", large);
            Check(window.Width == 330 && settings.CompactAutoWidth, "Service removal can reduce width without changing mode");

            Message(window, 0x0231, 0);
            SetField(window, "compactPreferredWidth", 500.0);
            Call(window, "QueueCompactAutoWidth");
            Call(window, "QueueCompactAutoWidth");
            Check(window.Width == 330 && Field<DispatcherOperation>(window, "widgetGeometryRefresh") == null,
                "Automatic width remains deferred for an active drag");
            Message(window, 0x0232, 0);
            Check(settings.CompactAutoWidth, "Finishing a pure drag retains automatic mode");
            var pending = Field<DispatcherOperation>(window, "widgetGeometryRefresh");
            Call(window, "QueueCompactAutoWidth");
            Check(pending != null && Object.ReferenceEquals(pending, Field<DispatcherOperation>(window, "widgetGeometryRefresh")), "Service and monitor refreshes share one coalesced geometry operation");

            foreach (int edge in new int[] { 1, 2, 4, 5, 7, 8 }) {
                settings.CompactAutoWidth = true;
                Message(window, 0x0231, 0);
                Message(window, 0x0214, edge);
                window.Width += 20;
                Message(window, 0x0232, 0);
                Check(!settings.CompactAutoWidth, "Changing width with native edge " + edge + " selects manual width");
                Check(settings.CompactWidth == window.Width, "Manual resize captures the chosen width for edge " + edge);
            }
            settings.CompactAutoWidth = true;
            Message(window, 0x0231, 0); Message(window, 0x0214, 2); Message(window, 0x0232, 0);
            Check(settings.CompactAutoWidth, "Touching a sizing handle without an actual width change retains automatic mode");
            Message(window, 0x0231, 0); Message(window, 0x0214, 3); window.Width += 10; Message(window, 0x0232, 0);
            Check(settings.CompactAutoWidth, "Top-edge or DPI-only width changes do not select manual width");

            settings.CompactAutoWidth = false;
            var copied = new WidgetSettings(); WidgetWindow.CopyWidgetGeometry(settings, copied);
            Check(!copied.CompactAutoWidth && copied.CompactWidth == settings.CompactWidth, "Saving settings dialog preserves the current manual width preference");
            window.Width = 430;
            Call(window, "ApplyWidgetWorkArea", small);
            Check(window.Width == 430, "Manual mode keeps chosen width while reconciling its monitor");
        } finally {
            if (window != null) { Field<DispatcherTimer>(window, "timer").Stop(); window.Close(); }
            SettingsStore.DataDirectoryOverride = previous;
            string full = Path.GetFullPath(isolated);
            if (full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("compact-auto-test-data-", StringComparison.Ordinal) && Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }
}
