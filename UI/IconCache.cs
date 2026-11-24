using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopBar.Interop;

namespace DesktopBar.UI
{
    public static class IconCache
    {
        private sealed class IconRequest
        {
            public string Path;
            public bool IsDirectory;
            public bool IsImage;
            public bool IsPlaceholder;
            public int PixelSize;
            public string Key;
            public List<WeakReference<Image>> Targets;
        }

        private sealed class RequestQueue
        {
            internal readonly LinkedList<IconRequest> Items = new();
            internal Thread Worker;
        }

        private const int CacheCapacity = 512;
        private const int PrefetchCapacity = 96;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, (BitmapSource Image, LinkedListNode<string> Key)> Cache = new();
        private static readonly LinkedList<string> CachedKeys = new();
        private static readonly Dictionary<string, BitmapSource> Fallbacks = new();
        private static readonly Dictionary<string, LinkedListNode<IconRequest>> Pending = new();
        private static readonly RequestQueue PrefetchQueue = new();
        private static readonly RequestQueue VisibleQueue = new();
        private static readonly DependencyProperty RequestKeyProperty = DependencyProperty.RegisterAttached(
            "IconRequestKey", typeof(string), typeof(IconCache));

        public static BitmapSource GetFallback(bool isDir, string ext)
            => GetFallback(isDir, ext, IconPixelSize());

        private static BitmapSource GetFallback(bool isDir, string ext, int pixelSize)
        {
            string key = (isDir ? "dir:" : "file:") + pixelSize;
            lock (Gate)
                if (Fallbacks.TryGetValue(key, out var cached)) return cached;
            var icon = NativeMethods.GetSystemTypeIcon(isDir, pixelSize) ?? TypeIcon(isDir, ext);
            lock (Gate) Fallbacks[key] = icon;
            return icon;
        }

        private static int IconPixelSize()
            => Math.Max(48, (int)Math.Ceiling(48 * Ui.Scale * 1.5));

        internal static void LoadAsync(FileSystemInfo item, Image target = null)
        {
            bool isDirectory = item is DirectoryInfo;
            bool isImage = !isDirectory && FileImage.IsImage(item.Extension);
            bool placeholder;
            long modified;
            try
            {
                placeholder = FileImage.NeedsLocalData(item.Attributes);
                modified = item.LastWriteTimeUtc.Ticks;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            int pixels = IconPixelSize();
            string key = (isDirectory ? "dir:" : "file:") + item.FullName + "|" + pixels + "|" + modified
                + (placeholder ? "|offline" : "");
            BitmapSource icon;
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    CachedKeys.Remove(cached.Key);
                    CachedKeys.AddLast(cached.Key);
                    icon = cached.Image;
                }
                else
                {
                    var queue = target == null ? PrefetchQueue : VisibleQueue;
                    if (!Pending.TryGetValue(key, out var node))
                    {
                        if (target == null && queue.Items.Count >= PrefetchCapacity) return;
                        node = queue.Items.AddLast(new IconRequest
                        {
                            Path = item.FullName, IsDirectory = isDirectory, IsImage = isImage,
                            IsPlaceholder = placeholder, PixelSize = pixels, Key = key
                        });
                        Pending.Add(key, node);
                    }
                    if (target != null)
                    {
                        if (target.GetValue(RequestKeyProperty) is string previous && previous != key) DetachTarget(target, null);
                        var targets = node.Value.Targets ??= new List<WeakReference<Image>>();
                        targets.RemoveAll(reference => !reference.TryGetTarget(out _));
                        if (!targets.Exists(reference => reference.TryGetTarget(out var image) && ReferenceEquals(image, target)))
                        {
                            targets.Add(new WeakReference<Image>(target));
                            target.SetValue(RequestKeyProperty, key);
                            target.Unloaded -= DetachTarget;
                            target.Unloaded += DetachTarget;
                        }
                        if (node.List == PrefetchQueue.Items)
                        {
                            PrefetchQueue.Items.Remove(node);
                            VisibleQueue.Items.AddLast(node);
                        }
                    }
                    if (node.List == queue.Items && queue.Worker == null)
                    {
                        string name = target == null ? "YukiDesk icon prefetch" : "YukiDesk visible icons";
                        queue.Worker = new Thread(() => ProcessQueue(queue)) { IsBackground = true, Name = name };
                        queue.Worker.SetApartmentState(ApartmentState.STA);
                        queue.Worker.Start();
                    }
                    Monitor.PulseAll(Gate);
                    return;
                }
            }
            if (target != null) target.Source = icon;
        }

        private static void DetachTarget(object sender, RoutedEventArgs args)
        {
            var image = (Image)sender;
            image.Unloaded -= DetachTarget;
            if (image.GetValue(RequestKeyProperty) is not string key) return;
            image.ClearValue(RequestKeyProperty);
            lock (Gate)
            {
                if (!Pending.TryGetValue(key, out var node) || node.Value.Targets is not { } targets) return;
                targets.RemoveAll(reference => !reference.TryGetTarget(out var target) || ReferenceEquals(target, image));
                if (targets.Count != 0 || node.List != VisibleQueue.Items) return;
                VisibleQueue.Items.Remove(node);
                Pending.Remove(key);
            }
        }

        private static void ProcessQueue(RequestQueue queue)
        {
            while (true)
            {
                IconRequest request;
                lock (Gate)
                {
                    while (queue.Items.Count == 0)
                    {
                        if (Monitor.Wait(Gate, TimeSpan.FromSeconds(30)) || queue.Items.Count != 0) continue;
                        queue.Worker = null;
                        return;
                    }
                    request = queue.Items.First.Value;
                    queue.Items.RemoveFirst();
                }
                BitmapSource icon = null;
                try
                {
                    if (request.IsImage)
                        icon = (request.IsPlaceholder ? null : FileImage.ReadThumbnail(request.Path, request.PixelSize))
                            ?? NativeMethods.GetShellThumbnail(request.Path, request.PixelSize, cacheOnly: true);
                    if (!request.IsPlaceholder) icon ??= NativeMethods.GetFileIcon(request.Path, request.IsDirectory, request.PixelSize);
                    icon ??= GetFallback(request.IsDirectory, Path.GetExtension(request.Path), request.PixelSize);
                }
                catch (Exception ex) { App.Log("提取文件图标失败: " + ex.Message); }

                List<WeakReference<Image>> targets;
                lock (Gate)
                {
                    Pending.Remove(request.Key);
                    if (icon != null)
                    {
                        if (Cache.Count >= CacheCapacity)
                        {
                            Cache.Remove(CachedKeys.First.Value);
                            CachedKeys.RemoveFirst();
                        }
                        Cache.Add(request.Key, (icon, CachedKeys.AddLast(request.Key)));
                    }
                    targets = request.Targets;
                }
                if (targets == null || icon == null) continue;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted) continue;
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    foreach (var reference in targets)
                        if (reference.TryGetTarget(out var image) && image.IsLoaded) image.Source = icon;
                }));
            }
        }

        private static BitmapSource TypeIcon(bool isDir, string ext)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var color = isDir ? Color.FromRgb(245, 190, 64) : ExtensionColor(ext);
                dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(3, 6, 42, 36), 7, 7);
                if (isDir)
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(255, 214, 92)), null, new Rect(6, 3, 21, 10), 4, 4);
                else
                {
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 2);
                    dc.DrawLine(pen, new Point(12, 17), new Point(36, 17));
                    dc.DrawLine(pen, new Point(12, 24), new Point(32, 24));
                    dc.DrawLine(pen, new Point(12, 31), new Point(28, 31));
                }
            }
            var bmp = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        private static Color ExtensionColor(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".doc": case ".docx": case ".rtf": return Color.FromRgb(54, 111, 220);
                case ".xls": case ".xlsx": case ".csv": return Color.FromRgb(48, 164, 104);
                case ".ppt": case ".pptx": return Color.FromRgb(225, 102, 55);
                case ".pdf": return Color.FromRgb(208, 66, 74);
                case ".zip": case ".rar": case ".7z": return Color.FromRgb(163, 104, 212);
                case ".jpg": case ".jpeg": case ".png": case ".gif": return Color.FromRgb(42, 168, 186);
                case ".mp3": case ".wav": case ".flac": return Color.FromRgb(219, 93, 160);
                case ".bat": case ".cmd": case ".ps1": return Color.FromRgb(89, 104, 122);
                case ".txt": case ".md": return Color.FromRgb(92, 137, 196);
                default: return Color.FromRgb(112, 122, 143);
            }
        }
    }
}
