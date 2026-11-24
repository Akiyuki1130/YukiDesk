using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopBar.UI
{
    public static class Prompt
    {
        public static string Show(string title, string label, string initial)
        {
            var box = Field(label, initial, out var field);
            return ShowInput(title, field, box, () => null) && !string.IsNullOrWhiteSpace(box.Text)
                ? box.Text.Trim()
                : null;
        }

        internal static TimeSpan? ShowCountdown()
        {
            var minutes = Field("分钟", "5", out var minuteField);
            var seconds = Field("秒", "0", out var secondField);
            var fields = new Grid();
            fields.ColumnDefinitions.Add(new ColumnDefinition());
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            fields.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(secondField, 2);
            fields.Children.Add(minuteField);
            fields.Children.Add(secondField);

            TimeSpan duration = default;
            bool accepted = ShowInput("倒计时", fields, minutes, () =>
                TryCountdownDuration(minutes.Text, seconds.Text, out duration)
                    ? null : "请输入 1 秒到 720 分钟，秒数为 0–59。");
            return accepted ? duration : null;
        }

        internal static bool TryCountdownDuration(string minuteText, string secondText, out TimeSpan duration)
        {
            duration = default;
            if (!int.TryParse(minuteText, out int minutes) || minutes is < 0 or > 720
                || !int.TryParse(secondText, out int seconds) || seconds is < 0 or > 59)
                return false;
            int totalSeconds = minutes * 60 + seconds;
            if (totalSeconds is < 1 or > 720 * 60) return false;
            duration = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }

        private static TextBox Field(string label, string initial, out StackPanel field)
        {
            var text = new TextBlock { Text = label, FontSize = 14 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            var box = new TextBox
            {
                Style = (Style)Application.Current.FindResource("DarkBox"),
                Text = initial ?? "",
                Margin = new Thickness(0, 6, 0, 0),
                FontSize = 14
            };
            AutomationProperties.SetName(box, label);
            box.SetResourceReference(TextBox.ForegroundProperty, "ThemePanelText");
            box.SetResourceReference(TextBox.CaretBrushProperty, "ThemePanelText");
            box.SetResourceReference(TextBox.BackgroundProperty, "ThemePanelHover");
            box.SetResourceReference(TextBox.BorderBrushProperty, "ThemePanelBorder");
            field = new StackPanel();
            field.Children.Add(text);
            field.Children.Add(box);
            return box;
        }

        private static bool ShowInput(string title, FrameworkElement fields, TextBox first, Func<string> validate)
        {
            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 15)
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            var error = new TextBlock { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed,
                FontWeight = FontWeights.Bold };
            error.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            var buttonStyle = (Style)Application.Current.FindResource("BtnTheme");
            var cancel = new Button { Content = "取消", Style = buttonStyle, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            var ok = new Button { Content = "确定", Style = buttonStyle, IsDefault = true };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            var body = new StackPanel();
            body.Children.Add(titleText);
            body.Children.Add(fields);
            body.Children.Add(error);
            body.Children.Add(buttons);
            var card = new Border
            {
                CornerRadius = new CornerRadius(26),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(22),
                Child = body
            };
            PopupPalette.Apply(card, dialog: true);
            var window = new Window
            {
                FontFamily = Ui.TextFont,
                FontWeight = FontWeights.SemiBold,
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Content = card
            };
            ok.Click += (_, _) =>
            {
                string message = validate();
                if (message == null) window.DialogResult = true;
                else
                {
                    error.Text = message;
                    error.Visibility = Visibility.Visible;
                }
            };
            window.Loaded += (_, _) => { first.Focus(); first.SelectAll(); };
            return window.ShowDialog() == true;
        }
    }
}
