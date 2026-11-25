using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopBar.Interop;

namespace DesktopBar.UI
{
    public sealed class FlyoutItem
    {
        public string Text { get; }
        public Action OnClick { get; }
        public bool Danger { get; }
        public bool Enabled { get; }
        public string Group { get; }

        public FlyoutItem(string text, Action onClick = null, bool danger = false, bool enabled = true, string group = null)
        {
            Text = text;
            OnClick = onClick;
            Danger = danger;
            Enabled = enabled;
            Group = group;
        }
    }

    /// <summary>
    /// 与展开页同主题的免激活菜单，在指针或触发按钮旁弹出。
    /// 自带场景（桌面层窗口不抢焦点）下比 WPF ContextMenu 更可靠。
    /// 返回 Popup，调用方用 ModuleBase.TrackFlyout 登记，避免面板被自动收起。
    /// </summary>
    public static class FlyoutMenu
    {
        public static Popup Show(IReadOnlyList<FlyoutItem> items, string caption = null, FrameworkElement anchor = null)
            => ShowCore(items, caption, anchor, keyboard: false);

        internal static Popup ShowKeyboard(IReadOnlyList<FlyoutItem> items, string caption, FrameworkElement anchor)
            => ShowCore(items, caption, anchor, keyboard: true);

        private sealed class MenuPopup : Popup
        {
            internal Func<KeyEventArgs, bool> Navigate { get; set; }
        }

        internal static bool HandleKey(Popup popup, KeyEventArgs args)
        {
            if (args.Handled || popup is not MenuPopup { IsOpen: true } menu) return false;
            if (args.Key == Key.Escape) menu.IsOpen = false;
            else if (menu.Navigate?.Invoke(args) != true) return false;
            args.Handled = true;
            return true;
        }

        private static Popup ShowCore(IReadOnlyList<FlyoutItem> items, string caption, FrameworkElement anchor, bool keyboard)
        {
            if (items == null || items.Count == 0) return null;
            double s = Ui.Scale;
            bool animate = SystemParameters.ClientAreaAnimation && SystemParameters.MenuAnimation;

            var sp = new StackPanel();
            var choices = keyboard ? new List<Button>() : null;
            TextElement.SetFontFamily(sp, Ui.TextFont);
            void AddCaption(string value)
            {
                var captionText = new TextBlock
                {
                    Text = value,
                    FontSize = 12 * s,
                    FontWeight = FontWeights.SemiBold,
                    Opacity = 0.78,
                    MaxWidth = 250 * s,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ToolTip = value,
                    Margin = new Thickness(8 * s, (sp.Children.Count == 0 ? 2 : 10) * s, 8 * s, 2 * s)
                };
                captionText.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
                sp.Children.Add(captionText);
            }
            if (!string.IsNullOrEmpty(caption)) AddCaption(caption);

            MenuPopup popup = null;
            string group = null;
            foreach (var item in items)
            {
                if (item.Group != null && item.Group != group) AddCaption(item.Group);
                group = item.Group;
                var button = PanelControls.GlassButton(item.Text, () =>
                {
                    popup.IsOpen = false;
                    item.OnClick?.Invoke();
                }, compact: item.Group != null);
                if (item.Group != null)
                {
                    button.HorizontalContentAlignment = HorizontalAlignment.Left;
                    button.Margin = new Thickness(0, 3 * s, 0, 0);
                }
                button.MinWidth = 140 * s;
                button.IsEnabled = item.Enabled;
                var text = new TextBlock
                {
                    Text = item.Text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 250 * s
                };
                if (item.Danger) text.FontWeight = FontWeights.Bold;
                text.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
                button.Content = text;
                button.ToolTip = item.Text;
                System.Windows.Automation.AutomationProperties.SetName(button, item.Text);
                sp.Children.Add(button);
                if (item.Enabled) choices?.Add(button);
            }

            var border = new Border
            {
                CornerRadius = new CornerRadius(22 * s),
                BorderThickness = new Thickness(1 * s),
                Padding = new Thickness(6 * s),
                Child = new ScrollViewer
                {
                    Content = sp,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = SystemParameters.WorkArea.Height * 0.65
                }
            };
            PopupPalette.Apply(border);
            var entranceScale = new ScaleTransform();
            if (animate)
            {
                border.RenderTransformOrigin = new Point(0.20, 0.12);
                border.RenderTransform = entranceScale;
            }
            popup = new MenuPopup
            {
                Child = border,
                AllowsTransparency = true,
                PopupAnimation = animate ? PopupAnimation.Fade : PopupAnimation.None,
                Placement = anchor == null ? PlacementMode.MousePoint : PlacementMode.Bottom,
                PlacementTarget = anchor,
                VerticalOffset = anchor == null ? 0 : 4 * s,
                StaysOpen = false,
                Focusable = false
            };
            if (keyboard && anchor != null) BindKeyboard(popup, anchor, choices);
            // 鼠标离开菜单超时后自动关闭（点击/移回菜单会取消），避免菜单一直悬着。
            var closeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(900, (App.Settings?.HoverCloseDelayMs ?? 250) + 650))
            };
            closeTimer.Tick += (s2, e2) =>
            {
                if (!border.IsMouseOver && PointerOver(anchor)) return;
                closeTimer.Stop();
                if (!border.IsMouseOver) popup.IsOpen = false;
            };
            border.MouseEnter += (s2, e2) => closeTimer.Stop();
            border.MouseLeave += (s2, e2) => { if (!keyboard) closeTimer.Start(); };
            popup.Opened += (s2, e2) =>
            {
                if (!keyboard && !border.IsMouseOver) closeTimer.Start();
                if (animate)
                {
                    var grow = new DoubleAnimation(0.965, 1, TimeSpan.FromMilliseconds(125))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                        FillBehavior = FillBehavior.Stop
                    };
                    entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                    entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                }
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

        private static void BindKeyboard(MenuPopup popup, FrameworkElement anchor, List<Button> choices)
        {
            int selected = -1;
            void Select(int index)
            {
                if (selected >= 0) choices[selected].ClearValue(Control.BackgroundProperty);
                selected = index;
                choices[index].SetResourceReference(Control.BackgroundProperty, "ThemePanelSelected");
                choices[index].BringIntoView();
            }

            popup.Opened += (_, _) => { if (choices.Count > 0) Select(0); };
            popup.Navigate = args =>
            {
                if (choices.Count == 0) return false;
                var modifiers = Keyboard.Modifiers;
                if (modifiers != ModifierKeys.None && !(args.Key == Key.Tab && modifiers == ModifierKeys.Shift)) return false;
                switch (args.Key)
                {
                    case Key.Up: Select((selected + choices.Count - 1) % choices.Count); return true;
                    case Key.Down: Select((selected + 1) % choices.Count); return true;
                    case Key.Tab:
                        Select((selected + choices.Count + (modifiers == ModifierKeys.Shift ? -1 : 1)) % choices.Count);
                        return true;
                    case Key.Home: Select(0); return true;
                    case Key.End: Select(choices.Count - 1); return true;
                    case Key.Enter:
                    case Key.Space:
                        if (selected >= 0 && !args.IsRepeat)
                            choices[selected].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        return true;
                    default: return false;
                }
            };

            void CloseOnFocusLost(object sender, DependencyPropertyChangedEventArgs args)
            {
                if (!(bool)args.NewValue) popup.IsOpen = false;
            }
            anchor.IsKeyboardFocusWithinChanged += CloseOnFocusLost;
            popup.Closed += (_, _) =>
            {
                anchor.IsKeyboardFocusWithinChanged -= CloseOnFocusLost;
                popup.Navigate = null;
            };
        }

        private static bool PointerOver(FrameworkElement element)
        {
            if (element == null || !element.IsVisible || PresentationSource.FromVisual(element) == null
                || !NativeMethods.TryGetCursorPosition(out int x, out int y)) return false;
            return new Rect(element.RenderSize).Contains(element.PointFromScreen(new Point(x, y)));
        }
    }
}
