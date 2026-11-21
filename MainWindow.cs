using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

        internal List<ModuleBase> DiagnosticModules => _modules;

        private readonly List<ModuleBase> _modules = new List<ModuleBase>();
        private readonly List<ModuleBase> _folderModules = new List<ModuleBase>();
        private readonly DispatcherTimer _pinTimer;
        private readonly BarRevealController _reveal;
        private readonly DispatcherTimer _folderRefresh;
        private FileSystemWatcher _folderRootWatcher;
        private string _folderRoot;
        private int _folderScanVersion;
        private StackPanel _folderStrip;
        internal event Action FolderCatalogChanged;
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
            _reveal = new BarRevealController(this);
            _folderRefresh = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _folderRefresh.Tick += (_, _) =>
            {
                _folderRefresh.Stop();
                _ = RefreshFolderCatalogAsync();
            };
            FontFamily = Ui.TextFont;
            Bar = this;   // 全局单例:MicaService/GlassService/自检都靠它拿"当前顶栏",
                          // 这里漏赋值过一次,导致液态玻璃几何换算永远拿到 null 而静默不生效。
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
                // 登记窗口，由玻璃服务按照当前截图兼容设置决定是否排除。
                GlassService.RegisterWindow(handle);
                GlassService.SetBarWindow(handle);
                _reveal.Attach(handle);
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
                    // 诊断开关:YUKI_NO_REPIN=1 关掉 3 秒保底重钉,用来量化重钉本身的开销。
                    if (Environment.GetEnvironmentVariable("YUKI_NO_REPIN") != "1") _pinTimer.Start();
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
                _folderRefresh.Stop();
                _folderRootWatcher?.Dispose();
                _reveal.Dispose();
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

        internal void PinToDesktopLayer()
        {
            if (_reveal.IsRaised) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) NativeMethods.PinAboveDesktop(hwnd);
        }

        internal bool HasRevealInteraction => IsMouseOver || IsMouseCaptureWithin || ModuleBase.ActivePanelKeepsBarRevealed;

        internal bool IsAnyCapsuleCovered(IntPtr hwnd)
        {
            int screenWidth = NativeMethods.ScreenWidth();
            foreach (var module in _modules)
            {
                var pill = module.Pill;
                if (!pill.IsVisible || pill.ActualWidth < 1 || pill.ActualHeight < 1) continue;
                var center = pill.PointToScreen(new Point(pill.ActualWidth / 2, pill.ActualHeight / 2));
                if (center.X >= 0 && center.X < screenWidth
                    && NativeMethods.IsCoveredByApplication(hwnd, (int)center.X, (int)center.Y)) return true;
            }
            return false;
        }

        internal bool RevealHotkeyRegistered => _reveal.IsHotkeyRegistered;
        internal void SetEdgeReveal(bool enabled) => _reveal.SetEdgeEnabled(enabled);
        internal bool TrySetRevealHotkey(bool enabled, BarHotkey shortcut) => _reveal.TrySetHotkey(enabled, shortcut);
        internal void CaptureRevealHotkey(Action<BarHotkey> capture) => _reveal.CaptureHotkey = capture;

        internal void ShowForReveal()
        {
            base.Show();
            GlassService.SetActive(true);
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
            _glassPills.Clear();
            _pendingGlassPills.Clear();
            _glassLayoutDirty = true;
            DisposeModules();
            Height = 46 * Ui.Scale;

            var root = new Grid();

            // 玻璃层放在最底(背景条之下):Image 承载壁纸或捕获条带,
            // Clip=胶囊并集保证胶囊外 alpha=0、点击照常穿透桌面。元素由服务缓存,
            // 全量重建时复用同一块 WriteableBitmap,避免重建瞬间玻璃空一帧。
            var glassHost = GlassService.EnsureHost();
            if (glassHost != null) root.Children.Add(glassHost);

            // 顶栏背景条:默认完全透明(null → 不拦截点击)。玻璃/液态玻璃档强制透明——
            // 玻璃染色层已承担压暗职责,再叠背景条会双重压暗。
            var barBg = new Border
            {
                Height = 46 * Ui.Scale,
                VerticalAlignment = VerticalAlignment.Top,
                Background = ((S.PillMaterial == 3 || S.PillMaterial == 4) || S.BarBgOpacity <= 0.005)
                    ? null
                    : Ui.Brush(S.BarBgColor, S.BarBgOpacity)
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

            _folderStrip = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(_folderStrip);
            ApplyFolderOrder();
            WatchFolderRoot();

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
            if (glassHost != null)
            {
                // 胶囊并集裁剪随布局变化重算(模块显隐/文件夹增删/时钟重排都走这里)。
                // UpdateGlassClip 内部做 Rect 集合比较防抖,LayoutUpdated 高频触发也只做 O(n) 比较。
                root.LayoutUpdated += (s2, e2) => UpdateGlassClip();
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_closing || layoutVersion != _layoutVersion) return;
                UpdateGlassClip();
                var hwnd = new WindowInteropHelper(this).Handle;
                if (S.ReserveDesktopSpace)
                    DesktopSpaceService.Apply(S, hwnd);
                else
                    DesktopSpaceService.Apply(S);
            }));
        }

        internal void ApplyFolderOrder()
        {
            foreach (var module in _folderModules)
            {
                module.Dispose();
                _modules.Remove(module);
            }
            _folderModules.Clear();
            if (_folderStrip == null) return;
            _folderStrip.Children.Clear();
            foreach (var name in S.Folders)
            {
                var folder = new FolderModule(name, S.Folder);
                _folderModules.Add(folder);
                _modules.Add(folder);
                _folderStrip.Children.Add(folder);
                SetModuleVisibility(folder, S.ShowFolders);
            }
        }

        private void WatchFolderRoot()
        {
            if (string.Equals(_folderRoot, S.StorageRoot, StringComparison.OrdinalIgnoreCase)) return;
            _folderRoot = S.StorageRoot;
            _folderRefresh.Stop();
            _folderRootWatcher?.Dispose();
            _folderRootWatcher = null;
            _ = RefreshFolderCatalogAsync();
        }

        private async Task RefreshFolderCatalogAsync()
        {
            string root = _folderRoot;
            int version = ++_folderScanVersion;
            bool needsWatcher = _folderRootWatcher == null;
            var (names, watcher) = await Task.Run(() => ReadFolderCatalog(root, needsWatcher));
            if (_closing || version != _folderScanVersion || !string.Equals(root, S.StorageRoot, StringComparison.OrdinalIgnoreCase))
            {
                watcher?.Dispose();
                return;
            }
            _folderRootWatcher ??= watcher;
            if (names == null)
            {
                _folderRootWatcher?.Dispose();
                _folderRootWatcher = null;
                ScheduleFolderRefresh(TimeSpan.FromSeconds(3));
                return;
            }
            if (_folderRootWatcher == null) ScheduleFolderRefresh(TimeSpan.FromSeconds(3));
            var order = MiniFolderCatalog.Reconcile(S.Folders, names);
            if (S.Folders.SequenceEqual(order, StringComparer.Ordinal)) return;
            S.Folders = order;
            ApplyFolderOrder();
            FolderCatalogChanged?.Invoke();
        }

        private (string[] Names, FileSystemWatcher Watcher) ReadFolderCatalog(string root, bool needsWatcher)
        {
            FileSystemWatcher watcher = null;
            if (needsWatcher)
            {
                try
                {
                    watcher = new FileSystemWatcher(root) { NotifyFilter = NotifyFilters.DirectoryName };
                    watcher.Created += CatalogChanged;
                    watcher.Deleted += CatalogChanged;
                    watcher.Renamed += CatalogChanged;
                    watcher.Error += (_, _) => CatalogChanged(watcher, null);
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    watcher?.Dispose();
                    watcher = null;
                }
            }
            try
            {
                return (MiniFolderCatalog.ReadNames(root), watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watcher?.Dispose();
                return (null, null);
            }
        }

        private void CatalogChanged(object sender, FileSystemEventArgs args)
        {
            var watcher = (FileSystemWatcher)sender;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_closing || !string.Equals(_folderRoot, watcher.Path, StringComparison.OrdinalIgnoreCase)) return;
                if (args == null && ReferenceEquals(watcher, _folderRootWatcher))
                {
                    _folderRootWatcher.Dispose();
                    _folderRootWatcher = null;
                }
                ScheduleFolderRefresh(TimeSpan.FromMilliseconds(300));
            }));
        }

        private void ScheduleFolderRefresh(TimeSpan delay)
        {
            _folderRefresh.Stop();
            _folderRefresh.Interval = delay;
            _folderRefresh.Start();
        }

        private List<(ModuleBase Module, Rect Bounds)> _glassPills = new();
        private List<(ModuleBase Module, Rect Bounds)> _pendingGlassPills = new();
        private bool _glassLayoutDirty = true;

        internal void ResetGlassInk()
        {
            foreach (var pill in _glassPills) pill.Module.ApplyPillInk(null);
        }

        internal void UpdateGlassInk(byte[] pixels, int width, int height, double imageHeight, GlassService.GlassLook look, double opacity)
        {
            if (ActualWidth <= 0 || imageHeight <= 0) return;
            foreach (var (module, bounds) in _glassPills)
            {
                var area = new Rect(bounds.X / ActualWidth, bounds.Y / imageHeight,
                    bounds.Width / ActualWidth, bounds.Height / imageHeight);
                module.ApplyPillInk(GlassService.SurfaceLightness(pixels, width, height, area, look, opacity));
            }
        }

        internal void RefreshGlassClip()
        {
            _glassLayoutDirty = true;
            UpdateGlassClip();
        }

        /// <summary>
        /// 重算玻璃层裁剪几何 = 所有可见胶囊圆角矩形的并集(DIP,窗口坐标系)。
        /// 并集之外的玻璃像素保持 alpha=0,维持"胶囊外点击穿透到桌面"的契约。
        /// 必须做 Rect 集合比较防抖:LayoutUpdated 一帧可触发多次,而每次给 Clip 赋
        /// 新 GeometryGroup 都会再触发布局失效——不比较就会形成无限失效循环,
        /// 把 UI 线程打满一个核(实测 118%,旧版纯云母只有 3%)。
        /// </summary>
        private void UpdateGlassClip()
        {
            if (_closing) return;
            if (S.PillMaterial != 3 && S.PillMaterial != 4) return;
            var root = Content as Grid;
            if (root == null) return;
            _pendingGlassPills.Clear();
            if (root.ActualWidth < 1 || root.ActualHeight < 1) return;
            var viewport = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            foreach (var module in _modules)
            {
                var pill = module.Pill;
                if (module.Visibility != Visibility.Visible || pill.ActualWidth < 1 || pill.ActualHeight < 1
                    || !root.IsAncestorOf(pill)) continue;
                var rect = pill.TransformToAncestor(root)
                    .TransformBounds(new Rect(0, 0, pill.ActualWidth, pill.ActualHeight));
                if (rect.IntersectsWith(viewport)) _pendingGlassPills.Add((module, rect));
            }

            if (!_glassLayoutDirty && SameGlassLayout()) return;
            (_glassPills, _pendingGlassPills) = (_pendingGlassPills, _glassPills);
            _pendingGlassPills.Clear();
            _glassLayoutDirty = false;
            var rects = new List<Rect>(_glassPills.Count);
            foreach (var pill in _glassPills) rects.Add(pill.Bounds);
            // 实验性选项(环境变量):胶囊间隙也铺玻璃,强度按倍数淡化。0/未设置=维持"胶囊外完全透明"的穿透契约。
            double gapFill = GlassService.GapFill;
            GeometryGroup group = null;
            try
            {
                group = new GeometryGroup { FillRule = FillRule.Nonzero };
                if (gapFill > 0.01)
                {
                    // 整条顶栏一块玻璃(圆角与窗口等高):间隙也模糊,胶囊仍是更浓的一层。
                    group.Children.Add(new RectangleGeometry(new Rect(0, 0, root.ActualWidth, root.ActualHeight), 10 * Ui.Scale, 10 * Ui.Scale));
                }
                else
                {
                    foreach (var r in rects)
                        // 19*scale 与 ModuleBase 胶囊 CornerRadius 同源,保持圆角一致。
                        group.Children.Add(new RectangleGeometry(r, 19 * Ui.Scale, 19 * Ui.Scale));
                }
                GlassService.SetClip(group, rects, gapFill);
            }
            catch
            {
                try { GlassService.SetClip(null, null); } catch { }
            }
        }

        // 布局回调可能很频繁，同一个模块的亚像素抖动不触发纹理重建。
        private bool SameGlassLayout()
        {
            if (_glassPills.Count != _pendingGlassPills.Count) return false;
            for (int i = 0; i < _glassPills.Count; i++)
            {
                var (module, a) = _glassPills[i];
                var (nextModule, b) = _pendingGlassPills[i];
                if (!ReferenceEquals(module, nextModule) || Math.Abs(a.X - b.X) > 0.25 || Math.Abs(a.Y - b.Y) > 0.25
                    || Math.Abs(a.Width - b.Width) > 0.25 || Math.Abs(a.Height - b.Height) > 0.25)
                    return false;
            }
            return true;
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
            _reveal.Reset();
            _pinTimer.Stop();
            Hide();
            Opacity = 1;
            GlassService.SetActive(false);
        }

        public new void Show()
        {
            base.Show();
            if (!_closing)
            {
                PinToDesktopLayer();
                _pinTimer.Start();
            }
            GlassService.SetActive(true);
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
            _mediaHandler(_media.Current);
        }
    }
}
