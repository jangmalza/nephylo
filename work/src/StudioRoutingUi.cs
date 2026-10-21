using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UsageWidget
{
    internal sealed partial class StudioWindow
    {
        readonly ComboBox routingMode=new ComboBox();
        readonly Button routingSettingsButton=new Button();
        readonly TextBlock routingSummary=new TextBlock();
        readonly DockPanel routingFooter=new DockPanel();
        StudioRoutingStore routingStore;
        StudioRoutingSettings routingSettings;
        bool routingInProgress,restoringRouting,routingStorageError;
        CancellationTokenSource routingCancellation;
        internal static Func<IStudioDecisionClient> DecisionClientFactoryOverride=null;

        void InitializeRouting()
        {
            routingStore=new StudioRoutingStore(store.RoutingHome);
            try{routingSettings=demoMode?new StudioRoutingSettings():routingStore.LoadSettings();}
            catch{routingSettings=new StudioRoutingSettings();routingStorageError=true;}
        }
        void BuildRoutingControls(Panel selectors,Panel composer)
        {
            routingMode.Items.Add(S("수동 선택","Manual"));routingMode.Items.Add(S("자동 선택","Automatic"));routingMode.MinWidth=105;routingMode.FontSize=12;routingMode.MinHeight=34;routingMode.Margin=new Thickness(0,0,8,0);
            AutomationProperties.SetName(routingMode,S("모델 선택 방식","Model selection mode"));selectors.Children.Add(routingMode);
            routingMode.SelectionChanged+=(s,e)=>{
                if(restoringRouting||selected==null)return;
                if(routingInProgress||busy||connecting||handoffEditing||ModelPinned()){UpdateRoutingPresentation();return;}
                bool before=selected.AutoRouting;selected.AutoRouting=routingMode.SelectedIndex==1;dirty=true;
                if(!SaveCurrent()){selected.AutoRouting=before;UpdateRoutingPresentation();return;}
                try{routingSettings.AutoForNew=selected.AutoRouting;if(!demoMode&&!routingStorageError)routingStore.SaveSettings(routingSettings);}catch{routingStorageError=true;}
                Enable();
            };
            Setup(routingSettingsButton,S("판단 엔진","Decision engine"),OpenRoutingSettings);routingSettingsButton.FontSize=11;routingSettingsButton.MinHeight=28;routingSettingsButton.Padding=new Thickness(7,4,7,4);routingSettingsButton.Margin=new Thickness(0,5,8,0);QuietButton(routingSettingsButton);
            AutomationProperties.SetName(routingSettingsButton,S("자동 라우팅 판단 엔진 설정","Automatic routing settings"));
            var row=routingFooter;row.Margin=new Thickness(0,4,0,0);DockPanel.SetDock(routingSettingsButton,Dock.Right);row.Children.Add(routingSettingsButton);
            routingSummary.FontSize=11;routingSummary.Foreground=muted;routingSummary.TextWrapping=TextWrapping.Wrap;routingSummary.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(routingSummary);composer.Children.Add(row);
        }
        void UpdateRoutingPresentation()
        {
            restoringRouting=true;routingMode.SelectedIndex=selected!=null&&selected.AutoRouting?1:0;restoringRouting=false;
            routingFooter.Visibility=selected!=null&&!ModelPinned()&&(selected.AutoRouting||selected.Route!=null)?Visibility.Visible:Visibility.Collapsed;
            if(selected==null)return;
            if(routingInProgress){routingSummary.Text=S("판단 엔진이 모델을 선택하고 있습니다…","The decision engine is choosing a model…");return;}
            var route=selected.Route;
            if(route!=null&&(!selected.AutoRouting||ModelPinned())){
                routingSummary.Text=S("자동 선택 · ","Automatic · ")+(route.Intent==null?TaskName(route.Task):IntentName(route.Intent))+(ModelPinned()?S(" · 모델 고정"," · Model pinned"):S(" · 선택 기록"," · Selection record"));
                routingSummary.ToolTip=RouteExplanation(route);return;
            }
            routingSummary.Text=selected.AutoRouting?StudioRoutingPolicy.EngineName(routingSettings.Engine)+(StudioRoutingPolicy.NormalizePolicy(routingSettings.Policy)==StudioRoutingPolicy.TaskAffinity?S(" · 질문 특성 우선"," · Task-aware preference"):S(" · 질문을 판단 서비스로 전송해 모델 선택"," · Send this question to the decision service to choose a model")):S("선택한 AI·모델로 전송합니다.","Send to the selected AI and model.");
            routingSummary.ToolTip=selected.AutoRouting?S("현재 질문만 판단 서비스로 보내 작업 유형·파일 필요 여부를 분류합니다. 모델 목록과 잔여량은 이 PC에서 처리하며 대화 전체·파일·환경변수·키는 첨부하지 않습니다. 선택 후 해당 작업의 모델은 고정됩니다.","Only this question goes to the decision service for task/file classification. Catalog and allowance stay local; no history, files, environment variables or keys are attached. The selected model stays pinned for this task."):null;
        }
        string RouteExplanation(StudioRouteRecord route)
        {
            string allowance=route.Remaining.HasValue?route.Remaining.Value.ToString("0.#")+"%":S("확인 불가","Unknown");
            string task=TaskName(route.Task);
            bool affinity=route.SelectionMode==StudioRoutingPolicy.TaskAffinity;
            bool taskPolicy=route.SelectionMode=="task-policy-v2"||affinity,grouped=taskPolicy||route.SelectionMode=="profile-v2";
            string selection=grouped?"\n"+S("작업 규모: ","Workload: ")+WorkloadName(route.Workload)+"\n"+S("후보 · 전체/작업군/잔량 구간: ","Candidates · pool/profile/allowance band: ")+route.PoolCandidates+" / "+route.WorkloadCandidates+" / "+route.SelectedBandCandidates+"\n"+S("확정 정책: 권한·작업군 → 잔량 구간 → 공급자·계열·세대 고정 순서","Selection policy: scope/profile → allowance band → stable provider/family/generation order"):"";
            if(affinity)selection="\n"+S("세부 요청: ","Requested output: ")+IntentName(route.Intent)+"\n"+S("작업 규모: ","Workload: ")+WorkloadName(route.Workload)+"\n"+S("후보 · 전체/작업군/잔량 구간: ","Candidates · pool/profile/allowance band: ")+route.PoolCandidates+" / "+route.WorkloadCandidates+" / "+route.SelectedBandCandidates+"\n"+S("질문별 선호 AI: ","Task-preferred AI: ")+ProviderName(route.PreferredProvider)+"\n"+S("선택 근거: ","Selection reason: ")+AffinityReason(route.SelectionReason)+"\n"+S("확정 정책: 권한·작업군 → 잔량 구간 → 질문별 선호 → 계열·세대","Selection policy: scope/profile → allowance band → task preference → family/generation");
            return S("작업 분류: ","Task: ")+task+selection+"\n"+(taskPolicy?S("작업 유형 분류 확률: ","Task classification probability: "):grouped?S("작업 규모 분류 확률: ","Workload classification probability: "):S("선택 확률: ","Choice probability: "))+route.Probability.ToString("P0")+(route.Confidence.HasValue?"\n"+S("엔진 확신도: ","Engine confidence: ")+route.Confidence.Value.ToString("P0"):"")+"\n"+S("선택 시 계정 잔여량: ","Observed account allowance: ")+allowance+S(" · 구간 "," · band ")+(route.AllowanceBand??S("이전 기록","legacy"))+"\n"+S("판단 시간: ","Decision time: ")+route.DecisionMilliseconds+" ms"+(route.AnswerMilliseconds.HasValue?"\n"+S("답변 시간: ","Answer time: ")+route.AnswerMilliseconds+" ms":"")+"\n"+S("확률은 분류값이며 모델 품질·성공률이 아닙니다. 금액·모델별 잔여량·품질 우위는 추정하지 않습니다.","Probabilities describe classification, not model quality or success rates. Monetary cost, model-specific allowance and quality superiority are not estimated.");
        }
        string IntentName(string value)
        {switch(value){case "coding":return S("코드 작성·구현","Code implementation");case "code_analysis":return S("코드·정형 데이터 분석","Code/structured technical data analysis");case "text_analysis":return S("텍스트 분석","Prose analysis");case "technical_planning":return S("기술 설계·계획","Technical design/planning");case "general_planning":return S("일반·콘텐츠 계획","Nontechnical/content planning");case "debugging":return S("오류 진단","Debugging");case "writing":return S("글쓰기","Writing");default:return S("일반 질문","General question");}}
        string TaskName(string task){return task=="debugging"?S("오류 진단","Debugging"):task=="coding"?S("코드 작성·구현","Code implementation"):task=="analysis"?S("분석","Analysis"):task=="writing"?S("글쓰기","Writing"):task=="planning"?S("설계·계획","Planning"):S("일반 질문","General question");}
        string AffinityReason(string value)
        {switch(value){case "task_preference":return S("같은 잔량 구간에서 질문별 선호 적용","Task preference within the same allowance band");case "allowance_precedence":return S("선호 AI보다 잔량 구간을 우선","Allowance band takes precedence over task preference");case "file_access_required":return S("프로젝트 파일을 읽을 수 있는 후보 선택","Selected a candidate able to inspect project files");default:return S("선호 AI가 후보 조건을 충족하지 않아 가능한 후보 선택","Preferred AI was ineligible; selected an eligible candidate");}}
        string WorkloadName(string value){return value=="quick"?S("간단한 작업","Quick work"):value=="deep"?S("복합 설계·분석","Deep planning/analysis"):S("글쓰기·코드·분석","Writing/code/analysis");}
        void EnableRoutingControls()
        {
            UpdateRoutingPresentation();bool idle=!busy&&!connecting&&!handoffEditing&&!routingInProgress;
            routingMode.IsEnabled=idle&&!storageBlocked&&selected!=null&&!selected.Archived&&!ModelPinned();
            routingSettingsButton.IsEnabled=idle&&!demoMode;
            if(selected!=null&&selected.AutoRouting&&!ModelPinned()){
                models.IsEnabled=false;
                modelPlaceholder.Text=S("자동 선택 대기","Automatic selection");
                providerPicker.ToolTip=S("연결 상태를 확인할 AI입니다. 실제 전송 모델은 첫 질문에서 자동 선택합니다.","AI connection to inspect. The actual model is selected automatically for the first question.");
                setupModel.Text=S("○ 모델 · 자동 선택","○ Model · Automatic selection");
                setupModel.ToolTip=S("첫 질문을 보낼 때 판단 엔진이 선택합니다.","The decision engine selects it when the first question is sent.");
                send.IsEnabled=!demoMode&&idle&&!storageBlocked&&!routingStorageError&&!selected.Archived&&!HasPreparedTransfer()&&!DeliveryUncertain()&&Directory.Exists(selected.Project??"")&&!String.IsNullOrWhiteSpace(input.Text);
                send.ToolTip=S("첫 질문을 판단 서비스로 분류한 뒤 선택된 AI에 한 번 전송합니다.","Classify the first question, then send it once to the chosen AI.");
            }
            else modelPlaceholder.Text=S("모델 선택","Choose model");
            if(routingInProgress){
                foreach(var control in new Control[]{connect,login,choose,fresh,list,search,projectFilter,archived,providerPicker,models,input,rename,archive,handoffOpen,quotaRefresh,apiKeySettings,send})control.IsEnabled=false;
                stop.Visibility=Visibility.Visible;stop.IsEnabled=routingCancellation!=null&&!routingCancellation.IsCancellationRequested;
            }
        }
        string RoutingFailureText(Exception error)
        {
            var failure=error as StudioRouteFailure;string kind=failure==null?"network":failure.Kind;
            if(error is OperationCanceledException)return S("자동 선택을 취소했습니다. 질문은 전송하지 않았습니다.","Automatic selection cancelled. No model question was sent.");
            switch(kind){
                case "key":return S("판단 엔진의 API 키를 설정하세요. 질문은 전송하지 않았습니다.","Configure the decision engine API key. No model question was sent.");
                case "authentication":return S("판단 엔진의 API 키·이용 권한을 확인하세요. 질문은 전송하지 않았습니다.","Check decision API credentials and access. No model question was sent.");
                case "endpoint":return S("Azure 리소스 주소와 Decision-1 배포 이름을 확인하세요.","Check the Azure resource endpoint and Decision-1 deployment name.");
                case "storage":return S("라우팅 설정을 저장하거나 읽지 못했습니다. 원본 파일과 질문은 보존했습니다.","Routing settings could not be saved/read. Original files and the question are preserved.");
                case "settings":return S("판단 엔진 또는 모델 선택 정책이 변경됐거나 확인되지 않았습니다. 설정을 다시 확인하세요. 질문은 전송하지 않았습니다.","The decision engine or selection policy changed or could not be verified. Review settings; no model question was sent.");
                case "candidate":return S("연결 가능한 모델이 없거나 연결·계정·잔여량이 변경됐습니다. 연결을 확인하거나 수동 선택하세요.","No eligible model, or connection/account/allowance changed. Check connections or choose manually.");
                case "review":
                    string subject=failure.Subject=="task"?S("작업 분류","Task classification"):failure.Subject=="files"?S("파일 필요 여부","File requirement"):S("자동 선택 판단","Routing classification");
                    string scores=failure.Probability.HasValue?S(" · 선택 확률 "," · choice probability ")+failure.Probability.Value.ToString("P0")+(failure.Confidence.HasValue?S(" · 확신도 "," · confidence ")+failure.Confidence.Value.ToString("P0"):""):"";
                    return subject+S("을 자동 확정하지 못했습니다"," could not be confirmed automatically")+scores+S(". 수동 선택해 주세요. 질문은 전송하지 않았습니다.",". Choose manually; no model question was sent.");
                case "scope":return S("프로젝트 파일이 필요한 작업에는 파일을 읽을 수 있는 모델을 선택하세요. 질문은 전송하지 않았습니다.","This task needs a model that can inspect project files. No model question was sent.");
                case "profile":return S("요청한 작업 규모에 맞는 연결 모델이 없습니다. 모델을 수동 선택해 주세요. 다른 작업군으로 자동 전환하지 않았습니다.","No connected model matches this workload. Select manually; no automatic switch to another workload was made.");
                case "rate_limit":return S("판단 서비스의 요청 한도입니다. 잠시 후 다시 시도하거나 수동 선택하세요. 자동 재시도하지 않습니다.","Decision service rate limit. Retry later or select manually. Nothing is retried automatically.");
                case "unavailable":return S("판단 서비스에 일시적인 오류가 있습니다. 질문을 보존했으며 자동 재시도하지 않습니다. 잠시 후 다시 시도하거나 수동 선택하세요.","The decision service is temporarily unavailable. The question is preserved and nothing is retried automatically. Retry later or select manually.");
                default:return S("판단 서비스 응답을 확인하지 못했습니다. 질문을 보존했으며 다른 엔진·모델로 자동 재전송하지 않습니다.","Could not verify the decision response. The question is preserved and is not automatically resent to another engine/model.");
            }
        }
        List<StudioRouteCandidate> RoutingCandidates()
        {
            var result=new List<StudioRouteCandidate>();
            foreach(var entry in providerConnections.OrderBy(x=>x.Key,StringComparer.Ordinal)){
                string provider=entry.Key;var slot=entry.Value;
                if(provider=="gemini-api"&&!routingSettings.IncludeGeminiApi||provider=="gemini-cli"&&!routingSettings.IncludeGeminiCli||!slot.Authenticated||!slot.CatalogReady||slot.Refreshing||slot.Client==null||!slot.Client.Connected||slot.Client.AccountIdentity==null||!slot.Client.AccountIdentity.IsAuthenticated||!slot.Client.AccountIdentity.Known)continue;
                foreach(var model in slot.Models.OrderBy(x=>x.Id,StringComparer.Ordinal)){
                    if(StudioQuotaParser.SafeText(model.Id,200)==null)continue;
                    var candidate=new StudioRouteCandidate{Id=StudioRouteInput.CandidateId(provider,model.Id),Provider=provider,Model=model.Id,Label=StudioQuotaParser.SafeText(model.Label,120)??model.Id,Connection=slot.Client.ConnectionId,Account=slot.Client.AccountIdentity.Fingerprint};
                    StudioRoutingPolicy.ApplyQuota(candidate,slot.Quota,slot.Client.AccountIdentity,candidate.Connection,DateTime.UtcNow);
                    if(!candidate.Excluded)result.Add(candidate);
                }
            }
            return StudioRoutingPolicy.CanonicalCandidates(result);
        }
        async Task<bool> PrepareAutomaticRoute()
        {
            if(selected==null||!selected.AutoRouting||ModelPinned())return true;
            if(!send.IsEnabled||routingInProgress||busy||connecting||handoffEditing||storageBlocked||routingStorageError||demoMode)return false;
            var session=selected;string prompt=input.Text.Trim(),projectPath=session.Project;
            if(!SaveCurrent())return false;
            routingInProgress=true;routingCancellation=new CancellationTokenSource();BeginWorkflow(true);Enable();
            try{
                var config=routingStore.LoadSettings();var key=routingStore.LoadKey(config.Engine);if(String.IsNullOrEmpty(key))throw new StudioRouteFailure("key");
                if(config.Engine!=routingSettings.Engine||StudioRoutingPolicy.NormalizePolicy(config.Policy)!=StudioRoutingPolicy.NormalizePolicy(routingSettings.Policy))throw new StudioRouteFailure("settings");
                foreach(var provider in StudioProviderCatalog.Ids){
                    if(provider=="gemini-api"&&!config.IncludeGeminiApi||provider=="gemini-cli"&&!config.IncludeGeminiCli)continue;
                    routingCancellation.Token.ThrowIfCancellationRequested();
                    var slot=Slot(provider);if(slot==null||slot.Client==null||!slot.Client.Connected||!slot.Authenticated)slot=await ConnectProvider(provider,false,false);
                    // Initial account/updated may invalidate the first read while
                    // App Server starts. Fetch fresh metadata before assembling
                    // candidates rather than silently omitting a signed-in AI.
                    if(slot!=null&&slot.Client!=null&&slot.Client.Connected){await RefreshProviderAccount(slot);if(slot.Refreshing)throw new StudioRouteFailure("candidate");}
                    if(slot!=null&&slot.Authenticated&&slot.Client.Connected){try{slot.Quota=await slot.Client.QuotaAsync();}catch{slot.Quota=null;}}
                }
                routingCancellation.Token.ThrowIfCancellationRequested();
                var candidates=RoutingCandidates();if(candidates.Count==0)throw new StudioRouteFailure("candidate");
                var request=new StudioRouteInput{Prompt=prompt,Candidates=candidates,Policy=config.Policy};StudioRouteDecision decision;
                SetWorkflowPhase("classifying");
                using(var engine=DecisionClientFactoryOverride!=null?DecisionClientFactoryOverride():new StudioDecisionClient())decision=await engine.DecideAsync(config,key,request,routingCancellation.Token);
                routingCancellation.Token.ThrowIfCancellationRequested();
                if(config.Engine!=routingSettings.Engine||StudioRoutingPolicy.NormalizePolicy(config.Policy)!=StudioRoutingPolicy.NormalizePolicy(routingSettings.Policy))throw new StudioRouteFailure("settings");
                if(closed||selected!=session||session.Project!=projectPath||input.Text.Trim()!=prompt||session.Archived||!session.AutoRouting||ModelPinned()||HasPreparedTransfer()||DeliveryUncertain())throw new StudioRouteFailure("candidate");
                SetWorkflowPhase("selecting");var plan=StudioRoutingPolicy.Plan(request,decision);var picked=plan.Candidate;
                var freshPlan=StudioRoutingPolicy.Plan(new StudioRouteInput{Prompt=prompt,Candidates=RoutingCandidates(),Policy=config.Policy},decision);var latest=freshPlan.Candidate;
                if(latest.Id!=picked.Id||latest.Connection!=picked.Connection||latest.Account!=picked.Account)throw new StudioRouteFailure("candidate");
                string previousProvider=session.PreferredProvider,previousModel=session.Model;var previousRoute=session.Route;
                session.PreferredProvider=picked.Provider;session.Model=picked.Model;
                session.Route=new StudioRouteRecord{Engine=config.Engine,Task=freshPlan.Intent==null?decision.Task.Value:StudioRouteRecord.TaskForIntent(freshPlan.Intent),Provider=picked.Provider,Model=picked.Model,PromptHash=StudioRouteInput.Hash(prompt),CreatedUtc=DateTime.UtcNow.ToString("o"),Probability=decision.Task.Probability,Confidence=decision.Task.Confidence,Remaining=latest.Remaining,DecisionMilliseconds=decision.Milliseconds,InputTokens=decision.InputTokens,SelectionMode=StudioRoutingPolicy.NormalizePolicy(config.Policy),Workload=freshPlan.Workload,AllowanceBand=freshPlan.AllowanceBand,PoolCandidates=freshPlan.PoolCandidates,WorkloadCandidates=freshPlan.WorkloadCandidates,SelectedBandCandidates=freshPlan.SelectedBandCandidates,Intent=freshPlan.Intent,PreferredProvider=freshPlan.PreferredProvider,SelectionReason=freshPlan.SelectionReason};
                dirty=true;ApplyProvider(picked.Provider);
                if(!SaveCurrent()){session.PreferredProvider=previousProvider;session.Model=previousModel;session.Route=previousRoute;dirty=true;ApplyProvider(previousProvider??"codex");SetWorkflowPhase("review");return false;}
                workflowPrepared=true;status.Text=StudioRoutingPolicy.EngineName(config.Engine)+S(" 모델 선택 완료 · 전송 준비"," selected the model · Preparing to send");return true;
            }catch(Exception error){if(!closed){status.Text=RoutingFailureText(error);SetWorkflowPhase(error is OperationCanceledException?"cancelled":"review");}return false;}
            finally{routingInProgress=false;routingCancellation.Dispose();routingCancellation=null;if(!closed)Enable();}
        }
    }
}
