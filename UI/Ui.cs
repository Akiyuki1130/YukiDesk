using System;
using System.IO;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace DesktopBar.UI
{
    public static class Ui
    {
        public static double Scale => Math.Max(0.75, Math.Min(1.5, App.Settings?.Scale ?? 1.0));

        private const string FontDirectory = "Assets/Fonts";
        private const string FontFamilyName = "Resource Han Rounded SC";
        public static FontFamily TextFont { get; } = new FontFamily(
            new Uri(AppContext.BaseDirectory, UriKind.Absolute), $"./{FontDirectory}/#{FontFamilyName}");

        internal static Viewbox SnowflakeGlyph(double size, string inkKey)
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            const double center = 12, radius = 10.5, halfTipWidth = 2.2;
            for (int arm = 0; arm < 6; arm++)
            {
                double angle = -Math.PI / 2 + arm * Math.PI / 3;
                double x = Math.Cos(angle), y = Math.Sin(angle);
                var line = new Line
                {
                    X1 = center, Y1 = center,
                    X2 = center + x * radius * 0.60, Y2 = center + y * radius * 0.60,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                line.SetResourceReference(Shape.StrokeProperty, inkKey);
                canvas.Children.Add(line);

                double baseX = center + x * radius * 0.52, baseY = center + y * radius * 0.52;
                var tip = new Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(center + x * radius, center + y * radius),
                        new Point(baseX - y * halfTipWidth, baseY + x * halfTipWidth),
                        new Point(baseX + y * halfTipWidth, baseY - x * halfTipWidth)
                    }
                };
                tip.SetResourceReference(Shape.FillProperty, inkKey);
                canvas.Children.Add(tip);
            }
            return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
        }

        internal static void ApplyTrayFont(System.Windows.Forms.ContextMenuStrip menu)
        {
            var fonts = new PrivateFontCollection();
            try
            {
                // WinForms 不能沿用 WPF 的字体资源路径，单独加载进程内字体。
                fonts.AddFontFile(Path.Combine(AppContext.BaseDirectory, FontDirectory, "ResourceHanRoundedSC-Regular.ttf"));
                var families = fonts.Families;
                var font = new System.Drawing.Font(families[0], menu.Font.SizeInPoints);
                menu.Font = font;
                menu.Disposed += (s, e) =>
                {
                    font.Dispose();
                    foreach (var family in families) family.Dispose();
                    fonts.Dispose();
                };
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or ExternalException)
            {
                fonts.Dispose();
                App.Log("托盘字体加载失败: " + ex.Message);
            }
        }

        internal static Grid SliderWithValue(Slider slider, TextBlock value, double valueWidth, bool panel = false)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            slider.Margin = new Thickness(0, 0, 10, 0);
            value.HorizontalAlignment = HorizontalAlignment.Center;
            value.VerticalAlignment = VerticalAlignment.Center;
            var badge = new Border
            {
                Child = value, MinWidth = valueWidth, MinHeight = 28, CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1), Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.SetResourceReference(Border.BackgroundProperty, panel ? "ThemePanelSubtle" : "SettingsControlBackground");
            badge.SetResourceReference(Border.BorderBrushProperty, panel ? "ThemePanelControl" : "SettingsDivider");
            Grid.SetColumn(badge, 1);
            row.Children.Add(slider);
            row.Children.Add(badge);
            return row;
        }

        public static Color ParseColor(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex?.Trim()); }
            catch { return Colors.White; }
        }

        public static SolidColorBrush Brush(string hex, double opacity = 1)
        {
            var c = ParseColor(hex);
            byte a = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
            var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
    }
}
