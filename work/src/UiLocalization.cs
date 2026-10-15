using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UsageWidget
{
    // Derived controls retain their original source values; language changes affect display only.
    // No view is rebuilt, so unsaved input and in-flight operations survive a language switch.
    internal static class UiL
    {
        private static readonly List<WeakReference> Controls = new List<WeakReference>();
        internal static readonly DependencyProperty PreserveTextProperty = DependencyProperty.RegisterAttached("PreserveText",typeof(bool),typeof(UiL),new FrameworkPropertyMetadata(false));
        internal static void Raw(TextBlock text) { text.SetValue(PreserveTextProperty,true); text.CoerceValue(TextBlock.TextProperty); }
        internal static string Source(DependencyObject element, DependencyProperty property) { return element.ReadLocalValue(property) as string ?? element.GetValue(property) as string ?? ""; }
        internal static void Preserve(TextBlock text, bool preserve) { text.SetValue(PreserveTextProperty,preserve); text.CoerceValue(TextBlock.TextProperty); }
        private static object Coerce(DependencyObject owner, object value) { return value is string && !(bool)owner.GetValue(PreserveTextProperty) ? L.Text((string)value) : value; }
        internal static void Register(Type type, DependencyProperty textProperty = null)
        {
            FrameworkElement.ToolTipProperty.OverrideMetadata(type,new FrameworkPropertyMetadata(null,null,Coerce));
            AutomationProperties.NameProperty.OverrideMetadata(type,new FrameworkPropertyMetadata("",null,Coerce));
            AutomationProperties.ItemStatusProperty.OverrideMetadata(type,new FrameworkPropertyMetadata("",null,Coerce));
            if (textProperty != null) textProperty.OverrideMetadata(type,new FrameworkPropertyMetadata(textProperty == TextBlock.TextProperty || textProperty == Window.TitleProperty ? (object)"" : null,null,Coerce));
        }
        internal static void Track(FrameworkElement element, Type baseType)
        {
            Controls.Add(new WeakReference(element));
            if (Controls.Count > 4096) Controls.RemoveAll(item => !item.IsAlive);
        }
        internal static void AddStyles(ResourceDictionary resources)
        {
            // Reuse the original implicit styles. Dynamic Style resource references can re-enter
            // WPF's ComboBox style cache while selected items are attached to their parent.
            foreach (var type in new[] { typeof(LocalizedTextBlock),typeof(LocalizedButton),typeof(LocalizedCheckBox),typeof(LocalizedComboBoxItem),typeof(LocalizedMenuItem),typeof(LocalizedBorder),typeof(LocalizedStackPanel),typeof(LocalizedGrid),typeof(LocalizedExpander),typeof(LocalizedTextBox),typeof(LocalizedComboBox),typeof(LocalizedWindow) }) {
                var style = resources[type.BaseType] as Style;
                if (style != null) resources[type] = style;
            }
        }
        internal static void Refresh()
        {
            for (int i=Controls.Count-1;i>=0;i--) {
                var element = Controls[i].Target as FrameworkElement;
                if (element == null) { Controls.RemoveAt(i); continue; }
                element.CoerceValue(FrameworkElement.ToolTipProperty); element.CoerceValue(AutomationProperties.NameProperty);
                element.CoerceValue(AutomationProperties.ItemStatusProperty);
                if (element is TextBlock) element.CoerceValue(TextBlock.TextProperty);
                if (element is ContentControl) element.CoerceValue(ContentControl.ContentProperty);
                if (element is HeaderedContentControl) element.CoerceValue(HeaderedContentControl.HeaderProperty);
                if (element is MenuItem) element.CoerceValue(HeaderedItemsControl.HeaderProperty);
                if (element is Window) element.CoerceValue(Window.TitleProperty);
            }
        }
    }
    internal class LocalizedTextBlock : TextBlock { static LocalizedTextBlock() { UiL.Register(typeof(LocalizedTextBlock),TextProperty); } internal LocalizedTextBlock() { UiL.Track(this,typeof(TextBlock)); } }
    internal class LocalizedButton : Button { static LocalizedButton() { UiL.Register(typeof(LocalizedButton),ContentProperty); } internal LocalizedButton() { UiL.Track(this,typeof(Button)); } }
    internal class LocalizedCheckBox : CheckBox { static LocalizedCheckBox() { UiL.Register(typeof(LocalizedCheckBox),ContentProperty); } internal LocalizedCheckBox() { UiL.Track(this,typeof(CheckBox)); } }
    internal class LocalizedComboBoxItem : ComboBoxItem { static LocalizedComboBoxItem() { UiL.Register(typeof(LocalizedComboBoxItem),ContentProperty); } internal LocalizedComboBoxItem() { UiL.Track(this,typeof(ComboBoxItem)); } }
    internal class LocalizedMenuItem : MenuItem { static LocalizedMenuItem() { UiL.Register(typeof(LocalizedMenuItem),HeaderProperty); } internal LocalizedMenuItem() { UiL.Track(this,typeof(MenuItem)); } }
    internal class LocalizedBorder : Border { static LocalizedBorder() { UiL.Register(typeof(LocalizedBorder)); } internal LocalizedBorder() { UiL.Track(this,typeof(Border)); } }
    internal class LocalizedStackPanel : StackPanel { static LocalizedStackPanel() { UiL.Register(typeof(LocalizedStackPanel)); } internal LocalizedStackPanel() { UiL.Track(this,typeof(StackPanel)); } }
    internal class LocalizedGrid : Grid { static LocalizedGrid() { UiL.Register(typeof(LocalizedGrid)); } internal LocalizedGrid() { UiL.Track(this,typeof(Grid)); } }
    internal class LocalizedExpander : Expander { static LocalizedExpander() { UiL.Register(typeof(LocalizedExpander),HeaderProperty); } internal LocalizedExpander() { UiL.Track(this,typeof(Expander)); } }
    internal class LocalizedTextBox : TextBox { static LocalizedTextBox() { UiL.Register(typeof(LocalizedTextBox)); } internal LocalizedTextBox() { UiL.Track(this,typeof(TextBox)); } }
    internal class LocalizedComboBox : ComboBox { static LocalizedComboBox() { UiL.Register(typeof(LocalizedComboBox)); } internal LocalizedComboBox() { UiL.Track(this,typeof(ComboBox)); } }
    public class LocalizedWindow : Window { static LocalizedWindow() { UiL.Register(typeof(LocalizedWindow),TitleProperty); } public LocalizedWindow() { UiL.Track(this,typeof(Window)); } }
    internal static class LocalizedMessageBox
    {
        internal static MessageBoxResult Show(string text,string caption,MessageBoxButton button,MessageBoxImage image) { return MessageBox.Show(L.Text(text),L.Text(caption),button,image); }
        internal static MessageBoxResult Show(Window owner,string text,string caption,MessageBoxButton button,MessageBoxImage image) { return MessageBox.Show(owner,L.Text(text),L.Text(caption),button,image); }
        internal static MessageBoxResult Show(Window owner,string text,string caption,MessageBoxButton button,MessageBoxImage image,MessageBoxResult defaultResult) { return MessageBox.Show(owner,L.Text(text),L.Text(caption),button,image,defaultResult); }
    }
}
