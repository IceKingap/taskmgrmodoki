using System.ComponentModel;
using System.Diagnostics;
using SysMon.Data;
using SysMon.UI.Tiles;

namespace SysMon.UI;

internal sealed class MainForm : Form
{
    private readonly Model _model = new();
    private readonly AppState _app = new();
    private readonly Settings _settings;
    private readonly Sampler _sampler;
    private readonly TopBar _top;
    private readonly ScrollPanel _body;

    private readonly CpuTile _cpu;
    private readonly MemoryTile _mem;
    private readonly NetTile _net;
    private readonly ProcessTile _proc;
    private readonly SensorTile _sensor;
    private readonly List<GpuTile> _gpus = new();
    private readonly Dictionary<string, DiskTile> _disks = new();
    private readonly Dictionary<Tile, int> _lastHeights = new();
    private int _lastLayoutWidth;

    /// <summary>画面確認用: "--hover cpu:0.5,0.3" でタイル内の相対位置にマウスがある状態を再現する。</summary>
    public string DebugHover;

    public MainForm()
    {
        _settings = Settings.Load();
        _app.Settings = _settings;
        _sampler = new Sampler { IntervalMs = _settings.IntervalMs };
        _app.Sampler = _sampler;
        _app.RequestLayout = () => LayoutTiles(force: true);
        _app.RestartElevated = RestartElevated;
        _app.ApplySettings = ApplySettings;
        Ui.IntervalSec = _settings.IntervalMs / 1000.0;

        Text = "SysMon";
        BackColor = Theme.Page;
        ForeColor = Theme.Ink;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        float s = DeviceDpi / 96f;
        MinimumSize = new Size((int)(380 * s), (int)(300 * s));
        var wa = Screen.PrimaryScreen!.WorkingArea;
        var size = new Size((int)(1180 * s), (int)Math.Min(880 * s, wa.Height - 40));
        var loc = new Point(wa.Left + (wa.Width - size.Width) / 2, wa.Top + (wa.Height - size.Height) / 2);
        if (_settings.W > 0 && _settings.H > 0)
        {
            var saved = new Rectangle(_settings.X, _settings.Y, _settings.W, _settings.H);
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(saved))) { loc = saved.Location; size = saved.Size; }
        }
        Bounds = new Rectangle(loc, size);
        if (_settings.Maximized) WindowState = FormWindowState.Maximized;
        TopMost = _settings.TopMost;

        _top = new TopBar(_model, _app) { Dock = DockStyle.Top };
        _top.Height = _top.BarHeight;
        _body = new ScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Page };
        _body.HandleCreated += (_, _) => SetWindowTheme(_body.Handle, "DarkMode_Explorer", null);
        Controls.Add(_body);
        Controls.Add(_top);

        _cpu = new CpuTile(_model, _app);
        _mem = new MemoryTile(_model, _app);
        _net = new NetTile(_model, _app);
        _proc = new ProcessTile(_model, _app);
        _sensor = new SensorTile(_model, _app);
        foreach (var t in new Tile[] { _cpu, _mem, _net, _proc, _sensor }) _body.Controls.Add(t);

        // 縦スクロールバーの出入りではサイズでなくクライアント幅だけが変わるので、そちらで並べ直す
        _body.ClientSizeChanged += (_, _) => LayoutTiles();
        _sampler.Sampled += snap =>
        {
            try { BeginInvoke(() => OnSample(snap)); } catch (InvalidOperationException) { }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // ダークなタイトルバー（Windows 10 20H1+ / 11）
        int on = 1;
        DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LayoutTiles(force: true);
        _sampler.Start();
    }

    private void OnSample(Snapshot snap)
    {
        if (IsDisposed) return;
        _model.Info ??= _sampler.Info;
        _model.Apply(snap);
        SyncDynamicTiles(snap);
        LayoutTiles();
        if (DebugHover != null && _model.Samples == 3) ApplyDebugHover();
        foreach (Control c in _body.Controls) c.Invalidate();
        _top.Invalidate();
    }

    /// <summary>GPU とディスクはデバイス数に応じてタイルを増減する。</summary>
    private void SyncDynamicTiles(Snapshot snap)
    {
        while (_gpus.Count < snap.Gpus.Count)
        {
            var t = new GpuTile(_model, _app, _gpus.Count);
            _gpus.Add(t);
            _body.Controls.Add(t);
        }
        foreach (var d in snap.Disks)
        {
            if (_disks.ContainsKey(d.Key)) continue;
            var t = new DiskTile(_model, _app, d.Key);
            _disks[d.Key] = t;
            _body.Controls.Add(t);
        }
    }

    private IEnumerable<Tile> OrderedTiles()
    {
        yield return _cpu;
        foreach (var g in _gpus) yield return g;
        yield return _mem;
        var disks = _model.Last?.Disks;
        if (disks != null)
            foreach (var d in disks)
                if (_disks.TryGetValue(d.Key, out var t)) yield return t;
        yield return _net;
        yield return _proc;
        yield return _sensor;
    }

    /// <summary>
    /// 幅に応じて列数を決め、各タイルを「いちばん短い列」に順に積む（メーソンリー配置）。
    /// タイルの高さが変わったときだけ並べ直す。
    /// </summary>
    private void LayoutTiles(bool force = false)
    {
        if (_body.ClientSize.Width <= 0) return;
        float s = DeviceDpi / 96f;
        int pad = (int)(8 * s), gap = (int)(8 * s);
        int avail = _body.ClientSize.Width - pad * 2;
        int cols = Math.Clamp((avail + gap) / ((int)(330 * s) + gap), 1, 6);
        int colW = (avail - gap * (cols - 1)) / cols;

        var tiles = OrderedTiles().ToList();
        var heights = new Dictionary<Tile, int>();
        bool changed = force || colW != _lastLayoutWidth;
        foreach (var t in tiles)
        {
            int h = t.HasContent ? t.MeasureHeight(colW) : 0;
            heights[t] = h;
            if (!_lastHeights.TryGetValue(t, out int old) || old != h) changed = true;
        }
        if (!changed) return;
        _lastLayoutWidth = colW;
        _lastHeights.Clear();
        foreach (var kv in heights) _lastHeights[kv.Key] = kv.Value;

        var colY = Enumerable.Repeat(pad, cols).ToArray();
        int scrollY = _body.AutoScrollPosition.Y;
        _body.SuspendLayout();
        foreach (var t in tiles)
        {
            int h = heights[t];
            if (h == 0) { t.Visible = false; continue; }
            int col = 0;
            for (int i = 1; i < cols; i++) if (colY[i] < colY[col]) col = i;
            var b = new Rectangle(pad + col * (colW + gap), colY[col] + scrollY, colW, h);
            if (t.Bounds != b) t.Bounds = b;
            if (!t.Visible) t.Visible = true;
            colY[col] += h + gap;
        }
        foreach (Control c in _body.Controls)
            if (c is Tile t && !tiles.Contains(t)) t.Visible = false;
        _body.AutoScrollMinSize = new Size(0, colY.Max() - gap + pad);
        _body.ResumeLayout();
    }

    private void ApplyDebugHover()
    {
        var parts = DebugHover.Split(':', ',');
        if (parts.Length != 3) return;
        var named = new Dictionary<string, Tile> { ["cpu"] = _cpu, ["mem"] = _mem, ["net"] = _net, ["proc"] = _proc, ["sensor"] = _sensor };
        for (int i = 0; i < _gpus.Count; i++) named["gpu" + (i == 0 ? "" : i.ToString())] = _gpus[i];
        int di = 0;
        foreach (var d in _disks.Values) named["disk" + di++] = d;
        if (!named.TryGetValue(parts[0], out var t)) return;
        float fx = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        float fy = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        t.MousePos = new Point((int)(t.Width * fx), (int)(t.Height * fy));
    }

    private void ApplySettings()
    {
        _sampler.IntervalMs = _settings.IntervalMs;
        Ui.IntervalSec = _settings.IntervalMs / 1000.0;
        TopMost = _settings.TopMost;
        _settings.Save();
        _sampler.Poke();
        LayoutTiles(force: true);
        foreach (Control c in _body.Controls) c.Invalidate();
        _top.Invalidate();
    }

    private void RestartElevated()
    {
        SaveBounds();
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception)
        {
            return; // UAC でキャンセル
        }
        Close();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _top.Height = _top.BarHeight;
        LayoutTiles(force: true);
    }

    private void SaveBounds()
    {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.X = b.X; _settings.Y = b.Y; _settings.W = b.Width; _settings.H = b.Height;
        _settings.Maximized = WindowState == FormWindowState.Maximized;
        _settings.Save();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveBounds();
        _sampler.Dispose();
        base.OnFormClosing(e);
    }

    /// <summary>縦スクロールだけのダブルバッファ付きパネル（タイルは常に幅に収めて並べるので横スクロールは不要）。</summary>
    private sealed class ScrollPanel : Panel
    {
        public ScrollPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            HorizontalScroll.Maximum = 0;
            HorizontalScroll.Enabled = false;
            HorizontalScroll.Visible = false;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style &= ~0x00100000; // WS_HSCROLL
                return cp;
            }
        }

        // フォーカスを取ったタイルへ勝手にスクロールしない
        protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
    }
}
