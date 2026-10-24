using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using DesktopBar.Interop;   // NativeMethods:屏幕尺寸/系统 DPI(烘焙线程也要用,必须无线程亲和)
using DesktopBar.UI;   // Ui.Scale

namespace DesktopBar.Services
{
    /// <summary>
    /// 自绘胶囊材质(云母 Mica / 亚克力 Acrylic)烘焙器。
    /// 两种材质走同一条烘焙管线,只是配方不同:云母压平成接近单色的石材质感,
    /// 亚克力保留更多色彩、更磨砂、更透明,并多一层随明暗主题的提亮/压暗混合。
    ///
    /// 为什么能自绘:云母的本质不是"模糊背后的窗口",而是
    /// 「采样桌面壁纸 → 重模糊 → 亮度/色调映射 → 叠细噪点 → 跟随明暗主题」。
    /// 它只依赖壁纸,不依赖背后是什么窗口,所以可以用已知内容复现——
    /// 零屏幕捕获、零每帧开销、任意胶囊形状(主报告 §9.22.5 / §9.23.8)。
    ///
    /// 成本特征:一次性烘焙成一张 <see cref="BitmapSource"/>,静止时 0 重绘;
    /// 与液态玻璃"每帧采样背景"完全不是一个量级。
    ///
    /// 位图坐标系:横向覆盖整块主屏物理宽度,纵向覆盖
    /// [-<see cref="Bleed"/>, 顶栏物理高度 + <see cref="Bleed"/>]。
    /// 上缘留出血量是为了让重模糊能从屏幕外把内容带进来。
    /// 于是"胶囊在屏幕上的物理矩形"可以直接当 <see cref="ImageBrush.Viewbox"/> 用。
    /// </summary>
    public static class MicaService
    {
        /// <summary>壁纸解码边长上限。云母反正要重模糊,不需要原图分辨率。</summary>
        private const int MaxDecodeEdge = 2048;

        // 烘焙链路诊断开关:YUKI_MICA_TRACE=1 时把"派发→完成→失败"打到控制台。
        // 顶栏宿主在这个环境里写不了 %AppData%\YukiDesk\log.txt(子进程写盘被沙箱拦),
        // 日志整体失效,所以排障只能靠标准输出。
        private static void Tr(string s)
        {
            try { if (Environment.GetEnvironmentVariable("YUKI_MICA_TRACE") == "1") Console.WriteLine("[mica] " + s); }
            catch { }
        }
        /// <summary>盒式模糊遍数。3 遍盒式 ≈ 高斯,足以骗过眼睛且是 O(n)。</summary>
        private const int BoxPasses = 3;
        /// <summary>亮度压带后的最小/最大目标亮度,防止用户主题色把云母压成纯黑或纯白。</summary>
        private const double TargetMin = 0.06;
        private const double TargetMax = 0.96;
        /// <summary>噪点随机数种子。固定种子保证同一壁纸每次烘焙结果一致(缓存可比对)。</summary>
        private const int NoiseSeed = 0x5EED;

        private static readonly object Gate = new object();
        private static bool _started;
        private static bool _stopped;
        private static int _generation;
        private static string _signature = "";
        private static BitmapSource _backdrop;
        private static Color _average = Color.FromRgb(0x14, 0x14, 0x1C);
        private static bool _dark = true;
        private static double _bleed;
        private static UserPreferenceChangedEventHandler _pref;
        private static Timer _debounce;

        /// <summary>烘焙结果代次。每次重算 +1,UI 用它判断画笔要不要重建。</summary>
        public static int Generation { get { lock (Gate) return _generation; } }

        /// <summary>云母位图;null = 还没算好或算失败(调用方应回退到纯色)。</summary>
        public static BitmapSource Backdrop { get { lock (Gate) return _backdrop; } }

        /// <summary>云母平均色。展开页用平涂近似(展开页会伸到顶栏条带之外,没有壁纸可取样)。</summary>
        public static Color Average { get { lock (Gate) return _average; } }

        /// <summary>当前是否是深色云母。</summary>
        public static bool IsDark { get { lock (Gate) return _dark; } }

        /// <summary>位图上缘之外的物理像素数(模糊出血)。</summary>
        public static double Bleed { get { lock (Gate) return _bleed; } }

        /// <summary>是否有任何材质在使用云母。都没用时完全不烘焙,零开销。</summary>
        public static bool Needed => (App.Settings?.PillMaterial ?? 0) != 0;

        /// <summary>当前生效材质:0=纯色 1=云母 2=亚克力。</summary>
        public static int Material => App.Settings?.PillMaterial ?? 0;

        /// <summary>
        /// 当前材质的不透明度。云母/亚克力各有独立设置项;
        /// 胶囊画笔与展开页平涂都从这取,保证两个入口只有一个真相源。
        /// </summary>
        public static double BackdropOpacity
        {
            get
            {
                var s = App.Settings;
                if (s == null) return 0.86;
                double v = s.PillMaterial == 2 ? s.AcrylicOpacity : s.MicaOpacity;
                return Math.Max(0.05, Math.Min(1, v));
            }
        }

        /// <summary>重算完成时在 UI 线程触发。</summary>
        public static event Action Changed;

        public static void Start()
        {
            if (_started || _stopped) return;
            _started = true;
            _pref = OnUserPreferenceChanged;
            try { SystemEvents.UserPreferenceChanged += _pref; } catch { }
            // 首次同步算一次:让顶栏第一帧就带上云母,而不是先闪一下纯色再跳成云母。
            // 与 ThemeService 首次同步算壁纸亮度是同一个取舍(启动路径,仅一次)。
            if (Needed) BakeOnce();
        }

        public static void Stop()
        {
            _stopped = true;
            if (_pref != null)
            {
                try { SystemEvents.UserPreferenceChanged -= _pref; } catch { }
                _pref = null;
            }
            _debounce?.Dispose();
            _debounce = null;
            var d = _bakeDispatcher;
            _bakeDispatcher = null;
            _bakeThread = null;
            if (d != null && !d.HasShutdownStarted)
            {
                try { d.BeginInvoke(new Action(() => d.InvokeShutdown())); } catch { }
            }
            Changed = null;
        }

        /// <summary>请求重算。签名没变则只通知 UI 重建画笔(比如只改了对不透明度)。</summary>
        public static void Invalidate()
        {
            if (_stopped || !Needed) return;
            // 烘焙必须跑在"有 Dispatcher 的 STA 线程"上,不能跑线程池。
            // Bake() 里要建 BitmapDecoder / TransformedBitmap / FormatConvertedBitmap /
            // WriteableBitmap,它们全是 DispatcherObject;线程池线程没有 Dispatcher,
            // 在上面创建直接 AV——实测同一次操作连崩两次:
            // 13:19:54 崩在 GPP64.dll(GDI+,Graphics.FromHwnd 与 UI 线程并发)、
            // 13:19:57 崩在 coreclr.dll(WPF 成像对象建在线程池线程),都是 c0000005。
            var d = EnsureBakeDispatcher();
            Tr("Invalidate: 烘焙线程=" + (d == null ? "null" : d.Thread.ManagedThreadId.ToString()));
            if (d == null || d.HasShutdownStarted || d.HasShutdownFinished)
            {
                // 烘焙线程起不来时退回 **UI 线程**,不是线程池:线程池没有 Dispatcher,
                // WPF 成像对象建在上面就是 13:19 那两次 c0000005(GPP64 + coreclr)。
                // UI 线程只是把这一次烘焙拖慢约 0.3 秒,不会崩。
                var ui = Application.Current?.Dispatcher;
                if (ui != null && !ui.HasShutdownStarted && !ui.HasShutdownFinished)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        try { BakeOnce(); }
                        catch (Exception ex) { App.Log("云母重算失败: " + ex.Message); }
                    }));
                    return;
                }
                // 连 Application.Current 都没有(无界面自检进程),此时本来就在调用者线程上,直接用。
                try { BakeOnce(); }
                catch (Exception ex) { App.Log("云母重算失败: " + ex.Message); }
                return;
            }
            try
            {
                d.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        string sig = ComputeSignature();
                        bool same;
                        lock (Gate) same = string.Equals(sig, _signature, StringComparison.Ordinal);
                        if (same)
                        {
                            RaiseChanged();
                            return;
                        }
                        BakeOnce();
                    }
                    catch (Exception ex) { App.Log("云母重算失败: " + ex.Message); }
                }));
            }
            catch (Exception ex) { App.Log("云母烘焙派发失败: " + ex.Message); }
        }

        private static Thread _bakeThread;
        private static Dispatcher _bakeDispatcher;

        /// <summary>
        /// 烘焙专用线程:STA + 常驻 Dispatcher.Run()。
        /// 这是让 WPF 成像对象能在后台线程安全创建的唯一姿势——
        /// 线程必须真的在跑消息循环,否则 DispatcherObject 内部要等的东西永远等不到。
        /// 整个进程只建一次,退出时由 <see cref="Stop"/> 关掉。
        /// </summary>
        private static Dispatcher EnsureBakeDispatcher()
        {
            if (_bakeDispatcher != null) return _bakeDispatcher;
            lock (Gate)
            {
                if (_bakeDispatcher != null) return _bakeDispatcher;
                try
                {
                    var t = new Thread(() =>
                    {
                        try
                        {
                            var d0 = Dispatcher.CurrentDispatcher;   // 懒建,并装上同步上下文
                            _bakeDispatcher = d0;
                            Dispatcher.Run();
                        }
                        catch { }
                    });
                    t.IsBackground = true;
                    t.SetApartmentState(ApartmentState.STA);
                    t.Name = "YukiDesk.MicaBake";
                    t.Start();
                    _bakeThread = t;

                    // 等 Dispatcher 就绪再返回,否则 BeginInvoke 会打到 null 上。
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (_bakeDispatcher == null && sw.ElapsedMilliseconds < 3000) Thread.Sleep(1);
                }
                catch (Exception ex)
                {
                    App.Log("云母烘焙线程创建失败,退回线程池: " + ex.Message);
                }
                return _bakeDispatcher;
            }
        }

        /// <summary>自检用的尺寸诊断。三种口径都要能对上,对不上就是 Viewbox 错位的根源。</summary>
        public static string Diag()
        {
            return "SystemParameters(DIP)=" + SystemParameters.PrimaryScreenWidth + "x" + SystemParameters.PrimaryScreenHeight
                 + " GetSystemMetrics(物理)=" + NativeMethods.ScreenWidth() + "x" + NativeMethods.ScreenHeight()
                 + " GetDpiForSystem=" + NativeMethods.SystemDpiScale().ToString("0.000")
                 + " Ui.Scale=" + Ui.Scale;
        }

        /// <summary>
        /// 同步烘焙一次。正常路径走后台线程;无界面自检(--micadump)直接调它。
        /// </summary>
        public static bool BakeOnce()
        {
            try
            {
                string sig = ComputeSignature();
                Baked b = Bake(sig);
                if (b == null)
                {
                    lock (Gate) _signature = sig;
                    return false;
                }
                Publish(b, sig);
                Tr("BakeOnce 完成: 线程=" + Thread.CurrentThread.ManagedThreadId
                   + " 位图=" + b.Bitmap.PixelWidth + "x" + b.Bitmap.PixelHeight);
                return true;
            }
            catch (Exception ex)
            {
                App.Log("云母烘焙失败: " + ex.Message);
                return false;
            }
        }

        // ---------------------------------------------------------------- 几何

        /// <summary>
        /// 取某个视觉元素在云母位图上的取样矩形(物理像素)。
        /// 元素必须已经连上呈现源,否则返回 Empty。
        /// </summary>
        public static Rect ViewboxFor(Visual anchor)
        {
            var bmp = Backdrop;
            if (bmp == null || anchor == null) return Rect.Empty;
            var fe = anchor as FrameworkElement;
            if (fe == null || fe.ActualWidth < 1 || fe.ActualHeight < 1) return Rect.Empty;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(anchor);
                // PointToScreen 返回 DIP;胶囊在屏幕上的物理矩形正好就是位图坐标,
                // 因为位图横向铺满主屏、纵向从 -Bleed 起算。
                var pt = anchor.PointToScreen(new Point(0, 0));
                double x = pt.X * dpi.DpiScaleX;
                double y = pt.Y * dpi.DpiScaleY + Bleed;
                return new Rect(x, y, fe.ActualWidth * dpi.DpiScaleX, fe.ActualHeight * dpi.DpiScaleY);
            }
            catch { return Rect.Empty; }
        }

        /// <summary>按给定取样矩形与不透明度建一支背景画笔。</summary>
        public static ImageBrush CreateBrush(Rect viewbox, double opacity)
        {
            var bmp = Backdrop;
            if (bmp == null) return null;
            return new ImageBrush(bmp)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = viewbox,
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0, 0, 1, 1),
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                Opacity = opacity
            };
        }

        // ---------------------------------------------------------------- 烘焙

        private sealed class Baked
        {
            public BitmapSource Bitmap;
            public Color Average;
            public bool Dark;
            public double Bleed;
        }

        private static string ComputeSignature()
        {
            var s = App.Settings;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                string path = key?.GetValue("WallPaper") as string ?? "";
                string style = key?.GetValue("WallpaperStyle") as string ?? "10";
                string tile = key?.GetValue("TileWallpaper") as string ?? "0";
                string stamp = "";
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists) stamp = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
                }
                catch { }
                // 签名要覆盖所有会影响像素的输入;纯不透明度不算(它在画笔层生效)。
                // 材质档也必须进签名:云母↔亚克力切换要按新配方重烘焙位图,
                // 否则签名不变 → 只 RaiseChanged 不重算,亚克力会一直显示云母的旧图。
                return string.Join("|",
                    s?.PillMaterial ?? 0,
                    path, style, tile, stamp,
                    s?.MicaBlurRadius ?? 28, s?.MicaContrast ?? 0.30, s?.MicaSaturation ?? 0.35,
                    s?.MicaTint ?? 0.18, s?.MicaNoise ?? 0.035,
                    s?.AcrylicBlurRadius ?? 44, s?.AcrylicContrast ?? 0.55, s?.AcrylicSaturation ?? 0.75,
                    s?.AcrylicTint ?? 0.24, s?.AcrylicNoise ?? 0.05,
                    ThemeService.Background, ThemeService.Text,
                    NativeMethods.ScreenWidth(), Ui.Scale,
                    DpiScaleX(), DpiScaleY());
            }
            catch { return "err"; }
        }

        private static double DpiScaleX()
        {
            // 纯 Win32,任意线程安全。
            // 这里绝不能写 Graphics.FromHwnd(NULL)——那是 GDI+ 且要共享桌面 DC,
            // 在烘焙线程上和 UI 线程的 GDI+ 并发会直接 AV(GPP64.dll,实测 13:19:54 崩溃)。
            return NativeMethods.SystemDpiScale();
        }

        private static double DpiScaleY()
        {
            return NativeMethods.SystemDpiScale();
        }

        private static Baked Bake(string signature)
        {
            var s = App.Settings;
            if (s == null) return null;

            // ---- 1. 读壁纸路径与适配方式 ----
            string path = "";
            int style = 10;
            bool tile = false;
            int ox = 0, oy = 0;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
            {
                if (key != null)
                {
                    path = key.GetValue("WallPaper") as string ?? "";
                    int.TryParse(key.GetValue("WallpaperStyle") as string ?? "10", out style);
                    tile = (key.GetValue("TileWallpaper") as string ?? "0") == "1";
                    int.TryParse(key.GetValue("WallpaperOriginX") as string ?? "0", out ox);
                    int.TryParse(key.GetValue("WallpaperOriginY") as string ?? "0", out oy);
                }
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                App.Log("云母:找不到壁纸文件,回退纯色。" + path);
                return null;
            }
            // style 0 = 平铺;TileWallpaper=1 也表示平铺
            if (style == 0) tile = true;

            // ---- 2. 解码壁纸 ----
            int ww, wh;
            byte[] wp;
            try
            {
                var dec = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = dec.Frames[0];
                ww = frame.PixelWidth;
                wh = frame.PixelHeight;
                if (ww < 1 || wh < 1) return null;
                BitmapSource src = frame;
                if (Math.Max(ww, wh) > MaxDecodeEdge)
                {
                    double k = (double)MaxDecodeEdge / Math.Max(ww, wh);
                    src = new TransformedBitmap(frame, new ScaleTransform(k, k));
                    ww = Math.Max(1, (int)Math.Round(ww * k));
                    wh = Math.Max(1, (int)Math.Round(wh * k));
                }
                // 统一成 32bpp;壁纸不透明,四种 32bpp 格式的 RGB 完全一致。
                var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int stride = ww * 4;
                wp = new byte[stride * wh];
                conv.CopyPixels(wp, stride, 0);
            }
            catch (Exception ex)
            {
                App.Log("云母:壁纸解码失败(" + path + "): " + ex.Message);
                return null;
            }

            // ---- 3. 几何:位图覆盖 [-bleed, 顶栏高+bleed] ----
            // 注意:GetSystemMetrics 返回的就是【物理像素】,不能再乘 DPI 缩放。
            // 换成 SystemParameters.PrimaryScreenWidth 的话那个是 DIP(1/96 英寸),才要乘 dsx——
            // 两者混用会把位图宽度放大 1.5 倍(实测 3840 而非 2560),Viewbox 会整体错位。
            bool acrylic = s.PillMaterial == 2;
            double blurRadius = acrylic ? s.AcrylicBlurRadius : s.MicaBlurRadius;
            double dsy = DpiScaleY();
            double physW = NativeMethods.ScreenWidth();
            double barPhysH = 46.0 * Ui.Scale * dsy;
            double blurPhys = Math.Max(1, blurRadius * dsy);
            double bleed = Math.Ceiling(blurPhys) + 8;
            int bw = (int)Math.Ceiling(physW);
            int bh = (int)Math.Ceiling(barPhysH) + (int)bleed * 2;
            if (bw < 8 || bh < 8) return null;

            // ---- 4. 采样壁纸 → 顶条缓冲 ----
            byte[] strip = new byte[bw * bh * 4];
            for (int by = 0; by < bh; by++)
            {
                // 物理屏幕 y;-bleed 以上(屏幕外) clamp 到 0,等价于复制壁纸顶行
                double sy = by - bleed;
                if (sy < 0) sy = 0;
                int row = by * bw * 4;
                for (int bx = 0; bx < bw; bx++)
                {
                    if (!MapWallpaper(style, tile, ox, oy, physW, NativeMethods.ScreenHeight(),
                            ww, wh, bx, sy, out double wx, out double wy))
                    {
                        // 壁纸之外:用桌面背景色(深色桌默认接近黑)
                        strip[row + bx * 4 + 0] = 0;
                        strip[row + bx * 4 + 1] = 0;
                        strip[row + bx * 4 + 2] = 0;
                    }
                    else
                    {
                        int ix = (int)wx;
                        int iy = (int)wy;
                        if (ix >= ww) ix = ww - 1;
                        if (iy >= wh) iy = wh - 1;
                        int si = (iy * ww + ix) * 4;
                        strip[row + bx * 4 + 0] = wp[si + 0];
                        strip[row + bx * 4 + 1] = wp[si + 1];
                        strip[row + bx * 4 + 2] = wp[si + 2];
                    }
                    strip[row + bx * 4 + 3] = 255;
                }
            }

            // ---- 5. 重模糊(3 遍分离盒式 ≈ 高斯) ----
            byte[] tmp = new byte[strip.Length];
            for (int i = 0; i < BoxPasses; i++)
            {
                BoxBlurH(strip, tmp, bw, bh, (int)Math.Round(blurPhys));
                BoxBlurV(tmp, strip, bw, bh, (int)Math.Round(blurPhys));
            }

            // ---- 6. 亮度压带 + 主题染色 + 噪点 ----
            var tint = ParseColor(ThemeService.Background);
            double tintLuma = Luma(tint);
            double target = Math.Max(TargetMin, Math.Min(TargetMax, tintLuma));
            double contrast = Math.Max(0, Math.Min(1, acrylic ? s.AcrylicContrast : s.MicaContrast));
            // 真实 Mica 接近单色:先按保留下来的彩度把壁纸色相压淡,再做亮度压带,
            // 否则彩色壁纸会烘焙出鲜艳的纯色块(实测默认 Win11 壁纸蓝通道飙到 253)。
            // 亚克力相反,它就是要保留壁纸色彩,所以默认彩度高得多。
            double satKeep = Math.Max(0, Math.Min(1, acrylic ? s.AcrylicSaturation : s.MicaSaturation));
            double tintA = Math.Max(0, Math.Min(1, acrylic ? s.AcrylicTint : s.MicaTint));
            double noiseAmp = Math.Max(0, Math.Min(0.2, acrylic ? s.AcrylicNoise : s.MicaNoise)) * 255.0;
            // 亚克里的 luminosity 层:浅色主题整体混白、深色主题混黑,得到磨砂玻璃的
            // "奶白/烟熏"底色。云母不做这步(它要压平成接近单色的石材质感),
            // 这是两种材质观感差异的分水岭之一。
            double veilA = acrylic ? 0.08 : 0;
            double veilC = tintLuma >= 0.5 ? 255.0 : 0.0;

            // 只统计顶栏实际覆盖的那几行,平均值才对得上眼睛看到的东西。
            // 注意位图行号要比屏幕物理 y 多一个 bleed(位图从 -bleed 起算),
            // 早期漏了偏移,统计到的是屏幕外那一段。
            int y0 = (int)bleed + (int)Math.Round(4.0 * Ui.Scale * dsy);
            int y1 = (int)bleed + (int)Math.Round(46.0 * Ui.Scale * dsy);
            double sum = 0;
            long cnt = 0;
            for (int by = y0; by < y1 && by < bh; by++)
            {
                int row = by * bw * 4;
                for (int bx = 0; bx < bw; bx += 2)
                {
                    sum += Luma(strip[row + bx * 4 + 2], strip[row + bx * 4 + 1], strip[row + bx * 4 + 0]);
                    cnt++;
                }
            }
            double avg = cnt > 0 ? sum / cnt : target;
            // 诊断:色调映射前,位图左/中/右三段的均值。用来判断"采样+模糊"这段对不对。
            App.Log(string.Format("云母映射前: 左={0} 中={1} 右={2}",
                ThirdMean(strip, bw, bh, 0), ThirdMean(strip, bw, bh, 1), ThirdMean(strip, bw, bh, 2)));
            // 诊断用:这些量直接决定材质的明暗,出问题时必须能一眼看出来。
            App.Log(string.Format("材质烘焙({0}): target={1:F4} avg={2:F4} contrast={3:F2} tint=#{4:X2}{5:X2}{6:X2} y0={7} y1={8} bleed={9} bw={10} bh={11}",
                acrylic ? "acrylic" : "mica", target, avg, contrast, tint.R, tint.G, tint.B, y0, y1, bleed, bw, bh));

            var rnd = new Random(NoiseSeed);
            double accR = 0, accG = 0, accB = 0;
            for (int by = 0; by < bh; by++)
            {
                int row = by * bw * 4;
                for (int bx = 0; bx < bw; bx++)
                {
                    int i = row + bx * 4;
                    double r = strip[i + 2], g = strip[i + 1], b = strip[i + 0];
                    double l = Luma(r, g, b);
                    // 注意量纲:Luma 返回 0..1,而 r/g/b 是 0..255,灰度值必须乘回来。
                    // 早期版本漏了这一步,结果整张云母被压暗约 2.4 倍。
                    double gray = l * 255.0;
                    r = gray + (r - gray) * satKeep;
                    g = gray + (g - gray) * satKeep;
                    b = gray + (b - gray) * satKeep;
                    // 把亮度拉进目标带,但保留色相:按比例缩放 RGB
                    double nl = target + (l - avg) * contrast;
                    if (nl < 0) nl = 0;
                    if (nl > 1) nl = 1;
                    double k = l > 0.0001 ? nl / l : 0;
                    if (k > 4) k = 4;
                    r *= k; g *= k; b *= k;
                    // 主题染色
                    r += (tint.R - r) * tintA;
                    g += (tint.G - g) * tintA;
                    b += (tint.B - b) * tintA;
                    // 亚克力 luminosity 层(见上)
                    if (veilA > 0)
                    {
                        r += (veilC - r) * veilA;
                        g += (veilC - g) * veilA;
                        b += (veilC - b) * veilA;
                    }
                    // 细噪点(云母与"纯模糊"最大的观感区别之一)
                    if (noiseAmp > 0)
                    {
                        r += (rnd.NextDouble() * 2 - 1) * noiseAmp;
                        g += (rnd.NextDouble() * 2 - 1) * noiseAmp;
                        b += (rnd.NextDouble() * 2 - 1) * noiseAmp;
                    }
                    strip[i + 2] = Clamp8(r);
                    strip[i + 1] = Clamp8(g);
                    strip[i + 0] = Clamp8(b);
                    accR += strip[i + 2]; accG += strip[i + 1]; accB += strip[i + 0];
                }
            }

            var bmp = new WriteableBitmap(bw, bh, 96, 96, PixelFormats.Pbgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, bw, bh), strip, bw * 4, 0);
            bmp.Freeze();
            long total = (long)bw * bh;
            var average = Color.FromRgb(Clamp8(accR / total), Clamp8(accG / total), Clamp8(accB / total));

            return new Baked
            {
                Bitmap = bmp,
                Average = average,
                Dark = Luma(average) < 0.5,
                Bleed = bleed
            };
        }

        /// <summary>壁纸适配方式 → 屏幕物理坐标反查壁纸像素坐标。</summary>
        private static bool MapWallpaper(int style, bool tile, int ox, int oy,
            double sw, double sh, int ww, int wh, double sx, double sy,
            out double wx, out double wy)
        {
            double kx, ky, dx, dy;
            switch (style)
            {
                case 2: // Stretch:两轴独立缩放铺满
                    kx = sw / ww; ky = sh / wh; dx = 0; dy = 0; break;
                case 6: // Fit:等比完整可见,居中
                    kx = ky = Math.Min(sw / ww, sh / wh);
                    dx = (sw - ww * kx) / 2; dy = (sh - wh * ky) / 2; break;
                case 1: // Center:1:1 居中
                    kx = ky = 1; dx = (sw - ww) / 2; dy = (sh - wh) / 2; break;
                case 0: // Tile:1:1 从 origin 平铺
                    kx = ky = 1; dx = ox; dy = oy; break;
                case 10: // Fill:等比铺满,居中裁切(默认)
                default:
                    kx = ky = Math.Max(sw / ww, sh / wh);
                    dx = (sw - ww * kx) / 2; dy = (sh - wh * ky) / 2; break;
            }
            wx = (sx - dx) / kx;
            wy = (sy - dy) / ky;
            if (tile)
            {
                wx = ((wx % ww) + ww) % ww;
                wy = ((wy % wh) + wh) % wh;
                return true;
            }
            return wx >= 0 && wy >= 0 && wx < ww && wy < wh;
        }

        // ---------------------------------------------------------------- 模糊

        /// <summary>水平盒式模糊。滑窗 + 边界钳制,单行 O(w)。</summary>
        private static void BoxBlurH(byte[] s, byte[] d, int w, int h, int r)
        {
            if (r < 1) { Array.Copy(s, d, s.Length); return; }
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int c = 0; c < 4; c++)
                {
                    int lo = 0, hi = Math.Min(r, w - 1);
                    int sum = 0;
                    for (int i = lo; i <= hi; i++) sum += s[row + i * 4 + c];
                    for (int x = 0; x < w; x++)
                    {
                        int nlo = x - r; if (nlo < 0) nlo = 0;
                        int nhi = x + r; if (nhi > w - 1) nhi = w - 1;
                        while (hi < nhi) { hi++; sum += s[row + hi * 4 + c]; }
                        while (lo < nlo) { sum -= s[row + lo * 4 + c]; lo++; }
                        while (hi > nhi) { sum -= s[row + hi * 4 + c]; hi--; }
                        while (lo > nlo) { lo--; sum += s[row + lo * 4 + c]; }
                        d[row + x * 4 + c] = (byte)(sum / (nhi - nlo + 1));
                    }
                }
            }
        }

        /// <summary>垂直盒式模糊。同上,沿列走。</summary>
        private static void BoxBlurV(byte[] s, byte[] d, int w, int h, int r)
        {
            if (r < 1) { Array.Copy(s, d, s.Length); return; }
            int stride = w * 4;
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int p = x * 4 + c;
                    int lo = 0, hi = Math.Min(r, h - 1);
                    int sum = 0;
                    for (int i = lo; i <= hi; i++) sum += s[i * stride + p];
                    for (int y = 0; y < h; y++)
                    {
                        int nlo = y - r; if (nlo < 0) nlo = 0;
                        int nhi = y + r; if (nhi > h - 1) nhi = h - 1;
                        while (hi < nhi) { hi++; sum += s[hi * stride + p]; }
                        while (lo < nlo) { sum -= s[lo * stride + p]; lo++; }
                        while (hi > nhi) { sum -= s[hi * stride + p]; hi--; }
                        while (lo > nlo) { lo--; sum += s[lo * stride + p]; }
                        d[y * stride + p] = (byte)(sum / (nhi - nlo + 1));
                    }
                }
            }
        }

        // ---------------------------------------------------------------- 发布

        private static void Publish(Baked b, string signature)
        {
            lock (Gate)
            {
                _backdrop = b.Bitmap;
                _average = b.Average;
                _dark = b.Dark;
                _bleed = b.Bleed;
                _signature = signature;
                _generation++;
            }
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.HasShutdownStarted || d.HasShutdownFinished) return;
            if (d.CheckAccess()) Changed?.Invoke();
            else
            {
                try { d.BeginInvoke(new Action(() => Changed?.Invoke())); }
                catch { }
            }
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // 换壁纸/改壁纸适配方式/改系统主题都会走这里。防抖 800ms:
            // 一次换壁纸可能连带十几次偏好变化事件,没必要每次全算。
            if (_debounce == null)
            {
                _debounce = new Timer(_ => Invalidate(), null, Timeout.Infinite, Timeout.Infinite);
            }
            try { _debounce.Change(800, Timeout.Infinite); }
            catch { }
        }

        // ---------------------------------------------------------------- 工具

        private static Color ParseColor(string hex)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return Color.FromRgb(0x14, 0x14, 0x1C);
                var c = (Color)ColorConverter.ConvertFromString(hex);
                return Color.FromRgb(c.R, c.G, c.B);
            }
            catch { return Color.FromRgb(0x14, 0x14, 0x1C); }
        }


        // 诊断用:位图第 idx 段(0/1/2)的 RGB 均值。
        private static string ThirdMean(byte[] px, int w, int h, int idx)
        {
            long r = 0, g = 0, b = 0, n = 0;
            int x0 = w * idx / 3, x1 = w * (idx + 1) / 3;
            for (int y = 0; y < h; y += 3)
            {
                int row = y * w * 4;
                for (int x = x0; x < x1; x += 3)
                {
                    int i = row + x * 4;
                    r += px[i + 2]; g += px[i + 1]; b += px[i + 0]; n++;
                }
            }
            return n == 0 ? "n/a" : string.Format("({0:F1},{1:F1},{2:F2})",
                (double)r / n, (double)g / n, (double)b / n);
        }

        // 参数用 double:调用点传进来的是已经乘过比例系数的中间值。
        private static double Luma(double r, double g, double b)
            => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;

        private static double Luma(Color c)
            => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        private static byte Clamp8(double v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (byte)v;
        }
    }
}
