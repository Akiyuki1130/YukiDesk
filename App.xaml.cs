using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopBar.Models;
using DesktopBar.Services;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace DesktopBar
{
    public partial class App : System.Windows.Application
    {
        private static Mutex _mutex;
        public static AppSettings Settings;
        public static MainWindow Bar;
        private static SettingsWindow _settingsWin;
        private static readonly object LogGate = new object();
        // 主进程与温度从属进程并发写同一 log.txt,用命名互斥体把"轮转+追加"包成跨进程原子操作。
        private static Mutex _logMutex;
        private Forms.NotifyIcon _tray;
        private Icon _trayIcon;

        public static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YukiDesk");
        public static void Log(string msg)
        {
            try
            {
                if (_logMutex == null) Interlocked.CompareExchange(ref _logMutex, new Mutex(false, "Local\\YukiDesk_Log_Mutex"), null);
                bool held = false;
                try { held = _logMutex.WaitOne(800); } catch (AbandonedMutexException) { held = true; }
                try
                {
                    lock (LogGate)
                    {
                        Directory.CreateDirectory(AppDataDir);
                        string logPath = Path.Combine(AppDataDir, "log.txt");
                        try
                        {
                            if (File.Exists(logPath) && new FileInfo(logPath).Length > 2 * 1024 * 1024)
                            {
                                string backup = logPath + ".1";
                                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                                try { File.Move(logPath, backup); } catch { }
                            }
                        }
                        catch { }
                        File.AppendAllText(logPath,
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                    }
                }
                finally { if (held) try { _logMutex.ReleaseMutex(); } catch { } }
            }
            catch { }
        }

        // ---------- 无界面自检:液态玻璃着色器 ----------
        // 只验证 "HLSL 能不能在 ps_3_0 下编过 + WPF 能不能收下字节码", 不建窗口不捕获。
        // 用法: YukiDesk.exe --glasstest
        private static void RunGlassTest()
        {
            App.Log("=== 液态玻璃自检 开始 ===");
            try
            {
                // 半分辨率像素下的胶囊(按实测 DIP*0.75 近似;这里只关心指令数,几何精确性无所谓)
                double[] dips = { 34.2, 165.5, 52.2, 77.4, 79.2, 112.5, 108.0, 34.2, 34.2, 52.2, 77.4, 108.0, 34.2 };
                var rects = new float[dips.Length][];
                double x = 9.0;
                for (int i = 0; i < dips.Length; i++)
                {
                    rects[i] = new float[]
                    {
                        (float)((x + dips[i] / 2.0) * 0.75),
                        (float)((3.6 + 17.1) * 0.75),
                        (float)(dips[i] / 2.0 * 0.75),
                        (float)(17.1 * 0.75)
                    };
                    x += dips[i] + 6.0;
                }
                for (int n = 1; n <= rects.Length; n++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var fx = UI.LiquidGlassEffect.Create(rects, n, 12.0, 1280, 54);
                    sw.Stop();
                    App.Log(string.Format("  [glasstest] n={0} -> {1} ({2} ms)", n, fx == null ? "失败" : "成功", sw.ElapsedMilliseconds));
                    if (fx == null) break;
                }
            }
            catch (Exception ex) { App.Log("  [glasstest] 自检异常: " + ex); }
            App.Log("=== 液态玻璃自检 结束 ===");
        }

        // ---------- 无界面自检:WPF ShaderEffect 的采样输入到底是什么 ----------
        // 把一张合成的高饱和位图(左半边纯红、右半边纯绿、顶部一行纯白)塞进 Image,
        // 挂上液态玻璃着色器,用 RenderTargetBitmap 离屏渲染后读回像素。
        // 目的:完全绕开主窗口/捕获链路,单独判定"WPF 交给采样器的是不是这张位图"。
        // 用法: YukiDesk.exe --glasssampler [输出目录]
        private static void RunGlassSamplerTest(string[] args)
        {
            App.Log("=== 着色器采样自检 开始 ===");
            string dir = Path.Combine(Path.GetTempPath(), "yukidesk-glasssampler");
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--glasssampler") dir = args[i + 1];
            try { Directory.CreateDirectory(dir); } catch { }
            try
            {
                // 合成位图:左红右绿,第 0~8 行纯白。Bgra32,alpha=255(不透明)。
                int sw = 256, sh = 60;
                var src = new System.Windows.Media.Imaging.WriteableBitmap(sw, sh, 96, 96, PixelFormats.Bgra32, null);
                var px = new byte[sw * sh * 4];
                for (int y = 0; y < sh; y++)
                {
                    for (int x = 0; x < sw; x++)
                    {
                        int i = (y * sw + x) * 4;
                        if (y < 8) { px[i] = 255; px[i + 1] = 255; px[i + 2] = 255; px[i + 3] = 255; }
                        else if (x < sw / 2) { px[i] = 0; px[i + 1] = 0; px[i + 2] = 255; px[i + 3] = 255; }
                        else { px[i] = 0; px[i + 1] = 255; px[i + 2] = 0; px[i + 3] = 255; }
                    }
                }
                src.WritePixels(new Int32Rect(0, 0, sw, sh), px, sw * 4, 0);
                src.Freeze();

                // 一个覆盖整个面的"胶囊"(半宽/半高=整面),保证 sd 内场覆盖全图,位移不为 0 的地方也能看出来。
                var rects = new float[][] { new float[] { sw * 0.5f, sh * 0.5f, sw * 0.5f, sh * 0.5f } };
                var fx = UI.LiquidGlassEffect.Create(rects, 1, 15.0, sw, sh);
                App.Log("  [glasssampler] 着色器创建 = " + (fx == null ? "失败" : "成功"));
                if (fx == null) { App.Log("=== 着色器采样自检 结束 ==="); return; }
                fx.Input = new System.Windows.Media.ImageBrush(src) { Stretch = System.Windows.Media.Stretch.Fill };
                fx.Refr = 0.0; fx.Rim = 0.0; fx.Chroma = 0.0;   // 先把位移/棱光/色散全关掉,只测"采样值"

                // 决定性对照:把采样 Brush 换成"纯品红",与元素内容(红/绿/白图案)明显不同。
                // 若输出还是图案 → Brush 根本没被绑定(采样到的是隐式输入);
                // 若输出品红 → Brush 绑定成功,问题在别处(比如 WriteableBitmap 当 Brush)。
                {
                    int mw = 8, mh = 8;
                    var mp = new byte[mw * mh * 4];
                    for (int i = 0; i < mp.Length; i += 4) { mp[i] = 255; mp[i + 1] = 0; mp[i + 2] = 255; mp[i + 3] = 255; }
                    var mag = System.Windows.Media.Imaging.BitmapSource.Create(mw, mh, 96, 96, PixelFormats.Bgra32, null, mp, mw * 4);
                    mag.Freeze();
                    fx.Input = new System.Windows.Media.ImageBrush(mag) { Stretch = System.Windows.Media.Stretch.Fill };
                    var img2 = new System.Windows.Controls.Image { Source = src, Width = sw, Height = sh, Stretch = System.Windows.Media.Stretch.Fill, Effect = fx };
                    var host2 = new System.Windows.Controls.Grid
                    {
                        Width = sw, Height = sh,
                        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 128, 0))
                    };
                    host2.Children.Add(img2);
                    host2.Measure(new System.Windows.Size(sw, sh));
                    host2.Arrange(new System.Windows.Rect(0, 0, sw, sh));
                    host2.UpdateLayout();
                    var rtbX = new System.Windows.Media.Imaging.RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
                    rtbX.Render(host2);
                    var xp = new byte[sw * sh * 4];
                    rtbX.CopyPixels(xp, sw * 4, 0);
                    SavePng(rtbX, Path.Combine(dir, "sampler-magentabrush.png"));
                    App.Log("  [glasssampler] 采样Brush=品红 左半=" + Mean4(xp, sw, 0, 20, sw / 2, sh - 20)
                            + " 右半=" + Mean4(xp, sw, sw / 2, 20, sw / 2, sh - 20));
                }
                var img = new System.Windows.Controls.Image { Source = src, Width = sw, Height = sh, Stretch = System.Windows.Media.Stretch.Fill };
                img.Effect = fx;
                var host = new System.Windows.Controls.Grid
                {
                    Width = sw, Height = sh,
                    // 品红底:着色器输出"黑"与"元素根本没画"在这两种情况下才区分得开(黑底会把两者混为一谈)。
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 0, 255))
                };
                host.Children.Add(img);
                host.Measure(new System.Windows.Size(sw, sh));
                host.Arrange(new System.Windows.Rect(0, 0, sw, sh));
                host.UpdateLayout();

                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(host);
                var outPx = new byte[sw * sh * 4];
                rtb.CopyPixels(outPx, sw * 4, 0);
                SavePng(rtb, Path.Combine(dir, "sampler-effect.png"));
                // 对照组:同一张图不挂着色器,证明"位图本身是亮的"。
                img.Effect = null;
                host.UpdateLayout();
                var rtb2 = new System.Windows.Media.Imaging.RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
                rtb2.Render(host);
                var rawPx = new byte[sw * sh * 4];
                rtb2.CopyPixels(rawPx, sw * 4, 0);
                SavePng(rtb2, Path.Combine(dir, "sampler-plain.png"));

                // 对照组 2:内置 BlurEffect。若内置效果也"画不出来",说明是 RTB/软件渲染不支持效果,
                // 而不是我们的着色器有问题。
                try
                {
                    img.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 8 };
                    host.UpdateLayout();
                    var rtb3 = new System.Windows.Media.Imaging.RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
                    rtb3.Render(host);
                    var blurPx = new byte[sw * sh * 4];
                    rtb3.CopyPixels(blurPx, sw * 4, 0);
                    SavePng(rtb3, Path.Combine(dir, "sampler-blur.png"));
                    App.Log("  [glasssampler] 内置 BlurEffect 左半=" + Mean4(blurPx, sw, 0, 20, sw / 2, sh - 20)
                            + " 顶行=" + Mean4(blurPx, sw, 0, 0, sw, 8));
                }
                catch (Exception ex2) { App.Log("  [glasssampler] 内置效果异常: " + ex2.Message); }
                App.Log("  [glasssampler] 区域均值 B,G,R,A   左半 着色=" + Mean4(outPx, sw, 0, 20, sw / 2, sh - 20)
                        + " 原图=" + Mean4(rawPx, sw, 0, 20, sw / 2, sh - 20));
                App.Log("  [glasssampler]                  右半 着色=" + Mean4(outPx, sw, sw / 2, 20, sw / 2, sh - 20)
                        + " 原图=" + Mean4(rawPx, sw, sw / 2, 20, sw / 2, sh - 20));
                App.Log("  [glasssampler]                  顶行 着色=" + Mean4(outPx, sw, 0, 0, sw, 8)
                        + " 原图=" + Mean4(rawPx, sw, 0, 0, sw, 8));
                App.Log("  [glasssampler] 环境变量 YUKI_GLASS_SHADER_DEBUG="
                        + (Environment.GetEnvironmentVariable("YUKI_GLASS_SHADER_DEBUG") ?? "(未设=0)"));
            }
            catch (Exception ex) { App.Log("  [glasssampler] 自检异常: " + ex); }
            App.Log("=== 着色器采样自检 结束 ===");
        }

        private static string Mean4(byte[] px, int stride, int x0, int y0, int w, int h)
        {
            double b = 0, g = 0, r = 0, a = 0; long n = 0;
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                {
                    int i = (y * stride + x) * 4;
                    b += px[i]; g += px[i + 1]; r += px[i + 2]; a += px[i + 3]; n++;
                }
            if (n == 0) return "n/a";
            return string.Format("[{0:F0},{1:F0},{2:F0},{3:F0}]", b / n, g / n, r / n, a / n);
        }

        private static void SavePng(System.Windows.Media.Imaging.BitmapSource bmp, string path)
        {
            try
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
                enc.Save(fs);
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // 全局异常兜底必须在 --tempslave 分支之前注册:从属进程没有任何 UI,漏注册就完全失声。
            DispatcherUnhandledException += (s, args) =>
            {
                Log("未处理异常: " + args.Exception);
                // 启动完成前(主窗/托盘还没建)吞异常会留下"无窗口无托盘却占着单实例互斥体"的僵尸,
                // 此时必须让进程退出;启动完成后才吞掉以保住顶栏。
                if (Bar == null) { args.Handled = false; Shutdown(1); }
                else args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) => Log("后台线程致命异常: " + args.ExceptionObject);
            // 温度从属模式:计划任务以最高权限启动,读原生 Tctl 写共享内存,不建互斥体/托盘/主窗。
            if (e.Args != null && Array.IndexOf(e.Args, "--tempslave") >= 0)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Services.TemperatureSlave.Run(() => Dispatcher.BeginInvoke(new Action(Shutdown)));
                return;
            }
            // 无界面自检:只跑液态玻璃着色器编译,2 秒内给出编译错误原文,不开任何窗口。
            // 用法: YukiDesk.exe --glasstest
            if (e.Args != null && Array.IndexOf(e.Args, "--glasstest") >= 0)
            {
                RunGlassTest();
                Shutdown();
                return;
            }
            // 无界面自检:离屏渲染一张合成位图 + 液态玻璃着色器,判定 WPF 采样输入是否正确。
            // 用法: YukiDesk.exe --glasssampler [输出目录]
            if (e.Args != null && Array.IndexOf(e.Args, "--glasssampler") >= 0)
            {
                RunGlassSamplerTest(e.Args);
                Shutdown();
                return;
            }
            _mutex = new Mutex(true, "YukiDesk_SingleInstance_Mutex", out bool created);
            if (!created) { Shutdown(); return; }
            Settings = SettingsService.Load(out bool firstRun);
            // 配置是用户的明确偏好；不要用一次可能失败的注册表读取反向覆盖它。
            AutoStartService.Set(Settings.AutoStart);
            // 上次会话残留在 %TEMP%\YukiDeskClipboard 的临时副本:剪贴板仍引用的保留,其余清掉。
            try { FileClipboard.CleanupStaleTempCopies(); } catch { }
            try { Directory.CreateDirectory(Settings.StorageRoot); foreach (var n in Settings.Folders) Directory.CreateDirectory(Path.Combine(Settings.StorageRoot, n)); } catch { }
            // 配置文件损坏时不要用默认值立即覆写(原始内容已在 .corrupt-* 隔离副本里),等用户显式保存。
            if (Settings.Folders.Count == 0 && !SettingsService.CorruptConfigDetected)
            {
                Settings.Folders.Add("常用");
                try { Directory.CreateDirectory(Path.Combine(Settings.StorageRoot, "常用")); } catch { }
                Save();
            }
            if (firstRun && !SettingsService.CorruptConfigDetected) Save();
            Log("YukiDesk 启动配置已加载");
            // 无界面自检:只烘焙云母位图并写 PNG,不建任何窗口、不占屏幕。
            // 用法: YukiDesk.exe --micadump <输出.png>
            if (e.Args != null && Array.IndexOf(e.Args, "--micadump") >= 0)
            {
                RunMicaDump(e.Args);
                Shutdown();
                return;
            }
            ThemeService.Start();
            Log("YukiDesk 主题服务已启动");
            Services.MicaService.Start();
            Log("YukiDesk 云母服务已启动");
            Services.GlassService.Start();
            Log("YukiDesk 玻璃服务已启动");

            // 自检:主窗口+托盘就绪之后再把设置窗口真的建出来并展示 6 秒,然后关掉。
            // "打开设置就崩"这类回归只能这么复现——所有卡片要在 Loaded 之后
            // 才会跑 ApplyMica/Invalidate,不展示就什么都触发不到。
            // 必须在主窗口之后开:产品里设置窗口永远是后开的,提前到 OnStartup 里开
            // 会让设置窗口的 Loaded 和 new MainWindow() 抢跑,那是自检自己造出来的崩溃。
            if (Array.IndexOf(e.Args ?? Array.Empty<string>(), "--settings") >= 0)
            {
                var t = new Timer(_ =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Log("[selftest] 打开设置窗口");
                            try { ShowSettings(); }
                            catch (Exception ex) { Console.WriteLine("[selftest] ShowSettings 抛异常: " + ex); }
                            DumpSettingsWin("ShowSettings 返回");
                        }));
                    }
                    catch { }
                }, null, 1500, Timeout.Infinite);
                var t2 = new Timer(_ =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DumpSettingsWin("8.5 秒到点");
                            Log("[selftest] 关闭设置窗口");
                            _settingsWin?.Close();
                        }));
                    }
                    catch { }
                }, null, 8500, Timeout.Infinite);
            }
            Bar = new MainWindow();
            Log("YukiDesk 主窗口已创建");
            Bar.Show();
            Log("YukiDesk 主窗口已显示");
            BuildTray();
            Log("YukiDesk 托盘已创建");
            // GitHub 更新检查(设置-关于里可开关):首次延迟触发,避免拖慢启动。
            UpdateService.StartAutoCheck();
            // 玻璃可视化取证:用法 YukiDesk.exe --glassshot <输出目录>
            // 自家窗口全部 WDA_EXCLUDEFROMCAPTURE,外部截图根本拍不到顶栏,只能进程内自证。
            // 取证完成后进程自行退出(ShutdownMode 会切成 OnExplicitShutdown)。
            if (Array.IndexOf(e.Args ?? Array.Empty<string>(), "--glassshot") >= 0)
            {
                Log("[glassshot] 进入可视化取证模式");
                Services.GlassDiagnostics.Start(e.Args);
            }
            // 玻璃性能自检:用法 YukiDesk.exe --glassperf [秒数]
            // 正常跑顶栏(不改壁纸、不抓图、不暂停捕获),按区间统计 CPU 占用/捕获帧/BitBlt 耗时,结束后自行退出。
            if (Array.IndexOf(e.Args ?? Array.Empty<string>(), "--glassperf") >= 0)
            {
                Log("[glassperf] 进入性能自检模式");
                Services.GlassDiagnostics.StartPerf(e.Args);
            }
            // 登录温度任务存在但从属不在运行(例如本会话手动重启过应用)时主动拉起一次;
            // 只在任务路径与当前 exe 一致时执行,查询较重,放后台线程。
            // --settings 自检时跳过:设置窗口自己的 _tempStatusTimer 也在周期跑同一个
            // QuerySlaveTask(),两路并发 spawn schtasks.exe 会把进程直接打崩(SIGSEGV,
            // 无 WER 记录),实测 3 秒内必崩。正常启动没有第二路探测,不受影响。
            if (Array.IndexOf(e.Args ?? Array.Empty<string>(), "--settings") < 0)
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var (exists, pathOk) = SettingsWindow.QuerySlaveTask();
                        if (exists && pathOk && !TemperatureSlave.IsFresh)
                        {
                            Log("检测到登录温度任务存在但从属未运行,主动拉起");
                            SettingsWindow.RunSlaveTaskOnce();
                        }
                    }
                    catch { }
                });
            }
        }

        // 仅给 --settings 自检用的控制台诊断:设置窗口"开了没开、活没活"只能这么看,
        // 因为顶栏宿主进程在这个沙箱里写不了 %AppData%\YukiDesk\log.txt,日志方案整体失效。
        private static void DumpSettingsWin(string stage)
        {
            try
            {
                var w = _settingsWin;
                if (w == null) { Console.WriteLine("[selftest][" + stage + "] _settingsWin=null"); return; }
                string h;
                try { h = "0x" + new System.Windows.Interop.WindowInteropHelper(w).Handle.ToString("X"); }
                catch (Exception ex) { h = "取句柄失败:" + ex.GetType().Name; }
                Console.WriteLine("[selftest][" + stage + "] title=" + w.Title
                    + " visible=" + w.IsVisible + " loaded=" + w.IsLoaded
                    + " opacity=" + w.Opacity + " state=" + w.WindowState
                    + " w=" + w.Width + " h=" + w.Height
                    + " hwnd=" + h);
            }
            catch (Exception ex) { Console.WriteLine("[selftest][" + stage + "] 诊断本身抛异常: " + ex); }
        }

        private void BuildTray()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                    _trayIcon = Icon.ExtractAssociatedIcon(exe);
            }
            catch { _trayIcon = null; }
            _tray = new Forms.NotifyIcon
            {
                Text = "YukiDesk",
                Icon = _trayIcon ?? System.Drawing.SystemIcons.Application,
                Visible = true
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开设置", null, (s, e) => Dispatcher.BeginInvoke(new Action(ShowSettings)));
            menu.Items.Add("显示/隐藏顶栏", null, (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Bar.Visibility == Visibility.Visible) Bar.HideWithPanels();
                else Bar.Show();
            })));
            menu.Items.Add("打开数据目录", null, (s, e) => Dispatcher.BeginInvoke(new Action(() => UI.ShellOpen.OpenPath(Settings.StorageRoot))));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出 YukiDesk", null, (s, e) => Dispatcher.BeginInvoke(new Action(Shutdown)));
            _tray.ContextMenuStrip = menu;
            _tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(new Action(ShowSettings)); };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 只阻止新操作;已经在跑的后台复制/移动必须等它收尾,否则进程退出会留下半份文件。
            try { UI.OperationProgressDialog.BeginShutdown(); } catch { }
            try { UI.OperationProgressDialog.WaitForIdle(5000); } catch { }
            try { Services.UpdateService.StopAutoCheck(); } catch { }
            try { Services.ThemeService.Stop(); } catch { }
            try { Services.MicaService.Stop(); } catch { }
            // 玻璃捕获线程引用主窗几何与位图,必须在主窗 Close 之前停(下一位就是 Restore/Close)。
            try { Services.GlassService.Stop(); } catch { }
            try { DesktopSpaceService.Restore(); } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { _trayIcon?.Dispose(); } catch { }
            try { Bar?.ClosePanels(); } catch { }
            try { Bar?.Close(); } catch { }
            try { SpectrumService.Instance.Stop(); } catch { }
            try { _logMutex?.Dispose(); } catch { }
            base.OnExit(e);
        }

        public static void Save() => SettingsService.Save(Settings);
        public static void ApplyAndSave() { Save(); Bar?.ApplySettings(); }

        /// <summary>
        /// 无界面云母自检:同步烘焙一次并把位图写成 PNG,同时在 stdout 打几何与统计信息。
        /// 不创建任何窗口,所以不会占用屏幕——给"只在后台工作"的场景用。
        /// </summary>
        private static void RunMicaDump(string[] args)
        {
            string outPath = null;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--micadump") outPath = args[i + 1];
            if (string.IsNullOrWhiteSpace(outPath)) outPath = Path.Combine(Path.GetTempPath(), "yukidesk-mica.png");

            // 自检要覆盖"没开材质"的路径:显式打开一次,免得测的是回退分支。
            // --acrylic:烘焙亚克力配方而不是云母,验证材质档切换后重算(签名含材质)。
            if (Array.IndexOf(args, "--acrylic") >= 0) Settings.PillMaterial = 2;
            else if (Settings.PillMaterial == 0) Settings.PillMaterial = 1;
            Console.WriteLine("[diag] " + Services.MicaService.Diag());
            Services.ThemeService.Start();

            // --micabg:走 MicaService.Invalidate() 的真实后台烘焙路径(不建任何窗口)。
            // WPF 成像对象(BitmapDecoder/WriteableBitmap)是 DispatcherObject,
            // 在线程池线程上创建会直接 AV(coreclr/GPP64 双重崩溃),必须走这个模式才复现得了。
            if (Array.IndexOf(args, "--micabg") >= 0)
            {
                // A/B 对照:扰动一个进签名的参数,强制烘焙真的跑一遍。
                // 否则 Start() 已经按当前参数算过,签名相同就只会 RaiseChanged,
                // 根本走不到 Bake(),旧代码的崩溃也就复现不出来。
                if (Environment.GetEnvironmentVariable("YUKI_MICA_LEGACY") == "1")
                    Settings.MicaBlurRadius = Math.Round(Settings.MicaBlurRadius) + 1;
                Console.WriteLine("[micabg] 准备 Invalidate");
                Services.MicaService.Invalidate();
                Console.WriteLine("[micabg] Invalidate 已返回");
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                while (Services.MicaService.Backdrop == null && sw2.ElapsedMilliseconds < 20000)
                    System.Threading.Thread.Sleep(50);
                Console.WriteLine("[micabg] 等待结束 Backdrop=" + (Services.MicaService.Backdrop == null ? "null" : "ok"));
                var b2 = Services.MicaService.Backdrop;
                string r = b2 == null
                    ? "null(烘焙线程 20s 内没出结果,见日志)"
                    : b2.PixelWidth + "x" + b2.PixelHeight + " 平均色=" + Services.MicaService.Average;
                Console.WriteLine("[micabg] 烘焙线程位图 = " + r);
                Log("[micabg] 烘焙线程位图 = " + r);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = Services.MicaService.BakeOnce();
            sw.Stop();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("MicaService.BakeOnce = " + ok + "  耗时 " + sw.ElapsedMilliseconds + "ms");
            var bmp = Services.MicaService.Backdrop;
            if (bmp == null)
            {
                sb.AppendLine("位图 = null(回退纯色)。检查壁纸路径是否可读。");
            }
            else
            {
                sb.AppendLine("位图 = " + bmp.PixelWidth + "x" + bmp.PixelHeight
                              + " 出血=" + Services.MicaService.Bleed
                              + " 平均色=" + Services.MicaService.Average
                              + " 深色=" + Services.MicaService.IsDark);
                try
                {
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
                    enc.Save(fs);
                    sb.AppendLine("已写出 " + outPath);
                }
                catch (Exception ex) { sb.AppendLine("写 PNG 失败: " + ex.Message); }
            }
            Console.Write(sb.ToString());
            Log(sb.ToString().Replace("\n", " | "));
        }

        /// <summary>托盘气泡通知(更新提醒等);托盘未创建或已退出时静默。</summary>
        public static void ShowTrayBalloon(string title, string text)
        {
            var app = Application.Current as App;
            try { app?._tray?.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.Info); } catch { }
        }

        /// <summary>配置写盘失败的用户可见反馈(磁盘满/ACL/杀软锁定),否则用户以为已保存、重启后回滚。</summary>
        public static void ShowConfigSaveFailure(Exception ex)
        {
            try
            {
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        UI.ConfirmDialog.Notice("保存设置失败",
                            "设置未能写入磁盘：" + ex.Message
                            + "\n请检查磁盘空间或文件权限,否则部分修改可能在重启后丢失。");
                    }
                    catch { }
                }));
            }
            catch { }
        }
        public static void ShowSettings()
        {
            Bar?.ClosePanels();
            if (_settingsWin != null)
            {
                _settingsWin.WindowState = WindowState.Normal;
                // 呼出已有设置窗口时强制弹到所有窗口之上，避免被别的软件压住找不到。
                _settingsWin.Topmost = true;
                _settingsWin.Activate();
                _settingsWin.Topmost = false;
                return;
            }
            var w = new SettingsWindow(); _settingsWin = w; w.Closed += (s, e) => _settingsWin = null;
            // 首帧前整窗透明,避免"直接闪出来"。
            w.Opacity = 0;
            // Topmost 短暂置顶把窗口带到最前，随后恢复普通层级（点别处可正常被遮挡）。
            w.Topmost = true;
            w.Show();
            w.Activate();
            w.Topmost = false;
            PlayOpenAnimation(w);
        }

        /// <summary>设置窗口打开动画:整窗淡入 + 上浮归位 + 内容轻微缩放。</summary>
        private static void PlayOpenAnimation(SettingsWindow w)
        {
            try
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var duration = TimeSpan.FromMilliseconds(240);

                // 整窗淡入
                var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
                fade.Completed += (s, e) => { w.Opacity = 1; w.BeginAnimation(Window.OpacityProperty, null); };
                w.BeginAnimation(Window.OpacityProperty, fade);

                // 上浮:从下方 18px 滑到目标位置
                double top = w.Top;
                var rise = new DoubleAnimation(top + 18, top, duration) { EasingFunction = ease };
                rise.Completed += (s, e) => { w.Top = top; w.BeginAnimation(Window.TopProperty, null); };
                w.BeginAnimation(Window.TopProperty, rise);

                // 内容轻微缩放(0.96→1)
                var transform = new ScaleTransform(0.96, 0.96);
                w.WindowRoot.RenderTransform = transform;
                w.WindowRoot.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                var scaleX = new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease };
                var scaleY = new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease };
                scaleX.Completed += (s, e) => { w.WindowRoot.RenderTransform = null; };
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            }
            catch { }
        }
    }
}
