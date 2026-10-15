using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        // Browser-login state belongs to the widget, not to a disposable management
        // window. These two editors hold no API/password input. Retaining them also
        // retains the result and uncommitted workspace selection after an async login.
        private sealed class RetainedConnectionEditor
        {
            internal StackPanel Body;
            internal Expander Order;
            internal Action Refresh;
        }

        private readonly Dictionary<string,RetainedConnectionEditor> retainedConnectionEditors = new Dictionary<string,RetainedConnectionEditor>();

        private void RetainConnectionEditor(string id, StackPanel body, Expander order)
        {
            if (id != "codex" && id != "higgsfield") return;
            Action refresh;
            connectionStateRefreshers.TryGetValue(id,out refresh);
            retainedConnectionEditors[id] = new RetainedConnectionEditor { Body = body, Order = order, Refresh = refresh };
        }

        private bool RestoreConnectionEditor(string id, out StackPanel body, out Expander order)
        {
            RetainedConnectionEditor saved;
            body = null; order = null;
            if (!retainedConnectionEditors.TryGetValue(id,out saved)) return false;
            body = saved.Body; order = saved.Order;
            if (saved.Refresh != null) connectionStateRefreshers[id] = saved.Refresh;
            return true;
        }

        private void DetachServiceEditors(Dictionary<string,StackPanel> editors, StackPanel body)
        {
            // Pending callbacks may finish after close. Detach before another window
            // attaches retained content and clear every secret input synchronously.
            foreach (var editor in editors.Values) ClearSecretInputs(editor);
            body.Children.Clear(); editors.Clear(); connectionStateRefreshers.Clear();
            if (exiting) retainedConnectionEditors.Clear();
        }
    }
}
