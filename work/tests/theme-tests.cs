using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

// In-process theme regression checks. Demo mode and isolated storage prevent live
// account access. No window is shown, connection action invoked or API queried.
internal static class ThemeTests
{
    private static int assertions;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static T Field<T>(WidgetWindow window, string name)
    {
        return (T)typeof(WidgetWindow).GetField(name, PrivateInstance).GetValue(window);
    }

    private static object Call(WidgetWindow window, string name, params object[] args)
    {
        try { return typeof(WidgetWindow).GetMethod(name, PrivateInstance).Invoke(window, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        var match = parent as T;
        if (match != null) yield return match;
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            var item = child as DependencyObject;
            if (item == null) continue;
            foreach (var found in Descendants<T>(item)) yield return found;
        }
    }

    private static Color ColorOf(Brush brush)
    {
        var solid = brush as SolidColorBrush;
        Check(solid != null, "Theme surface must expose a solid color");
        return solid.Color;
    }

    private static double Channel(byte value)
    {
        double c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Contrast(Brush one, Brush two)
    {
        Color a = ColorOf(one), b = ColorOf(two);
        double l1 = 0.2126 * Channel(a.R) + 0.7152 * Channel(a.G) + 0.0722 * Channel(a.B);
        double l2 = 0.2126 * Channel(b.R) + 0.7152 * Channel(b.G) + 0.0722 * Channel(b.B);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static SolidColorBrush DrawingInk(DrawingImage image)
    {
        return ((DrawingGroup)image.Drawing).Children.OfType<GeometryDrawing>()
            .Select(drawing => drawing.Brush as SolidColorBrush).First(brush => brush != null && brush.Color.A != 0);
    }

    private static void CheckSharedBrushesAndDrawings()
    {
        WidgetPalette.SetTheme("light");
        var paper = WidgetPalette.Brush("#FAFAF7");
        var ink = WidgetPalette.Brush("#2448F5");
        Color originalPaper = paper.Color, originalInk = ink.Color;
        var icons = new[] { "codex", "eleven", "openai", "claude", "gemini", "cursor" }
            .Select(id => (DrawingImage)ServiceIcons.Get(id)).ToArray();
        var wordmark = PaperDetails.Wordmark();
        var divider = (DrawingBrush)PaperDetails.Rule();
        var dividerInk = (SolidColorBrush)((GeometryDrawing)divider.Drawing).Brush;
        Color originalDivider = dividerInk.Color;
        Check(!WidgetPalette.IsDark, "Light mode reports its state accurately");
        Check(Object.ReferenceEquals(paper, WidgetPalette.Brush("#FAFAF7")), "Repeated color lookup returns the same live brush");
        Check(icons.All(icon => !icon.IsFrozen) && !wordmark.IsFrozen && !divider.IsFrozen,
            "Cached vector assets remain mutable after construction");
        WidgetPalette.SetTheme("dark");
        Check(WidgetPalette.IsDark && paper.Color != originalPaper && ink.Color != originalInk,
            "Dark mode changes the original cached brush objects");
        Check(!paper.IsFrozen && !ink.IsFrozen, "Shared palette brushes stay mutable after switching");
        Check(paper.Color.R <= 32 && paper.Color.G <= 32 && paper.Color.B <= 32,
            "Dark mode uses the requested near-black background");
        foreach (DrawingImage icon in icons)
            Check(DrawingInk(icon).Color == ink.Color, "Previously created service icon follows the active accent");
        Check(DrawingInk(wordmark).Color == ink.Color, "Previously created pixel wordmark follows the active accent");
        Check(dividerInk.Color != originalDivider, "Previously created dotted divider follows the active theme");
        WidgetPalette.SetTheme("light");
        Check(paper.Color == originalPaper && ink.Color == originalInk && dividerInk.Color == originalDivider,
            "Returning to light restores the existing paper palette exactly");
        Check(icons.All(icon => DrawingInk(icon).Color == originalInk) && DrawingInk(wordmark).Color == originalInk,
            "Cached icons and wordmark restore their original colors without regeneration");
    }

    private static void CheckStyledControls(WidgetWindow window)
    {
        WidgetPalette.SetTheme("light");
        var host = new StackPanel { Resources = window.Resources };
        var root = (Grid)Field<Border>(window,"normalWidgetRoot").Child;
        root.Children.Add(host);
        var button = new Button { Content = "연결 확인", Style = (Style)window.Resources[typeof(Button)] };
        var combo = new ComboBox { Style = (Style)window.Resources[typeof(ComboBox)] };
        combo.Items.Add("유지할 서비스"); combo.SelectedIndex = 0;
        var check = new CheckBox { Content = "유지할 설정", IsChecked = true, Style = (Style)window.Resources[typeof(CheckBox)] };
        var draft = new TextBox { Text = "작성 중인 값", Style = (Style)window.Resources[typeof(TextBox)] };
        var password = new PasswordBox { Password = "synthetic-draft-only", Style = (Style)window.Resources[typeof(PasswordBox)] };
        foreach (Control control in new Control[] { button, combo, check, draft, password }) host.Children.Add(control);
        host.Measure(new Size(360, 800)); host.Arrange(new Rect(0, 0, 360, 800));
        button.ApplyTemplate(); combo.ApplyTemplate(); check.ApplyTemplate();
        var box = (Border)check.Template.FindName("Box", check);
        Check(box != null, "Real check box template is instantiated without opening a window");
        Color buttonLight = ColorOf(button.Background), comboLight = ColorOf(combo.Background);
        Color checkLight = ColorOf(box.Background), inputLight = ColorOf(draft.Background);
        Color windowLight = ColorOf(window.Background);
        WidgetPalette.SetTheme("dark");
        Check(ColorOf(window.Background) != windowLight, "Already constructed main window follows dark mode");
        Check(ColorOf(button.Background) != buttonLight && ColorOf(combo.Background) != comboLight,
            "Sealed button and combo box styles update on theme switch");
        Check(ColorOf(box.Background) != checkLight, "Active checked-state template trigger follows the dark palette");
        Check(ColorOf(draft.Background) != inputLight && ColorOf(password.Background) == ColorOf(draft.Background),
            "Text and protected key input backgrounds switch together");
        Check(Contrast(button.Foreground, button.Background) >= 4.5 && Contrast(combo.Foreground, combo.Background) >= 4.5,
            "Dark button and combo text remain readable against their surfaces");
        Check(Contrast(draft.Foreground, draft.Background) >= 4.5 && Contrast(password.Foreground, password.Background) >= 4.5,
            "Dark text and key input text remain readable");
        Check(draft.Text == "작성 중인 값" && password.Password == "synthetic-draft-only"
            && combo.SelectedIndex == 0 && check.IsChecked == true,
            "Switching themes preserves drafts, protected key input, service selection and check state");
        WidgetPalette.SetTheme("light");
        Check(ColorOf(button.Background) == buttonLight && ColorOf(combo.Background) == comboLight
            && ColorOf(box.Background) == checkLight && ColorOf(draft.Background) == inputLight,
            "Existing sealed styles and checked template restore the light colors");
        Check(draft.Text == "작성 중인 값" && password.Password == "synthetic-draft-only",
            "Switching back does not discard unfinished input");
        root.Children.Remove(host);
    }

    private static void CheckConnectionDrafts(WidgetWindow window)
    {
        foreach (string id in new[] { "eleven", "openai" })
        {
            var editor = new StackPanel { Resources = window.Resources };
            Call(window, "BuildApiKeySettings", editor, id);
            var key = Descendants<PasswordBox>(editor).Single();
            key.Password = "synthetic-unsaved-" + id;
            var fields = Descendants<TextBox>(editor).ToArray();
            for (int index = 0; index < fields.Length; index++) fields[index].Text = "draft-" + index;
            WidgetPalette.SetTheme("dark"); WidgetPalette.SetTheme("light");
            Check(key.Password == "synthetic-unsaved-" + id, id + " real connection editor preserves unsaved protected input");
            Check(fields.Select((field, index) => field.Text == "draft-" + index).All(value => value),
                id + " real connection editor preserves optional unfinished fields");
        }
    }

    private static void CheckParsedResources()
    {
        WidgetPalette.SetTheme("light");
        var resources = WidgetPalette.ParseResources(@"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
            <Style x:Key='Probe' TargetType='Border'><Setter Property='Background' Value='#E2E7F5'/><Setter Property='BorderBrush' Value='#2448F5'/></Style>
        </ResourceDictionary>");
        var border = new Border { Resources = resources, Style = (Style)resources["Probe"] };
        Color light = ColorOf(border.Background);
        Check(border.Style.IsSealed, "Parsed theme style is sealed by its first real consumer");
        WidgetPalette.SetTheme("dark");
        Check(ColorOf(border.Background) != light, "Parsed literal color remains theme-aware after style sealing");
        Check(ColorOf(border.BorderBrush) == WidgetPalette.Brush("#2448F5").Color, "Parsed border and code-created accent stay synchronized");
        WidgetPalette.SetTheme("light");
        Check(ColorOf(border.Background) == light, "Parsed style restores its exact light surface");
    }

    [STAThread]
    public static int Main()
    {
        string parent = Path.GetFullPath(Path.GetDirectoryName(typeof(ThemeTests).Assembly.Location));
        string isolated = Path.Combine(parent, "theme-test-data-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\theme-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated, "claude-config");
        WidgetWindow window = null;
        try
        {
            window = new WidgetWindow(true);
            Field<DispatcherTimer>(window, "timer").Stop();
            Field<Forms.NotifyIcon>(window, "tray").Visible = false;
            Call(window, "ShowDemo");
            Check(!window.IsLoaded && !window.IsVisible, "Theme fixture never shows or loads an application window");
            CheckSharedBrushesAndDrawings();
            CheckStyledControls(window);
            CheckConnectionDrafts(window);
            CheckParsedResources();
            Check(!window.IsLoaded && !window.IsVisible && !Directory.Exists(isolated),
                "Theme switching never starts live UI, persists credentials or writes settings in demo mode");
            Console.WriteLine("Theme tests passed: " + assertions + " assertions. No live APIs, credentials or visible application windows were used.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Theme test failed: " + ex);
            return 1;
        }
        finally
        {
            if (window != null)
            {
                Field<DispatcherTimer>(window, "timer").Stop();
                window.Close();
                Field<CodexService>(window, "codex").Dispose();
                Field<Forms.NotifyIcon>(window, "tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null;
            SettingsStore.StartupRegistrySubKeyOverride = null;
            ClaudeUsageService.ConfigDirectoryOverride = null;
            string full = Path.GetFullPath(isolated);
            if (full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("theme-test-data-", StringComparison.Ordinal) && Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }
}
