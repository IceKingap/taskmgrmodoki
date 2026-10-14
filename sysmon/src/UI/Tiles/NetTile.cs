using SysMon.Data;

namespace SysMon.UI.Tiles;

internal sealed class NetTile : Tile
{
    public NetTile(Model m, AppState app) : base(m, app) { }

    public override bool HasContent => M.Last?.Nets.Count > 0;

    protected override void Build(Canvas c)
    {
        var s = M.Last;
        var main = s?.Nets.FirstOrDefault(n => n.Primary) ?? s?.Nets.FirstOrDefault();
        if (main == null || !M.Nets.TryGetValue(main.Id, out var h)) { Ui.Header(c, "ネットワーク", "", ""); return; }

        Ui.Header(c, "ネットワーク", $"{main.Name} · {main.Description}", "↓ " + Fmt.Bits(main.RxBps));
        c.Gap(6);

        int window = App.Window;
        Ui.Graph(c, c.Px(56), new GraphSpec
        {
            Series = new[] { h.Rx, h.Tx },
            Colors = new[] { Theme.S1, Theme.S2 },
            Names = new[] { "受信", "送信" },
            Format = v => Fmt.Bits(v),
            Max = float.NaN,
            AutoFloor = 125_000, // 1 Mbps (bytes/s)
            Scale = YScale.Bits,
        }, window);
        c.Gap(7);

        Ui.Kv(c, new List<KV>
        {
            new("受信", Fmt.Rate(main.RxBps)),
            new("送信", Fmt.Rate(main.TxBps)),
            new("リンク速度", Fmt.LinkSpeed(main.LinkBps)),
            new("受信", Fmt.Bits(main.RxBps)),
            new("送信", Fmt.Bits(main.TxBps)),
            new("IPv4", main.Ipv4 ?? "—"),
            new("累計受信", Fmt.Bytes(main.RxTotal)),
            new("累計送信", Fmt.Bytes(main.TxTotal)),
        }, 3);

        var others = s.Nets.Where(n => n != main).ToList();
        if (others.Count > 0)
        {
            c.Gap(5);
            Ui.Section(c, "その他のアダプター");
            var t = c.T;
            int rowH = t.Small.H() + c.Px(3);
            int colW = c.Px(84);
            foreach (var n in others)
            {
                if (!c.Measuring)
                {
                    bool idle = n.RxBps < 1 && n.TxBps < 1;
                    var ink = idle ? Theme.Muted : Theme.Ink2;
                    Ui.Text(c.G, n.Name, t.Small, idle ? Theme.Muted : Theme.Ink2, c.L, c.Y, c.W - colW * 2 - c.Px(6));
                    Ui.TextR(c.G, "↓ " + Fmt.Rate(n.RxBps), t.Small, ink, c.R - colW, c.Y);
                    Ui.TextR(c.G, "↑ " + Fmt.Rate(n.TxBps), t.Small, ink, c.R, c.Y);
                }
                c.Y += rowH;
            }
        }
    }
}
