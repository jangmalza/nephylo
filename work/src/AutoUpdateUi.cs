using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace UsageWidget
{
    // Persist the attempt before shutdown so a failed installer cannot cause a restart loop.
    internal sealed class AutoUpdateAttempt
    {
        public string Version { get; set; }
        public DateTime AttemptUtc { get; set; }
        internal bool IsCoolingDown(string version, DateTime now)
        { return Version == version && AttemptUtc <= now.AddMinutes(5) && AttemptUtc > now.AddHours(-24); }
        internal static AutoUpdateAttempt Load()
        {
            try {
                string path = Path.Combine(SettingsStore.DataDirectory,"update-attempt.json");
                if (!File.Exists(path) || new FileInfo(path).Length > 4096) return new AutoUpdateAttempt();
                return new JavaScriptSerializer().Deserialize<AutoUpdateAttempt>(File.ReadAllText(path,Encoding.UTF8)) ?? new AutoUpdateAttempt();
            } catch { return new AutoUpdateAttempt(); }
        }
        internal void Save()
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            string path = Path.Combine(SettingsStore.DataDirectory,"update-attempt.json"), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(this),new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public partial class WidgetWindow
    {
        private readonly CancellationTokenSource updateLifetime = new CancellationTokenSource();
        private bool updateBusy, updateInitialized;
        private DateTime nextUpdateCheckUtc = DateTime.MaxValue;
        private AutoUpdateAttempt updateAttempt = new AutoUpdateAttempt();
        private PreparedUpdate preparedUpdate;
        private string updateStatus = "아직 새 버전을 확인하지 않았습니다.";
        private TextBlock updateStatusText;
        private Button updateCheckButton, updateInstallButton;

        private void InitializeAutoUpdates()
        {
            if (demo || updateInitialized) return;
            updateInitialized = true;
            updateAttempt = AutoUpdateAttempt.Load();
            nextUpdateCheckUtc = DateTime.UtcNow.AddSeconds(30);
            Task.Run(() => { try { AutoUpdateInstaller.CleanupOldStages(); } catch { } });
            try {
                var result = AutoUpdateInstaller.ReadLastResult();
                if (result != null && !result.Success && result.Code == "installation-complete")
                    SetUpdateStatus("업데이트 설치를 마쳤습니다. 새 버전의 시작 상태를 확인하고 있습니다.");
                else if (result != null && !result.Success)
                    SetUpdateStatus("지난 업데이트를 완료하지 못했습니다. 현재 버전을 계속 사용하며, 새 버전 확인에서 다시 시도할 수 있습니다.");
            } catch { }
        }

        private void StopAutoUpdates() { updateLifetime.Cancel(); }

        internal void ConfirmUpdateStartup()
        {
            if (updateStatus == "업데이트 설치를 마쳤습니다. 새 버전의 시작 상태를 확인하고 있습니다.")
                SetUpdateStatus("업데이트를 완료했습니다.");
        }

        private string PreparedUpdateStatus()
        {
            return settings.AutoUpdateEnabled ? "업데이트가 준비되었습니다. 관리 화면을 닫으면 자동으로 설치하고 다시 시작합니다." :
                "업데이트가 준비되었습니다. 지금 설치하고 다시 시작을 눌러 적용해 주세요.";
        }

        private async Task PollAutoUpdateAsync()
        {
            if (demo || exiting || updateBusy || !settings.AutoUpdateEnabled || !AutoUpdateInstaller.CanAutoInstall()) return;
            if (preparedUpdate != null) { await ApplyPreparedUpdateAsync(false); return; }
            if (DateTime.UtcNow >= nextUpdateCheckUtc) await CheckForUpdateAsync(false);
        }

        private void SetUpdateStatus(string message)
        {
            updateStatus = message;
            RefreshDashboardIfOpen();
            if (updateStatusText != null) updateStatusText.Text = message;
            if (updateCheckButton != null) updateCheckButton.IsEnabled = !updateBusy && !exiting;
            if (updateInstallButton != null) {
                updateInstallButton.Visibility = preparedUpdate != null ? Visibility.Visible : Visibility.Collapsed;
                updateInstallButton.IsEnabled = !updateBusy && !exiting;
            }
        }

        private async Task CheckForUpdateAsync(bool manual)
        {
            if (demo) { SetUpdateStatus("미리보기에서는 배포 서버에 연결하지 않습니다."); return; }
            if (exiting || updateBusy) return;
            if (preparedUpdate != null) { SetUpdateStatus(PreparedUpdateStatus()); return; }
            updateBusy = true;
            nextUpdateCheckUtc = DateTime.UtcNow.AddHours(6);
            SetUpdateStatus("새 버전을 확인하고 있습니다…");
            try {
                using (var client = new AutoUpdateClient()) {
                    var manifest = await client.CheckAsync(Assembly.GetExecutingAssembly().GetName().Version,updateLifetime.Token);
                    if (manifest == null) { SetUpdateStatus("최신 버전을 사용 중입니다."); return; }
                    if (!AutoUpdateInstaller.CanAutoInstall()) {
                        SetUpdateStatus("새 버전이 있습니다. 자동 설치를 사용하려면 Nephylo-Setup.exe로 한 번 설치해 주세요."); return;
                    }
                    if (!manual && updateAttempt.IsCoolingDown(manifest.VersionText,DateTime.UtcNow)) {
                        SetUpdateStatus("지난 업데이트를 완료하지 못했습니다. 현재 버전을 계속 사용하며, 새 버전 확인에서 다시 시도할 수 있습니다."); return;
                    }
                    if (!manual && !settings.AutoUpdateEnabled) return;
                    SetUpdateStatus("새 버전을 다운로드하고 검증하고 있습니다…");
                    preparedUpdate = await client.DownloadAsync(manifest,updateLifetime.Token);
                    SetUpdateStatus(PreparedUpdateStatus());
                }
            } catch (OperationCanceledException) {
                if (!exiting) SetUpdateStatus("업데이트를 확인하지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.");
            } catch {
                SetUpdateStatus("업데이트를 확인하지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.");
            } finally { updateBusy = false; SetUpdateStatus(updateStatus); }
            if (!manual && settings.AutoUpdateEnabled && !exiting) await ApplyPreparedUpdateAsync(false);
        }

        private bool UpdateCanRestart(bool manual)
        {
            if (exiting || refreshing || HasServiceConnectionWork || claudeConnectionBusy || loginPending || codexLoginOperation != null || higgsfieldOperation != null) return false;
            foreach (Window child in OwnedWindows) if (!manual || child != releaseWindow) return false;
            return true;
        }

        private async Task ApplyPreparedUpdateAsync(bool manual)
        {
            if (preparedUpdate == null || updateBusy || demo || !AutoUpdateInstaller.CanAutoInstall()) return;
            if (!manual && (!settings.AutoUpdateEnabled || updateAttempt.IsCoolingDown(preparedUpdate.Manifest.VersionText,DateTime.UtcNow))) return;
            if (!UpdateCanRestart(manual)) { SetUpdateStatus("업데이트가 준비되었습니다. 연결 작업을 마치고 다른 관리 화면을 닫아 주세요."); return; }
            updateBusy = true; exiting = true; timer.Stop(); IsEnabled = false;
            if (releaseWindow != null) releaseWindow.IsEnabled = false;
            SetUpdateStatus("업데이트를 설치한 뒤 자동으로 다시 시작합니다…");
            bool launched = false;
            try {
                // An automatic update never discards unsaved history or interrupts a recovery dialog.
                if (usageTrackingMemoryFallback && usageJournal != null && (usageJournal.GetHistory().Count > 0 || usageJournal.GetRecentNotices().Count > 0))
                    throw new IOException("History must be saved before updating.");
                await FlushUsageTrackingAsync();
                CaptureWidgetGeometry();
                SettingsStore.Save(settings);
                updateAttempt = new AutoUpdateAttempt { Version = preparedUpdate.Manifest.VersionText, AttemptUtc = DateTime.UtcNow };
                updateAttempt.Save();
                await Task.Run(() => AutoUpdateInstaller.StartPreparedUpdate(preparedUpdate));
                launched = true; shutdownFlushed = true;
                Close();
            } catch {
                // Do not repeatedly retry a save/helper failure every timer tick.
                updateAttempt = new AutoUpdateAttempt { Version = preparedUpdate.Manifest.VersionText, AttemptUtc = DateTime.UtcNow };
                SetUpdateStatus("업데이트를 시작하지 못했습니다. 현재 버전과 기록을 유지합니다. 저장 공간을 확인한 뒤 다시 시도해 주세요.");
            } finally {
                if (!launched) {
                    exiting = false; updateBusy = false; IsEnabled = true;
                    if (releaseWindow != null) releaseWindow.IsEnabled = true;
                    timer.Start(); SetUpdateStatus(updateStatus);
                }
            }
        }
    }
}
