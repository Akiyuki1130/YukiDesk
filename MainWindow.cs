using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopBar.Interop;
using DesktopBar.Models;
using DesktopBar.Services;
using DesktopBar.UI;

namespace DesktopBar
{
    /// <summary>
    /// 桌面层顶栏窗口:透明全屏窗口钉在 Z 序底部(桌面之上、所有软件之下),
    /// WS_EX_NOACTIVATE 保证点击不抢焦点、不会被提到前台。
    /// </summary>
    public class MainWindow : Window
    {
        public static MainWindow Bar;

        private static AppSettings S => App.Settings;

        private readonly List<ModuleBase> _modules = new List<ModuleBase>();
        private readonly List<ModuleBase> _folderModules = new List<ModuleBase>();
        private readonly DispatcherTimer _pinTimer;
        private MediaService _media;
        private Action<MediaInfo> _mediaHandler;
        private Action _mediaLost;
        private MusicModule _music;
        private SpectrumModule _spectrum;
        private ModuleBase _power, _thisPc, _recycle, _volume;
        private PerfModule _perfCpu, _perfGpu, _perfMemory, _perfNetwork;
        private bool _closing;
        private bool _mediaAvailable = true;
        private int _layoutVersion;

        public MainWindow()
        {
            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = 46 * Ui.Scale;   // 只有药丸条的高度;悬停面板是独立弹出窗口
            Title = "YukiDesk";
            ThemeService.Start();

            SourceInitialized += (s, e) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                NativeMethods.ApplyNoActivateToolStyle(handle);
                NativeMethods.EnsureShown(handle);
                App.Log("YukiDesk 主窗口句柄=" + handle + " 尺寸=" + ActualWidth + "x" + ActualHeight);
            };

            _pinTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _pinTimer.Tick += (s, e) =>
            {
                if (!_closing && Visibility == Visibility.Visible)
                    PinToDesktopLayer();
            };

            Loaded += (s, e) =>
            {
                App.Log("YukiDesk 主窗口 Loaded");
                BuildLayout();
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    if (_closing || Visibility != Visibility.Visible) return;
                    PinToDesktopLayer();
                    _pinTimer.Start();
                }));
            };
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible) ClosePanels();
            };
            // 分辨率/缩放/显示器变化:顶栏宽度只在构造时定过一次,不跟事件就会停留旧值。
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Closed += (s, e) =>
            {
                _closing = true;
                _layoutVersion++;
                _pinTimer.Stop();
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                DisposeModules();
                try { _media?.Dispose(); } catch { }
                _media = null;
            };
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing) return;
                try
                {
                    Width = SystemParameters.PrimaryScreenWidth;
                    PinToDesktopLayer();
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (S.ReserveDesktopSpace) DesktopSpaceService.Apply(S, hwnd);
                    else DesktopSpaceService.Apply(S);
                    App.Log("显示设置已变化,顶栏已重新适配");
                }
                catch (Exception ex) { App.Log("适配显示设置变化失败: " + ex.Message); }
            }));
        }

        private void PinToDesktopLayer()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) NativeMethods.PinAboveDesktop(hwnd);
        }

        public void ApplySettings()
        {
            if (_closing) return;
            if (CheckAccess()) BuildLayout();
            else Dispatcher.BeginInvoke(new Action(() => { if (!_closing) BuildLayout(); }));
        }

        private void BuildLayout()
        {
            if (_closing) return;
            int layoutVersion = ++_layoutVersion;
            DisposeModules();
            Height = 46 * Ui.Scale;

            var root = new Grid();

            // 顶栏背景条:默认完全透明(null → 不拦截点击)
            var barBg = new Border
            {
                Height = 46 * Ui.Scale,
                VerticalAlignment = VerticalAlignment.Top,
                Background = S.BarBgOpacity > 0.005 ? Ui.Brush(S.BarBgColor, S.BarBgOpacity) : null
            };
            root.Children.Add(barBg);

            var cols = new Grid();
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10 * Ui.Scale, 0, 0, 0) };
            // 右组不再加面板边距:每个模块自带 10px 右间距,最右胶囊到屏幕边缘正好也是 10px,与左侧一致。
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 2);
            cols.Children.Add(left);
            cols.Children.Add(right);

            // 最左:雪花设置按钮 + 电源按钮。模块始终创建,显隐由 ApplyModuleVisibility 控制,
            // 这样切换"模块显示"开关时不需要重建整条顶栏(重建会让所有胶囊闪一下)。
            var settingsMod = new SettingsModule(S.SettingsBtn);
            _modules.Add(settingsMod);
            left.Children.Add(settingsMod);
            _power = new PowerModule(S.SettingsBtn);
            _modules.Add(_power);
            left.Children.Add(_power);

            // 此电脑：放在电源和回收站之间。显隐走 ShowThisPc(其余模块同款开关)。
            _thisPc = new ThisPcModule(S.SettingsBtn);
            _modules.Add(_thisPc);
            left.Children.Add(_thisPc);

            // 回收站：放在此电脑和小文件夹之间
            _recycle = new RecycleBinModule(S.SettingsBtn);
            _modules.Add(_recycle);
            left.Children.Add(_recycle);

            // 小文件夹
            _folderModules.Clear();
            foreach (var name in S.Folders)
            {
                var fm = new FolderModule(name, S.Folder);
                _folderModules.Add(fm);
                _modules.Add(fm);
                left.Children.Add(fm);
            }

            // 时间:靠右时跟随右侧内容,居中时覆盖完整顶栏宽度。
            var clock = new ClockModule(S.Clock);
            _modules.Add(clock);
            Grid centeredClock = null;
            if (S.TimeOnRight)
            {
                right.Children.Add(clock);
            }
            else
            {
                // 居中时间使用覆盖全窗口的独立网格，不能依赖左右内容的剩余列宽。
                clock.HorizontalAlignment = HorizontalAlignment.Center;
                clock.VerticalAlignment = VerticalAlignment.Top;
                centeredClock = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Top
                };
                centeredClock.Children.Add(clock);
            }

            // 音乐
            _music = new MusicModule(S.Music);
            _modules.Add(_music);
            if (S.TimeOnRight) right.Children.Insert(0, _music);
            else right.Children.Add(_music);

            var volume = new VolumeModule(S.SettingsBtn);
            _modules.Add(volume);
            _volume = volume;
            right.Children.Add(volume);

            // 实时音频频谱:插到右组最前,正好落在音乐播放器左边。
            _spectrum = new SpectrumModule(S.Music);
            _modules.Add(_spectrum);
            right.Children.Insert(0, _spectrum);

            // 性能监测:逐个插到右组最前,最终从左到右为 网络、内存、GPU、CPU,紧邻频谱左侧。
            _perfNetwork = AddPerfModule(right, PerfMetric.Network);
            _perfMemory = AddPerfModule(right, PerfMetric.Memory);
            _perfGpu = AddPerfModule(right, PerfMetric.Gpu);
            _perfCpu = AddPerfModule(right, PerfMetric.Cpu);

            ApplyModuleVisibility();

            root.Children.Add(cols);
            if (centeredClock != null)
                root.Children.Add(centeredClock);
            Content = root;
            App.Log("YukiDesk 主栏布局已构建");
            WireMedia();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_closing || layoutVersion != _layoutVersion) return;
                var hwnd = new WindowInteropHelper(this).Handle;
                if (S.ReserveDesktopSpace)
                    DesktopSpaceService.Apply(S, hwnd);
                else
                    DesktopSpaceService.Apply(S);
            }));
        }

        private PerfModule AddPerfModule(StackPanel right, PerfMetric metric)
        {
            var module = new PerfModule(metric, S.SettingsBtn);
            _modules.Add(module);
            right.Children.Insert(0, module);
            return module;
        }

        /// <summary>
        /// 按"模块显示"设置显隐各模块。只改可见性、不重建顶栏,
        /// 供设置页的模块开关直接调用,避免整条胶囊重建带来的闪烁。
        /// </summary>
        public void ApplyModuleVisibility()
        {
            if (!CheckAccess()) { Dispatcher.BeginInvoke(new Action(ApplyModuleVisibility)); return; }
            SetModuleVisibility(_power, S.ShowPower);
            SetModuleVisibility(_thisPc, S.ShowThisPc);
            SetModuleVisibility(_recycle, S.ShowRecycleBin);
            foreach (var fm in _folderModules)
                SetModuleVisibility(fm, S.ShowFolders);
            SetModuleVisibility(_music, S.ShowMusic && _mediaAvailable);
            if (_spectrum != null)
            {
                _spectrum.Visibility = S.ShowSpectrum ? Visibility.Visible : Visibility.Collapsed;
                _spectrum.SetSpectrumActive(S.ShowSpectrum);
            }
            SetModuleVisibility(_volume, S.ShowVolume);
            SetModuleVisibility(_perfCpu, S.ShowMonitorCpu);
            SetModuleVisibility(_perfGpu, S.ShowMonitorGpu);
            SetModuleVisibility(_perfMemory, S.ShowMonitorMemory);
            SetModuleVisibility(_perfNetwork, S.ShowMonitorNetwork);
        }

        /// <summary>立即关闭所有独立展开页，防止顶栏隐藏后留下可交互的置顶窗口。</summary>
        public void ClosePanels()
        {
            if (!CheckAccess()) { Dispatcher.BeginInvoke(new Action(ClosePanels)); return; }
            foreach (var module in _modules)
                module.ClosePanelImmediately();
        }

        /// <summary>隐藏顶栏前先收起独立展开页，保持胶囊和面板的生命周期一致。</summary>
        public void HideWithPanels()
        {
            ClosePanels();
            _pinTimer.Stop();
            Hide();
        }

        public new void Show()
        {
            base.Show();
            if (!_closing)
            {
                PinToDesktopLayer();
                _pinTimer.Start();
            }
        }

        private static void SetModuleVisibility(ModuleBase module, bool visible)
        {
            if (module == null) return;
            if (!visible) module.ClosePanelImmediately();
            module.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DisposeModules()
        {
            ClosePanels();
            foreach (var module in _modules)
                module.Dispose();
            _modules.Clear();
            _folderModules.Clear();
            _power = null;
            _thisPc = null;
            _recycle = null;
            _music = null;
            _spectrum = null;
            _volume = null;
            _perfCpu = null;
            _perfGpu = null;
            _perfMemory = null;
            _perfNetwork = null;
        }

        private void WireMedia()
        {
            if (_media == null)
            {
                _media = new MediaService(Dispatcher);
                _ = _media.StartAsync();
            }
            if (_mediaHandler != null) _media.Updated -= _mediaHandler;
            if (_mediaLost != null) _media.SessionLost -= _mediaLost;

            var music = _music;
            _mediaHandler = info =>
            {
                if (music == null || !ReferenceEquals(_music, music)) return;
                music.Update(info);
                _mediaAvailable = info.HasSession;
                SetModuleVisibility(music, S.ShowMusic && _mediaAvailable);
            };
            _mediaLost = () =>
            {
                _mediaAvailable = false;
                if (music != null && ReferenceEquals(_music, music))
                    SetModuleVisibility(music, false);
            };
            _media.Updated += _mediaHandler;
            _media.SessionLost += _mediaLost;
        }
    }
}
