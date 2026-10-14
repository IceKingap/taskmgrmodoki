using System.Runtime.InteropServices;
using System.Text;

namespace SysMon.Native;

/// <summary>NVIDIA ドライバ同梱の nvml.dll を直接呼ぶ。無ければ Available=false。</summary>
internal static class Nvml
{
    public static bool Available { get; private set; }

    public static bool Init()
    {
        try
        {
            Available = nvmlInit_v2() == 0;
        }
        catch (DllNotFoundException) { Available = false; }
        catch (EntryPointNotFoundException) { Available = false; }
        return Available;
    }

    public static void Shutdown()
    {
        if (!Available) return;
        try { nvmlShutdown(); } catch { }
        Available = false;
    }

    public static int DeviceCount()
    {
        return nvmlDeviceGetCount_v2(out uint n) == 0 ? (int)n : 0;
    }

    public static IntPtr Device(int index)
    {
        return nvmlDeviceGetHandleByIndex_v2((uint)index, out IntPtr d) == 0 ? d : IntPtr.Zero;
    }

    public static string Name(IntPtr d)
    {
        var buf = new byte[96];
        if (nvmlDeviceGetName(d, buf, (uint)buf.Length) != 0) return "NVIDIA GPU";
        int len = Array.IndexOf(buf, (byte)0);
        return Encoding.UTF8.GetString(buf, 0, len < 0 ? buf.Length : len);
    }

    // ---- P/Invoke ----
    public const int NVML_TEMPERATURE_GPU = 0;
    public const int NVML_CLOCK_GRAPHICS = 0, NVML_CLOCK_SM = 1, NVML_CLOCK_MEM = 2, NVML_CLOCK_VIDEO = 3;
    public const int NVML_PCIE_UTIL_TX_BYTES = 0, NVML_PCIE_UTIL_RX_BYTES = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization { public uint gpu; public uint memory; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Memory { public ulong total; public ulong free; public ulong used; }

    private const string Dll = "nvml.dll";

    [DllImport(Dll)] private static extern int nvmlInit_v2();
    [DllImport(Dll)] private static extern int nvmlShutdown();
    [DllImport(Dll)] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport(Dll)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(Dll)] private static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);

    [DllImport(Dll)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization util);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory mem);
    [DllImport(Dll)] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Dll)] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport(Dll)] public static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport(Dll)] public static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPcieThroughput(IntPtr device, int counter, out uint kbPerSec);
    [DllImport(Dll)] public static extern int nvmlDeviceGetEncoderUtilization(IntPtr device, out uint util, out uint periodUs);
    [DllImport(Dll)] public static extern int nvmlDeviceGetDecoderUtilization(IntPtr device, out uint util, out uint periodUs);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPerformanceState(IntPtr device, out int pstate);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMemoryBusWidth(IntPtr device, out uint bits);
    [DllImport(Dll)] public static extern int nvmlDeviceGetCurrPcieLinkGeneration(IntPtr device, out uint gen);
    [DllImport(Dll)] public static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr device, out uint width);
    [DllImport(Dll)] public static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
}
