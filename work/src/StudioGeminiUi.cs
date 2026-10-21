using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UsageWidget
{
    internal sealed partial class StudioWindow
    {
        void InvalidateProvider(string provider)
        {
            var slot=Slot(provider);if(slot!=null&&slot.Client!=null)slot.Client.Dispose();providerConnections.Remove(provider);
            if(activeProvider==provider){client=null;authenticated=false;catalogReady=false;ClearQuota();ApplyProvider(provider);}
        }
        bool LaunchGeminiLogin()
        {
            if(busy||connecting||demoMode)return false;
            try{
                InvalidateProvider("gemini-cli");
                Process.Start(StudioGeminiCliClient.CreateLoginStartInfo(store.GeminiCliHome));
                var text=S("열린 공식 Gemini CLI에서 ‘Sign in with Google’로 로그인한 뒤 다시 연결하세요. Google 인증정보를 Nephylo에 입력하지 않습니다.","In the official Gemini CLI window, choose Sign in with Google, then reconnect. Do not enter Google credentials into Nephylo.");
                if(handoffEditing)handoffNotice.Text=text;else status.Text=text;Enable();return true;
            }catch(Exception error){if(handoffEditing)handoffNotice.Text=Guidance(error,"gemini-cli");else status.Text=Guidance(error,"gemini-cli");return false;}
        }
        async Task ConfigureGeminiApiKey(bool fromHandoff=false)
        {
            if(busy||connecting||demoMode||handoffEditing&&!fromHandoff)return;
            var secrets=new StudioGeminiSecrets(store.GeminiApiHome);bool changed=false,hasKey=false,readFailed=false;
            try{hasKey=secrets.HasKey;}catch{readFailed=true;}
            var dialog=new Window{Owner=this,Title=S("Gemini API 연결","Gemini API connection"),Width=500,Height=360,MinWidth=400,MinHeight=320,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,Background=Background,Foreground=Foreground,FontFamily=FontFamily,Resources=Resources,ShowInTaskbar=false};
            var panel=new StackPanel{Margin=new Thickness(22)};dialog.Content=panel;
            panel.Children.Add(new TextBlock{Text=S("Gemini API 키","Gemini API key"),FontSize=19,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,10)});
            panel.Children.Add(new TextBlock{Text=S("Google AI Studio에서 발급한 본인 키를 입력하세요. API 프로젝트의 무료 한도 또는 사용량 과금이 적용되며 Gemini CLI·Google 구독과 별도입니다.","Enter your own Google AI Studio key. API project free quotas or usage billing apply, separately from Gemini CLI and Google subscriptions."),TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=muted,Margin=new Thickness(0,0,0,12)});
            var password=new PasswordBox{MaxLength=512,Padding=new Thickness(10),FontSize=14,Background=surface,Foreground=Foreground,BorderBrush=line,BorderThickness=new Thickness(1)};AutomationProperties.SetName(password,S("Gemini API 키 입력","Enter Gemini API key"));panel.Children.Add(password);
            var note=new TextBlock{Text=readFailed?S("저장된 키를 읽을 수 없습니다. 삭제한 뒤 다시 입력하세요.","The saved key could not be read. Remove it before entering another key."):hasKey?S("키가 저장되어 있습니다. 새 키를 입력하면 교체됩니다.","A key is saved. Enter a new key to replace it."):S("키는 이 Windows 계정에 암호화 저장하며 대화에 포함하지 않습니다.","The key is encrypted for this Windows account and is never included in conversations."),TextWrapping=TextWrapping.Wrap,Foreground=muted,FontSize=12,Margin=new Thickness(0,10,0,16)};panel.Children.Add(note);
            var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(actions);
            actions.Children.Add(Setup(new Button(),S("닫기","Close"),()=>dialog.Close()));
            var clear=Setup(new Button(),S("저장된 키 삭제","Remove saved key"),()=>{
                try{secrets.Clear();password.Clear();changed=true;dialog.DialogResult=true;}catch{note.Text=S("키를 삭제하지 못했습니다. 저장 폴더 권한을 확인하세요.","Could not remove the key. Check storage permissions.");}
            });clear.IsEnabled=hasKey||readFailed;actions.Children.Add(clear);
            var save=Setup(new Button(),S("저장·연결","Save and connect"),()=>{
                var value=password.Password.Trim();if(!StudioGeminiSecrets.ValidKey(value)){note.Text=S("올바른 API 키를 입력하세요. 앞뒤 공백 외의 공백·줄바꿈은 허용되지 않습니다.","Enter a valid API key without embedded whitespace or line breaks.");return;}
                try{secrets.Save(value);password.Clear();changed=true;dialog.DialogResult=true;}catch{note.Text=S("키를 저장하지 못했습니다. 폴더 권한을 확인하거나 손상된 키를 먼저 삭제하세요.","Could not save the key. Check permissions or remove a corrupt saved key first.");}
            });save.Background=Foreground;save.Foreground=Background;actions.Children.Add(save);dialog.Closed+=(s,e)=>password.Clear();dialog.ShowDialog();
            if(!changed||closed)return;InvalidateProvider("gemini-api");
            var result=await ConnectProvider("gemini-api",!fromHandoff&&activeProvider=="gemini-api",true);
            if(fromHandoff&&handoffEditing&&!closed){RefreshHandoffTargets();if(result!=null)handoffNotice.Text=TargetReady()?S("API 키 연결을 확인했습니다. 모델과 전달 내용을 검토하세요.","API connection checked. Review the model and context."):S("API 키와 사용 가능한 모델을 확인하세요. 아직 질문은 전송하지 않았습니다.","Check the API key and available models. No question was sent.");}
            Enable();
        }
    }
}
