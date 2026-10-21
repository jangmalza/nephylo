using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageWidget;

// Uses the production Window, storage, provider pool and handoff flow with an
// isolated profile. No process, browser, credentials, network or live AI is used.
internal static class StudioProviderTests
{
    static int count;
    static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string label){count++;if(!ok)throw new Exception(label);}

    sealed class ProviderData
    {
        internal readonly string Provider;
        internal string Email="same-user@example.invalid";
        internal bool SignedIn=true;
        internal string[] Models;
        internal int Counter;
        internal readonly HashSet<string> Threads=new HashSet<string>();
        internal readonly List<RunRecord> Runs=new List<RunRecord>();
        internal readonly List<string> Resumes=new List<string>();
        internal ProviderData(string provider){Provider=provider;Models=new[]{provider+"-model"};}
    }
    sealed class RunRecord
    {
        internal string Thread,Model,Prompt,Account;
    }
    sealed class FakeClient : IStudioAgentClient
    {
        readonly ProviderData data;
        readonly string connection=Guid.NewGuid().ToString("N");
        bool connected;
        internal Action BeforeRun,AfterThread;
        internal TaskCompletionSource<string> HeldRun;
        internal TaskCompletionSource<StudioQuotaSnapshot> HeldQuota;
        internal TaskCompletionSource<List<StudioModel>> HeldModels;
        internal bool HoldNextRun;
        internal Exception NextFailure;
        internal bool InitialIdentityMissing;internal int AccountReads;
        internal FakeClient(ProviderData state){data=state;}
        public string Provider {get{return data.Provider;}}
        public string Profile {get{return Provider=="claude"?"ade-claude-code":"ade-codex";}}
        public string ConnectionId {get{return connection;}}
        public bool Connected {get{return connected;}}
        public StudioAccountIdentity AccountIdentity {get;private set;}
        public event Action<string,string> Notice;
        public event Action<string,string,string> Message;
        public Task ConnectAsync(string executable,string home){connected=true;return Task.FromResult(0);}
        public Task<Dictionary<string,object>> AccountAsync()
        {
            var account=new Dictionary<string,object>{{"type",Provider=="codex"?"chatgpt":"claude"},{"email",data.Email}};
            var response=new Dictionary<string,object>{{"account",data.SignedIn?(object)account:null}};
            AccountIdentity=Provider=="codex"?StudioAccountIdentity.FromResponse(response):StudioAccountIdentity.FromClaudeStatus(new Dictionary<string,object>{{"loggedIn",data.SignedIn},{"authMethod","claude.ai"},{"email",data.Email}});
            if(InitialIdentityMissing&&++AccountReads==1)AccountIdentity=null;
            return Task.FromResult(response);
        }
        public Task<List<StudioModel>> ModelsAsync(){return HeldModels!=null?HeldModels.Task:Task.FromResult(data.Models.Select(id=>new StudioModel{Id=id,Label=id}).ToList());}
        public Task<string> ThreadAsync(string id,string project,string model)
        {
            if(!connected)throw new IOException();
            if(id==null){id=Provider+"-thread-"+(++data.Counter);data.Threads.Add(id);}
            else {if(!data.Threads.Contains(id))throw new InvalidOperationException("Foreign or missing provider thread");data.Resumes.Add(id);}
            if(AfterThread!=null)AfterThread();return Task.FromResult(id);
        }
        public async Task<string> RunBoundAsync(string thread,string project,string model,string prompt,string expected)
        {
            if(!connected||AccountIdentity==null||AccountIdentity.Fingerprint!=expected)throw new StudioFailure(StudioFailureKind.Authentication);
            if(!data.Threads.Contains(thread))throw new InvalidOperationException("Wrong provider received thread");
            if(!data.Models.Contains(model))throw new StudioFailure(StudioFailureKind.Model);
            data.Runs.Add(new RunRecord{Thread=thread,Model=model,Prompt=prompt,Account=expected});
            if(BeforeRun!=null)BeforeRun();
            Emit("answer",Provider+" first ",false);
            if(NextFailure!=null){var failure=NextFailure;NextFailure=null;throw failure;}
            if(HoldNextRun){HoldNextRun=false;HeldRun=new TaskCompletionSource<string>();var result=await HeldRun.Task;return result;}
            Emit("answer",Provider+" answer",true);
            return "completed";
        }
        internal void Emit(string id,string text,bool final){var handler=Message;if(handler!=null)handler(id,text,final?Boolean.TrueString:Boolean.FalseString);}
        internal void Signal(string kind,string value){var handler=Notice;if(handler!=null)handler(kind,value);}
        public Task InterruptAsync(){if(HeldRun!=null)HeldRun.TrySetResult("interrupted");return Task.FromResult(0);}
        internal StudioQuotaSnapshot ObservedQuota()
        {
            if(Provider=="claude")return StudioQuotaSnapshot.Unknown(connection,AccountIdentity);
            return new StudioQuotaSnapshot(DateTime.UtcNow,connection,AccountIdentity.Fingerprint,null,new[]{new StudioQuotaBucket("codex","Codex",null,null,null,new StudioQuotaWindow("primary",73,300,DateTime.UtcNow.AddHours(1)),null)},true,true);
        }
        public Task<StudioQuotaSnapshot> QuotaAsync(){return HeldQuota!=null?HeldQuota.Task:Task.FromResult(ObservedQuota());}
        public void Dispose(){connected=false;if(HeldRun!=null)HeldRun.TrySetException(new IOException());}
    }

    sealed class Fixture : IDisposable
    {
        readonly string profile;
        readonly SynchronizationContext previous;
        readonly ResourceDictionary resources;
        readonly WidgetWindow owner;
        internal StudioWindow Window;
        internal readonly Dictionary<string,ProviderData> Data=new Dictionary<string,ProviderData>();
        internal readonly Dictionary<string,FakeClient> Latest=new Dictionary<string,FakeClient>();
        internal Fixture(string root,string theme=null,string language=null)
        {
            profile=Path.Combine(root,"profile");SettingsStore.DataDirectoryOverride=profile;
            previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Data["codex"]=new ProviderData("codex");Data["claude"]=new ProviderData("claude");
            StudioWindow.ProviderFactoryOverride=p=>{var client=new FakeClient(Data[p]);Latest[p]=client;return client;};
            StudioWindow.ProviderExecutableOverride=p=>"unused-synthetic-"+p;
            owner=new WidgetWindow(true);
            if(theme!=null)typeof(WidgetWindow).GetMethod("ApplyRequestedTheme",Flags,null,new[]{typeof(string)},null).Invoke(owner,new object[]{theme});
            resources=(ResourceDictionary)typeof(WidgetWindow).GetMethod("MakeResources",BindingFlags.Static|BindingFlags.NonPublic,null,Type.EmptyTypes,null).Invoke(null,null);
            if(language!=null)L.Language=language;Open();Session.Project=root;
        }
        internal void Open(){Window=new StudioWindow("",false,resources,owner.Background,owner.Foreground,owner.FontFamily);}
        internal object Get(string field){return typeof(StudioWindow).GetField(field,Flags).GetValue(Window);}
        internal void Set(string field,object value){typeof(StudioWindow).GetField(field,Flags).SetValue(Window,value);}
        internal object Call(string name,params object[] args)
        {
            try{return typeof(StudioWindow).GetMethod(name,Flags|BindingFlags.DeclaredOnly).Invoke(Window,args);}
            catch(TargetInvocationException error){throw error.InnerException??error;}
        }
        internal StudioSession Session {get{return (StudioSession)Get("selected");}}
        internal string FilePath {get{return Path.Combine(profile,"studio",Session.Id+".dat");}}
        internal TextBox Input {get{return (TextBox)Get("input");}}
        internal ComboBox Models {get{return (ComboBox)Get("models");}}
        internal void Connect(string provider,bool activate){Pump((Task)Call("ConnectProvider",provider,activate,false));}
        internal void SelectModel(string id){Models.SelectedItem=Models.Items.Cast<StudioModel>().Single(m=>m.Id==id);}
        internal void Send(string text){Input.Text=text;Call("Enable");Check(((Button)Get("send")).IsEnabled,"send available for selected provider: "+text+"; active="+Get("activeProvider")+" session="+Call("SessionProvider")+" model="+Session.Model+" authed="+Get("authenticated")+" storage="+Get("storageBlocked")+" catalog="+Get("catalogReady")+" match="+Call("ClientMatchesSession")+" confirm="+Call("NeedsAccountConfirmation")+" project="+Directory.Exists(Session.Project??""));Pump((Task)Call("Send"));}
        internal void Prepare(string provider,string payload,string resumeId=null)
        {
            Call("OpenHandoff");Check((bool)Get("handoffEditing"),"context editor opened");
            ((ComboBox)Get("handoffProvider")).SelectedIndex=provider=="claude"?1:0;
            Call("RefreshHandoffTargets");
            var list=(ComboBox)Get("handoffDestination");
            list.SelectedItem=list.Items.Cast<object>().Single(x=>(string)x.GetType().GetField("Model",Flags).GetValue(x)==provider+"-model"&&(string)x.GetType().GetField("ExecutionId",Flags).GetValue(x)==resumeId);
            ((TextBox)Get("handoffEditor")).Text=payload;Call("UpdateHandoffButtons");
            Check(((Button)Get("handoffSend")).IsEnabled,"reviewed destination is ready");
        }
        internal void Submit(){Pump((Task)Call("SubmitHandoff"));}
        internal void Restart(){Window.Close();Check((bool)Get("closed"),"window closed with saved state");Open();}
        public void Dispose()
        {
            if(Window!=null){Set("discardOnClose",true);Window.Close();}owner.Close();
            StudioWindow.ProviderFactoryOverride=null;StudioWindow.ProviderExecutableOverride=null;SettingsStore.DataDirectoryOverride=null;SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
    static void Pump(Task task)
    {
        var frame=new DispatcherFrame();var started=DateTime.UtcNow;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(5)};
        timer.Tick+=(s,e)=>{if(task.IsCompleted||DateTime.UtcNow-started>TimeSpan.FromSeconds(15))frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();
        if(!task.IsCompleted)throw new TimeoutException("Provider fixture task timed out");task.GetAwaiter().GetResult();
    }
    static void Drain(){Pump(Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.Background).Task);}
    static void Capture(Fixture f,string file,int width,int height)
    {
        f.Window.Width=width;f.Window.Height=height;f.Call("ApplySidebar");f.Call("Enable");
        var view=(FrameworkElement)f.Window.Content;view.Width=width;view.Height=height;view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);var bg=new DrawingVisual();using(var dc=bg.RenderOpen())dc.DrawRectangle(f.Window.Background,null,new Rect(0,0,width,height));bitmap.Render(bg);bitmap.Render(view);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(file))encoder.Save(stream);
    }

    static string EmptyText(Fixture fixture)
    {
        var view=(StudioMessageView)fixture.Get("transcript");
        var empty=(StackPanel)typeof(StudioMessageView).GetField("empty",Flags).GetValue(view);
        return String.Join("\n",empty.Children.OfType<TextBlock>().Select(x=>x.Text));
    }
    static void ProviderPresentation(string root)
    {
        Directory.CreateDirectory(root);
        using(var f=new Fixture(root)){
            f.Connect("codex",true);Pump((Task)f.Call("RefreshQuota",f.Get("client")));
            var summary=(TextBlock)f.Get("quotaSummary");var details=(TextBlock)f.Get("quotaDetails");
            Check(summary.Text.Contains("73%")&&((string)summary.ToolTip).Contains("73%"),"confirmed Codex allowance has current details in its tooltip");
            ((ComboBox)f.Get("providerPicker")).SelectedIndex=1;
            Check(summary.Text.Contains("Claude")&&(string)summary.ToolTip==details.Text&&!((string)summary.ToolTip).Contains("73%"),"Claude replaces both visible and accessible Codex allowance details");
            Capture(f,Path.Combine(root,"claude-empty.png"),1000,800);
            Check(EmptyText(f).Contains("Claude Code")&&!EmptyText(f).Contains("Codex")&&EmptyText(f).Contains("프로젝트 파일에 접근하거나 명령을 실행하지 않습니다"),"Claude empty state explains text-only work without project access");
            Check(!((TextBlock)f.Get("status")).Text.Contains("Codex"),"switching an empty task clears old Codex connection instructions");
            var view=(StudioMessageView)f.Get("transcript");view.Measure(new Size(600,200));view.Arrange(new Rect(0,0,600,200));view.UpdateLayout();
            Check(EmptyText(f).Contains("Claude Code")&&!EmptyText(f).Contains("Codex"),"compact empty state retains selected provider");
            f.Restart();
            Check((string)f.Get("activeProvider")=="claude"&&!((TextBlock)f.Get("status")).Text.Contains("Codex"),"restored empty Claude task has provider-neutral startup instructions");
            Check(EmptyText(f).Contains("Claude Code")&&!EmptyText(f).Contains("Codex"),"restored Claude task restores its empty-state provider");

            ((ComboBox)f.Get("providerPicker")).SelectedIndex=0;f.Connect("codex",true);Pump((Task)f.Call("RefreshQuota",f.Get("client")));
            summary=(TextBlock)f.Get("quotaSummary");details=(TextBlock)f.Get("quotaDetails");
            Check(((string)summary.ToolTip).Contains("73%"),"return to Codex restores its own allowance tooltip");
            f.Set("quotaSnapshot",null);f.Call("UpdateQuotaDisplay");
            Check(!((string)summary.ToolTip).Contains("73%")&&(string)summary.ToolTip==details.Text,"unknown allowance replaces a previously confirmed tooltip");
            Pump((Task)f.Call("RefreshQuota",f.Get("client")));f.Set("authenticated",false);f.Call("UpdateQuotaDisplay");
            Check(!((string)summary.ToolTip).Contains("73%")&&(string)summary.ToolTip==details.Text,"loss of authentication clears stale allowance from tooltip");
            f.Set("authenticated",true);f.Call("UpdateQuotaDisplay");
            Check(((string)summary.ToolTip).Contains("73%"),"same valid connection may restore current quota details");
            f.Data["codex"].Email="changed-user@example.invalid";Pump(f.Latest["codex"].AccountAsync());f.Call("UpdateQuotaDisplay");
            Check(!((string)summary.ToolTip).Contains("73%")&&(string)summary.ToolTip==details.Text,"account mismatch never exposes old account allowance in tooltip");
            Check(EmptyText(f).Contains("Codex")&&!EmptyText(f).Contains("Claude Code"),"switching back restores Codex empty-state copy");
            f.Set("storageBlocked",true);((TextBlock)f.Get("status")).Text="Synthetic save failure must remain visible";f.Call("UpdateProviderPresentation");
            Check(((TextBlock)f.Get("status")).Text=="Synthetic save failure must remain visible","provider presentation does not overwrite an actual failure status");
        }
    }

    static void ConnectionStatusRefresh(string root)
    {
        Directory.CreateDirectory(root);
        using(var f=new Fixture(root)){
            f.Data["codex"].SignedIn=false;f.Session.Model="codex-model";f.Input.Text="Unsent synthetic question.";f.Connect("codex",true);
            var status=(TextBlock)f.Get("status");var state=(TextBlock)f.Get("connectionState");
            Check(status.Text.Contains("Codex 로그인")&&!((Button)f.Get("send")).IsEnabled,"logged-out Codex initially shows its sign-in guidance");
            f.Data["codex"].SignedIn=true;f.Latest["codex"].Signal("account","");Drain();
            Check(status.Text.Contains("Codex 연결됨")&&!status.Text.Contains("로그인이 필요"),"asynchronous account update replaces stale Codex sign-in instruction");
            Check(state.Text.Contains("Codex 연결됨")&&((Button)f.Get("send")).IsEnabled&&f.Models.SelectedItem!=null,"asynchronous account update synchronizes header, model and send control");
            f.Session.PreferredProvider="claude";f.Session.Model=null;f.Call("RestoreSessionProvider");
            Check(status.Text.Contains("Claude Code")&&!status.Text.Contains("Codex"),"selecting a saved Claude draft cannot retain Codex connection status");
            f.Data["claude"].SignedIn=false;f.Connect("claude",true);
            Check(status.Text.Contains("Claude Code 인증"),"logged-out Claude shows its own sign-in guidance");
            f.Data["claude"].SignedIn=true;f.Latest["claude"].Signal("account","");Drain();
            Check(!status.Text.Contains("인증 또는")&&state.Text.Contains("Claude Code 연결됨"),"Claude account update replaces stale sign-in guidance with connected or model-selection guidance: "+status.Text);
            f.SelectModel("claude-model");
            foreach(var retained in new[]{"완료","중단했습니다.","저장했습니다.","Synthetic response failure"}){
                status.Text=retained;Pump((Task)f.Call("RefreshAccount",f.Get("client")));
                Check(status.Text==retained,"background account refresh preserves non-connection task status: "+retained);
            }
            f.Set("storageBlocked",true);status.Text="Synthetic save failure must remain visible";Pump((Task)f.Call("RefreshAccount",f.Get("client")));f.Call("ApplyProvider","codex");
            Check(status.Text=="Synthetic save failure must remain visible","provider refresh and selection preserve storage recovery status");f.Set("storageBlocked",false);
            f.Call("ShowFailure",new StudioFailure(StudioFailureKind.Network));var failure=status.Text;f.Call("ApplyProvider","claude");Pump((Task)f.Call("RefreshAccount",f.Get("client")));
            Check(status.Text==failure&&f.Get("displayedFailure")!=null,"provider selection and async refresh preserve actual failure status and classification");
        }
        using(var f=new Fixture(Path.Combine(root,"pending"))){
            Directory.CreateDirectory(f.Session.Project);f.Session.Model="codex-model";f.Data["codex"].SignedIn=false;f.Connect("codex",true);
            var codex=f.Latest["codex"];f.Data["codex"].SignedIn=true;var pending=new TaskCompletionSource<List<StudioModel>>();codex.HeldModels=pending;
            var refresh=(Task)f.Call("RefreshAccount",codex);Check(!refresh.IsCompleted,"account catalog refresh can remain pending during a provider switch");
            f.Session.PreferredProvider="claude";f.Call("RestoreSessionProvider");
            pending.SetResult(new List<StudioModel>{new StudioModel{Id="codex-model",Label="codex-model"}});Pump(refresh);codex.HeldModels=null;
            Check((string)f.Get("activeProvider")=="claude"&&!((TextBlock)f.Get("status")).Text.Contains("Codex"),"inactive completed account refresh does not overwrite current provider guidance");
            f.Session.PreferredProvider="codex";f.Call("RestoreSessionProvider");
            Check((bool)f.Get("authenticated")&&(bool)f.Get("catalogReady")&&f.Models.Items.Count==1,"provider switch cannot cache stale UI over the newly refreshed provider slot");
            var earlier=new TaskCompletionSource<List<StudioModel>>();codex.HeldModels=earlier;var obsolete=(Task)f.Call("RefreshAccount",codex);codex.HeldModels=null;
            f.Data["codex"].SignedIn=false;Pump((Task)f.Call("RefreshAccount",codex));earlier.SetResult(new List<StudioModel>{new StudioModel{Id="obsolete",Label="obsolete"}});Pump(obsolete);
            Check(!(bool)f.Get("authenticated")&&!(bool)f.Get("catalogReady")&&f.Models.Items.Count==0,"older catalog response cannot overwrite a newer signed-out account state");
            Check(((TextBlock)f.Get("status")).Text.Contains("Codex 로그인"),"latest signed-out account state owns the connection instruction");
        }
    }
    static void ClaudeAllowancePresentation(string root)
    {
        Directory.CreateDirectory(root);
        using(var f=new Fixture(root)){
            f.Data["claude"].Models=new[]{"sonnet","opus"};f.Connect("claude",true);f.SelectModel("sonnet");var client=f.Latest["claude"];var now=DateTime.UtcNow;
            var observed=new StudioQuotaSnapshot(now,client.ConnectionId,client.AccountIdentity.Fingerprint,null,new[]{new StudioQuotaBucket("claude-common","Claude",null,null,null,new StudioQuotaWindow("primary",99,300,now.AddHours(1)),new StudioQuotaWindow("secondary",97,10080,now.AddDays(1)))},null,true);
            client.HeldQuota=new TaskCompletionSource<StudioQuotaSnapshot>();client.HeldQuota.SetResult(observed);Pump((Task)f.Call("RefreshQuota",client));
            var summary=(TextBlock)f.Get("quotaSummary");var details=(TextBlock)f.Get("quotaDetails");
            Check(summary.Text.Contains("ADE Claude")&&summary.Text.Contains("99%")&&summary.Text.Contains("97%"),"Claude header shows both actual common-limit windows");
            Check(details.Text.Contains("실험적")&&details.Text.Contains("질문 전송 없이")&&details.Text.Contains("계정 공통")&&(string)summary.ToolTip==details.Text,"Claude details describe source, scope and no-prompt query with matching tooltip");
            Check(((Button)f.Get("quotaRefresh")).IsEnabled,"authenticated Claude can refresh allowance");
            Capture(f,Path.Combine(root,"claude-allowance.png"),1000,800);
            var opusOnly=new StudioQuotaBucket("claude-opus","Claude Opus","opus",null,null,null,new StudioQuotaWindow("secondary",0,10080,now.AddDays(1)));
            var scoped=new StudioQuotaSnapshot(now,client.ConnectionId,client.AccountIdentity.Fingerprint,null,observed.Buckets.Concat(new[]{opusOnly}),null,true);
            client.HeldQuota=new TaskCompletionSource<StudioQuotaSnapshot>();client.HeldQuota.SetResult(scoped);Pump((Task)f.Call("RefreshQuota",client));
            var compact=(TextBlock)f.Get("detailQuotaValue");var rows=(Dictionary<string,TextBlock>)f.Get("sidebarAllowances");
            Check(compact.Text=="97%"&&rows["claude"].Text=="97%","Opus exhaustion cannot be shown as the selected Sonnet allowance");
            f.SelectModel("opus");Check(compact.Text=="0%"&&rows["claude"].Text=="0%","the selected model's exact scoped exhaustion is included in compact allowance");
            ((ComboBox)f.Get("providerPicker")).SelectedIndex=0;f.Call("UpdateMainQuotas");
            Check(rows["claude"].Text=="97%","an inactive provider summary uses common quota rather than an unrelated model-specific limit");
            ((ComboBox)f.Get("providerPicker")).SelectedIndex=1;f.SelectModel("sonnet");
            var onlyScoped=new StudioQuotaSnapshot(now,client.ConnectionId,client.AccountIdentity.Fingerprint,null,new[]{opusOnly},null,true);
            client.HeldQuota=new TaskCompletionSource<StudioQuotaSnapshot>();client.HeldQuota.SetResult(onlyScoped);Pump((Task)f.Call("RefreshQuota",client));
            Check(compact.Text.Contains("확인 필요")&&rows["claude"].Text.Contains("확인 필요"),"a foreign-model-only quota remains unknown for the current model");
            client.HeldQuota=new TaskCompletionSource<StudioQuotaSnapshot>();client.HeldQuota.SetResult(observed);Pump((Task)f.Call("RefreshQuota",client));
            f.Data["claude"].Email="other@example.invalid";Pump(client.AccountAsync());f.Call("UpdateQuotaDisplay");
            Check(!summary.Text.Contains("99%")&&!summary.Text.Contains("97%"),"account change hides old Claude allowance immediately");
            f.Set("quotaSnapshot",null);f.Call("UpdateQuotaDisplay");Check(details.Text.Contains("2.1.304")&&!summary.Text.Contains("99%"),"unavailable Claude allowance explains official CLI prerequisites without fabrication");
        }
    }

    static void Flow(string root)
    {
        using(var f=new Fixture(root)){
            f.Connect("codex",true);f.SelectModel("codex-model");f.Send("Remember the synthetic token: cobalt.");
            var source=StudioSessions.Active(f.Session);var sourceId=source.Id;var sourceThread=source.ThreadId;
            Check(source.Provider=="codex"&&source.Profile=="ade-codex","Codex execution uses dedicated provider profile");
            Check(f.Data["codex"].Runs.Count==1&&f.Data["claude"].Runs.Count==0,"first question sent only to Codex");
            Pump((Task)f.Call("RefreshQuota",f.Get("client")));Check(((TextBlock)f.Get("quotaSummary")).Text.Contains("73%"),"Codex allowance is visible");
            f.Connect("claude",false);Check((string)f.Get("activeProvider")=="codex","preparing destination leaves source provider active");
            Check(f.Latest["codex"].AccountIdentity.Fingerprint!=f.Latest["claude"].AccountIdentity.Fingerprint,"same email identities are namespaced by provider");
            Check(!f.Latest["codex"].AccountIdentity.Fingerprint.Contains("same-user")&&!f.Latest["claude"].AccountIdentity.Fingerprint.Contains("same-user"),"persistent identity contains no raw email");
            f.Input.Text="Keep this unsent question.";f.Call("SaveCurrent");
            const string payload="  Goal: continue after Codex.\nToken: cobalt.\nNext: remember amber too.\n ";
            f.Prepare("claude",payload);
            int messageCount=f.Session.Messages.Count,executionCount=f.Session.ProviderSessions.Count;
            using(var locked=File.Open(f.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read)){
                f.Submit();Check(f.Data["claude"].Runs.Count==0&&f.Session.Messages.Count==messageCount,"save failure performs no Claude execution");
                Check(f.Session.ActiveExecutionId==sourceId&&f.Session.ProviderSessions.Count==executionCount&&(string)f.Get("activeProvider")=="codex","save failure retains original provider and execution");
                Check(f.Session.Handoffs.Last().State=="draft"&&f.Input.Text=="Keep this unsent question.","save failure preserves editable context and unsent draft");
            }
            Check((bool)f.Call("SaveCurrent"),"failed save recovers after lock release");f.Call("UpdateHandoffButtons");f.Submit();
            var target=StudioSessions.Active(f.Session);var targetId=target.Id;var targetThread=target.ThreadId;
            Check(target.Provider=="claude"&&target.Profile=="ade-claude-code"&&target.Id!=sourceId,"handoff starts separate Claude execution");
            Check(target.ThreadId.StartsWith("claude-")&&source.ThreadId==sourceThread,"handoff preserves independent native thread IDs");
            Check(f.Data["claude"].Runs.Single().Prompt==payload&&f.Session.Messages[f.Session.Messages.Count-2].Text==payload,"exact reviewed payload including whitespace is sent and stored");
            Check(f.Session.Handoffs.Last().State=="completed"&&f.Session.Handoffs.Last().TargetExecutionId==targetId,"Claude completion is attached to target execution");
            Check(f.Input.Text=="Keep this unsent question."&&f.Session.Draft==f.Input.Text,"handoff leaves source unsent draft intact");
            Check(!((TextBlock)f.Get("quotaSummary")).Text.Contains("73%")&&((TextBlock)f.Get("quotaSummary")).Text.Contains("Claude"),"Claude never inherits Codex allowance");
            Check(((TextBlock)f.Get("providerDisclosure")).Text.Contains("Anthropic"),"Claude data recipient disclosed");
            Check(f.Models.Items.Cast<StudioModel>().All(m=>m.Id.StartsWith("claude-")),"model catalog follows active provider");
            Check(!f.Models.IsEnabled&&!((ComboBox)f.Get("providerPicker")).IsEnabled,"started task pins provider and model");
            Capture(f,Path.Combine(root,"claude-conversation.png"),1000,700);
            int runs=f.Data["claude"].Runs.Count;f.Submit();Check(f.Data["claude"].Runs.Count==runs,"second submit cannot duplicate completed transfer");

            f.Send("Continue in the same Claude conversation.");Check(f.Data["claude"].Runs.Last().Thread==targetThread&&f.Data["claude"].Resumes.Contains(targetThread),"Claude follow-up resumes its existing session");
            f.Input.Text="Restart-safe draft.";f.Call("SaveCurrent");f.Restart();
            Check(f.Session.ActiveExecutionId==targetId&&(string)f.Get("activeProvider")=="claude","restart restores active Claude execution and provider");
            Check(f.Input.Text=="Restart-safe draft."&&f.Session.ProviderSessions.Single(x=>x.Id==sourceId).ThreadId==sourceThread,"restart restores draft and original Codex thread");
            f.Connect("claude",true);f.Send("Continue after restart.");Check(f.Data["claude"].Runs.Last().Thread==targetThread,"Claude resumes exact session after application restart");
            Check(f.Data["codex"].Runs.Count==1,"Claude follow-ups never execute in Codex");

            // Account/model/provider guards are checked against the actual Send control.
            f.Input.Text="Unsent account and model guard question.";
            var live=f.Get("client");f.Connect("codex",false);f.Set("client",f.Latest["codex"]);f.Call("Enable");
            Check(!((Button)f.Get("send")).IsEnabled,"saved Claude execution cannot send through Codex client");f.Set("client",live);f.Call("Enable");
            var active=StudioSessions.Active(f.Session);var expected=active.AccountKey;active.AccountKey="different-provider-account";f.Call("Enable");
            Check(!((Button)f.Get("send")).IsEnabled&&(bool)f.Call("AccountMismatch"),"different account cannot silently resume Claude thread");active.AccountKey=expected;
            f.Data["claude"].Models=new[]{"claude-other"};f.Connect("claude",true);
            Check(f.Models.SelectedItem==null&&!((Button)f.Get("send")).IsEnabled&&f.Session.Model=="claude-model","missing stored model has no fallback");
            f.Data["claude"].Models=new[]{"claude-model"};f.Connect("claude",true);Check(((Button)f.Get("send")).IsEnabled,"explicit catalog refresh restores existing model");

            // A retained inactive provider may emit delayed output. It must not join
            // the active Claude answer, even while a different turn is running.
            var claude=f.Latest["claude"];claude.HoldNextRun=true;claude.BeforeRun=()=>f.Latest["codex"].Emit("late-codex","WRONG PROVIDER OUTPUT",true);
            f.Input.Text="Held synthetic turn.";var held=(Task)f.Call("Send");Drain();
            Check(!held.IsCompleted&&claude.HeldRun!=null,"synthetic active turn is held for isolation check");
            Check(!f.Session.Messages.Last().Text.Contains("WRONG PROVIDER"),"inactive provider streaming event is ignored");
            claude.Emit("answer","Claude held answer",true);claude.HeldRun.TrySetResult("completed");Pump(held);claude.BeforeRun=null;
            Check(f.Session.Messages.Last().Text=="Claude held answer"&&f.Session.Messages.Last().Provider=="claude","active provider response is not contaminated");

            // Late Codex quota output cannot surface on Claude's active controls.
            f.Call("ApplyProvider","codex");f.Call("Enable");var codex=f.Latest["codex"];codex.HeldQuota=new TaskCompletionSource<StudioQuotaSnapshot>();
            var quotaTask=(Task)f.Call("RefreshQuota",codex);f.Call("ApplyProvider","claude");f.Call("Enable");codex.HeldQuota.TrySetResult(codex.ObservedQuota());Pump(quotaTask);codex.HeldQuota=null;
            Check(!((TextBlock)f.Get("quotaSummary")).Text.Contains("73%")&&((TextBlock)f.Get("quotaSummary")).Text.Contains("Claude"),"late Codex quota cannot overwrite Claude display");

            f.Input.Text="Preserve me when returning.";f.Call("SaveCurrent");
            const string returnPayload="Codex: Claude retained cobalt and added amber. Continue here.";
            f.Prepare("codex",returnPayload,sourceId);f.Call("RefreshHandoffTargets");
            var resumedChoice=((ComboBox)f.Get("handoffDestination")).SelectedItem;
            Check((string)resumedChoice.GetType().GetField("ExecutionId",Flags).GetValue(resumedChoice)==sourceId,"destination reconnect preserves previous execution selection");
            f.Call("SaveCurrent");f.Set("handoffEditing",false);f.Call("OpenHandoff");
            Check(((StudioHandoffRecord)f.Get("handoffDraft")).ResumeExecutionId==sourceId,"closing and reopening draft preserves reviewed previous execution");f.Submit();
            Check(f.Session.ActiveExecutionId==sourceId&&f.Session.ThreadId==sourceThread&&f.Session.ProviderSessions.Count==2,"Claude to Codex return resumes original execution instead of cloning");
            Check(f.Data["codex"].Runs.Last().Prompt==returnPayload&&f.Data["codex"].Runs.Last().Thread==sourceThread,"return context is sent exactly to original Codex thread");
            Check(f.Input.Text=="Preserve me when returning.","return transfer preserves unsent draft");
            Check(f.Session.Messages.All(m=>f.Session.ProviderSessions.Single(e=>e.Id==m.ExecutionId).Provider==m.Provider),"all message provider labels match stored execution identities");

            // Freeze without submitting, then restart. It must remain a draft to
            // review, not an implicit new request or automatic provider switch.
            f.Prepare("claude","Prepared but not delivered.");var frozen=(StudioHandoffRecord)f.Call("FreezeHandoff");f.Set("dirty",true);f.Call("SaveCurrent");
            runs=f.Data.Values.Sum(d=>d.Runs.Count);f.Restart();f.Connect("codex",true);
            Check(!((Button)f.Get("send")).IsEnabled&&f.Data.Values.Sum(d=>d.Runs.Count)==runs,"prepared cross-provider record is not automatically submitted after restart");
            f.Call("OpenHandoff");Check(((TextBox)f.Get("handoffEditor")).Text=="Prepared but not delivered."&&f.Session.Handoffs.Single(h=>h.Id==frozen.Id).State=="draft","prepared transfer reopens exact editable payload");
            Check(f.Session.ActiveExecutionId==sourceId&&(string)f.Get("activeProvider")=="codex","prepared transfer recovery keeps original source provider");
        }
    }
    static void Recovery(string root)
    {
        Directory.CreateDirectory(root);
        using(var f=new Fixture(root)){
            f.Connect("codex",true);f.SelectModel("codex-model");f.Send("Source context for recovery.");
            var source=StudioSessions.Active(f.Session);var sourceId=source.Id;var sourceThread=source.ThreadId;
            f.Connect("claude",false);f.Input.Text="Unsent recovery draft.";f.Call("SaveCurrent");
            const string payload="Reviewed recovery payload with cobalt.";f.Prepare("claude",payload);
            // Lock only after the target execution was durably created, immediately
            // before SendCore saves the prospective user/assistant message pair.
            FileStream locked=null;
            f.Latest["claude"].AfterThread=()=>{locked=File.Open(f.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read);};
            try{
                f.Submit();Check(f.Data["claude"].Runs.Count==0,"pre-run save failure sends nothing after target session allocation");
                Check(f.Session.Messages.Count==2&&f.Session.Handoffs.Last().State=="prepared","pre-run save failure rolls back unsent message pair and retains prepared payload");
                Check(f.Input.Text=="Unsent recovery draft."&&f.Session.Draft==f.Input.Text,"pre-run save failure keeps exact input draft");
                Check(!((Button)f.Get("send")).IsEnabled&&(bool)f.Get("storageBlocked"),"save failure blocks ordinary sends while prepared transfer needs recovery");
            }finally{if(locked!=null)locked.Dispose();f.Latest["claude"].AfterThread=null;}
            Check((bool)f.Call("SaveCurrent"),"prepared target recovery can persist after unlocking");f.Restart();f.Connect("claude",true);
            Check(f.Data["claude"].Runs.Count==0&&!((Button)f.Get("send")).IsEnabled,"restart cannot silently submit prepared target request");
            f.Call("OpenHandoff");
            Check(f.Session.ActiveExecutionId==sourceId&&f.Session.ThreadId==sourceThread&&(string)f.Get("activeProvider")=="codex","prepared target recovery restores source provider and thread");
            Check(f.Session.ProviderSessions.Count==1&&((TextBox)f.Get("handoffEditor")).Text==payload,"unused target is removed while exact reviewed draft survives");
            Check(f.Input.Text=="Unsent recovery draft.","prepared target recovery retains original unsent question");
            f.Connect("codex",false);f.Call("RefreshHandoffTargets");
            var choices=(ComboBox)f.Get("handoffDestination");choices.SelectedIndex=0;f.Call("UpdateHandoffButtons");
            Check(((Button)f.Get("handoffSend")).IsEnabled,"recovered payload requires and accepts explicit resend");f.Submit();
            Check(f.Data["claude"].Runs.Count==1&&f.Data["claude"].Runs[0].Prompt==payload,"explicit recovery sends exactly once to a fresh Claude session");

            // A failure after submission is intentionally unconfirmed. The UI
            // must not turn it into a retry or a transfer to another provider.
            f.Latest["claude"].NextFailure=new IOException("Synthetic transport ended after submission");
            f.Send("Synthetic unknown delivery.");
            Check((bool)f.Call("DeliveryUncertain")&&!((Button)f.Get("send")).IsEnabled&&!((Button)f.Get("handoffOpen")).IsEnabled,"unknown Claude delivery blocks both resend and cross-provider transfer");
            Check(f.Session.Messages.Last().State=="unconfirmed","unknown Claude delivery keeps partial reply with unconfirmed state");
            int runs=f.Data["claude"].Runs.Count;f.Restart();f.Connect("claude",true);
            Check((bool)f.Call("DeliveryUncertain")&&f.Data["claude"].Runs.Count==runs,"unknown delivery survives restart without replay");
            f.Call("AcknowledgeDelivery");Check(!(bool)f.Call("DeliveryUncertain")&&f.Data["claude"].Runs.Count==runs,"manual result review never resends previous request");

            var claude=f.Latest["claude"];claude.HoldNextRun=true;f.Input.Text="Synthetic stoppable response.";
            var running=(Task)f.Call("Send");Drain();Check(claude.HeldRun!=null&&!running.IsCompleted,"Claude synthetic streaming turn is active before stop");
            Pump((Task)f.Call("Stop"));Pump(running);
            Check(f.Session.Messages.Last().State=="interrupted"&&f.Session.Messages.Last().Text.Contains("claude first"),"Claude stop preserves partial answer with interrupted state");
            Check(!(bool)f.Call("DeliveryUncertain")&&!((Button)f.Get("send")).IsEnabled,"confirmed stop leaves an empty composer without replay");
            f.Input.Text="Explicit new question after stopping.";Check(((Button)f.Get("send")).IsEnabled,"confirmed stop permits a newly composed question");
        }
    }
    static void UnmaterializedAndUnknownIdentity(string root)
    {
        Directory.CreateDirectory(root);
        using(var f=new Fixture(root)){
            f.Session.PreferredProvider="claude";f.Call("RestoreSessionProvider");f.Connect("claude",true);f.SelectModel("claude-model");
            f.Latest["claude"].AfterThread=()=>f.Set("cancelRequested",true);
            f.Send("Cancel before first prompt.");
            var unused=StudioSessions.Active(f.Session).ThreadId;
            Check(f.Session.Messages.Count==0&&f.Data["claude"].Runs.Count==0&&!String.IsNullOrEmpty(unused),"pre-send cancellation persists an unused Claude ID without submitting");
            f.Data["claude"].Threads.Remove(unused);f.Restart();f.Connect("claude",true);f.Send("Explicit first prompt after restart.");
            var materialized=StudioSessions.Active(f.Session).ThreadId;
            Check(materialized!=unused&&!f.Data["claude"].Resumes.Contains(unused),"empty Claude execution allocates fresh ID instead of resuming an unmaterialized ID");
            f.Restart();f.Connect("claude",true);f.Send("Resume materialized conversation.");
            Check(f.Data["claude"].Runs.Last().Thread==materialized,"messages prevent automatic Claude session fallback");
        }
        using(var f=new Fixture(Path.Combine(root,"unknown"))){
            Directory.CreateDirectory(f.Session.Project);f.Data["claude"].Email="";
            f.Session.PreferredProvider="claude";f.Call("RestoreSessionProvider");f.Connect("claude",true);f.SelectModel("claude-model");f.Send("Unknown identity in confirmed new connection.");
            Check(!f.Latest["claude"].AccountIdentity.Known&&f.Data["claude"].Runs.Count==1,"authenticated unknown identity may start on current connection");
            var thread=f.Session.ThreadId;f.Restart();f.Connect("claude",true);
            Check((bool)f.Call("NeedsAccountConfirmation")&&!((Button)f.Get("send")).IsEnabled,"unknown identity requires explicit confirmation after reconnect");
            f.Call("ConfirmExecution");f.Send("Explicitly confirmed unknown identity resume.");
            Check(f.Data["claude"].Runs.Last().Thread==thread,"explicit unknown identity confirmation preserves provider session");
        }
    }
    static void ReviewGuards(string root)
    {
        Directory.CreateDirectory(root);
        using(var f=new Fixture(root)){
            f.Connect("codex",true);f.SelectModel("codex-model");
            f.Latest["codex"].AfterThread=()=>{throw new StudioFailure(StudioFailureKind.Compatibility);};f.Send("Preparation must fail before returning thread.");
            Check(f.Session.ProviderSessions.Count==0&&f.Session.ActiveExecutionId==null,"failed first thread preparation removes unused execution");
            ((ComboBox)f.Get("providerPicker")).SelectedIndex=1;f.Connect("claude",true);f.SelectModel("claude-model");f.Send("Claude after failed Codex preparation.");
            Check(StudioSessions.Active(f.Session).Provider=="claude"&&f.Session.Model=="claude-model","new provider remains usable after first preparation failure");
            f.Latest["codex"].AfterThread=null;f.Connect("codex",false);f.Prepare("codex","Review me before choosing an account.");
            var source=f.Session.ActiveExecutionId;var countBefore=f.Data["codex"].Runs.Count;f.Data["codex"].Email="changed-account@example.invalid";f.Submit();
            Check(f.Data["codex"].Runs.Count==countBefore&&f.Session.ActiveExecutionId==source&&f.Session.Handoffs.Last().State=="draft","handoff preflight account switch sends nothing and preserves editable source");
            Check(((TextBlock)f.Get("handoffNotice")).Text.Contains("계정")&&(bool)f.Get("handoffEditing"),"account switch requires explicit review");
            f.Submit();Check(f.Data["codex"].Runs.Count==countBefore+1,"explicit second confirmation may use freshly reviewed account");
        }
        using(var f=new Fixture(Path.Combine(root,"u"))){
            Directory.CreateDirectory(f.Session.Project);f.Data["claude"].Email="";
            f.Connect("codex",true);f.SelectModel("codex-model");f.Send("Codex source.");var codex=StudioSessions.Active(f.Session).Id;
            f.Connect("claude",false);f.Prepare("claude","To unknown identity Claude.");f.Submit();var claude=StudioSessions.Active(f.Session).Id;var claudeThread=f.Session.ThreadId;
            f.Prepare("codex","Return to Codex.",codex);f.Submit();f.Restart();f.Connect("codex",true);f.Connect("claude",false);
            f.Call("OpenHandoff");((ComboBox)f.Get("handoffProvider")).SelectedIndex=1;f.Call("RefreshHandoffTargets");
            var choices=(ComboBox)f.Get("handoffDestination");choices.SelectedItem=choices.Items.Cast<object>().Single(x=>(string)x.GetType().GetField("ExecutionId",Flags).GetValue(x)==claude);
            ((TextBox)f.Get("handoffEditor")).Text="Resume previous Claude after app restart.";f.Call("UpdateHandoffButtons");
            Check(!((Button)f.Get("handoffSend")).IsEnabled&&((Button)f.Get("handoffConfirmAccount")).Visibility==Visibility.Visible,"inactive unknown-identity session is selectable but requires confirmation after restart");
            Capture(f,Path.Combine(root,"handoff-confirm-narrow.png"),720,500);
            f.Call("ConfirmHandoffAccount");Check(((Button)f.Get("handoffSend")).IsEnabled,"explicit destination account confirmation permits previous Claude session");f.Submit();
            Check(f.Session.ActiveExecutionId==claude&&f.Session.ThreadId==claudeThread,"unknown-identity return resumes original Claude session rather than cloning");
        }
    }
    static string ComposerHint(Fixture f){return ((TextBlock)f.Get("composerHint")).Text;}
    static bool Under(DependencyObject child,DependencyObject ancestor)
    {for(var current=child;current!=null;current=VisualTreeHelper.GetParent(current))if(current==ancestor)return true;return false;}
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {if(root is T)yield return (T)root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants<T>(VisualTreeHelper.GetChild(root,i)))yield return child;}
    static void ComposerAndCriticalActions(string root)
    {
        Directory.CreateDirectory(root);
        using(var f=new Fixture(root)){
            var action=(Button)f.Get("composerAction");var send=(Button)f.Get("send");var details=(Border)f.Get("connectionDetails");
            f.Call("Enable");
            Check(ComposerHint(f).Contains("Codex")&&ComposerHint(f).Contains("연결")&&action.Visibility==Visibility.Visible&&((string)action.Content).Contains("Codex")&&((string)action.Content).Contains("연결 확인"),"disconnected composer names provider and offers its next action");
            Check(f.Get("nextComposerAction")!=null&&!send.IsEnabled,"disconnected next action exists without permitting a request");
            Check(((WrapPanel)f.Get("setupProgress")).Visibility==Visibility.Visible&&((TextBlock)f.Get("setupConnection")).Text.StartsWith("○"),"first-use checklist shows missing connection without connecting automatically");
            f.Data["codex"].SignedIn=false;f.Connect("codex",true);
            Check(ComposerHint(f).Contains("로그인")&&(string)action.Content=="로그인"&&action.IsEnabled,"connected signed-out provider gives actionable sign-in guidance");
            f.Data["codex"].SignedIn=true;f.Connect("codex",true);f.Session.Project=null;f.Call("Enable");
            Check(ComposerHint(f).Contains("프로젝트")&&(string)action.Content=="폴더 선택","unavailable project points to choosing a folder");
            f.Session.Project=root;f.Call("Enable");
            Check(ComposerHint(f).Contains("모델")&&(string)action.Content=="모델 선택"&&((TextBlock)f.Get("modelPlaceholder")).Visibility==Visibility.Visible,"missing model has visible placeholder and next action");
            Check(Descendants<TextBlock>((StudioMessageView)f.Get("transcript")).Any(t=>t.Text==ComposerHint(f))&&!Descendants<TextBlock>((StudioMessageView)f.Get("transcript")).Any(t=>t.Text.Contains("프로젝트를 선택하고 Codex를 연결")),"empty conversation follows actual model-selection readiness after connecting");
            Check(((Border)f.Get("workProgress")).Visibility==Visibility.Collapsed,"connection-only completion does not leave a duplicate task progress checklist");
            f.Window.Show();f.Window.Activate();Drain();((Action)f.Get("nextComposerAction"))();Drain();
            var modelMenu=(ContextMenu)f.Get("activeMainMenu");Check(modelMenu!=null&&modelMenu.IsOpen&&modelMenu.PlacementTarget==(Button)f.Get("newModelButton")&&modelMenu.Items.Count>0,"choose-model next action opens the compact model selector with available choices");modelMenu.IsOpen=false;f.Window.Hide();
            f.SelectModel("codex-model");
            Check(((TextBlock)f.Get("modelPlaceholder")).Visibility==Visibility.Collapsed&&ComposerHint(f).Contains("질문을 입력")&&!send.IsEnabled,"ready empty composer asks for a question rather than enabling send");
            Check(new[]{"setupConnection","setupProject","setupModel"}.All(n=>((TextBlock)f.Get(n)).Text.StartsWith("✓")),"setup checks reflect authentication, available folder and explicitly selected model");
            foreach(var blank in new[]{""," ","\r\n\t ","\u3000"}){
                f.Input.Text=blank;f.Call("Enable");int before=f.Data["codex"].Runs.Count;Pump((Task)f.Call("Send"));
                Check(!send.IsEnabled&&f.Data["codex"].Runs.Count==before,"empty or whitespace-only draft cannot be sent: "+blank.Length);
                Check((string)send.ToolTip==ComposerHint(f),"disabled send explains the exact composer requirement");
            }
            f.Input.Text="Synthetic nonblank question.";
            Check(send.IsEnabled&&ComposerHint(f).Contains("Ctrl+Enter")&&action.Visibility==Visibility.Collapsed,"nonblank ready question exposes shortcut without unnecessary action");
            Check(details.Visibility==Visibility.Collapsed&&((TextBlock)f.Get("providerDisclosure")).Visibility==Visibility.Visible,"connection details begin collapsed while first-send disclosure remains visible");
            f.Set("storageBlocked",true);f.Set("busy",true);f.Call("Enable");
            Check(ComposerHint(f).Contains("저장")&&!action.IsEnabled&&!send.IsEnabled,"save failure takes priority over generic streaming guidance");
            Check(((WrapPanel)f.Get("setupProgress")).Visibility==Visibility.Collapsed,"save recovery replaces setup checklist");
            f.Set("storageBlocked",false);f.Set("busy",false);f.Call("Enable");
            f.Session.Archived=true;f.Call("Enable");
            Check(ComposerHint(f).Contains("보관")&&(string)action.Content=="보관 해제"&&!send.IsEnabled,"archived conversation offers restore instead of send");
            ((Action)f.Get("nextComposerAction"))();Check(!f.Session.Archived&&send.IsEnabled,"composer restore action returns to the same usable draft");
            f.Send("Synthetic source for empty-composer context transfer.");
            Check(((WrapPanel)f.Get("setupProgress")).Visibility==Visibility.Collapsed&&StudioWindow.ConversationStatus(f.Session,false)=="응답 완료","established conversation hides checklist and distinguishes answer completion");
            Check(StudioWindow.ConversationStatus(f.Session,true)=="응답 중","active answer is distinct from completion");
            f.Session.Draft="Unsent draft";Check(StudioWindow.ConversationStatus(f.Session,false)=="초안 있음","draft indicator does not imply it has been saved");
            f.Session.Messages.Last().State="interrupted";Check(StudioWindow.ConversationStatus(f.Session,false)=="중단됨","interruption outranks draft");
            f.Session.Messages.Last().State="failed";Check(StudioWindow.ConversationStatus(f.Session,false)=="확인 필요","failure cannot be labeled complete");
            f.Session.Messages.Last().State="completed";f.Session.Draft="";
            Check(String.IsNullOrEmpty(f.Input.Text)&&!send.IsEnabled,"successful send clears composer and disables another empty send");
            var execution=StudioSessions.Active(f.Session);var originalAccount=execution.AccountKey;execution.AccountKey=null;
            ((Dictionary<string,string>)f.Get("executionConfirmations")).Clear();f.Call("Enable");
            var confirm=(Button)f.Get("confirmExecution");
            Check(confirm.Visibility==Visibility.Visible&&confirm.IsEnabled&&!Under(confirm,details)&&details.Visibility==Visibility.Collapsed,"account confirmation is available outside collapsed connection details");
            Check(ComposerHint(f).Contains("현재 계정")&&!send.IsEnabled,"account confirmation requirement is explained in composer");
            execution.AccountKey=originalAccount;execution.DeliveryState="awaitingVerification";f.Call("Enable");
            Check(StudioWindow.ConversationStatus(f.Session,false)=="전송 확인 필요","uncertain delivery remains visible in the sidebar");
            var review=(Button)f.Get("acknowledgeDelivery");
            Check(review.Visibility==Visibility.Visible&&review.IsEnabled&&!Under(review,details),"uncertain-delivery review remains reachable when details are collapsed");
            Check(ComposerHint(f).Contains("전송 결과")&&!send.IsEnabled,"uncertain request result replaces generic empty-question guidance");
            f.Call("AcknowledgeDelivery");f.Connect("claude",false);
            f.Prepare("claude","Explicit reviewed context with an empty main composer.");
            Check(String.IsNullOrEmpty(f.Input.Text)&&!send.IsEnabled&&((Button)f.Get("handoffSend")).IsEnabled,"reviewed context can be sent independently of empty ordinary composer");
            f.Submit();
            Check(f.Data["claude"].Runs.Count==1&&f.Data["claude"].Runs[0].Prompt=="Explicit reviewed context with an empty main composer."&&String.IsNullOrEmpty(f.Input.Text),"blank-composer handoff sends only its reviewed text once");
            f.Call("NewSession");f.Data["claude"].SignedIn=false;f.Connect("claude",true);
            Check(ComposerHint(f).Contains("Claude Code")&&ComposerHint(f).Contains("이용 권한")&&(string)action.Content=="로그인","Claude composer distinguishes authentication and entitlement requirements");
        }
    }
    static void SidebarNavigation(string root)
    {
        Directory.CreateDirectory(root);
        using(var f=new Fixture(root)){
            f.Window.Width=720;f.Window.Height=500;f.Call("ApplySidebar");
            f.Input.Text="First conversation draft.";f.Call("SaveCurrent");var first=f.Session;
            f.Call("NewSession");var second=f.Session;f.Input.Text="Second conversation draft.";f.Call("SaveCurrent");
            var list=(ListBox)f.Get("list");var search=(TextBox)f.Get("search");
            f.Set("narrowSidebarOpen",true);f.Call("ApplySidebar");list.SelectedItem=first;
            Check(f.Session==first&&!(bool)f.Get("narrowSidebarOpen")&&((Border)f.Get("sidebar")).Visibility==Visibility.Collapsed,"successful narrow-screen selection closes the sidebar after saving");
            Check(second.Draft=="Second conversation draft."&&f.Input.Text=="First conversation draft.","selection preserves both outgoing and incoming drafts");
            f.Set("narrowSidebarOpen",true);f.Call("ApplySidebar");f.Input.Text="Changed draft must survive a failed switch.";
            using(File.Open(f.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read)){
                list.SelectedItem=second;
                Check(f.Session==first&&list.SelectedItem==first&&(bool)f.Get("narrowSidebarOpen"),"failed outgoing save leaves current conversation and narrow drawer selected");
                Check(f.Input.Text=="Changed draft must survive a failed switch."&&(bool)f.Get("storageBlocked"),"failed sidebar navigation retains the exact unsaved draft and recovery state");
            }
            Check((bool)f.Call("SaveCurrent"),"drawer save failure recovers after lock release");list.SelectedItem=second;
            Check(f.Session==second&&!(bool)f.Get("narrowSidebarOpen"),"explicit successful retry closes the narrow drawer");
            foreach(var locked in new[]{"busy","connecting","handoffEditing"}){
                f.Set(locked,true);f.Call("Enable");f.Set("narrowSidebarOpen",false);f.Set("sidebarCollapsed",true);f.Call("ApplySidebar");f.Call("FocusConversationSearch");
                Check(!(bool)f.Get("narrowSidebarOpen")&&(bool)f.Get("sidebarCollapsed"),"Ctrl+K search helper does not change layout while "+locked);
                f.Set(locked,false);f.Call("Enable");
            }
            f.Call("FocusConversationSearch");
            Check((bool)f.Get("narrowSidebarOpen")&&!(bool)f.Get("sidebarCollapsed")&&((Border)f.Get("sidebar")).Visibility==Visibility.Visible,"Ctrl+K helper reveals search in an unlocked narrow layout");
            // A temporary fixture window supplies real WPF focus, using only this
            // isolated test UI. No installed application or external UI is controlled.
            f.Window.Show();f.Window.Activate();Drain();search.Focus();f.Call("CloseNarrowSidebar");Drain();
            Check(!(bool)f.Get("narrowSidebarOpen")&&f.Input.IsKeyboardFocused,"Escape helper closes the drawer and returns keyboard focus to composer");
            f.Session.Archived=true;f.Call("Enable");f.Set("narrowSidebarOpen",true);f.Call("ApplySidebar");search.Focus();f.Call("CloseNarrowSidebar");Drain();
            Check(((Button)f.Get("sidebarToggle")).IsKeyboardFocused,"Escape helper focuses sidebar toggle when composer is read-only");
            f.Session.Archived=false;f.Call("Enable");f.Window.Hide();
        }
    }
    static void PresentationRenders(string root)
    {
        Directory.CreateDirectory(root);
        foreach(var theme in new[]{"dark","light"}){
            var folder=Path.Combine(root,theme);Directory.CreateDirectory(folder);
            using(var f=new Fixture(folder,theme)){
                f.Session.Project=null;f.Call("RefreshList");f.Call("Render");
                Capture(f,Path.Combine(root,"ade-setup-"+theme+"-normal.png"),1180,800);
                Capture(f,Path.Combine(root,"ade-setup-"+theme+"-narrow.png"),720,500);
                f.Session.Project=folder;
                f.Session.Title="대시보드 가독성 개선";f.Session.TitleIsCustom=true;
                f.Connect("codex",true);f.SelectModel("codex-model");f.Send("기존 흑백 디자인을 유지하면서 대시보드 가독성 개선점을 정리해 주세요.");
                f.Session.Messages.Last().Text="현재 화면의 정보 우선순위를 먼저 정리하겠습니다.\n\n## 먼저 개선할 부분\n- 제목과 현재 작업 상태를 간결하게 표시합니다.\n- 중요한 정보는 항상 보이게 두고 세부 설명은 필요할 때 펼칩니다.\n- 다음 행동을 입력창 가까이 안내합니다.\n\n이 방향으로 진행하면 기존 디자인을 유지하면서도 대화에 집중할 수 있습니다.";
                f.Input.Text="좋아요. 우선순위가 높은 항목부터 진행해 주세요.";f.Call("Render");Pump((Task)f.Call("RefreshQuota",f.Get("client")));
                Capture(f,Path.Combine(root,"ade-ux-"+theme+"-normal-conversation.png"),1180,800);
                Capture(f,Path.Combine(root,"ade-ux-"+theme+"-narrow-conversation.png"),720,500);
                Check(((Border)f.Get("connectionDetails")).Visibility==Visibility.Collapsed&&((Button)f.Get("send")).IsEnabled,"connected "+theme+" conversation render retains a usable composer with collapsed details");
                f.Set("narrowSidebarOpen",true);f.Call("ApplySidebar");Capture(f,Path.Combine(root,"ade-ux-"+theme+"-narrow-drawer.png"),720,500);f.Call("CloseNarrowSidebar");
                f.Connect("claude",false);f.Prepare("claude","기존 흑백 디자인을 유지하고 대시보드 가독성을 개선합니다. 핵심 정보와 다음 행동이 먼저 보이게 정리해 주세요.");
                Capture(f,Path.Combine(root,"ade-ux-"+theme+"-normal-context-review.png"),1180,800);
                Capture(f,Path.Combine(root,"ade-ux-"+theme+"-narrow-context-review.png"),720,500);
                Check(((Button)f.Get("handoffSend")).IsEnabled,"connected "+theme+" context review render preserves explicit handoff action");
            }
        }
    }
    static Rect ScreenBounds(FrameworkElement control,FrameworkElement root)
    {return control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));}
    static void ConnectionFlowPresentation(string root)
    {
        Directory.CreateDirectory(root);
        using(var f=new Fixture(Path.Combine(root,"cd"))){
            ((ComboBox)f.Get("providerPicker")).SelectedIndex=1;f.Connect("claude",true);f.SelectModel("claude-model");f.Send("Keep the source execution");string source=f.Session.ThreadId;
            StudioWindow.ProviderFactoryOverride=p=>{var agent=new FakeClient(f.Data[p]){InitialIdentityMissing=p=="codex"};f.Latest[p]=agent;return agent;};
            f.Call("OpenHandoff");((ComboBox)f.Get("handoffProvider")).SelectedIndex=0;Pump((Task)f.Call("CheckHandoffConnection"));
            Check(f.Latest["codex"].AccountReads==2&&((ComboBox)f.Get("handoffDestination")).Items.Count>0,"cold destination identity is refreshed once without requiring another user click");
            Check(((TextBlock)f.Get("handoffNotice")).Text.Contains("모델을 선택")&&!((TextBlock)f.Get("handoffNotice")).Text.Contains("인증·모델"),"an authenticated connection without a chosen model asks for model selection rather than reporting an authentication failure");
            Check(f.Data["codex"].Runs.Count==0&&f.Session.ThreadId==source&&!((Button)f.Get("handoffSend")).IsEnabled,"metadata recheck neither submits a prompt nor bypasses the destination/model review");
        }
        using(var f=new Fixture(Path.Combine(root,"behavior"))){
            f.Connect("codex",true);f.SelectModel("codex-model");f.Send("Seed a pinned conversation");
            f.Input.Text="Keep this unsent draft";f.Call("SaveCurrent");string thread=f.Session.ThreadId;int messages=f.Session.Messages.Count,runs=f.Data["codex"].Runs.Count;
            f.Restart();Drain();var action=(Button)f.Get("composerAction");var send=(Button)f.Get("send");var change=(Button)f.Get("composerHandoff");
            Check(((TextBlock)f.Get("composerModelText")).Text.Contains("codex-model")&&((TextBlock)f.Get("composerModelText")).Text.Contains("고정"),"restart shows the saved pinned model before connecting instead of a choose-model placeholder");
            Check(((WrapPanel)f.Get("composerSelectors")).Visibility==Visibility.Collapsed&&((WrapPanel)f.Get("composerPinnedModel")).Visibility==Visibility.Visible,"pinned task replaces disabled selectors with a readable model and handoff action");
            Check(ComposerHint(f).Contains("복원")&&action.Visibility==Visibility.Visible&&send.Visibility==Visibility.Collapsed&&!send.IsEnabled,"restored conversation explains the connection step and exposes one primary action");
            Check(!Under(change,(Border)f.Get("connectionDetails"))&&change.IsEnabled,"model change is reachable beside the current model with details closed");
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Drain();
            Check(f.Latest["codex"].Connected&&f.Data["codex"].Runs.Count==runs&&f.Session.Messages.Count==messages&&f.Session.ThreadId==thread,"connection action checks the official-provider abstraction without submitting or replacing an execution");
            Check(f.Input.Text=="Keep this unsent draft"&&send.Visibility==Visibility.Visible&&send.IsEnabled&&action.Visibility==Visibility.Collapsed,"connection success keeps the draft and restores explicit send");
            f.Set("workflowPhase","review");f.Call("Enable");Check(((Border)f.Get("workProgress")).Visibility==Visibility.Collapsed,"connection-only metadata updates never leave a conflicting model/answer checklist in a restored task");
            f.Connect("claude",false);change.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Drain();
            Check((bool)f.Get("handoffEditing")&&f.Data["claude"].Runs.Count==0&&f.Session.ThreadId==thread&&f.Input.Text=="Keep this unsent draft","nearby model-change action opens the existing review panel without sending or losing the draft");
            Descendants<Button>((Border)f.Get("handoffPanel")).Single(b=>Object.Equals(b.Content,"닫기")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Drain();
            f.Set("busy",true);f.Call("Enable");Check(!change.IsEnabled&&!send.IsEnabled,"model-change shortcut cannot bypass the in-flight execution guard");f.Set("busy",false);
            f.Set("storageBlocked",true);f.Call("Enable");Check(!change.IsEnabled&&!send.IsEnabled,"model-change shortcut cannot bypass a blocked save");f.Set("storageBlocked",false);
            StudioSessions.Active(f.Session).DeliveryState="awaitingVerification";f.Call("Enable");Check(!change.IsEnabled&&!send.IsEnabled,"uncertain delivery also blocks the new model-change shortcut");StudioSessions.Active(f.Session).DeliveryState="idle";
            f.Session.Route=new StudioRouteRecord{Engine="openai-decisions",Task="coding",Intent="coding",PreferredProvider="codex",SelectionReason="task_preference",Provider="codex",Model="codex-model",PromptHash=StudioRouteInput.Hash("fixture"),CreatedUtc=DateTime.UtcNow.AddHours(-1).ToString("o"),Probability=.95,Confidence=.95,Remaining=42,SelectionMode=StudioRoutingPolicy.TaskAffinity,Workload="balanced",AllowanceBand="healthy",PoolCandidates=2,WorkloadCandidates=2,SelectedBandCandidates=2};f.Call("Enable");
            Check(!((Expander)f.Get("quotaDisclosure")).IsExpanded&&!((Expander)f.Get("routeDisclosure")).IsExpanded&&!((Expander)f.Get("scopeDisclosure")).IsExpanded,"technical quota, decision and scope sections begin collapsed");
            Check(((TextBlock)f.Get("detailRoute")).Text.Contains("코드 작성")&&!((TextBlock)f.Get("detailRoute")).Text.Contains("95%"),"default selection explanation names the task without flooding the summary with classifier scores");
            Check(((TextBlock)f.Get("detailRouteHistory")).Text.Contains("42%")&&((TextBlock)f.Get("detailRouteHistory")).Text.Contains("선택 당시")&&((TextBlock)f.Get("quotaSummary")).Text.Contains("73%"),"historical selection allowance is explicitly distinguished from the current provider observation");
            ((Expander)f.Get("routeDisclosure")).IsExpanded=true;Drain();Check(((TextBlock)f.Get("detailRouteAdvanced")).Text.Contains("95%")&&((TextBlock)f.Get("detailRouteAdvanced")).Text.Contains("후보"),"full decision confidence and candidate evidence remain available on demand");
            f.Call("ClearQuota");Check(!((TextBlock)f.Get("quotaSummary")).Text.Contains("42%")&&!((TextBlock)f.Get("detailQuotaCaption")).Text.Contains("42%"),"unknown live allowance is never replaced by a historical decision percentage");
            f.Session.Route=null;f.Call("NewSession");f.Call("Enable");Check(((WrapPanel)f.Get("composerSelectors")).Visibility==Visibility.Visible&&((WrapPanel)f.Get("composerPinnedModel")).Visibility==Visibility.Collapsed,"a new conversation retains the original manual/automatic provider and model controls");
        }
        foreach(var theme in new[]{"dark","light"})foreach(bool english in new[]{false,true}){
            L.Language=english?"en":"ko";
            using(var f=new Fixture(Path.Combine(root,theme+(english?"-en":"-ko")),theme,english?"en":"ko")){
            f.Connect("codex",true);f.SelectModel("codex-model");f.Send("Pinned connection layout fixture");f.Input.Text="preserved draft";f.Call("SaveCurrent");f.Restart();Drain();
            Check(((TextBlock)f.Get("composerModelText")).Text.Contains(english?"Pinned":"고정")&&((string)((Button)f.Get("composerAction")).Content).Contains(english?"Connect":"연결 확인"),"layout fixtures use actual localized connection and pinned-model labels");
            foreach(var size in new[]{new[]{1180,800},new[]{720,500}})foreach(bool details in new[]{false,true}){
                f.Call("SetDetailsOpen",details);Capture(f,Path.Combine(root,theme+(english?"-en":"-ko")+"-"+size[0]+(details?"-details":"")+".png"),size[0],size[1]);var view=(FrameworkElement)f.Window.Content;
                foreach(var name in new[]{"composerAction","composerHandoff","composerModelText"}){var control=(FrameworkElement)f.Get(name);var bounds=ScreenBounds(control,view);Check(control.Visibility==Visibility.Visible&&bounds.Left>=0&&bounds.Right<=size[0]+1&&bounds.Top>=0&&bounds.Bottom<=size[1]+1,"connection and model-change controls fit "+theme+" "+english+" "+size[0]+" details="+details);}
                if(details){var bounds=ScreenBounds((FrameworkElement)f.Get("handoffOpen"),view);Check(bounds.Bottom<=size[1]+1,"default details keep the model-change action above the fold without reading technical sections");foreach(var name in new[]{"quotaDisclosure","routeDisclosure","scopeDisclosure"}){var disclosure=(Expander)f.Get(name);Check(Descendants<ToggleButton>(disclosure).Any(b=>AutomationProperties.GetName(b)==Convert.ToString(disclosure.Header)),"detail disclosure exposes its purpose to keyboard and screen-reader navigation");}}
            }
            }
        }
        L.Language="ko";
    }
    static void WorkspaceInteraction(string root)
    {
        Directory.CreateDirectory(root);
        using(var f=new Fixture(root)){
            f.Connect("codex",true);f.SelectModel("codex-model");f.Input.Text="preserved draft with selected text";f.Input.Select(10,5);f.Call("CaptureDraftView");f.Call("SaveCurrent");string first=f.Session.Id;
            f.Call("NewSession");f.Input.Text="second draft";f.Call("SaveCurrent");var list=(ListBox)f.Get("list");list.SelectedItem=((List<StudioSession>)f.Get("sessions")).Single(s=>s.Id==first);Drain();
            Check(f.Input.Text=="preserved draft with selected text"&&f.Input.SelectionStart==10&&f.Input.SelectionLength==5,"switching conversations restores draft and caret selection");
            ((CheckBox)f.Get("motionPreference")).IsChecked=false;f.Call("SetDetailsOpen",true);
            foreach(var size in new[]{new[]{1180,800},new[]{720,500}}){
                Capture(f,Path.Combine(root,"details-"+size[0]+".png"),size[0],size[1]);var view=(FrameworkElement)f.Window.Content;
                var details=(Border)f.Get("connectionDetails");var rect=ScreenBounds(details,view);var editor=ScreenBounds(f.Input,view);var send=ScreenBounds((Button)f.Get("send"),view);
                Check(rect.Left>=0&&rect.Right<=size[0]+1&&rect.Top>=0&&rect.Bottom<=size[1]+1,"details stay within "+size[0]+"x"+size[1]);
                Check(editor.Left>=0&&editor.Right<=size[0]+1&&send.Bottom<=size[1]+1,"composer and send remain inside the window with details open");
                if(size[0]>=1100)Check(ScreenBounds((FrameworkElement)f.Get("workspaceMain"),view).Right<=rect.Left+1,"wide details dock beside rather than over the conversation");
                else Check(((Border)f.Get("drawerShade")).Visibility==Visibility.Visible&&KeyboardNavigation.GetTabNavigation(details)==KeyboardNavigationMode.Cycle,"narrow details use a dismissible overlay with contained tab navigation");
                var close=(Button)f.Get("closeDetails");var closeBounds=ScreenBounds(close,view);
                Check(close.IsEnabled&&close.Visibility==Visibility.Visible&&close.ActualWidth>20&&closeBounds.Left>=rect.Left&&closeBounds.Right<=rect.Right+1&&closeBounds.Bottom<=size[1],"details retain a visible reachable close action even after effects were disabled while the panel was hidden");
            }
            f.Restart();Drain();Check(((CheckBox)f.Get("motionPreference")).IsChecked==false&&(bool)f.Get("detailsOpen"),"motion and details preferences survive window recreation");
            Check(f.Input.Text=="preserved draft with selected text"&&f.Input.SelectionStart==10&&f.Input.SelectionLength==5,"restart restores the draft selection without rewriting the draft");
            f.Call("SetDetailsOpen",false);f.Connect("codex",true);f.SelectModel("codex-model");f.Latest["codex"].HoldNextRun=true;f.Input.Text="Stream while reading prior content";f.Call("Enable");var sending=(Task)f.Call("Send");Drain();
            Check(!sending.IsCompleted&&(string)f.Get("workflowPhase")=="streaming"&&((Border)f.Get("workProgress")).Visibility==Visibility.Visible,"real provider events drive the streaming stage while the response is pending");
            Check(!((TextBox)f.Get("input")).IsEnabled&&((Button)f.Get("stop")).IsEnabled,"presentation changes preserve busy input and stop guards");
            Pump((Task)f.Call("Stop"));Pump(sending);Check((string)f.Get("workflowPhase")=="cancelled"&&f.Session.Messages.Last().State=="interrupted","provider-confirmed stop determines the final phase");
            f.Call("SetDetailsOpen",true);Check(((TextBlock)f.Get("detailRoute")).Text.Length>0,"selection explanation is available in the details panel");
        }
    }
    static void RenameFlow(string root)
    {
        Directory.CreateDirectory(root);
        using(var f=new Fixture(Path.Combine(root,"flow"))){
            f.Connect("codex",true);f.SelectModel("codex-model");f.Send("Synthetic source conversation for rename QA.");f.Input.Text="Unsent rename draft stays separate.";f.Call("SaveCurrent");
            string id=f.Session.Id,title=f.Session.Title,draft=f.Input.Text,thread=f.Session.ThreadId;
            string messages=String.Join("\n",f.Session.Messages.Select(m=>m.Id+":"+m.Role+":"+m.Text+":"+m.State));int countBefore=((List<StudioSession>)f.Get("sessions")).Count;
            byte[] before=File.ReadAllBytes(f.FilePath);
            f.Call("RenameConversation");var editor=(TextBox)f.Get("renameInput");var panel=(Border)f.Get("renamePanel");var save=(Button)f.Get("renameSave");
            Check((bool)f.Get("renameEditing")&&panel.Visibility==Visibility.Visible&&editor.Text==title,"rename opens inside the existing ADE with the current title");
            Check(!((Button)f.Get("fresh")).IsEnabled&&!((ListBox)f.Get("list")).IsEnabled&&!f.Input.IsEnabled&&f.Input.Text==draft,"rename modal locks navigation without altering the unsent draft");
            foreach(var invalid in new[]{"","   ","\r\n\t","title\nsecond line",new string('x',121)}){
                editor.Text=invalid;f.Call("CommitRename");Check((bool)f.Get("renameEditing")&&!save.IsEnabled&&f.Session.Title==title&&File.ReadAllBytes(f.FilePath).SequenceEqual(before),"invalid rename is not saved or closed: "+invalid.Length);
            }
            editor.Text="Cancelled title";f.Call("CancelRename");
            Check(!(bool)f.Get("renameEditing")&&panel.Visibility==Visibility.Collapsed&&f.Session.Title==title&&File.ReadAllBytes(f.FilePath).SequenceEqual(before),"cancel discards only the title editor and preserves the exact conversation file");
            Check(((Button)f.Get("fresh")).IsEnabled&&((Button)f.Get("sidebarToggle")).IsEnabled&&((Button)f.Get("detailsToggle")).IsEnabled&&((Button)f.Get("screenSettings")).IsEnabled&&f.Input.IsEnabled,"cancel restores previously available navigation and composer controls");
            f.Call("RenameConversation");editor.Text="  ADE 이름 변경 QA · 저장 완료  ";f.Call("NewSession");f.Call("OpenHandoff");f.Call("OpenComposerModelMenu");
            Check(f.Session.Id==id&&!((bool)f.Get("handoffEditing"))&&((List<StudioSession>)f.Get("sessions")).Count==countBefore,"new-task and context/model entry points cannot abandon a pending rename");
            f.Window.Close();Check(!(bool)f.Get("closed")&&(bool)f.Get("renameEditing")&&((TextBlock)f.Get("renameNotice")).Text.Contains("취소"),"window close asks for save or cancel while a title edit is pending");
            f.Call("CommitRename");
            Check(!(bool)f.Get("renameEditing")&&f.Session.Title=="ADE 이름 변경 QA · 저장 완료"&&f.Session.TitleIsCustom&&f.Session.Id==id&&f.Session.ThreadId==thread&&f.Input.Text==draft,"successful rename trims and persists only title metadata for the same task");
            Check(String.Join("\n",f.Session.Messages.Select(m=>m.Id+":"+m.Role+":"+m.Text+":"+m.State))==messages&&f.Data["codex"].Runs.Count==1,"rename neither changes messages nor submits a provider request");
            f.Restart();
            Check(f.Session.Id==id&&f.Session.Title=="ADE 이름 변경 QA · 저장 완료"&&f.Session.TitleIsCustom&&f.Input.Text==draft&&f.Session.ThreadId==thread,"saved title, draft, task and execution survive full window recreation");
            f.Call("RenameConversation");editor=(TextBox)f.Get("renameInput");save=(Button)f.Get("renameSave");title=f.Session.Title;bool custom=f.Session.TitleIsCustom;before=File.ReadAllBytes(f.FilePath);
            Check(editor.Text==title,"reopening rename reads the persisted current title");
            using(var locked=new FileStream(f.FilePath,FileMode.Open,FileAccess.Read,FileShare.None)){
                editor.Text="이름 저장 실패 후 복구";f.Call("CommitRename");
                Check((bool)f.Get("renameEditing")&&((Border)f.Get("renamePanel")).Visibility==Visibility.Visible&&editor.Text=="이름 저장 실패 후 복구","failed title save keeps the exact candidate and editor open");
                Check((bool)f.Get("storageBlocked")&&f.Session.Title==title&&f.Session.TitleIsCustom==custom&&((TextBlock)f.Get("renameNotice")).Text.Contains("저장"),"failed atomic save restores the displayed original title and exposes recovery guidance");
                Check(save.IsEnabled&&!((Button)f.Get("fresh")).IsEnabled&&!f.Input.IsEnabled&&f.Input.Text==draft,"save retry remains reachable while all unrelated task changes stay blocked");
            }
            Check(File.ReadAllBytes(f.FilePath).SequenceEqual(before),"failed rename preserves the previous encrypted conversation bytes");f.Call("CommitRename");
            Check(!(bool)f.Get("renameEditing")&&!(bool)f.Get("storageBlocked")&&f.Session.Title=="이름 저장 실패 후 복구"&&f.Input.Text==draft,"unlock and retry saves the pending title without asking the user to type it again");
            f.Restart();Check(f.Session.Title=="이름 저장 실패 후 복구"&&f.Session.Id==id&&f.Input.Text==draft&&f.Session.Messages.Count==2,"recovered rename also survives restart with the same conversation content");
        }
        foreach(var theme in new[]{"dark","light"})foreach(bool english in new[]{false,true}){
            using(var f=new Fixture(Path.Combine(root,theme+(english?"-en":"-ko")),theme,english?"en":"ko")){
                f.Input.Text="original draft";f.Call("SaveCurrent");f.Call("RenameConversation");var editor=(TextBox)f.Get("renameInput");editor.Text=english?"Saved custom title":"저장한 대화 이름";
                foreach(var size in new[]{new[]{1180,800},new[]{720,500}}){Capture(f,Path.Combine(root,theme+(english?"-en":"-ko")+"-"+size[0]+".png"),size[0],size[1]);var view=(FrameworkElement)f.Window.Content;var shell=ScreenBounds((Border)f.Get("renamePanel"),view);var box=ScreenBounds(editor,view);var button=ScreenBounds((Button)f.Get("renameSave"),view);Check(shell.Left>=0&&shell.Right<=size[0]+1&&shell.Top>=0&&shell.Bottom<=size[1]+1&&box.Left>=shell.Left&&box.Right<=shell.Right&&button.Bottom<=shell.Bottom,"rename panel, editor and save fit "+theme+" "+english+" "+size[0]+"x"+size[1]);}
                double editorWidth=((Border)f.Get("renamePanel")).ActualWidth;editor.Text="x";f.Window.UpdateLayout();Check(Math.Abs(((Border)f.Get("renamePanel")).ActualWidth-editorWidth)<1&&editorWidth>=440,"typing a short title does not shrink or move the rename dialog: "+theme+" "+english);editor.Text=english?"Saved custom title":"저장한 대화 이름";
                f.Call("CommitRename");string title=f.Session.Title;f.Input.Text="edited after custom naming";f.Call("SaveCurrent");f.Restart();Check(f.Session.Title==title&&f.Session.TitleIsCustom&&f.Input.Text=="edited after custom naming","custom title is not replaced by editing a pre-send draft: "+theme+" "+english);
            }
        }
        L.Language="ko";
    }
    static void MainScreenContract(string root)
    {
        Directory.CreateDirectory(root);
        using(var f=new Fixture(root)){
            f.Connect("codex",true);f.Connect("claude",false);f.Session.AutoRouting=true;f.Call("RestoreModelSelection");Check(!(bool)f.Get("modelNoticeActive")&&!((TextBlock)f.Get("status")).Text.Contains("모델을 직접 선택"),"automatic start does not inherit a stale manual-model notice");Check(((TextBlock)f.Get("providerDisclosure")).Text.Contains("분류한 뒤 선택된 AI"),"automatic first-send disclosure names classification and the selected AI rather than only the inspected provider");f.Session.AutoRouting=false;f.Call("Enable");f.SelectModel("codex-model");f.Input.Text="MAIN-UI: this unsent draft belongs to the source conversation.";f.Call("SaveCurrent");
            string id=f.Session.Id,draft=f.Input.Text;
            Check((bool)f.Get("centeringComposer")&&((StackPanel)f.Get("composerBottom")).Parent==(Grid)f.Get("startCenter"),"new work places the existing composer in the centered start layout");
            ((List<Button>)f.Get("starterButtons"))[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(f.Input.Text==draft&&f.Data.Values.All(d=>d.Runs.Count==0),"starter actions never overwrite an existing draft or submit a question");
            Check(((TextBlock)f.Get("newModelCaption")).Text.Contains("codex-model")&&((Button)f.Get("newModelButton")).IsEnabled,"compact model control reflects the selected real catalog entry");
            f.Window.Left=-20000;f.Window.Top=-20000;f.Window.WindowStartupLocation=WindowStartupLocation.Manual;f.Window.Show();Drain();
            f.Call("OpenComposerModelMenu");var menu=(ContextMenu)f.Get("activeMainMenu");
            Check(menu.IsOpen&&f.Data.Values.All(d=>d.Runs.Count==0)&&f.Input.Text==draft,"opening the model menu neither submits nor rewrites a draft");menu.IsOpen=false;
            Check(!(bool)f.Call("SelectComposerModel","claude","removed-model")&&f.Session.PreferredProvider=="codex"&&f.Session.Model=="codex-model","a stale model-menu choice cannot clear or replace the original provider/model");
            Check((bool)f.Call("SelectComposerModel","claude","claude-model")&&f.Session.Id==id&&f.Session.PreferredProvider=="claude"&&f.Session.Model=="claude-model"&&f.Input.Text==draft,"compact selection delegates to persisted provider/model controls without creating a new conversation or losing its draft");
            f.Restart();Check(f.Session.Id==id&&f.Session.Model=="claude-model"&&f.Input.Text==draft,"compact model selection and exact draft survive recreation");
            f.Connect("claude",true);f.Call("Enable");Check(((TextBlock)f.Get("detailQuotaValue")).Text.Contains("확인 필요"),"unavailable Claude allowance stays unknown in the new large value");
            var rows=(Dictionary<string,TextBlock>)f.Get("sidebarAllowances");Check(!rows["claude"].Text.Contains("73"),"sidebar never borrows Codex allowance for Claude");
            f.Connect("codex",false);Check((bool)f.Call("SelectComposerModel","codex","codex-model"),"connected second provider remains selectable before the first send");Drain();f.Call("UpdateMainQuotas");Check(((TextBlock)f.Get("detailQuotaValue")).Text=="73%"&&rows["codex"].Text=="73%","selecting a connected provider refreshes the bound allowance and updates both compact surfaces");
            f.Set("authenticated",false);f.Call("UpdateMainQuotas");Check(!rows["codex"].Text.Contains("73")&&!((TextBlock)f.Get("detailQuotaValue")).Text.Contains("73"),"losing authentication clears both compact allowance surfaces immediately");f.Set("authenticated",true);
            f.Send("MAIN-UI: synthetic main-screen contract question.");
            Check(!(bool)f.Get("centeringComposer")&&((StackPanel)f.Get("composerBottom")).Parent==(Grid)f.Get("workspaceMain"),"an established conversation returns the same composer to its bottom location");
            string thread=f.Session.ThreadId;f.Input.Text=draft;f.Call("SaveCurrent");f.Call("OpenComposerModelMenu");menu=(ContextMenu)f.Get("activeMainMenu");
            Check(menu.Items.Count==2&&!((MenuItem)menu.Items[0]).IsEnabled&&((MenuItem)menu.Items[1]).IsEnabled,"pinned model menu offers context review instead of silently selecting another model");menu.IsOpen=false;
            Check(!(bool)f.Call("SelectComposerModel","claude","claude-model")&&f.Session.ThreadId==thread&&f.Input.Text==draft,"direct compact-menu selection cannot bypass a pinned conversation");
            foreach(var guard in new[]{"busy","connecting","storageBlocked","handoffEditing","routingInProgress"}){f.Set(guard,true);f.Call("OpenComposerModelMenu");Check(!menu.IsOpen&&f.Input.Text==draft&&f.Session.ThreadId==thread,"compact menu preserves execution/draft while blocked by "+guard);f.Set(guard,false);}
            f.Set("handoffEditing",true);f.Call("NewSession");Check(f.Session.Id==id&&f.Input.Text==draft&&f.Session.ThreadId==thread,"new-conversation keyboard path cannot abandon an active context review");f.Set("handoffEditing",false);
            f.Call("FocusConversationSearch");Check(((Grid)f.Get("searchHost")).Visibility==Visibility.Visible,"Ctrl+K reveals the inline search field before focusing it");
            ((TextBox)f.Get("search")).Text="no matching fixture";f.Call("CloseConversationSearch");Check(((Grid)f.Get("searchHost")).Visibility==Visibility.Collapsed&&((TextBox)f.Get("search")).Text==""&&f.Input.Text==draft,"closing inline search clears only the filter and preserves the conversation draft");
            var footerMenu=(ContextMenu)f.Call("MainMenu",(Button)f.Get("connectionOverview"));Check(footerMenu.Placement==PlacementMode.Top,"footer menus open above their action instead of beneath the app");var titleMenu=(ContextMenu)f.Call("MainMenu",(Button)f.Get("threadMenu"));Check(titleMenu.Placement==PlacementMode.Custom&&titleMenu.CustomPopupPlacementCallback!=null,"title menu uses a bounded right-aligned popup placement");
            foreach(var size in new[]{new[]{1180,800},new[]{720,500}}){Capture(f,Path.Combine(root,"main-contract-"+size[0]+".png"),size[0],size[1]);var view=(FrameworkElement)f.Window.Content;foreach(var name in new[]{"input","composerModelButton","composerHandoff","threadMenu","sidebarToggle"}){var element=(FrameworkElement)f.Get(name);var bounds=ScreenBounds(element,view);Check(bounds.Left>=0&&bounds.Right<=size[0]+1&&bounds.Top>=0&&bounds.Bottom<=size[1]+1,"main screen keeps "+name+" within "+size[0]+"x"+size[1]);}}
        }
    }
    static void MotionBehavior()
    {
        var previous=StudioMotion.SystemPreferenceOverride;bool enabled=true;var host=new Window{Width=220,Height=140,Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};
        var grid=new Grid();var column=new ColumnDefinition{Width=new GridLength(160)};grid.ColumnDefinitions.Add(column);grid.ColumnDefinitions.Add(new ColumnDefinition());var body=new Border{Child=new TextBlock{Text="Visible content",Height=50}};grid.Children.Add(body);host.Content=grid;
        var motion=new StudioMotion(()=>enabled);
        try{
            StudioMotion.SystemPreferenceOverride=()=>true;host.Show();host.UpdateLayout();motion.Enter(body);
            Check(body.HasAnimatedProperties&&((TextBlock)body.Child).Text=="Visible content","enter motion starts without deferring content availability");
            motion.Show(body,false);motion.Show(body,true);Pump(Task.Delay(240));Check(body.Visibility==Visibility.Visible&&Math.Abs(body.Opacity-1)<.001,"rapid reopening cannot be hidden by an old closing clock");
            motion.Column(column,0);enabled=false;motion.StopAll();host.UpdateLayout();Check(!column.HasAnimatedProperties&&column.Width.Value==0,"disabling effects immediately settles an active sidebar width transition");
            motion.Fold(body,false);Check(body.Visibility==Visibility.Collapsed,"folding works immediately with effects disabled");
            motion.Fold(body,true);Check(body.Visibility==Visibility.Visible&&!body.HasAnimatedProperties,"expansion retains content and semantics with effects disabled");
            enabled=true;StudioMotion.SystemPreferenceOverride=()=>false;motion.Enter(body);Check(!body.HasAnimatedProperties,"Windows reduced-motion preference overrides enabled app effects");
            var button=new Button{Content="Unavailable",Opacity=.48,IsEnabled=false};grid.Children.Add(button);motion.Feedback(button);motion.StopAll();Check(Math.Abs(button.Opacity-.48)<.001,"settling motion does not erase disabled-control opacity");
            StudioMotion.SystemPreferenceOverride=()=>true;motion.Fold(body,false);Pump(Task.Delay(240));Check(body.Visibility==Visibility.Collapsed&&!body.HasAnimatedProperties,"animated fold completes at the collapsed layout state");
            motion.Fold(body,true);Pump(Task.Delay(240));Check(body.Visibility==Visibility.Visible&&Double.IsNaN(body.Height),"expanded disclosure returns to automatic layout after motion");
        }finally{motion.Dispose();host.Close();StudioMotion.SystemPreferenceOverride=previous;}
    }
    static void DraftAndPlaceholderRegression(string root)
    {
        using(var f=new Fixture(Path.Combine(root,"c"))){
            f.Session.Project=null;f.Input.Text="Synthetic draft to erase";
            Check((bool)f.Call("SaveCurrent")&&File.Exists(f.FilePath),"draft is persisted before clearing");
            f.Input.Clear();Check((bool)f.Call("SaveCurrent")&&!(bool)f.Get("dirty"),"clearing persisted draft completes a real save");
            f.Restart();Check(f.Input.Text==""&&f.Session.Draft=="","cleared draft stays empty after reopening the real window");
        }
        using(var f=new Fixture(Path.Combine(root,"l"))){
            f.Session.Project=null;f.Input.Text="Keep until cleared save succeeds";f.Call("SaveCurrent");var original=File.ReadAllBytes(f.FilePath);f.Input.Clear();
            using(File.Open(f.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read)){
                Check(!(bool)f.Call("SaveCurrent")&&(bool)f.Get("storageBlocked"),"clearing a locked draft reports storage failure rather than false success");
                Check(original.SequenceEqual(File.ReadAllBytes(f.FilePath))&&f.Input.Text=="","failed clearing preserves previous file and current empty editor");
            }
            Check((bool)f.Call("SaveCurrent"),"explicit retry saves cleared draft after releasing lock");
            f.Restart();Check(f.Input.Text=="","cleared value survives restart after recovery");
        }
        using(var f=new Fixture(Path.Combine(root,"p"))){
            f.Session.Project=null;var id=f.Session.Id;
            for(int i=0;i<150;i++)f.Call("NewSession");
            Check(((List<StudioSession>)f.Get("sessions")).Count==1&&f.Session.Id==id,"repeated new-conversation clicks reuse the pristine placeholder");
            Check(!Directory.Exists(Path.GetDirectoryName(f.FilePath))||Directory.GetFiles(Path.GetDirectoryName(f.FilePath),"*.dat").Length==0,"empty placeholder does not create stored conversations");
            f.Input.Text="Keep this draft";f.Call("NewSession");
            Check(((List<StudioSession>)f.Get("sessions")).Count==2&&f.Session.Id!=id,"a nonempty draft gets a distinct new conversation");
            f.Restart();var restored=(List<StudioSession>)f.Get("sessions");
            Check(restored.Any(s=>s.Id==id&&s.Draft=="Keep this draft"),"placeholder reuse never discards an outgoing draft");
            f.Session.Project=null;f.Session.Draft="";f.Session.TitleIsCustom=true;f.Session.Title="Named empty conversation";f.Set("dirty",true);var named=f.Session.Id;f.Call("NewSession");
            Check(f.Session.Id!=named,"named empty conversations remain separate");
        }
    }
    static IEnumerable<TextBlock> RowText(DependencyObject node)
    {
        var text=node as TextBlock;if(text!=null)yield return text;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)foreach(var child in RowText(VisualTreeHelper.GetChild(node,i)))yield return child;
    }
    static void DeferredStatusSaveRegression(string root)
    {
        using(var f=new Fixture(root)){
            f.Connect("codex",true);f.SelectModel("codex-model");f.Send("Synthetic status save boundary.");
            Capture(f,Path.Combine(root,"status-before.png"),1180,800);
            var list=(ListBox)f.Get("list");var source=list.ItemsSource;var selected=f.Session;int collectionChanges=0;
            ((INotifyCollectionChanged)list.Items).CollectionChanged+=(s,e)=>collectionChanges++;
            using(list.Items.DeferRefresh()){
                selected.Archived=true;f.Set("dirty",true);
                Check((bool)f.Call("SaveCurrent")&&!(bool)f.Get("storageBlocked"),"deferred view cannot turn an archived disk save into storage failure");
            }
            int unreadable;var saved=((StudioStore)f.Get("store")).Load(out unreadable).Single(s=>s.Id==selected.Id);
            Check(saved.Archived&&list.SelectedItem==selected&&list.ItemsSource==source,"archive save preserves persisted state, selection and collection identity");
            Check(collectionChanges==0,"status presentation does not reset or regroup the conversation list");
            Check(RowText(list).Any(t=>t.Text.StartsWith("보관됨 · ")),"realized conversation status binding updates without a collection refresh");
            selected.Archived=false;f.Set("dirty",true);f.Call("SaveCurrent");f.Call("Render");
            f.Connect("claude",false);f.Prepare("claude","Reviewed context for deferred-view regression.");
            var frozen=(StudioHandoffRecord)f.Call("FreezeHandoff");f.Set("dirty",true);collectionChanges=0;
            using(list.Items.DeferRefresh()){
                Check((bool)f.Call("SaveCurrent")&&!(bool)f.Get("storageBlocked"),"prepared handoff save succeeds while WPF defers collection refresh");
            }
            saved=((StudioStore)f.Get("store")).Load(out unreadable).Single(s=>s.Id==selected.Id);
            Check(saved.Handoffs.Single(h=>h.Id==frozen.Id).State=="prepared"&&f.Session==selected,"prepared transfer stays persisted instead of rolling back after presentation update");
            Check(collectionChanges==0&&f.Data["claude"].Runs.Count==0,"status update neither resets the list nor submits an inference");
            Check(RowText(list).Any(t=>t.Text.StartsWith("전달문 검토 · ")),"handoff status binding updates in the existing row");
        }
    }
    [STAThread] static int Main()
    {
        var root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"../../outputs/studio-provider-tests/fixture-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        try{DraftAndPlaceholderRegression(Path.Combine(root,"dr"));DeferredStatusSaveRegression(Path.Combine(root,"dv"));Flow(root);Recovery(Path.Combine(root,"recovery"));UnmaterializedAndUnknownIdentity(Path.Combine(root,"unmaterialized"));ReviewGuards(Path.Combine(root,"review-guards"));ProviderPresentation(Path.Combine(root,"presentation"));ClaudeAllowancePresentation(Path.Combine(root,"claude-allowance"));ConnectionStatusRefresh(Path.Combine(root,"connection-status"));ComposerAndCriticalActions(Path.Combine(root,"composer"));SidebarNavigation(Path.Combine(root,"sidebar"));WorkspaceInteraction(Path.Combine(root,"workspace"));ConnectionFlowPresentation(Path.Combine(root,"connection-flow"));MainScreenContract(Path.Combine(root,"main-contract"));RenameFlow(Path.Combine(root,"rename"));MotionBehavior();PresentationRenders(Path.Combine(root,"ux-renders"));Console.WriteLine("PASS: "+count+" provider pool, cross-provider context, restart and UI readiness checks; isolated fake adapters, no authentication or network. "+root);return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
