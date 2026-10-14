using System.Drawing.Drawing2D;
using SysMon.Data;

namespace SysMon.UI;

/// <summary>
/// 1 枚のカード。中身は Build(Canvas) で上から順に積む。同じ Build を計測（G == null）と描画の両方に使うので
/// 高さの計算と描画が食い違わない。
/// </summary>
internal abstract class Tile : Control
{
    protected readonly Model M;
    protected readonly AppState App;
    internal Point? MousePos;
    internal TooltipInfo Tip;
    private readonly List<(Rectangle r, Action a)> _hits = new();

    protected Tile(Model m, AppState app)
    {
        M = m;
        App = app;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Page;
    }

    protected Theme T => Theme.For(DeviceDpi);

    /// <summary>非表示にしたいタイル（データなし等）は false。</summary>
    public virtual bool HasContent => true;

    public int MeasureHeight(int width)
    {
        var c = NewCanvas(null, width);
        Build(c);
        return c.Y + c.Px(Ui.PadDip);
    }

    private Canvas NewCanvas(Graphics g, int width)
    {
        var t = T;
        int pad = (int)MathF.Round(Ui.PadDip * t.S);
        return new Canvas(g, this, t, pad, width - pad, (int)MathF.Round((Ui.PadDip - 2) * t.S));
    }

    protected abstract void Build(Canvas c);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var t = T;
        g.Clear(Theme.Page);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ui.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 6 * t.S))
        {
            using var fill = new SolidBrush(Theme.Tile);
            g.FillPath(fill, path);
            using var pen = new Pen(Theme.Border, 1f);
            g.DrawPath(pen, path);
        }
        ResetFrame();
        Ui.BeginBatch();
        Build(NewCanvas(g, Width));
        Ui.FlushBatch(g);
        if (Tip != null) Ui.DrawTooltip(g, t, Tip, ClientRectangle);
    }



    protected void ResetFrame()
    {
        _hits.Clear();
        Tip = null;
    }

    internal void AddHit(Rectangle r, Action a) => _hits.Add((r, a));

    internal bool Hovered(Rectangle r) => MousePos is Point p && r.Contains(p);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        MousePos = e.Location;
        Cursor = _hits.Any(h => h.r.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        MousePos = null;
        Invalidate();
    }

    private Rectangle? _pressed;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = null;
        if (e.Button != MouseButtons.Left) return;
        foreach (var h in _hits)
            if (h.r.Contains(e.Location)) { _pressed = h.r; break; }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || _pressed is not Rectangle pr) return;
        _pressed = null;
        if (!pr.Contains(e.Location)) return;
        foreach (var h in _hits.ToList())
            if (h.r == pr) { h.a(); break; }
    }
}

/// <summary>レイアウトカーソル。G が null のときは計測のみ。</summary>
internal sealed class Canvas
{
    public readonly Graphics G;
    public readonly Tile Owner;
    public readonly Theme T;
    public readonly float S;
    public readonly int L, R;
    public int Y;

    public Canvas(Graphics g, Tile owner, Theme t, int l, int r, int y)
    {
        G = g; Owner = owner; T = t; S = t.S; L = l; R = r; Y = y;
    }

    public int W => R - L;
    public bool Measuring => G == null;
    public int Px(float dip) => (int)MathF.Round(dip * S);
    public void Gap(float dip) => Y += Px(dip);
}

internal sealed class TooltipInfo
{
    public Point Anchor;
    public string Title;
    public List<(Color? color, string label, string value)> Lines = new();
}

internal sealed class GraphSpec
{
    public Series[] Series;
    public Color[] Colors;
    public string[] Names;
    public Func<float, string> Format = Fmt.Pct;
    public float Max = 100;          // NaN = 自動
    public float AutoFloor = 1;      // 自動スケール時の上限の下限
    public YScale Scale = YScale.Decimal; // 自動スケールの丸め方
    public bool ShowMax;             // 固定上限でも右上に上限値を出す
    public string Caption;           // 1 系列のときの左上ラベル
    public bool Labels = true;
    public bool Fill = true;
    public Func<int, string> TipTitle;
}
