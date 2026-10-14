using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SysMon.Native;

namespace SysMon.Data;

/// <summary>
/// 専用スレッドで定期的に全データを集め、Snapshot をコールバックで渡す。
/// 取得元: PDH (CPU/メモリ/ディスク/GPUエンジン), NVML (NVIDIA GPU), IP Helper (ネット),
/// NtQuerySystemInformation (プロセス), LibreHardwareMonitor (温度など)。
/// </summary>
internal sealed class Sampler : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly AutoResetEvent _wake = new(false);
    public volatile int IntervalMs = 1000;
    public event Action<Snapshot> Sampled;

    public SystemInfo Info { get; private set; }
    public HwSensors Hw { get; private set; }
    public volatile string HwState = "初期化中";

    private PdhQuery _pdh, _pdhGpu;
    private PdhCounter _cpuUtil, _cpuPerf, _cpuFreq, _cpuPriv, _intr;
    private Dictionary<(int group, int index), (int core, byte eff)> _topology;
    private readonly IntPtr _spiBuf = System.Runtime.InteropServices.Marshal.AllocHGlobal(4096);
    private long _prevCtx = -1, _prevSys = -1, _prevSpiTick;
    private PdhCounter _pageFaults, _pagesIn, _modified, _standbyCore, _standbyNormal, _standbyReserve, _freeZero, _pagedPool, _nonPagedPool;
    private PdhCounter _diskIdle, _diskSec, _diskRead, _diskWrite, _diskQueue, _diskReadOps, _diskWriteOps;
    private PdhCounter _gpuEngine, _gpuShared, _gpuDedicated;

    private readonly ProcessMonitor _procs = new();
    private readonly List<NvGpu> _nv = new();
    private HwSensors _hwReady;

    // ネット
    private NetworkInterface[] _nics = Array.Empty<NetworkInterface>();
    private long _nicRefreshTick;
    private readonly Dictionary<string, (long rx, long tx, long tick)> _netPrev = new();
    private readonly Dictionary<string, (long rx, long tx)> _netBase = new();
    private string _primaryNic;

    // ディスク温度
    private long _diskTempTick;
    private readonly Dictionary<int, float> _diskTemps = new();

    private sealed class NvGpu
    {
        public IntPtr Dev;
        public string Name;
        public int BusWidth;
        public float MaxCore, MaxMem, PowerLimit;
        public float PcieRx = float.NaN, PcieTx = float.NaN;
        public ulong Throttle;
        public float Enc = float.NaN, Dec = float.NaN, MemClock = float.NaN;
    }

    private long _tick;

    public Sampler()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Sampler", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    /// <summary>間隔変更などで次のサンプルをすぐ取りたいとき。</summary>
    public void Poke() => _wake.Set();

    private void Run()
    {
        Info = SystemInfo.Load();
        SetupPdh();
        SetupNvml();

        // LHM は初期化が数秒かかることがあるので別スレッドで
        var hwThread = new Thread(() =>
        {
            var hw = new HwSensors();
            try
            {
                hw.Open();
                Hw = hw;
                _hwReady = hw;
                HwState = !hw.IsAdmin ? "要管理者"
                        : !hw.PawnIoInstalled ? "PawnIO未導入"
                        : "OK";
            }
            catch (Exception ex)
            {
                HwState = "エラー: " + ex.Message;
            }
        }) { IsBackground = true, Name = "LHM init" };
        hwThread.Start();

        _pdh.Collect(); // レート系カウンタは 2 回目から有効
        _pdhGpu.Collect();
        _procs.Sample(null);
        var sw = Stopwatch.StartNew();
        long last = 0;
        while (!_stop)
        {
            int wait = Math.Max(10, IntervalMs - (int)(sw.ElapsedMilliseconds - last));
            _wake.WaitOne(wait);
            if (_stop) break;
            long now = sw.ElapsedMilliseconds;
            double dt = (now - last) / 1000.0;
            last = now;
            try
            {
                var snap = Collect(dt);
                Sampled?.Invoke(snap);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }

    // ------------------------------------------------------------ setup

    private void SetupPdh()
    {
        _pdh = new PdhQuery();
        _cpuUtil = _pdh.Add(@"\Processor Information(*)\% Processor Utility");
        _cpuPerf = _pdh.Add(@"\Processor Information(*)\% Processor Performance");
        _cpuFreq = _pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
        _cpuPriv = _pdh.Add(@"\Processor Information(_Total)\% Privileged Utility");
        _intr = _pdh.Add(@"\Processor Information(_Total)\Interrupts/sec");
        // \System のカウンタ（コンテキスト切替など）は収集に数 ms かかるので NtQuerySystemInformation で代替

        _pageFaults = _pdh.Add(@"\Memory\Page Faults/sec");
        _pagesIn = _pdh.Add(@"\Memory\Pages Input/sec");
        _modified = _pdh.Add(@"\Memory\Modified Page List Bytes");
        _standbyCore = _pdh.Add(@"\Memory\Standby Cache Core Bytes");
        _standbyNormal = _pdh.Add(@"\Memory\Standby Cache Normal Priority Bytes");
        _standbyReserve = _pdh.Add(@"\Memory\Standby Cache Reserve Bytes");
        _freeZero = _pdh.Add(@"\Memory\Free & Zero Page List Bytes");
        _pagedPool = _pdh.Add(@"\Memory\Pool Paged Bytes");
        _nonPagedPool = _pdh.Add(@"\Memory\Pool Nonpaged Bytes");

        _diskIdle = _pdh.Add(@"\PhysicalDisk(*)\% Idle Time");
        _diskSec = _pdh.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
        _diskRead = _pdh.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _diskWrite = _pdh.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _diskQueue = _pdh.Add(@"\PhysicalDisk(*)\Current Disk Queue Length");
        _diskReadOps = _pdh.Add(@"\PhysicalDisk(*)\Disk Reads/sec");
        _diskWriteOps = _pdh.Add(@"\PhysicalDisk(*)\Disk Writes/sec");

        // GPU エンジンのカウンタはプロセス × エンジン数だけインスタンスがあって重いので別クエリにする
        _pdhGpu = new PdhQuery();
        _gpuEngine = _pdhGpu.Add(@"\GPU Engine(*)\Utilization Percentage");
        _gpuShared = _pdhGpu.Add(@"\GPU Adapter Memory(*)\Shared Usage");
        _gpuDedicated = _pdhGpu.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
    }

    private void SetupNvml()
    {
        if (!Nvml.Init()) return;
        int n = Nvml.DeviceCount();
        for (int i = 0; i < n; i++)
        {
            var d = Nvml.Device(i);
            if (d == IntPtr.Zero) continue;
            var g = new NvGpu { Dev = d, Name = Nvml.Name(d) };
            if (Nvml.nvmlDeviceGetMemoryBusWidth(d, out uint bw) == 0) g.BusWidth = (int)bw;
            g.MaxCore = Nvml.nvmlDeviceGetMaxClockInfo(d, Nvml.NVML_CLOCK_GRAPHICS, out uint mc) == 0 ? mc : float.NaN;
            g.MaxMem = Nvml.nvmlDeviceGetMaxClockInfo(d, Nvml.NVML_CLOCK_MEM, out uint mm) == 0 ? mm : float.NaN;
            g.PowerLimit = Nvml.nvmlDeviceGetEnforcedPowerLimit(d, out uint pl) == 0 ? pl / 1000f : float.NaN;
            _nv.Add(g);
        }
    }

    // ------------------------------------------------------------ collect

    private Snapshot Collect(double dt)
    {
        _tick++;
        var s = new Snapshot { Time = DateTime.Now, IntervalSec = dt };
        _pdh.Collect();
        _pdhGpu.Collect();

        CollectCpu(s);
        CollectMemory(s);
        CollectDisks(s);
        var (engines, gpuByPid, sharedByLuid, dedicatedByLuid) = CollectGpuEngines();
        CollectGpus(s, engines, sharedByLuid, dedicatedByLuid);
        CollectNet(s, dt);

        s.Procs = _procs.Sample(gpuByPid);
        s.Processes = _procs.ProcessCount;
        s.Threads = _procs.ThreadCount;
        s.Handles = _procs.HandleCount;
        s.QueueLength = _procs.ReadyThreads;
        s.Uptime = TimeSpan.FromMilliseconds(Win32.GetTickCount64());

        var hw = _hwReady;
        if (hw != null)
        {
            try { s.Hw = hw.Read(); } catch { }
        }
        return s;
    }

    private static float F(PdhCounter c) => c == null ? float.NaN : (float)c.Value;

    private void CollectCpu(Snapshot s)
    {
        float freq = F(_cpuFreq), perf = float.NaN;
        var perfOf = new Dictionary<(int, int), float>();
        if (_cpuPerf != null)
        {
            foreach (var kv in _cpuPerf.Array())
            {
                if (kv.Key == "_Total") perf = (float)kv.Value;
                else if (ParseCpuInstance(kv.Key) is (int, int) id) perfOf[id] = (float)kv.Value;
            }
        }
        s.CpuBaseMHz = freq;
        s.CpuFreqMHz = freq * perf / 100f;

        if (_cpuUtil != null)
        {
            var list = new List<(int group, int idx, float v)>();
            foreach (var kv in _cpuUtil.Array())
            {
                float v = (float)Math.Clamp(kv.Value, 0, 100);
                if (kv.Key == "_Total") { s.CpuTotal = v; continue; }
                if (ParseCpuInstance(kv.Key) is (int g, int i)) list.Add((g, i, v));
            }
            list.Sort((a, b) => a.group != b.group ? a.group.CompareTo(b.group) : a.idx.CompareTo(b.idx));
            int n = list.Count;
            s.CpuThreads = list.Select(x => x.v).ToArray();
            s.CpuThreadMHz = list.Select(x => perfOf.TryGetValue((x.group, x.idx), out float p) ? freq * p / 100f : float.NaN).ToArray();

            // 物理コア: SMT の兄弟スレッドのうち大きい方をコアの使用率とする（どちらかが動いていればコアは埋まっている）
            _topology ??= LoadTopology();
            if (_topology.Count > 0)
            {
                int cores = _topology.Values.Max(x => x.core) + 1;
                var coreUtil = new float[cores];
                var eff = new byte[cores];
                var coreOf = new int[n];
                Array.Fill(coreUtil, float.NaN);
                for (int t = 0; t < n; t++)
                {
                    if (!_topology.TryGetValue((list[t].group, list[t].idx), out var ce)) { coreOf[t] = -1; continue; }
                    coreOf[t] = ce.core;
                    eff[ce.core] = ce.eff;
                    float v = list[t].v;
                    if (float.IsNaN(coreUtil[ce.core]) || v > coreUtil[ce.core]) coreUtil[ce.core] = v;
                }
                s.CpuCores = coreUtil;
                var valid = coreUtil.Where(v => !float.IsNaN(v)).ToArray();
                if (valid.Length > 0) s.CpuCoreAvg = valid.Average();
                s.CpuCoreEff = eff;
                s.CpuCoreOf = coreOf;
            }
        }
        s.CpuKernel = Math.Clamp(F(_cpuPriv), 0, 100);
        s.Interrupts = F(_intr);

        if (Win32.NtQuerySystemInformation(Win32.SystemPerformanceInformation, _spiBuf, 4096, out int len) == 0 && len >= Win32.SpiSystemCalls + 4)
        {
            long ctx = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(_spiBuf, Win32.SpiContextSwitches);
            long sys = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(_spiBuf, Win32.SpiSystemCalls);
            long now = Environment.TickCount64;
            if (_prevCtx >= 0 && now > _prevSpiTick)
            {
                double sec = (now - _prevSpiTick) / 1000.0;
                // ULONG なので一周に備えて 32bit で差分を取る
                s.ContextSwitches = (float)((uint)(ctx - _prevCtx) / sec);
                s.Syscalls = (float)((uint)(sys - _prevSys) / sec);
            }
            _prevCtx = ctx; _prevSys = sys; _prevSpiTick = now;
        }
    }

    /// <summary>"0,5" → (0, 5)。"_Total" や "0,_Total" は null。</summary>
    private static (int, int)? ParseCpuInstance(string name)
    {
        int comma = name.IndexOf(',');
        if (comma < 0) return null;
        if (int.TryParse(name.AsSpan(0, comma), out int g) && int.TryParse(name.AsSpan(comma + 1), out int i)) return (g, i);
        return null;
    }

    private static Dictionary<(int, int), (int, byte)> LoadTopology()
    {
        var map = new Dictionary<(int, int), (int, byte)>();
        try
        {
            var cores = Win32.PhysicalCores();
            // Windows の論理プロセッサ順（最初のスレッドの番号順）にコア番号を振る
            cores.Sort((a, b) => a.threads.Min().CompareTo(b.threads.Min()));
            for (int c = 0; c < cores.Count; c++)
                foreach (var t in cores[c].threads) map[t] = (c, cores[c].effClass);
        }
        catch { }
        return map;
    }

    private void CollectMemory(Snapshot s)
    {
        // GetPerformanceInfo は全プロセスのハンドル数を数えるので重い。GlobalMemoryStatusEx + PDH で代替
        var ms = new Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        if (Win32.GlobalMemoryStatusEx(ref ms))
        {
            s.MemTotal = ms.ullTotalPhys;
            s.MemAvail = ms.ullAvailPhys;
            s.CommitLimit = ms.ullTotalPageFile;
            s.CommitTotal = ms.ullTotalPageFile - ms.ullAvailPageFile;
        }
        s.PagedPool = (ulong)Math.Max(0, F(_pagedPool));
        s.NonPagedPool = (ulong)Math.Max(0, F(_nonPagedPool));
        s.MemInstalled = Info.MemInstalled;
        s.ModifiedBytes = F(_modified);
        s.StandbyBytes = F(_standbyCore) + F(_standbyNormal) + F(_standbyReserve);
        s.FreeBytes = F(_freeZero);
        s.PageFaults = F(_pageFaults);
        s.HardFaults = F(_pagesIn);
    }

    private void CollectDisks(Snapshot s)
    {
        if (_diskIdle == null) return;
        var map = new Dictionary<string, DiskSample>();
        DiskSample Get(string key)
        {
            if (key == "_Total") return null;
            if (!map.TryGetValue(key, out var d))
            {
                int sp = key.IndexOf(' ');
                int.TryParse(sp > 0 ? key[..sp] : key, out int idx);
                map[key] = d = new DiskSample { Key = key, Index = idx, Letters = sp > 0 ? key[(sp + 1)..] : "" };
            }
            return d;
        }
        foreach (var kv in _diskIdle.Array()) { var d = Get(kv.Key); if (d != null) d.ActivePct = (float)Math.Clamp(100 - kv.Value, 0, 100); }
        foreach (var kv in _diskSec.Array()) { var d = Get(kv.Key); if (d != null) d.ResponseMs = (float)(kv.Value * 1000); }
        foreach (var kv in _diskRead.Array()) { var d = Get(kv.Key); if (d != null) d.ReadBps = (float)kv.Value; }
        foreach (var kv in _diskWrite.Array()) { var d = Get(kv.Key); if (d != null) d.WriteBps = (float)kv.Value; }
        foreach (var kv in _diskQueue.Array()) { var d = Get(kv.Key); if (d != null) d.Queue = (float)kv.Value; }
        foreach (var kv in _diskReadOps.Array()) { var d = Get(kv.Key); if (d != null) d.ReadIops = (float)kv.Value; }
        foreach (var kv in _diskWriteOps.Array()) { var d = Get(kv.Key); if (d != null) d.WriteIops = (float)kv.Value; }
        s.Disks = map.Values.OrderBy(d => d.Letters == "" ? "~" : d.Letters).ThenBy(d => d.Index).ToList();

        // ストレージ温度: IOCTL_STORAGE_QUERY_PROPERTY は管理者不要（タスクマネージャーと同じ方法）。5 秒ごと
        long now = Environment.TickCount64;
        if (now - _diskTempTick > 5000)
        {
            _diskTempTick = now;
            foreach (var d in s.Disks) _diskTemps[d.Index] = DiskTemperature.Query(d.Index);
        }
        foreach (var d in s.Disks) d.TempC = _diskTemps.GetValueOrDefault(d.Index, float.NaN);
    }

    /// <summary>
    /// GPU Engine カウンタを集計。タスクマネージャーと同じく、エンジンごとに全プロセスを合計し、
    /// 種類ごと (3D/Copy/...) には最大のエンジンを採る。プロセス別はそのプロセスの最大エンジン。
    /// </summary>
    private (Dictionary<string, Dictionary<string, float>> byLuid, Dictionary<int, float> byPid,
             Dictionary<string, float> shared, Dictionary<string, float> dedicated) CollectGpuEngines()
    {
        var engineSum = new Dictionary<(string luid, string eng, string type), float>();
        var pidEngine = new Dictionary<(int pid, string luid, string eng), float>();
        if (_gpuEngine != null)
        {
            foreach (var kv in _gpuEngine.Array())
            {
                // pid_1234_luid_0x00000000_0x0000D1C2_phys_0_eng_3_engtype_VideoDecode
                string n = kv.Key;
                int pPid = n.IndexOf("pid_", StringComparison.Ordinal);
                int pLuid = n.IndexOf("_luid_", StringComparison.Ordinal);
                int pPhys = n.IndexOf("_phys_", StringComparison.Ordinal);
                int pEng = n.IndexOf("_eng_", StringComparison.Ordinal);
                int pType = n.IndexOf("_engtype_", StringComparison.Ordinal);
                if (pPid < 0 || pLuid < 0 || pPhys < 0 || pEng < 0 || pType < 0) continue;
                int.TryParse(n.AsSpan(pPid + 4, pLuid - pPid - 4), out int pid);
                string luid = n.Substring(pLuid + 6, pPhys - pLuid - 6).ToLowerInvariant();
                string eng = n.Substring(pPhys + 1, pType - pPhys - 1);
                string type = n[(pType + 9)..];
                float v = (float)kv.Value;
                var ek = (luid, eng, type);
                engineSum[ek] = engineSum.GetValueOrDefault(ek) + v;
                var pk = (pid, luid, eng);
                pidEngine[pk] = pidEngine.GetValueOrDefault(pk) + v;
            }
        }
        var byLuid = new Dictionary<string, Dictionary<string, float>>();
        foreach (var ((luid, _, type), v) in engineSum)
        {
            if (!byLuid.TryGetValue(luid, out var types)) byLuid[luid] = types = new Dictionary<string, float>();
            string t = NormalizeEngine(type);
            types[t] = Math.Max(types.GetValueOrDefault(t), Math.Min(100f, v));
        }
        var byPid = new Dictionary<int, float>();
        foreach (var ((pid, _, _), v) in pidEngine)
            byPid[pid] = Math.Max(byPid.GetValueOrDefault(pid), Math.Min(100f, v));

        var shared = new Dictionary<string, float>();
        var dedicated = new Dictionary<string, float>();
        if (_gpuShared != null)
            foreach (var kv in _gpuShared.Array()) shared[LuidOf(kv.Key)] = shared.GetValueOrDefault(LuidOf(kv.Key)) + (float)kv.Value;
        if (_gpuDedicated != null)
            foreach (var kv in _gpuDedicated.Array()) dedicated[LuidOf(kv.Key)] = dedicated.GetValueOrDefault(LuidOf(kv.Key)) + (float)kv.Value;
        return (byLuid, byPid, shared, dedicated);
    }

    private static string LuidOf(string instance)
    {
        // luid_0x00000000_0x0000D1C2_phys_0
        int p = instance.IndexOf("luid_", StringComparison.Ordinal);
        int q = instance.IndexOf("_phys", StringComparison.Ordinal);
        if (p < 0) return instance.ToLowerInvariant();
        return (q > p ? instance.Substring(p + 5, q - p - 5) : instance[(p + 5)..]).ToLowerInvariant();
    }

    private static string NormalizeEngine(string t)
    {
        if (t.StartsWith("Compute", StringComparison.OrdinalIgnoreCase) || t.Equals("Cuda", StringComparison.OrdinalIgnoreCase)) return "Compute";
        if (t.StartsWith("VideoDecode", StringComparison.OrdinalIgnoreCase)) return "Decode";
        if (t.StartsWith("VideoEncode", StringComparison.OrdinalIgnoreCase)) return "Encode";
        if (t.StartsWith("Copy", StringComparison.OrdinalIgnoreCase)) return "Copy";
        if (t.StartsWith("3D", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Graphics", StringComparison.OrdinalIgnoreCase)) return "3D";
        return t;
    }

    private void CollectGpus(Snapshot s, Dictionary<string, Dictionary<string, float>> engines,
                             Dictionary<string, float> shared, Dictionary<string, float> dedicated)
    {
        // Windows 側のアダプタ (LUID) と NVML デバイスの対応は、専用メモリ使用量の多い順で当てる（単一 dGPU 前提の簡易対応）
        var luids = dedicated.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();

        for (int i = 0; i < _nv.Count; i++)
        {
            var g = _nv[i];
            var d = g.Dev;
            // 電力制限は Afterburner などで実行中に変わるので定期的に読み直す（起動時の値のままだと追従しない）
            if (_tick % 2 == 1 && Nvml.nvmlDeviceGetEnforcedPowerLimit(d, out uint pl) == 0) g.PowerLimit = pl / 1000f;
            var gs = new GpuSample { Name = g.Name, FromNvml = true, BusWidth = g.BusWidth, MaxCoreMHz = g.MaxCore, MaxMemMHz = g.MaxMem, PowerLimitW = g.PowerLimit };
            if (Nvml.nvmlDeviceGetUtilizationRates(d, out var u) == 0) { gs.Util = u.gpu; gs.MemCtrlUtil = u.memory; }
            if (Nvml.nvmlDeviceGetMemoryInfo(d, out var m) == 0) { gs.VramUsed = m.used; gs.VramTotal = m.total; }
            if (Nvml.nvmlDeviceGetTemperature(d, Nvml.NVML_TEMPERATURE_GPU, out uint t) == 0) gs.Temp = t;
            if (Nvml.nvmlDeviceGetPowerUsage(d, out uint mw) == 0) gs.PowerW = mw / 1000f;
            if (Nvml.nvmlDeviceGetClockInfo(d, Nvml.NVML_CLOCK_GRAPHICS, out uint cc) == 0) gs.CoreMHz = cc;
            if ((_tick % 2 == 0 || float.IsNaN(g.MemClock)) && Nvml.nvmlDeviceGetClockInfo(d, Nvml.NVML_CLOCK_MEM, out uint mc) == 0) g.MemClock = mc;
            gs.MemMHz = g.MemClock;
            if (Nvml.nvmlDeviceGetFanSpeed(d, out uint fan) == 0) gs.FanPct = fan;
            // PCIe 帯域は NVML 内部で 20ms 間ビジーウェイトして測るので、受信と送信を交互に 4 回に 1 回だけ取る
            if (_tick % 4 == 0 && Nvml.nvmlDeviceGetPcieThroughput(d, Nvml.NVML_PCIE_UTIL_RX_BYTES, out uint rx) == 0) g.PcieRx = rx * 1024f;
            if (_tick % 4 == 2 && Nvml.nvmlDeviceGetPcieThroughput(d, Nvml.NVML_PCIE_UTIL_TX_BYTES, out uint tx) == 0) g.PcieTx = tx * 1024f;
            gs.PcieRxBps = g.PcieRx;
            gs.PcieTxBps = g.PcieTx;
            if (Nvml.nvmlDeviceGetCurrPcieLinkGeneration(d, out uint gen) == 0) gs.PcieGen = (int)gen;
            if (Nvml.nvmlDeviceGetCurrPcieLinkWidth(d, out uint wid) == 0) gs.PcieWidth = (int)wid;
            if (_tick % 2 == 0)
            {
                if (Nvml.nvmlDeviceGetEncoderUtilization(d, out uint enc, out _) == 0) g.Enc = enc;
                if (Nvml.nvmlDeviceGetDecoderUtilization(d, out uint dec, out _) == 0) g.Dec = dec;
            }
            gs.EncUtil = g.Enc;
            gs.DecUtil = g.Dec;
            if (Nvml.nvmlDeviceGetPerformanceState(d, out int ps) == 0) gs.PState = ps;
            // クロック制限理由は 1 回 15ms 以上 CPU を使うので 10 回に 1 回
            if (_tick % 10 == 1 && Nvml.nvmlDeviceGetCurrentClocksThrottleReasons(d, out ulong tr) == 0) g.Throttle = tr;
            gs.ThrottleReasons = g.Throttle;

            // VRAM 帯域の推定: メモリコントローラ使用率 × 現在のメモリクロックでの理論帯域
            // (NVML のメモリクロックは GDDR の実効転送レートの 1/2 を返すので ×2)
            if (g.BusWidth > 0 && !float.IsNaN(gs.MemMHz))
            {
                float peakNow = gs.MemMHz * 2f * g.BusWidth / 8f / 1000f;
                gs.VramBwGBs = peakNow * gs.MemCtrlUtil / 100f;
                gs.VramBwPeakGBs = float.IsNaN(g.MaxMem) ? peakNow : g.MaxMem * 2f * g.BusWidth / 8f / 1000f;
            }

            if (i < luids.Count)
            {
                if (engines.TryGetValue(luids[i], out var e)) gs.Engines = e;
                if (shared.TryGetValue(luids[i], out float sh)) gs.SharedUsed = sh;
            }
            // NVML の使用率は「カーネルが動いていた時間の割合」で、3D が張り付いていても 100% にならないことがある。
            // エンジン別（Windows）・エンコーダ・デコーダを含めて一番大きいものを GPU の使用率にする
            foreach (float v in gs.Engines.Values.Append(gs.EncUtil).Append(gs.DecUtil))
                if (!float.IsNaN(v) && (float.IsNaN(gs.Util) || v > gs.Util)) gs.Util = v;
            s.Gpus.Add(gs);
        }

        // NVIDIA 以外: Windows のカウンタだけで最低限
        if (_nv.Count == 0)
        {
            foreach (var luid in luids)
            {
                if (dedicated.GetValueOrDefault(luid) < 64 * 1024 * 1024) continue;
                var gs = new GpuSample { Name = "GPU", VramUsed = dedicated[luid] };
                if (engines.TryGetValue(luid, out var e)) { gs.Engines = e; gs.Util = e.Values.DefaultIfEmpty(0).Max(); }
                if (shared.TryGetValue(luid, out float sh)) gs.SharedUsed = sh;
                s.Gpus.Add(gs);
            }
        }
    }

    private void CollectNet(Snapshot s, double dt)
    {
        long tick = Environment.TickCount64;
        if (tick - _nicRefreshTick > 30_000 || _nics.Length == 0)
        {
            _nicRefreshTick = tick;
            try
            {
                _nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.OperationalStatus == OperationalStatus.Up && !IsPseudo(n))
                    .ToArray();
                _primaryNic = PickPrimary(_nics);
            }
            catch { }
        }
        foreach (var nic in _nics)
        {
            try
            {
                var st = nic.GetIPStatistics();
                long rx = st.BytesReceived, tx = st.BytesSent;
                var ns = new NetSample
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    LinkBps = nic.Speed,
                    Primary = nic.Id == _primaryNic,
                };
                if (_netPrev.TryGetValue(nic.Id, out var p) && tick > p.tick)
                {
                    double sec = (tick - p.tick) / 1000.0;
                    ns.RxBps = (float)(Math.Max(0, rx - p.rx) / sec);
                    ns.TxBps = (float)(Math.Max(0, tx - p.tx) / sec);
                }
                _netPrev[nic.Id] = (rx, tx, tick);
                if (!_netBase.ContainsKey(nic.Id)) _netBase[nic.Id] = (rx, tx);
                var b = _netBase[nic.Id];
                ns.RxTotal = rx - b.rx;
                ns.TxTotal = tx - b.tx;
                if (ns.Primary)
                {
                    try
                    {
                        ns.Ipv4 = nic.GetIPProperties().UnicastAddresses
                            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                    }
                    catch { }
                }
                s.Nets.Add(ns);
            }
            catch { }
        }
        s.Nets = s.Nets.OrderByDescending(n => n.Primary).ThenBy(n => n.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>フィルタードライバのバインド（"…-QoS Packet Scheduler-0000" 等）や WAN Miniport は実体ではないので除く。</summary>
    private static bool IsPseudo(NetworkInterface n)
    {
        string d = n.Description;
        if (d.Length > 5 && d[^5] == '-' && char.IsDigit(d[^1]) && char.IsDigit(d[^4])) return true;
        if (d.StartsWith("WAN Miniport", StringComparison.OrdinalIgnoreCase)) return true;
        if (d.Contains("Virtual Switch Extension Adapter", StringComparison.OrdinalIgnoreCase)) return true;
        if (d.StartsWith("Microsoft Kernel Debug", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string PickPrimary(NetworkInterface[] nics)
    {
        static bool Virtual(NetworkInterface n)
        {
            string d = n.Description.ToLower(CultureInfo.InvariantCulture);
            return d.Contains("virtual") || d.Contains("hyper-v") || d.Contains("vpn") || d.Contains("tap-") ||
                   d.Contains("tunnel") || d.Contains("bluetooth") || d.Contains("vmware") || d.Contains("virtualbox") ||
                   d.Contains("tailscale") || d.Contains("wireguard") || d.Contains("zerotier");
        }
        NetworkInterface best = null;
        foreach (var n in nics)
        {
            if (Virtual(n)) continue;
            bool gw = false;
            try { gw = n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)); } catch { }
            if (best == null || gw) { best = n; if (gw) break; }
        }
        return (best ?? nics.FirstOrDefault())?.Id;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(2000);
        _pdh?.Dispose();
        _pdhGpu?.Dispose();
        System.Runtime.InteropServices.Marshal.FreeHGlobal(_spiBuf);
        _procs.Dispose();
        Hw?.Dispose();
        Nvml.Shutdown();
    }
}
