<#
.SYNOPSIS
    Publishes PairUp as a self-contained win-x64 binary and compiles it into a proper
    Setup.exe installer with Inno Setup.
.PARAMETER Version
    Version string to stamp into the installer (e.g. "0.2.0"). Defaults to the AppVersion
    constant currently set in MainWindow.xaml.cs.
#>
param(
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "src\PairUp.App\PairUp.App.csproj"
$mainWindowCs = Join-Path $root "src\PairUp.App\MainWindow.xaml.cs"
$issPath = Join-Path $PSScriptRoot "PairUp.iss"

if (-not $Version) {
    $match = Select-String -Path $mainWindowCs -Pattern 'AppVersion = "([\d.]+)"' | Select-Object -First 1
    if (-not $match) { throw "Could not determine version from MainWindow.xaml.cs; pass -Version explicitly." }
    $Version = $match.Matches[0].Groups[1].Value
}

Write-Host "Publishing PairUp v$Version (self-contained win-x64)..." -ForegroundColor Cyan
dotnet publish $csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$iscc = Get-ChildItem -Path "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
                             "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                             "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" `
                       -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe (Inno Setup compiler) not found. Install Inno Setup 6 first." }

Write-Host "Compiling installer with $($iscc.FullName)..." -ForegroundColor Cyan
& $iscc.FullName "/DMyAppVersion=$Version" $issPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

$output = Join-Path $PSScriptRoot "Output\PairUp-Setup-$Version.exe"
Write-Host "Installer built: $output" -ForegroundColor Green
