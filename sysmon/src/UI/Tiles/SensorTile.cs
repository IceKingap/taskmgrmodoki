using SysMon.Data;

namespace SysMon.UI.Tiles;

/// <summary>マザーボードの温度・ファン・電圧（LibreHardwareMonitor）。使えないときは理由と対処を出す。</summary>
internal sealed class SensorTile : Tile
{
    public SensorTile(Model m, AppState app) : base(m, app) { }

    protected override void Build(Canvas c)
    {
        var s = M.Last;
        var hwState = App.Sampler?.Hw;
        var r = s?.Hw;
        bool hasBoard = r != null && (r.BoardTemps.Count > 0 || r.Fans.Count > 0 || r.Voltages.Count > 0);

        string status = hwState == null ? "初期化中…"
                      : !hwState.IsAdmin ? "制限モード（管理者権限なし）"
                      : !hwState.PawnIoInstalled ? "PawnIO ドライバ未導入"
                      : r?.BoardName ?? "";
        Ui.Header(c, "センサー", status, "");
        c.Gap(6);

        if (hwState != null && (!hwState.IsAdmin || !hwState.PawnIoInstalled))
        {
            if (!hwState.IsAdmin)
            {
                Ui.Note(c, "CPU 温度・電力・ファン・電圧・メモリ帯域は管理者権限が必要です。", Theme.Ink2);
                c.Gap(3);
                int y = c.Y;
                Ui.Chip(c, c.L + ChipWidth(c, "管理者として再起動"), y, "管理者として再起動", false, App.RestartElevated);
                c.Y += c.T.Small.H() + c.Px(6);
                c.Gap(6);
            }
            if (!hwState.PawnIoInstalled)
            {
                Ui.Note(c, "さらに PawnIO ドライバ（LibreHardwareMonitor 用）の導入が必要です:", Theme.Ink2);
                Ui.Note(c, "  winget install namazso.PawnIO");
                c.Gap(4);
            }
        }

        if (!hasBoard)
        {
            if (hwState != null && hwState.IsAdmin && hwState.PawnIoInstalled)
                Ui.Note(c, "マザーボードのセンサーが見つかりません。");
            return;
        }

        if (r.BoardTemps.Count > 0)
        {
            Ui.Section(c, "温度");
            Ui.Kv(c, r.BoardTemps.Select(x => new KV(x.Name, Fmt.Temp(x.Value))).ToList(), 2);
            c.Gap(5);
        }
        if (r.Fans.Count > 0)
        {
            Ui.Section(c, "ファン");
            Ui.Kv(c, r.Fans.Select(x => new KV(x.Name, float.IsNaN(x.Extra) ? Fmt.Rpm(x.Value) : $"{Fmt.Rpm(x.Value)} · {x.Extra:0}%")).ToList(), 2);
            c.Gap(5);
        }
        if (r.Voltages.Count > 0)
        {
            Ui.Section(c, "電圧");
            Ui.Kv(c, r.Voltages.Select(x => new KV(x.Name, Fmt.Volt(x.Value))).ToList(), 2);
        }
    }

    private static int ChipWidth(Canvas c, string text) => Ui.TextW(text, c.T.Small) + c.Px(14);
}
