using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageWidget;

// Renders product-owned WPF components in memory. This does not automate or
// capture the user's desktop and never initializes live providers or accounts.
internal static class DashboardRender
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target,string name) { return (T)target.GetType().GetField(name,Private).GetValue(target); }
    private static void Set(object target,string name,object value) { target.GetType().GetField(name,Private).SetValue(target,value); }
    private static object Call(object target,string name,params object[] arguments)
    { try { return target.GetType().GetMethod(name,Private).Invoke(target,arguments); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [STAThread]
    public static int Main(string[] arguments)
    {
        if (arguments.Length != 1) return 2;
        string destination = Path.GetFullPath(arguments[0]); Directory.CreateDirectory(destination);
        string isolated = Path.Combine(Path.GetTempPath(),"nephylo-dashboard-render-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\dashboard-render-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated,"claude");
        WidgetWindow owner = null;
        try {
            owner = new WidgetWindow(true);
            Field<DispatcherTimer>(owner,"timer").Stop(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Visible = false;
            Call(owner,"ShowDemo"); new WindowInteropHelper(owner).EnsureHandle();
            var dashboard = (Window)Call(owner,"BuildDashboardWindow");
            int count = 0;
            foreach (string language in new[] { "ko","en" }) foreach (string theme in new[] { "dark","light" }) {
                Call(owner,"ApplyLanguage",language); Call(owner,"ApplyRequestedTheme",theme);
                foreach (string page in new[] { "overview","history","analysis","timeline","detail" }) {
                    Set(owner,"dashboardSelectedService","codex");
                    Call(owner,"SetDashboardHistoryService","codex"); Call(owner,"NavigateDashboard",page);
                    foreach (bool narrow in new[] { false,true }) {
                        int width = narrow ? 620 : 1100, height = narrow ? 560 : 760;
                        foreach (double scale in new[] { 1.0,1.5 }) {
                            string name = page + "-" + language + "-" + theme + "-" + width + "x" + height + "-" + (int)(scale * 100) + ".png";
                            Render(owner,dashboard,width,height,scale,Path.Combine(destination,name)); count++;
                            if (page == "history") {
                                Render(owner,dashboard,width,height,scale,Path.Combine(destination,"insights-" + name),true); count++;
                            }
                        }
                    }
                }
                var views = (List<ProviderView>)Call(owner,"DashboardViews");
                var saved = views.ToDictionary(view => view.Id,view => view.Snapshot);
                try {
                    foreach (string state in new[] { "empty","error","loading","filtered" }) {
                        foreach (var view in views) {
                            view.Snapshot = state == "empty" || state != "filtered" && view.Id != "codex" ? null : saved[view.Id];
                            view.Error = state == "error" && view.Id == "codex" ? "연결을 확인해 주세요." : null;
                            view.Busy = state == "loading" && view.Id == "codex";
                        }
                        Set(owner,"dashboardFilter",state == "filtered" ? "low" : "all");
                        Set(owner,"dashboardFavoritesOnly",state == "filtered"); Call(owner,"NavigateDashboard","overview");
                        foreach (int width in new[] { 620,1100 }) {
                            Render(owner,dashboard,width,560,1,Path.Combine(destination,state + "-" + language + "-" + theme + "-" + width + ".png")); count++;
                        }
                    }
                } finally {
                    foreach (var view in views) { view.Snapshot = saved[view.Id]; view.Error = null; view.Busy = false; }
                    Set(owner,"dashboardFilter","all"); Set(owner,"dashboardFavoritesOnly",false);
                }
            }
            if (owner.IsVisible || dashboard.IsVisible || Directory.Exists(isolated)) throw new Exception("The render fixture must not show a window or write user data.");
            Console.WriteLine("Rendered " + count + " isolated WPF dashboard component screenshots: " + destination);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if (owner != null) {
                Set(owner,"demo",true); Field<DispatcherTimer>(owner,"timer").Stop(); Call(owner,"CloseDashboard"); owner.Close();
                Field<CodexService>(owner,"codex").Dispose(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null; SettingsStore.StartupRegistrySubKeyOverride = null; ClaudeUsageService.ConfigDirectoryOverride = null; L.Language = "ko";
        }
    }

    private static void Render(WidgetWindow owner,Window dashboard,int width,int height,double scale,string path,bool scrollToEnd = false)
    {
        dashboard.Width = width; dashboard.Height = height;
        Field<WidgetSettings>(owner,"settings").DashboardSidebarCollapsed = false;
        Call(owner,"UpdateDashboardSidebar"); Call(owner,"RenderDashboard",true); Drain();
        var root = Field<Grid>(owner,"dashboardRoot"); root.Width = width; root.Height = height;
        root.Measure(new Size(width,height)); root.Arrange(new Rect(0,0,width,height)); root.UpdateLayout(); Drain();
        var scroll = Field<ScrollViewer>(owner,"dashboardScroll");
        if (scrollToEnd) scroll.ScrollToEnd(); else scroll.ScrollToTop();
        root.UpdateLayout(); Drain();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale),(int)Math.Ceiling(height * scale),96 * scale,96 * scale,PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(dashboard.Background,null,new Rect(0,0,width,height));
        bitmap.Render(background); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(path)) encoder.Save(output);
    }
}
