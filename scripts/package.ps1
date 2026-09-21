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

$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
$publishDir = Join-Path $outputRoot 'publish'
$releaseDir = Join-Path $outputRoot 'releases'
$icon = Join-Path $repoRoot 'src\Kalan.App\Assets\AppIcon.ico'

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
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
