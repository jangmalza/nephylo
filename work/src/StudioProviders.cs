using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UsageWidget
{
    internal sealed partial class StudioWindow
    {
        sealed class ProviderConnection
        {
            internal IStudioAgentClient Client;
            internal bool Authenticated,CatalogReady;
            internal bool Refreshing;
            internal int RefreshVersion;
            internal List<StudioModel> Models=new List<StudioModel>();
            internal StudioQuotaSnapshot Quota;
        }
        readonly Dictionary<string,ProviderConnection> providerConnections=new Dictionary<string,ProviderConnection>();
        readonly ComboBox providerPicker=new ComboBox();
        readonly TextBlock providerHelp=new TextBlock(),providerDisclosure=new TextBlock();
        readonly Button apiKeySettings=new Button();
        string activeProvider="codex";
        bool restoringProvider;
        internal static Func<string,IStudioAgentClient> ProviderFactoryOverride=null;
        internal static Func<string,string> ProviderExecutableOverride=null;
        static string ProviderName(string provider){return StudioProviderCatalog.Name(provider);}
        static string ProviderProfile(string provider){return StudioProviderCatalog.Profile(provider);}
        string SessionProvider(){var execution=CurrentExecution();return execution!=null?execution.Provider:StudioProviderCatalog.Normalize(selected==null?null:selected.PreferredProvider);}
        bool ClientMatchesSession(){return client!=null&&client.Provider==SessionProvider()&&client.Profile==ProviderProfile(SessionProvider());}
        ProviderConnection Slot(string provider){ProviderConnection slot;return providerConnections.TryGetValue(provider,out slot)?slot:null;}
        void CacheActiveProvider()
        {
            if(client==null)return;var slot=Slot(client.Provider);if(slot==null||slot.Client!=client||slot.Refreshing)return;
            slot.Authenticated=authenticated;slot.CatalogReady=catalogReady;slot.Models=models.Items.Cast<StudioModel>().ToList();slot.Quota=quotaSnapshot;
        }
        void ApplyProvider(string provider)
        {
            provider=StudioProviderCatalog.Normalize(provider);
            CacheActiveProvider();activeProvider=provider;var slot=Slot(provider);
            client=slot==null?null:slot.Client;authenticated=slot!=null&&slot.Authenticated&&client!=null&&client.Connected;catalogReady=slot!=null&&slot.CatalogReady&&authenticated;
            loginPending=false;quotaLoading=false;quotaSnapshot=slot==null?null:slot.Quota;
            restoringProvider=true;providerPicker.SelectedIndex=StudioProviderCatalog.Index(provider);restoringProvider=false;
            restoringModel=true;models.Items.Clear();if(slot!=null)foreach(var model in slot.Models)models.Items.Add(model);restoringModel=false;
            RestoreModelSelection();UpdateProviderPresentation();SyncConnectionStatus();
        }
        void RestoreSessionProvider(){var provider=SessionProvider();if(provider!=activeProvider||client!=null&&client.Provider!=provider)ApplyProvider(provider);else UpdateProviderPresentation();}
        void BuildProviderPicker(WrapPanel row)
        {
            foreach(var provider in StudioProviderCatalog.Ids)providerPicker.Items.Add(ProviderName(provider));providerPicker.MinWidth=120;providerPicker.MinHeight=28;providerPicker.FontSize=12;providerPicker.Margin=new Thickness(0,0,10,6);providerPicker.SelectedIndex=0;
            AutomationProperties.SetName(providerPicker,S("작업 AI 선택","Task AI"));row.Children.Add(providerPicker);
            providerPicker.SelectionChanged+=(s,e)=>{
                if(restoringProvider||selected==null||providerPicker.SelectedIndex<0)return;
                if(busy||connecting||handoffEditing||ModelPinned()||HasPreparedTransfer()||DeliveryUncertain()){restoringProvider=true;providerPicker.SelectedIndex=StudioProviderCatalog.Index(SessionProvider());restoringProvider=false;return;}
                if(!SaveCurrent()){restoringProvider=true;providerPicker.SelectedIndex=StudioProviderCatalog.Index(activeProvider);restoringProvider=false;return;}
                selected.PreferredProvider=StudioProviderCatalog.FromIndex(providerPicker.SelectedIndex);selected.Model=null;selected.Route=null;dirty=true;ApplyProvider(selected.PreferredProvider);
                if(SaveCurrent()&&!authenticated)status.Text=S("사용할 AI 연결과 로그인을 확인한 뒤 모델과 프로젝트를 선택하세요.","Connect and sign in to your selected AI, then choose a model and project.");
                Render();
            };
        }
        void UpdateProviderPresentation()
        {
            var name=ProviderName(activeProvider);AutomationProperties.SetName(models,name+S(" 모델"," model"));
            if(transcript!=null)transcript.SetProvider(activeProvider);
            providerHelp.Text=activeProvider=="claude"?S("대화 전용 · Claude Code · 도구 없음 · Ctrl+Enter 전송","Chat only · Claude Code · No tools · Ctrl+Enter to send"):S("읽기 전용 · Codex · Ctrl+Enter 전송","Read-only · Codex · Ctrl+Enter to send");
            providerHelp.ToolTip=activeProvider=="claude"?S("공식 Claude Code를 실행합니다. 프로젝트 파일·명령 도구는 사용하지 않으며 전달한 텍스트로 대화합니다. 조직 관리 정책은 공식 CLI에서 적용될 수 있습니다.","Runs official Claude Code with tools disabled, using only supplied text. Organization-managed policy may still apply in the official CLI."):S("파일 변경·권한 확대 요청은 거절합니다.","File changes and permission escalation are declined.");
            providerDisclosure.Text=activeProvider=="claude"?S("전송한 질문·문맥은 Anthropic으로 전달됩니다. 공식 CLI도 자체 세션 기록을 저장합니다.","Sent questions/context go to Anthropic. The official CLI also stores its own session records."):S("질문과 필요한 프로젝트 내용은 OpenAI로 전송됩니다.","Prompts and relevant project content are sent to OpenAI.");
            if(StudioProviderCatalog.IsGemini(activeProvider)){
                providerHelp.Text=S("대화 전용 · ","Chat only · ")+name+S(" · 도구 없음 · Ctrl+Enter 전송"," · No tools · Ctrl+Enter to send");
                providerHelp.ToolTip=activeProvider=="gemini-api"?S("이 API 실행에서 완료된 대화와 현재 질문만 Google로 전송합니다. 실패·중단된 질문, 다른 AI 대화, 파일, 환경 변수는 자동 첨부하지 않습니다.","Only completed turns from this API execution and the current question go to Google. Failed/interrupted turns, other AI sessions, files and environment variables are not automatically attached."):S("공식 Gemini CLI를 전용 프로필에서 실행합니다. 파일·명령·MCP 도구는 차단하고 OAuth 인증은 공식 CLI에서 관리합니다.","Runs official Gemini CLI in a dedicated profile. File, command and MCP tools are blocked; OAuth remains managed by the official CLI.");
                providerDisclosure.Text=activeProvider=="gemini-api"?S("질문·완료된 대화는 Google Gemini API로 전송됩니다. API 키의 프로젝트 한도·과금이 적용됩니다.","Questions and completed turns go to Google Gemini API. The API key project's quota and billing apply."):S("질문·문맥은 공식 Gemini CLI를 통해 Google로 전송됩니다. CLI 자체 세션 기록은 별도 저장됩니다.","Questions/context go to Google through official Gemini CLI, which keeps separate session records.");
            }
            providerPicker.IsEnabled=!busy&&!connecting&&!handoffEditing&&!ModelPinned()&&!HasPreparedTransfer()&&!DeliveryUncertain()&&selected!=null&&!selected.Archived;
            providerPicker.ToolTip=ModelPinned()?S("이 대화의 AI와 모델을 유지합니다. 변경하려면 ‘문맥 전달 · 모델 변경’을 사용하세요.","This conversation keeps its AI and model. Use context transfer to change them."):S("이 대화에서 사용할 AI를 선택하세요.","Choose the AI for this conversation.");ToolTipService.SetShowOnDisabled(providerPicker,true);ToolTipService.SetShowOnDisabled(models,true);
            connect.Content=name+(authenticated?S(" 재연결"," reconnect"):S(" 연결"," connect"));
            login.Content=activeProvider=="claude"?S("Claude Code에서 로그인","Sign in through Claude Code"):activeProvider=="gemini-cli"?S("Gemini CLI에서 로그인","Sign in through Gemini CLI"):activeProvider=="gemini-api"?S("API 키 설정","Configure API key"):S("브라우저 로그인","Browser sign-in");
            if(activeProvider=="gemini-api")login.Visibility=Visibility.Collapsed;
            apiKeySettings.Visibility=activeProvider=="gemini-api"?Visibility.Visible:Visibility.Collapsed;apiKeySettings.IsEnabled=!busy&&!connecting&&!handoffEditing&&!demoMode;
            connectionState.Text=connecting?S("○ 연결 확인 중","○ Checking connection"):loginPending?S("○ 로그인 대기","○ Awaiting sign-in"):authenticated?"● "+name+S(" 연결됨"," connected"):"○ "+name+S(" 연결 필요"," needs connection");
        }
        string Guidance(Exception error,string provider=null){return StudioFailure.Guidance(StudioFailure.Classify(error),L.English,provider??activeProvider);}
        static string ConnectionStatusText(string provider,bool connected,bool signedIn,bool english)
        {
            if(!connected)return ProviderName(provider)+(english?" needs connection. Connect, then choose a model and project.":" 연결이 필요합니다. 연결 후 모델과 프로젝트를 선택하세요.");
            if(signedIn)return ProviderName(provider)+(english?" connected · Sending consumes your account allowance.":" 연결됨 · 질문 전송 시 계정 사용량이 소모됩니다.");
            if(provider=="claude")return english?"Claude Code authentication/entitlement is required. Sign in through official Claude Code, then reconnect.":"공식 Claude Code 인증 또는 이용 권한이 필요합니다. Claude Code에서 로그인한 뒤 다시 연결하세요.";
            if(provider=="gemini-cli")return english?"Sign in with Google through the dedicated Gemini CLI profile, then reconnect.":"전용 Gemini CLI 프로필에서 Google 로그인 후 다시 연결하세요.";
            if(provider=="gemini-api")return english?"Configure your Gemini API key. Its project quota and billing are separate from Google account subscriptions.":"Gemini API 키를 설정하세요. API 프로젝트 한도·과금은 Google 계정 구독과 별도입니다.";
            return english?"Sign in to ADE's Codex profile using Browser sign-in.":"ADE 전용 Codex 로그인이 필요합니다. 브라우저 로그인을 눌러 주세요.";
        }
        static bool IsConnectionStatus(string text)
        {
            foreach(var provider in StudioProviderCatalog.Ids)foreach(var english in new[]{false,true})
                if(text==ConnectionStatusText(provider,false,false,english)||text==ConnectionStatusText(provider,true,false,english)||text==ConnectionStatusText(provider,true,true,english))return true;
            return text=="사용할 AI 연결과 로그인을 확인한 뒤 모델과 프로젝트를 선택하세요."||text=="Connect and sign in to your selected AI, then choose a model and project."
                ||text=="브라우저에서 로그인을 완료하세요."||text=="Complete sign-in in your browser."
                ||text=="로그인을 취소했습니다. Codex 연결로 다시 시작할 수 있습니다."||text=="Sign-in cancelled. Use Connect Codex to try again."
                ||text=="공식 Claude Code 창에서 직접 로그인한 뒤 ‘Claude Code 연결’을 다시 누르세요. 인증정보는 Nephylo에 입력하지 않습니다."||text=="Complete sign-in in the official Claude Code window, then reconnect. Do not enter credentials into Nephylo.";
        }
        void SyncConnectionStatus()
        {
            // Only replace connection guidance owned by this view. A background
            // account event must not hide a save failure, a turn outcome or recovery.
            if(busy||loginPending||storageBlocked||displayedFailure.HasValue||modelNoticeActive||DeliveryUncertain()||!IsConnectionStatus(status.Text))return;
            status.Text=ConnectionStatusText(activeProvider,client!=null&&client.Connected,authenticated,L.English);
        }
        async Task Connect(){if(busy||connecting||handoffEditing)return;await ConnectProvider(activeProvider,true,true);}
        async Task<ProviderConnection> ConnectProvider(string provider,bool activate,bool force)
        {
            if(busy||connecting||demoMode)return Slot(provider);
            connecting=true;if(activate&&!routingInProgress)BeginWorkflow(false,true);Enable();var slot=Slot(provider);
            try{
                if(slot==null||slot.Client==null||!slot.Client.Connected||force){
                    if(slot!=null&&slot.Client!=null)slot.Client.Dispose();
                    var next=ProviderFactoryOverride!=null?ProviderFactoryOverride(provider):CreateProviderClient(provider);
                    slot=new ProviderConnection{Client=next};providerConnections[provider]=slot;WireProvider(slot);
                    if(activate)ApplyProvider(provider);
                    string path=ProviderExecutableOverride!=null?ProviderExecutableOverride(provider):provider=="gemini-api"?null:provider=="gemini-cli"?StudioGeminiCliClient.FindExecutable():provider=="claude"?StudioClaudeClient.FindExecutable():await Task.Run(()=>CodexService.FindExecutable(executableOverride));
                    await next.ConnectAsync(path,ProviderHome(provider));
                }
                await RefreshProviderAccount(slot);
                if(closed)return slot;
                if(activate){ApplyProvider(provider);if(!storageBlocked){displayedFailure=null;status.Text=ConnectionStatusText(provider,slot.Client!=null&&slot.Client.Connected,slot.Authenticated,L.English);}if(!routingInProgress)SetWorkflowPhase(slot.Authenticated?"connected":"review");Render();}
                return slot;
            }catch(Exception error){
                if(slot!=null){slot.Authenticated=false;slot.CatalogReady=false;slot.Quota=null;if(slot.Client!=null)slot.Client.Dispose();}
                if(!closed){if(activate){ApplyProvider(provider);ShowFailure(error);if(!routingInProgress)SetWorkflowPhase("failed");}else handoffNotice.Text=Guidance(error,provider);}
                return null;
            }finally{if(!closed){connecting=false;Enable();}}
        }
        async Task RefreshAccount(IStudioAgentClient current)
        {var slot=Slot(current.Provider);if(slot!=null&&slot.Client==current)await RefreshProviderAccount(slot);}
        IStudioAgentClient CreateProviderClient(string provider)
        {
            switch(provider){case "codex":return new StudioCodexClient();case "claude":return new StudioClaudeClient();case "gemini-cli":return new StudioGeminiCliClient();case "gemini-api":return new StudioGeminiApiClient();default:throw new StudioFailure(StudioFailureKind.Compatibility);}
        }
        string ProviderHome(string provider)
        {switch(provider){case "codex":return store.CodexHome;case "claude":return store.ClaudeHome;case "gemini-cli":return store.GeminiCliHome;case "gemini-api":return store.GeminiApiHome;default:throw new StudioFailure(StudioFailureKind.Compatibility);}}
        async Task RefreshProviderAccount(ProviderConnection slot)
        {
            var current=slot.Client;var version=++slot.RefreshVersion;slot.Refreshing=true;
            try{
                var result=await current.AccountAsync();if(closed||Slot(current.Provider)!=slot||slot.Client!=current||version!=slot.RefreshVersion)return;
                var signedIn=current.Connected&&StudioCodexClient.Obj(result,"account")!=null&&current.AccountIdentity!=null&&current.AccountIdentity.IsAuthenticated;
                var available=signedIn?await current.ModelsAsync():new List<StudioModel>();if(closed||Slot(current.Provider)!=slot||slot.Client!=current||version!=slot.RefreshVersion)return;
                slot.Authenticated=signedIn&&current.Connected;slot.CatalogReady=slot.Authenticated;slot.Quota=null;slot.Models=slot.Authenticated?available:new List<StudioModel>();
                if(client==current){authenticated=slot.Authenticated;catalogReady=slot.CatalogReady;quotaSnapshot=null;ReplaceModels(slot.Models);catalogReady=slot.CatalogReady;UpdateProviderPresentation();SyncConnectionStatus();Enable();if(authenticated&&(current.Provider=="codex"||current.Provider=="claude"))Ui(async()=>await RefreshQuota(current));}
            }finally{if(version==slot.RefreshVersion)slot.Refreshing=false;}
        }
        void WireProvider(ProviderConnection slot)
        {
            var current=slot.Client;
            current.Notice+=(kind,value)=>Ui(async()=>{
                if(Slot(current.Provider)!=slot||slot.Client!=current)return;
                if(kind=="account"||kind=="disconnected"){slot.Authenticated=false;slot.CatalogReady=false;slot.Quota=null;}
                if(kind=="quota")slot.Quota=null;
                if(client!=current)return;
                if(kind=="response"&&value=="output-limit"&&busy&&response!=null){responseLimited=true;return;}
                if(kind=="login"){loginPending=false;try{if(value=="success")await RefreshAccount(current);else ShowFailure(new StudioFailure(StudioFailureKind.Authentication));}catch(Exception e){if(client==current)ShowFailure(e);}Enable();}
                else if(kind=="quota"){quotaSnapshot=null;await RefreshQuota(current);}
                else if(kind=="account"){
                    authenticated=false;catalogReady=false;ClearQuota();
                    if(busy){current.Dispose();status.Text=S("실행 중 계정이 변경되었습니다. 전송 결과를 확인하세요.","Account changed during execution. Review the request outcome.");Enable();}
                    else{try{await RefreshAccount(current);}catch(Exception e){if(client==current)ShowFailure(e);}}
                }
                else if(kind=="denied")status.Text=S("현재 권한 범위를 벗어난 작업 요청을 거절했습니다.","Declined a request outside the current permission scope.");
                else if(kind=="disconnected"){authenticated=false;loginPending=false;ClearQuota();if(!storageBlocked&&!displayedFailure.HasValue)ShowFailure(new StudioFailure(StudioFailureKind.Disconnected));Enable();}
            });
            current.Message+=(id,text,final)=>Ui(()=>{
                if(client!=current||!busy||response==null||response.Provider!=current.Provider)return;
                if(!cancelRequested)SetWorkflowPhase("streaming");
                if(!answers.ContainsKey(id)){answers[id]="";answerOrder.Add(id);}
                answers[id]=final==Boolean.TrueString?text:answers[id]+text;response.Text=String.Join("\n\n",answerOrder.Select(x=>answers[x]));
                if(response.Text.Length>512000){status.Text=S("응답이 너무 길어 중단합니다.","Stopping an oversized response.");current.Dispose();return;}dirty=true;renderPending=true;
            });
        }
        void DisposeProviders()
        {
            var clients=providerConnections.Values.Select(x=>x.Client).Where(x=>x!=null).ToList();if(client!=null)clients.Add(client);foreach(var c in clients.Distinct())c.Dispose();providerConnections.Clear();
        }
        bool LaunchClaudeLogin()
        {
            try{var path=StudioClaudeClient.FindExecutable();Directory.CreateDirectory(store.ClaudeHome);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path,"auth login"){UseShellExecute=true,WorkingDirectory=store.ClaudeHome,WindowStyle=System.Diagnostics.ProcessWindowStyle.Normal});status.Text=S("공식 Claude Code 창에서 직접 로그인한 뒤 ‘Claude Code 연결’을 다시 누르세요. 인증정보는 Nephylo에 입력하지 않습니다.","Complete sign-in in the official Claude Code window, then reconnect. Do not enter credentials into Nephylo.");return true;}
            catch(Exception e){if(handoffEditing)handoffNotice.Text=Guidance(e,"claude");else status.Text=Guidance(e,"claude");return false;}
        }
    }
}
