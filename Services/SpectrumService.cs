using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopBar.Services
{
    /// <summary>
    /// 实时音频频谱:WASAPI 环回采集系统播放流,FFT 后按对数频段聚合成
    /// BarCount 个 0~1 的能量值,供频谱药丸渲染。采集在后台线程进行,
    /// 系统默认输出设备变化或端点失效时自动重连。
    /// </summary>
    public sealed class SpectrumService
    {
        public const int BarCount = 16;
        private const int FftSize = 2048; // 必须是 2 的幂
        private const int Hop = 512;      // 每累积 512 个新样本做一次 FFT(约 10.7ms @48kHz)

        private static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid AudioClientIid = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid CaptureClientIid = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        private static readonly Guid FloatSubFormat = new Guid("00000003-0000-0010-8000-00AA00389B71");
        private static readonly Guid PcmSubFormat = new Guid("00000001-0000-0010-8000-00AA00389B71");

        private const uint ClsctxAll = 0x17;
        private const int ShareModeShared = 0;
        private const int StreamFlagsLoopback = 0x00020000;
        private const uint BufferFlagSilent = 0x2;

        private static SpectrumService _instance;
        public static SpectrumService Instance => _instance ??= new SpectrumService();

        private static readonly float[] CosTable = BuildTwiddle(true);
        private static readonly float[] SinTable = BuildTwiddle(false);

        private readonly object _gate = new object();
        private readonly float[] _hist = new float[FftSize];
        private readonly float[] _smooth = new float[BarCount];
        private float[] _floatBuf;
        private short[] _shortBuf;
        private float[] _re = new float[FftSize], _im = new float[FftSize];
        private float[] _window;
        private int[] _edges;
        private int _histPos, _histCount, _pending, _lastRate;
        private int _decim = 1, _decimCount;
        private float _decimAccum;
        private int _effectiveRate = 48000;
        private readonly bool _debug = Environment.GetEnvironmentVariable("YUKI_SPECTRUM_DEBUG") == "1";
        private int _dbgPackets;
        private float _dbgSamplePeak, _dbgBandPeak;
        private long _dbgLast;
        private float _agc; // 最近约 2 秒的频段峰值,用于自动增益归一化
        private Thread _thread;
        private CancellationTokenSource _runCancellation;
        private int _users;
        private long _lastDataTick;
        private long _lastBandsTick;

        private volatile float[] _bands = new float[BarCount];

        /// <summary>当前频段能量(0~1)。数组整体只读替换,读取方拿引用直接遍历即可。</summary>
        public float[] Bands => _bands;

        /// <summary>最近一次已发布频段的时间(Environment.TickCount64),用于判断显示是否停滞。</summary>
        public long LastDataTick => _lastBandsTick;

        private SpectrumService() { }

        public void AddRef()
        {
            lock (_gate)
            {
                _users++;
                StartWorkerLocked();
            }
        }

        public void ReleaseRef()
        {
            CancellationTokenSource cancellation = null;
            lock (_gate)
            {
                _users = Math.Max(0, _users - 1);
                if (_users == 0)
                    cancellation = _runCancellation;
            }

            // 不在 _gate 内等待；工作线程退出时会取得该锁来清理状态。
            try { cancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>请求停止所有采集引用，用于应用正常退出时回收 WASAPI 资源。</summary>
        public void Stop()
        {
            CancellationTokenSource cancellation;
            Thread thread;
            lock (_gate)
            {
                _users = 0;
                cancellation = _runCancellation;
                thread = _thread;
            }

            try { cancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
            if (thread != null && thread != Thread.CurrentThread && !thread.Join(1500))
                App.Log("频谱采集线程停止超时，将等待后台线程自行退出");
        }

        private void StartWorkerLocked()
        {
            if (_users <= 0 || _thread != null) return;

            _runCancellation?.Dispose();
            _runCancellation = new CancellationTokenSource();
            var cancellation = _runCancellation;
            _thread = new Thread(() => RunLoop(cancellation))
            {
                IsBackground = true,
                Name = "SpectrumLoopback"
            };
            _thread.Start();
            App.Log("频谱采集线程已启动");
        }

        private void RunLoop(CancellationTokenSource runCancellation)
        {
            GlassDiagnostics.TagThread("频谱采集");
            var token = runCancellation.Token;
            try
            {
                if (Environment.GetEnvironmentVariable("YUKI_SPECTRUM_SELFTEST") == "1") SelfTest();
                long lastErrorLog = 0;
                int failures = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        RunOnce(token);
                        failures = 0;
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        long now = Environment.TickCount64;
                        if (now - lastErrorLog > 30000)
                        {
                            App.Log("频谱采集异常(将自动重连): " + ex.Message);
                            lastErrorLog = now;
                        }
                    }

                    // 退避后重连:设备切换、端点失效都走这里。
                    // 连续失败(如设备给出 24bit 等不支持的混音格式)时把重连间隔拉到 3 秒,
                    // 避免每 300ms 一次"连接-失败"循环空转。
                    int backoff = failures > 5 ? 300 : 10;
                    for (int i = 0; i < 30 && !token.IsCancellationRequested; i++)
                        token.WaitHandle.WaitOne(backoff);
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_runCancellation, runCancellation))
                    {
                        _thread = null;
                        _runCancellation = null;
                        ResetBandsLocked();
                        if (_users > 0) StartWorkerLocked();
                    }
                }

                runCancellation.Dispose();
            }
        }

        private void ResetBandsLocked()
        {
            Array.Clear(_hist, 0, _hist.Length);
            Array.Clear(_smooth, 0, _smooth.Length);
            _histPos = 0;
            _histCount = 0;
            _pending = 0;
            _agc = 0;
            _bands = new float[BarCount];
            _lastDataTick = 0;
            _lastBandsTick = 0;
        }

        /// <summary>FFT 链路自检:注入 0.5 幅度 440Hz 正弦,验证频段聚合与 AGC 输出。</summary>
        private void SelfTest()
        {
            _decim = 1;
            _effectiveRate = 48000;
            _agc = 0;
            for (int i = 0; i < FftSize * 2; i++)
            {
                Push((float)Math.Sin(2 * Math.PI * 440 * i / 48000.0) * 0.5f);
            }
            float peak = 0;
            int peakBand = -1;
            for (int b = 0; b < BarCount; b++)
                if (_bands[b] > peak) { peak = _bands[b]; peakBand = b; }
            App.Log("频谱自检: 峰值=" + peak.ToString("F3") + " 频段#" + peakBand);
        }

        private void RunOnce(CancellationToken token)
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass));
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            IntPtr format = IntPtr.Zero;
            try
            {
                Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device), "查找默认播放设备");
                string deviceId = ReadDeviceId(device);
                var clientIid = AudioClientIid;
                Check(device.Activate(ref clientIid, ClsctxAll, IntPtr.Zero, out client), "连接播放设备");
                Check(client.GetMixFormat(out format), "读取混音格式");

                var wfx = Marshal.PtrToStructure<WAVEFORMATEX>(format);
                int channels = wfx.nChannels;
                int rate = (int)wfx.nSamplesPerSec;
                bool isFloat = IsFloatFormat(format, ref wfx);
                // 高采样率(如 192kHz)先抽取到约 48kHz:恢复低频分辨率,FFT 也更省。
                _decim = Math.Max(1, rate / 48000);
                _effectiveRate = rate / _decim;
                _decimAccum = 0;
                _decimCount = 0;
                // 设备/格式重连后不能混入旧端点的采样历史。
                Array.Clear(_hist, 0, _hist.Length);
                Array.Clear(_smooth, 0, _smooth.Length);
                _histPos = _histCount = _pending = 0;
                _agc = 0;
                _bands = new float[BarCount];
                _lastBandsTick = 0;
                _lastRate = 0; // 让 ComputeBands 按新采样率重建窗表
                App.Log("频谱: 环回采集 " + rate + "Hz " + channels + "声道 " + (isFloat ? "float32" : "pcm16")
                    + (_decim > 1 ? " 抽取1/" + _decim + "→" + _effectiveRate + "Hz" : ""));

                Check(client.Initialize(ShareModeShared, StreamFlagsLoopback, 2000000, 0, format, IntPtr.Zero), "初始化环回采集");
                var captureIid = CaptureClientIid;
                Check(client.GetService(ref captureIid, out capture), "获取环回采集接口");
                Check(client.Start(), "启动环回采集");

                long lastPoll = 0;
                long lastPacket = Environment.TickCount64;
                long lastDecay = lastPacket;
                while (!token.IsCancellationRequested)
                {
                    long now = Environment.TickCount64;
                    if (now - lastPoll > 2000)
                    {
                        lastPoll = now;
                        if (GetDefaultEndpointId(enumerator) != deviceId) break; // 默认输出变了,重连新端点
                    }

                    Check(capture.GetNextPacketSize(out uint packet), "查询数据包");
                    if (packet == 0)
                    {
                        // 系统静音时环回不出包:以有限发布频率衰减，避免每次轮询都分配新快照。
                        if (now - lastPacket > 300 && now - lastDecay >= 33)
                        {
                            DecayAll();
                            lastDecay = now;
                        }
                        token.WaitHandle.WaitOne(15);
                        continue;
                    }

                    uint frames = 0;
                    bool bufferAcquired = false;
                    try
                    {
                        Check(capture.GetBuffer(out IntPtr data, out frames, out uint flags, out _, out _), "读取音频数据");
                        bufferAcquired = true;
                        _lastDataTick = now;
                        lastPacket = now;
                        if (frames > 0)
                        {
                            if ((flags & BufferFlagSilent) == 0 && data != IntPtr.Zero)
                                Accumulate(data, (int)frames, channels, isFloat);
                            else
                                AccumulateSilence((int)frames);
                        }
                        if (_debug) _dbgPackets++;
                    }
                    finally
                    {
                        if (bufferAcquired)
                        {
                            try { Check(capture.ReleaseBuffer(frames), "释放音频数据"); }
                            catch { }
                        }
                    }

                    if (_debug && now - _dbgLast > 5000)
                    {
                        _dbgLast = now;
                        App.Log("频谱调试: 近5秒包=" + _dbgPackets
                            + " 样本峰值=" + _dbgSamplePeak.ToString("F4")
                            + " 最大频段值=" + _dbgBandPeak.ToString("F3"));
                        _dbgPackets = 0;
                        _dbgSamplePeak = 0;
                        _dbgBandPeak = 0;
                    }
                }
            }
            finally
            {
                try { client?.Stop(); } catch { }
                ReleaseCom(capture);
                ReleaseCom(client);
                ReleaseCom(device);
                ReleaseCom(enumerator);
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            }
        }

        private static bool IsFloatFormat(IntPtr format, ref WAVEFORMATEX wfx)
        {
            if (wfx.wFormatTag == 0x0003) return true; // WAVE_FORMAT_IEEE_FLOAT
            if (wfx.wFormatTag == 0xFFFE) // WAVE_FORMAT_EXTENSIBLE:SubFormat GUID 在偏移 24
            {
                var sub = Marshal.PtrToStructure<Guid>(format + 24);
                if (sub == FloatSubFormat) return true;
                if (sub == PcmSubFormat && wfx.wBitsPerSample == 16) return false;
            }
            else if (wfx.wFormatTag == 0x0001 && wfx.wBitsPerSample == 16) return false;
            throw new InvalidOperationException("不支持的混音格式 tag=" + wfx.wFormatTag + " bits=" + wfx.wBitsPerSample);
        }

        private void Accumulate(IntPtr data, int frames, int channels, bool isFloat)
        {
            int total = frames * channels;
            if (isFloat)
            {
                if (_floatBuf == null || _floatBuf.Length < total) _floatBuf = new float[total + channels * 64];
                Marshal.Copy(data, _floatBuf, 0, total);
                for (int f = 0; f < frames; f++)
                {
                    int baseIdx = f * channels;
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += _floatBuf[baseIdx + c];
                    PushDecimated(sum / channels);
                }
            }
            else
            {
                if (_shortBuf == null || _shortBuf.Length < total) _shortBuf = new short[total + channels * 64];
                Marshal.Copy(data, _shortBuf, 0, total);
                for (int f = 0; f < frames; f++)
                {
                    int baseIdx = f * channels;
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += _shortBuf[baseIdx + c] / 32768f;
                    PushDecimated(sum / channels);
                }
            }
        }

        private void AccumulateSilence(int frames)
        {
            for (int i = 0; i < frames; i++) PushDecimated(0f);
        }

        /// <summary>抽取滤波:高采样率下先平均 _decim 个样本再入历史缓冲。</summary>
        private void PushDecimated(float value)
        {
            if (_decim == 1)
            {
                Push(value);
                return;
            }

            _decimAccum += value;
            if (++_decimCount < _decim) return;
            Push(_decimAccum / _decim);
            _decimAccum = 0;
            _decimCount = 0;
        }

        private void Push(float value)
        {
            float a = value < 0 ? -value : value;
            if (a > _dbgSamplePeak) _dbgSamplePeak = a;
            _hist[_histPos] = value;
            _histPos = (_histPos + 1) & (FftSize - 1);
            if (_histCount < FftSize) _histCount++;
            // 按样本数累积;此前误按包数计数导致 FFT 约 5 秒才跑一次。
            if (++_pending >= Hop && _histCount == FftSize)
            {
                _pending = 0;
                ComputeBands(_effectiveRate);
            }
        }

        private void ComputeBands(int rate)
        {
            if (_window == null || _lastRate != rate) BuildTables(rate);
            // 按时间序取出最近 FftSize 个样本并加汉宁窗。
            for (int k = 0; k < FftSize; k++)
            {
                _re[k] = _hist[(_histPos + k) & (FftSize - 1)] * _window[k];
                _im[k] = 0f;
            }
            Fft(_re, _im);

            var next = new float[BarCount];
            float half = FftSize / 4f; // 汉宁窗下满幅正弦的主瓣幅度约为 N/4
            float framePeak = 0;
            for (int b = 0; b < BarCount; b++)
            {
                // 取频段内最强 bin 的能量:平均会把宽频段稀释成近零,高段柱子永远不动。
                float best = 0;
                for (int k = _edges[b]; k < _edges[b + 1]; k++)
                {
                    float m2 = _re[k] * _re[k] + _im[k] * _im[k];
                    if (m2 > best) best = m2;
                }
                float mag = (float)Math.Sqrt(best) / half;
                next[b] = mag;
                if (mag > framePeak) framePeak = mag;
            }

            // AGC 自动增益:按最近约 2 秒的峰值归一化;下限压得很低,
            // 小音量播放时每根柱子(含高频段)都有明显跳动。
            _agc = Math.Max(framePeak, _agc * 0.995f);
            float norm = Math.Max(_agc, 0.004f);
            for (int b = 0; b < BarCount; b++)
            {
                float v = next[b] / norm;
                if (v > 1) v = 1;
                if (v > _dbgBandPeak) _dbgBandPeak = v;
                v = (float)Math.Pow(v, 0.6); // 伽马<1 提亮中低能量,视觉更活跃
                // 音乐高频能量天然偏低,轻微补偿让整体更均匀。
                v = Math.Min(1f, v * (1f + 0.6f * b / (BarCount - 1)));
                // 攻击即时、释放平滑,柱子既跟手又不抖。
                _smooth[b] = v > _smooth[b] ? v : _smooth[b] * 0.78f + v * 0.22f;
                next[b] = _smooth[b];
            }
            _bands = next;
            _lastBandsTick = Environment.TickCount64;
        }

        private void DecayAll()
        {
            var next = new float[BarCount];
            for (int b = 0; b < BarCount; b++)
            {
                _smooth[b] *= 0.72f;
                next[b] = _smooth[b];
            }
            _bands = next;
            _lastBandsTick = Environment.TickCount64;
        }

        private void BuildTables(int rate)
        {
            _lastRate = rate;
            _window = new float[FftSize];
            for (int i = 0; i < FftSize; i++)
                _window[i] = 0.5f * (1f - (float)Math.Cos(2 * Math.PI * i / (FftSize - 1)));

            // 对数等比划分 40Hz~15kHz 成 BarCount 个频段。
            _edges = new int[BarCount + 1];
            double binHz = rate / (double)FftSize;
            double fmin = 40, fmax = Math.Min(15000, rate / 2.0);
            for (int i = 0; i <= BarCount; i++)
            {
                double f = fmin * Math.Pow(fmax / fmin, i / (double)BarCount);
                int bin = (int)Math.Round(f / binHz);
                if (bin < 1) bin = 1;
                if (bin > FftSize / 2 - 1) bin = FftSize / 2 - 1;
                if (i > 0 && bin <= _edges[i - 1]) bin = _edges[i - 1] + 1;
                if (bin > FftSize / 2 - 1) bin = FftSize / 2 - 1;
                _edges[i] = bin;
            }
        }

        private static float[] BuildTwiddle(bool cosine)
        {
            // 旋转因子表:索引 k 对应角度 -2πk/n,各蝶形级通过步进复用。
            var table = new float[FftSize / 2];
            for (int k = 0; k < FftSize / 2; k++)
            {
                double angle = -2 * Math.PI * k / FftSize;
                table[k] = (float)(cosine ? Math.Cos(angle) : Math.Sin(angle));
            }
            return table;
        }

        private static void Fft(float[] re, float[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1;
                int stride = n / len;
                for (int i = 0; i < n; i += len)
                {
                    for (int k = 0; k < half; k++)
                    {
                        int tw = k * stride;
                        float wr = CosTable[tw], wi = SinTable[tw];
                        int a = i + k, bIdx = a + half;
                        float tr = re[bIdx] * wr - im[bIdx] * wi;
                        float ti = re[bIdx] * wi + im[bIdx] * wr;
                        re[bIdx] = re[a] - tr;
                        im[bIdx] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }

        private static string GetDefaultEndpointId(IMMDeviceEnumerator enumerator)
        {
            IMMDevice d = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out d) != 0 || d == null) return "";
                return ReadDeviceId(d);
            }
            catch { return ""; }
            finally { ReleaseCom(d); }
        }

        private static string ReadDeviceId(IMMDevice device)
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                Check(device.GetId(out ptr), "读取设备ID");
                return Marshal.PtrToStringUni(ptr) ?? "";
            }
            finally { if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr); }
        }

        private static void Check(int hr, string operation)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        private static void ReleaseCom(object value)
        {
            try { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); } catch { }
        }

        private enum EDataFlow { Render, Capture, All }
        private enum ERole { Console, Multimedia, Communications }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            // 保留 COM vtable 的完整方法槽位,否则后续方法会错位调用。
            int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            int RegisterEndpointNotificationCallback(IntPtr client);
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, uint clsctx, IntPtr activationParams, out IAudioClient client);
            int OpenPropertyStore(uint access, out IntPtr store);
            int GetId(out IntPtr id);
            int GetState(out uint state);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
            int GetBufferSize(out uint bufferFrames);
            int GetStreamLatency(out long latency);
            int GetCurrentPadding(out uint padding);
            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            int GetMixFormat(out IntPtr format);
            int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
            int Start();
            int Stop();
            int Reset();
            int SetEventHandle(IntPtr handle);
            // 原型是 (REFIID, void**):IID 必须显式声明,否则封送错位。
            int GetService(ref Guid iid, out IAudioCaptureClient service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            int GetBuffer(out IntPtr data, out uint frames, out uint flags, out long devicePosition, out long qpcPosition);
            int ReleaseBuffer(uint frames);
            int GetNextPacketSize(out uint frames);
        }
    }
}
