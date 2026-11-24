using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopBar.Interop;
using DesktopBar.Models;
using DesktopBar.UI;

namespace DesktopBar.Services
{
    /// <summary>
    /// 实时背景玻璃(PillMaterial=3):捕获顶栏背后的真实屏幕内容(窗口+桌面图标+壁纸),
    /// 高斯模糊后按胶囊并集铺在主窗口最底层——Apple/小米式"真·毛玻璃"。悬停展开页同理。
    ///
    /// 性能设计(实测教训:WPF BlurEffect 铺满 2560 宽条带会把渲染线程打满一个核):
    ///   - 主体用四分之一分辨率和三趟盒式模糊，轮廓保留半分辨率锐帧，
    ///     WPF 侧只剩一张普通位图,时钟 250ms/频谱 60fps 的局部重绘不再触发任何模糊重算;
    ///   - 降采样差分门控:静止桌面 0 写屏;遮挡(全屏窗口盖住顶栏)时 0 采样;
    ///   - 展开页实时采样固定区域；开合时移动纹理视口，静止不重新模糊或上传。
    ///
    /// 捕获配方(根因报告 §9.12.1):GetDC(NULL)+CreateDIBSection(top-down)+BitBlt(SRCCOPY);
    /// 截图兼容模式只采集玻璃下方的窗口并与壁纸合成。整屏模式排除自家窗口以防自采。
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
            public int BodyWidth => Width / 4;
            public int BodyHeight => Height / 4;
            public int BlurQuarter;
            public int FpsMs;            // 捕获基础间隔
            public int WallpaperFadeMs;
            public bool Liquid;
            public bool UseWindowBackdrop;
            public bool WallpaperOnly;
            public int DisplayGeneration;
            public int MidY;             // 遮挡探测采样点的物理 y(胶囊中线)
            public double ImageHeightDip;
        }

        private static readonly object Gate = new object();
        private static readonly HashSet<IntPtr> _excluded = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, long> _hiddenPanelExclusions = new();
        private static double ReadEnvDouble(string name, double fallback)
        {
            double v;
            if (double.TryParse(Environment.GetEnvironmentVariable(name), out v)) return v;
            return fallback;
        }

        // 斜面明暗(方向光高光/背光压暗):胶囊与展开页共用一个强度,保证是同一种材质。
        private static readonly double LiquidSheen = ReadEnvDouble("YUKI_LIQUID_SHEEN", 1.0);
        private static volatile GlassSpec _spec;

        private static bool _started;
        private static volatile bool _stopped, _enabled, _active = true, _stopThread;
        // 偶数为桌面底图，奇数为前台底图。编号同时区分快速连续的唤出。
        private static volatile int _barBackdropGeneration;
        private static volatile bool _revealNeedsWindow;
        private static long _holdUntil;
        private sealed record PanelExclusion(int Session, long Until);
        private static volatile PanelExclusion _panelExclusion;
        private static IntPtr _barWindow;

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
        internal static long StatPanelCaptures, StatPanelBlurs, StatPanelWrites, StatPanelSharpWrites, StatPanelWarmOpens;
        internal static long StatDxgiIdle;      // GPU 捕获:等不到新合成帧的次数(桌面没变化,零成本)
        internal static long StatDxgiSkip;      // GPU 捕获:拿到帧但脏矩形不碰顶栏条带,整帧跳过
        // 分阶段耗时(Stopwatch 刻度):定位"4~5ms 一帧到底花在哪"
        internal static long StatBlitTicks, StatDownTicks, StatDiffTicks, StatBlurTicks;
        internal static bool HasFrame { get { return _latest != null; } }
        internal static int LatestWidth { get { return _latestW; } }
        internal static int LatestHeight { get { return _latestH; } }

        private static Thread _thread;
        private static readonly AutoResetEvent _wake = new AutoResetEvent(false);

        // 发布区(捕获线程写,UI 线程读,全部在 Gate 下)。轮换池 3 块:
        // UI 线程 WritePixels 拷贝(~0.5ms)与捕获线程复用之间至少隔 2 帧,杜绝撕裂。
        private static readonly byte[][] _rotate = new byte[3][];
        // 未模糊帧也走三块轮换池:着色器轮廓折射要用它(见 LiquidGlassEffect.Sharp)。
        private static readonly byte[][] _rotateSharp = new byte[3][];
        private static int _rotateIndex;
        private static byte[] _latest, _latestSharp;
        private static long _latestSequence, _consumedSequence, _latestCaptureTick;
        private static bool _latestSlideBlending;
        private static int _latestBackdropGeneration;
        private static int _latestW, _latestH, _displayGeneration;
        private static GlassSpec _latestSpec;

        // 玻璃层三件套(UI 线程),跨 ApplySettings 全量重建复用,避免重建瞬间空一帧
        private static Grid _host;
        private static Image _image;
        private static Border _tint;
        private static WriteableBitmap _wb, _wbSharp;
        private static bool _bodyReady;
        private static int _appliedBackdropGeneration;
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
        private static bool IsGlass => App.ActiveSettings?.PillMaterial == 3 || App.ActiveSettings?.PillMaterial == 4;
        private static bool ShouldExcludeWindows => _enabled && App.ActiveSettings is { ShowInScreenCapture: false, WallpaperOnlyCapture: false };

        // 捕获线程的静止计数(自适应降频:静止越久采样越稀)
        // 静止程度用"距离上次发布多久"衡量(TickCount64 毫秒),不用帧数:
        // 帧数阈值在 60fps 设置下要等 0.5~1.5 秒才降频,期间白烧 12% 单核。
        private static long _lastPubTick;   // 上次发布的时刻(TickCount64),用于小幅漂移限流
        // 「捕获开始 → UI 线程写进 WriteableBitmap」的延迟统计,用于回答"玻璃有几帧延迟"。
        private static long _pubCaptureTick, _panelWrittenCaptureTick;
        private static double _latLastMs, _latSumMs, _latMaxMs;
        private static int _latCount;
        // 「捕获开始 → WPF 开始渲染这一帧」= 端到端管线延迟(一个屏幕刷新周期 ≈ 一帧)。
        // 只在 --glassperf 里临时订阅 CompositionTarget.Rendering:常驻订阅会让 WPF 渲染循环永不歇,白烧 CPU。
        private static EventHandler _latRenderHook;
        private static RenderLatencySamples _barRenderSamples, _panelRenderSamples;
        private static int _renderFrames;
        private static long _renderStartTick;
        // 诊断开关:跳过静止差分门控,强制每帧发布(=模拟"顶栏背后一直在动"的最坏情况,如动态壁纸/视频)。
        private static bool _alwaysPub;

        // GPU 捕获(DXGI Desktop Duplication)。抓一帧约 0.35ms(BitBlt 是 3.5~6ms),
        // 而且 AcquireNextFrame 会阻塞到下一次桌面合成 → 节拍自动等于刷新率、端到端只延迟一帧。
        // 设备初始化失败(老系统/受保护内容/会话被占用)时回退 GDI,功能不受影响。
        private static volatile DxgiCapture _dxgi;
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
            _panelWake.Set();
        }

        private sealed class RenderLatencySamples
        {
            private readonly double[] _values = new double[8192];
            private int _count, _next;
            private long _seen;

            public RenderLatencySamples(long alreadyWritten) { _seen = alreadyWritten; }

            public void Add(long captureTick, long renderTick)
            {
                if (captureTick == 0 || captureTick == _seen) return;
                _seen = captureTick;
                double ms = (renderTick - captureTick) * 1000.0 / Stopwatch.Frequency;
                if (ms < 0) return;
                _values[_next] = ms;
                _next = (_next + 1) % _values.Length;
                _count = Math.Min(_count + 1, _values.Length);
            }

            public string Report()
            {
                if (_count == 0) return "无新帧";
                double[] ordered = _values.AsSpan(0, _count).ToArray();
                Array.Sort(ordered);
                double median = ordered[(int)Math.Ceiling(ordered.Length * 0.50) - 1];
                double p95 = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
                return $"P50={median:F2}ms P95={p95:F2}ms 峰值={ordered[ordered.Length - 1]:F2}ms 样本={_count}";
            }
        }

        /// <summary>仅在自检期间订阅 WPF 渲染事件，记录已经写入位图的帧。</summary>
        internal static void SetRenderLatencyProbe(bool on)
        {
            if (on && _latRenderHook == null)
            {
                _barRenderSamples = new RenderLatencySamples(Interlocked.Read(ref _pubCaptureTick));
                _panelRenderSamples = new RenderLatencySamples(Interlocked.Read(ref _panelWrittenCaptureTick));
                _renderFrames = 0;
                _renderStartTick = Environment.TickCount64;
                _latRenderHook = (s, e) =>
                {
                    _renderFrames++;
                    long now = Stopwatch.GetTimestamp();
                    _barRenderSamples.Add(Interlocked.Read(ref _pubCaptureTick), now);
                    _panelRenderSamples.Add(Interlocked.Read(ref _panelWrittenCaptureTick), now);
                };
                CompositionTarget.Rendering += _latRenderHook;
            }
            else if (!on && _latRenderHook != null)
            {
                CompositionTarget.Rendering -= _latRenderHook;
                _latRenderHook = null;
            }
        }

        internal static string RenderLatencyReport()
        {
            long ms = Environment.TickCount64 - _renderStartTick;
            double fps = ms <= 0 ? 0.0 : _renderFrames * 1000.0 / ms;
            return $"WPF渲染帧={_renderFrames} 帧率={fps:F1}fps 捕获→渲染回调"
                + $" 顶栏[{_barRenderSamples?.Report()}] 面板[{_panelRenderSamples?.Report()}]";
        }

        /// <summary>诊断快照:一行人类可读状态,写进日志/取证目录。</summary>
        internal static string DiagnosticSnapshot()
        {
            var s = App.ActiveSettings;
            var spec = _spec;
            double busyMs = StatBusyTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string fpsText = spec == null ? "?" : EffectiveFps(s?.GlassMaxFps ?? 0, panel: false).ToString();
            return string.Format(
                "材质={0} 启用={1} 采样规格={2}x{3}(主体 {4}x{5}) 捕获帧={6} 发布={7} BitBlt={8} 累计忙={9:F1}ms 液态着色器={10} 帧率上限={11} 泵tick={12} 写帧={13} 写锐帧={14} 面板帧={15} 捕获→写屏={16:F1}/{17:F1}/{18:F1}ms(末/均/峰) 捕获相位={19} GPU捕获={20} 空等={21} 脏区跳过={22}",
                s == null ? -1 : s.PillMaterial, _enabled,
                spec == null ? 0 : spec.Width, spec == null ? 0 : spec.Height,
                _latestW, _latestH,
                Interlocked.Read(ref StatFrames), Interlocked.Read(ref StatPublishes),
                Interlocked.Read(ref StatBlits), busyMs,
                _liquid != null && ReferenceEquals(_image?.Effect, _liquid) ? "已启用" : "无",
                fpsText,
                Interlocked.Read(ref StatPumps), Interlocked.Read(ref StatWrites), Interlocked.Read(ref StatSharpWrites),
                Interlocked.Read(ref StatPanelFrames),
                _latLastMs, _latCount == 0 ? 0.0 : _latSumMs / _latCount, _latMaxMs,
                _loopPhase,
                _dxgi == null ? "无(回退GDI)" : "DXGI",
                Interlocked.Read(ref StatDxgiIdle), Interlocked.Read(ref StatDxgiSkip));
        }

        /// <summary>取证:把当前发布的玻璃主体另存为 PNG。</summary>
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
            _panelThreadStop = true;
            StopLivePanel(null);
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
            // 按现有退出策略把最后一个 DXGI 会话交给进程回收，避免驱动 Release 阻塞退出。
            if (_thread == null || !_thread.IsAlive) _dxgi = null;
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
            var s = App.ActiveSettings;
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
            _image.Opacity = s.GlassOpacity;
            SetFallbackTint(s);
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
            var s = App.ActiveSettings;
            if (s == null) return;
            ThemeService.Refresh();
            RefreshSpec();
            if (_image != null) _image.Opacity = Math.Max(0.05, Math.Min(1, s.GlassOpacity));
            SetFallbackTint(s);
            try { MainWindow.Bar?.RefreshGlassClip(); } catch { }
            RefreshPanelMaterial(s);
        }

        internal static void ApplyCaptureVisibility()
        {
            if (_stopped || !_enabled || App.ActiveSettings == null) return;
            // Arm the capture delay and hide these windows before enabling the screen-backed readers.
            if (!_shotPause) ApplyExclusionAll();
            RefreshSpec();
            RefreshPanelMaterial(App.ActiveSettings);
            if (!ShouldExcludeWindows) RestoreExclusionAll();
            _panelWake.Set();
        }

        private static bool _liquidAttempted;
        private static int _wallpaperGeneration;

        internal readonly record struct GlassLook(double Blur, Color Tint, double TintOpacity,
            double Refraction, double Rim, double Chroma, double Lift);

        internal static double FrostLevel(AppSettings settings) => settings.GlassFrost ?? (settings.GlassStyle switch
        {
            1 => 0.75, 2 => 0.50, _ => 0.25
        });

        internal static GlassLook CurrentLook(AppSettings settings, double? frostPreview = null)
        {
            if (settings.PillMaterial != 4) return CustomLook(settings);

            double frost = Math.Clamp(frostPreview ?? FrostLevel(settings), 0, 1);
            if (settings.GlassStyle == 2)
            {
                var custom = CustomLook(settings);
                // 50% 等于自定义面板里的基准模糊和染色；雾面不会改动折射参数。
                double blur = frost <= 0.50 ? 2 + (custom.Blur - 2) * frost * 2
                    : custom.Blur + (Math.Min(60, custom.Blur * 2) - custom.Blur) * (frost * 2 - 1);
                return custom with { Blur = blur, TintOpacity = Math.Min(1, custom.TintOpacity * frost * 2) };
            }

            // 25% 和 75% 分别保留原清透与柔雾的强度。
            double radius = frost <= 0.25 ? 2 + frost * 36 : 11 + (frost - 0.25) * 22;
            double tint = frost <= 0.25 ? frost * 0.22 : 0.055 + (frost - 0.25) * 0.19;
            return settings.GlassStyle == 1
                ? new GlassLook(radius, Color.FromRgb(252, 246, 237), tint, 0.39, 0.46, 0, 0.21)
                : new GlassLook(radius, Color.FromRgb(250, 244, 236), tint, 0.63, 0.60, 0, 0.24);
        }

        private static GlassLook CustomLook(AppSettings settings) =>
            new GlassLook(settings.GlassBlurRadius, Ui.ParseColor(settings.GlassTint), settings.GlassTintOpacity,
                settings.GlassRefraction, settings.GlassRim, settings.GlassChroma, 0);

        internal static bool UsesAdaptiveInk(AppSettings settings) =>
            settings?.PillMaterial is 3 or 4 && ThemeService.FollowsBackground(settings);

        internal static void WallpaperChanged()
        {
            Interlocked.Increment(ref _wallpaperGeneration);
            _wake.Set();
            _panelWake.Set();
        }

        internal static int EffectiveFps(int configured, bool panel) => configured > 0 ? configured : panel ? 36 : 48;

        private static void SetFallbackTint(AppSettings settings)
        {
            if (_tint == null) return;
            var look = CurrentLook(settings);
            var brush = new SolidColorBrush(look.Tint);
            brush.Freeze();
            _tint.Background = brush;
            _tint.Opacity = look.TintOpacity;
        }

        private static readonly double[] LinearChannels = BuildLinearChannels();

        private static double[] BuildLinearChannels()
        {
            var table = new double[256];
            for (int i = 0; i < table.Length; i++)
            {
                double c = i / 255.0;
                table[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return table;
        }

        // 采样文字所在的胶囊中部，并在计算对比度前近似合成实际的提亮、染色和覆盖度。
        internal static double SurfaceLightness(byte[] pixels, int width, int height, Rect area, GlassLook look, double opacity)
            => SurfaceLightness(pixels, width, height, area, look, opacity, out _);

        internal static double SurfaceLightness(byte[] pixels, int width, int height, Rect area, GlassLook look, double opacity, out double spread)
        {
            double total = 0;
            double low = 1, high = 0;
            double tint = look.TintOpacity * opacity;
            double redTint = look.Tint.R * tint, greenTint = look.Tint.G * tint, blueTint = look.Tint.B * tint;
            for (int row = 1; row <= 3; row++)
            {
                int y = Math.Clamp((int)((area.Y + area.Height * row / 4) * height), 0, height - 1);
                for (int col = 1; col <= 5; col++)
                {
                    int x = Math.Clamp((int)((area.X + area.Width * col / 6) * width), 0, width - 1);
                    int i = (y * width + x) * 4;
                    byte r = pixels[i + 2], g = pixels[i + 1], b = pixels[i];
                    double dark = 1 - (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
                    double lift = look.Lift * dark * dark * opacity * (1 - look.TintOpacity);
                    double source = 1 - tint - lift;
                    int rr = (int)(source * r + lift * 255 + redTint + 0.5);
                    int gg = (int)(source * g + lift * 255 + greenTint + 0.5);
                    int bb = (int)(source * b + lift * 255 + blueTint + 0.5);
                    double sample = 0.2126 * LinearChannels[rr] + 0.7152 * LinearChannels[gg] + 0.0722 * LinearChannels[bb];
                    total += sample;
                    low = Math.Min(low, sample);
                    high = Math.Max(high, sample);
                }
            }
            spread = high - low;
            return total / 15;
        }

        private static void RefreshPillInk(byte[] body, int width, int height, GlassSpec spec)
        {
            var bar = MainWindow.Bar;
            var settings = App.ActiveSettings;
            if (bar == null || body == null || width == 0 || height == 0) return;
            if (!UsesAdaptiveInk(settings)) return;
            var look = CurrentLook(settings);
            if (_image?.Effect == null) look = look with { Lift = 0 };
            bar.UpdateGlassInk(body, width, height, spec.ImageHeightDip, look, settings.GlassOpacity);
        }

        private static void UpdateLiquidConstants()
        {
            var s = App.ActiveSettings;
            var spec = _spec;
            var bar = MainWindow.Bar;
            if (_image == null || s == null || spec == null || bar == null) return;
            if (s.PillMaterial == 4 && !_liquidAttempted)
            {
                _liquidAttempted = true;
                _liquid = LiquidGlassEffect.CreatePills(1, spec.HalfWidth, spec.HalfHeight);
                App.Log(_liquid != null ? "液态玻璃:胶囊着色器已启用" : "液态玻璃:胶囊使用普通玻璃");
            }
            var effect = s.PillMaterial == 4 ? _liquid : null;
            if (!UsesAdaptiveInk(s)) bar.ResetGlassInk();
            if (effect != null)
            {
                double sx = spec.HalfWidth / Math.Max(1, bar.ActualWidth);
                double sy = spec.HalfHeight / Math.Max(1, spec.ImageHeightDip);
                double radius = 19 * Ui.Scale * sy;
                SetGeometry(effect, radius, spec.HalfWidth, spec.HalfHeight);
                effect.SetPills(_pillRectsDip, sx, sy, EdgeWidth(radius));
                SetOptics(effect, s);
                SyncLiquidInput();
            }
            ShowGlassEffect(effect);
            lock (Gate)
            {
                if (!_latestSlideBlending && _latestBackdropGeneration == _barBackdropGeneration
                    && SameCaptureSpec(spec, _latestSpec))
                    RefreshPillInk(_latest, _latestW, _latestH, spec);
            }
        }

        private static void ShowGlassEffect(LiquidGlassEffect effect)
        {
            var visible = _bodyReady && _image.Visibility == Visibility.Visible ? effect : null;
            if (_image.Effect != visible) _image.Effect = visible;
            if (_tint != null) _tint.Visibility = visible == null ? Visibility.Visible : Visibility.Collapsed;
        }

        private static double EdgeWidth(double radius) => Math.Max(1, radius * 0.55);

        private static void SetGeometry(LiquidGlassEffect effect, double radius, double width, double height)
        {
            effect.TexW = width;
            effect.TexH = height;
            effect.Radius = radius;
            effect.EdgeWidth = EdgeWidth(radius);
        }

        internal static void SetOptics(LiquidGlassEffect effect, AppSettings settings) => SetOptics(effect, CurrentLook(settings));

        internal static void SetOptics(LiquidGlassEffect effect, GlassLook look)
        {
            effect.Refr = look.Refraction;
            effect.Rim = look.Rim;
            effect.Chroma = look.Chroma;
            effect.Sheen = LiquidSheen;
            effect.Lift = look.Lift;
            effect.Tint = new Point4D(look.Tint.R / 255.0, look.Tint.G / 255.0, look.Tint.B / 255.0, look.TintOpacity);
        }

        internal static BitmapSource BlurPreview(BitmapSource source, double radius)
        {
            int width = source.PixelWidth;
            int height = source.PixelHeight;
            var pixels = new byte[width * height * 4];
            source.CopyPixels(pixels, width * 4, 0);
            BoxBlur3(pixels, new byte[pixels.Length], width, height, BoxRadiusFor(radius, 2));
            var blurred = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            blurred.Freeze();
            return blurred;
        }

        /// <summary>把当前位图挂到着色器的显式采样源上(见 LiquidGlassEffect.Input 的注释)。</summary>
        private static void SyncLiquidInput()
        {
            if (_liquid == null || _wb == null) return;
            if (_liquidBrush == null || !ReferenceEquals(_liquidBrush.ImageSource, _wb))
                _liquidBrush = new ImageBrush(_wb) { Stretch = Stretch.Fill };
            if (!ReferenceEquals(_liquid.Input, _liquidBrush)) _liquid.Input = _liquidBrush;
            if (_wbSharp != null && (_sharpBrush == null || !ReferenceEquals(_sharpBrush.ImageSource, _wbSharp)))
                _sharpBrush = new ImageBrush(_wbSharp) { Stretch = Stretch.Fill };
            var sharp = _wbSharp == null ? _liquidBrush : _sharpBrush;
            if (!ReferenceEquals(_liquid.Sharp, sharp)) _liquid.Sharp = sharp;
        }

        /// <summary>主窗/悬停面板注册:玻璃启用期间排除出屏幕捕获;未启用时先登记,启用时统一设置。</summary>
        internal static void RegisterWindow(IntPtr hwnd, bool hiddenPanel = false)
        {
            if (hwnd == IntPtr.Zero || _stopped) return;
            lock (Gate) _excluded.Add(hwnd);
            if (ShouldExcludeWindows && !_shotPause && NativeMethods.ExcludeFromCapture(hwnd))
            {
                long until = Environment.TickCount64 + HoldMs;
                // 隐藏的展开页还没有进入桌面画面，两条采样线程都可以继续取景。
                if (hiddenPanel)
                {
                    lock (Gate) _hiddenPanelExclusions[hwnd] = until;
                    return;
                }
                Volatile.Write(ref _holdUntil, until);
                _wake.Set();
            }
        }

        internal static void PreparePanelVisibility(IntPtr hwnd)
        {
            lock (Gate)
            {
                if (!_hiddenPanelExclusions.Remove(hwnd, out long until) || until <= Environment.TickCount64
                    || !ShouldExcludeWindows || _panelRequest is not { } request || request.Window != hwnd) return;
                _panelExclusion = new PanelExclusion(request.Session, until);
            }
        }

        private static long ScreenHoldUntil(PanelRequest request = null)
        {
            long hold = Volatile.Read(ref _holdUntil);
            var panel = _panelExclusion;
            return panel != null && (request == null || panel.Session == request.Session) ? Math.Max(hold, panel.Until) : hold;
        }

        internal static void SetBarWindow(IntPtr hwnd) => Volatile.Write(ref _barWindow, hwnd);

        /// <summary>窗口销毁前注销(悬停面板 Dispose 路径),防止句柄表膨胀。</summary>
        internal static void ForgetWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            lock (Gate)
            {
                _excluded.Remove(hwnd);
                _hiddenPanelExclusions.Remove(hwnd);
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

        internal static void SetBarRevealed(bool revealed)
        {
            if (_stopped || ((_barBackdropGeneration & 1) != 0) == revealed) return;
            _barBackdropGeneration++;
            _wake.Set();
        }

        internal static void PrepareRevealBackdrop(bool covered)
        {
            _revealNeedsWindow = covered;
            SetBarRevealed(true);
        }

        internal static bool IsRevealFrameReady => !_enabled || _testSrc
            || (_bodyReady && _appliedBackdropGeneration == _barBackdropGeneration);

        internal static void UseTintIfRevealFrameMissing()
        {
            if (IsRevealFrameReady || _image == null) return;
            _image.Visibility = Visibility.Hidden;
            if (_tint != null)
            {
                _tint.Background = Ui.Brush(ThemeService.Background);
                _tint.Opacity = Math.Max(0.60, App.ActiveSettings.ThemeOpacity);
                _tint.Visibility = Visibility.Visible;
            }
            MainWindow.Bar?.ResetGlassInk();
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
            var ui = Application.Current?.Dispatcher;
            if (ui == null || !ui.CheckAccess()) { ui?.BeginInvoke(new Action(() => SetClip(clip, rects, gapFill))); return; }
            _pillRectsDip = rects;
            if (_host == null) return;
            UpdateLiquidConstants();
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
                var s = App.ActiveSettings;
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

        private sealed record PanelOptions(bool Liquid, int Blur, Color Tint, double TintOpacity, bool UseWindowBackdrop, bool WallpaperOnly)
        {
            public bool UsesScreen => !UseWindowBackdrop && !WallpaperOnly;
        }
        private enum PanelRead { Failed, Unchanged, Changed }
        private sealed record PanelRequest(Int32Rect Bounds, double Dpi, int Session, IntPtr Window, PanelOptions Options, bool Freeze);

        private sealed class PanelFrame
        {
            public PanelRequest Request;
            public Int32Rect Region;
            public byte[] Body, Sharp;
            public long Sequence, CaptureTick;
        }

        // 只有捕获线程访问 GDI 句柄、原始像素和差分基线。
        private sealed class PanelCapture : IDisposable
        {
            private IntPtr _screenDc, _memoryDc, _dib, _bits;
            private int _width, _height;
            private byte[] _full, _previousFull;
            private byte[] _raw, _previous, _blurScratch;
            private PanelRequest _previousRequest;
            private Int32Rect _previousRegion;
            private WallpaperMonitor.Snapshot _previousWallpaper;
            private Int32Rect _wallpaperRegion;
            private readonly WindowBackdrop _backdrop = new();
            private byte[] _wallpaperPixels;
            private int _windowBackdropSession;

            public PanelRead Read(PanelRequest request, Int32Rect region)
            {
                if (request.Options.UseWindowBackdrop || request.Options.WallpaperOnly)
                {
                    var wallpaper = ThemeService.CurrentWallpaper;
                    if (wallpaper == null) return PanelRead.Failed;
                    if (wallpaper != _previousWallpaper || _wallpaperPixels == null || region != _wallpaperRegion)
                    {
                        _wallpaperPixels = WallpaperMonitor.ReadWallpaperRegion(
                            wallpaper, region.X, region.Y, region.Width / 2, region.Height / 2);
                        _wallpaperRegion = region;
                        _previousWallpaper = wallpaper;
                    }
                    if (_wallpaperPixels == null) return PanelRead.Failed;
                    var pixels = _wallpaperPixels;
                    if (request.Options.UseWindowBackdrop)
                    {
                        if (_windowBackdropSession != request.Session)
                        {
                            _windowBackdropSession = request.Session;
                            _backdrop.InvalidateLayers();
                        }
                        pixels = _backdrop.Composite(request.Window, region, _wallpaperPixels);
                        if (_backdrop.ShouldWaitForWindowFrame(!ReferenceEquals(pixels, _wallpaperPixels), Environment.TickCount64))
                            return PanelRead.Failed;
                    }
                    else _backdrop.Suspend();
                    if (ReferenceEquals(request, _previousRequest) && region == _previousRegion
                        && pixels.AsSpan().SequenceEqual(_previous)) return PanelRead.Unchanged;
                    if (_dib != IntPtr.Zero) DestroyScreenCapture();
                    _width = region.Width;
                    _height = region.Height;
                    Resize(ref _raw, pixels.Length);
                    pixels.CopyTo(_raw, 0);
                    Resize(ref _blurScratch, _width * _height / 4);
                    return PanelRead.Changed;
                }
                _backdrop.Suspend();
                if (_dib == IntPtr.Zero || _width != region.Width || _height != region.Height)
                {
                    DestroyScreenCapture();
                    _width = region.Width;
                    _height = region.Height;
                    if (!NativeMethods.CreateStripCapture(_width, _height, out _screenDc, out _memoryDc, out _dib, out _bits))
                        return PanelRead.Failed;
                    Resize(ref _full, _width * _height * 4);
                    Resize(ref _previousFull, _full.Length);
                    Resize(ref _raw, _width * _height);
                    Resize(ref _previous, _width * _height);
                    Resize(ref _blurScratch, _width * _height / 4);
                    _previousRequest = null;
                }
                if (!NativeMethods.BlitStrip(_memoryDc, _width, _height, _screenDc, region.X, region.Y))
                    return PanelRead.Failed;

                Marshal.Copy(_bits, _full, 0, _full.Length);
                bool needsFrame = !ReferenceEquals(request, _previousRequest) || region != _previousRegion;
                if (!needsFrame && _full.AsSpan().SequenceEqual(_previousFull)) return PanelRead.Unchanged;

                (_full, _previousFull) = (_previousFull, _full);
                Downsample2x(_previousFull, _width, _height, _raw, _width / 2, _height / 2);
                return needsFrame || !_raw.AsSpan().SequenceEqual(_previous) ? PanelRead.Changed : PanelRead.Unchanged;
            }

            public void Fill(PanelFrame frame, PanelRequest request, Int32Rect region)
            {
                Resize(ref frame.Body, _blurScratch.Length);
                Downsample2x(_raw, _width / 2, _height / 2, frame.Body, _width / 4, _height / 4);
                if (request.Options.Liquid)
                {
                    Resize(ref frame.Sharp, _raw.Length);
                    Array.Copy(_raw, frame.Sharp, _raw.Length);
                }
                else frame.Sharp = null;

                BoxBlur3(frame.Body, _blurScratch, _width / 4, _height / 4, request.Options.Blur);
                if (!request.Options.Liquid)
                {
                    var tint = request.Options.Tint;
                    BakeTint(frame.Body, tint.R, tint.G, tint.B, request.Options.TintOpacity);
                }

                (_raw, _previous) = (_previous, _raw);
                _previousRequest = request;
                _previousRegion = region;
            }

            private static void Resize(ref byte[] buffer, int length)
            {
                if (buffer == null || buffer.Length != length) buffer = new byte[length];
            }

            internal void SuspendWindows() => _backdrop.Suspend();

            public void ResetBaseline() => _previousRequest = null;

            private void DestroyScreenCapture() => NativeMethods.DestroyStripCapture(ref _screenDc, ref _memoryDc, ref _dib);

            public void Dispose()
            {
                _backdrop.Dispose();
                DestroyScreenCapture();
            }
        }

        private static Border _panelTarget;
        private static Rectangle _panelSurface;
        private static LiquidGlassEffect _panelEffect;
        private static WriteableBitmap _panelBodyWb, _panelSharpWb;
        private static ImageBrush _panelBodyBrush, _panelSharpBrush;
        private static readonly AutoResetEvent _panelWake = new AutoResetEvent(false);
        private static Thread _panelThread;
        private static volatile bool _panelThreadStop;
        private static volatile int _panelFps;
        private static volatile int _loopPhase;
        private static volatile PanelRequest _panelRequest;
        private static volatile bool _panelCapturePaused;
        private static PanelRequest _panelDisplayedRequest;
        private static Int32Rect _panelDisplayedRegion;
        private static Rect _panelVisibleRect, _panelInkRect;
        private static long _panelDisplayedAt, _panelSequence, _panelConsumedSequence;
        private static long _panelVerifiedAt, _panelVerifiedSequence;
        private static double _panelScale = 1, _panelCornerRadius;
        private static int _panelSession, _panelPumpQueued;
        private static PanelFrame _panelPending;
        private static Action _panelReady;

        private static void PrewarmPanelEffect()
        {
            if (_panelEffect == null && App.ActiveSettings?.PillMaterial == 4)
                _panelEffect = LiquidGlassEffect.CreatePanel(1, 256, 128);
        }

        private static PanelOptions GetPanelOptions(AppSettings settings, double dpi)
        {
            bool liquid = settings.PillMaterial == 4 && _panelEffect != null;
            var look = CurrentLook(settings);
            return new PanelOptions(liquid, BoxRadiusFor(look.Blur * dpi, 4),
                liquid ? Colors.Transparent : look.Tint, liquid ? 0 : look.TintOpacity,
                settings.ShowInScreenCapture && !settings.WallpaperOnlyCapture, settings.WallpaperOnlyCapture);
        }

        internal static void StartLivePanel(Border panel, Rectangle surface, double dipX, double dipY,
            double dipW, double dipH, Action ready)
        {
            if (_stopped || !IsGlass)
            {
                ready();
                return;
            }
            PrewarmPanelEffect();
            if (_panelSurface != null)
            {
                _panelSurface.Effect = null;
                _panelSurface.Fill = Brushes.Transparent;
            }
            _panelTarget = panel;
            _panelCapturePaused = false;
            _panelSurface = surface;
            _panelReady = ready;
            var dpi = VisualTreeHelper.GetDpi(panel);
            var bounds = DipRectToPixels(dpi, dipX, dipY, dipW, dipH);
            var window = Window.GetWindow(panel);
            var request = new PanelRequest(bounds, dpi.DpiScaleX, ++_panelSession,
                window == null ? IntPtr.Zero : new WindowInteropHelper(window).Handle,
                GetPanelOptions(App.ActiveSettings, dpi.DpiScaleX), App.ActiveSettings.FreezePanelGlass);
            _panelVisibleRect = _panelInkRect = DipRectToVisiblePixels(dpi, new Rect(dipX, dipY, dipW, dipH));
            _panelScale = 1;
            _panelCornerRadius = 16 * Ui.Scale * dpi.DpiScaleX;
            _panelFps = App.ActiveSettings.GlassMaxFps;
            _panelRequest = request;
            if (request.Options.Liquid) SetOptics(_panelEffect, App.ActiveSettings);
            surface.Fill = Brushes.Transparent;

            // A foreground window can change between opens even at the same screen position.
            long verified;
            lock (Gate) verified = _panelVerifiedSequence == _panelConsumedSequence ? _panelVerifiedAt : _panelDisplayedAt;
            if (!request.Options.UseWindowBackdrop && _panelDisplayedRequest is PanelRequest shown
                && shown.Bounds == bounds && shown.Dpi == request.Dpi
                && shown.Options == request.Options && Environment.TickCount64 - verified <= 250)
            {
                _panelDisplayedRequest = request;
                PresentPanel(request);
                Interlocked.Increment(ref StatPanelWarmOpens);
                SignalPanelReady();
            }
            if (_panelThread == null)
            {
                _panelThread = new Thread(PanelLoop) { IsBackground = true, Name = "YukiDesk.PanelGlass" };
                _panelThread.Start();
            }
            _panelWake.Set();
        }

        internal static void UpdateLivePanelRect(Border panel, Rect capture, Rect content, Rect visible, double scale, double radius)
        {
            var request = _panelRequest;
            if (request == null || !ReferenceEquals(_panelTarget, panel)) return;
            var dpi = VisualTreeHelper.GetDpi(panel);
            var bounds = DipRectToPixels(dpi, capture.X, capture.Y, capture.Width, capture.Height);
            _panelVisibleRect = DipRectToVisiblePixels(dpi, visible);
            _panelInkRect = DipRectToVisiblePixels(dpi, content);
            _panelScale = scale;
            _panelCornerRadius = radius * dpi.DpiScaleX;
            if (bounds != request.Bounds || dpi.DpiScaleX != request.Dpi)
            {
                request = request with { Bounds = bounds, Dpi = dpi.DpiScaleX, Options = GetPanelOptions(App.ActiveSettings, dpi.DpiScaleX) };
                _panelRequest = request;
                _panelWake.Set();
            }
            PresentPanel(request);
        }

        private static void RefreshPanelMaterial(AppSettings settings)
        {
            var request = _panelRequest;
            if (request == null) return;
            if (!IsGlass)
            {
                StopLivePanel(null);
                return;
            }
            PrewarmPanelEffect();
            var options = GetPanelOptions(settings, request.Dpi);
            if (options.Liquid) SetOptics(_panelEffect, settings);
            if (!UsesAdaptiveInk(settings)) ModuleBase.SetGlassInk(_panelTarget, null, panel: true);
            if (_panelFps != settings.GlassMaxFps)
            {
                _panelFps = settings.GlassMaxFps;
                _panelWake.Set();
            }
            if (options != request.Options || request.Freeze != settings.FreezePanelGlass)
            {
                request = request with { Options = options, Freeze = settings.FreezePanelGlass };
                _panelRequest = request;
                _panelWake.Set();
            }
            lock (Gate)
            {
                var frame = _panelPending;
                if (frame != null && ReferenceEquals(frame.Request, request) && frame.Sequence == _panelConsumedSequence)
                    RefreshPanelInk(request, frame);
            }
            PresentPanel(request);
        }

        internal static string DiagnosticPanelState()
        {
            var bounds = _panelRequest?.Bounds ?? Int32Rect.Empty;
            return $"取景={(_panelRequest == null ? "关闭" : _panelRequest.Freeze ? "定格" : "实时")} 玻璃层Fill={_panelSurface?.Fill?.GetType().Name}"
                + $" Effect={_panelSurface?.Effect?.GetType().Name} 捕获矩形={bounds.Width}x{bounds.Height}"
                + $" 缩放={_panelScale:F2} 位图={_panelBodyWb?.PixelWidth}x{_panelBodyWb?.PixelHeight}";
        }

        internal static void StopLivePanel(Border panel)
        {
            if (panel != null && !ReferenceEquals(_panelTarget, panel)) return;
            _panelRequest = null;
            _panelCapturePaused = false;
            _panelReady = null;
            if (_panelSurface != null)
            {
                _panelSurface.Effect = null;
                _panelSurface.Fill = Brushes.Transparent;
            }
            _panelTarget = null;
            _panelSurface = null;
            _panelWake.Set();
        }

        internal static void SetPanelCapturePaused(Border panel, bool paused)
        {
            var request = _panelRequest;
            if (request == null || !ReferenceEquals(_panelTarget, panel) || _panelCapturePaused == paused) return;
            // Keep the displayed texture and discard queued frames from the drag image's lifetime.
            if (!paused) _panelRequest = request with { };
            _panelCapturePaused = paused;
            _panelWake.Set();
        }

        private static void SignalPanelReady()
        {
            var ready = _panelReady;
            _panelReady = null;
            ready?.Invoke();
        }

        private static Int32Rect DipRectToPixels(DpiScale dpi, double x, double y, double width, double height)
        {
            int left = (int)Math.Floor(x * dpi.DpiScaleX);
            int top = (int)Math.Floor(y * dpi.DpiScaleY);
            int right = (int)Math.Ceiling((x + width) * dpi.DpiScaleX);
            int bottom = (int)Math.Ceiling((y + height) * dpi.DpiScaleY);
            return new Int32Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        }

        // Capture regions are addressed by whole pixels. The texture viewport follows
        // the compositor's fractional-pixel transform so the backdrop moves smoothly.
        private static Rect DipRectToVisiblePixels(DpiScale dpi, Rect rect)
            => new(rect.X * dpi.DpiScaleX, rect.Y * dpi.DpiScaleY,
                Math.Max(1, rect.Width * dpi.DpiScaleX), Math.Max(1, rect.Height * dpi.DpiScaleY));

        private static Int32Rect PanelCaptureRegion(PanelRequest request)
        {
            var b = request.Bounds;
            // 固定纹理覆盖整个开合过程，并给模糊核和外向折射保留屏幕像素。
            int padding = (int)Math.Ceiling(Math.Max(b.Width, b.Height) * 0.05 + 16 * request.Dpi)
                + request.Options.Blur * 8;
            int x = Math.Max(0, b.X - padding) & ~3;
            int y = Math.Max(0, b.Y - padding) & ~3;
            int r = Math.Min(NativeMethods.ScreenWidth(), b.X + b.Width + padding);
            int bottom = Math.Min(NativeMethods.ScreenHeight(), b.Y + b.Height + padding);
            return new Int32Rect(x, y, Math.Max(0, r - x) & ~3, Math.Max(0, bottom - y) & ~3);
        }

        private static void PanelLoop()
        {
            GlassDiagnostics.TagThread("面板捕获");
            using var capture = new PanelCapture();
            var first = new PanelFrame();
            var second = new PanelFrame();
            long lastPublish = 0;
            PanelRequest regionRequest = null;
            PanelRequest frozenRequest = null;
            Int32Rect region = Int32Rect.Empty;
            while (!_panelThreadStop)
            {
                var request = _panelRequest;
                if (request == null || _shotPause || _panelCapturePaused)
                {
                    if (request == null) capture.SuspendWindows();
                    _panelWake.WaitOne();
                    continue;
                }
                if (ReferenceEquals(frozenRequest, request))
                {
                    capture.SuspendWindows();
                    _panelWake.WaitOne();
                    continue;
                }
                long hold = request.Options.UsesScreen ? ScreenHoldUntil(request) - Environment.TickCount64 : 0;
                if (hold > 0)
                {
                    _panelWake.WaitOne((int)Math.Min(hold, 100));
                    continue;
                }

                long start = Stopwatch.GetTimestamp();
                try
                {
                    if (!ReferenceEquals(regionRequest, request))
                    {
                        regionRequest = request;
                        region = PanelCaptureRegion(request);
                        lastPublish = Environment.TickCount64;
                    }
                    var read = region.Width >= 8 && region.Height >= 8 ? capture.Read(request, region) : PanelRead.Failed;
                    if (read != PanelRead.Failed) Interlocked.Increment(ref StatPanelCaptures);
                    if (read == PanelRead.Changed)
                    {
                        PanelFrame frame;
                        lock (Gate) frame = ReferenceEquals(_panelPending, first) ? second : first;
                        capture.Fill(frame, request, region);
                        Interlocked.Increment(ref StatPanelBlurs);
                        lock (Gate)
                        {
                            if (_shotPause || _panelCapturePaused || !ReferenceEquals(_panelRequest, request)) continue;
                            if (request.Options.UsesScreen && Environment.TickCount64 < ScreenHoldUntil(request))
                            {
                                capture.ResetBaseline();
                                continue;
                            }
                            frame.Request = request;
                            frame.Region = region;
                            frame.CaptureTick = start;
                            frame.Sequence = ++_panelSequence;
                            _panelPending = frame;
                            _panelVerifiedSequence = frame.Sequence;
                            _panelVerifiedAt = Environment.TickCount64;
                        }
                        lastPublish = Environment.TickCount64;
                        Interlocked.Increment(ref StatPanelFrames);
                        if (request.Freeze) frozenRequest = request;
                        SchedulePanelPump();
                    }
                    else if (read == PanelRead.Unchanged)
                    {
                        lock (Gate)
                        {
                            if (ReferenceEquals(_panelPending?.Request, request))
                            {
                                _panelVerifiedSequence = _panelPending.Sequence;
                                _panelVerifiedAt = Environment.TickCount64;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.Log("面板玻璃:采样失败 " + ex.Message);
                    _panelWake.WaitOne(100);
                }

                int fps = EffectiveFps(_panelFps, panel: true);
                int budget = (1000 + fps - 1) / fps;
                if (request.Options.UseWindowBackdrop) budget = Math.Max(50, budget);
                long idle = Environment.TickCount64 - lastPublish;
                if (idle > 350) budget = Math.Max(budget, idle > 1000 ? 100 : 55);
                int spent = (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _panelWake.WaitOne(Math.Max(1, budget - spent));
            }
        }

        private static void SchedulePanelPump()
        {
            if (_stopped || Interlocked.Exchange(ref _panelPumpQueued, 1) != 0) return;
            var ui = Application.Current?.Dispatcher;
            if (ui == null || ui.HasShutdownStarted)
            {
                Interlocked.Exchange(ref _panelPumpQueued, 0);
                return;
            }
            ui.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                Interlocked.Exchange(ref _panelPumpQueued, 0);
                PanelPumpTick();
            }));
        }

        private static void PanelPumpTick()
        {
            var request = _panelRequest;
            if (_stopped || _shotPause || request == null || _panelSurface == null || _panelCapturePaused) return;
            lock (Gate)
            {
                var frame = _panelPending;
                if (frame == null || frame.Sequence == _panelConsumedSequence || !ReferenceEquals(frame.Request, request))
                    return;

                int width = frame.Region.Width / 4;
                int height = frame.Region.Height / 4;
                var bounds = new Int32Rect(0, 0, width, height);
                if (_panelBodyWb == null || _panelBodyWb.PixelWidth != width || _panelBodyWb.PixelHeight != height)
                {
                    _panelBodyWb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                    _panelBodyBrush = new ImageBrush(_panelBodyWb) { Stretch = Stretch.Fill };
                }
                _panelBodyWb.WritePixels(bounds, frame.Body, width * 4, 0);
                Interlocked.Increment(ref StatPanelWrites);
                if (frame.Sharp != null)
                {
                    int sharpWidth = width * 2;
                    int sharpHeight = height * 2;
                    if (_panelSharpWb == null || _panelSharpWb.PixelWidth != sharpWidth || _panelSharpWb.PixelHeight != sharpHeight)
                    {
                        _panelSharpWb = new WriteableBitmap(sharpWidth, sharpHeight, 96, 96, PixelFormats.Bgra32, null);
                        _panelSharpBrush = new ImageBrush(_panelSharpWb) { Stretch = Stretch.Fill };
                    }
                    _panelSharpWb.WritePixels(new Int32Rect(0, 0, sharpWidth, sharpHeight), frame.Sharp, sharpWidth * 4, 0);
                    Interlocked.Increment(ref StatPanelSharpWrites);
                }
                Interlocked.Exchange(ref _panelWrittenCaptureTick, frame.CaptureTick);
                _panelConsumedSequence = frame.Sequence;
                _panelDisplayedRequest = request;
                _panelDisplayedRegion = frame.Region;
                _panelDisplayedAt = Environment.TickCount64;
                RefreshPanelInk(request, frame);
            }
            PresentPanel(request);
            SignalPanelReady();
        }

        private static void RefreshPanelInk(PanelRequest request, PanelFrame frame)
        {
            var settings = App.ActiveSettings;
            if (!UsesAdaptiveInk(settings))
            {
                ModuleBase.SetGlassInk(_panelTarget, null, panel: true);
                return;
            }

            var bounds = _panelInkRect;
            var region = frame.Region;
            var area = new Rect((bounds.X - region.X) / (double)region.Width, (bounds.Y - region.Y) / (double)region.Height,
                bounds.Width / (double)region.Width, bounds.Height / (double)region.Height);
            // 普通玻璃在后台已经把染色烘焙进像素，只有液态玻璃需要模拟着色器。
            var look = request.Options.Liquid ? CurrentLook(settings) : default;
            var lightness = SurfaceLightness(frame.Body, region.Width / 4, region.Height / 4, area, look, settings.GlassOpacity);
            ModuleBase.SetGlassInk(_panelTarget, lightness, panel: true);
        }

        private static void PresentPanel(PanelRequest request)
        {
            var surface = _panelSurface;
            var shown = _panelDisplayedRequest;
            if (surface == null || shown == null || shown.Session != request.Session) return;
            var region = _panelDisplayedRegion;
            var visible = _panelVisibleRect;
            double width = Math.Min(visible.Width, region.Width);
            double height = Math.Min(visible.Height, region.Height);
            double x = Math.Clamp(visible.X - region.X, 0, region.Width - width);
            double y = Math.Clamp(visible.Y - region.Y, 0, region.Height - height);
            var viewbox = new Rect(x / region.Width, y / region.Height, width / region.Width, height / region.Height);
            if (_panelBodyBrush.Viewbox != viewbox) _panelBodyBrush.Viewbox = viewbox;
            if (surface.Fill != _panelBodyBrush) surface.Fill = _panelBodyBrush;
            var settings = App.ActiveSettings;
            surface.Opacity = settings.GlassOpacity;
            var effect = request.Options.Liquid && shown.Options.Liquid ? _panelEffect : null;
            if (effect != null)
            {
                // 采样可能被屏幕边界截短，折射轮廓始终按展开页实际尺寸计算。
                SetGeometry(effect, _panelCornerRadius * 0.5, visible.Width * 0.5, visible.Height * 0.5);
                effect.SharpUv = new Point4D(viewbox.Width, viewbox.Height, viewbox.X, viewbox.Y);
                if (effect.Input != _panelBodyBrush) effect.Input = _panelBodyBrush;
                if (effect.Sharp != _panelSharpBrush) effect.Sharp = _panelSharpBrush;
            }
            if (surface.Effect != effect) surface.Effect = effect;
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
            var s = App.ActiveSettings;
            double dsy = NativeMethods.SystemDpiScale();
            int physW = NativeMethods.ScreenWidth();
            double barPhysH = 46.0 * Ui.Scale * dsy;
            // 条带从屏幕 y=0 抓到 barPhysH+下出血:真实屏幕没有负 y,上缘靠屏幕边界自然截断。
            double blurPhys = Math.Max(1, (s == null ? 24 : CurrentLook(s).Blur) * dsy);
            int bleed = (int)Math.Ceiling(blurPhys) + 8;
            int h = (int)Math.Ceiling(barPhysH) + bleed;
            int fps = EffectiveFps(s?.GlassMaxFps ?? 0, panel: false);
            var spec = new GlassSpec
            {
                Width = Math.Max(4, physW & ~3),
                Height = Math.Max(4, Math.Min(NativeMethods.ScreenHeight(), h + 3) & ~3),
                BlurQuarter = BoxRadiusFor(blurPhys, 4),
                FpsMs = (1000 + fps - 1) / fps,
                WallpaperFadeMs = SystemParameters.ClientAreaAnimation ? s?.WallpaperFadeDurationMs ?? 360 : 0,
                MidY = Math.Max(4, (int)Math.Ceiling(barPhysH / 2)),
                Liquid = s?.PillMaterial == 4,
                UseWindowBackdrop = s is { ShowInScreenCapture: true, WallpaperOnlyCapture: false },
                WallpaperOnly = s?.WallpaperOnlyCapture == true,
                DisplayGeneration = _displayGeneration,
            };
            spec.HalfWidth = spec.Width / 2;
            spec.HalfHeight = spec.Height / 2;
            spec.ImageHeightDip = spec.Height / dsy;
            _spec = spec;
            if (_image != null)
            {
                if (_wb == null || _wb.PixelWidth != spec.BodyWidth || _wb.PixelHeight != spec.BodyHeight)
                {
                    _wb = new WriteableBitmap(spec.BodyWidth, spec.BodyHeight, 96, 96, PixelFormats.Bgra32, null);
                    _bodyReady = false;
                    _wbSharp = null;
                    _image.Source = _wb;
                    _consumedSequence = 0;
                    SyncLiquidInput();
                }
                _image.Height = spec.ImageHeightDip;
            }
            if (_tint != null) _tint.Height = spec.ImageHeightDip;
            if (_testSrc && _wb != null)
            {
                FillTestPattern(_wb.PixelWidth, _wb.PixelHeight);
                _bodyReady = true;
                // 合成图案模式也喂未模糊帧,否则轮廓折射采到空白(诊断时容易误判为"折射没生效")
                if (_liquid != null)
                {
                    if (_wbSharp == null || _wbSharp.PixelWidth != spec.HalfWidth || _wbSharp.PixelHeight != spec.HalfHeight)
                        _wbSharp = new WriteableBitmap(spec.HalfWidth, spec.HalfHeight, 96, 96, PixelFormats.Bgra32, null);
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
                if (!_shotPause) ApplyExclusionAll();
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
                StopLivePanel(null);
                RestoreExclusionAll();
                _wake.Set();
            }
        }

        private static void ApplyExclusionAll()
        {
            if (!ShouldExcludeWindows) return;
            Volatile.Write(ref _holdUntil, Environment.TickCount64 + HoldMs);
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
            if (!_enabled || _stopped || _shotPause) return;
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
                long captureTick;
                // 界面拷贝期间，捕获线程不会复用当前发布的槽位。
                lock (Gate)
                {
                    var f = _latest;
                    if (f == null || _latestSequence == _consumedSequence || _image == null) return;
                    var spec = _spec;
                    if (_latestBackdropGeneration != _barBackdropGeneration || !SameCaptureSpec(spec, _latestSpec)) return;
                    if (_wb == null || _wb.PixelWidth != _latestW || _wb.PixelHeight != _latestH)
                    {
                        _wb = new WriteableBitmap(_latestW, _latestH, 96, 96, PixelFormats.Bgra32, null);
                        _wbSharp = null;
                        _image.Source = _wb;
                        _image.Height = spec.ImageHeightDip;
                        _consumedSequence = 0;
                        SyncLiquidInput();
                    }
                    _wb.WritePixels(new Int32Rect(0, 0, _latestW, _latestH), f, _latestW * 4, 0);
                    _bodyReady = true;
                    _appliedBackdropGeneration = _latestBackdropGeneration;
                    if (_image.Visibility != Visibility.Visible)
                    {
                        SetFallbackTint(App.ActiveSettings);
                        _image.Visibility = Visibility.Visible;
                    }
                    ShowGlassEffect(App.ActiveSettings?.PillMaterial == 4 ? _liquid : null);
                    Interlocked.Increment(ref StatWrites);
                    var sf = _latestSharp;
                    if (sf != null && _liquid != null && f == _latest)
                    {
                        if (_wbSharp == null || _wbSharp.PixelWidth != spec.HalfWidth || _wbSharp.PixelHeight != spec.HalfHeight)
                        {
                            _wbSharp = new WriteableBitmap(spec.HalfWidth, spec.HalfHeight, 96, 96, PixelFormats.Bgra32, null);
                            SyncLiquidInput();
                        }
                        _wbSharp.WritePixels(new Int32Rect(0, 0, spec.HalfWidth, spec.HalfHeight), sf, spec.HalfWidth * 4, 0);
                        Interlocked.Increment(ref StatSharpWrites);
                    }
                    if (!_latestSlideBlending) RefreshPillInk(f, _latestW, _latestH, spec);
                    _consumedSequence = _latestSequence;
                    captureTick = _latestCaptureTick;
                    wroteFrame = true;
                }
                // 延迟样本只在"真的写了新帧"之后结算,并且丢弃 >500ms 的陈旧样本
                // (静止很久后的第一帧会把整段静止时间也计入,那不是延迟)。
                long ct = wroteFrame ? captureTick : 0;
                Interlocked.Exchange(ref _pubCaptureTick, ct);
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
                    _displayGeneration++;
                    RefreshSpec();
                    if (_panelRequest is PanelRequest request)
                    {
                        _panelDisplayedRequest = null;
                        _panelRequest = request with { };
                        _panelWake.Set();
                    }
                    _holdUntil = Environment.TickCount64 + HoldMs;
                    App.Log("玻璃:显示设置变化,捕获条带已按新分辨率重建");
                }
                catch { }
            }));
        }

        // ---------- 内部:捕获线程 ----------

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

        // 只由顶栏捕获线程替换或释放会话。
        private static void ResetDxgi(bool allowRetry)
        {
            var previous = _dxgi;
            _dxgi = null;
            _dxgiTried = !allowRetry;
            previous?.Dispose();
        }

        private static bool SameCaptureSpec(GlassSpec a, GlassSpec b)
        {
            return a != null && b != null && a.Width == b.Width && a.Height == b.Height
                && a.BlurQuarter == b.BlurQuarter && a.Liquid == b.Liquid && a.DisplayGeneration == b.DisplayGeneration
                && a.UseWindowBackdrop == b.UseWindowBackdrop && a.WallpaperOnly == b.WallpaperOnly;
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
            using var windowBackdrop = new WindowBackdrop();
            IntPtr screenDc = IntPtr.Zero, memoryDc = IntPtr.Zero, dib = IntPtr.Zero, bits = IntPtr.Zero;
            int capW = 0, capH = 0, hw = 0, hh = 0;
            byte[] full = null, halfRaw = null, halfBase = null, workB = null;
            bool hasBaseline = false, hasDxgiPixels = false, deferredFrame = false, loggedSelf = false, loggedCapFail = false;
            GlassSpec previousSpec = null;
            int wallpaperGeneration = Volatile.Read(ref _wallpaperGeneration);
            WallpaperMonitor.Snapshot renderedSlide = null;
            WallpaperMonitor.Snapshot unreadableSlide = null;
            byte[] fadeFrom = null, fadeTo = null;
            long fadeStartedAt = 0;
            int fadeDurationMs = 0;
            long retrySlideAt = 0;
            bool covered = false;
            int previousBackdropGeneration = _barBackdropGeneration;
            long nextCoverageCheck = 0;
            try
            {
                while (!_stopThread)
                {
                    var spec = _spec;
                    if (!_enabled || !_active || spec == null)
                    {
                        windowBackdrop.Suspend();
                        _loopPhase = 1;
                        hasBaseline = false;
                        deferredFrame = false;
                        _wake.WaitOne(400);
                        continue;
                    }
                    bool samplesScreen = !spec.UseWindowBackdrop && !spec.WallpaperOnly;
                    if (samplesScreen && Environment.TickCount64 < ScreenHoldUntil())
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
                    int wallpaper = Volatile.Read(ref _wallpaperGeneration);
                    int backdropGeneration = _barBackdropGeneration;
                    bool revealed = (backdropGeneration & 1) != 0;
                    if (wallpaperGeneration != wallpaper || !SameCaptureSpec(spec, previousSpec)
                        || backdropGeneration != previousBackdropGeneration)
                    {
                        if (backdropGeneration != previousBackdropGeneration)
                        {
                            if (spec.UseWindowBackdrop) windowBackdrop.InvalidateLayers();
                            else
                            {
                                hasDxgiPixels = false;
                                renderedSlide = null;
                                fadeFrom = fadeTo = null;
                            }
                        }
                        previousBackdropGeneration = backdropGeneration;
                        wallpaperGeneration = wallpaper;
                        hasBaseline = false;
                        deferredFrame = false;
                        nextCoverageCheck = 0;
                    }
                    if (previousSpec != null && (spec.DisplayGeneration != previousSpec.DisplayGeneration
                        || spec.UseWindowBackdrop != previousSpec.UseWindowBackdrop || spec.WallpaperOnly != previousSpec.WallpaperOnly))
                    {
                        windowBackdrop.Suspend();
                        ResetDxgi(true);
                        hasDxgiPixels = false;
                        renderedSlide = null;
                        fadeFrom = fadeTo = null;
                        NativeMethods.DestroyStripCapture(ref screenDc, ref memoryDc, ref dib);
                    }
                    previousSpec = spec;

                    if (capW != spec.Width || capH != spec.Height)
                    {
                        NativeMethods.DestroyStripCapture(ref screenDc, ref memoryDc, ref dib);
                        capW = spec.Width;
                        capH = spec.Height;
                        hasDxgiPixels = false;
                        renderedSlide = null;
                        fadeFrom = fadeTo = null;
                        hw = spec.HalfWidth;
                        hh = spec.HalfHeight;
                        full = new byte[capW * capH * 4];
                        halfRaw = new byte[hw * hh * 4];
                        halfBase = new byte[hw * hh * 4];
                        workB = new byte[spec.BodyWidth * spec.BodyHeight * 4];
                        lock (Gate)
                        {
                            for (int i = 0; i < _rotate.Length; i++) _rotate[i] = new byte[workB.Length];
                            Array.Clear(_rotateSharp);
                            _rotateIndex = 0;
                            _latest = null;
                            _latestSharp = null;
                        }
                    }

                    var slide = ThemeService.CurrentWallpaper;
                    bool fixedWallpaper = spec.UseWindowBackdrop || spec.WallpaperOnly;
                    if (fixedWallpaper && slide == null)
                    {
                        _wake.WaitOne(300);
                        continue;
                    }
                    // 截图兼容模式在壁纸上合成后方窗口。旧整屏模式的幻灯片路径仍做平滑切换。
                    bool useWallpaper = fixedWallpaper || (!revealed && slide?.IsSlideshow == true);
                    bool slideCompleted = false;
                    if (useWallpaper)
                    {
                        if (slide != renderedSlide)
                        {
                            long now = Environment.TickCount64;
                            if (slide != unreadableSlide || now >= retrySlideAt)
                            {
                                var next = fixedWallpaper
                                    ? WallpaperMonitor.ReadWallpaperRegion(slide, slide.Monitor.Left, slide.Monitor.Top, hw, hh)
                                    : WallpaperMonitor.ReadSlideshowStrip(slide, hw, hh);
                                if (next != null)
                                {
                                    if (renderedSlide != null && spec.WallpaperFadeMs > 0)
                                    {
                                        // 快速连续换图时从正在显示的混合画面继续，避免退回上一张。
                                        fadeFrom = (byte[])halfRaw.Clone();
                                        fadeTo = next;
                                        fadeStartedAt = Environment.TickCount64;
                                        fadeDurationMs = spec.WallpaperFadeMs;
                                    }
                                    else
                                    {
                                        halfRaw = next;
                                        fadeFrom = fadeTo = null;
                                        slideCompleted = true;
                                    }
                                    renderedSlide = slide;
                                    unreadableSlide = null;
                                    hasBaseline = false;
                                    hasDxgiPixels = false;
                                }
                                else
                                {
                                    unreadableSlide = slide;
                                    retrySlideAt = now + 750;
                                }
                            }
                            if (slide != renderedSlide && fadeTo == null)
                            {
                                if (fixedWallpaper || renderedSlide != null)
                                {
                                    _wake.WaitOne(300);
                                    continue;
                                }
                                useWallpaper = false;
                            }
                        }
                        if (fadeTo != null)
                        {
                            double progress = spec.WallpaperFadeMs > 0
                                ? (Environment.TickCount64 - fadeStartedAt) / (double)fadeDurationMs
                                : 1;
                            if (progress >= 1)
                            {
                                halfRaw = fadeTo;
                                fadeFrom = fadeTo = null;
                                slideCompleted = true;
                            }
                            else BlendWallpaperStrip(fadeFrom, fadeTo, halfRaw, progress);
                        }
                        if (!spec.UseWindowBackdrop && useWallpaper && hasBaseline && fadeTo == null && !slideCompleted)
                        {
                            _wake.WaitOne(300);
                            continue;
                        }
                    }
                    else if (renderedSlide != null)
                    {
                        renderedSlide = null;
                        fadeFrom = fadeTo = null;
                        hasBaseline = false;
                        hasDxgiPixels = false;
                    }

                    long checkAt = Environment.TickCount64;
                    if (!useWallpaper && !revealed && checkAt >= nextCoverageCheck)
                    {
                        covered = IsCovered(spec);
                        nextCoverageCheck = checkAt + 150;
                    }
                    if (!useWallpaper && !revealed && covered)
                    {
                        hasBaseline = false;
                        deferredFrame = false;
                        _loopPhase = 5;
                        _wake.WaitOne(160);
                        continue;
                    }

                    if (!useWallpaper) EnsureDxgi();
                    var dxgi = useWallpaper ? null : _dxgi;
                    long iterationStart = Stopwatch.GetTimestamp();
                    long busyStart = iterationStart;
                    bool copied = false;
                    bool reusePixels = useWallpaper;
                    if (dxgi != null)
                    {
                        int waitMs = !hasBaseline ? 16 : spec.FpsMs == 0 ? 100 : Math.Min(100, spec.FpsMs);
                        int acquired = dxgi.Acquire(waitMs);
                        if (acquired == 1)
                        {
                            Interlocked.Increment(ref StatDxgiIdle);
                            if (!hasDxgiPixels)
                            {
                                // 静止的最大化窗口可能没有新的 DXGI 帧。唤出首帧先取当前屏幕。
                                if (!revealed || hasBaseline) continue;
                            }
                            else
                            {
                                if (hasBaseline && !deferredFrame) continue;
                                reusePixels = true;
                            }
                        }
                        else if (acquired != 0)
                        {
                            ResetDxgi(true);
                            hasDxgiPixels = false;
                            hasBaseline = false;
                            _loopPhase = 8;
                            Thread.Sleep(100);
                            continue;
                        }
                        else
                        {
                            bool skipped;
                            try
                            {
                                skipped = hasDxgiPixels && !dxgi.BandDirty(0, 0, capW, capH);
                                busyStart = Stopwatch.GetTimestamp();
                                if (!skipped) copied = dxgi.CopyBand(0, 0, capW, capH, full);
                            }
                            finally { dxgi.ReleaseFrame(); }
                            if (skipped && hasBaseline && !deferredFrame)
                            {
                                Interlocked.Increment(ref StatDxgiSkip);
                                long idleSkip = Environment.TickCount64 - _lastPubTick;
                                _wake.WaitOne(idleSkip > 1000 ? 70 : Math.Max(5, spec.FpsMs / 2));
                                continue;
                            }
                            if (skipped) reusePixels = true;
                            else if (!copied)
                            {
                                App.Log("玻璃:DXGI 条带读回失败，回退 GDI BitBlt");
                                ResetDxgi(false);
                                hasBaseline = false;
                                hasDxgiPixels = false;
                            }
                            else if (revealed && !hasBaseline && IsBlackCaptureBand(full))
                            {
                                // DXGI 刚开始采集时可能先交付空纹理。用 GDI 确认当前画面，再决定首帧。
                                copied = false;
                                hasDxgiPixels = false;
                            }
                            else hasDxgiPixels = true;
                        }
                    }

                    if (!copied && !reusePixels)
                    {
                        busyStart = Stopwatch.GetTimestamp();
                        if (dib == IntPtr.Zero && !NativeMethods.CreateStripCapture(capW, capH,
                            out screenDc, out memoryDc, out dib, out bits))
                        {
                            if (!loggedCapFail)
                            {
                                loggedCapFail = true;
                                App.Log("玻璃:创建捕获条带失败 err=" + Marshal.GetLastWin32Error());
                            }
                            _loopPhase = 4;
                            Thread.Sleep(250);
                            continue;
                        }
                        if (!NativeMethods.BlitStrip(memoryDc, capW, capH, screenDc, 0, 0))
                        {
                            _loopPhase = 6;
                            Thread.Sleep(250);
                            continue;
                        }
                        Marshal.Copy(bits, full, 0, full.Length);
                    }
                    if (!reusePixels)
                    {
                        Interlocked.Increment(ref StatFrames);
                        Interlocked.Increment(ref StatBlits);
                        Interlocked.Add(ref StatBlitTicks, Stopwatch.GetTimestamp() - busyStart);
                        long tDown = Stopwatch.GetTimestamp();
                        Downsample2x(full, capW, capH, halfRaw, hw, hh);
                        Interlocked.Add(ref StatDownTicks, Stopwatch.GetTimestamp() - tDown);
                    }

                    var backdrop = spec.UseWindowBackdrop
                        ? windowBackdrop.Composite(Volatile.Read(ref _barWindow), new Int32Rect(0, 0, capW, capH), halfRaw,
                            wallpaperChanging: fadeTo != null)
                        : halfRaw;
                    if (revealed && !hasBaseline && spec.UseWindowBackdrop
                        && windowBackdrop.ShouldWaitForWindowFrame(!ReferenceEquals(backdrop, halfRaw), Environment.TickCount64,
                            expectWindow: _revealNeedsWindow))
                    {
                        _wake.WaitOne(50);
                        continue;
                    }
                    long tDiff = System.Diagnostics.Stopwatch.GetTimestamp();
                    double diff = hasBaseline ? MeanAbsDiff(halfBase, backdrop, hw, hh, 16) : 1e9;
                    Interlocked.Add(ref StatDiffTicks, System.Diagnostics.Stopwatch.GetTimestamp() - tDiff);
                    long nowTick = Environment.TickCount64;
                    bool windowRegionChanged = spec.UseWindowBackdrop && !backdrop.AsSpan().SequenceEqual(halfBase);
                    bool publish = slideCompleted || _alwaysPub || diff >= DiffThreshold || windowRegionChanged;
                    // 小幅漂移(例如慢速滚动、小面积动画)不必按满帧率发布:每次发布都要整条窗口重呈。
                    // 但 100ms 的闸门(10fps)在液态玻璃下能看出卡顿,收到 50ms(20fps)——
                    // 折中:CPU 只多一半,肉眼基本连续。
                    // 闸门按帧率缩放(≈半速):30fps 时约 66ms,60fps 时 33ms,300fps 时 6ms。
                    int pubGateMs = Math.Max(2, spec.FpsMs * 2);
                    // 窗口或壁纸动画停在限流区间时，下一圈补取最后一帧，即使 DXGI 不再发脏区。
                    deferredFrame = !useWallpaper && publish && !_alwaysPub
                        && diff < SmallDiffThreshold && nowTick - _lastPubTick < pubGateMs;
                    if (publish && !deferredFrame)
                    {
                        byte[] workA, sharpA;
                        lock (Gate)
                        {
                            int slot = _rotateIndex;
                            if (ReferenceEquals(_rotate[slot], _latest)) slot = (slot + 1) % _rotate.Length;
                            _rotateIndex = (slot + 1) % _rotate.Length;
                            workA = _rotate[slot];
                            sharpA = spec.Liquid ? _rotateSharp[slot] ??= new byte[halfRaw.Length] : null;
                        }
                        Downsample2x(backdrop, hw, hh, workA, spec.BodyWidth, spec.BodyHeight);
                        // 未模糊帧只做降采样拷贝(无模糊),供轮廓折射采样;与模糊帧同槽位轮换,杜绝撕裂。
                        if (sharpA != null) Array.Copy(backdrop, sharpA, sharpA.Length);
                        long tBlur = System.Diagnostics.Stopwatch.GetTimestamp();
                        BoxBlur3(workA, workB, spec.BodyWidth, spec.BodyHeight, spec.BlurQuarter);
                        Interlocked.Add(ref StatBlurTicks, System.Diagnostics.Stopwatch.GetTimestamp() - tBlur);
                        lock (Gate)
                        {
                            if (_stopThread || !_enabled || _shotPause || backdropGeneration != _barBackdropGeneration
                                || !SameCaptureSpec(_spec, spec)) continue;
                            if (samplesScreen && Environment.TickCount64 < ScreenHoldUntil())
                            {
                                hasBaseline = false;
                                hasDxgiPixels = false;
                                continue;
                            }
                            _latestSpec = spec;
                            _latestBackdropGeneration = backdropGeneration;
                            _latestSequence++;
                            _latestCaptureTick = reusePixels ? 0 : busyStart;
                            _latestSlideBlending = fadeTo != null;
                            _latest = workA;
                            _latestSharp = sharpA;
                            _latestW = spec.BodyWidth;
                            _latestH = spec.BodyHeight;
                        }
                        _lastPubTick = nowTick;
                        Array.Copy(backdrop, halfBase, halfBase.Length);
                        hasBaseline = true;
                        Interlocked.Increment(ref StatPublishes);
                        SchedulePump();   // 有新帧才唤醒 UI 线程(静止时 0 唤醒)
                        if (slideCompleted) App.Log("玻璃:已更新幻灯片底图");
                        if (!loggedSelf)
                        {
                            loggedSelf = true;
                            SelfCheck(workA, spec.BodyWidth, spec.BodyHeight, out double m, out double sd, out long n);
                            App.Log(string.Format("玻璃:首帧主体 {0}x{1} 红通道均值={2:F1} 标准差={3:F1} 采样={4}",
                                spec.BodyWidth, spec.BodyHeight, m, sd, n));
                        }
                    }
                    Interlocked.Add(ref StatBusyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - busyStart);

                    _loopPhase = 7;
                    long idle = Environment.TickCount64 - _lastPubTick;
                    int delay = fadeTo != null ? Math.Max(spec.FpsMs, 16)
                        : Math.Max(spec.FpsMs, idle > 1000 ? 70 : idle > 350 ? 40 : 0);
                    if (spec.UseWindowBackdrop)
                        delay = Math.Max(delay, windowBackdrop.HasSources || fadeTo != null ? 50 : 140);
                    // DXGI 的本轮等待也算在帧间隔内。
                    int spent = (int)Stopwatch.GetElapsedTime(iterationStart).TotalMilliseconds;
                    if (delay > spent) _wake.WaitOne(delay - spent);
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

        private static bool IsBlackCaptureBand(byte[] pixels)
        {
            foreach (uint pixel in MemoryMarshal.Cast<byte, uint>(pixels.AsSpan()))
                if ((pixel & 0x00FFFFFF) != 0) return false;
            return true;
        }

        private static void BlendWallpaperStrip(byte[] from, byte[] to, byte[] pixels, double progress)
        {
            // 两端减速，只有中段产生明显变化。玻璃主体和清晰折射纹理使用同一权重。
            int weight = (int)Math.Round(progress * progress * (3 - 2 * progress) * 256);
            int keep = 256 - weight;
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)((from[i] * keep + to[i] * weight + 128) >> 8);
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

        /// <summary>将物理模糊半径换算到处理位图的采样尺度，三趟盒式近似。</summary>
        private static int BoxRadiusFor(double radius, int reduction)
        {
            return Math.Clamp((int)Math.Round(radius * 0.9 / reduction), 1, 96);
        }

        /// <summary>2×2 平均降采样(只动 BGR,alpha 补 255)。半分辨率域做模糊,大半径下视觉无差。</summary>
        private static void Downsample2x(byte[] src, int sw, int sh, byte[] dst, int w, int h)
        {
            ReadOnlySpan<uint> source = MemoryMarshal.Cast<byte, uint>(src.AsSpan());
            Span<uint> output = MemoryMarshal.Cast<byte, uint>(dst.AsSpan());
            const uint redBlue = 0x00FF00FF, green = 0x0000FF00;
            for (int y = 0; y < h; y++)
            {
                int row = y * 2;
                int top = row * sw, bottom = Math.Min(row + 1, sh - 1) * sw;
                int target = y * w;
                for (int x = 0; x < w; x++)
                {
                    int left = x * 2, right = Math.Min(left + 1, sw - 1);
                    uint a = source[top + left], b = source[top + right];
                    uint c = source[bottom + left], d = source[bottom + right];
                    // The two lanes in redBlue are wide enough to sum four byte values independently.
                    uint rb = (a & redBlue) + (b & redBlue) + (c & redBlue) + (d & redBlue);
                    uint g = (a & green) + (b & green) + (c & green) + (d & green);
                    output[target + x] = ((rb >> 2) & redBlue) | ((g >> 2) & green) | 0xFF000000;
                }
            }
        }

        private const int BlurShift = 24;

        private static byte BlurAverage(int sum, uint reciprocal) => (byte)(((uint)sum * reciprocal) >> BlurShift);

        /// <summary>滑动和盒式模糊(水平+垂直一趟,只动 BGR)。O(n) 与半径无关,边 replicate。</summary>
        private static void BlurPassH(byte[] src, byte[] dst, int w, int h, int r, uint reciprocal)
        {
            int stride = w * 4;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int blue = src[row] * (r + 1), green = src[row + 1] * (r + 1), red = src[row + 2] * (r + 1);
                for (int k = 1; k <= r; k++)
                {
                    int source = row + Math.Min(k, w - 1) * 4;
                    blue += src[source]; green += src[source + 1]; red += src[source + 2];
                }
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;
                    dst[o] = BlurAverage(blue, reciprocal);
                    dst[o + 1] = BlurAverage(green, reciprocal);
                    dst[o + 2] = BlurAverage(red, reciprocal);
                    int ai = row + Math.Min(x + r + 1, w - 1) * 4;
                    int ri = row + Math.Max(x - r, 0) * 4;
                    blue += src[ai] - src[ri];
                    green += src[ai + 1] - src[ri + 1];
                    red += src[ai + 2] - src[ri + 2];
                }
            }
        }

        private static void BlurPassV(byte[] src, byte[] dst, int w, int h, int r, uint reciprocal, Span<int> sums)
        {
            int stride = w * 4;
            for (int col = 0, s = 0; col < stride; col += 4, s += 3)
            {
                sums[s] = src[col] * (r + 1);
                sums[s + 1] = src[col + 1] * (r + 1);
                sums[s + 2] = src[col + 2] * (r + 1);
            }
            for (int k = 1; k <= r; k++)
            {
                int row = Math.Min(k, h - 1) * stride;
                for (int col = 0, s = 0; col < stride; col += 4, s += 3)
                {
                    sums[s] += src[row + col];
                    sums[s + 1] += src[row + col + 1];
                    sums[s + 2] += src[row + col + 2];
                }
            }
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int add = Math.Min(y + r + 1, h - 1) * stride;
                int remove = Math.Max(y - r, 0) * stride;
                for (int col = 0, s = 0; col < stride; col += 4, s += 3)
                {
                    int o = row + col, ai = add + col, ri = remove + col;
                    dst[o] = BlurAverage(sums[s], reciprocal);
                    dst[o + 1] = BlurAverage(sums[s + 1], reciprocal);
                    dst[o + 2] = BlurAverage(sums[s + 2], reciprocal);
                    sums[s] += src[ai] - src[ri];
                    sums[s + 1] += src[ai + 1] - src[ri + 1];
                    sums[s + 2] += src[ai + 2] - src[ri + 2];
                }
            }
        }

        /// <summary>三趟水平+垂直盒式 ≈ 高斯(经典等价)。结果写回 a,b 为中间缓冲。</summary>
        private static void BoxBlur3(byte[] a, byte[] b, int w, int h, int r)
        {
            if (r < 1) return;
            uint window = (uint)(2 * r + 1);
            // With r <= 96, rounding the reciprocal upward preserves the exact integer quotient
            // for every channel sum in [0, 255 * window].
            uint reciprocal = ((1u << BlurShift) + window - 1) / window;
            int[] sums = ArrayPool<int>.Shared.Rent(w * 3);
            try
            {
                for (int it = 0; it < 3; it++)
                {
                    BlurPassH(a, b, w, h, r, reciprocal);
                    BlurPassV(b, a, w, h, r, reciprocal, sums.AsSpan(0, w * 3));
                }
            }
            finally { ArrayPool<int>.Shared.Return(sums); }
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
            for (int band = 1; band < 8; band += 2)
            {
                int rowStart = height * band / 8 * width * 4;
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
