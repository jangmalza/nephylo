using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static class GeometryTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    [STAThread]
    private static int Main()
    {
        try {
            var value = new WidgetSettings { Left = -1420, Top = 60, Width = 480, Height = 720 };
            Rect secondary = new Rect(-1920, 0, 1920, 1040);
            Rect primary = new Rect(0, 0, 1280, 680);
            Check(WidgetWindow.ResolveWidgetBounds(value, false, secondary) == new Rect(-1420, 60, 480, 720),
                "Normal geometry restores on a monitor with a negative origin");
            Check(WidgetWindow.ResolveWidgetBounds(value, true, secondary) == new Rect(-1420, 60, 420, 44),
                "A legacy compact preference starts near normal geometry with a fixed-height minibar");

            WidgetWindow.SaveWidgetGeometry(value, true, 50, 24, 760, 44);
            Check(value.Left == -1420 && value.Top == 60 && value.Width == 480 && value.Height == 720,
                "Closing or updating in compact mode leaves normal bounds untouched");
            Check(WidgetWindow.ResolveWidgetBounds(value, true, primary) == new Rect(50, 24, 760, 44),
                "Compact geometry restores its own position and width");
            WidgetWindow.SaveWidgetGeometry(value, false, -1380, 40, 520, 780);
            Check(value.CompactLeft == 50 && value.CompactTop == 24 && value.CompactWidth == 760,
                "Moving or resizing normal mode leaves compact bounds untouched");

            var desired = new WidgetSettings { CompactView = true, Theme = "light", RefreshSeconds = 300 };
            WidgetWindow.CopyWidgetGeometry(value, desired);
            Check(desired.Left == -1380 && desired.Top == 40 && desired.Width == 520 && desired.Height == 780,
                "Saving a settings dialog copies the normal geometry");
            Check(desired.CompactLeft == 50 && desired.CompactTop == 24 && desired.CompactWidth == 760,
                "Saving a settings dialog also copies compact geometry");
            Check(desired.CompactView && desired.Theme == "light" && desired.RefreshSeconds == 300,
                "Geometry copying does not replace the user's pending preferences");

            Rect compactMoved = WidgetWindow.ResolveWidgetBounds(value, true, secondary);
            Check(secondary.Contains(compactMoved) && compactMoved.Height == 44 && compactMoved.Width == 760,
                "A disconnected monitor's compact position clamps to the available work area");
            Rect normalMoved = WidgetWindow.ResolveWidgetBounds(value, false, primary);
            Check(primary.Contains(normalMoved) && normalMoved.Width == 520 && normalMoved.Height == 680,
                "A disconnected monitor's normal window clamps both position and height");
            Check(value.Height == 780 && value.CompactLeft == 50,
                "Resolving bounds does not overwrite separately saved geometry");

            foreach (double scale in new double[] { 1, 1.25, 1.5, 2 }) {
                // Work areas have already been transformed from device pixels by the window.
                Rect logicalArea = new Rect(-1920 / scale, 40 / scale, 1920 / scale, 1040 / scale);
                Rect compact = WidgetWindow.ClampWidgetBounds(5000, -5000, 4000, 999, true, logicalArea);
                Check(logicalArea.Contains(compact) && compact.Height == 44 && compact.Width == logicalArea.Width,
                    "Minibar stays within the logical work area at scale " + scale);
                Rect normal = WidgetWindow.ClampWidgetBounds(-5000, 5000, 2000, 2000, false, logicalArea);
                Check(logicalArea.Contains(normal) && normal.Width <= 1000 && normal.Height <= 1400,
                    "Normal window respects the logical work area at scale " + scale);
            }

            Rect small = new Rect(0, 0, 220, 200);
            Check(small.Contains(WidgetWindow.ClampWidgetBounds(100, 100, 420, 44, true, small)),
                "Compact bounds do not overflow an unusually small desktop work area");
            Check(small.Contains(WidgetWindow.ClampWidgetBounds(100, 100, 320, 360, false, small)),
                "Normal bounds do not overflow an unusually small desktop work area");
            Rect fallback = WidgetWindow.ClampWidgetBounds(double.NaN, double.PositiveInfinity, double.NaN, double.NaN, true, primary);
            Check(primary.Contains(fallback) && fallback.Width == 420 && fallback.Height == 44,
                "Invalid transient geometry uses safe compact defaults");
            Rect shortNormal = WidgetWindow.ClampWidgetBounds(20, 20, 320, 220, false, primary);
            Check(shortNormal.Height == 220 && primary.Contains(shortNormal),
                "A short normal card can fit at 220 DIP without the previous forced empty height");
            Check(WidgetWindow.CalculateContentHeight(90, 36, 28, WidgetSettings.MinimumHeight, 900) == 220,
                "A single short provider fits its compact normal-mode minimum");
            Check(WidgetWindow.CalculateContentHeight(450, 36, 28, WidgetSettings.MinimumHeight, 900) == 540,
                "Multiple providers still grow to their natural content height");
            WidgetWindow.SaveWidgetGeometry(value, true, double.NaN, double.PositiveInfinity, 0, 0);
            Check(value.CompactLeft == 50 && value.CompactTop == 24 && value.CompactWidth == 760,
                "An unmeasured or invalid window cannot erase last valid compact bounds");
            CheckModeSwitchIntegration();
            Console.WriteLine("Geometry tests passed: " + assertions + " assertions. No visible windows, real settings or provider connections were opened.");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine("Geometry tests failed: " + ex);
            return 1;
        }
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(WidgetWindow window, string name)
    {
        return (T)typeof(WidgetWindow).GetField(name, Private).GetValue(window);
    }

    private static void Call(WidgetWindow window, string name, params object[] arguments)
    {
        try { typeof(WidgetWindow).GetMethod(name, Private).Invoke(window, arguments); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    private static void CheckModeSwitchIntegration()
    {
        string parent = Path.GetFullPath(Path.GetDirectoryName(typeof(GeometryTests).Assembly.Location));
        string isolated = Path.Combine(parent, "geometry-test-data-" + Guid.NewGuid().ToString("N"));
        string previousDirectory = SettingsStore.DataDirectoryOverride;
        WidgetWindow window = null;
        SettingsStore.DataDirectoryOverride = isolated;
        try {
            window = new WidgetWindow(true);
            Field<DispatcherTimer>(window, "timer").Stop();
            Field<Forms.NotifyIcon>(window, "tray").Visible = false;
            WidgetSettings settings = Field<WidgetSettings>(window, "settings");
            settings.Left = window.Left; settings.Top = window.Top;
            settings.Width = 320; settings.Height = 360;
            settings.CompactLeft = window.Left; settings.CompactTop = window.Top;
            settings.CompactWidth = 420;
            settings.CompactAutoWidth = false;
            Call(window, "ApplyWidgetGeometry");
            double normalWidth = window.Width, normalHeight = window.Height;
            double normalLeft = window.Left, normalTop = window.Top;
            Call(window, "SetCompactView", true);
            Check(window.Height == 44 && window.MinHeight == 44 && window.MaxHeight == 44,
                "WPF compact constraints replace the normal minimum without coercion errors");
            Check(window.Width == 420 && settings.Width == normalWidth && settings.Height == normalHeight,
                "Entering compact mode restores its own width while retaining normal dimensions");
            window.Width = Math.Min(600, window.MaxWidth);
            Call(window, "CaptureWidgetGeometry");
            SettingsStore.Save(settings);
            WidgetSettings saved = SettingsStore.Load();
            Check(saved.CompactView && saved.CompactWidth == window.Width && saved.Width == normalWidth && saved.Height == normalHeight,
                "Closing or updating from the actual compact window persists both geometries independently");
            Call(window, "SetCompactView", false);
            Check(window.Width == normalWidth && window.Height == normalHeight && window.Left == normalLeft && window.Top == normalTop,
                "Returning to normal mode restores the prior actual window bounds");
            Check(window.MinHeight == 220 && window.MaxHeight > 44,
                "Normal mode releases the compact maximum-height constraint");
            CheckManualHeightChoice(window);
            Call(window, "SetCompactView", true);
            Check(window.Width == saved.CompactWidth && window.Height == 44,
                "Re-entering compact mode preserves a user-resized compact width");
            CheckMonitorChanges(window);
        } finally {
            if (window != null) {
                Field<DispatcherTimer>(window, "timer").Stop();
                window.Close();
            }
            SettingsStore.DataDirectoryOverride = previousDirectory;
            string fullIsolated = Path.GetFullPath(isolated);
            if (fullIsolated.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullIsolated).StartsWith("geometry-test-data-", StringComparison.Ordinal) && Directory.Exists(fullIsolated))
                Directory.Delete(fullIsolated, true);
        }
    }

    private static void CheckManualHeightChoice(WidgetWindow window)
    {
        WidgetSettings settings = Field<WidgetSettings>(window, "settings");
        settings.FitContentHeight = true;
        Call(window, "WidgetGeometryMessage", IntPtr.Zero, 0x0231, IntPtr.Zero, IntPtr.Zero, false);
        Call(window, "WidgetGeometryMessage", IntPtr.Zero, 0x0214, new IntPtr(6), IntPtr.Zero, false);
        window.Height = 430;
        Call(window, "WidgetGeometryMessage", IntPtr.Zero, 0x0232, IntPtr.Zero, IntPtr.Zero, false);
        Check(!settings.FitContentHeight && settings.Height == 430,
            "An actual bottom-edge height change keeps the user's chosen height instead of re-fitting on refresh");
        SettingsStore.Save(settings);
        WidgetSettings saved = SettingsStore.Load();
        Check(!saved.FitContentHeight && saved.Height == 430,
            "A manually sized height and disabled automatic fit survive a restart");

        Call(window, "SetFitContentHeight", true);
        Call(window, "WidgetGeometryMessage", IntPtr.Zero, 0x0231, IntPtr.Zero, IntPtr.Zero, false);
        Call(window, "WidgetGeometryMessage", IntPtr.Zero, 0x0214, new IntPtr(2), IntPtr.Zero, false);
        window.Width += 15;
        Call(window, "WidgetGeometryMessage", IntPtr.Zero, 0x0232, IntPtr.Zero, IntPtr.Zero, false);
        Check(settings.FitContentHeight,
            "A width-only resize keeps content fitting enabled for wrapping and future provider updates");

        Call(window, "WidgetGeometryMessage", IntPtr.Zero, 0x0231, IntPtr.Zero, IntPtr.Zero, false);
        window.Height += 10;
        Call(window, "WidgetGeometryMessage", IntPtr.Zero, 0x0232, IntPtr.Zero, IntPtr.Zero, false);
        Check(settings.FitContentHeight,
            "A drag or DPI-adjusted height without a sizing handle does not disable automatic fitting");

        Call(window, "WidgetGeometryMessage", IntPtr.Zero, 0x0231, IntPtr.Zero, IntPtr.Zero, false);
        Call(window, "WidgetGeometryMessage", IntPtr.Zero, 0x0214, new IntPtr(4), IntPtr.Zero, false);
        window.Width += 10;
        Call(window, "WidgetGeometryMessage", IntPtr.Zero, 0x0232, IntPtr.Zero, IntPtr.Zero, false);
        Check(settings.FitContentHeight,
            "Using a corner handle without changing height does not disable automatic fitting");
    }

    private static void CheckMonitorChanges(WidgetWindow window)
    {
        WidgetSettings settings = Field<WidgetSettings>(window, "settings");
        var saved = new WidgetSettings();
        WidgetWindow.CopyWidgetGeometry(settings,saved);
        Rect smaller = new Rect(0,0,1280,680), larger = new Rect(0,0,1920,1040);
        window.Left = 20; window.Top = 30; window.Width = 600;
        Call(window,"ApplyWidgetWorkArea",smaller);
        Check(window.MaxWidth == 1280 && window.Height == 44 && window.MaxHeight == 44,
            "A compact window uses the smaller monitor's width cap without changing its fixed height");
        Call(window,"ApplyWidgetWorkArea",larger);
        Check(window.MaxWidth == 1920 && window.Width == 600 && window.Left == 20 && window.Top == 30,
            "Moving to a larger monitor removes the old width cap without moving or expanding an already fitting minibar");
        window.Width = 1600;
        Call(window,"ApplyWidgetWorkArea",larger);
        Check(window.Width == 1600 && window.MaxWidth == 1920,
            "After a monitor move the minibar can be widened beyond the previous monitor's limit");
        Call(window,"ApplyWidgetWorkArea",smaller);
        Check(window.MaxWidth == 1280 && window.Width == 1280 && window.Left == 0 && window.Height == 44,
            "Moving a wide minibar back to a smaller monitor keeps the entire current window reachable");
        Rect scaledSecondary = new Rect(-1280,0,1280,1040 / 1.5);
        Call(window,"ApplyWidgetWorkArea",scaledSecondary);
        Check(scaledSecondary.Contains(new Rect(window.Left,window.Top,window.Width,window.Height)) && window.Height == 44,
            "A changed 150-percent logical work area keeps the minibar within a negative-origin monitor at 44 DIP");
        Check(settings.Left == saved.Left && settings.Top == saved.Top && settings.Width == saved.Width && settings.Height == saved.Height
            && settings.CompactLeft == saved.CompactLeft && settings.CompactTop == saved.CompactTop && settings.CompactWidth == saved.CompactWidth,
            "Monitor reconciliation does not overwrite either saved view's geometry");

        settings.CompactView = false;
        window.Width = 500; window.Height = 500;
        Call(window,"ApplyWidgetWorkArea",smaller);
        Check(window.MaxHeight == 680 && window.MinHeight == 220,
            "Normal mode adopts the smaller monitor's height limit");
        Call(window,"ApplyWidgetWorkArea",larger);
        Check(window.MaxHeight == 1040 && window.Height == 500,
            "Normal mode releases the previous height limit without changing the current user-sized height");
        settings.CompactView = true;
        Call(window,"ApplyWidgetWorkArea",larger);
        window.Width = 1600; window.Left = 15;
        Call(window,"WidgetGeometryMessage",IntPtr.Zero,0x0231,IntPtr.Zero,IntPtr.Zero,false);
        Call(window,"ApplyWidgetWorkArea",smaller);
        Call(window,"WidgetGeometryMessage",IntPtr.Zero,0x02E0,IntPtr.Zero,IntPtr.Zero,false);
        Check(Field<bool>(window,"widgetGeometryMoving") && window.Width == 1600 && window.Left == 15 && window.MaxWidth == 1920,
            "Crossing a monitor edge during an active drag never snaps the window back or changes its constraints mid-drag");
        Check(Field<bool>(window,"widgetGeometryRefreshRequested") && Field<DispatcherOperation>(window,"widgetGeometryRefresh") == null,
            "DPI changes during dragging are remembered without scheduling a competing reposition operation");
        Call(window,"WidgetGeometryMessage",IntPtr.Zero,0x0232,IntPtr.Zero,IntPtr.Zero,false);
        var pending = Field<DispatcherOperation>(window,"widgetGeometryRefresh");
        Check(!Field<bool>(window,"widgetGeometryMoving") && pending != null && pending.Status == DispatcherOperationStatus.Pending,
            "Finishing the drag schedules one deferred reconciliation after native DPI and layout updates");
        Call(window,"QueueWidgetGeometryRefresh");
        Check(Object.ReferenceEquals(pending,Field<DispatcherOperation>(window,"widgetGeometryRefresh")),
            "Repeated location and display notifications coalesce into one pending geometry update");
        Call(window,"WidgetGeometryClosed",null,EventArgs.Empty);
        Check(Field<bool>(window,"widgetGeometryDisposed") && pending.Status == DispatcherOperationStatus.Aborted
            && Field<DispatcherOperation>(window,"widgetGeometryRefresh") == null,
            "Closing or source shutdown aborts pending geometry work and detaches monitoring");
        Call(window,"QueueWidgetGeometryRefresh");
        Check(Field<DispatcherOperation>(window,"widgetGeometryRefresh") == null,
            "A late event after cleanup cannot enqueue work on the disposed window");
    }
}
