# builds a self contained clipnest folder and a zip file you can share.
# run it from the repository root with: powershell -executionpolicy bypass -file scripts\package.ps1
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "publish\$Runtime"
$distDir = Join-Path $root "dist"
$zipPath = Join-Path $distDir "ClipNest-$Runtime.zip"

# start from a clean output folder
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# run the tests first so a broken build is never packaged
dotnet test (Join-Path $root "tests\ClipNest.Core.Tests\ClipNest.Core.Tests.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

# one exe that includes the .net runtime, so users do not need to install .net
dotnet publish (Join-Path $root "src\ClipNest.App\ClipNest.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Copy-Item (Join-Path $root "LICENSE") $publishDir
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.md") $publishDir

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath

Write-Host "done: $zipPath"
