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

    # Files from the internet are tagged by Windows; remove the tag so nothing blocks them.
    try { Get-ChildItem -LiteralPath $game -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue } catch { }

    Write-Host ''
    Write-Host 'Checking the install:'
    $need = @('winhttp.dll', 'doorstop_config.ini', 'dotnet\coreclr.dll', 'BepInEx\core\BepInEx.Unity.IL2CPP.dll',
              'BepInEx\plugins\FnafSelfHost\FnafSelfHost.dll', 'BepInEx\plugins\FnafSelfHost\Open.Nat.dll',
              'BepInEx\unity-libs\6000.0.24.zip')
    $bad = 0
    foreach ($f in $need) {
        if (Test-Path -LiteralPath (Join-Path $game $f)) { Write-Host "  OK       $f" }
        else { Write-Host "  MISSING  $f" -ForegroundColor Red; $bad++ }
    }
    if ($bad -gt 0) { throw "$bad file(s) are missing - the install did not finish. Run Install.bat again, or send a screenshot of this window." }

    Write-Host ''
    Write-Host 'Done! Start the game as normal.'
    Write-Host 'The FIRST start shows a black console window for 1-3 minutes while the mod gets ready - just wait.'
    Write-Host 'A black console window should appear when the game starts - if it does not, the mod is NOT running.'
    Write-Host 'If Windows Firewall asks, click Allow.'
}
catch {
    Write-Host ''
    Write-Host "Something went wrong: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'Manual fix: download "BepInEx-Unity.IL2CPP-win-x64" (6.0.0-be.788) from https://builds.bepinex.dev/projects/bepinex_be,'
    Write-Host 'unzip it into this folder, then run Install.bat again.'
}
