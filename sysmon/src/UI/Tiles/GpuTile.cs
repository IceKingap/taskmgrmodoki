using SysMon.Data;

namespace SysMon.UI.Tiles;

internal sealed class GpuTile : Tile
{
    private readonly int _index;

    public GpuTile(Model m, AppState app, int index) : base(m, app) { _index = index; }

    public override bool HasContent => M.Last != null && M.Last.Gpus.Count > _index;

    protected override void Build(Canvas c)
    {
        var s = M.Last;
        if (s == null || s.Gpus.Count <= _index) { Ui.Header(c, "GPU", "", ""); return; }
        var g = s.Gpus[_index];
        var h = M.Gpus[_index];
        int window = App.Window;

        string title = s.Gpus.Count > 1 ? $"GPU {_index}" : "GPU";
        Ui.Header(c, title, g.Name.Replace("NVIDIA ", "").Replace("GeForce ", ""), Fmt.PctInt(g.Util));
        c.Gap(6);

        Ui.Graph(c, c.Px(56), new GraphSpec
        {
            Series = new[] { h.Util },
            Colors = new[] { Theme.S1 },
            Caption = "使用率 (最大のエンジン)",
        }, window);
        c.Gap(7);

        if (!float.IsNaN(g.VramTotal))
        {
            float frac = g.VramUsed / g.VramTotal;
            Ui.Meter(c, "VRAM", $"{g.VramUsed / 1073741824f:0.0} / {g.VramTotal / 1073741824f:0.0} GB  ({frac * 100:0}%)",
                     frac, frac > 0.92f ? Theme.Warning : Theme.S1);
            c.Gap(6);
        }

        if (!float.IsNaN(g.VramBwGBs))
        {
            // VRAM のリアルタイム帯域は見出しと同じ大きさで出す（推定 = メモリコントローラ負荷 × 現クロックでの理論帯域）
            c.Gap(4);
            bool havePeak = !float.IsNaN(g.VramBwPeakGBs) && g.VramBwPeakGBs > 0;
            string sub = havePeak ? $"推定 · ピーク {Fmt.GBs(g.VramBwPeakGBs)} の {g.VramBwGBs / g.VramBwPeakGBs * 100:0}%" : "推定";
            Ui.Header(c, "VRAM 帯域", sub, Fmt.GBs(g.VramBwGBs));
            c.Gap(4);
            Ui.Graph(c, c.Px(64), new GraphSpec
            {
                Series = new[] { h.VramBw },
                Colors = new[] { Theme.S3 },
                Caption = "VRAM 帯域",
                Format = Fmt.GBs,
                Max = havePeak ? g.VramBwPeakGBs : float.NaN,
                ShowMax = true,
            }, window);
            c.Gap(7);
        }

        var eng = new List<(string, float)>();
        void E(string key, string label, float fallback = float.NaN)
        {
            float v = g.Engines.TryGetValue(key, out float x) ? x : fallback;
            if (!float.IsNaN(v)) eng.Add((label, v));
        }
        E("3D", "3D");
        E("Copy", "Copy");
        E("Compute", "Compute");
        E("Decode", "Decode", g.DecUtil);
        E("Encode", "Encode", g.EncUtil);
        if (eng.Count > 0) { Ui.MeterRow(c, eng); c.Gap(7); }

        var hw = HwFor(s, _index);
        var items = new List<KV>
        {
            new("温度", Fmt.Temp(g.Temp), Theme.StatusFor(g.Temp, 80, 88)),
            new("ホットスポット", Fmt.Temp(hw?.Hotspot ?? float.NaN), Theme.StatusFor(hw?.Hotspot ?? float.NaN, 95, 105)),
            new("電力", float.IsNaN(g.PowerW) ? "—" : float.IsNaN(g.PowerLimitW) ? Fmt.Watt(g.PowerW) : $"{g.PowerW:0} / {g.PowerLimitW:0} W"),
            new("コア", Fmt.MHz(g.CoreMHz)),
            new("メモリ", Fmt.MHz(g.MemMHz)),
            new("ファン", float.IsNaN(hw?.FanRpm ?? float.NaN) ? Fmt.PctInt(g.FanPct) : $"{Fmt.PctInt(g.FanPct)} · {hw.FanRpm:0} rpm"),
            new("メモリ負荷", Fmt.PctInt(g.MemCtrlUtil)),
            new("共有メモリ", Fmt.GB(g.SharedUsed)),
            new("PCIe", g.PcieGen > 0 ? $"{g.PcieGen}.0 x{g.PcieWidth}" : "—"),
            new("PCIe 受信", Fmt.Rate(g.PcieRxBps)),
            new("PCIe 送信", Fmt.Rate(g.PcieTxBps)),
            new("P ステート", g.PState >= 0 ? $"P{g.PState}" : "—"),
            new("クロック制限", Throttle(g.ThrottleReasons, g.FromNvml)),
        };
        if (!float.IsNaN(hw?.MemJunction ?? float.NaN)) items.Add(new("メモリ温度", Fmt.Temp(hw.MemJunction)));
        Ui.Kv(c, items, 3);
    }

    /// <summary>
    /// NVML の GPU に対応する LHM の GPU。同じ名前なら出現順で対応させる（同型 2 枚でも 1 枚目↔1 枚目）。
    /// LHM に出てこない GPU（TCC モードの Tesla など）は null。
    /// </summary>
    private static GpuHw HwFor(Snapshot s, int index)
    {
        var list = s.Hw?.Gpus;
        if (list == null || list.Count == 0) return null;
        string name = s.Gpus[index].Name?.Trim() ?? "";
        int nth = 0;
        for (int i = 0; i < index; i++)
            if (string.Equals(s.Gpus[i].Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)) nth++;
        var same = list.Where(x => string.Equals(x.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (nth < same.Count) return same[nth];
        // NVIDIA 以外は名前が取れない（"GPU"）ので、1 枚どうしのときだけ対応させる
        if (s.Gpus.Count == 1 && list.Count == 1 && !s.Gpus[0].FromNvml) return list[0];
        return null;
    }

    private static string Throttle(ulong r, bool nvml)
    {
        if (!nvml) return "—";
        if (r == 0) return "なし";
        var parts = new List<string>();
        if ((r & 0x4) != 0 || (r & 0x80) != 0) parts.Add("電力");
        if ((r & 0x20) != 0 || (r & 0x40) != 0) parts.Add("温度");
        if ((r & 0x8) != 0) parts.Add("HW");
        if ((r & 0x2) != 0 || (r & 0x100) != 0) parts.Add("設定");
        if (parts.Count == 0 && (r & 0x1) != 0) parts.Add("アイドル");
        return parts.Count == 0 ? "その他" : string.Join("/", parts);
    }
}
