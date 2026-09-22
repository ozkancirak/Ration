[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot 'artifacts\release'
} else {
    $OutputRoot
}
$project = Join-Path $repoRoot 'src\Kalan.App\Kalan.App.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Kalan.App.csproj içinde Version bulunamadı.'
}

$outputPath = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot
} else {
    Join-Path $repoRoot $OutputRoot
}
$outputRoot = [IO.Path]::GetFullPath($outputPath)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))

# OutputRoot is user input. Keep packaging inside the repository's explicit
# artifacts area and never recursively remove the root itself.
$artifactsUri = [Uri]::new($artifactsRoot.TrimEnd('\') + '\')
$outputUri = [Uri]::new($outputRoot.TrimEnd('\') + '\')
$relativeUri = $artifactsUri.MakeRelativeUri($outputUri)
$relativeOutput = [Uri]::UnescapeDataString($relativeUri.ToString())
if ($relativeUri.IsAbsoluteUri -or
    $relativeOutput.StartsWith('../', [StringComparison]::Ordinal) -or
    $relativeOutput.StartsWith('..\', [StringComparison]::Ordinal)) {
    throw "-OutputRoot yalnızca repo\artifacts altında olabilir: $outputRoot"
}

if (Test-Path -LiteralPath $outputRoot) {
    $outputItem = Get-Item -LiteralPath $outputRoot -Force
    if ($outputItem.PSIsContainer -and
        ($outputItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "-OutputRoot reparse/junction olamaz: $outputRoot"
    }
}

$publishDir = Join-Path $outputRoot 'publish'
$releaseDir = Join-Path $outputRoot 'releases'
$icon = Join-Path $repoRoot 'src\Kalan.App\Assets\AppIcon.ico'

foreach ($ownedDir in @($publishDir, $releaseDir)) {
    if (-not (Test-Path -LiteralPath $ownedDir)) { continue }

    $ownedItem = Get-Item -LiteralPath $ownedDir -Force
    if (-not $ownedItem.PSIsContainer -or
        ($ownedItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Paketleme çıktı klasörü normal bir klasör değil: $ownedDir"
    }

    Remove-Item -LiteralPath $ownedDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir, $releaseDir | Out-Null

Push-Location $repoRoot
try {
    dotnet restore Kalan.slnx
    dotnet tool restore
    dotnet publish $project `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:WindowsAppSDKSelfContained=true `
        -p:WindowsPackageType=None `
        -p:Version=$version `
        --no-restore `
        -o $publishDir

    $mainExe = Join-Path $publishDir 'Kalan.App.exe'
    if (-not (Test-Path -LiteralPath $mainExe)) {
        throw "Publish çıktısında Kalan.App.exe yok: $publishDir"
    }

    dotnet tool run vpk pack `
        --packId Kalan `
        --packVersion $version `
        --packTitle Kalan `
        --packDir $publishDir `
        --mainExe Kalan.App.exe `
        --icon $icon `
        --channel win-x64 `
        --outputDir $releaseDir

    $setup = Get-ChildItem -LiteralPath $releaseDir -Filter '*-Setup.exe' -File
    $portable = Get-ChildItem -LiteralPath $releaseDir -Filter '*-Portable.zip' -File
    if (-not $setup -or -not $portable) {
        throw "Velopack Setup.exe veya portable zip üretmedi: $releaseDir"
    }

    Write-Host "Sürüm: $version"
    Write-Host "Setup: $($setup.FullName)"
    Write-Host "Portable: $($portable.FullName)"
}
finally {
    Pop-Location
}
