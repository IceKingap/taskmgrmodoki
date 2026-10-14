using SysMon.Data;

namespace SysMon.UI.Tiles;

internal sealed class DiskTile : Tile
{
    public readonly string Key;
    private long _volTick;
    private List<(string name, long total, long free)> _volumes = new();

    public DiskTile(Model m, AppState app, string key) : base(m, app) { Key = key; }

    public override bool HasContent => M.Last?.Disks.Any(d => d.Key == Key) == true;

    protected override void Build(Canvas c)
    {
        var s = M.Last;
        var d = s?.Disks.FirstOrDefault(x => x.Key == Key);
        if (d == null || !M.Disks.TryGetValue(Key, out var h)) { Ui.Header(c, "ディスク", "", ""); return; }

        SystemInfo.DiskInfo di = null;
        M.Info?.Disks.TryGetValue(d.Index, out di);
        string title = $"ディスク {d.Index}" + (d.Letters != "" ? $" ({d.Letters.Replace(" ", ", ")})" : "");
        string sub = di == null ? "" : di.Kind != "" ? $"{di.Model} · {di.Kind}" : di.Model;
        Ui.Header(c, title, sub, Fmt.PctInt(d.ActivePct));
        c.Gap(6);

        int window = App.Window;
        Ui.Graph(c, c.Px(34), new GraphSpec
        {
            Series = new[] { h.Active },
            Colors = new[] { Theme.S1 },
            Caption = "アクティブ時間",
        }, window);
        c.Gap(4);
        Ui.Graph(c, c.Px(42), new GraphSpec
        {
            Series = new[] { h.Read, h.Write },
            Colors = new[] { Theme.S1, Theme.S2 },
            Names = new[] { "読取", "書込" },
            Format = v => Fmt.Rate(v),
            Max = float.NaN,
            AutoFloor = 1024 * 1024,
            Scale = YScale.Binary,
        }, window);
        c.Gap(7);

        var hw = s.Hw;
        float temp = d.TempC;
        float life = float.NaN, written = float.NaN, hours = float.NaN;
        if (hw != null && di != null)
        {
            if (float.IsNaN(temp)) temp = Find(hw.DiskTemps, di.Model);
            life = Find(hw.DiskLife, di.Model);
            written = Find(hw.DiskWrittenGB, di.Model);
            hours = Find(hw.DiskHours, di.Model);
        }
        var items = new List<KV>
        {
            new("読取", Fmt.Rate(d.ReadBps)),
            new("書込", Fmt.Rate(d.WriteBps)),
            new("応答時間", Fmt.Ms(d.ResponseMs)),
            new("IOPS", Fmt.Count(d.ReadIops + d.WriteIops)),
            new("キュー", d.Queue.ToString("0")),
            new("温度", Fmt.Temp(temp), Theme.StatusFor(temp, 60, 70)),
        };
        if (!float.IsNaN(life)) items.Add(new("寿命残り", Fmt.PctInt(life)));
        if (!float.IsNaN(written)) items.Add(new("総書込", written >= 1000 ? $"{written / 1000:0.0} TB" : $"{written:0} GB"));
        if (!float.IsNaN(hours)) items.Add(new("使用時間", $"{hours:N0} h"));
        Ui.Kv(c, items, 3);

        RefreshVolumes(d.Letters);
        if (_volumes.Count > 0)
        {
            c.Gap(5);
            foreach (var (name, total, free) in _volumes)
            {
                float frac = total > 0 ? (float)(total - free) / total : 0;
                Ui.Meter(c, name, $"{Fmt.Bytes(total - free)} / {Fmt.Bytes(total)}  (空き {Fmt.Bytes(free)})", frac,
                         frac > 0.92f ? Theme.Warning : Theme.S1);
                c.Gap(4);
            }
        }
    }

    private void RefreshVolumes(string letters)
    {
        long now = Environment.TickCount64;
        if (now - _volTick < 15000 && _volumes.Count > 0) return;
        _volTick = now;
        var list = new List<(string, long, long)>();
        foreach (var l in letters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var di = new DriveInfo(l);
                if (di.IsReady) list.Add((l, di.TotalSize, di.TotalFreeSpace));
            }
            catch { }
        }
        _volumes = list;
    }

    private static float Find(Dictionary<string, float> byModel, string model)
    {
        foreach (var kv in byModel) if (Same(kv.Key, model)) return kv.Value;
        return float.NaN;
    }

    private static bool Same(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        a = a.Trim(); b = b.Trim();
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }
}
