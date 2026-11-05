# Builds SteamOptimizer.exe with the C# compiler that ships with Windows (.NET Framework 4.x) - no SDK needed.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out  = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $out | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

# App icon (generated, PNG-in-ICO)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 256,256
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'
$br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Rectangle]::new(0,0,256,256)), ([System.Drawing.Color]::FromArgb(0,150,230)), ([System.Drawing.Color]::FromArgb(20,60,160)), 90.0
$g.FillEllipse($br,6,6,244,244)
$pts = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(144,32),[System.Drawing.PointF]::new(64,144),[System.Drawing.PointF]::new(120,144),[System.Drawing.PointF]::new(104,224),[System.Drawing.PointF]::new(192,104),[System.Drawing.PointF]::new(136,104))
$g.FillPolygon([System.Drawing.Brushes]::White,$pts)
$ms = New-Object System.IO.MemoryStream; $bmp.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$ico = Join-Path $out 'app.ico'
$fs = [System.IO.File]::Create($ico); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
$bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
$bw.Write([uint32]$png.Length); $bw.Write([uint32]22); $bw.Write($png); $bw.Close(); $fs.Close()

$exe = Join-Path $out 'SteamOptimizer.exe'
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$exe" "/win32icon:$ico" `
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll `
  /r:System.ServiceProcess.dll /r:System.Web.Extensions.dll `
  (Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $exe"

# Installer: a small per-user setup exe with the app embedded (no admin rights, no external tooling)
$setup = Join-Path $out 'SteamOptimizer-Setup-0.1.0.exe'
& $csc /nologo /target:winexe /optimize+ "/out:$setup" "/win32icon:$ico" "/resource:$exe,app.exe" `
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
  (Join-Path $root 'installer\Installer.cs')
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
Write-Host "Built $setup"
