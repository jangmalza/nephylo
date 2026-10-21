using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace UsageWidget
{
    internal sealed partial class StudioWindow
    {
        Border handoffPanel;
        readonly TextBox handoffEditor=new TextBox();
        readonly ComboBox handoffProvider=new ComboBox(),handoffDestination=new ComboBox();
        readonly ListBox handoffMessages=new ListBox();
        readonly TextBlock handoffNotice=new TextBlock(),handoffHistory=new TextBlock();
        readonly TextBlock handoffDestinationHelp=new TextBlock(),handoffSummary=new TextBlock();
        TextBlock handoffSubtitle,handoffScope;
        FrameworkElement handoffReviewHeading;
        ScrollViewer handoffNoticeScroll;
        readonly Button handoffSend=new Button(),handoffCopy=new Button();
        readonly Button handoffConnect=new Button(),handoffLogin=new Button();
        readonly Button handoffConfirmAccount=new Button();
        StudioHandoffRecord handoffDraft;
        bool restoringHandoff;
        sealed class HandoffChoice
        {
            internal string Model,ExecutionId,Label;
            public string Display{get{return Label;}}
            public string Description{get;set;}
            public override string ToString(){return Label;}
        }
        sealed class HandoffExcerpt
        {
            internal string Id,Text,Label;
            public override string ToString(){return Label;}
        }
        void BuildHandoffPanel(Grid main)
        {
            handoffPanel=new Border{Background=Background,BorderBrush=line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(13),Padding=new Thickness(20),MaxWidth=700,Margin=new Thickness(10),HorizontalAlignment=HorizontalAlignment.Center,Visibility=Visibility.Collapsed};
            Grid.SetRowSpan(handoffPanel,3);Panel.SetZIndex(handoffPanel,20);main.Children.Add(handoffPanel);
            var shade=new Border{Background=new SolidColorBrush(Color.FromArgb(150,0,0,0))};shade.SetBinding(UIElement.VisibilityProperty,new Binding("Visibility"){Source=handoffPanel});Grid.SetRowSpan(shade,3);Panel.SetZIndex(shade,19);main.Children.Add(shade);
            var dock=new DockPanel();handoffPanel.Child=dock;
            var header=new DockPanel{Margin=new Thickness(0,0,0,16)};DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
            var close=Setup(new Button(),S("닫기","Close"),()=>{if(SaveCurrent()){handoffEditing=false;motion.Show(handoffPanel,false);handoffDraft=null;Enable();if(input.IsEnabled)input.Focus();}else handoffNotice.Text=StudioFailure.Guidance(StudioFailureKind.Storage,L.English);});close.Margin=new Thickness(12,0,0,0);close.Padding=new Thickness(12,5,12,5);close.VerticalAlignment=VerticalAlignment.Top;DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
            var title=new StackPanel();header.Children.Add(title);
            title.Children.Add(new TextBlock{Text=S("다른 AI로 이어가기","Continue with another AI"),FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
            handoffSubtitle=new TextBlock{Text=S("필요한 내용만 골라 다른 AI와 이어가세요.","Choose what the next AI needs to continue."),FontSize=12,Foreground=muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,0)};title.Children.Add(handoffSubtitle);
            var footer=new Border{BorderBrush=line,BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(0,12,0,0),Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);
            var review=new StackPanel();footer.Child=review;handoffReviewHeading=HandoffSection("3",S("확인 후 전달","Review and send"));review.Children.Add(handoffReviewHeading);
            handoffNotice.FontSize=12;handoffNotice.TextWrapping=TextWrapping.Wrap;handoffNotice.LineHeight=18;handoffNotice.Margin=new Thickness(10,8,10,8);AutomationProperties.SetName(handoffNotice,S("문맥 전달 상태","Context transfer status"));
            handoffNoticeScroll=new ScrollViewer{Content=handoffNotice,MaxHeight=76,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};review.Children.Add(new Border{Background=surface,BorderBrush=line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),Margin=new Thickness(0,0,0,10),Child=handoffNoticeScroll});
            var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};review.Children.Add(actions);
            actions.Children.Add(Setup(new Button(),S("전달문 저장","Save draft"),()=>{handoffNotice.Text=SaveCurrent()?S("전달문을 이 PC에 저장했습니다. 아직 전송하지 않았습니다.","Context draft saved on this PC. Nothing has been sent."):StudioFailure.Guidance(StudioFailureKind.Storage,L.English);}));
            actions.Children.Add(Setup(handoffCopy,S("확인한 전달문 복사","Copy reviewed context"),CopyHandoff));
            actions.Children.Add(Setup(handoffSend,S("확인 후 전송","Confirm and send"),async()=>await SubmitHandoff()));
            foreach(Button b in actions.Children){b.FontSize=12;b.Padding=new Thickness(10,7,10,7);b.Margin=new Thickness(6,0,0,6);b.MinHeight=36;}
            handoffSend.Background=Foreground;handoffSend.Foreground=Background;handoffSend.BorderBrush=Foreground;handoffSend.FontWeight=FontWeights.SemiBold;
            var body=new StackPanel{Margin=new Thickness(0,0,8,0)};dock.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
            body.Children.Add(HandoffSection("1",S("이어갈 AI와 모델","Destination AI and model")));
            var pickers=new Grid{Margin=new Thickness(0,0,0,8)};pickers.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});pickers.ColumnDefinitions.Add(new ColumnDefinition());body.Children.Add(pickers);
            foreach(var provider in StudioProviderCatalog.Ids)handoffProvider.Items.Add(ProviderName(provider));handoffProvider.Width=140;handoffProvider.Margin=new Thickness(0,0,8,0);handoffProvider.MinHeight=36;handoffProvider.FontSize=12;pickers.Children.Add(handoffProvider);
            handoffDestination.MinWidth=150;handoffDestination.MinHeight=36;handoffDestination.FontSize=12;handoffDestination.HorizontalAlignment=HorizontalAlignment.Stretch;Grid.SetColumn(handoffDestination,1);pickers.Children.Add(handoffDestination);
            var choiceText=new FrameworkElementFactory(typeof(TextBlock));choiceText.SetBinding(TextBlock.TextProperty,new Binding("Display"));choiceText.SetBinding(FrameworkElement.ToolTipProperty,new Binding("Description"));choiceText.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);handoffDestination.ItemTemplate=new DataTemplate{VisualTree=choiceText};
            handoffDestinationHelp.FontSize=12;handoffDestinationHelp.Foreground=muted;handoffDestinationHelp.TextWrapping=TextWrapping.Wrap;handoffDestinationHelp.LineHeight=18;handoffDestinationHelp.Margin=new Thickness(0,0,0,10);body.Children.Add(handoffDestinationHelp);
            var targetRow=new WrapPanel{Margin=new Thickness(0,0,0,12)};body.Children.Add(targetRow);
            targetRow.Children.Add(Setup(handoffConnect,S("대상 연결 확인","Connect destination"),async()=>await CheckHandoffConnection()));
            targetRow.Children.Add(Setup(handoffLogin,S("대상 AI 로그인","Destination sign-in"),async()=>await HandoffLogin()));
            targetRow.Children.Add(Setup(handoffConfirmAccount,S("현재 대상 계정으로 이어가기","Use current destination account"),ConfirmHandoffAccount));
            foreach(var b in new[]{handoffConnect,handoffLogin,handoffConfirmAccount}){b.FontSize=11;b.Padding=new Thickness(8,5,8,5);b.MinHeight=30;b.Margin=new Thickness(0,0,6,8);}
            AutomationProperties.SetName(handoffProvider,S("전달 대상 AI","Destination AI"));AutomationProperties.SetName(handoffDestination,S("대상 모델 또는 이전 실행","Destination model or previous execution"));
            handoffProvider.SelectionChanged+=(s,e)=>{if(!restoringHandoff)RefreshHandoffTargets();};
            handoffDestination.SelectionChanged+=(s,e)=>{if(restoringHandoff||handoffDraft==null)return;var choice=handoffDestination.SelectedItem as HandoffChoice;if(choice!=null){handoffDraft.TargetProvider=StudioProviderCatalog.FromIndex(handoffProvider.SelectedIndex)??"codex";handoffDraft.TargetModel=choice.Model;handoffDraft.ResumeExecutionId=choice.ExecutionId;dirty=true;}UpdateHandoffButtons();};
            body.Children.Add(HandoffSection("2",S("전달할 내용","Context to include")));
            handoffScope=new TextBlock{Text=S("아래 내용만 전송합니다. 파일·인증 정보·입력 중인 질문은 자동 첨부하지 않습니다.","Only the text below is sent. Files, credentials and your unsent question are not attached automatically."),FontSize=12,Foreground=muted,TextWrapping=TextWrapping.Wrap,LineHeight=18,Margin=new Thickness(0,0,0,10)};body.Children.Add(handoffScope);
            var excerptBody=new StackPanel();var excerpts=AnimatedDisclosure(S("대화에서 메시지 추가","Add messages from this conversation"),excerptBody);body.Children.Add(excerpts);
            handoffMessages.SelectionMode=SelectionMode.Extended;handoffMessages.MaxHeight=140;handoffMessages.FontSize=12;handoffMessages.Background=surface;handoffMessages.Foreground=Foreground;handoffMessages.BorderBrush=line;AutomationProperties.SetName(handoffMessages,S("전달할 메시지","Messages to include"));excerptBody.Children.Add(handoffMessages);
            excerptBody.Children.Add(Setup(new Button(),S("선택한 메시지 추가","Add selected messages"),AddHandoffExcerpts));
            handoffEditor.AcceptsReturn=true;handoffEditor.TextWrapping=TextWrapping.Wrap;handoffEditor.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;handoffEditor.Height=280;handoffEditor.MaxLength=StudioHandoff.MaximumPayloadLength;handoffEditor.Padding=new Thickness(12);handoffEditor.FontSize=13;handoffEditor.Background=surface;handoffEditor.BorderBrush=line;handoffEditor.ToolTip=handoffScope.Text;body.Children.Add(handoffEditor);AutomationProperties.SetName(handoffEditor,S("실제로 전달할 문맥","Exact context to transfer"));
            handoffSummary.FontSize=11;handoffSummary.Foreground=muted;handoffSummary.TextWrapping=TextWrapping.Wrap;handoffSummary.Margin=new Thickness(0,6,0,12);body.Children.Add(handoffSummary);
            handoffEditor.TextChanged+=(s,e)=>{if(restoringHandoff||handoffDraft==null)return;handoffDraft.Payload=handoffEditor.Text;dirty=true;UpdateHandoffButtons();};
            body.Children.Add(Setup(new Button(),S("현재 대화 기준으로 다시 확인","Confirm against current conversation"),()=>{
                if(handoffDraft==null||selected==null)return;handoffDraft.SourceHash=StudioHandoff.SourceHash(selected);dirty=true;handoffNotice.Text=S("현재 대화를 기준으로 확인했습니다. 전달문에 누락된 변경이 없는지 검토하세요.","Reviewed against the current conversation. Check the draft for any missing changes.");UpdateHandoffButtons();
            }));
            handoffHistory.TextWrapping=TextWrapping.Wrap;handoffHistory.FontSize=12;handoffHistory.LineHeight=18;handoffHistory.Margin=new Thickness(8);var history=AnimatedDisclosure(S("이 대화의 전달 기록","Context transfer history"),handoffHistory);history.Margin=new Thickness(0,12,0,0);body.Children.Add(history);
            motion.Feedback(handoffProvider);motion.Feedback(handoffDestination);
        }
        FrameworkElement HandoffSection(string number,string title)
        {
            var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,10)};
            row.Children.Add(new Border{Background=surface,BorderBrush=line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(11),Width=22,Height=22,Margin=new Thickness(0,0,8,0),Child=new TextBlock{Text=number,FontSize=11,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}});
            row.Children.Add(new TextBlock{Text=title,FontSize=13,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});return row;
        }
        async Task CheckHandoffConnection()
        {
            if(busy||connecting||!handoffEditing)return;
            var provider=HandoffProvider();var result=await ConnectProvider(provider,false,false);
            if(closed||!handoffEditing||HandoffProvider()!=provider||result==null||Slot(provider)!=result)return;
            // App Server can invalidate its first account read during startup.
            // Recheck metadata while the editor stays locked; never resend a prompt.
            if(result.Client!=null&&result.Client.Connected&&(!result.Authenticated||!result.CatalogReady)){
                connecting=true;Enable();
                try{await RefreshProviderAccount(result);}
                catch(Exception error){if(!closed)handoffNotice.Text=Guidance(error,provider);return;}
                finally{if(!closed){connecting=false;Enable();}}
            }
            if(closed||!handoffEditing||HandoffProvider()!=provider||Slot(provider)!=result)return;
            RefreshHandoffTargets();
            bool available=result.Client!=null&&result.Client.Connected&&result.Authenticated&&result.CatalogReady;
            handoffNotice.Text=available?(result.Models.Count>0?S("대상 연결을 확인했습니다. 사용할 모델을 선택하고 전달 내용을 검토하세요.","Destination connected. Choose a model and review the context."):S("연결된 계정에서 사용할 수 있는 모델이 없습니다. 계정의 이용 조건을 확인하세요.","This account returned no available models. Check the account's access.")):ProviderName(provider)+S(" 로그인·이용 권한을 확인하세요. 전달문은 계속 저장하거나 복사할 수 있습니다."," needs sign-in/access checks. You can still save or copy the context.");
        }
        void OpenHandoff()
        {
            if(busy||connecting||renameEditing||storageBlocked||selected==null||selected.Archived||DeliveryUncertain()||CurrentExecution()==null)return;
            if(!SaveCurrent())return;
            try{
                var pending=selected.Handoffs.LastOrDefault(h=>h.State=="prepared"&&(h.TargetExecutionId==selected.ActiveExecutionId||String.IsNullOrEmpty(h.TargetExecutionId)&&h.SourceExecutionId==selected.ActiveExecutionId));
                if(pending!=null){
                    var unused=CurrentExecution();StudioSessions.Activate(selected,pending.SourceExecutionId);pending.TargetExecutionId=null;pending.State="draft";pending.PayloadHash=null;pending.ReviewHash=null;
                    if(unused!=null&&unused.Id!=selected.ActiveExecutionId&&unused.DeliveryState=="idle"&&!selected.Messages.Any(m=>m.ExecutionId==unused.Id)&&!selected.Handoffs.Any(h=>h!=pending&&(h.SourceExecutionId==unused.Id||h.TargetExecutionId==unused.Id)))selected.ProviderSessions.Remove(unused);
                    dirty=true;RestoreSessionProvider();RestoreModelSelection();
                }
                handoffDraft=pending??selected.Handoffs.LastOrDefault(h=>h.State=="draft"&&h.SourceExecutionId==selected.ActiveExecutionId);
                if(handoffDraft==null){if(selected.Handoffs.Count>=StudioHandoff.MaximumRecords)throw new InvalidDataException();handoffDraft=StudioHandoff.CreateDraft(selected,SessionProvider(),selected.Model,new int[0]);
                    if(!L.English)handoffDraft.Payload="# 문맥 전달\n\n## 작업 목표\n\n## 필수 조건\n\n## 확정된 결정\n\n## 완료된 결과와 남은 문제\n\n## 다음 요청\n\n";
                    selected.Handoffs.Add(handoffDraft);dirty=true;
                }
                handoffEditing=true;restoringHandoff=true;handoffProvider.SelectedIndex=StudioProviderCatalog.Index(handoffDraft.TargetProvider);handoffEditor.Text=handoffDraft.Payload;
                handoffMessages.Items.Clear();foreach(var m in selected.Messages)handoffMessages.Items.Add(new HandoffExcerpt{Id=m.Id,Text=m.Text??"",Label=(m.Role=="user"?S("나","You"):ProviderName(m.Provider))+" · "+(m.Text??"").Replace("\r"," ").Replace("\n"," ").Substring(0,Math.Min(80,(m.Text??"").Replace("\r"," ").Replace("\n"," ").Length))});
                restoringHandoff=false;RefreshHandoffTargets();UpdateHandoffHistory();detailsOpen=false;ApplyPanels(true);motion.Show(handoffPanel,true);
                handoffNotice.Text=StudioHandoff.IsFresh(selected,handoffDraft)?S("전송 전 대상과 내용을 확인하세요. 새 AI가 전달문을 읽는 데 사용량이 소모됩니다. 연결 전에도 복사할 수 있습니다.","Check the destination and content before sending. Reading this context uses the destination AI's allowance. Copy is available before connection."):S("대화가 변경되었습니다. 전달문을 검토하고 현재 대화 기준으로 다시 확인하세요.","The conversation changed. Review the context and confirm it against the current conversation.");
                if(!SaveCurrent())handoffNotice.Text=StudioFailure.Guidance(StudioFailureKind.Storage,L.English);Enable();
            }catch{handoffEditing=false;motion.Show(handoffPanel,false);handoffDraft=null;restoringHandoff=false;status.Text=S("문맥 전달을 준비하지 못했습니다. 전달 기록·크기 제한과 저장 상태를 확인하세요.","Could not prepare context transfer. Check record/size limits and storage.");Enable();}
        }
        void RefreshHandoffTargets()
        {
            if(handoffDraft==null)return;
            var provider=HandoffProvider();if(handoffDraft.TargetProvider!=provider)handoffDraft.ResumeExecutionId=null;
            restoringHandoff=true;handoffDestination.Items.Clear();
            CacheActiveProvider();var slot=Slot(provider);
            if(slot!=null&&slot.Authenticated&&slot.CatalogReady&&slot.Client.Connected){
                foreach(StudioModel m in slot.Models)handoffDestination.Items.Add(new HandoffChoice{Model=m.Id,Label=m.Label+S(" · 새로 시작"," · Start new"),Description=m.Label+"\n"+S("새 AI 대화를 시작하며, 검토한 전달문만 가져갑니다. 원래 대화는 유지됩니다.","Start a new AI conversation with only the reviewed context. The original conversation is kept.")});
                foreach(var execution in selected.ProviderSessions.Where(e=>e.Provider==provider&&e.Id!=selected.ActiveExecutionId&&!String.IsNullOrEmpty(e.ThreadId)&&e.DeliveryState=="idle"&&(TargetAccountMatches(e,slot)||TargetCanConfirm(e,slot))&&e.Profile==ProviderProfile(provider)&&slot.Models.Any(m=>m.Id==e.Model)))
                    handoffDestination.Items.Add(new HandoffChoice{Model=execution.Model,ExecutionId=execution.Id,Label=execution.Model+S(" · 이어가기 "," · Resume ")+execution.Id.Substring(0,6),Description=execution.Model+"\n"+S("이 AI의 이전 대화를 이어가며, 검토한 전달문을 추가합니다.","Resume this AI's previous conversation and add the reviewed context.")+"\n"+S("실행 ID: ","Execution ID: ")+execution.Id+(TargetAccountMatches(execution,slot)?"":"\n"+S("현재 대상 계정 확인이 필요합니다.","Confirm the current destination account first."))});
            }
            else handoffDestination.Items.Add(new HandoffChoice{Model="manual",Label=S("연결 후 모델 선택","Connect to select a model"),Description=S("대상 연결을 확인한 뒤 모델을 선택하세요. 연결 전에도 전달문을 저장하거나 복사할 수 있습니다.","Check the destination connection, then select a model. You can save or copy the context before connecting.")});
            handoffDestination.SelectedItem=handoffDestination.Items.Cast<HandoffChoice>().FirstOrDefault(x=>x.Model==handoffDraft.TargetModel&&x.ExecutionId==handoffDraft.ResumeExecutionId);
            if(handoffDestination.SelectedItem==null&&handoffDestination.Items.Count==1&&((HandoffChoice)handoffDestination.Items[0]).Model=="manual")handoffDestination.SelectedIndex=0;
            restoringHandoff=false;var choice=handoffDestination.SelectedItem as HandoffChoice;
            handoffDraft.TargetProvider=StudioProviderCatalog.FromIndex(handoffProvider.SelectedIndex)??"codex";if(choice!=null)handoffDraft.TargetModel=choice.Model;dirty=true;UpdateHandoffButtons();
        }
        void AddHandoffExcerpts()
        {
            if(handoffDraft==null)return;var payload=new StringBuilder(handoffEditor.Text);var ids=new List<string>(handoffDraft.SelectedMessageIds);
            foreach(HandoffExcerpt item in handoffMessages.SelectedItems){if(ids.Contains(item.Id))continue;if(ids.Count>=StudioHandoff.MaximumSelectedMessages){handoffNotice.Text=S("최대 40개 메시지를 선택할 수 있습니다.","Select up to 40 messages.");return;}payload.Append("\n\n---\n").Append(item.Label).Append('\n').Append(item.Text);ids.Add(item.Id);}
            if(payload.Length>StudioHandoff.MaximumPayloadLength){handoffNotice.Text=S("전달문은 32,000자 이내로 작성하세요. 필요한 메시지만 선택해 주세요.","Keep context within 32,000 characters. Select fewer messages.");return;}
            handoffDraft.SelectedMessageIds=ids;handoffEditor.Text=payload.ToString();dirty=true;
        }
        void UpdateHandoffButtons()
        {
            var valid=handoffEditing&&handoffDraft!=null&&handoffDraft.State=="draft"&&!String.IsNullOrWhiteSpace(handoffEditor.Text)&&handoffDestination.SelectedItem!=null;
            if(handoffPanel!=null)handoffPanel.IsEnabled=!busy&&!connecting;
            handoffCopy.IsEnabled=valid&&!busy&&!connecting;
            handoffSend.Content=S("확인 후 ","Confirm and send to ")+ProviderName(HandoffProvider())+(L.English?"":"로 전송");
            handoffConnect.IsEnabled=handoffEditing&&!busy&&!connecting&&!demoMode;handoffLogin.IsEnabled=handoffConnect.IsEnabled;
            handoffLogin.Content=HandoffProvider()=="gemini-api"?S("API 키 설정","Configure API key"):S("대상 AI 로그인","Destination sign-in");
            var choice=handoffDestination.SelectedItem as HandoffChoice;var slot=Slot(HandoffProvider());var execution=choice==null||choice.ExecutionId==null?null:selected.ProviderSessions.FirstOrDefault(e=>e.Id==choice.ExecutionId);
            handoffDestination.ToolTip=choice==null?S("새 대화로 시작하거나 이 AI의 이전 대화를 이어갈 수 있습니다.","Start a new conversation or resume this AI's previous one."):choice.Description;
            handoffDestinationHelp.Text=choice==null?S("모델을 선택하세요. ‘새로 시작’은 새 AI 대화, ‘이어가기’는 이전 AI 대화를 사용합니다.","Choose a model. Start new opens a new AI conversation; Resume continues a previous one."):choice.Model=="manual"?S("대상 연결을 확인하면 모델을 선택할 수 있습니다. 전달문 저장·복사는 먼저 가능합니다.","Check the destination connection to choose a model. You can save or copy the context now."):choice.ExecutionId==null?S("새 AI 대화에 검토한 전달문만 보냅니다. 원래 대화는 유지됩니다.","Only the reviewed context goes to the new AI conversation. Your original conversation is kept."):S("이 AI의 이전 대화를 이어가며 전달문을 추가합니다.","Continue this AI's previous conversation and add the context.");
            handoffSummary.Text=handoffEditor.Text.Length.ToString("N0")+S(" / 32,000자 · 추가한 메시지 "," / 32,000 characters · Added messages: ")+(handoffDraft==null?0:handoffDraft.SelectedMessageIds.Count).ToString();
            handoffConfirmAccount.Visibility=execution!=null&&slot!=null&&TargetCanConfirm(execution,slot)&&!TargetAccountMatches(execution,slot)?Visibility.Visible:Visibility.Collapsed;
            if(handoffConfirmAccount.Visibility==Visibility.Visible)handoffDestinationHelp.Text+=S(" 먼저 현재 대상 계정을 확인하세요."," Confirm the current destination account first.");
            handoffConfirmAccount.IsEnabled=handoffEditing&&!busy&&!connecting&&!storageBlocked;
            handoffProvider.IsEnabled=handoffDestination.IsEnabled=!busy&&!connecting;
            handoffSend.IsEnabled=valid&&HasHandoffContent(handoffEditor.Text)&&!busy&&!connecting&&!storageBlocked&&TargetReady()&&selected.Messages.Count<398&&StudioHandoff.IsFresh(selected,handoffDraft);
            handoffSend.ToolTip=storageBlocked?S("저장 오류를 해결한 뒤 전송할 수 있습니다.","Resolve the saving error before sending."):
                !HasHandoffContent(handoffEditor.Text)?S("목표·다음 요청을 작성하거나 전달할 메시지를 추가하세요.","Write a goal/next request or add selected messages."):
                !TargetReady()?S("대상 연결·로그인·모델 선택을 확인하세요. 이전 대화는 현재 계정 확인이 필요할 수 있습니다.","Check the destination connection, sign-in and model. Resuming a previous conversation may require account confirmation."):
                selected!=null&&selected.Messages.Count>=398?S("이 대화의 메시지 수 제한에 도달했습니다. 전달문을 복사해 새 대화에서 이어가세요.","This conversation has reached its message limit. Copy the context to continue in a new conversation."):
                handoffDraft!=null&&!StudioHandoff.IsFresh(selected,handoffDraft)?S("대화가 변경되었습니다. 전달문을 검토하고 현재 대화 기준으로 다시 확인하세요.","The conversation changed. Review the context and confirm it against the current conversation."):
                S("검토한 전달문을 선택한 AI에 보냅니다. 사용량이 소모됩니다.","Send the reviewed context to the selected AI. This uses its allowance.");
            ToolTipService.SetShowOnDisabled(handoffSend,true);
        }
        string HandoffProvider(){return StudioProviderCatalog.FromIndex(handoffProvider.SelectedIndex)??"codex";}
        bool TargetReady()
        {
            var slot=Slot(HandoffProvider());var choice=handoffDestination.SelectedItem as HandoffChoice;
            if(slot==null||slot.Client==null||!slot.Client.Connected||!slot.Authenticated||!slot.CatalogReady||choice==null||!slot.Models.Any(m=>m.Id==choice.Model))return false;
            if(choice.ExecutionId!=null){var execution=selected.ProviderSessions.FirstOrDefault(e=>e.Id==choice.ExecutionId);if(execution==null||execution.Provider!=HandoffProvider()||execution.Profile!=slot.Client.Profile||execution.Model!=choice.Model||!TargetAccountMatches(execution,slot))return false;}
            return true;
        }
        bool TargetAccountMatches(StudioExecutionSession execution,ProviderConnection slot)
        {
            var identity=slot.Client.AccountIdentity;if(identity==null||!identity.IsAuthenticated)return false;
            if(identity.Known&&!String.IsNullOrEmpty(execution.AccountKey))return execution.AccountKey==identity.Fingerprint;
            string connection;return executionConfirmations.TryGetValue(execution.Id,out connection)&&connection==slot.Client.ConnectionId;
        }
        bool TargetCanConfirm(StudioExecutionSession execution,ProviderConnection slot)
        {
            var identity=slot.Client==null?null:slot.Client.AccountIdentity;
            return slot.Authenticated&&slot.Client.Connected&&identity!=null&&identity.IsAuthenticated&&(!identity.Known||String.IsNullOrEmpty(execution.AccountKey));
        }
        void ConfirmHandoffAccount()
        {
            if(!handoffEditing||busy||connecting||storageBlocked||selected==null)return;
            var slot=Slot(HandoffProvider());var choice=handoffDestination.SelectedItem as HandoffChoice;
            var execution=choice==null?null:selected.ProviderSessions.FirstOrDefault(e=>e.Id==choice.ExecutionId);
            if(slot==null||execution==null||execution.Provider!=HandoffProvider()||execution.Profile!=slot.Client.Profile||!TargetCanConfirm(execution,slot))return;
            var previous=execution.AccountKey;if(slot.Client.AccountIdentity.Known)execution.AccountKey=slot.Client.AccountIdentity.Fingerprint;dirty=true;
            if(!SaveCurrent()){execution.AccountKey=previous;return;}
            executionConfirmations[execution.Id]=slot.Client.ConnectionId;RefreshHandoffTargets();handoffNotice.Text=S("현재 대상 계정으로 이전 실행을 이어가도록 확인했습니다. 전달문을 검토한 뒤 전송하세요.","Confirmed resuming the previous execution with the current destination account. Review the context before sending.");
        }
        async Task HandoffLogin()
        {
            if(!handoffEditing||busy||connecting)return;var provider=HandoffProvider();
            if(provider=="gemini-api"){await ConfigureGeminiApiKey(true);return;}
            if(provider=="gemini-cli"){LaunchGeminiLogin();return;}
            if(provider=="claude"){if(LaunchClaudeLogin())handoffNotice.Text=S("공식 Claude Code 로그인 후 대상 연결 확인을 다시 누르세요.","After official Claude Code sign-in, recheck the destination connection.");return;}
            var slot=Slot(provider);if(slot==null||slot.Client==null||!slot.Client.Connected)slot=await ConnectProvider(provider,false,false);
            if(closed||slot==null||slot.Client==null||!slot.Client.Connected)return;
            try{var url=await ((StudioCodexClient)slot.Client).LoginAsync();System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true});handoffNotice.Text=S("Codex 브라우저 로그인 후 대상 연결 확인을 다시 누르세요.","After Codex browser sign-in, recheck the destination connection.");}
            catch(Exception e){handoffNotice.Text=Guidance(e,provider);}
        }
        static bool HasHandoffContent(string payload)
        {
            var template=new HashSet<string>(new[]{"# 문맥 전달","## 작업 목표","## 필수 조건","## 확정된 결정","## 완료된 결과와 남은 문제","## 다음 요청","# Context handoff","## Goal","## Constraints","## Decisions","## Results and remaining issues","## Selected conversation excerpts","## Next request","---"},StringComparer.Ordinal);
            return (payload??"").Split('\n').Any(line=>!String.IsNullOrWhiteSpace(line)&&!template.Contains(line.Trim()));
        }
        void UpdateHandoffHistory()
        {
            handoffHistory.Text=String.Join("\n",selected.Handoffs.Select(h=>ProviderName(h.TargetProvider)+" · "+h.TargetModel+" · "+HandoffState(h.State)));
        }
        static string HandoffState(string state)
        {
            switch(state){case "draft":return S("전달문 작성 중","Draft");case "prepared":return S("전송 전 저장됨","Saved before send");case "awaitingVerification":return S("전송 결과 확인 필요","Outcome needs review");case "completed":return S("응답 완료","Response completed");case "interrupted":return S("전달 후 중단","Interrupted");case "failed":return S("전달 실패","Failed");case "exported":return S("복사됨 · 외부 전송 미확인","Copied · External delivery unverified");default:return S("취소됨","Cancelled");}
        }
        StudioHandoffRecord FreezeHandoff()
        {
            if(handoffDraft==null||selected==null)throw new InvalidDataException();
            var choice=handoffDestination.SelectedItem as HandoffChoice;if(choice==null)throw new InvalidDataException();
            handoffDraft.TargetProvider=StudioProviderCatalog.FromIndex(handoffProvider.SelectedIndex)??"codex";handoffDraft.TargetModel=choice.Model;handoffDraft.ResumeExecutionId=choice.ExecutionId;handoffDraft.Payload=handoffEditor.Text;
            return StudioHandoff.Prepare(selected,handoffDraft);
        }
        void RestoreEditableHandoff(StudioHandoffRecord frozen)
        {
            var index=selected.Handoffs.IndexOf(frozen);if(index>=0)selected.Handoffs[index]=handoffDraft;dirty=true;UpdateHandoffButtons();
        }
        void CopyHandoff()
        {
            if(!handoffCopy.IsEnabled)return;
            if(storageBlocked){try{Clipboard.SetText(handoffEditor.Text);handoffNotice.Text=S("전달문을 복사했습니다. 저장 실패 상태와 외부 전송 미확인 상태는 유지됩니다.","Context copied. Saving still needs attention; external delivery is unverified.");}catch{handoffNotice.Text=S("복사하지 못했습니다. 직접 선택해 복사하세요.","Could not copy. Select and copy the text manually.");}return;}
            StudioHandoffRecord frozen=null;
            try{
                frozen=FreezeHandoff();dirty=true;if(!SaveCurrent()){RestoreEditableHandoff(frozen);handoffNotice.Text=StudioFailure.Guidance(StudioFailureKind.Storage,L.English);return;}
                Clipboard.SetText(frozen.Payload);StudioHandoff.MarkExported(frozen);dirty=true;var saved=SaveCurrent();handoffDraft=null;
                handoffNotice.Text=saved?S("복사했습니다. 대상 AI에 직접 붙여넣으세요. 실제 전송·실행 여부는 Nephylo가 확인하지 않습니다.","Copied. Paste it into the destination AI yourself. Nephylo cannot verify external delivery or execution."):S("복사했지만 기록 저장에 실패했습니다. 닫기 전에 저장을 다시 시도하세요.","Copied, but saving the record failed. Retry saving before closing.");UpdateHandoffHistory();UpdateHandoffButtons();
            }catch{if(frozen!=null&&frozen.State=="prepared")RestoreEditableHandoff(frozen);handoffNotice.Text=S("복사하지 못했습니다. 대화 변경 여부·전달문 크기·저장 상태를 확인하세요.","Could not copy. Check for conversation changes, context size and storage errors.");}
        }
        async Task SubmitHandoff()
        {
            if(!handoffSend.IsEnabled)return;
            var source=CurrentExecution();var previousModel=selected.Model;var previousThread=selected.ThreadId;var choice=(HandoffChoice)handoffDestination.SelectedItem;
            var sourceProvider=activeProvider;var targetProvider=HandoffProvider();var targetSlot=Slot(targetProvider);
            var reviewedClient=targetSlot.Client;var reviewedConnection=reviewedClient.ConnectionId;var reviewedIdentity=reviewedClient.AccountIdentity;
            StudioHandoffRecord frozen=null;StudioExecutionSession target=null;bool created=false;
            try{
                connecting=true;Enable();await RefreshProviderAccount(targetSlot);if(closed)return;connecting=false;Enable();
                var checkedIdentity=targetSlot.Client.AccountIdentity;
                if(Slot(targetProvider)!=targetSlot||targetSlot.Client!=reviewedClient||targetSlot.Client.ConnectionId!=reviewedConnection||reviewedIdentity==null||checkedIdentity==null||!reviewedIdentity.IsAuthenticated||!checkedIdentity.IsAuthenticated||reviewedIdentity.Fingerprint!=checkedIdentity.Fingerprint||reviewedIdentity.Type!=checkedIdentity.Type){
                    RefreshHandoffTargets();handoffNotice.Text=S("대상 계정이 변경되어 전송하지 않았습니다. 대상 연결·모델·전달문을 다시 확인한 뒤 전송하세요.","Destination account changed; nothing was sent. Review the destination, model and context before sending again.");return;
                }
                if(!TargetReady()){handoffNotice.Text=S("대상 인증·모델 상태가 변경됐습니다. 연결과 모델을 다시 확인하세요.","Destination authentication/model changed. Recheck its connection and model.");return;}
                frozen=FreezeHandoff();dirty=true;if(!SaveCurrent()){RestoreEditableHandoff(frozen);handoffNotice.Text=StudioFailure.Guidance(StudioFailureKind.Storage,L.English);return;}
                if(choice.ExecutionId==null){target=StudioSessions.Start(selected,targetProvider,targetSlot.Client.Profile,choice.Model,targetSlot.Client.AccountIdentity==null?null:targetSlot.Client.AccountIdentity.Fingerprint);created=true;}
                else target=StudioSessions.Activate(selected,choice.ExecutionId);
                frozen.TargetExecutionId=target.Id;dirty=true;
                if(!SaveCurrent()){
                    if(created)selected.ProviderSessions.Remove(target);selected.ActiveExecutionId=source.Id;selected.Model=previousModel;selected.ThreadId=previousThread;frozen.TargetExecutionId=null;RestoreEditableHandoff(frozen);ApplyProvider(sourceProvider);Enable();handoffNotice.Text=StudioFailure.Guidance(StudioFailureKind.Storage,L.English);return;
                }
                ApplyProvider(targetProvider);executionConfirmations[target.Id]=client.ConnectionId;handoffEditing=false;motion.Show(handoffPanel,false);handoffDraft=null;RestoreModelSelection();Render();
                await SendCore(frozen.Payload,frozen);
            }catch{
                if(frozen!=null&&frozen.State=="prepared"){
                    if(target!=null){if(created)selected.ProviderSessions.Remove(target);selected.ActiveExecutionId=source.Id;selected.Model=previousModel;selected.ThreadId=previousThread;frozen.TargetExecutionId=null;ApplyProvider(sourceProvider);}
                    RestoreEditableHandoff(frozen);
                }
                handoffNotice.Text=S("문맥 전달을 준비하지 못했습니다. 원래 대화는 유지했습니다.","Could not prepare the transfer. The source conversation is preserved.");
                Enable();
            }finally{if(!closed){connecting=false;Enable();}}
        }
    }
}
