using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UsageWidget;

internal static class StudioDevelopmentTests
{
    static int count;
    static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool value,string message) { count++;if (!value) throw new Exception(message); }
    static string Hash(byte[] bytes) { using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(bytes)); }
    static T Field<T>(StudioWindow window,string name) { return (T)typeof(StudioWindow).GetField(name,Private).GetValue(window); }
    static void Pump() { var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame); }
    static bool Throws(Action action) { try { action();return false; } catch { return true; } }

    static void Options(string root)
    {
        Check(!StudioDevelopmentMode.IsRequested(new string[0]),"ordinary widget invocation stays ordinary");
        Check(!StudioDevelopmentMode.IsRequested(new[]{"--demo"}),"widget preview is not a development profile");
        foreach(var malformed in new[]{"--ade-dev-bad","--ADE-DEV","--ade-dev=somewhere"})
            Check(StudioDevelopmentMode.IsRequested(new[]{malformed}),"malformed development switches cannot fall into widget initialization");
        var options=StudioDevelopmentOptions.Parse(new[]{"--ade-dev","--ade-dev-profile","worktree-1","--ade-dev-theme","light","--ade-dev-language","en"});
        Check(options.Profile=="worktree-1"&&options.Theme=="light"&&options.Language=="en","explicit development choices parsed");
        Check(StudioDevelopmentOptions.Parse(new[]{"--ade-dev"}).Profile=="default","direct launch has stable development default");
        foreach(var invalid in new[]{"", "..", "../widget", "a/b", "a\\b", "C:\\live", "a:b", "foo bar", ".nephylo", "UPPER", "-leading", new string('a',41)})
            Check(Throws(()=>StudioDevelopmentOptions.Parse(new[]{"--ade-dev","--ade-dev-profile",invalid})),"profile cannot redirect or escape fixed development storage");
        foreach(var invalid in new[]{
            new[]{"--ade-dev","--install"},new[]{"--ade-dev","--demo"},new[]{"--ade-dev","--claude-statusline"},
            new[]{"--ade-dev","--ade-dev"},new[]{"--ade-dev-profile","a"},new[]{"--ade-dev","--ade-dev-profile"},
            new[]{"--ade-dev","--ade-dev-theme","red"},new[]{"--ade-dev","--ade-dev-language","fr"},
            new[]{"--ade-dev","--ade-dev-profile","a","--ade-dev-profile","b"},new[]{"--ade-dev","--data-dir",root}})
            Check(Throws(()=>StudioDevelopmentOptions.Parse(invalid)),"mixed, duplicate or unsafe arguments fail closed");
        var a=new StudioDevelopmentProfile(root,"alpha");var b=new StudioDevelopmentProfile(root,"beta");
        Check(a.DirectoryPath.StartsWith(Path.Combine(root,".nephylo-ade-dev")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"fixed developer root used");
        Check(!a.DirectoryPath.StartsWith(Path.Combine(root,".nephylo")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"development data cannot alias normal widget data");
        Check(a.MutexName!=b.MutexName&&a.WakeName!=b.WakeName,"profile locks are independent");
        Check(a.MutexName!="Local\\UsageWidget.SingleInstance.v1"&&a.MutexName!="Local\\UsageWidget.SingleInstance.v1.Demo","widget and preview locks are independent");
        Check(a.MutexName==new StudioDevelopmentProfile(root.ToUpperInvariant(),"alpha").MutexName,"Windows path casing shares one development owner");
        Check(new StudioDevelopmentProfile(root,"con").DirectoryPath.EndsWith("profile-con",StringComparison.Ordinal),"fixed directory prefix prevents reserved Windows basename");
    }

    static void SettingsAndWindows(string root)
    {
        string live=Path.Combine(root,"live-widget");Directory.CreateDirectory(live);
        var productionSettings=Path.Combine(live,"settings.json");File.WriteAllText(productionSettings,"{\"Theme\":\"light\",\"Language\":\"en\",\"CodexPath\":\"preserved\"}");
        var liveStore=new StudioStore(Path.Combine(live,"studio"));
        var existing=new StudioSession{Title="Existing real-use fixture",Draft="EXISTING-DRAFT-MUST-STAY",PreferredProvider="codex"};liveStore.Save(existing);liveStore.SaveWorkspace(existing.Id,false);
        var before=Directory.GetFiles(live,"*",SearchOption.AllDirectories).ToDictionary(f=>f,f=>Hash(File.ReadAllBytes(f)));
        string prior=SettingsStore.DataDirectoryOverride, priorLanguage=L.Language;
        SettingsStore.DataDirectoryOverride=live;StudioWindow window=null;
        try {
            var profile=new StudioDevelopmentProfile(root,"alpha");
            var options=StudioDevelopmentOptions.Parse(new[]{"--ade-dev","--ade-dev-profile","alpha","--ade-dev-theme","dark","--ade-dev-language","ko"});
            window=StudioDevelopmentMode.CreateWindow(options,profile);window.Show();Pump();
            Check(window.Owner==null&&window.Title.Contains("개발용")&&window.Title.Contains("alpha"),"standalone window visibly identifies development profile");
            Check(SettingsStore.DataDirectoryOverride==live,"standalone ADE does not change widget global storage override");
            Check(Application.Current==null||Application.Current.Windows.Cast<Window>().All(w=>!(w is WidgetWindow)),"development window does not construct hidden widget owner");
            Check(!Field<List<StudioSession>>(window,"sessions").Any(s=>s.Id==existing.Id),"live conversations are not imported");
            Check(Field<StudioStore>(window,"store").CodexHome==Path.Combine(profile.StudioDirectory,"codex-home"),"Codex developer profile is under separate data root");
            foreach(var provider in new[]{"codex","claude","gemini-cli","gemini-api"}) {
                string home=(string)typeof(StudioWindow).GetMethod("ProviderHome",Private).Invoke(window,new object[]{provider});
                Check(home.StartsWith(profile.StudioDirectory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"all provider workspaces stay in development storage");
            }
            var selected=Field<StudioSession>(window,"selected");selected.Project=root;
            Field<TextBox>(window,"input").Text="DEVELOPMENT-DRAFT-ONLY";
            Check((bool)typeof(StudioWindow).GetMethod("SaveCurrent",Private).Invoke(window,null),"development draft persists through normal save path");
            string id=selected.Id;window.Close();window=null;Pump();
            var data=File.ReadAllBytes(Path.Combine(profile.StudioDirectory,id+".dat"));
            Check(!Encoding.UTF8.GetString(data).Contains("DEVELOPMENT-DRAFT-ONLY"),"developer conversations retain encrypted persistence");
            var persisted=profile.LoadSettings();Check(persisted.Theme=="dark"&&persisted.Language=="ko","developer appearance persisted separately");
            window=StudioDevelopmentMode.CreateWindow(StudioDevelopmentOptions.Parse(new[]{"--ade-dev","--ade-dev-profile","alpha"}),profile);window.Show();Pump();
            Check(Field<StudioSession>(window,"selected").Id==id&&Field<TextBox>(window,"input").Text=="DEVELOPMENT-DRAFT-ONLY","restart restores developer draft and selection");
            Check(Field<StudioSession>(window,"selected").Project==root,"restart restores developer project");
            window.Close();window=null;Pump();
            Check(profile.LoadSettings().Theme=="dark","reopen retains previous settings without explicit overrides");
            var beta=new StudioDevelopmentProfile(root,"beta");beta.SaveSettings(new StudioDevelopmentSettings{Theme="light",Language="en"});
            Check(profile.LoadSettings().Theme=="dark"&&beta.LoadSettings().Theme=="light","different developer profiles retain independent settings");
            foreach(var file in before)Check(File.Exists(file.Key)&&Hash(File.ReadAllBytes(file.Key))==file.Value,"existing widget settings, conversation and workspace bytes preserved");
            Check(Directory.GetFiles(live,"*",SearchOption.AllDirectories).Length==before.Count,"no development files appear in live widget storage");
            string preserved=Path.Combine(root,"broken");var broken=new StudioDevelopmentProfile(preserved,"alpha");broken.EnsureDirectory();File.WriteAllText(broken.SettingsFile,"not-json");var corrupt=Hash(File.ReadAllBytes(broken.SettingsFile));
            Check(Throws(()=>broken.LoadSettings()),"corrupt developer settings fail visibly");
            Check(Throws(()=>broken.SaveSettings(new StudioDevelopmentSettings())),"save does not overwrite corrupt developer settings");
            Check(Hash(File.ReadAllBytes(broken.SettingsFile))==corrupt,"corrupt original remains byte-identical");
            var blocked=new StudioDevelopmentProfile(root,"blocked");File.WriteAllText(blocked.DirectoryPath,"occupied");
            Check(Throws(()=>blocked.LoadSettings()),"unavailable developer storage fails instead of falling back to widget storage");
            Check(Hash(File.ReadAllBytes(productionSettings))==before[productionSettings],"storage failures leave original settings intact");
        } finally { if(window!=null)window.Close();SettingsStore.DataDirectoryOverride=prior;L.Language=priorLanguage; }
    }

    static int Host(string[] args)
    {
        string root=args[1],name=args[2],control=args[3];
        var profile=new StudioDevelopmentProfile(root,name);
        var options=StudioDevelopmentOptions.Parse(new[]{"--ade-dev","--ade-dev-profile",name});
        return StudioDevelopmentMode.Run(options,profile,window=>{
            window.ShowActivated=false;window.ShowInTaskbar=false;window.Opacity=0;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
            window.Loaded+=(s,e)=>{
                var selected=Field<StudioSession>(window,"selected");
                // Existence is the parent's readiness signal. Publish only after
                // the writer has closed, so it cannot read a locked/partial JSON.
                string ready=control+".ready",temporary=ready+"."+Process.GetCurrentProcess().Id+".tmp";
                File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(new{pid=Process.GetCurrentProcess().Id,draft=selected.Draft,windows=Application.Current.Windows.Count,widgetWindows=Application.Current.Windows.Cast<Window>().Count(w=>w is WidgetWindow),theme=profile.LoadSettings().Theme}));
                File.Move(temporary,ready);
                timer.Start();
            };
            timer.Tick+=(s,e)=>{if(File.Exists(control+".stop")){timer.Stop();window.Close();}};
            window.Closed+=(s,e)=>timer.Stop();
        });
    }
    static Process StartHost(string root,string name,string control)
    {
        var arguments=new[]{"--host",root,name,control};
        return Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,String.Join(" ",arguments.Select(StudioClaudeClient.Quote))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true});
    }
    static void Wait(Func<bool> condition,string message)
    {
        var watch=Stopwatch.StartNew();while(!condition()){if(watch.Elapsed>TimeSpan.FromSeconds(12))throw new Exception(message);Thread.Sleep(30);}
    }
    static void Stop(Process process,string control)
    {
        if(process==null)return;
        try { if(!process.HasExited){File.WriteAllText(control+".stop","");if(!process.WaitForExit(12000)){process.Kill();process.WaitForExit();throw new Exception("Fixture host did not close normally.");}}Check(process.ExitCode==0,"fixture process exits cleanly"); }
        finally { process.Dispose(); }
    }
    static void Processes(string root)
    {
        Directory.CreateDirectory(root);string firstControl=Path.Combine(root,"first"),secondControl=Path.Combine(root,"second"),otherControl=Path.Combine(root,"other"),resumeControl=Path.Combine(root,"resume");
        var profile=new StudioDevelopmentProfile(root,"alpha");var store=new StudioStore(profile.StudioDirectory);
        var session=new StudioSession{Title="Developer process fixture",Draft="PROCESS-RESTART-DRAFT",PreferredProvider="codex"};store.Save(session);store.SaveWorkspace(session.Id,true);
        profile.SaveSettings(new StudioDevelopmentSettings{Theme="light",Language="en"});
        Process first=null,second=null,other=null,resume=null;
        try {
            first=StartHost(root,"alpha",firstControl);Wait(()=>File.Exists(firstControl+".ready"),"first development process did not start");
            var ready=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(firstControl+".ready"));
            Check(Convert.ToInt32(ready["windows"])==1&&Convert.ToInt32(ready["widgetWindows"])==0,"standalone process creates ADE only");
            Check((string)ready["draft"]=="PROCESS-RESTART-DRAFT"&&(string)ready["theme"]=="light","actual child restores its own draft and appearance");
            second=StartHost(root,"alpha",secondControl);Check(second.WaitForExit(12000)&&second.ExitCode==0,"same-profile relaunch returns after signaling existing owner");
            Check(!first.HasExited&&!File.Exists(secondControl+".ready"),"same-profile launch does not create second writer");
            other=StartHost(root,"beta",otherControl);Wait(()=>File.Exists(otherControl+".ready"),"second independent development profile did not start");
            Check(!first.HasExited&&!other.HasExited&&first.Id!=other.Id,"different development profiles run concurrently");
            Check(!Directory.Exists(Path.Combine(root,".nephylo")),"process startup does not initialize or migrate live widget data");
            Stop(first,firstControl);first=null;
            resume=StartHost(root,"alpha",resumeControl);Wait(()=>File.Exists(resumeControl+".ready"),"development process restart did not start");
            var restored=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(resumeControl+".ready"));
            Check((string)restored["draft"]=="PROCESS-RESTART-DRAFT","full process restart preserves independent draft");
            Check(!other.HasExited,"closing and restarting one profile leaves another running");
        } finally { Stop(second,secondControl);Stop(first,firstControl);Stop(other,otherControl);Stop(resume,resumeControl); }
    }
    [STAThread] static int Main(string[] args)
    {
        if(args.Length>0&&args[0]=="--host")return Host(args);
        string root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"nephylo-ade-dev-tests-"+Guid.NewGuid().ToString("N")));
        try {Directory.CreateDirectory(root);Options(root);SettingsAndWindows(Path.Combine(root,"ui"));Processes(Path.Combine(root,"processes"));Console.WriteLine("Studio development mode tests passed: "+count);return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally {
            string expected=Path.Combine(Path.GetFullPath(Path.GetTempPath()),"nephylo-ade-dev-tests-");
            if(root.StartsWith(expected,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(root))Directory.Delete(root,true);
        }
    }
}
