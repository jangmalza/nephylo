using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageWidget
{
    // Local presentation only. Model output is always text; it is never evaluated as XAML or HTML.
    internal sealed class StudioMessageView : Grid
    {
        readonly ScrollViewer scroll = new ScrollViewer();
        readonly StackPanel messages = new StackPanel();
        readonly List<MessageCard> cards = new List<MessageCard>();
        readonly Button latest = new Button();
        readonly StackPanel empty = new StackPanel();
        readonly Brush foreground, subtle, outline, userSurface, codeSurface;
        string sessionId, provider = "codex",startHint;
        internal bool ExternalWelcome=false;
        internal Func<string,FrameworkElement> ProviderIconFactory=null;
        bool following = true, scrollQueued,restoringScroll;
        int generation,readerVersion;
        Dictionary<string,StudioViewBookmark> viewStates=new Dictionary<string,StudioViewBookmark>();
        Action viewChanged;
        StudioMotion motion;
        static string S(string ko, string en) { return L.English ? en : ko; }

        internal StudioMessageView(Brush background, Brush foreground)
        {
            this.foreground = foreground;
            Color bg = ColorOf(background, Colors.Black), fg = ColorOf(foreground, Colors.White);
            subtle = Mix(bg, fg, .68); outline = Mix(bg, fg, .18);
            userSurface = Mix(bg, fg, .065); codeSurface = Mix(bg, fg, .025);
            Background = background;
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            scroll.Padding = new Thickness(20, 18, 20, 28);
            messages.MaxWidth = 760;
            messages.HorizontalAlignment = HorizontalAlignment.Stretch;
            scroll.Content = messages;
            Children.Add(scroll);
            AutomationProperties.SetName(scroll, S("대화 내용", "Conversation"));
            scroll.ScrollChanged += (s, e) => {
                // Content growth must not be mistaken for the user scrolling away.
                if (!restoringScroll&&Math.Abs(e.ExtentHeightChange) < .1 && Math.Abs(e.ViewportHeightChange) < .1)
                    following = scroll.ScrollableHeight-scroll.VerticalOffset<=2;
                if(!restoringScroll)CapturePosition();
                if(following&&cards.Any(c=>c.PendingMarkup))QueueScroll();
                ShowLatest();
            };
            scroll.PreviewMouseWheel+=(s,e)=>{if(e.Delta>0&&scroll.VerticalOffset>0)ReaderLeftTail();};
            scroll.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.PageUp||e.Key==Key.Home||e.Key==Key.Up)ReaderLeftTail();};
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            empty.Margin = new Thickness(24);
            empty.MaxWidth = 480;
            empty.IsHitTestVisible = false;
            empty.Children.Add(new TextBlock {
                Text = S("어떤 작업을 시작할까요?", "What would you like to work on?"),
                FontSize = 23, FontWeight = FontWeights.SemiBold, Foreground = foreground,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
            });
            empty.Children.Add(new TextBlock {
                Text = S("프로젝트를 선택하고 Codex를 연결하세요.\n코드 구조를 살펴보거나 개선 방향을 함께 정리할 수 있습니다.",
                    "Choose a project and connect Codex.\nExplore the code or work through an improvement together."),
                FontSize = 14, Foreground = subtle, TextWrapping = TextWrapping.Wrap,
                LineHeight = 23, Margin = new Thickness(0, 0, 0, 18)
            });
            empty.Children.Add(new TextBlock {
                Text = S("읽기 전용 미리보기 · 파일 변경과 권한 확대 요청은 거절합니다.",
                    "Read-only preview · File changes and permission escalation are declined."),
                FontSize = 12, Foreground = subtle, TextWrapping = TextWrapping.Wrap, LineHeight = 20
            });
            Children.Add(empty);
            SizeChanged += (s, e) => {
                scroll.Padding = ActualWidth < 520 ? new Thickness(8, 14, 8, 20) : new Thickness(20, 18, 20, 28);
                ResizeEmptyState();
            };
            latest.Content = S("↓ 최신 답변으로", "↓ Latest response");
            latest.Foreground = foreground;
            latest.Background = codeSurface;
            latest.BorderBrush = outline;
            latest.BorderThickness = new Thickness(1);
            latest.Padding = new Thickness(14, 7, 14, 7);
            latest.Margin = new Thickness(0, 0, 16, 12);
            latest.HorizontalAlignment = HorizontalAlignment.Right;
            latest.VerticalAlignment = VerticalAlignment.Bottom;
            latest.Visibility = Visibility.Collapsed;
            latest.Click += (s, e) => ResetScroll();
            AutomationProperties.SetName(latest, S("최신 답변으로 이동", "Go to the latest response"));
            Children.Add(latest);
        }
        internal void AttachWorkspace(Dictionary<string,StudioViewBookmark> states,Action changed,StudioMotion effects)
        {viewStates=states??new Dictionary<string,StudioViewBookmark>();viewChanged=changed;motion=effects;if(motion!=null)motion.Feedback(latest);}
        StudioViewBookmark Bookmark()
        {
            if(String.IsNullOrEmpty(sessionId))return null;StudioViewBookmark value;
            if(!viewStates.TryGetValue(sessionId,out value)){value=new StudioViewBookmark();viewStates[sessionId]=value;}return value;
        }
        internal void CapturePosition()
        {
            if(restoringScroll||cards.Count==0)return;var bookmark=Bookmark();if(bookmark==null)return;
            bookmark.HasScroll=true;bookmark.Following=following;bookmark.Offset=Math.Max(0,scroll.VerticalOffset);bookmark.Anchor=null;bookmark.AnchorOffset=0;
            foreach(var card in cards){
                double top=card.Root.TranslatePoint(new Point(),messages).Y+scroll.Padding.Top;
                if(top>bookmark.Offset)break;
                if(top+card.Root.ActualHeight>=bookmark.Offset){bookmark.Anchor=card.Key;bookmark.AnchorOffset=bookmark.Offset-top;break;}
            }
            if(viewChanged!=null)viewChanged();
        }
        void ReaderLeftTail(){following=false;readerVersion++;restoringScroll=false;CapturePosition();ShowLatest();}
        void RestorePosition()
        {
            var bookmark=Bookmark();int token=generation,reader=readerVersion;restoringScroll=true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{
                if(generation!=token)return;
                if(readerVersion!=reader){restoringScroll=false;return;}
                if(following)scroll.ScrollToEnd();
                else{
                    double offset=bookmark==null?0:bookmark.Offset;var anchor=bookmark==null?null:cards.FirstOrDefault(c=>c.Key==bookmark.Anchor);
                    if(anchor!=null)offset=anchor.Root.TranslatePoint(new Point(),messages).Y+scroll.Padding.Top+Math.Min(bookmark.AnchorOffset,Math.Max(0,anchor.Root.ActualHeight-1));
                    scroll.ScrollToVerticalOffset(Math.Max(0,offset));
                }
                Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{if(generation!=token)return;restoringScroll=false;CapturePosition();ShowLatest();}));
            }));
        }

        internal void SetProvider(string value)
        {
            var next = StudioProviderCatalog.Normalize(value);
            if (provider == next) return;
            provider = next; ResizeEmptyState();
        }
        internal void SetStartHint(string value)
        {if(startHint==value)return;startHint=value;ResizeEmptyState();}

        void ResizeEmptyState()
        {
            // Recovery actions can temporarily leave only a short transcript viewport.
            // Keep its title visible rather than centering a full-height panel outside the clip.
            bool shortView = ActualHeight < 240, tinyView = ActualHeight < 125;
            var title = (TextBlock)empty.Children[0];
            var description = (TextBlock)empty.Children[1];
            var policy = (TextBlock)empty.Children[2];
            empty.Margin = shortView ? new Thickness(16, 8, 16, 8) : new Thickness(24);
            title.FontSize = tinyView && ActualHeight < 60 ? 14 : shortView ? 18 : 23;
            title.Margin = new Thickness(0, 0, 0, tinyView ? 0 : shortView ? 8 : 12);
            title.TextWrapping = tinyView ? TextWrapping.NoWrap : TextWrapping.Wrap;
            title.TextTrimming = tinyView ? TextTrimming.CharacterEllipsis : TextTrimming.None;
            description.Visibility = tinyView ? Visibility.Collapsed : Visibility.Visible;
            description.Text = provider == "claude" ?
                shortView ? S("프로젝트를 선택하고 Claude Code를 연결하세요.", "Choose a project and connect Claude Code.") :
                    S("프로젝트를 선택하고 Claude Code를 연결하세요.\n직접 입력하거나 전달한 문맥을 바탕으로 함께 정리할 수 있습니다.",
                        "Choose a project and connect Claude Code.\nWork through ideas using the text and context you provide.") :
                shortView ? S("프로젝트를 선택하고 Codex를 연결하세요.", "Choose a project and connect Codex.") :
                    S("프로젝트를 선택하고 Codex를 연결하세요.\n코드 구조를 살펴보거나 개선 방향을 함께 정리할 수 있습니다.",
                        "Choose a project and connect Codex.\nExplore the code or work through an improvement together.");
            description.Margin = new Thickness(0, 0, 0, shortView ? 0 : 18);
            if(StudioProviderCatalog.IsGemini(provider))description.Text=provider=="gemini-api"?
                S("Gemini API 키를 설정하고 모델을 선택하세요.\n완료된 대화와 현재 질문으로 이어갑니다.","Configure a Gemini API key and choose a model.\nContinue with completed turns and your current question."):
                S("공식 Gemini CLI를 연결하고 Google 로그인을 확인하세요.\n직접 입력하거나 전달한 문맥으로 대화합니다.","Connect official Gemini CLI and confirm Google sign-in.\nChat using the text and context you provide.");
            if(!String.IsNullOrWhiteSpace(startHint))description.Text=startHint;
            policy.Text = provider == "claude" ? S("대화 전용 · 프로젝트 파일에 접근하거나 명령을 실행하지 않습니다.",
                "Chat only · Project files are not accessed and commands are not run.") :
                S("읽기 전용 미리보기 · 파일 변경과 권한 확대 요청은 거절합니다.",
                    "Read-only preview · File changes and permission escalation are declined.");
            policy.Visibility = shortView ? Visibility.Collapsed : Visibility.Visible;
            if(StudioProviderCatalog.IsGemini(provider))policy.Text=S("대화 전용 · 파일·명령 도구는 사용하지 않습니다.","Chat only · File and command tools are not used.");
        }

        internal void Update(StudioSession session)
        {
            string nextId = session == null ? null : session.Id;
            bool switched=sessionId!=nextId;
            if (switched) {
                CapturePosition();generation++;scrollQueued=false;sessionId = nextId; cards.Clear(); messages.Children.Clear();
                var state=Bookmark();following=state==null||!state.HasScroll||state.Following;
            }
            int count = session == null || session.Messages == null ? 0 : session.Messages.Count;
            // StudioStore enforces the same bound; also protect the visual tree from malformed fixtures.
            count = Math.Min(count, 400);
            bool changed = false;
            while (cards.Count > count) {
                int index = cards.Count - 1;
                messages.Children.RemoveAt(index); cards.RemoveAt(index); changed = true;
            }
            for (int i = 0; i < count; i++) {
                if (i == cards.Count) {
                    var card = new MessageCard(this,session.Messages[i].Id??("message-"+i));
                    cards.Add(card); messages.Children.Add(card.Root); changed = true;
                    if(!switched&&motion!=null)motion.Enter(card.Root);
                }
                if (cards[i].Update(session.Messages[i])) changed = true;
            }
            empty.Visibility = count == 0 && !ExternalWelcome ? Visibility.Visible : Visibility.Collapsed;
            if(switched){RestorePosition();if(motion!=null)motion.Enter(messages,false);}
            else if (changed && following) QueueScroll();
            ShowLatest();
        }

        internal void ResetScroll() { readerVersion++;following = true;restoringScroll=false;QueueScroll(); }
        bool IsNearBottom() { return scroll.ScrollableHeight - scroll.VerticalOffset <= 36; }
        void ShowLatest() { latest.Visibility = cards.Count > 0 && !following && !IsNearBottom() ? Visibility.Visible : Visibility.Collapsed; }
        void QueueScroll()
        {
            if (scrollQueued) return;
            scrollQueued = true;int token=generation,reader=readerVersion;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                if(generation!=token)return;
                scrollQueued = false;
                if (following&&readerVersion==reader) {foreach(var card in cards)card.FinishMarkup();scroll.ScrollToEnd();}
                ShowLatest();
            }));
        }
        static Color ColorOf(Brush brush, Color fallback)
        {
            var solid = brush as SolidColorBrush;
            return solid == null ? fallback : solid.Color;
        }
        static Brush Mix(Color a, Color b, double amount)
        {
            var result = new SolidColorBrush(Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount),
                (byte)(a.B + (b.B - a.B) * amount)));
            result.Freeze(); return result;
        }
        Button CopyButton(string label, Func<string> content)
        {
            var b = new Button {
                Content = label, Foreground = subtle, Background = Brushes.Transparent,
                BorderBrush = outline, BorderThickness = new Thickness(0), FontSize = 12,
                Padding = new Thickness(8, 3, 8, 3), MinHeight = 28,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = label
            };
            AutomationProperties.SetName(b, label);
            b.Click += (s, e) => {
                try { Clipboard.SetText(content() ?? ""); b.Content = S("복사됨", "Copied"); }
                catch { b.Content = S("복사 실패", "Copy failed"); b.ToolTip = S("텍스트를 선택해 직접 복사해 주세요.", "Select the text and copy it manually."); }
            };
            if(motion!=null)motion.Feedback(b);
            return b;
        }

        sealed class MessageCard
        {
            readonly StudioMessageView owner;
            internal readonly Border Root = new Border();
            readonly StackPanel content = new StackPanel();
            readonly TextBlock name = new TextBlock(), meta = new TextBlock(), status = new TextBlock();
            readonly DockPanel header=new DockPanel();
            readonly ContentControl providerIcon=new ContentControl();
            readonly Button copy;
            readonly string key;
            TextBox plain;
            internal string Key {get{return key;}}
            string text, role, state, model, provider;
            bool initialized,pendingMarkup;
            string displayedIconProvider;
            internal bool PendingMarkup {get{return pendingMarkup;}}
            internal void FinishMarkup()
            {
                if(!pendingMarkup)return;pendingMarkup=false;
                content.Children.Clear();plain=null;owner.Markdown(content,text,key);
                if(state=="completed"){status.Text="";status.Visibility=Visibility.Collapsed;}
            }
            internal MessageCard(StudioMessageView owner,string key)
            {
                this.owner = owner;this.key=key;
                Root.Margin = new Thickness(0, 0, 0, 28);
                Root.Padding = new Thickness(0, 4, 0, 8);
                Root.CornerRadius = new CornerRadius(8);
                Root.BorderThickness = new Thickness(0);
                var stack = new StackPanel(); Root.Child = stack;
                header.Margin = new Thickness(0, 0, 0, 12);stack.Children.Add(header);
                copy = owner.CopyButton(S("답변 복사", "Copy response"), () => text);
                providerIcon.Margin=new Thickness(0,0,8,0);providerIcon.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(providerIcon,Dock.Left);header.Children.Add(providerIcon);
                var titles = new DockPanel { VerticalAlignment = VerticalAlignment.Center }; header.Children.Add(titles);
                name.Foreground = owner.foreground; name.FontWeight = FontWeights.SemiBold; name.FontSize = 13;
                name.Margin = new Thickness(0, 2, 10, 2); DockPanel.SetDock(name, Dock.Left); titles.Children.Add(name);
                meta.Foreground = owner.subtle; meta.FontSize = 12; meta.Margin = new Thickness(0, 3, 8, 2);
                meta.TextTrimming = TextTrimming.CharacterEllipsis; titles.Children.Add(meta);
                status.Foreground = owner.subtle; status.FontSize = 12; status.LineHeight = 20;
                status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 0, 0, 8);
                stack.Children.Add(status);
                stack.Children.Add(content);
                var footer=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,6,0,0)};copy.FontSize=10;copy.MinHeight=22;copy.Padding=new Thickness(3,2,5,2);footer.Children.Add(copy);stack.Children.Add(footer);
            }
            internal bool Update(StudioMessage message)
            {
                string nextText = message == null ? "" : message.Text ?? "";
                string nextRole = message == null ? "assistant" : message.Role ?? "assistant";
                string nextState = message == null ? "" : message.State ?? "";
                string nextModel = message == null ? "" : message.Model ?? "";
                string nextProvider = message == null ? "codex" : message.Provider ?? "codex";
                bool bodyChanged = !initialized || nextText != text || (state == "running") != (nextState == "running") || nextRole != role;
                bool changed = bodyChanged || nextState != state || nextModel != model || nextProvider != provider;
                if (!changed) return false;
                // Final Markdown can change paragraph heights and destroy a live
                // selection. Keep the reader's existing surface until they return
                // to the tail; this is not a delay in receiving answer text.
                pendingMarkup=nextRole!="user"&&nextState!="running"&&nextText.Length<=48000&&!owner.following&&plain!=null&&(pendingMarkup||state=="running");
                text = nextText; role = nextRole; state = nextState; model = nextModel; provider = nextProvider; initialized = true;
                bool user = role == "user";
                header.Visibility=user?Visibility.Collapsed:Visibility.Visible;
                if(!user&&owner.ProviderIconFactory!=null&&displayedIconProvider!=provider){providerIcon.Content=owner.ProviderIconFactory(provider);displayedIconProvider=provider;}
                name.Text = user ? S("나", "You") : provider=="claude"?"Claude":StudioProviderCatalog.Name(provider);
                Root.Background = user ? owner.userSurface : Brushes.Transparent;Root.Padding=user?new Thickness(16,12,16,12):new Thickness(0,4,0,8);Root.HorizontalAlignment=user?HorizontalAlignment.Right:HorizontalAlignment.Stretch;Root.MaxWidth=user?650:Double.PositiveInfinity;
                Root.BorderBrush = user ? owner.outline : Brushes.Transparent;
                meta.Text = user ? "" : model;
                meta.ToolTip = String.IsNullOrWhiteSpace(meta.Text) ? null : meta.Text;
                var parts = new List<string>();
                if (!user && state == "running") parts.Add(S("답변 작성 중", "Writing response"));
                if (!user && state == "interrupted") parts.Add(S("중단됨 · 부분 답변", "Interrupted · Partial response"));
                if (!user && state == "failed") parts.Add(S("실패 · 아래 안내 확인", "Failed · See guidance below"));
                if (!user && state == "unconfirmed") parts.Add(S("전송 결과 확인 필요 · 자동 재전송 안 함", "Outcome unconfirmed · No automatic retry"));
                if (!user && pendingMarkup && state == "completed") parts.Add(S("응답 완료", "Response received"));
                status.Text = String.Join(" · ", parts);
                status.Visibility = String.IsNullOrEmpty(status.Text) ? Visibility.Collapsed : Visibility.Visible;
                copy.Visibility = String.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
                copy.Content = S("복사", "Copy");
                AutomationProperties.SetName(copy, user ? S("질문 복사", "Copy message") : S("답변 복사", "Copy response"));
                if (bodyChanged) {
                    if (String.IsNullOrEmpty(text)) {
                        content.Children.Clear();plain=null;
                        content.Children.Add(new TextBlock {
                            Text = state == "running" ? S("응답을 기다리고 있습니다…", "Waiting for a response…") : S("답변 내용이 없습니다.", "No response text."),
                            Foreground = owner.subtle, FontSize = 14, LineHeight = 23, TextWrapping = TextWrapping.Wrap
                        });
                    } else if (user || state == "running" || text.Length > 48000 || pendingMarkup) {
                        // Reuse the live text control: text is available immediately and
                        // neither other cards nor the active selection are recreated per delta.
                        if(plain==null){content.Children.Clear();plain=owner.PlainText(text,false);content.Children.Add(plain);}
                        else{int selectionStart=plain.SelectionStart,selectionLength=plain.SelectionLength;double offset=plain.VerticalOffset;
                            if(text.StartsWith(plain.Text,StringComparison.Ordinal))plain.AppendText(text.Substring(plain.Text.Length));else plain.Text=text;
                            selectionStart=Math.Min(selectionStart,text.Length);plain.Select(selectionStart,Math.Min(selectionLength,text.Length-selectionStart));plain.ScrollToVerticalOffset(offset);
                        }
                    } else {content.Children.Clear();plain=null;owner.Markdown(content,text,key);}
                }
                return true;
            }
        }

        TextBox PlainText(string value, bool code)
        {
            var box = new TextBox {
                Text = value, IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = code ? TextWrapping.NoWrap : TextWrapping.Wrap,
                BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(0),
                Background = Brushes.Transparent, Foreground = foreground, FontSize = code ? 13 : 15,
                HorizontalScrollBarVisibility = code ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = code ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                IsReadOnlyCaretVisible = false, MinHeight = 20
            };
            TextBlock.SetLineHeight(box, code ? 21 : 25);
            if (code) { box.FontFamily = new FontFamily("Consolas"); box.MaxHeight = 320; }
            box.PreviewMouseWheel += (s, e) => {
                bool canScrollCode = code && (e.Delta > 0 ? box.VerticalOffset > 0 : box.ExtentHeight - box.ViewportHeight - box.VerticalOffset > 1);
                if (!canScrollCode) ScrollConversation(e);
            };
            AutomationProperties.SetName(box, code ? S("코드", "Code") : S("메시지 내용", "Message content"));
            return box;
        }
        void Markdown(Panel panel, string value,string key)
        {
            string[] lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            // A log or generated file with hundreds of tiny lines should remain a single control.
            if (lines.Length > 180) { panel.Children.Add(PlainText(value, false)); return; }
            var prose = new List<string>(); var code = new StringBuilder();
            bool inCode = false; string fence = "", language = "";
            int blocks = 0;
            foreach (string line in lines) {
                string trimmed = line.TrimStart();
                bool opens = trimmed.StartsWith("```") || trimmed.StartsWith("~~~");
                if (!inCode && opens && blocks < 80) {
                    AddProse(panel, prose); prose.Clear();
                    fence = trimmed.StartsWith("```") ? "```" : "~~~";
                    language = trimmed.Substring(3).Trim();
                    if (language.Length > 30) language = language.Substring(0, 30);
                    inCode = true; code.Length = 0; blocks++;
                } else if (inCode && trimmed.StartsWith(fence)) {
                    AddCode(panel, code.ToString().TrimEnd('\n'), language,key+":"+blocks); inCode = false;
                } else if (inCode) code.Append(line).Append('\n');
                else prose.Add(line);
            }
            if (inCode) AddCode(panel, code.ToString().TrimEnd('\n'), language,key+":"+blocks);
            AddProse(panel, prose);
        }
        void AddCode(Panel panel, string value, string language,string key)
        {
            var border = new Border {
                Background = codeSurface, BorderBrush = outline, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 12)
            };
            var stack = new StackPanel(); border.Child = stack;
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) }; stack.Children.Add(top);
            var copy = CopyButton(S("코드 복사", "Copy code"), () => value);
            DockPanel.SetDock(copy, Dock.Right); top.Children.Add(copy);
            // Use an independent button so collapsing never copies or executes content.
            var fold=new Button{Content=S("코드 접기","Collapse code"),Foreground=subtle,Background=Brushes.Transparent,BorderThickness=new Thickness(0),FontSize=11,Padding=new Thickness(7,3,7,3),MinHeight=28};
            DockPanel.SetDock(fold,Dock.Right);top.Children.Add(fold);if(motion!=null)motion.Feedback(fold);
            top.Children.Add(new TextBlock {
                Text = String.IsNullOrWhiteSpace(language) ? S("코드", "Code") : language,
                Foreground = subtle, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var code=PlainText(value,true);stack.Children.Add(code);panel.Children.Add(border);
            var bookmark=Bookmark();bool expanded=bookmark==null||!bookmark.FoldedCode.Contains(key);code.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;
            Action label=()=>{fold.Content=expanded?S("코드 접기","Collapse code"):S("코드 펼치기","Expand code");AutomationProperties.SetName(fold,(string)fold.Content);};label();
            fold.Click+=(s,e)=>{CapturePosition();expanded=!expanded;label();var current=Bookmark();if(current!=null){current.FoldedCode.Remove(key);if(!expanded){if(current.FoldedCode.Count>=64)current.FoldedCode.RemoveAt(0);current.FoldedCode.Add(key);}if(viewChanged!=null)viewChanged();}if(motion!=null)motion.Fold(code,expanded);else code.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;};
        }
        void AddProse(Panel panel, List<string> lines)
        {
            if (lines.Count == 0) return;
            // A single document per prose section keeps text selection continuous across paragraphs.
            var document = new FlowDocument { PagePadding = new Thickness(0), FontSize = 15, Foreground = foreground };
            var paragraphLines = new StringBuilder();
            Action flush = () => {
                if (paragraphLines.Length == 0) return;
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 11), LineHeight = 25 };
                InlineText(p, paragraphLines.ToString()); document.Blocks.Add(p); paragraphLines.Length = 0;
            };
            for(int lineIndex=0;lineIndex<lines.Count;lineIndex++) {
                string line=lines[lineIndex];
                string trimmed = line.TrimStart();
                if(lineIndex+1<lines.Count&&line.Contains("|")&&Regex.IsMatch(lines[lineIndex+1],@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$")){
                    var cells=TableCells(line);if(cells.Count>=2&&cells.Count<=6&&cells.All(c=>c.Length<=256)){
                        flush();var table=new Table{CellSpacing=0,Margin=new Thickness(0,6,0,14)};for(int column=0;column<cells.Count;column++)table.Columns.Add(new TableColumn{Width=GridLength.Auto});var rows=new TableRowGroup();table.RowGroups.Add(rows);AddTableRow(rows,cells,true);lineIndex++;
                        for(int rowsRead=0;lineIndex+1<lines.Count&&rowsRead<40;rowsRead++){
                            var next=TableCells(lines[lineIndex+1]);if(!lines[lineIndex+1].Contains("|")||next.Count!=cells.Count||next.Any(c=>c.Length>256))break;lineIndex++;AddTableRow(rows,next,false);
                        }
                        document.Blocks.Add(table);continue;
                    }
                }
                if (String.IsNullOrWhiteSpace(line)) { flush(); continue; }
                int heading = 0;
                while (heading < trimmed.Length && heading < 6 && trimmed[heading] == '#') heading++;
                if (heading > 0 && trimmed.Length > heading && trimmed[heading] == ' ') {
                    flush();
                    var p = new Paragraph { FontSize = heading == 1 ? 20 : heading == 2 ? 17 : 15,
                        FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 10) };
                    InlineText(p, trimmed.Substring(heading + 1)); document.Blocks.Add(p); continue;
                }
                bool bullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ");
                int number = 0;
                while (number < trimmed.Length && number < 4 && Char.IsDigit(trimmed[number])) number++;
                bool ordered = number > 0 && trimmed.Length > number + 1 && trimmed[number] == '.' && trimmed[number + 1] == ' ';
                if (bullet || ordered) {
                    flush();
                    var p = new Paragraph { Margin = new Thickness(10, 0, 0, 6), LineHeight = 25 };
                    InlineText(p, bullet ? "• " + trimmed.Substring(2) : trimmed); document.Blocks.Add(p); continue;
                }
                if (paragraphLines.Length > 0) paragraphLines.Append('\n');
                paragraphLines.Append(line);
            }
            flush();
            if (document.Blocks.Count == 0) return;
            var box = new RichTextBox {
                Document = document, IsReadOnly = true, IsDocumentEnabled = false,
                BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(0),
                Background = Brushes.Transparent, Foreground = foreground,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            AutomationProperties.SetName(box, S("답변 내용", "Response content"));
            box.PreviewMouseWheel += (s, e) => ScrollConversation(e);
            panel.Children.Add(box);
        }
        static List<string> TableCells(string line)
        {
            var text=line.Trim().Trim('|');var cells=new List<string>();var current=new StringBuilder();bool code=false;
            for(int i=0;i<text.Length;i++){char c=text[i];if(c=='\\'&&i+1<text.Length&&text[i+1]=='|'){current.Append('|');i++;continue;}if(c=='`')code=!code;if(c=='|'&&!code){cells.Add(current.ToString().Trim());current.Length=0;}else current.Append(c);}cells.Add(current.ToString().Trim());return cells;
        }
        void AddTableRow(TableRowGroup rows,List<string> cells,bool header)
        {
            var row=new TableRow();rows.Rows.Add(row);foreach(var value in cells){var p=new Paragraph{Margin=new Thickness(0),LineHeight=22};InlineText(p,value);row.Cells.Add(new TableCell(p){Padding=new Thickness(8,7,8,7),BorderBrush=outline,BorderThickness=new Thickness(0,0,0,1),Background=header?codeSurface:Brushes.Transparent,FontWeight=header?FontWeights.SemiBold:FontWeights.Normal,FontSize=13});}
        }
        void ScrollConversation(MouseWheelEventArgs e)
        {
            // Read-only text controls otherwise consume the wheel even with their own bars disabled.
            double lines = SystemParameters.WheelScrollLines;
            double step = lines < 0 ? scroll.ViewportHeight : Math.Max(1, lines) * 16;
            if(e.Delta>0&&scroll.VerticalOffset>0)ReaderLeftTail();
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 120.0 * step);
            e.Handled = true;
        }
        void InlineText(Paragraph paragraph, string value)
        {
            // Only bold and inline-code spans are interpreted. Links/HTML remain inert text.
            int start = 0;
            while (start < value.Length) {
                int bold = value.IndexOf("**", start, StringComparison.Ordinal);
                int tick = value.IndexOf('`', start);
                int next = bold < 0 ? tick : tick < 0 ? bold : Math.Min(bold, tick);
                if (next < 0) { paragraph.Inlines.Add(new Run(value.Substring(start))); break; }
                string delimiter = next == bold ? "**" : "`";
                int end = value.IndexOf(delimiter, next + delimiter.Length, StringComparison.Ordinal);
                if (end < 0) { paragraph.Inlines.Add(new Run(value.Substring(start))); break; }
                if (next > start) paragraph.Inlines.Add(new Run(value.Substring(start, next - start)));
                var run = new Run(value.Substring(next + delimiter.Length, end - next - delimiter.Length));
                if (delimiter == "**") run.FontWeight = FontWeights.SemiBold;
                else { run.FontFamily = new FontFamily("Consolas"); run.Background = codeSurface; run.FontSize = 12.5; }
                paragraph.Inlines.Add(run); start = end + delimiter.Length;
            }
        }
    }
}
