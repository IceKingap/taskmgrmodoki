namespace SysMon.Data;

/// <summary>固定長リングバッファの時系列。index 0 が最新。欠損は NaN。</summary>
internal sealed class Series
{
    private readonly float[] _buf;
    private int _head = -1;
    public int Count { get; private set; }

    public Series(int capacity = Model.Capacity) { _buf = new float[capacity]; }

    public void Push(float v)
    {
        _head = (_head + 1) % _buf.Length;
        _buf[_head] = v;
        if (Count < _buf.Length) Count++;
    }

    public float this[int age] => age < Count ? _buf[(_head - age + _buf.Length) % _buf.Length] : float.NaN;

    public float Last => Count > 0 ? _buf[_head] : float.NaN;

    public float Max(int window)
    {
        float m = float.NaN;
        int n = Math.Min(window, Count);
        for (int i = 0; i < n; i++)
        {
            float v = this[i];
            if (!float.IsNaN(v) && (float.IsNaN(m) || v > m)) m = v;
        }
        return m;
    }

    /// <summary>サンプルが途切れたデバイス用に NaN で埋めて時間軸をそろえる。</summary>
    public void PadTo(int count)
    {
        while (Count < count) Push(float.NaN);
    }
}

internal sealed class DiskHist
{
    public readonly Series Active = new(), Read = new(), Write = new(), Response = new();
}

internal sealed class NetHist
{
    public readonly Series Rx = new(), Tx = new();
}

internal sealed class GpuHist
{
    public readonly Series Util = new(), VramPct = new(), VramBw = new(), Temp = new(), Power = new();
}

/// <summary>UI スレッド側の状態。Snapshot を受け取って履歴に積む。</summary>
internal sealed class Model
{
    public const int Capacity = 1200;

    public Snapshot Last;
    public SystemInfo Info;
    public int Samples;

    public readonly Series CpuTotal = new(), CpuKernel = new(), CpuTemp = new(), CpuPower = new(), CpuFreq = new();
    public Series[] CpuThreads = Array.Empty<Series>(), CpuCores = Array.Empty<Series>();
    public readonly Series MemPct = new(), DramRead = new(), DramWrite = new(), CommitPct = new();
    public readonly Dictionary<string, DiskHist> Disks = new();
    public readonly Dictionary<string, NetHist> Nets = new();
    public readonly List<GpuHist> Gpus = new();

    public event Action Changed;

    public void Apply(Snapshot s)
    {
        Last = s;
        Samples++;

        CpuTotal.Push(s.CpuTotal);
        CpuKernel.Push(s.CpuKernel);
        CpuFreq.Push(s.CpuFreqMHz);
        if (CpuThreads.Length != s.CpuThreads.Length)
            CpuThreads = Enumerable.Range(0, s.CpuThreads.Length).Select(_ => new Series()).ToArray();
        for (int i = 0; i < s.CpuThreads.Length; i++) CpuThreads[i].Push(s.CpuThreads[i]);
        if (CpuCores.Length != s.CpuCores.Length)
            CpuCores = Enumerable.Range(0, s.CpuCores.Length).Select(_ => new Series()).ToArray();
        for (int i = 0; i < s.CpuCores.Length; i++) CpuCores[i].Push(s.CpuCores[i]);

        var hw = s.Hw;
        CpuTemp.Push(hw?.CpuTemp ?? float.NaN);
        CpuPower.Push(hw?.CpuPower ?? float.NaN);

        MemPct.Push(s.MemTotal > 0 ? (float)(100.0 * (s.MemTotal - s.MemAvail) / s.MemTotal) : float.NaN);
        CommitPct.Push(s.CommitLimit > 0 ? (float)(100.0 * s.CommitTotal / s.CommitLimit) : float.NaN);
        DramRead.Push(hw?.DramReadGBs ?? float.NaN);
        DramWrite.Push(hw?.DramWriteGBs ?? float.NaN);

        foreach (var d in s.Disks)
        {
            if (!Disks.TryGetValue(d.Key, out var h)) { Disks[d.Key] = h = new DiskHist(); h.Active.PadTo(Samples - 1); h.Read.PadTo(Samples - 1); h.Write.PadTo(Samples - 1); h.Response.PadTo(Samples - 1); }
            h.Active.Push(d.ActivePct);
            h.Read.Push(d.ReadBps);
            h.Write.Push(d.WriteBps);
            h.Response.Push(d.ResponseMs);
        }
        foreach (var n in s.Nets)
        {
            if (!Nets.TryGetValue(n.Id, out var h)) { Nets[n.Id] = h = new NetHist(); h.Rx.PadTo(Samples - 1); h.Tx.PadTo(Samples - 1); }
            h.Rx.Push(n.RxBps);
            h.Tx.Push(n.TxBps);
        }
        while (Gpus.Count < s.Gpus.Count) Gpus.Add(new GpuHist());
        for (int i = 0; i < s.Gpus.Count; i++)
        {
            var g = s.Gpus[i];
            var h = Gpus[i];
            h.Util.Push(g.Util);
            h.VramPct.Push(g.VramTotal > 0 ? g.VramUsed / g.VramTotal * 100f : float.NaN);
            h.VramBw.Push(g.VramBwGBs);
            h.Temp.Push(g.Temp);
            h.Power.Push(g.PowerW);
        }

        Changed?.Invoke();
    }
}
