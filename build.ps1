param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'LIMORA\LIMORA.csproj'
$tests = Join-Path $root 'Tests\LIMORA.Tests.csproj'
$dist = Join-Path $root 'dist'

if (-not $SkipTests) {
    dotnet run --project $tests --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'LIMORA tests failed.' }
}

dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
    -p:DebugSymbols=false -p:DebugType=None `
    --output $dist
if ($LASTEXITCODE -ne 0) { throw 'LIMORA publish failed.' }

$exe = Join-Path $dist 'LIMORA.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish did not produce $exe" }
Write-Host "Ready: $exe"
