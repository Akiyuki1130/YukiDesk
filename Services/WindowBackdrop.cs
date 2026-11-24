using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using DesktopBar.Interop;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace DesktopBar.Services
{
    // 两个玻璃线程共享窗口会话与 D3D 设备。选窗以各自 HWND 为起点，合成只读交叠区域。
    internal sealed class WindowBackdrop : IDisposable
    {
        private const int MaxLayers = 4;
        private const long MaxSessionPixels = 18_000_000;
        private const int FirstFrameGraceMs = 240;
        private static readonly object Gate = new();
        private static readonly HashSet<WindowBackdrop> Readers = new();
        private static readonly Dictionary<IntPtr, Feed> Feeds = new();
        private static DxgiCapture _readback;
        private static IDirect3DDevice _device;
        private static long _retryDeviceAt;

        private sealed class Feed
        {
            public WindowCapture Source;
            public long RetryAt;
        }

        private sealed class Layer
        {
            public IntPtr Window;
            public Int32Rect Frame, WindowBounds, Crop, PixelsAt, CapturedArea;
            public WindowCapture Source;
            public long Sequence;
            public byte[] Pixels;
            public bool Ready;
        }

        private List<Layer> _layers = new(MaxLayers), _nextLayers = new(MaxLayers);
        private readonly StringBuilder _windowClass = new(80);
        private bool _initialized, _supported;
        private IntPtr _anchor;
        private Int32Rect _area;
        private long _nextScan;
        private long? _missingBackdropSince;
        private byte[] _composed, _composedWallpaper;

        private bool _waitingForWindowFrame;
        internal bool HasSources => _layers.Count > 0;

        internal void InvalidateLayers()
        {
            _nextScan = 0;
            _missingBackdropSince = null;
            _composedWallpaper = null;
        }

        internal bool ShouldWaitForWindowFrame(bool hasWindowPixels, long now, bool expectWindow = false)
        {
            if (!_waitingForWindowFrame && (hasWindowPixels || (!expectWindow && !HasSources)))
            {
                _missingBackdropSince = null;
                return false;
            }
            // A source may be listed even when it cannot deliver a frame. After the
            // grace period present the available layers (or wallpaper) and keep retrying.
            _missingBackdropSince ??= now;
            return now - _missingBackdropSince.Value < FirstFrameGraceMs;
        }

        internal byte[] Composite(IntPtr anchor, Int32Rect area, byte[] wallpaper, bool wallpaperChanging = false)
        {
            _waitingForWindowFrame = false;
            if (anchor == IntPtr.Zero || !Initialize()) return wallpaper;
            lock (Gate)
            {
                long now = Environment.TickCount64;
                bool geometryChanged = _anchor != anchor || _area != area;
                bool dirty = geometryChanged || wallpaperChanging;
                if (geometryChanged || now >= _nextScan)
                {
                    _anchor = anchor;
                    _area = area;
                    _nextScan = now + 100;
                    bool layersChanged = ReadLayers(anchor, area);
                    bool readersChanged = _layers.Count == 0 ? Readers.Remove(this) : Readers.Add(this);
                    if (layersChanged || readersChanged) TrimSessions();
                    dirty |= layersChanged;
                }
                if (_layers.Count == 0 || !EnsureDevice(now)) return ShowWallpaper(wallpaper);

                foreach (var layer in _layers)
                {
                    var source = layer.Source;
                    long sequence = layer.Sequence;
                    var destination = layer.PixelsAt;
                    var crop = layer.Crop;
                    bool ready = ReadPixels(layer, now);
                    dirty |= ready != layer.Ready || (ready && (source != layer.Source || sequence != layer.Sequence
                        || destination != layer.PixelsAt || crop != layer.Crop));
                    layer.Ready = ready;
                }
                return ComposeReadyLayers(wallpaper, dirty);
            }
        }

        private byte[] ComposeReadyLayers(byte[] wallpaper, bool dirty)
        {
            if (!_layers.Exists(layer => layer.Ready)) return ShowWallpaper(wallpaper);
            if (!dirty && ReferenceEquals(wallpaper, _composedWallpaper)) return _composed;
            if (_composed?.Length != wallpaper.Length) _composed = new byte[wallpaper.Length];
            wallpaper.CopyTo(_composed, 0);
            for (int i = _layers.Count - 1; i >= 0; i--)
                if (_layers[i].Ready) BlendLayer(_composed, _area.Width / 2, _layers[i]);
            _composedWallpaper = wallpaper;
            return _composed;
        }

        private byte[] ShowWallpaper(byte[] wallpaper)
        {
            _composedWallpaper = null;
            return wallpaper;
        }

        internal void Suspend()
        {
            _missingBackdropSince = null;
            _composedWallpaper = null;
            if (_anchor == IntPtr.Zero) return;
            lock (Gate)
            {
                _anchor = IntPtr.Zero;
                _layers.Clear();
                _nextLayers.Clear();
                Readers.Remove(this);
                TrimSessions();
            }
        }

        private bool Initialize()
        {
            if (_initialized) return _supported;
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) || RoInitialize(1) < 0)
                return false;
            _initialized = true;
            try { _supported = GraphicsCaptureSession.IsSupported(); }
            catch (Exception ex) when (ex is COMException or PlatformNotSupportedException) { }
            return _supported;
        }

        private static bool EnsureDevice(long now)
        {
            if (_readback != null) return true;
            if (now < _retryDeviceAt) return false;
            try
            {
                _readback = DxgiCapture.CreateWindowReader(out _device);
                return true;
            }
            catch (Exception ex)
            {
                _retryDeviceAt = now + 5000;
                App.Log("逐窗口玻璃: 无法初始化采集设备 " + ex.Message);
                return false;
            }
        }

        private bool ReadLayers(IntPtr anchor, Int32Rect area)
        {
            _nextLayers.Clear();
            const uint NextWindow = 2;
            const uint Cloaked = 14, ExtendedFrame = 9;
            const int Layered = 0x80000;
            int visited = 0;
            for (var window = GetWindow(anchor, NextWindow);
                window != IntPtr.Zero && _nextLayers.Count < MaxLayers && visited++ < 256;
                window = GetWindow(window, NextWindow))
            {
                if (!IsWindowVisible(window) || IsIconic(window) || !GetWindowRect(window, out var bounds)
                    || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top
                    || !Intersects(bounds, area)) continue;
                GetWindowThreadProcessId(window, out uint owner);
                if (owner == Environment.ProcessId || owner == 0) continue;
                if (DwmGetWindowAttribute(window, Cloaked, out var cloaked, sizeof(uint)) == 0 && cloaked != 0) continue;
                if (GetWindowDisplayAffinity(window, out var affinity) && affinity != 0) continue;
                _windowClass.Clear();
                GetClassName(window, _windowClass, _windowClass.Capacity);
                if (_windowClass.ToString() is "Progman" or "WorkerW" or "SHELLDLL_DefView") continue;

                var frame = GetFrameBounds(window, ExtendedFrame, out var extended, Marshal.SizeOf<Rect>()) == 0
                    && extended.Right > extended.Left && extended.Bottom > extended.Top ? extended : bounds;
                if (!Intersects(frame, area)) continue;
                var layer = _layers.Find(entry => entry.Window == window) ?? new Layer { Window = window };
                layer.Frame = frame.ToRect();
                layer.WindowBounds = bounds.ToRect();
                _nextLayers.Add(layer);
                int style = GetWindowLong(window, -20);
                // An opaque window covering the entire region hides all lower layers.
                if ((style & Layered) == 0 && frame.Left <= area.X && frame.Top <= area.Y
                    && frame.Right >= area.X + area.Width && frame.Bottom >= area.Y + area.Height) break;
            }
            bool changed = !_layers.SequenceEqual(_nextLayers);
            (_layers, _nextLayers) = (_nextLayers, _layers);
            _nextLayers.Clear();
            return changed;
        }

        private static bool Intersects(Rect rect, Int32Rect area) => rect.Left < area.X + area.Width
            && rect.Right > area.X && rect.Top < area.Y + area.Height && rect.Bottom > area.Y;

        private bool ReadPixels(Layer layer, long now)
        {
            if (!Feeds.TryGetValue(layer.Window, out var feed))
                Feeds.Add(layer.Window, feed = new Feed());
            if (feed.Source == null && now < feed.RetryAt) return false;
            try
            {
                if (feed.Source == null)
                {
                    long reserved = Feeds.Values.Sum(entry => entry.Source?.PixelCount ?? 0);
                    if (reserved + (long)layer.Frame.Width * layer.Frame.Height > MaxSessionPixels) return false;
                    feed.Source = new WindowCapture(layer.Window, _device);
                }
                var source = feed.Source;
                bool haveFrame = source.Update();
                if (!haveFrame && !source.AwaitingResizedFrame)
                {
                    _waitingForWindowFrame = true;
                    return false;
                }
                var size = source.Size;
                var bounds = size.Width == layer.WindowBounds.Width && size.Height == layer.WindowBounds.Height
                    ? layer.WindowBounds : layer.Frame;
                int left = (Math.Max(_area.X, bounds.X) - _area.X + 1) / 2;
                int top = (Math.Max(_area.Y, bounds.Y) - _area.Y + 1) / 2;
                int right = (Math.Min(_area.X + _area.Width, bounds.X + Math.Min(bounds.Width, size.Width)) - _area.X + 1) / 2;
                int bottom = (Math.Min(_area.Y + _area.Height, bounds.Y + Math.Min(bounds.Height, size.Height)) - _area.Y + 1) / 2;
                if (left >= right || top >= bottom) return false;
                var target = new Int32Rect(left, top, right - left, bottom - top);
                var crop = new Int32Rect(_area.X + 2 * left - bounds.X, _area.Y + 2 * top - bounds.Y,
                    target.Width * 2 - 1, target.Height * 2 - 1);
                if (!haveFrame)
                {
                    bool retained = layer.Ready && ReferenceEquals(source, layer.Source)
                        && layer.CapturedArea == _area && layer.Crop == crop && layer.PixelsAt == target;
                    _waitingForWindowFrame |= !retained;
                    return retained;
                }
                if (ReferenceEquals(source, layer.Source) && source.Sequence == layer.Sequence && crop == layer.Crop)
                {
                    layer.PixelsAt = target;
                    layer.CapturedArea = _area;
                    return true;
                }
                int bytes = crop.Width * crop.Height * 4;
                if (layer.Pixels == null || layer.Pixels.Length < bytes) layer.Pixels = new byte[bytes];
                if (!source.Copy(_readback, crop.X, crop.Y, crop.Width, crop.Height, layer.Pixels)) return false;
                layer.Source = source;
                layer.Sequence = source.Sequence;
                layer.Crop = crop;
                layer.PixelsAt = target;
                layer.CapturedArea = _area;
                return true;
            }
            catch (Exception)
            {
                feed.Source?.Dispose();
                feed.Source = null;
                feed.RetryAt = now + 3000;
                return false;
            }
        }

        private static void BlendLayer(byte[] output, int width, Layer layer)
        {
            var destination = layer.PixelsAt;
            var pixels = layer.Pixels;
            for (int y = 0; y < destination.Height; y++)
            {
                int source = y * 2 * layer.Crop.Width * 4;
                int target = ((destination.Y + y) * width + destination.X) * 4;
                for (int x = 0; x < destination.Width; x++, source += 8, target += 4)
                {
                    int alpha = pixels[source + 3];
                    if (alpha == 0) continue;
                    if (alpha == 255)
                    {
                        output[target] = pixels[source];
                        output[target + 1] = pixels[source + 1];
                        output[target + 2] = pixels[source + 2];
                        continue;
                    }
                    int inverse = 255 - alpha;
                    output[target] = Blend(pixels[source], output[target], inverse);
                    output[target + 1] = Blend(pixels[source + 1], output[target + 1], inverse);
                    output[target + 2] = Blend(pixels[source + 2], output[target + 2], inverse);
                }
            }
        }

        private static byte Blend(byte source, byte background, int inverse)
            => (byte)Math.Min(255, source + (background * inverse + 127) / 255);

        private static void TrimSessions()
        {
            foreach (var window in Feeds.Keys.ToArray())
            {
                if (Readers.Any(reader => reader._layers.Any(layer => layer.Window == window))) continue;
                Feeds[window].Source?.Dispose();
                Feeds.Remove(window);
            }
            if (Readers.Count != 0) return;
            WindowCapture.Close(_device);
            _device = null;
            _readback?.Dispose();
            _readback = null;
        }

        public void Dispose()
        {
            Suspend();
            if (_initialized) RoUninitialize();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            internal int Left, Top, Right, Bottom;
            internal readonly Int32Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
        }

        [DllImport("combase.dll", ExactSpelling = true)] private static extern int RoInitialize(uint type);
        [DllImport("combase.dll", ExactSpelling = true)] private static extern void RoUninitialize();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int GetFrameBounds(IntPtr window, uint attribute, out Rect value, int size);
    }
}
