using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using UsageWidget;

internal static class DashboardGeometryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int count;
    private static void Check(bool value,string message) { count++; if (!value) throw new Exception(message); }
    private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    private static T Field<T>(object target,string name) { return (T)target.GetType().GetField(name,Private).GetValue(target); }
    private static void Set(object target,string name,object value) { target.GetType().GetField(name,Private).SetValue(target,value); }
    private static void Call(object target,string name,params object[] arguments)
    { try { target.GetType().GetMethod(name,Private).Invoke(target,arguments); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
    private static void Within(Rect result,Rect area,string label)
    {
        Check(Finite(result.Left) && Finite(result.Top) && Finite(result.Width) && Finite(result.Height),label + ": all coordinates are finite");
        Check(result.Width > 0 && result.Height > 0,label + ": usable dimensions");
        Check(result.Left >= area.Left && result.Top >= area.Top && result.Right <= area.Right + 0.001 && result.Bottom <= area.Bottom + 0.001,label + ": fully inside the selected work area");
    }

    [STAThread]
    public static int Main()
    {
        string isolated = Path.Combine(Path.GetTempPath(),"nephylo-dashboard-geometry-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\dashboard-geometry-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated,"claude");
        WidgetWindow owner = null;
        try {
            CheckBounds(); CheckCopy(); CheckPersistence();
            owner = new WidgetWindow(true);
            Field<DispatcherTimer>(owner,"timer").Stop(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Visible = false;
            CheckHiddenWindowRestore(owner);
            Console.WriteLine("Dashboard geometry tests passed: " + count + " assertions. Isolated temporary settings; no live account or visible window.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if (owner != null) {
                Set(owner,"demo",true); Field<DispatcherTimer>(owner,"timer").Stop(); owner.Close();
                Field<CodexService>(owner,"codex").Dispose(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null; SettingsStore.StartupRegistrySubKeyOverride = null; ClaudeUsageService.ConfigDirectoryOverride = null;
            string settings = Path.Combine(isolated,"settings.json");
            if (File.Exists(settings)) File.Delete(settings);
            if (Directory.Exists(isolated) && Directory.GetFileSystemEntries(isolated).Length == 0) Directory.Delete(isolated);
        }
    }

    private static void CheckBounds()
    {
        var areas = new[] { new Rect(0,0,1920,1040),new Rect(0,0,1280,680),new Rect(-1920,0,1920,1040),new Rect(0,-900,1600,860),new Rect(50,30,500,300) };
        foreach (var area in areas) {
            Within(WidgetWindow.ClampDashboardBounds(Double.NaN,Double.NaN,1100,760,area),area,"New dashboard " + area);
            Within(WidgetWindow.ClampDashboardBounds(60000,-60000,24000,18000,area),area,"Disconnected monitor " + area);
            Within(WidgetWindow.ClampDashboardBounds(Double.PositiveInfinity,Double.NegativeInfinity,Double.NaN,Double.PositiveInfinity,area),area,"Malformed settings " + area);
            Within(WidgetWindow.ClampDashboardBounds(area.Left,area.Top,-1,-1,area),area,"Invalid negative dimensions " + area);
        }
        var primary = areas[0];
        var centered = WidgetWindow.ClampDashboardBounds(Double.NaN,Double.NaN,1100,760,primary);
        Check(centered.Width == 1100 && centered.Height == 760 && centered.Left == 410 && centered.Top == 140,"First dashboard centers at its usable default size");
        var negative = WidgetWindow.ClampDashboardBounds(-1800,100,900,700,areas[2]);
        Check(negative.Left == -1800 && negative.Top == 100,"Valid negative coordinates preserve a left-side monitor's position");
        var small = WidgetWindow.ClampDashboardBounds(50,30,1100,760,areas[4]);
        Check(small.Width == 500 && small.Height == 300,"Minimum dashboard size never exceeds a smaller available screen");
        Within(WidgetWindow.ClampDashboardBounds(Double.NaN,Double.NaN,1100,760,Rect.Empty),new Rect(0,0,1024,768),"Missing display information");
    }

    private static void CheckCopy()
    {
        var source = new WidgetSettings { DashboardLeft = -1200,DashboardTop = 80,DashboardWidth = 1000,DashboardHeight = 670,DashboardSidebarCollapsed = true };
        var target = new WidgetSettings { Left = 40,Top = 50,Width = 421,Height = 230,CompactLeft = 10,CompactTop = 20,CompactWidth = 640,Theme = "light",Language = "en" };
        WidgetWindow.CopyDashboardGeometry(source,target);
        Check(target.DashboardLeft == -1200 && target.DashboardTop == 80 && target.DashboardWidth == 1000 && target.DashboardHeight == 670 && target.DashboardSidebarCollapsed,"A settings dialog copies the current dashboard size and sidebar preference");
        Check(target.Left == 40 && target.Top == 50 && target.Width == 421 && target.Height == 230 && target.CompactLeft == 10 && target.CompactTop == 20 && target.CompactWidth == 640,"Copying dashboard settings never overwrites the widget or minibar bounds");
        Check(target.Theme == "light" && target.Language == "en","Copying dashboard geometry does not undo other pending user preferences");
    }

    private static void CheckPersistence()
    {
        var value = new WidgetSettings { Left = 42,Top = 70,Width = 390,Height = 270,CompactLeft = -1300,CompactTop = 35,CompactWidth = 580,
            DashboardLeft = -1700,DashboardTop = 90,DashboardWidth = 1000,DashboardHeight = 650,DashboardSidebarCollapsed = true };
        SettingsStore.Save(value);
        var restored = SettingsStore.Load();
        Check(restored.DashboardLeft == -1700 && restored.DashboardTop == 90 && restored.DashboardWidth == 1000 && restored.DashboardHeight == 650 && restored.DashboardSidebarCollapsed,"Dashboard coordinates and sidebar choice persist across a reload");
        Check(restored.Left == 42 && restored.Top == 70 && restored.Width == 390 && restored.Height == 270 && restored.CompactLeft == -1300 && restored.CompactTop == 35 && restored.CompactWidth == 580,"Saving the dashboard preserves both independent widget modes");
        value.DashboardLeft = Double.PositiveInfinity; value.DashboardTop = Double.NaN;
        value.DashboardWidth = -20; value.DashboardHeight = Double.NaN;
        SettingsStore.Save(value); restored = SettingsStore.Load();
        Check(Double.IsNaN(restored.DashboardLeft) && Double.IsNaN(restored.DashboardTop) && restored.DashboardWidth == 1100 && restored.DashboardHeight == 760,"Corrupt dashboard geometry normalizes to an unpositioned usable default");
        File.WriteAllText(Path.Combine(SettingsStore.DataDirectory,"settings.json"),"{\"Width\":390,\"Height\":270,\"LayoutVersion\":2}");
        restored = SettingsStore.Load();
        Check(restored.Width == 390 && restored.Height == 270 && restored.DashboardWidth == 1100 && restored.DashboardHeight == 760 && !restored.DashboardSidebarCollapsed,"Upgrading old settings adds dashboard defaults without resetting the user's existing widget");
    }

    private static void CheckHiddenWindowRestore(WidgetWindow owner)
    {
        var settings = Field<WidgetSettings>(owner,"settings");
        settings.Left = 55; settings.Top = 65; settings.Width = 420; settings.Height = 240;
        settings.CompactLeft = 75; settings.CompactTop = 85; settings.CompactWidth = 520;
        settings.DashboardLeft = Double.NaN; settings.DashboardTop = Double.NaN;
        settings.DashboardWidth = 1100; settings.DashboardHeight = 760;
        var dashboard = new Window();
        try {
            Call(owner,"RestoreDashboardGeometry",dashboard);
            Check(!dashboard.IsLoaded && !dashboard.IsVisible && !owner.IsVisible,"Restoring dashboard geometry does not show either window");
            Check(Finite(dashboard.Left) && Finite(dashboard.Top) && dashboard.Width > 0 && dashboard.Height > 0,"A hidden dashboard receives a finite visible-screen position before HWND creation");
            Check(settings.Left == 55 && settings.Top == 65 && settings.Width == 420 && settings.Height == 240 && settings.CompactLeft == 75 && settings.CompactTop == 85 && settings.CompactWidth == 520,"Dashboard restore does not alter either saved widget position");
            Call(owner,"SaveDashboardGeometry",dashboard);
            Check(Double.IsNaN(settings.DashboardLeft) && Double.IsNaN(settings.DashboardTop),"Preview geometry remains in memory and never saves into real user settings");
            Set(owner,"demo",false);
            Call(owner,"SaveDashboardGeometry",dashboard);
            Check(Finite(settings.DashboardLeft) && Finite(settings.DashboardTop),"A normal dashboard can persist its own restore bounds");
            var persisted = SettingsStore.Load();
            Check(persisted.DashboardWidth == dashboard.Width && persisted.DashboardHeight == dashboard.Height && persisted.Left == 55 && persisted.CompactWidth == 520,"Saving a dashboard persists its current size while retaining widget and minibar geometry");
        }
        finally { Set(owner,"demo",true); dashboard.Close(); }
        Check(!Field<bool>(owner,"exiting"),"Closing the independent geometry fixture does not exit its widget owner");
    }
}
