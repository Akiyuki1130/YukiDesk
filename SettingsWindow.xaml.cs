using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using DesktopBar.Interop;
using DesktopBar.Models;
using DesktopBar.Services;
using DesktopBar.UI;

namespace DesktopBar
{
    public partial class SettingsWindow : Window
    {
        private AppSettings S => App.Settings;
        private ListBox _folderList;
        private bool _updatingTheme;
        private string _currentPage;
        private int _pageAnimationVersion;

        public SettingsWindow()
        {
            InitializeComponent();
            // 先应用主题再创建动态控件，避免控件捕获初始化阶段的低对比度画刷。
            ApplyWindowTheme();
            BuildAppearancePage();
            BuildTimePage();
            BuildFoldersPage();
            BuildModulesPage();
            BuildGeneralPage();
            BuildAboutPage();
            ShowPage("Appearance");
            ThemeService.Changed += OnThemeChanged;
            ApplyWindowTheme();
            Closed += (s, e) =>
            {
                ThemeService.Changed -= OnThemeChanged;
                _tempStatusTimer?.Stop();
                _tempStatusTimer = null;
            };
        }

        // ---------- 导航 ----------
        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PagesGrid == null) return;
            ShowPage((string)((RadioButton)sender).Tag);
        }

        private void ShowPage(string tag)
        {
            if (PagesGrid == null || string.IsNullOrEmpty(tag)) return;
            var next = PagesGrid.Children.OfType<FrameworkElement>()
                .FirstOrDefault(x => string.Equals((string)x.Tag, tag, StringComparison.Ordinal));
            if (next == null) return;
            if (string.Equals(_currentPage, tag, StringComparison.Ordinal)) return;

            var previous = PagesGrid.Children.OfType<FrameworkElement>()
                .FirstOrDefault(x => string.Equals((string)x.Tag, _currentPage, StringComparison.Ordinal));
            double previousOpacity = previous?.Opacity ?? 1;
            _currentPage = tag;
            int version = ++_pageAnimationVersion;
            var duration = TimeSpan.FromMilliseconds(Math.Max(120, Math.Min(320, S.AnimationDurationMs)));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            foreach (var child in PagesGrid.Children.OfType<FrameworkElement>())
            {
                child.BeginAnimation(OpacityProperty, null);
                child.RenderTransform = null;
                child.IsHitTestVisible = ReferenceEquals(child, next);
                Panel.SetZIndex(child, ReferenceEquals(child, next) ? 1 : 0);
                if (!ReferenceEquals(child, next) && !ReferenceEquals(child, previous))
                {
                    child.Visibility = Visibility.Collapsed;
                    child.Opacity = 0;
                }
            }

            next.Visibility = Visibility.Visible;
            next.IsHitTestVisible = true;
            next.Opacity = 0;
            next.RenderTransformOrigin = new Point(0.5, 0.5);
            next.RenderTransform = new TranslateTransform(16, 0);
            var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
            var slideIn = new DoubleAnimation(16, 0, duration) { EasingFunction = ease };
            next.BeginAnimation(OpacityProperty, fadeIn);
            ((TranslateTransform)next.RenderTransform).BeginAnimation(TranslateTransform.XProperty, slideIn);

            if (previous != null && !ReferenceEquals(previous, next))
            {
                previous.Visibility = Visibility.Visible;
                previous.IsHitTestVisible = false;
                double currentOpacity = Math.Clamp(previous.Opacity, 0, 1);
                previous.Opacity = currentOpacity;
                var fadeOut = new DoubleAnimation(currentOpacity, 0, duration) { EasingFunction = ease };
                fadeOut.Completed += (s, e) =>
                {
                    if (version == _pageAnimationVersion && !ReferenceEquals(previous, next))
                    {
                        previous.Visibility = Visibility.Collapsed;
                        previous.RenderTransform = null;
                        previous.BeginAnimation(OpacityProperty, null);
                        previous.Opacity = 0;
                        Panel.SetZIndex(previous, 0);
                    }
                };
                previous.BeginAnimation(OpacityProperty, fadeOut);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindParent<Button>(e.OriginalSource as DependencyObject) != null)
            {
                e.Handled = true;
                return;
            }

            // 已移除最大化:双击标题栏不再切换最大化,仅拦截拖动。
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                if (WindowState == WindowState.Maximized)
                    WindowState = WindowState.Normal;
                try { DragMove(); } catch (InvalidOperationException) { }
                e.Handled = true;
            }
        }

        private static T FindParent<T>(DependencyObject element) where T : DependencyObject
        {
            while (element != null)
            {
                if (element is T match) return match;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private void OnThemeChanged() => Dispatcher.BeginInvoke(new Action(ApplyWindowTheme));

        private static bool SystemUsesLightTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 0) ?? 0) != 0;
            }
            catch { return false; }
        }

        private void ApplyWindowTheme()
        {
            if (_updatingTheme || WindowRoot == null) return;
            _updatingTheme = true;
            bool light = S.SettingsThemeMode == 1
                || (S.SettingsThemeMode == 0 && SystemUsesLightTheme());
            var resources = Application.Current.Resources;
            // 跟随系统:浅色模式用干净的白色纸面,深色模式用灰黑底白字(带一点冷调,避免纯黑发闷)。
            resources["SettingsWindowBackground"] = Ui.Brush(light ? "#F5F6F8" : "#1A1B21");
            resources["SettingsTitleBackground"] = Ui.Brush(light ? "#FFFFFF" : "#202129");
            resources["SettingsWindowBorder"] = Ui.Brush(light ? "#DDDFE6" : "#3A3C46");
            resources["SettingsTextBrush"] = Ui.Brush(light ? "#1A1B22" : "#FFFFFF");
            resources["SettingsSubTextBrush"] = Ui.Brush(light ? "#494E5C" : "#E4E4E4");
            resources["SettingsLabelBrush"] = Ui.Brush(light ? "#101218" : "#FFFFFF");
            resources["SettingsHoverBackground"] = Ui.Brush(light ? "#EBEDF2" : "#2F313A");
            resources["SettingsCardBackground"] = Ui.Brush(light ? "#FFFFFF" : "#23242C");
            resources["SettingsCardBorder"] = Ui.Brush(light ? "#E3E5EC" : "#393B44");
            resources["SettingsControlBackground"] = Ui.Brush(light ? "#FFFFFF" : "#2C2D35");
            resources["SettingsControlBorder"] = Ui.Brush(light ? "#C9CDD8" : "#464850");
            resources["SettingsButtonBackground"] = Ui.Brush(light ? "#EDEFF4" : "#31323B");
            resources["SettingsButtonHover"] = Ui.Brush(light ? "#E0E3EB" : "#3C3E47");
            resources["SettingsButtonPressed"] = Ui.Brush(light ? "#D4D8E2" : "#26272E");
            resources["SettingsAccent"] = Ui.Brush(light ? "#5575E8" : "#6C8CFF");
            resources["SettingsAccentHover"] = Ui.Brush(light ? "#6786F0" : "#7D9BFF");
            resources["SettingsAccentPressed"] = Ui.Brush(light ? "#4564D0" : "#5A78E6");
            resources["SettingsSwitchTrack"] = Ui.Brush(light ? "#6D84D8" : "#465A9D", 0.62);
            resources["SettingsSwitchThumb"] = Ui.Brush(light ? "#FFFFFF" : "#F5F5F5");
            resources["SettingsSwitchThumbBorder"] = Ui.Brush(light ? "#B9BFCF" : "#5D5F68");
            resources["SettingsTrack"] = Ui.Brush(light ? "#DBDEE6" : "#3A3C46");
            resources["SettingsNavHover"] = Ui.Brush(light ? "#ECEDF2" : "#2F313A");
            resources["SettingsNavSelected"] = Ui.Brush(light ? "#5575E8" : "#6C8CFF");
            resources["SettingsSelected"] = Ui.Brush(light ? "#E2E7F6" : "#3C3E47");
            resources["SettingsListHover"] = Ui.Brush(light ? "#E8EAF0" : "#464850");
            resources["SettingsPopupBackground"] = Ui.Brush(light ? "#FFFFFF" : "#272830");
            resources["SettingsScrollThumb"] = Ui.Brush(light ? "#B9BFCC" : "#565861");
            _dropLine?.InvalidateVisual();
            _updatingTheme = false;
        }

        private void Save() => App.ApplyAndSave();

        // ---------- 通用构建助手 ----------
        // 注意:主题画刷必须用 SetResourceReference(DynamicResource)引用——
        // FindResource 是一次性快照,主题切换后不会更新,会出现深底深字看不清。

        private Border Card(string title, string subtitle, out StackPanel body)
        {
            var sp = new StackPanel();
            var titleTb = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            };
            titleTb.SetResourceReference(TextElement.ForegroundProperty, "SettingsTextBrush");
            sp.Children.Add(titleTb);
            if (!string.IsNullOrEmpty(subtitle))
            {
                var subTb = new TextBlock
                {
                    Text = subtitle,
                    FontSize = 11.5,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                subTb.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
                sp.Children.Add(subTb);
            }
            body = sp;
            return new Border { Style = (Style)FindResource("Card"), Child = sp };
        }

        /// <summary>动态主题色的说明文字(顶部间距 12,可自定义)。</summary>
        private TextBlock MakeHint(string text, double topMargin = 12)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Margin = new Thickness(0, topMargin, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            tb.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
            return tb;
        }

        private FrameworkElement Row(string label, FrameworkElement control)
        {
            var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 0),
                TextWrapping = TextWrapping.Wrap
            };
            l.SetResourceReference(TextElement.ForegroundProperty, "SettingsLabelBrush");
            Grid.SetColumn(l, 0);
            Grid.SetColumn(control, 1);
            g.Children.Add(l);
            g.Children.Add(control);
            return g;
        }

        private FrameworkElement NumberSlider(double min, double max, double val, string suffix, Action<double> changed)
        {
            var s = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, val)),
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                Style = (Style)FindResource("SliderDark"),
                Margin = new Thickness(0, 0, 56, 0)
            };
            var t = new TextBlock
            {
                Text = Math.Round(val) + suffix,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            t.SetResourceReference(TextElement.ForegroundProperty, "SettingsLabelBrush");
            var g = new Grid();
            g.Children.Add(s);
            g.Children.Add(t);
            // 提交策略:回调可能触发顶栏全量重建(如改缩放/主题),不能在 ValueChanged 里实时执行,
            // 否则拖动时每帧重建可视树会卡死。改为 250ms 防抖提交:拖动/点轨道/方向键/PageUp
            // 改值全部覆盖,停手后提交一次;Thumb.DragCompleted 再立即提交,保证松手即生效。
            s.ValueChanged += (o, e) => t.Text = Math.Round(e.NewValue) + suffix;
            DispatcherTimer debounce = null;
            void ScheduleCommit()
            {
                if (debounce == null)
                {
                    debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    debounce.Tick += (o2, e2) =>
                    {
                        debounce.Stop();
                        changed(Math.Round(s.Value));
                    };
                }
                debounce.Stop();
                debounce.Start();
            }
            s.ValueChanged += (o, e) => ScheduleCommit();
            // 模板在样式应用后才实例化,Loaded 时 Thumb 才能被找到。
            s.Loaded += (o, e) =>
            {
                var thumb = s.Template?.FindName("thumb", s) as Thumb;
                if (thumb != null)
                    thumb.DragCompleted += (o2, e2) => { debounce?.Stop(); changed(Math.Round(s.Value)); };
            };
            return g;
        }

        private FrameworkElement ToggleRow(string label, bool val, Action<bool> changed, Action<ToggleButton> onCreated = null)
        {
            var t = new ToggleButton
            {
                IsChecked = val,
                Style = (Style)FindResource("Switch"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            onCreated?.Invoke(t);
            t.Click += (s, e) => changed(t.IsChecked == true);
            return Row(label, t);
        }

        private ColorEditor NewColorEditor(string hex, Action<string> changed)
        {
            var ce = new ColorEditor(hex);
            ce.Changed += changed;
            return ce;
        }

        private FrameworkElement TabStyleCard(string title, TabStyle st)
        {
            return Card(title, null, out var body);
        }

        /// <summary>设置窗口主题三选:跟随系统(默认)/浅色/深色。</summary>
        private FrameworkElement BuildThemeModePicker()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var options = new (string Label, int Mode)[]
            {
                ("跟随系统", 0), ("浅色", 1), ("深色", 2)
            };
            foreach (var (label, mode) in options)
            {
                var rb = new RadioButton
                {
                    Content = label,
                    GroupName = "thememode",
                    IsChecked = S.SettingsThemeMode == mode,
                    Style = (Style)FindResource("NavRadio")
                };
                rb.SetResourceReference(TextElement.ForegroundProperty, "SettingsTextBrush");
                rb.Checked += (s, e) => { S.SettingsThemeMode = mode; ApplyWindowTheme(); Save(); };
                panel.Children.Add(rb);
            }
            return panel;
        }

        // ---------- 外观 ----------
        private void BuildAppearancePage()
        {
            AppearanceHost.Children.Clear();
            var theme = Card("统一选项卡与展开页", "所有药丸和展开背景使用同一套颜色与透明度。", out var body);
            body.Children.Add(Row("主题色盘", NewColorEditor(S.ThemeColor, h => { S.ThemeColor = h; S.AutoTheme = false; ThemeService.Refresh(); Save(); })));
            // 顶栏文字与矢量图标的颜色；手动指定后关闭壁纸明暗自动适配，避免自动黑白文字覆盖自定义色。
            body.Children.Add(Row("文字与图标颜色", NewColorEditor(S.ThemeTextColor, h => { S.ThemeTextColor = h; S.AutoTheme = false; ThemeService.Refresh(); Save(); })));
            // 透明度允许域是 0.05..1(Normalize 钳制),滑块下限对齐 5%,避免显示 0% 实际 5% 的偏差。
            body.Children.Add(Row("透明度", NumberSlider(5, 100, S.ThemeOpacity * 100, "%", v => { S.ThemeOpacity = Math.Max(0.05, Math.Round(v) / 100); ThemeService.Refresh(); Save(); })));
            body.Children.Add(Row("缩放", NumberSlider(75, 150, S.Scale * 100, "%", v => { S.Scale = Math.Round(v) / 100; Save(); })));
            body.Children.Add(ToggleRow("自动适配壁纸明暗", S.AutoTheme, v => { S.AutoTheme = v; ThemeService.Refresh(); Save(); }));
            body.Children.Add(Row("设置窗口主题", BuildThemeModePicker()));
            body.Children.Add(ToggleRow("显示统一边框", S.BorderEnabled, v => { S.BorderEnabled = v; ThemeService.Refresh(); Save(); }));
            body.Children.Add(Row("边框颜色", NewColorEditor(S.BorderColor, h => { S.BorderColor = h; ThemeService.Refresh(); Save(); })));
            body.Children.Add(Row("边框透明度", NumberSlider(0, 100, S.BorderOpacity * 100, "%", v => { S.BorderOpacity = Math.Round(v) / 100; ThemeService.Refresh(); Save(); })));
            AppearanceHost.Children.Add(theme);

            var barCard = Card("顶栏背景", "默认完全透明，不拦截桌面点击。", out var barBody);
            barBody.Children.Add(Row("颜色", NewColorEditor(S.BarBgColor, h => { S.BarBgColor = h; Save(); })));
            barBody.Children.Add(Row("不透明度", NumberSlider(0, 100, S.BarBgOpacity * 100, "%", v => { S.BarBgOpacity = Math.Round(v) / 100; Save(); })));
            AppearanceHost.Children.Add(barCard);

            BuildMicaCard();
        }

        /// <summary>胶囊材质卡片当前实例;切换材质时整体重建,让滑条显示对应材质的参数。</summary>
        private Border _materialCard;

        /// <summary>
        /// 胶囊材质卡片。玻璃(3) = 实时捕获顶栏背后屏幕 + 高斯模糊,Apple/小米式真·毛玻璃;
        /// 云母/亚克力 = 壁纸烘焙管线(已从界面下架,存量配置自动迁移到玻璃,后端保留可回滚)。
        /// 玻璃的参数改动只调 GlassService.ApplyLive,不触发顶栏全量重建。
        /// </summary>
        private void BuildMicaCard()
        {
            if (_materialCard != null) AppearanceHost.Children.Remove(_materialCard);
            bool liquid = S.PillMaterial == 4;
            bool glass = S.PillMaterial == 3 || liquid;
            bool acrylic = S.PillMaterial == 2;
            string name = S.PillMaterial == 0 ? "纯色" : liquid ? "液态玻璃" : glass ? "高斯模糊" : acrylic ? "亚克力" : "云母";
            string subtitle = liquid
                ? "液态玻璃：实时捕获背后屏幕 → 高斯模糊 → 着色器叠加折射/色散/边缘光（iOS 26 式凸透镜边缘收缩）。"
                  + "胶囊轮廓有真实折射感；参数见下方,静止桌面自动降到近零开销。"
                : glass
                ? "高斯模糊：捕获顶栏背后的真实屏幕（窗口+图标+壁纸）→ 高斯模糊 → 染色，铺进胶囊。"
                  + "静止桌面自动降到近零开销；云母/亚克力已下架（旧配置自动迁移到高斯模糊）。"
                : acrylic
                ? "自绘亚克力：采样壁纸 → 磨砂重模糊 → 保留色彩 → 随明暗主题提亮/压暗 → 噪点，烘焙成一张位图。"
                  + "比云母更通透、更“彩色”，是磨砂玻璃质感；零屏幕捕获、零每帧开销，每个胶囊按自己在屏幕上的位置取对应一段。"
                : "自绘 Mica：采样壁纸 → 重模糊 → 亮度压带 → 主题染色 → 细噪点，烘焙成一张位图。"
                  + "零屏幕捕获、零每帧开销，静止时不重绘；每个胶囊按自己在屏幕上的位置取对应一段。";
            var card = Card("胶囊材质 · " + name, subtitle, out var body);
            body.Children.Add(Row("材质", BuildPillMaterialPicker()));
            if (S.PillMaterial == 0)
            {
                body.Children.Add(MakeHint("当前为纯色：胶囊沿用上方「主题」卡片的外观设置。选择「高斯模糊」后，这里会显示模糊与染色的参数。", 12));
            }
            else if (glass)
            {
                body.Children.Add(Row("不透明度", NumberSlider(5, 100, S.GlassOpacity * 100, "%",
                    v => { S.GlassOpacity = Math.Max(0.05, Math.Round(v) / 100); App.Save(); Services.GlassService.ApplyLive(); })));
                body.Children.Add(MakeHint("不透明度 100%：玻璃层完全盖住背后的画面（推荐，模糊/折射/色散最清楚）。"
                    + "调低会让背后清晰的画面直接透上来，观感变淡、模糊感随之消失。", 12));
                body.Children.Add(Row("模糊半径", NumberSlider(4, 60, S.GlassBlurRadius, " px",
                    v => { S.GlassBlurRadius = Math.Round(v); App.Save(); Services.GlassService.ApplyLive(); })));
                body.Children.Add(Row("染色颜色", NewColorEditor(S.GlassTint,
                    h => { S.GlassTint = h; App.Save(); Services.GlassService.ApplyLive(); })));
                body.Children.Add(Row("染色浓度", NumberSlider(0, 100, S.GlassTintOpacity * 100, "%",
                    v => { S.GlassTintOpacity = Math.Round(v) / 100; App.Save(); Services.GlassService.ApplyLive(); })));
                body.Children.Add(Row("帧率上限", NumberSlider(5, 300, S.GlassMaxFps, " fps",
                    v => { S.GlassMaxFps = (int)Math.Round(v); App.Save(); Services.GlassService.ApplyLive(); })));
                body.Children.Add(MakeHint("帧率上限 = 每秒最多抓几次屏幕，它决定「背后变化 → 玻璃更新」的延迟上限。"
                    + "单次抓屏约 4.5ms，所以实际能跑到 200fps 左右；但屏幕每秒只刷新 60 次，WPF 合成也不会更快，"
                    + "超过 60 只会多烧 CPU（背后完全静止时仍会自动降频，不额外耗电）。", 12));
                body.Children.Add(Row("间隙玻璃", NumberSlider(0, 100, S.GlassGapFill * 100, "%",
                    v => { S.GlassGapFill = Math.Round(v) / 100; App.Save(); Services.GlassService.ApplyLive(); })));
                body.Children.Add(MakeHint("间隙玻璃 0%：只有胶囊是玻璃，胶囊之间完全透明，点击可穿透到桌面图标。"
                    + "调高后整条顶栏都模糊其下内容，但这条不再点击穿透（系统按像素透明度判定穿透）。", 12));
                if (liquid)
                {
                    body.Children.Add(Row("折射强度", NumberSlider(0, 100, S.GlassRefraction * 100, "%",
                        v => { S.GlassRefraction = Math.Round(v) / 100; App.Save(); Services.GlassService.ApplyLive(); })));
                    body.Children.Add(Row("边缘光", NumberSlider(0, 100, S.GlassRim * 100, "%",
                        v => { S.GlassRim = Math.Round(v) / 100; App.Save(); Services.GlassService.ApplyLive(); })));
                    body.Children.Add(Row("色散", NumberSlider(0, 100, S.GlassChroma * 100, "%",
                        v => { S.GlassChroma = Math.Round(v) / 100; App.Save(); Services.GlassService.ApplyLive(); })));
                }
                body.Children.Add(MakeHint("高斯模糊/液态玻璃会实时捕获顶栏背后的屏幕。注意：捕获期间用截图/录屏/OBS 看不到顶栏本身"
                    + "（系统级排除，防止玻璃把自己拍进去）；顶栏被全屏窗口盖住时自动暂停捕获。", 12));
            }
            else
            {
                double op = acrylic ? S.AcrylicOpacity : S.MicaOpacity;
                double blur = acrylic ? S.AcrylicBlurRadius : S.MicaBlurRadius;
                double contrast = acrylic ? S.AcrylicContrast : S.MicaContrast;
                double sat = acrylic ? S.AcrylicSaturation : S.MicaSaturation;
                double tint = acrylic ? S.AcrylicTint : S.MicaTint;
                double noise = acrylic ? S.AcrylicNoise : S.MicaNoise;
                body.Children.Add(Row("不透明度", NumberSlider(5, 100, op * 100, "%",
                    v => { if (acrylic) S.AcrylicOpacity = Math.Max(0.05, Math.Round(v) / 100); else S.MicaOpacity = Math.Max(0.05, Math.Round(v) / 100); ApplyMica(); })));
                body.Children.Add(Row("模糊半径", NumberSlider(4, 80, blur, " px",
                    v => { if (acrylic) S.AcrylicBlurRadius = Math.Round(v); else S.MicaBlurRadius = Math.Round(v); ApplyMica(); })));
                body.Children.Add(Row("细节对比", NumberSlider(0, 100, contrast * 100, "%",
                    v => { var x = Math.Round(v) / 100; if (acrylic) S.AcrylicContrast = x; else S.MicaContrast = x; ApplyMica(); })));
                body.Children.Add(Row("彩度", NumberSlider(0, 100, sat * 100, "%",
                    v => { var x = Math.Round(v) / 100; if (acrylic) S.AcrylicSaturation = x; else S.MicaSaturation = x; ApplyMica(); })));
                body.Children.Add(Row("主题染色", NumberSlider(0, 100, tint * 100, "%",
                    v => { var x = Math.Round(v) / 100; if (acrylic) S.AcrylicTint = x; else S.MicaTint = x; ApplyMica(); })));
                body.Children.Add(Row("细噪点", NumberSlider(0, 20, noise * 100, "%",
                    v => { var x = Math.Round(v) / 100; if (acrylic) S.AcrylicNoise = x; else S.MicaNoise = x; ApplyMica(); })));
                body.Children.Add(MakeHint("「模糊半径/细节对比/彩度/主题染色/细噪点」改动会重新烘焙位图（后台线程约 0.3 秒）；"
                    + "「不透明度」改立即生效。壁纸或系统主题变化时会自动重算。", 12));
            }
            _materialCard = card;
            AppearanceHost.Children.Add(card);
        }

        /// <summary>胶囊材质选择：纯色（默认）/ 高斯模糊（实时背景高斯）/ 液态玻璃（折射+色散+边缘光）。云母/亚克力已下架。</summary>
        private FrameworkElement BuildPillMaterialPicker()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var options = new (string Label, int Mode)[]
            {
                ("纯色", 0), ("高斯模糊", 3), ("液态玻璃", 4)
            };
            foreach (var (label, mode) in options)
            {
                var rb = new RadioButton
                {
                    Content = label,
                    GroupName = "pillmaterial",
                    IsChecked = S.PillMaterial == mode,
                    Style = (Style)FindResource("NavRadio"),
                    Margin = new Thickness(0, 0, 14, 0)
                };
                int captured = mode;
                rb.Checked += (s, e) =>
                {
                    S.PillMaterial = captured;
                    ApplyMica();
                    // 重建卡片让滑条显示新材质的参数。放到 Background 优先级执行:
                    // 此刻 Checked 路由事件还在派发中,立刻拆掉正参与派发的可视化树不稳。
                    Dispatcher.BeginInvoke(new Action(BuildMicaCard), DispatcherPriority.Background);
                };
                panel.Children.Add(rb);
            }
            return panel;
        }

        /// <summary>
        /// 材质参数变更：写盘 + 触发重算。玻璃档走顶栏全量重建(Save→ApplySettings→BuildLayout
        /// 里挂/摘玻璃层),云母首次开启时位图还不存在,同步算一次避免先闪纯色。
        /// </summary>
        private void ApplyMica()
        {
            if (S.PillMaterial == 3 || S.PillMaterial == 4)
            {
                Save();
                Services.GlassService.ApplyLive();
                return;
            }
            Save();
            if (S.PillMaterial != 0 && Services.MicaService.Backdrop == null)
                Services.MicaService.BakeOnce();
            else
                Services.MicaService.Invalidate();
            Services.ThemeService.Refresh();
        }

        // ---------- 时间 ----------
        private void BuildTimePage()
        {
            TimeHost.Children.Clear();

            var card = Card("时间显示", null, out var body);

            var r1 = new RadioButton
            {
                Content = "靠右",
                GroupName = "timepos",
                IsChecked = S.TimeOnRight,
                Style = (Style)FindResource("NavRadio")
            };
            var r2 = new RadioButton
            {
                Content = "居中",
                GroupName = "timepos",
                IsChecked = !S.TimeOnRight,
                Style = (Style)FindResource("NavRadio")
            };
            r1.Checked += (s, e) => { S.TimeOnRight = true; Save(); };
            r2.Checked += (s, e) => { S.TimeOnRight = false; Save(); };
            var posPanel = new StackPanel { Orientation = Orientation.Horizontal };
            posPanel.Children.Add(r1);
            posPanel.Children.Add(r2);
            body.Children.Add(Row("显示位置", posPanel));

            body.Children.Add(ToggleRow("显示秒数", S.ShowSeconds, v => { S.ShowSeconds = v; Save(); }));
            body.Children.Add(ToggleRow("显示日期", S.ShowDate, v => { S.ShowDate = v; Save(); }));
            body.Children.Add(ToggleRow("显示星期", S.ShowWeekday, v => { S.ShowWeekday = v; Save(); }));
            body.Children.Add(MakeHint("鼠标悬停在时间选项卡上时,会展开显示星期、日期和农历日期。", 14));
            TimeHost.Children.Add(card);
        }

        // ---------- 小文件夹 ----------

        private static bool IsValidFolderName(string name) => SettingsService.IsValidFolderName(name);

        private string GetFolderPath(string name)
        {
            if (!IsValidFolderName(name)) return null;
            try
            {
                string root = Path.GetFullPath(S.StorageRoot);
                string candidate = Path.GetFullPath(Path.Combine(root, name));
                string relative = Path.GetRelativePath(root, candidate);
                if (relative == "." || Path.IsPathRooted(relative)
                    || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) return null;
                return string.Equals(relative, name, StringComparison.OrdinalIgnoreCase) ? candidate : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 把存储根目录下实际存在的子文件夹同步进 S.Folders:
        /// 磁盘新出现的文件夹自动追加到列表末尾(顶栏最右),配置里已不存在的自动剔除。
        /// 返回是否有变更。
        /// </summary>
        private bool SyncFoldersFromDisk()
        {
            try
            {
                if (!Directory.Exists(S.StorageRoot)) return false;
                var onDisk = Directory.GetDirectories(S.StorageRoot)
                    .Select(Path.GetFileName)
                    .Where(n => IsValidFolderName(n))
                    .ToList();
                var comparer = StringComparer.OrdinalIgnoreCase;
                bool changed = false;
                // 新发现的文件夹追加到末尾(顶栏最右)。
                foreach (var name in onDisk.Where(n => !S.Folders.Contains(n, comparer)))
                {
                    S.Folders.Add(name);
                    changed = true;
                }
                // 配置里有但磁盘上已删掉的,自动剔除。
                for (int i = S.Folders.Count - 1; i >= 0; i--)
                {
                    if (!onDisk.Contains(S.Folders[i], comparer))
                    {
                        S.Folders.RemoveAt(i);
                        changed = true;
                    }
                }
                return changed;
            }
            catch (Exception ex) { App.Log("同步小文件夹失败: " + ex.Message); return false; }
        }

        private void BuildFoldersPage()
        {
            FoldersHost.Children.Clear();
            bool synced = SyncFoldersFromDisk();
            if (synced) Save();

            var pathCard = Card("存储路径",
                "每个小文件夹都是这个目录下的一个真实子文件夹,拖入的文件就存在里面。目录下的所有文件夹会自动加入顶栏。", out var pbody);
            var pathBox = new TextBox
            {
                Style = (Style)FindResource("DarkBox"),
                Text = S.StorageRoot,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                IsReadOnly = true
            };
            var browse = new Button { Content = "浏览…", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += (s, e) =>
            {
                var dlg = new OpenFolderDialog { Title = "选择存储根目录" };
                if (dlg.ShowDialog() != true) return;
                string newRoot = dlg.FolderName;
                try
                {
                    if (string.Equals(Path.GetFullPath(newRoot), Path.GetFullPath(S.StorageRoot), StringComparison.OrdinalIgnoreCase)) return;
                }
                catch { return; }
                // 换目录会把新目录下所有子文件夹吞进顶栏:误选用户目录/系统目录会瞬间生成一堆
                // 选项卡且立即持久化,原目录内容不会迁移。必须让用户确认后再执行。
                int subCount = 0;
                try { subCount = Directory.GetDirectories(newRoot).Count(n => IsValidFolderName(Path.GetFileName(n))); } catch { }
                if (!ConfirmDialog.Show("更换存储路径",
                    "将把存储路径切换为:\n" + newRoot
                    + "\n\n新目录下的 " + subCount + " 个子文件夹会自动加入顶栏;原目录中的文件不会被移动或删除,顶栏此后指向新位置。确定继续吗?",
                    "更换", okIsDefault: false))
                    return;
                S.StorageRoot = newRoot;
                pathBox.Text = S.StorageRoot;
                SyncFoldersFromDisk();
                Save();
                RefreshFolderList();
                App.Bar?.ApplySettings();
            };
            var pgrid = new Grid();
            pgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(pathBox, 0);
            Grid.SetColumn(browse, 1);
            pgrid.Children.Add(pathBox);
            pgrid.Children.Add(browse);
            pbody.Children.Add(pgrid);
            FoldersHost.Children.Add(pathCard);

            var listCard = Card("小文件夹管理",
                "拖动列表项排序(顶栏同步生效);列表自动包含目录下所有文件夹。「删除文件夹」会将该文件夹及其内容移入回收站。", out var lbody);
            _folderList = new ListBox
            {
                Style = (Style)FindResource("DarkList"),
                Height = 210,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 12.5,
                AllowDrop = true,
                ItemTemplate = MakeFolderItemTemplate()
            };
            _folderList.SetResourceReference(TextElement.ForegroundProperty, "SettingsTextBrush");
            SetupFolderDragSort();
            RefreshFolderList();

            var btnAdd = new Button { Content = "新建文件夹", Style = (Style)FindResource("BtnAccent") };
            var btnRename = new Button { Content = "重命名", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
            var btnRemove = new Button { Content = "删除文件夹", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
            btnAdd.Click += (s, e) =>
            {
                var name = Prompt.Show("新建小文件夹", "文件夹名称:", "新文件夹");
                if (name == null) return;
                if (!IsValidFolderName(name))
                {
                    ConfirmDialog.Notice("名称无效", "请输入单级文件夹名称，不能包含路径分隔符或 ..。");
                    return;
                }
                try
                {
                    string dir = GetFolderPath(name);
                    if (dir == null) return;
                    Directory.CreateDirectory(dir);
                    if (!S.Folders.Contains(name, StringComparer.OrdinalIgnoreCase)) S.Folders.Add(name);
                    Save();
                    RefreshFolderList();
                    App.Bar?.ApplySettings();
                }
                catch (Exception ex) { ConfirmDialog.Notice("错误", ex.Message); }
            };
            btnRename.Click += (s, e) =>
            {
                if (_folderList.SelectedIndex < 0)
                {
                    ConfirmDialog.Notice("提示", "请先在列表中选中一个小文件夹。");
                    return;
                }
                int idx = _folderList.SelectedIndex;
                if (idx >= S.Folders.Count) return;
                string old = S.Folders[idx];
                var name = Prompt.Show("重命名", "新的名称:", old);
                if (string.IsNullOrWhiteSpace(name) || name == old) return;
                if (!IsValidFolderName(name))
                {
                    ConfirmDialog.Notice("名称无效", "请输入单级文件夹名称，不能包含路径分隔符或 ..。");
                    return;
                }
                try
                {
                    string oldDir = GetFolderPath(old);
                    string newDir = GetFolderPath(name);
                    if (oldDir == null || newDir == null) return;
                    if (!Directory.Exists(oldDir))
                    {
                        ConfirmDialog.Notice("重命名失败", "原文件夹不存在，未修改配置。");
                        return;
                    }
                    // 仅大小写变化(abc→ABC)时 Windows 的 Exists 判断会误报"已存在",放行走 Move。
                    bool caseOnly = string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase);
                    if (!caseOnly && Directory.Exists(newDir))
                    {
                        ConfirmDialog.Notice("重命名失败", "目标文件夹已存在。");
                        return;
                    }
                    if (S.Folders.Where((folder, index) => index != idx).Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        ConfirmDialog.Notice("重命名失败", "同名小文件夹已存在。");
                        return;
                    }
                    Directory.Move(oldDir, newDir);
                    S.Folders[idx] = name;
                    Save();
                    RefreshFolderList();
                    App.Bar?.ApplySettings();
                }
                catch (Exception ex) { ConfirmDialog.Notice("错误", ex.Message); }
            };
            btnRemove.Click += async (s, e) =>
            {
                if (_folderList.SelectedIndex < 0) return;
                int idx = _folderList.SelectedIndex;
                if (idx >= S.Folders.Count) return;
                string sel = S.Folders[idx];
                if (!ConfirmDialog.ShowDanger("确认删除", "删除小文件夹“" + sel + "”？\n\n文件夹及其中的所有文件将移入回收站(可在回收站还原),顶栏选项卡同步移除。", "删除")) return;
                try
                {
                    string dir = GetFolderPath(sel);
                    if (dir == null)
                    {
                        ConfirmDialog.Notice("错误", "该配置中的文件夹名称无效，已阻止删除。");
                        return;
                    }
                    if (Directory.Exists(dir))
                    {
                        // 目录内容连同文件夹一起移入回收站(带撤销信息);直接删除不可恢复,不用。
                        bool success = false;
                        var result = await OperationProgressDialog.RunDetailedAsync("正在移入回收站", true, 1,
                            progress => System.Threading.Tasks.Task.Run(() =>
                            {
                                success = NativeMethods.DeleteToRecycleBin(new[] { dir });
                                progress.Report(1, success ? sel : "操作失败", success);
                            }));
                        if (!result.Started)
                        {
                            ConfirmDialog.Notice("操作进行中", "已有文件操作正在进行，请稍后再试。");
                            return;
                        }
                        if (!success)
                        {
                            ConfirmDialog.Notice("错误", "删除文件夹失败(可能被其他程序占用)。");
                            return;
                        }
                    }
                    // await 期间用户可能已拖动排序或改名,按名称重新定位,不能信 await 前的旧索引。
                    int cur = S.Folders.FindIndex(f => string.Equals(f, sel, StringComparison.OrdinalIgnoreCase));
                    if (cur < 0) { RefreshFolderList(); return; }
                    S.Folders.RemoveAt(cur);
                    Save();
                    RefreshFolderList();
                    App.Bar?.ApplySettings();
                }
                catch (Exception ex) { ConfirmDialog.Notice("错误", ex.Message); }
            };

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            btnRow.Children.Add(btnAdd);
            btnRow.Children.Add(btnRename);
            btnRow.Children.Add(btnRemove);
            lbody.Children.Add(_folderList);
            lbody.Children.Add(btnRow);
            FoldersHost.Children.Add(listCard);
        }

        /// <summary>列表行模板:名称居左;"打开"与拖动手柄靠右,拖动手柄在最右。</summary>
        private DataTemplate MakeFolderItemTemplate()
        {
            var tpl = new DataTemplate();
            // DockPanel 让右侧按钮先停靠,名称元素占满剩余空间;
            // ColumnDefinition 不是可视树元素,不能通过 FrameworkElementFactory 追加到模板。
            var row = new FrameworkElementFactory(typeof(DockPanel));
            row.SetValue(DockPanel.LastChildFillProperty, true);

            // 名称(剩余空间)
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderRow.Name)));
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetResourceReference(TextElement.ForegroundProperty, "SettingsTextBrush");
            // "打开"按钮(右侧)
            var open = new FrameworkElementFactory(typeof(Button));
            open.SetValue(ContentControl.ContentProperty, "打开");
            open.SetValue(Control.FontSizeProperty, 11.0);
            open.SetValue(Control.PaddingProperty, new Thickness(8, 3, 8, 3));
            open.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            open.SetValue(ToolTipProperty, "在资源管理器中打开该文件夹");
            open.SetValue(FrameworkElement.StyleProperty, (Style)FindResource("Btn"));
            open.SetValue(DockPanel.DockProperty, Dock.Right);
            open.AddHandler(Button.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if ((s as FrameworkElement)?.DataContext is FolderRow folder)
                {
                    string path = GetFolderPath(folder.Name);
                    if (path != null) ShellOpen.OpenPath(path);
                }
            }));

            // 拖动手柄(最右):只有按住它才发起拖拽。
            var grip = new FrameworkElementFactory(typeof(Button));
            grip.SetValue(ContentControl.ContentProperty, "≡");
            grip.SetValue(Control.FontSizeProperty, 14.0);
            grip.SetValue(Control.PaddingProperty, new Thickness(7, 2, 7, 2));
            grip.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            grip.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            grip.SetValue(ToolTipProperty, "按住拖动调整顺序");
            grip.SetValue(FrameworkElement.StyleProperty, (Style)FindResource("Btn"));
            grip.SetValue(FrameworkElement.CursorProperty, Cursors.SizeAll);
            grip.SetValue(DockPanel.DockProperty, Dock.Right);
            grip.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler((s, e) =>
                {
                    e.Handled = true;
                    var container = FindParent<ListBoxItem>(s as DependencyObject);
                    if (container == null) return;
                    int from = _folderList.ItemContainerGenerator.IndexFromContainer(container);
                    if (from < 0) return;
                    _folderList.SelectedIndex = from;
                    var data = new DataObject("folderIndex", from);
                    DragDrop.DoDragDrop(_folderList, data, DragDropEffects.Move);
                }));
            row.AppendChild(grip);
            row.AppendChild(open);
            // LastChildFill 让名称填充剩余宽度,同时保持按钮在最右侧。
            row.AppendChild(label);

            tpl.VisualTree = row;
            return tpl;
        }

        private void RefreshFolderList()
        {
            // 行视图:名称 + 内联"打开"按钮。
            _rows = S.Folders.Select(n => new FolderRow(n)).ToList();
            _folderList.ItemsSource = _rows;
        }

        private List<FolderRow> _rows;

        /// <summary>列表行数据:名称 + 内联打开按钮。</summary>
        private class FolderRow
        {
            public string Name { get; }
            public string OpenLabel => "打开";
            public FolderRow(string name) { Name = name; }
        }

        // ---------- 拖拽排序(预览式) ----------
        private DropLineAdorner _dropLine;
        private int _previewIndex = -1;    // 当前预览插入位置

        /// <summary>
        /// 预览式拖拽排序:按住手柄拖动时列表顺序不变,只显示一条跟随鼠标的插入指示线;
        /// 松手后才真正重排一次。彻底解决"拖到奇怪位置、顺序乱七八糟"的问题。
        /// </summary>
        private void SetupFolderDragSort()
        {
            _folderList.AllowDrop = true;
            _dropLine = new DropLineAdorner(_folderList, this);
            void AttachDropLine()
            {
                var layer = AdornerLayer.GetAdornerLayer(_folderList);
                if (layer != null && _dropLine.Parent == null) layer.Add(_dropLine);
            }
            _folderList.Loaded += (s, e) => AttachDropLine();
            AttachDropLine();
            _folderList.DragOver += (s, e) =>
            {
                if (!e.Data.GetDataPresent("folderIndex")) return;
                e.Effects = DragDropEffects.Move;
                _previewIndex = CalcInsertIndex(e.GetPosition(_folderList).Y);
                _dropLine.Show(_previewIndex);
                e.Handled = true;
            };
            _folderList.DragLeave += (s, e) =>
            {
                _dropLine?.Hide();
                _previewIndex = -1;
                e.Handled = true;
            };
            _folderList.Drop += (s, e) =>
            {
                _dropLine?.Hide();
                if (!e.Data.GetDataPresent("folderIndex")) { e.Handled = true; return; }
                int from = (int)e.Data.GetData("folderIndex");
                int to = _previewIndex;
                _previewIndex = -1;
                CommitDragMove(from, to);
                e.Handled = true;
            };
        }

        /// <summary>由列表内 y 坐标算插入位置:行中点以上插到该行前,以下插到该行后。</summary>
        private int CalcInsertIndex(double y)
        {
            int count = S.Folders.Count;
            for (int i = 0; i < count; i++)
            {
                if (_folderList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem lbi)
                {
                    double top = lbi.TransformToAncestor(_folderList).Transform(new System.Windows.Point(0, 0)).Y;
                    double mid = top + lbi.ActualHeight / 2;
                    if (y < mid) return i;
                }
            }
            return count;
        }

        /// <summary>指示线的 y 坐标(列表内容坐标):index 行的顶边,末尾则是最后一行底边。</summary>
        private double InsertLineY(int index)
        {
            if (index < S.Folders.Count
                && _folderList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
                return item.TransformToAncestor(_folderList).Transform(new System.Windows.Point(0, 0)).Y - 2;
            if (index > 0
                && _folderList.ItemContainerGenerator.ContainerFromIndex(index - 1) is ListBoxItem last)
                return last.TransformToAncestor(_folderList).Transform(new System.Windows.Point(0, 0)).Y + last.ActualHeight + 2;
            return 4;
        }

        private void CommitDragMove(int from, int to)
        {
            if (from < 0 || from >= S.Folders.Count || to < 0 || to > S.Folders.Count || from == to) return;
            var name = S.Folders[from];
            S.Folders.RemoveAt(from);
            if (to > from) to--;
            int insert = Math.Min(to, S.Folders.Count);
            S.Folders.Insert(insert, name);
            Save();
            RefreshFolderList();
            _folderList.SelectedIndex = insert;
            App.Bar?.ApplySettings();
        }

        /// <summary>装饰层插入指示线:横贯列表宽度的强调色圆角条,淡入淡出。</summary>
        private sealed class DropLineAdorner : Adorner
        {
            private readonly SettingsWindow _owner;
            private double _y = -10;
            public DropLineAdorner(ListBox list, SettingsWindow owner) : base(list)
            {
                IsHitTestVisible = false;
                _owner = owner;
            }
            public void Show(int index)
            {
                _y = _owner.InsertLineY(index);
                if (Opacity < 0.95)
                {
                    BeginAnimation(OpacityProperty, null);
                    BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(90)));
                }
                InvalidateVisual();
            }
            public void Hide()
            {
                BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
                _y = -10;
            }
            protected override void OnRender(DrawingContext dc)
            {
                if (Opacity <= 0.01 || _y < 0) return;
                var list = (ListBox)AdornedElement;
                double w = list.ActualWidth - 8;
                var brush = (Brush)Application.Current.FindResource("SettingsAccent");
                dc.DrawRoundedRectangle(brush, null, new Rect(4, _y - 1.5, w, 3), 1.5, 1.5);
                // 两端小圆点,像系统拖拽指示。
                dc.DrawEllipse(brush, null, new System.Windows.Point(4, _y), 3, 3);
                dc.DrawEllipse(brush, null, new System.Windows.Point(4 + w, _y), 3, 3);
            }
        }

        // ---------- 音乐 ----------
        private void BuildModulesPage()
        {
            ModulesHost.Children.Clear();

            // 切换只更新对应模块的显隐(ApplyModuleVisibility),不重建顶栏,避免其他胶囊闪烁。
            var card = Card("模块显示",
                "控制顶栏各胶囊的显示与隐藏,切换立即生效、互不影响。", out var body);
            body.Children.Add(ToggleRow("电源按钮", S.ShowPower,
                v => { S.ShowPower = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("此电脑", S.ShowThisPc,
                v => { S.ShowThisPc = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("小文件夹", S.ShowFolders,
                v => { S.ShowFolders = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("回收站", S.ShowRecycleBin,
                v => { S.ShowRecycleBin = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("音乐播放器", S.ShowMusic,
                v => { S.ShowMusic = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("音频频谱", S.ShowSpectrum,
                v => { S.ShowSpectrum = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("音量按钮", S.ShowVolume,
                v => { S.ShowVolume = v; Save(); App.Bar?.ApplyModuleVisibility(); }));

            body.Children.Add(ToggleRow("CPU 占用率", S.ShowMonitorCpu,
                v => { S.ShowMonitorCpu = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("GPU 占用率", S.ShowMonitorGpu,
                v =>
                {
                    S.ShowMonitorGpu = v;
                    Save();
                    App.Bar?.ApplyModuleVisibility();
                    if (_gpuCombo != null) _gpuCombo.IsEnabled = v;
                    if (v && (_gpuCombo == null || _gpuCombo.Items.Count == 0)) LoadGpuOptions();
                }));
            _gpuCombo = new ComboBox
            {
                Width = 230,
                MinHeight = 32,
                Style = (Style)FindResource("DarkCombo"),
                DisplayMemberPath = nameof(DesktopBar.Services.GpuAdapterInfo.Name),
                IsEnabled = S.ShowMonitorGpu
            };
            _gpuCombo.SelectionChanged += (s, e) =>
            {
                if (_updatingGpuCombo) return;
                S.MonitorGpuLuid = (_gpuCombo.SelectedItem as DesktopBar.Services.GpuAdapterInfo)?.Luid ?? "";
                Save();
            };
            body.Children.Add(Row("监测的 GPU", _gpuCombo));
            body.Children.Add(ToggleRow("内存占用", S.ShowMonitorMemory,
                v => { S.ShowMonitorMemory = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("网络上下行", S.ShowMonitorNetwork,
                v => { S.ShowMonitorNetwork = v; Save(); App.Bar?.ApplyModuleVisibility(); }));

            // 原生 CPU 温度(AMD 平台):经 PawnIO 读取真 Tctl;未装时回落 SoC/热区传感器。
            if (DesktopBar.Services.PerfService.CpuIsAmd)
            {
                _nativeTempStatus = new TextBlock
                {
                    Text = "未启用",
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _nativeTempStatus.SetResourceReference(TextElement.ForegroundProperty, "SettingsLabelBrush");
                _nativeTempButton = new Button
                {
                    Content = "安装 PawnIO…",
                    Style = (Style)FindResource("Btn"),
                    Margin = new Thickness(10, 0, 0, 0)
                };
                _nativeTempButton.Click += (s, e) => InstallPawnIO();
                _nativeTempDisableButton = new Button
                {
                    Content = "停用",
                    Style = (Style)FindResource("Btn"),
                    Margin = new Thickness(10, 0, 0, 0),
                    ToolTip = "删除登录温度任务并停用原生温度;PawnIO 本身可在系统\"应用\"里单独卸载"
                };
                _nativeTempDisableButton.Click += (s, e) => DisableNativeTemp();
                var nativeHost = new StackPanel { Orientation = Orientation.Horizontal };
                nativeHost.Children.Add(_nativeTempStatus);
                nativeHost.Children.Add(_nativeTempButton);
                nativeHost.Children.Add(_nativeTempDisableButton);
                body.Children.Add(Row("原生 CPU 温度", nativeHost));
                body.Children.Add(MakeHint("开启后 CPU 胶囊温度显示处理器原生 Tctl 核心温度(与 AIDA64/HWiNFO 同源)。"
                    + "PawnIO 设备仅管理员可访问,因此启用时会创建一个登录时以最高权限静默启动的温度从属任务"
                    + "(隐藏进程,读取后经共享内存交给主程序,主程序退出时自动结束);首次启用只需一次管理员授权。"
                    + "未启用时显示 CPU 封装内 SoC 温度(满载时比 Tctl 略低)。"));
                System.Threading.Tasks.Task.Run(ProbeSlaveTask);
                EnsureTempStatusTimer();
                RefreshNativeTempStatus();
            }

            body.Children.Add(MakeHint("CPU、GPU、内存、网络胶囊位于音频频谱左侧;采样只在任一胶囊可见时进行。"
                + "GPU 占用率来自系统 GPU 引擎计数器,多显卡时可在上方选择要监测的 GPU。"));
            ModulesHost.Children.Add(card);

            if (S.ShowMonitorGpu) LoadGpuOptions();
        }

        private ComboBox _gpuCombo;
        private bool _updatingGpuCombo;
        private int _gpuLoadVersion;
        private TextBlock _nativeTempStatus;
        private Button _nativeTempButton;
        private Button _nativeTempDisableButton;
        private DispatcherTimer _tempStatusTimer;
        private long _lastTaskProbeTick = -60000;
        private bool _lastTaskExists;
        private bool _lastTaskPathOk = true;
        private bool _pawnInstalling;

        private void RefreshNativeTempStatus(string note = null)
        {
            if (_nativeTempStatus == null) return;
            bool avail = DesktopBar.Services.PawnIoTctl.IsAvailable || DesktopBar.Services.TemperatureSlave.IsFresh;
            string text;
            if (avail) text = "已启用(Tctl)";
            else if (note != null) text = note;
            else if (_lastTaskExists && !_lastTaskPathOk)
                text = "登录任务指向旧路径,点击\"启用原生温度…\"重建";
            else if (_lastTaskExists && !TemperatureSlaveFresh())
                text = "登录任务存在,但从属进程未提供数据";
            else text = "未启用";
            _nativeTempStatus.Text = text;
            if (_nativeTempDisableButton != null && !_pawnInstalling)
                _nativeTempDisableButton.Visibility = _lastTaskExists ? Visibility.Visible : Visibility.Collapsed;
            if (_nativeTempButton != null && !_pawnInstalling)
            {
                _nativeTempButton.Visibility = avail ? Visibility.Collapsed : Visibility.Visible;
                _nativeTempButton.Content = PawnIoInstalled() ? "启用原生温度…" : "安装 PawnIO…";
            }
        }

        private static bool TemperatureSlaveFresh() => DesktopBar.Services.TemperatureSlave.IsFresh;

        /// <summary>
        /// 周期刷新温度状态:状态行只在构建时评估一次会永远停留旧值;
        /// 每 2 秒刷新一次(共享内存检测很轻),每 15 秒才真正查一次 schtasks(进程启动较重)。
        /// </summary>
        private void EnsureTempStatusTimer()
        {
            if (_tempStatusTimer != null) return;
            _tempStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _tempStatusTimer.Tick += (s, e) =>
            {
                if (_pawnInstalling || _nativeTempStatus == null) return;
                long now = Environment.TickCount64;
                if (now - _lastTaskProbeTick > 15000)
                {
                    _lastTaskProbeTick = now;
                    System.Threading.Tasks.Task.Run(ProbeSlaveTask);
                    // 任务存在但从属没有数据时,主动 /Run 拉起一次(任务是本用户创建的,可直接运行)。
                    if (_lastTaskExists && _lastTaskPathOk && !DesktopBar.Services.TemperatureSlave.IsFresh
                        && !DesktopBar.Services.PawnIoTctl.IsAvailable)
                        System.Threading.Tasks.Task.Run(RunSlaveTaskOnce);
                }
                RefreshNativeTempStatus();
            };
            _tempStatusTimer.Start();
        }

        internal static (bool exists, bool pathOk) QuerySlaveTask()
        {
            try
            {
                // 用 /XML 而非 /V 列表:列表输出会把长值按宽度折行,路径被拆开导致误判;
                // XML 的 <Command> 是完整单行。
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe",
                    "/Query /TN YukiDeskTctlSlave /XML")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (p.ExitCode != 0) return (false, false);
                // 校验任务指向的命令行仍包含当前 exe 路径,防止 YukiDesk 换目录后任务指向旧版本。
                string exe = Environment.ProcessPath ?? "";
                bool ok = string.IsNullOrEmpty(exe) || output.IndexOf(exe.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) >= 0;
                return (true, ok);
            }
            catch { return (false, false); }
        }

        private void ProbeSlaveTask()
        {
            var (exists, pathOk) = QuerySlaveTask();
            _lastTaskExists = exists;
            _lastTaskPathOk = pathOk;
        }

        internal static void RunSlaveTaskOnce()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Run /TN YukiDeskTctlSlave")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(5000);
            }
            catch (Exception ex) { App.Log("拉起温度从属任务失败: " + ex.Message); }
        }

        /// <summary>停用原生温度:删除登录计划任务(用户自建任务可直接删,失败才请求提权重试)。</summary>
        private async void DisableNativeTemp()
        {
            if (_pawnInstalling) return;
            _pawnInstalling = true;
            var btn = _nativeTempDisableButton;
            try
            {
                if (btn != null) btn.IsEnabled = false;
                bool deleted = await System.Threading.Tasks.Task.Run(() => RunSchtasksDelete(false));
                if (!deleted)
                    deleted = await System.Threading.Tasks.Task.Run(() => RunSchtasksDelete(true));
                _lastTaskExists = !deleted;
                if (deleted) App.Log("已停用原生 CPU 温度(计划任务已删除)");
                else ConfirmDialog.Notice("停用未完成", "计划任务删除失败,可在任务计划程序中手动删除 YukiDeskTctlSlave。");
            }
            finally
            {
                _pawnInstalling = false;
                if (btn != null) btn.IsEnabled = true;
                RefreshNativeTempStatus();
            }

            static bool RunSchtasksDelete(bool elevate)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Delete /F /TN YukiDeskTctlSlave")
                    {
                        UseShellExecute = elevate
                    };
                    if (elevate) psi.Verb = "runas";
                    using var p = System.Diagnostics.Process.Start(psi);
                    p?.WaitForExit(8000);
                    return p == null || p.ExitCode == 0;
                }
                catch { return false; }
            }
        }

        private static bool PawnIoInstalled()
        {
            try
            {
                return File.Exists(System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll"));
            }
            catch { return false; }
        }

        private async void InstallPawnIO()
        {
            if (_pawnInstalling) return;
            _pawnInstalling = true;
            var btn = _nativeTempButton;
            try
            {
                if (btn != null) btn.IsEnabled = false;
                string exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) throw new FileNotFoundException("无法定位 YukiDesk.exe");

                // 一次性提升的批处理:未装则先跑 PawnIO 安装器(同一管理员令牌,不再弹 UAC),
                // 随后创建"登录时以最高权限启动温度从属进程"的计划任务并立即运行一次。
                bool needDownload = !PawnIoInstalled();
                string setup = System.IO.Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
                if (needDownload)
                {
                    RefreshNativeTempStatus("正在下载 PawnIO…");
                    const string url = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";
                    byte[] data;
                    using (var http = new System.Net.Http.HttpClient())
                        data = await http.GetByteArrayAsync(url);
                    if (data == null || data.Length < 100 * 1024) throw new IOException("下载内容异常");
                    File.WriteAllBytes(setup, data);
                    // 提权执行前验证 Authenticode 签名可信:防止被替换/篡改的二进制借 UAC 执行。
                    if (!Interop.NativeMethods.VerifyAuthenticodeSignature(setup))
                    {
                        try { File.Delete(setup); } catch { }
                        throw new IOException("下载的 PawnIO 安装器没有有效的数字签名,已中止安装。");
                    }
                }

                RefreshNativeTempStatus(needDownload ? "请在弹出的窗口中完成安装…" : "请在授权窗口点击“是”…");
                // 随机文件名 + 执行后删除:固定名脚本在写入与提权执行之间存在被替换的窗口。
                string script = System.IO.Path.Combine(Path.GetTempPath(),
                    "yukidesk-" + System.IO.Path.GetRandomFileName() + ".cmd");
                // cmd 按系统 OEM 代码页解析批处理:默认 UTF-8 无 BOM 在中文用户名路径下会乱码。
                var oem = System.Text.Encoding.GetEncoding(
                    System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                string content = "@echo off\r\n";
                if (needDownload) content += "\"" + setup + "\"\r\n";
                content += "schtasks /Create /F /TN \"YukiDeskTctlSlave\" /TR \"\\\"" + exe + "\\\" --tempslave\" /SC ONLOGON /RL HIGHEST\r\n";
                content += "schtasks /Run /TN \"YukiDeskTctlSlave\"\r\n";
                File.WriteAllText(script, content, oem);

                int exitCode = -1;
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/D /c \"" + script + "\"")
                {
                    UseShellExecute = true,
                    Verb = "runas" // 一次 UAC
                };
                var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    await System.Threading.Tasks.Task.Run(() => { try { proc.WaitForExit(); exitCode = proc.ExitCode; } catch { } });
                }
                try { File.Delete(script); } catch { }

                // 从属任务启动后约 2 秒发布共享内存,主循环 1 秒采样一次;留足刷新余量。
                for (int i = 0; i < 6; i++)
                {
                    await System.Threading.Tasks.Task.Delay(800);
                    if (DesktopBar.Services.TemperatureSlave.IsFresh) break;
                }
                // 安装完成立刻重新探测 PawnIO 通道,不必等 30 秒退避期。
                DesktopBar.Services.PawnIoTctl.InvalidateProbe();
                ProbeSlaveTask();
                RefreshNativeTempStatus();
                if (!DesktopBar.Services.TemperatureSlave.IsFresh && !DesktopBar.Services.PawnIoTctl.IsAvailable)
                    RefreshNativeTempStatus(exitCode != 0
                        ? "未完成(授权窗口被取消或被策略拦截),可再次点击重试"
                        : "已取消或尚未生效,可再次点击重试");
            }
            catch (Exception ex)
            {
                RefreshNativeTempStatus("未完成: " + ex.Message);
            }
            finally
            {
                _pawnInstalling = false;
                if (btn != null)
                {
                    btn.IsEnabled = true;
                    if (!DesktopBar.Services.PawnIoTctl.IsAvailable && !DesktopBar.Services.TemperatureSlave.IsFresh)
                        btn.Visibility = Visibility.Visible;
                }
            }
        }

        private async void LoadGpuOptions()
        {
            int version = ++_gpuLoadVersion;
            try
            {
                var gpus = await System.Threading.Tasks.Task.Run(DesktopBar.Services.PerfService.EnumerateGpus);
                if (_gpuCombo == null || version != _gpuLoadVersion) return;
                _updatingGpuCombo = true;
                _gpuCombo.ItemsSource = gpus;
                int selected = -1;
                for (int i = 0; i < gpus.Count; i++)
                    if (string.Equals(gpus[i].Luid, S.MonitorGpuLuid, StringComparison.OrdinalIgnoreCase))
                    { selected = i; break; }
                _gpuCombo.SelectedIndex = selected >= 0 ? selected : (gpus.Count > 0 ? 0 : -1);
                _updatingGpuCombo = false;
                if (gpus.Count == 0)
                    _gpuCombo.IsEnabled = false;
            }
            catch (Exception ex)
            {
                App.Log("加载显卡列表失败: " + ex.Message);
            }
        }

        // ---------- 通用 ----------
        private void BuildGeneralPage()
        {
            GeneralHost.Children.Clear();

            var card1 = Card("启动", null, out var b1);
            b1.Children.Add(ToggleRow("开机自动启动", S.AutoStart, v =>
            {
                S.AutoStart = v;
                AutoStartService.Set(v);
                Save();
            }));
            b1.Children.Add(MakeHint("通过当前用户注册表 Run 键实现,登录系统后立即启动,无等待。"));
            // 开关初值取注册表实况而非内存值:用户手删 Run 键/被组策略拦截时两者会脱节;
            // 写入失败时回读校正并把开关拨回去,不让 UI 显示一个没生效的状态。
            ToggleButton autostartToggle = null;
            b1.Children.Add(ToggleRow("开机自动启动", AutoStartService.IsEnabled(), v =>
            {
                bool ok = AutoStartService.Set(v);
                S.AutoStart = AutoStartService.IsEnabled();
                Save();
                if (!ok)
                {
                    ConfirmDialog.Notice("设置未生效",
                        "开机自动启动未能写入注册表(可能被组策略或安全软件拦截)。");
                    autostartToggle?.SetCurrentValue(ToggleButton.IsCheckedProperty, S.AutoStart);
                }
            }, onCreated: t => autostartToggle = t));
            b1.Children.Add(Row("悬停关闭", NumberSlider(80, 2000, S.HoverCloseDelayMs, "ms", v => { S.HoverCloseDelayMs = (int)Math.Round(v); Save(); })));
            b1.Children.Add(Row("动画时长", NumberSlider(80, 1200, S.AnimationDurationMs, "ms", v => { S.AnimationDurationMs = (int)Math.Round(v); Save(); })));
            GeneralHost.Children.Add(card1);

            var cardSpace = Card("桌面空间", null, out var bs);
            bs.Children.Add(ToggleRow("为顶栏预留桌面空间", S.ReserveDesktopSpace, v => { S.ReserveDesktopSpace = v; DesktopSpaceService.Apply(S); Save(); }));
            bs.Children.Add(Row("预留高度", NumberSlider(38, 160, S.DesktopSpaceHeight, "px", v => { S.DesktopSpaceHeight = (int)Math.Round(v); if (S.ReserveDesktopSpace) DesktopSpaceService.Apply(S); Save(); })));
            GeneralHost.Children.Add(cardSpace);

            var card2 = Card("数据", null, out var b2);
            var openBtn = new Button { Content = "打开数据目录", Style = (Style)FindResource("Btn") };
            openBtn.Click += (s, e) => ShellOpen.OpenPath(S.StorageRoot);
            b2.Children.Add(Row("存储目录", openBtn));
            b2.Children.Add(MakeHint("配置文件:" + Path.Combine(App.AppDataDir, "settings.json") +
                                     "\n日志文件:" + Path.Combine(App.AppDataDir, "log.txt")));
            GeneralHost.Children.Add(card2);
        }

        // ---------- 关于 ----------
        private TextBlock _updateStatus;
        private Button _checkButton;

        private void BuildAboutPage()
        {
            AboutHost.Children.Clear();

            var about = Card("关于 YukiDesk",
                "一款常驻 Windows 桌面层的顶栏小工具:小文件夹、音乐播放器、音量、回收站、音频频谱,外观全部可自定义。", out var ab);
            var verTb = new TextBlock
            {
                Text = "v" + UpdateService.CurrentVersion.ToString(3),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            verTb.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
            ab.Children.Add(Row("当前版本", verTb));
            var ghBtn = new Button { Content = "打开 GitHub 主页", Style = (Style)FindResource("Btn") };
            ghBtn.Click += (s, e) => NativeMethods.ShellExecuteOpen("https://github.com/Akiyuki1130/YukiDesk");
            ab.Children.Add(Row("项目主页", ghBtn));
            ab.Children.Add(MakeHint("github.com/Akiyuki1130/YukiDesk\n如果 YukiDesk 对你有帮助,欢迎去点一个 Star。"));
            AboutHost.Children.Add(about);

            var upd = Card("软件更新",
                "通过 GitHub Releases 检测与下载更新;系统开了代理时自动走系统代理。", out var ub);
            ub.Children.Add(ToggleRow("自动检查更新", S.AutoCheckUpdate, v => { S.AutoCheckUpdate = v; Save(); }));
            ub.Children.Add(ToggleRow("自动下载更新", S.AutoUpdate, v => { S.AutoUpdate = v; Save(); }));
            _checkButton = new Button { Content = "检查更新", Style = (Style)FindResource("BtnAccent") };
            _checkButton.Click += async (s, e) =>
            {
                if (UpdateService.Busy) return;
                _checkButton.IsEnabled = false;
                try { await UpdateService.RunCheckAsync(SetUpdateStatus, autoDownload: S.AutoUpdate); }
                finally { _checkButton.IsEnabled = true; }
            };
            ub.Children.Add(Row("手动检查更新", _checkButton));
            _updateStatus = new TextBlock
            {
                Text = "",
                FontSize = 11.5,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            _updateStatus.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
            ub.Children.Add(_updateStatus);
            ub.Children.Add(MakeHint("「自动下载更新」需要开启「自动检查更新」;下载完成后会先询问,确认后才安装。" +
                                     "安装新版本时会退出 YukiDesk 并启动安装程序,设置与小文件夹不会丢失。", 14));
            AboutHost.Children.Add(upd);
        }

        private void SetUpdateStatus(string text)
        {
            if (_updateStatus == null) return;
            _updateStatus.Text = text;
        }
    }
}
