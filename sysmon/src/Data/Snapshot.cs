namespace SysMon.Data;

/// <summary>1 回のサンプリング結果。サンプラースレッドで作られ、UI スレッドへ渡される（以後不変）。</summary>
internal sealed class Snapshot
{
    public DateTime Time;
    public double IntervalSec;

    // ---- CPU ----
    public float CpuTotal = float.NaN;         // % (タスクマネージャー準拠: % Processor Utility)
    public float[] CpuThreads = System.Array.Empty<float>();
    public float[] CpuThreadMHz = System.Array.Empty<float>();   // 論理プロセッサごとの周波数 (定格 × % Processor Performance)
    public float[] CpuCores = System.Array.Empty<float>();       // 物理コアごとの使用率 = 属するスレッドの最大
    public float CpuCoreAvg = float.NaN;                         // 物理コア使用率の平均（全コアが埋まっていれば 100%）
    public int[] CpuCoreOf = System.Array.Empty<int>();          // スレッド → 物理コア番号
    public byte[] CpuCoreEff = System.Array.Empty<byte>();       // 物理コアの EfficiencyClass（ハイブリッド CPU の P/E 判定）
    public float CpuKernel = float.NaN;        // % Privileged Utility
    public float CpuFreqMHz = float.NaN;       // 定格 × % Processor Performance
    public float CpuBaseMHz = float.NaN;
    public float ContextSwitches = float.NaN;  // /s
    public float Interrupts = float.NaN;       // /s
    public float Syscalls = float.NaN;         // /s
    public float QueueLength = float.NaN;
    public int Processes, Threads, Handles;
    public TimeSpan Uptime;

    // ---- メモリ (bytes) ----
    public ulong MemTotal, MemAvail, MemInstalled;
    public ulong CommitTotal, CommitLimit;
    public ulong PagedPool, NonPagedPool;
    public float ModifiedBytes = float.NaN, StandbyBytes = float.NaN, FreeBytes = float.NaN;
    public float PageFaults = float.NaN, HardFaults = float.NaN;   // /s

    // ---- ディスク / ネット / GPU / プロセス ----
    public List<DiskSample> Disks = new();
    public List<NetSample> Nets = new();
    public List<GpuSample> Gpus = new();
    public List<ProcRow> Procs = new();

    // ---- ハードウェアセンサー (LibreHardwareMonitor) ----
    public HwReadings Hw;
}

internal sealed class DiskSample
{
    public int Index;
    public string Key;          // "1 C:" など PDH のインスタンス名
    public string Letters;      // "C:"
    public float ActivePct, ResponseMs, ReadBps, WriteBps, Queue, ReadIops, WriteIops;
    public float TempC = float.NaN;
}

internal sealed class NetSample
{
    public string Id, Name, Description, Ipv4;
    public long LinkBps;
    public float RxBps, TxBps;
    public long RxTotal, TxTotal;    // アプリ起動からの累計
    public bool Primary;
}

internal sealed class GpuSample
{
    public string Name;
    public bool FromNvml;
    public float Util = float.NaN, MemCtrlUtil = float.NaN;
    public float VramUsed = float.NaN, VramTotal = float.NaN, SharedUsed = float.NaN; // bytes
    public float Temp = float.NaN, PowerW = float.NaN, PowerLimitW = float.NaN;
    public float CoreMHz = float.NaN, MemMHz = float.NaN, MaxCoreMHz = float.NaN, MaxMemMHz = float.NaN;
    public float FanPct = float.NaN;
    public float PcieRxBps = float.NaN, PcieTxBps = float.NaN;
    public int PcieGen, PcieWidth, PState = -1, BusWidth;
    public float EncUtil = float.NaN, DecUtil = float.NaN;
    public float VramBwGBs = float.NaN, VramBwPeakGBs = float.NaN;
    public ulong ThrottleReasons;
    public Dictionary<string, float> Engines = new(); // Windows の GPU エンジン別使用率 (3D/Copy/...)
}

internal sealed class ProcRow
{
    public string Name;
    public int Count;
    public float CpuPct, GpuPct;
    public long PrivateBytes;
    public float IoBps;
}

internal sealed class SensorReading
{
    public string Name;
    public float Value;
    public float Extra = float.NaN;   // ファンなら制御 (%)
    public SensorReading(string name, float value) { Name = name; Value = value; }
}

internal sealed class GpuHw
{
    public string Name;
    public float Hotspot = float.NaN, MemJunction = float.NaN, FanRpm = float.NaN;
}

internal sealed class HwReadings
{
    public float CpuTemp = float.NaN, CpuPower = float.NaN, CpuVoltage = float.NaN, SocTemp = float.NaN;
    public float CpuMaxClock = float.NaN, CpuEffClock = float.NaN, FabricClock = float.NaN, MemClock = float.NaN, UncoreClock = float.NaN;
    public float[] CoreClocks = System.Array.Empty<float>();
    public List<SensorReading> CcdTemps = new();
    public float DramReadGBs = float.NaN, DramWriteGBs = float.NaN;
    public List<GpuHw> Gpus = new();   // LHM が見えている GPU ごと（NVML 側とは名前で対応させる）
    public Dictionary<string, float> DiskTemps = new();     // key = モデル名
    public Dictionary<string, float> DiskLife = new();      // key = モデル名, 残り寿命 %
    public Dictionary<string, float> DiskWrittenGB = new(); // key = モデル名, 総書込量 (GB)
    public Dictionary<string, float> DiskHours = new();     // key = モデル名, 使用時間 (h)
    public List<SensorReading> BoardTemps = new(), Fans = new(), Voltages = new(), DimmTemps = new();
    public string BoardName;
}
