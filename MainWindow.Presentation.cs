using System;
using System.Linq;
using DesktopBar.Services;
using DesktopBar.UI;
using Microsoft.Win32;

namespace DesktopBar
{
    public partial class MainWindow
    {
        private bool _sessionLocked, _systemSuspended;
        private bool PresentationSuspended => _sessionLocked || _systemSuspended;

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
        {
            if (Dispatcher.HasShutdownStarted) return;
            if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)
                Dispatcher.BeginInvoke(new Action(() => SetSessionLocked(args.Reason == SessionSwitchReason.SessionLock)));
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
        {
            if (Dispatcher.HasShutdownStarted) return;
            if (args.Mode is PowerModes.Suspend or PowerModes.Resume)
                Dispatcher.BeginInvoke(new Action(() => SetSystemSuspended(args.Mode == PowerModes.Suspend)));
        }

        private void SetSessionLocked(bool locked)
        {
            bool previous = PresentationSuspended;
            _sessionLocked = locked;
            ApplyPresentationPause(previous);
        }

        private void SetSystemSuspended(bool suspended)
        {
            bool previous = PresentationSuspended;
            _systemSuspended = suspended;
            ApplyPresentationPause(previous);
        }

        private void ApplyPresentationPause(bool previous)
        {
            bool suspended = PresentationSuspended;
            if (_closing || suspended == previous) return;
            _reveal.SetSuspended(suspended);
            if (suspended) ClosePanels();
            else if (IsVisible) PinToDesktopLayer();

            UpdatePresentationTimers();
            if (_appSceneTimer != null) _appSceneTimer.IsEnabled = !suspended && S.AppScenes.Count > 0;
            foreach (var clock in _modules.OfType<ClockModule>()) clock.SetPresentationDemand(!suspended);
            RefreshModuleDemandNow();
        }

        private void UpdatePresentationTimers()
        {
            if (_closing) return;
            bool active = IsVisible && !PresentationSuspended;
            _pinTimer.IsEnabled = active && Environment.GetEnvironmentVariable("YUKI_NO_REPIN") != "1";
            _moduleDemandTimer.IsEnabled = active;
            GlassService.SetActive(active && !IsOnSecondaryScreen);
        }
    }
}
