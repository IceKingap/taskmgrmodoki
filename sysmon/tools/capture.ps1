# 指定プロセスのメインウィンドウを PrintWindow で PNG に保存する（UI 確認用）。
param([string]$ProcessName = 'SysMon', [string]$Out = "$PSScriptRoot\..\shot.png", [int]$Id = 0)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Cap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
}
"@
[Cap]::SetThreadDpiAwarenessContext([IntPtr]-4) | Out-Null
$p = $(if ($Id) { Get-Process -Id $Id } else { Get-Process $ProcessName -ErrorAction Stop }) | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$r = New-Object Cap+RECT
[Cap]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.R - $r.L; $h = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[Cap]::PrintWindow($p.MainWindowHandle, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "$Out ${w}x${h}"
