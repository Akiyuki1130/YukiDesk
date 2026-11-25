using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopBar.UI
{
    /// <summary>布局变化时栅格化胶囊并集。纹理 R/G 记录法线，B 记录带符号的边缘距离。</summary>
    internal sealed class PillGlassGeometry
    {
        private WriteableBitmap _bitmap;
        private ImageBrush _brush;
        private uint[] _pixels;
        private float[] _distances;
        private IReadOnlyList<Rect> _layout;
        private double _scaleX, _scaleY, _radius, _edgeWidth;

        internal ImageBrush Update(IReadOnlyList<Rect> layout, int width, int height,
            double scaleX, double scaleY, double radius, double edgeWidth)
        {
            bool newSize = _bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height;
            if (newSize)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                _brush = new ImageBrush(_bitmap) { Stretch = Stretch.Fill };
                _pixels = new uint[width * height];
                _distances = new float[_pixels.Length];
            }
            else if (ReferenceEquals(_layout, layout) && _scaleX == scaleX && _scaleY == scaleY
                && _radius == radius && _edgeWidth == edgeWidth)
            {
                return _brush;
            }

            Array.Fill(_pixels, 0xff8080ffu);
            Array.Fill(_distances, float.PositiveInfinity);
            if (layout != null)
            {
                foreach (var rect in layout)
                {
                    if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                        AddPill(rect, width, height, scaleX, scaleY, radius, edgeWidth);
                }
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _pixels, width * 4, 0);
            _layout = layout;
            _scaleX = scaleX;
            _scaleY = scaleY;
            _radius = radius;
            _edgeWidth = edgeWidth;
            return _brush;
        }

        private void AddPill(Rect rect, int width, int height, double sx, double sy, double radius, double edgeWidth)
        {
            double halfW = rect.Width * sx * 0.5;
            double halfH = rect.Height * sy * 0.5;
            double centerX = rect.X * sx + halfW;
            double centerY = rect.Y * sy + halfH;
            radius = Math.Min(radius, Math.Min(halfW, halfH));
            double straightX = halfW - radius;
            double straightY = halfH - radius;
            int left = Math.Max(0, (int)Math.Floor(rect.Left * sx - edgeWidth - 1));
            int top = Math.Max(0, (int)Math.Floor(rect.Top * sy - edgeWidth - 1));
            int right = Math.Min(width, (int)Math.Ceiling(rect.Right * sx + edgeWidth + 1));
            int bottom = Math.Min(height, (int)Math.Ceiling(rect.Bottom * sy + edgeWidth + 1));

            for (int y = top; y < bottom; y++)
            {
                double dy = y + 0.5 - centerY;
                double qy = Math.Abs(dy) - straightY;
                double outerY = Math.Max(qy, 0);
                int row = y * width;
                uint vertical = 0x800000u | EncodeSign(dy) << 8;
                for (int x = left; x < right; x++)
                {
                    double dx = x + 0.5 - centerX;
                    double qx = Math.Abs(dx) - straightX;
                    double outerX = Math.Max(qx, 0);
                    bool corner = outerX > 0 && outerY > 0;
                    // 直边的距离和法线只有一个方向。圆角才需要平方根与归一化。
                    double outside = corner ? Math.Sqrt(outerX * outerX + outerY * outerY) : outerX + outerY;
                    float distance = (float)(outside + Math.Min(Math.Max(qx, qy), 0) - radius);
                    int i = row + x;
                    if (distance >= _distances[i] || distance >= edgeWidth) continue;
                    _distances[i] = distance;

                    uint normal = corner
                        ? Encode(Math.Sign(dx) * outerX / outside) << 16 | Encode(Math.Sign(dy) * outerY / outside) << 8
                        : qx > qy ? EncodeSign(dx) << 16 | 0x8000u : vertical;
                    _pixels[i] = 0xff000000u | normal | Encode(distance / edgeWidth);
                }
            }
        }

        private static uint EncodeSign(double value) => value < 0 ? 0u : value > 0 ? 255u : 128u;

        private static uint Encode(double value) => (uint)Math.Round((Math.Clamp(value, -1, 1) * 0.5 + 0.5) * 255);
    }
}
