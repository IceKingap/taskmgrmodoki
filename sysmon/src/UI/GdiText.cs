using System.Runtime.InteropServices;

namespace SysMon.UI;

/// <summary>
/// ExtTextOut を直接使う軽量テキスト描画。TextRenderer は 1 回ごとに DC の保存/復元やフォント解決をするため、
/// 毎秒数百回描くこのアプリでは描画時間の大半を占めていた。
/// フォントリンクに頼らないよう、日本語を含む文字列は呼び出し側で Yu Gothic UI を選んでいる。
/// </summary>
internal static class GdiText
{
    private static readonly Dictionary<Font, IntPtr> Fonts = new();
    private static readonly IntPtr MeasureDc = CreateCompatibleDC(IntPtr.Zero);
    private static readonly Dictionary<(string, Font), int> Widths = new();

    public static IntPtr HFont(Font f)
    {
        if (!Fonts.TryGetValue(f, out IntPtr h))
        {
            h = CreateFontW(-(int)MathF.Round(f.Size), 0, 0, 0, 400, 0, 0, 0, 1 /* DEFAULT_CHARSET */,
                            0, 0, 5 /* CLEARTYPE_QUALITY */, 0, f.Name);
            Fonts[f] = h;
        }
        return h;
    }

    public static int Width(string s, Font f)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        if (Widths.TryGetValue((s, f), out int w)) return w;
        if (Widths.Count > 8000) Widths.Clear();
        SelectObject(MeasureDc, HFont(f));
        GetTextExtentPoint32W(MeasureDc, s, s.Length, out SIZE sz);
        Widths[(s, f)] = sz.cx;
        return sz.cx;
    }

    public enum Align { Left, Right, Center }

    public readonly record struct Op(string S, Font F, int X, int Y, int W, int H, Color C, Align A, bool Ellipsis, bool VCenter);

    public static void Draw(IntPtr hdc, List<Op> ops)
    {
        SetBkMode(hdc, 1 /* TRANSPARENT */);
        IntPtr oldFont = IntPtr.Zero, curFont = IntPtr.Zero;
        int curColor = -1;
        foreach (var op in ops)
        {
            IntPtr hf = HFont(op.F);
            if (hf != curFont)
            {
                IntPtr prev = SelectObject(hdc, hf);
                if (oldFont == IntPtr.Zero) oldFont = prev;
                curFont = hf;
            }
            int color = op.C.R | (op.C.G << 8) | (op.C.B << 16);
            if (color != curColor) { SetTextColor(hdc, color); curColor = color; }

            string s = op.S;
            int w = Width(s, op.F);
            if (op.Ellipsis && w > op.W)
            {
                s = Truncate(s, op.F, op.W);
                if (s.Length == 0) continue;
                w = Width(s, op.F);
            }
            int x = op.A switch
            {
                Align.Right => op.X + op.W - w,
                Align.Center => op.X + (op.W - w) / 2,
                _ => op.X,
            };
            int y = op.VCenter ? op.Y + (op.H - op.F.H()) / 2 : op.Y;
            ExtTextOutW(hdc, x, y, 0, IntPtr.Zero, s, s.Length, IntPtr.Zero);
        }
        if (oldFont != IntPtr.Zero) SelectObject(hdc, oldFont);
    }

    /// <summary>幅に収まるよう末尾を「…」で切る（二分探索）。</summary>
    private static string Truncate(string s, Font f, int maxW)
    {
        const string ell = "…";
        int ew = Width(ell, f);
        if (ew > maxW) return "";
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Width(s[..mid], f) + ew <= maxW) lo = mid; else hi = mid - 1;
        }
        return s[..lo].TrimEnd() + ell;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string s, int len, out SIZE size);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint options, IntPtr rect, string s, int len, IntPtr dx);
}
