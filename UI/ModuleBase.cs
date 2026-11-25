using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopBar.Interop;
using DesktopBar.Models;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    /// <summary>统一胶囊选项卡与悬停面板行为。</summary>
    public abstract class ModuleBase : UserControl
    {
        protected readonly Grid Root = new Grid();
        public readonly Border Pill;
        protected readonly Border Panel;
        protected Window PanelWindow;
        protected TabStyle _style;
        private readonly List<(TextBlock tb, double delta)> _texts = new List<(TextBlock, double)>();
        protected const string ContentInkKey = "YukiGlassContentInk";
        private static readonly Brush DarkGlassInk = Ui.Brush("#101820");
        private static readonly Brush LightGlassInk = Ui.Brush("#FBFDFF");
        private Brush _pendingPillInk;
        private DispatcherTimer _pillInkTimer;
        private static readonly Brush GlassHover = Ui.Brush("#FFFFFF", 0.075);
        private static readonly Brush DarkTextBacking = Ui.Brush("#FFF4E7", .27);
        private static readonly Brush LightTextBacking = Ui.Brush("#16120F", .25);
        private bool _hasInkBacking, _pendingInkBacking;
        private readonly DispatcherTimer _closeTimer;
        private readonly TranslateTransform _panelTransform = new();
        private readonly ScaleTransform _panelScale = new();
        private readonly ScaleTransform _panelContentScale = new();
        private readonly PanelMotion _panelMotion = new();
        private bool _panelAbove;
        private int _panelGeneration;
        private bool _closing;
        private bool _disposed;
        private int _shellInteractions;
        private int _fileDropTargets;
        private DispatcherTimer _dropSettleTimer;
        protected bool FileDropInteractionActive => _fileDropTargets > 0 || _dropSettleTimer?.IsEnabled == true;
        protected bool Open;
        private static ModuleBase _active;
        internal bool MenuIsOpen => _openMenu?.IsOpen == true;
        internal bool HasVisiblePanel => PanelWindow.IsVisible;
        internal bool HasOpenUi => Open || _closing || HasVisiblePanel || MenuIsOpen;
        internal event Action<ModuleBase> PanelHidden;
        internal static bool ActivePanelKeepsBarRevealed => _active?.PanelWindow.IsVisible == true
            && (_active.PanelWindow.IsMouseOver || _active.HasPanelInteraction);

        // 玻璃使用固定捕获纹理。开合时只更新对应当前屏幕矩形的采样坐标。
        private readonly Rectangle _panelGlass;
        private readonly Grid _panelLiveLayer;
        private readonly Rectangle _panelBackdropFade;
        private readonly Border _panelChrome;
        private readonly Grid _panelSurface;
        private readonly RectangleGeometry _panelClip = new();
        private DispatcherTimer _firstFrameTimer;
        private Action _firstFrameFallback;
        private double _panelMotionInset;
        private double _fallbackOpacity, _fallbackAlpha;
        private bool _blendingBackdrop;
        private TimeSpan? _backdropBlendStart;
        private TimeSpan? _lastPanelFrame;
        private double _panelLeft, _panelTop, _panelWidth, _panelHeight;   // 未变换时的完整矩形(DIP)
        private Size _panelEnvelope = Size.Empty;
        private double _panelCaptureHeight;
        private bool _panelTracking;
        private double _trackMinScale = 1.0, _trackMaxScale = 1.0;   // 跟踪期观测到的缩放范围(诊断用)
        private int _trackPushes;                                   // 跟踪期向采样线程推了几次矩形

        protected ModuleBase(TabStyle style)
        {
            _style = style ?? new TabStyle();
            Focusable = false;
            double scale = Ui.Scale;
            Pill = new Border
            {
                CornerRadius = new CornerRadius(19 * scale),
                Height = 38 * scale,
                Padding = new Thickness(16 * scale, 0, 16 * scale, 0),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand
            };
            Root.Children.Add(Pill);
            Root.Margin = new Thickness(0, 4 * scale, 10 * scale, 0);
            Content = Root;

            Panel = new Border
            {
                CornerRadius = new CornerRadius(16 * scale),
                Padding = new Thickness(12 * scale),
                Background = Brushes.Transparent,
                RenderTransform = _panelContentScale
            };
            _panelChrome = new Border { CornerRadius = Panel.CornerRadius, IsHitTestVisible = false };
            // 背景填满展开页。整页共用 WPF 裁剪，首帧、玻璃和备用背景的外轮廓一致。
            _panelGlass = new Rectangle
            {
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };
            _panelBackdropFade = new Rectangle
            {
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _panelLiveLayer = new Grid { IsHitTestVisible = false };
            _panelLiveLayer.Children.Add(_panelGlass);
            var transforms = new TransformGroup();
            transforms.Children.Add(_panelScale);
            transforms.Children.Add(_panelTransform);
            _panelSurface = new Grid { RenderTransform = transforms, Clip = _panelClip, Opacity = 0 };
            _panelSurface.Children.Add(_panelLiveLayer);
            _panelSurface.Children.Add(_panelBackdropFade);
            _panelSurface.Children.Add(_panelChrome);
            _panelSurface.Children.Add(Panel);
            // 展开页内容异步加载完成(文件夹列表/回收站条目)时窗口会被 SizeToContent 撑大,
            // 这里必须重推采样矩形,否则玻璃还在按打开瞬间那块小区域采样,和面板错位。
            _panelSurface.SizeChanged += (s2, e2) => UpdatePanelFrame();
            CreatePanelWindow();

            _closeTimer = new DispatcherTimer();
            _closeTimer.Tick += OnCloseTimer;
            Root.MouseEnter += (s, e) =>
            {
                _closeTimer.Stop();
                RefreshGlassHover();
                if (HoverOpensPanel) ShowPanel();
            };
            Root.MouseLeave += (s, e) =>
            {
                RefreshGlassHover();
                if (Open) StartCloseTimer();
            };
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible) ClosePanelImmediately();
            };
            ThemeService.Changed += OnThemeChanged;
            MicaService.Changed += OnThemeChanged;
            // 胶囊位置/大小变化时要重算云母取样矩形。只在真的用云母时才做,
            // 由 UpdateMicaViewbox 内部的 _micaBrush == null 早退保证零成本。
            Pill.LayoutUpdated += (s2, e2) => UpdateMicaViewbox();
            ApplyTheme();
        }

        protected virtual bool HoverOpensPanel => true;
        protected virtual bool KeepPanelOpenOnPointerExit => false;
        private bool HasPanelInteraction => PanelWindow.IsMouseCaptureWithin || _openMenu?.IsOpen == true
            || _shellInteractions > 0 || FileDropInteractionActive || KeepPanelOpenOnPointerExit;
        protected virtual bool HasPanel => true;

        // ---- 取证(--glassshot)只读访问器:面板可视化自检需要知道"哪些模块会开面板" ----
        internal bool DiagnosticHasPanel => HasPanel;
        internal string DiagnosticName => GetType().Name;
        internal Window DiagnosticPanelWindow => PanelWindow;
        internal Border DiagnosticPanel => Panel;
        internal Rectangle DiagnosticPanelGlass => _panelGlass;
        internal bool DiagnosticPanelAnimated => _panelMotion.IsRunning || _blendingBackdrop;
        internal string DiagnosticPanelTransform => string.Format(
            "ScaleX={0:F3} ScaleY={1:F3} TY={2:F2} W={3:F1} H={4:F1} 跟踪={5} 推送={6} 缩放范围={7:F2}~{8:F2}",
            _panelScale.ScaleX, _panelScale.ScaleY, _panelTransform.Y,
            _panelSurface.ActualWidth, _panelSurface.ActualHeight, _panelTracking, _trackPushes, _trackMinScale, _trackMaxScale);
        /// <summary>展开页水平居中于选项卡(默认左对齐选项卡左缘)。</summary>
        protected virtual bool CenterPanelOnPill => false;
        protected virtual Size GetPanelEnvelope(Size content) => Size.Empty;
        protected virtual bool CaptureFullPanelEnvelope => true;
        protected virtual void OnPanelShown() { }
        protected virtual void OnPanelHidden() { }
        protected virtual void OnFileDropSettled() { }

        private void CreatePanelWindow()
        {
            var host = new Grid();
            host.Children.Add(_panelSurface);
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Topmost = true,
                Content = host,
                Visibility = Visibility.Hidden
            };
            w.SourceInitialized += (s, e) =>
            {
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                NativeMethods.ApplyNoActivateToolStyle(h);
                // 实时模式下登记为排除窗口，避免面板进入顶栏或展开页自己的采样。
                // 窗口懒创建:SourceInitialized 在首次 ShowPanel 时才触发,正合适。
                GlassService.RegisterWindow(h, hiddenPanel: true);
            };
            w.MouseEnter += (s, e) => _closeTimer.Stop();
            w.MouseLeave += (s, e) => { if (Open) StartCloseTimer(); };
            w.IsMouseCaptureWithinChanged += (_, _) =>
            {
                if (w.IsMouseCaptureWithin) _closeTimer.Stop();
                else if (Open && !PointerOverPanel()) StartCloseTimer();
            };
            w.LocationChanged += (s, e) => UpdatePanelFrame();
            Panel.MouseEnter += (s, e) => _closeTimer.Stop();
            Panel.MouseLeave += (s, e) => { if (Open) StartCloseTimer(); };
            w.FontFamily = Ui.TextFont;
            PanelWindow = w;
        }

        private void StartCloseTimer()
        {
            if (!Open || HasPanelInteraction) return;
            _closeTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(80, App.ActiveSettings?.HoverCloseDelayMs ?? 250));
            _closeTimer.Stop();
            _closeTimer.Start();
        }

        private void OnCloseTimer(object sender, EventArgs e)
        {
            if (PointerOverPanel())
            {
                if (Root.IsMouseOver || PanelWindow.IsMouseOver) _closeTimer.Stop();
                return;
            }

            _closeTimer.Stop();
            if (!HasPanelInteraction) HidePanel();
        }

        private bool PointerOverPanel()
        {
            if (!NativeMethods.TryGetCursorPosition(out int x, out int y)) return Root.IsMouseOver || PanelWindow.IsMouseOver;
            var point = new Point(x, y);
            if (ContainsScreenPoint(Pill, point)) return true;
            if (_panelSurface.Opacity > 0) return ContainsScreenPoint(_panelSurface, point);
            if (!Open || !UsesPanelGlass) return false;
            // 等首帧期间，指针已经移到展开页将要出现的位置就继续等待。
            var dpi = VisualTreeHelper.GetDpi(Pill);
            return GetPanelBounds().Contains(x / dpi.DpiScaleX, y / dpi.DpiScaleY);
        }

        private IntPtr _previousPanelForeground;

        protected void EnterPanelKeyboardMode(IInputElement target)
        {
            if (!Open || !PanelWindow.IsVisible) return;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(PanelWindow).Handle;
            if (_previousPanelForeground == IntPtr.Zero && NativeMethods.GetForegroundWindow() != hwnd)
                _previousPanelForeground = NativeMethods.GetForegroundWindow();
            NativeMethods.AllowActivation(hwnd);
            PanelWindow.Focusable = true;
            PanelWindow.Activate();
            Keyboard.Focus(target);
            _closeTimer.Stop();
        }

        protected void LeaveKeyboardMode()
        {
            bool keyboardMode = PanelWindow.Focusable;
            var hwnd = keyboardMode ? new System.Windows.Interop.WindowInteropHelper(PanelWindow).Handle : IntPtr.Zero;
            bool restore = hwnd != IntPtr.Zero && NativeMethods.GetForegroundWindow() == hwnd;
            PanelWindow.Focusable = false;
            if (FocusManager.GetFocusedElement(PanelWindow) != null) FocusManager.SetFocusedElement(PanelWindow, null);
            if (PanelWindow.IsKeyboardFocusWithin) Keyboard.ClearFocus();
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.ApplyNoActivateToolStyle(hwnd);
            if (restore && _previousPanelForeground != IntPtr.Zero)
                NativeMethods.SetForegroundWindow(_previousPanelForeground);
            _previousPanelForeground = IntPtr.Zero;
        }

        private static bool ContainsScreenPoint(FrameworkElement element, Point point)
            => element.IsVisible && PresentationSource.FromVisual(element) != null
                && new Rect(element.RenderSize).Contains(element.PointFromScreen(point));

        private Popup _openMenu;

        protected IntPtr FileOperationOwner => new System.Windows.Interop.WindowInteropHelper(
            PanelWindow.IsVisible ? PanelWindow : Window.GetWindow(this) ?? PanelWindow).Handle;

        protected void WireFileDrop(FrameworkElement target, string path, Action<Action> runDrop)
            => ShellDropTarget.Wire(target, path, runDrop, SetFileDropActive,
                ReferenceEquals(target, PanelWindow) ? _panelSurface : target);

        private void SetFileDropActive(bool active)
        {
            _fileDropTargets += active ? 1 : -1;
            if (active)
            {
                _dropSettleTimer?.Stop();
                _closeTimer.Stop();
                if (_closing && PanelWindow.IsVisible)
                {
                    _closing = false;
                    Open = true;
                    _panelMotion.Reset(1);
                    UpdatePanelFrame();
                    if (!_blendingBackdrop) StopPanelTracking();
                }
                GlassService.SetPanelCapturePaused(Panel, true);
            }
            else if (_fileDropTargets == 0 && !_disposed)
            {
                if (_dropSettleTimer == null)
                {
                    _dropSettleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(150)
                    };
                    _dropSettleTimer.Tick += (_, _) =>
                    {
                        _dropSettleTimer.Stop();
                        GlassService.SetPanelCapturePaused(Panel, false);
                        if (!Open) return;
                        // OLE can leave WPF's hover state stale until the next mouse message.
                        StartCloseTimer();
                        OnFileDropSettled();
                    };
                }
                _dropSettleTimer.Start();
            }
        }

        protected void RunFileOperation(Action action)
        {
            _closeTimer.Stop();
            _shellInteractions++;
            try { ShellFileActions.Run(action); }
            finally
            {
                _shellInteractions--;
                if (Open && !Root.IsMouseOver && !Panel.IsMouseOver) StartCloseTimer();
            }
        }

        /// <summary>登记正在打开的右键菜单：期间不自动收起面板，菜单关闭后恢复。</summary>
        protected void TrackFlyout(Popup menu)
        {
            if (menu == null) return;
            if (_openMenu != null && !ReferenceEquals(_openMenu, menu))
            {
                try { _openMenu.IsOpen = false; } catch { }
            }
            _openMenu = menu;
            _closeTimer.Stop();
            menu.Closed += (s, e) =>
            {
                if (!ReferenceEquals(_openMenu, menu)) return;
                _openMenu = null;
                // 菜单已关闭；鼠标若仍停在胶囊或展开页上则保持打开。
                if (Open && !Root.IsMouseOver && !Panel.IsMouseOver) StartCloseTimer();
            };
        }

        private void ApplyTheme()
        {
            var s = App.ActiveSettings;
            double scale = Ui.Scale;
            ApplyPillBackground(s, scale);
            Pill.BorderBrush = s != null && s.BorderEnabled ? Ui.Brush(ThemeService.Border, s.BorderOpacity) : Brushes.Transparent;
            Pill.BorderThickness = s != null && s.BorderEnabled ? new Thickness(Math.Max(0, s.BorderThickness * scale)) : new Thickness(0);
            // Reuse the pill brush so the expanded page has exactly the same color and alpha.
            // 材质档(云母/亚克力)例外:展开页会伸到顶栏条带之外,没有壁纸可取样,
            // 只能用材质平均色平涂(真实 Mica/Acrylic 弹层因为模糊极重,看起来也接近平涂,
            // 观感损失很小)。玻璃档同理:展开页是独立窗口,底下没有玻璃层,退主题色。
            // 玻璃/液态玻璃档:展开页的玻璃由底下的玻璃层(带着色器的 Rectangle)画,
            // 面板自身必须完全透明,否则会把玻璃层整个盖住(以前面板只有一块背景贴图,
            // 所以这里退主题色;现在改实时玻璃层,面板只负责承载文字/控件)。
            _panelChrome.Background = UsesPanelGlass
                ? Brushes.Transparent
                : (Pill.Background is ImageBrush
                    ? new SolidColorBrush(MicaService.Average) { Opacity = MicaService.BackdropOpacity }
                    : Pill.Background);
            _panelChrome.BorderBrush = Pill.BorderBrush;
            _panelChrome.BorderThickness = Pill.BorderThickness;
            Panel.BorderThickness = Pill.BorderThickness;
            PanelWindow.Background = Brushes.Transparent;
            var ink = Ui.Brush(ThemeService.Text);
            Resources[ContentInkKey] = ink;
            PanelWindow.Resources[ContentInkKey] = ink;
            if (!UsesPanelGlass || !GlassService.UsesAdaptiveInk(s))
            {
                ApplyPillInk(null);
                SetGlassInk(Panel, null, panel: true);
            }
            foreach (var (tb, delta) in _texts) ApplyFont(tb, delta);
        }

        internal void ApplyPillInk(double? lightness, double spread = 0)
        {
            if (!lightness.HasValue)
            {
                CancelPendingPillInk();
                SetGlassInk(Pill, null);
                _hasInkBacking = false;
                RefreshGlassHover();
                return;
            }

            var previous = Pill.Resources[ContentInkKey] as Brush;
            var next = PreferredGlassInk(lightness.Value, previous);
            bool backing = spread >= (_hasInkBacking ? .47 : .58);
            if (previous == null)
            {
                Pill.Resources[ContentInkKey] = next;
                previous = next;
            }
            if (ReferenceEquals(previous, next) && _hasInkBacking == backing)
            {
                CancelPendingPillInk();
                return;
            }
            if (ReferenceEquals(_pendingPillInk, next) && _pendingInkBacking == backing) return;

            // 新的明暗判断稳定一小段时间再换色。最后一帧之后即使没有新图，也由定时器提交。
            _pendingPillInk = next;
            _pendingInkBacking = backing;
            if (_pillInkTimer == null)
            {
                _pillInkTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(160)
                };
                _pillInkTimer.Tick += (sender, args) =>
                {
                    var confirmed = _pendingPillInk;
                    CancelPendingPillInk();
                    if (confirmed != null && !_disposed && IsVisible && GlassService.UsesAdaptiveInk(App.ActiveSettings))
                    {
                        Pill.Resources[ContentInkKey] = confirmed;
                        _hasInkBacking = _pendingInkBacking;
                        RefreshGlassHover();
                    }
                };
            }
            _pillInkTimer.Stop();
            _pillInkTimer.Start();
        }

        private void CancelPendingPillInk()
        {
            if (_pendingPillInk == null) return;
            _pendingPillInk = null;
            _pillInkTimer?.Stop();
        }

        internal static Brush PreferredGlassInk(double lightness, Brush previous = null)
        {
            // 两种墨色对比度接近时，保持上一帧颜色，避免幻灯片过渡或鼠标移动引起闪烁。
            double threshold = previous == DarkGlassInk ? 0.18 : previous == LightGlassInk ? 0.22 : 0.20;
            return lightness >= threshold ? DarkGlassInk : LightGlassInk;
        }

        internal static void SetGlassInk(Border surface, double? lightness, bool panel = false)
        {
            if (surface == null) return;
            var resources = surface.Resources;
            var previous = resources[ContentInkKey] as Brush;
            if (!lightness.HasValue)
            {
                if (previous == null) return;
                resources.Remove(ContentInkKey);
                if (panel)
                {
                    foreach (var key in PanelContrastKeys) resources.Remove(key.Name);
                }
                return;
            }
            var next = PreferredGlassInk(lightness.Value, previous);
            if (previous == next) return;
            resources[ContentInkKey] = next;
            if (!panel) return;
            string color = next == DarkGlassInk ? "#101820" : "#FBFDFF";
            foreach (var key in PanelContrastKeys) resources[key.Name] = Ui.Brush(color, key.Opacity);
        }

        private static readonly (string Name, double Opacity)[] PanelContrastKeys =
        {
            ("ThemePanelText", 1), ("ThemePanelSubtle", 0.06), ("ThemePanelControl", 0.10),
            ("ThemePanelControlHover", 0.18), ("ThemePanelHover", 0.12), ("ThemePanelSelected", 0.20)
        };

        private void RefreshGlassHover()
        {
            if (!UsesPanelGlass) return;
            Brush background = _hasInkBacking ? (Pill.Resources[ContentInkKey] == DarkGlassInk ? DarkTextBacking : LightTextBacking)
                : App.ActiveSettings.PillMaterial == 4 && Root.IsMouseOver ? GlassHover : Brushes.Transparent;
            if (Pill.Background != background) Pill.Background = background;
        }

        private void OnThemeChanged()
        {
            if (Dispatcher.CheckAccess()) ApplyTheme();
            else Dispatcher.BeginInvoke(new Action(ApplyTheme));
        }

        /// <summary>
        /// 云母画笔与它当前对应的取样矩形。取样矩形随胶囊位置/大小变化,
        /// 所以每次布局变化都要重算一次(只改画刷属性,不重新烘焙位图,很便宜)。
        /// </summary>
        private ImageBrush _micaBrush;
        private Rect _micaViewbox = Rect.Empty;
        private bool _micaLogged;

        /// <summary>
        /// 按材质设置给胶囊上背景。云母/亚克力档用一支 <see cref="ImageBrush"/> 从烘焙位图里
        /// 截取"胶囊在屏幕上的那一段",所以每个胶囊看到的明暗分布和真实桌面一致;
        /// 玻璃档(3)胶囊自身不画背景——玻璃画在主窗最底层的玻璃层上,胶囊透明透出即可。
        /// </summary>
        private void ApplyPillBackground(AppSettings s, double scale)
        {
            if (UsesPanelGlass)
            {
                // 实时玻璃:背景在主窗玻璃层。这里必须用 Transparent 而不是 null:
                // 胶囊的悬停(MouseEnter/Leave → 展开页)依赖胶囊自身是一块可命中面,
                // null 会让胶囊空白区域不参与命中,悬停开面板就坏了;Transparent 视觉相同。
                _micaBrush = null;
                _micaViewbox = Rect.Empty;
                RefreshGlassHover();
                return;
            }
            bool mica = s != null && (s.PillMaterial == 1 || s.PillMaterial == 2) && MicaService.Backdrop != null;
            if (!mica)
            {
                _micaBrush = null;
                _micaViewbox = Rect.Empty;
                Pill.Background = Ui.Brush(ThemeService.Background, s?.ThemeOpacity ?? 0.55);
                return;
            }

            double opacity = MicaService.BackdropOpacity;
            if (_micaBrush == null || !ReferenceEquals(_micaBrush.ImageSource, MicaService.Backdrop))
            {
                _micaBrush = MicaService.CreateBrush(Rect.Empty, opacity);
                _micaViewbox = Rect.Empty; // 新画刷要从零开始定标
            }
            else
            {
                _micaBrush.Opacity = opacity;
            }
            if (_micaBrush == null)
            {
                // 极端情况:位图在两次调用之间被清掉了,退回纯色保证可见
                Pill.Background = Ui.Brush(ThemeService.Background, s?.ThemeOpacity ?? 0.55);
                return;
            }
            Pill.Background = _micaBrush;
            UpdateMicaViewbox();
        }

        /// <summary>把云母画刷的取样矩形同步到胶囊当前在屏幕上的物理位置。</summary>
        private void UpdateMicaViewbox()
        {
            if (_micaBrush == null) return;
            try
            {
                var vb = MicaService.ViewboxFor(Pill);
                if (vb.IsEmpty || vb == _micaViewbox) return;
                _micaViewbox = vb;
                _micaBrush.Viewbox = vb;
                // 每个模块只记一次:用来核对"胶囊在屏幕上的物理矩形 → 云母位图坐标"映射对不对。
                if (!_micaLogged)
                {
                    _micaLogged = true;
                    App.Log(string.Format("云母胶囊: {0} 取样矩形=({1:F1},{2:F1},{3:F1}x{4:F1}) 位图={5}x{6} 出血={7:F1}",
                        GetType().Name, vb.X, vb.Y, vb.Width, vb.Height,
                        MicaService.Backdrop.PixelWidth, MicaService.Backdrop.PixelHeight, MicaService.Bleed));
                }
            }
            catch { }
        }

        public void ShowPanel()
        {
            if (_disposed || !HasPanel || !IsVisible || !IsLoaded) return;
            if (ReferenceEquals(_active, this))
            {
                if (_closing)
                {
                    _closeTimer.Stop();
                    Open = true;
                    _closing = false;
                    AnimatePanel(open: true);
                    return;
                }
                if (Open) return;
            }
            // Register capture affinity on a hidden HWND before the first frame starts.
            // Modules that are never opened do not need a native flyout window at startup.
            IntPtr hwnd;
            try { hwnd = new System.Windows.Interop.WindowInteropHelper(PanelWindow).EnsureHandle(); }
            catch (Exception ex)
            {
                App.Log("无法创建展开页窗口: " + ex.Message);
                return;
            }
            if (_active != null && !ReferenceEquals(_active, this)) _active.HidePanelImmediate();
            _active = this;
            Open = true;
            _closing = false;
            int openVersion = ++_panelGeneration;
            _closeTimer.Stop();
            OnPanelShown();

            double gap = 8 * Ui.Scale;
            // PointToScreen 返回物理像素,而 PanelWindow.Left/Top 与 WorkArea 都是 DIP:
            // 先按本胶囊的 DPI 换算回 DIP,后面所有几何计算统一在 DIP 域进行,
            // 否则高缩放下展开页错位约 (scale-1)*100%,贴边 clamp 与上下翻转判断也全部失真。
            var dpi = VisualTreeHelper.GetDpi(Pill);
            var screenPt = Pill.PointToScreen(new Point(0, 0));
            var p = new Point(screenPt.X / dpi.DpiScaleX, screenPt.Y / dpi.DpiScaleY);
            var pillBounds = new Rect(p, new Size(Pill.ActualWidth, Pill.ActualHeight));
            Panel.Visibility = Visibility.Visible;
            MeasurePanel();
            double width = Math.Max(1, Panel.DesiredSize.Width);
            double height = Math.Max(1, Panel.DesiredSize.Height);
            var work = SystemParameters.WorkArea;
            if (App.Bar?.IsOnSecondaryScreen == true)
            {
                var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)screenPt.X, (int)screenPt.Y)).WorkingArea;
                work = new Rect(screen.Left / dpi.DpiScaleX, screen.Top / dpi.DpiScaleY,
                    screen.Width / dpi.DpiScaleX, screen.Height / dpi.DpiScaleY);
            }
            _panelEnvelope = GetPanelEnvelope(new Size(width, height));
            bool reserve = !_panelEnvelope.IsEmpty;
            if (reserve)
                _panelEnvelope = new Size(Math.Min(_panelEnvelope.Width, work.Width), Math.Min(_panelEnvelope.Height, work.Height));
            double hostWidth = reserve ? _panelEnvelope.Width : width;
            double hostHeight = reserve ? _panelEnvelope.Height : height;
            // Keep resizable content inside a single layered HWND for the duration of the flyout.
            double left = CenterPanelOnPill
                ? p.X + (Pill.ActualWidth - width) / 2
                : p.X;
            left = Math.Max(work.Left, Math.Min(work.Right - hostWidth, left));
            double below = pillBounds.Bottom + gap;
            double upperEdge = pillBounds.Top - gap;
            bool above = below + hostHeight > work.Bottom && (upperEdge - hostHeight >= work.Top
                || reserve && upperEdge - work.Top > work.Bottom - below);
            Panel.RenderTransformOrigin = new Point(.5, above ? 1 : 0);
            _panelMotionInset = SystemParameters.ClientAreaAnimation ? gap * .5 : 0;
            _panelSurface.Margin = above ? new Thickness(0, 0, 0, _panelMotionInset) : new Thickness(0, _panelMotionInset, 0, 0);
            if (reserve)
            {
                hostHeight = Math.Min(hostHeight, Math.Max(height, above ? upperEdge - work.Top : work.Bottom - below));
                _panelEnvelope = new Size(hostWidth, hostHeight);
                _panelCaptureHeight = CaptureFullPanelEnvelope ? hostHeight : Math.Min(hostHeight, height + 96 * Ui.Scale);
                _panelSurface.HorizontalAlignment = HorizontalAlignment.Left;
                _panelSurface.VerticalAlignment = above ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                _panelSurface.MaxWidth = hostWidth;
                _panelSurface.MaxHeight = hostHeight;
                _panelSurface.ClipToBounds = true;
                PanelWindow.SizeToContent = SizeToContent.Manual;
                PanelWindow.Width = hostWidth;
                PanelWindow.Height = hostHeight + _panelMotionInset;
            }
            double targetTop = above ? upperEdge - hostHeight : below;
            _panelAbove = above;
            _panelLeft = left;
            _panelTop = above && reserve ? targetTop + hostHeight - height : targetTop;
            _panelWidth = width;
            _panelHeight = height;
            _panelMotion.Reset(0);
            PanelWindow.Left = left;
            PanelWindow.Top = targetTop - (above ? 0 : _panelMotionInset);
            bool glass = UsesPanelGlass;
            bool windowBackdrop = glass && (App.ActiveSettings.ShowInScreenCapture || App.ActiveSettings.WallpaperOnlyCapture);
            UpdatePanelFrame();
            bool revealed = false;

            void Reveal()
            {
                if (revealed || openVersion != _panelGeneration || !Open || _closing) return;
                CancelPanelFallback();
                revealed = true;
                if (Environment.GetEnvironmentVariable("YUKI_PANEL_SHOWLOG") == "1")
                {
                    App.Log($"面板显形 {GetType().Name} Effect={_panelGlass.Effect?.GetType().Name} Fill={_panelGlass.Fill}");
                }
                if (glass && !windowBackdrop) GlassService.PreparePanelVisibility(hwnd);
                PanelWindow.Visibility = Visibility.Visible;
                AnimatePanel(open: true);
            }

            if (glass)
            {
                if (windowBackdrop)
                {
                    // WGC walks the z-order below this HWND. Show it at zero opacity first so
                    // the scan starts from its on-screen position in the topmost group.
                    PanelWindow.Show();
                }
                var capture = GetPanelCaptureBounds(GetPanelBounds());
                GlassService.StartLivePanel(Panel, _panelGlass, capture.X, capture.Y, capture.Width, capture.Height, () =>
                {
                    if (openVersion != _panelGeneration) return;
                    if (_panelBackdropFade.Visibility == Visibility.Visible)
                    {
                        if (SystemParameters.ClientAreaAnimation)
                        {
                            _blendingBackdrop = true;
                            _backdropBlendStart = null;
                            StartPanelTracking();
                        }
                        else ClearPanelBackdropFade();
                    }
                    Reveal();
                });
                UpdatePanelFrame();
                if (FileDropInteractionActive) GlassService.SetPanelCapturePaused(Panel, true);
                if (!revealed) SchedulePanelFallback(ShowPanelFallback);
            }
            else
            {
                GlassService.StopLivePanel(Panel);
                Reveal();
            }

            void ShowPanelFallback()
            {
                if (revealed || openVersion != _panelGeneration || !Open || _closing) return;
                var fallback = Ui.Brush("#222228", 0.75);
                _panelBackdropFade.Fill = fallback;
                _fallbackOpacity = Math.Max(0.9, App.ActiveSettings.GlassOpacity);
                _fallbackAlpha = fallback.Color.A / 255.0 * fallback.Opacity * _fallbackOpacity;
                _panelBackdropFade.Opacity = _fallbackOpacity;
                _panelBackdropFade.Visibility = Visibility.Visible;
                _panelLiveLayer.Opacity = 0;
                SetGlassInk(Panel, 0, panel: true);
                Reveal();
            }
        }

        private void SchedulePanelFallback(Action fallback)
        {
            if (_firstFrameTimer == null)
            {
                _firstFrameTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(750)
                };
                _firstFrameTimer.Tick += (_, _) =>
                {
                    var showFallback = _firstFrameFallback;
                    CancelPanelFallback();
                    showFallback?.Invoke();
                };
            }
            // 首帧就绪或面板关闭时撤销定时器，避免频繁开合时留下旧唤醒。
            _firstFrameFallback = fallback;
            _firstFrameTimer.Start();
        }

        private void CancelPanelFallback()
        {
            _firstFrameTimer?.Stop();
            _firstFrameFallback = null;
        }

        private void ClearPanelBackdropFade()
        {
            _blendingBackdrop = false;
            _backdropBlendStart = null;
            _panelLiveLayer.Opacity = 1;
            _panelBackdropFade.Visibility = Visibility.Collapsed;
            _panelBackdropFade.Fill = null;
        }

        private void MeasurePanel() => Panel.Measure(new Size(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));

        protected void PreparePanelLayout()
        {
            var visibility = Panel.Visibility;
            Panel.Visibility = Visibility.Visible;
            MeasurePanel();
            Panel.Arrange(new Rect(Panel.DesiredSize));
            Panel.Visibility = visibility;
        }

        private void AnimatePanel(bool open)
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                if (open)
                {
                    _panelMotion.Reset(1);
                    UpdatePanelFrame();
                }
                else HidePanelImmediate();
                return;
            }
            double duration = Math.Clamp((App.ActiveSettings?.AnimationDurationMs ?? 180) + 10, 85, 900);
            _panelMotion.MoveTo(open ? 1 : 0, duration * (open ? 1 : .72));
            if (!_panelMotion.IsRunning)
            {
                if (!open) HidePanelImmediate();
                else
                {
                    UpdatePanelFrame();
                }
                return;
            }
            StartPanelTracking();
        }

        private double ApplyPanelMotion(Rect bounds)
        {
            double progress = _panelMotion.Progress;
            double distance = 1 - progress;
            double reveal = Math.Min(14 * Ui.Scale, bounds.Height * .10) * distance;
            double height = bounds.Height - reveal;
            _panelScale.ScaleY = height / bounds.Height;
            // The flyout keeps its full width and the controls retain their natural proportions.
            _panelContentScale.ScaleY = 1 / _panelScale.ScaleY;
            double offset = _panelMotionInset * distance;
            _panelTransform.Y = _panelAbove ? reveal + offset : -offset;
            _panelSurface.Opacity = 1 - distance * distance;
            Panel.Opacity = PanelMotion.Ease(Math.Clamp((progress - .08) / .64, 0, 1));

            double radius = Math.Min(Math.Min(bounds.Width, height) / 2, Panel.CornerRadius.TopLeft);
            _panelClip.Rect = new Rect(bounds.Size);
            _panelClip.RadiusX = radius;
            _panelClip.RadiusY = radius / _panelScale.ScaleY;
            return radius;
        }

        private void StartPanelTracking()
        {
            if (_panelTracking) return;
            _panelTracking = true;
            _lastPanelFrame = null;
            _trackMinScale = 1.0;
            _trackMaxScale = 1.0;
            _trackPushes = 0;
            CompositionTarget.Rendering += OnPanelRendering;
        }

        private void StopPanelTracking()
        {
            if (!_panelTracking) return;
            _panelTracking = false;
            CompositionTarget.Rendering -= OnPanelRendering;
            // 一次性诊断:动画期间到底有没有"每帧按当前窗格大小"重采(缩放范围说明动画中采到了中间尺寸)。
            if (Environment.GetEnvironmentVariable("YUKI_PANEL_TRACK_LOG") == "1")
                App.Log(string.Format("面板动画跟踪:{0} 推送={1} 缩放范围={2:F2}~{3:F2}",
                    GetType().Name, _trackPushes, _trackMinScale, _trackMaxScale));
        }

        private void OnPanelRendering(object sender, EventArgs e)
        {
            if (_disposed || (!Open && !_closing)) { StopPanelTracking(); return; }
            var renderingTime = ((RenderingEventArgs)e).RenderingTime;
            if (_lastPanelFrame == renderingTime) return;
            _lastPanelFrame = renderingTime;

            if (_panelMotion.IsRunning)
            {
                _panelMotion.Advance(renderingTime);
                UpdatePanelFrame();
            }
            if (_closing && !_panelMotion.IsRunning)
            {
                HidePanelImmediate();
                return;
            }

            if (_blendingBackdrop)
            {
                var start = _backdropBlendStart ??= renderingTime;
                const double blendDuration = 78;
                double elapsed = (renderingTime - start).TotalMilliseconds;
                if (elapsed >= blendDuration) ClearPanelBackdropFade();
                else
                {
                    double mix = PanelMotion.Ease(elapsed / blendDuration);
                    _panelBackdropFade.Opacity = _fallbackOpacity * (1 - mix);
                    // Compensate for the fallback layered above the live glass so the
                    // middle of the handoff maintains the intended alpha.
                    _panelLiveLayer.Opacity = mix == 0 ? 0 : Math.Min(1, mix / (1 - _fallbackAlpha * (1 - mix)));
                }
            }
            if (!_panelMotion.IsRunning && !_blendingBackdrop) StopPanelTracking();
        }

        private Rect GetPanelBounds()
        {
            double fullW = PanelWindow.IsVisible && _panelSurface.ActualWidth > 1 ? _panelSurface.ActualWidth : _panelWidth;
            double fullH = PanelWindow.IsVisible && _panelSurface.ActualHeight > 1 ? _panelSurface.ActualHeight : _panelHeight;
            var margin = _panelSurface.Margin;
            double fullLeft = double.IsNaN(PanelWindow.Left) ? _panelLeft : PanelWindow.Left + margin.Left;
            double fullTop = double.IsNaN(PanelWindow.Top) ? _panelTop : PanelWindow.Top + margin.Top;
            if (_panelAbove && !_panelEnvelope.IsEmpty && !double.IsNaN(PanelWindow.Top))
                fullTop += _panelEnvelope.Height - fullH;
            return new Rect(fullLeft, fullTop, fullW, fullH);
        }

        private Rect GetPanelCaptureBounds(Rect bounds)
        {
            var capture = bounds;
            if (!_panelEnvelope.IsEmpty)
            {
                if (bounds.Height > _panelCaptureHeight)
                    _panelCaptureHeight = Math.Min(_panelEnvelope.Height, bounds.Height + 96 * Ui.Scale);
                double captureTop = _panelAbove ? bounds.Bottom - _panelCaptureHeight : bounds.Top;
                capture = new Rect(bounds.X, captureTop, _panelEnvelope.Width, _panelCaptureHeight);
            }
            if (!_panelAbove) capture.Y -= _panelMotionInset;
            capture.Height += _panelMotionInset;
            return capture;
        }

        private void UpdatePanelFrame()
        {
            if (_disposed || (!Open && !_closing)) return;
            var bounds = GetPanelBounds();
            if (bounds.Width < 1 || bounds.Height < 1) return;
            double radius = ApplyPanelMotion(bounds);
            if (!UsesPanelGlass) return;
            double sy = _panelScale.ScaleY;
            if (sy < _trackMinScale) _trackMinScale = sy;
            if (sy > _trackMaxScale) _trackMaxScale = sy;
            _trackPushes++;
            var visible = new Rect(bounds.X, bounds.Y + _panelTransform.Y, bounds.Width, bounds.Height * sy);
            GlassService.UpdateLivePanelRect(Panel, GetPanelCaptureBounds(bounds), bounds, visible, sy, radius);
        }

        protected void SetPanelWidth(double width, bool animate = true)
        {
            if ((double)Panel.GetAnimationBaseValue(WidthProperty) == width) return;
            double from = Panel.ActualWidth;
            Panel.BeginAnimation(WidthProperty, null);
            Panel.Width = width;
            if (!animate || !Panel.IsVisible || from <= 0 || !SystemParameters.ClientAreaAnimation) return;
            Panel.BeginAnimation(WidthProperty, new DoubleAnimation(from, width, TimeSpan.FromMilliseconds(255))
            {
                EasingFunction = new PanelEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop
            });
        }

        public void HidePanel() => HidePanelAnimated();

        private void HidePanelAnimated()
        {
            if (!Open || _closing) return;
            if (!PanelWindow.IsVisible || _panelMotion.Progress == 0)
            {
                HidePanelImmediate();
                return;
            }
            Open = false;
            _closing = true;
            LeaveKeyboardMode();
            if (_closing) AnimatePanel(open: false);
        }

        /// <summary>宿主隐藏、模块禁用或退出时立即收起独立展开窗口。</summary>
        public void ClosePanelImmediately() => HidePanelImmediate();

        private void HidePanelImmediate()
        {
            _panelGeneration++;
            CancelPanelFallback();
            bool wasOpen = Open || _closing || PanelWindow.Visibility == Visibility.Visible;
            _closeTimer.Stop();
            Open = false;
            _closing = false;
            LeaveKeyboardMode();
            if (_openMenu != null)
            {
                try { _openMenu.IsOpen = false; } catch { }
                _openMenu = null;
            }
            _panelMotion.Reset(0);
            _panelSurface.Opacity = 0;
            StopPanelTracking();
            ClearPanelBackdropFade();
            PanelWindow.Visibility = Visibility.Hidden;
            GlassService.StopLivePanel(Panel);
            Panel.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(_active, this)) _active = null;
            if (wasOpen)
            {
                OnPanelHidden();
                PanelHidden?.Invoke(this);
            }
        }

        public virtual void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _dropSettleTimer?.Stop();
            CancelPendingPillInk();
            _closeTimer.Stop();
            if (ReferenceEquals(_active, this)) _active = null;
            ThemeService.Changed -= OnThemeChanged;
            MicaService.Changed -= OnThemeChanged;
            HidePanelImmediate();
            try
            {
                var h = new System.Windows.Interop.WindowInteropHelper(PanelWindow).Handle;
                if (h != IntPtr.Zero) GlassService.ForgetWindow(h);
            }
            catch { }
            try { PanelWindow.Close(); } catch { }
        }

        protected void SetTabStyle(TabStyle st)
        {
            _style = st ?? _style;
            ApplyTheme();
        }

        private static bool UsesPanelGlass => App.Bar?.IsOnSecondaryScreen != true && App.ActiveSettings?.PillMaterial is 3 or 4;

        internal void RefreshThemeForScreen() => ApplyTheme();

        protected TextBlock MakeText(string text = "", double delta = 0)
        {
            var t = new TextBlock
            {
                Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold
            };
            _texts.Add((t, delta));
            ApplyFont(t, delta);
            return t;
        }

        protected TextBlock MakePillText(string text, double delta = 0)
        {
            var label = MakeText(text, delta);
            label.FontWeight = FontWeights.Medium;
            return label;
        }

        private void ApplyFont(TextBlock t, double delta)
        {
            t.FontFamily = Ui.TextFont;
            t.FontSize = Math.Max(9, (14 + delta) * Ui.Scale);
            t.SetResourceReference(TextBlock.ForegroundProperty, ContentInkKey);
        }

        protected TextBlock MakePanelTitle(string text)
        {
            // 登记进 _texts 随主题刷新，同时应用缩放和面板字色。
            var t = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 0, 2, 8)
            };
            _texts.Add((t, 1));
            ApplyFont(t, 1);
            return t;
        }

        protected FrameworkElement CenterIcon(FrameworkElement icon)
        {
            var host = new Grid
            {
                Width = 38 * Ui.Scale,
                Height = 38 * Ui.Scale,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            host.Children.Add(icon);
            return host;
        }

        // 顶栏矢量图标统一在 24×24 设计网格中按标准比例绘制，
        // 再由 Viewbox 等比缩放，避免 Stretch.Fill 拉伸变形。
        private Viewbox IconViewbox(double size, FrameworkElement content)
        {
            double s = Ui.Scale;
            return new Viewbox
            {
                Width = size * s,
                Height = size * s,
                Stretch = Stretch.Uniform,
                Child = content,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        protected FrameworkElement MakePowerGlyph(double size = 22)
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            var path = new Path
            {
                Data = Geometry.Parse("M12,1.5 L12,10.5 M5.68,6.81 A8.5,8.5 0 1 0 18.32,6.81"),
                Stretch = Stretch.None,
                Stroke = Ui.Brush(ThemeService.Text, 1),
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            path.SetResourceReference(Shape.StrokeProperty, ContentInkKey);
            canvas.Children.Add(path);
            return IconViewbox(size, canvas);
        }

        protected FrameworkElement MakeTrashGlyph(double size = 22)
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            var path = new Path
            {
                // 提手 + 盖沿 + 梯形桶身（Z 闭合出斜边）+ 两道竖线，24 网格描边风格。
                Data = Geometry.Parse(
                    "M9.2,6.6 L9.2,4.6 Q9.2,3.6 10.2,3.6 L13.8,3.6 Q14.8,3.6 14.8,4.6 L14.8,6.6 " +
                    "M4.2,6.6 L19.8,6.6 " +
                    "M5.6,6.6 L18.4,6.6 L17.65,19.3 Q17.55,20.4 16.45,20.4 L7.55,20.4 Q6.45,20.4 6.35,19.3 Z " +
                    "M10.2,10.3 L10.2,16.7 M13.8,10.3 L13.8,16.7"),
                Stroke = Ui.Brush(ThemeService.Text, 1),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.None
            };
            path.SetResourceReference(Shape.StrokeProperty, ContentInkKey);
            canvas.Children.Add(path);
            return IconViewbox(size, canvas);
        }

        // 此电脑:显示器(圆角屏幕 + 支柱 + 底座)，与其他顶栏图标同一 24 网格描边风格。
        protected FrameworkElement MakeThisPcGlyph(double size = 20.5)
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            var path = new Path
            {
                Data = Geometry.Parse(
                    "M4.4,4.2 L19.6,4.2 Q21.2,4.2 21.2,5.8 L21.2,14 Q21.2,15.6 19.6,15.6 L4.4,15.6 Q2.8,15.6 2.8,14 L2.8,5.8 Q2.8,4.2 4.4,4.2 Z " +
                    "M12,15.6 L12,18.4 M7.8,19.6 L16.2,19.6"),
                Stroke = Ui.Brush(ThemeService.Text, 1),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.None
            };
            path.SetResourceReference(Shape.StrokeProperty, ContentInkKey);
            canvas.Children.Add(path);
            return IconViewbox(size, canvas);
        }

        protected FrameworkElement MakeSpeakerGlyph(out Path wave1, out Path wave2, out Path mute, double size = 23)
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            var body = new Path
            {
                Data = Geometry.Parse("M3,9.5 L7,9.5 L11.5,5.5 L11.5,18.5 L7,14.5 L3,14.5 Z"),
                Fill = Ui.Brush(ThemeService.Text, 1),
                Stretch = Stretch.None
            };
            body.SetResourceReference(Shape.FillProperty, ContentInkKey);
            canvas.Children.Add(body);
            wave1 = new Path
            {
                Data = Geometry.Parse("M14.5,9 A4.2,4.2 0 0 1 14.5,15"),
                Stroke = Ui.Brush(ThemeService.Text, 1),
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.None
            };
            wave2 = new Path
            {
                Data = Geometry.Parse("M17.2,6.8 A6.0,6.0 0 0 1 17.2,17.2"),
                Stroke = Ui.Brush(ThemeService.Text, 1),
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.None
            };
            wave1.SetResourceReference(Shape.StrokeProperty, ContentInkKey);
            wave2.SetResourceReference(Shape.StrokeProperty, ContentInkKey);
            canvas.Children.Add(wave1);
            canvas.Children.Add(wave2);
            mute = new Path
            {
                Data = Geometry.Parse("M15.5,10 L20.5,15 M20.5,10 L15.5,15"),
                Stroke = Ui.Brush(ThemeService.Text, 1), StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0
            };
            mute.SetResourceReference(Shape.StrokeProperty, ContentInkKey);
            canvas.Children.Add(mute);
            return IconViewbox(size, canvas);
        }

        protected TextBlock MakeGlyph(string glyph, double size)
        {
            return MakeIconGlyph(glyph, size, "Segoe UI Symbol");
        }        protected TextBlock MakeIconGlyph(string glyph, double size, string family)
        {
            double scale = Ui.Scale;
            var t = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily(family),
                FontSize = size * scale,
                Width = 38 * scale,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = Ui.Brush(ThemeService.Text, 1)
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, ContentInkKey);
            return t;
        }

        protected FrameworkElement MakeFolderGlyph(double size = 18)
        {
            double s = Ui.Scale;
            var path = new Path
            {
                Data = Geometry.Parse("M1,4 L7,4 L9,2 L17,2 Q19,2 19,4 L19,15 Q19,17 17,17 L2,17 Q0,17 0,15 L0,6 Q0,4 1,4 Z"),
                Fill = Brushes.Transparent,
                Stroke = Ui.Brush(ThemeService.Text, 1),
                StrokeThickness = 1.35 * s,
                Stretch = Stretch.Fill,
                Width = size * s,
                Height = size * 0.82 * s,
                VerticalAlignment = VerticalAlignment.Center
            };
            path.SetResourceReference(Shape.StrokeProperty, ContentInkKey);
            return path;
        }

        // 性能监测图标:统一 24 网格描边风格,随主题文字色变化。
        private FrameworkElement StrokeGlyph(string data, double size, double thickness = 1.7)
        {
            var path = new Path
            {
                Data = Geometry.Parse(data),
                Stroke = Ui.Brush(ThemeService.Text, 1),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.None
            };
            path.SetResourceReference(Shape.StrokeProperty, ContentInkKey);
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(path);
            return IconViewbox(size, canvas);
        }

        /// <summary>CPU 芯片:圆角封装 + 内核 + 四边引脚。</summary>
        protected FrameworkElement MakeCpuGlyph(double size = 20)
        {
            return StrokeGlyph(
                "M8.8,6.8 L15.2,6.8 Q17.2,6.8 17.2,8.8 L17.2,15.2 Q17.2,17.2 15.2,17.2 L8.8,17.2 Q6.8,17.2 6.8,15.2 L6.8,8.8 Q6.8,6.8 8.8,6.8 Z " +
                "M9.8,9.8 L14.2,9.8 L14.2,14.2 L9.8,14.2 Z " +
                "M9.5,4.6 L9.5,6.8 M14.5,4.6 L14.5,6.8 M9.5,17.2 L9.5,19.4 M14.5,17.2 L14.5,19.4 " +
                "M4.6,9.5 L6.8,9.5 M4.6,14.5 L6.8,14.5 M17.2,9.5 L19.4,9.5 M17.2,14.5 L19.4,14.5",
                size, 1.6);
        }

        /// <summary>显卡:板卡外形 + 风扇圆 + 供电槽 + 底部挡板脚。</summary>
        protected FrameworkElement MakeGpuGlyph(double size = 21)
        {
            return StrokeGlyph(
                "M3.2,7.2 L20.8,7.2 L20.8,15.8 L3.2,15.8 Z " +
                "M6,11.5 A2.8,2.8 0 1 1 11.6,11.5 A2.8,2.8 0 1 1 6,11.5 " +
                "M14.4,10 L18.8,10 M14.4,13 L18.8,13 " +
                "M6,15.8 L6,18.2 M10,15.8 L10,18.2",
                size, 1.6);
        }

        /// <summary>内存:内存条外形 + 四颗颗粒 + 底部金手指。</summary>
        protected FrameworkElement MakeMemoryGlyph(double size = 21)
        {
            return StrokeGlyph(
                "M4,7 L20,7 L20,16 L4,16 Z " +
                "M6.2,8.8 L8.1,8.8 L8.1,14.2 L6.2,14.2 Z M9.4,8.8 L11.3,8.8 L11.3,14.2 L9.4,14.2 Z " +
                "M12.6,8.8 L14.5,8.8 L14.5,14.2 L12.6,14.2 Z M15.8,8.8 L17.7,8.8 L17.7,14.2 L15.8,14.2 Z " +
                "M6.5,16 L6.5,18.6 M10,16 L10,18.6 M13.5,16 L13.5,18.6 M17,16 L17,18.6",
                size, 1.5);
        }

        /// <summary>网络:上行/下行双向箭头。</summary>
        protected FrameworkElement MakeNetworkGlyph(double size = 21)
        {
            return StrokeGlyph(
                "M7.8,19.6 L7.8,8.6 M4.2,12 L7.8,8.4 L11.4,12 " +
                "M16.2,4.4 L16.2,15.4 M12.6,12 L16.2,15.6 L19.8,12",
                size);
        }
    }

    /// <summary>
    /// 单行跑马灯：文本超出可用宽度时无缝循环滚动，否则静态显示。
    /// hug=true 时控件宽度收缩到内容宽度（药丸场景），否则固定宽度、静态时内容居中（展开页场景）。
    /// </summary>
    public class MarqueeText : Grid
    {
        private const string Separator = "　　";
        private readonly TextBlock _tb;
        private readonly TranslateTransform _translate = new TranslateTransform();
        private readonly double _maxWidth;
        private readonly bool _hug;
        private string _text = "";

        public MarqueeText(TextBlock inner, double maxWidth, double height, bool hug)
        {
            _tb = inner;
            _maxWidth = maxWidth;
            _hug = hug;
            Height = height;
            VerticalAlignment = VerticalAlignment.Center;
            ClipToBounds = true;
            var scroller = new Canvas { RenderTransform = _translate };
            scroller.Children.Add(_tb);
            Children.Add(scroller);
        }

        public void SetText(string text)
        {
            _text = text ?? "";
            Refresh();
        }

        private void Refresh()
        {
            if (string.IsNullOrEmpty(_text)) { _translate.X = 0; return; }
            double one = Measure(_text).Width;
            double loop = one + Measure(Separator).Width;
            bool scrolling = one > _maxWidth + 0.5;
            // 只有滚动时才放两份文本（无缝循环）；静态时只放一份，避免短歌名看到重复副本。
            string display = scrolling ? _text + Separator + _text + Separator : _text;
            if (!string.Equals(_tb.Text, display, StringComparison.Ordinal)) _tb.Text = display;
            double targetWidth = _hug ? Math.Min(_maxWidth, one) : _maxWidth;
            if (double.IsNaN(Width) || Math.Abs(Width - targetWidth) > 0.5)
            {
                Width = targetWidth;
                UpdateLayout();
            }
            Canvas.SetTop(_tb, Math.Max(0, (Height - Measure(_text).Height) / 2));
            _translate.BeginAnimation(TranslateTransform.XProperty, null);
            if (scrolling)
            {
                double speed = Math.Max(18, 30 * Ui.Scale);
                _translate.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(0, -loop, TimeSpan.FromSeconds(loop / speed))
                    {
                        RepeatBehavior = RepeatBehavior.Forever
                    });
            }
            else
            {
                _translate.X = _hug ? 0 : Math.Max(0, (targetWidth - one) / 2);
            }
        }

        private FormattedText Measure(string s)
        {
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(_tb.FontFamily, _tb.FontStyle, _tb.FontWeight, _tb.FontStretch),
                _tb.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
    }
}
