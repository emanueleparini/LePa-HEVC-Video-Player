<#
.SYNOPSIS
  Builds LePa HEVC Player: self-contained publish, portable zip and Inno Setup installer.
  Output goes to ./artifacts.
.PARAMETER Sign
  Code-sign the app, the installer and the uninstaller with Azure Artifact Signing.
  Needs the Windows SDK (signtool) and an Azure CLI login (`az login`) with the
  "Artifact Signing Certificate Profile Signer" role; account details are in installer/artifact-signing.json.
#>
param(
    [switch]$SkipInstaller,
    [switch]$Sign
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src/LePaHevcPlayer/LePaHevcPlayer.csproj'
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "LePa HEVC Player $version" -ForegroundColor Cyan

if ($Sign) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $signtool) { throw 'signtool.exe not found: install the Windows SDK' }

    # signtool plug-in that signs through Azure Artifact Signing, downloaded once into .tools/
    $dlibVersion = '1.0.128'
    $dlibDir = Join-Path $root ".tools/artifact-signing-$dlibVersion"
    $dlib = Join-Path $dlibDir 'bin/x64/Azure.CodeSigning.Dlib.dll'
    if (-not (Test-Path $dlib)) {
        $nupkg = Join-Path $root ".tools/artifact-signing-$dlibVersion.zip"
        New-Item -ItemType Directory -Force (Split-Path $nupkg) | Out-Null
        Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.ArtifactSigning.Client/$dlibVersion" -OutFile $nupkg
        Expand-Archive $nupkg -DestinationPath $dlibDir -Force
        Remove-Item $nupkg
    }
    $metadata = Join-Path $root 'installer/artifact-signing.json'
    $signArgs = @('sign', '/fd', 'SHA256', '/tr', 'http://timestamp.acs.microsoft.com', '/td', 'SHA256',
                  '/dlib', $dlib, '/dmdf', $metadata)

    function Invoke-Sign([string[]]$files) {
        & $signtool @signArgs @files
        if ($LASTEXITCODE) { throw "Signing failed: $files" }
    }
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained -o $publish
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

$exe = Join-Path $publish 'LePa HEVC Player.exe'

# Sign only our own binaries, and before the plugin cache: plugins.dat records the plugin DLLs'
# timestamps and sizes, so nothing under libvlc/ may change after it is generated.
if ($Sign) { Invoke-Sign @($exe, (Join-Path $publish 'LePa HEVC Player.dll')) }

# Pre-build libVLC's plugin cache so the first launch only loads the plugins it needs.
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

    $isccArgs = @('/Q', "/DAppVersion=$version", "/DPublishDir=$publish")
    if ($Sign) {
        # Inno signs both Setup.exe and the uninstaller; $q is a quote, $f the quoted file name.
        $command = (@($signtool) + $signArgs | ForEach-Object { if ($_ -match ' ') { "`$q$_`$q" } else { $_ } }) -join ' '
        $isccArgs += @('/DSign', "/Sartifactsigning=$command `$f")
    }
    & $iscc @isccArgs (Join-Path $root 'installer/LePaHevcPlayer.iss')
    if ($LASTEXITCODE) { throw 'Inno Setup failed' }
    $setup = Join-Path $artifacts "LePa-HEVC-Player-Setup-$version-x64.exe"
    Write-Host "Installer: $setup" -ForegroundColor Green
}

if ($Sign) {
    $signed = @($exe) + @(if (-not $SkipInstaller) { $setup })
    & $signtool verify /pa @signed
    if ($LASTEXITCODE) { throw 'Signature verification failed' }
    Write-Host 'Signatures verified' -ForegroundColor Green
}
