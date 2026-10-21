using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UsageWidget
{
    internal sealed partial class StudioWindow
    {
        void OpenRoutingSettings()
        {
            if(busy||connecting||routingInProgress||handoffEditing||demoMode)return;
            StudioRoutingSettings preferences;
            try{preferences=routingStore.LoadSettings();}
            catch(Exception error){status.Text=RoutingFailureText(error);return;}
            var dialog=new Window{Title=S("자동 라우팅 · 판단 엔진","Automatic routing · Decision engine"),Owner=this,Width=550,Height=610,MinWidth=440,MinHeight=440,Background=Background,Foreground=Foreground,FontFamily=FontFamily,Resources=Resources,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false};
            var panel=new StackPanel{Margin=new Thickness(22)};dialog.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            panel.Children.Add(new TextBlock{Text=S("판단 엔진","Decision engine"),FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)});
            panel.Children.Add(new TextBlock{Text=S("판단 엔진은 모델을 선택하고, 답변은 기존 Codex·Claude 연결로 실행합니다. 판단 API의 사용량·과금은 구독과 별도입니다.","The decision engine selects a model; existing Codex/Claude connections produce the answer. Decision API usage and billing are separate from subscriptions."),FontSize=12,Foreground=muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
            var engine=new ComboBox{MinHeight=38,Margin=new Thickness(0,0,0,12)};foreach(var id in StudioRoutingPolicy.Engines)engine.Items.Add(StudioRoutingPolicy.EngineName(id));engine.SelectedIndex=Array.IndexOf(StudioRoutingPolicy.Engines,preferences.Engine);AutomationProperties.SetName(engine,S("판단 엔진 선택","Choose decision engine"));panel.Children.Add(engine);
            var info=new TextBlock{FontSize=12,Foreground=muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};panel.Children.Add(info);
            panel.Children.Add(new TextBlock{Text=S("모델 선택 정책","Model selection policy"),Margin=new Thickness(0,0,0,5)});
            var policy=new ComboBox{MinHeight=38,Margin=new Thickness(0,0,0,8)};policy.Items.Add(S("질문 특성 우선","Task-aware preference"));policy.Items.Add(S("기존 Codex 우선","Legacy Codex priority"));policy.SelectedIndex=StudioRoutingPolicy.NormalizePolicy(preferences.Policy)==StudioRoutingPolicy.TaskAffinity?0:1;AutomationProperties.SetName(policy,S("모델 선택 정책","Model selection policy"));panel.Children.Add(policy);
            panel.Children.Add(new TextBlock{Text=S("질문 특성 우선: 글쓰기·텍스트 분석·일반 계획은 Claude, 코드·정형 데이터 분석·기술 설계는 Codex를 선호합니다. 권한·작업군·잔여량 구간을 먼저 적용하며, 성능 우위를 측정한 점수는 아닙니다. 시작한 대화의 모델은 유지합니다.","Task-aware: prefer Claude for writing, prose analysis and nontechnical plans; Codex for code, structured technical data and technical designs. Scope, workload and allowance band come first. These are explicit preferences, not measured quality scores. Started conversations keep their model."),FontSize=11,Foreground=muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
            var azure=new StackPanel();panel.Children.Add(azure);
            azure.Children.Add(new TextBlock{Text=S("Azure 리소스 주소","Azure resource endpoint"),Margin=new Thickness(0,0,0,5)});
            var endpoint=new TextBox{Text=preferences.AzureEndpoint??"",MaxLength=300,Padding=new Thickness(9),Margin=new Thickness(0,0,0,10)};AutomationProperties.SetName(endpoint,S("MS Decision-1 Azure 리소스 주소","MS Decision-1 Azure endpoint"));azure.Children.Add(endpoint);
            azure.Children.Add(new TextBlock{Text=S("Decision-1 배포 이름","Decision-1 deployment name"),Margin=new Thickness(0,0,0,5)});
            var deployment=new TextBox{Text=preferences.AzureDeployment??"Microsoft-Decision-1",MaxLength=80,Padding=new Thickness(9),Margin=new Thickness(0,0,0,12)};AutomationProperties.SetName(deployment,S("MS Decision-1 배포 이름","MS Decision-1 deployment"));azure.Children.Add(deployment);
            panel.Children.Add(new TextBlock{Text=S("API 키","API key"),Margin=new Thickness(0,0,0,5)});
            var secret=new PasswordBox{MaxLength=2048,Padding=new Thickness(9),Background=surface,Foreground=Foreground,BorderBrush=line,BorderThickness=new Thickness(1)};AutomationProperties.SetName(secret,S("판단 엔진 API 키 입력","Decision engine API key"));panel.Children.Add(secret);
            var note=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=muted,Margin=new Thickness(0,7,0,12)};panel.Children.Add(note);
            var geminiCli=new CheckBox{Content=S("자동 선택에 Gemini CLI 포함","Include Gemini CLI"),IsChecked=preferences.IncludeGeminiCli,Margin=new Thickness(0,0,0,8)};
            var geminiApi=new CheckBox{Content=S("Gemini API 포함 · 별도 API 과금","Include Gemini API · Separate billing"),IsChecked=preferences.IncludeGeminiApi,Margin=new Thickness(0,0,0,12)};panel.Children.Add(geminiCli);panel.Children.Add(geminiApi);
            panel.Children.Add(new TextBlock{Text=S("현재 질문만 판단 서비스로 보내 유형·파일 필요 여부를 분류합니다. 모델 목록·잔여량에 따른 후보 선별은 이 PC에서 처리합니다. 대화 전체·파일·환경변수·다른 키는 첨부하지 않습니다. API 키는 이 Windows 계정에 암호화 저장합니다.","Only this question is sent for task/file classification. Model-catalog and allowance filtering stay on this PC. No full history, files, environment variables or other keys are attached. Keys are encrypted for this Windows account."),TextWrapping=TextWrapping.Wrap,Foreground=muted,FontSize=11,Margin=new Thickness(0,0,0,14)});
            var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(actions);
            actions.Children.Add(Setup(new Button(),S("닫기","Close"),()=>dialog.Close()));
            actions.Children.Add(Setup(new Button(),S("이 엔진 키 삭제","Remove this engine key"),()=>{
                if(engine.SelectedIndex<0)return;
                try{routingStore.ClearKey(StudioRoutingPolicy.Engines[engine.SelectedIndex]);secret.Clear();note.Text=S("이 엔진의 저장된 키를 삭제했습니다.","Saved key for this engine removed.");}catch(Exception error){note.Text=RoutingFailureText(error);}
            }));
            var save=Setup(new Button(),S("저장","Save"),()=>{
                if(engine.SelectedIndex<0||policy.SelectedIndex<0)return;
                var updated=new StudioRoutingSettings{Engine=StudioRoutingPolicy.Engines[engine.SelectedIndex],Policy=policy.SelectedIndex==0?StudioRoutingPolicy.TaskAffinity:StudioRoutingPolicy.LegacyTaskPolicy,AutoForNew=preferences.AutoForNew,IncludeGeminiCli=geminiCli.IsChecked==true,IncludeGeminiApi=geminiApi.IsChecked==true,AzureEndpoint=endpoint.Text.Trim(),AzureDeployment=deployment.Text.Trim()};
                try{
                    StudioDecisionClient.Endpoint(updated);
                    string key=secret.Password.Trim();if(key.Length>0)routingStore.SaveKey(updated.Engine,key);
                    routingStore.SaveSettings(updated);routingSettings=updated;routingStorageError=false;secret.Clear();dialog.DialogResult=true;
                    status.Text=S("판단 엔진 설정을 저장했습니다. 아직 질문은 전송하지 않았습니다.","Decision engine settings saved. No question has been sent.");
                }catch(Exception error){note.Text=RoutingFailureText(error);}
            });save.Background=Foreground;save.Foreground=Background;actions.Children.Add(save);
            Action update=()=>{
                secret.Clear();if(engine.SelectedIndex<0)return;string id=StudioRoutingPolicy.Engines[engine.SelectedIndex];azure.Visibility=id=="ms-decision"?Visibility.Visible:Visibility.Collapsed;
                info.Text=id=="jev"?S("TypeSafe API 키 · Jev · api.typesafe.ai","TypeSafe API key · Jev · api.typesafe.ai"):id=="openai-decisions"?S("OpenAI API 프로젝트 키 · Decisions / GPT-6-Luna · ChatGPT·Codex 로그인과 별도","OpenAI API project key · Decisions / GPT-6-Luna · Separate from ChatGPT/Codex sign-in"):S("Azure Foundry API 키 · Microsoft-Decision-1 배포 필요","Azure Foundry API key · Microsoft-Decision-1 deployment required");
                try{note.Text=String.IsNullOrEmpty(routingStore.LoadKey(id))?S("저장된 키가 없습니다. 준비한 API 키를 직접 입력하세요.","No key saved. Enter your API key here."):S("키가 저장되어 있습니다. 비워 두면 유지하며, 새 키를 입력하면 교체합니다.","A key is saved. Leave blank to keep it, or enter another to replace it.");}catch{note.Text=S("저장된 키를 읽지 못했습니다. 이 엔진 키를 삭제한 뒤 다시 저장하세요.","Could not read saved key. Remove this engine key before saving another.");}
            };
            engine.SelectionChanged+=(s,e)=>update();dialog.Closed+=(s,e)=>secret.Clear();update();WidgetWindow.ApplyDialogTheme(dialog);dialog.ShowDialog();Enable();
        }
    }
}
