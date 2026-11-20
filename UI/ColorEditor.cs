using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    /// <summary>可交互的 HSV 调色盘，支持二维饱和度/明度选择与色相选择。</summary>
    public class ColorEditor : StackPanel
    {
        private readonly SpectrumSurface _spectrum;
        private readonly HueSurface _hue;
        private readonly Ellipse _spectrumMarker;
        private readonly Border _hueMarker;
        private readonly TextBox _box;
        private readonly Border _preview;
        private readonly StackPanel _palette;
        private readonly Button _togglePalette;
        private DispatcherTimer _paletteTimer;
        private int _paletteAnimationVersion;
        private double _hueValue;
        private double _saturation;
        private double _brightness;
        private byte _alpha = 255;
        private string _lastValidHex = "#FFFFFF";
        private bool _dragSpectrum;
        private bool _dragHue;
        private bool _dragging => _dragSpectrum || _dragHue;
        private bool _updating;
        private bool _ready;

        public event Action<string> Changed;

        public ColorEditor(string hex)
        {
            Orientation = Orientation.Vertical;
            MinWidth = 214;

            _spectrum = new SpectrumSurface { Width = 240, Height = 142, Cursor = Cursors.Cross };
            _spectrumMarker = new Ellipse
            {
                Width = 16,
                Height = 16,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };
            _spectrum.Children.Add(_spectrumMarker);
            _spectrum.MouseLeftButtonDown += SpectrumDown;
            _spectrum.MouseMove += SpectrumMove;
            _spectrum.MouseLeftButtonUp += SpectrumUp;
            _spectrum.LostMouseCapture += (s, e) => EndDrag();
            _spectrum.MouseLeave += SpectrumMove;

            _hue = new HueSurface
            {
                Width = 240,
                Height = 18,
                Margin = new Thickness(0, 8, 0, 0),
                Cursor = Cursors.Hand
            };
            _hueMarker = new Border
            {
                Width = 4,
                Height = 22,
                Background = Brushes.White,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };
            _hue.Children.Add(_hueMarker);
            _hue.MouseLeftButtonDown += HueDown;
            _hue.MouseMove += HueMove;
            _hue.MouseLeftButtonUp += HueUp;
            _hue.LostMouseCapture += (s, e) => EndDrag();

            _palette = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Opacity = 0,
                Height = 0,
                ClipToBounds = true,
                Margin = new Thickness(0, 8, 0, 0)
            };
            _palette.Children.Add(_spectrum);
            _palette.Children.Add(_hue);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 0)
            };
            _preview = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            _preview.SetResourceReference(Border.BorderBrushProperty, "SettingsControlBorder");
            _box = new TextBox
            {
                Width = 108,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)Application.Current.FindResource("DarkBox"),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = Ui.TextFont,
                FontSize = 13.5
            };
            System.Windows.Automation.AutomationProperties.SetName(_box, "十六进制颜色");
            _box.TextChanged += (s, e) => { if (!_updating) ApplyHex(_box.Text, commit: false); };
            _box.LostKeyboardFocus += (s, e) => CommitHex();
            _box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; }
            };
            row.Children.Add(_preview);
            row.Children.Add(_box);
            _togglePalette = new Button
            {
                Content = "色盘", MinWidth = 60,
                Style = (Style)Application.Current.FindResource("Btn"),
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(11, 7, 11, 7),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "展开或收起 HSV 色盘"
            };
            _togglePalette.Click += (s, e) => TogglePalette();
            row.Children.Add(_togglePalette);
            Children.Add(row);
            Children.Add(_palette);

            _spectrum.Background = Ui.Brush("#FFFFFF", 0.06);
            _hue.Background = Ui.Brush("#FFFFFF", 0.06);
            _ready = true;
            ApplyHex(hex, commit: false);
            CommitHex();
        }

        private void TogglePalette()
        {
            bool expanding = _palette.Visibility != Visibility.Visible;
            var duration = TimeSpan.FromMilliseconds(170);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            int version = ++_paletteAnimationVersion;
            _paletteTimer?.Stop();
            _palette.BeginAnimation(HeightProperty, null);
            _palette.BeginAnimation(OpacityProperty, null);

            if (expanding)
            {
                _palette.Visibility = Visibility.Visible;
                _palette.Height = double.NaN;
                _palette.Opacity = 0;
                _palette.Measure(new Size(260, double.PositiveInfinity));
                double target = Math.Max(1, _palette.DesiredSize.Height);
                _palette.Height = 0;
                _palette.BeginAnimation(HeightProperty, new DoubleAnimation(0, target, duration) { EasingFunction = ease });
                _palette.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
                _paletteTimer = new DispatcherTimer { Interval = duration };
                _paletteTimer.Tick += (s, e) =>
                {
                    _paletteTimer.Stop();
                    if (version != _paletteAnimationVersion) return;
                    _palette.BeginAnimation(HeightProperty, null);
                    _palette.BeginAnimation(OpacityProperty, null);
                    _palette.Height = double.NaN;
                    _palette.Opacity = 1;
                };
                _paletteTimer.Start();
                _togglePalette.Content = "收起";
            }
            else
            {
                _palette.Measure(new Size(260, double.PositiveInfinity));
                double current = Math.Max(1, _palette.ActualHeight > 0 ? _palette.ActualHeight : _palette.DesiredSize.Height);
                _palette.Height = current;
                _palette.BeginAnimation(HeightProperty, new DoubleAnimation(current, 0, duration) { EasingFunction = ease });
                _palette.BeginAnimation(OpacityProperty, new DoubleAnimation(_palette.Opacity, 0, duration) { EasingFunction = ease });
                _paletteTimer = new DispatcherTimer { Interval = duration };
                _paletteTimer.Tick += (s, e) =>
                {
                    _paletteTimer.Stop();
                    if (version != _paletteAnimationVersion) return;
                    _palette.BeginAnimation(HeightProperty, null);
                    _palette.BeginAnimation(OpacityProperty, null);
                    _palette.Height = 0;
                    _palette.Opacity = 0;
                    _palette.Visibility = Visibility.Collapsed;
                };
                _paletteTimer.Start();
                _togglePalette.Content = "色盘";
            }
        }

        private void ApplyHex(string hex, bool commit)
        {
            var color = TryParse(hex);
            if (color == null) return;
            _alpha = color.Value.A;
            _lastValidHex = Normalize(color.Value);
            ColorToHsv(color.Value, out _hueValue, out _saturation, out _brightness);
            UpdateVisuals(color.Value);
            if (commit && _ready) Changed?.Invoke(_lastValidHex);
        }

        private void CommitHex()
        {
            var color = TryParse(_box.Text);
            if (color == null)
            {
                _updating = true;
                _box.Text = _lastValidHex;
                _updating = false;
                return;
            }
            ApplyHex(_box.Text, commit: true);
        }

        private void UpdateVisuals(Color color)
        {
            _updating = true;
            _spectrum.Hue = _hueValue;
            _spectrum.InvalidateVisual();
            _hue.InvalidateVisual();
            _preview.Background = new SolidColorBrush(color);
            _box.Text = Normalize(color);
            double spectrumActualWidth = _spectrum.ActualWidth > 1 ? _spectrum.ActualWidth : _spectrum.Width;
            double spectrumActualHeight = _spectrum.ActualHeight > 1 ? _spectrum.ActualHeight : _spectrum.Height;
            double spectrumWidth = Math.Max(1, spectrumActualWidth - _spectrumMarker.Width);
            double spectrumHeight = Math.Max(1, spectrumActualHeight - _spectrumMarker.Height);
            Canvas.SetLeft(_spectrumMarker, Math.Clamp(_saturation, 0, 1) * spectrumWidth);
            Canvas.SetTop(_spectrumMarker, (1 - Math.Clamp(_brightness, 0, 1)) * spectrumHeight);
            double hueActualWidth = _hue.ActualWidth > 1 ? _hue.ActualWidth : _hue.Width;
            double hueWidth = Math.Max(1, hueActualWidth - _hueMarker.Width);
            Canvas.SetLeft(_hueMarker, Math.Clamp(_hueValue / 360.0, 0, 1) * hueWidth);
            Canvas.SetTop(_hueMarker, -2);
            _updating = false;
        }

        private void SpectrumDown(object sender, MouseButtonEventArgs e)
        {
            _dragSpectrum = true;
            _spectrum.CaptureMouse();
            SetSpectrum(e.GetPosition(_spectrum));
        }

        private void SpectrumMove(object sender, MouseEventArgs e)
        {
            if (_dragSpectrum && e.LeftButton == MouseButtonState.Pressed)
                SetSpectrum(e.GetPosition(_spectrum));
        }

        private void SpectrumUp(object sender, MouseButtonEventArgs e)
        {
            EndDrag();
            _spectrum.ReleaseMouseCapture();
        }

        /// <summary>拖动结束:提交一次最终颜色(拖动期间 Changed 被抑制)。</summary>
        private void EndDrag()
        {
            bool hadDrag = _dragSpectrum || _dragHue;
            _dragSpectrum = false;
            _dragHue = false;
            if (hadDrag) CommitIfReady();
        }

        private void CommitIfReady()
        {
            if (_ready && !_dragging)
            {
                var color = HsvToColor(_hueValue, _saturation, _brightness, _alpha);
                Changed?.Invoke(Normalize(color));
            }
        }

        private void SetSpectrum(Point p)
        {
            double actualWidth = _spectrum.ActualWidth > 1 ? _spectrum.ActualWidth : _spectrum.Width;
            double actualHeight = _spectrum.ActualHeight > 1 ? _spectrum.ActualHeight : _spectrum.Height;
            double width = Math.Max(1, actualWidth - _spectrumMarker.Width);
            double height = Math.Max(1, actualHeight - _spectrumMarker.Height);
            _saturation = Math.Clamp((p.X - _spectrumMarker.Width / 2) / width, 0, 1);
            _brightness = 1 - Math.Clamp((p.Y - _spectrumMarker.Height / 2) / height, 0, 1);
            ApplyHsv();
        }

        private void HueDown(object sender, MouseButtonEventArgs e)
        {
            _dragHue = true;
            _hue.CaptureMouse();
            SetHue(e.GetPosition(_hue));
        }

        private void HueMove(object sender, MouseEventArgs e)
        {
            if (_dragHue && e.LeftButton == MouseButtonState.Pressed)
                SetHue(e.GetPosition(_hue));
        }

        private void HueUp(object sender, MouseButtonEventArgs e)
        {
            EndDrag();
            _hue.ReleaseMouseCapture();
        }

        private void SetHue(Point p)
        {
            double actualWidth = _hue.ActualWidth > 1 ? _hue.ActualWidth : _hue.Width;
            double width = Math.Max(1, actualWidth - _hueMarker.Width);
            _hueValue = Math.Clamp((p.X - _hueMarker.Width / 2) / width, 0, 1) * 360;
            ApplyHsv();
        }

        private void ApplyHsv()
        {
            var color = HsvToColor(_hueValue, _saturation, _brightness, _alpha);
            UpdateVisuals(color);
            // 拖动中不提交,只更新视觉;与滑块一致,松手/抬笔后才触发 Changed。
            // 拖动时实时回调会触发顶栏重建,表现为色盘卡顿拖不动。
            if (!_dragging) Changed?.Invoke(Normalize(color));
        }

        private static Color? TryParse(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex?.Trim()); }
            catch { return null; }
        }

        private static string Normalize(Color v) =>
            v.A == 255 ? $"#{v.R:X2}{v.G:X2}{v.B:X2}" : $"#{v.A:X2}{v.R:X2}{v.G:X2}{v.B:X2}";

        private static void ColorToHsv(Color color, out double h, out double s, out double v)
        {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            h = 0;
            if (d > 0.00001)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * ((b - r) / d + 2);
                else h = 60 * ((r - g) / d + 4);
                if (h < 0) h += 360;
            }
            s = max <= 0 ? 0 : d / max;
            v = max;
        }

        private static Color HsvToColor(double h, double s, double v, byte alpha = 255)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60 % 2) - 1));
            double m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromArgb(alpha, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        private sealed class SpectrumSurface : Canvas
        {
            public double Hue { get; set; }
            protected override void OnRender(DrawingContext dc)
            {
                base.OnRender(dc);
                var baseColor = HsvToColor(Hue, 1, 1);
                dc.DrawRectangle(new SolidColorBrush(baseColor), null, new Rect(0, 0, ActualWidth, ActualHeight));
                dc.DrawRectangle(new LinearGradientBrush(Colors.White, Colors.Transparent, 0), null, new Rect(0, 0, ActualWidth, ActualHeight));
                dc.DrawRectangle(new LinearGradientBrush(Colors.Transparent, Colors.Black, 90), null, new Rect(0, 0, ActualWidth, ActualHeight));
            }
        }

        private sealed class HueSurface : Canvas
        {
            protected override void OnRender(DrawingContext dc)
            {
                base.OnRender(dc);
                var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                for (int i = 0; i <= 6; i++) brush.GradientStops.Add(new GradientStop(HsvToColor(i * 60, 1, 1), i / 6.0));
                dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
            }
        }
    }
}
