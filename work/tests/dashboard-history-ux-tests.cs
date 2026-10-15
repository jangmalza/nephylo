using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageWidget;

internal static class DashboardHistoryUxTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static int count;
    private static void Check(bool value,string message) { ++count; if(!value) throw new Exception(message); }
    private static T Field<T>(object value,string name) { return (T)value.GetType().GetField(name,Private).GetValue(value); }
    private static void Set(object value,string name,object data) { value.GetType().GetField(name,Private).SetValue(value,data); }
    private static object Call(object value,string name,params object[] args)
    { try { return value.GetType().GetMethod(name,Private).Invoke(value,args); } catch(TargetInvocationException error) { throw error.InnerException??error; } }
    private static object Static(string name,params object[] args)
    { return typeof(WidgetWindow).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args); }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach(object child in LogicalTreeHelper.GetChildren(root)) if(child is DependencyObject)
            foreach(var descendant in Walk((DependencyObject)child)) yield return descendant;
    }
    private static void Drain()
    {
        var frame=new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));
        Dispatcher.PushFrame(frame);
    }
    [STAThread]
    public static int Main(string[] arguments)
    {
        string isolated=Path.Combine(Path.GetTempPath(),"nephylo-history-ux-"+Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride=isolated; SettingsStore.StartupRegistrySubKeyOverride=@"Software\UsageWidget\Tests\history-ux-"+Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride=Path.Combine(isolated,"claude"); WidgetWindow owner=null;
        try {
            L.Language="ko"; owner=new WidgetWindow(true);
            Field<DispatcherTimer>(owner,"timer").Stop(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Visible=false;
            Call(owner,"ShowDemo"); new WindowInteropHelper(owner).EnsureHandle(); Call(owner,"BuildDashboardWindow");
            CheckMeaning(); CheckHistory(owner); CheckAnalysis(owner);
            if(arguments.Length>0) Render(owner,Path.GetFullPath(arguments[0]));
            Check(!owner.IsLoaded && !owner.IsVisible && !Directory.Exists(isolated),"UX fixture never opens live providers or writes account settings");
            Console.WriteLine("Dashboard history UX tests passed: "+count+" assertions. Hidden WPF presentation fixtures."); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if(owner!=null) { Set(owner,"demo",true); Call(owner,"CloseDashboard"); Field<DispatcherTimer>(owner,"timer").Stop(); owner.Close();
                Field<CodexService>(owner,"codex").Dispose(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Dispose(); }
            SettingsStore.DataDirectoryOverride=null; SettingsStore.StartupRegistrySubKeyOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null; L.Language="ko";
        }
    }
    private static void CheckMeaning()
    {
        var noRecords=new DashboardObservedPeriod();
        var zero=new DashboardObservedPeriod { Consumption=0,IntervalCount=1,PointCount=2,CoveredHours=1 };
        Check((string)Static("ObservedConsumption",noRecords,"%p")=="—","No calculation is displayed as a dash, never numerical zero");
        Check((string)Static("ObservedConsumption",zero,"%p")=="0 %p","An actually observed flat interval retains a numerical zero");
        Check(((string)Static("ObservedCoverage",noRecords,24.0)).Contains("기록 없음"),"Empty record quality does not misleadingly show measured zero hours");
        noRecords.PointCount=1;
        Check(((string)Static("ObservedCoverage",noRecords,24.0)).Contains("계산 대기"),"A single reading explicitly waits for a usable interval");
        string range=WidgetWindow.DashboardObservedDateRange(new DateTimeOffset(2026,12,31,12,0,0,TimeSpan.FromHours(9)),new DateTimeOffset(2027,1,1,12,0,0,TimeSpan.FromHours(9)));
        Check(range.Contains("2026/") && range.Contains("2027/"),"Period labels include years when a comparison crosses a year boundary");
    }
    private static void CheckHistory(WidgetWindow owner)
    {
        Call(owner,"SetDashboardHistoryService","codex"); Call(owner,"NavigateDashboard","history");
        var page=Field<StackPanel>(owner,"dashboardHistoryPageRoot"); var content=Field<StackPanel>(owner,"dashboardHistoryContent");
        var actions=page.Children.OfType<WrapPanel>().Single();
        Check(page.Children.IndexOf(actions)>page.Children.IndexOf(content),"Secondary export/settings actions follow the main chart and records");
        var item=Field<object>(owner,"dashboardHistoryInsights"); var disclosure=Field<Expander>(item,"Disclosure");
        Check(!disclosure.IsExpanded,"Comparison methodology starts collapsed");
        Check(AutomationProperties.GetAutomationId(disclosure)=="dashboard-history-methodology","Comparison and record disclosures have distinct stable keyboard targets");
        string current=Field<TextBlock>(item,"CurrentRange").Text, previous=Field<TextBlock>(item,"PreviousRange").Text;
        Check(current.Contains(" – ") && previous.EndsWith(current.Split(new[]{" – "},StringSplitOptions.None)[0],StringComparison.Ordinal),"Current and immediately preceding equal periods show their actual joined boundary");
        Check(Field<TextBlock>(owner,"dashboardHistoryChartRange").Text.Contains("현지 시간"),"The balance chart labels the selected interval and time basis");
        Check(Field<TextBlock>(item,"Reason").Text.IndexOf("25%",StringComparison.Ordinal)<0 && Field<TextBlock>(item,"Methodology").Text.Contains("25%"),"Threshold details are available in disclosure rather than filling the primary status");
        disclosure.IsExpanded=true; var chart=Field<UsageHistoryChart>(owner,"dashboardHistoryChart");
        Call(owner,"RefreshDashboardHistoryIfOpen");
        Check(Object.ReferenceEquals(disclosure,Field<Expander>(Field<object>(owner,"dashboardHistoryInsights"),"Disclosure")) && disclosure.IsExpanded,"Polling keeps methodology expansion and keyboard focus targets stable");
        Check(Object.ReferenceEquals(chart,Field<UsageHistoryChart>(owner,"dashboardHistoryChart")),"Presentation updates never replace the interactive graph");
        Call(owner,"BuildDashboardHistoryPage",false);
        Check(Field<Expander>(Field<object>(owner,"dashboardHistoryInsights"),"Disclosure").IsExpanded,"Rebuilding presentation remembers the methodology disclosure choice");
        var filters=(Grid)((FrameworkElement)Field<ComboBox>(owner,"dashboardHistoryService").Parent).Parent;
        filters.Width=420; filters.Measure(new Size(420,Double.PositiveInfinity)); filters.Arrange(new Rect(0,0,420,filters.DesiredSize.Height)); Drain();
        Static("ArrangeDashboardHistoryFilters",filters,false);
        var rangePanel=(FrameworkElement)Field<ComboBox>(owner,"dashboardHistoryRange").Parent;
        Check(Grid.GetRow(rangePanel)==1 && Grid.GetColumnSpan(rangePanel)==3,"Very narrow history filters place period on a separate row instead of squeezing labels");
    }
    private static void CheckAnalysis(WidgetWindow owner)
    {
        Call(owner,"SetDashboardHistoryService","codex"); Call(owner,"NavigateDashboard","analysis");
        var cards=Field<System.Collections.IDictionary>(owner,"dashboardAnalysisCards"); var item=cards.Values.Cast<object>().First();
        var description=Field<TextBlock>(item,"Description");
        Check(description.Text.Split('\n').Length<=2,"Analysis shows at most two explanation lines before methodology");
        var support=Field<TextBlock>(item,"Support").Text.Split('\n');
        Check(support.Distinct().Count()==support.Length,"Analysis methodology omits repeated warnings and explanations");
        var metrics=(Grid)((FrameworkElement)Field<TextBlock>(item,"Average").Parent).Parent;
        metrics.Width=400; metrics.Measure(new Size(400,Double.PositiveInfinity)); metrics.Arrange(new Rect(0,0,400,metrics.DesiredSize.Height)); Drain();
        Static("ArrangeDashboardAnalysisMetrics",metrics);
        Check(Grid.GetRow((FrameworkElement)Field<TextBlock>(item,"Reset").Parent)==1,"Narrow analysis keeps the reset date on its own readable row");
        var view=Field<ProviderView>(owner,"codexView"); string error=view.Error;
        try {
            view.Error="Fixture failure"; Call(owner,"RefreshDashboardHistoryIfOpen");
            Check(Field<TextBlock>(item,"Recommended").Text=="—" && Field<TextBlock>(item,"RecommendedUnit").Text.Contains("확인 필요"),"Unavailable recommendations explain a missing current balance rather than implying zero allowance");
        } finally { view.Error=error; }
    }
    private static void Render(WidgetWindow owner,string destination)
    {
        Directory.CreateDirectory(destination);
        Call(owner,"ResetDashboardHistoryState");
        foreach(string language in new[]{"ko","en"}) foreach(string theme in new[]{"dark","light"}) foreach(string page in new[]{"history","analysis"}) foreach(int width in new[]{620,1100}) {
            Call(owner,"ApplyLanguage",language); Call(owner,"ApplyRequestedTheme",theme); Call(owner,"SetDashboardHistoryService","codex"); Call(owner,"NavigateDashboard",page);
            var dashboard=Field<Window>(owner,"dashboardWindow"); dashboard.Width=width; dashboard.Height=760;
            Call(owner,"UpdateDashboardSidebar"); Call(owner,"RenderDashboard",true); Drain();
            var root=Field<Grid>(owner,"dashboardRoot"); root.Width=width; root.Height=760; root.Measure(new Size(width,760)); root.Arrange(new Rect(0,0,width,760)); root.UpdateLayout(); Drain();
            var scroll=Field<ScrollViewer>(owner,"dashboardScroll");
            foreach(bool end in page=="history" ? new[]{false,true} : new[]{false}) {
                if(end) scroll.ScrollToEnd(); else scroll.ScrollToTop(); root.UpdateLayout(); Drain();
                var bitmap=new RenderTargetBitmap(width,760,96,96,PixelFormats.Pbgra32); var background=new DrawingVisual();
                using(var drawing=background.RenderOpen()) drawing.DrawRectangle(dashboard.Background,null,new Rect(0,0,width,760));
                bitmap.Render(background); bitmap.Render(root); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string filename=page+"-"+language+"-"+theme+"-"+width+(end ? "-details" : "")+".png";
                using(var stream=File.Create(Path.Combine(destination,filename))) encoder.Save(stream);
            }
        }
    }
}
