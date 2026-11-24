using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    internal sealed class PerfTrend : FrameworkElement
    {
        private (double X, double Y)[] _points = Array.Empty<(double, double)>();
        private double _max = 100;
        private StreamGeometry _line;

        internal PerfTrend() { Height = 40 * Ui.Scale; Margin = new Thickness(2, 4, 2, 1); }

        internal double SetSamples(Func<PerfSample, double> value, bool percent)
        {
            long cutoff = Environment.TickCount64 - 60000;
            _points = PerfService.Instance.RecentSamples()
                .Select(item => (Math.Clamp((item.Time - cutoff) / 60000.0, 0, 1), value(item.Sample)))
                .Where(point => double.IsFinite(point.Item2)).ToArray();
            double peak = _points.Length == 0 ? -1 : _points.Max(point => point.Y);
            _max = percent ? 100 : Math.Max(1024, peak);
            BuildLine(RenderSize);
            InvalidateVisual();
            return peak;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            BuildLine(sizeInfo.NewSize);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            var rule = TryFindResource("ThemePanelSubtle") as Brush ?? Brushes.DimGray;
            drawingContext.DrawRoundedRectangle(rule, null, new Rect(RenderSize), 5, 5);
            if (_line != null)
            {
                var ink = TryFindResource("SettingsAccent") as Brush ?? Brushes.Peru;
                drawingContext.DrawGeometry(null, new Pen(ink, 1.5), _line);
            }
        }

        private void BuildLine(Size size)
        {
            _line = null;
            if (_points.Length < 2 || size.Width <= 4 || size.Height <= 4) return;
            var shape = new StreamGeometry();
            using (var writer = shape.Open())
            {
                bool start = true;
                double lastX = -1;
                foreach (var (x, y) in _points)
                {
                    if (y < 0) { start = true; continue; }
                    var position = new Point(2 + x * (size.Width - 4), size.Height - 2 - Math.Clamp(y / _max, 0, 1) * (size.Height - 4));
                    if (start || x - lastX > 0.06) writer.BeginFigure(position, false, false);
                    else writer.LineTo(position, true, false);
                    start = false;
                    lastX = x;
                }
            }
            shape.Freeze();
            _line = shape;
        }
    }
}
