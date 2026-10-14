using System.Globalization;

namespace SysMon.UI;

/// <summary>配色（ダーク固定）とフォント。フォントは DPI ごとにピクセル指定で作ってキャッシュする。</summary>
internal sealed class Theme
{
    // 面・インク
    public static readonly Color Page = Hex("#0d0d0d");
    public static readonly Color Tile = Hex("#1a1a19");
    public static readonly Color Plot = Hex("#141413");
    public static readonly Color Border = Hex("#2e2e2c");
    public static readonly Color Grid = Hex("#2c2c2a");
    public static readonly Color Axis = Hex("#383835");
    public static readonly Color Ink = Hex("#ffffff");
    public static readonly Color Ink2 = Hex("#c3c2b7");
    public static readonly Color Muted = Hex("#898781");
    public static readonly Color ChipOn = Hex("#2d3a4d");
    public static readonly Color ChipOff = Hex("#232322");
    public static readonly Color TooltipBg = Hex("#262625");

    // 系列色（カテゴリ順固定: 1 青 / 2 橙 / 3 アクア / 4 黄 / 5 マゼンタ / 7 紫）
    public static readonly Color S1 = Hex("#3987e5");
    public static readonly Color S2 = Hex("#d95926");
    public static readonly Color S3 = Hex("#199e70");
    public static readonly Color S4 = Hex("#c98500");
    public static readonly Color S5 = Hex("#d55181");
    public static readonly Color S7 = Hex("#9085e9");

    // 状態色（系列には使わない）
    public static readonly Color Good = Hex("#0ca30c");
    public static readonly Color Warning = Hex("#fab219");
    public static readonly Color Serious = Hex("#ec835a");
    public static readonly Color Critical = Hex("#d03b3b");

    public readonly float S;
    public readonly Font Tiny, Small, Body, Title, Big, Mid;

    private static readonly Dictionary<int, Theme> Cache = new();

    public static Theme For(int dpi)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(dpi, out var t)) Cache[dpi] = t = new Theme(dpi);
            return t;
        }
    }

    private Theme(int dpi)
    {
        S = dpi / 96f;
        const string family = "Segoe UI";
        Tiny = new Font(family, 10f * S, FontStyle.Regular, GraphicsUnit.Pixel);
        Small = new Font(family, 11f * S, FontStyle.Regular, GraphicsUnit.Pixel);
        Body = new Font(family, 12f * S, FontStyle.Regular, GraphicsUnit.Pixel);
        Title = new Font("Segoe UI Semibold", 13f * S, FontStyle.Regular, GraphicsUnit.Pixel);
        Mid = new Font("Segoe UI Semibold", 12f * S, FontStyle.Regular, GraphicsUnit.Pixel);
        Big = new Font("Segoe UI Semibold", 19f * S, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static readonly Dictionary<Font, Font> JpFonts = new();

    /// <summary>
    /// 日本語を含む文字列用の同サイズの Yu Gothic UI。Segoe UI のままだと GDI のフォントリンクで
    /// 日本語だけ約 1.33 倍に拡大されて行の大きさがそろわないため。
    /// </summary>
    public static Font Jp(Font f)
    {
        lock (JpFonts)
        {
            if (!JpFonts.TryGetValue(f, out var j))
            {
                string family = f.Name.Contains("Semibold") ? "Yu Gothic UI Semibold" : "Yu Gothic UI";
                JpFonts[f] = j = new Font(family, f.Size, f.Style, GraphicsUnit.Pixel);
            }
            return j;
        }
    }

    public static bool HasCjk(string s)
    {
        foreach (char ch in s)
            if ((ch >= 0x3000 && ch <= 0x9FFF) || (ch >= 0xF900 && ch <= 0xFAFF) || (ch >= 0xFF00 && ch <= 0xFFEF)) return true;
        return false;
    }

    public static Color Hex(string h) => ColorTranslator.FromHtml(h);

    public static Color Alpha(Color c, int a) => Color.FromArgb(a, c);

    /// <summary>温度などのしきい値に応じた状態色。値の文字色には使わず、横のドットにだけ使う。</summary>
    public static Color? StatusFor(float v, float warn, float crit)
    {
        if (float.IsNaN(v)) return null;
        if (v >= crit) return Critical;
        if (v >= warn) return Warning;
        return null;
    }
}

internal static class FontExt
{
    private static readonly Dictionary<Font, int> Heights = new();

    /// <summary>
    /// Font.Height のキャッシュ版。Font.Height は呼ぶたびに画面 DC から Graphics を作って計算するため非常に重い
    /// （毎秒数千回呼ぶと描画時間の大半を占める）。
    /// </summary>
    public static int H(this Font f)
    {
        lock (Heights)
        {
            if (!Heights.TryGetValue(f, out int h)) Heights[f] = h = f.Height;
            return h;
        }
    }
}

internal static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Pct(float v) => float.IsNaN(v) ? "—" : v.ToString(v < 9.95f && v > 0 ? "0.0" : "0", C) + "%";
    public static string PctInt(float v) => float.IsNaN(v) ? "—" : Math.Round(v).ToString("0", C) + "%";
    public static string Temp(float v) => float.IsNaN(v) ? "—" : v.ToString("0", C) + "°C";
    public static string Watt(float v) => float.IsNaN(v) ? "—" : v.ToString(v < 10 ? "0.0" : "0", C) + " W";
    public static string Volt(float v) => float.IsNaN(v) ? "—" : v.ToString("0.000", C) + " V";
    public static string Rpm(float v) => float.IsNaN(v) ? "—" : v.ToString("0", C) + " rpm";
    public static string Ms(float v) => float.IsNaN(v) ? "—" : v.ToString(v < 10 ? "0.0" : "0", C) + " ms";

    public static string MHz(float v)
    {
        if (float.IsNaN(v) || v <= 0) return "—";
        return v >= 1000 ? (v / 1000f).ToString("0.00", C) + " GHz" : v.ToString("0", C) + " MHz";
    }

    public static string Bytes(double b)
    {
        if (double.IsNaN(b)) return "—";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (Math.Abs(b) >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        string f = i == 0 ? "0" : b < 10 ? "0.00" : b < 100 ? "0.0" : "0";
        return b.ToString(f, C) + " " + u[i];
    }

    /// <summary>GB 固定表示（メモリ量など桁をそろえたい所）。</summary>
    public static string GB(double b) => double.IsNaN(b) ? "—" : (b / 1073741824.0).ToString(b < 10 * 1073741824.0 ? "0.00" : "0.0", C) + " GB";

    public static string Rate(double bps) => double.IsNaN(bps) ? "—" : Bytes(bps) + "/s";

    public static string Bits(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec)) return "—";
        double b = bytesPerSec * 8;
        string[] u = { "bps", "Kbps", "Mbps", "Gbps" };
        int i = 0;
        while (b >= 1000 && i < u.Length - 1) { b /= 1000; i++; }
        string f = i == 0 ? "0" : b < 10 ? "0.0" : "0";
        return b.ToString(f, C) + " " + u[i];
    }

    public static string LinkSpeed(long bps)
    {
        if (bps <= 0) return "—";
        if (bps >= 1_000_000_000) return (bps / 1e9).ToString("0.#", C) + " Gbps";
        return (bps / 1e6).ToString("0", C) + " Mbps";
    }

    public static string Count(double v)
    {
        if (double.IsNaN(v)) return "—";
        if (v >= 1e6) return (v / 1e6).ToString("0.0", C) + "M";
        if (v >= 1e4) return (v / 1e3).ToString("0", C) + "k";
        if (v >= 1e3) return (v / 1e3).ToString("0.0", C) + "k";
        return v.ToString("0", C);
    }

    public static string GBs(float v) => float.IsNaN(v) ? "—" : v.ToString(v < 10 ? "0.00" : v < 100 ? "0.0" : "0", C) + " GB/s";

    public static string Uptime(TimeSpan t)
    {
        return t.Days > 0 ? $"{t.Days}日 {t.Hours}:{t.Minutes:00}" : $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
