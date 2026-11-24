using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DesktopBar.Services
{
    /// <summary>一次媒体状态更新的不可变快照；发布到 UI 后不会再被服务修改。</summary>
    public sealed class MediaInfo
    {
        public long Generation { get; }
        public bool HasSession { get; }
        public string SessionKey { get; }
        public string TrackKey { get; }
        public string ThumbnailKey { get; }
        public string Title { get; }
        public string Artist { get; }
        public bool Playing { get; }
        public TimeSpan Position { get; }
        public TimeSpan Duration { get; }
        public bool SeekSupported { get; }
        public BitmapSource Cover { get; }
        internal TimeSpan Start { get; }
        internal TimeSpan SeekFrom { get; }
        internal TimeSpan SeekTo { get; }
        internal double PlaybackRate { get; }

        public MediaInfo(long generation, bool hasSession, string sessionKey, string trackKey,
            string thumbnailKey, string title, string artist, BitmapSource cover, bool playing,
            TimeSpan position, TimeSpan duration, bool seekSupported, TimeSpan start = default,
            TimeSpan seekFrom = default, TimeSpan? seekTo = null, double playbackRate = 1)
        {
            Generation = generation;
            HasSession = hasSession;
            SessionKey = sessionKey ?? "";
            TrackKey = trackKey ?? "";
            ThumbnailKey = thumbnailKey ?? "";
            Title = title ?? "";
            Artist = artist ?? "";
            Cover = cover;
            Playing = playing;
            Position = position;
            Duration = duration;
            SeekSupported = seekSupported;
            Start = start;
            SeekFrom = seekFrom;
            SeekTo = seekTo ?? duration;
            PlaybackRate = double.IsFinite(playbackRate) ? playbackRate : 1;
        }
    }

    /// <summary>通过系统媒体控制(SMTC)读取后台播放状态并发布不可变快照。</summary>
    public sealed class MediaService : IDisposable
    {
        private const ulong MaxCoverBytes = 20_000_000;

        private readonly Dispatcher _dispatcher;
        private GlobalSystemMediaTransportControlsSessionManager _manager;
        private GlobalSystemMediaTransportControlsSession _session;
        private DispatcherTimer _timer, _retryTimer;
        private long _sessionGeneration;
        private long _refreshGeneration;
        private long _publishedGeneration;
        private bool _started;
        private bool _disposed;
        private bool _progressDemand;
        private bool _seeking;
        private SeekRequest _queuedSeek;
        private string _sessionKey = "";
        private MediaInfo _current = new MediaInfo(0, false, "", "", "", "", "", null,
            false, TimeSpan.Zero, TimeSpan.Zero, false);

        private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> _currentSessionChanged;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> _sessionsChanged;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _mediaPropertiesChanged;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _playbackInfoChanged;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> _timelinePropertiesChanged;

        public event Action<MediaInfo> Updated;
        public event Action SessionLost;
        public MediaInfo Current => _current;

        public MediaService(Dispatcher dispatcher) { _dispatcher = dispatcher; }

        private sealed record SeekRequest(Func<Task<bool>> Apply, TaskCompletionSource<bool> Completion);

        internal Task<bool> SeekAsync(MediaInfo expected, TimeSpan position)
            => EnqueueSeek(() => ApplySeekAsync(expected, position));

        private Task<bool> EnqueueSeek(Func<Task<bool>> apply)
        {
            if (_disposed) return Task.FromResult(false);
            _queuedSeek?.Completion.TrySetResult(false);
            var request = new SeekRequest(apply, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            _queuedSeek = request;
            if (!_seeking) _ = ProcessSeeksAsync();
            return request.Completion.Task;
        }

        private async Task ProcessSeeksAsync()
        {
            _seeking = true;
            try
            {
                while (_queuedSeek is { } request)
                {
                    _queuedSeek = null;
                    try { request.Completion.TrySetResult(await request.Apply()); }
                    catch (Exception ex)
                    {
                        App.Log("媒体定位失败: " + ex.Message);
                        request.Completion.TrySetResult(false);
                    }
                }
            }
            finally { _seeking = false; }
        }

        private async Task<bool> ApplySeekAsync(MediaInfo expected, TimeSpan position)
        {
            var session = _session;
            if (_disposed || session == null || !_current.SeekSupported || !SameTrack(expected, _current)) return false;
            long ticks = _current.Start.Ticks + Math.Clamp(position.Ticks, _current.SeekFrom.Ticks, _current.SeekTo.Ticks);
            bool changed = await session.TryChangePlaybackPositionAsync(ticks);
            if (changed) Push(session);
            return changed;
        }

        internal static bool SameTrack(MediaInfo left, MediaInfo right)
            => left is { HasSession: true } && right is { HasSession: true }
                && left.TrackKey.Length > 0 && left.TrackKey == right.TrackKey && left.SessionKey == right.SessionKey;

        public async Task StartAsync()
        {
            if (_disposed || _started) return;
            _started = true;
            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_disposed) return;
                _manager = manager;
            }
            catch (Exception ex)
            {
                App.Log("SMTC 初始化失败(60 秒后重试): " + ex);
                _started = false;
                Post(() => Hook(null));
                // 初始化瞬间 SMTC 不可用时不能永久放弃,否则音乐胶囊直到重启都不再出现。
                Post(ScheduleRetry);
                return;
            }

            _currentSessionChanged = (manager, args) => Post(SelectSession);
            _sessionsChanged = (manager, args) => Post(SelectSession);
            _manager.CurrentSessionChanged += _currentSessionChanged;
            _manager.SessionsChanged += _sessionsChanged;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += OnTimerTick;
            SelectSession();
        }

        private void ScheduleRetry()
        {
            if (_disposed || _started) return;
            _retryTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, RetryStartup, _dispatcher);
            _retryTimer.Start();
        }

        private void RetryStartup(object sender, EventArgs args)
        {
            _retryTimer.Stop();
            _ = StartAsync();
        }

        internal void SetProgressDemand(bool active)
        {
            if (_disposed || _progressDemand == active) return;
            _progressDemand = active;
            UpdatePolling();
            if (active && _session is { } session) Post(() => Push(session));
        }

        private void UpdatePolling()
        {
            if (_timer != null) _timer.IsEnabled = _progressDemand && _session != null;
        }

        internal (string Id, string Title)[] Sources()
        {
            try
            {
                return (_manager?.GetSessions() ?? Array.Empty<GlobalSystemMediaTransportControlsSession>())
                    .Select(session => session.SourceAppUserModelId)
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(id => (id, SourceTitle(id))).ToArray();
            }
            catch (Exception ex) { App.Log("读取媒体来源失败: " + ex.Message); return Array.Empty<(string, string)>(); }
        }

        internal static string SourceTitle(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "无播放来源";
            string name = System.IO.Path.GetFileName(id.Split('!')[0]);
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }

        internal void ChooseSource(string id)
        {
            App.Settings.PreferredMediaSource = id ?? "";
            App.Save();
            SelectSession();
        }

        private void SelectSession()
        {
            if (_manager == null || _disposed) return;
            var preferred = App.Settings.PreferredMediaSource;
            try
            {
                var selected = string.IsNullOrEmpty(preferred) ? null : _manager.GetSessions()
                    .FirstOrDefault(session => string.Equals(session.SourceAppUserModelId, preferred, StringComparison.OrdinalIgnoreCase));
                Hook(string.IsNullOrEmpty(preferred) ? _manager.GetCurrentSession() : selected);
            }
            catch (Exception ex) { App.Log("选择媒体来源失败: " + ex.Message); }
        }

        internal async Task<bool> ControlAsync(int direction)
        {
            var session = _session;
            if (session == null || _disposed) return false;
            try
            {
                return direction < 0 ? await session.TrySkipPreviousAsync()
                    : direction > 0 ? await session.TrySkipNextAsync()
                    : await session.TryTogglePlayPauseAsync();
            }
            catch (Exception ex) { App.Log("媒体会话控制失败: " + ex.Message); return false; }
        }

        private void OnTimerTick(object sender, EventArgs args)
        {
            Push(_session);
            // 轮询只在"正在播放"时维持高频(进度推进);暂停时降频到 1.5s,
            // 减少常驻的同步跨进程 SMTC 调用对 UI 线程的占用。
            var interval = _current.Playing ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMilliseconds(1500);
            if (_timer != null && _timer.Interval != interval) _timer.Interval = interval;
        }

        private void Post(Action action)
        {
            if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
            try { _dispatcher.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        private void Hook(GlobalSystemMediaTransportControlsSession session)
        {
            if (_disposed || ReferenceEquals(_session, session)) return;
            UnhookSession();
            _session = session;
            _sessionGeneration++;
            _refreshGeneration++;
            _sessionKey = session?.SourceAppUserModelId ?? "";
            UpdatePolling();
            PublishEmpty(session != null);

            if (session == null)
            {
                SessionLost?.Invoke();
                return;
            }

            _mediaPropertiesChanged = (sender, args) => Post(() => BeginRefresh(session));
            _playbackInfoChanged = (sender, args) => Post(() => Push(session));
            _timelinePropertiesChanged = (sender, args) => Post(() => Push(session));
            session.MediaPropertiesChanged += _mediaPropertiesChanged;
            session.PlaybackInfoChanged += _playbackInfoChanged;
            session.TimelinePropertiesChanged += _timelinePropertiesChanged;
            BeginRefresh(session);
            Push(session);
        }

        private void UnhookSession()
        {
            if (_session == null) return;
            try { if (_mediaPropertiesChanged != null) _session.MediaPropertiesChanged -= _mediaPropertiesChanged; } catch { }
            try { if (_playbackInfoChanged != null) _session.PlaybackInfoChanged -= _playbackInfoChanged; } catch { }
            try { if (_timelinePropertiesChanged != null) _session.TimelinePropertiesChanged -= _timelinePropertiesChanged; } catch { }
            _mediaPropertiesChanged = null;
            _playbackInfoChanged = null;
            _timelinePropertiesChanged = null;
        }

        private void BeginRefresh(GlobalSystemMediaTransportControlsSession session)
        {
            if (_disposed || !ReferenceEquals(_session, session)) return;
            long sessionGeneration = _sessionGeneration;
            long refreshGeneration = ++_refreshGeneration;
            _ = RefreshPropsAsync(session, sessionGeneration, refreshGeneration);
        }

        private async Task RefreshPropsAsync(GlobalSystemMediaTransportControlsSession session,
            long sessionGeneration, long refreshGeneration)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (!IsCurrent(session, sessionGeneration, refreshGeneration)) return;
                string title = props?.Title ?? "";
                string artist = props?.Artist ?? "";
                string sessionKey = session.SourceAppUserModelId ?? "";
                string trackKey = BuildTrackKey(sessionKey, props);

                var previous = _current;
                bool sameTrack = string.Equals(previous.TrackKey, trackKey, StringComparison.Ordinal);
                Publish(ReadPlayback(session, sessionKey, trackKey, sameTrack ? previous.ThumbnailKey : "", title,
                    artist, sameTrack ? previous.Cover : null));

                var cover = await LoadCoverAsync(props?.Thumbnail);
                if (!IsCurrent(session, sessionGeneration, refreshGeneration)) return;
                if (sameTrack && cover.Key == "error" && previous.Cover != null)
                    cover = new CoverResult(previous.Cover, previous.ThumbnailKey);

                // 封面读取期间时间线可能推进；发布封面前再次读取，避免旧快照回退进度。
                Publish(ReadPlayback(session, sessionKey, trackKey, cover.Key, title, artist, cover.Image));
            }
            catch (Exception ex)
            {
                if (IsCurrent(session, sessionGeneration, refreshGeneration))
                    App.Log("读取媒体信息: " + ex.Message);
            }
        }

        private bool IsCurrent(GlobalSystemMediaTransportControlsSession session, long sessionGeneration,
            long refreshGeneration)
        {
            return !_disposed && ReferenceEquals(_session, session)
                && _sessionGeneration == sessionGeneration && _refreshGeneration == refreshGeneration;
        }

        private static string BuildTrackKey(string sessionKey,
            GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            const string separator = "\u001F";
            return string.Join(separator, sessionKey ?? "", props?.Title ?? "", props?.Artist ?? "",
                props?.AlbumArtist ?? "", props?.AlbumTitle ?? "", props?.Subtitle ?? "",
                props?.TrackNumber.ToString() ?? "");
        }

        private MediaInfo ReadPlayback(GlobalSystemMediaTransportControlsSession session, string sessionKey, string trackKey,
            string thumbnailKey, string title, string artist, BitmapSource cover)
        {
            var timeline = session.GetTimelineProperties();
            var playback = session.GetPlaybackInfo();
            bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double rate = playback?.PlaybackRate ?? 1;
            var absolute = ProjectPosition(timeline.Position, timeline.EndTime, timeline.LastUpdatedTime, DateTimeOffset.UtcNow, playing, rate);
            var (start, duration, position, seekFrom, seekTo) = RelativeTimeline(timeline.StartTime, timeline.EndTime, absolute,
                timeline.MinSeekTime, timeline.MaxSeekTime);
            bool seekSupported = playback?.Controls?.IsPlaybackPositionEnabled == true && seekTo > seekFrom;
            return new MediaInfo(NextPublishedGeneration(), true, sessionKey, trackKey, thumbnailKey, title, artist, cover,
                playing, position, duration, seekSupported, start, seekFrom, seekTo, rate);
        }

        internal static (TimeSpan Start, TimeSpan Duration, TimeSpan Position, TimeSpan SeekFrom, TimeSpan SeekTo) RelativeTimeline(
            TimeSpan start, TimeSpan end, TimeSpan current, TimeSpan minSeek, TimeSpan maxSeek)
        {
            long first = Math.Max(0, start.Ticks);
            long last = Math.Max(first, end.Ticks);
            TimeSpan Offset(TimeSpan value) => TimeSpan.FromTicks(Math.Clamp(value.Ticks, first, last) - first);
            return (TimeSpan.FromTicks(first), TimeSpan.FromTicks(last - first), Offset(current), Offset(minSeek), Offset(maxSeek));
        }

        internal static TimeSpan ProjectPosition(TimeSpan position, TimeSpan end, DateTimeOffset updated,
            DateTimeOffset now, bool playing, double rate)
        {
            double ticks = position.Ticks;
            if (playing && updated > DateTimeOffset.UnixEpoch && now > updated)
                ticks += (now - updated).Ticks * (double.IsFinite(rate) ? rate : 1);
            if (ticks <= 0) return TimeSpan.Zero;
            if (end > TimeSpan.Zero && ticks >= end.Ticks) return end;
            return ticks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
        }

        private static long _lastPushFailLogTick = -60000;

        private void Push(GlobalSystemMediaTransportControlsSession session)
        {
            if (_disposed || session == null || !ReferenceEquals(_session, session) || !_current.HasSession) return;
            try
            {
                Publish(ReadPlayback(session, _current.SessionKey, _current.TrackKey, _current.ThumbnailKey,
                    _current.Title, _current.Artist, _current.Cover));
            }
            catch (Exception ex)
            {
                // 会话失效时进度停更;至少留一条节流日志,不然"进度卡死"无线索可查。
                long now = Environment.TickCount64;
                if (now - _lastPushFailLogTick > 30000)
                {
                    _lastPushFailLogTick = now;
                    App.Log("SMTC 进度刷新失败: " + ex.Message);
                }
            }
        }

        private void PublishEmpty(bool hasSession) => Publish(new MediaInfo(NextPublishedGeneration(), hasSession,
            _sessionKey, "", "", "", "", null, false, TimeSpan.Zero, TimeSpan.Zero, false));

        private long NextPublishedGeneration() => ++_publishedGeneration;

        private void Publish(MediaInfo info)
        {
            if (_disposed) return;
            _current = info;
            Updated?.Invoke(info);
        }

        private sealed class CoverResult
        {
            public BitmapSource Image { get; }
            public string Key { get; }
            public CoverResult(BitmapSource image, string key) { Image = image; Key = key; }
        }

        private static async Task<CoverResult> LoadCoverAsync(IRandomAccessStreamReference reference)
        {
            if (reference == null) return new CoverResult(null, "none");
            try
            {
                using var stream = await reference.OpenReadAsync();
                if (stream.Size == 0) return new CoverResult(null, "empty");
                if (stream.Size > MaxCoverBytes) return new CoverResult(null, "oversized:" + stream.Size);
                uint size = (uint)stream.Size;
                var bytes = new byte[size];
                using var reader = new DataReader(stream);
                await reader.LoadAsync(size).AsTask();
                reader.ReadBytes(bytes);
                // SHA-256 与 WPF 图像解码都可能昂贵；冻结后的 BitmapSource 可安全回到 UI 使用。
                return await Task.Run(() => DecodeCover(bytes));
            }
            catch { return new CoverResult(null, "error"); }
        }

        private static CoverResult DecodeCover(byte[] bytes)
        {
            try
            {
                string key = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes));
                var image = new BitmapImage();
                using var memory = new MemoryStream(bytes, writable: false);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 384;
                image.StreamSource = memory;
                image.EndInit();
                image.Freeze();
                return new CoverResult(image, key);
            }
            catch { return new CoverResult(null, "error"); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queuedSeek?.Completion.TrySetResult(false);
            _queuedSeek = null;
            _retryTimer?.Stop();
            _retryTimer = null;
            _sessionGeneration++;
            _refreshGeneration++;
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnTimerTick;
                _timer = null;
            }
            UnhookSession();
            _session = null;
            if (_manager != null)
            {
                try { if (_currentSessionChanged != null) _manager.CurrentSessionChanged -= _currentSessionChanged; } catch { }
                try { if (_sessionsChanged != null) _manager.SessionsChanged -= _sessionsChanged; } catch { }
            }
            _currentSessionChanged = null;
            _sessionsChanged = null;
            _manager = null;
            Updated = null;
            SessionLost = null;
        }
    }
}
