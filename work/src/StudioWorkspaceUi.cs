using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageWidget
{
    internal sealed partial class StudioWindow
    {
        Grid workspaceRoot,workspaceMain;
        ColumnDefinition detailsColumn;
        readonly Border drawerShade=new Border();
        readonly Button closeDetails=new Button();
        readonly Button detailsRoutingSettings=new Button();
        readonly CheckBox motionPreference=new CheckBox();
        readonly TextBlock detailRoute=new TextBlock(),detailDisclosure=new TextBlock();
        readonly TextBlock detailRouteHistory=new TextBlock(),detailRouteAdvanced=new TextBlock(),detailQuotaCaption=new TextBlock();
        readonly ScrollViewer detailsScroll=new ScrollViewer();
        Expander quotaDisclosure,routeDisclosure,scopeDisclosure;
        readonly Border workProgress=new Border();
        readonly TextBlock[] workSteps={new TextBlock(),new TextBlock(),new TextBlock(),new TextBlock()};
        readonly TextBlock workPhaseLabel=new TextBlock();
        readonly TextBlock workIndicator=new TextBlock();
        readonly DispatcherTimer workspaceCheckpoint=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(600)};
        StudioWorkspaceState workspaceState=new StudioWorkspaceState();
        StudioMotion motion;
        bool detailsOpen,workspaceDirty;
        string workflowSession,workflowPhase="idle";
        int workflowReached;
        bool workflowAutomatic,workflowPrepared,workflowConnectionOnly;

        void InitializeWorkspacePresentation()
        {
            workspaceState=demoMode?new StudioWorkspaceState():store.LoadWorkspace();
            detailsOpen=workspaceState.DetailsOpen;
            motion=new StudioMotion(()=>!closed&&IsVisible&&workspaceState.AnimationsEnabled!=false);
            workspaceCheckpoint.Tick+=(s,e)=>{if(workspaceDirty&&!closed){workspaceDirty=false;SaveWorkspace();}};workspaceCheckpoint.Start();
            SystemParameters.StaticPropertyChanged+=SystemMotionChanged;
            Closed+=(s,e)=>{workspaceCheckpoint.Stop();SystemParameters.StaticPropertyChanged-=SystemMotionChanged;motion.Dispose();};
        }
        void SystemMotionChanged(object sender,PropertyChangedEventArgs e)
        {
            if(e.PropertyName!="ClientAreaAnimation"||closed||Dispatcher.HasShutdownStarted)return;
            Dispatcher.BeginInvoke(new Action(()=>{if(closed)return;motion.StopAll();ApplyPanels(false);}));
        }
        void BuildMotionPreference(Panel parent)
        {
            motionPreference.Content=S("화면 효과","Interface motion");motionPreference.FontSize=11;motionPreference.Foreground=muted;motionPreference.Margin=new Thickness(0,0,0,12);motionPreference.IsChecked=workspaceState.AnimationsEnabled!=false;
            motionPreference.ToolTip=S("짧은 화면 전환 효과입니다. Windows에서 애니메이션을 끄면 함께 꺼집니다.","Short transitions. Windows animation preferences take precedence.");
            AutomationProperties.SetName(motionPreference,S("ADE 화면 효과","ADE interface motion"));
            RoutedEventHandler changed=(s,e)=>{workspaceState.AnimationsEnabled=motionPreference.IsChecked==true;motion.StopAll();ApplyPanels(false);SaveWorkspace();};
            motionPreference.Checked+=changed;motionPreference.Unchecked+=changed;parent.Children.Add(motionPreference);
        }
        void BuildWorkspaceDetails()
        {
            connectionDetails.Background=Background;connectionDetails.BorderBrush=line;connectionDetails.BorderThickness=new Thickness(1,0,0,0);connectionDetails.Padding=new Thickness(18,10,18,10);connectionDetails.Visibility=Visibility.Collapsed;
            Grid.SetColumn(connectionDetails,2);Panel.SetZIndex(connectionDetails,4);workspaceRoot.Children.Add(connectionDetails);
            var dock=new DockPanel();connectionDetails.Child=dock;
            var headingRow=new DockPanel{Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(headingRow,Dock.Top);dock.Children.Add(headingRow);
            IconButton(closeDetails,"close",S("작업 상세 닫기","Close task details"),()=>SetDetailsOpen(false));DockPanel.SetDock(closeDetails,Dock.Right);headingRow.Children.Add(closeDetails);headingRow.Children.Add(new TextBlock{Text=S("작업 정보","Task information"),FontSize=14,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
            var privacy=new TextBlock{Text=S("대화는 이 PC에 암호화 저장됩니다.","Conversations are encrypted on this PC."),FontSize=10,Foreground=muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,9,0,0)};DockPanel.SetDock(privacy,Dock.Bottom);dock.Children.Add(privacy);
            var body=new StackPanel();detailsScroll.Content=body;detailsScroll.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;detailsScroll.HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;dock.Children.Add(detailsScroll);
            body.Children.Add(DetailHeading(S("현재 AI","Current AI")));detailModelName.FontSize=13;detailModelName.FontWeight=FontWeights.SemiBold;detailModelName.TextWrapping=TextWrapping.Wrap;detailModelName.Margin=new Thickness(0,0,0,6);body.Children.Add(detailModelName);
            connectionState.FontSize=11;connectionState.Foreground=muted;connectionState.TextWrapping=TextWrapping.Wrap;connectionState.Margin=new Thickness(0,0,0,6);body.Children.Add(connectionState);
            var controls=new WrapPanel{Margin=new Thickness(0,0,0,10)};body.Children.Add(controls);
            controls.Children.Add(Setup(connect,S("Codex 연결","Connect Codex"),async()=>await Connect()));controls.Children.Add(Setup(login,S("브라우저 로그인","Browser sign-in"),async()=>await Login()));controls.Children.Add(Setup(cancelLogin,S("로그인 취소","Cancel sign-in"),CancelLogin));controls.Children.Add(Setup(apiKeySettings,S("API 키 설정","Configure API key"),async()=>await ConfigureGeminiApiKey()));
            foreach(var button in new[]{connect,login,cancelLogin,apiKeySettings}){button.FontSize=11;button.MinHeight=28;button.Padding=new Thickness(6,4,6,4);button.Margin=new Thickness(0,0,5,4);QuietButton(button);}
            var quotaTitle=new DockPanel{Margin=new Thickness(0,5,0,0)};body.Children.Add(quotaTitle);Setup(quotaRefresh,"",async()=>await RefreshQuota(client));quotaRefresh.Content=StudioVisuals.Icon("refresh",muted,13);quotaRefresh.Width=26;quotaRefresh.MinHeight=26;quotaRefresh.Padding=new Thickness(5);quotaRefresh.Margin=new Thickness(0);QuietButton(quotaRefresh);AutomationProperties.SetName(quotaRefresh,S("잔여량 다시 확인","Refresh allowance"));DockPanel.SetDock(quotaRefresh,Dock.Right);quotaTitle.Children.Add(quotaRefresh);quotaTitle.Children.Add(DetailHeading(S("현재 잔여량","Current allowance")));
            detailQuotaValue.Margin=new Thickness(0,0,0,7);body.Children.Add(detailQuotaValue);detailQuotaTrack.Height=4;detailQuotaTrack.CornerRadius=new CornerRadius(2);detailQuotaTrack.Background=line;detailQuotaTrack.Margin=new Thickness(0,0,0,8);detailQuotaFill.Background=Foreground;detailQuotaFill.Height=4;detailQuotaFill.HorizontalAlignment=HorizontalAlignment.Left;detailQuotaTrack.Child=detailQuotaFill;detailQuotaTrack.SizeChanged+=(s,e)=>UpdateMainQuotas();body.Children.Add(detailQuotaTrack);
            detailQuotaPeriod.FontSize=11;detailQuotaPeriod.Foreground=muted;detailQuotaPeriod.TextWrapping=TextWrapping.Wrap;body.Children.Add(detailQuotaPeriod);detailQuotaCaption.FontSize=11;detailQuotaCaption.Foreground=muted;detailQuotaCaption.LineHeight=18;detailQuotaCaption.TextWrapping=TextWrapping.Wrap;detailQuotaCaption.Margin=new Thickness(0,4,0,12);body.Children.Add(detailQuotaCaption);
            body.Children.Add(DetailHeading(S("선택 이유","Selection reason")));DetailText(detailRoute);detailRoute.FontSize=12;detailRoute.Foreground=Foreground;detailRoute.Margin=new Thickness(0,0,0,8);body.Children.Add(detailRoute);AutomationProperties.SetName(detailRoute,S("현재 작업 모델 선택 근거","Current task model selection details"));
            body.Children.Add(handoffOpen);handoffOpen.HorizontalAlignment=HorizontalAlignment.Stretch;handoffOpen.FontSize=11;handoffOpen.MinHeight=30;handoffOpen.Padding=new Thickness(8,6,8,6);handoffOpen.Margin=new Thickness(0,0,0,12);QuietButton(handoffOpen);
            quotaSummary.FontSize=12;quotaSummary.FontWeight=FontWeights.SemiBold;quotaSummary.Foreground=Foreground;quotaSummary.TextWrapping=TextWrapping.Wrap;quotaSummary.Margin=new Thickness(0,0,0,8);quotaDetails.FontSize=11;quotaDetails.Foreground=muted;quotaDetails.LineHeight=18;quotaDetails.TextWrapping=TextWrapping.Wrap;var quotaBody=new StackPanel();quotaBody.Children.Add(quotaSummary);quotaBody.Children.Add(quotaDetails);quotaDisclosure=AnimatedDisclosure(S("잔여량 상세 보기","Allowance details"),quotaBody);body.Children.Add(quotaDisclosure);
            DetailText(detailRouteHistory);DetailText(detailRouteAdvanced);var routeBody=new StackPanel();routeBody.Children.Add(detailRouteHistory);routeBody.Children.Add(detailRouteAdvanced);routeBody.Children.Add(Setup(detailsRoutingSettings,S("자동 선택 설정","Automatic selection settings"),OpenRoutingSettings));detailsRoutingSettings.FontSize=11;detailsRoutingSettings.Padding=new Thickness(8,5,8,5);detailsRoutingSettings.MinHeight=30;detailsRoutingSettings.HorizontalAlignment=HorizontalAlignment.Left;detailsRoutingSettings.Margin=new Thickness(0,10,0,8);routeDisclosure=AnimatedDisclosure(S("판단 세부 정보·설정","Selection details and settings"),routeBody);body.Children.Add(routeDisclosure);
            var scopeBody=new StackPanel();DetailText(taskHint);taskHint.Margin=new Thickness(0,0,0,10);scopeBody.Children.Add(taskHint);DetailText(providerHelp);providerHelp.Margin=new Thickness(0,0,0,8);scopeBody.Children.Add(providerHelp);DetailText(detailDisclosure);scopeBody.Children.Add(detailDisclosure);scopeDisclosure=AnimatedDisclosure(S("권한·전송 정보","Permissions and data sent"),scopeBody);body.Children.Add(scopeDisclosure);body.Children.Add(AnimatedDisclosure(S("시작 준비 확인","Start readiness details"),setupDetails));
        }
        void DetailText(TextBlock text){text.FontSize=13;text.Foreground=muted;text.LineHeight=20;text.TextWrapping=TextWrapping.Wrap;}
        TextBlock DetailHeading(string text){return new TextBlock{Text=text,FontSize=11,FontWeight=FontWeights.SemiBold,Foreground=muted,Margin=new Thickness(0,0,0,10)};}
        Expander AnimatedDisclosure(string title,FrameworkElement body)
        {
            var expander=new Expander{Header=title,Content=body,Foreground=Foreground,FontSize=12,Margin=new Thickness(0,0,0,10)};
            var stack=new FrameworkElementFactory(typeof(StackPanel));var button=new FrameworkElementFactory(typeof(ToggleButton),"heading");button.SetValue(AutomationProperties.NameProperty,title);
            var toggleShell=new FrameworkElementFactory(typeof(Border),"shell");toggleShell.SetValue(Border.CornerRadiusProperty,new CornerRadius(4));toggleShell.SetValue(Border.BorderThicknessProperty,new Thickness(1));toggleShell.SetValue(Border.BorderBrushProperty,Brushes.Transparent);toggleShell.SetValue(Border.BackgroundProperty,Brushes.Transparent);toggleShell.SetValue(Border.PaddingProperty,new Thickness(3,6,3,6));
            var toggleContent=new FrameworkElementFactory(typeof(ContentPresenter));toggleContent.SetBinding(ContentPresenter.ContentProperty,new Binding("Content"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});toggleContent.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);toggleShell.AppendChild(toggleContent);
            var toggleTemplate=new ControlTemplate(typeof(ToggleButton)){VisualTree=toggleShell};
            foreach(var property in new[]{UIElement.IsMouseOverProperty,UIElement.IsKeyboardFocusWithinProperty}){var active=new Trigger{Property=property,Value=true};active.Setters.Add(new Setter(Border.BorderBrushProperty,line,"shell"));toggleTemplate.Triggers.Add(active);}
            button.SetValue(Control.TemplateProperty,toggleTemplate);button.SetValue(Control.FocusVisualStyleProperty,null);
            button.SetBinding(ToggleButton.IsCheckedProperty,new Binding("IsExpanded"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent),Mode=BindingMode.TwoWay});
            button.SetBinding(Control.ForegroundProperty,new Binding("Foreground"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});button.SetValue(Control.BackgroundProperty,Brushes.Transparent);button.SetValue(Control.BorderThicknessProperty,new Thickness(0));button.SetValue(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Left);button.SetValue(Control.PaddingProperty,new Thickness(0,7,0,7));button.SetValue(FrameworkElement.MinHeightProperty,32.0);
            var header=new FrameworkElementFactory(typeof(StackPanel));header.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);var glyph=new FrameworkElementFactory(typeof(TextBlock),"glyph");glyph.SetValue(TextBlock.TextProperty,"›");glyph.SetValue(FrameworkElement.WidthProperty,18.0);header.AppendChild(glyph);var label=new FrameworkElementFactory(typeof(TextBlock));label.SetBinding(TextBlock.TextProperty,new Binding("Header"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});header.AppendChild(label);button.AppendChild(header);stack.AppendChild(button);
            var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetBinding(ContentPresenter.ContentProperty,new Binding("Content"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});stack.AppendChild(content);
            var template=new ControlTemplate(typeof(Expander)){VisualTree=stack};var open=new Trigger{Property=Expander.IsExpandedProperty,Value=true};open.Setters.Add(new Setter(TextBlock.TextProperty,"⌄","glyph"));template.Triggers.Add(open);expander.Template=template;body.Visibility=Visibility.Collapsed;
            expander.Expanded+=(s,e)=>motion.Fold(body,true);expander.Collapsed+=(s,e)=>motion.Fold(body,false);return expander;
        }
        void ToggleDetails(){SetDetailsOpen(!detailsOpen);}
        void SetDetailsOpen(bool open)
        {
            detailsOpen=open;if(open)narrowSidebarOpen=false;ApplyPanels(true);SaveWorkspace();
            if(open){closeDetails.Focus();Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{if(detailsOpen&&!closed)detailsScroll.ScrollToTop();}));}else detailsToggle.Focus();
        }
        void ApplyPanels(bool animate)
        {
            if(sidebar==null||workspaceRoot==null)return;
            double width=ActualWidth>0?ActualWidth:Width;bool narrow=width<960;bool navVisible=narrow?narrowSidebarOpen:!sidebarCollapsed;
            bool dockDetails=width>=1100;
            Action<FrameworkElement,bool> show=(element,visible)=>{if(animate)motion.Show(element,visible);else{element.BeginAnimation(UIElement.OpacityProperty,null);element.Opacity=1;element.Visibility=visible?Visibility.Visible:Visibility.Collapsed;element.IsHitTestVisible=visible;}};
            show(sidebar,navVisible);double navWidth=navVisible&&!narrow?244:0;
            if(animate)motion.Column(sidebarColumn,navWidth);else{sidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty,null);sidebarColumn.Width=new GridLength(navWidth);}
            Grid.SetColumnSpan(sidebar,narrow?3:1);Panel.SetZIndex(sidebar,4);sidebar.Width=narrow?248:Double.NaN;sidebar.HorizontalAlignment=narrow?HorizontalAlignment.Left:HorizontalAlignment.Stretch;
            KeyboardNavigation.SetTabNavigation(sidebar,narrow?KeyboardNavigationMode.Cycle:KeyboardNavigationMode.Continue);
            drawerClose.Visibility=narrow?Visibility.Visible:Visibility.Collapsed;
            double detailWidth=detailsOpen&&dockDetails?288:0;
            if(animate)motion.Column(detailsColumn,detailWidth);else{detailsColumn.BeginAnimation(ColumnDefinition.WidthProperty,null);detailsColumn.Width=new GridLength(detailWidth);}
            Grid.SetColumn(connectionDetails,dockDetails?2:0);Grid.SetColumnSpan(connectionDetails,dockDetails?1:3);connectionDetails.Width=dockDetails?Double.NaN:Math.Min(340,Math.Max(260,width-24));connectionDetails.HorizontalAlignment=dockDetails?HorizontalAlignment.Stretch:HorizontalAlignment.Right;
            KeyboardNavigation.SetTabNavigation(connectionDetails,dockDetails?KeyboardNavigationMode.Continue:KeyboardNavigationMode.Cycle);
            show(connectionDetails,detailsOpen);detailsToggle.ToolTip=S("연결·잔여량·모델 선택 근거·문맥 전달","Connection, allowance, model selection and context transfer");
            drawerShade.Visibility=(narrow&&navVisible||!dockDetails&&detailsOpen)?Visibility.Visible:Visibility.Collapsed;
            UpdateTaskCompactLayout();
        }
        void MarkWorkspaceDirty(){if(!demoMode&&!closed)workspaceDirty=true;}
        StudioViewBookmark CurrentBookmark()
        {
            if(selected==null)return null;StudioViewBookmark value;
            if(!workspaceState.Views.TryGetValue(selected.Id,out value)){value=new StudioViewBookmark();workspaceState.Views[selected.Id]=value;}return value;
        }
        void CaptureDraftView()
        {
            if(restoringDraft||selected==null)return;var value=CurrentBookmark();
            value.DraftSelectionStart=input.SelectionStart;value.DraftSelectionLength=input.SelectionLength;value.DraftVerticalOffset=Math.Max(0,input.VerticalOffset);MarkWorkspaceDirty();
        }
        void CaptureWorkspaceView(){CaptureDraftView();if(transcript!=null)transcript.CapturePosition();}
        void RestoreDraftView()
        {
            var current=selected;var bookmark=CurrentBookmark();if(bookmark==null)return;
            int start=bookmark.DraftSelectionStart,length=bookmark.DraftSelectionLength;double offset=bookmark.DraftVerticalOffset;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{if(closed||selected!=current)return;restoringDraft=true;try{start=Math.Min(start,input.Text.Length);input.Select(start,Math.Min(length,input.Text.Length-start));input.ScrollToVerticalOffset(offset);}finally{restoringDraft=false;}}));
        }
        void UpdateInputHeight()
        {
            bool compact=(ActualHeight>0?ActualHeight:Height)<650;int lines=Math.Max(1,(input.Text??"").Count(c=>c=='\n')+1);
            input.Height=Math.Min(compact?84:132,Math.Max(compact?48:64,lines*21+8));
        }
        void BuildWorkProgress(Panel parent)
        {
            workProgress.Margin=new Thickness(2,0,2,8);workProgress.Visibility=Visibility.Collapsed;
            var row=new WrapPanel();workProgress.Child=row;
            for(int i=0;i<workSteps.Length;i++){workSteps[i].FontSize=11;workSteps[i].Margin=new Thickness(0,0,12,3);}
            workIndicator.FontSize=15;workIndicator.Foreground=muted;workIndicator.Margin=new Thickness(0,0,7,0);row.Children.Add(workIndicator);
            workPhaseLabel.FontSize=11;workPhaseLabel.Foreground=muted;workPhaseLabel.Margin=new Thickness(0,0,0,3);row.Children.Add(workPhaseLabel);
            AutomationProperties.SetName(workProgress,S("AI 작업 진행 단계","AI task progress"));parent.Children.Add(workProgress);
        }
        void BeginWorkflow(bool automatic,bool connectionOnly=false)
        {workflowAutomatic=automatic;workflowConnectionOnly=connectionOnly;workflowSession=selected==null?null:selected.Id;workflowReached=0;workflowPrepared=false;workflowPhase="idle";SetWorkflowPhase("connecting");}
        void SetWorkflowPhase(string phase)
        {
            if(workflowPhase==phase)return;workflowPhase=phase;
            int index=phase=="connecting"||phase=="connected"?0:phase=="classifying"?1:phase=="selecting"||phase=="sending"?2:3;
            if(phase!="review"&&phase!="cancelled"&&phase!="failed"&&phase!="stopping")workflowReached=Math.Max(workflowReached,index);
            UpdateWorkspacePresentation();if(index>=0&&index<4&&motion!=null)motion.Enter(workSteps[index],false);
        }
        void UpdateWorkspacePresentation()
        {
            if(workspaceRoot==null)return;
            UpdateComposerModelControls();
            detailsRoutingSettings.IsEnabled=!busy&&!connecting&&!routingInProgress&&!handoffEditing&&!demoMode;
            var route=selected==null?null:selected.Route;
            var explanation=route==null?(selected!=null&&selected.AutoRouting?S("첫 질문을 보낼 때 현재 질문과 실제 연결 상태로 모델을 선택합니다.","The first send selects a model using the question and available connections."):S("직접 선택한 AI와 모델로 대화합니다.","This task uses the AI and model you select.")):StudioRoutingPolicy.EngineName(route.Engine)+" → "+ProviderName(route.Provider)+" / "+route.Model+"\n\n"+RouteExplanation(route);
            if(detailRouteAdvanced.Text!=explanation)detailRouteAdvanced.Text=explanation;
            UpdateDetailsSummary();
            detailDisclosure.Text=providerDisclosure.Text;
            providerDisclosure.Visibility=selected!=null&&!ModelPinned()?Visibility.Visible:Visibility.Collapsed;
            bool visible=selected!=null&&workflowSession==selected.Id&&!workflowConnectionOnly&&workflowPhase!="idle"&&workflowPhase!="connected";
            workProgress.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
            var names=new[]{S("연결 확인","Connection"),S("질문 분류","Classification"),S("모델 선택","Model"),S("답변","Response")};
            for(int i=0;i<4;i++){
                workSteps[i].Visibility=!workflowAutomatic&&i==1?Visibility.Collapsed:Visibility.Visible;
                bool done=workflowPhase=="completed"||workflowPhase=="connected"&&i==0||i<workflowReached;bool active=i==workflowReached&&!done;
                workSteps[i].Text=(done?"✓ ":active?"● ":"○ ")+names[i];workSteps[i].Foreground=done||active?Foreground:muted;workSteps[i].FontWeight=active?FontWeights.SemiBold:FontWeights.Normal;
            }
            UpdateMainScreen();
            workIndicator.Text=workflowPhase=="completed"?"✓":workflowPhase=="cancelled"||workflowPhase=="review"||workflowPhase=="failed"?"—":"◌";
            workPhaseLabel.Text=workflowPhase=="connected"?S("연결 확인됨","Connected"):workflowPhase=="answer"?S("응답 대기","Awaiting response"):workflowPhase=="streaming"?S("수신 중","Receiving"):workflowPhase=="completed"?S("응답 완료","Response received"):workflowPhase=="cancelled"?S("중단됨","Stopped"):workflowPhase=="stopping"?S("중단 요청 중","Stopping"):workflowPhase=="review"||workflowPhase=="failed"?S("확인 필요","Needs attention"):"";
            if(visible&&!storageBlocked&&!displayedFailure.HasValue&&!DeliveryUncertain()&&(busy||routingInProgress||status.Text==S("완료","Completed")))status.Visibility=Visibility.Collapsed;
            if(!storageBlocked&&!displayedFailure.HasValue&&status.Text==S("완료","Completed"))status.Visibility=Visibility.Collapsed;
        }
    }
}
