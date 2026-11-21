using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace DesktopBar.Interop
{
    /// <summary>DXGI Desktop Duplication 抓屏，COM 签名与结构布局以 Windows SDK 为准。</summary>
    internal sealed class DxgiCapture : IDisposable
    {
        private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
        private const int D3D11_USAGE_STAGING = 3;
        private const uint D3D11_CPU_ACCESS_READ = 0x20000;
        private const uint D3D11_MAP_READ = 1;
        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const int DXGI_ERROR_MORE_DATA = unchecked((int)0x887A0003);

        private static readonly Guid IID_IDXGIDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        private static readonly Guid IID_IDXGIOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        private static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private static readonly Guid SurfaceAccess = new Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
        private static readonly Guid D3DMultithread = new Guid("9b7e4e00-342c-4106-a19f-4f2704f689f0");

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OutputDesc
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public Rect DesktopCoordinates;
            public int AttachedToDesktop, Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DuplicationDesc
        {
            public int Width, Height, RefreshNumerator, RefreshDenominator, Format, ScanlineOrdering, Scaling;
            public int Rotation, DesktopImageInSystemMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FrameInfo
        {
            public long LastPresentTime, LastMouseUpdateTime;
            public int AccumulatedFrames, RectsCoalesced, ProtectedContentMaskedOut;
            public int PointerX, PointerY, PointerVisible;
            public uint TotalMetadataBufferSize, PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TextureDesc
        {
            public int Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality;
            public int Usage, BindFlags;
            public uint CpuAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MappedResource
        {
            public IntPtr Data;
            public int RowPitch, DepthPitch;
        }

        private static readonly int RectSize = Marshal.SizeOf<Rect>();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, ref Guid riid, out IntPtr ppv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapterFn(IntPtr self, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumOutputsFn(IntPtr self, uint index, out IntPtr output);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DuplicateOutputFn(IntPtr self, IntPtr device, out IntPtr duplication);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputDescFn(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDuplicationDescFn(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AcquireNextFrameFn(IntPtr self, uint timeoutMs, IntPtr frameInfo, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFrameRectsFn(IntPtr self, uint bufSize, IntPtr rects, out uint required);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseFrameFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture2DFn(IntPtr self, IntPtr desc, IntPtr initialData, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapFn(IntPtr self, IntPtr resource, uint sub, uint mapType, uint flags, IntPtr mapped);
        // ⚠ ID3D11DeviceContext::Unmap / CopySubresourceRegion 原生返回 void,不是 HRESULT:
        // 若按 int 声明会把 EAX 里的残留值当返回值,偶尔读出负数(实测 0x8F517520)误判为失败,
        // 导致整段抓帧全丢(捕获相位=6)且极难复现。
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapFn(IntPtr self, IntPtr resource, uint sub);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyFn(IntPtr self, IntPtr dst, uint dstSub, uint dstX, uint dstY, uint dstZ, IntPtr src, uint srcSub, IntPtr box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetMultithreadProtectedFn(IntPtr self, int enabled);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            int[] featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);
        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr device, out IntPtr result);

        private IntPtr _device, _context, _duplication, _resource;
        private int _stageW, _stageH;
        private IntPtr _stage, _stageDesc;
        private IntPtr _frameInfo, _mapBuf, _boxBuf, _dirtyBuf;
        private uint _dirtyBufSize;
        private bool _hasFrame;
        // 热路径复用委托和元数据缓冲，避免每帧封送函数指针或分配缓冲。
        private AcquireNextFrameFn _acquire;
        private GetFrameRectsFn _dirtyRects, _moveRects;
        private ReleaseFrameFn _idupReleaseFrame;
        private CreateTexture2DFn _createTexture;
        private CopyFn _copyRegion;
        private MapFn _map;
        private UnmapFn _unmap;
        private QueryInterfaceFn _qiResource;
        private ReleaseFn _relResource;
        private ReleaseFn _relTexture;
        private IntPtr _resVtbl, _texVtbl;

        public int Width { get; private set; }
        public int Height { get; private set; }
        /// <summary>最近一次 Acq​uire 是否持有未释放的帧(调用方用完必须 ReleaseFrame)。</summary>
        public bool HasFrame { get { return _hasFrame; } }

        /// <summary>创建设备 + 复制会话。失败返回 null(调用方回退 GDI BitBlt)。</summary>
        public static DxgiCapture TryCreate()
        {
            DxgiCapture cap = new DxgiCapture();
            string why;
            try { why = cap.Init(); }
            catch (Exception ex) { why = ex.Message; }
            if (why == null) return cap;
            App.Log("玻璃:GPU 捕获初始化失败(" + why + "),回退 GDI BitBlt");
            cap.Dispose();
            return null;
        }

        private string Init()
        {
            int hr = InitReadback();
            if (hr < 0) return "D3D11CreateDevice hr=0x" + ((uint)hr).ToString("X8");

            IntPtr dxgiDevice = QueryInterface(_device, IID_IDXGIDevice);
            if (dxgiDevice == IntPtr.Zero) return "QI IDXGIDevice 失败";

            IntPtr adapter;
            hr = Fn<GetAdapterFn>(dxgiDevice, 7)(dxgiDevice, out adapter);
            Release(dxgiDevice);
            if (hr < 0 || adapter == IntPtr.Zero) return "GetAdapter hr=0x" + ((uint)hr).ToString("X8");

            // 顶栏固定在主屏。适配器上找不到原点所属输出时交给 GDI，避免抓到副屏。
            IntPtr output = IntPtr.Zero;
            try { output = FindPrimaryOutput(adapter); }
            finally { Release(adapter); }
            if (output == IntPtr.Zero) return "适配器上找不到主显示器输出";

            IntPtr output1 = QueryInterface(output, IID_IDXGIOutput1);
            Release(output);
            if (output1 == IntPtr.Zero) return "QI IDXGIOutput1 失败";
            try { hr = Fn<DuplicateOutputFn>(output1, 22)(output1, _device, out _duplication); }
            finally { Release(output1); }
            if (hr < 0 || _duplication == IntPtr.Zero)
                return "DuplicateOutput hr=0x" + ((uint)hr).ToString("X8");

            var descBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<DuplicationDesc>());
            try
            {
                Fn<GetDuplicationDescFn>(_duplication, 7)(_duplication, descBuffer);
                var desc = Marshal.PtrToStructure<DuplicationDesc>(descBuffer);
                Width = desc.Width;
                Height = desc.Height;
                if (Width <= 0 || Height <= 0) return "输出尺寸无效";
                if (desc.Rotation != 1) return "显示器旋转 " + desc.Rotation;
                if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM) return "桌面格式 " + desc.Format;
            }
            finally { Marshal.FreeHGlobal(descBuffer); }

            _frameInfo = Marshal.AllocHGlobal(Marshal.SizeOf<FrameInfo>());
            _dirtyBufSize = 8192;
            _dirtyBuf = Marshal.AllocHGlobal((int)_dirtyBufSize);
            _acquire = Fn<AcquireNextFrameFn>(_duplication, 8);
            _dirtyRects = Fn<GetFrameRectsFn>(_duplication, 9);
            _moveRects = Fn<GetFrameRectsFn>(_duplication, 10);
            _idupReleaseFrame = Fn<ReleaseFrameFn>(_duplication, 14);
            return null;
        }

        private int InitReadback()
        {
            int[] levels = { 0xb000, 0xa000 };
            int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, levels, 2, 7, out _device, out _, out _context);
            if (hr < 0) return hr;
            _stageDesc = Marshal.AllocHGlobal(Marshal.SizeOf<TextureDesc>());
            _mapBuf = Marshal.AllocHGlobal(Marshal.SizeOf<MappedResource>());
            _boxBuf = Marshal.AllocHGlobal(6 * sizeof(uint));
            _createTexture = Fn<CreateTexture2DFn>(_device, 5);
            _copyRegion = Fn<CopyFn>(_context, 46);
            _map = Fn<MapFn>(_context, 14);
            _unmap = Fn<UnmapFn>(_context, 15);
            return 0;
        }

        internal static DxgiCapture CreateWindowReader(out IDirect3DDevice device)
        {
            var reader = new DxgiCapture();
            try
            {
                Marshal.ThrowExceptionForHR(reader.InitReadback());
                IntPtr threading = reader.QueryInterface(reader._context, D3DMultithread);
                if (threading != IntPtr.Zero)
                {
                    try { Fn<SetMultithreadProtectedFn>(threading, 5)(threading, 1); }
                    finally { Release(threading); }
                }
                IntPtr dxgi = reader.QueryInterface(reader._device, IID_IDXGIDevice);
                if (dxgi == IntPtr.Zero) throw new InvalidOperationException("IDXGIDevice unavailable");
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var result));
                    try { device = MarshalInterface<IDirect3DDevice>.FromAbi(result); }
                    finally { Release(result); }
                }
                finally { Release(dxgi); }
                return reader;
            }
            catch { reader.Dispose(); throw; }
        }

        internal bool CopyWindowBand(IDirect3DSurface surface, int x, int y, int width, int height, byte[] pixels)
        {
            var inspectable = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
            try
            {
                var access = QueryInterface(inspectable, SurfaceAccess);
                if (access == IntPtr.Zero) return false;
                try
                {
                    var iid = IID_ID3D11Texture2D;
                    if (Fn<QueryInterfaceFn>(access, 3)(access, ref iid, out var texture) < 0) return false;
                    try { return CopyTextureBand(texture, x, y, width, height, pixels); }
                    finally { Release(texture); }
                }
                finally { Release(access); }
            }
            finally { Release(inspectable); }
        }

        private static IntPtr FindPrimaryOutput(IntPtr adapter)
        {
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<OutputDesc>());
            try
            {
                var enumerate = Fn<EnumOutputsFn>(adapter, 7);
                for (uint i = 0; enumerate(adapter, i, out IntPtr output) >= 0 && output != IntPtr.Zero; i++)
                {
                    bool found = false;
                    try
                    {
                        if (Fn<GetOutputDescFn>(output, 7)(output, buffer) < 0) continue;
                        var desc = Marshal.PtrToStructure<OutputDesc>(buffer);
                        var rect = desc.DesktopCoordinates;
                        found = desc.AttachedToDesktop != 0 && rect.Left <= 0 && rect.Top <= 0
                            && rect.Right > 0 && rect.Bottom > 0;
                        if (found) return output;
                    }
                    finally { if (!found) Release(output); }
                }
                return IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private void FillTextureDesc(int width, int height)
        {
            var desc = new TextureDesc
            {
                Width = width, Height = height, MipLevels = 1, ArraySize = 1,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
                Usage = D3D11_USAGE_STAGING, CpuAccessFlags = D3D11_CPU_ACCESS_READ
            };
            Marshal.StructureToPtr(desc, _stageDesc, false);
        }

        /// <summary>等一帧。返回 0=拿到帧(调用方随后 CopyBand 并释放) 1=超时(桌面没变化) 2=会话失效需重建。</summary>
        public int Acquire(int timeoutMs)
        {
            if (_hasFrame) return 0;
            if (_duplication == IntPtr.Zero) return 2;
            int hr;
            if (_acquire == null) return 2;
            try { hr = _acquire(_duplication, (uint)Math.Max(0, timeoutMs), _frameInfo, out _resource); }
            catch { return 2; }
            if (hr == 0)
            {
                _hasFrame = true;
                if (_resource != IntPtr.Zero) return 0;
                ReleaseFrame();
            }
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) return 1;
            return 2;
        }

        private bool _copyDiag;
        /// <summary>只写一条失败原因(捕获失败通常每帧都一样,记一次足够)。</summary>
        private void Diag(string msg)
        {
            if (_copyDiag) return;
            _copyDiag = true;
            try { DesktopBar.App.Log("[dxgi] CopyBand " + msg); } catch { }
        }

        /// <summary>把当前帧的矩形区域读回 dst(BGRA,无行填充)。必须紧跟 Acquire 成功之后调用。</summary>
        public bool CopyBand(int x, int y, int width, int height, byte[] dst)
        {
            if (!_hasFrame || _resource == IntPtr.Zero || dst == null) return false;
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x > Width - width || y > Height - height)
                return false;
            IntPtr src = ResourceToTexture(_resource);
            if (src == IntPtr.Zero) { Diag("ResourceToTexture 失败"); return false; }
            try { return CopyTextureBand(src, x, y, width, height, dst); }
            finally { _relTexture(src); }
        }

        private bool CopyTextureBand(IntPtr src, int x, int y, int width, int height, byte[] dst)
        {
            int rowBytes = width * 4;
            if (dst.Length < (long)rowBytes * height) return false;
            if (_stage == IntPtr.Zero || _stageW < width || _stageH < height)
            {
                if (_stage != IntPtr.Zero) { Release(_stage); _stage = IntPtr.Zero; }
                int stagingWidth = Math.Max(_stageW, width);
                int stagingHeight = Math.Max(_stageH, height);
                FillTextureDesc(stagingWidth, stagingHeight);
                IntPtr tex;
                int hrc = _createTexture(_device, _stageDesc, IntPtr.Zero, out tex);
                if (hrc >= 0 && tex != IntPtr.Zero)
                {
                    _stage = tex;
                    _stageW = stagingWidth;
                    _stageH = stagingHeight;
                }
                else Diag("CreateTexture2D 失败 hr=0x" + hrc.ToString("X8") + " tex=" + tex);
            }
            if (_stage == IntPtr.Zero) return false;

            Marshal.WriteInt32(_boxBuf, 0, x);              // left
            Marshal.WriteInt32(_boxBuf, 4, y);              // top
            Marshal.WriteInt32(_boxBuf, 8, 0);              // front
            Marshal.WriteInt32(_boxBuf, 12, x + width);     // right
            Marshal.WriteInt32(_boxBuf, 16, y + height);    // bottom
            Marshal.WriteInt32(_boxBuf, 20, 1);             // back
            _copyRegion(_context, _stage, 0, 0, 0, 0, src, 0, _boxBuf);

            int hr = _map(_context, _stage, 0, D3D11_MAP_READ, 0, _mapBuf);
            if (hr < 0) { Diag("Map 失败 hr=0x" + hr.ToString("X8")); return false; }
            try
            {
                var mapped = Marshal.PtrToStructure<MappedResource>(_mapBuf);
                if (mapped.Data == IntPtr.Zero || mapped.RowPitch < rowBytes) return false;
                if (mapped.RowPitch == rowBytes)
                {
                    Marshal.Copy(mapped.Data, dst, 0, rowBytes * height);
                    return true;
                }
                for (int row = 0; row < height; row++)
                    Marshal.Copy(IntPtr.Add(mapped.Data, row * mapped.RowPitch), dst, row * rowBytes, rowBytes);
                return true;
            }
            finally { _unmap(_context, _stage, 0); }
        }

        /// <summary>脏矩形或移动区域的目标与条带相交时，从当前桌面纹理读回整条带。</summary>
        public bool BandDirty(int x, int y, int width, int height)
        {
            if (!_hasFrame) return true;
            // 只有鼠标指针变动时，桌面纹理本身没有变化。
            if (Marshal.ReadInt64(_frameInfo) == 0) return false;
            return RectsIntersect(_dirtyRects, 0, x, y, width, height)
                || RectsIntersect(_moveRects, 2 * sizeof(int), x, y, width, height);
        }

        private bool RectsIntersect(GetFrameRectsFn getRects, int rectOffset, int x, int y, int width, int height)
        {
            int hr = getRects(_duplication, _dirtyBufSize, _dirtyBuf, out uint length);
            if (hr == DXGI_ERROR_MORE_DATA && length > _dirtyBufSize && length <= int.MaxValue)
            {
                _dirtyBuf = Marshal.ReAllocHGlobal(_dirtyBuf, (IntPtr)length);
                _dirtyBufSize = length;
                hr = getRects(_duplication, _dirtyBufSize, _dirtyBuf, out length);
            }
            int stride = RectSize + rectOffset;
            if (hr < 0 || length > _dirtyBufSize || length % stride != 0) return true;
            for (int i = rectOffset; i < length; i += stride)
            {
                var rect = Marshal.PtrToStructure<Rect>(IntPtr.Add(_dirtyBuf, i));
                if (rect.Left < x + width && rect.Right > x && rect.Top < y + height && rect.Bottom > y)
                    return true;
            }
            return false;
        }

        public void ReleaseFrame()
        {
            if (!_hasFrame) return;
            _hasFrame = false;
            try
            {
                if (_resource == IntPtr.Zero) return;
                IntPtr vtable = Marshal.ReadIntPtr(_resource);
                if (_relResource != null && vtable == _resVtbl) _relResource(_resource);
                else Release(_resource);
            }
            finally
            {
                _resource = IntPtr.Zero;
                _idupReleaseFrame(_duplication);
            }
        }

        public void Dispose()
        {
            ReleaseFrame();
            if (_stage != IntPtr.Zero) { Release(_stage); _stage = IntPtr.Zero; }
            if (_duplication != IntPtr.Zero) { Release(_duplication); _duplication = IntPtr.Zero; }
            if (_context != IntPtr.Zero) { Release(_context); _context = IntPtr.Zero; }
            if (_device != IntPtr.Zero) { Release(_device); _device = IntPtr.Zero; }
            if (_stageDesc != IntPtr.Zero) { Marshal.FreeHGlobal(_stageDesc); _stageDesc = IntPtr.Zero; }
            if (_frameInfo != IntPtr.Zero) { Marshal.FreeHGlobal(_frameInfo); _frameInfo = IntPtr.Zero; }
            if (_mapBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_mapBuf); _mapBuf = IntPtr.Zero; }
            if (_boxBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_boxBuf); _boxBuf = IntPtr.Zero; }
            if (_dirtyBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_dirtyBuf); _dirtyBuf = IntPtr.Zero; }
        }

        /// <summary>
        /// 帧资源(IDXGIResource)→ ID3D11Texture2D。帧资源每帧都是新对象,但 vtable 是同一个,
        /// 所以 QI/Release 的方法指针只查一次(vtable 变了才重建),避免每帧分配委托。
        /// </summary>
        private IntPtr ResourceToTexture(IntPtr resource)
        {
            if (resource == IntPtr.Zero) return IntPtr.Zero;
            IntPtr vt = Marshal.ReadIntPtr(resource);
            if (_qiResource == null || vt != _resVtbl)
            {
                _resVtbl = vt;
                _qiResource = Fn<QueryInterfaceFn>(resource, 0);
                _relResource = Fn<ReleaseFn>(resource, 2);
            }
            Guid g = IID_ID3D11Texture2D;
            IntPtr ppv;
            if (_qiResource(resource, ref g, out ppv) < 0) return IntPtr.Zero;
            // Release 必须用「纹理自己 vtable 槽 2」的函数:不同 COM 类的 Release 实现里
            // 引用计数字段的偏移不一定相同,借资源的方法指针去释放纹理会踩坏内存(实测 AV)。
            IntPtr texVt = Marshal.ReadIntPtr(ppv);
            if (_relTexture == null || texVt != _texVtbl)
            {
                _texVtbl = texVt;
                _relTexture = Fn<ReleaseFn>(ppv, 2);
            }
            return ppv;
        }

        private IntPtr QueryInterface(IntPtr obj, Guid iid)
        {
            try
            {
                IntPtr ppv;
                Guid g = iid;
                if (Fn<QueryInterfaceFn>(obj, 0)(obj, ref g, out ppv) < 0) return IntPtr.Zero;
                return ppv;
            }
            catch { return IntPtr.Zero; }
        }

        private static void Release(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            try { Fn<ReleaseFn>(obj, 2)(obj); } catch { }
        }

        private static T Fn<T>(IntPtr obj, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            return (T)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size), typeof(T));
        }
    }
}
