using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    public partial class MusicModule
    {
        private const int SeekSettlingMs = 750;
        private AnimatedPanelSection _timelineSection;
        private Slider _position;
        private TextBlock _elapsed, _duration;
        private DispatcherTimer _timelineTimer;
        private MediaInfo _timelineInfo, _scrubbing;
        private long _timelineReceivedAt, _seekUntil;
        private int _seekVersion;
        private double _scrubStartedAt, _seekTarget;
        private bool _applyingProgress;

        private FrameworkElement BuildTimeline()
        {
            double scale = Ui.Scale;
            _position = new Slider
            {
                Minimum = 0, Maximum = 1, SmallChange = 5, LargeChange = 15,
                Style = (Style)Application.Current.FindResource("CapsuleSlider")
            };
            _position.SetResourceReference(Control.BackgroundProperty, "ThemePanelControl");
            _position.SetResourceReference(Control.ForegroundProperty, "ThemePanelText");
            AutomationProperties.SetName(_position, "播放进度");
            _position.PreviewMouseLeftButtonDown += (_, _) => BeginScrub();
            _position.PreviewKeyDown += (_, args) => { if (IsSeekKey(args.Key)) BeginScrub(); };
            _position.PreviewKeyUp += (_, args) => { if (IsSeekKey(args.Key)) CommitScrub(); };
            _position.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, args) =>
            {
                if (args.ChangedButton == MouseButton.Left) CommitScrub();
            }), handledEventsToo: true);
            _position.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, args) =>
            {
                if (args.Canceled) CancelScrub();
                else CommitScrub();
            }));
            _position.LostKeyboardFocus += (_, _) => CancelScrub();
            _position.ValueChanged += (_, _) =>
            {
                if (!_applyingProgress && _scrubbing != null) SetElapsed(_position.Value);
            };
            PanelControls.ReleasePointerFocus(_position);

            _elapsed = PanelControls.Text("0:00");
            _duration = PanelControls.Text("");
            _elapsed.Opacity = _duration.Opacity = .78;
            var times = new DockPanel { LastChildFill = false, Margin = new Thickness(12 * scale, -4 * scale, 12 * scale, 0) };
            DockPanel.SetDock(_duration, Dock.Right);
            times.Children.Add(_duration);
            times.Children.Add(_elapsed);
            var body = new StackPanel { Margin = new Thickness(0, 6 * scale, 0, 0) };
            body.Children.Add(_position);
            body.Children.Add(times);
            _timelineSection = new AnimatedPanelSection { Content = body };
            _timelineTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(125)
            };
            _timelineTimer.Tick += (_, _) => RenderProgress();
            return _timelineSection;
        }

        protected override Size GetPanelEnvelope(Size content) => new(content.Width,
            content.Height + (_timelineSection.Expanded ? 0 : 62 * Ui.Scale));

        protected override void OnPanelShown()
        {
            _controller?.SetProgressDemand(true);
            RenderProgress();
            UpdateTimelineTimer();
        }

        private void StopTimeline()
        {
            _controller?.SetProgressDemand(false);
            _timelineTimer.Stop();
            _scrubbing = null;
            _seekUntil = 0;
            _seekVersion++;
        }

        private void UpdateTimeline(MediaInfo info)
        {
            if (!info.SeekSupported || !MediaService.SameTrack(_timelineInfo, info))
            {
                _scrubbing = null;
                _seekUntil = 0;
                _seekVersion++;
            }
            _timelineInfo = info;
            _timelineReceivedAt = Environment.TickCount64;
            bool hasDuration = info.HasSession && info.TrackKey.Length != 0 && info.Duration > TimeSpan.Zero;
            if (_timelineSection.Expanded != hasDuration) _timelineSection.SetExpanded(hasDuration, animate: Open);
            if (hasDuration)
            {
                _position.IsEnabled = info.SeekSupported;
                string duration = FormatTime(info.Duration.TotalSeconds);
                if (_duration.Text != duration) _duration.Text = duration;
            }
            RenderProgress();
            UpdateTimelineTimer();
        }

        private void UpdateTimelineTimer() => _timelineTimer.IsEnabled = Open && _timelineSection.Expanded
            && (_timelineInfo.Playing || _seekUntil != 0);

        private void RenderProgress()
        {
            if (!_timelineSection.Expanded || _scrubbing != null) return;
            var info = _timelineInfo;
            long now = Environment.TickCount64;
            double duration = info.Duration.TotalSeconds;
            double position = info.Position.TotalSeconds;
            if (info.Playing) position += Math.Max(0, now - _timelineReceivedAt) / 1000.0 * info.PlaybackRate;
            if (_seekUntil != 0)
            {
                if (now >= _seekUntil || Math.Abs(info.Position.TotalSeconds - _seekTarget) < .8)
                {
                    _seekUntil = 0;
                    UpdateTimelineTimer();
                }
                else position = _seekTarget;
            }
            _applyingProgress = true;
            _position.Maximum = duration;
            _position.Value = Math.Clamp(position, 0, duration);
            _applyingProgress = false;
            SetElapsed(_position.Value);
        }

        private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down
            or Key.PageUp or Key.PageDown or Key.Home or Key.End;

        private void BeginScrub()
        {
            if (_scrubbing != null || _timelineInfo?.SeekSupported != true) return;
            _scrubbing = _timelineInfo;
            _scrubStartedAt = _position.Value;
        }

        private void CancelScrub()
        {
            if (_scrubbing == null) return;
            _scrubbing = null;
            RenderProgress();
        }

        private void CommitScrub()
        {
            var expected = _scrubbing;
            if (expected == null) return;
            _scrubbing = null;
            if (!Open || _controller == null || !MediaService.SameTrack(expected, _timelineInfo)
                || !_timelineInfo.SeekSupported || Math.Abs(_position.Value - _scrubStartedAt) < .05)
            {
                RenderProgress();
                return;
            }
            _seekTarget = Math.Clamp(_position.Value, _timelineInfo.SeekFrom.TotalSeconds, _timelineInfo.SeekTo.TotalSeconds);
            _seekUntil = Environment.TickCount64 + SeekSettlingMs;
            RenderProgress();
            UpdateTimelineTimer();
            var target = _seekTarget >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(_seekTarget);
            _ = SeekAsync(expected, target, ++_seekVersion);
        }

        private async Task SeekAsync(MediaInfo expected, TimeSpan target, int version)
        {
            bool accepted = await _controller.SeekAsync(expected, target);
            if (version != _seekVersion) return;
            _seekUntil = accepted ? Environment.TickCount64 + SeekSettlingMs : 0;
            RenderProgress();
            UpdateTimelineTimer();
        }

        private void SetElapsed(double seconds)
        {
            string value = FormatTime(seconds);
            if (_elapsed.Text != value) _elapsed.Text = value;
        }

        private static string FormatTime(double seconds)
        {
            var time = (long)Math.Max(0, seconds);
            return time >= 3600 ? $"{time / 3600}:{time / 60 % 60:00}:{time % 60:00}" : $"{time / 60}:{time % 60:00}";
        }
    }
}
