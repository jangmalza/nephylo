using System;
using System.Linq;
using System.Threading.Tasks;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private readonly ClaudeQuotaService claudeQuota = new ClaudeQuotaService();
        private Exception claudeQueryError;
        private bool claudeQueryRejectsReceipt;

        private async Task ReadClaude()
        {
            string distribution = settings.ClaudeWslDistribution, directory = settings.ClaudeWindowsConfigDirectory;
            string owner = ServiceReadingOwner;
            claudeView.Busy = true; RenderClaude();
            try {
                var result = await claudeQuota.ReadAsync(distribution, directory, ServiceConnectionsToken);
                if (!CanApplyServiceReading("claude", owner) || !IsSavedClaudeTarget(distribution, directory)) return;
                string source = String.IsNullOrEmpty(distribution) ? ClaudeUsageService.GetWindowsSourceId(directory) : ClaudeUsageService.GetSourceId(distribution);
                if (!ClaudeUsageService.CaptureJson(result.QuotaJson, result.ObservedAt, source, result.AccountFingerprint)) throw new InvalidOperationException("Claude 사용량 기록이 사용 중입니다. 잠시 후 다시 조회해 주세요.");
                claudeQueryError = null; claudeQueryRejectsReceipt = false;
                RefreshSucceeded(claudeView);
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                if (!CanApplyServiceReading("claude", owner) || !IsSavedClaudeTarget(distribution, directory)) return;
                claudeQueryError = error;
                string kind = error.Data["ClaudeQuotaKind"] as string;
                claudeQueryRejectsReceipt = kind == "authentication" || kind == "account" || kind == "unavailable";
                if (claudeQueryRejectsReceipt) ForgetAutomaticUsageService("claude");
                RefreshFailed(claudeView, error);
            } finally {
                claudeView.Busy = false;
                if (!exiting) { RenderClaude(); RefreshConnectionEditor("claude"); }
            }
        }

        private void ApplyClaudeQueryError()
        {
            if (claudeQueryError == null) return;
            var connection = claudeQueryError as ConnectionException;
            claudeView.Error = Friendly(claudeQueryError);
            claudeView.Issue = connection == null ? null : connection.Issue;
        }
    }
}
