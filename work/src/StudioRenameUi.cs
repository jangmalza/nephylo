using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageWidget
{
    internal sealed partial class StudioWindow
    {
        Border renamePanel,renameShade;
        readonly TextBox renameInput=new TextBox();
        readonly TextBlock renameNotice=new TextBlock();
        readonly Button renameSave=new Button(),renameCancel=new Button();
        StudioSession renameTarget;
        bool renameEditing,renameSaving;

        void BuildRenamePanel()
        {
            if(renamePanel!=null)return;
            renameShade=new Border{Background=new SolidColorBrush(Color.FromArgb(150,0,0,0)),Visibility=Visibility.Collapsed};
            Grid.SetColumnSpan(renameShade,3);Panel.SetZIndex(renameShade,40);workspaceRoot.Children.Add(renameShade);
            renamePanel=new Border{Background=Background,BorderBrush=line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(22),Margin=new Thickness(20),Width=460,MaxWidth=460,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed};
            Grid.SetColumnSpan(renamePanel,3);Panel.SetZIndex(renamePanel,41);workspaceRoot.Children.Add(renamePanel);
            KeyboardNavigation.SetTabNavigation(renamePanel,KeyboardNavigationMode.Cycle);FocusManager.SetIsFocusScope(renamePanel,true);AutomationProperties.SetName(renamePanel,S("대화 이름 변경","Rename conversation"));
            var body=new StackPanel();renamePanel.Child=body;
            body.Children.Add(new TextBlock{Text=S("대화 이름 변경","Rename conversation"),FontSize=18,FontWeight=FontWeights.SemiBold});
            body.Children.Add(new TextBlock{Text=S("대화 내용과 미전송 초안은 그대로 유지됩니다.","Conversation content and the unsent draft are kept."),FontSize=12,Foreground=muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,18)});
            renameInput.MaxLength=120;renameInput.Padding=new Thickness(10,8,10,8);renameInput.FontSize=14;renameInput.MinWidth=280;renameInput.Background=surface;renameInput.Foreground=Foreground;renameInput.BorderBrush=line;renameInput.BorderThickness=new Thickness(1);
            AutomationProperties.SetName(renameInput,S("대화 이름","Conversation name"));AutomationProperties.SetHelpText(renameInput,S("최대 120자, 한 줄로 입력하세요. Enter로 저장하고 Esc로 취소합니다.","Enter one line, up to 120 characters. Enter saves; Escape cancels."));body.Children.Add(renameInput);
            renameNotice.FontSize=12;renameNotice.Foreground=muted;renameNotice.TextWrapping=TextWrapping.Wrap;renameNotice.LineHeight=20;renameNotice.Margin=new Thickness(0,10,0,0);AutomationProperties.SetName(renameNotice,S("이름 변경 상태","Rename status"));body.Children.Add(renameNotice);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,18,0,0)};body.Children.Add(actions);
            actions.Children.Add(Setup(renameCancel,S("취소","Cancel"),CancelRename));actions.Children.Add(Setup(renameSave,S("저장","Save"),CommitRename));renameSave.Margin=new Thickness(0);renameSave.Background=Foreground;renameSave.Foreground=Background;renameSave.BorderBrush=Foreground;
            AutomationProperties.SetName(renameSave,S("대화 이름 저장","Save conversation name"));AutomationProperties.SetName(renameCancel,S("대화 이름 변경 취소","Cancel conversation rename"));
            renameInput.TextChanged+=(s,e)=>{if(renameEditing&&!renameSaving){renameNotice.Text="";EnableRenameControls();}};
        }
        void OpenRenamePanel()
        {
            if(selected==null||busy||connecting||routingInProgress||handoffEditing||renameEditing||!SaveCurrent())return;
            BuildRenamePanel();if(activeMainMenu!=null)activeMainMenu.IsOpen=false;
            renameTarget=selected;renameInput.Text=selected.Title??"";renameNotice.Text="";renameSaving=false;renameEditing=true;
            renamePanel.Visibility=renameShade.Visibility=Visibility.Visible;Enable();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{if(renameEditing&&!closed){renameInput.Focus();Keyboard.Focus(renameInput);renameInput.SelectAll();}}));
        }
        string RenameValidation()
        {
            var value=(renameInput.Text??"").Trim();
            if(value.Length==0)return S("이름을 입력해 주세요.","Enter a conversation name.");
            if(value.Length>120||value.Any(Char.IsControl))return S("이름은 120자 이내의 한 줄로 입력해 주세요.","Use a single line of up to 120 characters.");
            return null;
        }
        void EnableRenameControls()
        {
            if(!renameEditing||renamePanel==null)return;
            foreach(var control in new Control[]{fresh,list,search,searchOpen,projectFilter,archived,choose,send,input,models,providerPicker,routingMode,connect,login,cancelLogin,apiKeySettings,handoffOpen,composerHandoff,composerModelButton,newModelButton,headerHandoff,detailsRoutingSettings,routingSettingsButton,threadMenu,rename,archive,connectionOverview,screenSettings,sidebarToggle,detailsToggle})control.IsEnabled=false;
            renameInput.IsEnabled=!renameSaving;renameCancel.IsEnabled=!renameSaving;
            renameSave.IsEnabled=!renameSaving&&renameTarget==selected&&RenameValidation()==null;
        }
        void CommitRename()
        {
            if(!renameEditing||renameSaving||renameTarget==null||renameTarget!=selected||busy||connecting||routingInProgress||handoffEditing)return;
            string validation=RenameValidation();if(validation!=null){renameNotice.Text=validation;EnableRenameControls();return;}
            // Save other pending conversation changes before attempting the title.
            // A failed title write keeps the editor open and its candidate intact.
            if(!SaveCurrent()){renameNotice.Text=StudioFailure.Guidance(StudioFailureKind.Storage,L.English);EnableRenameControls();return;}
            var target=renameTarget;string priorTitle=target.Title;bool priorCustom=target.TitleIsCustom;
            renameSaving=true;target.Title=renameInput.Text.Trim();target.TitleIsCustom=true;dirty=true;EnableRenameControls();
            if(!SaveCurrent()){
                target.Title=priorTitle;target.TitleIsCustom=priorCustom;dirty=true;renameSaving=false;
                heading.Text=priorTitle;heading.ToolTip=priorTitle;UpdateConversationRowBindings(list);
                renameNotice.Text=StudioFailure.Guidance(StudioFailureKind.Storage,L.English);Enable();return;
            }
            renameSaving=false;HideRenamePanel();RefreshList();Render();if(input.IsEnabled)input.Focus();
        }
        void HideRenamePanel()
        {
            renameEditing=false;renameTarget=null;
            if(renamePanel!=null){renamePanel.Visibility=Visibility.Collapsed;renameShade.Visibility=Visibility.Collapsed;}
            sidebarToggle.IsEnabled=detailsToggle.IsEnabled=connectionOverview.IsEnabled=screenSettings.IsEnabled=true;
            Enable();
        }
        void CancelRename()
        {if(!renameEditing||renameSaving)return;HideRenamePanel();if(input.IsEnabled)input.Focus();else threadMenu.Focus();}
        bool HandleRenameKey(KeyEventArgs e)
        {
            if(!renameEditing)return false;
            if(e.Key==Key.Escape){CancelRename();e.Handled=true;}
            else if(e.Key==Key.Enter&&Keyboard.Modifiers==ModifierKeys.None){CommitRename();e.Handled=true;}
            else if((Keyboard.Modifiers&ModifierKeys.Control)!=0&&(e.Key==Key.K||e.Key==Key.N||e.Key==Key.H)){e.Handled=true;}
            return true;
        }
    }
}
