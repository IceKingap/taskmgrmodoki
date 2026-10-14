using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SysMon.Native;

internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;   // = コミット上限
        public ulong ullAvailPageFile;   // = コミット上限 - コミット済み
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKb);

    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    public const int SystemPerformanceInformation = 2;
    public const int SystemProcessInformation = 5;

    // SYSTEM_PERFORMANCE_INFORMATION のオフセット（phnt / System Informer の定義。NT 以来変わっていない前半部分）
    public const int SpiContextSwitches = 296, SpiSystemCalls = 308;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    /// <summary>
    /// 物理コアの一覧。各コアに属する論理プロセッサを (グループ, グループ内番号) で返す。
    /// EfficiencyClass はハイブリッド CPU で P コアほど大きい（全コア同じなら 0）。
    /// </summary>
    public static List<(byte effClass, List<(int group, int index)> threads)> PhysicalCores()
    {
        var cores = new List<(byte, List<(int, int)>)>();
        const int RelationProcessorCore = 0;
        uint len = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
        if (len == 0) return cores;
        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return cores;
            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship(4) Size(4) + PROCESSOR_RELATIONSHIP:
            //   Flags(1) EfficiencyClass(1) Reserved(20) GroupCount(2) pad(2) GroupMask[GroupCount] (GROUP_AFFINITY = 16 bytes)
            for (int off = 0; off < len;)
            {
                IntPtr p = buf + off;
                int size = Marshal.ReadInt32(p, 4);
                if (size <= 0) break;
                if (Marshal.ReadInt32(p) == RelationProcessorCore)
                {
                    byte eff = Marshal.ReadByte(p, 9);
                    int groups = Marshal.ReadInt16(p, 30);
                    var threads = new List<(int, int)>();
                    for (int gi = 0; gi < groups; gi++)
                    {
                        IntPtr ga = p + 32 + gi * 16;
                        ulong mask = (ulong)Marshal.ReadInt64(ga);
                        int group = Marshal.ReadInt16(ga, 8);
                        for (int b = 0; b < 64; b++) if ((mask & (1UL << b)) != 0) threads.Add((group, b));
                    }
                    cores.Add((eff, threads));
                }
                off += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return cores;
    }

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
