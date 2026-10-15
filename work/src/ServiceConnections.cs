using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private CancellationTokenSource codexLoginOperation;
        private Action clearCodexBrowserRecovery;
        private void CancelCodexLogin() { if (clearCodexBrowserRecovery != null) clearCodexBrowserRecovery(); clearCodexBrowserRecovery = null; if (codexLoginOperation != null) codexLoginOperation.Cancel(); }
        private void BuildElevenSettings(StackPanel body) { BuildApiKeySettings(body,"eleven"); }
        private void BuildOpenAiSettings(StackPanel body) { BuildApiKeySettings(body,"openai"); }

        private void BuildApiKeySettings(StackPanel body, string id)
        {
            bool isEleven = id == "eleven";
            var view = ViewFor(id);
            ConnectionSteps(body,"API 연결 · 한 번 설정하면 자동 갱신",
                isEleven ? "키 발급 페이지에서 API 키를 만드세요." : "조직 소유자 계정으로 Admin API 키를 만드세요.",
                isEleven ? "권한에서 사용자 → 읽기 (User → Read)를 켜세요." : "일반 API 키 대신 조직의 관리자 키를 사용하세요.",
                "아래에 키를 붙여 넣고 ‘연결 확인 후 저장’을 누르세요.");
            var links = new WrapPanel();
            links.Children.Add(SmallAction(isEleven ? "키 발급 · 권한 설정 ↗" : "관리자 키 발급 ↗",() => OpenOfficialPage(isEleven ? ElevenKeysUrl : OpenAiKeysUrl)));
            if (!isEleven) links.Children.Add(SmallAction("사용량 페이지 ↗",() => OpenUsagePage(id)));
            body.Children.Add(links);
            body.Children.Add(ConnectionText(isEleven ? "ElevenLabs API 키" : "OpenAI Admin API 키",14,"#252827",FontWeights.SemiBold,new Thickness(0,15,0,7)));
            var key = new PasswordBox(); System.Windows.Automation.AutomationProperties.SetName(key,isEleven ? "ElevenLabs API 키" : "OpenAI Admin API 키"); body.Children.Add(key);
            Func<string> savedKey = () => isEleven ? secret : openAiKey;
            var hint = ConnectionText(String.IsNullOrEmpty(savedKey()) ? "붙여 넣은 키는 화면에 표시되지 않습니다." : "저장된 키가 있습니다. 비워 두면 기존 키로 다시 확인합니다.",10,"#647070",FontWeights.Normal,new Thickness(0,6,0,12)); body.Children.Add(hint);
            var save = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "연결 확인 후 저장", HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 150 }; body.Children.Add(save);
            var feedback = MakeConnectionFeedback(body);
            connectionStateRefreshers[id] = () => {
                if (!save.IsEnabled) return;
                if (!RequireActiveService(id,feedback)) return;
                Feedback(feedback,view.LastSuccess.HasValue ? "연결됨" : "연결 준비",view.LastSuccess.HasValue ? "저장된 키로 사용량을 확인했습니다." : "연결에 성공한 키만 이 PC에 암호화 저장합니다.",false);
                if (view.Issue != null) FeedbackIssue(feedback,id,new ConnectionException(view.Issue));
                else if (!String.IsNullOrEmpty(view.Error)) Feedback(feedback,"연결을 확인해 주세요",view.Error,true);
            };
            RefreshConnectionEditor(id);
            var details = new LocalizedStackPanel();
            TextBox budget = null;
            if (!isEleven) {
                details.Children.Add(ConnectionText("월 예산 (USD · 선택)",14,"#475256",FontWeights.SemiBold,new Thickness(0,0,0,6)));
                budget = new LocalizedTextBox { Text = extras.OpenAiMonthlyBudgetUsd.HasValue ? extras.OpenAiMonthlyBudgetUsd.Value.ToString("0.##",CultureInfo.InvariantCulture) : "" };
                System.Windows.Automation.AutomationProperties.SetName(budget,"OpenAI 월 예산 USD"); details.Children.Add(budget);
                details.Children.Add(ConnectionText("비우면 사용 금액만 표시합니다. 입력하면 설정 예산에서 사용 금액을 뺍니다. 실제 결제 한도는 바뀌지 않습니다.",10,"#647070",FontWeights.Normal,new Thickness(0,6,0,12)));
            }
            details.Children.Add(ConnectionText(isEleven ? "웹과 API가 함께 사용하는 구독 크레딧입니다. 별도 PAYG 선불 잔액은 포함하지 않습니다." : "UTC 기준 이번 달 조직 전체 API 비용입니다. ChatGPT 구독·선불 잔액은 포함하지 않으며 집계가 지연될 수 있습니다.",11,"#647070",FontWeights.Normal));
            var remove = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "저장된 키 삭제 · 연결 해제", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,12,0,0), IsEnabled = !String.IsNullOrEmpty(savedKey()) }; details.Children.Add(remove);
            Details(body,isEleven ? "표시 범위 · 연결 해제" : "월 예산 · 표시 범위 · 연결 해제",details);
            save.Click += async (s,e) => {
                if (!RequireServiceConnection(id,feedback)) return;
                if (IsServiceConnectionPending(id)) { Feedback(feedback,"조회 진행 중","현재 조회 또는 로그인이 끝난 뒤 다시 눌러 주세요.",false); return; }
                if (key.Password.Length == 0 && String.IsNullOrEmpty(savedKey())) { Feedback(feedback,"API 키가 필요합니다","위의 발급 페이지에서 키를 만든 뒤 입력란에 붙여 넣어 주세요.",true); key.Focus(); return; }
                double? amount = null; double parsed;
                if (budget != null && !String.IsNullOrWhiteSpace(budget.Text)) {
                    if (!Double.TryParse(budget.Text.Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out parsed) || parsed < 0.01 || parsed > 1000000000) {
                        Feedback(feedback,"월 예산을 확인해 주세요","0.01 USD 이상의 금액을 입력하거나 비워 두세요. 예: 20.00",true); return;
                    }
                    amount = parsed;
                }
                string candidate = key.Password.Length == 0 ? savedKey() : key.Password;
                bool trackingSourceChanged = candidate.Trim() != savedKey() || (!isEleven && extras.OpenAiMonthlyBudgetUsd != amount);
                bool storedPhase = false, stored = false;
                IDisposable lease;
                if (!TryReserveServiceConnection(id,out lease)) return;
                save.IsEnabled = false; key.IsEnabled = false; remove.IsEnabled = false;
                if (budget != null) budget.IsEnabled = false;
                Feedback(feedback,"연결 확인 중…","사용량 조회에 성공하면 키를 저장합니다.",false);
                try {
                    if (demo) { Feedback(feedback,"미리보기","실제 API를 호출하거나 키를 저장하지 않았습니다.",false); return; }
                    await WaitForServiceRefreshAsync(id,ServiceConnectionsToken);
                    ThrowIfServiceConnectionChanged(id,lease);
                    var snapshot = await ApiConnectionFlow.CheckAndSaveAsync(candidate,
                        value => isEleven ? eleven.ReadAsync(value) : openAi.ReadAsync(value,amount),
                        value => {
                            ThrowIfServiceConnectionChanged(id,lease);
                            storedPhase = true;
                            if (isEleven) { SettingsStore.SaveApiKey(value); secret = value; }
                            else {
                                string prior = openAiKey; double? priorBudget = extras.OpenAiMonthlyBudgetUsd;
                                extras.OpenAiMonthlyBudgetUsd = amount;
                                try { ExtraSettingsStore.SaveOpenAiKey(value); ExtraSettingsStore.Save(extras); }
                                catch { extras.OpenAiMonthlyBudgetUsd = priorBudget; try { ExtraSettingsStore.SaveOpenAiKey(prior); } catch { openAiKey = ExtraSettingsStore.LoadOpenAiKey(); } throw; }
                                openAiKey = value;
                            }
                        });
                    stored = true;
                    if (exiting) return;
                    CommitVerifiedServiceSelection(id);
                    view.Snapshot = snapshot; view.LastSuccess = DateTimeOffset.Now; view.Error = null; view.Issue = null; RefreshSucceeded(view);
                    if (trackingSourceChanged) ForgetUsageService(id);
                    ObserveUsage(view,"api");
                    key.Clear(); hint.Text = "저장된 키가 있습니다. 비워 두면 기존 키로 다시 확인합니다.";
                    Feedback(feedback,"연결 완료 · 저장됨","사용량을 확인했습니다. 선택한 서비스는 위젯에서 자동으로 갱신합니다.",false);
                } catch (ArgumentException ex) { Feedback(feedback,"API 키를 확인해 주세요",ex.Message,true); }
                catch (Exception ex) {
                    if (exiting) return;
                    if (stored) Feedback(feedback,"연결 저장 완료 · 전환 확인 필요","연결 정보는 저장했습니다. 기존 서비스 선택은 유지되며, 위의 전환 버튼으로 다시 시도할 수 있습니다.",true);
                    else if (storedPhase) Feedback(feedback,"조회 성공 · 저장 실패","키를 저장하지 못했습니다. 위젯을 다시 실행한 뒤 연결해 주세요.",true);
                    else {
                        FeedbackIssue(feedback,id,ex); feedback.Detail.Text = UiL.Source(feedback.Detail,TextBlock.TextProperty) + (String.IsNullOrEmpty(savedKey()) ? "\n키는 저장하지 않았습니다." : "\n기존에 저장된 키는 유지했습니다.");
                        if (!String.IsNullOrEmpty(savedKey()) && candidate.Trim() == savedKey()) { RefreshFailed(view,ex); view.Issue = ConnectionDiagnostics.For(id,ex); view.Error = view.Issue.Title; }
                    }
                } finally {
                    candidate = null; lease.Dispose(); save.IsEnabled = true; key.IsEnabled = true; remove.IsEnabled = !String.IsNullOrEmpty(savedKey());
                    if (budget != null) budget.IsEnabled = true;
                    if (!exiting) { RefreshLocalExtras(); Render(elevenView,false); UpdateFooter(); }
                }
            };
            remove.Click += (s,e) => {
                if (IsServiceConnectionPending(id) || view.Busy) { Feedback(feedback,"조회 진행 중","현재 조회가 끝난 뒤 연결을 해제해 주세요.",false); return; }
                try {
                    if (demo) { Feedback(feedback,"미리보기","실제 저장된 키는 변경하지 않았습니다.",false); return; }
                    if (isEleven) { SettingsStore.SaveApiKey(""); secret = ""; } else { ExtraSettingsStore.SaveOpenAiKey(""); openAiKey = ""; }
                    key.Clear(); view.Snapshot = null; view.LastSuccess = null; view.Error = null; view.Issue = null;
                    ForgetUsageService(id);
                    remove.IsEnabled = false; hint.Text = "새 키를 붙여 넣으면 다시 연결할 수 있습니다.";
                    Feedback(feedback,"연결 해제됨","저장된 키를 삭제했습니다. 다시 연결하거나 위에서 다른 서비스로 전환할 수 있습니다.",false);
                    RefreshLocalExtras(); Render(elevenView,false); UpdateFooter();
                } catch { Feedback(feedback,"연결 해제 실패","키를 삭제하지 못했습니다. 위젯을 다시 실행한 뒤 시도해 주세요.",true); }
            };
        }

        private void BuildCodexSettings(StackPanel body)
        {
            bool operationFeedback = false;
            UsageSnapshot operationSnapshot = null;
            DateTimeOffset? operationLastSuccess = null;
            string operationError = null;
            Action rememberResult = () => { operationSnapshot = codexView.Snapshot; operationLastSuccess = codexView.LastSuccess; operationError = codexView.Error; };
            var environment = AddConnectionEnvironment(body,"codex",settings.CodexWslDistribution);
            ConnectionSteps(body,"설치 확인 → 로그인 확인 → 사용량 확인","Codex가 설치되어 있는지 먼저 확인합니다. 없으면 설치 안내를 따라 설치하세요.","설치 후 ‘연결 확인’을 누르세요. 로그인이 필요하면 브라우저에서 로그인합니다.","사용량 확인에 성공하면 연결합니다. 기존 서비스 연결과 이력은 유지됩니다.");
            var actions = new WrapPanel();
            var check = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "연결 확인", Margin = new Thickness(0,0,8,8) };
            var login = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "브라우저로 로그인", Margin = new Thickness(0,0,8,8) };
            var cancel = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "로그인 취소", Margin = new Thickness(0,0,0,8), Visibility = Visibility.Collapsed };
            var install = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "Codex 설치 방법 보기", Margin = new Thickness(0,0,8,8), Visibility = Visibility.Collapsed };
            actions.Children.Add(install); actions.Children.Add(check); actions.Children.Add(login); actions.Children.Add(cancel); body.Children.Add(actions);
            var feedback = MakeConnectionFeedback(body);
            var recovery = new CodexBrowserRecovery();
            var recoveryActions = new WrapPanel { Margin = new Thickness(0,8,0,0), Visibility = Visibility.Collapsed };
            var reopen = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "브라우저 다시 열기", Margin = new Thickness(0,0,8,8) };
            var copy = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "로그인 주소 복사", Margin = new Thickness(0,0,0,8) };
            recoveryActions.Children.Add(reopen); recoveryActions.Children.Add(copy); body.Children.Add(recoveryActions);
            Action clearRecovery = () => { recovery.Clear(); recoveryActions.Visibility = Visibility.Collapsed; };
            clearCodexBrowserRecovery = clearRecovery;
            Action openBrowser = () => {
                if (!recovery.Available) return;
                try { recovery.Use(url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })); Feedback(feedback,"브라우저 로그인 대기","브라우저에서 Codex 계정으로 로그인하세요. 완료하면 자동으로 갱신합니다. 최대 3분 동안 기다립니다.\n이 창을 닫아도 로그인 대기는 계속됩니다.",false); }
                catch { Feedback(feedback,"브라우저를 열지 못했습니다","다시 열거나 로그인 주소를 복사해 브라우저에 붙여 넣으세요. 로그인 대기는 계속됩니다.",true); }
            };
            reopen.Click += (s,e) => openBrowser();
            copy.Click += (s,e) => {
                if (!recovery.Available) return;
                try { recovery.Use(Clipboard.SetText); Feedback(feedback,"로그인 주소를 복사했습니다","브라우저 주소창에 붙여 넣어 로그인하세요. 이 주소를 다른 사람에게 공유하지 마세요.",false); }
                catch { Feedback(feedback,"주소를 복사하지 못했습니다","잠시 후 다시 누르거나 브라우저 다시 열기를 이용하세요.",true); }
            };
            install.Click += (s,e) => OpenOfficialPage(CodexGuideUrl);
            var links = new WrapPanel(); links.Children.Add(SmallAction("Codex 설치 안내 ↗",() => OpenOfficialPage(CodexGuideUrl))); body.Children.Add(links);
            var advanced = new LocalizedStackPanel();
            advanced.Children.Add(ConnectionText("자동 검색에 실패한 경우에만 codex.exe를 선택하세요.",11,"#647070",FontWeights.Normal,new Thickness(0,0,0,8)));
            var line = new LocalizedGrid(); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var path = new LocalizedTextBox { Text = settings.CodexPath }; System.Windows.Automation.AutomationProperties.SetName(path,"Codex 실행 파일 경로"); line.Children.Add(path);
            var browse = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "찾기", Margin = new Thickness(8,0,0,0) }; Grid.SetColumn(browse,1); line.Children.Add(browse); advanced.Children.Add(line);
            Details(body,"고급 설정 · Codex 실행 파일 위치",advanced);
            Func<bool> installed = () => {
                if (demo) return true;
                if (environment.Distribution.Length > 0) { var probe = environment.SelectedProbe; return probe == null || String.IsNullOrEmpty(probe.Error) && !String.IsNullOrEmpty(probe.CodexPath); }
                try { CodexService.FindExecutable(path.Text); return true; } catch { return false; }
            };
            Action updateActions = () => {
                bool found = installed();
                path.IsEnabled = browse.IsEnabled = environment.Distribution.Length == 0 && check.IsEnabled;
                install.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
                login.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
                check.Content = found ? "연결 확인" : "설치 다시 확인";
            };
            Action updateState = () => {
                if (!check.IsEnabled || loginPending) return;
                updateActions();
                if (operationFeedback && Object.ReferenceEquals(operationSnapshot,codexView.Snapshot) && operationLastSuccess == codexView.LastSuccess && operationError == codexView.Error) return;
                operationFeedback = false;
                if (!installed()) {
                    if (environment.Distribution.Length > 0) Feedback(feedback,"WSL 연결을 확인해 주세요",environment.Hint.Text,true);
                    else FeedbackIssue(feedback,"codex",new ConnectionException(ConnectionDiagnostics.CodexMissing(!String.IsNullOrWhiteSpace(path.Text))));
                    return;
                }
                if (!String.Equals(environment.Distribution,settings.CodexWslDistribution,StringComparison.Ordinal)) { Feedback(feedback,"연결 환경 선택됨","연결 확인에 성공하면 선택한 환경으로 전환합니다. 기존 연결은 그때까지 유지됩니다.",false); return; }
                if (!IsServiceActive("codex")) { Feedback(feedback,"설치 확인 완료 · 연결 준비",InactiveServiceMessage,false); return; }
                Feedback(feedback,codexView.LastSuccess.HasValue && String.IsNullOrEmpty(codexView.Error) ? "연결됨" : "설치 확인 완료 · 연결 준비",codexView.LastSuccess.HasValue && String.IsNullOrEmpty(codexView.Error) ? "기존 Codex 로그인으로 사용량을 확인했습니다." : "연결 확인을 눌러 로그인과 사용량을 확인하세요. 처음 연결한다면 브라우저로 로그인하세요.",false);
                if (codexView.Issue != null) FeedbackIssue(feedback,"codex",new ConnectionException(codexView.Issue));
                else if (!String.IsNullOrEmpty(codexView.Error)) Feedback(feedback,"연결을 확인해 주세요",codexView.Error,true);
            };
            environment.Changed = () => { if (!loginPending && check.IsEnabled) operationFeedback = false; updateState(); }; connectionStateRefreshers["codex"] = updateState; updateState();
            path.TextChanged += (s,e) => { if (!loginPending && check.IsEnabled) operationFeedback = false; updateState(); };
            browse.Click += (s,e) => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = L.Text("Codex 실행 파일 (*.exe)|*.exe"), CheckFileExists = true }; if (picker.ShowDialog(servicesWindow) == true) path.Text = picker.FileName; };
            login.Click += async (s,e) => {
                if (!RequireServiceConnection("codex",feedback)) return;
                if (demo) { Feedback(feedback,"미리보기","실제 로그인 페이지는 열지 않습니다.",false); return; }
                if (IsServiceConnectionPending("codex")) { Feedback(feedback,"조회 진행 중","현재 조회 또는 로그인이 끝난 뒤 다시 눌러 주세요.",false); return; }
                string next = path.Text.Trim(); string nextDistribution = environment.Distribution;
                if (!installed()) { updateState(); return; }
                if (nextDistribution.Length == 0 && next.Length > 0 && (!File.Exists(next) || !String.Equals(Path.GetExtension(next),".exe",StringComparison.OrdinalIgnoreCase))) { Feedback(feedback,"실행 파일을 확인해 주세요","존재하는 codex.exe를 선택하거나 경로를 비워 자동 검색하세요.",true); return; }
                IDisposable lease;
                if (!TryReserveServiceConnection("codex",out lease)) return;
                var operation = CancellationTokenSource.CreateLinkedTokenSource(ServiceConnectionsToken); codexLoginOperation = operation;
                operationFeedback = true;
                clearCodexBrowserRecovery = clearRecovery;
                login.IsEnabled = false; check.IsEnabled = false; path.IsEnabled = false; browse.IsEnabled = false; environment.SetEnabled(false); loginPending = true;
                cancel.Visibility = Visibility.Visible;
                Feedback(feedback,"로그인 준비 중","Codex를 실행하고 로그인 주소를 준비합니다. 브라우저가 열릴 때까지 잠시 기다려 주세요.",false); UpdateFooter();
                bool saving = false;
                try {
                    await WaitForServiceRefreshAsync("codex",operation.Token);
                    ThrowIfServiceConnectionChanged("codex",lease);
                    var snapshot = await codex.LoginAsync(next,nextDistribution,url => Dispatcher.Invoke(() => {
                        if (operation.IsCancellationRequested || exiting) return;
                        recovery.Set(url); recoveryActions.Visibility = Visibility.Visible; openBrowser();
                    }),operation.Token);
                    if (operation.IsCancellationRequested || exiting) return;
                    ThrowIfServiceConnectionChanged("codex",lease);
                    string previous = settings.CodexPath; string previousDistribution = settings.CodexWslDistribution;
                    settings.CodexPath = next; settings.CodexWslDistribution = nextDistribution; saving = true;
                    try { SettingsStore.Save(settings); } catch { settings.CodexPath = previous; settings.CodexWslDistribution = previousDistribution; throw; }
                    CommitVerifiedServiceSelection("codex");
                    codexView.Snapshot = snapshot; codexView.LastSuccess = DateTimeOffset.Now; codexView.Error = null; codexView.Issue = null; RefreshSucceeded(codexView);
                    ForgetUsageService("codex"); ObserveUsage(codexView,CodexUsageSource());
                    Feedback(feedback,"로그인 완료","Codex 사용량을 확인했습니다. 이후에는 자동 갱신됩니다.",false);
                } catch (OperationCanceledException) { if (!exiting) Feedback(feedback,"로그인 취소됨","새 로그인 대기를 종료했습니다. 기존 위젯 설정과 이력은 유지됩니다.",false); }
                catch (Exception ex) {
                    if (!exiting) {
                        if (saving) Feedback(feedback,"조회 성공 · 설정 저장 실패","위젯을 다시 실행한 뒤 연결을 확인해 주세요.",true);
                        else if (ex is TimeoutException) Feedback(feedback,"로그인 대기 시간 초과",ex.Message,true);
                        else FeedbackIssue(feedback,"codex",ex);
                    }
                } finally {
                    clearRecovery();
                    if (Object.ReferenceEquals(clearCodexBrowserRecovery,clearRecovery)) clearCodexBrowserRecovery = null;
                    if (Object.ReferenceEquals(codexLoginOperation,operation)) codexLoginOperation = null;
                    rememberResult(); operation.Dispose(); lease.Dispose(); loginPending = false;
                    login.IsEnabled = true; check.IsEnabled = true; path.IsEnabled = true; browse.IsEnabled = true; environment.SetEnabled(true); cancel.Visibility = Visibility.Collapsed;
                    updateActions();
                    nextRefresh = DateTimeOffset.MinValue;
                    if (!exiting) { Render(codexView,true); UpdateServiceVisibility(); UpdateFooter(); }
                }
            };
            cancel.Click += (s,e) => CancelCodexLogin();
            check.Click += async (s,e) => {
                if (!RequireServiceConnection("codex",feedback)) return;
                if (IsServiceConnectionPending("codex")) { Feedback(feedback,"조회 진행 중","현재 조회 또는 로그인이 끝난 뒤 다시 눌러 주세요.",false); return; }
                if (demo) { Feedback(feedback,"미리보기","실제 연결과 설정은 변경하지 않았습니다.",false); return; }
                string next = path.Text.Trim(); string nextDistribution = environment.Distribution;
                if (!installed()) { updateState(); return; }
                if (nextDistribution.Length == 0 && next.Length > 0 && (!File.Exists(next) || !String.Equals(Path.GetExtension(next),".exe",StringComparison.OrdinalIgnoreCase))) { Feedback(feedback,"실행 파일을 확인해 주세요","‘고급 설정’에서 존재하는 codex.exe를 선택하거나 경로를 비워 자동 검색하세요.",true); return; }
                IDisposable lease;
                if (!TryReserveServiceConnection("codex",out lease)) return;
                operationFeedback = true;
                check.IsEnabled = false; login.IsEnabled = false; path.IsEnabled = false; browse.IsEnabled = false; environment.SetEnabled(false);
                Feedback(feedback,"연결 확인 중…","Codex 로그인과 사용량을 확인합니다.",false);
                bool saving = false;
                try {
                    await WaitForServiceRefreshAsync("codex",ServiceConnectionsToken);
                    ThrowIfServiceConnectionChanged("codex",lease);
                    var snapshot = await codex.ReadAsync(next,nextDistribution);
                    if (exiting) return;
                    ThrowIfServiceConnectionChanged("codex",lease);
                    string previous = settings.CodexPath; string previousDistribution = settings.CodexWslDistribution;
                    settings.CodexPath = next; settings.CodexWslDistribution = nextDistribution; saving = true;
                    try { SettingsStore.Save(settings); } catch { settings.CodexPath = previous; settings.CodexWslDistribution = previousDistribution; throw; }
                    CommitVerifiedServiceSelection("codex");
                    codexView.Snapshot = snapshot; codexView.LastSuccess = DateTimeOffset.Now; codexView.Error = null; codexView.Issue = null; RefreshSucceeded(codexView);
                    ForgetUsageService("codex");
                    ObserveUsage(codexView,CodexUsageSource());
                    Feedback(feedback,"연결 완료","사용량을 확인했습니다. 이후에는 위젯에서 자동 갱신합니다.",false);
                } catch (Exception ex) {
                    if (exiting) return;
                    if (saving) Feedback(feedback,"조회 성공 · 설정 저장 실패","위젯을 다시 실행한 뒤 연결을 확인해 주세요.",true);
                    else { FeedbackIssue(feedback,"codex",ex); if (next == settings.CodexPath && nextDistribution == settings.CodexWslDistribution) { RefreshFailed(codexView,ex); codexView.Issue = ConnectionDiagnostics.For("codex",ex); codexView.Error = codexView.Issue.Title; } }
                } finally {
                    rememberResult(); lease.Dispose(); check.IsEnabled = true; login.IsEnabled = true; path.IsEnabled = true; browse.IsEnabled = true; environment.SetEnabled(true);
                    updateActions();
                    if (!exiting) { Render(codexView,true); UpdateFooter(); }
                }
            };
            body.Children.Add(ConnectionText("Codex 구독 한도와 추가 크레딧을 표시합니다. 일반 ChatGPT 대화의 남은 횟수는 포함하지 않습니다.",11,"#647070",FontWeights.Normal,new Thickness(0,16,0,0)));
        }

        private static void ClearSecretInputs(DependencyObject parent)
        {
            var password = parent as PasswordBox;
            if (password != null) password.Clear();
            foreach (object child in LogicalTreeHelper.GetChildren(parent)) {
                var dependency = child as DependencyObject; if (dependency != null) ClearSecretInputs(dependency);
            }
        }
    }
}

