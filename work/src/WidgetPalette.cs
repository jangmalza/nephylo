using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Data;

namespace UsageWidget
{
    // UI-thread palette. Existing controls and drawings retain the same brushes,
    // so switching appearance cannot discard an editor's unsaved input.
    internal static class WidgetPalette
    {
        // Keep the original resource keys stable while using monochrome ink.
        private static readonly Dictionary<string,string> Light = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            { "#252827", "#1B1B1B" }, { "#475256", "#484848" },
            { "#566369", "#585858" }, { "#647070", "#686868" },
            { "#2448F5", "#111111" }, { "#173AD2", "#000000" },
            { "#EEF1FC", "#F0F0ED" }, { "#CBD5F4", "#C7C7C3" },
            { "#E9EDFF", "#E9E9E5" }, { "#DCE3FF", "#DCDCD8" },
            { "#F0F3FA", "#F0F0ED" }, { "#D5DDEA", "#D5D5D0" },
            { "#7E8983", "#838383" }, { "#C8CECF", "#CBCBC7" },
            { "#D6D9D7", "#D6D6D2" }, { "#ADB7B9", "#AFAFAF" },
            { "#9FABB2", "#A3A3A3" }, { "#E2E7F5", "#E2E2DE" },
            { "#B4C0E4", "#BBBBB8" }, { "#9D511C", "#111111" },
            { "#91501F", "#3C3C3C" }, { "#C9A987", "#858585" }
        };
        private static readonly Dictionary<string,string> Dark = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            { "#FAFAF7", "#101113" }, { "#FBFBF8", "#1B1B1B" },
            { "#FFFFFF", "#181818" }, { "#F2F3EF", "#202020" },
            { "#252827", "#F1F1F1" }, { "#475256", "#C5C5C5" },
            { "#566369", "#BBBBBB" }, { "#647070", "#AAAAAA" },
            { "#2448F5", "#FFFFFF" }, { "#173AD2", "#FFFFFF" },
            { "#EEF1FC", "#242424" }, { "#CBD5F4", "#555555" },
            { "#E9EDFF", "#303030" }, { "#DCE3FF", "#3D3D3D" },
            { "#F0F3FA", "#202020" }, { "#D5DDEA", "#474747" },
            { "#7E8983", "#858585" }, { "#C8CECF", "#383838" },
            { "#D6D9D7", "#4B4B4B" }, { "#ADB7B9", "#6B6B6B" },
            { "#9FABB2", "#666666" }, { "#E2E7F5", "#303030" },
            { "#B4C0E4", "#6C6C6C" }, { "#9D511C", "#FFFFFF" },
            { "#91501F", "#DEDEDE" }, { "#C9A987", "#999999" }
        };
        private static readonly Dictionary<string,SolidColorBrush> Brushes = new Dictionary<string,SolidColorBrush>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string,PaletteColor> Colors = new Dictionary<string,PaletteColor>(StringComparer.OrdinalIgnoreCase);
        private sealed class PaletteColor : DependencyObject
        {
            public static readonly DependencyProperty ColorProperty = DependencyProperty.Register("Color",typeof(Color),typeof(PaletteColor));
            public Color Color { get { return (Color)GetValue(ColorProperty); } set { SetValue(ColorProperty,value); } }
        }
        internal static bool IsDark { get; private set; }
        internal static event EventHandler Changed;

        internal static SolidColorBrush Brush(string lightHex)
        {
            SolidColorBrush brush;
            if (!Brushes.TryGetValue(lightHex,out brush)) {
                var source = new PaletteColor { Color = Resolve(lightHex) };
                brush = new SolidColorBrush();
                // A binding also prevents WPF from freezing a brush when sealing
                // a style that happens to share it with a runtime-created control.
                BindingOperations.SetBinding(brush,SolidColorBrush.ColorProperty,new Binding("Color") { Source = source, Mode = BindingMode.OneWay });
                Colors.Add(lightHex,source);
                Brushes.Add(lightHex,brush);
            }
            return brush;
        }

        private static Color Resolve(string lightHex)
        {
            string value;
            if (!(IsDark ? Dark : Light).TryGetValue(lightHex,out value)) value = lightHex;
            return (Color)ColorConverter.ConvertFromString(value);
        }

        internal static void SetTheme(string theme)
        {
            IsDark = !String.Equals(theme,"light",StringComparison.OrdinalIgnoreCase);
            foreach (var item in Colors) item.Value.Color = Resolve(item.Key);
            var changed = Changed;
            if (changed != null) changed(null,EventArgs.Empty);
        }

        internal static ResourceDictionary ParseResources(string xaml)
        {
            var colors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string themed = Regex.Replace(xaml,"#[0-9A-Fa-f]{6}",match => {
                string color = match.Value.ToUpperInvariant(); colors.Add(color);
                return "{DynamicResource Palette" + color.Substring(1) + "}";
            });
            var resources = (ResourceDictionary)XamlReader.Parse(themed);
            foreach (string color in colors) resources["Palette" + color.Substring(1)] = Brush(color);
            return resources;
        }
    }
}
