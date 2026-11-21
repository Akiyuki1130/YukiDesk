using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;
using WinRT;

namespace DesktopBar.Interop
{
    /// <summary>Windows Graphics Capture source for one HWND. Only the compositor consumes frames.</summary>
    internal sealed class WindowCapture : IDisposable
    {
        private static readonly Guid FactoryInterface = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        private static readonly Guid CaptureItemInterface = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Lazy<Task<bool>> BorderlessAccess = new(RequestBorderlessAccessAsync);
        private const DirectXPixelFormat Format = DirectXPixelFormat.B8G8R8A8UIntNormalized;
        private readonly IDirect3DDevice _device;
        private readonly GraphicsCaptureItem _item;
        private readonly Direct3D11CaptureFramePool _pool;
        private GraphicsCaptureSession _session;
        private SizeInt32 _size;
        private Direct3D11CaptureFrame _frame;
        private bool _borderConfigured;
        private bool _started;
        private readonly long _borderDeadline = Environment.TickCount64 + 250;

        internal long Sequence { get; private set; }
        internal SizeInt32 Size => _size;
        internal long PixelCount => (long)_size.Width * _size.Height;

        internal WindowCapture(IntPtr window, IDirect3DDevice device)
        {
            _device = device;
            _item = CreateItem(window);
            _size = _item.Size;
            if (_size.Width <= 0 || _size.Height <= 0 || PixelCount > 18_000_000)
                throw new ArgumentException("Unsupported capture dimensions");
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, Format, 2, _size);
            try
            {
                _session = _pool.CreateCaptureSession(_item);
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) _session.IsCursorCaptureEnabled = false;
                PrepareSession();
            }
            catch { Dispose(); throw; }
        }

        internal bool Update()
        {
            if (!PrepareSession()) return false;
            var next = _pool.TryGetNextFrame();
            if (next == null) return _frame != null;
            _frame?.Dispose();
            _frame = null;
            var size = next.ContentSize;
            if (size.Width != _size.Width || size.Height != _size.Height)
            {
                next.Dispose();
                if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > 18_000_000) return false;
                _pool.Recreate(_device, Format, 2, size);
                _size = size;
                return false;
            }
            _frame = next;
            Sequence++;
            return true;
        }

        private bool PrepareSession()
        {
            if (!_borderConfigured && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            {
                var access = BorderlessAccess.Value;
                if (access.IsCompletedSuccessfully)
                {
                    _borderConfigured = true;
                    if (access.Result)
                    {
                        // The system keeps control of the indicator, including after a permission change.
                        try { _session.IsBorderRequired = false; }
                        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
                        {
                            App.Log("逐窗口玻璃: 无法应用无边框采集 " + ex.Message);
                        }
                    }
                }
                else if (!_started && Environment.TickCount64 < _borderDeadline) return false;
            }
            if (!_started)
            {
                _session.StartCapture();
                _started = true;
            }
            return true;
        }

        private static async Task<bool> RequestBorderlessAccessAsync()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) return false;
            try
            {
                var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
                App.Log("逐窗口玻璃: 无边框采集权限 " + status);
                return status == AppCapabilityAccessStatus.Allowed;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            {
                App.Log("逐窗口玻璃: 无边框采集权限不可用 " + ex.Message);
                return false;
            }
        }

        internal bool Copy(DxgiCapture reader, int x, int y, int width, int height, byte[] pixels)
        {
            if (_frame == null || x < 0 || y < 0 || x + width > _size.Width || y + height > _size.Height) return false;
            return reader.CopyWindowBand(_frame.Surface, x, y, width, height, pixels);
        }

        private static GraphicsCaptureItem CreateItem(IntPtr window)
        {
            const string runtimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out var name));
            try
            {
                var iid = FactoryInterface;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref iid, out var factory));
                try
                {
                    var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 3 * IntPtr.Size);
                    var create = Marshal.GetDelegateForFunctionPointer<CreateForWindow>(address);
                    iid = CaptureItemInterface;
                    Marshal.ThrowExceptionForHR(create(factory, window, ref iid, out var item));
                    try { return MarshalInspectable<GraphicsCaptureItem>.FromAbi(item); }
                    finally { Marshal.Release(item); }
                }
                finally { Marshal.Release(factory); }
            }
            finally { WindowsDeleteString(name); }
        }

        public void Dispose()
        {
            Close(_frame);
            _frame = null;
            Close(_session);
            _session = null;
            Close(_pool);
        }

        internal static void Close(IDisposable capture)
        {
            try { capture?.Dispose(); }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException) { }
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateForWindow(IntPtr factory, IntPtr window, ref Guid iid, out IntPtr item);
        [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int WindowsCreateString(string value, int length, out IntPtr handle);
        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsDeleteString(IntPtr value);
        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr result);
    }
}
