using System;
using System.Windows;
using System.Windows.Controls;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        // Dashboard-only reading defaults. The compact widget and connection
        // editors intentionally keep their own density and dimensions.
        private const double DashboardSidebarWidth = 218;
        private static TextBlock DashboardText(string text, double size, string color, FontWeight weight, Thickness? margin = null)
        {
            size = Math.Max(13, size);
            var block = T(text, size, color == "#647070" ? "#566369" : color, weight, margin);
            block.LineHeight = Math.Ceiling(size * (size >= 24 ? 1.25 : 1.45));
            block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            if (size >= 24) System.Windows.Documents.Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
            return block;
        }

        private Button DashboardAction(string label, Action action, bool primary = false)
        {
            var button = ManagementAction(label, action, primary);
            button.FontSize = 14; button.MinHeight = 38;
            button.Padding = new Thickness(14, 9, 14, 9);
            return button;
        }
    }
}
