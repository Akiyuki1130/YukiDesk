using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopBar.UI
{
    internal static class PanelControls
    {
        internal static Button Button(string title, Action click)
        {
            var button = new Button { Content = title, Margin = new Thickness(3, 1, 0, 2),
                Padding = new Thickness(9, 3, 9, 3), FontSize = 12 * Ui.Scale };
            button.SetResourceReference(FrameworkElement.StyleProperty, "Btn");
            button.Click += (_, _) => click();
            ReleasePointerFocus(button);
            return button;
        }

        internal static void ReleasePointerFocus(Control control)
        {
            control.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, args) =>
            {
                if (args.ChangedButton != MouseButton.Left || !control.IsKeyboardFocusWithin) return;
                FocusManager.SetFocusedElement(FocusManager.GetFocusScope(control), null);
                Keyboard.ClearFocus();
            }), handledEventsToo: true);
        }

        internal static Button GlassButton(string title, Action click, bool compact = false)
        {
            double scale = Ui.Scale;
            var button = new Button
            {
                Content = title,
                MinHeight = (compact ? 26 : 36) * scale,
                MinWidth = compact ? 44 * scale : 0,
                Padding = compact ? new Thickness(9 * scale, 2 * scale, 9 * scale, 2 * scale)
                    : new Thickness(14 * scale, 6 * scale, 14 * scale, 6 * scale),
                FontSize = (compact ? 12 : 13.5) * scale,
                Margin = compact ? new Thickness(4 * scale, 0, 0, 0) : new Thickness(0, 4 * scale, 0, 0),
                Focusable = false,
                HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "BtnGlass");
            button.Click += (_, _) => click();
            return button;
        }

        internal static TextBlock Text(string value)
        {
            var text = new TextBlock { Text = value, FontSize = 12 * Ui.Scale };
            text.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            return text;
        }

        internal static ListBox VirtualizedFileList<T>(double maxHeight) where T : FrameworkElement
        {
            var itemContainer = new Style(typeof(ListBoxItem));
            itemContainer.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            itemContainer.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem))
            {
                VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
            }));
            var list = new ListBox
            {
                MaxHeight = maxHeight,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
                ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(T)) },
                ItemContainerStyle = itemContainer
            };
            ScrollViewer.SetCanContentScroll(list, true);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            return list;
        }
    }
}
