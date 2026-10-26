using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopBar.Interop;
using DesktopBar.UI;

namespace DesktopBar.Services
{
    /// <summary>
    /// 实时背景玻璃(PillMaterial=3):捕获顶栏背后的真实屏幕内容(窗口+桌面图标+壁纸),
    /// 高斯模糊后按胶囊并集铺在主窗口最底层——Apple/小米式"真·毛玻璃"。悬停展开页同理。
    ///
    /// 性能设计(实测教训:WPF BlurEffect 铺满 2560 宽条带会把渲染线程打满一个核):
    ///   - 模糊全部在捕获线程用"半分辨率 + 滑动和三趟盒式模糊(O(n),与半径无关)"完成,
    ///     WPF 侧只剩一张普通位图,时钟 250ms/频谱 60fps 的局部重绘不再触发任何模糊重算;
    ///   - 降采样差分门控:静止桌面 0 写屏;遮挡(全屏窗口盖住顶栏)时 0 采样;
    ///   - 展开页玻璃是一次性捕获(打开时抓一张、模糊、烘焙染色),静止零开销。
    ///
    /// 捕获配方(根因报告 §9.12.1):GetDC(NULL)+CreateDIBSection(top-down)+BitBlt(SRCCOPY);
    /// 自家窗口(主窗+面板)全部 WDA_EXCLUDEFROMCAPTURE 排除,否则玻璃把自己拍进去(正反馈);
    /// BitBlt 不写 alpha → 全程 Bgr32。WriteableBitmap 只在 UI 线程创建与写入(DispatcherObject 亲和)。
    /// </summary>
    public static class GlassService
    {
        /// <summary>WDA 生效等待(ms)。lgwda 探针实测 300-400ms,取 600 余量。</summary>
        private const long HoldMs = 600;
        /// <summary>差分发布阈值(平均绝对差 0~255)。低于它视为静止,不写屏。</summary>
        private const double DiffThreshold = 1.0;
        // 小幅漂移阈值(高于"静止"门限但不必满帧发布):介于两者之间时最多 10fps。
        private const double SmallDiffThreshold = 8.0;

        private sealed class GlassSpec
        {
            public int Width;            // 条带物理宽(偶数,方便对半降采样)
            public int Height;           // 条带物理高(偶数)
            public int HalfWidth;
            public int HalfHeight;
            public int BlurHalf;         // 半分辨率域的盒式半径
            public int FpsMs;            // 捕获基础间隔
            public int MidY;             // 遮挡探测采样点的物理 y(胶囊中线)
            public double ImageHeightDip;
        }

        private static readonly object Gate = new object();
        private static readonly HashSet<IntPtr> _excluded = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, long> _wdaReady = new Dictionary<IntPtr, long>();
        private static double ReadEnvDouble(string name, double fallback)
        {
            double v;
            if (double.TryParse(Environment.GetEnvironmentVariable(name), out v)) return v;
            return fallback;
        }

        private static double ReadBakeGain()
        {
            double v;
            if (double.TryParse(Environment.GetEnvironmentVariable("YUKI_PANEL_BAKE_GAIN"), out v) && v > 0) return v;
            return 1.0;   // 与胶囊完全同参数(带宽/位移/rim/色散),面板和胶囊才是同一种材质
        }

        private static bool _panelGlassLogged;
        // 诊断:A/B 面板液态烘焙。YUKI_PANEL_NOBAKE=1 关掉烘焙(只剩模糊),YUKI_PANEL_BAKE_GAIN 缩放强度。
        private static readonly bool _panelNoBake = Environment.GetEnvironmentVariable("YUKI_PANEL_NOBAKE") == "1";
        private static readonly double _panelBakeGain = ReadBakeGain();
        // 折射带宽度:与胶囊的 HLSL 完全一致(6 半分辨率像素),材质才是同一种;
        // 面板面积大,带太宽会变成一圈"光雾",太窄又没有玻璃厚度。
        private static readonly double _panelBand = ReadEnvDouble("YUKI_PANEL_BAND", 6.0);
        // 斜面明暗(方向光高光/背光压暗):胶囊与面板共用一个强度,保证是同一种材质。
        private static readonly double LiquidSheen = ReadEnvDouble("YUKI_LIQUID_SHEEN", 1.0);
        private static readonly double _panelSheen = ReadEnvDouble("YUKI_PANEL_SHEEN", LiquidSheen);
        private static volatile GlassSpec _spec;

        private static bool _started, _stopped, _enabled, _active = true, _stopThread;
        private static long _holdUntil;

        // ---- 诊断/取证 ----(双用途:YUKI_GLASS_DUMP 与 --glassshot;常开计数开销可忽略)
        /// <summary>取证抓图期间暂停采样:抓图必须先恢复 WDA 让顶栏可被捕获,
        /// 若捕获线程同时还在 BitBlt,就会把自己拍进去形成正反馈。暂停即"冻结当前帧"。</summary>
        private static volatile bool _shotPause;
        internal static long StatFrames;        // 捕获循环轮数(含静止跳过)
        internal static long StatBlits;         // 实际 BitBlt 次数
        internal static long StatPublishes;     // 判定有变化并发布给 UI 的次数
        internal static long StatBusyTicks;     // BitBlt+降采样+模糊的累计 Stopwatch 刻度
        internal static long StatPumps;         // UI 泵 tick 次数(30fps 定时器)
        internal static long StatWrites;        // 实际 WritePixels 到模糊位图的次数
        internal static long StatSharpWrites;   // 实际 WritePixels 到未模糊位图的次数(仅液态玻璃)
        // 分阶段耗时(Stopwatch 刻度):定位"4~5ms 一帧到底花在哪"
        internal static long StatBlitTicks, StatDownTicks, StatDiffTicks, StatBlurTicks;
        internal static bool HasFrame { get { return _latest != null; } }
        internal static int LatestWidth { get { return _latestW; } }
        internal static int LatestHeight { get { return _latestH; } }

        private static Thread _thread;
        private static readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);

        // 发布区(捕获线程写,UI 线程读,全部在 Gate 下)。轮换池 3 块:
        // UI 线程 WritePixels 拷贝(~0.5ms)与捕获线程复用之间至少隔 2 帧,杜绝撕裂。
        private static readonly byte[][] _rotate = new byte[3][];
        // 未模糊帧也走三块轮换池:着色器轮廓折射要用它(见 LiquidGlassEffect.Sharp)。
        private static readonly byte[][] _rotateSharp = new byte[3][];
        private static int _rotateIndex;
        private static byte[] _latest, _consumed, _latestSharp;
        private static int _latestW, _latestH;

        // 玻璃层三件套(UI 线程),跨 ApplySettings 全量重建复用,避免重建瞬间空一帧
        private static Grid _host;
        private static Image _image;
        private static Border _tint;
        private static WriteableBitmap _wb, _wbSharp;
        // UI 泵由"发布"驱动:捕获线程发布新帧后才 BeginInvoke 一次(合并去重)。
        // 曾经是 30fps 常开 DispatcherTimer,静止桌面下每拍都唤醒 UI 线程纯属浪费(实测约 0.5% 单核)。
        private static int _pumpQueued;
        // 液态玻璃着色器(材质=4;编译失败为 null 降级普通玻璃)与胶囊矩形缓存(DIP)
        private static LiquidGlassEffect _liquid;
        private static ImageBrush _liquidBrush, _sharpBrush;
        // 取证开关(YUKI_GLASS_TESTSRC=1):不写捕获帧,改把一张"已知内容"的合成图案写进位图。
        // 用来区分"采样通道拿不到位图内容"与"位图内容本身不对"。
        private static bool _testSrc;
        private static List<Rect> _pillRectsDip;

        /// <summary>当前材质是否走玻璃管线(3=高斯玻璃 4=液态玻璃,共用同一条捕获)。</summary>
        private static bool IsGlass => App.Settings?.PillMaterial == 3 || App.Settings?.PillMaterial == 4;

        // 捕获线程的静止计数(自适应降频:静止越久采样越稀)
        private static int _staticRun;
        private static long _lastPubTick;   // 上次发布的时刻(TickCount64),用于小幅漂移限流
        // 诊断开关:跳过静止差分门控,强制每帧发布(=模拟"顶栏背后一直在动"的最坏情况,如动态壁纸/视频)。
        private static bool _alwaysPub;

        /// <summary>App 启动时调用:只订阅事件,不开线程。真正的开销由 EnsureHost(材质=3)驱动。</summary>
        internal static void Start()
        {
            _testSrc = Environment.GetEnvironmentVariable("YUKI_GLASS_TESTSRC") == "1";
            _alwaysPub = Environment.GetEnvironmentVariable("YUKI_GLASS_ALWAYS_PUB") == "1";
            if (_started || _stopped) return;
            _started = true;
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged; } catch { }
            // 取证开关:玻璃的窗口被 WDA 排除后任何外部截图都看不到顶栏,
            // 必须有进程内自证手段(输入帧 PNG + 主窗可视树 RTB)。
            var dd = Environment.GetEnvironmentVariable("YUKI_GLASS_DUMP");
            if (!string.IsNullOrWhiteSpace(dd)) { _dumpDir = dd; _dumpFrames = 0; }
        }

        private static string _dumpDir;
        private static int _dumpFrames = -1;

        /// <summary>取证:暂停采样并把所有自家窗口恢复为"可被屏幕捕获"(抓图前提)。</summary>
        internal static void BeginShoot()
        {
            _shotPause = true;
            RestoreExclusionAll();
        }

        /// <summary>取证:抓完图重新排除自家窗口并恢复采样。</summary>
        internal static void EndShoot()
        {
            ApplyExclusionAll();
            _shotPause = false;
            _wake.Set();
        }

        /// <summary>诊断快照:一行人类可读状态,写进日志/取证目录。</summary>
        internal static string DiagnosticSnapshot()
        {
            var s = App.Settings;
            var spec = _spec;
            double busyMs = StatBusyTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return string.Format(
                "材质={0} 启用={1} 采样规格={2}x{3}(半分辨率 {4}x{5}) 捕获帧={6} 发布={7} BitBlt={8} 累计忙={9:F1}ms 液态着色器={10} 帧率上限={11} 泵tick={12} 写帧={13} 写锐帧={14}",
                s == null ? -1 : s.PillMaterial, _enabled,
                spec == null ? 0 : spec.Width, spec == null ? 0 : spec.Height,
                _latestW, _latestH,
                Interlocked.Read(ref StatFrames), Interlocked.Read(ref StatPublishes),
                Interlocked.Read(ref StatBlits), busyMs,
                _liquid == null ? "无(降级普通玻璃)" : "已启用",
                spec == null ? 0 : Math.Round(1000.0 / Math.Max(1, spec.FpsMs)),
                Interlocked.Read(ref StatPumps), Interlocked.Read(ref StatWrites), Interlocked.Read(ref StatSharpWrites));
        }

        /// <summary>取证:把当前发布的玻璃输入帧(半分辨率模糊结果)另存为 PNG。</summary>
        internal static bool SaveLatestPng(string path)
        {
            byte[] f; int w, h;
            lock (Gate) { f = _latest; w = _latestW; h = _latestH; }
            if (f == null || w <= 0 || h <= 0) return false;
            try
            {
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, f, w * 4);
                bmp.Freeze();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(path)) enc.Save(fs);
                return true;
            }
            catch { return false; }
        }

        /// <summary>当前胶囊并集矩形(DIP,仅用于取证日志核对几何换算)。</summary>
        internal static List<Rect> DiagnosticPillRects { get { return _pillRectsDip; } }

        internal static void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
            Interlocked.Exchange(ref _pumpQueued, 0);
            _enabled = false;
            _stopThread = true;
            _wake.Set();
            try { _thread?.Join(1200); } catch { }
            RestoreExclusionAll();
            try { if (_host != null && _host.Parent is Panel p) p.Children.Remove(_host); } catch { }
            _host = null; _image = null; _tint = null; _wb = null; _thread = null;
            Interlocked.Exchange(ref _pumpQueued, 0);
        }

        /// <summary>
        /// BuildLayout 每次重建时调用:材质=3 时返回缓存的玻璃层元素(调用方加到 root 最底),
        /// 否则摘除玻璃层并停捕获。返回 null 表示玻璃未启用。
        /// </summary>
        internal static FrameworkElement EnsureHost()
        {
            if (_stopped) return null;
            var ui = Application.Current?.Dispatcher;
            if (ui == null) return null;
            if (!ui.CheckAccess()) return null; // BuildLayout 永远在 UI 线程;越线调用无法挂树,直接放弃
            var s = App.Settings;
            if (s == null || (s.PillMaterial != 3 && s.PillMaterial != 4))
            {
                try { if (_host != null && _host.Parent is Panel p) p.Children.Remove(_host); } catch { }
                SetEnabled(false);
                return null;
            }
            if (_host == null) BuildElements();
            RefreshSpec();
            SetEnabled(true);
            return _host;
        }

        /// <summary>材质参数微调(半径/染色/不透明度/帧率):只改规格与 WPF 属性,不重建顶栏。</summary>
        internal static void ApplyLive()
        {
            if (_stopped) return;
            var ui = Application.Current?.Dispatcher;
            if (ui == null) return;
            if (!ui.CheckAccess()) { ui.BeginInvoke(new Action(() => { try { ApplyLive(); } catch { } })); return; }
            var s = App.Settings;
            if (s == null) return;
            RefreshSpec();
            if (_image != null) _image.Opacity = Math.Max(0.05, Math.Min(1, s.GlassOpacity));
            if (_tint != null)
            {
                _tint.Background = Ui.Brush(s.GlassTint, 1);
                _tint.Opacity = Math.Max(0, Math.Min(1, s.GlassTintOpacity));
            }
            // 液态玻璃:着色器挂在 Image 上(输入=已模糊的捕获图),几何变化时由 UpdateLiquidConstants 重编译,
            // 编译失败静默降级普通玻璃
            bool liquid = s.PillMaterial == 4;
            if (!liquid && _image != null) _image.Effect = null;
            if (liquid) UpdateLiquidConstants();
            // 间隙玻璃强度变了要重算裁剪/遮罩(它由主窗的 LayoutUpdated 派生,这里主动踢一次)
            try { MainWindow.Bar?.RefreshGlassClip(); } catch { }
        }

        /// <summary>把胶囊矩形(DIP)换算成半分辨率捕获图像素坐标;几何变化时重编译着色器(几何烘焙进字面量)。</summary>
        private static bool _liquidDiagLogged;
        private static void UpdateLiquidConstants()
        {
            if (_liquidDiagLogged == false)
            {
                _liquidDiagLogged = true;
                App.Log(string.Format("液态玻璃:UpdateLiquidConstants 首次调用 image={0} spec={1} bar={2} 材质={3}",
                    _image == null ? "null" : "ok", _spec == null ? "null" : "ok",
                    MainWindow.Bar == null ? "null" : "ok", App.Settings == null ? -1 : App.Settings.PillMaterial));
            }
            if (_image == null) return;
            var s = App.Settings;
            var spec = _spec;
            var bar = MainWindow.Bar;
            if (s == null || spec == null || bar == null) return;
            if (s.PillMaterial != 4) return;   // 只有液态玻璃档挂着色器;普通玻璃(3)保持纯位图
            // DIP→半分辨率像素:x 方向按"条带铺满窗口宽"换算;y 方向 ImageHeightDip 对应 HalfHeight。
            double sx = spec.HalfWidth / Math.Max(1, bar.ActualWidth);
            double sy = spec.HalfHeight / Math.Max(1, spec.ImageHeightDip);
            var rects = _pillRectsDip;
            int count = Math.Min(16, rects == null ? 0 : rects.Count);
            var data = new float[count][];
            var sig = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                var r = rects[i];
                data[i] = new[]
                {
                    (float)((r.X + r.Width * 0.5) * sx),
                    (float)((r.Y + r.Height * 0.5) * sy),
                    (float)Math.Max(1, r.Width * 0.5 * sx),
                    (float)Math.Max(1, r.Height * 0.5 * sy)
                };
                sig.Append(data[i][0].ToString("F1")).Append(',').Append(data[i][1].ToString("F1"))
                   .Append(',').Append(data[i][2].ToString("F1")).Append(',').Append(data[i][3].ToString("F1")).Append(';');
            }
            double radiusPx = 19 * Ui.Scale * NativeMethods.SystemDpiScale() * 0.5;
            sig.Append('|').Append(radiusPx.ToString("F1")).Append('|')
               .Append(spec.HalfWidth).Append('x').Append(spec.HalfHeight);
            string signature = sig.ToString();
            if (signature != _liquidSignature)
            {
                // 编译失败也记下签名:下次几何真正变化才重试,避免每个布局事件都重编译
                _liquidSignature = signature;
                _liquid = LiquidGlassEffect.Create(data, count, radiusPx, spec.HalfWidth, spec.HalfHeight);
                SyncLiquidInput();
                _image.Effect = _liquid;        // null = 降级普通玻璃
                App.Log(_liquid != null
                    ? "液态玻璃:着色器已启用(胶囊=" + count + " 半径=" + radiusPx.ToString("F1") + "px)"
                    : "液态玻璃:着色器创建失败,已降级为普通玻璃");
            }
            if (_liquid != null)
            {
                _liquid.Refr = s.GlassRefraction;
                _liquid.Rim = s.GlassRim;
                _liquid.Chroma = s.GlassChroma;
                _liquid.Sheen = LiquidSheen;
            }
        }

        private static string _liquidSignature;

        /// <summary>把当前位图挂到着色器的显式采样源上(见 LiquidGlassEffect.Input 的注释)。</summary>
        private static void SyncLiquidInput()
        {
            if (_liquid == null || _wb == null) return;
            if (_liquidBrush == null || !ReferenceEquals(_liquidBrush.ImageSource, _wb))
                _liquidBrush = new ImageBrush(_wb) { Stretch = Stretch.Fill };
            // 每次都必须赋:着色器对象在几何变化时会被**整个重建**,新对象的 Input 是 null,
            // 采样器未绑定 → 采样到全黑(踩过:只在新位图时赋值,结果液态玻璃一直是死黑)。
            if (!ReferenceEquals(_liquid.Input, _liquidBrush)) _liquid.Input = _liquidBrush;
            if (_wbSharp != null && (_sharpBrush == null || !ReferenceEquals(_sharpBrush.ImageSource, _wbSharp)))
                _sharpBrush = new ImageBrush(_wbSharp) { Stretch = Stretch.Fill };
            if (_sharpBrush != null && !ReferenceEquals(_liquid.Sharp, _sharpBrush)) _liquid.Sharp = _sharpBrush;
        }

        /// <summary>主窗/悬停面板注册:玻璃启用期间排除出屏幕捕获;未启用时先登记,启用时统一设置。</summary>
        internal static void RegisterWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _stopped) return;
            lock (Gate)
            {
                _excluded.Add(hwnd);
                _wdaReady[hwnd] = Environment.TickCount64 + HoldMs;
            }
            if (_enabled && NativeMethods.ExcludeFromCapture(hwnd))
            {
                _holdUntil = Environment.TickCount64 + HoldMs;
                _wake.Set();
            }
        }

        /// <summary>窗口销毁前注销(悬停面板 Dispose 路径),防止句柄表膨胀。</summary>
        internal static void ForgetWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            lock (Gate)
            {
                _excluded.Remove(hwnd);
                _wdaReady.Remove(hwnd);
            }
            if (_enabled) NativeMethods.RestoreCapture(hwnd);
        }

        /// <summary>顶栏显隐(托盘切换):隐藏暂停捕获,显示恢复。</summary>
        internal static void SetActive(bool active)
        {
            if (_stopped) return;
            _active = active;
            _wake.Set();
        }

        /// <summary>设置胶囊并集裁剪几何(主窗 LayoutUpdated 防抖后调用);rects 同时喂给液态玻璃着色器。</summary>
        internal static void SetClip(Geometry clip, List<Rect> rects)
        {
            SetClip(clip, rects, 0);
        }

        /// <summary>
        /// gapFill &gt; 0 时:玻璃铺满整条顶栏(间隙也呈现玻璃),用透明度遮罩让"胶囊外"淡到 gapFill 倍。
        /// 遮罩值按 DIP 绝对坐标绘制,与胶囊矩形同源,避免与 DPI 换算打架。
        /// </summary>
        internal static void SetClip(Geometry clip, List<Rect> rects, double gapFill)
        {
            _pillRectsDip = rects;
            try { UpdateLiquidConstants(); } catch { }
            var ui = Application.Current?.Dispatcher;
            if (ui == null || !ui.CheckAccess()) { ui?.BeginInvoke(new Action(() => { try { SetClip(clip, rects, gapFill); } catch { } })); return; }
            if (_host == null) return;
            _host.Clip = clip;
            try { ApplyGapMask(gapFill, rects); } catch { }
            _host.Visibility = clip != null && _enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>胶囊间隙的玻璃强度倍数(0=间隙完全透明,维持穿透);环境变量 YUKI_GLASS_FILL 可临时覆盖以便取证。</summary>
        internal static double GapFill
        {
            get
            {
                try
                {
                    var v = Environment.GetEnvironmentVariable("YUKI_GLASS_FILL");
                    double d;
                    if (!string.IsNullOrWhiteSpace(v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
                        return Math.Max(0, Math.Min(1, d));
                }
                catch { }
                var s = App.Settings;
                return s == null ? 0 : Math.Max(0, Math.Min(1, s.GlassGapFill));
            }
        }

        /// <summary>胶囊外区域的玻璃透明度倍数(0=不铺间隙,1=整条同强度)。实验性产品选项。</summary>
        private static void ApplyGapMask(double gapFill, List<Rect> rects)
        {
            if (_image == null || _tint == null) return;
            double w = _host == null ? 0 : _host.ActualWidth;
            double h = _host == null ? 0 : _host.ActualHeight;
            if (gapFill <= 0.01 || gapFill >= 0.999 || w < 1 || h < 1 || rects == null || rects.Count == 0)
            {
                _image.OpacityMask = null;
                _tint.OpacityMask = null;
                return;
            }
            var dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(Alpha(gapFill), 255, 255, 255)), null, new RectangleGeometry(new Rect(0, 0, w, h))));
            foreach (var r in rects)
                dg.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(r, 19 * Ui.Scale, 19 * Ui.Scale)));
            var brush = new DrawingBrush(dg)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, w, h),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, w, h),
                TileMode = TileMode.None
            };
            brush.Freeze();
            _image.OpacityMask = brush;
            _tint.OpacityMask = brush;
        }

        private static byte Alpha(double v)
        {
            int a = (int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
            return (byte)(a < 0 ? 0 : (a > 255 ? 255 : a));
        }

        // ---------- 展开页玻璃(一次性捕获) ----------

        /// <summary>
        /// 面板打开「之前」调用(此时面板窗口还完全不可见):当场捕获面板背后区域 → 半分辨率盒式模糊 →
        /// 烘焙染色 → 生成对齐面板物理矩形的 ImageBrush 并直接设为面板背景。
        /// 必须在 Show 之前完成:否则面板会先以主题色出现、几十毫秒后才被换成模糊贴图,
        /// 肉眼就是「打开时闪一下才变模糊」。此时面板未显示,抓图不会拍到自己;顶栏由 WDA 排除。
        /// </summary>
        internal static void RefreshPanelGlass(Border panel, double dipX, double dipY, double dipW, double dipH)
        {
            if (_stopped) return;
            try { CapturePanelNow(panel, dipX, dipY, dipW, dipH); }
            catch { }
        }

        private static void CapturePanelNow(Border panel, double dipX, double dipY, double dipW, double dipH)
        {
            var s = App.Settings;
            if (s == null || (s.PillMaterial != 3 && s.PillMaterial != 4) || panel == null) return;
            if (dipW < 1 || dipH < 1) return;
            var dpi = VisualTreeHelper.GetDpi(panel);
            // 位置一律由调用方传入 DIP 矩形:此时面板窗口尚未显示、也没有 RenderTransform 动画,
            // 用 PointToScreen/ActualWidth 反而不可靠(未 Arrange)。
            double originX = dipX, originY = dipY;
            if (double.IsNaN(originX) || double.IsNaN(originY))
            {
                var fallback = panel.PointToScreen(new Point(0, 0));
                originX = fallback.X / dpi.DpiScaleX;
                originY = fallback.Y / dpi.DpiScaleY;
            }
            int px = (int)Math.Floor(originX * dpi.DpiScaleX), py = (int)Math.Floor(originY * dpi.DpiScaleY);
            int pw = (int)Math.Ceiling(dipW * dpi.DpiScaleX);
            int ph = (int)Math.Ceiling(dipH * dpi.DpiScaleY);
            if (pw < 8 || ph < 8) return;

            double dsy = NativeMethods.SystemDpiScale();
            int blurHalf = BoxHalfFor(s.GlassBlurRadius * dsy);
            int bleed = Math.Min(220, blurHalf * 2 + 12);        // 半分辨率半径 → 全分辨率出血×2
            int x = Math.Max(0, px - bleed), y = Math.Max(0, py - bleed);
            int rx = Math.Min(NativeMethods.ScreenWidth(), px + pw + bleed);
            int by = Math.Min(NativeMethods.ScreenHeight(), py + ph + bleed);
            int cw = (rx - x) & ~1, ch = (by - y) & ~1;          // 偶数化,方便对半降采样
            if (cw < 8 || ch < 8) return;

            byte tr = 20, tg = 20, tb = 28;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(s.GlassTint);
                tr = c.R; tg = c.G; tb = c.B;
            }
            catch { }
            double tintA = Math.Max(0, Math.Min(1, s.GlassTintOpacity));
            double glassOp = Math.Max(0.05, Math.Min(1, s.GlassOpacity));

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            byte[] blurred = null, sharp = null; int hw = 0, hh = 0;
            if (NativeMethods.CreateStripCapture(cw, ch, out var screenDc, out var memoryDc, out var dib, out var bits))
            {
                try
                {
                    if (NativeMethods.BlitStrip(memoryDc, cw, ch, screenDc, x, y))
                    {
                        var full = new byte[cw * ch * 4];
                        Marshal.Copy(bits, full, 0, full.Length);
                        hw = cw / 2; hh = ch / 2;
                        var half = new byte[hw * hh * 4];
                        Downsample2x(full, cw, ch, half, hw, hh);
                        blurred = new byte[half.Length];
                        Array.Copy(half, blurred, half.Length);
                        // 折射要"未模糊"的高频细节(和顶栏双采样源同一思路):先留一份锐帧再模糊
                        sharp = new byte[half.Length];
                        Array.Copy(half, sharp, half.Length);
                        BoxBlur3(blurred, half, hw, hh, blurHalf);
                        // 液态玻璃档:在 CPU 上把轮廓折射/色散/边缘光一次性烘进去,
                        // 面板观感与胶囊完全一致(不再只是"一块糊")。
                        if (s.PillMaterial == 4 && !_panelNoBake)
                        {
                            BakeLiquidPanel(blurred, sharp, hw, hh,
                                (px - x) / 2.0, (py - y) / 2.0, pw / 2.0, ph / 2.0,
                                16 * Ui.Scale * dpi.DpiScaleY * 0.5,
                                Math.Max(0, Math.Min(1, s.GlassRefraction)) * _panelBakeGain,
                                Math.Max(0, Math.Min(1, s.GlassRim)) * _panelBakeGain,
                                Math.Max(0, Math.Min(1, s.GlassChroma)) * _panelBakeGain,
                                _panelBand, _panelSheen);
                        }
                        BakeTint(blurred, tr, tg, tb, tintA);
                    }
                }
                finally { NativeMethods.DestroyStripCapture(ref screenDc, ref memoryDc, ref dib); }
            }
            if (blurred == null) return;
            var wb2 = new WriteableBitmap(hw, hh, 96, 96, PixelFormats.Bgr32, null);
            wb2.WritePixels(new Int32Rect(0, 0, hw, hh), blurred, hw * 4, 0);
            wb2.Freeze();
            // Viewbox = 面板物理矩形在半分辨率捕获图里的位置(全除 2),与云母画笔同口径。
            var brush = new ImageBrush
            {
                ImageSource = wb2,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect((px - x) / 2.0, (py - y) / 2.0, pw / 2.0, ph / 2.0),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0, 0, 1, 1),
                Stretch = Stretch.Fill,
                Opacity = glassOp
            };
            brush.Freeze();
            panel.Background = brush;
            if (!_panelGlassLogged)
            {
                _panelGlassLogged = true;
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                App.Log(string.Format("面板玻璃:预捕获 {0}x{1}(物理) 半分辨率 {2}x{3} 耗时={4:F1}ms",
                    pw, ph, hw, hh, ms));
            }
        }

        // ---------- 内部:UI 侧 ----------

        private static void BuildElements()
        {
            // 注意:这里不放 BlurEffect——满宽 BlurEffect 会让 WPF 每次窗口重绘都重算整条模糊,
            // 实测把渲染线程打满一个核;模糊已挪到捕获线程的 CPU 盒式三趟(半分辨率)。
            _image = new Image
            {
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Opacity = 0.9,
                IsHitTestVisible = false
            };
            _tint = new Border
            {
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false
            };
            _host = new Grid { IsHitTestVisible = false };
            _host.Children.Add(_image);
            _host.Children.Add(_tint);

        }

        /// <summary>
        /// 按当前设置重算捕获规格并同步 UI 元素。尺寸没变时复用位图(避免参数微调闪空帧);
        /// 捕获线程在下一圈发现规格变化后自行重建 GDI 资源。
        /// </summary>
        private static void RefreshSpec()
        {
            var s = App.Settings;
            double dsy = NativeMethods.SystemDpiScale();
            int physW = NativeMethods.ScreenWidth();
            double barPhysH = 46.0 * Ui.Scale * dsy;
            // 条带从屏幕 y=0 抓到 barPhysH+下出血:真实屏幕没有负 y,上缘靠屏幕边界自然截断。
            double blurPhys = Math.Max(1, (s == null ? 24 : s.GlassBlurRadius) * dsy);
            int bleed = (int)Math.Ceiling(blurPhys) + 8;
            int h = (int)Math.Ceiling(barPhysH) + bleed;
            int fps = s == null ? 30 : Math.Clamp(s.GlassMaxFps, 5, 60);
            var spec = new GlassSpec
            {
                Width = Math.Max(16, physW + 1) & ~1,
                Height = Math.Max(8, h + 1) & ~1,
                BlurHalf = BoxHalfFor((s == null ? 24 : s.GlassBlurRadius) * dsy),
                FpsMs = Math.Max(16, 1000 / fps),
                MidY = Math.Max(4, (int)Math.Ceiling(barPhysH / 2)),
            };
            spec.HalfWidth = spec.Width / 2;
            spec.HalfHeight = spec.Height / 2;
            spec.ImageHeightDip = spec.Height / dsy;
            _spec = spec;
            if (_image != null)
            {
                if (_wb == null || _wb.PixelWidth != spec.HalfWidth || _wb.PixelHeight != spec.HalfHeight)
                {
                    _wb = new WriteableBitmap(spec.HalfWidth, spec.HalfHeight, 96, 96, PixelFormats.Bgra32, null);
                    _wbSharp = null;
                    _image.Source = _wb;
                    _consumed = null;
                    SyncLiquidInput();
                }
                _image.Height = spec.ImageHeightDip;
            }
            if (_tint != null) _tint.Height = spec.ImageHeightDip;
            if (_testSrc && _wb != null)
            {
                FillTestPattern(_wb.PixelWidth, _wb.PixelHeight);
                // 合成图案模式也喂未模糊帧,否则轮廓折射采到空白(诊断时容易误判为"折射没生效")
                if (_liquid != null)
                {
                    if (_wbSharp == null || _wbSharp.PixelWidth != _wb.PixelWidth || _wbSharp.PixelHeight != _wb.PixelHeight)
                        _wbSharp = new WriteableBitmap(_wb.PixelWidth, _wb.PixelHeight, 96, 96, PixelFormats.Bgra32, null);
                    FillTestPattern(_wbSharp.PixelWidth, _wbSharp.PixelHeight, _wbSharp);
                    SyncLiquidInput();
                }
            }
            try { UpdateLiquidConstants(); } catch { }   // 纹理尺寸变了,着色器几何要跟着换
            _wake.Set();
        }

        private static void SetEnabled(bool on)
        {
            if (_stopped || _enabled == on) return;
            _enabled = on;
            if (on)
            {
                ApplyExclusionAll();
                _holdUntil = Environment.TickCount64 + HoldMs;
                if (_thread == null)
                {
                    _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "YukiDesk.GlassCapture" };
                    _thread.Start();
                }
                _wake.Set();
            }
            else
            {
                Interlocked.Exchange(ref _pumpQueued, 0);
                RestoreExclusionAll();
                _wake.Set();
            }
        }

        private static void ApplyExclusionAll()
        {
            IntPtr[] list;
            lock (Gate) list = new List<IntPtr>(_excluded).ToArray();
            foreach (var h in list) NativeMethods.ExcludeFromCapture(h);
        }

        private static void RestoreExclusionAll()
        {
            IntPtr[] list;
            lock (Gate) list = new List<IntPtr>(_excluded).ToArray();
            foreach (var h in list) NativeMethods.RestoreCapture(h);
        }

        /// <summary>取证用合成图案:上半白、下半按"红/绿/蓝"三段竖切,再加黑底白格用于看 uv 是否错位。</summary>
        private static void FillTestPattern(int w, int h, WriteableBitmap target = null)
        {
            try
            {
                if (target == null) target = _wb;
                if (target == null) return;
                var px = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w + x) * 4;
                        byte b, g, r;
                        if (y < h / 4) { b = 255; g = 255; r = 255; }          // 顶部 1/4 = 纯白
                        else
                        {
                            int q = x * 3 / Math.Max(1, w);
                            if (q == 0) { b = 0; g = 0; r = 255; }             // 红
                            else if (q == 1) { b = 0; g = 255; r = 0; }        // 绿
                            else { b = 255; g = 0; r = 0; }                    // 蓝
                        }
                        px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
                    }
                }
                target.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
            }
            catch { }
        }

        /// <summary>捕获线程发布新帧后调用:合并去重,把一次泵送排到 UI 线程 Render 优先级。</summary>
        private static void SchedulePump()
        {
            if (!_enabled || _stopped || _testSrc) return;
            if (Interlocked.Exchange(ref _pumpQueued, 1) != 0) return;
            try
            {
                var d = Application.Current == null ? null : Application.Current.Dispatcher;
                if (d == null || d.HasShutdownStarted) { Interlocked.Exchange(ref _pumpQueued, 0); return; }
                d.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    Interlocked.Exchange(ref _pumpQueued, 0);
                    PumpTick(null, EventArgs.Empty);
                }));
            }
            catch { Interlocked.Exchange(ref _pumpQueued, 0); }
        }

        private static void PumpTick(object sender, EventArgs e)
        {
            if (!_enabled || _stopped) return;
            if (_testSrc) return;   // 合成图案模式:不让捕获帧覆盖图案
            Interlocked.Increment(ref StatPumps);
            // 取证按 tick 计数:静止桌面可能只有 1 帧发布,按发布数会永远等不到转储。
            if (_dumpFrames >= 0 && _dumpFrames < 40 && ++_dumpFrames == 40)
            {
                try { DumpLatest(); } catch { }
            }
            try
            {
                // WritePixels 也放进锁:发布数组来自轮换池,锁内拷贝(约 0.5ms)保证不与捕获线程复用打架。
                lock (Gate)
                {
                    var f = _latest;
                    if (f == null || ReferenceEquals(f, _consumed) || _image == null) return;
                    var spec = _spec;
                    if (spec == null) return;
                    if (_wb == null || _wb.PixelWidth != _latestW || _wb.PixelHeight != _latestH)
                    {
                        _wb = new WriteableBitmap(_latestW, _latestH, 96, 96, PixelFormats.Bgra32, null);
                        _wbSharp = null;
                        _image.Source = _wb;
                        _image.Height = spec.ImageHeightDip;
                        _consumed = null;
                        SyncLiquidInput();
                    }
                    _wb.WritePixels(new Int32Rect(0, 0, _latestW, _latestH), f, _latestW * 4, 0);
                    Interlocked.Increment(ref StatWrites);
                    var sf = _latestSharp;
                    if (sf != null && _liquid != null && f == _latest)
                    {
                        if (_wbSharp == null || _wbSharp.PixelWidth != _latestW || _wbSharp.PixelHeight != _latestH)
                        {
                            _wbSharp = new WriteableBitmap(_latestW, _latestH, 96, 96, PixelFormats.Bgra32, null);
                            SyncLiquidInput();
                        }
                        _wbSharp.WritePixels(new Int32Rect(0, 0, _latestW, _latestH), sf, _latestW * 4, 0);
                        Interlocked.Increment(ref StatSharpWrites);
                    }
                    _consumed = f;
                }
            }
            catch { }
        }

        /// <summary>转储当前发布帧与主窗可视树(按 tick 触发,帧可能是"已消费"的那张,仍然有效)。</summary>
        private static void DumpLatest()
        {
            byte[] f; int w, h;
            lock (Gate)
            {
                f = _latest; w = _latestW; h = _latestH;
            }
            if (f != null) DumpOnce(f, w, h);
        }

        /// <summary>进程内取证:发布帧(WDA 排除+模糊后的画面)与主窗可视树 RTB 各存一张 PNG。</summary>
        private static void DumpOnce(byte[] frame, int w, int h)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_dumpDir);
                var input = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                input.WritePixels(new Int32Rect(0, 0, w, h), frame, w * 4, 0);
                input.Freeze();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(input));
                using (var fs = System.IO.File.Create(System.IO.Path.Combine(_dumpDir, "glass-input.png")))
                    enc.Save(fs);

                var bar = MainWindow.Bar;
                if (bar != null)
                {
                    double dpi = NativeMethods.SystemDpiScale() * 96;
                    var rtb = new RenderTargetBitmap(
                        (int)Math.Ceiling(bar.ActualWidth * dpi / 96), (int)Math.Ceiling(bar.ActualHeight * dpi / 96),
                        dpi, dpi, PixelFormats.Pbgra32);
                    rtb.Render(bar);
                    rtb.Freeze();
                    var enc2 = new PngBitmapEncoder();
                    enc2.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = System.IO.File.Create(System.IO.Path.Combine(_dumpDir, "glass-window.png")))
                        enc2.Save(fs);
                }
                App.Log("玻璃:取证已写入 " + _dumpDir);
            }
            catch (Exception ex) { App.Log("玻璃取证失败: " + ex.Message); }
        }

        private static void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            var ui = Application.Current?.Dispatcher;
            if (ui == null) return;
            ui.BeginInvoke(new Action(() =>
            {
                if (_stopped || !_enabled) return;
                try
                {
                    RefreshSpec();
                    _holdUntil = Environment.TickCount64 + HoldMs;
                    App.Log("玻璃:显示设置变化,捕获条带已按新分辨率重建");
                }
                catch { }
            }));
        }

        // ---------- 内部:捕获线程 ----------

        private static void CaptureLoop()
        {
            GlassDiagnostics.TagThread("玻璃捕获");
            IntPtr screenDc = IntPtr.Zero, memoryDc = IntPtr.Zero, dib = IntPtr.Zero, bits = IntPtr.Zero;
            int capW = 0, capH = 0, hw = 0, hh = 0;
            byte[] full = null, halfRaw = null, halfBase = null, workB = null;
            bool hasBaseline = false, loggedSelf = false, loggedCapFail = false;
            try
            {
                while (!_stopThread)
                {
                    var spec = _spec;
                    if (!_enabled || !_active || spec == null)
                    {
                        _wake.Wait(400);
                        _wake.Reset();
                        continue;
                    }
                    if (Environment.TickCount64 < Volatile.Read(ref _holdUntil))
                    {
                        Thread.Sleep(100);
                        continue;
                    }
                    // 取证抓图进行中:WDA 已被临时恢复(顶栏此刻可被捕获),
                    // 必须停止 BitBlt,否则就会把自己拍进下一帧形成正反馈。
                    if (_shotPause)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    if (capW != spec.Width || capH != spec.Height)
                    {
                        NativeMethods.DestroyStripCapture(ref screenDc, ref memoryDc, ref dib);
                        bits = IntPtr.Zero;
                        capW = capH = 0;
                        hasBaseline = false;
                        if (NativeMethods.CreateStripCapture(spec.Width, spec.Height,
                            out screenDc, out memoryDc, out dib, out bits))
                        {
                            capW = spec.Width;
                            capH = spec.Height;
                            hw = spec.HalfWidth;
                            hh = spec.HalfHeight;
                            full = new byte[capW * capH * 4];
                            halfRaw = new byte[hw * hh * 4];
                            halfBase = new byte[hw * hh * 4];
                            workB = new byte[hw * hh * 4];
                            lock (Gate)
                            {
                                for (int i = 0; i < _rotate.Length; i++) _rotate[i] = new byte[hw * hh * 4];
                                for (int i = 0; i < _rotateSharp.Length; i++) _rotateSharp[i] = new byte[hw * hh * 4];
                                _rotateIndex = 0;
                                _latest = null;
                                _latestSharp = null;
                            }
                        }
                        else
                        {
                            if (!loggedCapFail)
                            {
                                loggedCapFail = true;
                                App.Log("玻璃:创建捕获条带失败 err=" + Marshal.GetLastWin32Error() + ",稍后重试");
                            }
                            Thread.Sleep(1000);
                            continue;
                        }
                    }

                    // 遮挡保护:顶栏被普通窗口大面积盖住时(全屏游戏/最大化窗口)跳过采样,
                    // 反正玻璃也不可见——省 BitBlt 也避免独占全屏下抓到黑帧。
                    if (IsCovered(spec))
                    {
                        _staticRun = 0;
                        Thread.Sleep(300);
                        continue;
                    }

                    long busyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    Interlocked.Increment(ref StatFrames);
                    if (!NativeMethods.BlitStrip(memoryDc, capW, capH, screenDc, 0, 0))
                    {
                        Thread.Sleep(250);
                        continue;
                    }
                    Interlocked.Increment(ref StatBlits);
                    long tBlit = System.Diagnostics.Stopwatch.GetTimestamp();
                    Marshal.Copy(bits, full, 0, full.Length);
                    Interlocked.Add(ref StatBlitTicks, System.Diagnostics.Stopwatch.GetTimestamp() - tBlit);
                    long tDown = System.Diagnostics.Stopwatch.GetTimestamp();
                    Downsample2x(full, capW, capH, halfRaw, hw, hh);
                    Interlocked.Add(ref StatDownTicks, System.Diagnostics.Stopwatch.GetTimestamp() - tDown);

                    long tDiff = System.Diagnostics.Stopwatch.GetTimestamp();
                    double diff = hasBaseline ? MeanAbsDiff(halfBase, halfRaw, hw, hh, 16) : 1e9;
                    Interlocked.Add(ref StatDiffTicks, System.Diagnostics.Stopwatch.GetTimestamp() - tDiff);
                    long nowTick = Environment.TickCount64;
                    bool publish = _alwaysPub || diff >= DiffThreshold;
                    // 小幅漂移(例如慢速滚动、小面积动画)不必按满帧率发布:每次发布都要整条窗口重呈。
                    // 但 100ms 的闸门(10fps)在液态玻璃下能看出卡顿,收到 50ms(20fps)——
                    // 折中:CPU 只多一半,肉眼基本连续。
                    if (publish && !_alwaysPub && diff < SmallDiffThreshold && nowTick - _lastPubTick < 50) publish = false;
                    if (publish)
                    {
                        _lastPubTick = nowTick;
                        byte[] workA, sharpA;
                        lock (Gate)
                        {
                            int slot = _rotateIndex++ % _rotate.Length;
                            workA = _rotate[slot];
                            sharpA = _rotateSharp[slot % _rotateSharp.Length];
                        }
                        Array.Copy(halfRaw, workA, workA.Length);
                        // 未模糊帧只做降采样拷贝(无模糊),供轮廓折射采样;与模糊帧同槽位轮换,杜绝撕裂。
                        if (sharpA != null) Array.Copy(halfRaw, sharpA, sharpA.Length);
                        long tBlur = System.Diagnostics.Stopwatch.GetTimestamp();
                        BoxBlur3(workA, workB, hw, hh, spec.BlurHalf);
                        Interlocked.Add(ref StatBlurTicks, System.Diagnostics.Stopwatch.GetTimestamp() - tBlur);
                        lock (Gate)
                        {
                            _latest = workA;
                            _latestSharp = sharpA;
                            _latestW = hw;
                            _latestH = hh;
                        }
                        Array.Copy(halfRaw, halfBase, halfBase.Length);
                        hasBaseline = true;
                        _staticRun = 0;
                        Interlocked.Increment(ref StatPublishes);
                        SchedulePump();   // 有新帧才唤醒 UI 线程(静止时 0 唤醒)
                        if (!loggedSelf)
                        {
                            loggedSelf = true;
                            SelfCheck(workA, hw, hh, out double m, out double sd, out long n);
                            App.Log(string.Format("玻璃:首帧 {0}x{1}(半分辨率) 红通道均值={2:F1} 标准差={3:F1} 采样={4}",
                                hw, hh, m, sd, n));
                        }
                    }
                    else
                    {
                        _staticRun++;
                    }
                    Interlocked.Add(ref StatBusyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - busyStart);

                    // 自适应降频:连续静止就拉长采样间隔,静止桌面接近 0 开销;
                    // 任何变化(或被唤醒)都会立刻回到满速。
                    // 采样间隔就是「玻璃多久看一眼背后」= 用户感知的更新延迟上限,所以不能拉太长了:
                    // 之前深静止档是 1000ms,桌面静止 22 秒后任何运动都要最多 1 秒才反映到玻璃上,
                    // 观感就是"液态玻璃卡住了/很慢"。现在上限 250ms(深静止)/120ms(静止),
                    // 一次 BitBlt 的固定成本约 2.5ms,深静止档常驻约 1%。20fps 的持续大动限帧也已取消,
                    // 持续运动时直接跑满「帧率上限」设置(默认 30fps)。
                    int delay = spec.FpsMs;
                    if (_staticRun > 90) delay = 250;
                    else if (_staticRun > 30) delay = 120;
                    Thread.Sleep(delay);
                }
            }
            catch (Exception ex)
            {
                try { App.Log("玻璃捕获线程异常: " + ex.Message); } catch { }
            }
            finally
            {
                NativeMethods.DestroyStripCapture(ref screenDc, ref memoryDc, ref dib);
            }
        }

        /// <summary>
        /// 遮挡探测:条带横向 5 个点问 Windows 谁在顶(物理像素)。顶栏自己/我们的面板/
        /// 桌面层(Progman/WorkerW)都算"没被盖";≥3 点被别的窗口占住才算盖住。
        /// </summary>
        private static bool IsCovered(GlassSpec spec)
        {
            try
            {
                int[] xs = { (int)(spec.Width * 0.08), (int)(spec.Width * 0.30), (int)(spec.Width * 0.50),
                             (int)(spec.Width * 0.70), (int)(spec.Width * 0.92) };
                int covered = 0;
                foreach (var x in xs)
                {
                    var root = NativeMethods.RootWindowAt(x, spec.MidY);
                    if (root == IntPtr.Zero) continue;
                    lock (Gate) if (_excluded.Contains(root)) continue;
                    var cls = NativeMethods.WindowClassOf(root);
                    if (cls == "Progman" || cls == "WorkerW") continue;
                    covered++;
                }
                return covered >= 3;
            }
            catch { return false; }
        }

        // ---------- 像素处理(纯函数,捕获线程/线程池共用) ----------

        /// <summary>全分辨率物理 σ(DIP 半径 × DPI)→ 半分辨率盒式半径。三趟盒式 h≈σ,取 0.45 稍锐。</summary>
        private static int BoxHalfFor(double physSigma)
        {
            return Math.Max(1, Math.Min(96, (int)Math.Round(physSigma * 0.45)));
        }

        /// <summary>2×2 平均降采样(只动 BGR,alpha 补 255)。半分辨率域做模糊,大半径下视觉无差。</summary>
        private static void Downsample2x(byte[] src, int sw, int sh, byte[] dst, int w, int h)
        {
            for (int y = 0; y < h; y++)
            {
                int r0 = (y * 2) * sw * 4;
                int r1 = (y * 2 + 1 < sh ? y * 2 + 1 : y * 2) * sw * 4;
                int d = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int x0 = x * 2, x1 = x * 2 + 1 < sw ? x * 2 + 1 : x * 2;
                    int s00 = r0 + x0 * 4, s01 = r0 + x1 * 4, s10 = r1 + x0 * 4, s11 = r1 + x1 * 4;
                    dst[d] = (byte)((src[s00] + src[s01] + src[s10] + src[s11]) >> 2);
                    dst[d + 1] = (byte)((src[s00 + 1] + src[s01 + 1] + src[s10 + 1] + src[s11 + 1]) >> 2);
                    dst[d + 2] = (byte)((src[s00 + 2] + src[s01 + 2] + src[s10 + 2] + src[s11 + 2]) >> 2);
                    dst[d + 3] = 255;
                    d += 4;
                }
            }
        }

        /// <summary>滑动和盒式模糊(水平+垂直一趟,只动 BGR)。O(n) 与半径无关,边 replicate。</summary>
        private static void BlurPassH(byte[] src, byte[] dst, int w, int h, int r)
        {
            int win = 2 * r + 1;
            var sum = new int[3];
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                sum[0] = sum[1] = sum[2] = 0;
                for (int k = -r; k <= r; k++)
                {
                    int x = k < 0 ? 0 : (k >= w ? w - 1 : k);
                    int i = row + x * 4;
                    sum[0] += src[i]; sum[1] += src[i + 1]; sum[2] += src[i + 2];
                }
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;
                    dst[o] = (byte)(sum[0] / win);
                    dst[o + 1] = (byte)(sum[1] / win);
                    dst[o + 2] = (byte)(sum[2] / win);
                    int addX = x + r + 1, remX = x - r;
                    int ai = row + (addX >= w ? w - 1 : addX) * 4;
                    int ri = row + (remX < 0 ? 0 : remX) * 4;
                    sum[0] += src[ai] - src[ri];
                    sum[1] += src[ai + 1] - src[ri + 1];
                    sum[2] += src[ai + 2] - src[ri + 2];
                }
            }
        }

        private static void BlurPassV(byte[] src, byte[] dst, int w, int h, int r)
        {
            int win = 2 * r + 1;
            var sum = new int[3];
            for (int x = 0; x < w; x++)
            {
                int col = x * 4;
                sum[0] = sum[1] = sum[2] = 0;
                for (int k = -r; k <= r; k++)
                {
                    int y = k < 0 ? 0 : (k >= h ? h - 1 : k);
                    int i = y * w * 4 + col;
                    sum[0] += src[i]; sum[1] += src[i + 1]; sum[2] += src[i + 2];
                }
                for (int y = 0; y < h; y++)
                {
                    int o = y * w * 4 + col;
                    dst[o] = (byte)(sum[0] / win);
                    dst[o + 1] = (byte)(sum[1] / win);
                    dst[o + 2] = (byte)(sum[2] / win);
                    int addY = y + r + 1, remY = y - r;
                    int ai = (addY >= h ? h - 1 : addY) * w * 4 + col;
                    int ri = (remY < 0 ? 0 : remY) * w * 4 + col;
                    sum[0] += src[ai] - src[ri];
                    sum[1] += src[ai + 1] - src[ri + 1];
                    sum[2] += src[ai + 2] - src[ri + 2];
                }
            }
        }

        /// <summary>三趟水平+垂直盒式 ≈ 高斯(经典等价)。结果写回 a,b 为中间缓冲。</summary>
        private static void BoxBlur3(byte[] a, byte[] b, int w, int h, int r)
        {
            if (r < 1) return;
            for (int it = 0; it < 3; it++)
            {
                BlurPassH(a, b, w, h, r);
                BlurPassV(b, a, w, h, r);
            }
            // 模糊只写 BGR,alpha 位停留在池里的旧值(新池为 0)。
            // WPF 把它当纹理采样时会按"预乘 alpha"解释:alpha=0 → rgb 全被抹成 0,
            // 表现就是液态玻璃胶囊内部一片死黑(只剩边缘光)。这里统一补成不透明。
            SetAlphaOpaque(a);
        }

        /// <summary>把 BGRA 缓冲的 alpha 字节全部置 255(每像素一次字节写,69k 像素约 0.05ms)。</summary>
        private static void SetAlphaOpaque(byte[] px)
        {
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        }


        /// <summary>
        /// 展开页版液态玻璃:在 CPU 上一次性烘焙(不挂 ShaderEffect)。
        /// 展开页是"一次性静态捕获",不像顶栏每帧重来,所以直接把着色器那套数学搬到 CPU:
        /// 圆角矩形 SDF → 轮廓内侧一圈用**未模糊**帧折射采样(RGB 三通道错位做色散)→ 叠边缘光。
        /// 这样面板与胶囊观感完全一致,且不依赖 WPF Effect——面板是独立窗口 + 背景画刷,
        /// 给画刷挂 Effect 会牵动整窗重呈,得不偿失。
        /// rect 参数是面板在半分辨率捕获图里的像素矩形。
        /// </summary>
        private static void BakeLiquidPanel(byte[] body, byte[] sharp, int w, int h,
            double rectX, double rectY, double rectW, double rectH, double radius,
            double refr, double rim, double chroma, double band, double sheen)
        {
            if (band < 1.0) band = LiquidEdgeBand;
            if (body == null || w < 8 || h < 8 || rectW < 2 || rectH < 2) return;
            double cx = rectX + rectW * 0.5, cy = rectY + rectH * 0.5;
            double hx = Math.Max(0.5, rectW * 0.5), hy = Math.Max(0.5, rectH * 0.5);
            double rad = Math.Max(0, Math.Min(radius, Math.Min(hx, hy) - 0.5));
            bool lensOn = sharp != null && sharp.Length == body.Length && (refr > 0.001 || chroma > 0.001);
            // 只扫轮廓带(面板矩形外扩 8px 的环形区域),面板内部纯 body 不重算。
            double pad = band + 4;
            int x0 = Math.Max(0, (int)Math.Floor(rectX - pad));
            int x1 = Math.Min(w - 1, (int)Math.Ceiling(rectX + rectW + pad));
            int y0 = Math.Max(0, (int)Math.Floor(rectY - pad));
            int y1 = Math.Min(h - 1, (int)Math.Ceiling(rectY + rectH + pad));
            const double e = 2.0;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double d = SdRoundRect(px, py, cx, cy, hx, hy, rad);
                    if (Math.Abs(d) >= band) continue;   // 带外不动(body 保持模糊)
                    double edge = 1.0 - Math.Abs(d) / band;
                    if (edge <= 0) continue;
                    double e2 = edge * edge;
                    double gx = SdRoundRect(px + e, py, cx, cy, hx, hy, rad) - SdRoundRect(px - e, py, cx, cy, hx, hy, rad);
                    double gy = SdRoundRect(px, py + e, cx, cy, hx, hy, rad) - SdRoundRect(px, py - e, cx, cy, hx, hy, rad);
                    double gl = Math.Sqrt(gx * gx + gy * gy) + 1e-4;
                    double nx = gx / gl, ny = gy / gl;
                    // 带宽变大时位移同比例放大:透镜的"折角"是物理量,带越宽走得越远,否则宽边只会变糊
                    double dispScale = LiquidDispScale * (band / LiquidEdgeBand);
                    double dxs = nx * refr * e2 * dispScale;
                    double dys = ny * refr * e2 * dispScale;
                    int i = (y * w + x) * 4;
                    double cb = body[i], cg = body[i + 1], cr = body[i + 2];
                    if (lensOn)
                    {
                        double ca = chroma * e2 * LiquidChromaScale;
                        // 三个通道各自位移采样:边缘出现极轻微的红/蓝分离,是液态玻璃的标志性色散
                        SampleBilinear(sharp, w, h, px + dxs * (1.0 + ca), py + dys * (1.0 + ca), out _, out _, out double lr);
                        SampleBilinear(sharp, w, h, px + dxs, py + dys, out _, out double lg, out _);
                        SampleBilinear(sharp, w, h, px + dxs * (1.0 - ca), py + dys * (1.0 - ca), out double lb, out _, out _);
                        double mix = Math.Min(1.0, e2 * 1.1) * 0.85;
                        cb = cb * (1 - mix) + lb * mix;
                        cg = cg * (1 - mix) + lg * mix;
                        cr = cr * (1 - mix) + lr * mix;
                    }
                    // 轮廓内侧一圈亮边(与 HLSL 同口径:rim*edge²*saturate(-d/6+0.5)*0.5)
                    double rimTerm = rim * e2 * Math.Min(1.0, Math.Max(0.0, -d / band + 0.5)) * 0.5 * 255.0;
                    cb += rimTerm; cg += rimTerm; cr += rimTerm;
                    if (sheen > 0.001)
                    {
                        // 方向光(左上方)在斜面上打出高光、右下方压暗:玻璃厚板的"体积感"靠这一对比,
                        // 否则面板内部只是均匀半透明色,看起来像塑料片。
                        double dotL = -(nx * 0.55 + ny * 0.83);
                        double lit = Math.Max(0.0, dotL), shade = Math.Max(0.0, -dotL);
                        double sh = sheen * e2 * (lit - shade * 0.55) * 255.0 * 0.35;
                        cb += sh; cg += sh; cr += sh;
                    }
                    body[i] = ClampByte(cb);
                    body[i + 1] = ClampByte(cg);
                    body[i + 2] = ClampByte(cr);
                }
            }
        }

        private const double LiquidEdgeBand = 6.0;    // 折射带宽度(半分辨率像素),与 HLSL 的 6.0 一致
        private const double LiquidDispScale = 14.0;  // 折射位移上限,与 HLSL 的 14.0 一致
        private const double LiquidChromaScale = 0.08;

        /// <summary>圆角矩形有向距离场(与着色器里的 sd() 同一公式,内部为负)。</summary>
        private static double SdRoundRect(double px, double py, double cx, double cy,
            double hx, double hy, double radius)
        {
            double qx = Math.Abs(px - cx) - hx + radius;
            double qy = Math.Abs(py - cy) - hy + radius;
            double mx = Math.Max(qx, 0), my = Math.Max(qy, 0);
            return Math.Sqrt(mx * mx + my * my) + Math.Min(Math.Max(qx, qy), 0) - radius;
        }

        private static byte ClampByte(double v)
        {
            if (v <= 0) return 0;
            if (v >= 255) return 255;
            return (byte)(v + 0.5);
        }

        /// <summary>双线性采样 BGRA 缓冲(越界钳边),用于 CPU 折射。</summary>
        private static void SampleBilinear(byte[] px, int w, int h, double sx, double sy,
            out double b, out double g, out double r)
        {
            if (sx < 0) sx = 0; else if (sx > w - 1) sx = w - 1;
            if (sy < 0) sy = 0; else if (sy > h - 1) sy = h - 1;
            int ix = (int)sx, iy = (int)sy;
            double fx = sx - ix, fy = sy - iy;
            int ix1 = ix + 1 < w ? ix + 1 : ix;
            int iy1 = iy + 1 < h ? iy + 1 : iy;
            int i00 = (iy * w + ix) * 4, i01 = (iy * w + ix1) * 4;
            int i10 = (iy1 * w + ix) * 4, i11 = (iy1 * w + ix1) * 4;
            double w00 = (1 - fx) * (1 - fy), w01 = fx * (1 - fy), w10 = (1 - fx) * fy, w11 = fx * fy;
            b = px[i00] * w00 + px[i01] * w01 + px[i10] * w10 + px[i11] * w11;
            g = px[i00 + 1] * w00 + px[i01 + 1] * w01 + px[i10 + 1] * w10 + px[i11 + 1] * w11;
            r = px[i00 + 2] * w00 + px[i01 + 2] * w01 + px[i10 + 2] * w10 + px[i11 + 2] * w11;
        }

        /// <summary>把染色直接烘进像素(展开页没有 WPF 染色层,一次性捕获顺手烘焙)。</summary>
        private static void BakeTint(byte[] px, byte r, byte g, byte b, double a)
        {
            if (a <= 0) return;
            double ia = 1 - a;
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = (byte)(px[i] * ia + b * a);
                px[i + 1] = (byte)(px[i + 1] * ia + g * a);
                px[i + 2] = (byte)(px[i + 2] * ia + r * a);
            }
        }

        /// <summary>
        /// 降采样差分:x 每 16px、y 取 4 行,比较 B/G/R 三通道(GDI 不写 alpha,跳过)。
        /// 返回平均绝对差(0~255);静止桌面典型 &lt; 1。移植自 glasslayer 探针。
        /// </summary>
        private static double MeanAbsDiff(byte[] previous, byte[] current, int width, int height, int stepX = 32)
        {
            if (previous.Length != current.Length) return 255;
            long sum = 0;
            long count = 0;
            int[] rows = { height / 8, height * 3 / 8, height * 5 / 8, height * 7 / 8 };
            foreach (int y in rows)
            {
                int rowStart = y * width * 4;
                for (int x = 0; x < width; x += stepX)
                {
                    int i = rowStart + x * 4;
                    int db = previous[i] - current[i];
                    int dg = previous[i + 1] - current[i + 1];
                    int dr = previous[i + 2] - current[i + 2];
                    sum += Math.Abs(db) + Math.Abs(dg) + Math.Abs(dr);
                    count += 3;
                }
            }
            return count == 0 ? 0 : (double)sum / count;
        }

        /// <summary>自检:红通道均值/标准差,证明捕获不是空图或纯色(启动诊断用,与云母日志风格一致)。</summary>
        private static void SelfCheck(byte[] frame, int width, int height, out double meanR, out double sd, out long samples)
        {
            double sum = 0, square = 0;
            long n = 0;
            for (int y = 0; y < height; y += 7)
            {
                int rowStart = y * width * 4;
                for (int x = 0; x < width; x += 7)
                {
                    int r = frame[rowStart + x * 4 + 2];
                    sum += r;
                    square += (double)r * r;
                    n++;
                }
            }
            samples = n;
            meanR = n == 0 ? 0 : sum / n;
            sd = n == 0 ? 0 : Math.Sqrt(Math.Max(0, square / n - meanR * meanR));
        }
    }
}
