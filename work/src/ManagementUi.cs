using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private Window CreateManagementDialog(string title, double width, double height, double minWidth = 430)
        {
            var area = SystemParameters.WorkArea;
            double availableWidth = Math.Max(300, area.Width - 32);
            double availableHeight = Math.Max(300, area.Height - 32);
            var dialog = new LocalizedWindow {
                Title = "Nephylo · " + title, Owner = this,
                Width = Math.Min(width, availableWidth), Height = Math.Min(height, availableHeight),
                MinWidth = Math.Min(minWidth, availableWidth), MinHeight = Math.Min(440, availableHeight),
                MaxHeight = availableHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Back, Foreground = Fore, FontFamily = FontFamily,
                Resources = MakeResources(), Topmost = Topmost, UseLayoutRounding = true,
                Icon = AppIcon.CreateWindowIcon()
            };
            ApplyManagementReadingStyles(dialog.Resources);
            System.Windows.Media.TextOptions.SetTextFormattingMode(dialog,System.Windows.Media.TextFormattingMode.Display);
            ApplyDialogTheme(dialog);
            return dialog;
        }

        private StackPanel ManagementBody(Window dialog, string title, string description, out StackPanel footer)
        {
            var root = new DockPanel { Margin = new Thickness(24) };
            var header = new LocalizedStackPanel { Margin = new Thickness(0,0,0,20) };
            header.Children.Add(T("NEPHYLO",10,"#647070",FontWeights.SemiBold,new Thickness(0,0,0,8)));
            header.Children.Add(T(title,23,"#252827",FontWeights.SemiBold));
            if (!String.IsNullOrWhiteSpace(description))
                header.Children.Add(ManagementDescription(description,new Thickness(0,7,0,0)));
            DockPanel.SetDock(header,Dock.Top); root.Children.Add(header);
            footer = new LocalizedStackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var bottom = new LocalizedBorder {
                Child = footer, BorderBrush = BrushOf("#C8CECF"), BorderThickness = new Thickness(0,1,0,0),
                Padding = new Thickness(0,14,0,0), Margin = new Thickness(0,16,0,0)
            };
            DockPanel.SetDock(bottom,Dock.Bottom); root.Children.Add(bottom);
            var body = new LocalizedStackPanel();
            root.Children.Add(new ScrollViewer {
                Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0,0,5,0)
            });
            dialog.Content = root;
            return body;
        }

        private StackPanel ManagementSection(StackPanel parent, string title, string description = null)
        {
            var content = new LocalizedStackPanel();
            if (!String.IsNullOrWhiteSpace(title))
                content.Children.Add(T(title,15,"#252827",FontWeights.SemiBold,new Thickness(0,0,0,12)));
            if (!String.IsNullOrWhiteSpace(description))
                content.Children.Add(ManagementDescription(description,new Thickness(0,0,0,12)));
            parent.Children.Add(new LocalizedBorder {
                Child = content, Background = BrushOf("#FBFBF8"), BorderBrush = BrushOf("#C8CECF"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(16), Margin = new Thickness(0,0,0,14)
            });
            return content;
        }

        private Grid ManagementRow(StackPanel parent, string title, string description, FrameworkElement control)
        {
            var row = new LocalizedGrid { Margin = new Thickness(0,4,0,12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var caption = new LocalizedStackPanel { Margin = new Thickness(0,0,16,0), VerticalAlignment = VerticalAlignment.Center };
            caption.Children.Add(T(title,14,"#252827",FontWeights.Medium));
            if (!String.IsNullOrWhiteSpace(description))
                caption.Children.Add(ManagementDescription(description,new Thickness(0,5,0,0)));
            row.Children.Add(caption);
            var checkbox = control as CheckBox;
            if (checkbox != null) {
                caption.Background = System.Windows.Media.Brushes.Transparent;
                caption.MouseLeftButtonUp += (s,e) => {
                    if (!checkbox.IsEnabled) return;
                    checkbox.Focus();
                    checkbox.IsChecked = checkbox.IsChecked != true;
                    checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,checkbox));
                    e.Handled = true;
                };
            }
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control,1); row.Children.Add(control);
            bool stacked = false;
            row.SizeChanged += (s,e) => {
                if (e.NewSize.Width <= 0 || checkbox != null) return;
                double controlWidth = Double.IsNaN(control.Width) ? Math.Max(control.DesiredSize.Width,120) : control.Width;
                bool next = e.NewSize.Width - controlWidth - 16 < 200;
                control.MaxWidth = Math.Max(40,e.NewSize.Width);
                if (next == stacked) return;
                stacked = next;
                row.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : GridLength.Auto;
                Grid.SetColumnSpan(caption,stacked ? 2 : 1);
                caption.Margin = stacked ? new Thickness(0,0,0,10) : new Thickness(0,0,16,0);
                Grid.SetRow(control,stacked ? 1 : 0); Grid.SetColumn(control,stacked ? 0 : 1);
                Grid.SetColumnSpan(control,stacked ? 2 : 1);
                control.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            };
            if (String.IsNullOrWhiteSpace(AutomationProperties.GetName(control))) AutomationProperties.SetName(control,title);
            parent.Children.Add(row);
            return row;
        }

        private TextBlock ManagementDescription(string text, Thickness margin)
        {
            var description = T(text,13,"#566369",FontWeights.Normal,margin);
            description.LineHeight = 20;
            return description;
        }

        private Button ManagementAction(string label, Action click, bool primary = false)
        {
            var button = new LocalizedButton {
                Content = label, ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, MinWidth = 78,
                Padding = new Thickness(14,8,14,8), Margin = new Thickness(8,0,0,0)
            };
            if (primary) { button.Background = BrushOf("#2448F5"); button.Foreground = BrushOf("#FFFFFF"); button.BorderBrush = BrushOf("#2448F5"); }
            if (click != null) button.Click += (s,e) => click();
            return button;
        }

        private static DataTemplate ActionTextTemplate()
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding());
            text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);
            text.SetValue(TextBlock.TextAlignmentProperty,TextAlignment.Center);
            return new DataTemplate { VisualTree = text };
        }

        private static void ApplyManagementReadingStyles(ResourceDictionary resources)
        {
            // Scope input scale to management dialogs, not the compact widget.
            foreach (Type type in new[] { typeof(TextBox),typeof(PasswordBox),typeof(ComboBox),typeof(ComboBoxItem) }) {
                var style = new Style(type,resources[type] as Style);
                style.Setters.Add(new Setter(Control.FontSizeProperty,14.0));
                if (type != typeof(ComboBoxItem)) style.Setters.Add(new Setter(Control.MinHeightProperty,38.0));
                resources[type] = style;
            }
            UiL.AddStyles(resources);
        }
    }
}
