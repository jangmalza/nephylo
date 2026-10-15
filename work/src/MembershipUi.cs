using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private Border membershipStatusNotice;
        private TextBlock membershipStatusTitle, membershipStatusDetail;
        private Button membershipStatusRetry;
        private bool profileRecoveryNoticeVisible;
        private string profileRecoveryNoticeError;

        // Nephylo is accountless. Never load/revoke an old session or consult
        // a server to enable local features. Keep existing session files intact.
        private void InitializeMembership(MembershipClient supplied) { }
        private void AttachMembershipActivation(Window window) { }
        private Task RestoreMembershipAsync() { return Task.FromResult(0); }
        private Task CheckMembershipAsync() { return Task.FromResult(0); }
        private Task RecoverMembershipAfterNetworkAsync() { return Task.FromResult(0); }
        private Task RefreshMembershipOnActivationAsync() { return Task.FromResult(0); }
        private void ApplyMembershipState() { RefreshMembershipStatusNotice(); }
        private void OpenAccount() { OpenPlan(); }

        // Profile recovery is independent from the retired membership system.
        private FrameworkElement BuildMembershipStatusNotice()
        {
            var content = new LocalizedStackPanel();
            membershipStatusTitle = T("",11,"#252827",FontWeights.SemiBold);
            membershipStatusDetail = T("",10,"#647070",FontWeights.Normal,new Thickness(0,4,0,5));
            membershipStatusRetry = SmallAction("확인",async () => await RecheckMembershipFromNoticeAsync());
            content.Children.Add(membershipStatusTitle); content.Children.Add(membershipStatusDetail); content.Children.Add(membershipStatusRetry);
            membershipStatusNotice = new LocalizedBorder { Child=content, BorderBrush=BrushOf("#D6D9D7"), BorderThickness=new Thickness(1), Padding=new Thickness(9), Margin=new Thickness(0,0,0,9), Visibility=Visibility.Collapsed };
            System.Windows.Automation.AutomationProperties.SetName(membershipStatusNotice,"일부 기록 복구 안내");
            RefreshMembershipStatusNotice();
            return membershipStatusNotice;
        }

        private void RefreshMembershipStatusNotice()
        {
            if (membershipStatusNotice == null) return;
            string recovered = demo ? null : WidgetDataProfile.ReadRecoveryNoticeFrom(SettingsStore.DataDirectory, L.Language == "en");
            profileRecoveryNoticeVisible = !String.IsNullOrEmpty(recovered);
            membershipStatusTitle.Text = profileRecoveryNoticeVisible ? "일부 기록 복구 안내" : "";
            membershipStatusDetail.Text = profileRecoveryNoticeVisible ? profileRecoveryNoticeError ?? recovered : "";
            membershipStatusRetry.Content = "확인";
            membershipStatusNotice.Visibility = profileRecoveryNoticeVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private Task RecheckMembershipFromNoticeAsync()
        {
            if (!exiting && !demo && profileRecoveryNoticeVisible) {
                try { WidgetDataProfile.AcknowledgeRecoveryNoticeAt(SettingsStore.DataDirectory); profileRecoveryNoticeError = null; }
                catch { profileRecoveryNoticeError = "복구 안내를 닫지 못했습니다. 설정 폴더의 파일 권한을 확인한 뒤 다시 시도해 주세요."; }
                RefreshMembershipStatusNotice();
            }
            return Task.FromResult(0);
        }
    }
}
