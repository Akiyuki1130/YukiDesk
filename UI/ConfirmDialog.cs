using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    /// <summary>同名冲突弹窗的选择结果：替换旧项 / 保留为副本。</summary>
    public enum ConflictChoice { Replace, KeepBoth }

    /// <summary>
    /// 圆角半透明确认弹窗：始终置顶（呼出即在最上层）、深色毛玻璃质感、支持拖动与回车/Esc。
    /// 用于顶栏模块的确认场景，替代会被其他窗口压住的系统消息框。
    /// </summary>
    public static class ConfirmDialog
    {
        public static bool Show(string title, string message, string confirmText = "确定", bool okIsDefault = true)
            => ShowInternal(title, message, confirmText, "取消", true, false, okIsDefault);

        /// <summary>不可逆或高风险操作确认；默认键盘动作保持在取消侧。</summary>
        public static bool ShowDanger(string title, string message, string confirmText = "确定")
            => ShowInternal(title, message, confirmText, "取消", true, true);

        /// <summary>单按钮通知弹窗（同样置顶）。</summary>
        public static void Notice(string title, string message)
            => ShowInternal(title, message, "知道了", null, false, false);

        /// <summary>
        /// 同名冲突弹窗：替换（旧项目移入回收站）/ 作为副本（自动编号）/ 取消。
        /// applyToAll 来自“为全部文件执行此操作”勾选框；返回 null 表示取消（中止剩余项）。
        /// </summary>
        public static ConflictChoice? ShowConflict(string title, string message, out bool applyToAll)
        {
            ConflictChoice? result = null;
            bool checkedState = false, applyAll = false;
            applyToAll = false;
            var win = new Window
            {
                FontFamily = Ui.TextFont,
                FontWeight = FontWeights.SemiBold,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = true,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                MinWidth = 420
            };

            var msg = new TextBlock
            {
                Text = message,
                FontSize = 14,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78,
                Margin = new Thickness(0, 10, 0, 0)
            };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");

            // 自绘勾选框：默认 CheckBox 的系统样式与深色弹窗不搭。
            var checkMark = new TextBlock
            {
                Text = "✓",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            checkMark.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            var box = new Border
            {
                Width = 17,
                Height = 17,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = checkMark
            };
            box.SetResourceReference(Border.BackgroundProperty, "ThemePanelHover");
            box.SetResourceReference(Border.BorderBrushProperty, "ThemePanelText");
            var checkRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 16, 0, 0),
                Cursor = Cursors.Hand
            };
            checkRow.Children.Add(box);
            var checkLabel = new TextBlock
            {
                Text = "为全部文件执行此操作",
                FontSize = 13,
                Opacity = 0.78,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0)
            };
            checkLabel.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            checkRow.Children.Add(checkLabel);
            checkRow.MouseLeftButtonUp += (s, e) =>
            {
                checkedState = !checkedState;
                checkMark.Visibility = checkedState ? Visibility.Visible : Visibility.Collapsed;
                if (checkedState) box.Background = Brush("#4C7DFF");
                else box.SetResourceReference(Border.BackgroundProperty, "ThemePanelHover");
            };

            var cancel = new Button
            {
                Content = "取消",
                Style = (Style)Application.Current.FindResource("BtnTheme"),
                MinWidth = 92,
                IsCancel = true
            };
            var keepBoth = new Button
            {
                Content = "作为副本",
                Style = (Style)Application.Current.FindResource("BtnTheme"),
                MinWidth = 100,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var replace = new Button
            {
                Content = "替换",
                Style = (Style)Application.Current.FindResource("BtnThemeDanger"),
                MinWidth = 92,
                Margin = new Thickness(0, 0, 10, 0)
            };
            cancel.Click += (s, e) => { result = null; win.Close(); };
            keepBoth.Click += (s, e) => { result = ConflictChoice.KeepBoth; applyAll = checkedState; win.Close(); };
            replace.Click += (s, e) => { result = ConflictChoice.Replace; applyAll = checkedState; win.Close(); };
            // 初始焦点给取消:此前三个按钮都无焦点/无默认键,键盘用户只剩 Esc;
            // Enter 落在取消侧,永远不能误触发"替换"这种会移走旧项目的动作。
            win.Loaded += (s, e) => cancel.Focus();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 22, 0, 0)
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(keepBoth);
            buttons.Children.Add(replace);

            var content = new StackPanel();
            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.Bold
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            content.Children.Add(titleText);
            content.Children.Add(msg);
            content.Children.Add(checkRow);
            content.Children.Add(buttons);

            var border = new Border
            {
                // 留出阴影空间
                Margin = new Thickness(18),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24, 20, 24, 20),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 26,
                    ShadowDepth = 6,
                    Opacity = 0.5,
                    Color = Colors.Black
                },
                Child = content
            };
            border.SetResourceReference(Border.BackgroundProperty, "ThemePanelBackground");
            border.SetResourceReference(Border.BorderBrushProperty, "ThemePanelBorder");
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { win.DragMove(); } catch (InvalidOperationException) { }
                }
            };

            win.Content = border;
            win.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { result = null; win.Close(); }
            };
            win.ShowDialog();
            applyToAll = applyAll;
            return result;
        }

        private static bool ShowInternal(string title, string message, string confirmText, string cancelText,
            bool showCancel, bool danger, bool okIsDefault = true)
        {
            bool result = false;
            var win = new Window
            {
                FontFamily = Ui.TextFont,
                FontWeight = FontWeights.SemiBold,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = true,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                MinWidth = 380
            };

            var titleTb = new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.Bold
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            var msg = new TextBlock
            {
                Text = message,
                FontSize = 14,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78,
                Margin = new Thickness(0, 10, 0, 0)
            };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");

            var cancel = new Button
            {
                Content = cancelText,
                Style = (Style)Application.Current.FindResource("BtnTheme"),
                MinWidth = 92,
                Margin = new Thickness(0, 0, 10, 0),
                // Notice(单按钮)时取消按钮折叠,不再注册 IsCancel,避免 Esc 走两条等价通道。
                IsCancel = showCancel,
                Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed
            };
            var ok = new Button
            {
                Content = confirmText,
                Style = (Style)Application.Current.FindResource(danger ? "BtnThemeDanger" : "BtnTheme"),
                MinWidth = 92,
                IsDefault = !danger && okIsDefault
            };
            cancel.Click += (s, e) => { result = false; win.Close(); };
            ok.Click += (s, e) => { result = true; win.Close(); };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 22, 0, 0)
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            var content = new StackPanel();
            content.Children.Add(titleTb);
            content.Children.Add(msg);
            content.Children.Add(buttons);

            var border = new Border
            {
                // 留出阴影空间
                Margin = new Thickness(18),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24, 20, 24, 20),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 26,
                    ShadowDepth = 6,
                    Opacity = 0.5,
                    Color = Colors.Black
                },
                Child = content
            };
            border.SetResourceReference(Border.BackgroundProperty, "ThemePanelBackground");
            border.SetResourceReference(Border.BorderBrushProperty, "ThemePanelBorder");
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { win.DragMove(); } catch (InvalidOperationException) { }
                }
            };

            win.Content = border;
            win.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { result = false; win.Close(); }
            };
            win.Loaded += (s, e) =>
            {
                // 危险确认或显式要求"默认在取消侧"时,焦点落取消键:回车/空格都不会触发确认动作。
                if (showCancel && (danger || !okIsDefault)) cancel.Focus();
                else ok.Focus();
            };
            win.ShowDialog();
            return result;
        }

        private static Brush Brush(string hex, double opacity = 1)
            => Ui.Brush(hex, opacity);
    }
}
