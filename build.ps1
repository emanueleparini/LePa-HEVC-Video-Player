<#
.SYNOPSIS
  Builds LePa HEVC Player: self-contained publish, portable zip and Inno Setup installer.
  Output goes to ./artifacts.
#>
param(
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src/LePaHevcPlayer/LePaHevcPlayer.csproj'
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "LePa HEVC Player $version" -ForegroundColor Cyan

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained -o $publish
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# Pre-build libVLC's plugin cache so the first launch only loads the plugins it needs.
$exe = Join-Path $publish 'LePa HEVC Player.exe'
Start-Process $exe -ArgumentList '--build-plugin-cache' -Wait
$cache = Join-Path $publish 'libvlc/win-x64/plugins/plugins.dat'
if (-not (Test-Path $cache)) { throw 'libVLC plugin cache was not generated' }
Write-Host "Plugin cache: $cache" -ForegroundColor Green

$zip = Join-Path $artifacts "LePa-HEVC-Player-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Portable: $zip" -ForegroundColor Green

if (-not $SkipInstaller) {
    $iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $iscc) { throw 'Inno Setup 6 not found (https://jrsoftware.org/isinfo.php), or run with -SkipInstaller' }

    & $iscc /Q "/DAppVersion=$version" "/DPublishDir=$publish" (Join-Path $root 'installer/LePaHevcPlayer.iss')
    if ($LASTEXITCODE) { throw 'Inno Setup failed' }
    Write-Host "Installer: $(Join-Path $artifacts "LePa-HEVC-Player-Setup-$version-x64.exe")" -ForegroundColor Green
}
