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
    public partial class MainWindow : Window
    {
        public static MainWindow Bar;

        private static AppSettings S => App.Settings;

        internal List<ModuleBase> DiagnosticModules => _modules;

        private readonly List<ModuleBase> _modules = new List<ModuleBase>();
        private readonly List<ModuleBase> _folderModules = new List<ModuleBase>();
        private readonly DispatcherTimer _pinTimer;
        private readonly DispatcherTimer _moduleDemandTimer;
        private readonly Dictionary<ModuleBase, int> _coveredDemand = new();
        private readonly BarRevealController _reveal;
        private readonly DispatcherTimer _folderRefresh;
        private FileSystemWatcher _folderRootWatcher;
        private string _folderRoot;
        private string _folderInbox;
        private int _folderScanVersion;
        internal event Action FolderCatalogChanged;
        private MediaService _media;
        private Action<MediaInfo> _mediaHandler;
        private Action _mediaLost;
        private MusicModule _music;
        private SpectrumModule _spectrum;
        private ModuleBase _power, _thisPc, _recycle, _volume;
        private BatteryModule _battery;
        private PerfModule _perfCpu, _perfGpu, _perfMemory, _perfNetwork, _perfSummary;
        private bool _closing;
        private bool _mediaAvailable = true;
        private int _layoutVersion;

        public MainWindow()
        {
            _reveal = new BarRevealController(this);
            _moduleDemandTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(650)
            };
            _moduleDemandTimer.Tick += (_, _) => RefreshModuleDemandNow();
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
                RefreshAppSceneRules();
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    if (_closing || Visibility != Visibility.Visible) return;
                    PinToDesktopLayer();
                    UpdatePresentationTimers();
                }));
            };
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible) ClosePanels();
                UpdatePresentationTimers();
                if (!IsVisible) RefreshModuleDemandNow();
            };
            // 分辨率/缩放/显示器变化:顶栏宽度只在构造时定过一次,不跟事件就会停留旧值。
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Closed += (s, e) =>
            {
                _closing = true;
                _layoutVersion++;
                _pinTimer.Stop();
                _moduleDemandTimer.Stop();
                _appSceneTimer?.Stop();
                App.SceneSettings = null;
                _folderRefresh.Stop();
                _folderRootWatcher?.Dispose();
                _reveal.Dispose();
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
                Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
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
                    _reveal.ResetForDisplayChange();
                    Width = SystemParameters.PrimaryScreenWidth;
                    PinToDesktopLayer();
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (App.ActiveSettings.ReserveDesktopSpace) DesktopSpaceService.Apply(App.ActiveSettings, hwnd);
                    else DesktopSpaceService.Apply(App.ActiveSettings);
                    App.Log("显示设置已变化,顶栏已重新适配");
                }
                catch (Exception ex) { App.Log("适配显示设置变化失败: " + ex.Message); }
            }));
        }

        internal void PinToDesktopLayer()
        {
            if (_reveal.IsRaised || PresentationSuspended) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) NativeMethods.PinAboveDesktop(hwnd);
        }

        internal bool HasRevealInteraction => IsMouseOver || IsMouseCaptureWithin || ModuleBase.ActivePanelKeepsBarRevealed
            || _modules.Any(module => module.MenuIsOpen);

        internal bool IsAnyCapsuleCovered(IntPtr hwnd)
        {
            int screenWidth = NativeMethods.ScreenWidth();
            foreach (var module in _modules)
            {
                var pill = module.Pill;
                if (!pill.IsVisible || pill.ActualWidth < 1 || pill.ActualHeight < 1 || _barLayout?.IsDrawn(module) == false) continue;
                var center = pill.PointToScreen(new Point(pill.ActualWidth / 2, pill.ActualHeight / 2));
                if (center.X >= 0 && center.X < screenWidth
                    && NativeMethods.IsCoveredByApplication(hwnd, (int)center.X, (int)center.Y)) return true;
            }
            return false;
        }

        internal bool RevealHotkeyRegistered => _reveal.IsHotkeyRegistered;

        internal void RefreshModuleDemandNow()
        {
            if (_closing || _barLayout == null) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            foreach (var module in _modules)
            {
                if (module is not (PerfModule or SpectrumModule)) continue;
                bool active = false;
                var pill = module.Pill;
                if (IsVisible && !PresentationSuspended && module.IsVisible)
                {
                    active = module.HasVisiblePanel;
                    if (!active && _barLayout.IsDrawn(module) && pill.ActualWidth > 0 && pill.ActualHeight > 0)
                    {
                        var center = pill.PointToScreen(new Point(pill.ActualWidth / 2, pill.ActualHeight / 2));
                        bool covered = !_reveal.IsRaised && NativeMethods.IsCoveredByApplication(hwnd, (int)center.X, (int)center.Y);
                        int frames = covered ? Math.Min(2, _coveredDemand.GetValueOrDefault(module) + 1) : 0;
                        _coveredDemand[module] = frames;
                        active = frames < 2;
                    }
                }
                if (module is PerfModule perf) perf.SetPresentationDemand(active);
                else ((SpectrumModule)module).SetPresentationDemand(active);
            }
        }
        internal void SetEdgeReveal(bool enabled) => _reveal.SetEdgeEnabled(enabled);
        internal bool TrySetRevealHotkey(bool enabled, BarHotkey shortcut) => _reveal.TrySetHotkey(enabled, shortcut);
        internal void CaptureRevealHotkey(Action<BarHotkey> capture) => _reveal.CaptureHotkey = capture;

        internal void ShowForReveal()
        {
            base.Show();
            UpdatePresentationTimers();
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
            _barGlassHost = glassHost;
            if (glassHost != null)
            {
                glassHost.Visibility = IsOnSecondaryScreen ? Visibility.Hidden : Visibility.Visible;
                root.Children.Add(glassHost);
            }

            // 顶栏背景条:默认完全透明(null → 不拦截点击)。玻璃/液态玻璃档强制透明——
            // 玻璃染色层已承担压暗职责,再叠背景条会双重压暗。
            var barBg = new Border
            {
                Height = 46 * Ui.Scale,
                VerticalAlignment = VerticalAlignment.Top,
                Background = ((App.ActiveSettings.PillMaterial == 3 || App.ActiveSettings.PillMaterial == 4) || App.ActiveSettings.BarBgOpacity <= 0.005)
                    ? null
                    : Ui.Brush(App.ActiveSettings.BarBgColor, App.ActiveSettings.BarBgOpacity)
            };
            root.Children.Add(barBg);

            BuildBarModules(root);
            ApplyModuleVisibility();
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
                if (IsOnSecondaryScreen) return;
                var hwnd = new WindowInteropHelper(this).Handle;
                if (App.ActiveSettings.ReserveDesktopSpace)
                    DesktopSpaceService.Apply(App.ActiveSettings, hwnd);
                else
                    DesktopSpaceService.Apply(App.ActiveSettings);
            }));
        }

        internal void ApplyFolderOrder()
        {
            if (_barLayout == null) return;
            string previousInbox = _folderInbox;
            _folderInbox = S.InboxFolder;
            var previous = _folderEntries.ToDictionary(entry =>
            {
                var folder = (FolderModule)entry.Module;
                return (folder.FolderName, folder.FolderPath, folder.IsLinkedFolder);
            });
            _folderEntries.Clear();
            _folderModules.Clear();
            foreach (var name in S.Folders)
                AddFolder(name, MiniFolderCatalog.GetFolderPath(S.StorageRoot, name), linked: false);
            foreach (var path in S.LinkedFolders)
            {
                var trimmed = System.IO.Path.TrimEndingDirectorySeparator(path);
                var name = System.IO.Path.GetFileName(trimmed);
                AddFolder(string.IsNullOrEmpty(name) ? trimmed : name, path, linked: true);
            }
            foreach (var entry in previous.Values)
            {
                entry.Module.Dispose();
                _modules.Remove(entry.Module);
            }

            UpdateLayoutEntries();

            void AddFolder(string name, string path, bool linked)
            {
                bool reused = previous.Remove((name, path, linked), out var entry);
                if (!reused)
                {
                    var next = new FolderModule(name, S.Folder, linked ? path : null);
                    entry = new BarLayoutPanel.Entry(next, "folders", name);
                    _modules.Add(next);
                }
                var folder = (FolderModule)entry.Module;
                _folderModules.Add(folder);
                _folderEntries.Add(entry);
                SetModuleVisibility(folder, App.ActiveSettings.ShowFolders);
                if (reused && !string.Equals(previousInbox, _folderInbox, StringComparison.OrdinalIgnoreCase))
                    folder.RefreshInboxRole(previousInbox);
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
                double lightness = GlassService.SurfaceLightness(pixels, width, height, area, look, opacity, out double spread);
                module.ApplyPillInk(lightness, spread);
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
            if (App.ActiveSettings.PillMaterial != 3 && App.ActiveSettings.PillMaterial != 4) return;
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

        /// <summary>
        /// 按"模块显示"设置显隐各模块。只改可见性、不重建顶栏,
        /// 供设置页的模块开关直接调用,避免整条胶囊重建带来的闪烁。
        /// </summary>
        public void ApplyModuleVisibility()
        {
            if (!CheckAccess()) { Dispatcher.BeginInvoke(new Action(ApplyModuleVisibility)); return; }
            PerfService.Instance.ConfigureMetrics(App.ActiveSettings);
            SetModuleVisibility(_power, App.ActiveSettings.ShowPower);
            SetModuleVisibility(_thisPc, App.ActiveSettings.ShowThisPc);
            SetModuleVisibility(_recycle, App.ActiveSettings.ShowRecycleBin);
            foreach (var fm in _folderModules)
                SetModuleVisibility(fm, App.ActiveSettings.ShowFolders);
            SetModuleVisibility(_music, App.ActiveSettings.ShowMusic && _mediaAvailable);
            if (_spectrum != null)
            {
                _spectrum.Visibility = App.ActiveSettings.ShowSpectrum ? Visibility.Visible : Visibility.Collapsed;
                _spectrum.SetSpectrumActive(App.ActiveSettings.ShowSpectrum);
            }
            SetModuleVisibility(_volume, App.ActiveSettings.ShowVolume);
            SetModuleVisibility(_battery, App.ActiveSettings.ShowBattery);
            SetModuleVisibility(_perfCpu, App.ActiveSettings.ShowMonitorCpu && !App.ActiveSettings.CombinePerfModules);
            SetModuleVisibility(_perfGpu, App.ActiveSettings.ShowMonitorGpu && !App.ActiveSettings.CombinePerfModules);
            SetModuleVisibility(_perfMemory, App.ActiveSettings.ShowMonitorMemory && !App.ActiveSettings.CombinePerfModules);
            SetModuleVisibility(_perfNetwork, App.ActiveSettings.ShowMonitorNetwork && !App.ActiveSettings.CombinePerfModules);
            SetModuleVisibility(_perfSummary, App.ActiveSettings.CombinePerfModules && PerfModule.EnabledMetrics().Any());
            _perfSummary?.RefreshSummaryMetrics();
        }

        /// <summary>立即关闭所有独立展开页，防止顶栏隐藏后留下可交互的置顶窗口。</summary>
        public void ClosePanels()
        {
            if (!CheckAccess()) { Dispatcher.BeginInvoke(new Action(ClosePanels)); return; }
            foreach (var module in _modules)
                if (module.HasOpenUi) module.ClosePanelImmediately();
        }

        /// <summary>隐藏顶栏前先收起独立展开页，保持胶囊和面板的生命周期一致。</summary>
        public void HideWithPanels()
        {
            ClosePanels();
            _reveal.Reset();
            Hide();
            Opacity = 1;
            UpdatePresentationTimers();
        }

        public new void Show()
        {
            base.Show();
            if (!_closing) PinToDesktopLayer();
            UpdatePresentationTimers();
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
            _coveredDemand.Clear();
            _folderModules.Clear();
            _staticEntries.Clear();
            _folderEntries.Clear();
            _favoriteEntries.Clear();
            _barLayout = null;
            _power = null;
            _thisPc = null;
            _recycle = null;
            _music = null;
            _spectrum = null;
            _volume = null;
            _battery = null;
            _perfCpu = null;
            _perfGpu = null;
            _perfMemory = null;
            _perfNetwork = null;
            _perfSummary = null;
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
            music?.UseMediaService(_media);
            _mediaHandler = info =>
            {
                if (music == null || !ReferenceEquals(_music, music)) return;
                music.Update(info);
                _mediaAvailable = info.HasSession || !string.IsNullOrEmpty(S.PreferredMediaSource);
                SetModuleVisibility(music, App.ActiveSettings.ShowMusic && _mediaAvailable);
            };
            _mediaLost = () =>
            {
                _mediaAvailable = !string.IsNullOrEmpty(S.PreferredMediaSource);
                if (music != null && ReferenceEquals(_music, music))
                    SetModuleVisibility(music, App.ActiveSettings.ShowMusic && _mediaAvailable);
            };
            _media.Updated += _mediaHandler;
            _media.SessionLost += _mediaLost;
            _mediaHandler(_media.Current);
        }
    }
}
