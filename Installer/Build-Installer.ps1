# Build the full installer end-to-end.
#
# Usage (from anywhere):
#   powershell -ExecutionPolicy Bypass -File Installer\Build-Installer.ps1
#
# 1. Regenerates AppIcon.ico from the source PNG (so updating the PNG is
#    enough; you don't have to remember to run the icon builder separately).
# 2. Publishes a self-contained win-x64 build (apphost + DLLs + libvlc) for Inno.
# 3. Invokes Inno Setup Compiler (iscc.exe) to package it.

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RepoRoot,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

# Resolve $PSScriptRoot defensively — Windows PowerShell occasionally leaves it
# empty when a script is launched via "powershell -File" from another script,
# and we need a stable anchor for relative paths.
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptDir
}

function Resolve-IsccPath {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\iscc.exe",
        "${env:ProgramFiles}\Inno Setup 6\iscc.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    # Fall back to PATH lookup.
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    throw "Could not find Inno Setup Compiler. Install Inno Setup 6 from https://jrsoftware.org/isdl.php and re-run this script."
}

Write-Host "==> Regenerating AppIcon.ico from PNG..." -ForegroundColor Cyan
# Build-Icon.ps1 is a pure PowerShell script (no native calls) so $LASTEXITCODE
# isn't a reliable signal; rely on PowerShell's -ErrorActionPreference=Stop
# (set above) to surface any real failure as an exception instead.
& (Join-Path $scriptDir "Build-Icon.ps1")

if (-not $SkipPublish) {
    Write-Host "`n==> Publishing MusicWidget ($Configuration, win-x64, self-contained)..." -ForegroundColor Cyan
    $proj = Join-Path $RepoRoot "MusicWidget\MusicWidget.csproj"
    $pubProfile = "Properties\PublishProfiles\WinX64SelfContained.pubxml"

    # The .NET 10 SDK has a cold-cache target-ordering quirk where the very
    # first WPF build after a clean fails because MarkupCompilePass1 hasn't
    # populated MusicWidget_Content.g.cs yet. A second invocation always
    # succeeds because the generated files now exist. We retry once silently
    # so CI / one-shot release builds aren't broken by it.
    & dotnet publish $proj -c $Configuration -p:PublishProfile=$pubProfile --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "First publish failed (likely WPF cold-build quirk); retrying once..."
        & dotnet publish $proj -c $Configuration -p:PublishProfile=$pubProfile --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed twice (exit $LASTEXITCODE)." }
    }
}

Write-Host "`n==> Running Inno Setup Compiler..." -ForegroundColor Cyan
$iscc = Resolve-IsccPath
$iss  = Join-Path $scriptDir "MusicWidget.iss"
$csproj = Join-Path $RepoRoot "MusicWidget\MusicWidget.csproj"
$verMatch = Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
$appVersion = if ($verMatch) { $verMatch.Matches[0].Groups[1].Value } else { "2.2.0" }
Write-Host "    Version: $appVersion" -ForegroundColor DarkGray
& $iscc "/DMyAppVersion=$appVersion" $iss
if ($LASTEXITCODE -ne 0) { throw "iscc failed (exit $LASTEXITCODE)." }

$out = Join-Path $scriptDir "Output\MusicWidget-Setup-x64.exe"
if (Test-Path $out) {
    $sizeMb = [math]::Round((Get-Item $out).Length / 1MB, 1)
    Write-Host "`nInstaller built: $out ($sizeMb MB)" -ForegroundColor Green

    $webDownloads = Join-Path $RepoRoot "website\downloads"
    if (-not (Test-Path $webDownloads)) {
        New-Item -ItemType Directory -Path $webDownloads -Force | Out-Null
    }
    $webOut = Join-Path $webDownloads "MusicWidget-Setup-x64.exe"
    Copy-Item $out $webOut -Force
    Write-Host "Copied to website\downloads\MusicWidget-Setup-x64.exe (local preview)" -ForegroundColor Green

    $versionJson = Join-Path $RepoRoot "version.json"
    $webVersionJson = Join-Path $RepoRoot "website\version.json"
    $payload = @{ version = $appVersion; asset = "MusicWidget-Setup-x64.exe"; sizeMb = $sizeMb }
    if (Test-Path $versionJson) {
        $payload | ConvertTo-Json | Set-Content -Path $versionJson -Encoding UTF8
        Write-Host "Updated version.json ($appVersion, ${sizeMb} MB)" -ForegroundColor Green
    }
    $payload | ConvertTo-Json | Set-Content -Path $webVersionJson -Encoding UTF8
    Write-Host "Updated website\version.json ($appVersion, ${sizeMb} MB)" -ForegroundColor Green

    Write-Host "`nPublish to GitHub (website download button):" -ForegroundColor Cyan
    Write-Host "  git tag v$appVersion" -ForegroundColor DarkGray
    Write-Host "  git push origin v$appVersion" -ForegroundColor DarkGray
    Write-Host "  (or Actions -> Release -> Run workflow)" -ForegroundColor DarkGray
} else {
    Write-Warning "iscc completed but the expected installer file wasn't found at $out."
}
