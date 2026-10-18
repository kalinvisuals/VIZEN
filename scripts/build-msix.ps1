# Builds VIZEN for the Microsoft Store: artifacts\msix\VIZEN-<version>-x64.msix (unsigned: the Store signs it).
#   powershell -STA -ExecutionPolicy Bypass -File scripts\build-msix.ps1
# The identity values are on Partner Center > VIZEN > Product management > Product identity.
# -Publish:$false reuses artifacts\publish\win-x64 from scripts\build-installer.ps1.
param(
  # Partner Center > VIZEN > Product identity (Store ID 9P7K298JRBK2)
  [string]$IdentityName = 'kalinvisuals.VIZEN',
  [string]$Publisher = 'CN=91C5B2B3-F94B-4158-9794-3FC20B6D2E3F',
  [string]$PublisherDisplayName = 'kalinvisuals',
  [bool]$Publish = $true
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'VIZEN.App\VIZEN.App.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$msixVersion = "$version.0"
$publishDir = Join-Path $root 'artifacts\publish\win-x64'
$outDir = Join-Path $root 'artifacts\msix'
$layout = Join-Path $outDir 'layout'
$makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter makeappx.exe | Where-Object FullName -like '*\x64\*' | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe not found (install the Windows SDK)' }

if ($Publish) {
  Write-Host '==> dotnet publish (self-contained, win-x64)'
  if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
  dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir | Out-Host
  if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}
if (-not (Test-Path (Join-Path $publishDir 'VIZEN.App.exe'))) { throw "no published app in $publishDir" }

Write-Host '==> layout'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
Copy-Item $publishDir $layout -Recurse
$manifest = [IO.File]::ReadAllText((Join-Path $root 'installer\msix\AppxManifest.xml'))
$manifest = $manifest.Replace('$IdentityName$', $IdentityName).Replace('$Publisher$', [Security.SecurityElement]::Escape($Publisher)).
  Replace('$PublisherDisplayName$', [Security.SecurityElement]::Escape($PublisherDisplayName)).Replace('$Version$', $msixVersion)
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))

Write-Host '==> logos'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assets = Join-Path $layout 'Assets'; New-Item -ItemType Directory -Force $assets | Out-Null
$logoXaml = [IO.File]::ReadAllText((Join-Path $root 'VIZEN.App\Themes\Logo.xaml'))
$mark = [System.Windows.Media.Geometry]::Parse([regex]::Match($logoXaml, '<Geometry x:Key="Geo.LogoMark">([^<]+)</Geometry>').Groups[1].Value)
function Render($name, $w, $h, $badge) {
  $v = New-Object System.Windows.Media.DrawingVisual; $dc = $v.RenderOpen()
  $r = $badge / 2.0; $cx = $w / 2.0; $cy = $h / 2.0
  $dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x11, 0x13, 0x18))), $null, (New-Object System.Windows.Point $cx, $cy), $r, $r)
  $b = $mark.Bounds; $mw = $badge * 29.0 / 44.0; $s = $mw / $b.Width; $mh = $b.Height * $s
  $g = New-Object System.Windows.Media.TransformGroup
  $g.Children.Add((New-Object System.Windows.Media.TranslateTransform (-$b.X), (-$b.Y)))
  $g.Children.Add((New-Object System.Windows.Media.ScaleTransform $s, $s))
  $g.Children.Add((New-Object System.Windows.Media.TranslateTransform ($cx - $mw / 2), ($cy - $mh / 2 + $badge / 44.0)))
  $dc.PushTransform($g); $dc.DrawGeometry([System.Windows.Media.Brushes]::White, $null, $mark); $dc.Pop(); $dc.Close()
  $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $w, $h, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32); $bmp.Render($v)
  $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
  $f = [IO.File]::Create((Join-Path $assets $name)); $enc.Save($f); $f.Close()
}
foreach ($scale in 100, 200) {
  $k = $scale / 100
  Render "Square44x44Logo.scale-$scale.png" (44 * $k) (44 * $k) (44 * $k)
  Render "Square150x150Logo.scale-$scale.png" (150 * $k) (150 * $k) (100 * $k)
  Render "Wide310x150Logo.scale-$scale.png" (310 * $k) (150 * $k) (100 * $k)
  Render "StoreLogo.scale-$scale.png" (50 * $k) (50 * $k) (50 * $k)
}
foreach ($size in 16, 24, 32, 48, 256) { Render "Square44x44Logo.targetsize-$($size)_altform-unplated.png" $size $size $size }
# unqualified copies (used when no resource index applies)
foreach ($n in 'Square44x44Logo', 'Square150x150Logo', 'Wide310x150Logo', 'StoreLogo') { Copy-Item (Join-Path $assets "$n.scale-100.png") (Join-Path $assets "$n.png") }

Write-Host '==> resources.pri'
$makepri = Join-Path (Split-Path $makeappx.FullName) 'makepri.exe'
$priConfig = Join-Path $outDir 'priconfig.xml'
New-Item -ItemType Directory -Force $outDir | Out-Null
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
& $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri failed' }

Write-Host '==> makeappx'
New-Item -ItemType Directory -Force $outDir | Out-Null
$msix = Join-Path $outDir "VIZEN-$version-x64.msix"
if (Test-Path $msix) { Remove-Item $msix }
& $makeappx.FullName pack /d $layout /p $msix /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }
Write-Host "==> Done: $msix ($([math]::Round((Get-Item $msix).Length / 1MB, 1)) MB, version $msixVersion)"
