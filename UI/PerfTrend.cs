using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    internal sealed class PerfTrend : FrameworkElement
    {
        private static readonly DependencyProperty FillProperty = DependencyProperty.Register("TrendFill", typeof(Brush),
            typeof(PerfTrend), new PropertyMetadata(Brushes.DimGray, OnBrushChanged));
        private static readonly DependencyProperty InkProperty = DependencyProperty.Register("TrendInk", typeof(Brush),
            typeof(PerfTrend), new PropertyMetadata(Brushes.Peru, OnBrushChanged));
        private readonly DrawingVisual _graph = new();
        private (double X, double Y)[] _points = Array.Empty<(double, double)>();
        private double _max = 100;

        internal PerfTrend()
        {
            AddVisualChild(_graph);
            Height = 40 * Ui.Scale;
            Margin = new Thickness(2, 4, 2, 1);
            ToolTip = "最近 60 秒趋势";
            AutomationProperties.SetName(this, "最近 60 秒趋势");
            SetResourceReference(FillProperty, "ThemePanelSubtle");
            SetResourceReference(InkProperty, "SettingsAccent");
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index)
            => index == 0 ? _graph : throw new ArgumentOutOfRangeException(nameof(index));

        internal double SetSamples(Func<PerfSample, double> value, bool percent)
        {
            long cutoff = Environment.TickCount64 - 60000;
            var samples = PerfService.Instance.RecentSamples();
            if (_points.Length != samples.Length) _points = new (double, double)[samples.Length];
            double peak = -1;
            for (int index = 0; index < samples.Length; index++)
            {
                var (time, sample) = samples[index];
                double y = value(sample);
                _points[index] = (Math.Clamp((time - cutoff) / 60000.0, 0, 1), y);
                if (double.IsFinite(y)) peak = Math.Max(peak, y);
            }
            _max = percent ? 100 : Math.Max(1024, peak);
            DrawGraph();
            return peak;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            DrawGraph();
        }

        private static void OnBrushChanged(DependencyObject target, DependencyPropertyChangedEventArgs _)
            => ((PerfTrend)target).DrawGraph();

        private void DrawGraph()
        {
            Size size = RenderSize;
            using var drawing = _graph.RenderOpen();
            if (size.Width <= 4 || size.Height <= 4) return;
            drawing.DrawRoundedRectangle((Brush)GetValue(FillProperty), null, new Rect(size), 5, 5);
            if (_points.Length == 0) return;

            var ink = (Brush)GetValue(InkProperty);
            var shape = new StreamGeometry();
            Point? latest = null;
            using (var writer = shape.Open())
            {
                bool start = true;
                double lastX = -1;
                foreach (var (x, y) in _points)
                {
                    if (!double.IsFinite(y) || y < 0) { start = true; continue; }
                    var position = new Point(2 + x * (size.Width - 4), size.Height - 2 - Math.Clamp(y / _max, 0, 1) * (size.Height - 4));
                    if (start || x - lastX > 0.06) writer.BeginFigure(position, false, false);
                    else writer.LineTo(position, true, false);
                    start = false;
                    lastX = x;
                    latest = position;
                }
            }
            if (!latest.HasValue) return;
            shape.Freeze();
            drawing.DrawGeometry(null, new Pen(ink, 1.5), shape);
            drawing.DrawEllipse(ink, null, latest.Value, 2, 2);
        }
    }
}
