using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopBar.Services
{
    internal sealed class AudioMixer : IDisposable
    {
        internal sealed record Entry(int Index, string Name, float Volume);
        internal sealed record Snapshot(int Revision, Entry[] Entries);
        private sealed record Refresh(int Revision, TaskCompletionSource<Snapshot> Completion);
        private const int WriteIntervalMs = 60;

        private readonly object _gate = new();
        private readonly Dictionary<(int Revision, int Index), float> _writes = new();
        private Refresh _refresh;
        private int _revision;
        private long _writeAt;
        private bool _disposed;

        internal AudioMixer()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "YukiDesk application mixer" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        internal Task<Snapshot> ReadAsync()
        {
            var result = new TaskCompletionSource<Snapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            RequestRefresh(result);
            return result.Task;
        }

        internal void Clear() => RequestRefresh(null);

        private void RequestRefresh(TaskCompletionSource<Snapshot> completion)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    completion?.TrySetResult(null);
                    return;
                }
                _refresh?.Completion?.TrySetResult(null);
                _refresh = new Refresh(++_revision, completion);
                Monitor.Pulse(_gate);
            }
        }

        internal void SetVolume(int revision, int index, float value)
        {
            lock (_gate)
            {
                if (_disposed || revision != _revision) return;
                if (_writes.Count == 0) _writeAt = Environment.TickCount64 + WriteIntervalMs;
                _writes[(revision, index)] = value;
                Monitor.Pulse(_gate);
            }
        }

        private void Run()
        {
            using var volume = new VolumeService();
            VolumeService.ApplicationVolume[] sessions = null;
            int revision = 0;
            try
            {
                while (true)
                {
                    Refresh refresh;
                    KeyValuePair<(int Revision, int Index), float>[] writes;
                    lock (_gate)
                    {
                        while (!_disposed && _refresh == null)
                        {
                            if (_writes.Count == 0) Monitor.Wait(_gate);
                            else
                            {
                                long delay = _writeAt - Environment.TickCount64;
                                if (delay <= 0) break;
                                Monitor.Wait(_gate, (int)delay);
                            }
                        }
                        refresh = _refresh;
                        _refresh = null;
                        writes = _writes.ToArray();
                        _writes.Clear();
                        if (_disposed && writes.Length == 0) return;
                    }

                    if (writes.Length > 0 && sessions != null && volume.IsBoundToDefaultOutput())
                    {
                        foreach (var write in writes)
                        {
                            if (write.Key.Revision != revision || (uint)write.Key.Index >= sessions.Length) continue;
                            try { sessions[write.Key.Index].Write(write.Value); }
                            catch (Exception ex) { App.Log("写入应用音量失败: " + ex.Message); }
                        }
                    }
                    if (refresh == null) continue;
                    ReleaseSessions(sessions);
                    sessions = null;
                    if (refresh.Completion == null) continue;

                    volume.SyncDefaultIfChanged();
                    sessions = volume.ReadApplicationVolumes();
                    revision = refresh.Revision;
                    var snapshot = sessions == null ? null : new Snapshot(revision,
                        sessions.Select((item, index) => new Entry(index, item.Name, item.Volume)).ToArray());
                    lock (_gate)
                        refresh.Completion.TrySetResult(!_disposed && _revision == revision ? snapshot : null);
                }
            }
            finally { ReleaseSessions(sessions); }
        }

        private static void ReleaseSessions(VolumeService.ApplicationVolume[] sessions)
        {
            if (sessions == null) return;
            foreach (var session in sessions) session.Dispose();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _refresh?.Completion?.TrySetResult(null);
                _refresh = null;
                Monitor.Pulse(_gate);
            }
        }
    }
}
