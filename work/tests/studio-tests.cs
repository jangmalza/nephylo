using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageWidget;

internal static class StudioTests
{
    static int count;static JavaScriptSerializer json=new JavaScriptSerializer();
    static readonly Queue<string> ioFailures=new Queue<string>();
    static void RecordIoFailure(object sender,System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        var trace=e.Exception.StackTrace??"";
        if(!(e.Exception is IOException)&&!(e.Exception is UnauthorizedAccessException)&&!trace.Contains("StudioWindow.SaveCurrent")&&!trace.Contains("StudioStore")&&!trace.Contains("StudioWindow.SubmitHandoff"))return;
        lock(ioFailures){ioFailures.Enqueue(e.Exception.GetType().Name+" 0x"+e.Exception.HResult.ToString("X8")+": "+e.Exception.Message+"\n"+e.Exception.StackTrace);while(ioFailures.Count>12)ioFailures.Dequeue();}
    }
    static void Check(bool ok,string label){count++;if(!ok)throw new Exception(label);}
    static void Emit(object obj){Console.WriteLine(json.Serialize(obj));Console.Out.Flush();}
    static int Server()
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=new UTF8Encoding(false);string line;bool pendingApproval=false;string currentThread="thread1";int threadCounter=0;
        while((line=Console.ReadLine())!=null){
            var m=json.Deserialize<Dictionary<string,object>>(line);object id;m.TryGetValue("id",out id);var method=StudioCodexClient.Str(m,"method");var p=StudioCodexClient.Obj(m,"params");
            if(method==null){if(pendingApproval){var result=StudioCodexClient.Obj(m,"result");Check(StudioCodexClient.Str(result,"decision")=="decline","approval rejected");Emit(new{method="turn/completed",@params=new{threadId=currentThread,turn=new{id="turn1",status="completed"}}});pendingApproval=false;}continue;}
            if(method=="initialized")continue;
            if(method=="fixture/error"){
                var kind=StudioCodexClient.Str(p,"kind");
                Emit(new{id=id,error=new{code=kind=="compat"?-32601:-32000,message=kind=="auth"?"Unauthorized SECRET_AUTH_URL":kind=="quota"?"Rate limit exceeded SECRET_PROMPT":kind=="model"?"model_not_found SECRET_ACCOUNT":kind=="network"?"Network connection failed SECRET_URL":"SECRET_UNKNOWN",data=new{detail="PRIVATE_TOKEN"}}});continue;
            }
            object resultObj=new{};
            if(method=="account/read")resultObj=new{account=new{type="chatgpt",email="ade-fixture@example.invalid"}};
            if(method=="account/rateLimits/read")resultObj=new{rateLimits=new{limitId="codex",primary=new{usedPercent=27,windowDurationMins=300,resetsAt=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()},secondary=new{usedPercent=70,windowDurationMins=10080,resetsAt=DateTimeOffset.UtcNow.AddDays(4).ToUnixTimeSeconds()}}};
            if(method=="model/list")resultObj=new{data=new[]{new{model="test-model",displayName="Fixture model"}},nextCursor=(string)null};
            if(method=="thread/start"||method=="thread/resume"){
                if(method=="thread/start")currentThread="thread"+(++threadCounter);else{currentThread=StudioCodexClient.Str(p,"threadId");int existing;if(Int32.TryParse(currentThread.Substring(6),out existing))threadCounter=Math.Max(threadCounter,existing);}
                Check(StudioCodexClient.Str(p,"sandbox")=="read-only","sandbox preserved");Check(StudioCodexClient.Str(p,"approvalPolicy")=="untrusted","approval policy preserved");resultObj=new{thread=new{id=currentThread},sandbox=new{type=StudioCodexClient.Str(p,"model")=="unsafe"?"dangerFullAccess":"readOnly"},approvalPolicy="untrusted",approvalsReviewer="user"};
            }
            if(method=="turn/start"){
                currentThread=StudioCodexClient.Str(p,"threadId");
                Check(StudioCodexClient.Str(StudioCodexClient.Obj(p,"sandboxPolicy"),"type")=="readOnly","turn sandbox");
                var prompt=StudioCodexClient.Str(StudioCodexClient.Items(p,"input").First(),"text");
                if(prompt=="disconnect")return 0;
                Emit(new{id=id,result=new{turn=new{id="turn1",status="inProgress"}}});
                Emit(new{method="turn/started",@params=new{threadId=currentThread,turn=new{id="turn1"}}});
                if(prompt=="wait")continue;
                Emit(new{method="item/agentMessage/delta",@params=new{threadId="other",turnId="turn1",itemId="bad",delta="WRONG THREAD"}});
                Emit(new{method="item/agentMessage/delta",@params=new{threadId=currentThread,turnId="old-turn",itemId="bad",delta="WRONG TURN"}});
                Emit(new{method="item/agentMessage/delta",@params=new{threadId=currentThread,turnId="turn1",itemId="answer",delta="한글 "}});
                Emit(new{method="item/agentMessage/delta",@params=new{threadId=currentThread,turnId="turn1",itemId="answer",delta="응답"}});
                Emit(new{method="item/completed",@params=new{threadId=currentThread,turnId="turn1",item=new{id="answer",type="agentMessage",text="한글 응답"}}});
                if(prompt=="approve"){pendingApproval=true;Emit(new{id="approval1",method="item/fileChange/requestApproval",@params=new{threadId=currentThread,turnId="turn1"}});continue;}
                Emit(new{method="turn/completed",@params=new{threadId=currentThread,turn=new{id="turn1",status=prompt=="fail"?"failed":"completed",error=prompt=="fail"?new{message="Usage limit exceeded SECRET_PROMPT",codexErrorInfo="usageLimitExceeded"}:null}}});continue;
            }
            if(method=="turn/interrupt"){Emit(new{id=id,result=new{}});Emit(new{method="turn/completed",@params=new{threadId=currentThread,turn=new{id="turn1",status="interrupted"}}});continue;}
            Emit(new{id=id,result=resultObj});
        }return 0;
    }
    static async Task Protocol(string root)
    {
        var exe=Assembly.GetExecutingAssembly().Location;
        using(var client=new StudioCodexClient()){
            await client.ConnectAsync(exe,Path.Combine(root,"codex"));Check(client.Connected,"connected");
            Check(StudioCodexClient.Obj(await client.AccountAsync(),"account")!=null,"account verified");Check((await client.ModelsAsync()).Single().Id=="test-model","model discovered");
            string thread=await client.ThreadAsync(null,root,"test-model");Check(thread=="thread1","thread created");Check(await client.ThreadAsync(thread,root,"test-model")==thread,"thread resumed");
            bool unsafeBlocked=false;try{await client.ThreadAsync(null,root,"unsafe");}catch(StudioFailure e){unsafeBlocked=e.Kind==StudioFailureKind.Permission;}Check(unsafeBlocked,"server permission downgrade rejected before inference");
            foreach(var entry in new[]{new{key="auth",kind=StudioFailureKind.Authentication},new{key="quota",kind=StudioFailureKind.Quota},new{key="model",kind=StudioFailureKind.Model},new{key="network",kind=StudioFailureKind.Network},new{key="compat",kind=StudioFailureKind.Compatibility},new{key="unknown",kind=StudioFailureKind.Unknown}}){
                bool matched=false;try{await client.Request("fixture/error",new{kind=entry.key});}catch(StudioFailure e){matched=e.Kind==entry.kind;Check(!e.ToString().Contains("SECRET")&&!e.ToString().Contains("PRIVATE"),"upstream private text not retained");}Check(matched,"RPC classification: "+entry.key);
            }
            var chunks=new List<string>();client.Message+=(id,text,final)=>{lock(chunks)chunks.Add(text);};
            Check(await client.RunAsync(thread,root,"test-model","test")=="completed","completed status");Check(String.Join("",chunks).Contains("한글 응답"),"UTF8 delta/final");Check(!String.Join("",chunks).Contains("WRONG"),"foreign thread/turn ignored");
            Check(await client.RunAsync(thread,root,"test-model","approve")=="completed","approval decline did not hang");
            bool quota=false;try{await client.RunAsync(thread,root,"test-model","fail");}catch(StudioFailure e){quota=e.Kind==StudioFailureKind.Quota;}Check(quota,"turn failure retains quota category");
            var running=client.RunAsync(thread,root,"test-model","wait");await Task.Delay(100);await client.InterruptAsync();Check(await running=="interrupted","cancel ends run");
            running=client.RunAsync(thread,root,"test-model","wait");await client.InterruptAsync();Check(await running=="interrupted","cancel before start response");
            bool disconnected=false;try{await client.RunAsync(thread,root,"test-model","disconnect");}catch(IOException){disconnected=true;}Check(disconnected,"exit unblocks active turn");
        }
        using(var c=new StudioCodexClient()){await c.ConnectAsync(exe,Path.Combine(root,"codex2"));var r=c.RunAsync("thread1",root,"test-model","wait");await Task.Delay(50);c.Dispose();bool ended=false;try{await r;}catch{ended=true;}Check(ended,"dispose unblocks pending turn");}
    }
    static void Storage(string root)
    {
        var store=new StudioStore(Path.Combine(root,"history"));var s=new StudioSession{Title="private title",Project=root,ThreadId="thread1",Model="test-model",Draft="unsent draft"};
        s.Messages.Add(new StudioMessage{Role="user",Text="SECRET_PROMPT_123",State="completed"});s.Messages.Add(new StudioMessage{Role="assistant",Text="partial",State="running"});store.Save(s);
        var file=Path.Combine(root,"history",s.Id+".dat");Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("SECRET_PROMPT"),"stored history encrypted");int bad;var loaded=store.Load(out bad).Single();Check(loaded.ThreadId=="thread1"&&loaded.Project==root,"thread/project restored");Check(loaded.Messages[1].State=="unconfirmed","crash recovery is not completed");
        Check(loaded.Draft=="unsent draft","unsent draft restored");loaded.Messages[1].Text="finished";loaded.Messages[1].State="completed";store.Save(loaded);Check(store.Load(out bad).Single().Messages[1].Text=="finished","atomic replacement");
        loaded.Id="../escape";bool blocked=false;try{store.Save(loaded);}catch(InvalidDataException){blocked=true;}Check(blocked,"path traversal blocked");
        File.WriteAllText(file,"corrupt");Check(store.Load(out bad).Count==0&&bad==1,"corruption reported");Check(File.ReadAllText(file)=="corrupt","corrupt original retained");
    }
    static async Task Probe(string root)
    {
        using(var c=new StudioCodexClient()){
            await c.ConnectAsync(CodexService.FindExecutable(""),Path.Combine(root,"official-codex-home"));
            var account=await c.AccountAsync();var models=await c.ModelsAsync();
            Console.WriteLine(json.Serialize(new{officialServerConnected=c.Connected,accountPresent=StudioCodexClient.Obj(account,"account")!=null,modelCount=models.Count,inferenceRequested=false,isolatedProfile=true}));
        }
    }
    static void Ui(string root)
    {
        SettingsStore.DataDirectoryOverride=Path.Combine(root,"profile");
        var owner=new WidgetWindow(true);
        try{
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            foreach(var theme in new[]{"dark","light"}){
                typeof(WidgetWindow).GetMethod("ApplyRequestedTheme",flags,null,new[]{typeof(string)},null).Invoke(owner,new object[]{theme});
                var resources=(ResourceDictionary)typeof(WidgetWindow).GetMethod("MakeResources",BindingFlags.Static|BindingFlags.NonPublic,null,Type.EmptyTypes,null).Invoke(null,null);
                using(var holder=new UiLifetime(new StudioWindow("",true,resources,owner.Background,owner.Foreground,owner.FontFamily))){
                    var w=holder.Window;var wf=BindingFlags.Instance|BindingFlags.NonPublic;
                    var send=(Button)typeof(StudioWindow).GetField("send",wf).GetValue(w);Check(!send.IsEnabled,"cannot send without login/project/model");
                    var list=(ListBox)typeof(StudioWindow).GetField("list",wf).GetValue(w);var session=(StudioSession)list.SelectedItem;
                    var input=(TextBox)typeof(StudioWindow).GetField("input",wf).GetValue(w);input.Text="draft A";typeof(StudioWindow).GetMethod("NewSession",wf).Invoke(w,null);Check(input.Text=="","new session cannot inherit another draft");input.Text="draft B";list.SelectedItem=session;Check(input.Text=="draft A","switch restores only the selected draft");input.Text="";
                    session.Project=@"C:\Projects\example";session.Title="프로젝트 구조 살펴보기";session.Model="Codex";
                    session.Messages.Add(new StudioMessage{Role="user",Text="프로젝트 구조를 설명해 주세요. 파일은 수정하지 마세요.",State="completed"});
                    session.Messages.Add(new StudioMessage{Role="assistant",Text="## 프로젝트 살펴보기\n\n이 화면은 예시 데이터입니다. 실제 계정 응답이 아닙니다.\n\n- 프로젝트의 구조와 역할을 정리합니다.\n- 파일은 변경하지 않습니다.\n\n```csharp\n// 예시 코드\nvar workspace = \"Nephylo ADE\";\n```",State="completed",Model="Codex"});
                    typeof(StudioWindow).GetMethod("RefreshList",wf|BindingFlags.DeclaredOnly).Invoke(w,null);
                    typeof(StudioWindow).GetMethod("Render",wf|BindingFlags.DeclaredOnly).Invoke(w,null);
                    var view=(FrameworkElement)w.Content;view.Width=960;view.Height=660;view.Measure(new Size(1000,700));view.Arrange(new Rect(0,0,1000,700));view.UpdateLayout();
                    Check(view.DesiredSize.Width<=1000,"layout width");
                    var bitmap=new RenderTargetBitmap(1000,700,96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(var dc=background.RenderOpen())dc.DrawRectangle(w.Background,null,new Rect(0,0,1000,700));bitmap.Render(background);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var f=File.Create(Path.Combine(root,"studio-"+theme+".png")))png.Save(f);
                    list.IsEnabled=false;view.UpdateLayout();var disabledBitmap=new RenderTargetBitmap(1000,700,96,96,PixelFormats.Pbgra32);disabledBitmap.Render(background);disabledBitmap.Render(view);
                    var listPoint=list.TransformToAncestor(view).Transform(new Point(12,list.ActualHeight-12));var pixel=new byte[4];disabledBitmap.CopyPixels(new Int32Rect((int)listPoint.X,(int)listPoint.Y,1,1),pixel,4,0);Check(theme=="dark"?pixel[0]<100&&pixel[1]<100&&pixel[2]<100:pixel[0]>180&&pixel[1]>180&&pixel[2]>180,"disabled conversation list retains "+theme+" palette");
                    var disabledPng=new PngBitmapEncoder();disabledPng.Frames.Add(BitmapFrame.Create(disabledBitmap));using(var f=File.Create(Path.Combine(root,"studio-disabled-"+theme+".png")))disabledPng.Save(f);
                }
            }
            Check(!Directory.Exists(SettingsStore.DataDirectory),"demo does not persist drafts or touch user data");
        }finally{owner.Close();SettingsStore.DataDirectoryOverride=null;}
    }
    sealed class UiLifetime:IDisposable { internal StudioWindow Window;internal UiLifetime(StudioWindow w){Window=w;}public void Dispose(){Window.Close();} }
    static void AdeNavigation(string root)
    {
        SettingsStore.DataDirectoryOverride=Path.Combine(root,"ade-navigation");
        var flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;var owner=new WidgetWindow(true);StudioWindow w=null;
        Func<string,object> get=n=>typeof(StudioWindow).GetField(n,flags).GetValue(w);
        Action<string,object> set=(n,v)=>typeof(StudioWindow).GetField(n,flags).SetValue(w,v);
        Func<string,object[],object> call=(n,a)=>typeof(StudioWindow).GetMethod(n,flags).Invoke(w,a);
        try{
            var resources=(ResourceDictionary)typeof(WidgetWindow).GetMethod("MakeResources",BindingFlags.Static|BindingFlags.NonPublic,null,Type.EmptyTypes,null).Invoke(null,null);
            w=new StudioWindow("",false,resources,owner.Background,owner.Foreground,owner.FontFamily);
            var first=(StudioSession)get("selected");first.Title="Custom title";first.TitleIsCustom=true;set("dirty",true);call("SaveCurrent",new object[0]);
            int unreadable;Check(((StudioStore)get("store")).Load(out unreadable).Any(x=>x.Id==first.Id&&x.TitleIsCustom),"renaming an empty unassigned conversation is persisted");first.Project=root;
            var input=(TextBox)get("input");input.Text="draft one";Check(first.Title=="Custom title","custom title survives draft edits");call("SaveCurrent",new object[0]);
            call("NewSession",new object[0]);var second=(StudioSession)get("selected");Check(second.Project==root,"new conversation inherits active project");input.Text="searchable second";call("SaveCurrent",new object[0]);
            var list=(ListBox)get("list");var search=(TextBox)get("search");search.Text="Custom";Check(list.Items.Count==1&&list.Items[0]==first,"search filters conversations by title");Check(input.Text=="searchable second","filtering does not discard active draft");
            search.Text="no match";Check(list.Items.Count==0,"no-match search is empty");search.Text="";list.SelectedItem=first;Check(input.Text=="draft one","switch restores selected draft");
            var state=((StudioStore)get("store")).LoadWorkspace();Check(state.SelectedSessionId==first.Id,"selection preference persisted separately");
            call("ToggleArchive",new object[0]);Check(first.Archived&&((CheckBox)get("archived")).IsChecked==true,"archive keeps conversation discoverable; selected="+((StudioSession)get("selected")).Id+" expected="+first.Id+" blocked="+get("storageBlocked")+" status="+((TextBlock)get("status")).Text);Check(!input.IsEnabled&&!((Button)get("send")).IsEnabled,"archive prevents accidental sends and draft edits");
            call("ToggleArchive",new object[0]);Check(!first.Archived&&input.Text=="draft one","restore keeps conversation and draft");
            using(var locked=File.Open(Path.Combine(SettingsStore.DataDirectory,"studio",first.Id+".dat"),FileMode.Open,FileAccess.Read,FileShare.Read)){
                call("ToggleArchive",new object[0]);Check(!first.Archived&&input.IsEnabled&&(string)((Button)get("archive")).Content=="보관","archive save failure restores previous edit state");
                Check((bool)get("storageBlocked")&&((WrapPanel)get("saveRecovery")).Visibility==Visibility.Visible,"archive failure keeps save recovery visible");
            }
            call("SaveCurrent",new object[0]);
            w.Width=720;call("ApplySidebar",new object[0]);Check(((Border)get("sidebar")).Visibility==Visibility.Collapsed,"narrow windows collapse sidebar");
            call("ToggleSidebar",new object[0]);Check(Grid.GetColumnSpan((Border)get("sidebar"))==((Grid)get("workspaceRoot")).ColumnDefinitions.Count&&((ColumnDefinition)get("sidebarColumn")).Width.Value==0,"narrow sidebar spans the workspace without taking composer width");
            call("ToggleSidebar",new object[0]);
            w.Width=1180;call("ApplySidebar",new object[0]);call("ToggleSidebar",new object[0]);Check(((StudioStore)get("store")).LoadWorkspace().SidebarCollapsed,"manual sidebar preference saved");
            w.Close();w=new StudioWindow("",false,resources,owner.Background,owner.Foreground,owner.FontFamily);
            Check(((StudioSession)get("selected")).Id==first.Id&&((TextBox)get("input")).Text=="draft one","restart restores last conversation and draft");
            Check((bool)get("sidebarCollapsed"),"restart restores sidebar preference");
        }finally{if(w!=null)w.Close();owner.Close();SettingsStore.DataDirectoryOverride=null;}
    }
    static void UiProtocol(string root)
    {
        SettingsStore.DataDirectoryOverride=Path.Combine(root,"ade-protocol");var owner=new WidgetWindow(true);StudioWindow w=null;
        var flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;var previous=System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Func<string,object> get=n=>typeof(StudioWindow).GetField(n,flags).GetValue(w);
        Func<string,object[],object> call=(n,a)=>typeof(StudioWindow).GetMethod(n,flags).Invoke(w,a);
        try{
            var resources=(ResourceDictionary)typeof(WidgetWindow).GetMethod("MakeResources",BindingFlags.Static|BindingFlags.NonPublic,null,Type.EmptyTypes,null).Invoke(null,null);
            w=new StudioWindow(Assembly.GetExecutingAssembly().Location,false,resources,owner.Background,owner.Foreground,owner.FontFamily);
            Pump((Task)call("Connect",new object[0]));Check((bool)get("authenticated"),"UI connects to isolated synthetic account");
            var session=(StudioSession)get("selected");session.Project=root;((ComboBox)get("models")).SelectedIndex=0;((TextBox)get("input")).Text="test";
            call("SaveCurrent",new object[0]); // Simulate the draft autosave before sending.
            Pump((Task)call("Send",new object[0]));Check(session.Messages.Count==2&&session.Messages[1].Text=="한글 응답"&&session.Messages[1].State=="completed","UI consumes streamed RPC result");
            int bad;var saved=((StudioStore)get("store")).Load(out bad).Single();Check(saved.Messages.Count==2&&saved.Draft==""&&saved.ThreadId=="thread1","pre-saved draft still persists sent messages and completion");
            w.Close();w=new StudioWindow(Assembly.GetExecutingAssembly().Location,false,resources,owner.Background,owner.Foreground,owner.FontFamily);Pump((Task)call("Connect",new object[0]));
            ((TextBox)get("input")).Text="test";Pump((Task)call("Send",new object[0]));session=(StudioSession)get("selected");Check(session.ThreadId=="thread1"&&session.Messages.Count==4,"reopened conversation resumes same synthetic thread");
        }finally{if(w!=null)w.Close();owner.Close();System.Threading.SynchronizationContext.SetSynchronizationContext(previous);SettingsStore.DataDirectoryOverride=null;}
    }
    static void Pump(Task task)
    {
        var frame=new DispatcherFrame();var started=DateTime.UtcNow;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};
        timer.Tick+=(s,e)=>{if(task.IsCompleted||(DateTime.UtcNow-started).TotalSeconds>35)frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();
        if(!task.IsCompleted)throw new TimeoutException("UI fixture did not complete");task.GetAwaiter().GetResult();
    }
    static void TaskFlow(string root)
    {
        SettingsStore.DataDirectoryOverride=Path.Combine(root,"ade-task-flow");var owner=new WidgetWindow(true);StudioWindow w=null;
        var flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;var previous=System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Func<string,object> get=n=>typeof(StudioWindow).GetField(n,flags).GetValue(w);
        Func<string,object[],object> call=(n,a)=>typeof(StudioWindow).GetMethod(n,flags).Invoke(w,a);
        try{
            var resources=(ResourceDictionary)typeof(WidgetWindow).GetMethod("MakeResources",BindingFlags.Static|BindingFlags.NonPublic,null,Type.EmptyTypes,null).Invoke(null,null);
            w=new StudioWindow(Assembly.GetExecutingAssembly().Location,false,resources,owner.Background,owner.Foreground,owner.FontFamily);Pump((Task)call("Connect",new object[0]));
            var session=(StudioSession)get("selected");session.Project=root;var models=(ComboBox)get("models");models.SelectedIndex=0;var input=(TextBox)get("input");input.Text="initial question";Pump((Task)call("Send",new object[0]));
            var source=StudioSessions.Active(session);Check(source.ThreadId=="thread1"&&source.AccountKey!=null,"first task binds execution to ADE account");Check(!models.IsEnabled,"started task model selection is pinned");
            Pump((Task)call("RefreshQuota",new object[]{get("client")}));Check(((TextBlock)get("quotaSummary")).Text.Contains("73%"),"ADE displays official synthetic allowance");
            CaptureUi(w,root,"ade-task-allowance",1100,760);
            var unknown=((StudioCodexClient)get("client")).AccountIdentity;
            var expired=StudioQuotaParser.Parse(json.Deserialize<Dictionary<string,object>>("{\"rateLimits\":{\"primary\":{\"usedPercent\":100,\"resetsAt\":1}}}"),unknown,((StudioCodexClient)get("client")).ConnectionId,DateTime.UtcNow);
            typeof(StudioWindow).GetField("quotaSnapshot",flags).SetValue(w,expired);call("UpdateQuotaDisplay",new object[0]);Check(((TextBlock)get("quotaSummary")).Text.Contains("확인"),"reset crossing never auto-restores quota");
            var restricted=StudioQuotaParser.Parse(json.Deserialize<Dictionary<string,object>>("{\"ordinaryUsageAllowed\":false}"),unknown,((StudioCodexClient)get("client")).ConnectionId,DateTime.UtcNow);typeof(StudioWindow).GetField("quotaSnapshot",flags).SetValue(w,restricted);call("UpdateQuotaDisplay",new object[0]);Check(((TextBlock)get("quotaSummary")).Text.Contains("제한"),"provider restriction remains visible when percentages are unavailable");
            input.Text="original unsent draft";call("SaveCurrent",new object[0]);call("OpenHandoff",new object[0]);
            var editor=(TextBox)get("handoffEditor");Check(!editor.Text.Contains("initial question")&&!editor.Text.Contains("original unsent draft"),"context excludes conversation and current draft by default");
            Check(!((Button)get("handoffSend")).IsEnabled,"empty context template cannot trigger inference");
            var excerpts=(ListBox)get("handoffMessages");excerpts.SelectedIndex=0;call("AddHandoffExcerpts",new object[0]);Check(editor.Text.Contains("initial question"),"only explicit selected message appended");
            DependencyObject excerptParent=excerpts;while(excerptParent!=null&&!(excerptParent is Expander))excerptParent=LogicalTreeHelper.GetParent(excerptParent);((Expander)excerptParent).IsExpanded=true;
            CaptureUi(w,root,"ade-handoff-review",1100,760);CaptureUi(w,root,"ade-handoff-narrow",720,500);
            var excerptImage=new RenderTargetBitmap(Math.Max(1,(int)excerpts.ActualWidth),Math.Max(1,(int)excerpts.ActualHeight),96,96,PixelFormats.Pbgra32);excerptImage.Render(excerpts);var excerptPixel=new byte[4];excerptImage.CopyPixels(new Int32Rect((int)excerpts.ActualWidth-5,(int)excerpts.ActualHeight-5,1,1),excerptPixel,4,0);Check(excerptPixel[0]<100&&excerptPixel[1]<100&&excerptPixel[2]<100,"expanded context-message list retains dark palette");
            var handoffSend=(Button)get("handoffSend");var hp=handoffSend.TransformToAncestor((FrameworkElement)w.Content).Transform(new Point());Check(hp.Y>=0&&hp.Y+handoffSend.ActualHeight<=500,"narrow handoff submit remains visible");
            editor.Text="  reviewed context\n ";
            var file=Path.Combine(SettingsStore.DataDirectory,"studio",session.Id+".dat");int messagesBefore=session.Messages.Count;int segmentsBefore=session.ProviderSessions.Count;
            using(var locked=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.Read)){
                Pump((Task)call("SubmitHandoff",new object[0]));Check(session.Messages.Count==messagesBefore&&session.ProviderSessions.Count==segmentsBefore&&session.ActiveExecutionId==source.Id,"handoff disk failure performs no execution or switch");
                Check(((TextBox)get("input")).Text=="original unsent draft"&&session.Handoffs.Last().State=="draft","failed preparation preserves original draft and editable transfer");
            }
            call("SaveCurrent",new object[0]);Pump((Task)call("SubmitHandoff",new object[0]));
            var target=StudioSessions.Active(session);Check(target.Id!=source.Id&&target.ThreadId=="thread2"&&source.ThreadId=="thread1","new target retains original execution and remote thread; target="+target.Id+" source="+source.Id+" blocked="+get("storageBlocked")+" status="+((TextBlock)get("status")).Text+" notice="+((TextBlock)get("handoffNotice")).Text);
            Check(session.Handoffs.Last().State=="completed"&&session.Handoffs.Last().TargetExecutionId==target.Id,"successful reviewed context recorded");
            Check(session.Messages[session.Messages.Count-2].Text=="  reviewed context\n ","exact reviewed payload preserves whitespace");
            Check(input.Text=="original unsent draft"&&session.Draft==input.Text,"handoff does not consume unsent question draft");
            messagesBefore=session.Messages.Count;Pump((Task)call("SubmitHandoff",new object[0]));Check(session.Messages.Count==messagesBefore,"second submit does not duplicate execution");
            call("OpenHandoff",new object[0]);var destinations=(ComboBox)get("handoffDestination");var returnChoice=destinations.Items.Cast<object>().First(x=>(string)x.GetType().GetField("ExecutionId",flags).GetValue(x)==source.Id);destinations.SelectedItem=returnChoice;editor.Text="return with new context";Pump((Task)call("SubmitHandoff",new object[0]));Check(session.ActiveExecutionId==source.Id&&session.ThreadId=="thread1"&&session.ProviderSessions.Count==2,"explicit return resumes original execution without replacing it");
            // A prepared record saved before target creation is recovered as an editable draft.
            call("OpenHandoff",new object[0]);editor.Text="prepared but not sent";var frozen=(StudioHandoffRecord)call("FreezeHandoff",new object[0]);typeof(StudioWindow).GetField("dirty",flags).SetValue(w,true);call("SaveCurrent",new object[0]);w.Close();
            w=new StudioWindow(Assembly.GetExecutingAssembly().Location,false,resources,owner.Background,owner.Foreground,owner.FontFamily);session=(StudioSession)get("selected");Pump((Task)call("Connect",new object[0]));Check(!(bool)((Button)get("send")).IsEnabled,"restart does not send prepared context automatically");call("OpenHandoff",new object[0]);Check(((TextBox)get("handoffEditor")).Text=="prepared but not sent"&&session.Handoffs.Single(h=>h.Id==frozen.Id).State=="draft","prepared source-only record recovers exact editor content");
            // Copy-only Claude is labelled and cannot run through the Codex adapter.
            ((ComboBox)get("handoffProvider")).SelectedIndex=1;Check(!((Button)get("handoffSend")).IsEnabled&&((Button)get("handoffCopy")).IsEnabled,"Claude remains copy-only without an execution adapter");
            call("SaveCurrent",new object[0]);w.Close();w=new StudioWindow(Assembly.GetExecutingAssembly().Location,false,resources,owner.Background,owner.Foreground,owner.FontFamily);session=(StudioSession)get("selected");Pump((Task)call("Connect",new object[0]));
            input=(TextBox)get("input");input.Text="disconnect";Pump((Task)call("Send",new object[0]));Check((bool)call("DeliveryUncertain",new object[0])&&!((Button)get("handoffOpen")).IsEnabled,"unknown delivery blocks transfer and resend");Check(session.Messages.Last().State=="unconfirmed","network loss has unconfirmed outcome label");
            CaptureUi(w,root,"ade-outcome-unconfirmed",720,500);
            w.Close();w=new StudioWindow(Assembly.GetExecutingAssembly().Location,false,resources,owner.Background,owner.Foreground,owner.FontFamily);session=(StudioSession)get("selected");messagesBefore=session.Messages.Count;Pump((Task)call("Connect",new object[0]));Check((bool)call("DeliveryUncertain",new object[0])&&!((Button)get("send")).IsEnabled,"unknown delivery survives restart and reconnect");
            call("AcknowledgeDelivery",new object[0]);Check(session.Messages.Count==messagesBefore&&!(bool)call("DeliveryUncertain",new object[0]),"manual review acknowledges without replaying request");
            var execution=StudioSessions.Active(session);execution.AccountKey=null;((Dictionary<string,string>)get("executionConfirmations")).Clear();call("Enable",new object[0]);Check((bool)call("NeedsAccountConfirmation",new object[0])&&!((Button)get("send")).IsEnabled,"legacy unbound execution requires explicit account confirmation");call("ConfirmExecution",new object[0]);Check(execution.AccountKey==((StudioCodexClient)get("client")).AccountIdentity.Fingerprint&&!(bool)call("NeedsAccountConfirmation",new object[0]),"explicit current-account choice binds legacy execution");
            execution.AccountKey="different-account";typeof(StudioWindow).GetField("dirty",flags).SetValue(w,true);call("SaveCurrent",new object[0]);call("Enable",new object[0]);Check(!(bool)((Button)get("send")).IsEnabled&&(bool)call("AccountMismatch",new object[0]),"different execution account cannot resume silently");
        }finally{if(w!=null)w.Close();owner.Close();System.Threading.SynchronizationContext.SetSynchronizationContext(previous);SettingsStore.DataDirectoryOverride=null;}
    }
    static void CaptureUi(StudioWindow w,string root,string name,int width,int height)
    {
        w.Width=width;w.Height=height;typeof(StudioWindow).GetMethod("ApplySidebar",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(w,null);
        var view=(FrameworkElement)w.Content;view.Width=width;view.Height=height;view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);var bg=new DrawingVisual();using(var dc=bg.RenderOpen())dc.DrawRectangle(w.Background,null,new Rect(0,0,width,height));bitmap.Render(bg);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var f=File.Create(Path.Combine(root,name+".png")))png.Save(f);
    }
    static void Responsive(string root)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;SettingsStore.DataDirectoryOverride=Path.Combine(root,"responsive");var owner=new WidgetWindow(true);
        try{
            foreach(var english in new[]{false,true}){
                L.Language=english?"en":"ko";
                var resources=(ResourceDictionary)typeof(WidgetWindow).GetMethod("MakeResources",BindingFlags.Static|BindingFlags.NonPublic,null,Type.EmptyTypes,null).Invoke(null,null);
                using(var lifetime=new UiLifetime(new StudioWindow("",true,resources,owner.Background,owner.Foreground,owner.FontFamily))){
                    var w=lifetime.Window;w.Width=720;w.Height=500;typeof(StudioWindow).GetMethod("ApplySidebar",flags).Invoke(w,null);
                    var recovery=(WrapPanel)typeof(StudioWindow).GetField("saveRecovery",flags).GetValue(w);recovery.Visibility=Visibility.Visible;
                    ((TextBlock)typeof(StudioWindow).GetField("status",flags).GetValue(w)).Text=StudioFailure.Guidance(StudioFailureKind.Storage,english);
                    var models=(ComboBox)typeof(StudioWindow).GetField("models",flags).GetValue(w);models.Items.Add(new StudioModel{Id="long-model-id",Label="A long provider model display name"});models.SelectedIndex=0;
                    ((TextBlock)typeof(StudioWindow).GetField("status",flags).GetValue(w)).Text=StudioFailure.Guidance(StudioFailureKind.Storage,english);
                    foreach(var expanded in new[]{false,true}){
                        typeof(StudioWindow).GetField("narrowSidebarOpen",flags).SetValue(w,expanded);typeof(StudioWindow).GetMethod("ApplySidebar",flags).Invoke(w,null);
                        var view=(FrameworkElement)w.Content;view.Width=720;view.Height=500;view.Measure(new Size(720,500));view.Arrange(new Rect(0,0,720,500));view.UpdateLayout();
                        foreach(var b in recovery.Children.Cast<Button>().Concat(new[]{(Button)typeof(StudioWindow).GetField("send",flags).GetValue(w)})){
                            var p=b.TransformToAncestor(view).Transform(new Point(0,0));Check(p.X>=0&&p.Y>=0&&p.X+b.ActualWidth<=721&&p.Y+b.ActualHeight<=501,"narrow recovery and send stay within viewport");
                        }
                        var bitmap=new RenderTargetBitmap(720,500,96,96,PixelFormats.Pbgra32);var bg=new DrawingVisual();using(var dc=bg.RenderOpen())dc.DrawRectangle(w.Background,null,new Rect(0,0,720,500));bitmap.Render(bg);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var f=File.Create(Path.Combine(root,"ade-narrow-"+(english?"en":"ko")+(expanded?"-drawer":"")+".png")))png.Save(f);
                    }
                }
            }
        }finally{L.Language="ko";owner.Close();SettingsStore.DataDirectoryOverride=null;}
    }
    static void Fixes(string root)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
        SettingsStore.DataDirectoryOverride=Path.Combine(root,"fix-profile");var owner=new WidgetWindow(true);StudioWindow w=null;
        Func<string,object> get=n=>typeof(StudioWindow).GetField(n,flags).GetValue(w);
        Action<string,object> set=(n,v)=>typeof(StudioWindow).GetField(n,flags).SetValue(w,v);
        Func<string,object[],object> call=(n,a)=>typeof(StudioWindow).GetMethod(n,flags).Invoke(w,a);
        try{
            var resources=(ResourceDictionary)typeof(WidgetWindow).GetMethod("MakeResources",BindingFlags.Static|BindingFlags.NonPublic,null,Type.EmptyTypes,null).Invoke(null,null);
            w=new StudioWindow("",false,resources,owner.Background,owner.Foreground,owner.FontFamily);
            var session=(StudioSession)get("selected");session.Project=root;session.Model="retired-model";set("authenticated",true);
            set("client",new StudioCodexClient()); // Isolated presentation fixture; no process or request is started.
            var a=new StudioModel{Id="a",Label="Model A"};var b=new StudioModel{Id="b",Label="Model B"};
            call("ReplaceModels",new object[]{new[]{a,b}});var models=(ComboBox)get("models");var send=(Button)get("send");
            Check(models.SelectedItem==null&&!send.IsEnabled,"missing saved model cannot silently fall back");Check(session.Model=="retired-model","unavailable model identity retained");
            models.SelectedItem=b;Check(session.Model=="b"&&!send.IsEnabled,"selected model still requires a nonblank question");
            ((TextBox)get("input")).Text="fixture question";Check(send.IsEnabled,"explicit model choice and question enable sending");
            call("Render",new object[0]);Check(models.SelectedItem==b,"render cannot reset explicit selection");
            call("ReplaceModels",new object[]{new[]{new StudioModel{Id="a",Label="A new"},new StudioModel{Id="b",Label="B new"}}});Check(((StudioModel)models.SelectedItem).Id=="b","catalog refresh restores by model id");
            int bad;var store=(StudioStore)get("store");Check(store.Load(out bad).Single().Model=="b","model preference persisted before sending");
            call("NewSession",new object[0]);Check(models.SelectedItem==null&&!send.IsEnabled,"new session requires its own explicit model choice");
            ((ListBox)get("list")).SelectedItem=session;Check(((StudioModel)models.SelectedItem).Id=="b","switching conversation restores its model");
            call("ReplaceModels",new object[]{new[]{a}});Check(models.SelectedItem==null&&!send.IsEnabled,"disappearing active model blocks sending");models.SelectedItem=a;
            var input=(TextBox)get("input");input.Text="saved draft";Check((bool)call("SaveCurrent",new object[0]),"baseline save");
            var file=Path.Combine(SettingsStore.DataDirectory,"studio",session.Id+".dat");var before=File.ReadAllBytes(file);
            using(var held=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.Read)){
                input.Text="latest unsaved draft";
                var close=new System.ComponentModel.CancelEventArgs();typeof(Window).GetMethod("OnClosing",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(w,new object[]{close});
                Check(close.Cancel&&!(bool)get("closed"),"save failure cancels window closure");Check(input.Text=="latest unsaved draft","unsaved input remains accessible");
                Check(((WrapPanel)get("saveRecovery")).Visibility==Visibility.Visible,"save recovery actions visible");Check(!send.IsEnabled,"save failure blocks new requests");
                Check(((string)call("RecoveryText",new object[0])).Contains("latest unsaved draft"),"recovery copy includes unsent text");
                Check(before.SequenceEqual(File.ReadAllBytes(file)),"failed atomic save preserves previous bytes");
                foreach(var size in new[]{new Size(1000,700),new Size(720,500)}){
                    var view=(FrameworkElement)w.Content;view.Width=size.Width-40;view.Height=size.Height-40;view.Measure(size);view.Arrange(new Rect(size));view.UpdateLayout();
                    foreach(Button button in ((WrapPanel)get("saveRecovery")).Children){var point=button.TransformToAncestor(view).Transform(new Point(0,0));Check(button.ActualHeight>0&&point.Y>=0&&point.Y+button.ActualHeight<=view.ActualHeight+1,"recovery action stays visible at "+size.Width);}
                    var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);var bg=new DrawingVisual();using(var dc=bg.RenderOpen())dc.DrawRectangle(w.Background,null,new Rect(size));bitmap.Render(bg);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var f=File.Create(Path.Combine(root,"save-recovery-"+size.Width+".png")))png.Save(f);
                }
                typeof(WidgetWindow).GetField("studioWindow",flags).SetValue(owner,w);
                var ownerClose=new System.ComponentModel.CancelEventArgs();typeof(Window).GetMethod("OnClosing",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(owner,new object[]{ownerClose});
                Check(ownerClose.Cancel,"widget exit cannot bypass unsaved Studio guard");
                typeof(WidgetWindow).GetField("studioWindow",flags).SetValue(owner,null);
            }
            var retry=(Button)((WrapPanel)get("saveRecovery")).Children[0];retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!(bool)get("storageBlocked")&&((WrapPanel)get("saveRecovery")).Visibility==Visibility.Collapsed,"retry restores normal controls");
            Check(store.Load(out bad).Single(x=>x.Id==session.Id).Draft=="latest unsaved draft","retry writes latest draft");
            call("ShowFailure",new object[]{new StudioFailure(StudioFailureKind.Quota)});Check(((TextBlock)get("status")).Text.Contains("한도"),"quota error has quota-specific recovery");
            call("ShowFailure",new object[]{new StudioFailure(StudioFailureKind.Model)});Check(models.SelectedItem==null&&!send.IsEnabled,"model rejection requires explicit reselection");models.SelectedItem=a;
            call("ShowFailure",new object[]{new StudioFailure(StudioFailureKind.Authentication)});Check(!send.IsEnabled&&!(bool)get("authenticated"),"expired login prevents further sends");
            foreach(StudioFailureKind kind in Enum.GetValues(typeof(StudioFailureKind))){Check(!String.IsNullOrWhiteSpace(StudioFailure.Guidance(kind,false))&&!String.IsNullOrWhiteSpace(StudioFailure.Guidance(kind,true)),"bilingual recovery guidance: "+kind);}
        }finally{if(w!=null)w.Close();owner.Close();SettingsStore.DataDirectoryOverride=null;}
    }
    [STAThread] static int Main(string[] args)
    {
        if(args.Length>0&&args[0]=="app-server")return Server();
        AppDomain.CurrentDomain.FirstChanceException+=RecordIoFailure;
        string root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"../../outputs/studio-tests/fixture-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        try{if(args.Length>0&&args[0]=="--probe"){Probe(root).GetAwaiter().GetResult();return 0;}Storage(root);Protocol(root).GetAwaiter().GetResult();Ui(root);Fixes(root);AdeNavigation(root);UiProtocol(root);Responsive(root);TaskFlow(root);Console.WriteLine("PASS: "+count+" Studio storage, UI and real child JSON-RPC checks; no live account or inference. Renders: "+root);return 0;}catch(Exception ex){Console.Error.WriteLine(ex);lock(ioFailures)foreach(var failure in ioFailures)Console.Error.WriteLine("Fixture IO diagnostic: "+failure);return 1;}
    }
}
