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
        private System.Windows.Forms.Screen _revealScreen;
        private string _lastEdgeScreen;
        private readonly bool[] _folderRegistrations = new bool[3];
        private string _pendingFolder;

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
            for (int slot = 0; slot < _folderRegistrations.Length; slot++)
                if (!string.IsNullOrEmpty(settings.FolderShortcuts[slot])) SetFolderShortcut(slot, true);
        }

        internal bool SetFolderShortcut(int slot, bool enabled)
        {
            if (_source == null || (uint)slot >= _folderRegistrations.Length) return false;
            if (_folderRegistrations[slot] == enabled) return true;
            int id = 10 + slot;
            if (enabled && !Register(id, BarHotkey.ForFolder(slot))) return false;
            if (!enabled) NativeMethods.UnregisterHotKey(_source.Handle, id);
            _folderRegistrations[slot] = enabled;
            return true;
        }

        internal bool IsFolderShortcutRegistered(int slot)
            => (uint)slot < _folderRegistrations.Length && _folderRegistrations[slot];

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
            if (!Register(id, hotkey)) return false;
            UnregisterHotkey();
            _hotkeyId = id;
            _hotkey = hotkey;
            return true;
        }

        private bool Register(int id, BarHotkey hotkey) => NativeMethods.RegisterHotKey(_source.Handle, id,
            (uint)hotkey.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key));

        private void UnregisterHotkey()
        {
            if (_hotkeyId == 0) return;
            NativeMethods.UnregisterHotKey(_source.Handle, _hotkeyId);
            _hotkeyId = 0;
        }

        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != NativeMethods.WM_HOTKEY) return IntPtr.Zero;
            int slot = wParam.ToInt32() - 10;
            if ((uint)slot < _folderRegistrations.Length && _folderRegistrations[slot])
            {
                handled = true;
                if (CaptureHotkey != null)
                {
                    CaptureHotkey(BarHotkey.ForFolder(slot));
                    return IntPtr.Zero;
                }
                string path = App.Settings.FolderShortcuts[slot];
                if (!string.IsNullOrEmpty(path) && App.ActiveSettings.ShowFolders)
                {
                    var screen = PointerScreen();
                    if (!IsRaised && _bar.IsVisible && (screen == null || screen.Primary) && !_bar.IsAnyCapsuleCovered(hwnd))
                        _bar.OpenFolderShortcut(path);
                    else
                    {
                        _pendingFolder = path;
                        _hotkeyAfterShowMs = App.ActiveSettings.RevealDurationSeconds * 1000;
                        Raise(screen);
                        if (!_preparing && !_showing && !_dismissing) UsePendingFolder();
                    }
                }
                return IntPtr.Zero;
            }
            if (_hotkeyId == 0 || wParam.ToInt32() != _hotkeyId) return IntPtr.Zero;
            handled = true;
            if (CaptureHotkey != null) CaptureHotkey(_hotkey);
            else
            {
                var screen = PointerScreen();
                if (!IsRaised && _bar.IsVisible && (screen == null || screen.Primary) && !_bar.IsAnyCapsuleCovered(hwnd)) return IntPtr.Zero;
                _hotkeyAfterShowMs = App.ActiveSettings.RevealDurationSeconds * 1000;
                Raise(screen);
                if (IsRaised && !_preparing && !_showing) StartHotkeyCountdown();
            }
            return IntPtr.Zero;
        }

        private void Tick(object sender, EventArgs args)
        {
            long now = Environment.TickCount64;
            bool pointerValid = NativeMethods.TryGetCursorPosition(out int x, out int y);
            var cursor = new System.Drawing.Point(x, y);
            var screen = ResolveRevealScreen(cursor, pointerValid);
            var bounds = screen.Bounds;
            bool atEdge = pointerValid && bounds.Contains(cursor) && y == bounds.Top;
            if (_lastEdgeScreen != screen.DeviceName)
            {
                _lastEdgeScreen = screen.DeviceName;
                _edgeEnteredAt = null;
                _edgeChecked = false;
            }
            if (!_edgeEnabled || !atEdge)
            {
                _edgeEnteredAt = null;
                _edgeChecked = false;
            }
            else if (!_edgeReveal && !_edgeChecked)
            {
                _edgeEnteredAt ??= now;
                if (now - _edgeEnteredAt.Value >= App.ActiveSettings.RevealEdgeDwellMs)
                {
                    _edgeChecked = true;
                    if ((IsRaised || !screen.Primary || !_bar.IsVisible || _bar.IsAnyCapsuleCovered(_source.Handle))
                        && !NativeMethods.ForegroundMatches(App.Settings.EdgeExcludedApps))
                    {
                        _edgeReveal = true;
                        _pointerUntil = now + PointerGraceMs;
                        Raise(screen);
                    }
                }
            }

            if (!IsRaised || _preparing) return;
            bool insideEdgeBand = _edgeReveal && pointerValid && (_revealScreen?.Bounds ?? bounds).Contains(cursor)
                && y < NativeMethods.WindowBottom(_source.Handle);
            if (insideEdgeBand || _bar.HasRevealInteraction)
                _pointerUntil = now + PointerGraceMs;

            if (_hotkeyAfterShowMs > 0 || now < Math.Max(_hotkeyUntil, _pointerUntil))
            {
                if (_dismissing) Animate(show: true);
            }
            else if (!_dismissing)
                Animate(show: false);
        }

        private static System.Windows.Forms.Screen PointerScreen()
            => ResolveRevealScreen(System.Windows.Forms.Cursor.Position, true);

        private static System.Windows.Forms.Screen ResolveRevealScreen(System.Drawing.Point cursor, bool valid)
        {
            var name = App.ActiveSettings.RevealDisplayName;
            if (!string.IsNullOrEmpty(name))
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                    if (screen.DeviceName == name) return screen;
            if (App.ActiveSettings.RevealOnPointerScreen && valid) return System.Windows.Forms.Screen.FromPoint(cursor);
            return System.Windows.Forms.Screen.PrimaryScreen;
        }

        private void Raise(System.Windows.Forms.Screen screen)
        {
            if (!IsRaised)
            {
                _wasHidden = !_bar.IsVisible;
                IsRaised = true;
                _preparing = true;
                _bar.Opacity = 0;
                _revealScreen = screen;
                _bar.PlaceRevealOn(screen);
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

            bool covered = !_bar.IsOnSecondaryScreen && _bar.IsAnyCapsuleCovered(_source.Handle);
            if (!NativeMethods.SetWindowTopmost(_source.Handle, true))
            {
                FinishDismiss();
                return;
            }
            if (_bar.IsOnSecondaryScreen) { FinishPreparation(); return; }
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
            if (!_bar.IsOnSecondaryScreen) GlassService.UseTintIfRevealFrameMissing();
            _bar.RefreshModuleDemandNow();
            if (_edgeReveal) _pointerUntil = Environment.TickCount64 + PointerGraceMs;
            Animate(show: true);
            if (!_showing) { StartHotkeyCountdown(); UsePendingFolder(); }
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
                    UsePendingFolder();
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

        private void UsePendingFolder()
        {
            if (_pendingFolder == null) return;
            string path = _pendingFolder;
            _pendingFolder = null;
            StartHotkeyCountdown();
            _bar.OpenFolderShortcut(path);
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
            _pendingFolder = null;
            _hotkeyAfterShowMs = 0;
            _pointerUntil = _hotkeyUntil = 0;
            NativeMethods.SetWindowTopmost(_source.Handle, false);
            GlassService.SetBarRevealed(false);
            _bar.BeginAnimation(UIElement.OpacityProperty, null);
            _bar.PlaceRevealOn(null);
            _revealScreen = null;
            UpdateTimer();
        }

        internal void ResetForDisplayChange()
        {
            if (IsRaised) FinishDismiss();
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
                for (int slot = 0; slot < _folderRegistrations.Length; slot++) SetFolderShortcut(slot, false);
                _source.RemoveHook(OnMessage);
                _source = null;
            }
            if (IsRaised) GlassService.SetBarRevealed(false);
        }
    }
}
