using System.Drawing.Drawing2D;
using SysMon.Data;

namespace SysMon.UI;

/// <summary>最上段の細いバー: アプリ名・概要と、更新間隔 / 履歴長 / 最前面 / 管理者再起動のチップ。</summary>
internal sealed class TopBar : Tile
{
    public TopBar(Model m, AppState app) : base(m, app) { }

    public int BarHeight => (int)MathF.Round(34 * T.S);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var t = T;
        g.Clear(Theme.Page);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        ResetFrame();
        int pad = (int)MathF.Round(12 * t.S);
        Ui.BeginBatch();
        Build(new Canvas(g, this, t, pad, Width - pad, 0));
        Ui.FlushBatch(g);
    }

    protected override void Build(Canvas c)
    {
        var t = c.T;
        var set = App.Settings;
        int chipH = t.Small.H() + c.Px(6);
        int y = (Height - chipH) / 2;

        int x = c.R;
        x = Ui.Chip(c, x, y, $"更新 {set.IntervalMs / 1000.0:0.#} 秒", false, () =>
        {
            set.IntervalMs = Next(Settings.Intervals, set.IntervalMs);
            App.ApplySettings();
        }) - c.Px(4);
        x = Ui.Chip(c, x, y, $"履歴 {set.HistorySec} 秒", false, () =>
        {
            set.HistorySec = Next(Settings.Histories, set.HistorySec);
            App.ApplySettings();
        }) - c.Px(4);
        x = Ui.Chip(c, x, y, "最前面", set.TopMost, () =>
        {
            set.TopMost = !set.TopMost;
            App.ApplySettings();
        }) - c.Px(4);
        var hw = App.Sampler?.Hw;
        if (hw != null && !hw.IsAdmin)
            x = Ui.Chip(c, x, y, "管理者で再起動（温度を表示）", false, App.RestartElevated) - c.Px(4);

        int ty = (Height - t.Title.H()) / 2;
        Ui.Text(c.G, "SysMon", t.Title, Theme.Ink, c.L, ty);
        int lx = c.L + Ui.TextW("SysMon", t.Title) + c.Px(10);
        var s = M.Last;
        if (s != null)
        {
            string summary = $"{Environment.MachineName} · 稼働 {Fmt.Uptime(s.Uptime)} · プロセス {s.Processes:N0}";
            if (hw != null && hw.IsAdmin) summary += hw.PawnIoInstalled ? " · 管理者" : " · 管理者 (PawnIO なし)";
            Ui.Text(c.G, summary, t.Small, Theme.Muted, lx, (Height - t.Small.H()) / 2, x - lx - c.Px(8));
        }
    }

    private static int Next(int[] options, int current)
    {
        int i = Array.IndexOf(options, current);
        return options[(i + 1) % options.Length];
    }
}
