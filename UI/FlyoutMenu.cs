using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopBar.Interop;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    public sealed class FlyoutItem
    {
        public string Text { get; }
        public Action OnClick { get; }
        public bool Danger { get; }
        public bool Enabled { get; }

        public FlyoutItem(string text, Action onClick = null, bool danger = false, bool enabled = true)
        {
            Text = text;
            OnClick = onClick;
            Danger = danger;
            Enabled = enabled;
        }
    }

    /// <summary>
    /// 与展开页同主题的右键菜单：置顶 Popup + 免激活样式，在鼠标位置弹出。
    /// 自带场景（桌面层窗口不抢焦点）下比 WPF ContextMenu 更可靠。
    /// 返回 Popup，调用方用 ModuleBase.TrackFlyout 登记，避免面板被自动收起。
    /// </summary>
    public static class FlyoutMenu
    {
        public static Popup Show(IReadOnlyList<FlyoutItem> items, string caption = null)
        {
            if (items == null || items.Count == 0) return null;
            double s = Ui.Scale;

            var sp = new StackPanel();
            TextElement.SetFontFamily(sp, Ui.TextFont);
            if (!string.IsNullOrEmpty(caption))
            {
                var captionText = new TextBlock
                {
                    Text = caption,
                    FontSize = 12 * s,
                    FontWeight = FontWeights.SemiBold,
                    Opacity = 0.65,
                    Margin = new Thickness(6 * s, 2 * s, 6 * s, 6 * s)
                };
                captionText.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
                sp.Children.Add(captionText);
            }

            Popup popup = null;
            foreach (var item in items)
            {
                var text = new TextBlock
                {
                    Text = item.Text,
                    FontSize = 13.5 * s,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = item.Enabled ? 1 : 0.4
                };
                if (item.Danger) text.Foreground = Ui.Brush("#F2777C");
                else text.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
                // 静态半透明深色行（比菜单背景深一点），悬停加深。
                var row = new Border
                {
                    CornerRadius = new CornerRadius(8 * s),
                    Padding = new Thickness(10 * s, 7 * s, 10 * s, 7 * s),
                    MinWidth = 138 * s,
                    Child = text
                };
                row.SetResourceReference(Border.BackgroundProperty, "ThemePanelHover");
                var captured = item;
                if (item.Enabled)
                {
                    row.Cursor = System.Windows.Input.Cursors.Hand;
                    row.MouseEnter += (s2, e2) => row.SetResourceReference(Border.BackgroundProperty, "ThemePanelSelected");
                    row.MouseLeave += (s2, e2) => row.SetResourceReference(Border.BackgroundProperty, "ThemePanelHover");
                    row.MouseLeftButtonUp += (s2, e2) =>
                    {
                        if (popup != null) popup.IsOpen = false;
                        captured.OnClick?.Invoke();
                    };
                }
                sp.Children.Add(row);
            }

            var border = new Border
            {
                CornerRadius = new CornerRadius(10 * s),
                BorderThickness = new Thickness(1 * s),
                Padding = new Thickness(6 * s),
                Child = sp
            };
            border.SetResourceReference(Border.BackgroundProperty, "ThemePanelBackground");
            border.SetResourceReference(Border.BorderBrushProperty, "ThemePanelBorder");
            popup = new Popup
            {
                Child = border,
                AllowsTransparency = true,
                Placement = PlacementMode.MousePoint,
                StaysOpen = false,
                Focusable = false
            };
            // 鼠标离开菜单超时后自动关闭（点击/移回菜单会取消），避免菜单一直悬着。
            var closeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(900, (App.Settings?.HoverCloseDelayMs ?? 250) + 650))
            };
            closeTimer.Tick += (s2, e2) =>
            {
                closeTimer.Stop();
                if (popup != null) popup.IsOpen = false;
            };
            border.MouseEnter += (s2, e2) => closeTimer.Stop();
            border.MouseLeave += (s2, e2) => closeTimer.Start();
            popup.Opened += (s2, e2) =>
            {
                closeTimer.Start();
                try
                {
                    var src = (HwndSource)PresentationSource.FromVisual(border);
                    if (src != null) NativeMethods.ApplyNoActivateToolStyle(src.Handle);
                }
                catch { }
            };
            popup.Closed += (s2, e2) => closeTimer.Stop();
            popup.IsOpen = true;
            return popup;
        }
    }
}
