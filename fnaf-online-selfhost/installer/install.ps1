# Installs BepInEx (downloaded from its official build server) and the FNAF Online self-host mod into this folder.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$game   = $PSScriptRoot
$bepUrl = 'https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip'
$tmp    = Join-Path $env:TEMP 'bepinex-be788.zip'

try {
    if (-not (Test-Path (Join-Path $game 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'))) {
        Write-Host 'Downloading BepInEx (about 35 MB)...'
        Invoke-WebRequest -UseBasicParsing -Uri $bepUrl -OutFile $tmp
        Write-Host 'Installing BepInEx...'
        Expand-Archive -LiteralPath $tmp -DestinationPath $game -Force
        Remove-Item $tmp -Force
    } else {
        Write-Host 'BepInEx is already installed.'
    }

    Write-Host 'Installing the self-host mod...'
    Copy-Item -Path (Join-Path $PSScriptRoot 'mod\BepInEx\*') -Destination (Join-Path $game 'BepInEx') -Recurse -Force

    Write-Host ''
    Write-Host 'Done! Start the game as normal.'
    Write-Host 'The FIRST start shows a black console window for 1-3 minutes while the mod gets ready - just wait.'
    Write-Host 'If Windows Firewall asks, click Allow.'
}
catch {
    Write-Host ''
    Write-Host "Something went wrong: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'Manual fix: download "BepInEx-Unity.IL2CPP-win-x64" (6.0.0-be.788) from https://builds.bepinex.dev/projects/bepinex_be,'
    Write-Host 'unzip it into this folder, then run Install.bat again.'
}
