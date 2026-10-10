# Draws assets\vitals.ico: a glass tile with a pulse line. Run once; the .ico is committed.
Add-Type -AssemblyName System.Drawing
$d = Split-Path -Parent $MyInvocation.MyCommand.Path
$s = 256
$bmp = New-Object System.Drawing.Bitmap $s, $s
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 64; $w = $s - 8
$path.AddArc(4, 4, $r, $r, 180, 90); $path.AddArc(4 + $w - $r, 4, $r, $r, 270, 90)
$path.AddArc(4 + $w - $r, 4 + $w - $r, $r, $r, 0, 90); $path.AddArc(4, 4 + $w - $r, $r, $r, 90, 90); $path.CloseFigure()
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $s, $s), ([System.Drawing.Color]::FromArgb(255, 32, 40, 58)), ([System.Drawing.Color]::FromArgb(255, 14, 18, 28))
$g.FillPath($bg, $path)
$g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), 4), $path)
$line = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 40, 0), (New-Object System.Drawing.Point 216, 0), ([System.Drawing.Color]::FromArgb(255, 94, 155, 255)), ([System.Drawing.Color]::FromArgb(255, 111, 224, 255))
$pen = New-Object System.Drawing.Pen $line, 18
$pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
$pts = @((44,140),(92,140),(114,82),(146,182),(168,124),(212,124)) | ForEach-Object { New-Object System.Drawing.Point $_[0], $_[1] }
$g.DrawLines($pen, [System.Drawing.Point[]]$pts)
$g.Dispose()
$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
$bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0)
$bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$png.Length); $bw.Write([uint32]22)
$bw.Write($png); $bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $d 'vitals.ico'), $out.ToArray())
Write-Host 'Wrote vitals.ico'
