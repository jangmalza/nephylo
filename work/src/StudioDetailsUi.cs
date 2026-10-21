using System;
using System.Globalization;
using System.Windows;

namespace UsageWidget
{
    internal sealed partial class StudioWindow
    {
        string SelectionSummaryReason(string reason)
        {
            switch(reason){
                case "task_preference":return S("사용 가능한 모델 중 요청 유형에 맞는 AI를 우선했습니다.","Prioritized the configured task preference among available models.");
                case "allowance_precedence":return S("질문별 선호보다 잔여량 조건을 우선했습니다.","Allowance conditions took priority over the task preference.");
                case "file_access_required":return S("프로젝트 파일을 읽을 수 있는 연결을 선택했습니다.","Selected a connection able to inspect project files.");
                default:return S("선호 AI를 사용할 수 없어 연결 가능한 후보를 선택했습니다.","The preferred AI was unavailable; selected an eligible connection.");
            }
        }
        void UpdateDetailsSummary()
        {
            bool live=client!=null&&client.Connected&&authenticated;
            var now=DateTime.UtcNow;
            var state=!live||quotaSnapshot==null?StudioQuotaState.Unknown:quotaSnapshot.State(now,client.AccountIdentity,client.ConnectionId);
            detailQuotaCaption.Text=quotaLoading?S("현재 연결의 한도를 확인하고 있습니다.","Checking allowance for the current connection."):state==StudioQuotaState.Confirmed?S("최근 확인: ","Last checked: ")+quotaSnapshot.ObservedUtc.ToLocalTime().ToString("MM/dd HH:mm:ss"):!live?S("연결을 확인한 뒤 현재 한도를 조회할 수 있습니다.","Check the connection before refreshing current allowance."):state==StudioQuotaState.Stale||state==StudioQuotaState.ResetPending?S("이전 관측값입니다. 현재 잔여량을 다시 확인하세요.","This observation is old. Refresh the current allowance."):S("현재 한도를 확인하지 못했습니다. 상세에서 지원 범위를 확인하세요.","Current allowance is unavailable. See details for supported sources.");
            UpdateMainQuotas();
            var route=selected==null?null:selected.Route;
            if(route==null){
                detailRoute.Text=selected!=null&&selected.AutoRouting&&!ModelPinned()?S("첫 질문을 보낼 때 질문 유형과 연결·잔여량을 확인해 모델을 선택합니다.","The first send selects a model using the task, connections and observed allowance."):ModelPinned()?CurrentModelCaption()+S("로 이어갑니다. 직접 선택한 모델입니다."," continues this conversation. This model was selected manually."):S("질문을 보낼 AI와 모델을 직접 선택합니다.","Choose the AI and model for your question.");
                detailRouteHistory.Text="";detailRouteHistory.Visibility=Visibility.Collapsed;
            }else{
                string kind=route.Intent==null?TaskName(route.Task):IntentName(route.Intent);
                string reason=route.SelectionMode==StudioRoutingPolicy.TaskAffinity?SelectionSummaryReason(route.SelectionReason):S("연결·잔여량 조건과 설정된 선택 순서를 적용했습니다.","Applied connection/allowance conditions and the configured selection order.");
                detailRoute.Text=S(kind+" 요청에 "+ProviderName(route.Provider)+" / "+route.Model+"을 선택했습니다.","Selected "+ProviderName(route.Provider)+" / "+route.Model+" for "+kind.ToLowerInvariant()+".")+"\n"+reason;
                var execution=CurrentExecution();
                if(execution!=null&&(execution.Provider!=route.Provider||execution.Model!=route.Model))detailRoute.Text=S("현재 모델: ","Current model: ")+CurrentModelCaption()+"\n"+S("처음 자동 선택: ","Initial automatic selection: ")+ProviderName(route.Provider)+" / "+route.Model+" · "+kind;
                DateTime created;string observed=DateTime.TryParse(route.CreatedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out created)?created.ToLocalTime().ToString("MM/dd HH:mm"):S("선택 시점","At selection");
                detailRouteHistory.Text=S("선택 당시 잔여량: ","Allowance at selection: ")+(route.Remaining.HasValue?route.Remaining.Value.ToString("0.#",CultureInfo.CurrentCulture)+"%":S("미확인","Unknown"))+" · "+observed+"\n"+S("현재 잔여량은 위에서 별도로 확인합니다.","Current allowance is checked separately above.");
                detailRouteHistory.Visibility=Visibility.Visible;
            }
            detailsRoutingSettings.Content=ModelPinned()?S("새 작업 자동 선택 설정","Automatic selection for new tasks"):S("자동 선택 설정","Automatic selection settings");
            detailsRoutingSettings.ToolTip=S("새 작업의 첫 질문에 적용합니다. 진행 중인 대화의 모델은 바꾸지 않습니다.","Applies to the first question of a new task. The model of an ongoing conversation stays pinned.");
        }
    }
}
