# docs/brand/*.svg -> src/Ration.App/Assets altındaki PNG'ler ve AppIcon.ico.
# Chrome ile 1024 px çizer, System.Drawing ile küçültür. 32 px ve altı icon-small.svg'den gelir.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$brand = Join-Path $root 'docs\brand'
$assets = Join-Path $root 'src\Ration.App\Assets'
$chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
$tmp = Join-Path ([IO.Path]::GetTempPath()) "ration-icons"
New-Item -ItemType Directory -Force $tmp | Out-Null

function Render([string]$svg) {
    $html = Join-Path $tmp "$svg.html"
    $png = Join-Path $tmp "$svg.png"
    $src = ([Uri](Join-Path $brand $svg)).AbsoluteUri
    "<html><body style='margin:0'><img src='$src' width=1024 height=1024 style='display:block'></body></html>" |
        Set-Content -Encoding utf8 $html
    # Chrome ilerlemeyi stderr'e yazar; Stop tercihinde bu hata sayılır.
    $ErrorActionPreference = 'Continue'
    & $chrome --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 `
        --window-size=1024,1024 --screenshot="$png" ([Uri]$html).AbsoluteUri 2>$null | Out-Null
    [System.Drawing.Bitmap]::FromFile($png)
}

$large = Render 'icon.svg'
$small = Render 'icon-small.svg'

function Scaled([int]$w, [int]$h, [int]$iconSize) {
    $src = if ($iconSize -le 32) { $small } else { $large }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.DrawImage($src, [int](($w - $iconSize) / 2), [int](($h - $iconSize) / 2), $iconSize, $iconSize)
    $g.Dispose()
    $bmp
}

function Save([string]$name, [int]$w, [int]$h, [int]$iconSize) {
    $bmp = Scaled $w $h $iconSize
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Save 'Square44x44Logo.scale-200.png' 88 88 88
Save 'Square44x44Logo.targetsize-24_altform-unplated.png' 24 24 24
Save 'Square44x44Logo.targetsize-48_altform-lightunplated.png' 48 48 48
Save 'Square150x150Logo.scale-200.png' 300 300 300
Save 'LockScreenLogo.scale-200.png' 48 48 48
Save 'StoreLogo.png' 50 50 50
Save 'Wide310x150Logo.scale-200.png' 620 300 240
Save 'SplashScreen.scale-200.png' 1240 600 400

# ICO: PNG sıkıştırmalı girdiler (Vista+).
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$blobs = foreach ($s in $sizes) {
    $bmp = Scaled $s $s $s
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$out = New-Object IO.BinaryWriter ([IO.File]::Create((Join-Path $assets 'AppIcon.ico')))
$out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $out.Write([byte]$d); $out.Write([byte]$d); $out.Write([byte]0); $out.Write([byte]0)
    $out.Write([uint16]1); $out.Write([uint16]32)
    $out.Write([uint32]$blobs[$i].Length); $out.Write([uint32]$offset)
    $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $out.Write($b) }
$out.Close()

$large.Dispose(); $small.Dispose()
Write-Host "Ikonlar yazildi: $assets"
