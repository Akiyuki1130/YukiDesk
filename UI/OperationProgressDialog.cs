using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    /// <summary>文件操作的非模态主题进度窗；窗口只负责显示，实际工作在后台线程执行。</summary>
    public sealed class OperationProgressDialog : IDisposable
    {
        private static int _busy;
        private static int _shuttingDown;
        public static bool Busy => Volatile.Read(ref _busy) != 0;
        public static bool IsShuttingDown => Volatile.Read(ref _shuttingDown) != 0;
        private readonly Window _window;
        private readonly TextBlock _status;
        private readonly TextBlock _detail;
        private readonly Grid _progressHost;
        private readonly Border _fill;
        private readonly Border _indeterminateFill;
        private readonly bool _indeterminate;
        private readonly int _total;
        private readonly double _scale;
        private int _completed;
        private int _succeeded;
        private int _failed;
        private int _disposeStarted;
        private bool _closed;

        public sealed class RunResult
        {
            public bool Started { get; internal set; }
            public bool Completed { get; internal set; }
            public int Total { get; internal set; }
            public int Succeeded { get; internal set; }
            public int Failed { get; internal set; }
            public Exception Error { get; internal set; }

            public bool SucceededFully => Started && Completed && Failed == 0
                && (Total <= 0 || Succeeded >= Total);
        }

        private OperationProgressDialog(string title, bool indeterminate, int total)
        {
            _scale = Ui.Scale;
            _indeterminate = indeterminate;
            _total = Math.Max(1, total);
            _status = new TextBlock
            {
                Text = title,
                FontSize = 15 * _scale,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            // 进度窗是长寿命弹窗(大文件操作期间用户可切主题),颜色必须走动态资源而非快照。
            _status.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            _detail = new TextBlock
            {
                FontSize = 12.5 * _scale,
                Opacity = 0.72,
                Margin = new Thickness(0, 8 * _scale, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _detail.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");

            _progressHost = new Grid
            {
                Height = 7 * _scale,
                ClipToBounds = true,
                Margin = new Thickness(0, 16 * _scale, 0, 0)
            };
            var track = new Border
            {
                CornerRadius = new CornerRadius(4 * _scale)
            };
            track.SetResourceReference(Border.BackgroundProperty, "ThemePanelControl");
            _progressHost.Children.Add(track);
            _fill = new Border
            {
                Width = 0,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(4 * _scale)
            };
            _fill.SetResourceReference(Border.BackgroundProperty, "ThemePanelText");
            _progressHost.Children.Add(_fill);
            _indeterminateFill = new Border
            {
                Width = 86 * _scale,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(4 * _scale),
                Visibility = indeterminate ? Visibility.Visible : Visibility.Collapsed,
                RenderTransform = new TranslateTransform()
            };
            _indeterminateFill.SetResourceReference(Border.BackgroundProperty, "ThemePanelText");
            _progressHost.Children.Add(_indeterminateFill);
            if (!indeterminate)
                _indeterminateFill.Visibility = Visibility.Collapsed;
            _progressHost.SizeChanged += (s, e) => UpdateDeterminateWidth();

            var body = new StackPanel { Width = 300 * _scale };
            body.Children.Add(_status);
            body.Children.Add(_detail);
            body.Children.Add(_progressHost);
            var card = new Border
            {
                CornerRadius = new CornerRadius(14 * _scale),
                Padding = new Thickness(20 * _scale, 17 * _scale, 20 * _scale, 19 * _scale),
                BorderThickness = new Thickness(1 * _scale),
                Child = body
            };
            card.SetResourceReference(Border.BackgroundProperty, "ThemePanelBackground");
            card.SetResourceReference(Border.BorderBrushProperty, "ThemePanelBorder");

            _window = new Window
            {
                FontFamily = Ui.TextFont,
                FontWeight = FontWeights.SemiBold,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = card
            };
            _window.SourceInitialized += (s, e) =>
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
                    Interop.NativeMethods.ApplyNoActivateToolStyle(hwnd);
                }
                catch { }
            };
            _window.ContentRendered += (s, e) => StartIndeterminateAnimation();
            _window.Closed += (s, e) =>
            {
                _closed = true;
                ReleaseBusy();
            };
        }

        public static void BeginShutdown() => Interlocked.Exchange(ref _shuttingDown, 1);

        /// <summary>
        /// 退出时等待进行中的文件操作收尾。必须泵消息:完成回调(关进度窗/释放 Busy)
        /// 都要经 Dispatcher 处理,纯 Thread.Sleep 会把自己等死到超时。
        /// </summary>
        public static void WaitForIdle(int timeoutMs)
        {
            if (!Busy) return;
            App.Log("退出: 等待进行中的文件操作收尾…");
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Busy && Environment.TickCount64 < deadline)
            {
                try
                {
                    var frame = new DispatcherFrame(true);
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                        new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                }
                catch { }
                Thread.Sleep(50);
            }
            App.Log(Busy ? "退出: 等待文件操作超时(" + timeoutMs + "ms),继续退出"
                         : "退出: 文件操作已收尾");
        }

        public static bool TryOpen(string title, bool indeterminate, int total, out OperationProgressDialog dialog)
        {
            dialog = null;
            if (IsShuttingDown || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                return false;
            try
            {
                dialog = new OperationProgressDialog(title, indeterminate, total);
                dialog._window.Show();
                return true;
            }
            catch
            {
                Interlocked.Exchange(ref _busy, 0);
                dialog = null;
                return false;
            }
        }

        public void Report(int completed, string detail) => Report(completed, detail, true);

        public void Report(int completed, string detail, bool succeeded)
        {
            if (_closed || _window.Dispatcher.HasShutdownStarted) return;
            int target = Math.Max(0, Math.Min(_total, completed));
            int previous = Interlocked.Exchange(ref _completed, target);
            int delta = Math.Max(0, target - previous);
            if (delta > 0)
            {
                if (succeeded) Interlocked.Add(ref _succeeded, delta);
                else Interlocked.Add(ref _failed, delta);
            }

            void Update()
            {
                if (_closed) return;
                if (!_indeterminate)
                {
                    _completed = target;
                    UpdateDeterminateWidth();
                }
                _detail.Text = detail ?? "";
            }
            if (_window.Dispatcher.CheckAccess()) Update();
            else
            {
                try { _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Update)); }
                catch (InvalidOperationException) { }
            }
        }

        internal RunResult SnapshotResult(RunResult result)
        {
            result.Total = _total;
            result.Succeeded = Math.Max(0, Volatile.Read(ref _succeeded));
            result.Failed = Math.Max(0, Volatile.Read(ref _failed));
            int missing = Math.Max(0, _total - result.Succeeded - result.Failed);
            if (result.Error != null) result.Failed += missing;
            return result;
        }

        private void ReleaseBusy() => Interlocked.Exchange(ref _busy, 0);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            if (_closed)
            {
                ReleaseBusy();
                return;
            }
            try
            {
                if (_window.Dispatcher.HasShutdownStarted || _window.Dispatcher.HasShutdownFinished)
                {
                    _closed = true;
                    ReleaseBusy();
                    return;
                }
                if (_window.Dispatcher.CheckAccess())
                {
                    if (_window.IsVisible) _window.Close();
                    else
                    {
                        _closed = true;
                        ReleaseBusy();
                    }
                }
                else
                {
                    _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        try
                        {
                            if (!_closed && _window.IsVisible) _window.Close();
                            else
                            {
                                _closed = true;
                                ReleaseBusy();
                            }
                        }
                        catch { _closed = true; ReleaseBusy(); }
                    }));
                }
            }
            catch
            {
                _closed = true;
                ReleaseBusy();
            }
        }

        public static async Task<RunResult> RunDetailedAsync(string title, bool indeterminate, int total, Func<OperationProgressDialog, Task> operation)
        {
            var result = new RunResult { Total = Math.Max(1, total) };
            if (!TryOpen(title, indeterminate, total, out var dialog)) return result;
            result.Started = true;
            try
            {
                await operation(dialog).ConfigureAwait(true);
                result.Completed = true;
            }
            catch (Exception ex)
            {
                result.Error = ex;
                App.Log("后台文件操作失败: " + ex.Message);
            }
            finally
            {
                dialog.SnapshotResult(result);
                dialog.Dispose();
            }
            return result;
        }

        public static async Task<bool> RunAsync(string title, bool indeterminate, int total, Func<OperationProgressDialog, Task> operation)
            => (await RunDetailedAsync(title, indeterminate, total, operation).ConfigureAwait(true)).Started;

        /// <summary>Drop 事件需要保持未返回，避免 Explorer 在实际移动完成前处理 Move 源；消息泵仍让窗口和动画刷新。</summary>
        public static RunResult RunWithDispatcherPumpDetailed(string title, bool indeterminate, int total, Func<OperationProgressDialog, Task> operation)
        {
            var result = new RunResult { Total = Math.Max(1, total) };
            if (!TryOpen(title, indeterminate, total, out var dialog)) return result;
            result.Started = true;
            var task = Task.Run(async () => await operation(dialog).ConfigureAwait(false));
            var frame = new DispatcherFrame();
            task.ContinueWith(t => frame.Continue = false, CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
            try
            {
                Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();
                result.Completed = true;
            }
            catch (Exception ex)
            {
                result.Error = ex;
                App.Log("后台文件操作失败: " + ex.Message);
            }
            finally
            {
                dialog.SnapshotResult(result);
                dialog.Dispose();
            }
            return result;
        }

        public static bool RunWithDispatcherPump(string title, bool indeterminate, int total, Func<OperationProgressDialog, Task> operation)
            => RunWithDispatcherPumpDetailed(title, indeterminate, total, operation).Started;

        private void UpdateDeterminateWidth()
        {
            if (!_indeterminate && !_closed)
                _fill.Width = (_completed / (double)_total) * _progressHost.ActualWidth;
        }

        private void StartIndeterminateAnimation()
        {
            if (!_indeterminate || _closed) return;
            var transform = (TranslateTransform)_indeterminateFill.RenderTransform;
            var animation = new DoubleAnimation
            {
                From = -_indeterminateFill.Width,
                To = _progressHost.ActualWidth,
                Duration = TimeSpan.FromMilliseconds(900),
                RepeatBehavior = RepeatBehavior.Forever
            };
            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        }
    }
}
