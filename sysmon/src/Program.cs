using System.Text;
using SysMon.Data;
using SysMon.UI;

namespace SysMon;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 診断: SysMon.exe --dump <出力ファイル>  … 数回サンプリングして取得値とセンサー一覧を書き出す
        if (args.Length >= 2 && args[0] == "--dump") return Dump(args[1]);

        ApplicationConfiguration.Initialize();
        var form = new MainForm();
        int hi = Array.IndexOf(args, "--hover");
        if (hi >= 0 && hi + 1 < args.Length) form.DebugHover = args[hi + 1];
        Application.Run(form);
        return 0;
    }

    private static int Dump(string path)
    {
        var sb = new StringBuilder();
        using var sampler = new Sampler { IntervalMs = 1000 };
        Snapshot last = null;
        int count = 0;
        using var done = new ManualResetEventSlim();
        sampler.Sampled += s => { last = s; if (++count >= 4) done.Set(); };
        sampler.Start();
        done.Wait(TimeSpan.FromSeconds(30));
        // LHM の初期化を待つ
        for (int i = 0; i < 20 && sampler.Hw == null; i++) Thread.Sleep(500);
        Thread.Sleep(1500);

        var s = last;
        var info = sampler.Info;
        sb.AppendLine($"CPU {info?.CpuName} {info?.Cores}C/{info?.Threads}T  mem {info?.MemType}-{info?.MemSpeedMTs} x{info?.MemModules} peak {info?.MemPeakGBs} GB/s");
        if (s != null)
        {
            sb.AppendLine($"cpu total {s.CpuTotal:0.0}% kernel {s.CpuKernel:0.0}% freq {s.CpuFreqMHz:0} MHz base {s.CpuBaseMHz:0}  threads[{s.CpuThreads.Length}]: {string.Join(" ", s.CpuThreads.Select(v => v.ToString("0")))}");
            sb.AppendLine($"cores[{s.CpuCores.Length}]: {string.Join(" ", s.CpuCores.Select(v => v.ToString("0")))}  coreOf: {string.Join(",", s.CpuCoreOf)}  eff: {string.Join(",", s.CpuCoreEff)}");
            sb.AppendLine($"thread MHz: {string.Join(" ", s.CpuThreadMHz.Select(v => v.ToString("0")))}  lhm core MHz: {string.Join(" ", (s.Hw?.CoreClocks ?? Array.Empty<float>()).Select(v => v.ToString("0")))}");
            sb.AppendLine($"ctx {s.ContextSwitches:0} intr {s.Interrupts:0} sys {s.Syscalls:0} queue {s.QueueLength}  procs {s.Processes} threads {s.Threads} handles {s.Handles} up {s.Uptime}");
            sb.AppendLine($"mem total {s.MemTotal >> 20} MB avail {s.MemAvail >> 20} MB commit {s.CommitTotal >> 20}/{s.CommitLimit >> 20} MB paged {s.PagedPool >> 20} nonpaged {s.NonPagedPool >> 20} modified {s.ModifiedBytes / 1048576:0} standby {s.StandbyBytes / 1048576:0} free {s.FreeBytes / 1048576:0} pf {s.PageFaults:0} hf {s.HardFaults:0}");
            foreach (var d in s.Disks)
                sb.AppendLine($"disk [{d.Key}] active {d.ActivePct:0.0}% resp {d.ResponseMs:0.00} ms read {d.ReadBps:0} write {d.WriteBps:0} q {d.Queue} temp {d.TempC} iops {d.ReadIops + d.WriteIops:0}  model={(info != null && info.Disks.TryGetValue(d.Index, out var di) ? di.Model + " / " + di.Kind : "?")}");
            foreach (var n in s.Nets)
                sb.AppendLine($"net {(n.Primary ? "*" : " ")} {n.Name} | {n.Description} | link {n.LinkBps} rx {n.RxBps:0} tx {n.TxBps:0} ip {n.Ipv4}");
            foreach (var g in s.Gpus)
            {
                sb.AppendLine($"gpu {g.Name} nvml={g.FromNvml} util {g.Util} memctl {g.MemCtrlUtil} vram {g.VramUsed / 1048576:0}/{g.VramTotal / 1048576:0} MB shared {g.SharedUsed / 1048576:0} MB temp {g.Temp} power {g.PowerW}/{g.PowerLimitW} core {g.CoreMHz}/{g.MaxCoreMHz} mem {g.MemMHz}/{g.MaxMemMHz} bus {g.BusWidth} fan {g.FanPct}");
                sb.AppendLine($"    pcie {g.PcieGen}.0 x{g.PcieWidth} rx {g.PcieRxBps:0} tx {g.PcieTxBps:0} enc {g.EncUtil} dec {g.DecUtil} P{g.PState} throttle 0x{g.ThrottleReasons:X} vrambw {g.VramBwGBs:0.0}/{g.VramBwPeakGBs:0} GB/s");
                sb.AppendLine($"    engines: {string.Join(", ", g.Engines.Select(kv => $"{kv.Key}={kv.Value:0.0}"))}");
            }
            foreach (var p in s.Procs.OrderByDescending(p => p.PrivateBytes).Take(8))
                sb.AppendLine($"proc {p.Name} x{p.Count} cpu {p.CpuPct:0.0}% gpu {p.GpuPct:0}% mem {p.PrivateBytes >> 20} MB io {p.IoBps:0}");
            var hw = s.Hw;
            if (hw != null)
                sb.AppendLine($"hw cpuTemp {hw.CpuTemp} power {hw.CpuPower} volt {hw.CpuVoltage} maxClk {hw.CpuMaxClock} dram {hw.DramReadGBs}/{hw.DramWriteGBs} gpus {string.Join(", ", hw.Gpus.Select(g => $"{g.Name} hot={g.Hotspot} mem={g.MemJunction} fan={g.FanRpm}"))} disks {string.Join(",", hw.DiskTemps.Select(kv => kv.Key + "=" + kv.Value))}");
        }
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        sb.AppendLine($"process cpu time {proc.TotalProcessorTime.TotalMilliseconds:0} ms over {(DateTime.Now - proc.StartTime).TotalSeconds:0.0} s");
        sb.AppendLine("---- LibreHardwareMonitor ----");
        sb.AppendLine($"state: {sampler.HwState}");
        if (sampler.Hw != null) sb.AppendLine(sampler.Hw.Dump());
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return 0;
    }
}
