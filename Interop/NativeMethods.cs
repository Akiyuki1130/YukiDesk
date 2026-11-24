using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DesktopBar.Interop
{
    internal static class NativeMethods
    {
        // ---------- 桌面层 Z 序控制 ----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
        private static extern IntPtr FindWindowW(string cls, string win);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        internal static void PositionBar(IntPtr hwnd, int x, int y, int width, int height)
            => SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOACTIVATE | SWP_NOZORDER);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        internal static void AllowActivation(IntPtr hwnd)
            => SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);

        internal static bool ForegroundMatches(System.Collections.Generic.List<string> names)
        {
            if (names.Count == 0) return false;
            string program = ForegroundProgramName();
            return names.Exists(name => string.Equals(System.IO.Path.GetFileNameWithoutExtension(name),
                program, StringComparison.OrdinalIgnoreCase));
        }

        internal static string ForegroundProgramName()
        {
            if (GetWindowThreadProcessId(GetForegroundWindow(), out uint processId) == 0) return null;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                return process.ProcessName;
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        internal const int WM_HOTKEY = 0x0312;
        internal const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        [DllImport("user32.dll")]
        private static extern bool GetPhysicalCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        internal static bool TryGetCursorPosition(out int x, out int y)
        {
            bool ok = GetPhysicalCursorPos(out var point);
            x = point.X;
            y = point.Y;
            return ok;
        }

        internal static int WindowBottom(IntPtr hwnd)
            => GetWindowRect(hwnd, out var rect) ? rect.Bottom : 0;

        internal static bool SetWindowTopmost(IntPtr hwnd, bool topmost)
            => SetWindowPos(hwnd, new IntPtr(topmost ? -1 : -2), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        public static IntPtr FindDesktopHost() => FindWindowW("Progman", null);

        /// <summary>加工具窗口 + 不激活扩展样式:点击顶栏不会抢焦点、不会跳到前台。</summary>
        public static void ApplyNoActivateToolStyle(IntPtr hwnd)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        public static void EnsureShown(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW))
                App.Log("EnsureShown 失败 error=" + Marshal.GetLastWin32Error());
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int n);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);

        private const uint GW_HWNDPREV = 3;
        private const uint GW_HWNDNEXT = 2;
        private const int WS_EX_TOOLWINDOW_ = 0x80;
        private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

        /// <summary>该窗口是否属于"桌面装饰层"(壁纸引擎/桌面整理软件画的桌面内容)。</summary>
        private static bool IsDesktopDecoration(IntPtr h)
        {
            var sb = new StringBuilder(128);
            GetClassNameW(h, sb, 128);
            string cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "SysListView32" || cls == "SHELLDLL_DefView"
                || cls.Contains("WinUIDesktopWin32WindowClass")) return true;
            int ex = GetWindowLong(h, -20);
            // 无重定向位图 + 工具窗 = DirectComposition 合成的透明桌面层(桌面挂件/壁纸)
            return (ex & WS_EX_NOREDIRECTIONBITMAP) != 0 && (ex & WS_EX_TOOLWINDOW_) != 0;
        }

        /// <summary>
        /// 把顶栏钉在"桌面层":越过壁纸/桌面整理软件画的装饰窗口,紧贴其上、
        /// 所有普通软件窗口之下。SWP_SHOWWINDOW 是关键:开机静默启动(不抢焦点)时
        /// 分层窗口的呈现表面可能一直不被合成,带此标志才能强制 DWM 开始显示。
        /// </summary>
        public static void PinAboveDesktop(IntPtr hwnd)
        {
            try
            {
                // 防御:上次浮出留下的置顶标志必须先撤掉,否则顶栏永远浮在所有软件之上,
                // 下面的"钉到桌面层"只是反复把装饰窗垫到它下面,看不到效果。
                if (IsTopmostFlagged(hwnd)) UnfloatTopmost(hwnd);
                const uint FLAGS = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW;
                IntPtr progman = FindDesktopHost();
                if (progman == IntPtr.Zero)
                {
                    App.Log("钉底跳过:未找到桌面宿主窗口");
                    return;
                }
                IntPtr above = progman;
                if (progman != IntPtr.Zero)
                {
                    // 从桌面向上走,跳过所有"桌面装饰层"窗口,把顶栏插到它们的正上方。
                    // 注意:IsWindowVisible 对被 DWM cloak(隐藏)的窗口仍返回 true,
                    // 必须用 DWMWA_CLOAKED 跳过,否则会提前停下、被壁纸/挂件层盖住。
                    IntPtr h = GetWindow(progman, GW_HWNDPREV);
                    int guard = 0;
                    while (h != IntPtr.Zero && h != hwnd && guard++ < 64)
                    {
                        int cloak;
                        DwmGetWindowAttribute(h, 14, out cloak, 4);
                        if (cloak != 0) { h = GetWindow(h, GW_HWNDPREV); continue; }
                        if (IsWindowVisible(h) && IsDesktopDecoration(h)) above = h;
                        else if (IsWindowVisible(h)) break;
                        h = GetWindow(h, GW_HWNDPREV);
                    }
                }
                // 关键性能闸门:SetWindowPos 打在分层窗口上会触发整窗重新呈现(实测每次约 50ms),
                // 3 秒一次就是一条常驻的 1% 单核开销。位置本来就对时直接跳过,只做只读的 Z 序检查。
                if (IsSittingAbove(hwnd, above)) return;
                // SetWindowPos 语义:第一个参数(被移动窗口)会被插到第二个参数的正下方。
                // 因此要把"最上层的装饰窗口"移到顶栏正下方,顶栏才能贴着桌面层、盖过壁纸/图标/挂件。
                bool moved = SetWindowPos(hwnd, above, 0, 0, 0, 0, FLAGS);
                if (!moved)
                    App.Log("钉底失败: SetWindowPos 返回 false error=" + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { App.Log("钉底失败: " + ex.Message); }
        }

        /// <summary>
        /// 顶栏是否已经"贴在桌面层之上":朝 Z 序底部方向走(GW_HWNDNEXT),
        /// 中间只允许出现"不可见 / 被 DWM cloak / 桌面装饰"窗口,最终碰到桌面宿主即视为已就位。
        /// 反方向也检查一小段(装饰窗口被插到顶栏上方的情形)。普通应用窗口在顶栏"上方"是设计语义,不参与判断。
        /// </summary>
        private static bool IsSittingAbove(IntPtr hwnd, IntPtr above)
        {
            if (hwnd == IntPtr.Zero || above == IntPtr.Zero) return false;
            IntPtr h = GetWindow(hwnd, GW_HWNDNEXT);
            int guard = 0;
            while (h != IntPtr.Zero && guard++ < 64)
            {
                if (h == above) return true;
                if (IsTransparentInZOrder(h)) { h = GetWindow(h, GW_HWNDNEXT); continue; }
                break;   // 中间夹了普通窗口:顶栏被垫高了,需要重钉
            }
            h = GetWindow(hwnd, GW_HWNDPREV);
            guard = 0;
            while (h != IntPtr.Zero && guard++ < 8)
            {
                if (h == above) return true;
                if (IsTransparentInZOrder(h)) { h = GetWindow(h, GW_HWNDPREV); continue; }
                break;
            }
            return false;
        }

        /// <summary>该窗口能否在 Z 序检查中被忽略(不可见 / 被 cloak / 桌面装饰层)。</summary>
        private static bool IsTransparentInZOrder(IntPtr h)
        {
            if (!IsWindowVisible(h)) return true;
            int cloak;
            DwmGetWindowAttribute(h, 14, out cloak, 4);
            if (cloak != 0) return true;
            return IsDesktopDecoration(h);
        }

        /// <summary>窗口是否带 WS_EX_TOPMOST 标志。</summary>
        private static bool IsTopmostFlagged(IntPtr hwnd)
            => hwnd != IntPtr.Zero && (GetWindowLong(hwnd, GWL_EXSTYLE) & 0x8) != 0;

        /// <summary>撤掉置顶标志落回普通层(钉底前的防御,防外部软件把顶栏置顶后赖在顶层)。</summary>
        private static void UnfloatTopmost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (!SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW))
                App.Log("撤顶失败 error=" + Marshal.GetLastWin32Error());
        }

        // ---------- 媒体键 ----------
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        private const uint KEYEVENTF_EXTENDEDKEY = 0x1;
        private const uint KEYEVENTF_KEYUP = 0x2;

        public const byte VK_MEDIA_PREV = 0xB1;
        public const byte VK_MEDIA_NEXT = 0xB0;
        public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

        public static void SendMediaKey(byte vk)
        {
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ShellExecuteW(IntPtr hwnd, string verb, string file, string args, string dir, int show);

        /// <summary>用系统默认方式打开链接或程序(参数化 API,不拼接命令行)。返回是否成功启动。</summary>
        public static bool ShellExecuteOpen(string target)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(target)) return false;
                IntPtr result = ShellExecuteW(IntPtr.Zero, "open", target, null, null, 5 /*SW_SHOW*/);
                long code = result.ToInt64();
                if (code <= 32)
                {
                    App.Log("Shell 打开失败 code=" + code + ": " + target);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                App.Log("Shell 打开异常: " + target + " — " + ex.Message);
                return false;
            }
        }

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint flags);

        internal static bool SelectItemInExplorer(string path)
        {
            if (string.IsNullOrWhiteSpace(path)
                || SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) < 0 || pidl == IntPtr.Zero)
                return false;
            try
            {
                return SHOpenFolderAndSelectItems(pidl, 0, null, 0) >= 0;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }

        // ---------- Shell 图标 ----------
        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_ICONLOCATION = 0x200;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO sfi, uint cb, uint flags);

        /// <summary>按指定尺寸提取图标(nIconSize 高16位=大图标)。</summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHDefExtractIcon(string iconFile, int iconIndex, uint flags,
            out IntPtr hiconLarge, out IntPtr hiconSmall, uint nIconSize);

        // ---------- Explorer 图标提取 ----------
        // 先从 Shell 的图像工厂读取桌面实际使用的高分辨率图标；失败时再走兼容回退。
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        private const uint SIIGBF_BIGGERSIZEOK = 0x1;
        private const uint SIIGBF_ICONONLY = 0x4;
        private const uint SIIGBF_SCALEUP = 0x100;
        private static readonly Guid ShellItemImageFactoryIid =
            new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");

        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(
            string path,
            IntPtr bindCtx,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public uint xHotspot, yHotspot;
            public IntPtr hbmMask, hbmColor;
        }

        [DllImport("user32.dll")]
        private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

        /// <summary>HICON → BitmapSource(绕开 .NET8 下 CreateBitmapSourceFromHIcon 的句柄校验 bug)。</summary>
        private static BitmapSource BitmapToSource(IntPtr hBitmap)
        {
            if (hBitmap == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { DeleteObject(hBitmap); }
        }

        internal static BitmapSource GetShellThumbnail(string path, int pixelSize, bool cacheOnly = false)
        {
            const uint thumbnailOnly = 0x08;
            const uint inCacheOnly = 0x10;
            return ReadShellImage(path, pixelSize, thumbnailOnly | (cacheOnly ? inCacheOnly : 0));
        }

        private static BitmapSource TryShellDesktopIcon(string path, int pixelSize)
            => ReadShellImage(path, pixelSize, SIIGBF_ICONONLY | SIIGBF_SCALEUP);

        private static BitmapSource ReadShellImage(string path, int pixelSize, uint flags)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) && !Directory.Exists(path)) return null;
            IShellItemImageFactory factory = null;
            try
            {
                var iid = ShellItemImageFactoryIid;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null)
                    return null;
                int pixels = Math.Clamp(pixelSize, 48, 512);
                var size = new SIZE { cx = pixels, cy = pixels };
                int hr = factory.GetImage(size, SIIGBF_BIGGERSIZEOK | flags, out IntPtr bitmap);
                return hr == 0 ? BitmapToSource(bitmap) : null;
            }
            catch { return null; }
            finally { if (factory != null) Marshal.ReleaseComObject(factory); }
        }

        private static BitmapSource IconToSource(IntPtr hIcon)
        {
            if (!GetIconInfo(hIcon, out ICONINFO ii)) return null;
            try
            {
                if (ii.hbmColor == IntPtr.Zero) return null;
                var src = Imaging.CreateBitmapSourceFromHBitmap(ii.hbmColor, IntPtr.Zero,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            }
        }

        /// <summary>验证 HICON 在本进程是否真的可用(此环境下 shell 工厂句柄可能无效)。</summary>
        private static bool IconUsable(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero) return false;
            bool ok = GetIconInfo(hIcon, out ICONINFO ii);
            if (ok)
            {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            }
            return ok;
        }

        private static BitmapSource TryExtract(string iconFile, int index, int pixelSize)
        {
            try
            {
                pixelSize = Math.Max(48, Math.Min(256, pixelSize));
                uint iconSize = unchecked((uint)(pixelSize << 16));
                int hr = SHDefExtractIcon(iconFile, index, 0, out IntPtr big, out IntPtr small, iconSize);
                if ((hr != 0 && hr != 1) || big == IntPtr.Zero || !IconUsable(big))
                {
                    if (big != IntPtr.Zero) DestroyIcon(big);
                    if (small != IntPtr.Zero) DestroyIcon(small);
                    return null;
                }
                var src = IconToSource(big);
                DestroyIcon(big);
                if (small != IntPtr.Zero) DestroyIcon(small);
                return src;
            }
            catch { return null; }
        }

        /// <summary>提取 48px 系统(资源管理器)图标。本机 shell 的 IShellItemImageFactory /
        /// 部分图标 API 返回无效句柄,故只用 SHGetFileInfo 关联 + SHDefExtractIcon 直提。</summary>
        public static BitmapSource GetFileIcon(string path, bool isDir, int pixelSize = 96)
        {
            try
            {
                // 1) 优先读取 Explorer 桌面实际使用的高分辨率图标。
                var desktopIcon = TryShellDesktopIcon(path, pixelSize);
                if (desktopIcon != null) return desktopIcon;

                // 2) 兼容路径：按真实文件请求 Shell 图标，覆盖 .lnk/.exe/.msi 和普通文件。
                var sfi = new SHFILEINFO();
                uint attr = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                IntPtr r = SHGetFileInfo(path, attr, ref sfi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICONLOCATION);
                if (r != IntPtr.Zero && !string.IsNullOrEmpty(sfi.szDisplayName))
                {
                    var src = TryExtract(sfi.szDisplayName, sfi.iIcon, pixelSize);
                    if (src != null) return src;
                }

                // 3) 直接取 Shell 返回的实际 HICON。
                sfi = new SHFILEINFO();
                r = SHGetFileInfo(path, attr, ref sfi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON);
                if (r != IntPtr.Zero && sfi.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        if (IconUsable(sfi.hIcon))
                        {
                            var src = IconToSource(sfi.hIcon);
                            if (src != null) return src;
                        }
                    }
                    finally { DestroyIcon(sfi.hIcon); }
                }

                // 3) 类型默认图标:文件夹 / 通用文件
                return GetSystemTypeIcon(isDir, pixelSize);
            }
            catch { return null; }
        }

        /// <summary>取 imageres.dll 的系统类型图标（文件夹/通用文件），始终是 Explorer 同款 Windows 图标。</summary>
        public static BitmapSource GetSystemTypeIcon(bool isDir, int pixelSize)
        {
            string sys = Path.Combine(Environment.SystemDirectory, "imageres.dll");
            var icon = TryExtract(sys, isDir ? 3 : 2, pixelSize);
            if (icon != null) return icon;
            string shell32 = Path.Combine(Environment.SystemDirectory, "shell32.dll");
            return TryExtract(shell32, isDir ? 3 : 1, pixelSize);
        }

        // ---------- 桌面工作区预留 ----------
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint flags);
        [DllImport("shell32.dll", SetLastError = true)]
        private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
        // 系统级 DPI:不需要任何 DC,任意线程都能安全调。
        // 云母烘焙跑在后台线程,绝不能用 Graphics.FromHwnd(NULL) 拿 DPI——
        // 那是 GDI+ 且要共享桌面 DC,后台线程上和 UI 线程的 GDI+ 并发会直接 AV(GPP64.dll)。
        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
        private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

        /// <summary>主屏逻辑像素宽(任意线程安全,替代 SystemParameters.PrimaryScreenWidth)。</summary>
        internal static int ScreenWidth() => GetSystemMetrics(SM_CXSCREEN);

        /// <summary>主屏逻辑像素高(任意线程安全,替代 SystemParameters.PrimaryScreenHeight)。</summary>
        internal static int ScreenHeight() => GetSystemMetrics(SM_CYSCREEN);

        /// <summary>系统 DPI 缩放(96=100%)。Win10 1607 之前没有这个 API,退回 1.0。</summary>
        internal static double SystemDpiScale()
        {
            try { uint d = GetDpiForSystem(); return d > 0 ? d / 96.0 : 1.0; }
            catch { return 1.0; }
        }

        // ---------- 实时玻璃:GDI 条带捕获 / WDA 排除 / 遮挡探测 ----------
        // 配方来自液态玻璃根因报告(§9.12.1)与 glasslayer 探针:
        // GetDC(NULL) + CreateDIBSection(top-down) + BitBlt(SRCCOPY) 只抓顶栏那一条;
        // 自己的窗口必须先设 WDA_EXCLUDEFROMCAPTURE 排除,否则玻璃会把顶栏自己拍进去(正反馈)。
        // BitBlt 不写 alpha → 上层承载必须用 Bgr32,不能用 Bgra32(会把未定义字节当透明度)。

        internal const uint WDA_NONE = 0x00000000;
        internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const uint SRCCOPY = 0x00CC0020;
        private const uint GA_ROOT = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        // DeleteObject 复用本文件 30 行的既有声明(圆角区域/图标代码在用),不要重复定义。
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int maxCount);

        /// <summary>把窗口从屏幕捕获中排除(WDA_EXCLUDEFROMCAPTURE)。需要 Win10 2004+,失败返回 false 由调用方降级。</summary>
        internal static bool ExcludeFromCapture(IntPtr hwnd)
        {
            try { return hwnd != IntPtr.Zero && SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); }
            catch { return false; }
        }

        /// <summary>恢复窗口参与屏幕捕获(玻璃关闭时还原,保证截图里顶栏可见)。</summary>
        internal static bool RestoreCapture(IntPtr hwnd)
        {
            try { return hwnd != IntPtr.Zero && SetWindowDisplayAffinity(hwnd, WDA_NONE); }
            catch { return false; }
        }

        /// <summary>某点命中的根窗口(物理像素坐标);给玻璃做"顶栏是否被盖住"的遮挡探测。</summary>
        internal static IntPtr RootWindowAt(int x, int y)
        {
            try
            {
                var h = WindowFromPoint(new POINT { X = x, Y = y });
                if (h == IntPtr.Zero) return IntPtr.Zero;
                var root = GetAncestor(h, GA_ROOT);
                return root == IntPtr.Zero ? h : root;
            }
            catch { return IntPtr.Zero; }
        }

        internal static bool IsCoveredByApplication(IntPtr hwnd, int x, int y)
        {
            var root = RootWindowAt(x, y);
            return root != IntPtr.Zero && root != hwnd && !IsDesktopDecoration(root);
        }

        /// <summary>窗口类名(遮挡探测判定桌面层窗口用)。</summary>
        internal static string WindowClassOf(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                return GetClassName(hwnd, sb, 256) > 0 ? sb.ToString() : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 创建 top-down Bgr32 的 DIB 捕获条带。返回内存 DC 与位图指针;
        /// biHeight 传负值保证行序与 WriteableBitmap.WritePixels 一致,这个不能改。
        /// </summary>
        internal static bool CreateStripCapture(int width, int height,
            out IntPtr screenDc, out IntPtr memoryDc, out IntPtr dib, out IntPtr bits)
        {
            screenDc = memoryDc = dib = bits = IntPtr.Zero;
            try
            {
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return false;
                memoryDc = CreateCompatibleDC(screenDc);
                if (memoryDc == IntPtr.Zero) return false;
                var bmi = new BITMAPINFOHEADER
                {
                    biSize = 40u,
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                };
                dib = CreateDIBSection(screenDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
                SelectObject(memoryDc, dib);
                return true;
            }
            catch { return false; }
        }

        // ---------- 合成节拍(dwmapi / gdi32) ----------

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int index);

        /// <summary>
        /// 阻塞到下一次 DWM 合成(等价于等一次 vsync,约一个屏幕刷新周期)。
        /// 玻璃的「不限制帧率」模式用它给捕获定节拍:合成一结束立刻抓屏,抓完(约 5ms)
        /// 还赶得上这一轮的渲染提交,端到端只延迟一帧;顺便避免忙等烧 CPU。
        /// 合成被禁用(DWM 关闭/远程会话)时返回 false,调用方退回普通 Sleep。
        /// </summary>
        internal static bool WaitComposition()
        {
            try { return DwmFlush() == 0; } catch { return false; }
        }

        /// <summary>显示器刷新率(Hz);拿不到返回 0。</summary>
        internal static int ScreenRefreshHz()
        {
            IntPtr dc = IntPtr.Zero;
            try
            {
                dc = GetDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) return 0;
                int hz = GetDeviceCaps(dc, 116);   // VREFRESH
                return hz > 1 ? hz : 0;
            }
            catch { return 0; }
            finally { if (dc != IntPtr.Zero) { try { ReleaseDC(IntPtr.Zero, dc); } catch { } } }
        }

        internal static void DestroyStripCapture(ref IntPtr screenDc, ref IntPtr memoryDc, ref IntPtr dib)
        {
            try { if (dib != IntPtr.Zero) DeleteObject(dib); } catch { }
            try { if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc); } catch { }
            try { if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc); } catch { }
            screenDc = memoryDc = dib = IntPtr.Zero;
        }

        /// <summary>BitBlt 屏幕任意矩形到内存 DC(条带/面板共用)。不加 CAPTUREBLT(会带来光标闪烁)。</summary>
        internal static bool BlitStrip(IntPtr memoryDc, int width, int height, IntPtr screenDc, int srcX, int srcY)
        {
            try { return BitBlt(memoryDc, 0, 0, width, height, screenDc, srcX, srcY, SRCCOPY); }
            catch { return false; }
        }

        // ---------- 液态玻璃:运行时编译 HLSL(ps_3_0) ----------
        // 不想引入 fxc 构建依赖也不引 NuGet,就用系统自带 d3dcompiler_47 在首次使用时编译,
        // 字节码由调用方落盘缓存(以源码哈希命名,lgsrc 库踩过的坑:缓存名必须跟内容绑定)。

        [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ID3DBlob
        {
            // 必须 PreserveSig:ID3DBlob 的这两个方法在原生侧没有 HRESULT 返回,
            // 不标 PreserveSig 时 CLR 会按 "HRESULT + retval" 解释签名,
            // 结果是返回值(指针/长度)全变成垃圾——踩过:指针恒为 0,数据全丢。
            [PreserveSig] IntPtr GetBufferPointer();
            [PreserveSig] UIntPtr GetBufferSize();
        }

        [DllImport("d3dcompiler_47.dll")]
        private static extern int D3DCompile(byte[] srcData, int srcDataSize, string sourceName,
            IntPtr defines, IntPtr include, string entryPoint, string target, uint flags1, uint flags2,
            out ID3DBlob code, out ID3DBlob errorMsgs);

        /// <summary>编译一段 HLSL 到 ps_3_0(target 可换 ps_2_0 做降级实验)。失败抛异常(带编译器错误文本)。</summary>
        internal static byte[] CompilePs(string hlsl, string target = "ps_3_0")
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(hlsl);
            ID3DBlob code = null, errs = null;
            try
            {
                int hr = D3DCompile(bytes, bytes.Length, "liquid.hlsl", IntPtr.Zero, IntPtr.Zero,
                    "main", target, 0, 0, out code, out errs);
                if (hr < 0 || code == null)
                {
                    string msg = "hr=0x" + hr.ToString("X");
                    try
                    {
                        if (errs != null)
                        {
                            var p = errs.GetBufferPointer();
                            var n = (long)errs.GetBufferSize().ToUInt64();
                            if (p != IntPtr.Zero && n > 0)
                            {
                                var b = new byte[n];
                                Marshal.Copy(p, b, 0, (int)n);
                                msg += " " + System.Text.Encoding.UTF8.GetString(b);
                            }
                        }
                    }
                    catch { }
                    throw new InvalidOperationException("HLSL 编译失败: " + msg);
                }
                var len = (int)code.GetBufferSize().ToUInt64();
                var result = new byte[len];
                Marshal.Copy(code.GetBufferPointer(), result, 0, len);
                return result;
            }
            finally
            {
                try { if (code != null) Marshal.ReleaseComObject(code); } catch { }
                try { if (errs != null) Marshal.ReleaseComObject(errs); } catch { }
            }
        }

        private const uint SPI_GETWORKAREA = 0x0030;
        private const uint SPI_SETWORKAREA = 0x002F;
        private const uint SPIF_SENDCHANGE = 0x02;
        private const uint ABM_NEW = 0x00000000;
        private const uint ABM_REMOVE = 0x00000001;
        private const uint ABM_QUERYPOS = 0x00000002;
        private const uint ABM_SETPOS = 0x00000003;
        private const uint ABE_TOP = 1;
        private static RECT _originalWorkArea;
        private static bool _workAreaCaptured;
        private static IntPtr _appBarWindow;
        private static bool _appBarRegistered;

        public static bool SetDesktopTopReserve(IntPtr hwnd, int height)
        {
            if (hwnd == IntPtr.Zero) return false;
            height = Math.Max(1, height);
            try
            {
                if (_appBarRegistered && _appBarWindow != hwnd)
                    RemoveDesktopAppBar();

                // APPBARDATA.rc 契约是物理像素:height 参数与 SystemParameters 都是 DIP,
                // 必须按目标窗口 DPI 换算,否则高缩放下预留区宽度/高度不足。
                double dpi = 96;
                try { dpi = GetDpiForWindow(hwnd); } catch { if (dpi <= 0) dpi = 96; }
                if (dpi <= 0) dpi = 96;
                double scale = dpi / 96.0;
                var data = new APPBARDATA
                {
                    cbSize = Marshal.SizeOf<APPBARDATA>(),
                    hWnd = hwnd,
                    uEdge = ABE_TOP,
                    rc = new RECT
                    {
                        Left = 0,
                        Top = 0,
                        Right = (int)Math.Ceiling(SystemParameters.PrimaryScreenWidth * scale),
                        Bottom = (int)Math.Ceiling(height * scale)
                    }
                };

                if (!_appBarRegistered)
                {
                    uint added = (uint)SHAppBarMessage(ABM_NEW, ref data);
                    if (added == 0)
                    {
                        App.Log("注册桌面 AppBar 失败 error=" + Marshal.GetLastWin32Error());
                        return SetDesktopTopReserveFallback(height);
                    }
                    _appBarRegistered = true;
                    _appBarWindow = hwnd;
                }

                uint queried = (uint)SHAppBarMessage(ABM_QUERYPOS, ref data);
                if (queried == 0)
                {
                    App.Log("查询桌面 AppBar 位置失败 error=" + Marshal.GetLastWin32Error());
                    RemoveDesktopAppBar();
                    return SetDesktopTopReserveFallback(height);
                }
                data.rc.Bottom = data.rc.Top + (int)Math.Ceiling(height * scale);
                uint positioned = (uint)SHAppBarMessage(ABM_SETPOS, ref data);
                if (positioned == 0)
                {
                    App.Log("设置桌面 AppBar 位置失败 error=" + Marshal.GetLastWin32Error());
                    RemoveDesktopAppBar();
                    return SetDesktopTopReserveFallback(height);
                }

                int width = Math.Max(1, data.rc.Right - data.rc.Left);
                int actualHeight = Math.Max(1, data.rc.Bottom - data.rc.Top);
                bool positionedWindow = SetWindowPos(hwnd, IntPtr.Zero, data.rc.Left, data.rc.Top, width, actualHeight,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                if (!positionedWindow)
                {
                    App.Log("定位桌面 AppBar 窗口失败 error=" + Marshal.GetLastWin32Error());
                    return false;
                }
                App.Log("桌面 AppBar 已注册 hwnd=" + hwnd + " rect=" +
                    data.rc.Left + "," + data.rc.Top + "," + data.rc.Right + "," + data.rc.Bottom);
                return true;
            }
            catch (Exception ex)
            {
                App.Log("设置桌面 AppBar 失败: " + ex.Message);
                RemoveDesktopAppBar();
                return SetDesktopTopReserveFallback(height);
            }
        }

        private static bool SetDesktopTopReserveFallback(int height)
        {
            try
            {
                var r = new RECT();
                if (!_workAreaCaptured && SystemParametersInfo(SPI_GETWORKAREA, 0, ref r, 0))
                {
                    _originalWorkArea = r;
                    _workAreaCaptured = true;
                }
                if (!_workAreaCaptured) return false;
                r = _originalWorkArea;
                r.Top = Math.Min(r.Bottom - 100, r.Top + Math.Max(0, height));
                bool ok = SystemParametersInfo(SPI_SETWORKAREA, 0, ref r, SPIF_SENDCHANGE);
                App.Log("桌面空间兼容回退 result=" + ok + " error=" + Marshal.GetLastWin32Error());
                return ok;
            }
            catch (Exception ex) { App.Log("设置桌面顶部空间失败: " + ex.Message); return false; }
        }

        public static void RemoveDesktopAppBar()
        {
            try
            {
                if (_appBarRegistered && _appBarWindow != IntPtr.Zero)
                {
                    var data = new APPBARDATA
                    {
                        cbSize = Marshal.SizeOf<APPBARDATA>(),
                        hWnd = _appBarWindow
                    };
                    uint removed = (uint)SHAppBarMessage(ABM_REMOVE, ref data);
                    App.Log("桌面 AppBar 已移除 result=" + removed);
                }
            }
            catch (Exception ex) { App.Log("移除桌面 AppBar 失败: " + ex.Message); }
            finally { _appBarRegistered = false; _appBarWindow = IntPtr.Zero; }
        }

        public static void RestoreDesktopWorkArea()
        {
            RemoveDesktopAppBar();
            try
            {
                if (_workAreaCaptured)
                {
                    var r = _originalWorkArea;
                    bool ok = SystemParametersInfo(SPI_SETWORKAREA, 0, ref r, SPIF_SENDCHANGE);
                    App.Log("恢复桌面工作区 result=" + ok + " error=" + Marshal.GetLastWin32Error());
                    _workAreaCaptured = false;
                }
            }
            catch (Exception ex) { App.Log("恢复桌面工作区失败: " + ex.Message); }
        }

        // ---------- 系统操作 ----------
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ExitWindowsEx(uint flags, uint reason);
        [DllImport("user32.dll")]
        private static extern bool LockWorkStation();
        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
        private const uint EWX_LOGOFF = 0x00000000;
        private const uint EWX_SHUTDOWN = 0x00000001;
        private const uint EWX_REBOOT = 0x00000002;
        private const uint EWX_POWEROFF = 0x00000008;
        // SE_SHUTDOWN_NAME 在交互令牌中"存在但禁用",不先启用直接调 ExitWindowsEx 必然失败(ERROR_PRIVILEGE_NOT_HELD)。
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string systemName, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previous, IntPtr returnLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        // C 侧 LUID 按 4 字节对齐(LUID 实为两个 DWORD):Pack=4 使 Luid 落在偏移 4、
        // Attributes 在偏移 12、总长 16,与原生布局一致;默认打包会把 long 对齐到偏移 8,
        // API 读到垃圾 LUID,AdjustTokenPrivileges 会报 ERROR_NOT_ALL_ASSIGNED。
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public long Luid;
            public int Attributes;
        }
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const int SE_PRIVILEGE_ENABLED = 0x00000002;
        private const int ERROR_NOT_ALL_ASSIGNED = 1300;

        /// <summary>启用当前进程令牌的 SE_SHUTDOWN_NAME 特权;返回是否已启用(或本就持有)。</summary>
        public static bool EnableShutdownPrivilege()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                        TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                {
                    App.Log("启用关机特权失败: OpenProcessToken err=" + Marshal.GetLastWin32Error());
                    return false;
                }
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out long luid))
                {
                    App.Log("启用关机特权失败: LookupPrivilegeValue err=" + Marshal.GetLastWin32Error());
                    return false;
                }
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    App.Log("启用关机特权失败: AdjustTokenPrivileges err=" + Marshal.GetLastWin32Error());
                    return false;
                }
                if (Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED)
                {
                    App.Log("启用关机特权失败: 令牌未持有 SeShutdownPrivilege");
                    return false;
                }
                return true;
            }
            finally { if (token != IntPtr.Zero) CloseHandle(token); }
        }

        public static bool Shutdown()
        {
            if (!EnableShutdownPrivilege()) return false;
            return ExitWindowsEx(EWX_SHUTDOWN | EWX_POWEROFF, 0);
        }
        public static bool Restart()
        {
            if (!EnableShutdownPrivilege()) return false;
            return ExitWindowsEx(EWX_REBOOT, 0);
        }
        public static bool Sleep() => SetSuspendState(false, false, false);
        public static bool Lock() => LockWorkStation();
        public static bool DisplayOff()
        {
            try
            {
                // HWND_BROADCAST 的同步 SendMessage 会被任一挂起窗口卡住 UI 线程,必须用带超时的广播。
                IntPtr res = SendMessageTimeout((IntPtr)0xffff, 0x0112, (IntPtr)0xF170, (IntPtr)2,
                    SMTO_ABORTIFHUNG, 2000, out IntPtr _);
                return res != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                App.Log("关闭显示器失败: " + ex.Message);
                return false;
            }
        }
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);

        // ---------- Authenticode 签名校验(提权执行下载物前的安全闸) ----------

        /// <summary>查询文件 Authenticode 签名状态字符串(Valid/NotSigned/HashMismatch 等);
        /// 文件不存在、PowerShell 不可用或超时等"无法判定"的情况返回 null,与明确的坏状态区分。
        /// 经由 PowerShell Get-AuthenticodeSignature(底层同为 WinVerifyTrust):
        /// 实测直接 P/Invoke WinVerifyTrust 在部分宿主进程内会被干扰返回 TRUST_E_NOSIGNATURE,
        /// 而该查询在相同环境下结果正确。
        /// 路径由本应用生成(随机名/固定安装器名),不含用户可控引号。</summary>
        public static string GetAuthenticodeStatus(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            try
            {
                string escaped = filePath.Replace("'", "''");
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"(Get-AuthenticodeSignature -LiteralPath '" + escaped + "').Status\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(20000);
                if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;
                return output.Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>校验文件 Authenticode 签名可信(仅 Valid 视为通过);用于提权执行下载物前的硬闸。
        /// 仅在用户点击安装 PawnIO 时执行一次(每次查询要起一个 PowerShell 进程)。</summary>
        public static bool VerifyAuthenticodeSignature(string filePath)
        {
            string status = GetAuthenticodeStatus(filePath);
            bool valid = string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase);
            if (!valid) App.Log("签名校验未通过: " + (status ?? "无法判定"));
            return valid;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>当前线程的 Win32 线程 id(与 Process.Threads[i].Id 同一空间,性能诊断里用来对号入座)。</summary>
        public static int CurrentThreadId()
        {
            try { return (int)GetCurrentThreadId(); }
            catch { return 0; }
        }
    }
}
