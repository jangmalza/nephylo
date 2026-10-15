using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;
internal static class LocalizationTests
{
    private static int count;
    private static readonly Regex Hangul = new Regex("[가-힣]");
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool yes,string message) { count++; if (!yes) throw new Exception(message); }
    static T Field<T>(WidgetWindow window,string name) { return (T)typeof(WidgetWindow).GetField(name,Flags).GetValue(window); }
    static void Call(WidgetWindow window,string name,params object[] args) { typeof(WidgetWindow).GetMethod(name,Flags).Invoke(window,args); }
    static IEnumerable<DependencyObject> Walk(DependencyObject root) { yield return root; foreach (object child in LogicalTreeHelper.GetChildren(root)) if(child is DependencyObject) foreach(var descendant in Walk((DependencyObject)child)) yield return descendant; }
    static void CheckEnglish(DependencyObject root,string scope) { foreach(var item in Walk(root)) { var t=item as TextBlock; if(t!=null && !t.Text.Contains("언어") && t.Text!="한국어") Check(!Hangul.IsMatch(t.Text),scope+": untranslated text: "+t.Text); var c=item as ContentControl; if(c!=null && c.Content is string && (string)c.Content!="한국어") Check(!Hangul.IsMatch((string)c.Content),scope+": untranslated content: "+c.Content); } }
    [STAThread] public static int Main()
    {
        string root=Path.GetDirectoryName(typeof(LocalizationTests).Assembly.Location), isolated=Path.Combine(root,"localization-test-"+Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride=isolated; SettingsStore.StartupRegistrySubKeyOverride=@"Software\UsageWidget\Tests\locale-"+Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride=Path.Combine(isolated,"claude");
        WidgetWindow window=null;
        try {
            Check(SettingsStore.Load().Language=="ko","Existing installations default to Korean");
            var settings=new WidgetSettings {Language="en",Theme="light",CodexPath="sample-path",ServiceOrder=new[]{"eleven","codex"}};
            SettingsStore.Save(settings); var loaded=SettingsStore.Load();
            Check(loaded.Language=="en" && loaded.Theme=="light" && loaded.CodexPath=="sample-path" && loaded.ServiceOrder[0]=="eleven","English round-trips without changing existing preferences");
            settings.Language="invalid"; SettingsStore.Save(settings); Check(SettingsStore.Load().Language=="ko","Unsupported locale normalizes safely");
            File.WriteAllText(Path.Combine(isolated,"settings.json"),"{\"Theme\":\"light\"}"); Check(SettingsStore.Load().Language=="ko","Legacy settings load as Korean");
            window=new WidgetWindow(true); Field<DispatcherTimer>(window,"timer").Stop(); Field<Forms.NotifyIcon>(window,"tray").Visible=false; Call(window,"ShowDemo");
            var model=Field<ProviderView>(window,"codexView").Snapshot; string modelLabel=model.Rows[0].Label;
            var textbox=new LocalizedTextBox {Text="사용자 이름 user@example.com"}; var label=new LocalizedTextBlock {Text="연결 확인"};
            var raw=new LocalizedTextBlock {Text="사용 이력"}; UiL.Raw(raw);
            var button=new LocalizedButton {Content="저장",ToolTip="설정을 저장하지 못했습니다. 저장 폴더의 쓰기 권한을 확인하고 다시 시도해 주세요."}; AutomationProperties.SetName(button,"위젯 설정");
            Call(window,"ApplyLanguage","en");
            Check(L.Text("무료 이용 안내")=="Free access" && L.Text("모든 기능 무료")=="All features free" && L.Text("Nephylo 회원가입이나 로그인 없이 이용할 수 있습니다.")=="No Nephylo account or sign-in is required.","Accountless free-access labels translate into English");
            Check(label.Text=="Check connection" && (string)button.Content=="Save","Text and buttons switch immediately");
            Check((string)label.ReadLocalValue(TextBlock.TextProperty)=="연결 확인","Display translation retains original source value");
            Check(textbox.Text=="사용자 이름 user@example.com" && raw.Text=="사용 이력","User input and raw user labels are not translated");
            Check(AutomationProperties.GetName(button)=="Widget settings" && !Hangul.IsMatch((string)button.ToolTip),"Accessibility and tooltips switch");
            Check(window.Title=="Nephylo — Preview" && Field<Forms.NotifyIcon>(window,"tray").Text=="Nephylo · AI usage","Window and tray switch");
            foreach(var item in Walk(window)) {
                var t=item as TextBlock; if(t!=null) Check(!Hangul.IsMatch(t.Text),"Untranslated demo text: "+t.Text);
            }
            window.Show();
            var dialogs = new Dictionary<string,string> { {"OpenSettings","settingsWindow"},{"OpenAccount","planWindow"},{"OpenPlan","planWindow"},{"OpenReleaseInfo","releaseWindow"},{"OpenUsageHistory","historyWindow"},{"OpenNotificationSettings","notificationWindow"} };
            foreach (var pair in dialogs) {
                Call(window,pair.Key); var dialog=Field<Window>(window,pair.Value); Check(dialog!=null,"Dialog created: "+pair.Key); CheckEnglish(dialog,pair.Key);
                if (pair.Key=="OpenSettings") {
                    var theme=Walk(dialog).OfType<ComboBox>().Single(item=>AutomationProperties.GetName(item)=="Theme");
                    Check(theme.IsEnabled && theme.Items.Count==2,"Both themes remain available without a Nephylo login");
                }
                if (pair.Key=="OpenAccount" || pair.Key=="OpenPlan") {
                    var visible=String.Join("\n",Walk(dialog).OfType<TextBlock>().Select(item=>item.Text));
                    Check(visible.Contains("All features free") && visible.Contains("No Nephylo account or sign-in is required."),"Legacy account and plan entry points explain accountless free access");
                    Check(!Walk(dialog).OfType<TextBox>().Any(),"Free access information contains no sign-in inputs");
                }
                dialog.Close();
            }
            foreach (string service in new[]{"codex","eleven","openai","claude","gemini","cursor","higgsfield","higgsfield-api"}) { Call(window,"OpenServices",service); var dialog=Field<Window>(window,"servicesWindow"); CheckEnglish(dialog,service); dialog.Close(); }
            window.Hide();
            Check(model.Rows[0].Label==modelLabel,"Stored provider metric identity is unchanged");
            foreach(var source in new[] {"1분마다","자동 조회 2분 · 수동 기록 1개","초기화까지 4일 7시간","3시간 18분 후 초기화","36,614 / 39,121 크레딧","주간 86%","현재 한도에서 27.5% 사용","2일","7시간","30분","최근 기록 · 200개 중 최근 150개 표시","Codex · 주간 잔량이 10%입니다.","Higgsfield API · API 잔액 잔량이 1.00 USD입니다. 수동 입력값 기준입니다.","주간의 예정된 초기화 시각이 지났습니다. 새로고침하여 실제 잔량을 확인하세요.","다시 보내기 · 35초","이번 달 비용 $11.75 · 설정 예산 $50.00","사용 $11.75 / 예산 $50.00","지원 서비스 등록 무제한","Codex 접기 / 펼치기"}) Check(!Hangul.IsMatch(L.Text(source)),"Untranslated dynamic text: "+source+" => "+L.Text(source));
            foreach(var id in new[]{"eleven","openai","codex"}) foreach(var issue in new[]{ConnectionDiagnostics.InvalidKeyFormat(id),ConnectionDiagnostics.Timeout(id),ConnectionDiagnostics.Network(id),ConnectionDiagnostics.Malformed(id),ConnectionDiagnostics.Http(id,429,"{}"),ConnectionDiagnostics.Http(id,500,"{}"),ConnectionDiagnostics.Http(id,418,"{}")}) { Check(!Hangul.IsMatch(L.Text(issue.Title)),"Untranslated diagnostic title: "+L.Text(issue.Title)); Check(!Hangul.IsMatch(L.Text(issue.Message)),"Untranslated diagnostic: "+L.Text(issue.Message)); }
            Call(window,"ApplyLanguage","ko"); Check(label.Text=="연결 확인" && (string)button.Content=="저장" && window.Title=="Nephylo — 미리보기","Switching back restores source text immediately");
            Check(textbox.Text=="사용자 이름 user@example.com","Unsaved user input survives language round trip");
            Console.WriteLine("Localization tests passed: "+count+" assertions; isolated settings, no live API, accounts, startup or settings changes."); return 0;
        } catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if(window!=null) {Field<DispatcherTimer>(window,"timer").Stop(); window.Close(); Field<Forms.NotifyIcon>(window,"tray").Dispose();} SettingsStore.DataDirectoryOverride=null; SettingsStore.StartupRegistrySubKeyOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null; if(Path.GetFullPath(isolated).StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && Directory.Exists(isolated)) Directory.Delete(isolated,true); L.Language="ko"; }
    }
}
