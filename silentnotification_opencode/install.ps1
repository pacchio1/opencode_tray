<#
    Opencode Tray - installer
    Installs the icon in the Windows notification area + the opencode plugin.
    Non richiede diritti di amministratore (tutto in HKCU / LOCALAPPDATA).

    Uso:
      powershell -ExecutionPolicy Bypass -File install.ps1
      powershell -ExecutionPolicy Bypass -File install.ps1 -NoStartWithWindows -Port 47110
#>
[CmdletBinding()]
param(
    [string] $InstallDir,
    [int]    $Port = 47110,
    [switch] $NoStartWithWindows,
    [switch] $NoPlugin,
    [switch] $NoLaunch,
    [switch] $Force
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppName = "Opencode Tray"
$Version = "1.0.0"
$ExeName = "OpencodeTray.exe"

if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\opencode-tray"
}
$exePath = Join-Path $InstallDir $ExeName
$settingsDir = Join-Path $env:APPDATA "opencode-tray"
$settingsFile = Join-Path $settingsDir "settings.json"
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$startLink = Join-Path $startMenu "$AppName.lnk"
$startupDir = Join-Path $startMenu "Startup"
$startupLink = Join-Path $startupDir "OpencodeTray.lnk"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OpencodeTray"
$pluginDir = Join-Path $env:USERPROFILE ".config\opencode\plugins"
$pluginSrc = Join-Path $Root "plugin\opencode-tray.js"
$pluginDst = Join-Path $pluginDir "opencode-tray.js"
$sourceFile = Join-Path $Root "src\OpencodeTray.cs"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Note($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

function Stop-Tray {
    Get-Process -Name "OpencodeTray" -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force } catch { }
    }
    Start-Sleep -Milliseconds 600
}

function New-Shortcut($linkPath, $target) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $sc = $shell.CreateShortcut($linkPath)
        $sc.TargetPath = $target
        $sc.WorkingDirectory = (Split-Path -Parent $target)
        $sc.WindowStyle = 7
        $sc.IconLocation = "$target,0"
        $sc.Description = "$AppName - opencode status in the icon tray"
        $sc.Save()
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}

Write-Host ""
Write-Host "  $AppName - installazione" -ForegroundColor White
Write-Host "  ---------------------------------------------" -ForegroundColor DarkGray
Write-Host ""

# ---------------------------------------------------------------- 1. chiusura + cartella
Write-Step "Preparazione di $InstallDir"
Stop-Tray
if (-not (Test-Path -LiteralPath $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir | Out-Null }
Write-Ok "cartella pronta"

# ---------------------------------------------------------------- 2. compilazione
Write-Step "Compilazione di $ExeName"
$needBuild = $true
if ((Test-Path -LiteralPath $exePath) -and -not $Force) {
    $srcTime = (Get-Item -LiteralPath $sourceFile).LastWriteTimeUtc
    if ((Get-Item -LiteralPath $exePath).LastWriteTimeUtc -ge $srcTime) { $needBuild = $false }
}
if ($needBuild) {
    & (Join-Path $Root "build.ps1") -OutputDir $InstallDir
} else {
    Write-Ok "binary already present and updated"
}
if (-not (Test-Path -LiteralPath $exePath)) { throw "$ExeName not found after compilation." }
Copy-Item -LiteralPath (Join-Path $Root "uninstall.ps1") -Destination (Join-Path $InstallDir "uninstall.ps1") -Force
Write-Ok "installato in $exePath"

# ---------------------------------------------------------------- 3. impostazioni
Write-Step "Impostazioni"
if (-not (Test-Path -LiteralPath $settingsDir)) { New-Item -ItemType Directory -Path $settingsDir | Out-Null }
if ((Test-Path -LiteralPath $settingsFile) -and -not $Force) {
    Write-Ok "impostazioni esistenti conservate ($settingsFile)"
} else {
    $json = @"
{
  "port": $Port,
  "balloon": false,
  "sound": false,
  "flash": false,
  "heartbeatTimeoutSec": 25,
  "startWithWindows": $((-not $NoStartWithWindows).ToString().ToLower())
}
"@
    Set-Content -LiteralPath $settingsFile -Value $json -Encoding UTF8
    Write-Ok "create $settingsFile (porta $Port)"
}

# ---------------------------------------------------------------- 4. scorciatoie
Write-Step "Scorciatoie"
New-Shortcut -linkPath $startLink -target $exePath
Write-Ok "menu Start: $startLink"
if ($NoStartWithWindows) {
    if (Test-Path -LiteralPath $startupLink) { Remove-Item -LiteralPath $startupLink -Force }
    Write-Note "avvio automatico NON attivato"
} else {
    if (-not (Test-Path -LiteralPath $startupDir)) { New-Item -ItemType Directory -Path $startupDir | Out-Null }
    New-Shortcut -linkPath $startupLink -target $exePath
    Write-Ok "avvio automatico attivato: $startupLink"
}

# ---------------------------------------------------------------- 5. entry in "Programmi e caratteristiche"
Write-Step "Voce in Programmi e caratteristiche"
if (-not (Test-Path -LiteralPath $uninstallKey)) { New-Item -Path $uninstallKey | Out-Null }
$sizeKb = [math]::Round(((Get-ChildItem -LiteralPath $InstallDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1KB), 0)
New-ItemProperty -LiteralPath $uninstallKey -Name "DisplayName"      -Value $AppName                -PropertyType String -Force | Out-Null
New-ItemProperty -LiteralPath $uninstallKey -Name "DisplayVersion"   -Value $Version                -PropertyType String -Force | Out-Null
New-ItemProperty -LiteralPath $uninstallKey -Name "Publisher"        -Value "opencode"              -PropertyType String -Force | Out-Null
New-ItemProperty -LiteralPath $uninstallKey -Name "DisplayIcon"      -Value "$exePath,0"            -PropertyType String -Force | Out-Null
New-ItemProperty -LiteralPath $uninstallKey -Name "InstallLocation"  -Value $InstallDir             -PropertyType String -Force | Out-Null
New-ItemProperty -LiteralPath $uninstallKey -Name "HelpLink"         -Value "https://opencode.ai"    -PropertyType String -Force | Out-Null
New-ItemProperty -LiteralPath $uninstallKey -Name "UninstallString"  -Value "powershell -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`"" -PropertyType String -Force | Out-Null
New-ItemProperty -LiteralPath $uninstallKey -Name "QuietUninstallString" -Value "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$InstallDir\uninstall.ps1`" -Silent" -PropertyType String -Force | Out-Null
New-ItemProperty -LiteralPath $uninstallKey -Name "NoModify"         -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -LiteralPath $uninstallKey -Name "NoRepair"         -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -LiteralPath $uninstallKey -Name "EstimatedSize"    -Value ([int]$sizeKb) -PropertyType DWord -Force | Out-Null
Write-Ok "creata: $uninstallKey"

# ---------------------------------------------------------------- 6. plugin opencode
Write-Step "Plugin opencode"
if ($NoPlugin) {
    Write-Note "plugin NON installato (opzione -NoPlugin)"
} elseif (-not (Test-Path -LiteralPath $pluginSrc)) {
    Write-Note "sorgente plugin non trovato: $pluginSrc"
} else {
    if (-not (Test-Path -LiteralPath $pluginDir)) { New-Item -ItemType Directory -Path $pluginDir | Out-Null }
    Copy-Item -LiteralPath $pluginSrc -Destination $pluginDst -Force
    Write-Ok "installato: $pluginDst"
}

$oc = (Get-Command opencode -ErrorAction SilentlyContinue)
if ($oc) { Write-Ok "opencode found at $($oc.Source)" } else { Write-Note "opencode not found in PATH" }

# ---------------------------------------------------------------- 7. avvio
if ($NoLaunch) {
    Write-Step "Avvio"
    Write-Note "non avviato (opzione -NoLaunch)"
} else {
    Write-Step "Avvio di $AppName"
    Start-Process -FilePath $exePath -WindowStyle Hidden
    Start-Sleep -Milliseconds 1200
    if (Get-Process -Name "OpencodeTray" -ErrorAction SilentlyContinue) {
        Write-Ok "in esecuzione"
    } else {
        Write-Note "processo non avviato"
    }
}

Write-Host ""
Write-Host "  Installazione completata." -ForegroundColor Green
Write-Host "  ---------------------------------------------" -ForegroundColor DarkGray
Write-Host "  1. The icon appears in the hidden icon tray (arrow ^ at bottom right)."
Write-Host "  2. RESTART opencode, so the plugin gets loaded."
Write-Host "  3. green = working | yellow = finished / needs response | black = inactive."
Write-Host "  4. Stealth: no popup and no sound (activated from menu with double click on icon)."
Write-Host "  5. To remove: Programs and Features > $AppName > Uninstall"
Write-Host ""