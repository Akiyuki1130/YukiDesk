using System;
using System.Threading;

namespace DesktopBar.Services
{
    internal sealed class AudioOutput : IDisposable
    {
        internal sealed record Snapshot(string Id, string Name, float? Volume, bool? Muted, long VolumeRevision, long MuteRevision);
        private readonly record struct Write<T>(string Id, T Value, long Revision);
        private const int WriteIntervalMs = 60;
        private readonly AutoResetEvent _signal = new(false);
        private readonly object _gate = new();
        private readonly Action _updated;
        private Write<float>? _volumeWrite;
        private Write<bool>? _muteWrite;
        private long _writeAt, _volumeRevision, _muteRevision;
        private int _deviceChanged, _volumeChanged;
        private Snapshot _current;
        private volatile bool _disposed;

        internal Snapshot Current => Volatile.Read(ref _current);

        internal AudioOutput(Action updated)
        {
            _updated = updated;
            var thread = new Thread(Run) { IsBackground = true, Name = "YukiDesk audio output" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        internal void RequestRefresh()
        {
            Interlocked.Exchange(ref _deviceChanged, 1);
            Wake();
        }

        private void OnVolumeChanged()
        {
            Interlocked.Exchange(ref _volumeChanged, 1);
            Wake();
        }

        private void Wake()
        {
            try { _signal.Set(); }
            catch (ObjectDisposedException) { }
        }

        internal long SetVolume(string id, float value)
        {
            long revision;
            lock (_gate)
            {
                if (_disposed || id == null) return 0;
                if (_volumeWrite == null) _writeAt = Environment.TickCount64 + WriteIntervalMs;
                revision = ++_volumeRevision;
                _volumeWrite = new Write<float>(id, value, revision);
            }
            Wake();
            return revision;
        }

        internal long SetMute(string id, bool muted)
        {
            long revision;
            lock (_gate)
            {
                if (_disposed || id == null) return 0;
                revision = ++_muteRevision;
                _muteWrite = new Write<bool>(id, muted, revision);
            }
            Wake();
            return revision;
        }

        private void Run()
        {
            using var signal = _signal;
            try
            {
                using var service = new VolumeService();
                service.OutputChanged += RequestRefresh;
                service.VolumeChanged += OnVolumeChanged;
                string name = null;
                long nextProbe = 0, completedVolume = 0, completedMute = 0;
                while (!_disposed)
                {
                    long now = Environment.TickCount64;
                    bool probe = Interlocked.Exchange(ref _deviceChanged, 0) != 0 || now >= nextProbe;
                    bool read = Interlocked.Exchange(ref _volumeChanged, 0) != 0;
                    Write<float>? volume = null;
                    Write<bool>? mute;
                    long wakeAt;
                    lock (_gate)
                    {
                        if (_volumeWrite != null && now >= _writeAt)
                        {
                            volume = _volumeWrite;
                            _volumeWrite = null;
                        }
                        wakeAt = _volumeWrite != null ? _writeAt : long.MaxValue;
                        mute = _muteWrite;
                        _muteWrite = null;
                    }

                    if (probe)
                    {
                        bool watchDevice = service.WatchOutput();
                        service.SyncDefaultIfChanged();
                        name = service.BoundOutputName();
                        bool watchVolume = service.WatchVolume();
                        nextProbe = now + (watchDevice && watchVolume ? 30_000 : 4_000);
                    }
                    if (volume is { } level)
                    {
                        completedVolume = level.Revision;
                        if (!service.SetVolumeForOutput(level.Value, level.Id)) RequestRefresh();
                    }
                    if (mute is { } state)
                    {
                        completedMute = state.Revision;
                        if (!service.SetMuteForOutput(state.Value, state.Id)) RequestRefresh();
                    }
                    if (probe || read || volume.HasValue || mute.HasValue)
                    {
                        string id = service.OutputId;
                        float? percent = id == null ? null : service.GetVolume();
                        bool? muted = percent.HasValue ? service.GetMute() : null;
                        if (id != service.OutputId) nextProbe = Math.Min(nextProbe, now + 4_000);
                        Publish(id != null && id == service.OutputId
                            ? new Snapshot(id, name, percent, muted, completedVolume, completedMute)
                            : new Snapshot(null, null, null, null, completedVolume, completedMute));
                    }
                    int delay = (int)Math.Clamp(Math.Min(nextProbe, wakeAt) - Environment.TickCount64, 0, 30_000);
                    signal.WaitOne(delay);
                }
            }
            catch (Exception ex)
            {
                if (!_disposed) App.Log("系统音量后台线程结束: " + ex.Message);
            }
        }

        private void Publish(Snapshot snapshot)
        {
            if (_disposed || snapshot == Current) return;
            Volatile.Write(ref _current, snapshot);
            _updated();
        }

        public void Dispose()
        {
            _disposed = true;
            Wake();
        }
    }
}
