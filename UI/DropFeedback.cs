using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopBar.UI
{
    internal sealed class DropFeedback : Adorner
    {
        private AdornerLayer _layer;
        private DispatcherTimer _expiry;
        private string _message;
        private static readonly Brush Fill = Ui.Brush("#E8463025");
        private static readonly Brush Accent = Ui.Brush("#EBC2A084");

        internal DropFeedback(FrameworkElement target) : base(target)
        {
            IsHitTestVisible = false;
        }

        internal void Highlight(uint effect) => Show(Label(effect) is string label ? label + "到这里" : "此处不可放置");

        internal void Finish(uint effect, bool failed)
        {
            Show(failed ? "拖放失败" : Label(effect) is string label ? label + "已提交" : "拖放未完成");
            _expiry ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1250), DispatcherPriority.Background,
                (_, _) => Clear(), Dispatcher);
            _expiry.Stop();
            _expiry.Start();
        }

        internal void Clear()
        {
            _expiry?.Stop();
            _layer?.Remove(this);
            _layer = null;
            _message = null;
        }

        private void Show(string message)
        {
            if (_message == message) return;
            _expiry?.Stop();
            _message = message;
            if (_layer == null)
            {
                _layer = AdornerLayer.GetAdornerLayer(AdornedElement);
                _layer?.Add(this);
                if (_layer != null && SystemParameters.ClientAreaAnimation)
                    BeginAnimation(OpacityProperty, new DoubleAnimation(.65, 1, TimeSpan.FromMilliseconds(95)) { FillBehavior = FillBehavior.Stop });
            }
            InvalidateVisual();
        }

        private static string Label(uint effect) => (effect & 7) switch
        {
            1 => "复制", 2 => "移动", 4 => "创建快捷方式", _ => null
        };

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (string.IsNullOrEmpty(_message) || RenderSize.Width < 2 || RenderSize.Height < 2) return;
            double radius = Math.Min(RenderSize.Height / 2, 18 * Ui.Scale);
            drawingContext.DrawRoundedRectangle(null, new Pen(Accent, 2), new Rect(.8, .8, RenderSize.Width - 1.6,
                RenderSize.Height - 1.6), radius, radius);
            var label = new FormattedText(_message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Ui.TextFont, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
                11.5 * Ui.Scale, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, RenderSize.Width - 12),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            double width = Math.Min(RenderSize.Width, label.Width + 12);
            double height = Math.Min(RenderSize.Height, label.Height + 5);
            double x = (RenderSize.Width - width) / 2;
            double y = Math.Max(0, RenderSize.Height - height - 3);
            drawingContext.DrawRoundedRectangle(Fill, null, new Rect(x, y, width, height), height / 2, height / 2);
            drawingContext.DrawText(label, new Point(x + Math.Min(6, width / 2), y + 2));
        }
    }
}
