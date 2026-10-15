using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private enum UsageRecoveryChoice { Cancel, Retry, Backup, Discard }

        // The caller stops polling before entering this method. A failed save
        // must not trap the user in the app, or discard newer memory silently.
        private async Task<bool> TryFlushUsageTrackingForExitAsync()
        {
            while (true) {
                bool temporaryOnly = usageTrackingMemoryFallback && usageJournal != null && (usageJournal.GetHistory().Count > 0 || usageJournal.GetRecentNotices().Count > 0);
                if (!temporaryOnly) {
                    try { await FlushUsageTrackingAsync(); return true; }
                    catch { /* Offer recovery after the await, outside the catch for C# 5. */ }
                }
                UsageRecoveryChoice choice = ShowUsageSaveRecovery(!temporaryOnly);
                if (choice == UsageRecoveryChoice.Cancel) return false;
                if (choice == UsageRecoveryChoice.Discard) return true;
                if (choice == UsageRecoveryChoice.Retry) continue;
                var save = new SaveFileDialog {
                    Title = L.Text("종료 전 이력 백업"), Filter = L.Text("이력 백업 (*.json)|*.json"),
                    DefaultExt = ".json", AddExtension = true, OverwritePrompt = true,
                    FileName = "Nephylo-History-Backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"
                };
                if (save.ShowDialog(this) != true) continue;
                try {
                    string primaryPath = Path.Combine(SettingsStore.DataDirectory, "usage-journal.json");
                    if (String.Equals(Path.GetFullPath(save.FileName), Path.GetFullPath(primaryPath), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("기존 이력 파일과 다른 위치를 선택해 주세요.");
                    var journal = temporaryOnly ? usageJournal : persistentUsageJournal ?? usageJournal;
                    await Task.Run(() => journal.WriteRecoveryBackup(save.FileName));
                    return true;
                }
                catch {
                    LocalizedMessageBox.Show(this, "백업을 저장하지 못했습니다. 다른 저장 위치를 선택하거나 기존 파일의 사용 여부를 확인해 주세요.", "Nephylo", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private UsageRecoveryChoice ShowUsageSaveRecovery(bool canRetry)
        {
            var choice = UsageRecoveryChoice.Cancel;
            var dialog = CreateManagementDialog("이력 저장 확인", 560, 590);
            StackPanel footer;
            var body = ManagementBody(dialog, "이력을 저장하지 못했습니다", canRetry ? "저장 공간이나 폴더 권한을 확인해 주세요. 이전에 저장한 파일은 유지됩니다." : "저장 폴더를 열지 못해 이번 실행의 기록을 메모리에만 보관하고 있습니다. 종료 전에 다른 위치에 백업할 수 있습니다.", out footer);
            if (canRetry) {
                var retry = ManagementAction("저장 다시 시도", () => { choice = UsageRecoveryChoice.Retry; dialog.Close(); }, true);
                retry.HorizontalAlignment = HorizontalAlignment.Left; retry.Margin = new Thickness(0, 0, 0, 6);
                body.Children.Add(retry);
                body.Children.Add(ManagementDescription("문제를 해결했다면 현재 기록을 다시 저장합니다.", new Thickness(0, 0, 0, 20)));
            }
            var backup = ManagementAction("다른 위치에 백업 후 종료", () => { choice = UsageRecoveryChoice.Backup; dialog.Close(); });
            backup.HorizontalAlignment = HorizontalAlignment.Left; backup.Margin = new Thickness(0, 0, 0, 6);
            body.Children.Add(backup);
            body.Children.Add(ManagementDescription("현재 이력·알림 기록·설정을 JSON 파일로 보관합니다. API 키와 계정 정보는 포함하지 않습니다.", new Thickness(0, 0, 0, 20)));
            var discard = ManagementAction("저장하지 않고 종료", () => {
                if (LocalizedMessageBox.Show(dialog, "아직 저장되지 않은 이력·알림 기록·설정은 사라집니다. 기존 저장 파일은 유지됩니다. 종료할까요?", "저장하지 않고 종료", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes) {
                    choice = UsageRecoveryChoice.Discard; dialog.Close();
                }
            });
            discard.HorizontalAlignment = HorizontalAlignment.Left; discard.Margin = new Thickness(0, 0, 0, 6);
            body.Children.Add(discard);
            body.Children.Add(ManagementDescription("이번 실행에서 아직 저장하지 못한 변경만 포기합니다.", new Thickness(0, 0, 0, 0)));
            var cancel = ManagementAction("종료 취소", () => dialog.Close()); cancel.IsCancel = true; footer.Children.Add(cancel);
            dialog.ShowDialog();
            return choice;
        }
    }
}
