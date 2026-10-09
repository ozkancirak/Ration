[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    # Önceki sürümün tam paketi bu depodan indirilir; vpk pack bununla fark (delta) paketi üretir.
    # Kullanıcı yalnızca değişen kısmı indirir. Önceki sürüm yoksa ya da inmezse yalnızca tam paket çıkar.
    [string]$DeltaFromRepo
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot 'artifacts\release'
} else {
    $OutputRoot
}
$project = Join-Path $repoRoot 'src\Ration.App\Ration.App.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Ration.App.csproj içinde Version bulunamadı.'
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
$icon = Join-Path $repoRoot 'src\Ration.App\Assets\AppIcon.ico'

foreach ($ownedDir in @($publishDir, $releaseDir)) {
    if (-not (Test-Path -LiteralPath $ownedDir)) { continue }
    if (-not $ownedDir.StartsWith($artifactsRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Paketleme yalnızca artifacts altını temizleyebilir: $ownedDir" }

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
    dotnet restore Ration.slnx
    dotnet tool restore

    if ($DeltaFromRepo) {
        $download = @('download', 'github', '--repoUrl', $DeltaFromRepo, '--outputDir', $releaseDir, '--channel', 'win-x64')
        if ($env:GITHUB_TOKEN) { $download += @('--token', $env:GITHUB_TOKEN) }
        dotnet tool run vpk @download
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'Önceki sürüm indirilemedi; yalnızca tam paket üretilecek.'
            $global:LASTEXITCODE = 0
        }
    }

    dotnet publish $project `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:WindowsAppSDKSelfContained=true `
        -p:WindowsPackageType=None `
        -p:Version=$version `
        --no-restore `
        -o $publishDir

    $mainExe = Join-Path $publishDir 'Ration.exe'
    if (-not (Test-Path -LiteralPath $mainExe)) {
        throw "Publish çıktısında Ration.exe yok: $publishDir"
    }

    dotnet tool run vpk pack `
        --packId Ration `
        --packVersion $version `
        --packTitle Ration `
        --packDir $publishDir `
        --mainExe Ration.exe `
        --icon $icon `
        --channel win-x64 `
        --outputDir $releaseDir

    # Paketlenen uygulama açılıyor mu ve csproj'daki sürümü mü bildiriyor? Yayından önce yakalanır.
    $smokeOutput = Join-Path $outputRoot 'smoke-version.txt'
    $smoke = Start-Process -FilePath $mainExe -ArgumentList '--version' -PassThru -RedirectStandardOutput $smokeOutput
    $null = $smoke.Handle  # tutulmazsa ExitCode, çıkıştan sonra boş gelir
    if (-not $smoke.WaitForExit(30000)) {
        $smoke.Kill()
        throw 'Paketlenen uygulama --version ile 30 saniyede kapanmadı.'
    }
    $reported = if (Test-Path -LiteralPath $smokeOutput) { (Get-Content -LiteralPath $smokeOutput -Raw).Trim() } else { '' }
    if ($smoke.ExitCode -ne 0 -or $reported -ne "Ration $version") {
        throw "Paketlenen uygulama sürüm testini geçemedi (çıkış kodu $($smoke.ExitCode), çıktı '$reported', beklenen 'Ration $version')."
    }
    Write-Host "Sürüm testi: $reported"

    $deltas = @(Get-ChildItem -LiteralPath $releaseDir -Filter '*-delta.nupkg' -File)
    if ($DeltaFromRepo) { Write-Host "Delta paketleri: $($deltas.Count)" }

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
