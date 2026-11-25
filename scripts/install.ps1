param(
    [string]$SourceDir = "",
    [string]$TargetDir = "",
    [switch]$Silent
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = Split-Path -Parent $PSScriptRoot
}
$SourceDir = $SourceDir.TrimEnd('\', '/')
$PayloadDir = Join-Path $SourceDir "files"

if (-not (Test-Path (Join-Path $PayloadDir "BepInEx\plugins\TheForestFSR4.dll"))) {
    Write-Host "[ERROR] Plugin files not found at: $PayloadDir" -ForegroundColor Red
    Write-Host "Please extract the entire ZIP archive before running Install_FSR4.bat." -ForegroundColor Yellow
    exit 1
}

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

    foreach ($drive in @("C","D","E","F","G","H")) {
        foreach ($sub in @(
            "SteamLibrary\steamapps\common\The Forest",
            "Program Files (x86)\Steam\steamapps\common\The Forest",
            "Games\Steam\steamapps\common\The Forest",
            "Games\The Forest"
        )) {
            $candidate = "${drive}:\$sub"
            if (Test-Path (Join-Path $candidate "TheForest.exe")) {
                return $candidate
            }
        }
    }

    return $null
}

function Select-FolderDialog {
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "Select your 'The Forest' installation folder (containing TheForest.exe)"
    $dlg.ShowNewFolderButton = $false
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        return $dlg.SelectedPath
    }
    return $null
}

function Detect-GpuInt8Mode {
    $gpuNames = New-Object System.Collections.Generic.List[string]

    try {
        $regRoot = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"
        Get-ChildItem $regRoot -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.PSChildName -match '^\d{4}$') {
                $desc = (Get-ItemProperty -Path $_.PSPath -Name "DriverDesc" -ErrorAction SilentlyContinue).DriverDesc
                if (-not [string]::IsNullOrWhiteSpace($desc)) {
                    $gpuNames.Add($desc)
                }
            }
        }
    } catch {}

    if ($gpuNames.Count -eq 0) {
        try {
            $gpus = Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop
            foreach ($g in $gpus) {
                if (-not [string]::IsNullOrWhiteSpace($g.Name)) {
                    $gpuNames.Add($g.Name)
                }
            }
        } catch {}
    }

    foreach ($name in $gpuNames) {
        if ($name -match "Radeon RX 90\d\d|Radeon AI PRO R9") {
            return @{ Name = $name; Arch = "AMD RDNA 4 (Native FP8)"; Int8 = "auto" }
        }
        if ($name -match "Radeon RX 7\d\d\d|Radeon PRO W7") {
            return @{ Name = $name; Arch = "AMD RDNA 3 (INT8)"; Int8 = "1" }
        }
        if ($name -match "Radeon RX 6\d\d\d|Radeon PRO W6|Radeon 6\d\dM") {
            return @{ Name = $name; Arch = "AMD RDNA 2 (INT8)"; Int8 = "2" }
        }
    }
    foreach ($name in $gpuNames) {
        if ($name -match "NVIDIA|GeForce|RTX|GTX") {
            return @{ Name = $name; Arch = "NVIDIA (Auto)"; Int8 = "auto" }
        }
        if ($name -match "Radeon") {
            return @{ Name = $name; Arch = "AMD Radeon (Auto)"; Int8 = "auto" }
        }
    }
    return @{ Name = "Unknown GPU"; Arch = "Auto"; Int8 = "auto" }
}

function Get-GitHubAssetUrl {
    param([string]$Repo, [string]$Pattern, [string]$FallbackUrl)
    try {
        $headers = @{ "User-Agent" = "TheForest-FSR4-Mod-Installer" }
        $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers -TimeoutSec 15 -ErrorAction Stop
        foreach ($a in $rel.assets) {
            if ($a.name -match $Pattern) {
                return $a.browser_download_url
            }
        }
    } catch {}
    return $FallbackUrl
}

function Download-File {
    param([string]$Url, [string]$OutPath, [string]$Label)
    Write-Host "  Downloading $Label..." -ForegroundColor White
    $curl = Get-Command "curl.exe" -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source -fSL --retry 2 --connect-timeout 20 $Url -o $OutPath
        if ($LASTEXITCODE -ne 0) {
            throw "curl.exe failed to download $Label (exit $LASTEXITCODE)"
        }
    } else {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $Url -OutFile $OutPath -UseBasicParsing
    }
}

function Extract-SevenZip {
    param([string]$ArchivePath, [string]$DestDir)
    New-Item -ItemType Directory -Force -Path $DestDir | Out-Null

    # 1. Built-in Windows 11 / Win10 tar.exe (supports .7z via libarchive)
    $tar = Join-Path $env:SystemRoot "System32\tar.exe"
    if (Test-Path $tar) {
        & $tar -xf $ArchivePath -C $DestDir 2>$null
        if ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $DestDir "OptiScaler.dll"))) {
            return
        }
    }

    # 2. Fallback to 7-Zip if installed
    foreach ($z7 in @(
        "C:\Program Files\7-Zip\7z.exe",
        "C:\Program Files (x86)\7-Zip\7z.exe"
    )) {
        if (Test-Path $z7) {
            & $z7 x -y "-o$DestDir" $ArchivePath | Out-Null
            if ($LASTEXITCODE -eq 0) { return }
        }
    }

    # 3. Fallback to WinRAR if installed
    foreach ($wr in @(
        "C:\Program Files\WinRAR\WinRAR.exe",
        "C:\Program Files (x86)\WinRAR\WinRAR.exe"
    )) {
        if (Test-Path $wr) {
            & $wr x -y $ArchivePath "$DestDir\" | Out-Null
            if ($LASTEXITCODE -eq 0) { return }
        }
    }

    throw "Unable to extract .7z archive. Please install 7-Zip (https://www.7-zip.org/) or update Windows and re-run Install_FSR4.bat."
}

# Check if game is running
$running = Get-Process -Name "TheForest*" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[ERROR] The Forest is currently running. Please close the game and run Install_FSR4.bat again." -ForegroundColor Red
    exit 2
}

$gameDir = Find-TheForestDir -HintDir $TargetDir -ScriptRootDir $SourceDir
if (-not $gameDir -and -not $Silent) {
    Write-Host "Could not auto-detect The Forest automatically. Please select your game folder..." -ForegroundColor Yellow
    $gameDir = Select-FolderDialog
}

if (-not $gameDir -or -not (Test-Path (Join-Path $gameDir "TheForest.exe"))) {
    Write-Host "[ERROR] Could not find TheForest.exe in: $gameDir" -ForegroundColor Red
    exit 3
}

$gpuInfo = Detect-GpuInt8Mode
Write-Host "Game Folder : $gameDir" -ForegroundColor Cyan
Write-Host "Detected GPU: $($gpuInfo.Name) -> $($gpuInfo.Arch)" -ForegroundColor Cyan
Write-Host ""

$tempRoot = Join-Path $env:TEMP ("TheForestFSR4_Setup_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

try {
    # -------------------------------------------------------------------------
    # Step 1: Install BepInEx 5 x64 (local payload if present, else official GitHub)
    # -------------------------------------------------------------------------
    $localBep = Join-Path $PayloadDir "winhttp.dll"
    if (Test-Path $localBep) {
        Write-Host "[1/3] Installing BepInEx 5 from local package..." -ForegroundColor White
        Copy-Item (Join-Path $PayloadDir "winhttp.dll") (Join-Path $gameDir "winhttp.dll") -Force
        Copy-Item (Join-Path $PayloadDir "doorstop_config.ini") (Join-Path $gameDir "doorstop_config.ini") -Force
        Copy-Item (Join-Path $PayloadDir ".doorstop_version") (Join-Path $gameDir ".doorstop_version") -Force
        Copy-Item (Join-Path $PayloadDir "BepInEx\core") (Join-Path $gameDir "BepInEx\core") -Recurse -Force
    } else {
        Write-Host "[1/3] Fetching official BepInEx 5 (x64) from GitHub..." -ForegroundColor White
        $bepUrl = Get-GitHubAssetUrl -Repo "BepInEx/BepInEx" -Pattern '^BepInEx_win_x64_5\..*\.zip$' -FallbackUrl "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.6/BepInEx_win_x64_5.4.23.6.zip"
        $bepZip = Join-Path $tempRoot "bepinex.zip"
        $bepDir = Join-Path $tempRoot "bepinex"
        Download-File -Url $bepUrl -OutPath $bepZip -Label "BepInEx 5 x64"
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($bepZip, $bepDir)
        Copy-Item (Join-Path $bepDir "winhttp.dll") (Join-Path $gameDir "winhttp.dll") -Force
        Copy-Item (Join-Path $bepDir "doorstop_config.ini") (Join-Path $gameDir "doorstop_config.ini") -Force
        if (Test-Path (Join-Path $bepDir ".doorstop_version")) {
            Copy-Item (Join-Path $bepDir ".doorstop_version") (Join-Path $gameDir ".doorstop_version") -Force
        }
        New-Item -ItemType Directory -Force -Path (Join-Path $gameDir "BepInEx") | Out-Null
        Copy-Item (Join-Path $bepDir "BepInEx\core") (Join-Path $gameDir "BepInEx\core") -Recurse -Force
    }

    # -------------------------------------------------------------------------
    # Step 2: Install OptiScaler + AMD FidelityFX FSR 4 (local or official GitHub)
    # -------------------------------------------------------------------------
    $localOpti = Join-Path $PayloadDir "dxgi.dll"
    if (Test-Path $localOpti) {
        Write-Host "[2/3] Installing OptiScaler + FSR 4 from local package..." -ForegroundColor White
        foreach ($f in @("dxgi.dll","amd_fidelityfx_dx12.dll","amd_fidelityfx_upscaler_dx12.dll","fakenvapi.dll","fakenvapi.ini","OptiScaler.ini")) {
            $src = Join-Path $PayloadDir $f
            if (Test-Path $src) { Copy-Item $src (Join-Path $gameDir $f) -Force }
        }
    } else {
        Write-Host "[2/3] Fetching official OptiScaler + AMD FidelityFX FSR 4 from GitHub..." -ForegroundColor White
        $optiUrl = Get-GitHubAssetUrl -Repo "optiscaler/OptiScaler" -Pattern '(?i)^OptiScaler.*\.7z$' -FallbackUrl "https://github.com/optiscaler/OptiScaler/releases/download/v0.9.5/Optiscaler_0.9.5-final.20261116.7z"
        $opti7z = Join-Path $tempRoot "optiscaler.7z"
        $optiDir = Join-Path $tempRoot "optiscaler"
        Download-File -Url $optiUrl -OutPath $opti7z -Label "OptiScaler + FSR 4"
        Write-Host "  Extracting OptiScaler..." -ForegroundColor White
        Extract-SevenZip -ArchivePath $opti7z -DestDir $optiDir

        $optiDll = Join-Path $optiDir "OptiScaler.dll"
        if (-not (Test-Path $optiDll)) {
            throw "OptiScaler.dll was not found inside the downloaded archive."
        }
        Copy-Item $optiDll (Join-Path $gameDir "dxgi.dll") -Force

        foreach ($f in @("amd_fidelityfx_dx12.dll","amd_fidelityfx_upscaler_dx12.dll","fakenvapi.dll","fakenvapi.ini","OptiScaler.ini")) {
            $src = Join-Path $optiDir $f
            if (Test-Path $src) {
                Copy-Item $src (Join-Path $gameDir $f) -Force
            }
        }
    }

    # Configure OptiScaler.ini for The Forest + detected GPU
    $optiIniPath = Join-Path $gameDir "OptiScaler.ini"
    if (Test-Path $optiIniPath) {
        $ini = [IO.File]::ReadAllText($optiIniPath)
        $ini = $ini -replace '(?m)^Dx11Upscaler\s*=.*$', 'Dx11Upscaler = fsr31_12'
        $ini = $ini -replace '(?m)^EnableDlssInputs\s*=.*$', 'EnableDlssInputs = true'
        $ini = $ini -replace '(?m)^UpscalerIndex\s*=.*$', 'UpscalerIndex = 0'
        $ini = $ini -replace '(?m)^Fsr4Update\s*=.*$', 'Fsr4Update = true'
        $ini = $ini -replace '(?m)^Fsr4ForceEnableInt8\s*=.*$', ("Fsr4ForceEnableInt8 = " + $gpuInfo.Int8)
        $ini = $ini -replace '(?m)^LogToFile\s*=.*$', 'LogToFile = true'
        $ini = $ini -replace '(?m)^LogLevel\s*=.*$', 'LogLevel = 2'
        $ini = $ini -replace '(?m)^TargetProcessName\s*=.*$', 'TargetProcessName = TheForest.exe'
        [IO.File]::WriteAllText($optiIniPath, $ini)
    }

    # -------------------------------------------------------------------------
    # Step 3: Install TheForestFSR4 plugin & native bridge
    # -------------------------------------------------------------------------
    Write-Host "[3/3] Installing TheForestFSR4 plugin and native DX11 bridge..." -ForegroundColor White
    New-Item -ItemType Directory -Force -Path (Join-Path $gameDir "BepInEx\plugins"), (Join-Path $gameDir "BepInEx\config") | Out-Null
    Copy-Item (Join-Path $PayloadDir "BepInEx\plugins\TheForestFSR4.dll") (Join-Path $gameDir "BepInEx\plugins\TheForestFSR4.dll") -Force
    Copy-Item (Join-Path $PayloadDir "BepInEx\plugins\TheForestFSR4Native.dll") (Join-Path $gameDir "BepInEx\plugins\TheForestFSR4Native.dll") -Force

    $cfgSrc = Join-Path $PayloadDir "BepInEx\config\com.theforest.fsr4.cfg"
    $cfgDst = Join-Path $gameDir "BepInEx\config\com.theforest.fsr4.cfg"
    if ((Test-Path $cfgSrc) -and -not (Test-Path $cfgDst)) {
        Copy-Item $cfgSrc $cfgDst -Force
    }
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Verify core files exist
$verifyFiles = @(
    "dxgi.dll",
    "amd_fidelityfx_dx12.dll",
    "amd_fidelityfx_upscaler_dx12.dll",
    "OptiScaler.ini",
    "winhttp.dll",
    "doorstop_config.ini",
    "BepInEx\core\BepInEx.Preloader.dll",
    "BepInEx\plugins\TheForestFSR4.dll",
    "BepInEx\plugins\TheForestFSR4Native.dll"
)
$missing = @()
foreach ($vf in $verifyFiles) {
    if (-not (Test-Path (Join-Path $gameDir $vf))) {
        $missing += $vf
    }
}
if ($missing.Count -gt 0) {
    Write-Host "[ERROR] Missing files after install: $($missing -join ', ')" -ForegroundColor Red
    exit 4
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  SUCCESS! AMD FSR 4 has been installed to The Forest!" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "In-game Hotkeys:" -ForegroundColor Yellow
Write-Host "  F9     - Toggle FSR 4 ON / OFF live"
Write-Host "  F10    - Cycle Quality Preset (NativeAA / UltraQuality / Quality / Balanced / Performance / UltraPerformance)"
Write-Host "  Insert - Open OptiScaler overlay (RCAS sharpness, FSR 4 settings, stats)"
Write-Host ""
Write-Host "Config file: $gameDir\BepInEx\config\com.theforest.fsr4.cfg" -ForegroundColor Gray
Write-Host "To uninstall cleanly later, run Uninstall_FSR4.bat." -ForegroundColor Gray
exit 0
