using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysMon.Native;

/// <summary>
/// IOCTL_STORAGE_QUERY_PROPERTY (StorageDeviceTemperatureProperty) でドライブ温度を読む。
/// アクセス権 0 で開くので管理者不要。対応していないドライブ（古い SATA HDD など）は NaN。
/// </summary>
internal static class DiskTemperature
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const int StorageDeviceTemperatureProperty = 51;

    public static float Query(int diskIndex)
    {
        using var h = CreateFileW($@"\\.\PhysicalDrive{diskIndex}", 0, 3 /* share read|write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h.IsInvalid) return float.NaN;

        // STORAGE_PROPERTY_QUERY { PropertyId, QueryType = PropertyStandardQuery, AdditionalParameters[1] }
        var query = new byte[12];
        BitConverter.GetBytes(StorageDeviceTemperatureProperty).CopyTo(query, 0);
        var buf = new byte[512];
        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, buf, buf.Length, out int ret, IntPtr.Zero) || ret < 24)
            return float.NaN;

        // STORAGE_TEMPERATURE_DATA_DESCRIPTOR: InfoCount @12, TemperatureInfo[] @24 (各 16 byte, Temperature @+2)
        int count = BitConverter.ToUInt16(buf, 12);
        float best = float.NaN;
        for (int i = 0; i < count && 24 + i * 16 + 4 <= ret; i++)
        {
            short t = BitConverter.ToInt16(buf, 24 + i * 16 + 2);
            if (t > -40 && t < 150 && t != 0 && (float.IsNaN(best) || i == 0)) best = t; // index 0 = 総合温度
        }
        return best;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
}
