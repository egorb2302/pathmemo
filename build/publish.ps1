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

    [switch] $Trimmed
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

if ($Trimmed) {
    $publishArgs += '-p:PublishTrimmed=true'
    $publishArgs += '-p:TrimMode=partial'
}

Write-Host "publishing $Runtime -> $outDir" -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $outDir 'pathmemo.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "  $exe" -ForegroundColor Green
Write-Host "  $size MB" -ForegroundColor Green
