using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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

        // 斜面明暗(方向光高光/背光压暗):胶囊与展开页共用一个强度,保证是同一种材质。
        private static readonly double LiquidSheen = ReadEnvDouble("YUKI_LIQUID_SHEEN", 1.0);
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
        internal static long StatPanelFrames;   // 展开页实时玻璃真正发布的帧数(静态时为 0)
        internal static long StatDxgiIdle;      // GPU 捕获:等不到新合成帧的次数(桌面没变化,零成本)
        internal static long StatDxgiSkip;      // GPU 捕获:拿到帧但脏矩形不碰顶栏条带,整帧跳过
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
        // 静止程度用"距离上次发布多久"衡量(TickCount64 毫秒),不用帧数:
        // 帧数阈值在 60fps 设置下要等 0.5~1.5 秒才降频,期间白烧 12% 单核。
        private static long _lastPubTick;   // 上次发布的时刻(TickCount64),用于小幅漂移限流
        // 「捕获开始 → UI 线程写进 WriteableBitmap」的延迟统计,用于回答"玻璃有几帧延迟"。
        private static long _pubCaptureTick;
        private static double _latLastMs, _latSumMs, _latMaxMs;
        private static int _latCount;
        // 「捕获开始 → WPF 开始渲染这一帧」= 端到端管线延迟(一个屏幕刷新周期 ≈ 一帧)。
        // 只在 --glassperf 里临时订阅 CompositionTarget.Rendering:常驻订阅会让 WPF 渲染循环永不歇,白烧 CPU。
        private static EventHandler _latRenderHook;
        private static long _latRenderSeen;
        private static double _latRenderLastMs, _latRenderSumMs, _latRenderMaxMs;
        private static int _latRenderCount, _renderFrames;
        private static long _renderStartTick;
        // 诊断开关:跳过静止差分门控,强制每帧发布(=模拟"顶栏背后一直在动"的最坏情况,如动态壁纸/视频)。
        private static bool _alwaysPub;

        // GPU 捕获(DXGI Desktop Duplication)。抓一帧约 0.35ms(BitBlt 是 3.5~6ms),
        // 而且 AcquireNextFrame 会阻塞到下一次桌面合成 → 节拍自动等于刷新率、端到端只延迟一帧。
        // 设备初始化失败(老系统/受保护内容/会话被占用)时回退 GDI,功能不受影响。
        private static DxgiCapture _dxgi;
        private static bool _dxgiTried;

        /// <summary>App 启动时调用:只订阅事件,不开线程。真正的开销由 EnsureHost(材质=3)驱动。</summary>
        internal static void Start()
        {
            _testSrc = Environment.GetEnvironmentVariable("YUKI_GLASS_TESTSRC") == "1";
            _alwaysPub = Environment.GetEnvironmentVariable("YUKI_GLASS_ALWAYS_PUB") == "1";
            // 注意:这里**不**建 GPU 捕获会话。材质=0 时连 D3D 设备都不该创建
            // (D3D11 会带出 30+ 条驱动线程),真正需要时由捕获线程懒建(EnsureDxgi)。
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

        /// <summary>开启/关闭渲染侧延迟探针(只给性能自检用,平时必须关掉)。</summary>
        internal static void SetRenderLatencyProbe(bool on)
        {
            try
            {
                if (on)
                {
                    if (_latRenderHook != null) return;
                    _latRenderLastMs = _latRenderSumMs = _latRenderMaxMs = 0;
                    _latRenderCount = 0;
                    _renderFrames = 0;
                    _renderStartTick = Environment.TickCount64;
                    _latRenderSeen = 0;
                    _latRenderHook = (s, e) =>
                    {
                        _renderFrames++;
                        long ct = Interlocked.Read(ref _pubCaptureTick);
                        if (ct == 0 || ct == Interlocked.Read(ref _latRenderSeen)) return;
                        Interlocked.Exchange(ref _latRenderSeen, ct);
                        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - ct) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        _latRenderLastMs = ms;
                        _latRenderSumMs += ms;
                        if (ms > _latRenderMaxMs) _latRenderMaxMs = ms;
                        _latRenderCount++;
                    };
                    System.Windows.Media.CompositionTarget.Rendering += _latRenderHook;
                }
                else if (_latRenderHook != null)
                {
                    System.Windows.Media.CompositionTarget.Rendering -= _latRenderHook;
                    _latRenderHook = null;
                }
            }
            catch { }
        }

        /// <summary>渲染侧统计:实际渲染帧率 + 「捕获开始→开始渲染」延迟(端到端管线延迟,1 帧≈一个刷新周期)。</summary>
        internal static string RenderLatencyReport()
        {
            long ms = Environment.TickCount64 - _renderStartTick;
            double fps = ms <= 0 ? 0.0 : _renderFrames * 1000.0 / ms;
            return string.Format("渲染帧={0} 渲染帧率={1:F1}fps 捕获→开始渲染={2:F1}/{3:F1}/{4:F1}ms(末/均/峰) 样本={5}",
                _renderFrames, fps, _latRenderLastMs,
                _latRenderCount == 0 ? 0.0 : _latRenderSumMs / _latRenderCount, _latRenderMaxMs, _latRenderCount);
        }

        /// <summary>诊断快照:一行人类可读状态,写进日志/取证目录。</summary>
        internal static string DiagnosticSnapshot()
        {
            var s = App.Settings;
            var spec = _spec;
            double busyMs = StatBusyTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string fpsText = spec == null ? "?" : (spec.FpsMs <= 0 ? "不限" : Math.Round(1000.0 / spec.FpsMs).ToString("F0"));
            return string.Format(
                "材质={0} 启用={1} 采样规格={2}x{3}(半分辨率 {4}x{5}) 捕获帧={6} 发布={7} BitBlt={8} 累计忙={9:F1}ms 液态着色器={10} 帧率上限={11} 泵tick={12} 写帧={13} 写锐帧={14} 面板帧={15} 捕获→写屏={16:F1}/{17:F1}/{18:F1}ms(末/均/峰) 捕获相位={19} GPU捕获={20} 空等={21} 脏区跳过={22}",
                s == null ? -1 : s.PillMaterial, _enabled,
                spec == null ? 0 : spec.Width, spec == null ? 0 : spec.Height,
                _latestW, _latestH,
                Interlocked.Read(ref StatFrames), Interlocked.Read(ref StatPublishes),
                Interlocked.Read(ref StatBlits), busyMs,
                _liquid == null ? "无(降级普通玻璃)" : "已启用",
                fpsText,
                Interlocked.Read(ref StatPumps), Interlocked.Read(ref StatWrites), Interlocked.Read(ref StatSharpWrites),
                Interlocked.Read(ref StatPanelFrames),
                _latLastMs, _latCount == 0 ? 0.0 : _latSumMs / _latCount, _latMaxMs,
                _loopPhase,
                _dxgi == null ? "无(回退GDI)" : "DXGI",
                Interlocked.Read(ref StatDxgiIdle), Interlocked.Read(ref StatDxgiSkip));
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
            // 等捕获线程真的退出再释放 GPU 会话:线程若还卡在 AcquireNextFrame/CopyBand 原生调用里
            // 就被 Dispose 掉设备,会踩到已释放的 D3D 对象(进程退出瞬间随机 AV/异常退出码)。
            try
            {
                for (int i = 0; i < 20 && _thread != null && _thread.IsAlive; i++)
                {
                    _wake.Set();
                    _thread.Join(100);
                }
            }
            catch { }
            TraceStop("join 结束 alive=" + (_thread != null && _thread.IsAlive));
            bool captureGone = _thread == null || !_thread.IsAlive;
            DxgiCapture dx = captureGone ? _dxgi : null;   // 线程还在跑就只丢引用,绝不释放它正在用的原生对象
            _dxgi = null; _dxgiTried = true;   // 停止后不再重建(重启走 Start)
            if (dx != null) DisposeDxgiAsync(dx);
            TraceStop("GPU 会话处理完毕");
            RestoreExclusionAll();
            TraceStop("WDA 已还原");
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
            // 旧 root 在被新 Content 替换前仍持有玻璃层的父链(逻辑树里 Parent 不会自动清),
            // 直接 Add 到新 root 会抛「元素已经是另一个元素的逻辑子元素」并中断整次重建
            // (表现为设置改动不生效 + log 里每改一次一条未处理异常)。先摘离旧父节点。
            try
            {
                if (_host.Parent is Panel oldParent) oldParent.Children.Remove(_host);
                else if (_host.Parent is ContentControl oldContent) oldContent.Content = null;
                else if (_host.Parent is Decorator oldDecorator) oldDecorator.Child = null;
            }
            catch { }
            RefreshSpec();
            SetEnabled(true);
            // 展开页着色器提前编好:面板几何全走寄存器,同一份字节码服务任意尺寸,
            // 所以启动时创建一次就能被所有展开页复用,第一次悬停不再卡一次编译(约 100ms)。
            PrewarmPanelEffect();
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
            // 展开页正开着时滑杆要立刻生效:丢掉差分门控的上一帧,让捕获线程马上重出一帧。
            // 材质切离玻璃档(3/4)则直接停掉实时采样,由 ModuleBase.ApplyTheme 恢复主题色背景。
            if (_panelLive)
            {
                if (s.PillMaterial == 3 || s.PillMaterial == 4)
                {
                    _panelPrevSharp = null;
                    try { _panelWake?.Set(); } catch { }
                }
                else
                {
                    _panelLive = false;
                    _panelPrevSharp = null;
                    // 材质切离玻璃档:玻璃层必须自己清空(它是黑色填充+着色器),
                    // 否则展开页会被这块黑底盖住,看不到 ApplyTheme 恢复的主题色。
                    try
                    {
                        if (_panelSurface != null)
                        {
                            _panelSurface.Effect = null;
                            _panelSurface.Fill = Brushes.Transparent;
                        }
                    }
                    catch { }
                    try { _panelWake?.Set(); } catch { }
                }
            }
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

        // ---------- 展开页玻璃(实时) ----------
        //
        // 与顶栏胶囊完全同一条材质管线:展开页的玻璃就是一个挂着 LiquidGlassEffect 的 Rectangle,
        // 折射/色散/rim/斜面明暗全由同一份 HLSL、同一组设置项算 —— 以前是 CPU 端把着色器数学手抄一遍
        // 烘焙进位图,两边参数一有出入,肉眼就是「面板和胶囊不是一个质感」。
        // 实时:抓图线程在展开页显示期间按「当前屏幕上看到的那块矩形」持续采样(缩放/位移动画期间也跟),
        // 所以玻璃内容始终钉在桌面上,不会跟着面板一起被拉伸(那样看起来像贴纸而不是窗玻璃)。
        // 静态画面靠差分门控压成 0 帧发布(UI 侧不重呈、不烧 CPU)。

        private static Border _panelTarget;                 // 当前挂实时玻璃的展开页
        private static Rectangle _panelSurface;             // 玻璃层(带 Effect),铺满整个展开页
        private static LiquidGlassEffect _panelEffect;
        private static WriteableBitmap _panelBodyWb, _panelSharpWb;
        private static ImageBrush _panelBodyBrush, _panelSharpBrush;
        private static readonly ManualResetEventSlim _panelWake = new ManualResetEventSlim(false);
        private static Thread _panelThread;
        private static volatile bool _panelLive;
        private static volatile bool _panelThreadStop;
        // 捕获线程当前停在哪个分支(诊断用):1=等待启用 2=WDA 等待 3=抓图暂停 4=建条带失败 5=被遮挡 6=BitBlt 失败 7=正常
        private static volatile int _loopPhase;
        private static volatile int[] _panelRect;            // 目标屏幕矩形(物理像素) x,y,w,h
        private static double _panelScale = 1.0;             // 动画缩放系数(只影响圆角半径;模糊按真实屏幕强度算)
        private static int[] _panelLastRect;
        private static byte[] _panelPrevSharp;
        private static byte[] _panelPendingBody, _panelPendingSharp;
        private static int _panelPendingW, _panelPendingH, _panelPendingSeq, _panelConsumedSeq;
        private static double _panelPendingRadius;
        private static int _panelPumpQueued;
        private static int _panelStaticRun;        // 连续没出帧的采样次数(用于静止降频)
        private static bool _panelGlassDiag;

        /// <summary>
        /// 启动时把展开页着色器编好(材质 4)。面板几何(半径/纹理尺寸)全部走寄存器,
        /// 同一份字节码服务任意尺寸的展开页,所以这里创建一次,之后所有面板都复用它。
        /// 好处:第一次悬停展开页不用在 UI 线程上现编译(约 100ms,期间面板是空窗)。
        /// </summary>
        private static void PrewarmPanelEffect()
        {
            try
            {
                if (_panelEffect != null) return;
                var s = App.Settings;
                if (s == null || s.PillMaterial != 4) return;
                double rad = 16 * Ui.Scale * NativeMethods.SystemDpiScale() * 0.5;
                _panelEffect = LiquidGlassEffect.CreatePanel(rad, 256, 128);
                if (_panelEffect == null) App.Log("面板玻璃:展开页着色器预热失败,首次打开时会再尝试");
            }
            catch { }
        }

        /// <summary>
        /// 展开页打开「之前」调用(此时窗口还完全不可见):立刻同步抓一帧并挂上玻璃层。
        /// 关键顺序——必须「先出玻璃、再显形」:以前是 Show 之后异步抓图回填,面板会先以主题色出现、
        /// 几十毫秒后才变成模糊贴图,肉眼就是「打开时闪一下才变模糊」。此刻面板不可见,抓图不会拍到面板自己。
        /// </summary>
        internal static void StartLivePanel(Border panel, Rectangle surface, double dipX, double dipY, double dipW, double dipH)
        {
            if (_stopped || panel == null || surface == null) return;
            var s = App.Settings;
            if (s == null || (s.PillMaterial != 3 && s.PillMaterial != 4))
            {
                StopLivePanel(panel);
                return;
            }
            _panelTarget = panel;
            _panelSurface = surface;
            _panelScale = 1.0;
            _panelPrevSharp = null;
            _panelLastRect = null;
            _panelRect = DipRectToPixels(panel, dipX, dipY, dipW, dipH);
            _panelStaticRun = 0;
            _panelLive = true;
            if (_panelRect != null)
            {
                try { PublishPanelFrame(_panelRect[0], _panelRect[1], _panelRect[2], _panelRect[3], 1.0); }
                catch { }
                // 首帧就地泵一次:展开页显形之前玻璃就必须在位,不能等 BeginInvoke
                PanelPumpTick();
            }
            if (_panelThread == null)
            {
                _panelThreadStop = false;
                _panelThread = new Thread(PanelLoop) { IsBackground = true, Name = "YukiDesk.PanelGlass" };
                _panelThread.Start();
            }
            _panelWake.Set();
        }

        /// <summary>
        /// 动画期间每帧由 UI 线程调用(CompositionTarget.Rendering):把「当前肉眼看到的那块」矩形告诉抓图线程。
        /// 面板的打开/收起动画是 RenderTransform 缩放+位移,不是布局变化,窗口矩形与 ActualWidth 都不变,
        /// 所以只能从变换矩阵反算当前可见包围盒。
        /// </summary>
        internal static void UpdateLivePanelRect(Border panel, double dipX, double dipY, double dipW, double dipH, double scale)
        {
            if (!_panelLive || !ReferenceEquals(_panelTarget, panel)) return;
            if (dipW < 2 || dipH < 2) return;
            var rect = DipRectToPixels(panel, dipX, dipY, dipW, dipH);
            if (rect == null) return;
            _panelRect = rect;
            _panelScale = scale <= 0.05 ? 0.05 : (scale > 1.0 ? 1.0 : scale);
            _panelWake.Set();
        }

        /// <summary>取证用:展开页实时玻璃当前状态(一行)。</summary>
        internal static string DiagnosticPanelState()
        {
            var surf = _panelSurface;
            var rect = _panelRect;
            string fx = surf == null ? "无玻璃层" : (surf.Effect == null ? "无着色器" : surf.Effect.GetType().Name);
            string fill = surf == null || surf.Fill == null ? "null" : surf.Fill.GetType().Name;
            return string.Format("实时={0} 玻璃层Fill={1} Effect={2} 采样矩形={3}x{4} 缩放={5:F2} 位图={6}x{7}",
                _panelLive ? "开" : "关", fill, fx,
                rect == null ? 0 : rect[2], rect == null ? 0 : rect[3], _panelScale,
                _panelBodyWb == null ? 0 : _panelBodyWb.PixelWidth, _panelBodyWb == null ? 0 : _panelBodyWb.PixelHeight);
        }

        /// <summary>展开页收起/模块销毁时调用:停采样(玻璃层留着最后一帧,窗口已隐藏,不会重呈)。</summary>
        internal static void StopLivePanel(Border panel)
        {
            if (panel != null && !ReferenceEquals(_panelTarget, panel)) return;
            _panelLive = false;
            _panelRect = null;
            _panelLastRect = null;
            _panelPrevSharp = null;
            _panelTarget = null;
            _panelSurface = null;
            try { _panelWake.Set(); } catch { }
        }

        private static int[] DipRectToPixels(Border panel, double dipX, double dipY, double dipW, double dipH)
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(panel);
                int px = (int)Math.Floor(dipX * dpi.DpiScaleX);
                int py = (int)Math.Floor(dipY * dpi.DpiScaleY);
                int pw = (int)Math.Ceiling(dipW * dpi.DpiScaleX);
                int ph = (int)Math.Ceiling(dipH * dpi.DpiScaleY);
                if (pw < 4 || ph < 4) return null;
                return new[] { px, py, pw, ph };
            }
            catch { return null; }
        }

        private static void PanelLoop()
        {
            GlassDiagnostics.TagThread("面板捕获");
            while (!_panelThreadStop)
            {
                try
                {
                    var rect = _panelRect;
                    if (!_panelLive || _shotPause || rect == null)
                    {
                        // 没有展开页时彻底睡死,不占 CPU(只在 Start/动画帧/设置变更时被 Set 唤醒)
                        _panelWake.Wait(750);
                        _panelWake.Reset();
                        continue;
                    }
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool published = PublishPanelFrame(rect[0], rect[1], rect[2], rect[3], _panelScale);
                    // 静止降频:展开页背后没变化时每帧 BitBlt(固定约 2.4ms)纯属浪费。
                    // 一帧没发布就退到 100ms,持续静止再退到 250ms;一旦有变化立刻回满帧率。
                    if (published) _panelStaticRun = 0; else _panelStaticRun++;
                    var s = App.Settings;
                    // 0 = 不限制(与顶栏同一套语义):按 DWM 合成节拍抓,端到端只延迟一帧
                    int fps = s == null ? 30 : Math.Max(0, Math.Min(300, s.GlassMaxFps));
                    int budget = fps <= 0 ? 0 : 1000 / Math.Max(1, fps);
                    // 面板玻璃同理:静止退避只影响"背后开始动时多久跟上",退太深同样是抽一下卡顿。
                    if (_panelStaticRun > 40) budget = Math.Max(budget, 120);
                    else if (_panelStaticRun > 12) budget = Math.Max(budget, 60);
                    int spent = (int)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000L / System.Diagnostics.Stopwatch.Frequency);
                    if (budget <= 0)
                    {
                        if (!NativeMethods.WaitComposition()) _panelWake.Wait(15);
                        _panelWake.Reset();
                    }
                    else
                    {
                        int wait = budget - spent;
                        if (wait > 1)
                        {
                            _panelWake.Wait(wait - 1);
                            _panelWake.Reset();
                        }
                    }
                }
                catch { try { Thread.Sleep(20); } catch { } }
            }
        }

        /// <summary>
        /// 抓「展开页当前屏幕矩形」→ 半分辨率 → 一份锐帧(折射用)+ 一份盒式模糊 → 染色 → 交给 UI 线程。
        /// 静态画面(矩形与像素都没变)直接不发布:展开页窗口不重呈 = 静态零开销。
        /// </summary>
        private static bool PublishPanelFrame(int px, int py, int pw, int ph, double scale)
        {
            var s = App.Settings;
            if (s == null || (s.PillMaterial != 3 && s.PillMaterial != 4)) return false;
            if (pw < 8 || ph < 8) return false;
            if (scale < 0.05) scale = 0.05;
            double dsy = NativeMethods.SystemDpiScale();
            // 模糊半径不乘 scale:抓到的纹理是「当前可见矩形」按 1:1 映射到元素上的
            // (元素再被 RenderTransform 缩回同样大小),所以采样到的就是屏幕上真实的模糊强度;
            // 乘 scale 反而会让动画中的玻璃比周围桌面更清晰。圆角才需要按 scale 缩。
            int blurHalf = BoxHalfFor(s.GlassBlurRadius * dsy);
            int bleed = Math.Min(220, blurHalf * 2 + 12);
            int x = Math.Max(0, px - bleed), y = Math.Max(0, py - bleed);
            int rx = Math.Min(NativeMethods.ScreenWidth(), px + pw + bleed);
            int by = Math.Min(NativeMethods.ScreenHeight(), py + ph + bleed);
            int cw = (rx - x) & ~1, ch = (by - y) & ~1;
            if (cw < 8 || ch < 8) return false;
            int hw = cw / 2, hh = ch / 2;
            int ox = Math.Max(0, (px - x) / 2), oy = Math.Max(0, (py - y) / 2);
            int ow = Math.Min(hw - ox, Math.Max(2, (pw / 2) & ~1));
            int oh = Math.Min(hh - oy, Math.Max(2, (ph / 2) & ~1));
            if (ow < 4 || oh < 4) return false;

            byte tr = 20, tg = 20, tb = 28;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(s.GlassTint);
                tr = c.R; tg = c.G; tb = c.B;
            }
            catch { }
            double tintA = Math.Max(0, Math.Min(1, s.GlassTintOpacity));

            byte[] body = null, sharp = null;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (NativeMethods.CreateStripCapture(cw, ch, out var screenDc, out var memoryDc, out var dib, out var bits))
            {
                try
                {
                    if (NativeMethods.BlitStrip(memoryDc, cw, ch, screenDc, x, y))
                    {
                        var full = new byte[cw * ch * 4];
                        Marshal.Copy(bits, full, 0, full.Length);
                        var half = new byte[hw * hh * 4];
                        Downsample2x(full, cw, ch, half, hw, hh);
                        // 折射要「未模糊」的高频细节(与顶栏双采样源同一思路):先裁锐帧,再原地模糊后裁主体
                        sharp = CropHalf(half, hw, ox, oy, ow, oh);
                        var blurred = new byte[half.Length];
                        Array.Copy(half, blurred, half.Length);
                        BoxBlur3(blurred, half, hw, hh, blurHalf);
                        body = CropHalf(blurred, hw, ox, oy, ow, oh);
                        BakeTint(body, tr, tg, tb, tintA);
                    }
                }
                finally { NativeMethods.DestroyStripCapture(ref screenDc, ref memoryDc, ref dib); }
            }
            if (body == null || sharp == null) return false;

            // 门控:矩形没变、画面也没变 → 完全不出帧
            var last = _panelLastRect;
            bool rectSame = last != null && last[0] == px && last[1] == py && last[2] == pw && last[3] == ph;
            if (rectSame && _panelPrevSharp != null && _panelPrevSharp.Length == sharp.Length
                && MeanAbsDiff(_panelPrevSharp, sharp, ow, oh, 8) < DiffThreshold)
                return false;
            _panelLastRect = new[] { px, py, pw, ph };
            _panelPrevSharp = sharp;
            double radiusPx = 16 * Ui.Scale * dsy * 0.5 * scale;   // 展开页圆角 → 半分辨率像素
            lock (Gate)
            {
                _panelPendingBody = body;
                _panelPendingSharp = sharp;
                _panelPendingW = ow;
                _panelPendingH = oh;
                _panelPendingRadius = radiusPx;
                _panelPendingSeq++;
            }
            Interlocked.Increment(ref StatPanelFrames);
            SchedulePanelPump();
            if (!_panelGlassDiag)
            {
                _panelGlassDiag = true;
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                App.Log(string.Format("面板玻璃:实时采样 {0}x{1}(物理) 半分辨率 {2}x{3} 缩放={4:F2} 耗时={5:F1}ms",
                    pw, ph, ow, oh, scale, ms));
            }
            return true;
        }

        /// <summary>从半分辨率帧里裁出子矩形(按行 BlockCopy,帧很小,够用)。</summary>
        private static byte[] CropHalf(byte[] src, int sw, int sx, int sy, int w, int h)
        {
            var dst = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(src, ((sy + y) * sw + sx) * 4, dst, y * w * 4, w * 4);
            return dst;
        }

        /// <summary>有新帧才唤醒 UI 线程;静止/收起时 0 唤醒(与顶栏泵同一套合并去重)。</summary>
        private static void SchedulePanelPump()
        {
            if (_stopped) return;
            if (Interlocked.Exchange(ref _panelPumpQueued, 1) != 0) return;
            try
            {
                var d = Application.Current == null ? null : Application.Current.Dispatcher;
                if (d == null || d.HasShutdownStarted) { Interlocked.Exchange(ref _panelPumpQueued, 0); return; }
                d.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    Interlocked.Exchange(ref _panelPumpQueued, 0);
                    PanelPumpTick();
                }));
            }
            catch { Interlocked.Exchange(ref _panelPumpQueued, 0); }
        }

        /// <summary>UI 线程:写位图 → 挂/更新 LiquidGlassEffect(几何只改寄存器,不重编译)。</summary>
        private static void PanelPumpTick()
        {
            if (_stopped) return;
            var surface = _panelSurface;
            if (surface == null) return;
            byte[] body, sharp; int w, h, seq; double rad;
            lock (Gate)
            {
                body = _panelPendingBody;
                sharp = _panelPendingSharp;
                w = _panelPendingW;
                h = _panelPendingH;
                seq = _panelPendingSeq;
                rad = _panelPendingRadius;
            }
            if (body == null || sharp == null || w < 4 || h < 4 || seq == _panelConsumedSeq) return;
            _panelConsumedSeq = seq;
            var s = App.Settings;
            if (s == null) return;
            bool liquid = s.PillMaterial == 4;
            if (_panelBodyWb == null || _panelBodyWb.PixelWidth != w || _panelBodyWb.PixelHeight != h)
            {
                _panelBodyWb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                _panelSharpWb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                _panelBodyBrush = new ImageBrush(_panelBodyWb) { Stretch = Stretch.Fill };
                _panelSharpBrush = new ImageBrush(_panelSharpWb) { Stretch = Stretch.Fill };
            }
            _panelBodyWb.WritePixels(new Int32Rect(0, 0, w, h), body, w * 4, 0);
            _panelSharpWb.WritePixels(new Int32Rect(0, 0, w, h), sharp, w * 4, 0);
            if (liquid)
            {
                if (_panelEffect == null)
                {
                    _panelEffect = LiquidGlassEffect.CreatePanel(rad, w, h);
                    if (_panelEffect == null) App.Log("面板玻璃:液态着色器不可用,降级为普通模糊");
                }
                if (_panelEffect != null)
                {
                    // 几何(半径/纹理尺寸)全走寄存器:动画期间每帧变尺寸也不用重编译
                    _panelEffect.Radius = rad;
                    _panelEffect.TexW = w;
                    _panelEffect.TexH = h;
                    _panelEffect.Refr = Math.Max(0, Math.Min(1, s.GlassRefraction));
                    _panelEffect.Rim = Math.Max(0, Math.Min(1, s.GlassRim));
                    _panelEffect.Chroma = Math.Max(0, Math.Min(1, s.GlassChroma));
                    _panelEffect.Sheen = LiquidSheen;
                    // 效果对象重建/位图换尺寸后必须重绑采样源(不重绑会采到全黑,顶栏踩过这个坑)
                    if (!ReferenceEquals(_panelEffect.Input, _panelBodyBrush)) _panelEffect.Input = _panelBodyBrush;
                    if (!ReferenceEquals(_panelEffect.Sharp, _panelSharpBrush)) _panelEffect.Sharp = _panelSharpBrush;
                    if (!ReferenceEquals(surface.Effect, _panelEffect)) surface.Effect = _panelEffect;
                }
            }
            // 玻璃层的基础填充始终是「模糊位图」:着色器正常时它的输出会覆盖掉它;
            // 着色器缺失/正在重建时看到的是模糊玻璃而不是黑底,任何一瞬间都不会露黑。
            if (!ReferenceEquals(surface.Fill, _panelBodyBrush)) surface.Fill = _panelBodyBrush;
            if (_panelEffect == null || !ReferenceEquals(surface.Effect, _panelEffect)) surface.Effect = null;
            surface.Opacity = Math.Max(0.05, Math.Min(1, s.GlassOpacity));
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
            // 帧率上限 0 = 不限制:捕获按 DWM 合成节拍跑(等于屏幕刷新率),不设人为上限
            int fps = s == null ? 30 : Math.Clamp(s.GlassMaxFps, 0, 300);
            var spec = new GlassSpec
            {
                Width = Math.Max(16, physW + 1) & ~1,
                Height = Math.Max(8, h + 1) & ~1,
                BlurHalf = BoxHalfFor((s == null ? 24 : s.GlassBlurRadius) * dsy),
                FpsMs = fps <= 0 ? 0 : Math.Max(2, 1000 / fps),   // 0=不限制(按合成节拍跑)
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
                // 玻璃关掉时展开页也停止实时采样(面板随即由主题色接管)
                _panelLive = false;
                _panelPrevSharp = null;
                RestoreExclusionAll();
                _wake.Set();
                try { _panelWake?.Set(); } catch { }
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
            bool wroteFrame = false;
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
                    wroteFrame = true;
                }
                // 延迟样本只在"真的写了新帧"之后结算,并且丢弃 >500ms 的陈旧样本
                // (静止很久后的第一帧会把整段静止时间也计入,那不是延迟)。
                long ct = wroteFrame ? Interlocked.Read(ref _pubCaptureTick) : 0;
                if (ct != 0)
                {
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - ct) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (ms <= 500.0)
                    {
                        _latLastMs = ms;
                        _latSumMs += ms;
                        if (ms > _latMaxMs) _latMaxMs = ms;
                        _latCount++;
                    }
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

        /// <summary>
        /// 懒建 GPU 捕获会话。失败只记一次日志并回退 GDI BitBlt。
        /// </summary>
        /// <summary>
        /// 释放 GPU 捕获会话。Stop 只在进程退出时调用,而 DXGI 会话的 Release 偶尔会在驱动里
        /// 卡住(实测卡死 12 秒以上,导致托盘"退出"后进程僵着、exe 被锁、下次启动失败),
        /// 所以放到后台线程做:最多等 400ms,超时就丢下不管(进程马上结束,系统会回收)。
        /// </summary>
        /// <summary>退出期追踪:直接追加到 %TEMP%\yuki-exit.txt,绕开共享日志互斥体(日志锁本身也可能卡住)。</summary>
        internal static void TraceStop(string what)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yuki-exit.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " PID=" + System.Diagnostics.Process.GetCurrentProcess().Id + " " + what + Environment.NewLine);
            }
            catch { }
        }

        private static void DisposeDxgiAsync(DxgiCapture dx)
        {
            // 实测:在退出路径上 Release IDXGIOutputDuplication/ID3D11Device 有一半概率在显示驱动里
            // 卡住十几秒甚至直接 AV(进程退出码 139)——这是驱动侧的行为,不是我们引用的生命周期错。
            // Stop 只在进程退出时调用,所以最稳的做法是**不释放**:直接丢引用,交给系统在建进程销毁时回收。
            // 唯一代价是退出瞬间多占约 8MB 显存,进程结束后消失。
            TraceStop("跳过 GPU 显式释放(避免驱动卡死),引用已丢弃");
        }

        private static void EnsureDxgi()
        {
            if (_dxgi != null || _dxgiTried) return;
            _dxgiTried = true;
            // 对照/兜底开关:强制走 GDI(排查"GPU 捕获是否影响观感/稳定性"时用)。
            if (Environment.GetEnvironmentVariable("YUKI_GLASS_GDI") == "1") return;
            try { _dxgi = DxgiCapture.TryCreate(); } catch (Exception ex) { _dxgi = null; try { App.Log("玻璃:GPU 捕获异常 " + ex.Message); } catch { } }
            if (_dxgi != null)
            {
                try { App.Log(string.Format("玻璃:GPU 捕获已启用(DXGI Desktop Duplication {0}x{1}),AcquireNextFrame 自带合成节拍", _dxgi.Width, _dxgi.Height)); } catch { }
            }
        }

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
                        _loopPhase = 1;
                        _wake.Wait(400);
                        _wake.Reset();
                        continue;
                    }
                    if (Environment.TickCount64 < Volatile.Read(ref _holdUntil))
                    {
                        _loopPhase = 2;
                        Thread.Sleep(100);
                        continue;
                    }
                    // 取证抓图进行中:WDA 已被临时恢复(顶栏此刻可被捕获),
                    // 必须停止 BitBlt,否则就会把自己拍进下一帧形成正反馈。
                    if (_shotPause)
                    {
                        _loopPhase = 3;
                        Thread.Sleep(50);
                        continue;
                    }
                    // 会话失效/首次进入时懒建 GPU 捕获(失败则永远走 GDI)。
                    if (_dxgi == null && !_dxgiTried) EnsureDxgi();

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
                            _loopPhase = 4;
                            Thread.Sleep(1000);
                            continue;
                        }
                    }

                    // 遮挡保护:顶栏被普通窗口大面积盖住时(全屏游戏/最大化窗口)跳过采样,
                    // 反正玻璃也不可见——省 BitBlt 也避免独占全屏下抓到黑帧。
                    if (IsCovered(spec))
                    {
                        _loopPhase = 5;
                        Thread.Sleep(300);
                        continue;
                    }

                    long busyStart;
                    if (_dxgi != null)
                    {
                        // 等下一次桌面合成:静止时超时返回(完全不产生 CPU 负载),有变化时立刻返回。
                        int waitMs = spec.FpsMs <= 0 ? 100 : Math.Min(100, Math.Max(1, spec.FpsMs));
                        int acq = _dxgi.Acquire(waitMs);
                        if (acq == 1)
                        {
                            Interlocked.Increment(ref StatDxgiIdle);
                            continue;
                        }
                        if (acq != 0)
                        {
                            // 会话失效:切分辨率/全屏独占/安全桌面。丢掉重建,期间这一轮先跳过。
                            try { _dxgi.Dispose(); } catch { }
                            _dxgi = null;
                            _dxgiTried = false;
                            hasBaseline = false;
                            _loopPhase = 8;
                            Thread.Sleep(300);
                            continue;
                        }
                        if (hasBaseline && !_dxgi.BandDirty(0, 0, capW, capH))
                        {
                            // 脏矩形不碰顶栏条带:这一帧与玻璃无关,连读回都省。
                            _dxgi.ReleaseFrame();
                            Interlocked.Increment(ref StatDxgiSkip);
                            // 本机 DWM 是持续 300Hz 合成的,不睡觉就会每秒空转 300 次(实测常驻 3%+);
                            // 退避到 33ms 后静止开销≈0,而"背后开始动"最多 33ms 就被发现。
                            long idleSkip = Environment.TickCount64 - _lastPubTick;
                            if (idleSkip > 400) Thread.Sleep(idleSkip > 1200 ? 33 : 8);
                            continue;
                        }
                        Interlocked.Increment(ref StatFrames);
                        Interlocked.Increment(ref StatBlits);
                        long startD = System.Diagnostics.Stopwatch.GetTimestamp();
                        bool copied = _dxgi.CopyBand(0, 0, capW, capH, full);
                        _dxgi.ReleaseFrame();
                        if (!copied)
                        {
                            _loopPhase = 6;
                            Thread.Sleep(50);
                            continue;
                        }
                        Interlocked.Add(ref StatBlitTicks, System.Diagnostics.Stopwatch.GetTimestamp() - startD);
                        busyStart = startD;
                    }
                    else
                    {
                        busyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        Interlocked.Increment(ref StatFrames);
                        if (!NativeMethods.BlitStrip(memoryDc, capW, capH, screenDc, 0, 0))
                        {
                            _loopPhase = 6;
                            Thread.Sleep(250);
                            continue;
                        }
                        Interlocked.Increment(ref StatBlits);
                        long tBlit = System.Diagnostics.Stopwatch.GetTimestamp();
                        Marshal.Copy(bits, full, 0, full.Length);
                        Interlocked.Add(ref StatBlitTicks, System.Diagnostics.Stopwatch.GetTimestamp() - tBlit);
                    }
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
                    // 闸门按帧率缩放(≈半速):30fps 时约 66ms,60fps 时 33ms,300fps 时 6ms。
            int pubGateMs = Math.Max(2, spec.FpsMs * 2);
            if (publish && !_alwaysPub && diff < SmallDiffThreshold && nowTick - _lastPubTick < pubGateMs) publish = false;
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
                        Interlocked.Increment(ref StatPublishes);
                        Interlocked.Exchange(ref _pubCaptureTick, busyStart);
                        SchedulePump();   // 有新帧才唤醒 UI 线程(静止时 0 唤醒)
                        if (!loggedSelf)
                        {
                            loggedSelf = true;
                            SelfCheck(workA, hw, hh, out double m, out double sd, out long n);
                            App.Log(string.Format("玻璃:首帧 {0}x{1}(半分辨率) 红通道均值={2:F1} 标准差={3:F1} 采样={4}",
                                hw, hh, m, sd, n));
                        }
                    }
                    Interlocked.Add(ref StatBusyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - busyStart);

                    // 自适应降频:连续静止就拉长采样间隔,静止桌面接近 0 开销;
                    // 任何变化(或被唤醒)都会立刻回到满速。
                    // 采样间隔就是「玻璃多久看一眼背后」= 用户感知的更新延迟上限,所以不能拉太长了:
                    // 之前深静止档是 1000ms,桌面静止 22 秒后任何运动都要最多 1 秒才反映到玻璃上,
                    // 观感就是"液态玻璃卡住了/很慢"。现在上限 250ms(深静止)/120ms(静止),
                    // 一次 BitBlt 的固定成本约 2.5ms,深静止档常驻约 1%。20fps 的持续大动限帧也已取消,
                    // 持续运动时直接跑满「帧率上限」设置(默认 30fps)。
                    _loopPhase = 7;
                    if (_dxgi != null)
                    {
                        // GPU 捕获自带节拍(AcquireNextFrame 卡在合成上),但本机 DWM 是持续 300Hz 合成的,
                        // 长时间没有东西要显示时每帧唤醒(脏矩形查询+同步)也要约 15% 单核,所以"无事发生"时要退避。
                        // 但退避档位不能太深:静止时一次迭代只做「取帧+脏矩形判断」、不拷贝不模糊,
                        // 代价不到 0.1ms,而采样间隔就是「玻璃多久看一眼背后」= 变化被发现前的最大延迟。
                        // 实测 250ms 档(4fps)在"静止一会儿→内容突然开始动"时会被看成抽一下卡顿,
                        // 所以深静止档收到 33ms(30fps)、中间档 8ms(120fps):静态开销几乎不变,起步延迟降 7 倍。
                        long idleMsD = Environment.TickCount64 - _lastPubTick;
                        if (idleMsD > 400)
                        {
                            Thread.Sleep(idleMsD > 1200 ? 33 : 8);
                            continue;
                        }
                        long spentTicks = System.Diagnostics.Stopwatch.GetTimestamp() - busyStart;
                        int spent = (int)(spentTicks * 1000L / System.Diagnostics.Stopwatch.Frequency);
                        if (spec.FpsMs > spent) Thread.Sleep(spec.FpsMs - spent);
                        continue;
                    }
                    int delay = spec.FpsMs;
                    long idleMs = Environment.TickCount64 - _lastPubTick;
                    if (delay <= 0)
                    {
                        // 不限制帧率:节拍交给合成器。DwmFlush 在下一次合成完成后返回,
                        // 紧接着抓屏(约 5ms)→ 发布 → UI 线程写位图,仍赶得上这一轮渲染提交,
                        // 所以端到端只延迟一帧(以前固定 Sleep 会和合成节拍错相,经常晚一帧)。
                        // 背后完全静止时仍降频:60Hz 常驻 BitBlt 会白烧约 15% 单核。
                        if (idleMs > 1200) Thread.Sleep(33);
                        else if (idleMs > 400) Thread.Sleep(8);
                        else if (!NativeMethods.WaitComposition()) Thread.Sleep(16);
                        continue;
                    }
                    // 固定帧率档:静止也不用退到 250ms——那等于"静止 1 秒后任何变化都要 0.25 秒才反映"。
                    if (idleMs > 1200) delay = Math.Max(delay, 33);
                    else if (idleMs > 400) delay = Math.Max(delay, 8);
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
