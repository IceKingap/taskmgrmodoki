# ウィンドウ左上からの相対座標 (X, Y) に左クリックを送る（実カーソルは動かさない。UI 確認用）。
param([int]$X, [int]$Y, [string]$ProcessName = 'SysMon', [int]$Id = 0)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Clk {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr h, POINT p, uint flags);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
  public static void Click(IntPtr top, int sx, int sy) {
    IntPtr cur = top;
    while (true) {
      var p = new POINT { X = sx, Y = sy };
      ScreenToClient(cur, ref p);
      IntPtr child = ChildWindowFromPointEx(cur, p, 1 | 4);
      if (child == IntPtr.Zero || child == cur) break;
      cur = child;
    }
    var c = new POINT { X = sx, Y = sy };
    ScreenToClient(cur, ref c);
    IntPtr l = (IntPtr)((c.Y << 16) | (c.X & 0xFFFF));
    PostMessage(cur, 0x0200, IntPtr.Zero, l);
    PostMessage(cur, 0x0201, (IntPtr)1, l);
    PostMessage(cur, 0x0202, IntPtr.Zero, l);
  }
}
"@
[Clk]::SetThreadDpiAwarenessContext([IntPtr]-4) | Out-Null
$p = $(if ($Id) { Get-Process -Id $Id } else { Get-Process $ProcessName }) | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$r = New-Object Clk+RECT
[Clk]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
[Clk]::Click($p.MainWindowHandle, $r.L + $X, $r.T + $Y)
