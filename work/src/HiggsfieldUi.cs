using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private HiggsfieldSettings higgsfieldSettings;
        private readonly HiggsfieldUsageService higgsfield = new HiggsfieldUsageService();
        private readonly ProviderView higgsfieldView = new ProviderView { Accent = "#2448F5" };
        private readonly ProviderView higgsfieldApiView = new ProviderView { Accent = "#2448F5" };
        private readonly CancellationTokenSource higgsfieldLifetime = new CancellationTokenSource();
        private CancellationTokenSource higgsfieldOperation;
        private const string HiggsfieldCliGuide = "https://github.com/higgsfield-ai/cli#install";

        private void CancelHiggsfieldOperation()
        {
            if (higgsfieldOperation != null) higgsfieldOperation.Cancel();
        }

        private HiggsfieldSettings CopyHiggsfieldSettings()
        {
            return new HiggsfieldSettings { WebConnected = higgsfieldSettings.WebConnected, CliPath = higgsfieldSettings.CliPath, WorkspaceId = higgsfieldSettings.WorkspaceId,
                ApiBalanceUsd = higgsfieldSettings.ApiBalanceUsd, ApiObservedAt = higgsfieldSettings.ApiObservedAt };
        }

        private void SaveHiggsfieldSettings(HiggsfieldSettings value)
        {
            if (!demo) HiggsfieldSettingsStore.Save(value);
            higgsfieldSettings = value;
        }

        private async Task ReadHiggsfield()
        {
            if (demo || !IsServiceActive("higgsfield") || !higgsfieldSettings.WebConnected || higgsfieldOperation != null) return;
            var captured = higgsfieldSettings;
            string owner = ServiceReadingOwner;
            higgsfieldView.Busy = true; RenderHiggsfield();
            try {
                var result = await higgsfield.ReadWebConnectionAsync(captured.CliPath, captured.WorkspaceId, higgsfieldLifetime.Token);
                if (!CanApplyServiceReading("higgsfield",owner) || !higgsfieldSettings.WebConnected || higgsfieldOperation != null || !Object.ReferenceEquals(captured,higgsfieldSettings)) return;
                if (!String.Equals(captured.WorkspaceId ?? "",result.WorkspaceId ?? "",StringComparison.OrdinalIgnoreCase)) {
                    var next = CopyHiggsfieldSettings(); next.WorkspaceId = result.WorkspaceId; SaveHiggsfieldSettings(next);
                }
                higgsfieldView.Snapshot = result.Snapshot; higgsfieldView.LastSuccess = DateTimeOffset.Now;
                higgsfieldView.Error = null; higgsfieldView.Issue = null; RefreshSucceeded(higgsfieldView); ObserveUsage(higgsfieldView,"cli");
            } catch (OperationCanceledException) { }
            catch (Exception ex) { if (CanApplyServiceReading("higgsfield",owner) && higgsfieldSettings.WebConnected && higgsfieldOperation == null && Object.ReferenceEquals(captured,higgsfieldSettings)) { RefreshFailed(higgsfieldView,ex); higgsfieldView.Error = Friendly(ex); } }
            finally { higgsfieldView.Busy = false; higgsfieldView.BackgroundRefresh = false; if (!exiting) { RenderHiggsfield(); UpdateServiceVisibility(); } }
        }

        private void RenderHiggsfield()
        {
            if (higgsfieldView.Body == null) return;
            if (higgsfieldView.Snapshot == null && String.IsNullOrEmpty(higgsfieldView.Error)) {
                EmptyExtra(higgsfieldView,higgsfieldView.Busy ? "조회 중" : "로그인 연결",higgsfieldView.Busy ? "웹 구독 크레딧을 확인하고 있습니다." : "힉스필드 로그인 후 웹 구독 크레딧을 자동 조회합니다.");
                return;
            }
            Render(higgsfieldView,false);
            if (higgsfieldView.Snapshot != null)
                higgsfieldView.Body.Children.Add(T("웹 구독 크레딧 · API 달러 잔액은 별도",10,"#647070",FontWeights.Normal,new Thickness(0,5,0,0)));
        }

        private void RenderHiggsfieldApi()
        {
            if (higgsfieldApiView.Body == null || !IsServiceActive("higgsfield-api")) return;
            higgsfieldApiView.Snapshot = higgsfieldSettings.ApiBalanceUsd.HasValue && higgsfieldSettings.ApiObservedAt.HasValue ? HiggsfieldUsageService.ReadApiManual(higgsfieldSettings) : null;
            higgsfieldApiView.LastSuccess = higgsfieldSettings.ApiObservedAt;
            higgsfieldApiView.Error = null; higgsfieldApiView.Busy = false;
            if (higgsfieldApiView.Snapshot == null) {
                EmptyExtra(higgsfieldApiView,"수동 기록","API 콘솔에서 확인한 달러 잔액을 기록하세요. 자동 조회는 지원 준비 중입니다."); return;
            }
            if (!demo) ObserveUsage(higgsfieldApiView,"manual");
            Render(higgsfieldApiView,false);
            higgsfieldApiView.Badge.Text = "수동 기록";
            higgsfieldApiView.Body.Children.Add(T("기록 " + higgsfieldApiView.LastSuccess.Value.ToLocalTime().ToString("MM/dd HH:mm") + " · 자동 갱신 안 됨",10,"#647070",FontWeights.Normal,new Thickness(0,5,0,0)));
        }

        private void BuildHiggsfieldSettings(StackPanel body)
        {
            bool operationFeedback = false;
            bool workspaceSelectionPending = false;
            UsageSnapshot operationSnapshot = null;
            DateTimeOffset? operationLastSuccess = null;
            string operationError = null;
            Action rememberResult = () => { operationSnapshot = higgsfieldView.Snapshot; operationLastSuccess = higgsfieldView.LastSuccess; operationError = higgsfieldView.Error; };
            ConnectionSteps(body,"웹 구독 크레딧 · 공식 로그인 연결",
                "공식 Higgsfield CLI를 설치하세요. API 키는 필요하지 않습니다.",
                "‘힉스필드 로그인’을 누르고 브라우저에서 로그인하세요.",
                "작업 공간이 하나면 자동 연결합니다. 여러 개면 위젯에서 선택하세요.");
            body.Children.Add(SmallAction("공식 CLI 설치 안내 ↗",() => OpenOfficialPage(HiggsfieldCliGuide)));
            var actions = new WrapPanel();
            var login = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "힉스필드 로그인", Margin = new Thickness(0,0,8,8) };
            var check = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "연결 확인", Margin = new Thickness(0,0,8,8) };
            var loadWorkspaces = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "작업 공간 불러오기", Margin = new Thickness(0,0,8,8) };
            var cancel = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "취소", Margin = new Thickness(0,0,0,8), Visibility = Visibility.Collapsed };
            actions.Children.Add(login); actions.Children.Add(check); actions.Children.Add(loadWorkspaces); actions.Children.Add(cancel); body.Children.Add(actions);
            var feedback = MakeConnectionFeedback(body);
            var workspacePanel = new LocalizedStackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0,12,0,0) };
            workspacePanel.Children.Add(ConnectionText("연결할 작업 공간",14,"#252827",FontWeights.SemiBold,new Thickness(0,0,0,6)));
            var workspaceChoices = new LocalizedComboBox { FontSize = 14, MinHeight = 38, HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Resources[typeof(ComboBox)], ItemContainerStyle = (Style)Resources[typeof(ComboBoxItem)] };
            System.Windows.Automation.AutomationProperties.SetName(workspaceChoices,"Higgsfield 작업 공간 선택");
            workspacePanel.Children.Add(workspaceChoices);
            workspacePanel.Children.Add(ConnectionText("목록 조회와 선택만으로는 연결 설정이 바뀌지 않습니다. 연결 버튼으로 크레딧을 확인한 뒤 저장합니다.",10,"#647070",FontWeights.Normal,new Thickness(0,6,0,0)));
            var connectWorkspace = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "선택한 작업 공간 연결", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,8,0,0), IsEnabled = false };
            workspacePanel.Children.Add(connectWorkspace); body.Children.Add(workspacePanel);
            var advanced = new LocalizedStackPanel();
            advanced.Children.Add(ConnectionText("자동 검색에 실패한 경우 higgsfield.exe 또는 hf.exe를 선택하세요.",11,"#647070",FontWeights.Normal,new Thickness(0,0,0,8)));
            var line = new LocalizedGrid(); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var path = new LocalizedTextBox { Text = higgsfieldSettings.CliPath ?? "" };
            System.Windows.Automation.AutomationProperties.SetName(path,"Higgsfield 실행 파일 경로"); line.Children.Add(path);
            var browse = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "찾기", Margin = new Thickness(8,0,0,0) }; Grid.SetColumn(browse,1); line.Children.Add(browse); advanced.Children.Add(line);
            advanced.Children.Add(ConnectionText("워크스페이스 ID (선택)",14,"#647070",FontWeights.Normal,new Thickness(0,12,0,6)));
            var workspace = new LocalizedTextBox { Text = higgsfieldSettings.WorkspaceId ?? "", MaxLength = 36 };
            System.Windows.Automation.AutomationProperties.SetName(workspace,"Higgsfield 워크스페이스 ID"); advanced.Children.Add(workspace);
            advanced.Children.Add(ConnectionText("로그인 후 ‘작업 공간 불러오기’로 선택할 수 있습니다. 직접 입력하려면 higgsfield workspace list에 표시된 ID를 사용하세요. 비우면 CLI의 기존 선택 또는 유일한 작업 공간을 사용합니다.",10,"#647070",FontWeights.Normal,new Thickness(0,6,0,0)));
            var commandNotice = ConnectionText("",10,"#647070",FontWeights.Normal,new Thickness(0,4,0,0));
            Action<string> copyCommand = arguments => {
                string executable = String.IsNullOrWhiteSpace(path.Text) ? HiggsfieldUsageService.FindCliPath() : path.Text.Trim();
                if (String.IsNullOrEmpty(executable) || !File.Exists(executable)) { commandNotice.Text = "먼저 CLI를 설치하거나 실행 파일을 선택하세요."; return; }
                try { Clipboard.SetText("& '" + executable.Replace("'","''") + "' " + arguments); commandNotice.Text = "명령을 복사했습니다. PowerShell에 붙여 넣어 실행하세요."; }
                catch { commandNotice.Text = "명령을 복사하지 못했습니다. 잠시 후 다시 시도하세요."; }
            };
            var commands = new WrapPanel();
            commands.Children.Add(SmallAction("워크스페이스 조회 명령 복사",() => copyCommand("workspace list")));
            commands.Children.Add(SmallAction("로그인 명령 복사",() => copyCommand("auth login")));
            advanced.Children.Add(commands); advanced.Children.Add(commandNotice);
            browse.Click += (s,e) => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = L.Text("실행 파일 (*.exe)|*.exe"), CheckFileExists = true }; if (picker.ShowDialog(servicesWindow) == true) path.Text = picker.FileName; };
            Details(body,"고급 설정 · CLI · 워크스페이스",advanced);
            LocalizedButton disconnect = null;
            Action updateWorkspaceAction = () => {
                var selected = workspaceChoices.SelectedItem as ComboBoxItem;
                connectWorkspace.IsEnabled = higgsfieldOperation == null && selected != null && String.Equals(selected.Tag as string,workspace.Text.Trim(),StringComparison.OrdinalIgnoreCase);
            };
            workspaceChoices.SelectionChanged += (s,e) => {
                var selected = workspaceChoices.SelectedItem as ComboBoxItem;
                if (selected != null) workspace.Text = selected.Tag as string ?? "";
                updateWorkspaceAction();
            };
            workspace.TextChanged += (s,e) => { if (higgsfieldOperation == null && !workspaceSelectionPending) operationFeedback = false; updateWorkspaceAction(); };
            path.TextChanged += (s,e) => { if (higgsfieldOperation == null) { operationFeedback = false; workspaceSelectionPending = false; workspaceChoices.Items.Clear(); workspacePanel.Visibility = Visibility.Collapsed; } };
            Action<ReadOnlyCollection<HiggsfieldWorkspace>> showWorkspaces = choices => {
                workspaceSelectionPending = true;
                string preferred = workspace.Text.Trim();
                workspaceChoices.Items.Clear(); workspaceChoices.SelectedIndex = -1;
                foreach (var choice in choices) workspaceChoices.Items.Add(new ComboBoxItem {
                    Content = choice.Name + (String.IsNullOrWhiteSpace(choice.PlanType) ? "" : " · " + choice.PlanType), Tag = choice.Id });
                workspacePanel.Visibility = choices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                for (int i=0;i<choices.Count;i++) if (String.Equals(choices[i].Id,preferred,StringComparison.OrdinalIgnoreCase)) { workspaceChoices.SelectedIndex = i; break; }
                if (choices.Count == 1 && preferred.Length == 0) workspaceChoices.SelectedIndex = 0;
                updateWorkspaceAction();
            };
            Action<bool> operationBusy = busy => {
                login.IsEnabled = check.IsEnabled = loadWorkspaces.IsEnabled = path.IsEnabled = browse.IsEnabled = workspace.IsEnabled = workspaceChoices.IsEnabled = !busy;
                if (disconnect != null) disconnect.IsEnabled = !busy;
                cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                updateWorkspaceAction();
            };
            Action update = () => {
                if (higgsfieldOperation != null || !RequireServiceConnection("higgsfield",feedback)) return;
                if (operationFeedback && (workspaceSelectionPending || Object.ReferenceEquals(operationSnapshot,higgsfieldView.Snapshot) && operationLastSuccess == higgsfieldView.LastSuccess && operationError == higgsfieldView.Error)) return;
                operationFeedback = false;
                string found = String.IsNullOrWhiteSpace(path.Text) ? HiggsfieldUsageService.FindCliPath() : path.Text;
                if (!String.IsNullOrWhiteSpace(higgsfieldView.Error)) Feedback(feedback,"연결 확인 필요",higgsfieldView.Error,true);
                else if (higgsfieldSettings.WebConnected) Feedback(feedback,"위젯 연결됨","웹 구독 크레딧을 자동으로 확인합니다. 다른 계정은 다시 로그인해 연결하세요.",false);
                else Feedback(feedback,String.IsNullOrEmpty(found) ? "CLI 설치 필요" : "로그인 준비됨",String.IsNullOrEmpty(found) ? "공식 설치 안내를 따라 설치한 뒤 연결하세요. 별도로 받은 실행 파일은 고급 설정에서 선택할 수 있습니다." : "기존 CLI 로그인이 있으면 ‘연결 확인’을 누르세요. 처음 연결한다면 ‘힉스필드 로그인’을 누르세요.",false);
            };
            connectionStateRefreshers["higgsfield"] = update; update();
            Func<bool,Task> connect = async authenticate => {
                if (!RequireServiceConnection("higgsfield",feedback)) return;
                if (demo) { Feedback(feedback,"미리보기","실제 로그인을 시작하거나 계정을 조회하지 않았습니다.",false); return; }
                if (IsServiceConnectionPending("higgsfield") || higgsfieldOperation != null) { Feedback(feedback,"조회 진행 중","진행 중인 조회가 끝난 뒤 다시 눌러 주세요.",false); return; }
                string candidate = path.Text.Trim();
                string workspaceId = workspace.Text.Trim(); Guid parsedWorkspace;
                if (workspaceId.Length > 0 && (!Guid.TryParseExact(workspaceId,"D",out parsedWorkspace) || parsedWorkspace == Guid.Empty)) { Feedback(feedback,"워크스페이스 ID 확인","공식 CLI의 workspace list에 표시된 UUID 형식 ID를 입력하거나 비워 두세요.",true); return; }
                if (candidate.Length > 0 && (!File.Exists(candidate) || !String.Equals(Path.GetExtension(candidate),".exe",StringComparison.OrdinalIgnoreCase))) { Feedback(feedback,"실행 파일 확인","존재하는 .exe를 선택하거나 경로를 비워 자동 검색하세요.",true); return; }
                IDisposable lease;
                if (!TryReserveServiceConnection("higgsfield",out lease)) return;
                var operation = CancellationTokenSource.CreateLinkedTokenSource(higgsfieldLifetime.Token,ServiceConnectionsToken); higgsfieldOperation = operation;
                operationFeedback = true;
                operationBusy(true);
                Feedback(feedback,authenticate ? "브라우저 로그인 대기" : "연결 확인 중",authenticate ? "브라우저에서 힉스필드 계정으로 로그인하세요. 최대 3분 동안 기다립니다. 작업 공간이 여러 개면 위젯에서 선택할 수 있습니다.\n이 창을 닫아도 로그인 대기는 계속됩니다." : "공식 CLI로 현재 웹 구독 크레딧을 확인합니다.",false); UpdateFooter();
                try {
                    await WaitForServiceRefreshAsync("higgsfield",operation.Token);
                    ThrowIfServiceConnectionChanged("higgsfield",lease);
                    var result = authenticate ? await higgsfield.LoginConnectionAsync(candidate,workspaceId,operation.Token) : await higgsfield.ReadWebConnectionAsync(candidate,workspaceId,operation.Token);
                    if (operation.IsCancellationRequested || exiting) return;
                    ThrowIfServiceConnectionChanged("higgsfield",lease);
                    var next = CopyHiggsfieldSettings(); next.WebConnected = true; next.CliPath = candidate; next.WorkspaceId = result.WorkspaceId;
                    SaveHiggsfieldSettings(next);
                    workspaceSelectionPending = false;
                    workspace.Text = result.WorkspaceId ?? "";
                    CommitVerifiedServiceSelection("higgsfield");
                    ForgetUsageService("higgsfield");
                    higgsfieldView.Snapshot = result.Snapshot; higgsfieldView.LastSuccess = DateTimeOffset.Now; higgsfieldView.Error = null; higgsfieldView.Issue = null; RefreshSucceeded(higgsfieldView);
                    ObserveUsage(higgsfieldView,"cli");
                    Feedback(feedback,"연결 완료","웹 구독 크레딧을 확인했습니다. 선택한 서비스는 위젯에서 자동으로 갱신합니다.",false);
                } catch (OperationCanceledException) { if (!exiting) Feedback(feedback,"연결 취소됨","로그인이 완료되지 않았다면 다시 시도하세요. 기존 위젯 연결 설정은 유지했습니다.",false); }
                catch (HiggsfieldWorkspaceSelectionRequiredException ex) {
                    if (!exiting && !operation.IsCancellationRequested) {
                        showWorkspaces(ex.Workspaces);
                        Feedback(feedback,authenticate ? "로그인 완료 · 작업 공간 선택" : "작업 공간 선택 필요","연결할 작업 공간을 선택한 뒤 ‘선택한 작업 공간 연결’을 누르세요. 브라우저에 다시 로그인할 필요는 없습니다.",false);
                    }
                }
                catch (Exception ex) { if (!exiting) Feedback(feedback,"연결하지 못했습니다",Friendly(ex),true); }
                finally {
                    if (Object.ReferenceEquals(higgsfieldOperation,operation)) higgsfieldOperation = null;
                    rememberResult(); operation.Dispose(); lease.Dispose(); operationBusy(false);
                    if (!exiting) { RenderHiggsfield(); UpdateServiceVisibility(); UpdateFooter(); }
                }
            };
            login.Click += async (s,e) => await connect(true); check.Click += async (s,e) => await connect(false);
            connectWorkspace.Click += async (s,e) => {
                var selected = workspaceChoices.SelectedItem as ComboBoxItem;
                if (selected == null || !String.Equals(selected.Tag as string,workspace.Text.Trim(),StringComparison.OrdinalIgnoreCase)) { Feedback(feedback,"작업 공간 선택 필요","연결할 작업 공간을 목록에서 선택하세요.",true); return; }
                await connect(false);
            };
            loadWorkspaces.Click += async (s,e) => {
                if (!RequireServiceConnection("higgsfield",feedback)) return;
                if (demo) { Feedback(feedback,"미리보기","실제 로그인을 시작하거나 계정을 조회하지 않았습니다.",false); return; }
                if (IsServiceConnectionPending("higgsfield") || higgsfieldOperation != null) { Feedback(feedback,"조회 진행 중","진행 중인 조회가 끝난 뒤 다시 눌러 주세요.",false); return; }
                string candidate = path.Text.Trim();
                if (candidate.Length > 0 && (!File.Exists(candidate) || !String.Equals(Path.GetExtension(candidate),".exe",StringComparison.OrdinalIgnoreCase))) { Feedback(feedback,"실행 파일 확인","존재하는 .exe를 선택하거나 경로를 비워 자동 검색하세요.",true); return; }
                IDisposable lease;
                if (!TryReserveServiceConnection("higgsfield",out lease)) return;
                var operation = CancellationTokenSource.CreateLinkedTokenSource(higgsfieldLifetime.Token,ServiceConnectionsToken); higgsfieldOperation = operation;
                operationFeedback = true;
                operationBusy(true); workspaceChoices.Items.Clear(); workspacePanel.Visibility = Visibility.Collapsed;
                Feedback(feedback,"작업 공간 조회 중","로그인된 계정의 작업 공간 목록을 확인합니다. 브라우저 로그인은 시작하지 않습니다.",false); UpdateFooter();
                try {
                    await WaitForServiceRefreshAsync("higgsfield",operation.Token);
                    ThrowIfServiceConnectionChanged("higgsfield",lease);
                    var choices = await higgsfield.ListWorkspacesAsync(candidate,operation.Token);
                    if (operation.IsCancellationRequested || exiting) return;
                    ThrowIfServiceConnectionChanged("higgsfield",lease);
                    if (choices.Count == 0) throw new InvalidOperationException("Higgsfield 계정에서 조회할 작업 공간을 찾지 못했습니다. 로그인한 계정을 확인하거나 Higgsfield 지원팀에 문의해 주세요.");
                    showWorkspaces(choices);
                    Feedback(feedback,"작업 공간 선택","연결할 작업 공간을 선택한 뒤 ‘선택한 작업 공간 연결’을 누르세요. 브라우저에 다시 로그인할 필요는 없습니다.",false);
                } catch (OperationCanceledException) { if (!exiting) Feedback(feedback,"연결 취소됨","로그인이 완료되지 않았다면 다시 시도하세요. 기존 위젯 연결 설정은 유지했습니다.",false); }
                catch (Exception ex) { if (!exiting) Feedback(feedback,"작업 공간을 불러오지 못했습니다",Friendly(ex),true); }
                finally {
                    if (Object.ReferenceEquals(higgsfieldOperation,operation)) higgsfieldOperation = null;
                    rememberResult(); operation.Dispose(); lease.Dispose(); operationBusy(false);
                    if (!exiting) { RenderHiggsfield(); UpdateServiceVisibility(); UpdateFooter(); }
                }
            };
            cancel.Click += (s,e) => CancelHiggsfieldOperation();
            var help = new LocalizedStackPanel();
            help.Children.Add(ConnectionText("• 실행 파일을 못 찾으면 CLI 설치 후 위젯을 다시 실행하거나 고급 설정에서 선택하세요.\n• 인증 만료는 ‘힉스필드 로그인’으로 다시 연결하세요.\n• 브라우저가 열리지 않으면 터미널에서 higgsfield auth login을 실행한 뒤 ‘연결 확인’을 누르세요.\n• 작업 공간이 하나면 자동 연결합니다. 여러 개면 ‘작업 공간 불러오기’로 목록에서 선택하세요. ID 직접 입력은 고급 설정에서 가능합니다.\n• 응답 형식 오류는 공식 CLI를 업데이트하세요.\n• 전체 한도·초기화일이 제공되지 않아 잔여 비율과 초기화 알림을 표시하지 않습니다.",11,"#647070",FontWeights.Normal));
            disconnect = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "위젯 연결 해제", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,12,0,0) };
            disconnect.Click += (s,e) => {
                if (higgsfieldView.Busy || IsServiceConnectionPending("higgsfield") || higgsfieldOperation != null) { Feedback(feedback,"조회 진행 중","조회를 마치거나 취소한 뒤 연결을 해제하세요.",false); return; }
                try {
                    var next = CopyHiggsfieldSettings(); next.WebConnected = false; SaveHiggsfieldSettings(next);
                    operationFeedback = true;
                    workspaceSelectionPending = false;
                    higgsfieldView.Snapshot = null; higgsfieldView.LastSuccess = null; higgsfieldView.Error = null; ForgetUsageService("higgsfield");
                    RenderHiggsfield(); UpdateServiceVisibility(); UpdateFooter();
                    Feedback(feedback,"위젯 연결 해제됨","자동 조회를 중단했습니다. 공식 CLI의 로그인은 유지됩니다.",false);
                    rememberResult();
                } catch { Feedback(feedback,"저장 실패","설정 폴더의 쓰기 권한을 확인한 뒤 다시 시도하세요.",true); }
            };
            help.Children.Add(disconnect); Details(body,"연결 문제 해결 · 연결 해제",help);
            body.Children.Add(ConnectionText("공식 CLI가 로그인을 관리합니다. 위젯은 잔량 조회만 실행하며 이미지·영상 생성 요청을 보내지 않습니다.",11,"#647070",FontWeights.Normal,new Thickness(0,15,0,0)));
        }

        private void SaveHiggsfieldApiBalance(double? amount)
        {
            if (amount.HasValue && IsServiceConnectionPending("higgsfield-api")) throw new InvalidOperationException("조회 또는 로그인을 마친 뒤 전환해 주세요.");
            if (amount.HasValue && (Double.IsNaN(amount.Value) || Double.IsInfinity(amount.Value) || amount < 0 || amount > 1000000000)) throw new ArgumentException("0~1,000,000,000 사이의 USD 금액을 입력하세요.");
            var next = CopyHiggsfieldSettings(); next.ApiBalanceUsd = amount; next.ApiObservedAt = amount.HasValue ? (DateTimeOffset?)DateTimeOffset.Now : null;
            SaveHiggsfieldSettings(next);
            if (amount.HasValue) CommitVerifiedServiceSelection("higgsfield-api");
            if (!amount.HasValue) ForgetUsageService("higgsfield-api");
            RenderHiggsfieldApi(); UpdateServiceVisibility(); UpdateFooter();
        }

        private void BuildHiggsfieldApiSettings(StackPanel body)
        {
            ConnectionSteps(body,"개발자 API 달러 잔액 · 수동 기록",
                "API 콘솔의 Billing에서 현재 달러 잔액을 확인하세요.",
                "확인한 USD 금액을 아래에 입력하고 저장하세요.");
            body.Children.Add(ConnectionText("공개 문서에서 잔액 조회 API를 확인하지 못해 자동 조회를 제공하지 않습니다. 웹 구독 크레딧과 별도이며 API 키를 등록할 필요가 없습니다.",11,"#647070",FontWeights.Normal,new Thickness(0,4,0,8)));
            body.Children.Add(SmallAction("잔액 페이지 ↗",() => OpenUsagePage("higgsfield-api")));
            body.Children.Add(ConnectionText("현재 잔액 (USD)",14,"#252827",FontWeights.SemiBold,new Thickness(0,15,0,7)));
            var balance = new LocalizedTextBox { Text = higgsfieldSettings.ApiBalanceUsd.HasValue ? higgsfieldSettings.ApiBalanceUsd.Value.ToString("0.########",CultureInfo.InvariantCulture) : "" };
            System.Windows.Automation.AutomationProperties.SetName(balance,"Higgsfield API 달러 잔액"); body.Children.Add(balance);
            body.Children.Add(ConnectionText("예: 12.50 · 비율과 초기화일은 임의로 계산하지 않습니다.",10,"#647070",FontWeights.Normal,new Thickness(0,5,0,12)));
            var actions = new WrapPanel(); var save = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "지금 확인한 잔액 저장", Margin = new Thickness(0,0,8,8) }; var clear = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "기록 비우기", Margin = new Thickness(0,0,0,8) };
            actions.Children.Add(save); actions.Children.Add(clear); body.Children.Add(actions);
            var feedback = MakeConnectionFeedback(body);
            Action update = () => {
                if (!RequireServiceConnection("higgsfield-api",feedback)) return;
                Feedback(feedback,"자동 갱신 안 됨",higgsfieldSettings.ApiObservedAt.HasValue ? "마지막 수동 기록 " + higgsfieldSettings.ApiObservedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "\n잔액이 바뀌면 새 값을 기록하세요." : "아직 기록이 없습니다. API 콘솔에서 확인한 금액을 입력하세요.",false);
            };
            connectionStateRefreshers["higgsfield-api"] = update; update();
            save.Click += (s,e) => {
                if (!RequireServiceConnection("higgsfield-api",feedback)) return;
                double amount;
                if (!Double.TryParse(balance.Text.Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out amount) || amount < 0 || amount > 1000000000) { Feedback(feedback,"금액 확인","0~1,000,000,000 사이의 USD 금액을 입력하세요. 예: 12.50",true); return; }
                try { SaveHiggsfieldApiBalance(amount); update(); } catch { Feedback(feedback,"기록 저장 실패","설정 폴더의 쓰기 권한을 확인한 뒤 다시 시도하세요.",true); }
            };
            clear.Click += (s,e) => { try { SaveHiggsfieldApiBalance(null); balance.Clear(); update(); } catch { Feedback(feedback,"기록 삭제 실패","설정 폴더의 쓰기 권한을 확인한 뒤 다시 시도하세요.",true); } };
        }
    }
}
