using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace DesktopBar.UI
{
    internal sealed class AnimatedPanelSection : Border
    {
        private readonly Border _content = new() { Opacity = 0, VerticalAlignment = VerticalAlignment.Top };
        private int _animation;

        internal AnimatedPanelSection()
        {
            Visibility = Visibility.Collapsed;
            ClipToBounds = true;
            Child = _content;
        }

        internal UIElement Content
        {
            get => _content.Child;
            set
            {
                _content.Child = value;
                if (Expanded && IsVisible) SetExpanded(true);
            }
        }

        internal bool Expanded { get; private set; }

        internal void SetExpanded(bool expanded, bool animate = true, Action collapsed = null)
        {
            Expanded = expanded;
            int animation = ++_animation;
            double height = Visibility == Visibility.Collapsed ? 0 : double.IsNaN(Height) ? ActualHeight : Height;
            double opacity = _content.Opacity;
            BeginAnimation(HeightProperty, null);
            _content.BeginAnimation(OpacityProperty, null);
            _content.Opacity = expanded ? 1 : 0;
            IsHitTestVisible = expanded;

            void Finish()
            {
                Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                Height = _content.Height = double.NaN;
                if (!expanded) collapsed?.Invoke();
            }

            if (!animate || !SystemParameters.ClientAreaAnimation || Parent is not FrameworkElement { IsVisible: true } parent)
            {
                Finish();
                return;
            }

            // Reveal the already measured content inside the flyout's fixed native window.
            _content.Height = double.NaN;
            _content.Measure(new Size(parent.ActualWidth, double.PositiveInfinity));
            _content.Height = _content.DesiredSize.Height;
            double target = expanded ? _content.Height : 0;
            Height = target;
            var duration = TimeSpan.FromMilliseconds(expanded ? 305 : 205);
            DoubleAnimation Motion(double from, double to) => new(from, to, duration)
            {
                EasingFunction = new PanelEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            };
            var resize = Motion(height, target);
            resize.Completed += (_, _) =>
            {
                if (animation != _animation) return;
                Finish();
                BeginAnimation(HeightProperty, null);
                _content.BeginAnimation(OpacityProperty, null);
            };
            _content.BeginAnimation(OpacityProperty, Motion(opacity, _content.Opacity));
            BeginAnimation(HeightProperty, resize);
            Visibility = Visibility.Visible;
        }
    }
}
