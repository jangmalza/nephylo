using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageWidget;

// Real ADE window and encrypted session storage, synthetic provider adapters.
// No user profile, browser, login dialog, API key or network request is used.
internal static class StudioGeminiProviderTests
{
    static int count;
    static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly string[] Providers={"codex","claude","gemini-cli","gemini-api"};
    static void Check(bool condition,string label){count++;if(!condition)throw new Exception(label);}
    static string Profile(string provider){return provider=="codex"?"ade-codex":provider=="claude"?"ade-claude-code":provider=="gemini-cli"?"ade-gemini-cli":"ade-gemini-api";}
    static StudioMessage Copy(StudioMessage m){return new StudioMessage{Id=m.Id,Role=m.Role,Text=m.Text,State=m.State,Provider=m.Provider,ExecutionId=m.ExecutionId,Model=m.Model};}

    sealed class ProviderData
    {
        internal readonly string Provider;
        internal bool SignedIn=true;
        internal string Identity="synthetic-person@example.invalid";
        internal readonly string[] Models;
        internal readonly HashSet<string> Threads=new HashSet<string>();
        internal readonly List<RunRecord> Runs=new List<RunRecord>();
        internal readonly List<string> Resumes=new List<string>();
        internal int Counter;
        internal ProviderData(string provider){Provider=provider;Models=new[]{provider+"-model",provider+"-other-model"};}
    }
    sealed class RunRecord
    {
        internal string Thread,Model,Prompt,Account;
        internal List<StudioMessage> History;
    }
    sealed class FakeClient : IStudioAgentClient,IStudioConversationClient
    {
        readonly ProviderData data;
        readonly string connection=Guid.NewGuid().ToString("N");
        List<StudioMessage> history;
        string historyThread;
        bool connected;
        internal string NextResult="completed";
        internal Exception NextFailure;
        internal int HistoryCalls;
        internal FakeClient(ProviderData data){this.data=data;}
        public string Provider{get{return data.Provider;}}
        public string Profile{get{return StudioGeminiProviderTests.Profile(Provider);}}
        public string ConnectionId{get{return connection;}}
        public bool Connected{get{return connected;}}
        public StudioAccountIdentity AccountIdentity{get;private set;}
        public event Action<string,string> Notice;
        public event Action<string,string,string> Message;
        public Task ConnectAsync(string executable,string home){connected=true;return Task.FromResult(0);}
        public Task<Dictionary<string,object>> AccountAsync()
        {
            var response=new Dictionary<string,object>{{"account",data.SignedIn?(object)new Dictionary<string,object>{{"type",Provider},{"email",data.Identity}}:null}};
            if(Provider=="codex")AccountIdentity=StudioAccountIdentity.FromResponse(response);
            else if(Provider=="claude")AccountIdentity=StudioAccountIdentity.FromClaudeStatus(new Dictionary<string,object>{{"loggedIn",data.SignedIn},{"authMethod","claude.ai"},{"email",data.Identity}});
            else if(Provider=="gemini-cli")AccountIdentity=StudioAccountIdentity.FromGeminiCliStatus(data.SignedIn,data.Identity);
            else AccountIdentity=StudioAccountIdentity.FromGeminiApiKey(data.SignedIn?"synthetic-not-a-real-api-key-"+data.Identity:null);
            return Task.FromResult(response);
        }
        public Task<List<StudioModel>> ModelsAsync(){return Task.FromResult(data.Models.Select(m=>new StudioModel{Id=m,Label=m}).ToList());}
        public Task<string> ThreadAsync(string id,string project,string model)
        {
            if(!connected)throw new IOException();
            if(id==null){id=Provider+"-thread-"+(++data.Counter);data.Threads.Add(id);}
            else {if(!data.Threads.Contains(id))throw new InvalidOperationException("Foreign provider session was resumed");data.Resumes.Add(id);}
            return Task.FromResult(id);
        }
        public void PrepareHistory(string threadId,IList<StudioMessage> messages)
        {HistoryCalls++;historyThread=threadId;history=messages.Select(Copy).ToList();}
        public Task<string> RunBoundAsync(string threadId,string project,string model,string prompt,string expected)
        {
            if(!connected||AccountIdentity==null||!AccountIdentity.IsAuthenticated||AccountIdentity.Fingerprint!=expected)throw new StudioFailure(StudioFailureKind.Authentication);
            if(!data.Threads.Contains(threadId))throw new InvalidOperationException("Foreign provider received a request");
            if(!data.Models.Contains(model))throw new StudioFailure(StudioFailureKind.Model);
            if(Provider=="gemini-api"&&(history==null||historyThread!=threadId))throw new InvalidOperationException("API history was not prepared for the current execution");
            data.Runs.Add(new RunRecord{Thread=threadId,Model=model,Prompt=prompt,Account=expected,History=history==null?new List<StudioMessage>():history.Select(Copy).ToList()});
            Emit("partial",Provider+" partial",false);
            if(NextFailure!=null){var error=NextFailure;NextFailure=null;throw error;}
            var result=NextResult;NextResult="completed";
            Emit("partial",Provider+" answer to "+prompt,true);
            return Task.FromResult(result);
        }
        internal void Emit(string id,string text,bool final){var handler=Message;if(handler!=null)handler(id,text,final?Boolean.TrueString:Boolean.FalseString);}
        internal void Signal(string type){var handler=Notice;if(handler!=null)handler(type,"");}
        public Task InterruptAsync(){return Task.FromResult(0);}
        public Task<StudioQuotaSnapshot> QuotaAsync()
        {
            if(Provider!="codex")return Task.FromResult(StudioQuotaSnapshot.Unknown(connection,AccountIdentity));
            return Task.FromResult(new StudioQuotaSnapshot(DateTime.UtcNow,connection,AccountIdentity.Fingerprint,null,new[]{new StudioQuotaBucket("codex","Codex",null,null,null,new StudioQuotaWindow("primary",73,300,DateTime.UtcNow.AddHours(1)),null)},true,true));
        }
        public void Dispose(){connected=false;}
    }

    sealed class Fixture : IDisposable
    {
        readonly SynchronizationContext previous;
        readonly ResourceDictionary resources;
        readonly WidgetWindow owner;
        internal readonly string ProfileRoot;
        internal StudioWindow Window;
        internal readonly Dictionary<string,ProviderData> Data=new Dictionary<string,ProviderData>();
        internal readonly Dictionary<string,FakeClient> Latest=new Dictionary<string,FakeClient>();
        internal Fixture(string root)
        {
            Directory.CreateDirectory(root);ProfileRoot=Path.Combine(root,"profile");SettingsStore.DataDirectoryOverride=ProfileRoot;
            previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            foreach(var provider in Providers)Data[provider]=new ProviderData(provider);
            StudioWindow.ProviderFactoryOverride=p=>{var client=new FakeClient(Data[p]);Latest[p]=client;return client;};
            StudioWindow.ProviderExecutableOverride=p=>"synthetic-not-executed-"+p;
            owner=new WidgetWindow(true);
            resources=(ResourceDictionary)typeof(WidgetWindow).GetMethod("MakeResources",BindingFlags.Static|BindingFlags.NonPublic,null,Type.EmptyTypes,null).Invoke(null,null);
            Open();Session.Project=root;
        }
        internal void Open(){Window=new StudioWindow("",false,resources,owner.Background,owner.Foreground,owner.FontFamily);}
        internal object Get(string field){return typeof(StudioWindow).GetField(field,Flags).GetValue(Window);}
        internal void Set(string field,object value){typeof(StudioWindow).GetField(field,Flags).SetValue(Window,value);}
        internal object Call(string name,params object[] args)
        {
            try{return typeof(StudioWindow).GetMethod(name,Flags|BindingFlags.DeclaredOnly).Invoke(Window,args);}
            catch(TargetInvocationException error){throw error.InnerException??error;}
        }
        internal StudioSession Session{get{return (StudioSession)Get("selected");}}
        internal TextBox Input{get{return (TextBox)Get("input");}}
        internal ComboBox Models{get{return (ComboBox)Get("models");}}
        internal string FilePath{get{return Path.Combine(ProfileRoot,"studio",Session.Id+".dat");}}
        internal void SelectProvider(string provider){((ComboBox)Get("providerPicker")).SelectedIndex=Array.IndexOf(Providers,provider);}
        internal void Connect(string provider,bool activate=true){Pump((Task)Call("ConnectProvider",provider,activate,false));}
        internal void SelectModel(string provider){Models.SelectedItem=Models.Items.Cast<StudioModel>().Single(m=>m.Id==provider+"-model");}
        internal void Send(string prompt)
        {
            Input.Text=prompt;Call("Enable");Check(((Button)Get("send")).IsEnabled,"send is available: "+prompt+" / "+((TextBlock)Get("composerHint")).Text);
            Pump((Task)Call("Send"));
        }
        internal void Prepare(string provider,string payload,string executionId=null)
        {
            Call("OpenHandoff");Check((bool)Get("handoffEditing"),"context editor opens without inference");
            ((ComboBox)Get("handoffProvider")).SelectedIndex=Array.IndexOf(Providers,provider);Call("RefreshHandoffTargets");
            var list=(ComboBox)Get("handoffDestination");
            list.SelectedItem=list.Items.Cast<object>().Single(x=>(string)x.GetType().GetField("Model",Flags).GetValue(x)==provider+"-model"&&(string)x.GetType().GetField("ExecutionId",Flags).GetValue(x)==executionId);
            ((TextBox)Get("handoffEditor")).Text=payload;Call("UpdateHandoffButtons");
            Check(((Button)Get("handoffSend")).IsEnabled,"destination ready only after explicit review: "+provider);
        }
        internal void Submit(){Pump((Task)Call("SubmitHandoff"));}
        internal void Restart(){Window.Close();Check((bool)Get("closed"),"isolated window closes after saving");Open();}
        public void Dispose()
        {
            if(Window!=null){Set("discardOnClose",true);Window.Close();}owner.Close();StudioWindow.ProviderFactoryOverride=null;StudioWindow.ProviderExecutableOverride=null;SettingsStore.DataDirectoryOverride=null;SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
    static void Pump(Task task)
    {
        var frame=new DispatcherFrame();var started=DateTime.UtcNow;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(5)};
        timer.Tick+=(s,e)=>{if(task.IsCompleted||DateTime.UtcNow-started>TimeSpan.FromSeconds(20))frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();
        if(!task.IsCompleted)throw new TimeoutException("Isolated Gemini provider test timed out");task.GetAwaiter().GetResult();
    }
    static void Drain(){Pump(Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.Background).Task);}
    static void Capture(Fixture f,string path,int width,int height)
    {
        f.Window.Width=width;f.Window.Height=height;f.Call("ApplySidebar");f.Call("Enable");
        var content=(FrameworkElement)f.Window.Content;content.Width=width;content.Height=height;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(var dc=background.RenderOpen())dc.DrawRectangle(f.Window.Background,null,new Rect(0,0,width,height));bitmap.Render(background);bitmap.Render(content);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);
    }

    static void ProviderSelection(string root)
    {
        using(var f=new Fixture(root)){
            Check(((ComboBox)f.Get("providerPicker")).Items.Count==4,"task AI picker distinguishes all four connection modes");
            foreach(var provider in Providers){
                f.SelectProvider(provider);f.Connect(provider);f.SelectModel(provider);f.Input.Text="Unsent draft for "+provider;f.Call("SaveCurrent");
                Check((string)f.Get("activeProvider")==provider&&(string)f.Call("SessionProvider")==provider&&(provider=="codex"||f.Session.PreferredProvider==provider),"selected provider has a persistent exact identifier: "+provider);
                Check(f.Models.Items.Cast<StudioModel>().All(m=>m.Id.StartsWith(provider+"-",StringComparison.Ordinal)),"model list belongs to selected connection: "+provider);
                Check(((IStudioAgentClient)f.Get("client")).Profile==Profile(provider),"provider profile is isolated: "+provider);
                f.Restart();
                Check((string)f.Get("activeProvider")==provider&&f.Session.Model==provider+"-model"&&f.Input.Text=="Unsent draft for "+provider,"provider, model and unsent draft survive restart: "+provider);
                f.Connect(provider);
                Check(f.Models.SelectedItem!=null&&((Button)f.Get("send")).IsEnabled,"saved model becomes usable after reconnect: "+provider);
            }
            foreach(var provider in Providers)f.Connect(provider,false);
            Check(f.Latest.Values.Select(c=>c.AccountIdentity.Fingerprint).Distinct().Count()==4,"same synthetic person has distinct provider-scoped identities");
            Check(f.Data.Values.All(d=>d.Runs.Count==0),"provider selection and reconnect never run inference");
        }
    }

    static void FourProviderFlow(string root)
    {
        using(var f=new Fixture(root)){
            f.Connect("codex");f.SelectModel("codex");f.Send("Codex private source context; never auto-forward.");
            var codex=StudioSessions.Active(f.Session);string codexId=codex.Id,codexThread=codex.ThreadId;
            foreach(var provider in Providers.Skip(1))f.Connect(provider,false);
            const string draft="Protected unsent draft with private scratch notes.";f.Input.Text=draft;f.Call("SaveCurrent");
            const string toCli="Reviewed payload only: request A to Gemini CLI.";
            f.Prepare("gemini-cli",toCli);f.Submit();var cli=StudioSessions.Active(f.Session);string cliId=cli.Id,cliThread=cli.ThreadId;
            Check(cli.Provider=="gemini-cli"&&cli.Profile==Profile("gemini-cli")&&f.Data["gemini-cli"].Runs.Single().Prompt==toCli,"Codex to Gemini CLI sends exactly reviewed context to separate profile");
            Check(f.Input.Text==draft&&f.Session.Draft==draft,"CLI handoff preserves unsent composer draft");
            const string toApi="Reviewed payload only: request B to Gemini API.";
            f.Prepare("gemini-api",toApi);f.Submit();var api=StudioSessions.Active(f.Session);string apiId=api.Id,apiThread=api.ThreadId;
            var firstApi=f.Data["gemini-api"].Runs.Single();
            Check(api.Provider=="gemini-api"&&api.Profile==Profile("gemini-api")&&firstApi.Prompt==toApi,"CLI to API keeps both modes separate");
            Check(firstApi.History.Count==0,"new API execution receives no Codex or CLI messages, draft or current prompt as history");
            Check(f.Input.Text==draft&&f.Session.Draft==draft,"API handoff preserves unsent draft");
            const string toClaude="Reviewed payload C to Claude.";f.Prepare("claude",toClaude);f.Submit();
            Check(StudioSessions.Active(f.Session).Provider=="claude"&&f.Data["claude"].Runs.Single().Prompt==toClaude,"existing Claude route remains available after Gemini API");
            const string back="Reviewed return to original Codex execution.";f.Prepare("codex",back,codexId);f.Submit();
            Check(f.Session.ActiveExecutionId==codexId&&f.Session.ThreadId==codexThread&&f.Data["codex"].Resumes.Contains(codexThread),"return to Codex resumes original provider thread");
            Check(f.Session.ProviderSessions.Count==4&&f.Session.ProviderSessions.Select(e=>e.ThreadId).Distinct().Count()==4,"four independent provider execution sessions are retained");
            Check(f.Session.Handoffs.All(h=>h.State=="completed"&&f.Session.Messages.Count(m=>m.Role=="user"&&m.Text==h.Payload)==1),"every reviewed transfer has exactly one submitted payload");
            Check(f.Input.Text==draft&&f.Session.Messages.All(m=>m.Text!=draft),"unsent draft never becomes a transferred message");
            f.Restart();foreach(var provider in Providers)f.Connect(provider,provider=="codex");
            Check(f.Session.ActiveExecutionId==codexId&&f.Input.Text==draft&&f.Session.ProviderSessions.Count==4,"mixed-provider conversation and draft survive application restart");
            const string resumeApi="New reviewed request D for previous API execution.";f.Prepare("gemini-api",resumeApi,apiId);f.Submit();
            var resumed=f.Data["gemini-api"].Runs.Last();
            Check(f.Session.ActiveExecutionId==apiId&&f.Session.ThreadId==apiThread&&resumed.Thread==apiThread,"return to API restores its original local conversation");
            Check(resumed.History.Count==2&&resumed.History.All(m=>m.Provider=="gemini-api"&&m.ExecutionId==apiId),"API restart restores only its own completed exchange");
            Check(resumed.History[0].Text==toApi&&resumed.History.All(m=>m.Text!=resumeApi&&m.Text!=draft),"API history excludes newly submitted prompt and draft");
            f.Send("Explicit API follow-up E.");var follow=f.Data["gemini-api"].Runs.Last();
            Check(follow.History.Count==4&&follow.History.Select(m=>m.Id).Distinct().Count()==4,"API follow-up uses completed pairs once without duplicates");
            f.Prepare("gemini-cli","Return to previous CLI execution.",cliId);f.Submit();
            Check(f.Session.ThreadId==cliThread&&f.Data["gemini-cli"].Resumes.Contains(cliThread),"CLI return resumes original official-CLI session identity");
            f.Call("OpenHandoff");((ComboBox)f.Get("handoffProvider")).SelectedIndex=3;f.Call("RefreshHandoffTargets");
            ((TextBox)f.Get("handoffEditor")).Text="Unsent API handoff draft after restart.";f.Call("SaveCurrent");f.Restart();f.Call("OpenHandoff");
            Check((string)f.Call("HandoffProvider")=="gemini-api"&&((TextBox)f.Get("handoffEditor")).Text=="Unsent API handoff draft after restart.","saved handoff restores API route instead of coercing it to Codex");
        }
    }

    static void HistoryIsolation(string root)
    {
        using(var f=new Fixture(root)){
            f.SelectProvider("gemini-api");f.Connect("gemini-api");f.SelectModel("gemini-api");f.Send("Completed turn one.");
            string execution=StudioSessions.Active(f.Session).Id;var firstPair=f.Session.Messages.Select(Copy).ToList();
            f.Latest["gemini-api"].NextResult="interrupted";f.Send("Interrupted prompt must not replay.");
            Check(f.Session.Messages.Last().State=="interrupted","synthetic interrupted output is marked interrupted");
            f.Latest["gemini-api"].NextFailure=new StudioFailure(StudioFailureKind.Quota);f.Send("Rejected prompt must not replay.");
            Check(f.Session.Messages.Last().State=="failed","provider rejection is recorded as failed");
            f.Session.Messages.Add(new StudioMessage{Id=StudioSessions.NewId(),Role="user",Text="Orphan user text must not replay.",State="completed",Provider="gemini-api",ExecutionId=execution,Model="gemini-api-model"});
            f.Set("dirty",true);f.Call("SaveCurrent");f.Restart();f.Connect("gemini-api");f.Send("Clean next turn.");var run=f.Data["gemini-api"].Runs.Last();
            Check(run.History.Count==2&&run.History[0].Text==firstPair[0].Text&&run.History[1].Text==firstPair[1].Text,"API history excludes whole failed/interrupted exchanges and orphan user prompts");
            Check(run.History.All(m=>m.State=="completed")&&run.History.Select(m=>m.Role).SequenceEqual(new[]{"user","assistant"}),"prepared API history contains only ordered complete pairs");
            f.Prepare("gemini-api","New API execution reviewed context.");f.Submit();
            var fresh=f.Data["gemini-api"].Runs.Last();
            Check(StudioSessions.Active(f.Session).Id!=execution&&fresh.History.Count==0,"new execution of same API provider never inherits previous execution implicitly");
            Check(fresh.Prompt=="New API execution reviewed context.","same-provider handoff transfers only reviewed content");
        }
    }

    static void AuthenticationAndQuota(string root)
    {
        foreach(var provider in new[]{"gemini-cli","gemini-api"})using(var f=new Fixture(Path.Combine(root,provider))){
            f.Connect("codex");Pump((Task)f.Call("RefreshQuota",f.Get("client")));
            Check(((TextBlock)f.Get("quotaSummary")).Text.Contains("73%"),"Codex fixture begins with actual synthetic allowance");
            f.SelectProvider(provider);f.Data[provider].SignedIn=false;f.Connect(provider);f.Input.Text="Do not send without credentials.";f.Call("Enable");
            var hint=((TextBlock)f.Get("composerHint")).Text;var summary=(TextBlock)f.Get("quotaSummary");var details=(TextBlock)f.Get("quotaDetails");
            Check(!((Button)f.Get("send")).IsEnabled&&!((bool)f.Get("authenticated")),"missing "+provider+" credentials block sending");
            Check(hint.Contains("Gemini")&&!hint.Contains("Codex"),"missing credential guidance names selected Gemini mode: "+provider+" / "+hint);
            if(provider=="gemini-api")Check(hint.Contains("키")||hint.IndexOf("key",StringComparison.OrdinalIgnoreCase)>=0,"API readiness explains key setup rather than browser sign-in");
            Check(!summary.Text.Contains("73%")&&!details.Text.Contains("73%")&&!Convert.ToString(summary.ToolTip).Contains("73%"),"Gemini unknown allowance removes visible and accessible Codex values");
            Pump((Task)f.Call("Send"));Check(f.Data.Values.Sum(d=>d.Runs.Count)==0,"missing credentials never send, even by direct send action");
            Capture(f,Path.Combine(root,provider,"missing-auth-720x500.png"),720,500);
            f.Data[provider].SignedIn=true;f.Connect(provider);f.SelectModel(provider);f.Send("Authenticated synthetic request.");
            Check(!((TextBlock)f.Get("quotaSummary")).Text.Contains("73%"),"authenticated Gemini never borrows Codex allowance");
            f.Data[provider].Identity="different-synthetic-person@example.invalid";Pump(f.Latest[provider].AccountAsync());f.Input.Text="Blocked changed-account request.";f.Call("Enable");
            Check((bool)f.Call("NeedsAccountConfirmation")&&!((Button)f.Get("send")).IsEnabled,"saved Gemini execution rejects a changed credential identity");
            var before=f.Data[provider].Runs.Count;Pump((Task)f.Call("Send"));Check(f.Data[provider].Runs.Count==before,"wrong identity cannot resume provider session");
        }
    }

    static void HandoffAccountGuard(string root)
    {
        foreach(var provider in new[]{"gemini-cli","gemini-api"})using(var f=new Fixture(Path.Combine(root,provider))){
            f.Connect("codex");f.SelectModel("codex");f.Send("Source for identity recheck.");var source=f.Session.ActiveExecutionId;
            f.Connect(provider,false);f.Input.Text="Preserved source draft.";f.Prepare(provider,"Reviewed destination context.");f.Data[provider].Identity="changed-during-review@example.invalid";f.Submit();
            Check(f.Data[provider].Runs.Count==0&&f.Session.ActiveExecutionId==source&&f.Session.Handoffs.Last().State=="draft","destination identity changes abort handoff before send: "+provider);
            Check(f.Input.Text=="Preserved source draft."&&(bool)f.Get("handoffEditing"),"blocked handoff retains editable context and source draft: "+provider);
            f.Submit();Check(f.Data[provider].Runs.Count==1&&f.Data[provider].Runs[0].Prompt=="Reviewed destination context.","explicit second confirmation can use freshly reviewed identity: "+provider);
        }
    }

    static void UnknownCliIdentity(string root)
    {
        using(var f=new Fixture(root)){
            f.Data["gemini-cli"].Identity=null;f.SelectProvider("gemini-cli");f.Connect("gemini-cli");f.SelectModel("gemini-cli");f.Send("First synthetic CLI turn with opaque identity unavailable.");
            var cli=StudioSessions.Active(f.Session);string cliId=cli.Id,thread=cli.ThreadId;
            Check(!f.Latest["gemini-cli"].AccountIdentity.Known&&f.Data["gemini-cli"].Runs.Count==1,"authenticated Gemini CLI without an identifier is bound to current connection");
            f.Restart();f.Connect("gemini-cli");f.Input.Text="Resume after explicit account confirmation.";f.Call("Enable");
            Check((bool)f.Call("NeedsAccountConfirmation")&&!((Button)f.Get("send")).IsEnabled,"unknown CLI identity requires confirmation after restart");
            f.Call("ConfirmExecution");f.Send("Resume after explicit account confirmation.");
            Check(f.Session.ThreadId==thread&&f.Data["gemini-cli"].Runs.Last().Thread==thread,"explicit identity confirmation preserves the CLI session");
            f.Connect("codex",false);f.Prepare("codex","Reviewed context leaving unknown-identity CLI.");f.Submit();f.Restart();f.Connect("codex");f.Connect("gemini-cli",false);
            f.Call("OpenHandoff");((ComboBox)f.Get("handoffProvider")).SelectedIndex=2;f.Call("RefreshHandoffTargets");
            var list=(ComboBox)f.Get("handoffDestination");list.SelectedItem=list.Items.Cast<object>().Single(x=>(string)x.GetType().GetField("ExecutionId",Flags).GetValue(x)==cliId);
            ((TextBox)f.Get("handoffEditor")).Text="Explicit return to the prior CLI session.";f.Call("UpdateHandoffButtons");
            Check(!((Button)f.Get("handoffSend")).IsEnabled&&((Button)f.Get("handoffConfirmAccount")).Visibility==Visibility.Visible,"return to unknown-identity CLI cannot bypass per-connection confirmation");
            f.Call("ConfirmHandoffAccount");Check(((Button)f.Get("handoffSend")).IsEnabled,"explicit target identity confirmation enables reviewed CLI return");f.Submit();
            Check(f.Session.ActiveExecutionId==cliId&&f.Session.ThreadId==thread,"confirmed cross-provider return retains original CLI execution");
        }
    }

    static void StorageCompatibility(string root)
    {
        Directory.CreateDirectory(root);var store=new StudioStore(root);string oldId=StudioSessions.NewId();
        var old=new {Id=oldId,Title="Legacy Codex conversation",Project=root,Model="old-codex-model",ThreadId="old-codex-thread",Draft="Legacy draft",Messages=new[]{new{Role="user",Text="Old question",State="completed"},new{Role="assistant",Text="Old answer",State="completed"}}};
        File.WriteAllBytes(Path.Combine(root,oldId+".dat"),ProtectedData.Protect(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(old)),Encoding.UTF8.GetBytes("Nephylo.Studio.v1"),DataProtectionScope.CurrentUser));
        int unreadable;var legacy=store.Load(out unreadable).Single();
        Check(unreadable==0&&StudioSessions.Active(legacy).Provider=="codex"&&legacy.Draft=="Legacy draft","legacy pre-provider Codex storage still migrates without losing draft");
        Check(legacy.Messages.All(m=>m.Provider=="codex"&&m.ExecutionId==legacy.ActiveExecutionId),"legacy messages retain original Codex execution identity");
        foreach(var provider in Providers){
            var next=StudioSessions.Start(legacy,provider,Profile(provider),provider+"-model","opaque-"+provider);next.ThreadId=provider+"-persisted-thread";legacy.ThreadId=next.ThreadId;
            legacy.Messages.Add(new StudioMessage{Id=StudioSessions.NewId(),Role="user",Text="Stored "+provider,State="completed",Provider=provider,ExecutionId=next.Id,Model=next.Model});
            legacy.Messages.Add(new StudioMessage{Id=StudioSessions.NewId(),Role="assistant",Text="Reply "+provider,State="completed",Provider=provider,ExecutionId=next.Id,Model=next.Model});
        }
        store.Save(legacy);var loaded=store.Load(out unreadable).Single();
        Check(unreadable==0&&loaded.ProviderSessions.Count==5&&loaded.Messages.Count==10,"old Codex plus all four modern provider segments round-trip encrypted storage");
        Check(StudioSessions.Active(loaded).Provider=="gemini-api"&&loaded.ThreadId=="gemini-api-persisted-thread","active API session remains active after mixed history reload");
        Check(loaded.ProviderSessions.Any(e=>e.Provider=="claude"&&e.Profile=="ade-claude-code"),"existing Claude profile remains unchanged");
        Check(!File.ReadAllText(Path.Combine(root,oldId+".dat")).Contains("Stored gemini-api"),"new provider history is still encrypted at rest");
    }

    [STAThread]
    public static int Main()
    {
        // Leave room for the store's atomic-write suffix on legacy Windows paths.
        var root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"../../outputs/studio-gemini-provider-tests/f-"+Guid.NewGuid().ToString("N").Substring(0,12)));Directory.CreateDirectory(root);
        try{ProviderSelection(Path.Combine(root,"selection"));FourProviderFlow(Path.Combine(root,"flow"));HistoryIsolation(Path.Combine(root,"history"));AuthenticationAndQuota(Path.Combine(root,"auth"));HandoffAccountGuard(Path.Combine(root,"account-guard"));UnknownCliIdentity(Path.Combine(root,"unknown-cli"));StorageCompatibility(Path.Combine(root,"storage"));Console.WriteLine("PASS: "+count+" four-provider selection, Gemini route isolation, handoff, history, restart and credential guards; synthetic adapters, no authentication or network. "+root);return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
