using System.Reflection;
using System.Text;
using LibreHardwareMonitor.Hardware;
using SysMon.Native;

namespace SysMon.Data;

/// <summary>
/// LibreHardwareMonitor 経由の温度・電力・ファン等。CPU 温度などは管理者権限 + PawnIO ドライバが必要。
/// Ryzen では SMU の PM テーブルから DRAM 読み書き帯域も読む（LHM 内部オブジェクトをリフレクションで利用）。
/// </summary>
internal sealed class HwSensors : IDisposable
{
    private Computer _pc;
    private object _smu;
    private MethodInfo _getPmTable;
    private int _pmReadIdx = -1, _pmWriteIdx = -1;
    private readonly Dictionary<IHardware, long> _lastUpdate = new();

    public bool IsAdmin { get; private set; }
    public bool PawnIoInstalled { get; private set; }
    public uint PmTableVersion { get; private set; }
    public string Error { get; private set; }

    public void Open()
    {
        IsAdmin = Win32.IsAdmin();
        try { PawnIoInstalled = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; } catch { PawnIoInstalled = false; }

        // 管理者でなければ GPU (NvAPI: ホットスポット温度) とストレージ温度だけ試す
        bool full = IsAdmin;
        _pc = new Computer
        {
            IsCpuEnabled = full,
            IsMotherboardEnabled = full,
            IsMemoryEnabled = full,
            IsGpuEnabled = true,
            IsStorageEnabled = true,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsPsuEnabled = false,
            IsBatteryEnabled = false,
        };
        try
        {
            _pc.Open();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        if (full) SetupPmTable();
    }

    private void SetupPmTable()
    {
        try
        {
            foreach (var hw in _pc.Hardware)
            {
                if (hw.HardwareType != HardwareType.Cpu) continue;
                var f = hw.GetType().GetField("_smu", BindingFlags.NonPublic | BindingFlags.Instance);
                var smu = f?.GetValue(hw);
                if (smu == null) continue;
                var verField = smu.GetType().GetField("_pmTableVersion", BindingFlags.NonPublic | BindingFlags.Instance);
                uint ver = verField != null ? (uint)verField.GetValue(smu) : 0;
                PmTableVersion = ver;
                // ryzen_monitor (hattedsquirrel) の PM テーブル定義: CS_UMC_READS / CS_UMC_WRITES
                (_pmReadIdx, _pmWriteIdx) = ver switch
                {
                    0x380804 or 0x380805 or 0x380904 or 0x380905 => (68, 69),   // Vermeer (Zen 3)
                    0x240803 or 0x240903 => (64, 65),                            // Matisse (Zen 2)
                    0x400005 => (190, 191),                                      // Cezanne
                    _ => (-1, -1)
                };
                if (_pmReadIdx < 0) return;
                _smu = smu;
                _getPmTable = smu.GetType().GetMethod("GetPmTable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return;
            }
        }
        catch
        {
            _smu = null;
        }
    }

    public HwReadings Read()
    {
        var r = new HwReadings();
        if (_pc == null) return r;

        long now = Environment.TickCount64;
        foreach (var hw in _pc.Hardware)
        {
            // CPU・マザーボードは毎回、それ以外（GPU の NvAPI/D3D 問い合わせ、SMART など重いもの）は間引く
            int interval = hw.HardwareType switch
            {
                HardwareType.Cpu or HardwareType.Motherboard => 0,
                HardwareType.Storage => 10_000,
                _ => 5_000,
            };
            if (interval > 0 && _lastUpdate.TryGetValue(hw, out long last) && now - last < interval) continue;
            _lastUpdate[hw] = now;
            try
            {
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();
            }
            catch { /* 個別デバイスの失敗は無視 */ }
        }

        foreach (var hw in _pc.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu: ReadCpu(hw, r); break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel: ReadGpu(hw, r); break;
                case HardwareType.Storage: ReadStorage(hw, r); break;
                case HardwareType.Memory: ReadMemory(hw, r); break;
                case HardwareType.Motherboard:
                    foreach (var sub in hw.SubHardware) ReadBoard(sub, r);
                    break;
            }
        }

        if (_smu != null && _getPmTable != null)
        {
            try
            {
                if (_getPmTable.Invoke(_smu, null) is float[] t && t.Length > Math.Max(_pmReadIdx, _pmWriteIdx))
                {
                    float rd = t[_pmReadIdx], wr = t[_pmWriteIdx];
                    if (rd >= 0 && rd < 1000) r.DramReadGBs = rd;
                    if (wr >= 0 && wr < 1000) r.DramWriteGBs = wr;
                }
            }
            catch { }
        }
        return r;
    }

    private static float V(ISensor s) => s.Value ?? float.NaN;

    private static void ReadCpu(IHardware hw, HwReadings r)
    {
        var clocks = new SortedDictionary<int, float>();
        float tctl = float.NaN, tdie = float.NaN, pkgTemp = float.NaN, coreMax = float.NaN;
        foreach (var s in hw.Sensors)
        {
            string n = s.Name;
            float v = V(s);
            switch (s.SensorType)
            {
                case SensorType.Temperature:
                    if (n == "Core (Tctl/Tdie)" || n == "Core (Tctl)") tctl = v;
                    else if (n == "Core (Tdie)") tdie = v;
                    else if (n == "CPU Package" || n == "Package") pkgTemp = v;
                    else if (n == "Core Max") coreMax = v;
                    else if (n.StartsWith("CCD") && !n.Contains("Max") && !n.Contains("Average")) r.CcdTemps.Add(new SensorReading(n.Replace(" (Tdie)", ""), v));
                    else if (n == "SoC") r.SocTemp = v;
                    break;
                case SensorType.Power:
                    if (n == "Package" || n == "CPU Package") r.CpuPower = v;
                    break;
                case SensorType.Voltage:
                    if (n.StartsWith("Core (SVI") || n == "CPU Core" || n == "Core") r.CpuVoltage = v;
                    break;
                case SensorType.Clock:
                    if (n == "Cores (Average Effective)") r.CpuEffClock = v;
                    else if (n.StartsWith("Core #") && !n.Contains('(') && int.TryParse(n.AsSpan(6), out int ci)) clocks[ci] = v;
                    else if (n == "Fabric") r.FabricClock = v;
                    else if (n == "Uncore") r.UncoreClock = v;
                    else if (n == "Memory") r.MemClock = v;
                    break;
            }
        }
        r.CpuTemp = !float.IsNaN(tctl) ? tctl : !float.IsNaN(tdie) ? tdie : !float.IsNaN(pkgTemp) ? pkgTemp : coreMax;
        r.CoreClocks = clocks.Values.ToArray();
        if (r.CoreClocks.Length > 0) r.CpuMaxClock = r.CoreClocks.Where(x => !float.IsNaN(x)).DefaultIfEmpty(float.NaN).Max();
    }

    private static void ReadGpu(IHardware hw, HwReadings r)
    {
        // GPU ごとに別々に持つ（以前は 1 つの値を全 GPU で共有していて、最後に読んだ GPU の値が全タイルに出ていた）
        var g = new GpuHw { Name = hw.Name };
        foreach (var s in hw.Sensors)
        {
            float v = V(s);
            if (s.SensorType == SensorType.Fan && !float.IsNaN(v) && (float.IsNaN(g.FanRpm) || v > g.FanRpm)) g.FanRpm = v;
            if (s.SensorType != SensorType.Temperature) continue;
            if (s.Name == "GPU Hot Spot") g.Hotspot = v;
            else if (s.Name == "GPU Memory Junction") g.MemJunction = v;
        }
        r.Gpus.Add(g);
    }

    private static void ReadStorage(IHardware hw, HwReadings r)
    {
        foreach (var s in hw.Sensors)
        {
            float v = V(s);
            if (s.SensorType == SensorType.Temperature && !r.DiskTemps.ContainsKey(hw.Name) && v > 0 && v < 150)
                r.DiskTemps[hw.Name] = v;
            else if (s.SensorType == SensorType.Level && s.Name == "Percentage Used" && !float.IsNaN(v))
                r.DiskLife[hw.Name] = Math.Max(0, 100 - v);
            else if (s.SensorType == SensorType.Level && (s.Name == "Remaining Life" || s.Name == "Life") && !float.IsNaN(v))
                r.DiskLife[hw.Name] = v;
            else if (s.SensorType == SensorType.Data && s.Name == "Data Written" && !float.IsNaN(v))
                r.DiskWrittenGB[hw.Name] = v;
            else if (s.SensorType == SensorType.Factor && s.Name == "Power On Hours" && !float.IsNaN(v))
                r.DiskHours[hw.Name] = v;
        }
    }

    private static void ReadMemory(IHardware hw, HwReadings r)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType == SensorType.Temperature && V(s) > 0)
                r.DimmTemps.Add(new SensorReading(hw.Name, V(s)));
        }
        foreach (var sub in hw.SubHardware) ReadMemory(sub, r);
    }

    private static void ReadBoard(IHardware sub, HwReadings r)
    {
        r.BoardName ??= sub.Name;
        var controls = new Dictionary<string, float>();
        foreach (var s in sub.Sensors)
            if (s.SensorType == SensorType.Control && s.Value is float cv) controls[s.Name] = cv;

        foreach (var s in sub.Sensors)
        {
            float v = V(s);
            if (float.IsNaN(v)) continue;
            switch (s.SensorType)
            {
                case SensorType.Temperature:
                    if (v > 0 && v < 125) r.BoardTemps.Add(new SensorReading(JpName(s.Name), v));
                    break;
                case SensorType.Fan:
                    if (v > 0) r.Fans.Add(new SensorReading(JpName(s.Name), v) { Extra = controls.GetValueOrDefault(s.Name, float.NaN) });
                    break;
                case SensorType.Voltage:
                    // 名前の付いていない "Voltage #n" は何の電圧か分からないので出さない
                    if (!s.Name.StartsWith("Voltage #") && v > 0) r.Voltages.Add(new SensorReading(JpName(s.Name), v));
                    break;
            }
        }
        foreach (var s in sub.SubHardware) ReadBoard(s, r);
    }

    private static string JpName(string n) => n
        .Replace("Temperature #", "温度 #")
        .Replace("Fan #", "ファン #")
        .Replace("CMOS Battery", "CMOS 電池")
        .Replace("CPU Termination", "CPU 終端")
        .Replace("+3V Standby", "+3V スタンバイ");

    /// <summary>診断用: LHM が見えている全センサーをテキストで返す。</summary>
    public string Dump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"admin={IsAdmin} pawnio={PawnIoInstalled} pmtable=0x{PmTableVersion:X} umcIdx={_pmReadIdx}/{_pmWriteIdx} err={Error}");
        if (_pc == null) return sb.ToString();
        void Walk(IHardware hw, string indent)
        {
            sb.AppendLine($"{indent}[{hw.HardwareType}] {hw.Name}");
            foreach (var s in hw.Sensors) sb.AppendLine($"{indent}  {s.SensorType,-12} {s.Name,-32} {s.Value}");
            foreach (var sub in hw.SubHardware) Walk(sub, indent + "  ");
        }
        foreach (var hw in _pc.Hardware) Walk(hw, "");
        var pm = Read();
        sb.AppendLine($"DRAM read={pm.DramReadGBs} write={pm.DramWriteGBs}");
        return sb.ToString();
    }

    public void Dispose()
    {
        try { _pc?.Close(); } catch { }
        _pc = null;
    }
}
