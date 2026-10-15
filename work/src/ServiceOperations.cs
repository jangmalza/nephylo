using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        // UI-dispatcher owned. Reserve before awaiting so automatic reads cannot
        // slip in while a user-requested connection waits for its previous read.
        private readonly Dictionary<string, ServiceConnectionLease> serviceConnectionWork = new Dictionary<string, ServiceConnectionLease>();
        private readonly CancellationTokenSource serviceConnectionsLifetime = new CancellationTokenSource();
        private CancellationToken ServiceConnectionsToken { get { return serviceConnectionsLifetime.Token; } }
        private bool HasServiceConnectionWork { get { return serviceConnectionWork.Count != 0; } }
        private bool IsServiceConnectionPending(string id) { return id != null && serviceConnectionWork.ContainsKey(id); }
        private readonly string localReadingEpoch = Guid.NewGuid().ToString("N");
        private string ServiceReadingOwner { get { return localReadingEpoch; } }
        private bool CanApplyServiceReading(string id, string owner)
        { return !exiting && IsServiceActive(id) && String.Equals(owner, ServiceReadingOwner, StringComparison.Ordinal); }

        private sealed class ServiceConnectionLease : IDisposable
        {
            internal WidgetWindow Window;
            internal string ServiceId, AnalysisOwner, FreeService;
            internal bool Pro;
            public void Dispose()
            {
                var window = Window;
                if (window == null) return;
                Window = null;
                ServiceConnectionLease current;
                if (window.serviceConnectionWork.TryGetValue(ServiceId, out current) && Object.ReferenceEquals(current, this))
                    window.serviceConnectionWork.Remove(ServiceId);
                if (!window.exiting) window.RequestAutomaticRefresh();
            }
        }

        private bool TryReserveServiceConnection(string id, out IDisposable lease)
        {
            lease = null;
            if (exiting || serviceConnectionsLifetime.IsCancellationRequested || !ServicePlan.IsSupported(id) || IsServiceConnectionPending(id)) return false;
            var reservation = new ServiceConnectionLease {
                Window = this, ServiceId = id,
                AnalysisOwner = ServiceReadingOwner,
                Pro = ProFeaturesAvailable,
                FreeService = servicePlan == null ? "" : servicePlan.ActiveServiceId
            };
            serviceConnectionWork.Add(id, reservation);
            lease = reservation;
            return true;
        }

        private async Task WaitForServiceRefreshAsync(string id, CancellationToken cancellationToken)
        {
            if (!IsServiceConnectionPending(id)) throw new InvalidOperationException("Service connection must be reserved before waiting.");
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ServiceConnectionsToken)) {
                while (true) {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (exiting) throw new OperationCanceledException();
                    var view = ViewFor(id);
                    if (view == null || !view.Busy) return;
                    await Task.Delay(50, cancellation.Token);
                }
            }
        }

        private void ThrowIfServiceConnectionChanged(string id, IDisposable lease)
        {
            var reservation = lease as ServiceConnectionLease;
            ServiceConnectionLease current;
            if (exiting || serviceConnectionsLifetime.IsCancellationRequested) throw new OperationCanceledException();
            if (reservation == null || reservation.Window != this || reservation.ServiceId != id ||
                !serviceConnectionWork.TryGetValue(id, out current) || !Object.ReferenceEquals(current, reservation))
                throw new InvalidOperationException("연결 작업이 끝났습니다. 다시 연결을 확인해 주세요.");
            if (reservation.Pro != ProFeaturesAvailable ||
                !String.Equals(reservation.AnalysisOwner, ServiceReadingOwner, StringComparison.Ordinal) ||
                !reservation.Pro && !String.Equals(reservation.FreeService, servicePlan == null ? "" : servicePlan.ActiveServiceId, StringComparison.Ordinal))
                throw new InvalidOperationException("계정 또는 사용 중인 서비스가 변경되었습니다. 현재 선택을 유지했으니 연결을 다시 확인해 주세요.");
        }

        private void StopServiceConnections() { serviceConnectionsLifetime.Cancel(); }
    }
}
