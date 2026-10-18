# Store images for the VIZEN Connector listing (Microsoft Edge Add-ons; also fine for the Chrome Web Store):
#   store/logo-300.png         300 x 300 store logo (the app icon: dark circle with the white V)
#   store/promo-440x280.png    small promotional tile
#   store/screenshot-N.png     1280 x 800 screenshots with a caption, made from docs/screenshots (demo content only)
# Run with: powershell -STA -ExecutionPolicy Bypass -File scripts\make-store-assets.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'store'
New-Item -ItemType Directory -Force $out | Out-Null
$logo = [IO.File]::ReadAllText((Join-Path $root 'VIZEN.App\Themes\Logo.xaml'))
$mark = [System.Windows.Media.Geometry]::Parse([regex]::Match($logo, '<Geometry x:Key="Geo.LogoMark">([^<]+)</Geometry>').Groups[1].Value)
function Color($hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }
function Brush($hex) { New-Object System.Windows.Media.SolidColorBrush (Color $hex) }
$font = New-Object System.Windows.Media.Typeface 'Segoe UI Variable Display, Segoe UI'
$fontBold = New-Object System.Windows.Media.Typeface ((New-Object System.Windows.Media.FontFamily 'Segoe UI Variable Display, Segoe UI'), [System.Windows.FontStyles]::Normal, [System.Windows.FontWeights]::SemiBold, [System.Windows.FontStretches]::Normal)

function Draw-Badge($dc, $cx, $cy, $r) {
  $dc.DrawEllipse((Brush '#111318'), (New-Object System.Windows.Media.Pen (Brush '#2A2F3A'), ($r * 0.02)), (New-Object System.Windows.Point $cx, $cy), $r, $r)
  $b = $mark.Bounds; $w = $r * 2 * 29.0 / 44.0; $s = $w / $b.Width; $h = $b.Height * $s
  $g = New-Object System.Windows.Media.TransformGroup
  $g.Children.Add((New-Object System.Windows.Media.TranslateTransform (-$b.X), (-$b.Y)))
  $g.Children.Add((New-Object System.Windows.Media.ScaleTransform $s, $s))
  $g.Children.Add((New-Object System.Windows.Media.TranslateTransform ($cx - $w / 2), ($cy - $h / 2 + $r * 2 / 44.0)))
  $dc.PushTransform($g); $dc.DrawGeometry([System.Windows.Media.Brushes]::White, $null, $mark); $dc.Pop()
}
function Text($dc, $s, $size, $typeface, $hex, $x, $y, $maxWidth = 0) {
  $ft = New-Object System.Windows.Media.FormattedText $s, ([Globalization.CultureInfo]::InvariantCulture), ([System.Windows.FlowDirection]::LeftToRight), $typeface, $size, (Brush $hex), 1.0
  if ($maxWidth -gt 0) { $ft.MaxTextWidth = $maxWidth; $ft.TextAlignment = 'Center' }
  $dc.DrawText($ft, (New-Object System.Windows.Point $x, $y))
}
function Save($visual, $w, $h, $name) {
  $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $w, $h, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
  $bmp.Render($visual)
  $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
  $f = [IO.File]::Create((Join-Path $out $name)); $enc.Save($f); $f.Close(); $name
}
function Background($dc, $w, $h) {
  $bg = New-Object System.Windows.Media.LinearGradientBrush (Color '#0A0D14'), (Color '#1A0F18'), 45
  $dc.DrawRectangle($bg, $null, (New-Object System.Windows.Rect 0, 0, $w, $h))
  $glow = New-Object System.Windows.Media.RadialGradientBrush (Color '#33FF354E'), (Color '#00FF354E')
  $dc.DrawEllipse($glow, $null, (New-Object System.Windows.Point ($w * 0.5), 0), ($w * 0.6), ($h * 0.55))
}

# logo
$v = New-Object System.Windows.Media.DrawingVisual; $dc = $v.RenderOpen(); Draw-Badge $dc 150 150 148; $dc.Close(); Save $v 300 300 'logo-300.png'

# small promotional tile
$v = New-Object System.Windows.Media.DrawingVisual; $dc = $v.RenderOpen()
Background $dc 440 280; Draw-Badge $dc 220 100 52
Text $dc 'VIZEN Connector' 30 $fontBold '#FFFFFF' 0 168 440
Text $dc 'Every browser tab in VIZEN' 17 $font '#AEB6C6' 0 210 440
$dc.Close(); Save $v 440 280 'promo-440x280.png'

# Microsoft Store 1:1 box art
$v = New-Object System.Windows.Media.DrawingVisual; $dc = $v.RenderOpen()
Background $dc 1080 1080; Draw-Badge $dc 540 430 190
Text $dc 'VIZEN' 96 $fontBold '#FFFFFF' 0 680 1080
Text $dc 'Live overlays for Windows' 40 $font '#AEB6C6' 0 810 1080
$dc.Close(); Save $v 1080 1080 'box-art-1080.png'

# screenshots
$shots = @(
  @('sources.png', 'Every browser tab, on its own', 'Switch to a tab, mute it or move it to its own window, right from VIZEN.'),
  @('audio-mixer.png', 'Tabs with sound, in the mixer', 'See which tab is playing and mute it next to your other apps.'),
  @('browser-tabs.png', 'Set up once. Private by design.', 'Only tab titles and site names stay on your PC. No incognito tabs, no internet.')
)
$n = 1
foreach ($s in $shots) {
  $img = New-Object System.Windows.Media.Imaging.BitmapImage
  $img.BeginInit(); $img.CacheOption = 'OnLoad'; $img.UriSource = New-Object Uri (Join-Path $root "docs\screenshots\$($s[0])"); $img.EndInit()
  $v = New-Object System.Windows.Media.DrawingVisual; $dc = $v.RenderOpen()
  Background $dc 1280 800
  Text $dc $s[1] 40 $fontBold '#FFFFFF' 0 44 1280
  Text $dc $s[2] 20 $font '#AEB6C6' 0 102 1280
  $w = 1120; $h = [int]($w * $img.PixelHeight / $img.PixelWidth); $x = (1280 - $w) / 2; $y = 160
  $rect = New-Object System.Windows.Rect $x, $y, $w, $h
  $dc.PushClip((New-Object System.Windows.Media.RectangleGeometry $rect, 14, 14))
  $dc.DrawImage($img, $rect); $dc.Pop()
  $dc.DrawRoundedRectangle($null, (New-Object System.Windows.Media.Pen (Brush '#2A2F3A'), 1.5), $rect, 14, 14)
  $dc.Close(); Save $v 1280 800 "screenshot-$n.png"; $n++
}
