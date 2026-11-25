param(
    [string]$SourceDir = "",
    [string]$TargetDir = "",
    [switch]$Silent
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = Split-Path -Parent $PSScriptRoot
}
$SourceDir = $SourceDir.TrimEnd('\', '/')

function Find-TheForestDir {
    param([string]$HintDir, [string]$ScriptRootDir)

    if (-not [string]::IsNullOrWhiteSpace($HintDir) -and (Test-Path (Join-Path $HintDir "TheForest.exe"))) {
        return $HintDir
    }
    if (Test-Path (Join-Path $ScriptRootDir "TheForest.exe")) {
        return $ScriptRootDir
    }

    $steamPaths = @()
    foreach ($reg in @(
        "HKCU:\Software\Valve\Steam",
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKLM:\SOFTWARE\Valve\Steam"
    )) {
        try {
            $props = Get-ItemProperty -Path $reg -ErrorAction SilentlyContinue
            if ($props.SteamPath)    { $steamPaths += $props.SteamPath }
            if ($props.InstallPath)  { $steamPaths += $props.InstallPath }
        } catch {}
    }

    $libraries = New-Object System.Collections.Generic.List[string]
    foreach ($sp in ($steamPaths | Select-Object -Unique)) {
        $spNorm = $sp -replace '/', '\'
        if (Test-Path $spNorm) {
            if (-not $libraries.Contains($spNorm)) { $libraries.Add($spNorm) }
            $vdf = Join-Path $spNorm "steamapps\libraryfolders.vdf"
            if (Test-Path $vdf) {
                $content = Get-Content $vdf -Raw -ErrorAction SilentlyContinue
                $matches = [regex]::Matches($content, '"path"\s+"([^"]+)"')
                foreach ($m in $matches) {
                    $lib = ($m.Groups[1].Value -replace '\\\\', '\').TrimEnd('\')
                    if ((Test-Path $lib) -and -not $libraries.Contains($lib)) {
                        $libraries.Add($lib)
                    }
                }
            }
        }
    }

    foreach ($lib in $libraries) {
        $candidate = Join-Path $lib "steamapps\common\The Forest"
        if (Test-Path (Join-Path $candidate "TheForest.exe")) {
            return $candidate
        }
    }
    return $null
}

$running = Get-Process -Name "TheForest*" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[ERROR] The Forest is currently running. Please close the game before uninstalling." -ForegroundColor Red
    exit 2
}

$gameDir = Find-TheForestDir -HintDir $TargetDir -ScriptRootDir $SourceDir
if (-not $gameDir -and -not $Silent) {
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "Select your 'The Forest' installation folder (containing TheForest.exe)"
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $gameDir = $dlg.SelectedPath
    }
}

if (-not $gameDir -or -not (Test-Path (Join-Path $gameDir "TheForest.exe"))) {
    Write-Host "[ERROR] Could not find TheForest.exe." -ForegroundColor Red
    exit 3
}

Write-Host "Uninstalling FSR 4 Mod from: $gameDir" -ForegroundColor Cyan

$filesToRemove = @(
    "dxgi.dll",
    "amd_fidelityfx_dx12.dll",
    "amd_fidelityfx_upscaler_dx12.dll",
    "fakenvapi.dll",
    "fakenvapi.ini",
    "fakenvapi.log",
    "OptiScaler.ini",
    "OptiScaler.log",
    "winhttp.dll",
    "doorstop_config.ini",
    ".doorstop_version",
    "TheForestFSR4Native.log"
)

foreach ($f in $filesToRemove) {
    $p = Join-Path $gameDir $f
    if (Test-Path $p) {
        Remove-Item -Path $p -Force
        Write-Host "  Removed $f"
    }
}

$bepDir = Join-Path $gameDir "BepInEx"
if (Test-Path $bepDir) {
    Remove-Item -Path $bepDir -Recurse -Force
    Write-Host "  Removed BepInEx\"
}

Write-Host ""
Write-Host "Uninstall complete. Your The Forest installation is back to vanilla." -ForegroundColor Green
exit 0
