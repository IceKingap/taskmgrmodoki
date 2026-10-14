using System.Management;

namespace SysMon.Data;

/// <summary>起動時に 1 度だけ WMI から取る静的情報（型番・メモリ規格など）。</summary>
internal sealed class SystemInfo
{
    public string CpuName = "CPU";
    public int Cores, Threads = Environment.ProcessorCount;
    public string MemType = "";
    public int MemSpeedMTs;
    public int MemModules;
    public ulong MemInstalled;
    public Dictionary<int, DiskInfo> Disks = new();

    public sealed class DiskInfo
    {
        public string Model = "";
        public string Kind = "";   // "NVMe SSD" / "SATA HDD" など
        public ulong Size;
    }

    /// <summary>DDR の理論ピーク帯域 (GB/s)。チャネル数は枚数から推定（上限 2、HEDT は考慮しない）。</summary>
    public float MemPeakGBs
    {
        get
        {
            if (MemSpeedMTs <= 0) return float.NaN;
            int channels = Math.Clamp(MemModules, 1, 2);
            return MemSpeedMTs * 8f * channels / 1000f;
        }
    }

    public static SystemInfo Load()
    {
        var si = new SystemInfo();
        Try(() =>
        {
            using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
            {
                si.CpuName = ((string)o["Name"])?.Trim() ?? "CPU";
                si.Cores += Convert.ToInt32(o["NumberOfCores"]);
                si.Threads = Convert.ToInt32(o["NumberOfLogicalProcessors"]);
            }
        });
        Try(() =>
        {
            using var s = new ManagementObjectSearcher("SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in s.Get())
            {
                si.MemModules++;
                si.MemInstalled += Convert.ToUInt64(o["Capacity"]);
                int speed = Convert.ToInt32(o["ConfiguredClockSpeed"] ?? o["Speed"] ?? 0);
                if (speed > si.MemSpeedMTs) si.MemSpeedMTs = speed;
                si.MemType = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0) switch
                {
                    20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 30 => "LPDDR4",
                    34 => "DDR5", 35 => "LPDDR5", _ => si.MemType
                };
            }
        });
        Try(() =>
        {
            using var s = new ManagementObjectSearcher("SELECT Index, Model, Size FROM Win32_DiskDrive");
            foreach (ManagementObject o in s.Get())
            {
                int idx = Convert.ToInt32(o["Index"]);
                si.Disks[idx] = new DiskInfo { Model = ((string)o["Model"])?.Trim() ?? "", Size = Convert.ToUInt64(o["Size"] ?? 0UL) };
            }
        });
        Try(() =>
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, MediaType, BusType, FriendlyName FROM MSFT_PhysicalDisk");
            foreach (ManagementObject o in s.Get())
            {
                if (!int.TryParse(Convert.ToString(o["DeviceId"]), out int idx)) continue;
                if (!si.Disks.TryGetValue(idx, out var d)) si.Disks[idx] = d = new DiskInfo();
                if (string.IsNullOrEmpty(d.Model)) d.Model = Convert.ToString(o["FriendlyName"]) ?? "";
                string bus = Convert.ToInt32(o["BusType"] ?? 0) switch
                {
                    17 => "NVMe", 11 => "SATA", 7 => "USB", 10 => "SAS", 8 => "RAID", 3 => "ATA", 15 => "仮想", _ => ""
                };
                string media = Convert.ToInt32(o["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
                if (bus == "NVMe" && media == "") media = "SSD";
                d.Kind = string.Join(" ", new[] { bus, media }.Where(x => x != ""));
            }
        });
        return si;
    }

    private static void Try(Action a)
    {
        try { a(); } catch { /* WMI が使えない環境では型番表示を諦めるだけ */ }
    }
}
