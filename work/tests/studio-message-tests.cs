using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageWidget;

// The focused fixture compiles only StudioStore + StudioMessageView; no application startup or account access.
namespace UsageWidget { internal static class L { internal static bool English; } }
internal static class StudioMessageTests
{
    static int count;
    static void Check(bool value, string name) { count++; if (!value) throw new Exception(name); }
    static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T) yield return (T)root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    static void Layout(FrameworkElement view, double width = 760, double height = 470)
    {
        view.Width = width; view.Height = height;
        for (int i = 0; i < 3; i++) { view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout(); Pump(); }
    }
    static StudioSession Session()
    {
        var session = new StudioSession { Title = "Fixture", Project = @"C:\Fixture" };
        for (int i = 0; i < 18; i++) session.Messages.Add(new StudioMessage {
            Role = i % 2 == 0 ? "user" : "assistant", State = "completed", Model = "fixture-model",
            Text = "Message " + i + "\n" + String.Join(" ", Enumerable.Repeat("Selectable conversation content.", 12))
        });
        session.Messages.Last().State = "running"; return session;
    }
    static string Prose(DependencyObject root)
    {
        return String.Join("\n", Descendants<RichTextBox>(root).Select(x => new TextRange(x.Document.ContentStart, x.Document.ContentEnd).Text)) +
            String.Join("\n", Descendants<TextBox>(root).Select(x => x.Text));
    }
    static void Capture(FrameworkElement view, string path, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) png.Save(file);
    }
    static Rect Bounds(FrameworkElement child, FrameworkElement parent)
    {
        return child.TransformToAncestor(parent).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));
    }
    static void ScrollAndReuse()
    {
        var view = new StudioMessageView(Brushes.Black, Brushes.White); var session = Session();
        view.Update(session); Layout(view);
        var scroll = Field<ScrollViewer>(view, "scroll"); var panel = Field<StackPanel>(view, "messages"); var latest = Field<Button>(view, "latest");
        Check(panel.Children.Count == 18, "one card per message");
        Check(scroll.ScrollableHeight > 800, "long conversation scrolls");
        Check(scroll.ScrollableHeight - scroll.VerticalOffset < 2, "new conversation starts at latest message");
        var firstCard = panel.Children[0]; var firstText = Descendants<TextBox>(firstCard).First();
        firstText.Select(0, 7); string selection = firstText.SelectedText;
        var oldLiveText = Descendants<TextBox>(panel.Children[17]).First();
        scroll.ScrollToVerticalOffset(90); Layout(view); double before = scroll.VerticalOffset;
        Check(latest.Visibility == Visibility.Visible, "latest button appears after scrolling up");
        session.Messages[17].Text += "\nAdditional streamed text.";
        view.Update(session); Layout(view);
        Check(Object.ReferenceEquals(firstCard, panel.Children[0]), "unchanged message keeps its card");
        Check(Object.ReferenceEquals(firstText, Descendants<TextBox>(panel.Children[0]).First()), "unchanged message keeps its text control");
        Check(firstText.SelectedText == selection, "selection in earlier message survives streaming");
        Check(Object.ReferenceEquals(oldLiveText, Descendants<TextBox>(panel.Children[17]).First())&&oldLiveText.Text.EndsWith("Additional streamed text."), "streamed text updates immediately in the same selectable control");
        Check(Math.Abs(scroll.VerticalOffset - before) < 1, "streaming preserves manual reading position");
        latest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(view);
        Check(scroll.ScrollableHeight - scroll.VerticalOffset < 2 && latest.Visibility == Visibility.Collapsed, "latest action returns to bottom");
        session.Messages[17].Text += "\n" + new String('x', 1000); view.Update(session); Layout(view);
        Check(scroll.ScrollableHeight - scroll.VerticalOffset < 2, "streaming follows when already at bottom");
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        firstText.RaiseEvent(wheel); Layout(view);
        Check(wheel.Handled && latest.Visibility == Visibility.Visible, "wheel over selectable text scrolls outer conversation");
        var next = Session(); next.Messages.RemoveRange(5, 13); view.Update(next); Layout(view);
        Check(!Object.ReferenceEquals(firstCard, panel.Children[0]) && panel.Children.Count == 5, "switching session clears prior cards");
        Check(scroll.ScrollableHeight - scroll.VerticalOffset < 2, "switching session restores latest position");
        next.Messages.Clear(); view.Update(next); Layout(view);
        Check(panel.Children.Count == 0 && Field<StackPanel>(view, "empty").Visibility == Visibility.Visible, "empty conversation shows guidance");
    }
    static void MarkdownAndBounds(string output)
    {
        foreach (var mode in new[] { "dark", "light" }) {
            var background = mode == "dark" ? Brushes.Black : Brushes.White;
            var foreground = mode == "dark" ? Brushes.White : Brushes.Black;
            var view = new StudioMessageView(background, foreground);
            var session = new StudioSession();
            session.Messages.Add(new StudioMessage { Role = "user", State = "completed", Text = "프로젝트 구조와 개선 방향을 설명해 주세요." });
            session.Messages.Add(new StudioMessage { Role = "assistant", State = "completed", Model = "fixture-model", Text =
                "## 프로젝트 살펴보기\n\n**세 가지 영역**으로 나뉩니다. `src`를 먼저 확인하세요.\n\n- 대화 화면\n- 로컬 저장\n1. 검증 실행\n\n```csharp\nvar project = \"Nephylo\";\nConsole.WriteLine(project);\n```\n\n<script>alert('not executed')</script>\n[link](https://example.invalid/)" });
            view.Update(session); Layout(view, 760, 690); view.ResetScroll(); Layout(view, 760, 690);
            var prose = Prose(view);
            Check(prose.Contains("프로젝트 살펴보기") && !prose.Contains("## 프로젝트"), mode + " headings formatted");
            Check(prose.Contains("• 대화 화면") && prose.Contains("1. 검증 실행"), mode + " list markers rendered");
            Check(prose.Contains("<script>alert('not executed')</script>") && prose.Contains("[link](https://example.invalid/)"), mode + " HTML and links remain inert text");
            Check(Descendants<TextBox>(view).Any(x => x.Text.Contains("Console.WriteLine") && x.FontFamily.Source == "Consolas"), mode + " fenced code is selectable monospace");
            Check(Descendants<Button>(view).Any(x => (string)x.Content == "코드 복사"), mode + " code copy action available");
            Check(Descendants<RichTextBox>(view).All(x => x.IsReadOnly && !x.IsDocumentEnabled), mode + " model output cannot activate embedded content");
            Check(Descendants<TextBox>(view).All(x => x.IsReadOnly), mode + " transcript cannot be edited");
            var cards = Field<StackPanel>(view, "messages");
            Check(((SolidColorBrush)((Border)cards.Children[0]).Background).Color != ((SolidColorBrush)background).Color, mode + " subtle user surface differs from canvas");
            Check(Descendants<TextBox>(view).All(x => ((SolidColorBrush)x.Foreground).Color == ((SolidColorBrush)foreground).Color), mode + " text inherits contrasting theme");
            var bitmap = new RenderTargetBitmap(760, 690, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, "message-view-" + mode + ".png"))) png.Save(file);
            session.Messages[1].Text = new String('x', 51000); view.Update(session); Layout(view);
            Check(Descendants<RichTextBox>(view).Count() == 0 && Descendants<TextBox>(view).Any(x => x.Text.Length == 51000), mode + " long output uses bounded plain-text control");
            session.Messages[1].Text = String.Join("\n", Enumerable.Repeat("# tiny", 200)); view.Update(session); Layout(view);
            Check(Descendants<RichTextBox>(view).Count() == 0, mode + " line-heavy output avoids hundreds of paragraph elements");
            session.Messages[1].State = "interrupted"; view.Update(session); Layout(view);
            Check(Descendants<TextBlock>(view).Any(x => x.Text.Contains("중단됨")), mode + " interruption labels partial answer");
        }
        L.English = true;
        var english = new StudioMessageView(Brushes.Black, Brushes.White); english.Update(new StudioSession()); Layout(english);
        Check(Descendants<TextBlock>(english).Any(x => x.Text == "What would you like to work on?"), "English empty-state guidance");
        L.English = false;
    }
    static void ReadingBookmarksAndFolds(string output)
    {
        var states=new Dictionary<string,StudioViewBookmark>();var first=Session();var other=Session();
        var view=new StudioMessageView(Brushes.Black,Brushes.White);view.AttachWorkspace(states,()=>{},null);view.Update(first);Layout(view);
        var scroll=Field<ScrollViewer>(view,"scroll");scroll.ScrollToVerticalOffset(125);Layout(view);double position=scroll.VerticalOffset;
        view.Update(other);Layout(view);view.Update(first);Layout(view);
        Check(Math.Abs(scroll.VerticalOffset-position)<1,"returning to a conversation restores its reading location");
        var restored=new StudioMessageView(Brushes.Black,Brushes.White);restored.AttachWorkspace(states,()=>{},null);restored.Update(first);Layout(restored);
        var restoredScroll=Field<ScrollViewer>(restored,"scroll");Check(Math.Abs(restoredScroll.VerticalOffset-position)<1,"a recreated view restores the same persisted bookmark");
        restoredScroll.ScrollToTop();Layout(restored);restored.Update(other);Layout(restored);restored.Update(first);Layout(restored);
        Check(restoredScroll.VerticalOffset<1,"top bookmark does not acquire the transcript padding as an offset");
        restored.ResetScroll();Layout(restored);var panel=Field<StackPanel>(restored,"messages");var live=Descendants<TextBox>(panel.Children[17]).First();live.Select(2,9);var selectedText=live.SelectedText;
        first.Messages[17].Text+=" immediate delta";restored.Update(first);
        Check(live.Text.EndsWith(" immediate delta")&&live.SelectedText==selectedText,"streaming updates synchronously and preserves the live text selection");
        var wheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,120){RoutedEvent=UIElement.PreviewMouseWheelEvent};live.RaiseEvent(wheel);Layout(restored);
        Check(restoredScroll.ScrollableHeight-restoredScroll.VerticalOffset>36,"a queued follow scroll cannot override an upward reader gesture");
        first.Messages[17].Text+=" another delta";restored.Update(first);Layout(restored);
        Check(restoredScroll.ScrollableHeight-restoredScroll.VerticalOffset>36,"further deltas do not resume following without reader intent");
        double heldOffset=restoredScroll.VerticalOffset;live.Select(2,9);selectedText=live.SelectedText;
        first.Messages[17].State="completed";restored.Update(first);Layout(restored);
        Check(Object.ReferenceEquals(live,Descendants<TextBox>(panel.Children[17]).First())&&live.SelectedText==selectedText,"completion preserves the live surface and text selection while reading earlier content");
        Check(Math.Abs(restoredScroll.VerticalOffset-heldOffset)<1,"final Markdown cannot move the reader before they return to the tail");
        restored.ResetScroll();Layout(restored);
        Check(!Descendants<TextBox>(panel.Children[17]).Contains(live)&&restoredScroll.ScrollableHeight-restoredScroll.VerticalOffset<2,"returning to latest applies deferred formatting and follows the completed answer");
        var codeSession=new StudioSession();codeSession.Messages.Add(new StudioMessage{Id="stable-code",Role="assistant",State="completed",Text="| 항목 | 상태 |\n| --- | --- |\n| **저장** | 성공 |\n| 권한 | 읽기 전용 |\n\n```csharp\nvar safe = 1;\nConsole.WriteLine(safe);\n```"});
        restored.Update(codeSession);Layout(restored,640,480);
        var tables=Descendants<RichTextBox>(restored).SelectMany(t=>t.Document.Blocks.OfType<Table>()).ToList();
        Check(tables.Count==1&&tables[0].RowGroups[0].Rows.Count==3,"Markdown table renders selectable structured rows");
        var code=Descendants<TextBox>(restored).Single(x=>x.FontFamily.Source=="Consolas");
        var fold=Descendants<Button>(restored).Single(x=>Object.Equals(x.Content,"코드 접기"));fold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout(restored,640,480);
        Check(code.Visibility==Visibility.Collapsed&&Descendants<Button>(restored).Any(x=>Object.Equals(x.Content,"코드 복사")&&x.Visibility==Visibility.Visible),"code can collapse without hiding its copy action");
        restored.Update(other);Layout(restored);restored.Update(codeSession);Layout(restored,640,480);
        Check(Descendants<TextBox>(restored).Single(x=>x.FontFamily.Source=="Consolas").Visibility==Visibility.Collapsed,"code disclosure state survives conversation switches");
        var expand=Descendants<Button>(restored).Single(x=>Object.Equals(x.Content,"코드 펼치기"));expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout(restored,640,480);
        Check(Descendants<TextBox>(restored).Single(x=>x.FontFamily.Source=="Consolas").Visibility==Visibility.Visible,"code can be expanded again after restoration");
        Capture(restored,Path.Combine(output,"markdown-table-and-code.png"),640,480);
    }
    static void CompactEmptyState(string output)
    {
        foreach (bool english in new[] { false, true }) {
            L.English = english;
            var view = new StudioMessageView(Brushes.Black, Brushes.White); view.Update(new StudioSession());
            var panel = Field<StackPanel>(view, "empty"); var title = (TextBlock)panel.Children[0];
            foreach (var size in new[] { new Size(650, 100), new Size(280, 100), new Size(280, 50), new Size(340, 185) }) {
                Layout(view, size.Width, size.Height);
                Point position = title.TransformToAncestor(view).Transform(new Point(0, 0));
                Check(title.Visibility == Visibility.Visible && title.ActualHeight > 0 && position.Y >= 0 && position.Y + title.ActualHeight <= size.Height,
                    "empty heading stays within " + size + " viewport (English=" + english + ")");
                Check(((TextBlock)panel.Children[2]).Visibility == Visibility.Collapsed, "compact empty state hides secondary policy");
                if (size.Height < 125) Check(((TextBlock)panel.Children[1]).Visibility == Visibility.Collapsed, "tiny empty state hides description");
            }
            Layout(view, 650, 100);
            var bitmap = new RenderTargetBitmap(650, 100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, "empty-100-" + (english ? "en" : "ko") + ".png"))) png.Save(file);
            Layout(view);
            Check(title.FontSize == 23 && ((TextBlock)panel.Children[1]).Visibility == Visibility.Visible && ((TextBlock)panel.Children[2]).Visibility == Visibility.Visible,
                "expanded empty state restores full guidance");
        }
        L.English = false;
    }
    static void ReadableColumnAndHeaders(string output)
    {
        var view = new StudioMessageView(Brushes.Black, Brushes.White);
        string model = "claude-sonnet-fixture-model-with-a-long-descriptive-suffix";
        var session = new StudioSession();
        session.Messages.Add(new StudioMessage { Role = "user", State = "completed", Text = "기존 작업의 문맥을 바탕으로 다음 단계를 정리해 주세요." });
        session.Messages.Add(new StudioMessage { Role = "assistant", Provider = "claude", State = "unconfirmed", Model = model,
            Text = "## 확인한 내용\n\n프로젝트의 **흑백 디자인**은 유지합니다.\n\n- 대화는 읽기 좋은 너비로 표시합니다.\n- 전송 결과가 불확실하면 자동 재전송하지 않습니다.\n\n먼저 현재 상태를 확인해 주세요." });
        view.Update(session); Layout(view, 1600, 680);
        var panel = Field<StackPanel>(view, "messages"); Rect column = Bounds(panel, view);
        Check(panel.ActualWidth <= 760 && panel.ActualWidth > 740, "wide windows retain the approved readable conversation measure");
        Check(column.Left > 300 && view.ActualWidth - column.Right > 300, "wide conversation has balanced space on both sides");
        Check(Math.Abs(column.Left - (view.ActualWidth - column.Right)) < 24, "conversation column stays centered within the scroll viewport");
        Capture(view, Path.Combine(output, "conversation-wide.png"), 1600, 680);
        Layout(view, 320, 680);
        var card = (Border)panel.Children[1];
        var role = Descendants<TextBlock>(card).Single(x => x.Text == "Claude");
        var metadata = Descendants<TextBlock>(card).Single(x => x.Text == model);
        var warning = Descendants<TextBlock>(card).Single(x => x.Text.Contains("전송 결과 확인 필요"));
        var copy = Descendants<Button>(card).Single(x => AutomationProperties.GetName(x) == "답변 복사");
        Rect roleBounds = Bounds(role, view), copyBounds = Bounds(copy, view), warningBounds = Bounds(warning, view);
        Check(Bounds(panel, view).Right <= view.ActualWidth && panel.ActualWidth > 250, "narrow conversation uses the available viewport without horizontal overflow");
        Check(copyBounds.Top >= roleBounds.Bottom && copyBounds.Left >= 0 && copyBounds.Right <= view.ActualWidth && roleBounds.Right <= view.ActualWidth, "copy action is below the body and long model names do not crowd it");
        Check((string)metadata.ToolTip == model && metadata.TextTrimming == TextTrimming.CharacterEllipsis, "full long model remains available without forcing the header to wrap");
        Check(warning.Visibility == Visibility.Visible && warningBounds.Top >= Math.Min(roleBounds.Bottom, copyBounds.Bottom), "delivery uncertainty remains visible in its own wrapping row");
        Check(warningBounds.Left >= 0 && warningBounds.Right <= view.ActualWidth, "uncertain-delivery guidance fits narrow windows");
        Check(Prose(view).Contains("자동 재전송하지 않습니다"), "message body remains intact after responsive layout");
        Capture(view, Path.Combine(output, "conversation-narrow.png"), 320, 680);
        foreach (bool english in new[] { false, true }) {
            L.English = english;
            foreach (string provider in new[] { "claude", "codex" }) {
                var emptyView = new StudioMessageView(Brushes.Black, Brushes.White);
                emptyView.SetProvider(provider); emptyView.Update(new StudioSession()); Layout(emptyView, 560, 360);
                var empty = Field<StackPanel>(emptyView, "empty");
                string description = ((TextBlock)empty.Children[1]).Text, policy = ((TextBlock)empty.Children[2]).Text;
                Check(description.Contains(provider == "claude" ? "Claude Code" : "Codex"), "empty state names selected provider (English=" + english + ")");
                Check(provider != "claude" || (!description.Contains("Codex") && policy.Contains(english ? "Chat only" : "대화 전용")), "Claude empty state does not promise Codex project access (English=" + english + ")");
                foreach (TextBlock text in empty.Children) {
                    Rect bounds = Bounds(text, emptyView);
                    Check(bounds.Top >= 0 && bounds.Bottom <= emptyView.ActualHeight && bounds.Right <= emptyView.ActualWidth, "expanded empty guidance fits its viewport");
                }
                Capture(emptyView, Path.Combine(output, "empty-" + provider + "-" + (english ? "en" : "ko") + ".png"), 560, 360);
            }
        }
        L.English = false;
    }
    [STAThread] static int Main()
    {
        string output = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "../../outputs/studio-message-tests/fixture-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        try { ScrollAndReuse(); MarkdownAndBounds(output); ReadingBookmarksAndFolds(output); CompactEmptyState(output); ReadableColumnAndHeaders(output); Console.WriteLine("PASS: " + count + " message presentation checks; no account, network or clipboard writes. Renders: " + output); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
