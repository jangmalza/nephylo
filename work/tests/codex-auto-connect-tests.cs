using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static class CodexAutoConnectTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string ScenarioVariable = "NEPHYLO_CODEX_AUTO_TEST_SCENARIO";
    private const string LogVariable = "NEPHYLO_CODEX_AUTO_TEST_LOG";
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static object Get(object target, string name) { return target.GetType().GetField(name, Flags).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target,value); }
    private static object Call(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name,Flags).Invoke(target,args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { throw new InvalidOperationException("This fixture forbids HTTP requests."); }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly WidgetWindow Window;
        internal readonly WidgetSettings Settings;
        internal readonly ProviderView View;
        internal readonly MembershipClient Member;
        internal readonly Dictionary<string,StackPanel> Cards;
        internal Fixture(string root, bool pro, string path, string selected, string distribution = "")
        {
            Window = new WidgetWindow(true);
            ((DispatcherTimer)Get(Window,"timer")).Stop(); ((Forms.NotifyIcon)Get(Window,"tray")).Visible = false;
            Member = new MembershipClient(root,false,MembershipConfiguration.FromValues("https://fixture.supabase.co","sb_publishable_fixture_only",true),new NoNetwork(),()=>0L);
            if (pro) {
                Set(Member,"userId","synthetic-user"); Set(Member,"verifiedPro",true);
                Set(Member,"entitlementDeadline",100000L); Set(Member,"subscriptionDeadline",100000L);
            }
            Set(Window,"demo",false);
            Settings=(WidgetSettings)Get(Window,"settings"); Settings.CodexPath=path; Settings.CodexWslDistribution=distribution;
            var plan=new ServicePlan(root,true,null,null); plan.Select(selected); Set(Window,"servicePlan",plan);
            ((ExtraSettings)Get(Window,"extras")).CursorManual=new ManualUsageRecord { Label="fixture",RemainingPercent=50,ObservedAt=DateTimeOffset.Now };
            View=(ProviderView)Get(Window,"codexView"); Cards=(Dictionary<string,StackPanel>)Get(Window,"serviceCards");
            Call(Window,"RefreshLocalExtras");
            Check(!Window.IsVisible && !Window.IsLoaded,"Hidden fixture never starts the normal loaded/timer workflow");
            Check(Member.HasPro == pro,"Only the synthetic entitlement controls this fixture");
        }
        internal void Refresh(bool manual) { Await((Task)Call(Window,"RefreshDueAsync",manual)); }
        public void Dispose()
        {
            ((DispatcherTimer)Get(Window,"timer")).Stop(); ((CodexService)Get(Window,"codex")).Dispose();
            Set(Window,"demo",true); Window.Close(); ((Forms.NotifyIcon)Get(Window,"tray")).Dispose(); Member.Dispose();
        }
    }
    private static void Await(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(8)) {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));
            Dispatcher.PushFrame(frame); Thread.Sleep(5);
        }
        if (!task.IsCompleted) throw new TimeoutException("Synthetic Codex operation did not complete.");
        task.GetAwaiter().GetResult();
    }
    private static int Calls(string log) { return File.Exists(log) ? File.ReadAllLines(log).Length : 0; }
    private static int Server(string source)
    {
        string log=Environment.GetEnvironmentVariable(LogVariable), scenario=Environment.GetEnvironmentVariable(ScenarioVariable);
        if (String.IsNullOrEmpty(log) || String.IsNullOrEmpty(scenario)) return 2;
        var json=new JavaScriptSerializer(); string line;
        while ((line=Console.ReadLine()) != null) {
            var request=json.Deserialize<Dictionary<string,object>>(line); object id;
            if (!request.TryGetValue("id",out id)) continue;
            string method=(string)request["method"]; object result=new {};
            if (method == "account/read") {
                File.AppendAllText(log,"account-read\n");
                result=scenario == "unauthenticated" ? (object)new { account=(object)null } : new { account=new { type="chatgpt",planType="plus" } };
            }
            if (method == "account/rateLimits/read") result=new { rateLimits=new { primary=new { usedPercent=source == "Ubuntu" ? 12 : 25,windowDurationMins=300 } } };
            Console.WriteLine(json.Serialize(new { id=id,result=result })); Console.Out.Flush();
        }
        return 0;
    }
    private static void Test(string root)
    {
        string exe=Assembly.GetExecutingAssembly().Location, log=Path.Combine(root,"fake-app-server.log");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(LogVariable,log); Environment.SetEnvironmentVariable(ScenarioVariable,"success");
        using (var fixture=new Fixture(root,true,exe,"cursor")) {
            Check(!(bool)Call(fixture.Window,"HasCodexConnection"),"No auth.json or previous usage evidence exists");
            Check(fixture.Cards["codex"].Visibility == Visibility.Collapsed,"An unverified executable is not presented as a connected card");
            fixture.Refresh(false);
            Check(fixture.View.Snapshot != null && fixture.View.Snapshot.Rows[0].RemainingPercent == 75,"Fresh Pro automatically verifies account and quota without auth.json");
            Check(fixture.Cards["codex"].Visibility == Visibility.Visible,"A successful real transport response makes Codex visible");
            Check(fixture.View.LastSuccess.HasValue && fixture.View.RefreshSchedule.Failures == 0,"Successful probe enters the normal provider schedule");
            Check(((ServicePlan)Get(fixture.Window,"servicePlan")).ActiveServiceId == "cursor","Pro auto-discovery preserves the saved Free choice");
            int count=Calls(log); fixture.Refresh(false);
            Check(Calls(log) == count,"Successful auto-connection observes the provider interval");
        }
        using (var fixture=new Fixture(root,true,exe,"cursor")) {
            Check(!(bool)Call(fixture.Window,"HasCodexConnection"),"A fresh widget has no in-memory success from the previous run");
            fixture.Refresh(false);
            Check(fixture.View.Snapshot != null,"A later launch reconnects without needing an auth.json marker");
        }
        string missing=Path.Combine(root,"newly-installed.exe");
        using (var fixture=new Fixture(root,true,missing,"cursor")) {
            int count=Calls(log); fixture.Refresh(false);
            Check(fixture.View.Snapshot == null && fixture.View.Error == null && fixture.View.RefreshSchedule.Failures == 0,"Missing Pro executable is a cached absent candidate, not an API failure");
            Check(fixture.Cards["codex"].Visibility == Visibility.Collapsed && Calls(log) == count,"A missing Pro executable creates neither a phantom card nor a process");
            DateTimeOffset expiry=(DateTimeOffset)Get(fixture.Window,"codexCandidateExpires");
            File.Copy(exe,missing);
            fixture.Refresh(false);
            Check(fixture.View.Snapshot == null && Calls(log) == count,"Routine ticks do not repeatedly rediscover a recently absent executable");
            Check((DateTimeOffset)Get(fixture.Window,"codexCandidateExpires") == expiry,"Negative candidate cache remains stable between ticks");
            fixture.Refresh(true);
            Check(fixture.View.Snapshot != null && Calls(log) == count+1,"Explicit refresh immediately discovers a newly installed executable");
        }
        using (var fixture=new Fixture(root,true,Path.Combine(root,"not-installed.exe"),"cursor")) {
            fixture.Refresh(false);
            fixture.Settings.CodexPath=exe;
            Check((bool)Call(fixture.Window,"ShouldAttemptCodexRead",DateTimeOffset.Now,false),"Changing an explicit path invalidates the negative discovery cache");
            fixture.Refresh(false);
            Check(fixture.View.Snapshot != null,"A changed valid executable is verified without waiting for cache expiry");
        }
        using (var fixture=new Fixture(root,true,Path.Combine(root,"late-installed.exe"),"cursor")) {
            fixture.Refresh(false); File.Copy(exe,fixture.Settings.CodexPath);
            Set(fixture.Window,"codexCandidateExpires",DateTimeOffset.UtcNow.AddSeconds(-1));
            fixture.Refresh(false);
            Check(fixture.View.Snapshot != null,"An expired negative cache permits unattended discovery on a later tick");
        }
        Environment.SetEnvironmentVariable(ScenarioVariable,"unauthenticated");
        using (var fixture=new Fixture(root,true,exe,"cursor")) {
            fixture.Refresh(false); int count=Calls(log);
            Check(fixture.View.Snapshot == null && fixture.View.Issue != null && fixture.View.RefreshSchedule.Failures == 1,"Installed but unauthenticated candidate reports a connection error and records failure");
            Check(fixture.View.RefreshSchedule.NextAttempt > DateTimeOffset.Now,"Account failure retains the provider backoff");
            fixture.Refresh(false);
            Check(Calls(log) == count && fixture.View.RefreshSchedule.Failures == 1,"Routine ticks cannot bypass an account failure backoff");
            fixture.Refresh(true);
            Check(Calls(log) == count+1 && fixture.View.RefreshSchedule.Failures == 2,"Manual refresh can retry an installed candidate without treating it as missing");
            Check(fixture.Cards["codex"].Visibility == Visibility.Collapsed,"Failed candidate remains hidden when another service is connected");
        }
        Environment.SetEnvironmentVariable(ScenarioVariable,"success");
        using (var fixture=new Fixture(root,false,exe,"cursor")) {
            int count=Calls(log); fixture.Refresh(true);
            Check(Calls(log) == count+1 && fixture.View.Snapshot != null,"Accountless use discovers Codex alongside the old selected service");
            Check((DateTimeOffset)Get(fixture.Window,"codexCandidateExpires") > DateTimeOffset.UtcNow,"Accountless discovery retains the bounded candidate cache");
        }
        using (var fixture=new Fixture(root,false,Path.Combine(root,"free-missing.exe"),"codex")) {
            fixture.Refresh(false);
            Check(fixture.View.Issue == null && fixture.View.RefreshSchedule.Failures == 0,"Absent candidate is not an API failure");
            Check(fixture.Cards["codex"].Visibility == Visibility.Collapsed,"Absent Codex stays hidden when another service is connected");
        }
        int wslStarts=0;
        WslRuntime.RunnerOverride=(info,input,ms,ct)=>Task.FromResult(new WslCommandResult { Output=info.Arguments == "--list --quiet" ? "Ubuntu\r\n" : "NEPHYLO_WSL_V1\0/home/fixture\0/usr/bin/codex\0\0" });
        WslRuntime.CodexStartInfoOverride=distribution=> {
            wslStarts++;
            return new ProcessStartInfo(exe,"app-server "+WslRuntime.QuoteArgument(distribution)) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
        };
        using (var fixture=new Fixture(root,true,Path.Combine(root,"windows-not-installed.exe"),"cursor","Ubuntu")) {
            fixture.Refresh(false);
            Check(wslStarts == 1 && fixture.View.Snapshot.Rows[0].RemainingPercent == 88,"Saved WSL selection uses the exact synthetic distribution independent of Windows discovery");
            Check(fixture.Settings.CodexWslDistribution == "Ubuntu","Automatic WSL connection never rewrites its source");
        }
        Check(!Directory.Exists(Path.Combine(root,"codex-home")),"No Codex credential file was created or read from the real profile");
        Check(!Directory.Exists(Path.Combine(root,"claude-config")),"Fixtures never create or alter Claude settings");
        Check(!File.Exists(Path.Combine(root,"settings.json")) && !File.Exists(Path.Combine(root,"membership-session.dat")),"Auto-connect tests persist no settings or membership session");
    }
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "app-server") return Server(args.Length > 1 ? args[1] : "");
        string parent=Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        string root=Path.Combine(parent,"codex-auto-test-"+Guid.NewGuid().ToString("N"));
        string oldHome=Environment.GetEnvironmentVariable("CODEX_HOME");
        string oldScenario=Environment.GetEnvironmentVariable(ScenarioVariable), oldLog=Environment.GetEnvironmentVariable(LogVariable);
        SynchronizationContext oldContext=SynchronizationContext.Current;
        try {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            SettingsStore.DataDirectoryOverride=root; ClaudeUsageService.ConfigDirectoryOverride=Path.Combine(root,"claude-config");
            Environment.SetEnvironmentVariable("CODEX_HOME",Path.Combine(root,"codex-home"));
            Test(root);
            Console.WriteLine("Codex auto-connect tests passed: "+checks+" assertions. Synthetic app-server/WSL; no accounts, network, visible windows or user settings."); return 0;
        } catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            WslRuntime.RunnerOverride=null; WslRuntime.CodexStartInfoOverride=null;
            SettingsStore.DataDirectoryOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null;
            Environment.SetEnvironmentVariable("CODEX_HOME",oldHome); Environment.SetEnvironmentVariable(ScenarioVariable,oldScenario); Environment.SetEnvironmentVariable(LogVariable,oldLog);
            SynchronizationContext.SetSynchronizationContext(oldContext);
            string full=Path.GetFullPath(root);
            if (full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("codex-auto-test-",StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
}
