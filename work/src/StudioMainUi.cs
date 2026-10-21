using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace UsageWidget
{
    internal sealed partial class StudioWindow
    {
        Brush sidebarSurface,composerSurface,hoverSurface;
        readonly Button searchOpen=new Button(),threadMenu=new Button(),headerHandoff=new Button(),newModelButton=new Button();
        readonly Button connectionOverview=new Button(),screenSettings=new Button();
        readonly TextBlock composerProjectName=new TextBlock(),newModelCaption=new TextBlock(),scopeCaption=new TextBlock();
        readonly TextBlock detailModelName=new TextBlock(),detailQuotaValue=new TextBlock(),detailQuotaPeriod=new TextBlock();
        readonly Border detailQuotaTrack=new Border(),detailQuotaFill=new Border();
        readonly StackPanel setupDetails=new StackPanel();
        readonly Dictionary<string,TextBlock> sidebarAllowances=new Dictionary<string,TextBlock>();
        readonly StackPanel sidebarAllowanceRows=new StackPanel();
        Grid startCenter;
        StackPanel composerBottom,welcomePanel;
        FrameworkElement newModelIcon;
        Border composerBorder;
        Grid searchHost;
        bool mainReady,centeringComposer;
        bool? compactHeader;
        readonly List<Button> starterButtons=new List<Button>();
        ContextMenu activeMainMenu;
        internal sealed class ConversationTimestamp : IValueConverter
        {
            public object Convert(object value,Type targetType,object parameter,CultureInfo culture)
            {DateTime updated;if(!DateTime.TryParse((value as StudioSession)==null?null:((StudioSession)value).Updated,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out updated))return "";var local=updated.ToLocalTime();var ago=DateTime.Now-local;if(ago>=TimeSpan.Zero&&ago<TimeSpan.FromMinutes(1))return S("방금","Now");if(local.Date==DateTime.Now.Date)return local.ToString("HH:mm");if(local.Date==DateTime.Now.Date.AddDays(-1))return S("어제","Yesterday");return local.ToString("MM/dd");}
            public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture){throw new NotSupportedException();}
        }

        Button IconButton(Button b,string icon,string label,Action action)
        {
            Setup(b,label,action);b.Content=StudioVisuals.Icon(icon,muted,17);b.Width=30;b.Height=30;b.MinHeight=30;b.Padding=new Thickness(6);b.Margin=new Thickness(0);QuietButton(b);b.ToolTip=label;AutomationProperties.SetName(b,label);return b;
        }
        object LabeledIcon(string icon,string label,double size=14)
        {
            var panel=new StackPanel{Orientation=Orientation.Horizontal};var image=StudioVisuals.Icon(icon,muted,size);image.Margin=new Thickness(0,0,9,0);panel.Children.Add(image);panel.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis});return panel;
        }
        void BuildWindowFrame(Grid root)
        {
            WindowStyle=WindowStyle.None;
            WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=36,ResizeBorderThickness=new Thickness(6),CornerRadius=new CornerRadius(0),GlassFrameThickness=new Thickness(0),UseAeroCaptionButtons=false});
            var frame=new Grid{Background=sidebarSurface};frame.RowDefinitions.Add(new RowDefinition{Height=new GridLength(36)});frame.RowDefinitions.Add(new RowDefinition());Content=frame;
            var bar=new DockPanel{LastChildFill=true,Margin=new Thickness(14,0,8,0)};frame.Children.Add(bar);
            var controls=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(controls,Dock.Right);bar.Children.Add(controls);
            var minimize=IconButton(new Button(),"minus",S("최소화","Minimize"),()=>SystemCommands.MinimizeWindow(this));controls.Children.Add(minimize);
            var maximize=Setup(new Button(),"□",()=>{if(WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(this);else SystemCommands.MaximizeWindow(this);});maximize.Width=32;maximize.MinHeight=30;maximize.FontSize=15;maximize.Padding=new Thickness(0);maximize.Margin=new Thickness(3,0,3,0);QuietButton(maximize);AutomationProperties.SetName(maximize,S("최대화·복원","Maximize or restore"));controls.Children.Add(maximize);
            controls.Children.Add(IconButton(new Button(),"close",S("닫기","Close"),()=>Close()));
            foreach(UIElement button in controls.Children)WindowChrome.SetIsHitTestVisibleInChrome(button,true);
            var brand=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};brand.Children.Add(new Image{Source=Icon,Width=17,Height=17,Margin=new Thickness(0,0,8,0)});brand.Children.Add(new TextBlock{Text="Nephylo",FontSize=12,FontWeight=FontWeights.SemiBold});brand.Children.Add(new TextBlock{Text=developmentProfile==null?"ADE":"ADE · "+S("개발용","Development"),Foreground=muted,FontSize=10,Margin=new Thickness(12,2,0,0),ToolTip=developmentProfile==null?null:developmentProfile.Name+"\n"+S("별도 대화·설정","Separate conversations and settings")});bar.Children.Add(brand);
            if(developmentProfile!=null)AutomationProperties.SetName(brand,S("ADE 개발 데이터 분리","Isolated ADE development data"));
            Grid.SetRow(root,1);root.Margin=new Thickness(0,0,5,5);frame.Children.Add(root);
        }
        void BuildSidebarIntro(DockPanel nav)
        {
            var intro=new StackPanel();DockPanel.SetDock(intro,Dock.Top);nav.Children.Add(intro);
            Setup(fresh,S("새 대화","New conversation"),NewSession);fresh.Content=LabeledIcon("new",S("새 대화","New conversation"),16);fresh.FontSize=13;fresh.HorizontalContentAlignment=HorizontalAlignment.Left;fresh.Padding=new Thickness(10,8,10,8);fresh.Margin=new Thickness(0,2,0,0);QuietButton(fresh);AutomationProperties.SetName(fresh,S("새 대화","New conversation"));intro.Children.Add(fresh);
            Setup(searchOpen,S("대화 검색","Search conversations"),()=>{if(searchHost.Visibility==Visibility.Visible)CloseConversationSearch();else FocusConversationSearch();});searchOpen.Content=LabeledIcon("search",S("대화 검색","Search conversations"),16);searchOpen.FontSize=13;searchOpen.HorizontalContentAlignment=HorizontalAlignment.Left;searchOpen.Padding=new Thickness(10,8,10,8);searchOpen.Margin=new Thickness(0,2,0,0);QuietButton(searchOpen);searchOpen.ToolTip="Ctrl+K";AutomationProperties.SetName(searchOpen,S("대화 검색","Search conversations"));intro.Children.Add(searchOpen);
            searchHost=new Grid{Margin=new Thickness(6,6,6,0),Visibility=Visibility.Collapsed};intro.Children.Add(searchHost);
            search.Padding=new Thickness(8,7,8,7);search.FontSize=12;AutomationProperties.SetName(search,S("대화 제목·프로젝트 검색","Search title or project"));search.ToolTip="Ctrl+K";searchHost.Children.Add(search);
            var placeholder=new TextBlock{Text=S("제목·프로젝트 검색…","Search title or project…"),Foreground=muted,FontSize=11,Margin=new Thickness(9,8,9,0),IsHitTestVisible=false};searchHost.Children.Add(placeholder);
            search.TextChanged+=(s,e)=>{placeholder.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;if(!refreshingList)RefreshList();};
            var row=new DockPanel{Margin=new Thickness(10,22,2,3)};intro.Children.Add(row);
            var add=IconButton(new Button(),"plus",S("프로젝트에서 새 대화","New project conversation"),()=>{if(busy||connecting||handoffEditing)return;NewSession();if(choose.IsEnabled)ChooseProject();});add.Width=26;add.Height=26;add.MinHeight=26;DockPanel.SetDock(add,Dock.Right);row.Children.Add(add);
            var filter=IconButton(new Button(),"down",S("프로젝트 필터","Project filter"),()=>{projectFilter.Visibility=projectFilter.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;if(projectFilter.Visibility==Visibility.Visible){projectFilter.Focus();projectFilter.IsDropDownOpen=true;}});filter.Width=24;DockPanel.SetDock(filter,Dock.Right);row.Children.Add(filter);
            row.Children.Add(new TextBlock{Text=S("프로젝트","Projects"),Foreground=muted,FontSize=11,VerticalAlignment=VerticalAlignment.Center});
            projectFilter.Visibility=Visibility.Collapsed;projectFilter.Margin=new Thickness(6,0,6,7);projectFilter.MinHeight=30;projectFilter.FontSize=12;AutomationProperties.SetName(projectFilter,S("프로젝트 필터","Project filter"));
            projectFilter.SelectionChanged+=(s,e)=>{if(!updatingProjects){projectFilter.ToolTip=(projectFilter.SelectedItem as ProjectOption)==null?null:((ProjectOption)projectFilter.SelectedItem).Path;RefreshList();motion.Enter(list,false);}};intro.Children.Add(projectFilter);
        }
        void BuildSidebarFooter(DockPanel nav)
        {
            var foot=new StackPanel{Margin=new Thickness(8,8,8,0)};DockPanel.SetDock(foot,Dock.Bottom);nav.Children.Add(foot);
            archived.Content=LabeledIcon("archive",S("보관한 대화","Archived conversations"),13);archived.FontSize=11;archived.Foreground=muted;archived.Padding=new Thickness(0,7,0,10);archived.Checked+=(s,e)=>RefreshList();archived.Unchecked+=(s,e)=>RefreshList();AutomationProperties.SetName(archived,S("보관한 대화 보기","Show archived"));foot.Children.Add(archived);
            var archiveShell=new FrameworkElementFactory(typeof(Border),"archiveLink");archiveShell.SetValue(Border.CornerRadiusProperty,new CornerRadius(5));archiveShell.SetValue(Border.PaddingProperty,new Thickness(2,8,2,8));archiveShell.SetValue(Border.BackgroundProperty,Brushes.Transparent);var archiveContent=new FrameworkElementFactory(typeof(ContentPresenter));archiveContent.SetBinding(ContentPresenter.ContentProperty,new Binding("Content"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});archiveShell.AppendChild(archiveContent);var archiveTemplate=new ControlTemplate(typeof(CheckBox)){VisualTree=archiveShell};foreach(var property in new[]{ToggleButton.IsCheckedProperty,UIElement.IsMouseOverProperty,UIElement.IsKeyboardFocusWithinProperty}){var selected=new Trigger{Property=property,Value=true};selected.Setters.Add(new Setter(Border.BackgroundProperty,hoverSurface,"archiveLink"));archiveTemplate.Triggers.Add(selected);}archived.Template=archiveTemplate;
            var allowanceHost=new Border{BorderBrush=line,BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(0,8,0,3)};foot.Children.Add(allowanceHost);var allowances=new StackPanel();allowanceHost.Child=allowances;
            var headingRow=new DockPanel();allowances.Children.Add(headingRow);IconButton(connectionOverview,"link",S("AI 연결 관리","Manage AI connections"),OpenConnectionOverview);connectionOverview.Width=24;connectionOverview.Height=24;connectionOverview.MinHeight=24;DockPanel.SetDock(connectionOverview,Dock.Right);headingRow.Children.Add(connectionOverview);headingRow.Children.Add(new TextBlock{Text=S("연결된 AI","AI connections"),FontSize=10,Foreground=muted,VerticalAlignment=VerticalAlignment.Center});allowances.Children.Add(sidebarAllowanceRows);
            foreach(var provider in new[]{"codex","claude"}){
                string id=provider;var button=Setup(new Button(),ProviderName(id),()=>OpenConnectionOverview());QuietButton(button);button.Margin=new Thickness(0);button.Padding=new Thickness(1,4,1,4);button.MinHeight=27;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
                var row=new DockPanel();var value=new TextBlock{FontSize=10,Foreground=muted,VerticalAlignment=VerticalAlignment.Center,Text=S("확인 필요","Check needed")};sidebarAllowances[id]=value;DockPanel.SetDock(value,Dock.Right);row.Children.Add(value);var image=StudioVisuals.Provider(id,muted,13);image.Margin=new Thickness(0,0,8,0);DockPanel.SetDock(image,Dock.Left);row.Children.Add(image);row.Children.Add(new TextBlock{Text=id=="claude"?"Claude":"Codex",FontSize=11,Foreground=muted,VerticalAlignment=VerticalAlignment.Center});button.Content=row;AutomationProperties.SetName(button,ProviderName(id)+S(" 연결·잔여량"," connection and allowance"));sidebarAllowanceRows.Children.Add(button);
            }
            var bottom=new DockPanel{Margin=new Thickness(0,7,0,0)};foot.Children.Add(new Border{BorderBrush=line,BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(0,5,0,0),Child=bottom});
            var local=new TextBlock{Text=S("이 PC에 저장","Stored on this PC"),FontSize=10,Foreground=muted,VerticalAlignment=VerticalAlignment.Center,ToolTip=S("대화·초안 사본은 암호화 저장합니다. 공식 CLI 인증·세션 기록은 별도입니다.","Conversation and draft copies are encrypted. Official CLI authentication/session records are separate.")};DockPanel.SetDock(local,Dock.Right);bottom.Children.Add(local);
            Setup(screenSettings,S("설정","Settings"),OpenScreenSettings);screenSettings.Content=LabeledIcon("settings",S("설정","Settings"),13);screenSettings.FontSize=11;screenSettings.HorizontalContentAlignment=HorizontalAlignment.Left;screenSettings.Margin=new Thickness(0);screenSettings.Padding=new Thickness(0,8,0,8);QuietButton(screenSettings);AutomationProperties.SetName(screenSettings,S("ADE 화면 설정","ADE screen settings"));bottom.Children.Add(screenSettings);
            // Keep the preference control alive, but place it in a compact settings popup.
            var preferences=new StackPanel();BuildMotionPreference(preferences);screenSettings.Tag=preferences;
            foot.Children.Add(Setup(drawerClose,S("목록 닫기","Close sidebar"),ToggleSidebar));drawerClose.Margin=new Thickness(0,4,0,0);drawerClose.Visibility=Visibility.Collapsed;
        }
        void BuildMainHeading(StackPanel top)
        {
            var row=new DockPanel{Margin=new Thickness(0,4,0,16)};top.Children.Add(row);
            IconButton(sidebarToggle,"panel",S("사이드바 접기 · 펼치기","Toggle sidebar"),ToggleSidebar);sidebarToggle.Margin=new Thickness(-4,3,12,0);DockPanel.SetDock(sidebarToggle,Dock.Left);row.Children.Add(sidebarToggle);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(actions,Dock.Right);row.Children.Add(actions);
            Setup(headerHandoff,S("다른 AI로 이어가기","Continue with another AI"),OpenHandoff);headerHandoff.Content=LabeledIcon("transfer",S("다른 AI로 이어가기","Continue with another AI"),14);headerHandoff.FontSize=11;headerHandoff.Margin=new Thickness(4,0,8,0);headerHandoff.Padding=new Thickness(6,5,6,5);QuietButton(headerHandoff);AutomationProperties.SetName(headerHandoff,S("다른 AI로 이어가기 · 문맥 검토","Continue with another AI · Review context"));actions.Children.Add(headerHandoff);
            BuildTaskControls(actions,workspaceMain);
            IconButton(threadMenu,"more",S("대화 메뉴","Conversation menu"),OpenThreadMenu);threadMenu.Margin=new Thickness(5,0,0,0);actions.Children.Add(threadMenu);
            Setup(rename,S("이름 변경","Rename"),RenameConversation);Setup(archive,S("보관","Archive"),ToggleArchive);rename.Visibility=archive.Visibility=Visibility.Collapsed;actions.Children.Add(rename);actions.Children.Add(archive);
            var title=new StackPanel{VerticalAlignment=VerticalAlignment.Center};row.Children.Add(title);var breadcrumb=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,4)};var folder=StudioVisuals.Icon("folder",muted,11);folder.Margin=new Thickness(0,0,6,0);breadcrumb.Children.Add(folder);project.FontSize=10;project.Foreground=muted;project.TextTrimming=TextTrimming.CharacterEllipsis;project.MaxWidth=340;breadcrumb.Children.Add(project);title.Children.Add(breadcrumb);
            heading.FontSize=15;heading.FontWeight=FontWeights.SemiBold;heading.TextTrimming=TextTrimming.CharacterEllipsis;title.Children.Add(heading);
        }
        void BuildStartCenter()
        {
            startCenter=new Grid{Visibility=Visibility.Collapsed};startCenter.RowDefinitions.Add(new RowDefinition());startCenter.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});startCenter.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});startCenter.RowDefinitions.Add(new RowDefinition());Grid.SetRow(startCenter,1);workspaceMain.Children.Add(startCenter);
            welcomePanel=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,MaxWidth=520,Margin=new Thickness(0,0,0,24)};Grid.SetRow(welcomePanel,1);startCenter.Children.Add(welcomePanel);
            welcomePanel.Children.Add(new Image{Source=Icon,Width=38,Height=38,Margin=new Thickness(0,0,0,18)});welcomePanel.Children.Add(new TextBlock{Text=S("어떤 작업을 시작할까요?","What would you like to work on?"),FontSize=25,FontWeight=FontWeights.Normal,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap});welcomePanel.Children.Add(new TextBlock{Text=S("프로젝트를 고르고, 함께할 AI를 선택하세요.","Choose a project and the AI to work with."),FontSize=12,Foreground=muted,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,9,0,0)});
            var suggestions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,18,0,0)};Grid.SetRow(suggestions,3);startCenter.Children.Add(suggestions);
            foreach(var pair in new[]{new[]{S("코드 흐름 살펴보기","Explore code flow"),S("프로젝트의 대화 저장 흐름을 읽고, 실패할 수 있는 지점을 정리해 주세요.","Read the conversation storage flow and identify where it could fail.")},new[]{S("문서 초안 다듬기","Refine documentation"),S("처음 방문한 사용자가 이해하기 쉽게 README 구성을 제안해 주세요.","Suggest a README structure that first-time users can understand.")},new[]{S("오류 원인 정리하기","Investigate an error"),S("재시작 후 초안이 사라지는 현상의 원인을 좁히는 확인 순서를 정리해 주세요.","Outline checks to narrow down why a draft might disappear after restart.")}}){string prompt=pair[1];var button=Setup(new Button(),pair[0],()=>{if(!input.IsEnabled||!String.IsNullOrWhiteSpace(input.Text))return;input.Text=prompt;input.Focus();});button.FontSize=11;button.MinHeight=29;button.Padding=new Thickness(9,6,9,6);button.Margin=new Thickness(4,0,4,6);button.Background=Brushes.Transparent;button.ToolTip=S("질문을 채웁니다. 자동으로 보내거나 기존 초안을 덮어쓰지 않습니다.","Fills the input without sending or replacing an existing draft.");AutomationProperties.SetName(button,pair[0]);suggestions.Children.Add(button);starterButtons.Add(button);}
        }
        void OpenThreadMenu()
        {
            var menu=MainMenu(threadMenu);AddMainMenuItem(menu,S("이름 변경","Rename"),"new",RenameConversation,rename.IsEnabled);AddMainMenuItem(menu,Convert.ToString(archive.Content),"archive",ToggleArchive,archive.IsEnabled);menu.IsOpen=true;
        }
        void CloseConversationSearch()
        {if(!search.IsEnabled||busy||connecting||handoffEditing)return;search.Clear();searchHost.Visibility=Visibility.Collapsed;if(input.IsEnabled)input.Focus();}
        void OpenScreenSettings()
        {
            var menu=MainMenu(screenSettings);AddMainMenuItem(menu,S("화면 효과 · ","Interface motion · ")+(motionPreference.IsChecked==true?S("켜짐","On"):S("꺼짐","Off")),"settings",()=>motionPreference.IsChecked=motionPreference.IsChecked!=true);AddMainMenuItem(menu,S("판단 엔진 설정","Decision engine settings"),"spark",OpenRoutingSettings,detailsRoutingSettings.IsEnabled);menu.IsOpen=true;
        }
        void OpenConnectionOverview()
        {
            var menu=MainMenu(connectionOverview);
            foreach(var provider in StudioProviderCatalog.Ids){string id=provider;var slot=Slot(id);bool live=slot!=null&&slot.Client!=null&&slot.Client.Connected&&slot.Authenticated;AddMainMenuItem(menu,ProviderName(id)+" · "+(live?S("연결됨","Connected"):S("연결 확인","Check connection")),"link",async()=>await CheckSidebarConnection(id),!demoMode&&!busy&&!connecting&&!routingInProgress&&!handoffEditing);}
            AddMainMenuItem(menu,S("현재 연결 상세","Current connection details"),"details",()=>SetDetailsOpen(true));menu.IsOpen=true;
        }
        async System.Threading.Tasks.Task CheckSidebarConnection(string id)
        {
            if(busy||connecting||routingInProgress||handoffEditing||demoMode)return;
            var result=await ConnectProvider(id,id==activeProvider,false);if(closed)return;
            if(result==null){if(id!=activeProvider)status.Text=ProviderName(id)+" · "+handoffNotice.Text;UpdateMainQuotas();return;}
            if(id!=activeProvider&&result.Authenticated&&(id=="codex"||id=="claude")){
                var agent=result.Client;int version=result.RefreshVersion;try{var observed=await agent.QuotaAsync();if(!closed&&Slot(id)==result&&result.Client==agent&&result.RefreshVersion==version&&agent.Connected&&result.Authenticated)result.Quota=observed;}catch{if(!closed&&Slot(id)==result&&result.RefreshVersion==version)result.Quota=null;}
            }
            if(!closed)UpdateMainQuotas();
        }
        ContextMenu MainMenu(FrameworkElement target)
        {
            if(activeMainMenu!=null)activeMainMenu.IsOpen=false;
            var menu=new ContextMenu{PlacementTarget=target,Placement=PlacementMode.Bottom,Background=surface,Foreground=Foreground,BorderBrush=line,FontFamily=FontFamily,FontSize=12,Padding=new Thickness(5),MaxWidth=Math.Max(260,Math.Min(520,(ActualWidth>0?ActualWidth:Width)-48)),MaxHeight=Math.Max(220,Math.Min(520,(ActualHeight>0?ActualHeight:Height)-100))};activeMainMenu=menu;
            if(target==connectionOverview||target==screenSettings||target==composerModelButton||target==newModelButton)menu.Placement=PlacementMode.Top;
            if(target==threadMenu){menu.Placement=PlacementMode.Custom;menu.CustomPopupPlacementCallback=(popup,targetSize,offset)=>new[]{new CustomPopupPlacement(new Point(targetSize.Width-popup.Width,targetSize.Height+4),PopupPrimaryAxis.Vertical),new CustomPopupPlacement(new Point(targetSize.Width-popup.Width,-popup.Height-4),PopupPrimaryAxis.Vertical)};}
            var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.BackgroundProperty,surface);border.SetValue(Border.BorderBrushProperty,line);border.SetValue(Border.BorderThicknessProperty,new Thickness(1));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(8));border.SetValue(Border.PaddingProperty,new Thickness(5));var scroll=new FrameworkElementFactory(typeof(ScrollViewer));scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,ScrollBarVisibility.Auto);scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));border.AppendChild(scroll);menu.Template=new ControlTemplate(typeof(ContextMenu)){VisualTree=border};
            var row=new FrameworkElementFactory(typeof(Border),"row");row.SetValue(Border.CornerRadiusProperty,new CornerRadius(5));row.SetValue(Border.PaddingProperty,new Thickness(9,9,9,9));row.SetValue(Border.BackgroundProperty,Brushes.Transparent);var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetBinding(ContentPresenter.ContentProperty,new Binding("Header"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});row.AppendChild(presenter);var template=new ControlTemplate(typeof(MenuItem)){VisualTree=row};var hover=new Trigger{Property=MenuItem.IsHighlightedProperty,Value=true};hover.Setters.Add(new Setter(Border.BackgroundProperty,hoverSurface,"row"));template.Triggers.Add(hover);var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.5));template.Triggers.Add(disabled);var style=new Style(typeof(MenuItem));style.Setters.Add(new Setter(Control.TemplateProperty,template));style.Setters.Add(new Setter(Control.ForegroundProperty,Foreground));menu.Resources[typeof(MenuItem)]=style;return menu;
        }
        void AddMainMenuItem(ContextMenu menu,string text,string icon,Action action,bool enabled=true)
        {var row=new DockPanel();var image=StudioVisuals.Icon(icon,muted,14);image.Margin=new Thickness(0,0,9,0);DockPanel.SetDock(image,Dock.Left);row.Children.Add(image);row.Children.Add(new TextBlock{Text=text,MaxWidth=Math.Max(200,menu.MaxWidth-70),TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center});var item=new MenuItem{Header=row,IsEnabled=enabled,ToolTip=text};AutomationProperties.SetName(item,text);item.Click+=(s,e)=>action();menu.Items.Add(item);}
        StudioQuotaWindow CurrentAllowance(IStudioAgentClient agent,bool signedIn,StudioQuotaSnapshot snapshot)
        {
            if(agent==null||!agent.Connected||!signedIn||snapshot==null)return null;var now=DateTime.UtcNow;var state=snapshot.State(now,agent.AccountIdentity,agent.ConnectionId);if(state!=StudioQuotaState.Confirmed)return null;
            // An inactive provider has no selected model: show only unscoped
            // limits. A scoped limit must match the active model exactly.
            string model=agent==client&&selected!=null?selected.Model:null;
            return snapshot.Buckets.Where(b=>String.IsNullOrEmpty(b.ModelScope)||b.ModelScope==model).SelectMany(b=>new[]{b.Primary,b.Secondary}).Where(w=>w!=null&&w.State(now,state)==StudioQuotaState.Confirmed&&w.RemainingPercent.HasValue).OrderBy(w=>w.RemainingPercent.Value).FirstOrDefault();
        }
        void UpdateMainQuotas()
        {
            if(!mainReady)return;
            foreach(var entry in sidebarAllowances){var slot=Slot(entry.Key);var active=activeProvider==entry.Key;var agent=active?client:slot==null?null:slot.Client;var snapshot=active?quotaSnapshot:slot==null?null:slot.Quota;var valid=CurrentAllowance(agent,active?authenticated:slot!=null&&slot.Authenticated,snapshot);entry.Value.Text=valid==null?S("확인 필요","Check needed"):valid.RemainingPercent.Value.ToString("0.#",CultureInfo.CurrentCulture)+"%";entry.Value.ToolTip=valid==null?S("현재 연결에서 확인한 잔여량이 없습니다.","No current allowance has been observed for this connection."):AllowancePeriod(valid)+" · "+S("확인 ","Observed ")+snapshot.ObservedUtc.ToLocalTime().ToString("MM/dd HH:mm");}
            var current=CurrentAllowance(client,authenticated,quotaSnapshot);detailQuotaValue.Text=current==null?S("확인 필요","Check needed"):current.RemainingPercent.Value.ToString("0.#",CultureInfo.CurrentCulture)+"%";detailQuotaValue.FontSize=current==null?19:30;detailQuotaPeriod.Text=current==null?"":AllowancePeriod(current)+(current.ResetUtc.HasValue?S(" · 초기화 "," · Resets ")+current.ResetUtc.Value.ToLocalTime().ToString("MM/dd HH:mm"):"");detailQuotaTrack.Visibility=current==null?Visibility.Collapsed:Visibility.Visible;detailQuotaFill.Width=current==null?0:Math.Max(0,Math.Min(1,current.RemainingPercent.Value/100))*Math.Max(1,detailQuotaTrack.ActualWidth);detailQuotaValue.ToolTip=quotaSummary.Text;
        }
        string AllowancePeriod(StudioQuotaWindow window)
        {return !window.WindowMinutes.HasValue?S("공급자 한도","Provider allowance"):window.WindowMinutes.Value%1440==0?(window.WindowMinutes.Value/1440)+S("일 한도","d allowance"):window.WindowMinutes.Value%60==0?(window.WindowMinutes.Value/60)+S("시간 한도","h allowance"):window.WindowMinutes.Value+S("분 한도","m allowance");}
        void UpdateMainScreen()
        {
            if(!mainReady)return;searchOpen.IsEnabled=search.IsEnabled;threadMenu.IsEnabled=selected!=null;headerHandoff.IsEnabled=composerHandoff.IsEnabled;headerHandoff.Visibility=ModelPinned()?Visibility.Visible:Visibility.Collapsed;
            double width=ActualWidth>0?ActualWidth:Width;bool compact=width<1050;if(compactHeader!=compact){compactHeader=compact;headerHandoff.Content=compact?StudioVisuals.Icon("transfer",muted,16):LabeledIcon("transfer",S("다른 AI로 이어가기","Continue with another AI"),14);}
            newModelButton.IsEnabled=!busy&&!connecting&&!routingInProgress&&!handoffEditing&&!storageBlocked&&selected!=null&&!selected.Archived;newModelCaption.Text=selected!=null&&selected.AutoRouting?S("자동 선택","Automatic"):CurrentModelCaption();newModelButton.ToolTip=routingSummary.ToolTip??newModelCaption.Text;
            composerProjectName.Text=ProjectName(selected==null?null:selected.Project);composerProjectName.MaxWidth=width<900?100:150;
            scopeCaption.Text=activeProvider=="codex"?S("프로젝트 읽기 전용","Project read-only"):S("대화 전용 · 도구 없음","Chat only · No tools");scopeCaption.ToolTip=providerHelp.ToolTip;detailModelName.Text=CurrentModelCaption();
            if(selected!=null&&selected.AutoRouting&&!ModelPinned()){
                providerDisclosure.Text=S("첫 전송: 현재 질문을 ","First send: classify this question with ")+StudioRoutingPolicy.EngineName(routingSettings.Engine)+S("로 분류한 뒤 선택된 AI에 전송합니다.",", then send it to the selected AI.");providerDisclosure.ToolTip=routingSummary.ToolTip;detailDisclosure.Text=providerDisclosure.Text;
            }
            if(!storageBlocked&&!displayedFailure.HasValue&&(status.Text==S("모델을 선택했습니다. 작업을 시작하면 이 모델을 유지합니다.","Model selected. This task will keep this model once started.")||status.Text==S("저장된 모델을 선택했습니다.","Saved model selected.")||status.Text==S("저장했습니다.","Saved.")))status.Visibility=Visibility.Collapsed;
            bool isNew=selected!=null&&selected.Messages.Count==0&&!handoffEditing&&!storageBlocked&&!displayedFailure.HasValue&&(ActualHeight>0?ActualHeight:Height)>=650;
            if(isNew!=centeringComposer){var parent=composerBottom.Parent as Panel;if(parent!=null)parent.Children.Remove(composerBottom);centeringComposer=isNew;Grid.SetRow(composerBottom,2);if(isNew)startCenter.Children.Add(composerBottom);else workspaceMain.Children.Add(composerBottom);}
            startCenter.Visibility=isNew?Visibility.Visible:Visibility.Collapsed;transcript.Visibility=isNew?Visibility.Collapsed:Visibility.Visible;
            transcript.ExternalWelcome=isNew;composerBottom.Margin=isNew?new Thickness(10,0,10,0):new Thickness(10,8,10,0);welcomePanel.Children[0].Visibility=(ActualHeight>0?ActualHeight:Height)<750?Visibility.Collapsed:Visibility.Visible;
            // Normal readiness lives in tooltips and the first-send disclosure.
            // Blockers and recovery always stay next to their action.
            composerReadiness.Visibility=nextComposerAction!=null||busy||connecting||routingInProgress||storageBlocked||displayedFailure.HasValue||DeliveryUncertain()||NeedsAccountConfirmation()||HasPreparedTransfer()||handoffEditing?Visibility.Visible:Visibility.Collapsed;
            routingFooter.Visibility=Visibility.Collapsed;send.Visibility=busy||routingInProgress?Visibility.Collapsed:composerAction.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;
            foreach(var button in starterButtons)button.IsEnabled=input.IsEnabled&&String.IsNullOrWhiteSpace(input.Text);
            UpdateMainQuotas();
        }
        void OpenComposerModelMenu()
        {
            if(selected==null||busy||connecting||routingInProgress||handoffEditing||renameEditing||storageBlocked||selected.Archived)return;var menu=MainMenu(ModelPinned()?composerModelButton:newModelButton);
            if(ModelPinned()){AddMainMenuItem(menu,CurrentModelCaption()+S(" · 고정"," · Pinned"),"lock",()=>{},false);AddMainMenuItem(menu,S("문맥을 전달해 다른 AI로 이어가기","Continue with another AI using reviewed context"),"transfer",OpenHandoff,composerHandoff.IsEnabled);}
            else{
                AddMainMenuItem(menu,S("자동 선택 · 질문 특성·잔여량 참고","Automatic · Task and allowance aware"),"spark",()=>{routingMode.SelectedIndex=1;});
                foreach(var provider in StudioProviderCatalog.Ids){string id=provider;var slot=Slot(id);bool available=id==activeProvider?authenticated&&catalogReady:slot!=null&&slot.Authenticated&&slot.CatalogReady&&slot.Client!=null&&slot.Client.Connected;var catalog=id==activeProvider?models.Items.Cast<StudioModel>().ToList():slot==null?new List<StudioModel>():slot.Models;
                    if(!available||catalog.Count==0)AddMainMenuItem(menu,ProviderName(id)+S(" · 연결 확인"," · Check connection"),"link",async()=>{if(!SelectComposerModel(id,null))return;await ConnectProvider(id,true,false);},!demoMode);
                    else foreach(var model in catalog){string modelId=model.Id;string label=model.Label??model.Id;AddMainMenuItem(menu,ProviderName(id)+" · "+label,"spark",()=>SelectComposerModel(id,modelId));}
                }
                AddMainMenuItem(menu,S("판단 엔진 설정","Decision engine settings"),"settings",OpenRoutingSettings,detailsRoutingSettings.IsEnabled);
            }menu.IsOpen=true;
        }
        bool SelectComposerModel(string provider,string model)
        {
            if(selected==null||ModelPinned()||busy||connecting||routingInProgress||handoffEditing||renameEditing||storageBlocked||HasPreparedTransfer()||DeliveryUncertain())return false;
            if(model!=null){var slot=Slot(provider);bool ready=provider==activeProvider?authenticated&&catalogReady&&client!=null&&client.Connected:slot!=null&&slot.Authenticated&&slot.CatalogReady&&slot.Client!=null&&slot.Client.Connected&&!slot.Refreshing;if(!ready)return false;var source=provider==activeProvider?models.Items.Cast<StudioModel>():slot.Models;if(!source.Any(m=>m.Id==model))return false;}
            if(!SaveCurrent())return false;
            routingMode.SelectedIndex=0;if(storageBlocked)return false;providerPicker.SelectedIndex=StudioProviderCatalog.Index(provider);if(storageBlocked)return false;
            if(model!=null){var pick=models.Items.Cast<StudioModel>().FirstOrDefault(m=>m.Id==model);if(pick==null)return false;models.SelectedItem=pick;}Enable();if(client!=null&&client.Connected&&authenticated&&quotaSnapshot==null)Ui(async()=>await RefreshQuota(client));return !storageBlocked;
        }
    }
}
