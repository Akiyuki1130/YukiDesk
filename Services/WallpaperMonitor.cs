using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using DesktopBar.Interop;

namespace DesktopBar.Services
{
    /// <summary>读取 Windows 正在主屏显示的壁纸，按桌面填充方式采样指定区域。</summary>
    internal static class WallpaperMonitor
    {
        private static readonly Type DesktopWallpaperClass = Type.GetTypeFromCLSID(
            new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"));

        internal sealed record Snapshot(string Path, DateTime Modified, int Style, int OriginX, int OriginY,
            Rectangle Monitor, Rectangle Canvas, uint Background)
        {
            internal bool IsSlideshow { get; init; }
        }

        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            // 声明至 GetPosition 的所有槽位，保持 Windows SDK 的 ABI 顺序。
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] string path);
            [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor,
                [MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetMonitorDevicePathCount(out uint count);
            [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitor, out MonitorRect bounds);
            void SetBackgroundColor(uint color);
            [PreserveSig] int GetBackgroundColor(out uint color);
            void SetPosition(int position);
            [PreserveSig] int GetPosition(out int position);
            [PreserveSig] int SetSlideshow(IntPtr items);
            [PreserveSig] int GetSlideshow(out IntPtr items);
            [PreserveSig] int SetSlideshowOptions(uint options, uint interval);
            [PreserveSig] int GetSlideshowOptions(out uint options, out uint interval);
            [PreserveSig] int AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitor, int direction);
            [PreserveSig] int GetStatus(out uint state);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorRect
        {
            public int Left, Top, Right, Bottom;
        }

        internal static Snapshot Read()
        {
            Snapshot state = ReadDesktopWallpaper();
            if (state != null) return state;

            // Explorer 重启或 COM 暂不可用时仍可处理静态壁纸。
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var path = key?.GetValue("Wallpaper") as string ?? "";
            if (!int.TryParse(key?.GetValue("WallpaperStyle") as string, out int style)) style = 10;
            if (key?.GetValue("TileWallpaper") as string == "1") style = 0;
            int.TryParse(key?.GetValue("WallpaperOriginX") as string, out int x);
            int.TryParse(key?.GetValue("WallpaperOriginY") as string, out int y);
            var monitor = new Rectangle(0, 0, NativeMethods.ScreenWidth(), NativeMethods.ScreenHeight());
            uint background = (uint)(SystemColors.Desktop.R | SystemColors.Desktop.G << 8 | SystemColors.Desktop.B << 16);
            return new Snapshot(path, LastWrite(path), style, x, y, monitor, monitor, background);
        }

        private static Snapshot ReadDesktopWallpaper()
        {
            IDesktopWallpaper wallpaper = null;
            try
            {
                wallpaper = (IDesktopWallpaper)Activator.CreateInstance(DesktopWallpaperClass);
                if (wallpaper.GetMonitorDevicePathCount(out uint count) != 0) return null;
                string primary = null;
                Rectangle monitor = Rectangle.Empty, canvas = Rectangle.Empty;
                for (uint i = 0; i < count; i++)
                {
                    if (wallpaper.GetMonitorDevicePathAt(i, out var id) != 0
                        || string.IsNullOrEmpty(id) || wallpaper.GetMonitorRECT(id, out var rect) != 0) continue;
                    var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                    if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                    canvas = canvas.IsEmpty ? bounds : Rectangle.Union(canvas, bounds);
                    if (bounds.Location == Point.Empty)
                    {
                        primary = id;
                        monitor = bounds;
                    }
                }
                // 幻灯片必须传显示器 ID；传 null 会返回空路径。
                if (primary == null || wallpaper.GetWallpaper(primary, out var path) != 0) return null;
                wallpaper.GetBackgroundColor(out uint background);
                int style = wallpaper.GetPosition(out int position) == 0 ? position switch
                {
                    0 => 1, 1 => 0, 2 => 2, 3 => 6, 5 => 22, _ => 10
                } : 10;
                bool slideshow = wallpaper.GetStatus(out uint state) == 0 && (state & 0x07) == 0x03;
                return new Snapshot(path, LastWrite(path), style, 0, 0, monitor,
                    style == 22 ? canvas : monitor, background) { IsSlideshow = slideshow };
            }
            catch (COMException) { return null; }
            finally { if (wallpaper != null) Marshal.ReleaseComObject(wallpaper); }
        }

        private static DateTime LastWrite(string path) =>
            string.IsNullOrEmpty(path) ? DateTime.MinValue : File.GetLastWriteTimeUtc(path);

        // 每张幻灯片只取一次条带。直接在半分辨率采样，供模糊主体和折射边缘共用。
        internal static byte[] ReadSlideshowStrip(Snapshot state, int width, int height)
            => ReadRegion(state, state.Monitor.Left, state.Monitor.Top, width, height, allowSolidFallback: false);

        internal static byte[] ReadWallpaperRegion(Snapshot state, int x, int y, int width, int height)
            => ReadRegion(state, x, y, width, height, allowSolidFallback: true);

        private static byte[] ReadRegion(Snapshot state, int x, int y, int width, int height, bool allowSolidFallback)
        {
            try
            {
                using var wallpaper = string.IsNullOrEmpty(state.Path) ? null : TryOpen(state.Path);
                if (wallpaper == null && !allowSolidFallback) return null;
                var pixels = new byte[width * height * 4];
                int red = (int)(state.Background & 255);
                int green = (int)((state.Background >> 8) & 255);
                int blue = (int)((state.Background >> 16) & 255);
                var output = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
                output.Fill(0xff000000u | (uint)(red << 16 | green << 8 | blue));
                if (wallpaper == null) return pixels;
                int offsetX = x - state.Canvas.Left;
                int offsetY = y - state.Canvas.Top;
                int ww = wallpaper.Width, wh = wallpaper.Height;
                int cw = state.Canvas.Width, ch = state.Canvas.Height;
                bool Map(double sx, double sy, out double wx, out double wy) =>
                    MicaService.MapWallpaper(state.Style, state.Style == 0, state.OriginX, state.OriginY,
                        cw, ch, ww, wh, sx, sy, out wx, out wy);
                // 两轴映射独立，每列只算一次横坐标，每行只算一次纵坐标。
                var xPositions = new int[width];
                for (int column = 0; column < width; column++)
                    xPositions[column] = Map(offsetX + column * 2 + 0.5, ch * .5, out var wx, out _) ? (int)wx : -1;
                var locked = TryLock(wallpaper, ww, wh);
                try
                {
                    var rowBytes = locked == null ? null : new byte[ww * 4];
                    var rowPixels = MemoryMarshal.Cast<byte, uint>(rowBytes.AsSpan());
                    int copiedAt = -1;
                    for (int row = 0; row < height; row++)
                    {
                        if (!Map(cw * .5, offsetY + row * 2 + 0.5, out _, out var wy)) continue;
                        int sy = (int)wy;
                        if (locked != null && sy != copiedAt)
                        {
                            Marshal.Copy(locked.Scan0 + sy * locked.Stride, rowBytes, 0, rowBytes.Length);
                            copiedAt = sy;
                        }
                        int dest = row * width;
                        for (int column = 0; column < width; column++)
                        {
                            int sx = xPositions[column];
                            if (sx < 0) continue;
                            uint pixel = locked == null ? unchecked((uint)wallpaper.GetPixel(sx, sy).ToArgb()) : rowPixels[sx];
                            int alpha = (int)(pixel >> 24);
                            if (alpha == 255) output[dest + column] = pixel;
                            else if (alpha != 0)
                            {
                                int inverse = 255 - alpha;
                                int b = (blue * inverse + (int)(pixel & 255) * alpha) / 255;
                                int g = (green * inverse + (int)((pixel >> 8) & 255) * alpha) / 255;
                                int r = (red * inverse + (int)((pixel >> 16) & 255) * alpha) / 255;
                                output[dest + column] = 0xff000000u | (uint)(r << 16 | g << 8 | b);
                            }
                        }
                    }
                }
                finally { if (locked != null) wallpaper.UnlockBits(locked); }
                return pixels;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or ArgumentException)
            {
                return null;
            }
        }

        private static BitmapData TryLock(Bitmap wallpaper, int width, int height)
        {
            try { return wallpaper.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb); }
            catch (Exception ex) when (ex is ExternalException or ArgumentException) { return null; }
        }

        internal static double? SampleTop(Snapshot state, double barHeight)
        {
            Color background = Color.FromArgb((int)(state.Background & 255),
                (int)((state.Background >> 8) & 255), (int)((state.Background >> 16) & 255));
            double baseLightness = Lightness(background);
            if (string.IsNullOrEmpty(state.Path)) return baseLightness;
            using var wallpaper = TryOpen(state.Path) ?? TryOpen(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes", "TranscodedWallpaper"));
            if (wallpaper == null) return null;

            const int columns = 32, rows = 6;
            double total = 0;
            double height = Math.Clamp(barHeight, 1, Math.Max(1, state.Monitor.Height));
            for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                double x = state.Monitor.Left - state.Canvas.Left + state.Monitor.Width * (col + 0.5) / columns;
                double y = state.Monitor.Top - state.Canvas.Top + height * (row + 0.5) / rows;
                if (!MicaService.MapWallpaper(state.Style, state.Style == 0, state.OriginX, state.OriginY,
                    state.Canvas.Width, state.Canvas.Height, wallpaper.Width, wallpaper.Height, x, y, out var wx, out var wy))
                {
                    total += baseLightness;
                    continue;
                }
                var pixel = wallpaper.GetPixel((int)wx, (int)wy);
                total += baseLightness + (Lightness(pixel) - baseLightness) * (pixel.A / 255.0);
            }
            return total / (columns * rows);
        }

        private static Bitmap TryOpen(string path)
        {
            try { return new Bitmap(path); }
            catch (ArgumentException) { return null; }
            catch (ExternalException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static double Lightness(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;
    }
}
