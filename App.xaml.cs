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
