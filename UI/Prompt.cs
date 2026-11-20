using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    /// <summary>暗色风格的小输入对话框,返回输入内容(取消返回 null)。</summary>
    public static class Prompt
    {
        public static string Show(string title, string label, string initial)
        {
            string result = null;
            bool ok = false;

            var box = new TextBox
            {
                Style = (Style)Application.Current.FindResource("DarkBox"),
                Text = initial ?? "",
                Margin = new Thickness(0, 12, 0, 0),
                FontSize = 14
            };
            box.SetResourceReference(TextBox.ForegroundProperty, "ThemePanelText");
            box.SetResourceReference(TextBox.CaretBrushProperty, "ThemePanelText");
            box.SetResourceReference(TextBox.BackgroundProperty, "ThemePanelHover");
            box.SetResourceReference(TextBox.BorderBrushProperty, "ThemePanelBorder");

            var btnCancel = new Button
            {
                Content = "取消",
                Style = (Style)Application.Current.FindResource("BtnTheme"),
                IsCancel = true,
                Margin = new Thickness(0, 18, 8, 0)
            };
            var btnOk = new Button
            {
                Content = "确定",
                Style = (Style)Application.Current.FindResource("BtnTheme"),
                IsDefault = true,
                Margin = new Thickness(0, 18, 0, 0)
            };
            btnCancel.Click += (s, e) => ((Window)btnCancel.Tag).Close();
            btnOk.Click += (s, e) => { ok = true; ((Window)btnOk.Tag).Close(); };

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            btnRow.Children.Add(btnCancel);
            btnRow.Children.Add(btnOk);

            var body = new StackPanel { Margin = new Thickness(22) };
            var labelText = new TextBlock
            {
                Text = label,
                FontSize = 14
            };
            labelText.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            body.Children.Add(labelText);
            body.Children.Add(box);
            body.Children.Add(btnRow);

            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Child = body
            };
            card.SetResourceReference(Border.BackgroundProperty, "ThemePanelBackground");
            card.SetResourceReference(Border.BorderBrushProperty, "ThemePanelBorder");
            var w = new Window
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
            btnCancel.Tag = w;
            btnOk.Tag = w;
            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            w.ShowDialog();

            if (ok) result = box.Text?.Trim();
            // 确定+空输入与取消同样返回 null:所有调用方都是"名称"类输入,
            // 空值按取消静默处理,避免"名称无效"之类误报。
            if (string.IsNullOrWhiteSpace(result)) return null;
            return result;
        }
    }
}
