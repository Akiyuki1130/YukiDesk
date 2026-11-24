using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace DesktopBar.UI
{
    internal static class FileImage
    {
        private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".dib", ".tif", ".tiff",
            ".webp", ".heic", ".heif", ".avif", ".ico", ".svg", ".svgz"
        };

        internal static bool IsImage(string extension) => Extensions.Contains(extension);

        internal static bool NeedsLocalData(FileAttributes attributes)
        {
            const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
            const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
            return attributes != (FileAttributes)(-1)
                && (attributes & (FileAttributes.Offline | recallOnOpen | recallOnDataAccess)) != 0;
        }

        internal static BitmapSource ReadThumbnail(string path, int pixels)
        {
            if (!IsImage(Path.GetExtension(path))) return null;
            try
            {
                if (NeedsLocalData(File.GetAttributes(path))) return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 50 * 1024 * 1024) return null;
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                bool portrait = frame.PixelHeight > frame.PixelWidth;
                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (portrait) image.DecodePixelHeight = Math.Min(pixels, frame.PixelHeight);
                else image.DecodePixelWidth = Math.Min(pixels, frame.PixelWidth);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException
                or FileFormatException or System.Runtime.InteropServices.ExternalException)
            {
                return null;
            }
        }
    }
}
