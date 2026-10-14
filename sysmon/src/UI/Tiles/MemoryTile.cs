using SysMon.Data;

namespace SysMon.UI.Tiles;

internal sealed class MemoryTile : Tile
{
    public MemoryTile(Model m, AppState app) : base(m, app) { }

    protected override void Build(Canvas c)
    {
        var s = M.Last;
        var info = M.Info;
        string sub = "";
        if (info != null && info.MemInstalled > 0)
        {
            sub = $"{info.MemInstalled / 1073741824.0:0} GB";
            if (info.MemType != "") sub += $" {info.MemType}";
            if (info.MemSpeedMTs > 0) sub += $"-{info.MemSpeedMTs}";
            if (info.MemModules > 0) sub += $" · {info.MemModules} 枚";
        }
        Ui.Header(c, "メモリ", sub, s == null ? "" : Fmt.PctInt(M.MemPct.Last));
        if (s == null) return;
        c.Gap(6);

        int window = App.Window;
        Ui.Graph(c, c.Px(52), new GraphSpec
        {
            Series = new[] { M.MemPct },
            Colors = new[] { Theme.S1 },
            Caption = "使用率",
        }, window);
        c.Gap(7);

        // 構成: 使用中 / 変更済み / スタンバイ / 空き（タスクマネージャーの「メモリ構成」と同じ区分）
        double total = s.MemTotal;
        double modified = float.IsNaN(s.ModifiedBytes) ? 0 : s.ModifiedBytes;
        double standby = float.IsNaN(s.StandbyBytes) ? 0 : s.StandbyBytes;
        double free = float.IsNaN(s.FreeBytes) ? s.MemAvail - standby : s.FreeBytes;
        double inUse = Math.Max(0, total - s.MemAvail - modified);
        Ui.Stack(c, new List<(string, double, Color)>
        {
            ("使用中", inUse, Theme.S1),
            ("変更済み", modified, Theme.S2),
            ("スタンバイ", standby, Theme.S3),
            ("空き", Math.Max(0, free), Theme.Axis),
        });
        c.Gap(5);

        // メモリ帯域（Ryzen SMU の PM テーブル）
        var hw = s.Hw;
        bool haveBw = !float.IsNaN(M.DramRead.Max(window));
        if (haveBw)
        {
            Ui.Graph(c, c.Px(44), new GraphSpec
            {
                Series = new[] { M.DramRead, M.DramWrite },
                Colors = new[] { Theme.S1, Theme.S2 },
                Names = new[] { "読取", "書込" },
                Format = Fmt.GBs,
                Max = float.NaN,
                AutoFloor = 1,
            }, window);
            c.Gap(6);
        }

        float peak = info?.MemPeakGBs ?? float.NaN;
        float rd = hw?.DramReadGBs ?? float.NaN, wr = hw?.DramWriteGBs ?? float.NaN;
        var items = new List<KV>
        {
            new("使用中", Fmt.GB(total - s.MemAvail)),
            new("利用可能", Fmt.GB(s.MemAvail)),
            new("合計", Fmt.GB(total)),
            new("コミット", $"{s.CommitTotal / 1073741824.0:0.0} / {s.CommitLimit / 1073741824.0:0} GB"),
            new("キャッシュ済み", Fmt.GB(standby + modified)),
            new("予約済み", s.MemInstalled > total ? Fmt.Bytes(s.MemInstalled - total) : "—"),
            new("ページプール", Fmt.GB(s.PagedPool)),
            new("非ページプール", Fmt.GB(s.NonPagedPool)),
            new("ページフォールト", Fmt.Count(s.PageFaults) + "/s"),
            new("ハードフォールト", Fmt.Count(s.HardFaults) + "/s"),
        };
        if (haveBw)
        {
            items.Add(new("帯域 読取", Fmt.GBs(rd)));
            items.Add(new("帯域 書込", Fmt.GBs(wr)));
            float sum = rd + wr;
            items.Add(new("帯域 使用率", float.IsNaN(peak) || float.IsNaN(sum) ? "—" : Fmt.Pct(sum / peak * 100)));
            items.Add(new("理論ピーク", Fmt.GBs(peak)));
        }
        if (hw != null)
            foreach (var d in hw.DimmTemps.Take(4)) items.Add(new(d.Name + " 温度", Fmt.Temp(d.Value)));
        Ui.Kv(c, items, 3);

        if (!haveBw)
        {
            c.Gap(4);
            Ui.Note(c, BandwidthHint());
        }
    }

    private string BandwidthHint()
    {
        var hw = App.Sampler?.Hw;
        if (hw == null) return "メモリ帯域: センサー初期化中…";
        if (!hw.IsAdmin) return "メモリ帯域: 管理者で起動すると表示（Ryzen の SMU から取得）";
        if (!hw.PawnIoInstalled) return "メモリ帯域: PawnIO ドライバが必要です";
        if (hw.PmTableVersion == 0) return "メモリ帯域: この CPU では取得できません";
        return $"メモリ帯域: PM テーブル 0x{hw.PmTableVersion:X} は未対応です";
    }
}
