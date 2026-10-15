using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using UsageWidget;

internal static class HistoryChartTests
{
    private static int count;
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026,10,12,0,0,0,TimeSpan.Zero);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition,string label) { count++; if (!condition) throw new Exception(label); }
    private static T Field<T>(object target,string name) { return (T)target.GetType().GetField(name,Private).GetValue(target); }
    private static UsageHistoryPoint Point(int minute,double? value,string source = "code")
    {
        return new UsageHistoryPoint { ServiceId = "codex", Label = "weekly", Source = source, ObservedAt = Start.AddMinutes(minute),
            RemainingPercent = value, ResetsAt = Start.AddDays(3), ResolutionMinutes = 1 };
    }
    private static UsageHistoryChart Chart(params UsageHistoryPoint[] points)
    {
        var chart = new UsageHistoryChart(); chart.SetPoints(points.ToList(),Start,Start.AddHours(1),15,null); return chart;
    }
    private static int[] SegmentSizes(UsageHistoryChart chart) { return Field<List<List<int>>>(chart,"segments").Select(p => p.Count).ToArray(); }
    private static void Segments(UsageHistoryChart chart,params int[] expected) { Check(SegmentSizes(chart).SequenceEqual(expected),"continuous segments: " + String.Join(",",expected)); }
    private static void TestContinuity()
    {
        Segments(Chart(Point(0,90),Point(5,85),Point(10,80)),3);
        Segments(Chart(Point(0,90),Point(5,85),Point(40,80),Point(45,75)),2,2);
        Segments(Chart(Point(0,90),Point(5,Double.NaN),Point(10,80)),1,1);
        Segments(Chart(Point(0,90),Point(5,null),Point(10,80)),1,1);
        Segments(Chart(Point(0,90),Point(5,89,"api"),Point(10,88,"api")),1,2);
        Segments(Chart(Point(0,90,"manual"),Point(5,89,"manual")),1,1);
        Segments(Chart(Point(0,50),Point(5,100)),1,1);
        var reset = Point(5,80); reset.ResetsAt = Start.AddDays(4);
        Segments(Chart(Point(0,90),reset),1,1);
        var expired = Point(0,90); expired.ResetsAt = Start.AddMinutes(3);
        var expiredNext = Point(5,80); expiredNext.ResetsAt = expired.ResetsAt;
        Segments(Chart(expired,expiredNext),1,1);
        var limit = Point(5,80); limit.Limit = 200;
        Segments(Chart(Point(0,90),limit),1,1);
        Segments(Chart(Point(5,90),Point(5,80)),1,1);
        Segments(Chart(Point(-1,95),Point(0,90),Point(5,80),Point(70,75)),2);
        var coarseA = Point(0,90); var coarseB = Point(45,80); coarseA.ResolutionMinutes = coarseB.ResolutionMinutes = 30;
        Segments(Chart(coarseA,coarseB),2);
        Check(!UsageHistoryChart.Connects(null,Point(5,80),15),"no connection without previous observation");
        Check(!UsageHistoryChart.Connects(Point(0,90),null,15),"no connection to missing observation");
    }
    private static void TestNumbersAndUnits()
    {
        foreach (double value in new[] { Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity,-1.0,101.0 })
            Check(!UsageHistoryChart.MetricValue(Point(0,value)).HasValue,"reject invalid percentage " + value);
        Check(UsageHistoryChart.MetricValue(Point(0,0)) == 0,"zero is an actual observation");
        Check(UsageHistoryChart.MetricValue(Point(0,100)) == 100,"100 percent valid");
        var money = Point(0,null); money.ServiceId = "openai"; money.Remaining = 0.045;
        var credits = Point(5,null); credits.ServiceId = "higgsfield"; credits.Remaining = 12345;
        Check(UsageHistoryChart.MetricUnit(money) == "USD","USD unit");
        Check(UsageHistoryChart.MetricUnit(credits) == "크레딧","credit unit");
        Check(Field<double>(Chart(money),"maximum") >= 0.045 && Field<double>(Chart(money),"maximum") < 1,"small money axis is useful");
        Check(Field<double>(Chart(Point(0,20)),"maximum") == 100,"percent axis never amplifies small changes");
        Check(Field<double>(Chart(credits),"maximum") >= 12345,"credits axis includes actual amount");
        Check(Field<string>(Chart(money,credits),"message").Contains("단위"),"mixed units cannot be plotted together");
        var sameUnitOther = Point(5,85); sameUnitOther.ServiceId = "eleven";
        Check(Field<string>(Chart(Point(0,90),sameUnitOther),"message").Contains("항목"),"mixed services cannot become one line");
        Check(!Chart().MoveSelection(1),"empty series does not invent selectable point");
        foreach (double value in new[] { 0.0,-1.0,Double.NaN,Double.PositiveInfinity }) Check(UsageHistoryChart.NiceMaximum(value) == 1,"empty/invalid scale fallback");
        Check(!Double.IsInfinity(UsageHistoryChart.NiceMaximum(Double.MaxValue)),"large scale stays finite");
    }
    private static void TestActualSelection()
    {
        var original = Point(20,70);
        var chart = Chart(Point(55,30),original,Point(5,95));
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(chart);
        Check(peer != null && !String.IsNullOrWhiteSpace(peer.GetName()),"chart exposes an accessible focus target");
        Check(chart.FindClosestPointIndex(0) == 0,"nearest first real point");
        Check(chart.FindClosestPointIndex(1) == 2,"nearest last real point");
        Check(chart.FindClosestPointIndex(0.5) == 1,"hover selects actual point, not interpolated value");
        Check(chart.FindClosestPointIndex(Double.NaN) == -1,"invalid pointer position ignored");
        Check(chart.MoveSelection(0) && Field<int>(chart,"selectedIndex") == 2,"keyboard starts at latest point");
        Check(chart.MoveSelection(-1) && Field<int>(chart,"selectedIndex") == 1,"left selects previous observation");
        Check(AutomationProperties.GetItemStatus(chart).Contains("70%"),"accessible selected value");
        Check(chart.MoveSelection(-100) && Field<int>(chart,"selectedIndex") == 0,"home clamps at first observation");
        Check(chart.MoveSelection(100) && Field<int>(chart,"selectedIndex") == 2,"end clamps at latest observation");
        original.RemainingPercent = 2;
        Check(Field<List<UsageHistoryPoint>>(chart,"points")[1].RemainingPercent == 70,"journal mutations cannot alter graph snapshot");
        chart.MoveSelection(-1);
        var refreshed = new List<UsageHistoryPoint> { Point(5,95),Point(20,65),Point(55,30),Point(59,29) };
        chart.SetPoints(refreshed,Start,Start.AddHours(1).AddSeconds(30),15,null);
        Check(Field<int>(chart,"selectedIndex") == 1 && AutomationProperties.GetItemStatus(chart).Contains("65%"),"refresh preserves selection and updates its real value");
        chart.SetPoints(new List<UsageHistoryPoint> { Point(55,30) },Start,Start.AddHours(1),15,null);
        Check(Field<int>(chart,"selectedIndex") == -1,"removed point cannot leave stale selection");
        chart.SetPoints(null,Start,Start,15,null);
        Check(chart.FindClosestPointIndex(0.5) == -1,"null observations and zero range handled");
    }
    private static void TestMotionAndRendering()
    {
        var chart = Chart(Point(0,92),Point(5,80),Point(10,78),Point(15,70),Point(40,55),Point(50,30));
        Check(!chart.AnimationsEnabled,"chart motion opt-in to app preference");
        string identity = Field<string>(chart,"seriesIdentity");
        chart.SetPoints(new List<UsageHistoryPoint> { Point(0,92),Point(5,80),Point(55,25) },Start.AddSeconds(10),Start.AddHours(1).AddSeconds(10),15,null);
        Check(Field<string>(chart,"seriesIdentity") == identity,"poll clock drift does not replay entry motion");
        var revealProperty = (DependencyProperty)typeof(UsageHistoryChart).GetField("RevealProperty",BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        chart.AnimationsEnabled = true; chart.BeginAnimation(revealProperty,new DoubleAnimation(0.1,1,TimeSpan.FromSeconds(10)));
        chart.AnimationsEnabled = false;
        Check((double)chart.GetValue(revealProperty) == 1,"disabling motion stops active fade instantly");
        foreach (string theme in new[] { "dark","light" }) {
            WidgetPalette.SetTheme(theme);
            foreach (int width in new[] { 180,480,900 }) {
                chart.Measure(new Size(width,240)); chart.Arrange(new Rect(0,0,width,240)); chart.UpdateLayout(); chart.MoveSelection(0);
                var bitmap = new RenderTargetBitmap(width,240,96,96,PixelFormats.Pbgra32); bitmap.Render(chart);
                var pixels = new byte[width * 240 * 4]; bitmap.CopyPixels(pixels,width * 4,0);
                Check(pixels.Where((value,index) => index % 4 == 3 && value > 0).Count() > 100,"render paths, axes, hover for " + theme + "/" + width);
            }
        }
    }
    private static void TestClickRouting()
    {
        var chart = Chart(Point(0,90),Point(5,80),Point(10,70));
        var scroll = new System.Windows.Controls.ScrollViewer { Content = chart };
        scroll.Measure(new Size(500,260)); scroll.Arrange(new Rect(0,0,500,260)); scroll.UpdateLayout();
        int unhandledParentClicks = 0;
        scroll.AddHandler(System.Windows.Input.Mouse.MouseDownEvent,new System.Windows.Input.MouseButtonEventHandler((sender,e) => unhandledParentClicks++));
        var click = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left) {
            RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent
        };
        chart.RaiseEvent(click);
        Check(click.Handled,"chart consumes mouse click after requesting keyboard focus");
        Check(unhandledParentClicks == 0,"chart click cannot bubble unhandled to parent ScrollViewer");
    }
    [STAThread]
    private static int Main()
    {
        try { TestContinuity(); TestNumbersAndUnits(); TestActualSelection(); TestMotionAndRendering(); TestClickRouting(); Console.WriteLine("History chart tests passed: " + count); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
