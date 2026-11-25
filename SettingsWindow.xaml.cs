using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
        private bool _pagesReady;
        private string _currentPage;
        private bool? _appliedLightTheme;
        private readonly HashSet<string> _builtPages = new(StringComparer.Ordinal);
        private readonly HashSet<Action> _pendingSliderCommits = new();

        public SettingsWindow() : this(null) { }

        internal SettingsWindow(string initialPage)
        {
            InitializeComponent();
            FitStartupSize(SystemParameters.WorkArea);
            SizeChanged += (_, args) =>
            {
                if (args.WidthChanged) UpdateNavigationWidth(args.NewSize.Width);
            };
            // 先应用主题再创建动态控件，避免控件捕获初始化阶段的低对比度画刷。
            ApplyWindowTheme();
            TitleIconHost.Content = Ui.SnowflakeGlyph(18, "SettingsSubTextBrush");
            SettingsThemeHost.Content = BuildThemeModePicker();
            var folderBar = App.Bar;
            if (folderBar != null) folderBar.FolderCatalogChanged += RefreshFolderList;
            _pagesReady = true;
            NavigateTo(initialPage ?? "Appearance");
            if (_currentPage == null) ShowPage("Appearance");
            ThemeService.Changed += OnThemeChanged;
            UpdateService.StatusChanged += OnUpdateStatusChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
            IsVisibleChanged += (_, _) => UpdatePageActivity();
            StateChanged += (_, _) => UpdatePageActivity();
            Closing += (s, e) => CommitPendingSliders();
            Closed += (s, e) =>
            {
                if (folderBar != null) folderBar.FolderCatalogChanged -= RefreshFolderList;
                RemoveDesktopObservers();
                EndRevealShortcutCapture();
                ThemeService.Changed -= OnThemeChanged;
                UpdateService.StatusChanged -= OnUpdateStatusChanged;
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
                _tempStatusTimer?.Stop();
                _tempStatusTimer = null;
                _gpuLoadVersion++;
            };
        }

        private void FitStartupSize(Rect workArea)
        {
            if (workArea.Width <= 0 || workArea.Height <= 0) return;
            double width = workArea.Width - Math.Min(24, workArea.Width * .05);
            double height = workArea.Height - Math.Min(24, workArea.Height * .05);
            MinWidth = Math.Min(MinWidth, width);
            MinHeight = Math.Min(MinHeight, height);
            Width = Math.Min(Width, width);
            Height = Math.Min(Height, height);
            UpdateNavigationWidth(Width);
        }

        private void UpdateNavigationWidth(double windowWidth)
            => NavigationColumn.Width = new GridLength(windowWidth < 710 ? 154 : 200);

        private void CommitPendingSliders()
        {
            foreach (var commit in _pendingSliderCommits.ToArray()) commit();
        }

        // ---------- 导航 ----------
        private void Settings_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_recordingRevealShortcut)
            {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                var modifiers = Keyboard.Modifiers;
                if (key == Key.Escape && modifiers == ModifierKeys.None)
                    EndRevealShortcutCapture();
                else if (!e.IsRepeat && !BarHotkey.IsModifier(key))
                    SetRevealShortcut(new BarHotkey(key, modifiers));
            }
            else if (HandleSearchKeys(e)) e.Handled = true;
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PagesGrid == null) return;
            ShowPage((string)((RadioButton)sender).Tag);
        }

        private void ShowPage(string tag)
        {
            if (!_pagesReady || string.IsNullOrEmpty(tag) || _currentPage == tag) return;
            var pages = PagesGrid.Children.OfType<FrameworkElement>();
            var next = pages.FirstOrDefault(page => string.Equals((string)page.Tag, tag, StringComparison.Ordinal));
            if (next == null) return;

            EnsurePageBuilt(tag);
            bool animate = _currentPage != null && SystemParameters.ClientAreaAnimation;
            _currentPage = tag;
            PageTitle.Text = PageName(tag);

            foreach (var page in pages)
            {
                bool current = ReferenceEquals(page, next);
                page.BeginAnimation(OpacityProperty, null);
                page.Opacity = 1;
                page.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
                page.IsHitTestVisible = current;
            }
            if (animate)
                next.BeginAnimation(OpacityProperty, new DoubleAnimation(0.64, 1,
                    TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop });
            UpdatePageActivity();
        }

        private void EnsurePageBuilt(string page)
        {
            if (_builtPages.Contains(page)) return;
            switch (page)
            {
                case "Appearance": BuildAppearancePage(); break;
                case "Time": BuildTimePage(); break;
                case "Folders": BuildFoldersPage(); break;
                case "Modules": BuildModulesPage(); break;
                case "General": BuildGeneralPage(); break;
                case "About": BuildAboutPage(); break;
                default: return;
            }
            _builtPages.Add(page);
        }

        internal void NavigateTo(string page)
        {
            SettingsSearchBox.Clear();
            var entry = NavigationItems.Children.OfType<RadioButton>()
                .FirstOrDefault(button => (string)button.Tag == page);
            if (entry == null) return;
            entry.IsChecked = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_currentPage == page && entry.IsVisible) entry.BringIntoView();
            }));
        }

        internal (string Page, double Offset) LocationInSettings => (_currentPage, FindPage(_currentPage)?.VerticalOffset ?? 0);

        internal void RestoreLocation((string Page, double Offset) location)
        {
            var view = FindPage(location.Page);
            if (view == null) return;
            NavigateTo(location.Page);
            if (location.Offset > 0)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (_currentPage == location.Page) view.ScrollToVerticalOffset(location.Offset);
                }));
        }

        private ScrollViewer FindPage(string page) => PagesGrid.Children.OfType<ScrollViewer>()
            .FirstOrDefault(view => Equals(view.Tag, page));

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

        private void OnThemeChanged() => Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyWindowTheme();
            _glassPreview?.Refresh(S);
        }));

        private void OnSystemThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (S?.SettingsThemeMode == 0 && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(ApplyWindowTheme));
        }

        private static bool SystemUsesLightTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 0) ?? 0) != 0;
            }
            catch { return false; }
        }

        private static readonly (string Key, string Light, string Dark)[] SettingsPalette =
        {
            ("SettingsWindowBackground", "#FAF9F7", "#20201F"),
            ("SettingsSidebarBackground", "#F0EFEC", "#1B1B1A"),
            ("SettingsWindowBorder", "#D1CECA", "#40403E"),
            ("SettingsDivider", "#DFDCD7", "#363634"),
            ("SettingsTextBrush", "#242321", "#F0EEEA"),
            ("SettingsSubTextBrush", "#635F5A", "#B2AFAA"),
            ("SettingsLabelBrush", "#3B3936", "#DEDBD6"),
            ("SettingsHoverBackground", "#EEECE8", "#2B2B29"),
            ("SettingsControlBackground", "#FFFFFF", "#262625"),
            ("SettingsControlBorder", "#CFCCC7", "#51514E"),
            ("SettingsButtonBackground", "#EFEEEB", "#2B2B29"),
            ("SettingsButtonHover", "#E6E4DF", "#333332"),
            ("SettingsButtonPressed", "#DEDBD6", "#272726"),
            ("SettingsAccent", "#8F462F", "#F2B89B"),
            ("SettingsAccentFill", "#914A33", "#F2B89B"),
            ("SettingsAccentHover", "#7D3C27", "#F8C6AD"),
            ("SettingsAccentPressed", "#6E3220", "#E4A68A"),
            ("SettingsAccentInk", "#FFFFFF", "#211D1B"),
            ("SettingsSwitchTrack", "#DBD8D3", "#454542"),
            ("SettingsSwitchThumb", "#FFFFFF", "#F4F1ED"),
            ("SettingsSwitchThumbBorder", "#98948F", "#6C6965"),
            ("SettingsTrack", "#DFDDD9", "#444441"),
            ("SettingsNavHover", "#E8E6E2", "#282827"),
            ("SettingsNavSelected", "#E5E3DF", "#30302E"),
            ("SettingsSelected", "#F2E8E3", "#493B34"),
            ("SettingsSelectedBorder", "#C49A88", "#A97E6B"),
            ("SettingsListHover", "#EDEBE7", "#2F2F2D"),
            ("SettingsPopupBackground", "#FFFFFF", "#292928"),
            ("SettingsScrollThumb", "#9B9792", "#696762"),
            ("SettingsCloseHover", "#F4E5E1", "#432E2B"),
            ("SettingsCloseInk", "#903B31", "#FFBDB4"),
        };

        private void ApplyWindowTheme()
        {
            if (_updatingTheme || WindowRoot == null) return;
            bool light = S.SettingsThemeMode == 1 || (S.SettingsThemeMode == 0 && SystemUsesLightTheme());
            if (_appliedLightTheme == light) return;

            _updatingTheme = true;
            try
            {
                var resources = Application.Current.Resources;
                foreach (var (key, lightColor, darkColor) in SettingsPalette)
                    resources[key] = Ui.Brush(light ? lightColor : darkColor);
                _appliedLightTheme = light;
                _dropLine?.InvalidateVisual();
            }
            finally { _updatingTheme = false; }
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
                FontSize = 16,
                FontWeight = FontWeights.Medium
            };
            titleTb.SetResourceReference(TextElement.ForegroundProperty, "SettingsTextBrush");
            sp.Children.Add(titleTb);
            if (!string.IsNullOrEmpty(subtitle))
            {
                var subTb = new TextBlock
                {
                    Text = subtitle,
                    FontSize = 13,
                    Margin = new Thickness(0, 5, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                subTb.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
                sp.Children.Add(subTb);
            }
            body = sp;
            var section = new Border { Style = (Style)FindResource("SettingsSection"), Child = sp };
            section.SetValue(SearchLabelProperty, title);
            section.SetValue(SearchSectionProperty, title);
            return section;
        }

        /// <summary>动态主题色的说明文字(顶部间距 12,可自定义)。</summary>
        private TextBlock MakeHint(string text, double topMargin = 12)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Margin = new Thickness(0, topMargin, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            tb.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
            return tb;
        }

        private FrameworkElement Row(string label, FrameworkElement control, string aliases = null)
        {
            const double labelWidth = 146;
            var g = new Grid { Margin = new Thickness(0, 10, 0, 0), MinHeight = 32 };
            var labelColumn = new ColumnDefinition { Width = new GridLength(labelWidth) };
            g.ColumnDefinitions.Add(labelColumn);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock
            {
                Text = label,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 15, 0),
                TextWrapping = TextWrapping.Wrap
            };
            l.SetResourceReference(TextElement.ForegroundProperty, "SettingsLabelBrush");
            System.Windows.Automation.AutomationProperties.SetLabeledBy(control, l);
            if (control is Grid sliderRow && sliderRow.Children.Count > 0 && sliderRow.Children[0] is Slider slider)
                System.Windows.Automation.AutomationProperties.SetLabeledBy(slider, l);
            Grid.SetColumn(l, 0);
            Grid.SetColumn(control, 1);
            g.Children.Add(l);
            g.Children.Add(control);
            if (control is not ToggleButton)
                g.SizeChanged += (_, args) =>
                {
                    if (!args.WidthChanged) return;
                    bool compact = args.NewSize.Width < 352;
                    if (compact == (Grid.GetRow(control) == 1)) return;
                    if (compact && g.RowDefinitions.Count == 0)
                    {
                        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    }
                    labelColumn.Width = new GridLength(compact ? 0 : labelWidth);
                    l.Margin = compact ? new Thickness(0, 0, 0, 5) : new Thickness(0, 0, 15, 0);
                    Grid.SetColumn(l, compact ? 1 : 0);
                    Grid.SetRow(control, compact ? 1 : 0);
                };
            g.SetValue(SearchLabelProperty, label);
            if (aliases != null) g.SetValue(SearchAliasesProperty, aliases);
            return g;
        }

        private FrameworkElement NumberSlider(double min, double max, double val, string suffix, Action<double> changed,
            Action<double> preview = null, string name = null, double step = 1)
        {
            var s = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, val)),
                IsSnapToTickEnabled = true,
                TickFrequency = step,
                SmallChange = step,
                LargeChange = step * Math.Max(1, Math.Round((max - min) / (10 * step))),
                Style = (Style)FindResource("CapsuleSlider")
            };
            if (name != null) System.Windows.Automation.AutomationProperties.SetName(s, name);
            string unit = suffix == "%" ? suffix : " " + suffix.Trim();
            var t = new TextBlock
            {
                Text = Math.Round(s.Value) + unit,
                FontSize = 14,
                FontWeight = FontWeights.Bold
            };
            t.SetResourceReference(TextElement.ForegroundProperty, "SettingsTextBrush");

            // 数值和预览随拖动更新；松手提交，连续的键盘或轨道点击合并写入。
            double committed = Math.Round(s.Value);
            bool dragging = false;
            var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            void Commit()
            {
                debounce.Stop();
                _pendingSliderCommits.Remove(Commit);
                double value = Math.Round(s.Value);
                if (value == committed) return;
                committed = value;
                changed(value);
            }
            debounce.Tick += (o, e) => Commit();
            s.ValueChanged += (o, e) =>
            {
                double value = Math.Round(e.NewValue);
                t.Text = value + unit;
                preview?.Invoke(value);
                debounce.Stop();
                if (value == committed)
                {
                    _pendingSliderCommits.Remove(Commit);
                    return;
                }
                _pendingSliderCommits.Add(Commit);
                if (!dragging) debounce.Start();
            };
            s.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((o, e) =>
            {
                dragging = true;
                debounce.Stop();
            }), handledEventsToo: true);
            s.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((o, e) =>
            {
                dragging = false;
                Commit();
            }), handledEventsToo: true);
            s.LostKeyboardFocus += (o, e) => { if (!dragging) Commit(); };
            s.Unloaded += (o, e) =>
            {
                debounce.Stop();
                _pendingSliderCommits.Remove(Commit);
            };
            return Ui.SliderWithValue(s, t, 84);
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

        private FrameworkElement BuildThemeModePicker()
        {
            var panel = new UniformGrid { Columns = 3 };
            var options = new[] { ("系统", 0), ("浅色", 1), ("深色", 2) };
            foreach (var (label, mode) in options)
            {
                var rb = new RadioButton
                {
                    Content = label, GroupName = "SettingsThemeMode", IsChecked = S.SettingsThemeMode == mode,
                    Style = (Style)FindResource("SettingsChoice"),
                    FontSize = 13, MinWidth = 0, Padding = new Thickness(4, 6, 4, 6),
                    Margin = new Thickness(0, 0, mode == 2 ? 0 : 4, 0),
                    ToolTip = mode == 0 ? "跟随系统主题" : label
                };
                rb.Checked += (s, e) =>
                {
                    S.SettingsThemeMode = mode;
                    ApplyWindowTheme();
                    App.Save();
                };
                panel.Children.Add(rb);
            }
            return panel;
        }

        // ---------- 外观 ----------
        private void BuildAppearancePage()
        {
            AppearanceHost.Children.Clear();
            var theme = Card("桌面配色", null, out var body);
            _manualColorRows = new StackPanel();
            _solidColorRow = Row("胶囊底色", NewColorEditor(S.ThemeColor, h => { S.ThemeColor = h; SaveDesktopColors(); }));
            _manualColorRows.Children.Add(_solidColorRow);
            _manualColorRows.Children.Add(Row("文字与图标颜色", NewColorEditor(S.ThemeTextColor, h => { S.ThemeTextColor = h; SaveDesktopColors(); })));
            body.Children.Add(_manualColorRows);
            // 透明度允许域是 0.05..1(Normalize 钳制),滑块下限对齐 5%,避免显示 0% 实际 5% 的偏差。
            _solidOpacityRow = Row("纯色透明度", NumberSlider(5, 100, S.ThemeOpacity * 100, "%", v => { S.ThemeOpacity = v / 100; SaveDesktopColors(); }));
            body.Children.Add(_solidOpacityRow);
            body.Children.Add(Row("缩放", NumberSlider(75, 150, S.Scale * 100, "%", v => { S.Scale = Math.Round(v) / 100; Save(); })));
            body.Children.Add(ToggleRow("显示统一边框", S.BorderEnabled, v => { S.BorderEnabled = v; SaveDesktopColors(); }));
            body.Children.Add(Row("边框颜色", NewColorEditor(S.BorderColor, h => { S.BorderColor = h; SaveDesktopColors(); })));
            body.Children.Add(Row("边框透明度", NumberSlider(0, 100, S.BorderOpacity * 100, "%", v => { S.BorderOpacity = v / 100; SaveDesktopColors(); })));
            AppearanceHost.Children.Add(theme);

            var barCard = Card("顶栏背景", "默认完全透明，不拦截桌面点击。", out var barBody);
            barBody.Children.Add(Row("颜色", NewColorEditor(S.BarBgColor, h => { S.BarBgColor = h; Save(); })));
            barBody.Children.Add(Row("不透明度", NumberSlider(0, 100, S.BarBgOpacity * 100, "%", v => { S.BarBgOpacity = Math.Round(v) / 100; Save(); })));
            AppearanceHost.Children.Add(barCard);
            BuildMaterialCard();
        }

        private Border _materialCard;
        private GlassPreview _glassPreview;
        private bool _glassAdvanced;
        private StackPanel _manualColorRows;
        private FrameworkElement _solidColorRow, _solidOpacityRow;

        private void UpdateDesktopColorControls()
        {
            bool glass = S.PillMaterial is 3 or 4;
            _manualColorRows.Visibility = ThemeService.FollowsBackground(S) ? Visibility.Collapsed : Visibility.Visible;
            _solidColorRow.Visibility = _solidOpacityRow.Visibility = glass ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SaveDesktopColors()
        {
            App.Save();
            ThemeService.Refresh();
            _glassPreview?.Refresh(S);
        }

        private void BuildMaterialCard()
        {
            _searchEntries.Remove("Appearance");
            if (_materialCard != null) AppearanceHost.Children.Remove(_materialCard);
            if (_glassPreview?.Parent is Panel previousPreviewHost)
                previousPreviewHost.Children.Remove(_glassPreview);
            bool liquid = S.PillMaterial == 4;
            bool glass = S.PillMaterial is 3 or 4;
            var card = Card("胶囊与展开页", null, out var body);
            _materialCard = card;

            body.Children.Add(Row("材质", BuildChoices("pillmaterial", S.PillMaterial, new[]
            {
                ("纯色", 0), ("高斯模糊", 3), ("液态玻璃", 4)
            }, mode =>
            {
                S.PillMaterial = mode;
                Save();
                GlassService.ApplyLive();
                Dispatcher.BeginInvoke(new Action(BuildMaterialCard), DispatcherPriority.Background);
            })));

            bool follows = ThemeService.FollowsBackground(S);
            var colorHint = MakeHint("", 3);
            void UpdateColorHint()
            {
                bool auto = ThemeService.FollowsBackground(S);
                colorHint.Text = auto
                    ? (glass ? "各胶囊的文字和图标随背后画面切换明暗，支持壁纸幻灯片。"
                        : "随主屏壁纸顶端的明暗切换，支持壁纸幻灯片。")
                    : (glass ? "文字和图标使用下方「桌面配色」中的固定颜色。"
                        : "胶囊底色及文字和图标使用下方「桌面配色」中的固定颜色。");
            }
            body.Children.Add(Row("胶囊配色", BuildChoices("capsuleColors", follows ? 1 : 0, new[]
            {
                ("跟随背景", 1), ("固定配色", 0)
            }, mode =>
            {
                S.FollowCapsuleBackground = S.AutoTheme = mode == 1;
                SaveDesktopColors();
                GlassService.ApplyLive();
                UpdateDesktopColorControls();
                UpdateColorHint();
            })));
            UpdateDesktopColorControls();
            UpdateColorHint();
            body.Children.Add(colorHint);

            if (liquid)
            {
                body.Children.Add(Row("玻璃外观", BuildChoices("glassStyle", S.GlassStyle, new[]
                {
                    ("清透", 0), ("柔雾", 1), ("自定义", 2)
                }, style =>
                {
                    S.GlassStyle = style;
                    S.GlassFrost = null;
                    ApplyGlassSetting();
                    Dispatcher.BeginInvoke(new Action(BuildMaterialCard), DispatcherPriority.Background);
                })));
                var frostSlider = NumberSlider(0, 100, GlassService.FrostLevel(S) * 100, "%", value =>
                {
                    if (!ReferenceEquals(_materialCard, card) || S.PillMaterial != 4) return;
                    S.GlassFrost = value / 100;
                    ApplyGlassSetting();
                }, value =>
                {
                    if (ReferenceEquals(_materialCard, card) && S.PillMaterial == 4)
                        _glassPreview?.Refresh(S, value / 100);
                }, name: "雾面强度");
                body.Children.Add(Row("雾面强度", frostSlider));
            }
            _glassPreview ??= new GlassPreview();
            _glassPreview.Refresh(S);
            body.Children.Add(_glassPreview);
            if (glass)
            {
                var pacePicker = BuildGlassPacePicker();
                body.Children.Add(Row("更新节奏", pacePicker));
                body.Children.Add(MakeHint("自动模式会在画面静止时降低刷新频率。", 3));
                body.Children.Add(ToggleRow("展开页定格玻璃", S.FreezePanelGlass, enabled =>
                {
                    S.FreezePanelGlass = enabled;
                    ApplyGlassSetting();
                }));
                body.Children.Add(MakeHint("打开面板时取景一次，停留期间保留折射和雾面。", 3));
                var advanced = new StackPanel();
                void AddSlider(string label, int min, int max, double value, string suffix, Action<double> set)
                {
                    advanced.Children.Add(Row(label, NumberSlider(min, max, value, suffix, v =>
                    {
                        if (!ReferenceEquals(card, _materialCard)) return;
                        set(v);
                        ApplyGlassSetting();
                    })));
                }
                AddSlider("玻璃覆盖度", 5, 100, S.GlassOpacity * 100, "%", v => S.GlassOpacity = v / 100);
                AddSlider("壁纸换图过渡", 0, 1200, S.WallpaperFadeDurationMs, " ms", v => S.WallpaperFadeDurationMs = (int)v);
                advanced.Children.Add(MakeHint("用于 Windows 壁纸幻灯片。设为 0 毫秒时立即换图，动画跟随 Windows 的动画效果开关。", 3));
                AddSlider("胶囊间隙覆盖度", 0, 100, S.GlassGapFill * 100, "%", v => S.GlassGapFill = v / 100);
                AddSlider("自定更新上限", 1, 300, S.GlassMaxFps > 0 ? S.GlassMaxFps : 48, " fps", v =>
                {
                    S.GlassMaxFps = (int)v;
                    SetGlassPaceSelection(pacePicker, S.GlassMaxFps);
                });
                if (!liquid || S.GlassStyle == 2)
                {
                    AddSlider("模糊半径", 4, 60, S.GlassBlurRadius, " px", v => S.GlassBlurRadius = v);
                    advanced.Children.Add(Row("染色颜色", NewColorEditor(S.GlassTint, color =>
                    {
                        S.GlassTint = color;
                        ApplyGlassSetting();
                    })));
                    AddSlider("染色强度", 0, 100, S.GlassTintOpacity * 100, "%", v => S.GlassTintOpacity = v / 100);
                    if (liquid)
                    {
                        AddSlider("折射", 0, 100, S.GlassRefraction * 100, "%", v => S.GlassRefraction = v / 100);
                        AddSlider("边缘高光", 0, 100, S.GlassRim * 100, "%", v => S.GlassRim = v / 100);
                        AddSlider("色散", 0, 100, S.GlassChroma * 100, "%", v => S.GlassChroma = v / 100);
                    }
                }
                var expander = new Expander
                {
                    Header = "高级调节", Content = advanced, IsExpanded = _glassAdvanced,
                    Style = (Style)FindResource("SettingsExpander"),
                    Margin = new Thickness(0, 12, 0, 0)
                };
                expander.SetResourceReference(Control.ForegroundProperty, "SettingsLabelBrush");
                expander.Expanded += (s, e) => _glassAdvanced = true;
                expander.Collapsed += (s, e) => _glassAdvanced = false;
                body.Children.Add(expander);
            }
            AppearanceHost.Children.Insert(0, card);
        }

        private FrameworkElement BuildChoices(string group, int value, (string Label, int Mode)[] options, Action<int> changed)
        {
            var panel = new WrapPanel();
            foreach (var (label, mode) in options)
            {
                var button = new RadioButton
                {
                    Content = label, GroupName = group, IsChecked = value == mode, Style = (Style)FindResource("SettingsChoice")
                };
                button.Checked += (s, e) => changed(mode);
                panel.Children.Add(button);
            }
            return panel;
        }

        private static readonly (int Fps, string Label)[] GlassPaces =
        {
            (0, "自动"), (24, "省电 · 24 fps"), (60, "流畅 · 60 fps"), (120, "高刷 · 120 fps")
        };

        private bool _settingGlassPace;

        private ComboBox BuildGlassPacePicker()
        {
            var picker = new ComboBox
            {
                Style = (Style)FindResource("DarkCombo"),
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 150
            };
            SetGlassPaceSelection(picker, S.GlassMaxFps);
            picker.SelectionChanged += (s, e) =>
            {
                if (_settingGlassPace || picker.SelectedItem is not ComboBoxItem selected || selected.Tag is not int fps) return;
                S.GlassMaxFps = fps;
                ApplyGlassSetting();
                Dispatcher.BeginInvoke(new Action(BuildMaterialCard), DispatcherPriority.Background);
            };
            return picker;
        }

        private void SetGlassPaceSelection(ComboBox picker, int fps)
        {
            _settingGlassPace = true;
            try
            {
                picker.Items.Clear();
                foreach (var option in GlassPaces)
                    picker.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Fps });
                var match = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, fps));
                if (match == null)
                {
                    match = new ComboBoxItem { Content = $"自定 · {fps} fps", Tag = fps };
                    picker.Items.Add(match);
                }
                picker.SelectedItem = match;
            }
            finally { _settingGlassPace = false; }
        }

        private void ApplyGlassSetting()
        {
            if (!_updatingTheme)
            {
                App.Save();
                GlassService.ApplyLive();
                _glassPreview?.Refresh(S);
            }
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
                Style = (Style)FindResource("SettingsChoice")
            };
            var r2 = new RadioButton
            {
                Content = "居中",
                GroupName = "timepos",
                IsChecked = !S.TimeOnRight,
                Style = (Style)FindResource("SettingsChoice")
            };
            r1.Checked += (s, e) => { S.TimeOnRight = true; Save(); };
            r2.Checked += (s, e) => { S.TimeOnRight = false; Save(); };
            var posPanel = new StackPanel { Orientation = Orientation.Horizontal };
            posPanel.Children.Add(r1);
            posPanel.Children.Add(r2);
            body.Children.Add(Row("显示位置", posPanel));

            body.Children.Add(ToggleRow("显示秒数", S.ShowSeconds, v => { S.ShowSeconds = v; SaveClockDisplay(); }));
            body.Children.Add(ToggleRow("显示日期", S.ShowDate, v => { S.ShowDate = v; SaveClockDisplay(); }));
            body.Children.Add(ToggleRow("显示星期", S.ShowWeekday, v => { S.ShowWeekday = v; SaveClockDisplay(); }));
            var zoneLabels = new[] { ("", "关闭") }.Concat(TimeZoneInfo.GetSystemTimeZones().Select(zone => (zone.Id, zone.DisplayName))).ToArray();
            var zonePicker = new ComboBox { Width = 245, MinHeight = 32, ItemsSource = zoneLabels.Select(item => item.Item2).ToArray(),
                Style = (Style)FindResource("DarkCombo") };
            zonePicker.SelectedIndex = Math.Max(0, Array.FindIndex(zoneLabels, item => item.Item1 == S.SecondTimeZoneId));
            zonePicker.SelectionChanged += (_, _) =>
            {
                if (zonePicker.SelectedIndex < 0) return;
                S.SecondTimeZoneId = zoneLabels[zonePicker.SelectedIndex].Item1;
                SaveClockDisplay();
            };
            body.Children.Add(Row("展开页第二时区", zonePicker));
            body.Children.Add(MakeHint("鼠标悬停在时间选项卡上时,会展开显示星期、日期和农历日期。", 14));
            TimeHost.Children.Add(card);

            void SaveClockDisplay()
            {
                App.Save();
                App.Bar?.RefreshClockDisplay();
            }
        }

        // ---------- 小文件夹 ----------

        private void BuildFoldersPage()
        {
            FoldersHost.Children.Clear();
            var pathCard = Card("存储路径", "顶栏会同步显示这个目录下的子文件夹。", out var pathBody);
            var pathBox = new TextBox
            {
                Style = (Style)FindResource("DarkBox"), Text = S.StorageRoot,
                VerticalAlignment = VerticalAlignment.Center, FontSize = 13, IsReadOnly = true
            };
            var browse = new Button
            {
                Content = "浏览…", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0)
            };
            browse.Click += async (_, _) =>
            {
                var picker = new OpenFolderDialog { Title = "选择存储根目录" };
                if (picker.ShowDialog(this) != true) return;
                string root = picker.FolderName;
                if (string.Equals(root, S.StorageRoot, StringComparison.OrdinalIgnoreCase)) return;
                browse.IsEnabled = false;
                try
                {
                    var names = await System.Threading.Tasks.Task.Run(() => MiniFolderCatalog.ReadNames(root));
                    if (!IsVisible) return;
                    if (!ConfirmDialog.Show("更换存储路径",
                        "将显示新目录下的 " + names.Length + " 个子文件夹。文件仍保存在各自的目录中。\n\n" + root,
                        "更换", okIsDefault: false)) return;
                    S.StorageRoot = root;
                    S.Folders = MiniFolderCatalog.Reconcile(S.Folders, names);
                    pathBox.Text = root;
                    App.Save();
                    App.Bar?.ApplySettings();
                    RefreshFolderList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    if (IsVisible) ConfirmDialog.Notice("无法读取目录", ex.Message);
                }
                finally { browse.IsEnabled = true; }
            };
            var pathGrid = new Grid();
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition());
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(browse, 1);
            pathGrid.Children.Add(pathBox);
            pathGrid.Children.Add(browse);
            pathBody.Children.Add(pathGrid);
            var explorer = new Button
            {
                Content = "在资源管理器中管理文件夹", Style = (Style)FindResource("BtnAccent"),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0)
            };
            explorer.Click += (_, _) => ShellOpen.OpenFolder(S.StorageRoot);
            pathBody.Children.Add(explorer);
            FoldersHost.Children.Add(pathCard);

            var listCard = Card("小文件夹顺序", "拖动列表项调整顶栏顺序。也可以选中文件夹来重命名或删除。", out var listBody);
            _folderList = new ListBox
            {
                Style = (Style)FindResource("DarkList"), Height = 210, Margin = new Thickness(0, 8, 0, 0),
                FontSize = 14, AllowDrop = true, ItemTemplate = MakeFolderItemTemplate()
            };
            _folderList.SetResourceReference(TextElement.ForegroundProperty, "SettingsTextBrush");
            SetupFolderDragSort();
            RefreshFolderList();
            listBody.Children.Add(_folderList);
            var rename = new Button { Content = "重命名", Style = (Style)FindResource("Btn"), IsEnabled = false };
            var delete = new Button
            {
                Content = "删除文件夹", Style = (Style)FindResource("Btn"), IsEnabled = false,
                Margin = new Thickness(8, 0, 0, 0)
            };
            _folderList.SelectionChanged += (_, _) => rename.IsEnabled = delete.IsEnabled = _folderList.SelectedItem is FolderRow;
            void WithSelectedFolder(Action<IntPtr, string> action)
            {
                if (_folderList.SelectedItem is not FolderRow selected) return;
                string path = MiniFolderCatalog.GetFolderPath(S.StorageRoot, selected.Name);
                if (path == null) return;
                var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                ShellFileActions.Run(() => action(owner, path));
            }
            rename.Click += (_, _) => WithSelectedFolder(ShellFileActions.Rename);
            delete.Click += (_, _) => WithSelectedFolder((owner, path) => ShellFiles.Invoke(owner, path, "delete"));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(rename);
            actions.Children.Add(delete);
            listBody.Children.Add(actions);
            FoldersHost.Children.Add(listCard);
            FoldersHost.Children.Add(BuildLinkedFoldersCard());
            FoldersHost.Children.Add(BuildFolderShortcutsCard());
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
            open.SetValue(Control.FontSizeProperty, 13.0);
            open.SetValue(Control.PaddingProperty, new Thickness(8, 3, 8, 3));
            open.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            open.SetValue(ToolTipProperty, "在资源管理器中打开该文件夹");
            open.SetValue(FrameworkElement.StyleProperty, (Style)FindResource("Btn"));
            open.SetValue(DockPanel.DockProperty, Dock.Right);
            open.AddHandler(Button.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if ((s as FrameworkElement)?.DataContext is FolderRow folder)
                {
                    string path = MiniFolderCatalog.GetFolderPath(S.StorageRoot, folder.Name);
                    ShellOpen.OpenFolder(path);
                }
            }));

            // 拖动手柄(最右):只有按住它才发起拖拽。
            var grip = new FrameworkElementFactory(typeof(Button));
            grip.SetValue(ContentControl.ContentProperty, "≡");
            grip.SetValue(Control.FontSizeProperty, 14.0);
            grip.SetValue(FrameworkElement.WidthProperty, 36.0);
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
            if (_folderList == null) return;
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
            App.Save();
            RefreshFolderList();
            _folderList.SelectedIndex = insert;
            App.Bar?.ApplyFolderOrder();
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
            _gpuLoadVersion++;
            _gpuOptionsPending = _gpuOptionsLoaded = false;
            void SaveVisibility()
            {
                App.Save();
                App.Bar?.ApplyModuleVisibility();
            }

            var modules = Card("模块显示", null, out var moduleBody);
            var switches = new UniformGrid { Columns = 2 };
            void AddSwitch(string label, bool current, Action<bool> changed)
            {
                var row = ToggleRow(label, current, enabled => { changed(enabled); SaveVisibility(); });
                row.Margin = new Thickness(switches.Children.Count % 2 == 0 ? 0 : 20, 8,
                    switches.Children.Count % 2 == 0 ? 20 : 0, 0);
                switches.Children.Add(row);
            }
            AddSwitch("电源按钮", S.ShowPower, v => S.ShowPower = v);
            AddSwitch("此电脑", S.ShowThisPc, v => S.ShowThisPc = v);
            AddSwitch("小文件夹", S.ShowFolders, v => S.ShowFolders = v);
            AddSwitch("回收站", S.ShowRecycleBin, v => S.ShowRecycleBin = v);
            AddSwitch("音乐播放器", S.ShowMusic, v => S.ShowMusic = v);
            AddSwitch("音量按钮", S.ShowVolume, v => S.ShowVolume = v);
            AddSwitch("音频频谱", S.ShowSpectrum, v => S.ShowSpectrum = v);
            if (BatteryModule.Available)
                AddSwitch("电池与供电", S.ShowBattery, v => S.ShowBattery = v);
            moduleBody.Children.Add(switches);
            ModulesHost.Children.Add(modules);
            ModulesHost.Children.Add(BuildLayoutCard());
            ModulesHost.Children.Add(BuildFavoritesCard());

            var card = Card("性能监测", null, out var body);
            body.Children.Add(ToggleRow("CPU 占用率", S.ShowMonitorCpu,
                v => { S.ShowMonitorCpu = v; SaveVisibility(); }));
            body.Children.Add(ToggleRow("GPU 占用率", S.ShowMonitorGpu,
                v =>
                {
                    S.ShowMonitorGpu = v;
                    SaveVisibility();
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
                App.Save();
            };
            body.Children.Add(Row("监测的 GPU", _gpuCombo));
            body.Children.Add(ToggleRow("内存占用", S.ShowMonitorMemory,
                v => { S.ShowMonitorMemory = v; SaveVisibility(); }));
            body.Children.Add(ToggleRow("网络上下行", S.ShowMonitorNetwork,
                v => { S.ShowMonitorNetwork = v; SaveVisibility(); }));
            body.Children.Add(ToggleRow("合并性能胶囊", S.CombinePerfModules, v => { S.CombinePerfModules = v; SaveVisibility(); }));

            // 原生 CPU 温度(AMD 平台):经 PawnIO 读取真 Tctl;未装时回落 SoC/热区传感器。
            if (DesktopBar.Services.PerfService.CpuIsAmd)
            {
                _nativeTempStatus = new TextBlock
                {
                    Text = "未启用",
                    FontSize = 13,
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
                RefreshNativeTempStatus();
            }

            body.Children.Add(MakeHint("CPU、GPU、内存、网络胶囊位于音频频谱左侧;采样只在任一胶囊可见时进行。"
                + "GPU 占用率来自系统 GPU 引擎计数器,多显卡时可在上方选择要监测的 GPU。"));
            ModulesHost.Children.Add(card);
            ModulesHost.Children.Add(BuildPresetsCard());
            ModulesHost.Children.Add(BuildAppSceneCard());
        }

        private ComboBox _gpuCombo;
        private bool _updatingGpuCombo;
        private bool _gpuOptionsPending, _gpuOptionsLoaded;
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

        private bool IsPagePresented(string page) =>
            _currentPage == page && IsVisible && WindowState != WindowState.Minimized;

        private void UpdatePageActivity()
        {
            OnUpdateStatusChanged();
            if (!IsPagePresented("Modules"))
            {
                _tempStatusTimer?.Stop();
                return;
            }
            if (S.ShowMonitorGpu && !_gpuOptionsLoaded) LoadGpuOptions();
            if (_nativeTempStatus == null || _tempStatusTimer?.IsEnabled == true) return;
            if (_tempStatusTimer == null)
            {
                _tempStatusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                _tempStatusTimer.Tick += (_, _) => PollNativeTemperature();
            }
            PollNativeTemperature();
            _tempStatusTimer.Start();
        }

        /// <summary>
        /// 周期刷新温度状态:状态行只在构建时评估一次会永远停留旧值;
        /// 每 2 秒刷新一次(共享内存检测很轻),每 15 秒才真正查一次 schtasks(进程启动较重)。
        /// </summary>
        private void PollNativeTemperature()
        {
            if (_pawnInstalling) return;
            long now = Environment.TickCount64;
            if (now - _lastTaskProbeTick > 15000)
            {
                _lastTaskProbeTick = now;
                System.Threading.Tasks.Task.Run(ProbeSlaveTask);
                // 任务存在但从属没有数据时，主动拉起一次。
                if (_lastTaskExists && _lastTaskPathOk && !TemperatureSlaveFresh() && !PawnIoTctl.IsAvailable)
                    System.Threading.Tasks.Task.Run(RunSlaveTaskOnce);
            }
            RefreshNativeTempStatus();
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
            if (_gpuOptionsPending) return;
            _gpuOptionsPending = true;
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
                _gpuOptionsLoaded = true;
                _gpuCombo.IsEnabled = S.ShowMonitorGpu && gpus.Count > 0;
            }
            catch (Exception ex)
            {
                App.Log("加载显卡列表失败: " + ex.Message);
            }
            finally
            {
                if (version == _gpuLoadVersion)
                    _gpuOptionsPending = _updatingGpuCombo = false;
            }
        }

        // ---------- 通用 ----------
        private void BuildGeneralPage()
        {
            GeneralHost.Children.Clear();

            var card1 = Card("启动", null, out var b1);
            // 开关初值取注册表实况而非内存值:用户手删 Run 键/被组策略拦截时两者会脱节;
            // 写入失败时回读校正并把开关拨回去,不让 UI 显示一个没生效的状态。
            ToggleButton autostartToggle = null;
            b1.Children.Add(ToggleRow("开机自动启动", AutoStartService.IsEnabled(), v =>
            {
                bool ok = AutoStartService.Set(v);
                S.AutoStart = AutoStartService.IsEnabled();
                App.Save();
                autostartToggle?.SetCurrentValue(ToggleButton.IsCheckedProperty, S.AutoStart);
                if (!ok)
                {
                    ConfirmDialog.Notice("设置未生效",
                        "开机自动启动未能写入注册表(可能被组策略或安全软件拦截)。");
                }
            }, onCreated: t => autostartToggle = t));
            b1.Children.Add(MakeHint("登录 Windows 后为当前用户启动 YukiDesk。"));
            GeneralHost.Children.Add(card1);

            GeneralHost.Children.Add(BuildRevealCard());
            var motion = Card("展开与收起", null, out var motionBody);
            motionBody.Children.Add(Row("悬停关闭", NumberSlider(80, 2000, S.HoverCloseDelayMs, "ms", v =>
            {
                S.HoverCloseDelayMs = (int)v;
                App.Save();
            }), "鼠标离开 延迟 展开页"));
            motionBody.Children.Add(Row("动画时长", NumberSlider(80, 1200, S.AnimationDurationMs, "ms", v =>
            {
                S.AnimationDurationMs = (int)v;
                App.Save();
            }), "动画速度 展开页"));
            GeneralHost.Children.Add(motion);

            var captureCard = Card("玻璃取景", null, out var captureBody);
            string CaptureDescription(int mode) => mode switch
            {
                1 => "玻璃合成背后的窗口，桌面空白处映出壁纸。顶栏与展开页可出现在录屏和远程画面。",
                2 => "玻璃只取桌面壁纸。适合录制时减少采集开销，顶栏与展开页仍可被捕获。",
                _ => "玻璃从屏幕取景。屏幕捕获画面会隐藏顶栏与展开页。"
            };
            int captureMode = S.WallpaperOnlyCapture ? 2 : S.ShowInScreenCapture ? 1 : 0;
            var captureDescription = MakeHint(CaptureDescription(captureMode), 5);
            captureBody.Children.Add(Row("取景方式", BuildChoices("CaptureMode", captureMode,
                new[] { ("整屏采样", 0), ("逐窗口采样", 1), ("仅取壁纸", 2) }, mode =>
            {
                S.ShowInScreenCapture = mode == 1;
                S.WallpaperOnlyCapture = mode == 2;
                App.Save();
                GlassService.ApplyCaptureVisibility();
                captureDescription.Text = CaptureDescription(mode);
            }), "整屏采样 逐窗口采样 仅取壁纸 截图 录屏 录制 远程控制 屏幕共享"));
            captureBody.Children.Add(captureDescription);
            GeneralHost.Children.Add(captureCard);
            GeneralHost.Children.Add(BuildEdgeExclusionsCard());

            var cardSpace = Card("桌面空间", null, out var bs);
            bs.Children.Add(ToggleRow("为顶栏预留桌面空间", S.ReserveDesktopSpace, v => { S.ReserveDesktopSpace = v; DesktopSpaceService.Apply(S); App.Save(); }));
            bs.Children.Add(Row("预留高度", NumberSlider(38, 160, S.DesktopSpaceHeight, "px", v => { S.DesktopSpaceHeight = (int)v; if (S.ReserveDesktopSpace) DesktopSpaceService.Apply(S); App.Save(); })));
            GeneralHost.Children.Add(cardSpace);

            var card2 = Card("数据", null, out var b2);
            b2.Children.Add(Row("配置与日志", PanelControls.Button("打开配置目录", () => ShellOpen.OpenPath(App.AppDataDir)), "数据目录 配置文件 日志文件"));
            b2.Children.Add(Row("小文件夹存储", PanelControls.Button("打开存储目录", () => ShellOpen.OpenPath(S.StorageRoot)), "小文件夹目录"));
            GeneralHost.Children.Add(card2);
            GeneralHost.Children.Add(BuildBackupCard());
        }

        private Button _revealShortcutButton;
        private TextBlock _revealShortcutStatus;
        private bool _recordingRevealShortcut;

        private FrameworkElement BuildRevealCard()
        {
            var card = Card("顶栏唤出", null, out var body);
            body.Children.Add(ToggleRow("鼠标触顶唤出", S.RevealOnTopEdge, enabled =>
            {
                S.RevealOnTopEdge = enabled;
                App.Bar?.SetEdgeReveal(enabled);
                App.Save();
            }));
            body.Children.Add(Row("触顶停留", NumberSlider(0, AppSettings.MaxRevealEdgeDwellMs, S.RevealEdgeDwellMs, "ms", value =>
            {
                S.RevealEdgeDwellMs = (int)value;
                App.Save();
            }, step: 10)));
            body.Children.Add(MakeHint("鼠标在屏幕顶边停满设定时长后唤出。离开顶栏和展开页后收起。", 4));
            var displays = new List<(string Name, string Device, bool Pointer)> { ("主屏", "", false), ("跟随指针", "", true) };
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                if (!screen.Primary)
                    displays.Add(($"副屏 · {screen.Bounds.Width} × {screen.Bounds.Height} ({screen.DeviceName})", screen.DeviceName, false));
            var displayPicker = new ComboBox
            {
                Style = (Style)FindResource("DarkCombo"),
                Width = 230,
                ItemsSource = displays.Select(item => item.Name).ToArray(),
                SelectedIndex = Math.Max(0, displays.FindIndex(item => item.Device == S.RevealDisplayName && item.Pointer == S.RevealOnPointerScreen))
            };
            displayPicker.SelectionChanged += (_, _) =>
            {
                if (displayPicker.SelectedIndex < 0) return;
                var display = displays[displayPicker.SelectedIndex];
                S.RevealDisplayName = display.Device;
                S.RevealOnPointerScreen = display.Pointer;
                App.Save();
            };
            body.Children.Add(Row("唤出屏幕", displayPicker));
            body.Children.Add(MakeHint("副屏唤出使用纯色材质。收起后顶栏回到主屏。", 4));

            ToggleButton hotkeyToggle = null;
            body.Children.Add(ToggleRow("全局快捷键唤出", S.RevealWithHotkey, enabled =>
            {
                if (App.Bar?.TrySetRevealHotkey(enabled, BarHotkey.FromSettings(S)) != true)
                {
                    hotkeyToggle?.SetCurrentValue(ToggleButton.IsCheckedProperty, S.RevealWithHotkey);
                    ShowRevealShortcutStatus("这个快捷键无法注册，可能已被 Windows 或其他应用占用。");
                    return;
                }
                S.RevealWithHotkey = enabled;
                App.Save();
                ShowRevealShortcutStatus("");
            }, onCreated: toggle => hotkeyToggle = toggle));
            _revealShortcutButton = new Button
            {
                Content = BarHotkey.FromSettings(S).Display,
                Style = (Style)FindResource("Btn"),
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "点击后按下所需的快捷键"
            };
            _revealShortcutButton.Click += (_, _) =>
            {
                _revealShortcutButton.Focus();
                _recordingRevealShortcut = true;
                _revealShortcutButton.Content = "按下快捷键…";
                ShowRevealShortcutStatus("按组合键或功能键。按 Esc 取消。");
                App.Bar?.CaptureRevealHotkey(SetRevealShortcut);
            };
            _revealShortcutButton.LostKeyboardFocus += (_, _) => EndRevealShortcutCapture();
            body.Children.Add(Row("唤出快捷键", _revealShortcutButton));
            _revealShortcutStatus = MakeHint("", 4);
            body.Children.Add(_revealShortcutStatus);
            ShowRevealShortcutStatus(S.RevealWithHotkey && App.Bar?.RevealHotkeyRegistered == false
                ? "当前快捷键无法注册，请换一个组合。" : "");
            body.Children.Add(Row("快捷键停留", NumberSlider(AppSettings.MinRevealDurationSeconds,
                AppSettings.MaxRevealDurationSeconds, S.RevealDurationSeconds, "秒", value =>
            {
                S.RevealDurationSeconds = (int)value;
                App.Save();
            })));
            return card;
        }

        private void SetRevealShortcut(BarHotkey shortcut)
        {
            if (!_recordingRevealShortcut) return;
            if (!shortcut.IsValid)
            {
                ShowRevealShortcutStatus("请使用有效的组合键或功能键。按 Esc 取消。");
                return;
            }
            int slot = shortcut.FolderSlot;
            if (slot >= 0 && !string.IsNullOrEmpty(S.FolderShortcuts[slot]))
            {
                ShowRevealShortcutStatus("这组键已分配给小文件夹，请试试其他组合。按 Esc 取消。");
                return;
            }
            if (S.RevealWithHotkey && App.Bar?.TrySetRevealHotkey(true, shortcut) != true)
            {
                ShowRevealShortcutStatus("这个快捷键无法注册，请试试其他组合。按 Esc 取消。");
                return;
            }
            S.RevealHotkeyKey = shortcut.Key;
            S.RevealHotkeyModifiers = shortcut.Modifiers;
            App.Save();
            EndRevealShortcutCapture();
        }

        private void EndRevealShortcutCapture()
        {
            if (!_recordingRevealShortcut) return;
            _recordingRevealShortcut = false;
            App.Bar?.CaptureRevealHotkey(null);
            _revealShortcutButton.Content = BarHotkey.FromSettings(S).Display;
            ShowRevealShortcutStatus(S.RevealWithHotkey && App.Bar?.RevealHotkeyRegistered == false
                ? "当前快捷键无法注册，请换一个组合。" : "");
        }

        private void ShowRevealShortcutStatus(string text)
        {
            _revealShortcutStatus.Text = text;
            _revealShortcutStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------- 关于 ----------
        private TextBlock _updateStatus;
        private Button _checkButton;

        private void BuildAboutPage()
        {
            AboutHost.Children.Clear();

            var about = Card("YukiDesk", null, out var ab);
            var verTb = new TextBlock
            {
                Text = "v" + (typeof(SettingsWindow).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                    ?? UpdateService.CurrentVersion.ToString(3)),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            verTb.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
            ab.Children.Add(Row("当前版本", verTb));
            var ghBtn = new Button { Content = "打开 GitHub 主页", Style = (Style)FindResource("Btn") };
            ghBtn.Click += (s, e) => NativeMethods.ShellExecuteOpen("https://github.com/Akiyuki1130/YukiDesk");
            ab.Children.Add(Row("项目主页", ghBtn));
            var contributors = new UniformGrid { Columns = 2, Margin = new Thickness(0, 14, 0, 0) };
            var developer = GitHubProfileButton("开发者", "Akiyuki1130");
            developer.Margin = new Thickness(0, 0, 5, 0);
            contributors.Children.Add(developer);
            var collaborator = GitHubProfileButton("协助者", "OneCat2015");
            collaborator.Margin = new Thickness(5, 0, 0, 0);
            contributors.Children.Add(collaborator);
            ab.Children.Add(contributors);
            AboutHost.Children.Add(about);

            var upd = Card("软件更新", null, out var ub);
            ub.Children.Add(ToggleRow("自动检查更新", S.AutoCheckUpdate, v => { S.AutoCheckUpdate = v; App.Save(); }));
            ub.Children.Add(ToggleRow("自动下载更新", S.AutoUpdate, v => { S.AutoUpdate = v; App.Save(); }));
            _checkButton = new Button { Content = "检查更新", Style = (Style)FindResource("BtnAccent") };
            _checkButton.Click += async (s, e) =>
            {
                if (UpdateService.CanCancelDownload) UpdateService.CancelDownload();
                else await UpdateService.RunCheckAsync(autoDownload: S.AutoUpdate);
            };
            ub.Children.Add(Row("手动检查更新", _checkButton));
            _updateStatus = new TextBlock
            {
                FontSize = 12.5,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            _updateStatus.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
            RefreshUpdateStatus();
            ub.Children.Add(_updateStatus);
            ub.Children.Add(MakeHint("「自动下载更新」需要开启「自动检查更新」;下载完成后会先询问,确认后才安装。" +
                                     "安装新版本时会退出 YukiDesk 并启动安装程序,设置与小文件夹不会丢失。", 14));
            AboutHost.Children.Add(upd);
            AboutHost.Children.Add(BuildDiagnosticsCard());
        }

        private Button GitHubProfileButton(string role, string login)
        {
            string profile = $"https://github.com/{login}";
            var avatarUri = new Uri($"pack://application:,,,/YukiDesk;component/Assets/Contributors/{login}.png");
            var avatar = new System.Windows.Shapes.Ellipse
            {
                Width = 38,
                Height = 38,
                Margin = new Thickness(0, 0, 10, 0),
                Fill = new ImageBrush(new BitmapImage(avatarUri)) { Stretch = Stretch.UniformToFill }
            };
            var roleText = new TextBlock { Text = role, FontSize = 12 };
            roleText.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(roleText);
            labels.Children.Add(new TextBlock { Text = login, FontSize = 14, FontWeight = FontWeights.Medium });

            var arrow = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 1,9 L 9,1 M 4,1 L 9,1 L 9,6"),
                Width = 12,
                Height = 12,
                Margin = new Thickness(8, 0, 0, 0),
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
            };
            arrow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SettingsAccent");
            var content = new DockPanel();
            DockPanel.SetDock(avatar, Dock.Left);
            DockPanel.SetDock(arrow, Dock.Right);
            content.Children.Add(avatar);
            content.Children.Add(arrow);
            content.Children.Add(labels);
            var button = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 7, 12, 7),
                Style = (Style)FindResource("Btn"),
                ToolTip = profile
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{role} {login}，打开 GitHub 主页");
            button.Click += (_, _) => NativeMethods.ShellExecuteOpen(profile);
            return button;
        }

        private void OnUpdateStatusChanged()
        {
            if (IsPagePresented("About")) RefreshUpdateStatus();
        }

        private void RefreshUpdateStatus()
        {
            if (_checkButton == null) return;
            bool cancelable = UpdateService.CanCancelDownload;
            _checkButton.Content = cancelable ? "取消下载" : "检查更新";
            _checkButton.IsEnabled = cancelable || !UpdateService.Busy;
            _updateStatus.Text = UpdateService.Status;
            _updateStatus.Visibility = _updateStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
