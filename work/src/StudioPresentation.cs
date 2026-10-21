using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace UsageWidget
{
    internal sealed partial class StudioWindow
    {
        readonly TextBlock composerHint=new TextBlock(),modelPlaceholder=new TextBlock();
        readonly Button composerAction=new Button();
        readonly Border composerReadiness=new Border();
        readonly WrapPanel setupProgress=new WrapPanel();
        readonly TextBlock setupConnection=new TextBlock(),setupProject=new TextBlock(),setupModel=new TextBlock();
        string conversationStatusSignature;
        Action nextComposerAction;
        Style studioButtonStyle;

        Style StudioButtonStyle()
        {
            if(studioButtonStyle!=null)return studioButtonStyle;
            var border=new FrameworkElementFactory(typeof(Border),"shell");
            border.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));
            border.SetBinding(Border.BackgroundProperty,new Binding("Background"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            border.SetBinding(Border.BorderBrushProperty,new Binding("BorderBrush"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            border.SetBinding(Border.BorderThicknessProperty,new Binding("BorderThickness"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            border.SetBinding(Border.PaddingProperty,new Binding("Padding"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            var content=new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetBinding(ContentPresenter.ContentProperty,new Binding("Content"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            content.SetBinding(FrameworkElement.HorizontalAlignmentProperty,new Binding("HorizontalContentAlignment"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);
            var template=new ControlTemplate(typeof(Button)){VisualTree=border};
            var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Border.BorderBrushProperty,Foreground,"shell"));template.Triggers.Add(hover);
            var focus=new Trigger{Property=UIElement.IsKeyboardFocusedProperty,Value=true};focus.Setters.Add(new Setter(Border.BorderBrushProperty,Foreground,"shell"));template.Triggers.Add(focus);
            var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.48));template.Triggers.Add(disabled);
            studioButtonStyle=new Style(typeof(Button));studioButtonStyle.Setters.Add(new Setter(Control.TemplateProperty,template));
            studioButtonStyle.Setters.Add(new Setter(Control.BackgroundProperty,surface));studioButtonStyle.Setters.Add(new Setter(Control.ForegroundProperty,Foreground));
            studioButtonStyle.Setters.Add(new Setter(Control.BorderBrushProperty,line));studioButtonStyle.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(1)));
            studioButtonStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Center));
            studioButtonStyle.Setters.Add(new Setter(ToolTipService.ShowOnDisabledProperty,true));return studioButtonStyle;
        }
        void QuietButton(Button button)
        {button.Background=Brushes.Transparent;button.BorderBrush=Brushes.Transparent;}
        void BuildComposerReadiness(Panel parent)
        {
            setupProgress.Margin=new Thickness(2,0,2,8);
            foreach(var step in new[]{setupConnection,setupProject,setupModel}){step.FontSize=11;step.Margin=new Thickness(0,0,16,3);setupProgress.Children.Add(step);}
            AutomationProperties.SetName(setupProgress,S("새 대화 준비","New conversation setup"));setupDetails.Children.Add(setupProgress);
            var recovery=new WrapPanel();recovery.Children.Add(confirmExecution);recovery.Children.Add(acknowledgeDelivery);parent.Children.Add(recovery);
            composerReadiness.Margin=new Thickness(2,0,2,8);var row=new DockPanel{LastChildFill=true};composerReadiness.Child=row;
            Setup(composerAction,"",()=>{if(nextComposerAction!=null)nextComposerAction();});composerAction.FontSize=12;composerAction.MinHeight=34;composerAction.Padding=new Thickness(11,7,11,7);composerAction.Margin=new Thickness(0);composerAction.Background=Foreground;composerAction.Foreground=Background;composerAction.BorderBrush=Foreground;
            composerHint.FontSize=12;composerHint.Foreground=muted;composerHint.TextWrapping=TextWrapping.Wrap;composerHint.VerticalAlignment=VerticalAlignment.Center;
            AutomationProperties.SetName(composerHint,S("전송 준비 상태","Send readiness"));row.Children.Add(composerHint);parent.Children.Add(composerReadiness);
        }
        void UpdateComposerReadiness()
        {
            UpdateSetupProgress();UpdateConversationStatuses();
            string hint=null,action=null;nextComposerAction=null;
            if(storageBlocked)hint=S("대화를 저장해야 계속할 수 있습니다. 위의 복구 동작을 사용하세요.","Save the conversation before continuing. Use the recovery actions above.");
            else if(demoMode)hint=S("미리보기에서는 연결과 질문 전송을 실행하지 않습니다.","Preview does not connect accounts or send questions.");
            else if(selected==null)hint=S("새 대화를 만들어 시작하세요.","Start a new conversation.");
            else if(selected.Archived){hint=S("보관한 대화입니다.","This conversation is archived.");action=S("보관 해제","Restore");nextComposerAction=ToggleArchive;}
            else if(routingInProgress)hint=S("판단 엔진이 모델을 선택하고 있습니다. 중단 버튼으로 취소할 수 있습니다.","The decision engine is choosing a model. Use Stop to cancel.");
            else if(handoffEditing)hint=S("전달할 문맥을 검토하고 있습니다.","Reviewing context to transfer.");
            else if(busy)hint=S("중단해도 받은 답변은 대화에 남습니다.","Stopping keeps the partial answer in this conversation.");
            else if(connecting)hint=S("AI 연결을 확인하고 있습니다.","Checking the AI connection.");
            else if(loginPending)hint=S("열린 브라우저에서 로그인을 완료해 주세요.","Complete sign-in in the browser that was opened.");
            else if(selected.AutoRouting&&!ModelPinned()){
                if(!Directory.Exists(selected.Project??"")){hint=S("자동 선택에 사용할 프로젝트 폴더를 확인하세요.","Choose an available project folder before automatic selection.");action=S("폴더 선택","Choose folder");nextComposerAction=ChooseProject;}
                else hint=String.IsNullOrWhiteSpace(input.Text)?S("질문을 입력하면 첫 전송 시 모델을 자동 선택합니다.","Write a question; the first send automatically chooses a model."):S("보내기: 판단 서비스로 분류 후 선택된 AI에 한 번 전송","Send: classify with the decision service, then send once to the chosen AI");
            }
            else if(DeliveryUncertain())hint=S("이전 전송 결과를 먼저 확인해 주세요.","Review the previous request's outcome first.");
            else if(HasPreparedTransfer()){hint=S("보내기 전에 저장한 전달문이 있습니다.","A saved context transfer needs review.");action=S("전달문 확인","Review transfer");nextComposerAction=OpenHandoff;}
            else if(AccountMismatch())hint=S("이 대화에서 사용한 계정으로 다시 연결해 주세요.","Reconnect the account used for this conversation.");
            else if(NeedsAccountConfirmation())hint=S("현재 계정으로 이어갈지 위에서 확인해 주세요.","Confirm the account above before continuing.");
            else if(client==null||!client.Connected){hint=ModelPinned()?S("대화와 모델은 복원되었습니다. ","Conversation and model restored. ")+ProviderName(activeProvider)+S(" 연결을 확인하면 이어갈 수 있습니다."," needs a connection check to continue."):ProviderName(activeProvider)+S("에 연결해 사용할 모델을 확인하세요."," needs a connection before choosing a model.");action=S(ProviderName(activeProvider)+" 연결 확인","Connect "+ProviderName(activeProvider));nextComposerAction=async()=>await Connect();}
            else if(!authenticated){
                hint=activeProvider=="claude"?S("Claude Code 로그인·이용 권한이 필요합니다.","Claude Code sign-in and entitlement are required."):activeProvider=="gemini-cli"?S("공식 Gemini CLI에서 Google 로그인 후 다시 연결하세요.","Sign in with Google in official Gemini CLI, then reconnect."):activeProvider=="gemini-api"?S("Gemini API 키를 설정하고 프로젝트 한도를 확인하세요.","Configure a Gemini API key and check its project quota."):S("Codex에 로그인하면 질문을 보낼 수 있습니다.","Sign in to Codex to send a question.");
                action=activeProvider=="gemini-api"?S("API 키 설정","API key"):S("로그인","Sign in");nextComposerAction=async()=>await Login();
            }
            else if(!ClientMatchesSession())hint=S("이 대화의 AI 연결을 다시 확인해 주세요.","Recheck the AI connection for this conversation.");
            else if(!catalogReady){hint=S("사용 가능한 모델 목록을 확인해 주세요.","Refresh the available models.");action=S("모델 다시 확인","Refresh models");nextComposerAction=async()=>await Connect();}
            else if(!Directory.Exists(selected.Project??"")){hint=S("이 대화에서 사용할 프로젝트 폴더를 확인하세요.","Choose an available project folder for this conversation.");if(choose.IsEnabled){action=S("폴더 선택","Choose folder");nextComposerAction=ChooseProject;}}
            else if(models.SelectedItem==null){hint=ModelPinned()?S("저장된 모델을 사용할 수 없습니다. 다른 AI로 이어갈 수 있습니다.","The saved model is unavailable. Continue with another AI."):S(ProviderName(activeProvider)+"에서 사용할 모델을 아래에서 선택하세요.","Choose a "+ProviderName(activeProvider)+" model below.");if(models.IsEnabled){action=S("모델 선택","Choose model");nextComposerAction=OpenComposerModelMenu;}}
            else hint=String.IsNullOrWhiteSpace(input.Text)?S("질문을 입력하세요. Enter로 줄바꿈할 수 있습니다.","Write a question. Enter adds a new line."):S("Ctrl+Enter로 보내기 · Enter로 줄바꿈","Ctrl+Enter to send · Enter for a new line");
            composerHint.Text=hint;composerAction.Content=action;composerAction.Visibility=action==null?Visibility.Collapsed:Visibility.Visible;
            if(transcript!=null)transcript.SetStartHint(hint);
            composerAction.IsEnabled=!busy&&!connecting&&!routingInProgress&&!handoffEditing&&!demoMode&&!storageBlocked;
            AutomationProperties.SetName(composerAction,action??S("다음 단계","Next step"));
            send.Visibility=action==null||busy?Visibility.Visible:Visibility.Collapsed;
            modelPlaceholder.Visibility=models.SelectedItem==null?Visibility.Visible:Visibility.Collapsed;
            send.ToolTip=send.IsEnabled?S("Ctrl+Enter로 보내기","Send with Ctrl+Enter"):hint;
            status.Visibility=IsConnectionStatus(status.Text)&&!displayedFailure.HasValue&&!storageBlocked?Visibility.Collapsed:Visibility.Visible;
        }
        void UpdateSetupProgress()
        {
            // Setup is a checklist, not a claim that sending is permitted. Recovery
            // and account-confirmation guards remain authoritative below it.
            setupProgress.Visibility=selected!=null&&!selected.Archived&&!ModelPinned()&&!busy&&!connecting&&!routingInProgress&&!handoffEditing&&!storageBlocked&&!demoMode&&!DeliveryUncertain()&&!HasPreparedTransfer()?Visibility.Visible:Visibility.Collapsed;
            var picked=models.SelectedItem as StudioModel;
            SetSetupStep(setupConnection,S("AI 연결","AI connection"),client!=null&&client.Connected&&authenticated&&ClientMatchesSession());
            SetSetupStep(setupProject,S("프로젝트","Project"),selected!=null&&Directory.Exists(selected.Project??""));
            SetSetupStep(setupModel,S("모델","Model"),authenticated&&catalogReady&&picked!=null&&selected!=null&&picked.Id==selected.Model);
        }
        void SetSetupStep(TextBlock step,string label,bool ready)
        {
            step.Text=(ready?"✓ ":"○ ")+label;step.Foreground=ready?Foreground:muted;
            step.ToolTip=label+S(ready?" 확인됨":" 확인 필요",ready?" checked":" needs attention");
            AutomationProperties.SetName(step,(string)step.ToolTip);
        }
        internal static string ConversationStatus(StudioSession session,bool running)
        {
            if(session==null)return "";
            if(session.Archived)return S("보관됨","Archived");
            if(running)return S("응답 중","Responding");
            if(session.ProviderSessions.Any(x=>x.DeliveryState=="awaitingVerification"||x.DeliveryState=="running"))return S("전송 확인 필요","Review delivery");
            if(session.Handoffs.Any(h=>h.State=="prepared"&&(h.TargetExecutionId==session.ActiveExecutionId||String.IsNullOrEmpty(h.TargetExecutionId)&&h.SourceExecutionId==session.ActiveExecutionId)))return S("전달문 검토","Review context");
            var last=session.Messages.LastOrDefault();
            if(last!=null&&(last.State=="failed"||last.State=="unconfirmed"||last.State=="running"))return S("확인 필요","Needs attention");
            if(last!=null&&last.State=="interrupted")return S("중단됨","Interrupted");
            if(!String.IsNullOrWhiteSpace(session.Draft))return S("초안 있음","Draft");
            return last==null?S("새 대화","New conversation"):S("응답 완료","Answer received");
        }
        sealed class ConversationSummary : IValueConverter
        {
            readonly StudioWindow window;
            internal ConversationSummary(StudioWindow owner){window=owner;}
            public object Convert(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)
            {
                var session=value as StudioSession;if(session==null)return "";
                return ConversationStatus(session,window.selected==session&&window.busy)+" · "+(String.IsNullOrEmpty(session.Model)?ProviderName(session.PreferredProvider):session.Model);
            }
            public object ConvertBack(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture){throw new NotSupportedException();}
        }
        void UpdateConversationStatuses()
        {
            // Update realized row bindings without refreshing the collection view.
            // Refresh can reset selection or throw while WPF defers view changes,
            // including when called from a save/selection event. Unrealized rows
            // evaluate the converter normally when their container is created.
            var signature=String.Join("\n",sessions.Select(x=>x.Id+":"+x.Model+":"+x.PreferredProvider+":"+x.Updated+":"+ConversationStatus(x,x==selected&&busy)));
            if(signature==conversationStatusSignature)return;conversationStatusSignature=signature;
            UpdateConversationRowBindings(list);
        }
        static void UpdateConversationRowBindings(DependencyObject node)
        {
            var text=node as TextBlock;
            if(text!=null){
                var binding=BindingOperations.GetBindingExpression(text,TextBlock.TextProperty);
                if(binding!=null&&(binding.ParentBinding.Converter is ConversationSummary||binding.ParentBinding.Converter is ConversationTimestamp||binding.ParentBinding.Path!=null&&binding.ParentBinding.Path.Path=="Title")){
                    binding.UpdateTarget();
                    var tooltip=BindingOperations.GetBindingExpression(text,FrameworkElement.ToolTipProperty);if(tooltip!=null)tooltip.UpdateTarget();
                }
            }
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)UpdateConversationRowBindings(VisualTreeHelper.GetChild(node,i));
        }
        void FocusConversationSearch()
        {
            if(!search.IsEnabled||busy||connecting||handoffEditing)return;
            sidebarCollapsed=false;narrowSidebarOpen=true;if((ActualWidth>0?ActualWidth:Width)<960)detailsOpen=false;ApplyPanels(true);searchHost.Visibility=Visibility.Visible;search.Focus();search.SelectAll();
        }
        void CloseNarrowSidebar()
        {
            if(!narrowSidebarOpen)return;narrowSidebarOpen=false;ApplyPanels(true);SaveWorkspace();
            if(input.IsEnabled)input.Focus();else sidebarToggle.Focus();
        }
        void RevealSelectedConversation()
        {if(selected!=null&&list.Items.Contains(selected))list.ScrollIntoView(selected);}
    }
}
