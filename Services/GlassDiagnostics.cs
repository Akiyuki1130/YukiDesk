using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopBar.Interop;
using DesktopBar.UI;

namespace DesktopBar.Services
{
    /// <summary>
    /// 玻璃/液态玻璃的进程内可视化取证(--glassshot &lt;目录&gt;)。
    ///
    /// 为什么必须进程内自证:顶栏与悬停面板都设了 WDA_EXCLUDEFROMCAPTURE(否则玻璃会把自己拍进去),
    /// 这导致任何外部截图/录屏/远程桌面都看不到顶栏——"人眼看到的效果"和"能截到的图"不是一回事。
    /// 取证手法:暂停捕获线程 → 临时恢复 WDA(让顶栏可被捕获)→ 等 DWM 合成 → BitBlt 全屏 →
    /// 重新排除并恢复采样。抓图期间捕获线程是停的,玻璃层冻结在最后一帧,不会出现自己拍自己的正反馈,
    /// 于是这张全屏图就是"用户此刻真正看到的合成结果"。
    ///
    /// 同时为了能看出"模糊",取证会临时把桌面壁纸换成高频细节测试图(棋盘+文字),结束后还原。
    /// </summary>
    internal static class GlassDiagnostics
    {
        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, string value, uint winIni);

        private static string _dir;

        // 线程名登记表:Win32 线程 id → 可读名。Process.Threads 只给 id,没有名字,
        // 性能剖析时必须自己登记,否则只能对着 12280 / 23864 这种数字猜。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _threadNames
            = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        internal static void TagThread(string name)
        {
            try { _threadNames[NativeMethods.CurrentThreadId()] = name; } catch { }
        }
        private static string ThreadTag(int id)
        {
            string n; return _threadNames.TryGetValue(id, out n) ? "[" + n + "]" : "";
        }

        private static bool _skipPanels;
        private static string _only;          // --only=<片段>:只取证匹配的模块
        private static bool _skipWallpaper;   // --nopanels:只抓顶栏,迭代时省掉每面板 2.3s
        private static string _originalWallpaper;
        private static readonly List<string> _log = new List<string>();
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        internal static void Start(string[] args)
        {
            int i = Array.IndexOf(args ?? Array.Empty<string>(), "--glassshot");
            string dir = i >= 0 && i + 1 < args.Length ? args[i + 1]
                : Path.Combine(Path.GetTempPath(), "YukiDeskGlassShot");
            try { Directory.CreateDirectory(dir); } catch { }
            _dir = dir;
            _skipPanels = Array.IndexOf(args ?? Array.Empty<string>(), "--nopanels") >= 0;
            // --nobg:不换测试壁纸,直接在用户当前壁纸下取证(给"看一眼真实观感"用)
            _skipWallpaper = Array.IndexOf(args ?? Array.Empty<string>(), "--nobg") >= 0;
            // --only=<片段>:只取证名字里含该片段的模块(调参时把一轮 27s 压到几秒)
            foreach (var a in args ?? Array.Empty<string>())
            {
                if (a != null && a.StartsWith("--only=", StringComparison.Ordinal))
                {
                    _only = a.Substring(7).ToLowerInvariant();
                    break;
                }
            }
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Application.Current.Dispatcher.BeginInvoke(new Action(async () => await RunAsync()));
        }

        /// <summary>
        /// 性能自检(--glassperf [秒数]):不动壁纸、不抓图、不暂停捕获,按区间统计
        /// CPU 占用 / 捕获帧 / BitBlt 次数 / 捕获线程忙时,结束后写 log 并退出。
        /// </summary>
        internal static void StartPerf(string[] args)
        {
            int i = Array.IndexOf(args ?? Array.Empty<string>(), "--glassperf");
            int secs = 20;
            if (i >= 0 && i + 1 < args.Length) int.TryParse(args[i + 1], out secs);
            if (secs < 5) secs = 5;
            _dir = Path.Combine(Path.GetTempPath(), "YukiDeskGlassPerf");
            try { Directory.CreateDirectory(_dir); } catch { }
            _skipPanels = true;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Application.Current.Dispatcher.BeginInvoke(new Action(async () => await PerfAsync(secs)));
        }

        /// <summary>各原生线程的累计 CPU 秒(ProcessThread.TotalProcessorTime),用增量定位热点线程。</summary>
        private static Dictionary<int, double> ThreadCpu()
        {
            var d = new Dictionary<int, double>();
            try
            {
                var p = Process.GetCurrentProcess();
                foreach (ProcessThread t in p.Threads)
                {
                    try { d[t.Id] = t.TotalProcessorTime.TotalSeconds; } catch { }
                }
            }
            catch { }
            return d;
        }

        private static ProcessThread FindThread(int id)
        {
            try
            {
                foreach (ProcessThread t in Process.GetCurrentProcess().Threads) if (t.Id == id) return t;
            }
            catch { }
            return null;
        }

        private static async Task PerfAsync(int secs)
        {
            try
            {
                await Task.Delay(2500);                       // 等首帧、着色器编译、布局稳定
                var p = Process.GetCurrentProcess();
                double c0 = p.TotalProcessorTime.TotalSeconds;
                long f0 = System.Threading.Interlocked.Read(ref GlassService.StatFrames);
                long b0 = System.Threading.Interlocked.Read(ref GlassService.StatBlits);
                long s0 = System.Threading.Interlocked.Read(ref GlassService.StatPublishes);
                long bu0 = System.Threading.Interlocked.Read(ref GlassService.StatBusyTicks);
                long pu0 = System.Threading.Interlocked.Read(ref GlassService.StatPumps);
                long wr0 = System.Threading.Interlocked.Read(ref GlassService.StatWrites);
                long sw0 = System.Threading.Interlocked.Read(ref GlassService.StatSharpWrites);
                long bt0 = System.Threading.Interlocked.Read(ref GlassService.StatBlitTicks);
                long dt0 = System.Threading.Interlocked.Read(ref GlassService.StatDownTicks);
                long ft0 = System.Threading.Interlocked.Read(ref GlassService.StatDiffTicks);
                long bl0 = System.Threading.Interlocked.Read(ref GlassService.StatBlurTicks);
                // 诊断开关:隐藏顶栏再测,用来判断"空闲 CPU 是不是窗口合成/渲染本身吃掉的"。
                bool hideBar = Environment.GetEnvironmentVariable("YUKI_PERF_HIDE") == "1";
                var probeBar = MainWindow.Bar;
                if (hideBar && probeBar != null) { probeBar.Hide(); Note("性能[模式] 顶栏已隐藏"); }
                // 诊断开关:清空内容但保留窗口(区分"窗口/分层合成本身"与"可视树内容"的开销)
                if (Environment.GetEnvironmentVariable("YUKI_PERF_EMPTY") == "1" && probeBar != null)
                {
                    try { probeBar.Content = null; Note("性能[模式] 顶栏内容已清空"); } catch { }
                }
                // 诊断开关:按部件剥离可视树,二分"到底哪个部件让渲染线程常开"。
                // 用法 YUKI_PERF_STRIP=glass|bg|pills|onlysettings(可逗号组合)
                string strip = Environment.GetEnvironmentVariable("YUKI_PERF_STRIP");
                if (!string.IsNullOrEmpty(strip) && probeBar != null)
                {
                    var root = probeBar.Content as System.Windows.Controls.Panel;
                    if (root == null) Note("性能[剥离] 顶栏内容不是 Panel,跳过");
                    else
                    {
                        var items = strip.Split(',');
                        foreach (var raw in items)
                        {
                            string key = raw.Trim().ToLowerInvariant();
                            try
                            {
                                if (key == "glass")
                                {
                                    // 玻璃层宿主是插在 root 最底的非 Border 子元素
                                    for (int i = root.Children.Count - 1; i >= 0; i--)
                                    {
                                        if (!(root.Children[i] is System.Windows.Controls.Border))
                                        {
                                            root.Children.RemoveAt(i);
                                            Note("性能[剥离] 已移除玻璃层宿主 #" + i);
                                            break;
                                        }
                                    }
                                }
                                else if (key == "bg")
                                {
                                    foreach (System.Windows.UIElement c in root.Children)
                                    {
                                        var bd = c as System.Windows.Controls.Border;
                                        if (bd != null && bd.Background != null)
                                        {
                                            bd.Visibility = Visibility.Collapsed;
                                            Note("性能[剥离] 已折叠背景条 颜色=" + (bd.Background is SolidColorBrush scb ? scb.Color.ToString() : "非纯色"));
                                            break;
                                        }
                                    }
                                }
                                else if (key == "pills")
                                {
                                    int n = 0;
                                    foreach (System.Windows.UIElement c in root.Children)
                                    {
                                        var gr = c as System.Windows.Controls.Panel;
                                        if (gr != null && gr != root)
                                        {
                                            gr.Visibility = Visibility.Collapsed;
                                            n++;
                                        }
                                    }
                                    Note("性能[剥离] 已折叠模块容器 " + n + " 个");
                                }
                                else if (key.StartsWith("solo:", StringComparison.Ordinal))
                                {
                                    string want = key.Substring(5);
                                    var mods = MainWindow.Bar.DiagnosticModules;
                                    int keep = 0;
                                    foreach (var m in mods)
                                    {
                                        string nm = (m.DiagnosticName ?? "").ToLowerInvariant();
                                        if (nm.Contains(want)) { m.Visibility = Visibility.Visible; keep++; }
                                        else if (m.Visibility == Visibility.Visible) m.Visibility = Visibility.Collapsed;
                                    }
                                    Note("性能[剥离] 只留匹配 " + want + " 的模块 " + keep + " 个");
                                }
                                else if (key == "addpilltick" || key == "addpilltick_display" || key == "addpilltick_cache")
                                {
                                    // 每 1 秒改一次文字的空胶囊:用来量化"文字变化 → 重排+重呈"的真实代价,
                                    // 并顺带 A/B 两种常见缓解(文字渲染模式 Display / 位图缓存)。
                                    var tb = new System.Windows.Controls.TextBlock
                                    {
                                        Text = "T0",
                                        FontSize = 11,
                                        Foreground = Brushes.White,
                                        HorizontalAlignment = HorizontalAlignment.Center,
                                        VerticalAlignment = VerticalAlignment.Center
                                    };
                                    if (key == "addpilltick_display") TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
                                    var bd2 = new System.Windows.Controls.Border
                                    {
                                        Width = 120 * Ui.Scale,
                                        Height = 34.2,
                                        CornerRadius = new CornerRadius(17),
                                        Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                                        HorizontalAlignment = HorizontalAlignment.Left,
                                        VerticalAlignment = VerticalAlignment.Center,
                                        Margin = new Thickness(600, 3.6, 0, 0),
                                        Child = tb
                                    };
                                    if (key == "addpilltick_cache")
                                    {
                                        var cache = new System.Windows.Media.BitmapCache(1.0);
                                        TextOptions.SetTextRenderingMode(bd2, TextRenderingMode.ClearType);
                                        bd2.CacheMode = cache;
                                    }
                                    root.Children.Add(bd2);
                                    int n2 = 0;
                                    var tm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                                    tm.Tick += (s3, e3) => { n2++; tb.Text = "T" + (n2 % 10); };
                                    tm.Start();
                                    Note("性能[剥离] 已加入每秒改字的胶囊(" + key + ")");
                                }
                                else if (key == "addpill")
                                {
                                    // 合成一个"没有任何模块逻辑"的静态胶囊:用来判定"静态几何本身是否要钱"。
                                    var bd = new System.Windows.Controls.Border
                                    {
                                        Width = 120 * Ui.Scale,
                                        Height = 34.2,
                                        CornerRadius = new CornerRadius(17),
                                        Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                                        HorizontalAlignment = HorizontalAlignment.Left,
                                        VerticalAlignment = VerticalAlignment.Center,
                                        Margin = new Thickness(600, 3.6, 0, 0),
                                        Child = new System.Windows.Controls.TextBlock { Text = "STATIC", FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                                    };
                                    root.Children.Add(bd);
                                    Note("性能[剥离] 已加入静态胶囊(无模块逻辑)");
                                }
                                else if (key == "onlysettings")
                                {
                                    var mods = MainWindow.Bar.DiagnosticModules;
                                    int hidden = 0;
                                    for (int i = 1; i < mods.Count; i++)
                                    {
                                        if (mods[i].Visibility == Visibility.Visible) { mods[i].Visibility = Visibility.Collapsed; hidden++; }
                                    }
                                    Note("性能[剥离] 已折叠模块 " + hidden + " 个,只留 " + (mods.Count > 0 ? mods[0].DiagnosticName : "无"));
                                }
                            }
                            catch (Exception ex) { Note("性能[剥离] " + key + " 失败: " + ex.GetType().Name + ": " + ex.Message); }
                        }
                    }
                }
                // 可选:测量期间保持一个悬停面板打开(YUKI_PERF_PANEL=<模块名片段>),
            // 用来量「展开页实时玻璃」的常驻成本(采样线程在面板打开时按帧率上限跑)。
            ModuleBase openPanel = null;
            string perfPanel = Environment.GetEnvironmentVariable("YUKI_PERF_PANEL");
            if (!string.IsNullOrEmpty(perfPanel) && MainWindow.Bar != null)
            {
                foreach (var m in MainWindow.Bar.DiagnosticModules)
                {
                    if (!m.DiagnosticHasPanel) continue;
                    if (m.DiagnosticName.ToLowerInvariant().Contains(perfPanel.ToLowerInvariant())) { openPanel = m; break; }
                }
                if (openPanel != null) { openPanel.ShowPanel(); Note("性能[面板] 已打开 " + openPanel.DiagnosticName + " 动画属性=" + openPanel.DiagnosticPanelAnimated); }
            }
            TagThread("UI线程");
                // 计数布局失效:若空闲时 LayoutUpdated 仍在狂跑,说明有东西每帧弄脏可视树(会把渲染线程拖成常开)。
                long layout0 = 0;
                var bar = MainWindow.Bar;
                if (bar != null) bar.LayoutUpdated += (s, e) => System.Threading.Interlocked.Increment(ref layout0);
                var t0 = ThreadCpu();
                GlassService.SetRenderLatencyProbe(true);       // 量「捕获开始→开始渲染」的端到端管线延迟
                Note("性能[起点] " + GlassService.DiagnosticSnapshot());
                try
                {
                    int tier = System.Windows.Media.RenderCapability.Tier >> 16;
                    Note("性能[渲染] Tier=" + tier + "(0=软件 1=部分硬件 2=全硬件) RenderMode="
                        + System.Windows.Media.RenderOptions.ProcessRenderMode
                        + " 屏幕=" + NativeMethods.ScreenWidth() + "x" + NativeMethods.ScreenHeight()
                        + " 刷新率=" + NativeMethods.ScreenRefreshHz() + "Hz");
                }
                catch { }
                await Task.Delay(secs * 1000);
                double c1 = p.TotalProcessorTime.TotalSeconds;
                long f1 = System.Threading.Interlocked.Read(ref GlassService.StatFrames);
                long b1 = System.Threading.Interlocked.Read(ref GlassService.StatBlits);
                long s1 = System.Threading.Interlocked.Read(ref GlassService.StatPublishes);
                long bu1 = System.Threading.Interlocked.Read(ref GlassService.StatBusyTicks);
                double busyMs = (bu1 - bu0) * 1000.0 / Stopwatch.Frequency;
                long frames = f1 - f0;
                Note(string.Format(
                    "性能[区间] {0}s CPU={1:F2}%(单核) 捕获帧={2} BitBlt={3} 发布={4} 更新fps={13:F1} 捕获忙={5:F1}ms 均={6:F2}ms/帧 CPU增量={7:F2}s 线程={8} 工作集={9:F0}MB 泵={10} 写帧={11} 写锐帧={12}",
                    secs, (c1 - c0) / secs * 100.0, frames, b1 - b0, s1 - s0, busyMs,
                    frames > 0 ? busyMs / frames : 0.0, c1 - c0, p.Threads.Count, p.WorkingSet64 / 1048576.0,
                    System.Threading.Interlocked.Read(ref GlassService.StatPumps) - pu0,
                    System.Threading.Interlocked.Read(ref GlassService.StatWrites) - wr0,
                    System.Threading.Interlocked.Read(ref GlassService.StatSharpWrites) - sw0,
                    (s1 - s0) / (double)secs));
                // 按线程看 CPU 增量:定位"到底是哪个线程在烧",比只看进程总账有用得多。
                var t1 = ThreadCpu();
                var rows = new List<string>();
                foreach (var kv in t1)
                {
                    double d; t0.TryGetValue(kv.Key, out d);
                    double ms = (kv.Value - d) * 1000.0;
                    if (ms >= 20)
                    {
                        string extra = "";
                        try
                        {
                            var pt = FindThread(kv.Key);
                            if (pt != null)
                            {
                                double age = (DateTime.Now - pt.StartTime).TotalSeconds;
                                extra = string.Format(" 起于{0:F0}s前 等待原因={1}", age, pt.WaitReason);
                            }
                        }
                        catch { }
                        rows.Add(string.Format("  线程{0}{1} {2:F0}ms{3}", kv.Key, ThreadTag(kv.Key), ms, extra));
                    }
                }
                rows.Sort((a, b) => b.Length.CompareTo(a.Length));
                Note("性能[线程] " + string.Join(" | ", rows.ToArray()));
                double msOf(long a, long b, long n)
                {
                    return n <= 0 ? 0.0 : (b - a) * 1000.0 / Stopwatch.Frequency / n;
                }
                Note(string.Format("性能[分阶段] 每帧 拷贝={0:F2}ms 降采样={1:F2}ms 差分={2:F2}ms 模糊={3:F2}ms",
                    msOf(bt0, System.Threading.Interlocked.Read(ref GlassService.StatBlitTicks), b1 - b0),
                    msOf(dt0, System.Threading.Interlocked.Read(ref GlassService.StatDownTicks), b1 - b0),
                    msOf(ft0, System.Threading.Interlocked.Read(ref GlassService.StatDiffTicks), b1 - b0),
                    msOf(bl0, System.Threading.Interlocked.Read(ref GlassService.StatBlurTicks), s1 - s0)));
                Note("性能[布局] LayoutUpdated=" + System.Threading.Interlocked.Read(ref layout0) + " 次/" + secs + "s");
                if (openPanel != null) { try { Note("性能[面板] 状态 " + GlassService.DiagnosticPanelState() + " 动画属性=" + openPanel.DiagnosticPanelAnimated + " " + openPanel.DiagnosticPanelTransform); openPanel.ClosePanelImmediately(); } catch { } }
                // 端到端延迟:捕获开始 → WPF 开始渲染含这帧的绘制(1 帧 ≈ 一个刷新周期 ≈ 16.7ms)
                Note("性能[延迟] " + GlassService.RenderLatencyReport());
                GlassService.SetRenderLatencyProbe(false);
            if (hideBar && probeBar != null) { try { probeBar.Show(); } catch { } }
                Note("性能[终点] " + GlassService.DiagnosticSnapshot());
            }
            catch (Exception ex) { Note("性能自检异常: " + ex); }
            finally
            {
                try { File.WriteAllLines(Path.Combine(_dir, "perf.txt"), _log); } catch { }
                try { Application.Current.Shutdown(); } catch { }
            }
        }

        private static async Task RunAsync()
        {
            try
            {
                Note("== glassshot 开始 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Note(string.Format("屏幕物理={0}x{1} dpiScale={2:F2} 虚拟屏幕={3:F0}x{4:F0}",
                    NativeMethods.ScreenWidth(), NativeMethods.ScreenHeight(), NativeMethods.SystemDpiScale(),
                    SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
                DumpSettings();
                await Task.Delay(1200);
                Note("状态[初始] " + GlassService.DiagnosticSnapshot());

                // 1) 原壁纸下的原样顶栏(证明"不改环境也在工作")
                await ShotAsync("bar-original");

                // 2) 换成高频细节测试壁纸,才看得清"糊没糊"
                bool wallpaper = _skipWallpaper ? false : TryInstallTestWallpaper();
                if (wallpaper)
                {
                    await Task.Delay(900);
                    Note("状态[换壁纸后] " + GlassService.DiagnosticSnapshot());
                    await ShotAsync("bar-test");
                    GlassService.SaveLatestPng(Path.Combine(_dir, "glass-input-test.png"));
                }

                // 3) 逐个悬停面板:开头→等一次性玻璃捕获完成→抓图→收起
                var modules = new List<ModuleBase>();
                try { if (MainWindow.Bar != null) modules.AddRange(MainWindow.Bar.DiagnosticModules); } catch { }
                foreach (var m in _skipPanels ? new List<ModuleBase>() : modules)
                {
                    bool hasPanel = false;
                    string name = "?";
                    try { hasPanel = m.DiagnosticHasPanel; name = m.DiagnosticName; } catch { }
                    if (!hasPanel) continue;
                    if (!string.IsNullOrEmpty(_only) && !name.ToLowerInvariant().Contains(_only)) continue;
                    try
                    {
                        m.ShowPanel();
                        // 等面板淡入动画结束(可用 YUKI_SHOT_PANEL_DELAY 覆盖,用于抓"动画进行中"的画面)
            int fadeMs = 1400;
            int.TryParse(Environment.GetEnvironmentVariable("YUKI_SHOT_PANEL_DELAY"), out fadeMs);
            if (fadeMs <= 0) fadeMs = 1400;
            await Task.Delay(fadeMs);
                        try
                        {
                            var pw = m.DiagnosticPanelWindow; var pb = m.DiagnosticPanel;
                            var fill = pb == null ? null : pb.Background as System.Windows.Media.ImageBrush;
                            Note(string.Format("面板 {0} 矩形 DIP L={1:F1} T={2:F1} W={3:F1} H={4:F1} 背景={5} 位图={6} 不透明度={7:F2}",
                                name, pw.Left, pw.Top, pw.ActualWidth, pw.ActualHeight,
                                pb == null || pb.Background == null ? "null" : pb.Background.GetType().Name,
                                fill == null || fill.ImageSource == null ? "无" : fill.ImageSource.GetType().Name + " " + ((System.Windows.Media.Imaging.BitmapSource)fill.ImageSource).PixelWidth + "x" + ((System.Windows.Media.Imaging.BitmapSource)fill.ImageSource).PixelHeight,
                                fill == null ? 0 : fill.Opacity));
                            // 现在玻璃是垫在展开页里的一层(带着色器的 Rectangle),不再是面板背景贴图
                            Note("面板玻璃 " + name + " " + GlassService.DiagnosticPanelState());
                        }
                        catch (Exception ex) { Note("面板 " + name + " 矩形读取失败: " + ex.Message); }
                        await ShotAsync("panel-" + name);
                        m.HidePanel();
                        await Task.Delay(420);
                    }
                    catch (Exception ex) { Note("面板 " + name + " 取证失败: " + ex.Message); }
                }

                Note("状态[结束] " + GlassService.DiagnosticSnapshot());
                var p = Process.GetCurrentProcess();
                Note(string.Format("进程 CPU 累计={0:F2}s 工作集={1:F0}MB",
                    p.TotalProcessorTime.TotalSeconds, p.WorkingSet64 / 1048576.0));
            }
            catch (Exception ex)
            {
                Note("取证异常: " + ex);
            }
            finally
            {
                if (_originalWallpaper != null) TryRestoreWallpaper();
                try { File.WriteAllLines(Path.Combine(_dir, "log.txt"), _log); } catch { }
                try { Application.Current.Shutdown(); } catch { }
            }
        }

        // ---------- 单项取证 ----------

        /// <summary>抓一次全屏合成图。RTB 参考图(进程内渲染结果)也顺手存一份,便于区分
        /// "WPF 自己渲染成啥样"和"DWM 最终合成成啥样"。</summary>
        private static async Task ShotAsync(string name)
        {
            string file = Path.Combine(_dir, "shot-" + name + ".png");
            try
            {
                SaveRtb(MainWindow.Bar, Path.Combine(_dir, "rtb-" + name + ".png"));
                GlassService.BeginShoot();
                try
                {
                    // 默认等 420ms 让 DWM 把恢复后的窗口重新合成进去;抓"刚显形的那几帧"时可调小
                    int grabDelay = 420;
                    int.TryParse(Environment.GetEnvironmentVariable("YUKI_SHOT_GRAB_DELAY"), out grabDelay);
                    if (grabDelay < 0) grabDelay = 420;
                    await Task.Delay(grabDelay);
                    bool ok = await Task.Run(() => GrabScreen(file));
                    Note(string.Format("抓图 {0} {1} t={2:F1}s", name, ok ? "成功" : "失败", _clock.Elapsed.TotalSeconds));
                }
                finally { GlassService.EndShoot(); }
            }
            catch (Exception ex) { Note("抓图 " + name + " 异常: " + ex.Message); }
        }

        private static bool GrabScreen(string path)
        {
            int w = NativeMethods.ScreenWidth(), h = NativeMethods.ScreenHeight();
            if (w <= 0 || h <= 0) return false;
            if (!NativeMethods.CreateStripCapture(w, h, out IntPtr screenDc, out IntPtr memoryDc, out IntPtr dib, out IntPtr bits))
            { Note("全屏捕获失败 err=" + Marshal.GetLastWin32Error()); return false; }
            try
            {
                if (!NativeMethods.BlitStrip(memoryDc, w, h, screenDc, 0, 0)) return false;
                var px = new byte[w * h * 4];
                Marshal.Copy(bits, px, 0, px.Length);
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, w * 4);
                bmp.Freeze();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(path)) enc.Save(fs);
                return true;
            }
            finally { NativeMethods.DestroyStripCapture(ref screenDc, ref memoryDc, ref dib); }
        }

        private static void SaveRtb(FrameworkElement element, string path)
        {
            try
            {
                if (element == null) return;
                int w = Math.Max(1, (int)Math.Round(element.ActualWidth));
                int h = Math.Max(1, (int)Math.Round(element.ActualHeight));
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(element);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
            }
            catch { }
        }

        // ---------- 测试壁纸 ----------

        /// <summary>生成高频细节测试壁纸:24px 棋盘 + 顶栏区域强对比条 + 十字准星与文字。
        /// 细节越密,模糊是否生效越一眼可辨(糊到位应该只剩平滑色块,文字完全不可读)。</summary>
        private static bool TryInstallTestWallpaper()
        {
            try
            {
                string current = null;
                try { current = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string; } catch { }
                int w = NativeMethods.ScreenWidth(), h = NativeMethods.ScreenHeight();
                if (w <= 0 || h <= 0) return false;
                string file = Path.Combine(_dir, "wallpaper-test.png");

                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new LinearGradientBrush(
                        Color.FromRgb(0x10, 0x14, 0x1C), Color.FromRgb(0x2A, 0x3C, 0x50), 90), null, new Rect(0, 0, w, h));
                    // 全屏 24px 棋盘(给悬停面板当背景细节)
                    for (int y = 0; y < h; y += 24)
                        for (int x = 0; x < w; x += 24)
                        {
                            bool on = (((x / 24) + (y / 24)) & 1) == 0;
                            var c = on ? Color.FromRgb(0x24, 0x32, 0x42) : Color.FromRgb(0x14, 0x1C, 0x26);
                            dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(x, y, 24, 24));
                        }
                    // 顶栏区域(y 0..62 物理)再叠一层强对比 8px 条纹:模糊半径 36px 应把它抹成纯色
                    for (int x = 0; x < w; x += 8)
                    {
                        bool on = (x / 8) % 2 == 0;
                        var c = on ? Color.FromRgb(0xFA, 0xFA, 0xF0) : Color.FromRgb(0x08, 0x08, 0x0C);
                        dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(x, 0, 8, 64));
                    }
                    // 文字层:面板打开后被盖住的部分应该糊成一团,未盖住的部分仍然锐利
                    var typeface = new Typeface("Segoe UI");
                    for (int i = 0; i < 12; i++)
                    {
                        double ty = 80 + i * 90;
                        var ft = new FormattedText("BLUR TEST 模糊测试 0123456789 ABCDEFG · YukiDesk Glass",
                            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                            typeface, 26, new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0x8A, 0x3C)), 1.0);
                        dc.DrawText(ft, new Point(40, ty));
                    }
                }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(file)) enc.Save(fs);

                _originalWallpaper = current;
                if (!SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, file, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
                { Note("设置测试壁纸返回 false err=" + Marshal.GetLastWin32Error()); return false; }
                Note("测试壁纸已安装: " + file + " (原壁纸=" + (current ?? "null") + ")");
                return true;
            }
            catch (Exception ex) { Note("测试壁纸失败: " + ex.Message); return false; }
        }

        private static void TryRestoreWallpaper()
        {
            try
            {
                if (string.IsNullOrEmpty(_originalWallpaper)) return;
                SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, _originalWallpaper, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                Note("原壁纸已还原");
            }
            catch { }
        }

        // ---------- 日志 ----------

        private static void DumpSettings()
        {
            var s = App.Settings;
            if (s == null) return;
            Note(string.Format(
                "设置: PillMaterial={0} Scale={1} GlassBlurRadius={2} GlassOpacity={3} GlassTint={4}@{5} GlassMaxFps={6} Refr={7} Rim={8} Chroma={9} Anim={10}ms",
                s.PillMaterial, s.Scale, s.GlassBlurRadius, s.GlassOpacity, s.GlassTint, s.GlassTintOpacity,
                s.GlassMaxFps, s.GlassRefraction, s.GlassRim, s.GlassChroma, s.AnimationDurationMs));
            try
            {
                if (MainWindow.Bar != null)
                    Note(string.Format("主窗: {0:F1}x{1:F1} DIP 位置=({2:F1},{3:F1}) 可见={4}",
                        MainWindow.Bar.ActualWidth, MainWindow.Bar.ActualHeight,
                        MainWindow.Bar.Left, MainWindow.Bar.Top, MainWindow.Bar.IsVisible));
            }
            catch { }
            try
            {
                var rects = GlassService.DiagnosticPillRects;
                if (rects != null)
                {
                    var sb = new System.Text.StringBuilder("胶囊矩形(DIP): ");
                    foreach (var r in rects) sb.AppendFormat("({0:F1},{1:F1},{2:F1}x{3:F1}) ", r.X, r.Y, r.Width, r.Height);
                    Note(sb.ToString());
                }
            }
            catch { }
        }

        private static void Note(string msg)
        {
            string line = string.Format("[{0,7:F2}s] {1}", _clock.Elapsed.TotalSeconds, msg);
            _log.Add(line);
            try { App.Log("[glassshot] " + msg); } catch { }
        }
    }
}
