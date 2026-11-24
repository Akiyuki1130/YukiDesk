using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopBar.Interop;
using DesktopBar.Models;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DesktopBar.UI
{
    internal sealed class BatteryModule : ModuleBase
    {
        private readonly TextBlock _value, _details;
        private readonly DispatcherTimer _refresh;
        internal static bool Available =>
            (Forms.SystemInformation.PowerStatus.BatteryChargeStatus & Forms.BatteryChargeStatus.NoSystemBattery) == 0
            && Forms.SystemInformation.PowerStatus.BatteryLifePercent is >= 0 and <= 1;

        internal BatteryModule(TabStyle style) : base(style)
        {
            _value = MakePillText("");
            _value.MinWidth = 75 * Ui.Scale;
            _value.TextAlignment = TextAlignment.Center;
            Pill.Child = _value;
            _details = MakeText("");
            var body = new StackPanel { MinWidth = 170 * Ui.Scale };
            body.Children.Add(MakePanelTitle("电池与供电"));
            body.Children.Add(_details);
            body.Children.Add(PanelControls.GlassButton("Windows 电源设置", () =>
                NativeMethods.ShellExecuteOpen("ms-settings:powersleep"), compact: true));
            Panel.Child = body;
            _refresh = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
            _refresh.Tick += (_, _) => Refresh();
            IsVisibleChanged += (_, _) =>
            {
                _refresh.IsEnabled = IsVisible;
                if (IsVisible) Refresh();
            };
            SystemEvents.PowerModeChanged += PowerChanged;
            Refresh();
        }

        private void PowerChanged(object sender, PowerModeChangedEventArgs args)
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(Refresh));
        }

        protected override void OnPanelShown() => Refresh();

        private void Refresh()
        {
            if (!IsVisible && !Open) return;
            var power = Forms.SystemInformation.PowerStatus;
            string value = power.BatteryLifePercent < 0 ? "--" : power.BatteryLifePercent.ToString("P0");
            string state = (power.BatteryChargeStatus & Forms.BatteryChargeStatus.Charging) != 0
                ? "正在充电" : power.PowerLineStatus == Forms.PowerLineStatus.Online ? "已接通电源" : "使用电池";
            if (GetSystemPowerStatus(out var status) && status.EnergySaver == 1) state += " · 节能";
            _value.Text = "电池 " + value;
            _details.Text = value + " · " + state;
        }

        public override void Dispose()
        {
            _refresh?.Stop();
            SystemEvents.PowerModeChanged -= PowerChanged;
            base.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerState
        {
            internal byte PowerLine, BatteryFlag, BatteryPercent, EnergySaver;
            internal uint BatteryLife, FullBatteryLife;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SystemPowerState status);
    }
}
