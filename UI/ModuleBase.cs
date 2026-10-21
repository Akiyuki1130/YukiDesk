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
        private readonly List<TextBlock> _glyphs = new List<TextBlock>();
        private readonly List<Shape> _themedShapes = new List<Shape>();
        private readonly List<Shape> _themedFills = new List<Shape>();
        private readonly DispatcherTimer _closeTimer;
        private TranslateTransform _panelTransform;
        private ScaleTransform _panelScale;
        private TransformGroup _panelTransforms;
        private bool _panelAbove;
        private int _animationVersion;
        private bool _closing;
        private bool _disposed;
        protected bool Open;
        private static ModuleBase _active;

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
                Padding = new Thickness(12 * scale)
            };
            CreatePanelWindow();

            _closeTimer = new DispatcherTimer();
            _closeTimer.Tick += (s, e) => { _closeTimer.Stop(); HidePanel(); };
            Root.MouseEnter += (s, e) =>
            {
                _closeTimer.Stop();
                if (HoverOpensPanel) ShowPanel();
            };
            Root.MouseLeave += (s, e) => { if (Open) StartCloseTimer(); };
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
        protected virtual bool HasPanel => true;
        /// <summary>展开页水平居中于选项卡(默认左对齐选项卡左缘)。</summary>
        protected virtual bool CenterPanelOnPill => false;
        protected virtual void OnPanelShown() { }
        protected virtual void OnPanelHidden() { }

        private void CreatePanelWindow()
        {
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
                Content = Panel,
                Visibility = Visibility.Hidden
            };
            w.SourceInitialized += (s, e) =>
                NativeMethods.ApplyNoActivateToolStyle(new System.Windows.Interop.WindowInteropHelper(w).Handle);
            w.MouseEnter += (s, e) => _closeTimer.Stop();
            w.MouseLeave += (s, e) => { if (Open) StartCloseTimer(); };
            w.SizeChanged += (s, e) => ApplyRoundRegion(w);
            w.ContentRendered += (s, e) => ApplyRoundRegion(w);
            Panel.MouseEnter += (s, e) => _closeTimer.Stop();
            Panel.MouseLeave += (s, e) => { if (Open) StartCloseTimer(); };
            PanelWindow = w;
        }

        private void StartCloseTimer()
        {
            if (_openMenu != null) return; // 右键菜单打开期间保持展开页
            _closeTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(80, App.Settings?.HoverCloseDelayMs ?? 250));
            _closeTimer.Stop();
            _closeTimer.Start();
        }

        private Popup _openMenu;

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

        private static void ApplyRoundRegion(Window w)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero || w.ActualWidth < 1 || w.ActualHeight < 1) return;
                // CreateRoundRectRgn 契约是物理像素;ActualWidth/Height 是 DIP,必须按窗口 DPI 换算,
                // 否则高缩放下区域比窗口小,展开页右侧/下方被裁掉。
                var dpi = VisualTreeHelper.GetDpi(w);
                NativeMethods.SetRoundRegion(hwnd,
                    (int)Math.Ceiling(w.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(w.ActualHeight * dpi.DpiScaleY),
                    (int)Math.Ceiling(16 * Ui.Scale * dpi.DpiScaleX));
            }
            catch { }
        }

        private void ApplyTheme()
        {
            var s = App.Settings;
            double scale = Ui.Scale;
            ApplyPillBackground(s, scale);
            Pill.BorderBrush = s != null && s.BorderEnabled ? Ui.Brush(ThemeService.Border, s.BorderOpacity) : Brushes.Transparent;
            Pill.BorderThickness = s != null && s.BorderEnabled ? new Thickness(Math.Max(0, s.BorderThickness * scale)) : new Thickness(0);
            // Reuse the pill brush so the expanded page has exactly the same color and alpha.
            // 云母档例外:展开页会伸到顶栏条带之外,没有壁纸可取样,只能用云母平均色平涂
            // (真实 Mica 弹层因为模糊极重,看起来也接近平涂,观感损失很小)。
            Panel.Background = Pill.Background is ImageBrush
                ? new SolidColorBrush(MicaService.Average) { Opacity = Math.Max(0.05, Math.Min(1, s?.MicaOpacity ?? 0.86)) }
                : Pill.Background;
            Panel.BorderBrush = Pill.BorderBrush;
            Panel.BorderThickness = Pill.BorderThickness;
            PanelWindow.Background = Brushes.Transparent;
            // 下拉框等模板控件没有直接拿到模块画刷，提供一套同步的动态主题资源。
            var resources = Application.Current?.Resources;
            if (resources != null)
            {
                resources["ThemePanelBackground"] = Ui.Brush(ThemeService.Background, s?.ThemeOpacity ?? 0.55);
                resources["ThemePanelBorder"] = s != null && s.BorderEnabled
                    ? Ui.Brush(ThemeService.Border, s.BorderOpacity)
                    : Brushes.Transparent;
                resources["ThemePanelSubtle"] = Ui.Brush(ThemeService.Text, 0.06);
                resources["ThemePanelControl"] = Ui.Brush(ThemeService.Text, 0.10);
                resources["ThemePanelControlHover"] = Ui.Brush(ThemeService.Text, 0.18);
                resources["ThemePanelHover"] = Ui.Brush(ThemeService.Text, 0.12);
                resources["ThemePanelSelected"] = Ui.Brush(ThemeService.Text, 0.20);
                resources["ThemePanelText"] = Ui.Brush(ThemeService.Text, 1);
            }
            foreach (var (tb, delta) in _texts) ApplyFont(tb, delta);
            foreach (var g in _glyphs) g.Foreground = Ui.Brush(ThemeService.Text, 1);
            foreach (var shape in _themedShapes) shape.Stroke = Ui.Brush(ThemeService.Text, 1);
            foreach (var shape in _themedFills) shape.Fill = Ui.Brush(ThemeService.Text, 1);
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
        /// 按材质设置给胶囊上背景。云母档用一支 <see cref="ImageBrush"/> 从云母位图里
        /// 截取"胶囊在屏幕上的那一段",所以每个胶囊看到的明暗分布和真实桌面一致。
        /// </summary>
        private void ApplyPillBackground(AppSettings s, double scale)
        {
            bool mica = s != null && s.PillMaterial == 1 && MicaService.Backdrop != null;
            if (!mica)
            {
                _micaBrush = null;
                _micaViewbox = Rect.Empty;
                Pill.Background = Ui.Brush(ThemeService.Background, s?.ThemeOpacity ?? 0.55);
                return;
            }

            double opacity = Math.Max(0.05, Math.Min(1, s.MicaOpacity));
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
            if (ReferenceEquals(_active, this) && Open && !_closing) return;
            if (_active != null && !ReferenceEquals(_active, this)) _active.HidePanelImmediate();
            _active = this;
            Open = true;
            _closing = false;
            _animationVersion++;
            _closeTimer.Stop();
            Panel.BeginAnimation(OpacityProperty, null);
            _panelTransform?.BeginAnimation(TranslateTransform.YProperty, null);
            _panelScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _panelScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            OnPanelShown();

            double gap = 8 * Ui.Scale;
            // PointToScreen 返回物理像素,而 PanelWindow.Left/Top 与 WorkArea 都是 DIP:
            // 先按本胶囊的 DPI 换算回 DIP,后面所有几何计算统一在 DIP 域进行,
            // 否则高缩放下展开页错位约 (scale-1)*100%,贴边 clamp 与上下翻转判断也全部失真。
            var dpi = VisualTreeHelper.GetDpi(Pill);
            var screenPt = Pill.PointToScreen(new Point(0, Pill.ActualHeight + gap));
            var p = new Point(screenPt.X / dpi.DpiScaleX, screenPt.Y / dpi.DpiScaleY);
            Panel.Visibility = Visibility.Visible;
            Panel.Measure(new Size(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
            double width = Math.Max(1, Panel.DesiredSize.Width);
            double height = Math.Max(1, Panel.DesiredSize.Height);
            var work = SystemParameters.WorkArea;
            // 居中模式:展开页中心对齐选项卡中心;默认左对齐选项卡左缘。均限制在屏幕工作区内。
            double left = CenterPanelOnPill
                ? p.X + (Pill.ActualWidth - width) / 2
                : p.X;
            left = Math.Max(work.Left, Math.Min(work.Right - width, left));
            double below = p.Y;
            bool above = below + height > work.Bottom && p.Y - Pill.ActualHeight - gap - height >= work.Top;
            double targetTop = above ? p.Y - Pill.ActualHeight - gap - height : below;
            _panelAbove = above;
            PanelWindow.Left = left;
            PanelWindow.Top = targetTop;
            Panel.Visibility = Visibility.Visible;
            PanelWindow.Visibility = Visibility.Visible;

            _panelTransform = new TranslateTransform(0, (above ? 8 : -8) * Ui.Scale);
            _panelScale = new ScaleTransform(0.52, 0.68);
            _panelTransforms = new TransformGroup();
            _panelTransforms.Children.Add(_panelScale);
            _panelTransforms.Children.Add(_panelTransform);
            Panel.RenderTransform = _panelTransforms;
            // Scale from the capsule edge so the panel feels like the tab unfolding in place.
            double originX = Math.Max(0.12, Math.Min(0.88, ((p.X + Pill.ActualWidth * 0.5) - left) / width));
            Panel.RenderTransformOrigin = new Point(originX, above ? 1 : 0);
            Panel.Opacity = 0;
            var duration = TimeSpan.FromMilliseconds(Math.Max(120, App.Settings?.AnimationDurationMs ?? 180));
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 };
            _panelTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
            _panelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
            _panelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
            Panel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
        }

        public void HidePanel() => HidePanelAnimated();

        private void HidePanelAnimated()
        {
            if (!Open || _closing) return;
            Open = false;
            _closing = true;
            var duration = TimeSpan.FromMilliseconds(Math.Max(60, (App.Settings?.AnimationDurationMs ?? 180) * 0.75));
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            if (_panelTransform == null) _panelTransform = new TranslateTransform();
            _panelTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation((_panelAbove ? 6 : -6) * Ui.Scale, duration) { EasingFunction = ease });
            if (_panelScale == null) _panelScale = new ScaleTransform(1, 1);
            _panelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.58, duration) { EasingFunction = ease });
            _panelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.72, duration) { EasingFunction = ease });
            int animationVersion = ++_animationVersion;
            var fade = new DoubleAnimation(0, duration) { EasingFunction = ease };
            fade.Completed += (s, e) =>
            {
                if (animationVersion == _animationVersion && _closing)
                    HidePanelImmediate();
            };
            Panel.BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>宿主隐藏、模块禁用或退出时立即收起独立展开窗口。</summary>
        public void ClosePanelImmediately()
        {
            _animationVersion++;
            HidePanelImmediate();
        }

        private void HidePanelImmediate()
        {
            bool wasOpen = Open || _closing || PanelWindow.Visibility == Visibility.Visible;
            _closeTimer.Stop();
            Open = false;
            _closing = false;
            if (_openMenu != null)
            {
                try { _openMenu.IsOpen = false; } catch { }
                _openMenu = null;
            }
            Panel.BeginAnimation(OpacityProperty, null);
            if (_panelTransform != null) _panelTransform.BeginAnimation(TranslateTransform.YProperty, null);
            if (_panelScale != null)
            {
                _panelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _panelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            Panel.RenderTransform = null;
            PanelWindow.Visibility = Visibility.Hidden;
            Panel.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(_active, this)) _active = null;
            if (wasOpen) OnPanelHidden();
        }

        public virtual void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _animationVersion++;
            _closeTimer.Stop();
            if (ReferenceEquals(_active, this)) _active = null;
            ThemeService.Changed -= OnThemeChanged;
            MicaService.Changed -= OnThemeChanged;
            HidePanelImmediate();
            try { PanelWindow.Close(); } catch { }
        }

        protected void SetTabStyle(TabStyle st)
        {
            _style = st ?? _style;
            ApplyTheme();
        }

        protected TextBlock MakeText(string text = "", double delta = 0)
        {
            var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            _texts.Add((t, delta));
            ApplyFont(t, delta);
            return t;
        }

        private void ApplyFont(TextBlock t, double delta)
        {
            t.FontFamily = new FontFamily("Microsoft YaHei UI");
            t.FontSize = Math.Max(9, (13 + delta) * Ui.Scale);
            t.Foreground = Ui.Brush(ThemeService.Text, 1);
        }

        protected TextBlock MakePanelTitle(string text)
        {
            // 登记进 _texts 随主题刷新,否则面板标题在主题切换后保留旧色(delta=1 → 14px)。
            var t = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
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

        // 与应用图标(assets/YukiDesk.ico)同款:六条直臂从中心辐射,末端为实心三角箭头。
        protected FrameworkElement MakeSnowflakeGlyph(double size = 21)
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            const double cx = 12, cy = 12, r = 10.5;
            for (int i = 0; i < 6; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / 3;
                double ux = Math.Cos(a), uy = Math.Sin(a);
                var line = new Line
                {
                    X1 = cx, Y1 = cy,
                    X2 = cx + ux * r * 0.60, Y2 = cy + uy * r * 0.60,
                    Stroke = Ui.Brush(ThemeService.Text, 1),
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                _themedShapes.Add(line);
                canvas.Children.Add(line);

                double tipX = cx + ux * r, tipY = cy + uy * r;
                double baseX = cx + ux * r * 0.52, baseY = cy + uy * r * 0.52;
                double nx = -uy, ny = ux;
                const double halfW = 2.2;
                var tri = new Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(tipX, tipY),
                        new Point(baseX + nx * halfW, baseY + ny * halfW),
                        new Point(baseX - nx * halfW, baseY - ny * halfW)
                    },
                    Fill = Ui.Brush(ThemeService.Text, 1)
                };
                _themedFills.Add(tri);
                canvas.Children.Add(tri);
            }
            return IconViewbox(size, canvas);
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
            _themedShapes.Add(path);
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
            _themedShapes.Add(path);
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
            _themedShapes.Add(path);
            canvas.Children.Add(path);
            return IconViewbox(size, canvas);
        }

        protected FrameworkElement MakeSpeakerGlyph(out Path wave1, out Path wave2, double size = 23)
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            var body = new Path
            {
                Data = Geometry.Parse("M3,9.5 L7,9.5 L11.5,5.5 L11.5,18.5 L7,14.5 L3,14.5 Z"),
                Fill = Ui.Brush(ThemeService.Text, 1),
                Stretch = Stretch.None
            };
            _themedFills.Add(body);
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
            _themedShapes.Add(wave1);
            _themedShapes.Add(wave2);
            canvas.Children.Add(wave1);
            canvas.Children.Add(wave2);
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
            _glyphs.Add(t);
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
            _themedShapes.Add(path);
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
            _themedShapes.Add(path);
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
