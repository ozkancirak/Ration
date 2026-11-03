[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Directory,
    [Parameter(Mandatory)]
    [string]$Version
)

# Writes the SHA-256 of every file in the release folder to SHA256SUMS.txt. The format is the same as sha256sum
# ("<hash>  <file>"), so the downloader can verify with `sha256sum -c SHA256SUMS.txt` or
# with Get-FileHash in PowerShell.
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory)
$output = Join-Path $Directory 'SHA256SUMS.txt'

# Only the files the user downloads: this release's packages, the installer and the portable zip.
# The previous release's package downloaded for the delta is not an asset of this release; the feed files
# (releases.*.json, assets.*.json, RELEASES*) are rewritten by vpk when it uploads, or not uploaded,
# so their digests would not match the ones in the release.
$assets = Get-ChildItem -LiteralPath $Directory -File |
    Where-Object {
        $_.Name -ne 'SHA256SUMS.txt' -and (
            $_.Name -like "*-$Version-*.nupkg" -or
            $_.Name -like '*-Setup.exe' -or
            $_.Name -like '*-Portable.zip')
    } |
    Sort-Object Name

if (-not $assets) { throw "Sağlama toplamı yazılacak dosya yok: $Directory" }

$lines = foreach ($asset in $assets) {
    $hash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($asset.Name)"
}

# LF and no BOM: sha256sum -c trips on line endings and a BOM.
[IO.File]::WriteAllText($output, (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
Write-Host "SHA256SUMS.txt: $($assets.Count) dosya"
