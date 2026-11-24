using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopBar.Interop;
using DesktopBar.Services;
using DesktopBar.UI;

namespace DesktopBar
{
    public partial class MainWindow
    {
        private readonly List<BarLayoutPanel.Entry> _staticEntries = new();
        private readonly List<BarLayoutPanel.Entry> _folderEntries = new();
        private readonly List<BarLayoutPanel.Entry> _favoriteEntries = new();
        private BarLayoutPanel _barLayout;
        private FrameworkElement _barGlassHost;
        internal bool IsOnSecondaryScreen { get; private set; }

        internal void PlaceRevealOn(System.Windows.Forms.Screen screen)
        {
            bool secondary = screen != null && !screen.Primary;
            if (!secondary && !IsOnSecondaryScreen) return;
            var bounds = (screen ?? System.Windows.Forms.Screen.PrimaryScreen).Bounds;
            if (secondary != IsOnSecondaryScreen)
            {
                ClosePanels();
                IsOnSecondaryScreen = secondary;
                foreach (var module in _modules) module.RefreshThemeForScreen();
                if (_barGlassHost != null) _barGlassHost.Visibility = secondary ? Visibility.Hidden : Visibility.Visible;
                UpdatePresentationTimers();
            }
            var scale = VisualTreeHelper.GetDpi(this);
            Width = bounds.Width / scale.DpiScaleX;
            Height = 46 * Ui.Scale;
            NativeMethods.PositionBar(new WindowInteropHelper(this).Handle, bounds.Left, bounds.Top,
                bounds.Width, (int)Math.Round(Height * scale.DpiScaleY));
            if (!secondary) DesktopSpaceService.Apply(App.ActiveSettings, new WindowInteropHelper(this).Handle);
        }

        private void BuildBarModules(Grid root)
        {
            var more = new OverflowModule(S.SettingsBtn);
            _modules.Add(more);
            _barLayout = new BarLayoutPanel(more);
            root.Children.Add(_barLayout);
            Add(new SettingsModule(S.SettingsBtn), "settings", App.ShowSettings);
            _power = Add(new PowerModule(S.SettingsBtn), "power");
            _thisPc = Add(new ThisPcModule(S.SettingsBtn), "thispc",
                () => NativeMethods.ShellExecuteOpen("shell:MyComputerFolder"));
            _recycle = Add(new RecycleBinModule(S.SettingsBtn), "recycle");
            Add(new ClockModule(S.Clock), "clock").SetPresentationDemand(!PresentationSuspended);
            _perfNetwork = Add(new PerfModule(PerfMetric.Network, S.SettingsBtn), "network");
            _perfMemory = Add(new PerfModule(PerfMetric.Memory, S.SettingsBtn), "memory");
            _perfGpu = Add(new PerfModule(PerfMetric.Gpu, S.SettingsBtn), "gpu");
            _perfCpu = Add(new PerfModule(PerfMetric.Cpu, S.SettingsBtn), "cpu");
            _perfSummary = Add(new PerfModule(PerfMetric.Summary, S.SettingsBtn), "performance");
            _spectrum = Add(new SpectrumModule(S.Music), "spectrum");
            _music = Add(new MusicModule(S.Music), "music");
            _volume = Add(new VolumeModule(S.SettingsBtn), "volume");
            if (BatteryModule.Available) _battery = Add(new BatteryModule(S.SettingsBtn), "battery");
            RefreshFavorites();
            ApplyFolderOrder();
            WatchFolderRoot();

            T Add<T>(T module, string id, Action launch = null) where T : ModuleBase
            {
                _modules.Add(module);
                _staticEntries.Add(new BarLayoutPanel.Entry(module, id, BarLayoutPanel.Rules.First(rule => rule.Id == id).Title, launch));
                return module;
            }
        }

        internal void RefreshFavorites()
        {
            if (_barLayout == null) return;
            foreach (var entry in _favoriteEntries)
            {
                entry.Module.Dispose();
                _modules.Remove(entry.Module);
            }
            _favoriteEntries.Clear();
            if (App.ActiveSettings.ShowFavorites) Add("常用");
            foreach (var link in S.DesktopLinks.Where(link => link.OnBar))
                Add(link.Title ?? DesktopActions.Title(link.Target), () => DesktopActions.Open(link.Target));
            UpdateLayoutEntries();

            void Add(string title, Action run = null)
            {
                var module = new FavoriteModule(S.SettingsBtn, title, run);
                _modules.Add(module);
                _favoriteEntries.Add(new BarLayoutPanel.Entry(module, "favorites", title, module.Invoke));
            }
        }

        private void UpdateLayoutEntries() => _barLayout?.SetEntries(_staticEntries.Concat(_folderEntries).Concat(_favoriteEntries));

        internal void RefreshBarLayout() => _barLayout?.RefreshPlacements();

        internal void RefreshClockDisplay() => _modules.OfType<ClockModule>().FirstOrDefault()?.RefreshPreferences();

        internal void OpenFolderShortcut(string path)
        {
            var entry = _folderEntries.FirstOrDefault(item => item.Module is FolderModule folder
                && folder.IsVisible && string.Equals(folder.FolderPath, path, StringComparison.OrdinalIgnoreCase));
            if (entry != null) _barLayout?.OpenOverflowEntry(entry);
        }

        internal bool SetFolderShortcut(int index, bool enabled) => _reveal.SetFolderShortcut(index, enabled);
        internal bool IsFolderShortcutRegistered(int index) => _reveal.IsFolderShortcutRegistered(index);
    }
}
