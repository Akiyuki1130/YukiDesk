using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using DesktopBar.Interop;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DesktopBar.UI
{
    // 注册原生 OLE 目标，让 Windows Shell 收到完整的数据对象及其异步传输接口。
    internal sealed class ShellDropTarget : ShellFiles.IDropTarget
    {
        private sealed record Zone(string Path, Action<Action> RunDrop, Action<bool> SetActive);

        private static readonly ConditionalWeakTable<HwndSource, ShellDropTarget> Windows = new();
        private static readonly DependencyProperty ZoneProperty = DependencyProperty.RegisterAttached(
            "Zone", typeof(Zone), typeof(ShellDropTarget));

        private readonly HwndSource _source;
        private readonly IntPtr _window;
        private IDataObject _data;
        private Zone _zone;
        private ShellFiles.DropSession _session;
        private bool _registered;
        private bool _dropping;

        private ShellDropTarget(HwndSource source)
        {
            _source = source;
            _window = source.Handle;
            ShellFiles.RegisterDropTarget(_window, this);
            _registered = true;
            _source.AddHook(WindowMessage);
            _source.Disposed += (_, _) => Unregister();
        }

        internal static void Wire(FrameworkElement element, string path, Action<Action> runDrop, Action<bool> setActive)
        {
            if (string.IsNullOrEmpty(path)) return;
            element.SetValue(ZoneProperty, new Zone(path, runDrop, setActive));
            element.Loaded += (_, _) => EnsureRegistered(element);
            PresentationSource.AddSourceChangedHandler(element, (_, _) => EnsureRegistered(element));
            EnsureRegistered(element);
        }

        private static void EnsureRegistered(FrameworkElement element)
        {
            if (PresentationSource.FromVisual(element) is not HwndSource source || source.IsDisposed) return;
            try { Windows.GetValue(source, value => new ShellDropTarget(value)); }
            catch (Exception ex) { App.Log("无法注册 Windows 文件拖放: " + ex.Message); }
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int Destroy = 0x02;
            if (message == Destroy) Unregister();
            return IntPtr.Zero;
        }

        private void Unregister()
        {
            if (!_registered) return;
            _registered = false;
            try { DragLeave(); }
            finally { ShellFiles.UnregisterDropTarget(_window); }
        }

        private Zone HitTest(ShellFiles.ScreenPoint point)
        {
            if (_source.RootVisual is not UIElement root || !root.IsVisible) return null;
            var local = root.PointFromScreen(new Point(point.X, point.Y));
            var element = root.InputHitTest(local) as DependencyObject;
            while (element != null)
            {
                if (element.GetValue(ZoneProperty) is Zone zone) return zone;
                element = element switch
                {
                    Visual or Visual3D => VisualTreeHelper.GetParent(element),
                    FrameworkContentElement content => content.Parent,
                    _ => null
                };
            }
            return null;
        }

        private uint UpdateTarget(uint keys, ShellFiles.ScreenPoint point, uint effects)
        {
            var zone = _data != null ? HitTest(point) : null;
            if (ReferenceEquals(zone, _zone))
                return _session?.Over(keys, point, effects) ?? 0;

            EndSession();
            _zone = zone;
            if (zone == null) return 0;
            _session = ShellFiles.DropSession.Create(_window, zone.Path, _data);
            zone.SetActive(true);
            return _session.Enter(keys, point, effects);
        }

        private void EndSession()
        {
            var session = _session;
            var zone = _zone;
            _session = null;
            _zone = null;
            if (session == null) return;
            try { session.Dispose(); }
            finally { zone.SetActive(false); }
        }

        public int DragEnter(IDataObject data, uint keys, ShellFiles.ScreenPoint point, ref uint effects)
        {
            if (_dropping)
            {
                effects = 0;
                return 0;
            }
            DragLeave();
            _data = data;
            return DragOver(keys, point, ref effects);
        }

        public int DragOver(uint keys, ShellFiles.ScreenPoint point, ref uint effects)
        {
            if (_dropping)
            {
                effects = 0;
                return 0;
            }
            try { effects = UpdateTarget(keys, point, effects); }
            catch (Exception ex)
            {
                effects = 0;
                var failed = _zone;
                try { EndSession(); }
                catch (Exception cleanup) { App.Log("Windows 无法结束拖动: " + cleanup.Message); }
                _zone = failed;
                App.Log("Windows 无法接收拖动内容: " + ex.Message);
            }
            return 0;
        }

        public int DragLeave()
        {
            _data = null;
            try { EndSession(); }
            catch (Exception ex) { App.Log("Windows 无法结束拖动: " + ex.Message); }
            return 0;
        }

        public int Drop(IDataObject data, uint keys, ShellFiles.ScreenPoint point, ref uint effects)
        {
            uint allowed = effects;
            effects = 0;
            if (_dropping) return 0;
            try
            {
                _data = data;
                if (!ReferenceEquals(HitTest(point), _zone))
                    UpdateTarget(keys, point, allowed);
                var zone = _zone;
                using var session = _session;
                _session = null;
                _zone = null;
                if (session == null) return 0;

                uint performed = 0;
                _dropping = true;
                try { zone.RunDrop(() => performed = CompleteDrop(session, _window, zone.Path, keys, point, allowed)); }
                finally
                {
                    _dropping = false;
                    zone.SetActive(false);
                }
                effects = performed;
            }
            catch (Exception ex) { App.Log("Windows 无法完成拖放: " + ex.Message); }
            finally { DragLeave(); }
            return 0;
        }

        private static uint CompleteDrop(ShellFiles.DropSession session, IntPtr owner, string path, uint keys,
            ShellFiles.ScreenPoint point, uint allowed)
        {
            using var payload = session.MarshalData();
            if (payload == null) return session.Drop(keys, point, allowed);
            session.Leave();
            var completion = payload.DropAsync(owner, path, session.EnterKeys, keys, point, allowed);
            if (!completion.IsCompleted)
            {
                // OLE still needs the real result (especially for move). Pump WPF until the
                // worker returns it so the bar and STA callbacks remain responsive in the meantime.
                var frame = new DispatcherFrame(exitWhenRequested: false);
                _ = completion.ContinueWith(_ => frame.Continue = false,
                    System.Threading.CancellationToken.None, System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                    System.Threading.Tasks.TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            return completion.GetAwaiter().GetResult();
        }
    }
}
