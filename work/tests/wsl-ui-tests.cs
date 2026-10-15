using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static class WslUiTests
{
    private static int assertions;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Check(bool value,string message) { assertions++; if (!value) throw new Exception(message); }
    private static T Field<T>(WidgetWindow window,string name) { return (T)typeof(WidgetWindow).GetField(name,PrivateInstance).GetValue(window); }
    private static object Call(WidgetWindow window,string name,params object[] args)
    {
        try { return typeof(WidgetWindow).GetMethod(name,PrivateInstance).Invoke(window,args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        if (root is T) yield return (T)root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants<T>(child)) yield return item;
    }
    private static Button Button(DependencyObject root,string label) { return Descendants<Button>(root).Single(item => Convert.ToString(item.Content) == label); }
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); }
    private static void PumpUntil(Func<bool> complete)
    {
        if (complete()) return;
        var frame = new DispatcherFrame();
        var time = Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10),DispatcherPriority.Background,(s,e) => {
            if (complete() || time.ElapsedMilliseconds > 7000) frame.Continue = false;
        },Dispatcher.CurrentDispatcher);
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!complete()) throw new Exception("Hidden WSL UI operation timed out.");
    }
    private static T Complete<T>(Task<T> task) { PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static ProcessStartInfo ServerInfo(string source)
    {
        return new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--fake-app-server " + WslRuntime.QuoteArgument(source)) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)
        };
    }
    private static void Server(string source)
    {
        string line;
        while ((line = Console.ReadLine()) != null) {
            var request = Json.Deserialize<Dictionary<string,object>>(line);
            if (!request.ContainsKey("id")) continue;
            object id=request["id"]; string method=(string)request["method"];
            object result = new {};
            if (method == "account/read") result = new { account = new { type="chatgpt",planType="test" } };
            if (method == "account/rateLimits/read") result = new { rateLimits = new { primary = new { usedPercent=source=="Ubuntu" ? 12 : 24,windowDurationMins=300 } } };
            Console.WriteLine(Json.Serialize(new { id=id,result=result })); Console.Out.Flush();
        }
    }
    private static void Run(string isolated)
    {
        bool rejectProbe=false;
        int probes=0,serverStarts=0;
        WslRuntime.RunnerOverride = (info,input,timeout,ct) => {
            ct.ThrowIfCancellationRequested();
            if (info.Arguments == "--list --quiet") return Task.FromResult(new WslCommandResult { Output="Ubuntu\r\nDebian\r\n" });
            probes++;
            return Task.FromResult(new WslCommandResult { ExitCode=rejectProbe ? 1 : 0,Output="NEPHYLO_WSL_V1\0/home/test\0/usr/bin/codex\0/usr/bin/claude\0" });
        };
        WslRuntime.CodexStartInfoOverride = source => { serverStarts++; return ServerInfo(source); };
        var initial = new WidgetSettings { CodexPath=Assembly.GetExecutingAssembly().Location,CodexWslDistribution="",ClaudeWslDistribution="",Language="ko" };
        SettingsStore.Save(initial);
        string original=File.ReadAllText(Path.Combine(isolated,"settings.json"));
        WidgetWindow window=null;
        try {
            window=new WidgetWindow(false);
            Field<DispatcherTimer>(window,"timer").Stop();
            Field<Forms.NotifyIcon>(window,"tray").Visible=false;
            Check(!window.IsLoaded && !window.IsVisible,"Integration fixture is hidden and does not perform automatic live refresh");
            var settings=Field<WidgetSettings>(window,"settings");
            Field<ServicePlan>(window,"servicePlan").Select("eleven");
            var view=Field<ProviderView>(window,"codexView");
            var previous=new UsageSnapshot { Plan="Windows sentinel",Rows=new List<UsageRow> { new UsageRow { Label="remaining",RemainingPercent=51 } } };
            view.Snapshot=previous; view.LastSuccess=DateTimeOffset.Now; view.Error=null; view.Issue=null;
            var editor=new StackPanel(); Call(window,"BuildCodexSettings",editor);
            var choices=Descendants<ComboBox>(editor).Single();
            ((FrameworkElement)editor.Children[0]).RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpUntil(() => Button(editor,"다시 검색").IsEnabled);
            Check(choices.Items.Count==3 && probes==2,"Opening connection editor discovers installed distributions once");
            Check(serverStarts==0 && File.ReadAllText(Path.Combine(isolated,"settings.json"))==original,"Discovery neither authenticates nor changes saved connections");
            choices.SelectedItem=choices.Items.Cast<ComboBoxItem>().Single(item => Object.Equals(item.Tag,"Ubuntu"));
            Check(settings.CodexWslDistribution=="" && settings.ClaudeWslDistribution=="" && File.ReadAllText(Path.Combine(isolated,"settings.json"))==original,"Selecting a WSL candidate does not persist either source");
            Check(Object.ReferenceEquals(view.Snapshot,previous) && Field<ServicePlan>(window,"servicePlan").IsActive("eleven"),"Selecting candidate retains displayed Windows value and current Free service");
            rejectProbe=true;
            Click(Button(editor,"연결 확인"));
            PumpUntil(() => !(bool)Call(window,"IsServiceConnectionPending","codex"));
            Check(Button(editor,"연결 확인").IsEnabled && choices.IsEnabled,"Failed candidate releases its service reservation and controls");
            Check(settings.CodexWslDistribution=="" && File.ReadAllText(Path.Combine(isolated,"settings.json"))==original,"Failed WSL check preserves Windows settings on disk");
            Check(Object.ReferenceEquals(view.Snapshot,previous) && view.Error==null && view.Issue==null,"Failed candidate never marks the prior working Windows connection failed");
            Check(Field<ServicePlan>(window,"servicePlan").IsActive("eleven") && serverStarts==0,"Failed WSL check neither switches Free service nor starts app-server");

            // Cancel while the selected WSL source is being probed, before an auth URL exists.
            WslRuntime.RunnerOverride=async (info,input,timeout,ct) => { await Task.Delay(10000,ct); return new WslCommandResult(); };
            Click(Button(editor,"브라우저로 로그인"));
            Check(Field<bool>(window,"loginPending") && !choices.IsEnabled,"WSL login locks environment selection during its pending probe");
            Click(Button(editor,"로그인 취소"));
            PumpUntil(() => !Field<bool>(window,"loginPending") && !(bool)Call(window,"IsServiceConnectionPending","codex"));
            Check(choices.IsEnabled && settings.CodexWslDistribution=="" && serverStarts==0,"Cancel releases controls without persisting candidate or launching browser/server");
            Check(Field<ServicePlan>(window,"servicePlan").IsActive("eleven") && Object.ReferenceEquals(view.Snapshot,previous),"Canceled login keeps prior Free service and quota");

            WslRuntime.RunnerOverride=(info,input,timeout,ct) => Task.FromResult(new WslCommandResult { Output=info.Arguments=="--list --quiet" ? "Ubuntu\r\nDebian\r\n" : "NEPHYLO_WSL_V1\0/home/test\0/usr/bin/codex\0/usr/bin/claude\0" });
            Click(Button(editor,"연결 확인"));
            Check((bool)Call(window,"IsServiceConnectionPending","codex") && !Button(editor,"연결 확인").IsEnabled,
                "Successful asynchronous check is observed while its own service reservation is active");
            PumpUntil(() => !(bool)Call(window,"IsServiceConnectionPending","codex"));
            Check(Button(editor,"연결 확인").IsEnabled && choices.IsEnabled,"Successful candidate restores the connection controls");
            Check(settings.CodexWslDistribution=="Ubuntu" && SettingsStore.Load().CodexWslDistribution=="Ubuntu","Successful candidate verification commits its WSL source to settings");
            Check(Field<ServicePlan>(window,"servicePlan").IsActive("eleven") && view.Snapshot.Rows[0].RemainingPercent==88,"Verified WSL displays its returned quota without rewriting the legacy service preference");
            Check(serverStarts==1 && view.Error==null,"Successful UI check starts one selected app-server and clears obsolete failure feedback");
            Field<ServicePlan>(window,"servicePlan").Select("eleven");
            settings.CodexPath="a Windows path that must never be used for WSL.exe";
            var verified=Complete((Task<UsageSnapshot>)Call(window,"VerifyServiceForSelection","codex"));
            Check(verified.Rows[0].RemainingPercent==88 && serverStarts==1,"Free service verification uses the saved WSL distribution instead of Windows path");
            Check(Field<ServicePlan>(window,"servicePlan").IsActive("eleven"),"Read-only verification does not commit a Free service choice");

            // A valid Windows Claude record can never satisfy a WSL connection.
            string quota="{\"rate_limits\":{\"five_hour\":{\"used_percentage\":25,\"resets_at\":253402300000}}}";
            ClaudeUsageService.CaptureJson(quota,DateTimeOffset.UtcNow);
            Check(ClaudeUsageService.ReadLatest().Snapshot.Rows[0].RemainingPercent==75,"Windows Claude fixture contains usable quota");
            settings.ClaudeWslDistribution="Debian";
            bool rejected=false;
            try { ClaudeUsageService.ReadLatest("Debian"); } catch (InvalidOperationException) { rejected=true; }
            Check(rejected,"WSL Claude cannot reuse a valid Windows cache");
            Check(Complete((Task<UsageSnapshot>)Call(window,"VerifyServiceForSelection","claude"))==null,"WSL service selection cannot succeed from Windows Claude cache alone");
            var claudeEditor=new StackPanel(); Call(window,"BuildClaudeSettings",claudeEditor);
            Check(Descendants<ComboBox>(claudeEditor).Single().Items.Cast<ComboBoxItem>().Any(item => Object.Equals(item.Tag,"Debian")),"Saved WSL source remains selectable before discovery completes");
            settings.RefreshSeconds=300;
            Call(window,"TrySaveSettings");
            var restored=SettingsStore.Load();
            Check(restored.CodexWslDistribution=="Ubuntu" && restored.ClaudeWslDistribution=="Debian" && restored.RefreshSeconds==300,"General widget settings persistence retains both independent WSL selections");
            Check(!window.IsLoaded && !window.IsVisible,"All integration checks ran without showing a widget or browser");
        }
        finally {
            if (window!=null) {
                Field<DispatcherTimer>(window,"timer").Stop();
                Field<CodexService>(window,"codex").Dispose();
                window.Close();
                Field<Forms.NotifyIcon>(window,"tray").Dispose();
            }
        }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length>0 && args[0]=="--fake-app-server") { Server(args.Length>1 ? args[1] : ""); return 0; }
        string parent=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string isolated=Path.Combine(parent,"wsl-ui-test-data-"+Guid.NewGuid().ToString("N"));
        string priorCodexHome=Environment.GetEnvironmentVariable("CODEX_HOME");
        var priorContext=SynchronizationContext.Current;
        try {
            SettingsStore.DataDirectoryOverride=isolated;
            SettingsStore.StartupRegistrySubKeyOverride=@"Software\UsageWidget\Tests\wsl-ui-"+Guid.NewGuid().ToString("N");
            ClaudeUsageService.ConfigDirectoryOverride=Path.Combine(isolated,"claude-config");
            Environment.SetEnvironmentVariable("CODEX_HOME",Path.Combine(isolated,"codex-home"));
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Run(isolated);
            Console.WriteLine("WSL UI integration tests passed: "+assertions+" (isolated settings, fake WSL and app-server; no live API, authentication, browser or visible window)."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            WslRuntime.RunnerOverride=null; WslRuntime.CodexStartInfoOverride=null;
            SettingsStore.DataDirectoryOverride=null; SettingsStore.StartupRegistrySubKeyOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null;
            Environment.SetEnvironmentVariable("CODEX_HOME",priorCodexHome);
            SynchronizationContext.SetSynchronizationContext(priorContext);
            string full=Path.GetFullPath(isolated);
            if (full.StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("wsl-ui-test-data-",StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
}
