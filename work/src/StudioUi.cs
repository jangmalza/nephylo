using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private StudioWindow studioWindow;
        private void OpenStudio()
        {
            if(exiting)return;
            if(studioWindow!=null){studioWindow.Show();studioWindow.WindowState=WindowState.Normal;studioWindow.Activate();return;}
            var window=new StudioWindow(settings.CodexPath,demo,MakeResources(),Back,Fore,FontFamily);
            ApplyManagementReadingStyles(window.Resources); ApplyDialogTheme(window);
            window.Owner=this; studioWindow=window; window.Closed+=(s,e)=>studioWindow=null;window.Show();
        }
    }
    internal sealed partial class StudioWindow : Window
    {
        readonly string executableOverride;readonly bool demoMode;readonly StudioStore store;
        readonly StudioDevelopmentProfile developmentProfile;
        readonly List<StudioSession> sessions=new List<StudioSession>();
        readonly ListBox list=new ListBox();readonly ComboBox models=new ComboBox();
        readonly TextBox input=new TextBox();
        StudioMessageView transcript;
        readonly TextBlock status=new TextBlock(),project=new TextBlock();
        readonly Button connect=new Button(),login=new Button(),send=new Button(),stop=new Button(),choose=new Button(),fresh=new Button();
        readonly WrapPanel saveRecovery=new WrapPanel { Visibility=Visibility.Collapsed, Margin=new Thickness(0,0,0,8) };
        IStudioAgentClient client;StudioSession selected;StudioMessage response;
        bool busy,connecting,closed,dirty,authenticated,cancelRequested,loginPending,renderPending,restoringDraft;
        bool restoringModel,catalogReady,storageBlocked,discardOnClose,modelNoticeActive,responseLimited;
        StudioFailureKind? displayedFailure;
        readonly Dictionary<string,string> answers=new Dictionary<string,string>();readonly List<string> answerOrder=new List<string>();
        readonly DispatcherTimer checkpoint=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
        readonly DispatcherTimer streamDisplay=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)};
        static string S(string ko,string en){return L.English?en:ko;}
        internal StudioWindow(string executableOverride,bool demo,ResourceDictionary resources,Brush background,Brush foreground,FontFamily font)
            : this(executableOverride,demo,resources,background,foreground,font,null) { }
        internal StudioWindow(string executableOverride,bool demo,ResourceDictionary resources,Brush background,Brush foreground,FontFamily font,StudioDevelopmentProfile development)
        {
            this.executableOverride=executableOverride;demoMode=demo;developmentProfile=development;
            Title=development==null?"Nephylo ADE":"Nephylo ADE · "+S("개발용","Development")+" ["+development.Name+"]";Width=1320;Height=880;MinWidth=720;MinHeight=500;
            var area=SystemParameters.WorkArea;Width=Math.Min(Width,area.Width-32);Height=Math.Min(Height,area.Height-32);MinWidth=Math.Min(MinWidth,Width);MinHeight=Math.Min(MinHeight,Height);
            Background=background;Foreground=foreground;FontFamily=font;Resources=new ResourceDictionary();Resources.MergedDictionaries.Add(resources);WindowStartupLocation=WindowStartupLocation.CenterOwner;UseLayoutRounding=true;Icon=AppIcon.CreateWindowIcon();
            store=new StudioStore(development==null?Path.Combine(SettingsStore.DataDirectory,"studio"):development.StudioDirectory);
            InitializeWorkspacePresentation();
            InitializeRouting();
            Build();
            try{if(!demo){int unreadable;sessions.AddRange(store.Load(out unreadable));if(unreadable>0)status.Text=S("일부 대화 파일을 읽지 못했습니다. 원본은 보존했습니다.","Some conversations could not be read. Original files are preserved.");}}
            catch{status.Text=S("대화 저장 폴더에 접근할 수 없습니다.","Conversation storage is unavailable.");connect.IsEnabled=false;}
            var workspace=workspaceState;
            sidebarCollapsed=workspace.SidebarCollapsed;
            if(workspace.IsUnreadable)sidebarToggle.ToolTip=S("이전 화면 배치를 읽지 못했습니다. 대화 원본은 보존됩니다.","Previous layout could not be read. Conversations are preserved.");
            var restore=sessions.FirstOrDefault(x=>x.Id==workspace.SelectedSessionId)??sessions.FirstOrDefault(x=>!x.Archived);
            if(restore!=null&&restore.Archived)archived.IsChecked=true;
            RefreshList();if(restore!=null)list.SelectedItem=restore;else NewSession();
            SizeChanged+=(s,e)=>ApplySidebar();ApplySidebar();
            if(String.IsNullOrEmpty(status.Text))status.Text=demo?S("미리보기에서는 로그인과 질문 전송을 실행하지 않습니다.","Preview does not sign in or send prompts."):S("사용할 AI 연결과 로그인을 확인한 뒤 모델과 프로젝트를 선택하세요.","Connect and sign in to your selected AI, then choose a model and project.");
            Enable();
            checkpoint.Tick+=(s,e)=>{if(!demoMode&&!storageBlocked&&dirty&&selected!=null&&!closed&&!SaveCurrent()){if(busy&&client!=null)client.Dispose();}};
            checkpoint.Start();
            streamDisplay.Tick+=(s,e)=>{if(renderPending&&!closed){renderPending=false;Render();}};streamDisplay.Start();
            Closing+=(s,e)=>{
                if(renameEditing&&!discardOnClose){e.Cancel=true;renameNotice.Text=S("이름 변경을 저장하거나 취소한 뒤 닫아 주세요.","Save or cancel the name change before closing.");renameInput.Focus();return;}
                if(activeMainMenu!=null)activeMainMenu.IsOpen=false;
                if(routingInProgress){routingCancellation.Cancel();e.Cancel=true;status.Text=S("자동 선택을 취소하고 있습니다. 완료 후 닫아 주세요.","Cancelling automatic selection. Close again after it stops.");return;}
                var previous=response==null?null:response.State;
                var execution=CurrentExecution();var previousDelivery=execution==null?null:execution.DeliveryState;
                if(response!=null&&response.State=="running"){response.State="unconfirmed";if(execution!=null)execution.DeliveryState="awaitingVerification";dirty=true;}
                if(!discardOnClose&&!SaveCurrent()){if(response!=null)response.State=previous;if(execution!=null)execution.DeliveryState=previousDelivery;e.Cancel=true;return;}
                CaptureWorkspaceView();SaveWorkspace();closed=true;checkpoint.Stop();streamDisplay.Stop();DisposeProviders();
            };
        }
        internal bool TryCloseForOwner(){Close();return closed;}
        Button Setup(Button b,string text,Action action)
        {
            b.Content=text;b.Style=StudioButtonStyle();b.Padding=new Thickness(12,7,12,7);b.Margin=new Thickness(0,0,8,0);b.MinHeight=34;
            b.Click+=(s,e)=>action();if(motion!=null)motion.Feedback(b);return b;
        }
        string RecoveryText(){return selected==null?"":String.Join("\n\n",selected.Messages.Select(m=>(m.Role=="user"?"You":StudioProviderCatalog.Name(m.Provider))+"\n"+(m.Text??"")))+"\n\n"+S("미전송 초안","Unsent draft")+"\n"+(selected.Draft??"")+String.Join("",selected.Handoffs.Where(h=>h.State=="draft").Select(h=>"\n\n"+S("문맥 전달 초안","Context draft")+"\n"+h.Payload));}
        void ShowFailure(Exception error)
        {
            var kind=StudioFailure.Classify(error);
            displayedFailure=kind;
            if(kind==StudioFailureKind.Authentication){authenticated=false;ClearQuota();}
            if(kind==StudioFailureKind.Model){restoringModel=true;models.SelectedItem=null;restoringModel=false;}
            status.Text=StudioFailure.Guidance(storageBlocked?StudioFailureKind.Storage:kind,L.English,activeProvider);Enable();
        }
        void RestoreModelSelection()
        {
            restoringModel=true;
            try{models.SelectedItem=selected==null?null:models.Items.Cast<StudioModel>().FirstOrDefault(m=>m.Id==selected.Model);}
            finally{restoringModel=false;}
            if(selected!=null&&selected.AutoRouting&&!ModelPinned()){
                models.ToolTip=null;if(modelNoticeActive&&!storageBlocked){status.Text="";modelNoticeActive=false;}
            }else if(authenticated&&catalogReady&&selected!=null&&models.SelectedItem==null){
                models.ToolTip=models.Items.Count==0?S("사용 가능한 모델 목록이 비어 있습니다. 선택한 AI를 다시 연결하세요.","The model list is empty. Reconnect the selected AI."):String.IsNullOrEmpty(selected.Model)?S("사용할 모델을 직접 선택하세요.","Select a model to use."):StudioFailure.Guidance(StudioFailureKind.Model,L.English,activeProvider);
                if(!storageBlocked){status.Text=(string)models.ToolTip;modelNoticeActive=true;}
            }else{models.ToolTip=null;if(modelNoticeActive&&authenticated&&!storageBlocked){status.Text=S("저장된 모델을 선택했습니다.","Saved model selected.");modelNoticeActive=false;}}
            Enable();
        }
        void ReplaceModels(IEnumerable<StudioModel> available)
        {
            restoringModel=true;
            try{models.Items.Clear();foreach(var m in available)models.Items.Add(m);catalogReady=true;}
            finally{restoringModel=false;}
            RestoreModelSelection();
        }
        static bool IsPristineConversation(StudioSession session)
        {
            return session!=null&&session.Messages.Count==0&&session.ProviderSessions.Count==0&&session.Handoffs.Count==0&&session.Route==null&&!session.TitleIsCustom&&!session.Archived&&String.IsNullOrEmpty(session.Draft)&&String.IsNullOrEmpty(session.Project)&&String.IsNullOrEmpty(session.Model);
        }
        bool SaveCurrent()
        {
            if(demoMode||selected==null)return true;
            if(!dirty&&!storageBlocked)return true;
            try{
                // Persist clearing an existing draft; only a never-saved empty
                // placeholder may skip writing a conversation file.
                if(!IsPristineConversation(selected)||store.HasSaved(selected))store.Save(selected);
            }
            catch{storageBlocked=true;dirty=true;saveRecovery.Visibility=Visibility.Visible;status.Text=StudioFailure.Guidance(StudioFailureKind.Storage,L.English);Enable();return false;}
            // A presentation exception must never relabel a successful disk write
            // as storage failure and cause archive/handoff state to be rolled back.
            dirty=false;storageBlocked=false;saveRecovery.Visibility=Visibility.Collapsed;Enable();return true;
        }
        void NewSession()
        {
            if(busy||connecting||routingInProgress||handoffEditing||renameEditing)return;
            if(!SaveCurrent())return;
            var activeProject=(projectFilter.SelectedItem as ProjectOption);
            if(IsPristineConversation(selected)&&(activeProject==null||activeProject.Path==null)){
                refreshingList=true;search.Clear();archived.IsChecked=false;refreshingList=false;
                RefreshList();list.SelectedItem=selected;Render();input.Focus();return;
            }
            if(sessions.Count>=100){status.Text=S("대화 목록 한도입니다. 저장 폴더를 정리한 뒤 다시 여세요.","Conversation limit reached. Manage your saved conversations and reopen Nephylo ADE.");return;}
            var session=new StudioSession{Title=S("새 대화","New conversation"),PreferredProvider=activeProvider,AutoRouting=routingSettings.AutoForNew,Project=activeProject!=null&&activeProject.Path!=null?activeProject.Path:selected==null?null:selected.Project};
            sessions.Insert(0,session);refreshingList=true;search.Clear();archived.IsChecked=false;refreshingList=false;RefreshList();list.SelectedItem=session;
        }
        void ChooseProject()
        {
            using(var picker=new Forms.FolderBrowserDialog{Description=S("이 대화의 프로젝트 폴더","Project folder for this conversation"),ShowNewFolderButton=false}){
                if(picker.ShowDialog()!=Forms.DialogResult.OK)return;
                if(selected==null)return;selected.Project=Path.GetFullPath(picker.SelectedPath);dirty=true;SaveCurrent();
                updatingProjects=true;projectFilter.SelectedIndex=0;updatingProjects=false;RefreshList();Render();
            }
        }
        void Enable()
        {
            connect.IsEnabled=!busy&&!connecting&&!handoffEditing&&!demoMode;login.IsEnabled=!busy&&!connecting&&!handoffEditing&&!loginPending&&client!=null&&client.Connected&&!demoMode;
            var picked=models.SelectedItem as StudioModel;
            send.IsEnabled=!busy&&!connecting&&!handoffEditing&&!storageBlocked&&authenticated&&catalogReady&&selected!=null&&!selected.Archived&&ClientMatchesSession()&&!NeedsAccountConfirmation()&&!DeliveryUncertain()&&!HasPreparedTransfer()&&Directory.Exists(selected.Project??"")&&picked!=null&&picked.Id==selected.Model&&models.Items.Contains(picked)&&!String.IsNullOrWhiteSpace(input.Text);
            send.ToolTip=send.IsEnabled?"Ctrl+Enter":selected!=null&&selected.Archived?S("보관을 해제하면 이어서 질문할 수 있습니다.","Restore this conversation to continue."):S("로그인·프로젝트 위치·모델 선택·저장 상태를 확인하세요.","Check sign-in, project, model selection and save status.");
            stop.IsEnabled=busy&&!cancelRequested;stop.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
            fresh.IsEnabled=!busy&&!connecting&&!handoffEditing;list.IsEnabled=fresh.IsEnabled;search.IsEnabled=fresh.IsEnabled;projectFilter.IsEnabled=fresh.IsEnabled;archived.IsEnabled=fresh.IsEnabled;
            models.IsEnabled=!busy&&!connecting&&!handoffEditing&&!ModelPinned()&&authenticated&&catalogReady&&selected!=null&&!selected.Archived;
            choose.IsEnabled=!busy&&!connecting&&!handoffEditing&&selected!=null&&!selected.Archived&&!ModelPinned();input.IsEnabled=!busy&&!handoffEditing&&selected!=null&&!selected.Archived;
            choose.ToolTip=selected!=null&&!String.IsNullOrEmpty(selected.ThreadId)?S("시작한 대화의 프로젝트는 고정됩니다. 다른 폴더에는 새 대화를 만드세요.","This conversation's project is fixed. Start a new conversation for another folder."):null;
            rename.IsEnabled=archive.IsEnabled=!busy&&!connecting&&!handoffEditing&&selected!=null;
            archive.Content=selected!=null&&selected.Archived?S("보관 해제","Restore"):S("보관","Archive");
            login.Visibility=!authenticated&&client!=null&&client.Connected&&!loginPending?Visibility.Visible:Visibility.Collapsed;
            cancelLogin.Visibility=loginPending?Visibility.Visible:Visibility.Collapsed;
            UpdateProviderPresentation();EnableTaskControls();UpdateHandoffButtons();UpdateComposerReadiness();
            EnableRoutingControls();UpdateWorkspacePresentation();
            EnableRenameControls();
        }
        void Render()
        {
            project.Text=selected==null?"":String.IsNullOrEmpty(selected.Project)?S("프로젝트 폴더를 선택하세요.","Choose a project folder."):ProjectName(selected.Project)+(Directory.Exists(selected.Project)?"":S(" · 폴더 확인 필요"," · Folder unavailable"));
            project.ToolTip=selected==null?null:selected.Project;heading.Text=selected==null?S("새 대화","New conversation"):selected.Title;heading.ToolTip=heading.Text;
            transcript.Update(selected);
            Enable();
        }
        void Ui(Action action){if(closed)return;try{Dispatcher.BeginInvoke(new Action(()=>{if(!closed)action();}));}catch{}}
        async Task Login()
        {
            if(activeProvider=="gemini-api"){await ConfigureGeminiApiKey();return;}
            if(activeProvider=="gemini-cli"){if(!busy&&!connecting)LaunchGeminiLogin();return;}
            if(activeProvider=="claude"){if(!busy&&!connecting)LaunchClaudeLogin();return;}
            var current=client;if(current==null||busy||connecting)return;
            connecting=true;loginPending=true;displayedFailure=null;Enable();
            try{var codex=current as StudioCodexClient;if(codex==null)throw new StudioFailure(StudioFailureKind.Compatibility);var url=await codex.LoginAsync();if(closed||client!=current)return;try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true});}catch{throw new StudioFailure(StudioFailureKind.Browser);}status.Text=S("브라우저에서 로그인을 완료하세요.","Complete sign-in in your browser.");}
            catch(Exception error){if(!closed&&client==current){loginPending=false;ShowFailure(error);}}
            finally{if(!closed&&client==current){connecting=false;Enable();}}
        }
        async Task Send(){if(await PrepareAutomaticRoute())await SendCore(null,null);}
        async Task SendCore(string transferredPrompt,StudioHandoffRecord handoff)
        {
            if(handoff==null&&!send.IsEnabled)return;
            if(busy||connecting||storageBlocked||selected==null||selected.Archived||client==null||!authenticated||!ClientMatchesSession()||NeedsAccountConfirmation()||DeliveryUncertain())return;
            var session=selected;var current=client;var prompt=transferredPrompt??input.Text.Trim();var model=models.SelectedItem as StudioModel;
            if(String.IsNullOrWhiteSpace(prompt)||model==null||model.Id!=session.Model)return;
            if(session.Messages.Count>=398){status.Text=S("새 대화를 시작하세요.","Start a new conversation.");return;}
            if(!workflowPrepared)BeginWorkflow(false);workflowPrepared=false;SetWorkflowPhase("sending");
            busy=true;cancelRequested=false;responseLimited=false;displayedFailure=null;Enable();answers.Clear();answerOrder.Clear();response=null;status.Text=S("대화 준비 중…","Preparing conversation…");
            StudioExecutionSession execution=null;bool submitted=false,createdExecution=false;
            var measuredRoute=handoff==null&&session.Route!=null&&session.Route.PromptHash==StudioRouteInput.Hash(prompt)&&session.Route.Provider==current.Provider&&session.Route.Model==model.Id?session.Route:null;
            System.Diagnostics.Stopwatch answerWatch=null;
            try{
                var beforeIdentity=current.AccountIdentity==null?null:current.AccountIdentity.Fingerprint;
                var account=await current.AccountAsync();if(closed||current!=client)return;
                if(StudioCodexClient.Obj(account,"account")==null||current.AccountIdentity==null||!current.AccountIdentity.IsAuthenticated){authenticated=false;throw new StudioFailure(StudioFailureKind.Authentication);}
                if(beforeIdentity!=current.AccountIdentity.Fingerprint||NeedsAccountConfirmation()){ClearQuota();status.Text=S("실행 계정이 변경되었거나 확인이 필요합니다. 계정을 확인한 뒤 다시 진행하세요.","The execution account changed or needs verification. Review the account before continuing.");return;}
                execution=StudioSessions.Active(session);
                if(execution==null){execution=StudioSessions.Start(session,current.Provider,current.Profile,model.Id,current.AccountIdentity.Fingerprint);createdExecution=true;dirty=true;if(!SaveCurrent()){session.ProviderSessions.Remove(execution);session.ActiveExecutionId=null;session.ThreadId=null;execution=null;return;}}
                if(execution.Provider!=current.Provider||execution.Profile!=current.Profile)throw new StudioFailure(StudioFailureKind.Authentication);
                executionConfirmations[execution.Id]=current.ConnectionId;
                session.Model=model.Id;
                // A Claude UUID is only materialized when its first prompt is sent. A
                // pre-send cancellation/save failure may persist an unused UUID across
                // restarts; allocate a fresh one only while this execution has no messages.
                var resumeId=session.ThreadId;
                if(current.Provider=="claude"&&!session.Messages.Any(m=>m.ExecutionId==execution.Id))resumeId=null;
                var replayClient=current as IStudioReplayClient;
                if(replayClient!=null&&!String.IsNullOrEmpty(resumeId))replayClient.PrepareReplay(resumeId,session.Messages.Where(m=>m.ExecutionId==execution.Id&&m.Provider==execution.Provider&&(m.Role=="user"||!String.IsNullOrEmpty(m.Text))).Select(m=>new StudioMessage{Id=m.Id,Role=m.Role,Text=m.Text,State=m.State,Provider=m.Provider,ExecutionId=m.ExecutionId,Model=m.Model}).ToList());
                session.ThreadId=await current.ThreadAsync(resumeId,session.Project,model.Id);
                if(closed)return;
                execution.ThreadId=session.ThreadId;dirty=true;
                if(cancelRequested){status.Text=S("중단했습니다.","Stopped.");return;}
                var contextClient=current as IStudioConversationClient;
                if(contextClient!=null)contextClient.PrepareHistory(session.ThreadId,StudioProviderCatalog.CompletedHistory(session,execution.Id));
                var oldTitle=session.Title;var oldDraft=session.Draft;int oldCount=session.Messages.Count;
                if(session.Messages.Count==0&&!session.TitleIsCustom)session.Title=prompt.Length>36?prompt.Substring(0,36)+"…":prompt;
                session.Messages.Add(new StudioMessage{Id=StudioSessions.NewId(),Role="user",Text=prompt,State="completed",Provider=current.Provider,ExecutionId=execution.Id,Model=model.Id});response=new StudioMessage{Id=StudioSessions.NewId(),Role="assistant",Text="",State="running",Provider=current.Provider,ExecutionId=execution.Id,Model=model.Id};session.Messages.Add(response);
                if(handoff==null)session.Draft="";execution.DeliveryState="running";if(handoff!=null)StudioHandoff.MarkAwaitingVerification(handoff);dirty=true;
                if(!SaveCurrent()){session.Messages.RemoveRange(oldCount,session.Messages.Count-oldCount);session.Title=oldTitle;session.Draft=oldDraft;execution.DeliveryState="idle";if(handoff!=null)handoff.State="prepared";response=null;return;}
                if(handoff==null)input.Clear();RefreshList();list.SelectedItem=session;Render();transcript.ResetScroll();
                status.Text=ProviderName(current.Provider)+S(" 응답 중…"," is responding…");
                submitted=true;
                if(measuredRoute!=null)answerWatch=System.Diagnostics.Stopwatch.StartNew();
                SetWorkflowPhase("answer");var result=await current.RunBoundAsync(session.ThreadId,session.Project,model.Id,prompt,beforeIdentity);
                if(closed)return;
                // Drain already queued delta events before persisting the completed message.
                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
                response.State=result=="completed"?"completed":result=="interrupted"?"interrupted":"failed";
                SetWorkflowPhase(result=="completed"?"completed":result=="interrupted"?"cancelled":"failed");
                execution.DeliveryState="idle";if(handoff!=null)StudioHandoff.MarkFinished(handoff,response.State);
                dirty=true;
                status.Text=result=="completed"?S("완료","Completed"):result=="interrupted"?S("중단했습니다.","Stopped."):ProviderName(current.Provider)+S(" 작업이 실패했습니다. 인증·이용 권한을 확인하세요."," failed. Check authentication and entitlement.");
                if(result=="completed"&&responseLimited)status.Text=S("응답 길이 한도에서 종료됐습니다. 받은 내용을 확인하고 필요하면 이어서 질문하세요.","The response reached its output limit. Review the partial answer and ask a follow-up if needed.");
                SaveCurrent();
            }catch(Exception error){
                var kind=StudioFailure.Classify(error);
                bool uncertain=submitted&&(kind==StudioFailureKind.Timeout||kind==StudioFailureKind.Disconnected||kind==StudioFailureKind.Network||kind==StudioFailureKind.Unknown);
                if(response!=null){response.State=uncertain?"unconfirmed":"failed";dirty=true;}
                if(execution!=null){execution.DeliveryState=uncertain?"awaitingVerification":"idle";dirty=true;}
                if(createdExecution&&!submitted&&execution!=null&&String.IsNullOrEmpty(execution.ThreadId)&&!session.Messages.Any(m=>m.ExecutionId==execution.Id)){
                    session.ProviderSessions.Remove(execution);session.ActiveExecutionId=null;session.ThreadId=null;execution=null;dirty=true;
                }
                if(handoff!=null&&handoff.State=="awaitingVerification"&&!uncertain)StudioHandoff.MarkFinished(handoff,"failed");
                if(!closed){ShowFailure(error);SetWorkflowPhase(uncertain?"review":"failed");SaveCurrent();}
            }finally{busy=false;response=null;if(!closed){if(workflowPhase=="sending"||workflowPhase=="answer"||workflowPhase=="streaming")SetWorkflowPhase(cancelRequested?"cancelled":"review");if(answerWatch!=null&&measuredRoute!=null){measuredRoute.AnswerMilliseconds=answerWatch.ElapsedMilliseconds;dirty=true;}if(dirty&&!storageBlocked)SaveCurrent();Render();Enable();if(authenticated&&current==client)Ui(async()=>await RefreshQuota(current));}}
        }
        async Task Stop()
        {
            if(routingInProgress){stop.IsEnabled=false;SetWorkflowPhase("stopping");status.Text=S("자동 선택 취소 중…","Cancelling automatic selection…");routingCancellation.Cancel();return;}
            if(!busy||client==null)return;cancelRequested=true;stop.IsEnabled=false;SetWorkflowPhase("stopping");status.Text=S("중단 요청 중…","Stopping…");
            try{await client.InterruptAsync();}catch{client.Dispose();}
        }
    }
}
