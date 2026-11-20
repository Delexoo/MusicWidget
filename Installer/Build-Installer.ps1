# Builds the Tauri MusicWidget app and copies a stable installer name for releases.
# Requires: Node.js, Rust, NSIS (via Tauri bundler).
param(
    [switch]$SkipFrontend
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

Write-Host "==> Building MusicWidget (Tauri 2.3.5)..." -ForegroundColor Cyan

if (-not $SkipFrontend) {
    npm run build
    if ($LASTEXITCODE -ne 0) {
        throw "frontend build failed with exit code $LASTEXITCODE"
    }
}

npx tauri build
if ($LASTEXITCODE -ne 0) {
    throw "tauri build failed with exit code $LASTEXITCODE"
}

$nsis = Join-Path $RepoRoot "src-tauri\target\release\bundle\nsis\MusicWidget_2.3.5_x64-setup.exe"
if (-not (Test-Path $nsis)) {
    # Fallback: any MusicWidget_*_x64-setup.exe
    $nsis = Get-ChildItem (Join-Path $RepoRoot "src-tauri\target\release\bundle\nsis") -Filter "MusicWidget_*_x64-setup.exe" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not (Test-Path $nsis)) {
    throw "Tauri NSIS installer not found."
}

$outDir = Join-Path $RepoRoot "Installer\Output"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir "MusicWidget-Setup-x64.exe"
Copy-Item $nsis $out -Force

$sizeMb = [math]::Round((Get-Item $out).Length / 1MB)
$payload = @{ version = "2.3.5"; asset = "MusicWidget-Setup-x64.exe"; sizeMb = $sizeMb } | ConvertTo-Json
Set-Content -Path (Join-Path $RepoRoot "version.json") -Value $payload -Encoding utf8
Set-Content -Path (Join-Path $RepoRoot "website\version.json") -Value $payload -Encoding utf8

$webDownloads = Join-Path $RepoRoot "website\downloads"
New-Item -ItemType Directory -Force -Path $webDownloads | Out-Null
Copy-Item $out (Join-Path $webDownloads "MusicWidget-Setup-x64.exe") -Force

Write-Host "Installer ready: $out ($sizeMb MB)" -ForegroundColor Green
