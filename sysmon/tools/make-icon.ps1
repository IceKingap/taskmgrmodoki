# app.ico を生成する（PNG 圧縮の多解像度 ICO）。ビルド前に 1 度実行すればよい。
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\app.ico'
$sizes = 16, 24, 32, 48, 64, 256
$pngs = @()
foreach ($n in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [single]($n * 0.2)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = [single]($n - 1)
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($w - $r, 0, $r, $r, 270, 90)
    $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $path.AddArc(0, $w - $r, $r, $r, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#1a1a19'))), $path)
    # 折れ線 + 面
    $ys = 0.72, 0.60, 0.66, 0.38, 0.50, 0.24, 0.34
    $pts = @()
    for ($i = 0; $i -lt $ys.Count; $i++) {
        $x = $n * (0.14 + 0.72 * $i / ($ys.Count - 1))
        $pts += New-Object System.Drawing.PointF ([single]$x), ([single]($n * $ys[$i]))
    }
    $area = $pts + @((New-Object System.Drawing.PointF $pts[-1].X, ([single]($n * 0.86))), (New-Object System.Drawing.PointF $pts[0].X, ([single]($n * 0.86))))
    $blue = [System.Drawing.ColorTranslator]::FromHtml('#3987e5')
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(90, $blue))), [System.Drawing.PointF[]]$area)
    $pen = New-Object System.Drawing.Pen $blue, ([single][Math]::Max(1.3, $n * 0.075))
    $pen.LineJoin = 'Round'
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Output "wrote $out"
