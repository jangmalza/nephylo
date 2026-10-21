using System;
using System.Collections.Generic;
using System.Globalization;
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
        readonly TextBox search=new TextBox();
        readonly ComboBox projectFilter=new ComboBox();
        readonly CheckBox archived=new CheckBox();
        readonly TextBlock heading=new TextBlock(),connectionState=new TextBlock(),listEmpty=new TextBlock();
        readonly Button sidebarToggle=new Button(),rename=new Button(),archive=new Button(),cancelLogin=new Button();
        readonly Button drawerClose=new Button();
        ColumnDefinition sidebarColumn;
        Border sidebar;
        ScrollViewer taskHeaderHost;
        bool sidebarCollapsed,refreshingList,updatingProjects,narrowSidebarOpen;
        Brush surface,line,muted;
        sealed class ProjectOption
        {
            internal string Path;
            internal string Label;
            public override string ToString(){return Label;}
        }
        static string ProjectName(string path)
        {
            if(String.IsNullOrEmpty(path))return S("프로젝트 미지정","No project");
            try { var name=Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));return String.IsNullOrEmpty(name)?path:name; }
            catch { return path; }
        }
        internal sealed class ProjectHeading : IValueConverter
        {
            public object Convert(object value,Type targetType,object parameter,CultureInfo culture){return ProjectName(value as string);}
            public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture){throw new NotSupportedException();}
        }
        void Build()
        {
            var solid=Background as SolidColorBrush;var light=solid!=null&&solid.Color.R+solid.Color.G+solid.Color.B>400;
            surface=new SolidColorBrush(light?Color.FromRgb(248,248,248):Color.FromRgb(36,36,36));
            sidebarSurface=new SolidColorBrush(light?Color.FromRgb(241,241,241):Color.FromRgb(21,21,21));composerSurface=new SolidColorBrush(light?Color.FromRgb(248,248,248):Color.FromRgb(37,37,37));hoverSurface=new SolidColorBrush(light?Color.FromRgb(230,230,230):Color.FromRgb(49,49,49));Background=new SolidColorBrush(light?Colors.White:Color.FromRgb(27,27,27));
            line=new SolidColorBrush(light?Color.FromRgb(216,216,216):Color.FromRgb(57,57,57));
            muted=new SolidColorBrush(light?Color.FromRgb(90,90,90):Color.FromRgb(166,166,166));
            TextOptions.SetTextFormattingMode(this,TextFormattingMode.Display);
            var root=new Grid();workspaceRoot=root;sidebarColumn=new ColumnDefinition{Width=new GridLength(244)};root.ColumnDefinitions.Add(sidebarColumn);root.ColumnDefinitions.Add(new ColumnDefinition());detailsColumn=new ColumnDefinition{Width=new GridLength(0)};root.ColumnDefinitions.Add(detailsColumn);BuildWindowFrame(root);
            sidebar=new Border{Background=sidebarSurface,Padding=new Thickness(10,10,10,0)};root.Children.Add(sidebar);
            drawerShade.Background=new SolidColorBrush(Color.FromArgb(100,0,0,0));drawerShade.Visibility=Visibility.Collapsed;Grid.SetColumnSpan(drawerShade,3);Panel.SetZIndex(drawerShade,3);root.Children.Add(drawerShade);drawerShade.MouseLeftButtonDown+=(s,e)=>{if(detailsOpen)SetDetailsOpen(false);else CloseNarrowSidebar();e.Handled=true;};
            var nav=new DockPanel();sidebar.Child=nav;
            BuildSidebarIntro(nav);BuildSidebarFooter(nav);
            var listHost=new Grid();nav.Children.Add(listHost);
            list.Background=Brushes.Transparent;list.Foreground=Foreground;list.BorderThickness=new Thickness(0);list.FontSize=13;list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled);
            // The Windows default disabled ListBox template paints a light system surface.
            // Keep ADE's own palette while input is locked during a turn or context review.
            var listSurface=new FrameworkElementFactory(typeof(Border));listSurface.SetBinding(Border.BackgroundProperty,new Binding("Background"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            var listScroll=new FrameworkElementFactory(typeof(ScrollViewer));listScroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,ScrollBarVisibility.Auto);listScroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled);listScroll.SetValue(ScrollViewer.CanContentScrollProperty,true);listScroll.SetValue(Control.FocusableProperty,false);
            listScroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));listSurface.AppendChild(listScroll);list.Template=new ControlTemplate(typeof(ListBox)){VisualTree=listSurface};
            AutomationProperties.SetName(list,S("프로젝트별 대화","Conversations by project"));
            var itemPanel=new FrameworkElementFactory(typeof(StackPanel));itemPanel.SetValue(FrameworkElement.MarginProperty,new Thickness(12,9,10,9));
            var itemTitle=new FrameworkElementFactory(typeof(TextBlock));itemTitle.SetBinding(TextBlock.TextProperty,new Binding("Title"));itemTitle.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);itemTitle.SetValue(FrameworkElement.MaxWidthProperty,176.0);itemTitle.SetValue(TextBlock.FontSizeProperty,13.0);itemPanel.AppendChild(itemTitle);
            var itemMeta=new FrameworkElementFactory(typeof(DockPanel));itemMeta.SetValue(FrameworkElement.MarginProperty,new Thickness(0,5,0,0));itemPanel.AppendChild(itemMeta);var itemTime=new FrameworkElementFactory(typeof(TextBlock));itemTime.SetBinding(TextBlock.TextProperty,new Binding{Converter=new ConversationTimestamp()});itemTime.SetValue(TextBlock.FontSizeProperty,10.0);itemTime.SetValue(TextBlock.ForegroundProperty,muted);itemTime.SetValue(FrameworkElement.MarginProperty,new Thickness(8,0,0,0));itemTime.SetValue(DockPanel.DockProperty,Dock.Right);itemMeta.AppendChild(itemTime);
            var itemModel=new FrameworkElementFactory(typeof(TextBlock));var summaryBinding=new Binding{Converter=new ConversationSummary(this)};itemModel.SetBinding(TextBlock.TextProperty,summaryBinding);itemModel.SetBinding(FrameworkElement.ToolTipProperty,new Binding{Converter=new ConversationSummary(this)});itemModel.SetValue(TextBlock.FontSizeProperty,10.0);itemModel.SetValue(TextBlock.ForegroundProperty,muted);itemModel.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);itemMeta.AppendChild(itemModel);
            list.ItemTemplate=new DataTemplate{VisualTree=itemPanel};
            var rowBorder=new FrameworkElementFactory(typeof(Border),"row");rowBorder.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));rowBorder.SetValue(Border.BorderThicknessProperty,new Thickness(1));rowBorder.SetValue(Border.BorderBrushProperty,Brushes.Transparent);rowBorder.SetValue(Border.BackgroundProperty,Brushes.Transparent);
            var rowContent=new FrameworkElementFactory(typeof(ContentPresenter));rowContent.SetBinding(ContentPresenter.ContentProperty,new Binding("Content"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});rowContent.SetBinding(ContentPresenter.ContentTemplateProperty,new Binding("ContentTemplate"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});rowBorder.AppendChild(rowContent);
            var rowTemplate=new ControlTemplate(typeof(ListBoxItem)){VisualTree=rowBorder};
            var hoverRow=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hoverRow.Setters.Add(new Setter(Border.BackgroundProperty,surface,"row"));rowTemplate.Triggers.Add(hoverRow);
            var selectedRow=new Trigger{Property=ListBoxItem.IsSelectedProperty,Value=true};selectedRow.Setters.Add(new Setter(Border.BackgroundProperty,new SolidColorBrush(light?Color.FromRgb(228,228,228):Color.FromRgb(43,43,43)),"row"));rowTemplate.Triggers.Add(selectedRow);
            var focusedRow=new Trigger{Property=UIElement.IsKeyboardFocusWithinProperty,Value=true};focusedRow.Setters.Add(new Setter(Border.BorderBrushProperty,Foreground,"row"));rowTemplate.Triggers.Add(focusedRow);
            var rowStyle=new Style(typeof(ListBoxItem));rowStyle.Setters.Add(new Setter(Control.TemplateProperty,rowTemplate));rowStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));rowStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(0,2,0,2)));
            var fullTitle=new MultiBinding{StringFormat="{0}\n{1}"};fullTitle.Bindings.Add(new Binding("Title"));fullTitle.Bindings.Add(new Binding("Project"));rowStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,fullTitle));rowStyle.Setters.Add(new Setter(AutomationProperties.HelpTextProperty,new Binding("Title")));list.ItemContainerStyle=rowStyle;
            var groupHeader=new FrameworkElementFactory(typeof(TextBlock));groupHeader.SetBinding(TextBlock.TextProperty,new Binding("Name"){Converter=new ProjectHeading()});groupHeader.SetBinding(FrameworkElement.ToolTipProperty,new Binding("Name"));groupHeader.SetValue(TextBlock.FontSizeProperty,11.0);groupHeader.SetValue(TextBlock.ForegroundProperty,muted);groupHeader.SetValue(FrameworkElement.MarginProperty,new Thickness(10,16,5,7));groupHeader.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);
            var groupRow=new FrameworkElementFactory(typeof(DockPanel));var groupIcon=new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));groupIcon.SetValue(System.Windows.Shapes.Path.DataProperty,Geometry.Parse("M1,4 L1,12 L15,12 L15,4 L8,4 L6,2 L1,2 Z"));groupIcon.SetValue(System.Windows.Shapes.Shape.StrokeProperty,muted);groupIcon.SetValue(FrameworkElement.WidthProperty,14.0);groupIcon.SetValue(FrameworkElement.HeightProperty,14.0);groupIcon.SetValue(FrameworkElement.MarginProperty,new Thickness(9,16,6,7));groupIcon.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);groupRow.AppendChild(groupIcon);groupHeader.SetValue(FrameworkElement.MarginProperty,new Thickness(0,16,5,7));groupRow.AppendChild(groupHeader);list.GroupStyle.Add(new GroupStyle{HeaderTemplate=new DataTemplate{VisualTree=groupRow}});
            list.SelectionChanged+=(s,e)=>{
                if(refreshingList||busy||restoringDraft||!(list.SelectedItem is StudioSession))return;
                var next=(StudioSession)list.SelectedItem;if(next==selected)return;
                if(!SaveCurrent()){restoringDraft=true;list.SelectedItem=selected;restoringDraft=false;return;}
                CaptureWorkspaceView();selected=next;restoringDraft=true;input.Text=selected.Draft??"";restoringDraft=false;dirty=false;RestoreSessionProvider();RestoreModelSelection();Render();RestoreDraftView();RevealSelectedConversation();if((ActualWidth>0?ActualWidth:Width)<960)CloseNarrowSidebar();SaveWorkspace();
            };
            listHost.Children.Add(list);listEmpty.Text=S("일치하는 대화가 없습니다.","No matching conversations.");listEmpty.FontSize=12;listEmpty.Foreground=muted;listEmpty.TextWrapping=TextWrapping.Wrap;listEmpty.Margin=new Thickness(6,18,6,0);listEmpty.IsHitTestVisible=false;listHost.Children.Add(listEmpty);

            var main=new Grid{Margin=new Thickness(22,12,22,10)};workspaceMain=main;
            var mainSurface=new Border{Background=Background,BorderBrush=line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(9),Child=main};Grid.SetColumn(mainSurface,1);root.Children.Add(mainSurface);
            main.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});main.RowDefinitions.Add(new RowDefinition());main.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var top=new StackPanel();taskHeaderHost=new ScrollViewer{Content=top,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};main.Children.Add(taskHeaderHost);BuildMainHeading(top);
            transcript=new StudioMessageView(Background,Foreground);transcript.ProviderIconFactory=p=>StudioVisuals.Provider(p,Foreground,18);transcript.AttachWorkspace(workspaceState.Views,MarkWorkspaceDirty,motion);Grid.SetRow(transcript,1);main.Children.Add(transcript);

            var bottom=new StackPanel{Margin=new Thickness(10,8,10,0),MaxWidth=780,HorizontalAlignment=HorizontalAlignment.Stretch};composerBottom=bottom;Grid.SetRow(bottom,2);main.Children.Add(bottom);BuildWorkProgress(bottom);BuildStartCenter();
            status.TextWrapping=TextWrapping.Wrap;status.FontSize=12;status.Margin=new Thickness(0,0,0,8);bottom.Children.Add(status);AutomationProperties.SetName(status,S("작업 상태","Task status"));
            saveRecovery.Children.Add(Setup(new Button(),S("저장 다시 시도","Retry save"),()=>{if(SaveCurrent()){status.Text=S("저장했습니다.","Saved.");Enable();}}));
            saveRecovery.Children.Add(Setup(new Button(),S("대화·초안 복사","Copy conversation + draft"),()=>{try{Clipboard.SetText(RecoveryText());status.Text=S("복사했습니다. 저장 실패 상태는 유지됩니다.","Copied. Saving still needs attention.");}catch{status.Text=S("복사하지 못했습니다. 텍스트를 선택해 직접 복사하세요.","Could not copy. Select and copy the text manually.");}}));
            saveRecovery.Children.Add(Setup(new Button(),S("저장하지 않고 닫기","Close without saving"),()=>{if(LocalizedMessageBox.Show(S("저장되지 않은 내용을 잃을 수 있습니다. 닫을까요?","Unsaved content may be lost. Close anyway?"),"Nephylo ADE",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes){discardOnClose=true;Close();}}));
            foreach(Button button in saveRecovery.Children){button.FontSize=11;button.Padding=new Thickness(8,5,8,5);button.Margin=new Thickness(0,0,6,6);}
            bottom.Children.Add(saveRecovery);
            BuildComposerReadiness(bottom);
            var composer=new Border{BorderBrush=line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(13),Background=composerSurface,Padding=new Thickness(12,10,12,10)};composerBorder=composer;bottom.Children.Add(composer);
            var compose=new StackPanel();composer.Child=compose;
            var inputHost=new Grid();compose.Children.Add(inputHost);
            input.AcceptsReturn=true;input.TextWrapping=TextWrapping.Wrap;input.Height=76;input.MaxLength=32000;input.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;input.Padding=new Thickness(0,4,0,4);input.BorderThickness=new Thickness(0);input.Background=Brushes.Transparent;input.FontSize=14;inputHost.Children.Add(input);input.GotKeyboardFocus+=(s,e)=>composer.BorderBrush=muted;input.LostKeyboardFocus+=(s,e)=>composer.BorderBrush=line;
            var placeholder=new TextBlock{Text=S("이어서 작업할 내용을 입력하세요…","Describe what to work on next…"),Foreground=muted,FontSize=14,Margin=new Thickness(1,5,0,0),IsHitTestVisible=false};inputHost.Children.Add(placeholder);
            AutomationProperties.SetName(input,S("질문 입력","Message"));
            input.TextChanged+=(s,e)=>{placeholder.Visibility=String.IsNullOrEmpty(input.Text)?Visibility.Visible:Visibility.Collapsed;UpdateInputHeight();if(restoringDraft||busy||selected==null)return;selected.Draft=input.Text;dirty=true;if(selected.Messages.Count==0&&!selected.TitleIsCustom&&!String.IsNullOrWhiteSpace(input.Text)){selected.Title=input.Text.Length>36?input.Text.Substring(0,36)+"…":input.Text;heading.Text=selected.Title;UpdateConversationRowBindings(list);}if(workflowSession==selected.Id&&!routingInProgress)workflowPhase="idle";Enable();};
            input.SelectionChanged+=(s,e)=>{if(!restoringDraft&&!busy)CaptureDraftView();};
            input.PreviewKeyDown+=async(s,e)=>{if(e.Key==Key.Enter&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){e.Handled=true;if(send.IsEnabled)await Send();}};
            var composeActions=new DockPanel{Margin=new Thickness(0,8,0,0)};compose.Children.Add(composeActions);
            var sendActions=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(sendActions,Dock.Right);composeActions.Children.Add(sendActions);
            sendActions.Children.Add(composerAction);sendActions.Children.Add(Setup(stop,S("중단","Stop"),async()=>await Stop()));sendActions.Children.Add(Setup(send,"↑",async()=>await Send()));send.Margin=new Thickness(0);send.ToolTip="Ctrl+Enter";send.Width=32;send.MinHeight=32;send.Padding=new Thickness(0);send.FontSize=18;AutomationProperties.SetName(send,S("보내기","Send"));
            send.Background=Foreground;send.Foreground=Background;send.BorderBrush=Foreground;
            var selectionRow=new DockPanel();composeActions.Children.Add(selectionRow);
            Setup(choose,S("프로젝트 선택","Choose project"),ChooseProject);choose.MinHeight=30;choose.FontSize=11;choose.Margin=new Thickness(4,0,0,0);choose.Padding=new Thickness(7,5,7,5);QuietButton(choose);AutomationProperties.SetName(choose,S("프로젝트 폴더 선택","Choose project folder"));var projectChip=new StackPanel{Orientation=Orientation.Horizontal};var projectIcon=StudioVisuals.Icon("folder",muted,12);projectIcon.Margin=new Thickness(0,0,6,0);projectChip.Children.Add(projectIcon);composerProjectName.TextTrimming=TextTrimming.CharacterEllipsis;projectChip.Children.Add(composerProjectName);choose.Content=projectChip;DockPanel.SetDock(choose,Dock.Right);selectionRow.Children.Add(choose);
            var selectionHost=new Grid();selectionRow.Children.Add(selectionHost);var selectors=composerSelectors;selectors.VerticalAlignment=VerticalAlignment.Center;selectionHost.Children.Add(selectors);BuildPinnedComposer(selectionHost);
            Setup(newModelButton,S("AI·모델 선택","AI and model selection"),OpenComposerModelMenu);QuietButton(newModelButton);newModelButton.Padding=new Thickness(6,5,6,5);newModelButton.MinHeight=30;newModelButton.Margin=new Thickness(0);AutomationProperties.SetName(newModelButton,S("AI·모델 선택","AI and model selection"));var modelLabel=new StackPanel{Orientation=Orientation.Horizontal};newModelIcon=StudioVisuals.Icon("spark",muted,14);newModelIcon.Margin=new Thickness(0,0,7,0);modelLabel.Children.Add(newModelIcon);newModelCaption.FontSize=11;newModelCaption.MaxWidth=210;newModelCaption.TextTrimming=TextTrimming.CharacterEllipsis;modelLabel.Children.Add(newModelCaption);var chevron=StudioVisuals.Icon("down",muted,10);chevron.Margin=new Thickness(7,0,0,0);modelLabel.Children.Add(chevron);newModelButton.Content=modelLabel;selectors.Children.Add(newModelButton);
            // Keep the original selection controls and event handlers as the sole
            // owners of model/provider persistence. The compact menu delegates to them.
            var selectorFields=new WrapPanel{Visibility=Visibility.Collapsed};compose.Children.Add(selectorFields);BuildRoutingControls(selectorFields,compose);BuildProviderPicker(selectorFields);selectorFields.Children.Add(models);
            motion.Feedback(newModelButton);motion.Feedback(providerPicker);motion.Feedback(routingMode);motion.Feedback(models);
            models.MinWidth=140;models.MaxWidth=215;models.FontSize=12;models.MinHeight=34;AutomationProperties.SetName(models,S("Codex 모델","Codex model"));
            modelPlaceholder.Text=S("모델 선택","Choose model");modelPlaceholder.Visibility=Visibility.Collapsed;
            models.SelectionChanged+=(s,e)=>{if(restoringModel||busy||selected==null)return;if(ModelPinned()){RestoreModelSelection();return;}var model=models.SelectedItem as StudioModel;if(model==null)return;selected.Model=model.Id;selected.Route=null;dirty=true;if(SaveCurrent()){displayedFailure=null;modelNoticeActive=false;status.Text=S("모델을 선택했습니다. 작업을 시작하면 이 모델을 유지합니다.","Model selected. This task will keep this model once started.");}models.ToolTip=null;Enable();};
            var help=providerHelp;help.FontSize=11;help.Foreground=muted;help.Margin=new Thickness(0,8,0,0);help.TextWrapping=TextWrapping.Wrap;
            help.ToolTip=S("미리보기 · Windows · Codex 수동 선택\n질문과 필요한 프로젝트 내용은 OpenAI로 전송됩니다. 파일 수정·권한 확대 요청은 거절합니다.","Preview · Windows · Manual Codex selection\nPrompts and relevant project content go to OpenAI. File-change and permission-escalation requests are declined.");
            // Disclosure remains visible before any first send; full details stay available afterwards.
            var disclosure=providerDisclosure;disclosure.FontSize=11;disclosure.Foreground=muted;disclosure.TextWrapping=TextWrapping.Wrap;disclosure.Margin=new Thickness(2,3,0,0);bottom.Children.Add(disclosure);
            var caption=new DockPanel{Margin=new Thickness(3,8,3,0)};bottom.Children.Add(caption);var shortcut=new TextBlock{Text=S("Ctrl + Enter로 보내기","Ctrl + Enter to send"),FontSize=10,Foreground=muted};DockPanel.SetDock(shortcut,Dock.Right);caption.Children.Add(shortcut);var scope=Setup(new Button(),"",()=>{scopeDisclosure.IsExpanded=true;SetDetailsOpen(true);});QuietButton(scope);scope.Padding=new Thickness(0);scope.MinHeight=18;scope.Margin=new Thickness(0);var scopeRow=new StackPanel{Orientation=Orientation.Horizontal};var shield=StudioVisuals.Icon("shield",muted,11);shield.Margin=new Thickness(0,0,6,0);scopeRow.Children.Add(shield);scopeCaption.FontSize=10;scopeCaption.Foreground=muted;scopeRow.Children.Add(scopeCaption);scope.Content=scopeRow;AutomationProperties.SetName(scope,S("권한·전송 범위","Permissions and data sent"));caption.Children.Add(scope);
            PreviewKeyDown+=(s,e)=>{if(HandleRenameKey(e))return;if(e.Key==Key.K&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){FocusConversationSearch();e.Handled=true;}else if(e.Key==Key.N&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){NewSession();e.Handled=true;}else if(e.Key==Key.Escape&&!handoffEditing&&searchHost.Visibility==Visibility.Visible&&search.IsKeyboardFocusWithin){CloseConversationSearch();e.Handled=true;}else if(e.Key==Key.Escape&&!handoffEditing&&(narrowSidebarOpen||detailsOpen)){if(detailsOpen)SetDetailsOpen(false);else CloseNarrowSidebar();e.Handled=true;}else if(e.Key==Key.H&&(Keyboard.Modifiers&(ModifierKeys.Control|ModifierKeys.Shift))==(ModifierKeys.Control|ModifierKeys.Shift)){OpenHandoff();e.Handled=true;}};
            mainReady=true;Enable();
        }
        void RefreshList()
        {
            if(refreshingList)return;refreshingList=true;
            try{
                var prior=(projectFilter.SelectedItem as ProjectOption);var priorPath=prior==null?null:prior.Path;
                updatingProjects=true;projectFilter.Items.Clear();projectFilter.Items.Add(new ProjectOption{Label=S("모든 프로젝트","All projects")});
                foreach(var path in sessions.Select(x=>x.Project).Where(x=>!String.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(ProjectName))projectFilter.Items.Add(new ProjectOption{Path=path,Label=ProjectName(path)});
                projectFilter.SelectedItem=projectFilter.Items.Cast<ProjectOption>().FirstOrDefault(x=>String.Equals(x.Path,priorPath,StringComparison.OrdinalIgnoreCase))??projectFilter.Items[0];updatingProjects=false;
                var pathFilter=((ProjectOption)projectFilter.SelectedItem).Path;var query=search.Text.Trim();
                var filtered=sessions.Where(x=>x.Archived==(archived.IsChecked==true)&&(pathFilter==null||String.Equals(x.Project,pathFilter,StringComparison.OrdinalIgnoreCase))&&(query.Length==0||(x.Title??"").IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0||(x.Project??"").IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0)).ToList();
                var view=new ListCollectionView(filtered);view.GroupDescriptions.Add(new PropertyGroupDescription("Project"));list.ItemsSource=view;
                list.SelectedItem=filtered.Contains(selected)?selected:null;listEmpty.Visibility=filtered.Count==0?Visibility.Visible:Visibility.Collapsed;RevealSelectedConversation();
            }finally{updatingProjects=false;refreshingList=false;}
        }
        void SaveWorkspace()
        {
            if(demoMode)return;
            try{workspaceState.SelectedSessionId=selected==null?null:selected.Id;workspaceState.SidebarCollapsed=sidebarCollapsed;workspaceState.DetailsOpen=detailsOpen;foreach(var key in workspaceState.Views.Keys.Where(k=>!sessions.Any(x=>x.Id==k)).ToList())workspaceState.Views.Remove(key);store.SaveWorkspace(workspaceState);workspaceDirty=false;}
            catch{sidebarToggle.ToolTip=S("화면 배치를 저장하지 못했습니다. 대화 저장 상태는 아래에서 확인하세요.","Could not save layout preferences. Conversation save status is shown below.");}
        }
        void ToggleSidebar(){if((ActualWidth>0?ActualWidth:Width)<960){narrowSidebarOpen=!narrowSidebarOpen;if(narrowSidebarOpen)detailsOpen=false;}else sidebarCollapsed=!sidebarCollapsed;ApplyPanels(true);SaveWorkspace();}
        void ApplySidebar()
        {
            ApplyPanels(false);
        }
        void RenameConversation()
        {
            OpenRenamePanel();
        }
        void ToggleArchive()
        {
            if(selected==null||busy||connecting||!SaveCurrent())return;
            var previous=selected.Archived;selected.Archived=!previous;dirty=true;
            if(!SaveCurrent()){selected.Archived=previous;Enable();return;}
            // Keep the just-archived conversation visible and explicitly read-only until restored.
            archived.IsChecked=selected.Archived;RefreshList();Render();
        }
        void CancelLogin()
        {
            if(!loginPending)return;var current=client;client=null;loginPending=false;authenticated=false;catalogReady=false;connecting=false;ClearQuota();if(current!=null)current.Dispose();
            status.Text=S("로그인을 취소했습니다. Codex 연결로 다시 시작할 수 있습니다.","Sign-in cancelled. Use Connect Codex to try again.");Enable();
        }
    }
}
