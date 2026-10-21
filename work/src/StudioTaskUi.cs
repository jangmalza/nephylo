using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageWidget
{
    internal sealed partial class StudioWindow
    {
        readonly TextBlock quotaSummary=new TextBlock(),quotaDetails=new TextBlock(),taskHint=new TextBlock();
        readonly Button quotaRefresh=new Button(),handoffOpen=new Button(),confirmExecution=new Button();
        readonly Button acknowledgeDelivery=new Button();
        readonly Border connectionDetails=new Border();
        readonly Button detailsToggle=new Button();
        readonly TextBlock detailsChevron=new TextBlock();
        readonly DispatcherTimer quotaClock=new DispatcherTimer{Interval=TimeSpan.FromSeconds(30)};
        StudioQuotaSnapshot quotaSnapshot;
        bool quotaLoading,handoffEditing;
        readonly Dictionary<string,string> executionConfirmations=new Dictionary<string,string>();
        const string AdeProfile="ade-codex";

        void BuildTaskControls(StackPanel top,Grid main)
        {
            Setup(handoffOpen,S("다른 AI로 이어가기","Continue with another AI"),OpenHandoff);
            Setup(confirmExecution,S("현재 ADE 계정으로 연결","Use current ADE account"),ConfirmExecution);
            Setup(acknowledgeDelivery,S("이전 전송 확인 완료","Previous request reviewed"),AcknowledgeDelivery);
            foreach(var button in new[]{handoffOpen,quotaRefresh,confirmExecution,acknowledgeDelivery}){button.FontSize=11;button.Padding=new Thickness(8,4,8,4);button.MinHeight=28;button.Margin=new Thickness(0,0,6,6);}
            IconButton(detailsToggle,"details",S("연결·잔여량 상세 보기","Connection and allowance details"),ToggleDetails);QuietButton(detailsToggle);detailsToggle.MinHeight=30;detailsToggle.Padding=new Thickness(8,5,8,5);detailsToggle.Margin=new Thickness(4,0,0,0);top.Children.Add(detailsToggle);
            AutomationProperties.SetName(detailsToggle,S("연결·잔여량 상세 보기","Connection and allowance details"));detailsToggle.ToolTip=S("연결·잔여량·실행 권한을 자세히 확인합니다.","View connection, allowance and execution scope.");
            BuildWorkspaceDetails();
            BuildHandoffPanel(main);
            quotaClock.Tick+=(s,e)=>UpdateQuotaDisplay();quotaClock.Start();Closed+=(s,e)=>quotaClock.Stop();
        }
        bool ModelPinned(){return selected!=null&&(!String.IsNullOrEmpty(selected.ThreadId)||selected.Messages.Count>0);}
        StudioExecutionSession CurrentExecution(){return selected==null?null:StudioSessions.Active(selected);}
        bool NeedsAccountConfirmation()
        {
            var execution=CurrentExecution();if(execution==null||String.IsNullOrEmpty(execution.ThreadId)||client==null||!authenticated)return false;
            if(client.AccountIdentity!=null&&client.AccountIdentity.Known&&!String.IsNullOrEmpty(execution.AccountKey))return execution.AccountKey!=client.AccountIdentity.Fingerprint;
            string connection;return !executionConfirmations.TryGetValue(execution.Id,out connection)||connection!=client.ConnectionId;
        }
        bool AccountMismatch()
        {
            var execution=CurrentExecution();return execution!=null&&!String.IsNullOrEmpty(execution.AccountKey)&&client!=null&&client.AccountIdentity!=null&&client.AccountIdentity.Known&&execution.AccountKey!=client.AccountIdentity.Fingerprint;
        }
        bool DeliveryUncertain(){var execution=CurrentExecution();return execution!=null&&execution.DeliveryState=="awaitingVerification";}
        bool HasPreparedTransfer(){return selected!=null&&selected.Handoffs.Any(h=>h.State=="prepared"&&(h.TargetExecutionId==selected.ActiveExecutionId||String.IsNullOrEmpty(h.TargetExecutionId)&&h.SourceExecutionId==selected.ActiveExecutionId));}
        void ConfirmExecution()
        {
            if(busy||connecting||handoffEditing||client==null||!authenticated||AccountMismatch())return;
            var execution=CurrentExecution();if(execution==null)return;
            if(execution.Provider!=client.Provider)return;if(client.AccountIdentity!=null&&client.AccountIdentity.Known)execution.AccountKey=client.AccountIdentity.Fingerprint;execution.Profile=client.Profile;dirty=true;
            if(SaveCurrent()){executionConfirmations[execution.Id]=client.ConnectionId;status.Text=S("현재 ADE 연결로 이어갑니다. 위젯 계정과는 별도입니다.","Continuing with the current ADE connection, separate from the widget account.");Enable();}
        }
        void AcknowledgeDelivery()
        {
            if(busy||connecting||handoffEditing||!DeliveryUncertain())return;
            var execution=CurrentExecution();execution.DeliveryState="idle";dirty=true;
            if(!SaveCurrent()){execution.DeliveryState="awaitingVerification";Enable();return;}
            status.Text=S("이전 질문을 자동 재전송하지 않습니다. 필요하면 상태를 묻는 새 질문을 작성하세요.","The previous request will not be resent. Write a new status-check question if needed.");Enable();
        }
        void EnableTaskControls()
        {
            bool idle=!busy&&!connecting&&!handoffEditing;
            handoffOpen.IsEnabled=idle&&!storageBlocked&&selected!=null&&!selected.Archived&&!DeliveryUncertain()&&CurrentExecution()!=null;
            quotaRefresh.IsEnabled=(activeProvider=="codex"||activeProvider=="claude")&&!demoMode&&!quotaLoading&&client!=null&&client.Connected&&authenticated&&!connecting;
            confirmExecution.Visibility=NeedsAccountConfirmation()&&!AccountMismatch()?Visibility.Visible:Visibility.Collapsed;
            confirmExecution.IsEnabled=idle&&!storageBlocked;
            acknowledgeDelivery.Visibility=DeliveryUncertain()?Visibility.Visible:Visibility.Collapsed;acknowledgeDelivery.IsEnabled=idle&&!storageBlocked;
            if(AccountMismatch())taskHint.Text=S("이 대화를 실행한 계정과 현재 ADE 계정이 다릅니다. 원래 계정으로 연결하거나 문맥 전달로 새 실행을 준비하세요.","This conversation used a different account. Reconnect that account or prepare a new execution through context transfer.");
            else if(DeliveryUncertain())taskHint.Text=S("이전 전송의 완료 여부가 확인되지 않았습니다. 공급자에서 상태를 확인한 뒤 ‘이전 전송 확인 완료’를 눌러 주세요. 자동 재전송은 하지 않습니다.","The previous request's outcome is unknown. Check the provider, then choose Previous request reviewed. Nothing is resent automatically.");
            else if(HasPreparedTransfer())taskHint.Text=S("전송 전 저장된 전달문이 있습니다. ‘문맥 전달’에서 내용을 다시 확인하세요. 자동 재전송은 하지 않습니다.","A context transfer was saved before sending. Review it in Context transfer. It will not be resent automatically.");
            else if(NeedsAccountConfirmation())taskHint.Text=S("기존 대화의 계정 연결을 확인해 주세요. 현재 ADE 로그인으로 이어갈 때 위 버튼을 누르세요.","Verify the account for this saved conversation. Use the button above to continue with the current ADE sign-in.");
            else taskHint.Text=ModelPinned()?S("현재 작업의 모델 고정 · 변경하려면 문맥 전달을 사용하세요.","Model pinned for this task · Use context transfer to change it."):S("작업을 시작할 모델을 직접 선택하세요. 잔여량은 계정 한도이며 모델별 처리량을 보장하지 않습니다.","Choose a model before starting. Account allowance does not guarantee model-specific capacity.");
            if(DeliveryUncertain()&&!storageBlocked)status.Text=S("이전 질문의 처리 결과를 확인할 수 없습니다. 공급자에서 결과를 확인한 뒤 ‘이전 전송 확인 완료’를 누르세요. 자동으로 다시 보내지 않습니다.","The previous question's outcome is unknown. Check it with the provider, then choose Previous request reviewed. It will not be resent automatically.");
            UpdateQuotaDisplay();
            UpdateTaskCompactLayout();
        }
        void UpdateTaskCompactLayout()
        {
            var height=ActualHeight>0?ActualHeight:Height;bool compact=height<650;
            UpdateInputHeight();
            if(taskHeaderHost!=null)taskHeaderHost.MaxHeight=compact?86:Double.PositiveInfinity;
            taskHint.Visibility=!AccountMismatch()&&!DeliveryUncertain()&&!NeedsAccountConfirmation()&&!HasPreparedTransfer()?Visibility.Collapsed:Visibility.Visible;
            handoffOpen.ToolTip=taskHint.Text;
            if(handoffSubtitle!=null){
                handoffSubtitle.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
                handoffDestinationHelp.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
                handoffScope.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
                handoffReviewHeading.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
                handoffNoticeScroll.MaxHeight=compact?54:76;
                handoffEditor.Height=compact?180:280;
            }
        }
        void ClearQuota(){quotaSnapshot=null;quotaLoading=false;UpdateQuotaDisplay();}
        async Task RefreshQuota(IStudioAgentClient current)
        {
            if(demoMode||closed||current==null||current!=client||!current.Connected||!authenticated||quotaLoading)return;
            quotaLoading=true;Enable();
            try{var result=await current.QuotaAsync();if(!closed&&current==client&&authenticated){quotaSnapshot=result;var slot=Slot(current.Provider);if(slot!=null&&slot.Client==current)slot.Quota=result;}}
            catch{if(!closed&&current==client)quotaSnapshot=null;}
            finally{if(!closed&&current==client){quotaLoading=false;Enable();}}
        }
        static string QuotaStateText(StudioQuotaState state)
        {
            switch(state){case StudioQuotaState.Stale:return S("이전 값 · 다시 확인 필요","Previous value · Refresh needed");case StudioQuotaState.ResetPending:return S("초기화 후 확인 필요","Reset passed · Refresh needed");case StudioQuotaState.Mismatch:return S("연결 변경 · 다시 확인 필요","Connection changed · Refresh needed");default:return S("잔여량 확인 필요","Allowance unavailable");}
        }
        void UpdateQuotaDisplay()
        {
            UpdateQuotaContent();
            // Refresh the tooltip together with the visible content, including unavailable states.
            quotaSummary.ToolTip=quotaDetails.Text;
            UpdateDetailsSummary();
        }
        void UpdateQuotaContent()
        {
            if(StudioProviderCatalog.IsGemini(activeProvider)){
                quotaSummary.Text=ProviderName(activeProvider)+S(" · 잔여 한도 확인 불가"," · Remaining quota unavailable");
                quotaDetails.Text=activeProvider=="gemini-api"?S("API 프로젝트의 모델별 할당량·과금이 적용됩니다. 현재 이 연결은 잔여 한도를 제공하지 않습니다. 응답 토큰 수를 계정의 잔여량으로 해석하지 않으며 CLI 구독 한도와 합산하지 않습니다.","API project/model quotas and billing apply. This connection does not expose remaining quota. Response token counts are not account allowance and are not combined with CLI subscription limits."):S("Gemini CLI의 Google 계정 사용 조건이 적용됩니다. 현재 잔여 한도를 확인할 수 없습니다. Gemini API 또는 다른 AI 계정의 값을 가져와 표시하지 않습니다.","Gemini CLI Google-account terms and limits apply. Remaining quota is unavailable and is not substituted with Gemini API or another AI account's data.");return;
            }
            var quotaPrefix=activeProvider=="claude"?"ADE Claude · ":"ADE Codex · ";
            var now=DateTime.UtcNow;var state=quotaSnapshot==null||client==null?StudioQuotaState.Unknown:quotaSnapshot.State(now,client.AccountIdentity,client.ConnectionId);
            if(!authenticated||client==null||!client.Connected)state=StudioQuotaState.Unknown;
            quotaSummary.Text=quotaLoading?S("잔여량 확인 중…","Checking allowance…"):quotaPrefix+QuotaStateText(state);
            if(quotaSnapshot==null||state==StudioQuotaState.Mismatch||state==StudioQuotaState.Unknown){
                quotaDetails.Text=S("ADE 전용 실행 연결에서만 조회합니다. 위젯의 다른 계정 한도를 합치지 않습니다. 확인되지 않은 값은 추천에 사용하지 않으며 0% 또는 무제한으로 표시하지 않습니다.","Read only from ADE's execution connection. Widget account limits are not merged. Unknown values are not treated as zero or unlimited and do not inform recommendations.");
                if(activeProvider=="claude")quotaDetails.Text+=S("\n공식 Claude Code 2.1.304 이상과 구독 계정이 필요합니다. 실험적 조회 기능의 응답·계정·초기화 시각을 확인할 수 없으면 미확인으로 둡니다. API 키 한도와 유료 추가 사용량은 포함하지 않습니다.","\nRequires official Claude Code 2.1.304+ and a subscription account. Experimental usage data stays unknown when its response, account or reset time cannot be verified. API-key quota and paid extra usage are excluded.");
                bool trustedRestriction=quotaSnapshot!=null&&client!=null&&client.Connected&&authenticated&&quotaSnapshot.IsCurrentObservation(now,client.AccountIdentity,client.ConnectionId);
                if(trustedRestriction&&(quotaSnapshot.OrdinaryUsageAllowed==false||quotaSnapshot.Buckets.Any(b=>b.SpendControlReached==true||!String.IsNullOrEmpty(b.ReachedType)))){
                    quotaSummary.Text=quotaPrefix+S("잔여량 미확인 · 공급자 사용 제한 보고됨","Allowance unknown · Provider restriction reported");
                    quotaDetails.Text+=S("\n백분율을 받지 못했지만 공급자가 사용 제한을 보고했습니다. 공급자에서 현재 이용 조건을 확인하세요.","\nNo percentage was returned, but the provider reported a restriction. Check the current conditions with the provider.");
                }
                return;
            }
            var lines=new List<string>();var summaries=new List<string>();bool low=false,uncertain=state!=StudioQuotaState.Confirmed;
            foreach(var bucket in quotaSnapshot.Buckets){
                lines.Add(bucket.Name+(String.IsNullOrEmpty(bucket.ModelScope)?activeProvider=="claude"?S(" · 계정 공통 한도"," · Common account limit"):S(" · 모델 적용 범위 미확인"," · Model scope unconfirmed"):" · "+bucket.ModelScope));
                foreach(var window in new[]{bucket.Primary,bucket.Secondary}.Where(w=>w!=null)){
                    var ws=window.State(now,state);var label=window.WindowMinutes.HasValue?window.WindowMinutes.Value%1440==0?(window.WindowMinutes.Value/1440)+S("일","d"):window.WindowMinutes.Value%60==0?(window.WindowMinutes.Value/60)+S("시간","h"):window.WindowMinutes.Value+S("분","m"):window.Role=="primary"?S("단기","Primary"):S("추가 한도","Secondary");
                    var value=window.RemainingPercent.HasValue?window.RemainingPercent.Value.ToString("0.#",CultureInfo.CurrentCulture)+"%":S("미확인","Unknown");
                    var row=label+S(" 잔여 "," remaining ")+value;if(ws!=StudioQuotaState.Confirmed){row+=" · "+QuotaStateText(ws);uncertain=true;}else if(window.RemainingPercent.Value<=10)low=true;
                    if(window.ResetUtc.HasValue)row+=S(" · 초기화 "," · Resets ")+window.ResetUtc.Value.ToLocalTime().ToString("MM/dd HH:mm");lines.Add(row);summaries.Add(label+" "+value);
                }
                if(bucket.SpendControlReached==true||!String.IsNullOrEmpty(bucket.ReachedType))lines.Add(S("공급자가 사용 제한을 보고했습니다.","The provider reported a usage restriction."));
            }
            if(quotaSnapshot.OrdinaryUsageAllowed==false)lines.Add(S("일반 사용이 제한된 상태입니다. 공급자에서 이용 조건을 확인하세요.","Ordinary usage is restricted. Check conditions with the provider."));
            lines.Add(S("확인: ","Observed: ")+quotaSnapshot.ObservedUtc.ToLocalTime().ToString("MM/dd HH:mm:ss"));
            if(activeProvider=="claude")lines.Add(S("공식 CLI 실험적 조회 · 최대 60초 재사용 · 질문 전송 없이 확인","Official CLI experimental usage query · Cached up to 60s · No model prompt"));
            lines.Add(S("공급자가 보고한 한도입니다. 모델별 적용 범위가 없으면 공유 여부도 확정하지 않습니다. 다른 모델·서비스의 백분율과 합산하거나 작업량으로 환산하지 않습니다.","Provider-reported limits. Sharing is unconfirmed when model scope is absent. Percentages are not added across models/services or converted into task counts."));
            quotaDetails.Text=String.Join("\n",lines);
            var caution=uncertain?S(" · 다시 확인 필요"," · Refresh needed"):low?S(" · 잔여량 부족 주의"," · Low allowance"):S(" · 계정 한도 확인됨"," · Account allowance observed");
            if(quotaSnapshot.OrdinaryUsageAllowed==false||quotaSnapshot.Buckets.Any(b=>b.SpendControlReached==true||!String.IsNullOrEmpty(b.ReachedType)))caution=S(" · 사용 제한 확인 필요"," · Check usage restrictions");
            quotaSummary.Text=quotaPrefix+String.Join(" / ",summaries.Take(2))+caution;
        }
    }
}
