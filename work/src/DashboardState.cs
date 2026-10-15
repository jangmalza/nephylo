using System;
using System.Linq;

namespace UsageWidget
{
    internal sealed class DashboardAssessmentState
    {
        // Counts describe metric rows, never a sum of unlike allowance units.
        // A connected provider without metric rows contributes one unknown item.
        internal int LowCount, UnknownCount, UsableCount, ResetCount;
        internal DateTimeOffset? NextReset;
        internal string StatusLabel;
        internal bool Stale;
        internal bool HasLow { get { return LowCount > 0; } }
        internal bool HasUnknown { get { return UnknownCount > 0; } }
    }

    internal static class DashboardAssessment
    {
        internal static DashboardAssessmentState Assess(ProviderView view, UsageTrackingOptions options, DateTimeOffset now)
        {
            var result = new DashboardAssessmentState { StatusLabel = "확인 불가" };
            if (view == null) { result.UnknownCount = 1; return result; }
            var presentation = UsagePresentation.Describe(view,now);
            result.StatusLabel = presentation.Label;
            var rows = view.Snapshot == null || view.Snapshot.Rows == null ? new UsageRow[0] : view.Snapshot.Rows.Where(row => row != null).ToArray();
            if (rows.Length == 0) { result.UnknownCount = 1; return result; }
            options = options ?? new UsageTrackingOptions();
            foreach (var row in rows) {
                if (!IsUsableRow(view,row,now)) {
                    result.UnknownCount++;
                    if (HasNumericBalance(row) && IsStaleRow(view,row,now)) result.Stale = true;
                    continue;
                }
                result.UsableCount++;
                if (IsLowRow(view,row,options,now)) result.LowCount++;
                if (row.ResetsAt.HasValue && row.ResetsAt.Value > now) {
                    if (!result.NextReset.HasValue || row.ResetsAt.Value < result.NextReset.Value) result.NextReset = row.ResetsAt;
                    if (row.ResetsAt.Value <= now.AddHours(24)) result.ResetCount++;
                }
            }
            // Invalid or absent measurements should not inherit a reassuring plan
            // name (for example PRO) from an otherwise successful response.
            if (result.HasUnknown && !presentation.Warning && UsageFreshness.Source(view) != "manual") result.StatusLabel = result.Stale ? "이전 값" : "확인 불가";
            return result;
        }

        internal static bool IsUsableRow(ProviderView view, UsageRow row, DateTimeOffset now)
        {
            if (view == null || row == null || !String.IsNullOrEmpty(view.Error) || !HasNumericBalance(row)
                || row.ResetsAt.HasValue && row.ResetsAt.Value <= now || (row.Label ?? "").Contains("수신 대기")
                || UsageFreshness.IsObservationStale(view,now)) return false;
            if (UsageFreshness.IsCode(view)) {
                var observed = UsageFreshness.ObservedAt(view);
                if (view.ReceiptState == ProviderReceiptState.Waiting || view.ReceiptState == ProviderReceiptState.QuotaUnavailable
                    || !observed.HasValue) return false;
            }
            // ResetWaiting is provider-wide, but only the expired metric is
            // invalid. A freshly received weekly allowance remains usable.
            return true;
        }

        internal static bool IsLowRow(ProviderView view, UsageRow row, UsageTrackingOptions options, DateTimeOffset now)
        {
            if (!IsUsableRow(view,row,now)) return false;
            options = options ?? new UsageTrackingOptions();
            // Same precedence and units as UsageJournal.Observe. Notification
            // delivery being disabled does not change what a low balance means.
            if (ValidPercent(row.RemainingPercent)) return row.RemainingPercent.Value <= Math.Max(1,Math.Min(99,options.LowThresholdPercent));
            double? threshold = view.Id == "higgsfield" ? options.HiggsfieldCreditsThreshold
                : view.Id == "higgsfield-api" ? options.HiggsfieldApiUsdThreshold : null;
            return ValidAmount(threshold) && ValidAmount(row.Remaining) && row.Remaining.Value <= threshold.Value;
        }

        internal static bool IsStaleRow(ProviderView view, UsageRow row, DateTimeOffset now)
        {
            if (view == null || row == null) return false;
            return !String.IsNullOrEmpty(view.Error) || row.ResetsAt.HasValue && row.ResetsAt.Value <= now || UsageFreshness.IsObservationStale(view,now);
        }
        private static bool HasNumericBalance(UsageRow row) { return ValidPercent(row.RemainingPercent) || ValidAmount(row.Remaining); }
        private static bool ValidPercent(double? value) { return ValidAmount(value) && value.Value <= 100; }
        private static bool ValidAmount(double? value) { return value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) && value.Value >= 0; }
    }
}
