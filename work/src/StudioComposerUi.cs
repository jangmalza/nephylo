using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UsageWidget
{
    internal sealed partial class StudioWindow
    {
        readonly WrapPanel composerSelectors=new WrapPanel();
        readonly WrapPanel composerPinnedModel=new WrapPanel();
        readonly TextBlock composerModelText=new TextBlock();
        readonly Button composerHandoff=new Button();
        readonly Button composerModelButton=new Button();

        void BuildPinnedComposer(Panel parent)
        {
            composerPinnedModel.VerticalAlignment=VerticalAlignment.Center;
            Setup(composerModelButton,"",OpenComposerModelMenu);QuietButton(composerModelButton);composerModelButton.MinHeight=30;composerModelButton.Padding=new Thickness(5);composerModelButton.Margin=new Thickness(0,0,3,0);
            var label=new StackPanel{Orientation=Orientation.Horizontal};label.Children.Add(StudioVisuals.Icon("lock",muted,11));
            composerModelText.FontSize=11;composerModelText.Margin=new Thickness(6,0,7,0);composerModelText.VerticalAlignment=VerticalAlignment.Center;composerModelText.TextTrimming=TextTrimming.CharacterEllipsis;composerModelText.MaxWidth=190;label.Children.Add(composerModelText);label.Children.Add(StudioVisuals.Icon("down",muted,10));
            composerModelButton.Content=label;composerPinnedModel.Children.Add(composerModelButton);AutomationProperties.SetName(composerModelButton,S("현재 모델 · 변경 메뉴","Current model and change menu"));
            AutomationProperties.SetName(composerModelText,S("현재 대화의 고정 모델","Pinned model for this conversation"));
            composerPinnedModel.Children.Add(Setup(composerHandoff,S("다른 AI로 이어가기","Continue with another AI"),OpenHandoff));
            composerHandoff.Content=StudioVisuals.Icon("transfer",muted,14);composerHandoff.Width=28;composerHandoff.MinHeight=30;composerHandoff.Padding=new Thickness(5);composerHandoff.Margin=new Thickness(0);QuietButton(composerHandoff);
            AutomationProperties.SetName(composerHandoff,S("다른 AI로 이어가기 · 문맥 검토","Continue with another AI · Review context"));
            parent.Children.Add(composerPinnedModel);
        }
        string CurrentModelCaption()
        {
            if(selected==null)return "";
            var execution=CurrentExecution();string provider=execution==null?activeProvider:execution.Provider;
            string id=execution==null?selected.Model:execution.Model;
            if(String.IsNullOrWhiteSpace(id))return ProviderName(provider);
            var slot=Slot(provider);var model=slot==null?null:slot.Models.FirstOrDefault(m=>m.Id==id);
            string label=model==null?id:model.Label;
            if(String.IsNullOrWhiteSpace(label))label=id;
            if(provider=="claude"&&(id=="sonnet"||id=="opus"||id=="haiku"))return "Claude "+Char.ToUpperInvariant(id[0])+id.Substring(1);
            return ProviderName(provider)+" · "+label;
        }
        void UpdateComposerModelControls()
        {
            bool pinned=ModelPinned();
            composerSelectors.Visibility=pinned?Visibility.Collapsed:Visibility.Visible;
            composerPinnedModel.Visibility=pinned?Visibility.Visible:Visibility.Collapsed;
            composerModelText.Text=CurrentModelCaption()+S(" · 고정"," · Pinned");
            composerModelText.ToolTip=S("이 대화는 저장된 AI·모델로 이어갑니다. 다른 연결은 문맥을 검토한 뒤 시작합니다.","This conversation continues with its saved AI and model. Review context before starting another connection.");
            composerHandoff.IsEnabled=handoffOpen.IsEnabled;
            composerModelButton.IsEnabled=!busy&&!connecting&&!routingInProgress&&!handoffEditing&&!storageBlocked;
            composerHandoff.ToolTip=handoffOpen.IsEnabled?S("전달할 문맥과 대상 모델을 먼저 검토합니다. 이 버튼만으로 질문을 보내지 않습니다.","Review the context and destination model first. Opening this panel does not send a question."):handoffOpen.ToolTip;
        }
    }
}
