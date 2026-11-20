using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using DesktopBar.Interop;
using DesktopBar.Models;
using DesktopBar.UI;

namespace DesktopBar.Services
{
    public static class ThemeService
    {
        public static event Action Changed;
        private static bool _started, _stopped, _checkingWallpaper, _wallpaperBright;
        private static DispatcherTimer _wallpaperTimer;
        private static volatile WallpaperMonitor.Snapshot _wallpaper;
        internal static WallpaperMonitor.Snapshot CurrentWallpaper => _wallpaper;
        private static double _wallpaperBarHeight;
        private static Appearance _appearance;

        private sealed record Appearance(string Background, string Text, double Opacity,
            string Border, double BorderOpacity, double BorderThickness, bool Automatic);

        public static string Background => _appearance?.Background ?? "#14141C";
        public static string Text => _appearance?.Text ?? "#FFFFFF";
        public static string Border => App.Settings?.BorderColor ?? "#FFFFFF";

        internal static bool FollowsBackground(AppSettings settings) => settings != null
            && (settings.FollowCapsuleBackground
                ?? (settings.AutoTheme || (settings.PillMaterial == 4 && settings.GlassStyle < 2)));

        private static bool NeedsWallpaper(AppSettings settings) =>
            FollowsBackground(settings) || settings?.PillMaterial is 3 or 4;

        internal static (string Background, string Text) WallpaperTheme(bool bright) =>
            bright ? ("#111118", "#FFFFFF") : ("#F6F7FB", "#171821");

        public static void Start()
        {
            if (_started || _stopped) return;
            _started = true;
            _wallpaperTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _wallpaperTimer.Tick += (sender, args) => CheckWallpaper();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Refresh();
        }

        public static void Stop()
        {
            if (!_started) return;
            _stopped = true;
            _wallpaperTimer.Stop();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            Changed = null;
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.Color or UserPreferenceCategory.General)
                RequestWallpaperCheck();
        }

        private static void OnDisplaySettingsChanged(object sender, EventArgs e) => RequestWallpaperCheck();

        private static void RequestWallpaperCheck()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CheckWallpaper));
        }

        private static async void CheckWallpaper()
        {
            if (_stopped || _checkingWallpaper || !NeedsWallpaper(App.Settings)) return;
            _checkingWallpaper = true;
            try
            {
                var previous = _wallpaper;
                double barHeight = 46 * Ui.Scale * NativeMethods.SystemDpiScale();
                bool resized = barHeight != _wallpaperBarHeight;
                var (state, lightness) = await Task.Run(() =>
                {
                    var current = WallpaperMonitor.Read();
                    return (current, resized || current != previous ? WallpaperMonitor.SampleTop(current, barHeight) : null);
                });
                if (_stopped || !NeedsWallpaper(App.Settings)) return;
                bool wallpaperChanged = state != _wallpaper;
                _wallpaper = state;
                _wallpaperBarHeight = barHeight;
                if (lightness.HasValue)
                {
                    _wallpaperBright = lightness.Value > (_wallpaperBright ? 0.50 : 0.56);
                    Refresh();
                }
                if (wallpaperChanged)
                {
                    GlassService.WallpaperChanged();
                    App.Log("主题:主屏壁纸已更新");
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                or System.Runtime.InteropServices.ExternalException or ArgumentException) { }
            finally { _checkingWallpaper = false; }
        }

        public static void Refresh()
        {
            if (_stopped) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(Refresh));
                return;
            }
            var settings = App.Settings;
            if (settings == null) return;

            bool automatic = FollowsBackground(settings);
            bool watchWallpaper = NeedsWallpaper(settings);
            if (_wallpaperTimer != null && _wallpaperTimer.IsEnabled != watchWallpaper)
            {
                _wallpaper = null;
                if (watchWallpaper)
                {
                    _wallpaperTimer.Start();
                    CheckWallpaper();
                }
                else _wallpaperTimer.Stop();
            }

            var (background, text) = automatic ? WallpaperTheme(_wallpaperBright) : (settings.ThemeColor, settings.ThemeTextColor);
            var appearance = new Appearance(background, text, settings.ThemeOpacity,
                settings.BorderEnabled ? settings.BorderColor : null, settings.BorderOpacity, settings.BorderThickness, automatic);
            if (appearance == _appearance) return;
            _appearance = appearance;

            var resources = Application.Current?.Resources;
            if (resources != null)
            {
                resources["ThemePanelBackground"] = Ui.Brush(background, settings.ThemeOpacity);
                resources["ThemePanelBorder"] = appearance.Border != null ? Ui.Brush(appearance.Border, appearance.BorderOpacity) : Brushes.Transparent;
                resources["ThemePanelSubtle"] = Ui.Brush(text, 0.06);
                resources["ThemePanelControl"] = Ui.Brush(text, 0.10);
                resources["ThemePanelControlHover"] = Ui.Brush(text, 0.18);
                resources["ThemePanelHover"] = Ui.Brush(text, 0.12);
                resources["ThemePanelSelected"] = Ui.Brush(text, 0.20);
                resources["ThemePanelText"] = Ui.Brush(text);
            }
            Changed?.Invoke();
        }
    }
}
