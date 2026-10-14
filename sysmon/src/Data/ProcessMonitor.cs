using System.Runtime.InteropServices;
using SysMon.Native;

namespace SysMon.Data;

/// <summary>
/// NtQuerySystemInformation(SystemProcessInformation) で全プロセスを 1 回の呼び出しで取得し、
/// CPU 時間 / I/O 転送量の差分から使用率を出す。同名プロセスはまとめる（chrome ×30 など）。
/// </summary>
internal sealed class ProcessMonitor : IDisposable
{
    private IntPtr _buf;
    private int _bufSize = 1 << 20;
    private readonly int _cpuCount = Environment.ProcessorCount;
    private Dictionary<(long pid, long create), (long cpu, long io)> _prev = new();
    private long _prevTick;

    public int ProcessCount, ThreadCount, HandleCount, ReadyThreads;

    public ProcessMonitor()
    {
        _buf = Marshal.AllocHGlobal(_bufSize);
    }

    /// <param name="gpuByPid">PDH の GPU エンジンカウンタから求めたプロセス別 GPU 使用率</param>
    public List<ProcRow> Sample(Dictionary<int, float> gpuByPid)
    {
        int ret;
        int st;
        while ((st = Win32.NtQuerySystemInformation(Win32.SystemProcessInformation, _buf, _bufSize, out ret)) == Win32.STATUS_INFO_LENGTH_MISMATCH)
        {
            Marshal.FreeHGlobal(_buf);
            _bufSize = Math.Max(_bufSize * 2, ret + 65536);
            _buf = Marshal.AllocHGlobal(_bufSize);
        }
        if (st != 0) return new List<ProcRow>();

        long now = Environment.TickCount64;
        double elapsed100ns = (now - _prevTick) * 10_000.0;
        bool havePrev = _prevTick != 0 && elapsed100ns > 0;
        _prevTick = now;

        var cur = new Dictionary<(long, long), (long, long)>(_prev.Count + 64);
        var groups = new Dictionary<string, ProcRow>(StringComparer.OrdinalIgnoreCase);
        int procs = 0, threads = 0, handles = 0, ready = 0;

        // SYSTEM_PROCESS_INFORMATION (x64) のオフセット
        const int oNext = 0, oThreads = 4, oPrivateWs = 8, oCreate = 32, oUser = 40, oKernel = 48,
                  oNameLen = 56, oNameBuf = 64, oPid = 80, oHandles = 96, oRead = 232, oWrite = 240, oOther = 248;

        IntPtr p = _buf;
        while (true)
        {
            int next = Marshal.ReadInt32(p, oNext);
            long pid = Marshal.ReadIntPtr(p, oPid).ToInt64();
            int nThreads = Marshal.ReadInt32(p, oThreads);
            int nHandles = Marshal.ReadInt32(p, oHandles);
            procs++;
            threads += nThreads;
            handles += nHandles;

            // 各プロセスの直後に SYSTEM_THREAD_INFORMATION (x64: 80 byte, ThreadState @68) が並ぶ。
            // 実行可能 (Ready=1 / DeferredReady=7) で待っているスレッド数 = プロセッサ待ちキュー長
            IntPtr th = p + 256;
            for (int i = 0; i < nThreads; i++, th += 80)
            {
                int state = Marshal.ReadInt32(th, 68);
                if (state == 1 || state == 7) ready++;
            }

            if (pid != 0) // 0 = System Idle Process
            {
                long create = Marshal.ReadInt64(p, oCreate);
                long cpu = Marshal.ReadInt64(p, oUser) + Marshal.ReadInt64(p, oKernel);
                long io = Marshal.ReadInt64(p, oRead) + Marshal.ReadInt64(p, oWrite) + Marshal.ReadInt64(p, oOther);
                long privWs = Marshal.ReadInt64(p, oPrivateWs);
                int nameLen = Marshal.ReadInt16(p, oNameLen);
                IntPtr nameBuf = Marshal.ReadIntPtr(p, oNameBuf);
                string name = nameBuf != IntPtr.Zero && nameLen > 0 ? Marshal.PtrToStringUni(nameBuf, nameLen / 2) : (pid == 4 ? "System" : $"pid {pid}");
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

                var key = (pid, create);
                cur[key] = (cpu, io);

                float cpuPct = 0, ioBps = 0;
                if (havePrev && _prev.TryGetValue(key, out var pv))
                {
                    cpuPct = (float)(Math.Max(0, cpu - pv.cpu) / (elapsed100ns * _cpuCount) * 100.0);
                    ioBps = (float)(Math.Max(0, io - pv.io) / (elapsed100ns / 1e7));
                }

                if (!groups.TryGetValue(name, out var row))
                {
                    row = new ProcRow { Name = name };
                    groups[name] = row;
                }
                row.Count++;
                row.CpuPct += cpuPct;
                row.IoBps += ioBps;
                row.PrivateBytes += privWs;
                if (gpuByPid != null && gpuByPid.TryGetValue((int)pid, out float g)) row.GpuPct = Math.Max(row.GpuPct, g);
            }

            if (next == 0) break;
            p += next;
        }

        _prev = cur;
        ProcessCount = procs;
        ThreadCount = threads;
        HandleCount = handles;
        ReadyThreads = ready;
        foreach (var r in groups.Values) r.CpuPct = Math.Min(100f, r.CpuPct);
        return groups.Values.ToList();
    }

    public void Dispose()
    {
        if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
    }
}
