using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DesktopBar.Services
{
    internal static class AwakeSession
    {
        private const uint Continue = 0x80000000, SystemRequired = 1;
        private static DispatcherTimer _expiry;
        private static DateTimeOffset? _deadline;
        internal static bool Enabled { get; private set; }
        internal static string Label => _deadline is DateTimeOffset end ? "保持唤醒至 " + end.ToLocalTime().ToString("HH:mm")
            : Enabled ? "保持唤醒 · 直到取消" : "保持唤醒";

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);

        internal static bool Start(TimeSpan? duration)
        {
            if (SetThreadExecutionState(Continue | SystemRequired) == 0) return false;
            Enabled = true;
            _deadline = duration.HasValue ? DateTimeOffset.UtcNow + duration.Value : null;
            _expiry?.Stop();
            if (duration.HasValue)
            {
                _expiry ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
                _expiry.Tick -= OnExpiry;
                _expiry.Tick += OnExpiry;
                _expiry.Start();
            }
            return true;
        }

        private static void OnExpiry(object sender, EventArgs args)
        {
            if (_deadline is DateTimeOffset end && DateTimeOffset.UtcNow >= end) Stop();
        }

        internal static void Stop()
        {
            if (Enabled) SetThreadExecutionState(Continue);
            _expiry?.Stop();
            _deadline = null;
            Enabled = false;
        }
    }
}
