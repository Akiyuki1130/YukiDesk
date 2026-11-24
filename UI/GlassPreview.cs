using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using DesktopBar.Models;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    /// <summary>在固定明暗场景中显示实际材质效果，预览不依赖屏幕捕获。</summary>
    internal sealed class GlassPreview : UniformGrid
    {
        private readonly Sample _light = new Sample(dark: false);
        private readonly Sample _dark = new Sample(dark: true);

        public GlassPreview()
        {
            Columns = 2;
            Margin = new Thickness(0, 12, 0, 2);
            Children.Add(_light.Element);
            Children.Add(_dark.Element);
        }

        public void Refresh(AppSettings settings, double? frostPreview = null)
        {
            _light.Refresh(settings, frostPreview);
            _dark.Refresh(settings, frostPreview);
        }

        private sealed class Sample
        {
            private const int Width = 240;
            private const int Height = 84;
            private const int PillWidth = 118;
            private const int PillHeight = 38;
            private static readonly Lazy<BitmapSource> LightBackdrop = new(() => DrawBackdrop(false));
            private static readonly Lazy<BitmapSource> DarkBackdrop = new(() => DrawBackdrop(true));
            private readonly BitmapSource _backdrop;
            private readonly bool _dark;
            private readonly ImageBrush _blurred = new ImageBrush { Stretch = Stretch.Fill };
            private LiquidGlassEffect _effect;
            private bool _effectAttempted;
            private readonly Rectangle _glass;
            private readonly Rectangle _tint;
            private readonly Border _outline;
            private readonly TextBlock _text;
            private readonly byte[] _blurredPixels = new byte[Width * Height * 4];
            private double _previousRadius = -1;
            private AppSettings _settings;
            private GlassService.GlassLook _look;

            public FrameworkElement Element { get; }

            public Sample(bool dark)
            {
                _dark = dark;
                _backdrop = dark ? DarkBackdrop.Value : LightBackdrop.Value;
                double radius = PillHeight * 0.5;
                _glass = new Rectangle { Width = PillWidth, Height = PillHeight, RadiusX = radius, RadiusY = radius };
                _tint = new Rectangle
                {
                    Width = PillWidth, Height = PillHeight, RadiusX = radius, RadiusY = radius,
                    IsHitTestVisible = false
                };
                _text = new TextBlock
                {
                    Text = "12:48",
                    FontSize = 15,
                    FontWeight = FontWeights.Medium,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var canvas = new Grid { Background = new ImageBrush(_backdrop) { Stretch = Stretch.Fill } };
                canvas.Children.Add(_glass);
                canvas.Children.Add(_tint);
                _outline = new Border
                {
                    Width = PillWidth, Height = PillHeight, CornerRadius = new CornerRadius(radius),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
                canvas.Children.Add(_outline);
                canvas.Children.Add(_text);
                var tile = new Border
                {
                    Height = Height,
                    Child = canvas
                };
                tile.SizeChanged += (s, e) =>
                {
                    tile.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 5, 5);
                    UpdateViewport(e.NewSize.Width, e.NewSize.Height);
                };
                var label = new TextBlock { Text = dark ? "深色桌面" : "浅色桌面", FontSize = 12.5, Margin = new Thickness(1, 7, 0, 0) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "SettingsSubTextBrush");
                var stack = new StackPanel { Margin = dark ? new Thickness(5, 0, 0, 0) : new Thickness(0, 0, 5, 0) };
                stack.Children.Add(tile);
                stack.Children.Add(label);
                Element = stack;
                UpdateViewport(Width, Height);
            }

            public void Refresh(AppSettings settings, double? frostPreview)
            {
                _settings = settings;
                _outline.BorderBrush = settings.BorderEnabled ? Ui.Brush(ThemeService.Border, settings.BorderOpacity) : Brushes.Transparent;
                _outline.BorderThickness = new Thickness(settings.BorderEnabled ? settings.BorderThickness : 0);
                _text.Foreground = Ui.Brush(settings.ThemeTextColor);
                if (settings.PillMaterial == 0)
                {
                    var (background, text) = ThemeService.FollowsBackground(settings)
                        ? ThemeService.WallpaperTheme(!_dark) : (settings.ThemeColor, settings.ThemeTextColor);
                    _glass.Effect = null;
                    _glass.Fill = Ui.Brush(background, settings.ThemeOpacity);
                    _text.Foreground = Ui.Brush(text);
                    _glass.Opacity = 1;
                    _tint.Fill = null;
                    return;
                }

                var look = GlassService.CurrentLook(settings, frostPreview);
                if (look.Blur != _previousRadius)
                {
                    var bitmap = GlassService.BlurPreview(_backdrop, look.Blur);
                    bitmap.CopyPixels(_blurredPixels, Width * 4, 0);
                    _blurred.ImageSource = bitmap;
                    _previousRadius = look.Blur;
                }
                if (settings.PillMaterial == 4 && !_effectAttempted)
                {
                    _effectAttempted = true;
                    _effect = LiquidGlassEffect.CreatePanel(PillHeight * 0.5, PillWidth, PillHeight);
                    if (_effect != null)
                    {
                        _effect.Input = _blurred;
                        _effect.Sharp = new ImageBrush(_backdrop);
                        SetSharpViewport();
                        _effect.EdgeWidth = PillHeight * 0.275;
                    }
                }

                _glass.Fill = _blurred;
                _glass.Opacity = settings.GlassOpacity;
                _glass.Effect = settings.PillMaterial == 4 ? _effect : null;
                if (_glass.Effect == null)
                {
                    var tint = new SolidColorBrush(look.Tint) { Opacity = look.TintOpacity };
                    tint.Freeze();
                    _tint.Fill = tint;
                }
                else
                {
                    _tint.Fill = null;
                    GlassService.SetOptics(_effect, look);
                }
                _look = _glass.Effect == null ? look with { Lift = 0 } : look;
                RefreshAdaptiveInk();
            }

            private void UpdateViewport(double width, double height)
            {
                if (width < PillWidth || height < PillHeight) return;
                _blurred.Viewbox = new Rect((width - PillWidth) / (2 * width), (height - PillHeight) / (2 * height),
                    PillWidth / width, PillHeight / height);
                SetSharpViewport();
                RefreshAdaptiveInk();
            }

            private void SetSharpViewport()
            {
                var viewport = _blurred.Viewbox;
                if (_effect != null)
                    _effect.SharpUv = new Point4D(viewport.Width, viewport.Height, viewport.X, viewport.Y);
            }

            private void RefreshAdaptiveInk()
            {
                if (GlassService.UsesAdaptiveInk(_settings))
                {
                    double luminance = GlassService.SurfaceLightness(_blurredPixels, Width, Height,
                        _blurred.Viewbox, _look, _settings.GlassOpacity);
                    _text.Foreground = ModuleBase.PreferredGlassInk(luminance);
                }
            }

            private static BitmapSource DrawBackdrop(bool dark)
            {
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Ui.Brush(dark ? "#2C2B29" : "#E9E6E0"), null, new Rect(0, 0, Width, Height));
                    dc.DrawGeometry(Ui.Brush(dark ? "#403D39" : "#D6D0C7"), null,
                        Geometry.Parse("M 120,0 H 183 L 142,84 H 79 Z"));
                    var edge = new Pen(Ui.Brush(dark ? "#787069" : "#ACA094", 0.65), 1);
                    dc.DrawLine(edge, new Point(120, 0), new Point(79, Height));
                    dc.DrawLine(edge, new Point(183, 0), new Point(142, Height));
                }
                var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                bitmap.Freeze();
                return bitmap;
            }
        }
    }
}
