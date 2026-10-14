using System.Runtime.InteropServices;

namespace SysMon.Native;

/// <summary>
/// PDH (パフォーマンスカウンタ) の薄いラッパー。英語名で登録するので日本語 OS でもそのまま動く。
/// 1 回の Collect() でクエリ内の全カウンタをまとめて取得する。
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly List<PdhCounter> _counters = new();

    public PdhQuery()
    {
        uint st = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (st != 0) throw new InvalidOperationException($"PdhOpenQuery failed 0x{st:X8}");
    }

    /// <summary>カウンタを追加する。存在しないカウンタは null を返す（例外にしない）。</summary>
    public PdhCounter Add(string englishPath)
    {
        uint st = PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out IntPtr h);
        if (st != 0) return null;
        var c = new PdhCounter(h, englishPath);
        _counters.Add(c);
        return c;
    }

    public bool Collect() => PdhCollectQueryData(_query) == 0;

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    // ---- P/Invoke ----
    internal const uint PDH_FMT_DOUBLE = 0x00000200;
    internal const uint PDH_FMT_NOCAP100 = 0x00008000;
    internal const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Explicit)]
    internal struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll")]
    internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
}

internal sealed class PdhCounter
{
    private readonly IntPtr _h;
    private IntPtr _buf;
    private uint _bufSize;
    public string Path { get; }

    internal PdhCounter(IntPtr h, string path) { _h = h; Path = path; }

    /// <summary>単一インスタンスの値。取得できなければ NaN。</summary>
    public double Value
    {
        get
        {
            uint st = PdhQuery.PdhGetFormattedCounterValue(_h, PdhQuery.PDH_FMT_DOUBLE | PdhQuery.PDH_FMT_NOCAP100, IntPtr.Zero, out var v);
            if (st != 0 || v.CStatus > 1) return double.NaN;
            return v.doubleValue;
        }
    }

    /// <summary>ワイルドカードカウンタの (インスタンス名, 値) 一覧。</summary>
    public List<KeyValuePair<string, double>> Array()
    {
        var result = new List<KeyValuePair<string, double>>();
        uint fmt = PdhQuery.PDH_FMT_DOUBLE | PdhQuery.PDH_FMT_NOCAP100;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = _bufSize;
            uint st = PdhQuery.PdhGetFormattedCounterArrayW(_h, fmt, ref size, out uint count, _buf);
            if (st == PdhQuery.PDH_MORE_DATA)
            {
                if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
                _bufSize = size + 4096;
                _buf = Marshal.AllocHGlobal((int)_bufSize);
                continue;
            }
            if (st != 0) return result;

            // PDH_FMT_COUNTERVALUE_ITEM_W (x64): szName(8) + CStatus(4) + pad(4) + double(8) = 24 bytes
            const int itemSize = 24;
            for (int i = 0; i < count; i++)
            {
                IntPtr p = _buf + i * itemSize;
                uint status = (uint)Marshal.ReadInt32(p, 8);
                if (status > 1) continue;
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p));
                double val = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 16));
                result.Add(new KeyValuePair<string, double>(name, val));
            }
            return result;
        }
        return result;
    }
}
