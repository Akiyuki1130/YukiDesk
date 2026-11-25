using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DesktopBar.Models;
using Microsoft.Win32;

namespace DesktopBar.Services
{
    [Flags]
    internal enum PerfComponents
    {
        None = 0, Cpu = 1, Gpu = 2, Memory = 4, Network = 8,
        All = Cpu | Gpu | Memory | Network
    }

    /// <summary>一块显卡的信息(DXGI 枚举)。属性绑定用于设置页下拉框。</summary>
    public sealed class GpuAdapterInfo
    {
        public string Name { get; set; }
        /// <summary>LUID "高32位:低32位",与 GPU 引擎计数器实例里的 luid 对应。</summary>
        public string Luid { get; set; }
        internal ulong LuidValue { get; set; }
        /// <summary>PCI 厂商/设备 ID(0x1002=AMD,0x10DE=NVIDIA,0x8086=Intel),用于匹配 ADL/NVML 遥测。</summary>
        public int VendorId { get; set; }
        public int DeviceId { get; set; }
        public double UtilPercent { get; set; } = -1;
        /// <summary>核心温度(°C)与当前图形频率(MHz);-1 表示来源不可用。</summary>
        public double TempC { get; set; } = -1;
        public double ClockMhz { get; set; } = -1;
        public override string ToString() => Name ?? "";
    }

    /// <summary>一次性能采样快照。数值 -1 表示暂时未知(首个采样周期/计数器不可用)。</summary>
    public sealed class PerfSample
    {
        internal PerfComponents Components;
        public double CpuPercent;
        public ulong MemUsedBytes;
        public ulong MemTotalBytes;
        public double NetUpBps;
        public double NetDownBps;
        /// <summary>CPU 温度(°C,ACPI 热区,多数台式机不可读)。</summary>
        public double CpuTempC = -1;
        /// <summary>CPU 当前平均频率(MHz,% Processor Performance × 基准频率)。</summary>
        public double CpuFreqMhz = -1;
        /// <summary>内存条配置频率(MT/s,Win32_PhysicalMemory,静态)。</summary>
        public double MemSpeedMts = -1;
        /// <summary>显卡列表(含各自占用率);元素只读替换,勿跨线程修改。</summary>
        public List<GpuAdapterInfo> Gpus;
    }

    /// <summary>
    /// 性能监测采样:CPU(GetSystemTimes)、内存(GlobalMemoryStatusEx)、
    /// GPU(DXGI 枚举 + GPU 引擎 PDH 通配符计数器)、网络(网络接口 PDH 通配符计数器)。
    /// 扩展遥测(供悬停展开页):CPU 频率(PDH % Processor Performance × 基准频率)、
    /// CPU 温度(ACPI 热区 WMI,尽力而为)、内存频率(Win32_PhysicalMemory)、
    /// GPU 温度/频率(AMD 走 atiadlxx.dll PMLog,NVIDIA 走 nvml.dll,均无驱动/无管理员要求)。
    /// 通配符路径一次采集覆盖全部实例,实例随进程/接口增删自动跟随。
    /// 引用计数模式:任一胶囊可见时以 1 秒周期采样,全部隐藏后线程退出。
    /// </summary>
    public sealed class PerfService
    {
        public static PerfService Instance { get; } = new PerfService();

        private readonly object _gate = new object();
        private int _users;
        private Thread _thread;
        private CancellationTokenSource _cancellation;
        private bool _initialSampleRequested;
        private int _consecutiveFailures;
        private long _retryNotBefore;
        private volatile PerfSample _latest;
        private volatile PerfComponents _components = PerfComponents.All;
        private readonly Queue<(long Time, PerfSample Sample)> _history = new();

        internal (long Time, PerfSample Sample)[] RecentSamples()
        {
            long cutoff = Environment.TickCount64 - 60000;
            lock (_gate)
            {
                while (_history.Count > 0 && _history.Peek().Time < cutoff) _history.Dequeue();
                return _history.ToArray();
            }
        }

        /// <summary>最近一次采样快照(采样线程写入,读取方只读)。</summary>
        public PerfSample Latest => _latest;

        internal PerfSample LatestForConfiguration
        {
            get
            {
                var sample = _latest;
                return sample?.Components == _components ? sample : null;
            }
        }

        /// <summary>每次采样后触发;处理器运行在采样线程,UI 使用前需自行调度。</summary>
        public event Action<PerfSample> Updated;

        private PerfService() { }

        internal void ConfigureMetrics(AppSettings settings)
        {
            var components = PerfComponents.None;
            if (settings.ShowMonitorCpu) components |= PerfComponents.Cpu;
            if (settings.ShowMonitorGpu) components |= PerfComponents.Gpu;
            if (settings.ShowMonitorMemory) components |= PerfComponents.Memory;
            if (settings.ShowMonitorNetwork) components |= PerfComponents.Network;
            _components = components;
        }

        internal void RequestInitialSample()
        {
            lock (_gate)
            {
                if (LatestForConfiguration != null || _initialSampleRequested) return;
                _initialSampleRequested = true;
                StartWorkerLocked();
            }
        }

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
                if (_users == 0 && !_initialSampleRequested) cancellation = _cancellation;
            }
            try { cancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void StartWorkerLocked()
        {
            if ((_users <= 0 && !_initialSampleRequested) || _thread != null) return;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            var cancellation = _cancellation;
            _thread = new Thread(() => RunLoop(cancellation))
            {
                IsBackground = true,
                Name = "PerfMonitor"
            };
            _thread.Start();
        }

        private void RunLoop(CancellationTokenSource runCancellation)
        {
            GlassDiagnostics.TagThread("Perf采样");
            var token = runCancellation.Token;
            try
            {
                long wait = _retryNotBefore - Environment.TickCount64;
                if (wait > 0 && token.WaitHandle.WaitOne((int)wait)) return;
                using var cpu = new CpuSampler();
                using var mem = new MemorySampler();
                PdhHost pdh = null;
                PerfComponents lastComponents = PerfComponents.None;
                double? cpuBaseMhz = null;
                var cpuTemp = new CpuTempSampler();
                AdlHost adl = null;
                NvmlHost nvml = null;
                long adlRetryNotBefore = 0, nvmlRetryNotBefore = 0;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var components = _components;
                        bool readCpu = (components & PerfComponents.Cpu) != 0;
                        bool readGpu = (components & PerfComponents.Gpu) != 0;
                        if (pdh == null || (lastComponents & ~PerfComponents.Memory) != (components & ~PerfComponents.Memory))
                        {
                            pdh?.Dispose();
                            pdh = new PdhHost(components);
                        }
                        if (readCpu && (lastComponents & PerfComponents.Cpu) == 0)
                        {
                            cpu.Reset();
                            cpu.Next();
                            cpuBaseMhz ??= ReadCpuBaseMhz();
                        }
                        lastComponents = components;
                        var sample = new PerfSample
                        {
                            Components = components,
                            CpuPercent = readCpu ? cpu.Next() : -1,
                            NetUpBps = -1,
                            NetDownBps = -1
                        };
                        if ((components & PerfComponents.Memory) != 0)
                        {
                            (sample.MemTotalBytes, sample.MemUsedBytes) = mem.Next();
                            sample.MemSpeedMts = WmiReader.ReadMemSpeedMts();
                        }
                        pdh.Collect();
                        if (pdh.NetAvailable)
                            (sample.NetUpBps, sample.NetDownBps) = pdh.ReadNetTotals();

                        if (pdh.CpuPerfAvailable && cpuBaseMhz is > 0)
                        {
                            double perf = pdh.ReadCpuPerf();
                            if (perf >= 0) sample.CpuFreqMhz = cpuBaseMhz.Value * perf / 100.0;
                        }
                        // CPU 温度链(优先级从高到低):PawnIO 原生 Tctl(本进程直连或高权限从属进程
                        // 经共享内存发布)> AMD PMLog TEMP_CPU > ACPI 热区 WMI > AMD PMLog SoC。
                        double pawnTctl = readCpu ? PawnIoTctl.ReadCpuTempC() : -1;
                        if (readCpu && pawnTctl < 0) pawnTctl = TemperatureSlave.ReadTctl();
                        double wmiTemp = readCpu && pawnTctl < 0 ? cpuTemp.Next() : -1;

                        bool readAdapters = readGpu || readCpu && pawnTctl < 0;
                        var adapters = readAdapters ? AdaptersSnapshot() : null;
                        sample.Gpus = readGpu ? adapters : new List<GpuAdapterInfo>();
                        if (sample.Gpus.Count > 0 && pdh.GpuAvailable) pdh.UpdateGpuUsage(sample.Gpus);

                        // GPU 温度/频率遥测:按厂商惰性初始化对应提供方,失败静默降级(面板显示 --)。
                        // 初始化失败后 60 秒内不重试:每秒 LoadLibrary/ADL_Create 的重试既泄漏句柄又空转。
                        if (!readAdapters && adl != null) { adl.Dispose(); adl = null; }
                        if (!readGpu && nvml != null) { nvml.Dispose(); nvml = null; }
                        if (adapters?.Exists(g => g.VendorId == 0x1002) == true
                            && adl == null && Environment.TickCount64 >= adlRetryNotBefore)
                        {
                            adl = AdlHost.TryCreate();
                            if (adl == null) adlRetryNotBefore = Environment.TickCount64 + 60000;
                        }
                        if (readGpu && sample.Gpus.Exists(g => g.VendorId == 0x10DE)
                            && nvml == null && Environment.TickCount64 >= nvmlRetryNotBefore)
                        {
                            nvml = NvmlHost.TryCreate();
                            if (nvml == null) nvmlRetryNotBefore = Environment.TickCount64 + 60000;
                        }
                        (double amdCpuTemp, double amdSocTemp) = adl != null ? adl.Poll(adapters) : (-1, -1);
                        nvml?.Poll(sample.Gpus);
                        if (readCpu) sample.CpuTempC = pawnTctl >= 0 ? pawnTctl : amdCpuTemp >= 0 ? amdCpuTemp
                            : wmiTemp >= 0 ? wmiTemp : amdSocTemp;

                        bool keepSampling;
                        lock (_gate)
                        {
                            _history.Enqueue((Environment.TickCount64, sample));
                            if (_history.Count > 60) _history.Dequeue();
                            _latest = sample;
                            _initialSampleRequested = false;
                            keepSampling = _users > 0;
                        }
                        Updated?.Invoke(sample);
                        lock (_gate)
                        {
                            _consecutiveFailures = 0;
                            _retryNotBefore = 0;
                        }
                        if (!keepSampling || token.WaitHandle.WaitOne(1000)) break;
                    }
                }
                finally
                {
                    // 放 finally:异常重启线程的路径同样要释放 ADL/NVML,否则句柄随重启累积。
                    adl?.Dispose();
                    nvml?.Dispose();
                    pdh?.Dispose();
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    int delay;
                    lock (_gate)
                    {
                        _consecutiveFailures = Math.Min(6, _consecutiveFailures + 1);
                        delay = RetryDelayMs(_consecutiveFailures);
                        _retryNotBefore = Environment.TickCount64 + delay;
                    }
                    App.Log($"性能监测线程异常，{delay / 1000} 秒后重试: {ex.Message}");
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_cancellation, runCancellation))
                    {
                        _thread = null;
                        _cancellation = null;
                        if (_users > 0 || (_initialSampleRequested && (token.IsCancellationRequested || _consecutiveFailures < 3)))
                            StartWorkerLocked();
                        else _initialSampleRequested = false;
                    }
                }
                runCancellation.Dispose();
            }
        }

        internal static int RetryDelayMs(int failures) => Math.Min(30000, 1000 << (Math.Clamp(failures, 1, 6) - 1));

        // 显卡名单每 30 秒重枚举一次;初值取负数,保证启动后第一次比较就能触发刷新。
        private List<GpuAdapterInfo> _adapters = new List<GpuAdapterInfo>();
        private readonly object _adaptersGate = new object();
        private long _adaptersTick = -30000;

        private List<GpuAdapterInfo> AdaptersSnapshot()
        {
            long now = Environment.TickCount64;
            lock (_adaptersGate)
            {
                if (now - _adaptersTick > 30000)
                {
                    _adapters = EnumerateAdapters();
                    _adaptersTick = now;
                }
                return _adapters.ConvertAll(adapter => new GpuAdapterInfo
                {
                    Name = adapter.Name, Luid = adapter.Luid, LuidValue = adapter.LuidValue,
                    VendorId = adapter.VendorId, DeviceId = adapter.DeviceId
                });
            }
        }

        /// <summary>枚举显卡(DXGI);失败返回空列表。供设置页下拉框使用。</summary>
        public static List<GpuAdapterInfo> EnumerateGpus() => EnumerateAdapters();

        // ---------- 采样器 ----------

        private sealed class CpuSampler : IDisposable
        {
            private ulong _idle, _kernel, _user;
            private bool _primed;

            public void Reset() => _primed = false;

            public double Next()
            {
                if (!PerfNative.GetSystemTimes(out var idle, out var kernel, out var user)) return -1;
                ulong i = PerfNative.FileTimeValue(idle);
                ulong k = PerfNative.FileTimeValue(kernel);
                ulong u = PerfNative.FileTimeValue(user);
                if (!_primed || i < _idle || k < _kernel || u < _user)
                {
                    _idle = i; _kernel = k; _user = u;
                    _primed = true;
                    return -1;
                }
                ulong di = i - _idle, dk = k - _kernel, du = u - _user;
                _idle = i; _kernel = k; _user = u;
                ulong total = dk + du; // kernel 时间已含 idle,不再重复计入
                if (total == 0) return -1;
                return di >= total ? 0 : 100.0 * (total - di) / total;
            }

            public void Dispose() { }
        }

        private sealed class MemorySampler : IDisposable
        {
            public (ulong total, ulong used) Next()
            {
                if (!PerfNative.TryGetMemoryStatus(out ulong total, out ulong avail)) return (0, 0);
                return (total, total > avail ? total - avail : 0);
            }

            public void Dispose() { }
        }

        /// <summary>GPU 引擎 + 网络接口 + CPU 频率的 PDH 查询:通配符计数器一次覆盖所有实例,单实例计数器单独读取。</summary>
        private sealed class PdhHost : IDisposable
        {
            private IntPtr _query = IntPtr.Zero;
            private IntPtr _gpuUtil = IntPtr.Zero;
            private IntPtr _netRecv = IntPtr.Zero;
            private IntPtr _netSent = IntPtr.Zero;
            private IntPtr _cpuPerf = IntPtr.Zero;
            private IntPtr _counterBuffer;
            private int _counterBufferSize;
            private bool _primed;

            public bool GpuAvailable => _gpuUtil != IntPtr.Zero;
            public bool NetAvailable => _netRecv != IntPtr.Zero && _netSent != IntPtr.Zero;
            public bool CpuPerfAvailable => _cpuPerf != IntPtr.Zero;

            public PdhHost(PerfComponents components)
            {
                try
                {
                    if ((components & ~PerfComponents.Memory) == PerfComponents.None) return;
                    if (PerfNative.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
                    if ((components & PerfComponents.Gpu) != 0)
                        _gpuUtil = Add(@"\GPU Engine(*)\Utilization Percentage");
                    if ((components & PerfComponents.Network) != 0)
                    {
                        _netRecv = Add(@"\Network Interface(*)\Bytes Received/sec");
                        _netSent = Add(@"\Network Interface(*)\Bytes Sent/sec");
                    }
                    if ((components & PerfComponents.Cpu) != 0)
                        _cpuPerf = Add(@"\Processor Information(_Total)\% Processor Performance");
                    if (_query == IntPtr.Zero) return;
                    // 速率类计数器需要两次采集才有有效数据。
                    PerfNative.PdhCollectQueryData(_query);
                    PerfNative.PdhCollectQueryData(_query);
                    _primed = true;
                }
                catch (Exception ex)
                {
                    App.Log("性能计数器初始化失败: " + ex.Message);
                    _gpuUtil = _netRecv = _netSent = _cpuPerf = IntPtr.Zero;
                }
            }

            private IntPtr Add(string path) => PerfNative.PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == 0
                ? counter : IntPtr.Zero;

            public void Collect()
            {
                if (!_primed) return;
                PerfNative.PdhCollectQueryData(_query);
            }

            /// <summary>读取 % Processor Performance(%,基准频率的百分比);失败返回 -1。</summary>
            public double ReadCpuPerf()
            {
                if (_cpuPerf == IntPtr.Zero) return -1;
                if (PerfNative.PdhGetFormattedCounterValue(_cpuPerf, PerfNative.PDH_FMT_DOUBLE,
                        IntPtr.Zero, out var value) != 0 || value.CStatus != 0)
                    return -1;
                double v = value.DoubleValue;
                return v >= 0 && v < 10000 ? v : -1;
            }

            /// <summary>按 LUID 聚合 GPU 引擎占用率;无实例/读取失败视为 0%。</summary>
            public void UpdateGpuUsage(List<GpuAdapterInfo> adapters)
            {
                foreach (var gpu in adapters) gpu.UtilPercent = 0;
                if (!ReadArray(_gpuUtil, out int count)) return;
                for (int i = 0; i < count; i++)
                {
                    if (!ReadItem(i, out IntPtr name, out double value)
                        || !GpuLuid.TryParse(Marshal.PtrToStringUni(name), out ulong luid)) continue;
                    foreach (var gpu in adapters)
                    {
                        if (gpu.LuidValue != luid) continue;
                        gpu.UtilPercent += value;
                    }
                }
                foreach (var gpu in adapters) gpu.UtilPercent = Math.Min(100, gpu.UtilPercent);
            }

            /// <summary>聚合所有网络接口的收发字节速率;读取失败返回 (-1, -1)。</summary>
            public (double up, double down) ReadNetTotals()
            {
                if (TryReadTotal(_netRecv, out double down) && TryReadTotal(_netSent, out double up))
                    return (up, down);
                return (-1, -1);
            }

            private bool TryReadTotal(IntPtr counter, out double total)
            {
                total = 0;
                if (!ReadArray(counter, out int count)) return false;
                for (int i = 0; i < count; i++)
                    if (ReadItem(i, out _, out double value)) total += value;
                return true;
            }

            // PDH_FMT_COUNTERVALUE_ITEM_W(x64): { LPWSTR szName; PDH_FMT_COUNTERVALUE(16B) } = 24B,
            // 其中 PDH_FMT_COUNTERVALUE = { DWORD CStatus; double 值(union) }。
            private const int ItemSize = 24;
            private const int MaxBufferSize = 16 * 1024 * 1024;

            private bool ReadArray(IntPtr counter, out int itemCount)
            {
                itemCount = 0;
                if (counter == IntPtr.Zero) return false;
                try
                {
                    int size = _counterBufferSize;
                    int status = PerfNative.PdhGetFormattedCounterArrayW(counter, PerfNative.PDH_FMT_DOUBLE,
                        ref size, ref itemCount, _counterBuffer);
                    if (status == PerfNative.PDH_MORE_DATA)
                    {
                        // 从空缓冲询问尺寸，PDH 对非空但太小的缓冲返回的尺寸不保证有效。
                        if (_counterBuffer != IntPtr.Zero)
                        {
                            size = itemCount = 0;
                            status = PerfNative.PdhGetFormattedCounterArrayW(counter, PerfNative.PDH_FMT_DOUBLE,
                                ref size, ref itemCount, IntPtr.Zero);
                            if (status != PerfNative.PDH_MORE_DATA) return false;
                        }
                        if (size <= 0 || size > MaxBufferSize) return false;
                        size = Math.Max(4096, size);
                        IntPtr bigger = Marshal.AllocHGlobal(size);
                        Marshal.FreeHGlobal(_counterBuffer);
                        _counterBuffer = bigger;
                        _counterBufferSize = size;
                        status = PerfNative.PdhGetFormattedCounterArrayW(counter, PerfNative.PDH_FMT_DOUBLE,
                            ref size, ref itemCount, _counterBuffer);
                    }
                    return status == 0 && itemCount >= 0 && itemCount <= _counterBufferSize / ItemSize;
                }
                catch
                {
                    return false;
                }
            }

            private bool ReadItem(int index, out IntPtr name, out double value)
            {
                IntPtr item = _counterBuffer + index * ItemSize;
                name = Marshal.ReadIntPtr(item);
                value = 0;
                if (name == IntPtr.Zero || Marshal.ReadInt32(item, 8) != 0) return false;
                value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                return true;
            }

            public void Dispose()
            {
                Marshal.FreeHGlobal(_counterBuffer);
                _counterBuffer = IntPtr.Zero;
                _counterBufferSize = 0;
                if (_query != IntPtr.Zero)
                {
                    try { PerfNative.PdhCloseQuery(_query); } catch { }
                    _query = IntPtr.Zero;
                }
            }
        }

        /// <summary>从 GPU 引擎实例的名字读取 LUID 的高低两段。</summary>
        internal static class GpuLuid
        {
            public static bool TryParse(string instanceName, out ulong value)
            {
                value = 0;
                const string prefix = "luid_0x";
                int start = instanceName?.IndexOf(prefix, StringComparison.Ordinal) ?? -1;
                if (start < 0) return false;
                var parts = instanceName.AsSpan(start + prefix.Length);
                if (parts.Length < 19 || !parts.Slice(8, 3).SequenceEqual("_0x")
                    || !uint.TryParse(parts[..8], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint high)
                    || !uint.TryParse(parts.Slice(11, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint low))
                    return false;
                value = ((ulong)high << 32) | low;
                return true;
            }
        }

        // ---------- 扩展遥测提供方(悬停展开页数据) ----------

        private static bool? _cpuIsAmd;

        /// <summary>CPU 是否 AMD(注册表 VendorIdentifier);决定设置页是否展示原生 Tctl 安装入口。</summary>
        public static bool CpuIsAmd
        {
            get
            {
                if (_cpuIsAmd.HasValue) return _cpuIsAmd.Value;
                bool amd = false;
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    amd = string.Equals(key?.GetValue("VendorIdentifier") as string, "AuthenticAMD", StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                _cpuIsAmd = amd;
                return amd;
            }
        }

        /// <summary>CPU 基准频率(MHz,注册表 HARDWARE\DESCRIPTION\System\CentralProcessor\0 的 ~MHz)。</summary>
        private static double ReadCpuBaseMhz()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return key == null ? 0 : Convert.ToDouble(key.GetValue("~MHz", 0.0) ?? 0.0);
            }
            catch { return 0; }
        }

        /// <summary>显卡名归一化:跨 DXGI/ADL/NVML 来源匹配用(各家命名带不同前缀)。</summary>
        internal static string NormalizeGpuName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name.ToLowerInvariant())
            {
                if (char.IsWhiteSpace(ch) || ch == '(' || ch == ')') continue;
                sb.Append(ch);
            }
            string s = sb.ToString()
                .Replace("amd", "").Replace("radeon", "")
                .Replace("nvidia", "").Replace("geforce", "")
                .Replace("(tm)", "").Replace("(r)", "").Replace("tm", "");
            return s;
        }

        /// <summary>CPU 温度采样:ACPI 热区(root\WMI MSAcpi_ThermalZoneTemperature,单位 0.1 K)。
        /// 多数台式机无热区或拒绝标准用户读取,失败静默返回 -1(后续还有 ADL SoC 传感器兜底)。每 5 秒查询一次。</summary>
        private sealed class CpuTempSampler
        {
            private double _last = -1;
            private long _nextTick;

            public double Next()
            {
                long now = Environment.TickCount64;
                if (now >= _nextTick)
                {
                    _nextTick = now + 5000;
                    double v = WmiReader.ReadCpuTempC();
                    if (v >= 0) _last = v;
                }
                return _last;
            }
        }

        /// <summary>轻量 WMI 读取(SWbemLocator 动态 COM,不引 System.Management 包)。
        /// 仅采样线程调用;SWbemObject 的属性访问在个别对象上会抛 0x80004005,逐项容错取第一个有效值。</summary>
        private static class WmiReader
        {
            private static object _rootWmi;
            private static object _cimv2;
            private static double _memSpeed;
            private static bool _memSpeedDone;

            private static dynamic Connect(ref object cached, string ns)
            {
                if (cached != null) return cached;
                dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator"));
                cached = locator.ConnectServer(".", ns);
                return cached;
            }

            public static double ReadCpuTempC()
            {
                try
                {
                    dynamic svc = Connect(ref _rootWmi, @"root\WMI");
                    dynamic set = svc.ExecQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                    double best = -1;
                    foreach (object item in set)
                    {
                        if (item == null) continue;
                        try
                        {
                            dynamic o = item;
                            double c = Convert.ToDouble(o.CurrentTemperature) / 10.0 - 273.15;
                            // 取最高的热区温度(最接近芯片实际温度),过滤明显无效的值。
                            if (c > best && c < 150) best = c;
                        }
                        catch { }
                    }
                    return best;
                }
                catch (Exception)
                {
                    return -1;
                }
            }

            public static double ReadMemSpeedMts()
            {
                if (_memSpeedDone) return _memSpeed;
                _memSpeedDone = true;
                try
                {
                    dynamic svc = Connect(ref _cimv2, @"root\CIMV2");
                    dynamic set = svc.ExecQuery("SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
                    double best = -1;
                    foreach (object item in set)
                    {
                        if (item == null) continue;
                        try
                        {
                            dynamic o = item;
                            double v = Convert.ToDouble(o.ConfiguredClockSpeed);
                            if (v <= 0) v = Convert.ToDouble(o.Speed); // 部分内存条不报配置频率,退回标称
                            if (v > best && v < 20000) best = v;
                        }
                        catch { }
                    }
                    _memSpeed = best;
                }
                catch (Exception ex)
                {
                    App.Log("内存频率查询失败: " + ex.Message);
                }
                return _memSpeed;
            }
        }

        /// <summary>AMD ADL 遥测(atiadlxx.dll,Adrenalin 驱动自带):PMLog 一次性查询提供
        /// 核心温度(边缘温度,缺失时退回 GFX 温度)与当前图形频率。无需管理员;
        /// DLL 或导出缺失、非 AMD 平台时静默不可用。同一物理卡的多个显示克隆按 PCI DeviceId 去重。</summary>
        private sealed class AdlHost : IDisposable
        {
            private delegate IntPtr AdlAlloc(int size);
            private static readonly AdlAlloc Alloc = size => Marshal.AllocHGlobal(size);

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern int ADL2_Main_Control_Create(AdlAlloc callback, int connectedAdapters, ref IntPtr context);
            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern int ADL2_Main_Control_Destroy(IntPtr context);
            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, ref int num);
            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr info, int size);
            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern int ADL2_New_QueryPMLogData_Get(IntPtr context, int adapterIndex, ref PMLogDataOutput output);

            private const int AdlMaxPath = 256;
            private const int PmLogMaxSensors = 256;
            // ADLPMLogSensors 常量(LibreHardwareMonitor 同源):CLK_GFXCLK=1, TEMPERATURE_EDGE=8,
            // ASIC_POWER=23, TEMPERATURE_SOC=29, TEMPERATURE_CPU=32。
            private const int SensorClkGfx = 1;
            private const int SensorTempEdge = 8;
            private const int SensorAsicPower = 23;
            private const int SensorTempSoc = 29;
            private const int SensorTempCpu = 32;
            private const int SensorTempGfx = 28;

            [StructLayout(LayoutKind.Sequential)]
            private struct SensorData
            {
                public int Supported;
                public int Value;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct PMLogDataOutput
            {
                public int Size;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = PmLogMaxSensors)]
                public SensorData[] Sensors;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            private struct AdapterInfo
            {
                public int Size;
                public int AdapterIndex;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string Udid;
                public int BusNumber;
                public int DeviceNumber;
                public int FunctionNumber;
                public int VendorId;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string AdapterName;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string DisplayName;
                public int Present;
                public int Exist;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string DriverPath;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string DriverPathExt;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string PnpString;
                public int OsDisplayIndex;
            }

            private IntPtr _context;
            private readonly Dictionary<int, int> _adapterIndexByDeviceId = new Dictionary<int, int>();

            public static AdlHost TryCreate()
            {
                try
                {
                    var host = new AdlHost();
                    if (!host.Init())
                    {
                        // Init 半途失败时 context 可能已创建,必须 Destroy,否则每次重试泄漏一个。
                        host.Dispose();
                        App.Log("ADL 初始化失败(稍后 60 秒重试)");
                        return null;
                    }
                    return host;
                }
                catch (Exception ex)
                {
                    App.Log("ADL 初始化失败: " + ex.Message);
                    return null;
                }
            }

            private bool Init()
            {
                int rc = ADL2_Main_Control_Create(Alloc, 0, ref _context);
                if ((rc != 0 && rc != 4) || _context == IntPtr.Zero) return false; // 4 = ADL_OK_WAIT
                int num = 0;
                if (ADL2_Adapter_NumberOfAdapters_Get(_context, ref num) != 0 || num <= 0) return false;
                int esz = Marshal.SizeOf<AdapterInfo>();
                IntPtr buf = Marshal.AllocHGlobal(esz * num);
                try
                {
                    if (ADL2_Adapter_AdapterInfo_Get(_context, buf, esz * num) != 0) return false;
                    for (int i = 0; i < num; i++)
                    {
                        var a = Marshal.PtrToStructure<AdapterInfo>((IntPtr)((long)buf + i * esz));
                        // ADL 在 Windows 上报告的 VendorId 字段不可靠,从 UDID 解析 PCI 厂商/设备 ID。
                        var m = Regex.Match(a.Udid ?? "", @"PCI_VEN_([0-9A-Fa-f]{1,4})&DEV_([0-9A-Fa-f]{1,4})");
                        if (!m.Success) continue;
                        if (Convert.ToInt32(m.Groups[1].Value, 16) != 0x1002) continue;
                        int device = Convert.ToInt32(m.Groups[2].Value, 16);
                        if (!_adapterIndexByDeviceId.ContainsKey(device))
                            _adapterIndexByDeviceId[device] = i;
                    }
                    return _adapterIndexByDeviceId.Count > 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }

            /// <summary>为 AMD 显卡填充核心温度(°C)与当前图形频率(MHz),并返回 CPU 温度候选:
            /// cpuTempC = 核显/CPU 侧适配器上的 TEMPERATURE_CPU(32,APU 真 CPU 温度),
            /// socTempC = TEMPERATURE_SOC(29,桌面 AM5 上是 CPU 封装内 IO-die 传感器,读数偏低属正常)。
            /// 最终采用哪一级由调用方按优先级链决定。查询失败返回 -1。</summary>
            public (double cpuTempC, double socTempC) Poll(List<GpuAdapterInfo> adapters)
            {
                if (_context == IntPtr.Zero) return (-1, -1);
                double cpuTempBest = -1;      // TEMPERATURE_CPU
                double socTempBest = -1;      // TEMPERATURE_SOC(仅 CPU 侧适配器)
                foreach (var gpu in adapters)
                {
                    if (gpu.VendorId != 0x1002 || !_adapterIndexByDeviceId.TryGetValue(gpu.DeviceId, out int idx))
                        continue;
                    var od = new PMLogDataOutput();
                    int rc = ADL2_New_QueryPMLogData_Get(_context, idx, ref od);
                    if ((rc != 0 && rc != 4) || od.Sensors == null) continue;
                    double temp = -1;
                    if (SensorOk(od, SensorTempEdge)) temp = od.Sensors[SensorTempEdge].Value;
                    else if (SensorOk(od, SensorTempGfx)) temp = od.Sensors[SensorTempGfx].Value;
                    if (temp >= 0 && temp < 150) gpu.TempC = temp;
                    if (SensorOk(od, SensorClkGfx))
                    {
                        double clock = od.Sensors[SensorClkGfx].Value;
                        if (clock > 0 && clock < 10000) gpu.ClockMhz = clock;
                    }
                    // CPU 温度候选只取"Radeon(TM) Graphics"这类核显适配器(即 CPU 自带的 SMU),
                    // 且要求同时上报 ASIC_POWER(区分 APU 适配器与同名独显),避免把独显 SoC 温度当 CPU。
                    if (!NormalizeGpuName(gpu.Name).Contains("graphics")) continue;
                    if (SensorOk(od, SensorTempCpu) && ValidTemp(od.Sensors[SensorTempCpu].Value))
                        cpuTempBest = od.Sensors[SensorTempCpu].Value;
                    else if (socTempBest < 0 && SensorOk(od, SensorTempSoc) && SensorOk(od, SensorAsicPower)
                             && ValidTemp(od.Sensors[SensorTempSoc].Value))
                        socTempBest = od.Sensors[SensorTempSoc].Value;
                }
                return (cpuTempBest, socTempBest);
            }

            private static bool ValidTemp(double c) => c >= 0 && c < 150;

            private static bool SensorOk(PMLogDataOutput od, int id)
                => od.Sensors != null && id < od.Sensors.Length && od.Sensors[id].Supported != 0;

            public void Dispose()
            {
                if (_context != IntPtr.Zero)
                {
                    try { ADL2_Main_Control_Destroy(_context); } catch { }
                    _context = IntPtr.Zero;
                }
            }
        }

        /// <summary>NVIDIA NVML 遥测(nvml.dll,随 NVIDIA 驱动分发):核心温度与当前图形频率。
        /// DLL 不在(未装 NVIDIA 驱动的机器)或初始化失败时静默不可用。</summary>
        private sealed class NvmlHost : IDisposable
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int NvmlVoidFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int NvmlCountFn(out uint count);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int NvmlHandleFn(uint index, out IntPtr device);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int NvmlNameFn(IntPtr device, byte[] name, uint length);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int NvmlValueFn(IntPtr device, uint type, out uint value);

            private const uint NvmlTemperatureGpu = 0;  // nvmlTemperatureSensors_t.NVML_TEMPERATURE_GPU
            private const uint NvmlClockGraphics = 0;   // nvmlClockType_t.NVML_CLOCK_GRAPHICS

            private readonly IntPtr _lib;
            private readonly NvmlValueFn _getTemperature;
            private readonly NvmlValueFn _getClock;
            private readonly NvmlVoidFn _shutdown;
            private readonly List<IntPtr> _devices = new List<IntPtr>();
            private readonly List<string> _names = new List<string>();

            private NvmlHost(IntPtr lib)
            {
                _lib = lib;
                _shutdown = GetExport<NvmlVoidFn>("nvmlShutdown");
                _getTemperature = GetExport<NvmlValueFn>("nvmlDeviceGetTemperature");
                _getClock = GetExport<NvmlValueFn>("nvmlDeviceGetClockInfo");
            }

            public static NvmlHost TryCreate()
            {
                IntPtr lib = IntPtr.Zero;
                try
                {
                    // 新版驱动只在 System32/PATH,旧版在 NVSMI 目录,两处都试。
                    string nvsmi = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                        + @"\NVIDIA Corporation\NVSMI\nvml.dll";
                    if (!NativeLibrary.TryLoad("nvml.dll", out lib) && !NativeLibrary.TryLoad(nvsmi, out lib))
                        return null;
                    var host = new NvmlHost(lib);
                    if (!host.Init())
                    {
                        // Init 失败同样要 Shutdown(若已 Init)+ Free,否则句柄/上下文随重试累积。
                        host.Dispose();
                        App.Log("NVML 初始化失败(稍后 60 秒重试)");
                        return null;
                    }
                    return host;
                }
                catch (Exception ex)
                {
                    App.Log("NVML 初始化失败: " + ex.Message);
                    if (lib != IntPtr.Zero) { try { NativeLibrary.Free(lib); } catch { } }
                    return null;
                }
            }

            private T GetExport<T>(string name) where T : class
            {
                if (!NativeLibrary.TryGetExport(_lib, name, out IntPtr addr))
                    throw new EntryPointNotFoundException(name);
                return Marshal.GetDelegateForFunctionPointer<T>(addr);
            }

            private bool Init()
            {
                var init = GetExport<NvmlVoidFn>("nvmlInit_v2");
                var count = GetExport<NvmlCountFn>("nvmlDeviceGetCount_v2");
                var handle = GetExport<NvmlHandleFn>("nvmlDeviceGetHandleByIndex_v2");
                var name = GetExport<NvmlNameFn>("nvmlDeviceGetName");
                if (init() != 0) return false;
                if (count(out uint n) != 0 || n == 0) return false;
                var buf = new byte[96];
                for (uint i = 0; i < n; i++)
                {
                    if (handle(i, out IntPtr device) != 0 || device == IntPtr.Zero) continue;
                    if (name(device, buf, (uint)buf.Length) != 0) continue;
                    int len = Array.IndexOf<byte>(buf, 0);
                    if (len < 0) len = buf.Length;
                    _devices.Add(device);
                    _names.Add(Encoding.UTF8.GetString(buf, 0, len));
                }
                return _devices.Count > 0;
            }

            /// <summary>为 NVIDIA 显卡填充核心温度(°C)与当前图形频率(MHz);失败保持 -1。</summary>
            public void Poll(List<GpuAdapterInfo> gpus)
            {
                var nvidia = gpus.FindAll(g => g.VendorId == 0x10DE);
                if (nvidia.Count == 0 || _devices.Count == 0) return;
                for (int d = 0; d < _devices.Count; d++)
                {
                    double temp = _getTemperature(_devices[d], NvmlTemperatureGpu, out uint t) == 0 ? t : -1;
                    double clock = _getClock(_devices[d], NvmlClockGraphics, out uint c) == 0 ? c : -1;
                    // 名称归一化后匹配;只有一个 NVML 设备且只有一个 NVIDIA 适配器时直接配对。
                    int matched = -1;
                    for (int j = 0; j < nvidia.Count; j++)
                        if (string.Equals(_names[d], NormalizeGpuName(nvidia[j].Name), StringComparison.Ordinal))
                        {
                            matched = j;
                            break;
                        }
                    if (matched < 0 && _devices.Count == 1 && nvidia.Count == 1) matched = 0;
                    if (matched < 0) continue;
                    if (temp >= 0 && temp < 150) nvidia[matched].TempC = temp;
                    if (clock > 0 && clock < 10000) nvidia[matched].ClockMhz = clock;
                }
            }

            public void Dispose()
            {
                try { _shutdown?.Invoke(); } catch { }
                try { NativeLibrary.Free(_lib); } catch { }
            }
        }

        /// <summary>枚举物理显卡(过滤 WARP 软件渲染与同名驱动节点重复项)。实测部分系统上
        /// EnumAdapters 返回的适配器不支持 QI IDXGIAdapter1,故走经典 IDXGIAdapter + GetDesc 路径。</summary>
        internal static List<GpuAdapterInfo> EnumerateAdapters()
        {
            var list = new List<GpuAdapterInfo>();
            try
            {
                Guid iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
                if (PerfNative.CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == null)
                    return list;
                try
                {
                    uint index = 0;
                    while (factory.EnumAdapters(index++, out var adapter) == 0)
                    {
                        if (adapter == null) break;
                        try
                        {
                            if (adapter.GetDesc(out var desc) != 0) continue;
                            string name = desc.Description?.Trim();
                            // "Microsoft Basic Render Driver" 是 WARP 软件渲染,不计入显卡列表。
                            if (string.IsNullOrWhiteSpace(name)
                                || name.Equals("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase))
                                continue;
                            string luid = ((uint)desc.LuidHigh).ToString("X8", CultureInfo.InvariantCulture)
                                + ":" + desc.LuidLow.ToString("X8", CultureInfo.InvariantCulture);
                            // 同名驱动节点(不同 LUID)只保留第一个,下拉框不出现重复项。
                            if (list.Exists(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                            list.Add(new GpuAdapterInfo
                            {
                                Name = name,
                                Luid = luid,
                                LuidValue = ((ulong)(uint)desc.LuidHigh << 32) | desc.LuidLow,
                                UtilPercent = -1,
                                VendorId = (int)desc.VendorId,
                                DeviceId = (int)desc.DeviceId
                            });
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(adapter);
                        }
                    }
                }
                finally
                {
                    try { Marshal.ReleaseComObject(factory); } catch { }
                }
            }
            catch (Exception ex)
            {
                App.Log("枚举显卡失败: " + ex.Message);
            }
            return list;
        }
    }

    /// <summary>性能监测所需 Win32/DXGI/PDH 入口,仅服务内部使用。</summary>
    internal static class PerfNative
    {
        // ---------- CPU / 内存 ----------

        [StructLayout(LayoutKind.Sequential)]
        internal struct FILETIME
        {
            public uint LowPart;
            public uint HighPart;
        }

        internal static ulong FileTimeValue(FILETIME t) => ((ulong)t.HighPart << 32) | t.LowPart;

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

        internal static bool TryGetMemoryStatus(out ulong totalPhys, out ulong availPhys)
        {
            totalPhys = 0;
            availPhys = 0;
            try
            {
                var status = new MEMORYSTATUSEX();
                if (!GlobalMemoryStatusEx(status)) return false;
                totalPhys = status.ullTotalPhys;
                availPhys = status.ullAvailPhys;
                return totalPhys > 0;
            }
            catch { return false; }
        }

        // ---------- PDH ----------

        internal const uint PDH_FMT_DOUBLE = 0x00000200;
        internal const int PDH_MORE_DATA = unchecked((int)0x800007D2);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        internal static extern int PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        internal static extern int PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        internal static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        internal static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
            ref int bufferSize, ref int itemCount, IntPtr itemBuffer);

        [StructLayout(LayoutKind.Sequential)]
        internal struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double DoubleValue;
        }

        [DllImport("pdh.dll")]
        internal static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format,
            IntPtr counterType, out PDH_FMT_COUNTERVALUE value);

        [DllImport("pdh.dll")]
        internal static extern int PdhCloseQuery(IntPtr query);

        // ---------- DXGI 显卡枚举 ----------

        [DllImport("dxgi.dll")]
        internal static extern int CreateDXGIFactory1(ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        /// <summary>只调用 EnumAdapters/GetDesc;其余方法仅用于占住 COM 虚表槽位(含 IDXGIObject 继承的 4 个)。</summary>
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDXGIFactory1
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData(IntPtr name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(IntPtr name, IntPtr data);
            [PreserveSig] int GetPrivateData(IntPtr name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            // IDXGIFactory
            [PreserveSig] int EnumAdapters(uint index, out IDXGIFactoryAdapter adapter);
            [PreserveSig] int GetWindowAssociation(out IntPtr hwnd);
            [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
            [PreserveSig] int MakeWindowAssociation(IntPtr hwnd, uint flags);
        }

        [ComImport, Guid("2411E7E1-12AC-4CCF-BD14-9798E8534DC0")] // IDXGIAdapter
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDXGIFactoryAdapter
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData(IntPtr name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(IntPtr name, IntPtr data);
            [PreserveSig] int GetPrivateData(IntPtr name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            // IDXGIAdapter
            [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
            [PreserveSig] int GetDesc(out DXGIAdapterDesc desc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid guid, out long version);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DXGIAdapterDesc
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public ulong DedicatedVideoMemory;
            public ulong DedicatedSystemMemory;
            public ulong SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }
    }
}
