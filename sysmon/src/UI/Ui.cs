using System.Drawing.Drawing2D;

namespace SysMon.UI;

internal readonly record struct KV(string K, string V, Color? Dot = null);

internal enum YScale { Fixed, Decimal, Binary, Bits }

/// <summary>タイル内の部品（見出し・KV グリッド・グラフ・メーター・積み上げ棒・ツールチップ）。</summary>
internal static class Ui
{
    public const float PadDip = 10;
    public static double IntervalSec = 1;

    // ------------------------------------------------------------ text
    //
    // 描画中の文字はいったん貯めておき、図形を描き終えてから 1 つの HDC で ExtTextOut でまとめて描く
    // （文字は常に図形より手前）。TextRenderer を毎回呼ぶより桁違いに軽い。

    private static List<GdiText.Op> _batch;


    public static void BeginBatch() => _batch = new List<GdiText.Op>(128);

    public static void FlushBatch(Graphics g)
    {
        var ops = _batch;
        _batch = null;
        if (ops != null && ops.Count > 0) DrawOps(g, ops);
    }

    private static void DrawOps(Graphics g, List<GdiText.Op> ops)
    {
        IntPtr hdc = g.GetHdc();
        try { GdiText.Draw(hdc, ops); }
        finally { g.ReleaseHdc(hdc); }
    }

    private static void Draw(Graphics g, GdiText.Op op)
    {
        if (_batch != null) _batch.Add(op);
        else DrawOps(g, new List<GdiText.Op> { op });
    }

    private static Font Pick(string s, Font f) => Theme.HasCjk(s) ? Theme.Jp(f) : f;

    public static int TextW(string s, Font f) => string.IsNullOrEmpty(s) ? 0 : GdiText.Width(s, Pick(s, f));

    public static void Text(Graphics g, string s, Font f, Color c, int x, int y, int maxW = 100000)
    {
        if (g == null || string.IsNullOrEmpty(s) || maxW <= 4) return;
        var pf = Pick(s, f);
        Draw(g, new GdiText.Op(s, pf, x, y + (f.H() - pf.H()) / 2, maxW, pf.H(), c, GdiText.Align.Left, true, false));
    }

    public static void TextR(Graphics g, string s, Font f, Color c, int right, int y)
    {
        if (g == null || string.IsNullOrEmpty(s)) return;
        var pf = Pick(s, f);
        Draw(g, new GdiText.Op(s, pf, right - 4000, y + (f.H() - pf.H()) / 2, 4000, pf.H(), c, GdiText.Align.Right, false, false));
    }

    public static void TextC(Graphics g, string s, Font f, Color c, Rectangle r)
    {
        if (g == null || string.IsNullOrEmpty(s)) return;
        Draw(g, new GdiText.Op(s, Pick(s, f), r.X, r.Y, r.Width, r.Height, c, GdiText.Align.Center, false, true));
    }

    /// <summary>小さいフォントを大きいフォントの行にベースラインをそろえて置くための縦オフセット。</summary>
    public static int BaselineOffset(Font big, Font small) => (int)((big.H() - small.H()) * 0.78f);

    // ------------------------------------------------------------ shapes

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }


    public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = RoundRect(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    public static void Dot(Graphics g, Color c, float cx, float cy, float d)
    {
        using var b = new SolidBrush(c);
        g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
    }

    // ------------------------------------------------------------ rows

    /// <summary>見出し行: タイトル + 補足（薄字）… 右端に大きな値。middle で右寄せのチップを差し込める。</summary>
    public static void Header(Canvas c, string title, string sub, string big, Func<Canvas, int, int> middle = null)
    {
        var t = c.T;
        int h = t.Big.H();
        if (!c.Measuring)
        {
            var g = c.G;
            int bigW = TextW(big, t.Big);
            int right = c.R - (bigW > 0 ? bigW + c.Px(10) : 0);
            if (middle != null) right = middle(c, right) - c.Px(8);
            int ty = c.Y + BaselineOffset(t.Big, t.Title);
            Text(g, title, t.Title, Theme.Ink, c.L, ty, right - c.L);
            int tw = TextW(title, t.Title);
            int sx = c.L + tw + c.Px(7);
            if (!string.IsNullOrEmpty(sub) && right - sx > c.Px(20))
                Text(g, sub, t.Small, Theme.Muted, sx, c.Y + BaselineOffset(t.Big, t.Small), right - sx);
            if (bigW > 0) TextR(g, big, t.Big, Theme.Ink, c.R, c.Y);
        }
        c.Y += h;
    }

    /// <summary>右端 right から左へチップを置く。戻り値は置いたチップの左端。</summary>
    public static int Chip(Canvas c, int right, int y, string text, bool on, Action click, int h = 0)
    {
        var t = c.T;
        if (h == 0) h = t.Small.H() + c.Px(6);
        int w = TextW(text, t.Small) + c.Px(14);
        var r = new Rectangle(right - w, y, w, h);
        if (!c.Measuring)
        {
            bool hover = c.Owner.Hovered(r);
            var bg = on ? Theme.ChipOn : hover ? Theme.Axis : Theme.ChipOff;
            FillRound(c.G, bg, r, 4 * c.S);
            TextC(c.G, text, t.Small, on ? Theme.Ink : Theme.Ink2, r);
            if (click != null) c.Owner.AddHit(r, click);
        }
        return r.Left;
    }

    /// <summary>ラベルと値の固定列グリッド。値は各セル右寄せなので毎秒変わっても位置が揺れない。</summary>
    public static void Kv(Canvas c, IList<KV> items, int cols)
    {
        if (items.Count == 0) return;
        var t = c.T;
        int gap = c.Px(12);
        int cellW = (c.W - gap * (cols - 1)) / cols;
        int rowH = t.Body.H() + c.Px(3);
        int rows = (items.Count + cols - 1) / cols;
        if (!c.Measuring)
        {
            var g = c.G;
            int lo = BaselineOffset(t.Body, t.Small);
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.K == null) continue;
                int x = c.L + (i % cols) * (cellW + gap);
                int y = c.Y + (i / cols) * rowH;
                int vw = TextW(it.V, t.Body);
                TextR(g, it.V, t.Body, Theme.Ink, x + cellW, y);
                int dotW = 0;
                if (it.Dot is Color dc)
                {
                    float d = 6 * c.S;
                    Dot(g, dc, x + cellW - vw - c.Px(4) - d / 2, y + t.Body.H() * 0.55f, d);
                    dotW = c.Px(10);
                }
                Text(g, it.K, t.Small, Theme.Muted, x, y + lo, cellW - vw - dotW - c.Px(5));
            }
        }
        c.Y += rows * rowH;
    }

    public static void Section(Canvas c, string text)
    {
        var t = c.T;
        if (!c.Measuring)
        {
            Text(c.G, text, t.Tiny, Theme.Muted, c.L, c.Y);
            int x = c.L + TextW(text, t.Tiny) + c.Px(6);
            using var pen = new Pen(Theme.Grid, 1);
            int y = c.Y + t.Tiny.H() / 2 + 1;
            if (x < c.R) c.G.DrawLine(pen, x, y, c.R, y);
        }
        c.Y += t.Tiny.H() + c.Px(3);
    }

    public static void Note(Canvas c, string text, Color? color = null)
    {
        var t = c.T;
        if (!c.Measuring) Text(c.G, text, t.Small, color ?? Theme.Muted, c.L, c.Y, c.W);
        c.Y += t.Small.H() + c.Px(3);
    }

    /// <summary>ラベル + 値 + 細い横棒。</summary>
    public static void Meter(Canvas c, string label, string value, float frac, Color fill)
    {
        var t = c.T;
        if (!c.Measuring)
        {
            Text(c.G, label, t.Small, Theme.Muted, c.L, c.Y, c.W - TextW(value, t.Small) - c.Px(8));
            TextR(c.G, value, t.Small, Theme.Ink, c.R, c.Y);
        }
        c.Y += t.Small.H() + c.Px(3);
        int h = c.Px(5);
        if (!c.Measuring)
        {
            FillRound(c.G, Theme.Axis, new RectangleF(c.L, c.Y, c.W, h), h / 2f);
            if (!float.IsNaN(frac) && frac > 0)
                FillRound(c.G, fill, new RectangleF(c.L, c.Y, Math.Max(h, c.W * Math.Min(1, frac)), h), h / 2f);
        }
        c.Y += h;
    }

    /// <summary>小さなメーターを横に並べる（GPU エンジン別など）。</summary>
    public static void MeterRow(Canvas c, IList<(string label, float pct)> items)
    {
        if (items.Count == 0) return;
        var t = c.T;
        int gap = c.Px(8);
        int n = items.Count;
        int w = (c.W - gap * (n - 1)) / n;
        int h = c.Px(4);
        if (!c.Measuring)
        {
            for (int i = 0; i < n; i++)
            {
                int x = c.L + i * (w + gap);
                var (label, pct) = items[i];
                string v = Fmt.PctInt(pct);
                Text(c.G, label, t.Tiny, Theme.Muted, x, c.Y, w - TextW(v, t.Tiny) - c.Px(3));
                TextR(c.G, v, t.Tiny, Theme.Ink2, x + w, c.Y);
                int by = c.Y + t.Tiny.H() + c.Px(2);
                FillRound(c.G, Theme.Axis, new RectangleF(x, by, w, h), h / 2f);
                if (!float.IsNaN(pct) && pct > 0.5f)
                    FillRound(c.G, Theme.S1, new RectangleF(x, by, Math.Max(h, w * Math.Min(1, pct / 100f)), h), h / 2f);
            }
        }
        c.Y += t.Tiny.H() + c.Px(2) + h;
    }

    /// <summary>積み上げ横棒 + 凡例（メモリ構成）。セグメント間は 2px の地色の隙間。</summary>
    public static void Stack(Canvas c, IList<(string name, double value, Color color)> segs)
    {
        var t = c.T;
        int h = c.Px(9);
        double total = segs.Sum(s => Math.Max(0, s.value));
        if (!c.Measuring && total > 0)
        {
            float x = c.L;
            float gap = 2 * c.S;
            int visible = segs.Count(s => s.value > 0);
            float usable = c.W - gap * Math.Max(0, visible - 1);
            foreach (var s in segs)
            {
                if (s.value <= 0) continue;
                float w = (float)(usable * s.value / total);
                FillRound(c.G, s.color, new RectangleF(x, c.Y, Math.Max(1, w), h), 2 * c.S);
                x += w + gap;
            }
        }
        c.Y += h + c.Px(4);

        // 凡例（折り返しあり）
        int lx = c.L;
        int rowH = t.Small.H() + c.Px(2);
        int sq = c.Px(8);
        foreach (var s in segs)
        {
            string label = s.name + " " + Fmt.GB(s.value);
            int w = sq + c.Px(4) + TextW(label, t.Small) + c.Px(12);
            if (lx + w > c.R + c.Px(12) && lx > c.L) { lx = c.L; c.Y += rowH; }
            if (!c.Measuring)
            {
                FillRound(c.G, s.color, new RectangleF(lx, c.Y + (t.Small.H() - sq) / 2f + 1, sq, sq), 2 * c.S);
                Text(c.G, s.name, t.Small, Theme.Muted, lx + sq + c.Px(4), c.Y);
                Text(c.G, Fmt.GB(s.value), t.Small, Theme.Ink2, lx + sq + c.Px(4) + TextW(s.name + " ", t.Small), c.Y);
            }
            lx += w;
        }
        c.Y += rowH;
    }

    // ------------------------------------------------------------ graph

    public static Rectangle Graph(Canvas c, int heightPx, GraphSpec sp, int window)
    {
        var r = new Rectangle(c.L, c.Y, c.W, heightPx);
        c.Y += heightPx;
        if (!c.Measuring) DrawGraph(c.G, c.T, r, sp, window, c.Owner);
        return r;
    }

    public static float NiceCeil(float v, YScale scale)
    {
        if (float.IsNaN(v) || v <= 0) return 1;
        double unit = 1, x = v;
        if (scale == YScale.Binary)
        {
            while (x >= 1024) { x /= 1024; unit *= 1024; }
        }
        else if (scale == YScale.Bits)
        {
            x = v * 8;
            unit = 1 / 8.0;
        }
        double e = Math.Pow(10, Math.Floor(Math.Log10(x)));
        double f = x / e;
        double nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        return (float)(nf * e * unit);
    }

    public static void DrawGraph(Graphics g, Theme t, Rectangle r, GraphSpec sp, int window, Tile owner)
    {
        float s = t.S;
        using (var bg = new SolidBrush(Theme.Plot)) g.FillRectangle(bg, r);

        float max = sp.Max;
        if (float.IsNaN(max))
        {
            float m = sp.AutoFloor;
            foreach (var se in sp.Series) { float x = se.Max(window); if (!float.IsNaN(x) && x > m) m = x; }
            max = NiceCeil(m, sp.Scale);
        }
        if (max <= 0) max = 1;

        // 目盛り線（控えめ）
        using (var grid = new Pen(Theme.Grid, 1))
        {
            int lines = r.Height >= 44 * s ? 4 : 2;
            for (int i = 1; i < lines; i++)
            {
                int y = r.Top + r.Height * i / lines;
                g.DrawLine(grid, r.Left, y, r.Right - 1, y);
            }
        }

        float step = window > 1 ? (float)(r.Width - 1) / (window - 1) : r.Width;
        var old = g.Clip;
        g.SetClip(r);
        for (int si = sp.Series.Length - 1; si >= 0; si--)
        {
            var se = sp.Series[si];
            var col = sp.Colors[si];
            using var pen = new Pen(col, Math.Max(1.2f, 1.5f * s)) { LineJoin = LineJoin.Round };
            using var fill = new SolidBrush(Theme.Alpha(col, sp.Series.Length > 1 ? 34 : 48));
            var pts = new List<PointF>(window);
            void Flush()
            {
                if (pts.Count >= 2)
                {
                    if (sp.Fill)
                    {
                        var poly = new List<PointF>(pts) { new(pts[^1].X, r.Bottom), new(pts[0].X, r.Bottom) };
                        g.FillPolygon(fill, poly.ToArray());
                    }
                    g.DrawLines(pen, pts.ToArray());
                }
                else if (pts.Count == 1)
                {
                    Dot(g, col, pts[0].X, pts[0].Y, 3 * s);
                }
                pts.Clear();
            }
            for (int k = 0; k < window; k++)
            {
                int age = window - 1 - k;
                float v = se[age];
                if (float.IsNaN(v)) { Flush(); continue; }
                float y = r.Bottom - 1 - Math.Clamp(v / max, 0, 1) * (r.Height - 2);
                pts.Add(new PointF(r.Left + k * step, y));
            }
            Flush();
        }
        g.Clip = old;

        using (var axis = new Pen(Theme.Axis, 1)) g.DrawLine(axis, r.Left, r.Bottom - 1, r.Right - 1, r.Bottom - 1);

        if (sp.Labels)
        {
            int lx = r.Left + (int)(4 * s), ly = r.Top + (int)(2 * s);
            if (sp.Series.Length >= 2 && sp.Names != null)
            {
                // 2 系列以上は凡例を左上に
                int sq = (int)(7 * s);
                for (int i = 0; i < sp.Series.Length; i++)
                {
                    FillRound(g, sp.Colors[i], new RectangleF(lx, ly + (t.Tiny.H() - sq) / 2f + 1, sq, sq), 1.5f * s);
                    lx += sq + (int)(3 * s);
                    Text(g, sp.Names[i], t.Tiny, Theme.Ink2, lx, ly);
                    lx += TextW(sp.Names[i], t.Tiny) + (int)(8 * s);
                }
            }
            else if (!string.IsNullOrEmpty(sp.Caption))
            {
                Text(g, sp.Caption, t.Tiny, Theme.Muted, lx, ly);
            }
            if (float.IsNaN(sp.Max) || sp.ShowMax) TextR(g, sp.Format(max), t.Tiny, Theme.Muted, r.Right - (int)(4 * s), ly);
        }

        // ホバー: 縦線 + 各系列の点 + ツールチップ
        if (owner?.MousePos is Point mp && r.Contains(mp))
        {
            int k = Math.Clamp((int)MathF.Round((mp.X - r.Left) / step), 0, window - 1);
            int age = window - 1 - k;
            float x = r.Left + k * step;
            using (var vl = new Pen(Theme.Alpha(Theme.Ink2, 110), 1)) g.DrawLine(vl, x, r.Top, x, r.Bottom - 1);
            var tip = new TooltipInfo
            {
                Anchor = new Point((int)x, r.Top),
                Title = sp.TipTitle?.Invoke(age) ?? AgeText(age),
            };
            for (int i = 0; i < sp.Series.Length; i++)
            {
                float v = sp.Series[i][age];
                if (!float.IsNaN(v))
                {
                    float y = r.Bottom - 1 - Math.Clamp(v / max, 0, 1) * (r.Height - 2);
                    Dot(g, Theme.Tile, x, y, 7 * s);
                    Dot(g, sp.Colors[i], x, y, 5 * s);
                }
                string name = sp.Names != null && i < sp.Names.Length ? sp.Names[i] : sp.Caption ?? "";
                tip.Lines.Add((sp.Series.Length > 1 ? sp.Colors[i] : null, name, sp.Format(v)));
            }
            owner.Tip = tip;
        }
    }

    public static string AgeText(int age)
    {
        double sec = age * IntervalSec;
        return sec < 0.5 ? "現在" : sec < 60 ? $"{sec:0.#} 秒前" : $"{(int)(sec / 60)} 分 {(int)(sec % 60):00} 秒前";
    }

    public static void DrawTooltip(Graphics g, Theme t, TooltipInfo tip, Rectangle bounds)
    {
        float s = t.S;
        int pad = (int)(7 * s);
        int lineH = t.Small.H() + (int)(2 * s);
        int dotW = tip.Lines.Any(l => l.color != null) ? (int)(12 * s) : 0;
        int labelW = tip.Lines.Select(l => TextW(l.label, t.Small)).DefaultIfEmpty(0).Max();
        int valueW = tip.Lines.Select(l => TextW(l.value, t.Small)).DefaultIfEmpty(0).Max();
        int w = Math.Max(TextW(tip.Title, t.Small), dotW + labelW + (int)(14 * s) + valueW) + pad * 2;
        int h = lineH * (tip.Lines.Count + 1) + pad * 2 - (int)(2 * s);

        int x = tip.Anchor.X + (int)(12 * s);
        if (x + w > bounds.Right - 3) x = tip.Anchor.X - (int)(12 * s) - w;
        x = Math.Max(bounds.Left + 3, x);
        int y = Math.Min(tip.Anchor.Y, bounds.Bottom - 3 - h);
        y = Math.Max(bounds.Top + 3, y);

        var rect = new RectangleF(x, y, w, h);
        BeginBatch();
        FillRound(g, Theme.TooltipBg, rect, 5 * s);
        using (var path = RoundRect(rect, 5 * s))
        using (var pen = new Pen(Theme.Axis, 1)) g.DrawPath(pen, path);

        int cy = y + pad;
        Text(g, tip.Title, t.Small, Theme.Muted, x + pad, cy);
        cy += lineH;
        foreach (var (color, label, value) in tip.Lines)
        {
            if (color is Color cc) FillRound(g, cc, new RectangleF(x + pad, cy + (t.Small.H() - 8 * s) / 2f + 1, 8 * s, 8 * s), 2 * s);
            Text(g, label, t.Small, Theme.Ink2, x + pad + dotW, cy);
            TextR(g, value, t.Small, Theme.Ink, x + w - pad, cy);
            cy += lineH;
        }
        FlushBatch(g);
    }
}
