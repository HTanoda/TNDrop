# build.ps1 -- TNDrop: publish (self-contained) + Inno Setup installer build
#
# 1. dotnet test          -- 全テストが green であることを確認してから配布物を作る
# 2. dotnet publish        -- Release / win-x64 / self-contained を dist\publish へ出力
# 3. ISCC installer\setup.iss -- dist\TNDrop-Setup-{MyAppVersion}.exe を生成
#
# 本番機はオフライン (ランタイム未インストール) のため、publish 出力は自己完結 (self-contained)
# でなければならない。ISCC のパスは開発機の実際のインストール先に合わせてある。

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

Write-Host "==> dotnet test" -ForegroundColor Cyan
dotnet test
if (-not $?) { throw "dotnet test failed" }

Write-Host "==> dotnet publish (Release / win-x64 / self-contained)" -ForegroundColor Cyan
dotnet publish src/TNDrop.App -c Release -r win-x64 --self-contained true -o dist/publish
if (-not $?) { throw "dotnet publish failed" }

$isccCandidates = @(
    "C:\Program Files\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "ISCC.exe (Inno Setup 7) not found. Install it first (see the inno-setup-packaging skill)."
}

Write-Host "==> $iscc installer\setup.iss" -ForegroundColor Cyan
& $iscc "installer\setup.iss"
if (-not $?) { throw "ISCC (Inno Setup compile) failed" }

$issContent = Get-Content -Path "installer\setup.iss" -Raw
$issMatch = [regex]::Match($issContent, '#define\s+MyAppVersion\s+"([^"]+)"')
if (-not $issMatch.Success) {
    throw "Could not read MyAppVersion from installer\setup.iss"
}
$installerVersion = $issMatch.Groups[1].Value

$installerPath = Join-Path $repoRoot "dist\TNDrop-Setup-$installerVersion.exe"
if (-not (Test-Path $installerPath)) {
    throw "Expected installer not found at $installerPath"
}

$sizeMb = [Math]::Round((Get-Item $installerPath).Length / 1MB, 2)
Write-Host "==> Build complete: $installerPath ($sizeMb MB)" -ForegroundColor Green
