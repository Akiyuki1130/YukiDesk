using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopBar.Interop;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    internal sealed class BarRevealController : IDisposable
    {
        private const int PointerGraceMs = 320;
        private const int GlassPreparationMs = 360;
        private readonly MainWindow _bar;
        private readonly DispatcherTimer _timer;
        private HwndSource _source;
        private int _hotkeyId;
        private int _animationVersion;
        private int _hotkeyAfterShowMs;
        private BarHotkey _hotkey;
        private bool _edgeEnabled, _edgeChecked, _edgeReveal, _wasHidden, _preparing, _showing, _dismissing;
        private long? _edgeEnteredAt;
        private long _pointerUntil, _hotkeyUntil;
        private long _prepareUntil;
        private TimeSpan? _firstPreparationFrame;

        internal bool IsRaised { get; private set; }
        internal bool IsHotkeyRegistered => _hotkeyId != 0;
        internal Action<BarHotkey> CaptureHotkey { get; set; }

        internal BarRevealController(MainWindow bar)
        {
            _bar = bar;
            _timer = new DispatcherTimer(DispatcherPriority.Background, bar.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(55)
            };
            _timer.Tick += Tick;
        }

        internal void Attach(IntPtr hwnd)
        {
            _source = HwndSource.FromHwnd(hwnd);
            _source.AddHook(OnMessage);
            var settings = App.Settings;
            SetEdgeEnabled(settings.RevealOnTopEdge);
            if (settings.RevealWithHotkey && !TrySetHotkey(true, BarHotkey.FromSettings(settings)))
                App.Log("顶栏唤出快捷键注册失败");
        }

        internal void SetEdgeEnabled(bool enabled)
        {
            _edgeEnabled = enabled;
            if (!enabled)
            {
                _edgeEnteredAt = null;
                _edgeChecked = false;
                _edgeReveal = false;
            }
            UpdateTimer();
        }

        internal bool TrySetHotkey(bool enabled, BarHotkey hotkey)
        {
            if (!enabled)
            {
                UnregisterHotkey();
                _hotkeyAfterShowMs = 0;
                _hotkeyUntil = 0;
                return true;
            }
            if (_source == null || !hotkey.IsValid) return false;
            if (_hotkeyId != 0 && hotkey == _hotkey) return true;

            // 先注册新组合。遇到被占用的组合时，原快捷键仍能使用。
            int id = _hotkeyId == 1 ? 2 : 1;
            uint modifiers = (uint)hotkey.Modifiers | NativeMethods.MOD_NOREPEAT;
            if (!NativeMethods.RegisterHotKey(_source.Handle, id, modifiers, (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key)))
                return false;
            UnregisterHotkey();
            _hotkeyId = id;
            _hotkey = hotkey;
            return true;
        }

        private void UnregisterHotkey()
        {
            if (_hotkeyId == 0) return;
            NativeMethods.UnregisterHotKey(_source.Handle, _hotkeyId);
            _hotkeyId = 0;
        }

        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != NativeMethods.WM_HOTKEY || _hotkeyId == 0 || wParam.ToInt32() != _hotkeyId) return IntPtr.Zero;
            handled = true;
            if (CaptureHotkey != null) CaptureHotkey(_hotkey);
            else
            {
                if (!IsRaised && _bar.IsVisible && !_bar.IsAnyCapsuleCovered(hwnd)) return IntPtr.Zero;
                _hotkeyAfterShowMs = App.Settings.RevealDurationSeconds * 1000;
                Raise();
                if (IsRaised && !_preparing && !_showing) StartHotkeyCountdown();
            }
            return IntPtr.Zero;
        }

        private void Tick(object sender, EventArgs args)
        {
            long now = Environment.TickCount64;
            bool onPrimary = NativeMethods.TryGetCursorPosition(out int x, out int y)
                && x >= 0 && x < NativeMethods.ScreenWidth() && y >= 0;
            if (!_edgeEnabled || !onPrimary || y != 0)
            {
                _edgeEnteredAt = null;
                _edgeChecked = false;
            }
            else if (!_edgeReveal && !_edgeChecked)
            {
                _edgeEnteredAt ??= now;
                if (now - _edgeEnteredAt.Value >= App.Settings.RevealEdgeDwellMs)
                {
                    _edgeChecked = true;
                    if (IsRaised || !_bar.IsVisible || _bar.IsAnyCapsuleCovered(_source.Handle))
                    {
                        _edgeReveal = true;
                        _pointerUntil = now + PointerGraceMs;
                        Raise();
                    }
                }
            }

            if (!IsRaised || _preparing) return;
            bool insideEdgeBand = _edgeReveal && onPrimary && y < NativeMethods.WindowBottom(_source.Handle);
            if (insideEdgeBand || _bar.HasRevealInteraction)
                _pointerUntil = now + PointerGraceMs;

            if (_hotkeyAfterShowMs > 0 || now < Math.Max(_hotkeyUntil, _pointerUntil))
            {
                if (_dismissing) Animate(show: true);
            }
            else if (!_dismissing)
                Animate(show: false);
        }

        private void Raise()
        {
            if (!IsRaised)
            {
                _wasHidden = !_bar.IsVisible;
                IsRaised = true;
                _preparing = true;
                _bar.Opacity = 0;
                if (_wasHidden) _bar.ShowForReveal();
                _firstPreparationFrame = null;
                CompositionTarget.Rendering += OnPreparationRendering;
                UpdateTimer();
            }
            else if (_dismissing) Animate(show: true);
        }

        private void OnPreparationRendering(object sender, EventArgs args)
        {
            var frame = ((RenderingEventArgs)args).RenderingTime;
            _firstPreparationFrame ??= frame;
            if (frame == _firstPreparationFrame) return;

            CompositionTarget.Rendering -= OnPreparationRendering;
            _ = PromoteAfterCompositionAsync(_animationVersion);
        }

        private async Task PromoteAfterCompositionAsync(int version)
        {
            // 透明帧先留在原来的窗口层级合成，防止 DWM 在置顶时短暂显示上一次的不透明帧。
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!_preparing || version != _animationVersion) return;
            await Task.Run(NativeMethods.WaitComposition);
            if (!_preparing || version != _animationVersion) return;

            bool covered = _bar.IsAnyCapsuleCovered(_source.Handle);
            if (!NativeMethods.SetWindowTopmost(_source.Handle, true))
            {
                FinishDismiss();
                return;
            }
            // 置顶后才开始等待新帧。逐窗口采样只会读取顶栏下方的窗口。
            GlassService.PrepareRevealBackdrop(covered);
            _prepareUntil = Environment.TickCount64 + GlassPreparationMs;
            if (GlassService.IsRevealFrameReady) FinishPreparation();
            else CompositionTarget.Rendering += OnBackdropPreparationRendering;
        }

        private void OnBackdropPreparationRendering(object sender, EventArgs args)
        {
            if (GlassService.IsRevealFrameReady || Environment.TickCount64 >= _prepareUntil)
                FinishPreparation();
        }

        private void FinishPreparation()
        {
            StopPreparationRendering();
            _preparing = false;
            GlassService.UseTintIfRevealFrameMissing();
            if (_edgeReveal) _pointerUntil = Environment.TickCount64 + PointerGraceMs;
            Animate(show: true);
            if (!_showing) StartHotkeyCountdown();
        }

        private void StopPreparationRendering()
        {
            CompositionTarget.Rendering -= OnPreparationRendering;
            CompositionTarget.Rendering -= OnBackdropPreparationRendering;
        }

        private void Animate(bool show)
        {
            int version = ++_animationVersion;
            bool animate = SystemParameters.ClientAreaAnimation;
            _showing = show && animate;
            _dismissing = !show;
            double from = _bar.Opacity;
            double to = show ? 1 : 0;
            _bar.Opacity = from;
            _bar.BeginAnimation(UIElement.OpacityProperty, null);
            if (!animate)
            {
                _bar.Opacity = to;
                if (!show) FinishDismiss();
                return;
            }
            double fullDurationMs = Math.Clamp(App.Settings.AnimationDurationMs * 2.2, 320, 700) * (show ? 1 : 0.82);
            double durationMs = Math.Max(120, fullDurationMs * Math.Sqrt(Math.Abs(from - to)));
            var fade = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
            {
                // 时钟在下一帧才接管属性，等待期间也必须维持起始透明度。
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            fade.Completed += (_, _) =>
            {
                if (!IsRaised || version != _animationVersion) return;
                _bar.Opacity = to;
                _bar.BeginAnimation(UIElement.OpacityProperty, null);
                if (show)
                {
                    _showing = false;
                    StartHotkeyCountdown();
                }
                else FinishDismiss();
            };
            _bar.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void StartHotkeyCountdown()
        {
            if (_hotkeyAfterShowMs == 0) return;
            _hotkeyUntil = Environment.TickCount64 + _hotkeyAfterShowMs;
            _hotkeyAfterShowMs = 0;
        }

        private void FinishDismiss()
        {
            bool hide = _wasHidden;
            _bar.ClosePanels();
            Reset();
            if (hide) _bar.HideWithPanels();
            else
            {
                _bar.PinToDesktopLayer();
                _bar.Opacity = 1;
            }
        }

        internal void Reset()
        {
            if (!IsRaised) return;
            _animationVersion++;
            StopPreparationRendering();
            IsRaised = _preparing = _showing = _dismissing = _edgeReveal = false;
            _edgeEnteredAt = null;
            _hotkeyAfterShowMs = 0;
            _pointerUntil = _hotkeyUntil = 0;
            NativeMethods.SetWindowTopmost(_source.Handle, false);
            GlassService.SetBarRevealed(false);
            _bar.BeginAnimation(UIElement.OpacityProperty, null);
            UpdateTimer();
        }

        private void UpdateTimer() => _timer.IsEnabled = _edgeEnabled || IsRaised;

        public void Dispose()
        {
            _timer.Stop();
            StopPreparationRendering();
            _preparing = false;
            CaptureHotkey = null;
            if (_source != null)
            {
                UnregisterHotkey();
                _source.RemoveHook(OnMessage);
                _source = null;
            }
            if (IsRaised) GlassService.SetBarRevealed(false);
        }
    }
}
