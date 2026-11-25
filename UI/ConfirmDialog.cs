using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DesktopBar.UI
{
    /// <summary>
    /// 圆角确认弹窗：始终置顶，支持拖动与回车/Esc。
    /// 用于顶栏模块的确认场景，替代会被其他窗口压住的系统消息框。
    /// </summary>
    public static class ConfirmDialog
    {
        public static bool Show(string title, string message, string confirmText = "确定", bool okIsDefault = true)
            => ShowInternal(title, message, confirmText, "取消", true, false, okIsDefault);

        internal static bool Show(string title, string message, string confirmText, string cancelText, bool okIsDefault)
            => ShowInternal(title, message, confirmText, cancelText, true, false, okIsDefault);

        /// <summary>不可逆或高风险操作确认；默认键盘动作保持在取消侧。</summary>
        public static bool ShowDanger(string title, string message, string confirmText = "确定")
            => ShowInternal(title, message, confirmText, "取消", true, true);

        /// <summary>单按钮通知弹窗（同样置顶）。</summary>
        public static void Notice(string title, string message)
            => ShowInternal(title, message, "知道了", null, false, false);

        private static bool ShowInternal(string title, string message, string confirmText, string cancelText,
            bool showCancel, bool danger, bool okIsDefault = true)
        {
            bool result = false;
            var workArea = SystemParameters.WorkArea;
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
                MinWidth = Math.Min(380, workArea.Width),
                MaxWidth = Math.Min(620, workArea.Width)
            };

            var titleTb = new TextBlock
            {
                Text = title,
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.Bold
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            var msg = new TextBlock
            {
                Text = message,
                FontSize = 14,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.90,
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
            content.Children.Add(new ScrollViewer
            {
                Content = msg,
                MaxHeight = workArea.Height * .55,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            });
            content.Children.Add(buttons);

            var border = new Border
            {
                // 留出阴影空间
                Margin = new Thickness(18),
                CornerRadius = new CornerRadius(26),
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
            PopupPalette.Apply(border, dialog: true);
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

    }
}
