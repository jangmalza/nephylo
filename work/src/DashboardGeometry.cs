using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private void RestoreDashboardGeometry(Window window)
        {
            if (window == null) return;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            Rect area = GetDashboardWorkArea(window, settings.DashboardLeft, settings.DashboardTop);
            ApplyDashboardBounds(window, ClampDashboardBounds(settings.DashboardLeft, settings.DashboardTop,
                settings.DashboardWidth, settings.DashboardHeight, area), area);

            // Monitor transforms become accurate after the HWND is created. Keep
            // WPF's DPI handling intact and reconcile its resulting logical bounds.
            HwndSource source = null;
            HwndSourceHook hook = null;
            DispatcherOperation queued = null;
            bool closed = false, moving = false;
            Action reconcile = () => {
                if (closed || moving || window.WindowState != WindowState.Normal || window.Dispatcher.HasShutdownStarted || queued != null) return;
                queued = window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(() => {
                    queued = null;
                    if (closed || moving || window.WindowState != WindowState.Normal) return;
                    Rect currentArea = GetDashboardWorkArea(window,double.NaN,double.NaN);
                    ApplyDashboardBounds(window,ClampDashboardBounds(window.Left,window.Top,
                        window.ActualWidth > 0 ? window.ActualWidth : window.Width,
                        window.ActualHeight > 0 ? window.ActualHeight : window.Height,currentArea),currentArea);
                }));
            };
            EventHandler initialized = null;
            initialized = (sender,args) => {
                source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                if (source != null) {
                    hook = delegate(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
                        if (message == 0x0231) moving = true; // WM_ENTERSIZEMOVE
                        else if (message == 0x0232) { moving = false; reconcile(); }
                        else if (message == 0x02E0 || message == 0x007E || message == 0x001A) reconcile();
                        return IntPtr.Zero;
                    };
                    source.AddHook(hook);
                }
                reconcile();
            };
            window.SourceInitialized += initialized;
            EventHandler stateChanged = (sender,args) => reconcile();
            window.StateChanged += stateChanged;
            window.Closed += (sender,args) => {
                closed = true;
                window.SourceInitialized -= initialized;
                window.StateChanged -= stateChanged;
                if (queued != null) { queued.Abort(); queued = null; }
                if (source != null && hook != null) {
                    try { source.RemoveHook(hook); }
                    catch (InvalidOperationException) { /* HWND already disposed. */ }
                }
            };
        }

        private void SaveDashboardGeometry(Window window)
        {
            if (demo || window == null) return;
            // Preserve the normal restore size when closing a maximized/minimized
            // dashboard, rather than saving the monitor's dimensions.
            Rect bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left,window.Top,window.ActualWidth > 0 ? window.ActualWidth : window.Width,
                    window.ActualHeight > 0 ? window.ActualHeight : window.Height)
                : window.RestoreBounds;
            if (bounds.IsEmpty) return;
            Rect area = GetDashboardWorkArea(window,bounds.Left,bounds.Top);
            bounds = ClampDashboardBounds(bounds.Left,bounds.Top,bounds.Width,bounds.Height,area);
            settings.DashboardLeft = bounds.Left; settings.DashboardTop = bounds.Top;
            settings.DashboardWidth = bounds.Width; settings.DashboardHeight = bounds.Height;
            TrySaveSettings();
        }

        internal static void CopyDashboardGeometry(WidgetSettings source, WidgetSettings target)
        {
            target.DashboardLeft = source.DashboardLeft; target.DashboardTop = source.DashboardTop;
            target.DashboardWidth = source.DashboardWidth; target.DashboardHeight = source.DashboardHeight;
            target.DashboardSidebarCollapsed = source.DashboardSidebarCollapsed;
        }

        internal static Rect ClampDashboardBounds(double left, double top, double width, double height, Rect area)
        {
            if (area.IsEmpty || !IsGeometryNumber(area.Width) || !IsGeometryNumber(area.Height) || area.Width <= 0 || area.Height <= 0)
                area = new Rect(0,0,1024,768);
            width = IsGeometryNumber(width) ? width : 1100;
            height = IsGeometryNumber(height) ? height : 760;
            width = Math.Max(Math.Min(620,area.Width),Math.Min(width,area.Width));
            height = Math.Max(Math.Min(440,area.Height),Math.Min(height,area.Height));
            left = IsGeometryNumber(left) ? left : area.Left + (area.Width - width) / 2;
            top = IsGeometryNumber(top) ? top : area.Top + (area.Height - height) / 2;
            return new Rect(Math.Max(area.Left,Math.Min(left,area.Right-width)),
                Math.Max(area.Top,Math.Min(top,area.Bottom-height)),width,height);
        }

        private static void ApplyDashboardBounds(Window window, Rect bounds, Rect area)
        {
            // Release obsolete constraints when moving to a smaller monitor.
            window.MinWidth = 0; window.MinHeight = 0;
            window.MaxWidth = Double.PositiveInfinity; window.MaxHeight = Double.PositiveInfinity;
            window.MinWidth = Math.Min(620,area.Width); window.MinHeight = Math.Min(440,area.Height);
            window.Width = bounds.Width; window.Height = bounds.Height;
            window.Left = bounds.Left; window.Top = bounds.Top;
        }

        private Rect GetDashboardWorkArea(Window window, double preferredLeft, double preferredTop)
        {
            var source = PresentationSource.FromVisual(window) ?? PresentationSource.FromVisual(this);
            Matrix fromDevice, toDevice;
            if (source != null && source.CompositionTarget != null) {
                fromDevice = source.CompositionTarget.TransformFromDevice;
                toDevice = source.CompositionTarget.TransformToDevice;
            } else {
                var primary = Forms.Screen.PrimaryScreen.WorkingArea;
                var logical = SystemParameters.WorkArea;
                double scale = logical.Width > 0 ? primary.Width / logical.Width : 1;
                if (!IsGeometryNumber(scale) || scale <= 0) scale = 1;
                toDevice = new Matrix(scale,0,0,scale,0,0);
                fromDevice = new Matrix(1/scale,0,0,1/scale,0,0);
            }
            Forms.Screen screen;
            if (IsGeometryNumber(preferredLeft) && IsGeometryNumber(preferredTop)) {
                Point point = toDevice.Transform(new Point(preferredLeft,preferredTop));
                screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X,(int)point.Y));
            } else {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) handle = new WindowInteropHelper(this).Handle;
                screen = handle == IntPtr.Zero ? Forms.Screen.PrimaryScreen : Forms.Screen.FromHandle(handle);
            }
            var deviceArea = screen.WorkingArea;
            return new Rect(fromDevice.Transform(new Point(deviceArea.Left,deviceArea.Top)),
                fromDevice.Transform(new Point(deviceArea.Right,deviceArea.Bottom)));
        }
    }
}
