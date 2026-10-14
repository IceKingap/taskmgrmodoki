using System.Text.Json;

namespace SysMon;

internal sealed class Settings
{
    /// <summary>CPU タイルのグラフ: "avg"（全体）/ "core"（物理コア別）/ "thread"（論理スレッド別）</summary>
    public string CpuView { get; set; } = "thread";
    public int IntervalMs { get; set; } = 1000;
    public int HistorySec { get; set; } = 60;
    public bool TopMost { get; set; }
    public string ProcSort { get; set; } = "cpu";
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    public int W { get; set; }
    public int H { get; set; }
    public bool Maximized { get; set; }

    public static readonly int[] Intervals = { 500, 1000, 2000 };
    public static readonly int[] Histories = { 60, 120, 300 };

    private static string PathOf => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysMon", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(PathOf))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOf)!);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
