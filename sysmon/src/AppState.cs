using SysMon.Data;

namespace SysMon;

/// <summary>タイル間で共有する設定と操作。</summary>
internal sealed class AppState
{
    public Settings Settings;
    public Sampler Sampler;
    public Action RequestLayout = () => { };
    public Action RestartElevated = () => { };
    public Action ApplySettings = () => { };

    /// <summary>グラフに並べるサンプル数（履歴秒数 ÷ 更新間隔）。</summary>
    public int Window => Math.Clamp(Settings.HistorySec * 1000 / Math.Max(100, Settings.IntervalMs), 10, Model.Capacity);
}
