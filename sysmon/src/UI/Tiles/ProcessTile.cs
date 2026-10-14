using SysMon.Data;

namespace SysMon.UI.Tiles;

/// <summary>上位プロセス表。列見出しクリックで並べ替え。同名プロセスはまとめて表示。</summary>
internal sealed class ProcessTile : Tile
{
    private const int Rows = 12;

    private static readonly (string key, string label, float widthDip)[] Cols =
    {
        ("cpu", "CPU", 46),
        ("gpu", "GPU", 42),
        ("mem", "メモリ", 66),
        ("io", "I/O", 70),
    };

    public ProcessTile(Model m, AppState app) : base(m, app) { }

    protected override void Build(Canvas c)
    {
        var s = M.Last;
        string sortKey = App.Settings.ProcSort;
        Ui.Header(c, "プロセス", s == null ? "" : $"{s.Processes:N0} 個 · 同名はまとめて表示", "");
        if (s == null) return;
        c.Gap(5);

        var t = c.T;
        int rowH = t.Small.H() + c.Px(4);

        // 列位置（右から）
        var colRight = new int[Cols.Length];
        int x = c.R;
        for (int i = Cols.Length - 1; i >= 0; i--) { colRight[i] = x; x -= c.Px(Cols[i].widthDip); }
        int nameRight = x - c.Px(6);

        // 見出し
        if (!c.Measuring)
        {
            Ui.Text(c.G, "名前", t.Tiny, Theme.Muted, c.L, c.Y + c.Px(2));
            for (int i = 0; i < Cols.Length; i++)
            {
                var (key, label, w) = Cols[i];
                bool on = key == sortKey;
                string text = on ? label + " ▾" : label;
                var hit = new Rectangle(colRight[i] - c.Px(w), c.Y, c.Px(w), rowH);
                bool hover = Hovered(hit);
                if (on || hover) Ui.FillRound(c.G, on ? Theme.ChipOn : Theme.ChipOff, new RectangleF(hit.X + c.Px(4), hit.Y, hit.Width - c.Px(4), hit.Height - c.Px(2)), 3 * c.S);
                Ui.TextR(c.G, text, t.Tiny, on ? Theme.Ink : Theme.Ink2, colRight[i] - c.Px(3), c.Y + c.Px(2));
                string k = key;
                AddHit(hit, () => { App.Settings.ProcSort = k; App.ApplySettings(); Invalidate(); });
            }
        }
        c.Y += rowH;

        Func<ProcRow, double> metric = sortKey switch
        {
            "gpu" => p => p.GpuPct,
            "mem" => p => p.PrivateBytes,
            "io" => p => p.IoBps,
            _ => p => p.CpuPct,
        };
        var rows = s.Procs.OrderByDescending(metric).ThenByDescending(p => p.PrivateBytes).Take(Rows).ToList();
        double top = rows.Count > 0 ? Math.Max(1e-9, metric(rows[0])) : 1;

        foreach (var p in rows)
        {
            if (!c.Measuring)
            {
                // 並べ替え列の値に比例した薄い帯（行の背景）
                double frac = Math.Clamp(metric(p) / top, 0, 1);
                if (frac > 0.005)
                    Ui.FillRound(c.G, Theme.Alpha(Theme.S1, 38), new RectangleF(c.L - c.Px(3), c.Y, (float)((c.W + c.Px(3)) * frac), rowH - c.Px(1)), 2 * c.S);

                string name = p.Count > 1 ? $"{p.Name} ({p.Count})" : p.Name;
                Ui.Text(c.G, name, t.Small, Theme.Ink, c.L, c.Y + c.Px(2), nameRight - c.L);
                string[] vals =
                {
                    p.CpuPct >= 0.05f ? p.CpuPct.ToString(p.CpuPct < 10 ? "0.0" : "0") + "%" : "0%",
                    p.GpuPct >= 0.5f ? p.GpuPct.ToString("0") + "%" : "—",
                    Fmt.Bytes(p.PrivateBytes),
                    p.IoBps >= 1 ? Fmt.Rate(p.IoBps) : "—",
                };
                for (int i = 0; i < Cols.Length; i++)
                    Ui.TextR(c.G, vals[i], t.Small, Cols[i].key == sortKey ? Theme.Ink : Theme.Ink2, colRight[i] - c.Px(3), c.Y + c.Px(2));
            }
            c.Y += rowH;
        }
    }

}
