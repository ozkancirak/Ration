[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Directory,
    [Parameter(Mandatory)]
    [string]$Version
)

# Yayın klasöründeki her dosyanın SHA-256'sını SHA256SUMS.txt'ye yazar. Biçim sha256sum ile
# aynıdır ("<hash>  <dosya>"), bu yüzden indiren kişi `sha256sum -c SHA256SUMS.txt` ya da
# PowerShell'de Get-FileHash ile doğrulayabilir.
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory)
$output = Join-Path $Directory 'SHA256SUMS.txt'

# Yalnızca bu sürümün yüklenecek dosyaları: delta için indirilen önceki sürüm tam paketi
# bu sürümün varlığı değildir.
$assets = Get-ChildItem -LiteralPath $Directory -File |
    Where-Object {
        $_.Name -ne 'SHA256SUMS.txt' -and (
            $_.Name -like "*-$Version-*" -or
            $_.Name -like '*-Setup.exe' -or
            $_.Name -like '*-Portable.zip' -or
            $_.Name -like 'releases.*.json' -or
            $_.Name -like 'assets.*.json' -or
            $_.Name -like 'RELEASES*')
    } |
    Sort-Object Name

if (-not $assets) { throw "Sağlama toplamı yazılacak dosya yok: $Directory" }

$lines = foreach ($asset in $assets) {
    $hash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($asset.Name)"
}

# LF ve BOM'suz: sha256sum -c satır sonu ve BOM'a takılır.
[IO.File]::WriteAllText($output, (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
Write-Host "SHA256SUMS.txt: $($assets.Count) dosya"
