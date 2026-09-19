#Requires -Version 5.1
<#
  Publishes the single-file pathmemo executable.

  Compression is deliberately OFF: it saves ~40% size but costs 200-400 ms of
  decompression on every launch, and pathmemo is a CLI tool invoked dozens of
  times a day (README section 19.2).
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    # Trimming is ON by default: measured 16.1 MB vs 77.3 MB at P0, and the
    # dependency set is chosen precisely so that trimming stays safe (no
    # reflection-heavy packages - README section 18).
    [switch] $NoTrim,

    # Packs the exe and the README into dist\pathmemo-<runtime>.zip - the form
    # someone downloads, unpacks and double-clicks (README section 19.1).
    [switch] $Zip,

    # Stamps the build, so `pathmemo --version` and the title bar agree with the
    # release it came from. The release workflow passes the git tag; a local build
    # leaves it empty and keeps the number from Directory.Build.props.
    [string] $Version = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\PathMemo\PathMemo.csproj'
$outDir = Join-Path $repo "dist\$Runtime"

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=false',
    '-p:PublishReadyToRun=true',
    '-p:DebugType=embedded',
    '-o', $outDir
)

if (-not $NoTrim) {
    $publishArgs += '-p:PublishTrimmed=true'
    $publishArgs += '-p:TrimMode=partial'
}

if ($Version) {
    $publishArgs += "-p:Version=$Version"
    $publishArgs += "-p:InformationalVersion=$Version"
}

Write-Host "publishing $Runtime -> $outDir" -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $outDir 'pathmemo.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "  $exe" -ForegroundColor Green
Write-Host "  $size MB" -ForegroundColor Green
Write-Host "  double-click it, or run it with a command - both work (README section 2.1)" -ForegroundColor DarkGray

if ($Zip) {
    $name = if ($Version) { "pathmemo-$Version-$Runtime" } else { "pathmemo-$Runtime" }
    $zipPath = Join-Path $repo "dist\$name.zip"

    # Staged next to the output rather than in %TEMP%: a profile path containing an 8.3
    # name (C:\Users\MIXPC~1) makes PowerShell read the tilde as the home directory and
    # Remove-Item then deletes - or refuses to find - the wrong thing. -LiteralPath for
    # the same reason.
    $staging = Join-Path $outDir '.package'

    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Path $staging | Out-Null

    Copy-Item -LiteralPath $exe -Destination $staging
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $staging

    # The changelog travels with the download so an unpacked folder found months later
    # still says which release it is and what that release cannot do yet. The release page
    # says the same, but nobody keeps the page.
    foreach ($name in 'LICENSE', 'CHANGELOG.md') {
        $extra = Join-Path $repo $name
        if (Test-Path -LiteralPath $extra) { Copy-Item -LiteralPath $extra -Destination $staging }
    }

    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Remove-Item -LiteralPath $staging -Recurse -Force

    $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host ""
    Write-Host "  $zipPath" -ForegroundColor Green
    Write-Host "  $zipSize MB" -ForegroundColor Green
}
