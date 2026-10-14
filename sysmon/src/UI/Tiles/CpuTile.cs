using SysMon.Data;

namespace SysMon.UI.Tiles;

internal sealed class CpuTile : Tile
{
    public CpuTile(Model m, AppState app) : base(m, app) { }

    protected override void Build(Canvas c)
    {
        var s = M.Last;
        var info = M.Info;
        string sub = info != null ? $"{ShortCpuName(info.CpuName)} · {info.Cores}C/{info.Threads}T" : "";
        string view = App.Settings.CpuView;
        // コア表示では「物理コアがどれだけ埋まっているか」を大きく出す。
        // スレッド平均（タスクマネージャーと同じ値）は SMT の片側しか使わない負荷だと全コア 100% でも 50〜70% 程度にしかならない
        bool coreView = view == "core" && s != null && !float.IsNaN(s.CpuCoreAvg);
        string big = s == null ? "" : Fmt.PctInt(coreView ? s.CpuCoreAvg : s.CpuTotal);

        Ui.Header(c, "CPU", sub, big, (cc, right) =>
        {
            int y = cc.Y + (cc.T.Big.H() - (cc.T.Small.H() + cc.Px(6))) / 2;
            int l = Ui.Chip(cc, right, y, "スレッド", view == "thread", () => SetView("thread"));
            l = Ui.Chip(cc, l - cc.Px(3), y, "コア", view == "core", () => SetView("core"));
            return Ui.Chip(cc, l - cc.Px(3), y, "平均", view != "thread" && view != "core", () => SetView("avg"));
        });
        if (s == null) { Ui.Note(c, "取得中…"); return; }
        c.Gap(6);

        int window = App.Window;
        if (view == "core" && M.CpuCores.Length > 0) CoreGrid(c, window, s);
        else if (view == "thread" && M.CpuThreads.Length > 0) ThreadGrid(c, window, s);
        else
        {
            Ui.Graph(c, c.Px(96), new GraphSpec
            {
                Series = new[] { M.CpuTotal, M.CpuKernel },
                Colors = new[] { Theme.S1, Theme.S2 },
                Names = new[] { "使用率", "カーネル" },
            }, window);
        }
        c.Gap(7);

        var hw = s.Hw;
        var items = new List<KV>
        {
            coreView ? new("スレッド平均", Fmt.PctInt(s.CpuTotal)) : new("物理コア平均", Fmt.PctInt(s.CpuCoreAvg)),
            new("周波数", Fmt.MHz(s.CpuFreqMHz)),
            new("温度", Fmt.Temp(hw?.CpuTemp ?? float.NaN), Theme.StatusFor(hw?.CpuTemp ?? float.NaN, 80, 90)),
            new("電力", Fmt.Watt(hw?.CpuPower ?? float.NaN)),
            new("最大コア", Fmt.MHz(hw?.CpuMaxClock ?? float.NaN)),
            new("コア電圧", Fmt.Volt(hw?.CpuVoltage ?? float.NaN)),
            new("カーネル", Fmt.Pct(s.CpuKernel)),
        };
        if (hw != null && !float.IsNaN(hw.CpuEffClock))
        {
            // 実効クロック = スリープ時間も含めた全コア平均（HWiNFO の Effective Clock と同じ考え方）
            items.Add(new("実効クロック", Fmt.MHz(hw.CpuEffClock)));
        }
        if (hw != null && (hw.CcdTemps.Count > 0 || !float.IsNaN(hw.SocTemp)))
        {
            while (items.Count % 3 != 0) items.Add(default);
            foreach (var ccd in hw.CcdTemps.Take(2)) items.Add(new(ccd.Name + " 温度", Fmt.Temp(ccd.Value)));
            if (!float.IsNaN(hw.SocTemp)) items.Add(new("SoC 温度", Fmt.Temp(hw.SocTemp)));
            while (items.Count % 3 != 0) items.Add(default);
        }
        if (hw != null && !float.IsNaN(hw.FabricClock))
        {
            items.Add(new("FCLK", Fmt.MHz(hw.FabricClock)));
            items.Add(new("UCLK", Fmt.MHz(hw.UncoreClock)));
            items.Add(new("MCLK", Fmt.MHz(hw.MemClock)));
        }
        items.AddRange(new KV[]
        {
            new("プロセス", s.Processes.ToString("N0")),
            new("スレッド", s.Threads.ToString("N0")),
            new("ハンドル", s.Handles.ToString("N0")),
            new("コンテキスト切替", Fmt.Count(s.ContextSwitches) + "/s"),
            new("割り込み", Fmt.Count(s.Interrupts) + "/s"),
            new("システムコール", Fmt.Count(s.Syscalls) + "/s"),
            new("実行待ちキュー", float.IsNaN(s.QueueLength) ? "—" : s.QueueLength.ToString("0")),
            new("稼働時間", Fmt.Uptime(s.Uptime)),
        });
        Ui.Kv(c, items, 3);

        if (M.CpuTemp.Max(window) is float tmax && !float.IsNaN(tmax))
        {
            c.Gap(6);
            Ui.Graph(c, c.Px(34), new GraphSpec
            {
                Series = new[] { M.CpuTemp },
                Colors = new[] { Theme.S1 },
                Caption = "温度",
                Format = Fmt.Temp,
                Max = 100,
                ShowMax = true,
            }, window);
        }
    }

    /// <summary>物理コアの周波数。LHM（管理者時）の実測を優先し、無ければ兄弟スレッドの PDH 値の最大。</summary>
    private static float CoreMHz(Snapshot s, int core)
    {
        var lhm = s.Hw?.CoreClocks;
        if (lhm != null && lhm.Length == s.CpuCores.Length && core < lhm.Length && !float.IsNaN(lhm[core])) return lhm[core];
        float m = float.NaN;
        for (int t = 0; t < s.CpuCoreOf.Length && t < s.CpuThreadMHz.Length; t++)
            if (s.CpuCoreOf[t] == core && !float.IsNaN(s.CpuThreadMHz[t]) && (float.IsNaN(m) || s.CpuThreadMHz[t] > m)) m = s.CpuThreadMHz[t];
        return m;
    }

    private static string ShortMHz(float v) => float.IsNaN(v) || v <= 0 ? "" : (v / 1000f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GHz";

    private void ThreadGrid(Canvas c, int window, Snapshot s)
    {
        int n = M.CpuThreads.Length;
        Grid(c, window, n, n > 32 ? 30 : 38, i =>
        {
            int core = i < s.CpuCoreOf.Length ? s.CpuCoreOf[i] : -1;
            // スレッドの周波数は所属コアの値（LHM があればそちらが正確）
            float mhz = core >= 0 ? CoreMHz(s, core) : i < s.CpuThreadMHz.Length ? s.CpuThreadMHz[i] : float.NaN;
            return new Cell(M.CpuThreads[i], i.ToString(), mhz, age =>
            {
                string t = Ui.AgeText(age);
                if (core >= 0) t += $" · コア {core}";
                if (age == 0 && !float.IsNaN(mhz)) t += " · " + Fmt.MHz(mhz);
                return t;
            }, $"スレッド {i}");
        });
    }

    private void CoreGrid(Canvas c, int window, Snapshot s)
    {
        int n = M.CpuCores.Length;
        bool hybrid = s.CpuCoreEff.Length > 0 && s.CpuCoreEff.Distinct().Count() > 1;
        byte maxEff = s.CpuCoreEff.Length > 0 ? s.CpuCoreEff.Max() : (byte)0;
        Grid(c, window, n, n > 32 ? 30 : 42, i =>
        {
            var threads = Enumerable.Range(0, s.CpuCoreOf.Length).Where(t => s.CpuCoreOf[t] == i).ToArray();
            string label = i.ToString();
            if (hybrid && i < s.CpuCoreEff.Length) label += s.CpuCoreEff[i] == maxEff ? " P" : " E";
            float mhz = CoreMHz(s, i);
            return new Cell(M.CpuCores[i], label, mhz, age =>
            {
                string t = $"{Ui.AgeText(age)} · スレッド {string.Join(", ", threads)}";
                if (age == 0 && !float.IsNaN(mhz)) t += " · " + Fmt.MHz(mhz);
                return t;
            }, "コア使用率 (スレッドの最大)");
        });
    }

    private readonly record struct Cell(Series Series, string Label, float MHz, Func<int, string> Tip, string Caption);

    /// <summary>小さなグラフのタイル並び。左上に番号、右上に使用率、左下に周波数。</summary>
    private void Grid(Canvas c, int window, int n, int cellHDip, Func<int, Cell> cellOf)
    {
        var t = c.T;
        int gap = c.Px(3);
        int cols = Math.Clamp((c.W + gap) / (c.Px(54) + gap), 2, 16);
        // なるべく割り切れる列数に（24 → 6 列 × 4 行 など）
        for (int k = cols; k >= Math.Max(2, cols - 3); k--) if (n % k == 0) { cols = k; break; }
        int rows = (n + cols - 1) / cols;
        int cellW = (c.W - gap * (cols - 1)) / cols;
        int cellH = c.Px(cellHDip);

        if (!c.Measuring)
        {
            for (int i = 0; i < n; i++)
            {
                int x = c.L + (i % cols) * (cellW + gap);
                int y = c.Y + (i / cols) * (cellH + gap);
                var r = new Rectangle(x, y, cellW, cellH);
                var cell = cellOf(i);
                Ui.DrawGraph(c.G, t, r, new GraphSpec
                {
                    Series = new[] { cell.Series },
                    Colors = new[] { Theme.S1 },
                    Labels = false,
                    Caption = cell.Caption,
                    TipTitle = cell.Tip,
                }, window, this);
                Ui.Text(c.G, cell.Label, t.Tiny, Theme.Muted, x + c.Px(3), y + c.Px(1));
                Ui.TextR(c.G, Fmt.PctInt(cell.Series.Last), t.Tiny, Theme.Ink2, x + cellW - c.Px(3), y + c.Px(1));
                string f = ShortMHz(cell.MHz);
                if (f != "") Ui.Text(c.G, f, t.Tiny, Theme.Muted, x + c.Px(3), y + cellH - t.Tiny.H() - c.Px(1), cellW - c.Px(4));
            }
        }
        c.Y += rows * cellH + (rows - 1) * gap;
    }

    private void SetView(string view)
    {
        App.Settings.CpuView = view;
        App.ApplySettings();
        App.RequestLayout();
        Invalidate();
    }

    private static string ShortCpuName(string n)
    {
        n = n.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
        foreach (var junk in new[] { " Processor", " CPU", "-Core", " with Radeon Graphics" })
        {
            int p = n.IndexOf(junk, StringComparison.OrdinalIgnoreCase);
            if (junk == "-Core" && p > 0)
            {
                // "12-Core" を落とす
                int q = n.LastIndexOf(' ', p);
                if (q > 0) n = n[..q];
                continue;
            }
            if (p > 0) n = n[..p];
        }
        return string.Join(' ', n.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
