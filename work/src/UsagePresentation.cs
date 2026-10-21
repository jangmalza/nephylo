using System;
using System.Linq;

namespace UsageWidget
{
    internal enum ProviderReceiptState { None, Waiting, QuotaUnavailable, Received, ResetWaiting }

    internal sealed class UsagePresentationState
    {
        internal string Label, Message;
        internal bool Warning, Stale;
        internal DateTimeOffset? ObservedAt;
    }

    // The normal card, compact view, and global status describe the same reading.
    // Receipt availability is explicit because an empty Claude snapshot is not a
    // connection failure and is distinct from waiting for the first response.
    internal static class UsagePresentation
    {
        internal static bool ShowBusy(ProviderView view)
        {
            return view != null && view.Busy && (!view.BackgroundRefresh || view.Snapshot == null
                || view.Snapshot.Rows == null || !view.Snapshot.Rows.Any(row => row != null));
        }

        internal static UsagePresentationState Describe(ProviderView view, DateTimeOffset now)
        {
            string source = UsageFreshness.Source(view);
            var result = new UsagePresentationState {
                Label = "연결 전", Message = "아직 확인된 사용량이 없습니다.",
                ObservedAt = UsageFreshness.ObservedAt(view)
            };
            if (!String.IsNullOrEmpty(view.Error)) {
                result.Label = view.Snapshot == null ? "연결 확인" : "이전 값";
                result.Message = view.Issue != null && !String.IsNullOrEmpty(view.Issue.Message) ? view.Issue.Message : view.Error;
                result.Warning = true; result.Stale = view.Snapshot != null; return result;
            }
            if (view.Snapshot == null && (view.Busy || view.AwaitingInitialRead)) {
                result.Label = view.Busy ? "갱신 중" : "조회 대기";
                result.Message = view.Busy ? "사용량을 조회하고 있습니다." : "첫 사용량 조회를 기다리고 있습니다.";
                return result;
            }
            bool hasRows = view.Snapshot != null && view.Snapshot.Rows != null && view.Snapshot.Rows.Any(row => row != null);
            bool expired = hasRows && view.Snapshot.Rows.Any(row => row != null && row.ResetsAt.HasValue && row.ResetsAt.Value <= now);
            if (view.Id == "claude" && source == "cli" && view.ReceiptState == ProviderReceiptState.ResetWaiting) expired = true;
            string note = view.Snapshot == null ? "" : view.Snapshot.Note ?? "";
            if (source == "manual") {
                result.Label = expired ? "수동 · 기한 경과" : hasRows ? "수동" : "수동 확인";
                result.Message = expired ? "수동으로 기록한 초기화 예정 시각이 지났습니다. 사용량을 확인하고 다시 기록하세요." : "수동 기록 · 자동 갱신 안 됨";
                result.Warning = result.Stale = expired; return result;
            }
            if (view.Id == "claude" && UsageFreshness.IsCode(view)) {
                if (view.ReceiptState == ProviderReceiptState.QuotaUnavailable) {
                    result.Label = "수신됨 · 한도 미제공";
                    result.Message = String.IsNullOrEmpty(view.ReceiptMessage) ? "Claude Code 데이터는 도착했지만 구독 한도는 없습니다. 연결 · 설정에서 지원 범위와 수동 기록을 확인하세요." : view.ReceiptMessage;
                    result.Warning = true; return WithRestrictions(result,note);
                }
                if (view.ReceiptState == ProviderReceiptState.Waiting || !hasRows) {
                    result.Label = "연결 대기";
                    result.Message = String.IsNullOrEmpty(view.ReceiptMessage) ? "Claude Code를 연결하거나 사용량을 수동 기록하세요." : view.ReceiptMessage;
                    result.Warning = true; return WithRestrictions(result,note);
                }
                bool waitingReset = view.ReceiptState == ProviderReceiptState.ResetWaiting || expired
                    || view.Snapshot.Rows.Any(row => row != null && (row.Label ?? "").Contains("수신 대기"));
                bool old = UsageFreshness.IsObservationStale(view,now);
                result.Label = waitingReset ? "초기화 후 수신 대기" : old ? "이전 수신" : "CODE 연결";
                result.Message = waitingReset || old ? "Claude Code에서 새 응답 후 갱신됩니다." : "";
                result.Warning = result.Stale = waitingReset || old; return WithRestrictions(result,note);
            }
            if (expired) {
                result.Label = "초기화 확인 필요";
                result.Message = "초기화 예정 시각이 지났습니다. 새로고침하여 실제 잔량을 확인하세요.";
                result.Warning = result.Stale = true; return WithRestrictions(result,note);
            }
            if (hasRows && UsageFreshness.IsObservationStale(view,now)) {
                result.Label = "이전 값";
                result.Message = "최근 조회값이 아닙니다. 새로고침하여 실제 잔량을 확인하세요.";
                result.Warning = result.Stale = true; return WithRestrictions(result,note);
            }
            if (hasRows) { result.Label = ShowBusy(view) ? "갱신 중" : view.Snapshot.Plan ?? "연결됨"; result.Message = ""; }
            return WithRestrictions(result,note);
        }

        private static UsagePresentationState WithRestrictions(UsagePresentationState state, string note)
        {
            if (note.Contains("이용 제한") || note.Contains("지출 한도 도달")) {
                if (!state.Warning) state.Label = "이용 제한";
                state.Message = String.IsNullOrEmpty(state.Message) ? note : state.Message + "\n" + note;
                state.Warning = true;
            }
            return state;
        }
    }
}
