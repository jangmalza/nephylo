using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace UsageWidget
{
    internal sealed class ConnectionEnvironmentPicker
    {
        internal readonly StackPanel Root = new LocalizedStackPanel();
        internal readonly ComboBox Choices = new LocalizedComboBox();
        internal readonly Button Reload = new LocalizedButton { Content = "다시 검색", FontSize = 14, MinHeight = 38, Margin = new Thickness(8,0,0,0) };
        internal TextBlock Hint;
        internal Action Changed;
        internal List<WslDistribution> Distributions = new List<WslDistribution>();
        internal bool Loading;
        internal bool Enabled = true;
        internal string Distribution { get { var item = Choices.SelectedItem as ComboBoxItem; return item == null ? "" : item.Tag as string ?? ""; } }
        internal WslDistribution SelectedProbe { get { return Distributions.FirstOrDefault(item => String.Equals(item.Name,Distribution,StringComparison.Ordinal)); } }
        internal void SetEnabled(bool value) { Enabled = value; Choices.IsEnabled = value; Reload.IsEnabled = value && !Loading; }
    }

    public partial class WidgetWindow
    {
        // Discovery is shared by the two editors. Opening settings never changes a saved connection.
        private Task<List<WslDistribution>> wslDiscovery;

        private ConnectionEnvironmentPicker AddConnectionEnvironment(StackPanel body, string service, string selected)
        {
            var picker = new ConnectionEnvironmentPicker();
            picker.Root.Margin = new Thickness(0,0,0,14);
            picker.Root.Children.Add(ConnectionText("연결 환경",14,"#647070",FontWeights.SemiBold,new Thickness(0,0,0,5)));
            System.Windows.Automation.AutomationProperties.SetName(picker.Choices,"연결 환경");
            picker.Choices.MinWidth = 0; picker.Choices.FontSize = 14; picker.Choices.MinHeight = 38;
            var line = new LocalizedGrid();
            line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(picker.Choices); Grid.SetColumn(picker.Reload,1); line.Children.Add(picker.Reload);
            line.SizeChanged += (s,e) => {
                bool narrow = e.NewSize.Width < 350;
                Grid.SetColumn(picker.Reload,narrow ? 0 : 1); Grid.SetRow(picker.Reload,narrow ? 1 : 0);
                picker.Reload.HorizontalAlignment = HorizontalAlignment.Left;
                picker.Reload.Margin = narrow ? new Thickness(0,8,0,0) : new Thickness(8,0,0,0);
            };
            picker.Root.Children.Add(line);
            picker.Hint = ConnectionText("Windows 또는 WSL 배포판을 선택하고 연결을 확인하세요.",11,"#647070",FontWeights.Normal,new Thickness(0,6,0,0));
            picker.Root.Children.Add(picker.Hint); body.Children.Add(picker.Root);
            Action<string> populate = target => {
                picker.Choices.Items.Clear();
                picker.Choices.Items.Add(new LocalizedComboBoxItem { Content = "Windows", Tag = "" });
                var names = picker.Distributions.Select(item => item.Name).ToList();
                if (!String.IsNullOrEmpty(target) && !names.Contains(target,StringComparer.Ordinal)) names.Add(target);
                foreach (string name in names) picker.Choices.Items.Add(new LocalizedComboBoxItem { Content = "WSL · " + name, Tag = name });
                picker.Choices.SelectedItem = picker.Choices.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == (target ?? ""));
            };
            populate(selected);
            Action describe = () => {
                if (String.IsNullOrEmpty(picker.Distribution)) picker.Hint.Text = "Windows에 설치된 프로그램과 로그인 정보를 사용합니다.";
                else {
                    var probe = picker.SelectedProbe;
                    picker.Hint.Text = probe == null ? "저장된 WSL 환경입니다. 다시 검색하거나 연결을 확인하세요." :
                        !String.IsNullOrEmpty(probe.Error) ? "배포판을 확인하지 못했습니다. WSL에서 배포판을 한 번 실행한 뒤 다시 검색하세요." :
                        String.IsNullOrEmpty(service == "codex" ? probe.CodexPath : probe.ClaudePath) ? "선택한 배포판에 프로그램을 설치한 뒤 다시 검색하세요." :
                        "프로그램을 찾았습니다. 선택한 배포판의 기본 사용자로 연결합니다.";
                }
            };
            picker.Choices.SelectionChanged += (s,e) => { if (picker.Choices.SelectedItem == null) return; describe(); if (picker.Changed != null) picker.Changed(); };
            Func<bool,Task> discover = async force => {
                if (demo || picker.Loading) return;
                picker.Loading = true; picker.Reload.IsEnabled = false;
                picker.Hint.Text = "WSL 배포판 검색 중… 잠시 기다려 주세요.";
                try {
                    if (force || wslDiscovery == null) wslDiscovery = WslRuntime.DiscoverAsync();
                    var result = await wslDiscovery;
                    if (exiting) return;
                    string target = picker.Distribution;
                    picker.Distributions = result; populate(target); describe();
                    if (result.Count == 0 && target.Length == 0) picker.Hint.Text = "사용 가능한 WSL 배포판이 없습니다. Windows 연결을 사용할 수 있습니다.";
                    if (picker.Changed != null) picker.Changed();
                } catch {
                    picker.Hint.Text = "WSL을 검색하지 못했습니다. 배포판 실행 상태를 확인하고 다시 검색하세요.";
                } finally { picker.Loading = false; picker.Reload.IsEnabled = picker.Enabled; }
            };
            bool started = false;
            picker.Root.Loaded += async (s,e) => { if (started) return; started = true; await discover(false); };
            picker.Reload.Click += async (s,e) => await discover(true);
            describe();
            return picker;
        }
    }
}
