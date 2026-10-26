using System;
using System.Runtime.InteropServices;

namespace DesktopBar.Interop
{
    /// <summary>
    /// DXGI Desktop Duplication 抓屏(手写 COM 互操作,零第三方依赖)。
    ///
    /// 相比 GDI BitBlt 的三个决定性优势:
    ///  1) 抓一帧约 0.35ms —— BitBlt 是 3.5~6ms,其中约 2.4ms 是与尺寸无关的驱动往返,降分辨率也省不掉;
    ///  2) AcquireNextFrame 天然阻塞到「下一次桌面合成」,所以采样节拍自动等于显示器刷新率(本机 300Hz),
    ///     并且抓到的永远是刚合成完的那一帧 → 端到端只延迟一帧,不需要 Sleep 去凑节拍;
    ///  3) 桌面没有任何变化时 AcquireNextFrame 直接超时,静止场景零 CPU(以前靠帧数/时长启发式降频)。
    ///
    /// 坑(踩过,务必保留这段说明):
    ///  - 所有结构体出参**不能**用 \`out SomeStruct\` 的委托签名,必须传原生缓冲(AllocHGlobal + IntPtr),
    ///    否则 AcquireNextFrame 会把进程打崩、Map 会报 E_INVALIDARG;
    ///  - 暂存纹理的 CPUAccessFlags 必须是 0x20000(D3D11_CPU_ACCESS_READ);0x10000 是 WRITE,
    ///    写成 0x10000 会得到一张「只能写」的纹理,Map(READ) 永远 E_INVALIDARG;
    ///  - ID3D11Device 的 vtable 槽位在不同设备上并不总是规范顺序(本机 CreateTexture2D 在 slot 5,
    ///    规范顺序本该是 9),所以槽位靠 TryCreateTexture 实测探测,不硬编码。
    /// </summary>
    internal sealed class DxgiCapture : IDisposable
    {
        private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
        private const int D3D11_USAGE_STAGING = 3;
        private const uint D3D11_CPU_ACCESS_READ = 0x20000;
        private const uint D3D11_MAP_READ = 1;
        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        private const int DXGI_ERROR_MORE_DATA = unchecked((int)0x887A0003);

        private static readonly Guid IID_IDXGIDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        private static readonly Guid IID_IDXGIOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        private static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, ref Guid riid, out IntPtr ppv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapterFn(IntPtr self, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumOutputsFn(IntPtr self, uint index, out IntPtr output);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DuplicateOutputFn(IntPtr self, IntPtr device, out IntPtr duplication);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputDescFn(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AcquireNextFrameFn(IntPtr self, uint timeoutMs, IntPtr frameInfo, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFrameDirtyRectsFn(IntPtr self, uint bufSize, IntPtr rects, IntPtr required);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseFrameFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture2DFn(IntPtr self, IntPtr desc, IntPtr initialData, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapFn(IntPtr self, IntPtr resource, uint sub, uint mapType, uint flags, IntPtr mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UnmapFn(IntPtr self, IntPtr resource, uint sub);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CopyFn(IntPtr self, IntPtr dst, uint dstSub, uint dstX, uint dstY, uint dstZ, IntPtr src, uint srcSub, IntPtr box);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            int[] featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        private IntPtr _device, _context, _duplication, _resource;
        private int _texSlot = -1;
        private byte[] _descBuf;
        private int _stageW, _stageH;
        private IntPtr _stage, _stageDesc;
        private IntPtr _frameInfo, _mapBuf, _boxBuf, _dirtyBuf;
        private uint _dirtyBufSize;
        private bool _hasFrame;

        public int Width { get; private set; }
        public int Height { get; private set; }
        /// <summary>最近一次 Acq​uire 是否持有未释放的帧(调用方用完必须 ReleaseFrame)。</summary>
        public bool HasFrame { get { return _hasFrame; } }

        /// <summary>创建设备 + 复制会话。失败返回 null(调用方回退 GDI BitBlt)。</summary>
        public static DxgiCapture TryCreate()
        {
            DxgiCapture cap = new DxgiCapture();
            string why = cap.Init();
            if (why == null) return cap;
            try { App.Log("玻璃:GPU 捕获初始化失败(" + why + "),回退 GDI BitBlt"); } catch { }
            cap.Dispose();
            return null;
        }

        private string Init()
        {
            int[] levels = new int[] { 0xb000 /*11_0*/, 0xa000 /*10_0*/ };
            int hr = D3D11CreateDevice(IntPtr.Zero, 1 /*HARDWARE*/, IntPtr.Zero, 0x20 /*BGRA_SUPPORT*/, levels, 2, 7, out _device, out _, out _context);
            if (hr < 0 || _device == IntPtr.Zero || _context == IntPtr.Zero) return "D3D11CreateDevice hr=0x" + ((uint)hr).ToString("X8");

            IntPtr dxgiDevice = QueryInterface(_device, IID_IDXGIDevice);
            if (dxgiDevice == IntPtr.Zero) return "QI IDXGIDevice 失败";

            IntPtr adapter;
            hr = Fn<GetAdapterFn>(dxgiDevice, 7)(dxgiDevice, out adapter);
            Release(dxgiDevice);
            if (hr < 0 || adapter == IntPtr.Zero) return "GetAdapter hr=0x" + ((uint)hr).ToString("X8");

            // 选包含桌面左上角(顶栏所在位置)的那个输出;拿不到就退回 0 号输出。
            IntPtr output = IntPtr.Zero;
            IntPtr fallback = IntPtr.Zero;
            var enumOutputs = Fn<EnumOutputsFn>(adapter, 7);
            for (uint i = 0; i < 8; i++)
            {
                IntPtr o;
                if (enumOutputs(adapter, i, out o) < 0 || o == IntPtr.Zero) break;
                if (fallback == IntPtr.Zero) fallback = o;
                else if (ContainsTopLeft(o)) { Release(fallback); fallback = IntPtr.Zero; output = o; break; }
                if (output == IntPtr.Zero) { output = o; break; }
            }
            Release(adapter);
            if (output == IntPtr.Zero) output = fallback;
            if (output == IntPtr.Zero) return "找不到显示器输出";

            IntPtr output1 = QueryInterface(output, IID_IDXGIOutput1);
            if (output1 == IntPtr.Zero) { Release(output); return "QI IDXGIOutput1 失败(系统太旧)"; }
            Release(output);
            hr = Fn<DuplicateOutputFn>(output1, 22)(output1, _device, out _duplication);
            Release(output1);
            if (hr < 0 || _duplication == IntPtr.Zero)
                return "DuplicateOutput hr=0x" + ((uint)hr).ToString("X8") + "(别的进程已占用/受保护内容)";

            IntPtr desc = Alloc(32, out _descBuf);
            hr = Fn<GetOutputDescFn>(_duplication, 7)(_duplication, desc);
            if (hr < 0) return "idup GetDesc hr=0x" + ((uint)hr).ToString("X8");
            // DXGI_OUTDUPL_DESC = { DXGI_MODE_DESC ModeDesc; DXGI_MODE_ROTATION Rotation; BOOL DesktopImageInSystemMemory; }
            // DXGI_MODE_DESC = Width(0) Height(4) RefreshRate.Num(8) RefreshRate.Denom(12) Format(16) ScanlineOrdering(20) Scaling(24)
            Width = Marshal.ReadInt32(desc, 0);
            Height = Marshal.ReadInt32(desc, 4);
            int format = Marshal.ReadInt32(desc, 16);
            int rotation = Marshal.ReadInt32(desc, 28);
            if (rotation != 1) return "显示器旋转 " + rotation + "(只支持不旋转)";
            if (format != DXGI_FORMAT_B8G8R8A8_UNORM) return "桌面格式 " + format + "(只支持 87 B8G8R8A8)";

            _descBuf = new byte[68];
            _stageDesc = Marshal.AllocHGlobal(68);
            _frameInfo = Marshal.AllocHGlobal(24);
            _mapBuf = Marshal.AllocHGlobal(24);
            _boxBuf = Marshal.AllocHGlobal(24);
            _texSlot = DetectTextureSlot();
            if (_texSlot < 0) return "探测 CreateTexture2D 槽位失败";
            return null;
        }

        private static bool ContainsTopLeft(IntPtr output)
        {
            IntPtr buf = Marshal.AllocHGlobal(96);
            try
            {
                if (Fn<GetOutputDescFn>(output, 6)(output, buf) < 0) return false;
                // DXGI_OUTPUT_DESC.DesktopCoordinates = RECT,紧跟 MONITORINFOEXW 之后?
                // 实际布局:Monitor(MONITORINFOEXW 104 字节)在前,DesktopCoordinates 在偏移 104。
                int left = Marshal.ReadInt32(buf, 104);
                int top = Marshal.ReadInt32(buf, 108);
                int right = Marshal.ReadInt32(buf, 112);
                int bottom = Marshal.ReadInt32(buf, 116);
                return left <= 0 && top <= 0 && right > 0 && bottom > 0;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// 探测「在这台机器上」CreateTexture2D 的 vtable 槽位:建一张暂存纹理,QI 确认是 2D 纹理,
        /// 再真的 Map 一次确认可读。规范顺序是 9,但本机实测是 5。
        /// </summary>
        private int DetectTextureSlot()
        {
            int[] candidates = new int[] { 5, 9 };
            for (int i = 0; i < candidates.Length; i++)
            {
                int slot = candidates[i];
                FillTextureDesc(2560, 108);
                IntPtr tex;
                int hr;
                try { hr = Fn<CreateTexture2DFn>(_device, slot)(_device, _stageDesc, IntPtr.Zero, out tex); }
                catch { continue; }
                if (hr < 0 || tex == IntPtr.Zero) continue;
                bool ok = false;
                if (QueryInterface(tex, IID_ID3D11Texture2D) != IntPtr.Zero)
                {
                    IntPtr map = Marshal.AllocHGlobal(24);
                    try { ok = Fn<MapFn>(_context, 14)(_context, tex, 0, D3D11_MAP_READ, 0, map) == 0; if (ok) Fn<UnmapFn>(_context, 15)(_context, tex, 0); }
                    catch { ok = false; }
                    finally { Marshal.FreeHGlobal(map); }
                }
                Release(tex);
                if (ok) return slot;
            }
            return -1;
        }

        private void FillTextureDesc(int width, int height)
        {
            for (int i = 0; i < 44; i++) Marshal.WriteByte(_stageDesc, i, 0);
            Marshal.WriteInt32(_stageDesc, 0, width);
            Marshal.WriteInt32(_stageDesc, 4, height);
            Marshal.WriteInt32(_stageDesc, 8, 1);                     // MipLevels
            Marshal.WriteInt32(_stageDesc, 12, 1);                    // ArraySize
            Marshal.WriteInt32(_stageDesc, 16, DXGI_FORMAT_B8G8R8A8_UNORM);
            Marshal.WriteInt32(_stageDesc, 20, 1);                    // SampleDesc.Count
            Marshal.WriteInt32(_stageDesc, 28, D3D11_USAGE_STAGING);
            Marshal.WriteInt32(_stageDesc, 36, (int)D3D11_CPU_ACCESS_READ);
        }

        /// <summary>等一帧。返回 0=拿到帧(调用方随后 CopyBand 并释放) 1=超时(桌面没变化) 2=会话失效需重建。</summary>
        public int Acquire(int timeoutMs)
        {
            if (_hasFrame) return 0;
            if (_duplication == IntPtr.Zero) return 2;
            int hr;
            try { hr = Fn<AcquireNextFrameFn>(_duplication, 8)(_duplication, (uint)Math.Max(0, timeoutMs), _frameInfo, out _resource); }
            catch { return 2; }
            if (hr == 0 && _resource != IntPtr.Zero) { _hasFrame = true; return 0; }
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) return 1;
            return 2;
        }

        /// <summary>把当前帧的矩形区域读回 dst(BGRA,无行填充)。必须紧跟 Acquire 成功之后调用。</summary>
        public bool CopyBand(int x, int y, int width, int height, byte[] dst)
        {
            if (!_hasFrame || _resource == IntPtr.Zero || dst == null) return false;
            if (width <= 0 || height <= 0 || width > Width || height > Height) return false;
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x + width > Width) width = Width - x;
            if (y + height > Height) height = Height - y;

            IntPtr src = QueryInterface(_resource, IID_ID3D11Texture2D);
            if (src == IntPtr.Zero) return false;
            bool ok = false;
            try
            {
                if (_stage == IntPtr.Zero || _stageW != width || _stageH != height)
                {
                    if (_stage != IntPtr.Zero) { Release(_stage); _stage = IntPtr.Zero; }
                    FillTextureDesc(width, height);
                    IntPtr tex;
                    if (Fn<CreateTexture2DFn>(_device, _texSlot)(_device, _stageDesc, IntPtr.Zero, out tex) >= 0 && tex != IntPtr.Zero)
                    {
                        _stage = tex;
                        _stageW = width;
                        _stageH = height;
                    }
                }
                if (_stage == IntPtr.Zero) return false;

                Marshal.WriteInt32(_boxBuf, 0, x);              // left
                Marshal.WriteInt32(_boxBuf, 4, y);              // top
                Marshal.WriteInt32(_boxBuf, 8, 0);              // front
                Marshal.WriteInt32(_boxBuf, 12, x + width);     // right
                Marshal.WriteInt32(_boxBuf, 16, y + height);    // bottom
                Marshal.WriteInt32(_boxBuf, 20, 1);             // back
                int hr = Fn<CopyFn>(_context, 46)(_context, _stage, 0, 0, 0, 0, src, 0, _boxBuf);
                if (hr < 0) return false;

                for (int i = 0; i < 24; i++) Marshal.WriteByte(_mapBuf, i, 0);
                hr = Fn<MapFn>(_context, 14)(_context, _stage, 0, D3D11_MAP_READ, 0, _mapBuf);
                if (hr < 0) return false;
                IntPtr data = Marshal.ReadIntPtr(_mapBuf);
                int pitch = Marshal.ReadInt32(_mapBuf, 8);
                int rowBytes = width * 4;
                if (dst.Length < rowBytes * height) return false;
                if (pitch == rowBytes)
                {
                    Marshal.Copy(data, dst, 0, rowBytes * height);
                }
                else
                {
                    for (int row = 0; row < height; row++)
                        Marshal.Copy(new IntPtr(data.ToInt64() + (long)row * pitch), dst, row * rowBytes, rowBytes);
                }
                Fn<UnmapFn>(_context, 15)(_context, _stage, 0);
                ok = true;
            }
            finally
            {
                Release(src);
            }
            return ok;
        }

        /// <summary>矩形区域是否在本帧的脏矩形里(桌面没动就返回 false,可整帧跳过)。</summary>
        public bool BandDirty(int x, int y, int width, int height)
        {
            if (!_hasFrame || _duplication == IntPtr.Zero) return true;
            IntPtr required = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(required, 0);
                int hr = Fn<GetFrameDirtyRectsFn>(_duplication, 9)(_duplication, 0, IntPtr.Zero, required);
                int need = Marshal.ReadInt32(required);
                if (hr != DXGI_ERROR_MORE_DATA || need <= 0) return true;
                if (_dirtyBuf == IntPtr.Zero || _dirtyBufSize < (uint)need)
                {
                    if (_dirtyBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_dirtyBuf); _dirtyBuf = IntPtr.Zero; }
                    _dirtyBuf = Marshal.AllocHGlobal(need);
                    _dirtyBufSize = (uint)need;
                }
                Marshal.WriteInt32(required, need);
                hr = Fn<GetFrameDirtyRectsFn>(_duplication, 9)(_duplication, (uint)need, _dirtyBuf, required);
                if (hr < 0) return true;
                int count = Marshal.ReadInt32(required) / 16;
                for (int i = 0; i < count; i++)
                {
                    int left = Marshal.ReadInt32(_dirtyBuf, i * 16);
                    int top = Marshal.ReadInt32(_dirtyBuf, i * 16 + 4);
                    int right = Marshal.ReadInt32(_dirtyBuf, i * 16 + 8);
                    int bottom = Marshal.ReadInt32(_dirtyBuf, i * 16 + 12);
                    if (left < x + width && right > x && top < y + height && bottom > y) return true;
                }
                return false;
            }
            catch { return true; }
            finally { Marshal.FreeHGlobal(required); }
        }

        public void ReleaseFrame()
        {
            if (!_hasFrame) return;
            _hasFrame = false;
            if (_resource != IntPtr.Zero) { Release(_resource); _resource = IntPtr.Zero; }
            try { if (_duplication != IntPtr.Zero) Fn<ReleaseFrameFn>(_duplication, 14)(_duplication); } catch { }
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

        private static IntPtr Alloc(int bytes, out byte[] keep)
        {
            keep = new byte[bytes];
            return Marshal.AllocHGlobal(bytes);
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
