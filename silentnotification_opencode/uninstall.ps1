<#
    Opencode Tray - disinstallatore
    Rimuove processo, scorciatoie, voce di registro, impostazioni, plugin opencode
    e la cartella di installazione.

    Funziona sia lanciato dalla cartella di installazione sia dal progetto.

    Uso:
      powershell -ExecutionPolicy Bypass -File uninstall.ps1
      powershell -ExecutionPolicy Bypass -File uninstall.ps1 -KeepPlugin -KeepSettings
#>
[CmdletBinding()]
param(
    [switch] $KeepPlugin,
    [switch] $KeepSettings,
    [switch] $Silent
)

$ErrorActionPreference = "Stop"
$AppName = "Opencode Tray"
$ExeName = "OpencodeTray.exe"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OpencodeTray"

function Out($msg, $color) {
    if (-not $Silent) { Write-Host $msg -ForegroundColor $color }
}

# --- dove e' installato: questa cartella, oppure quanto dice il registro
$installDir = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $installDir $ExeName))) {
    $reg = $null
    if (Test-Path -LiteralPath $uninstallKey) {
        $reg = (Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue).InstallLocation
    }
    if ($reg -and (Test-Path -LiteralPath (Join-Path $reg $ExeName))) { $installDir = $reg }
}
$settingsDir = Join-Path $env:APPDATA "opencode-tray"
$startLink = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"
$startupLink = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup\OpencodeTray.lnk"
$pluginDst = Join-Path $env:USERPROFILE ".config\opencode\plugins\opencode-tray.js"

Get-Process -Name "OpencodeTray" -ErrorAction SilentlyContinue | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force } catch { }
}
Start-Sleep -Milliseconds 400
Out "  processo arrestato" "DarkGray"

foreach ($link in @($startupLink, $startLink)) {
    if (Test-Path -LiteralPath $link) {
        try {
            Remove-Item -LiteralPath $link -Force
            Out "  rimossa scorciatoia: $(Split-Path -Leaf $link)" "Green"
        } catch { Out "  impossibile rimuovere $link" "Yellow" }
    }
}

if (Test-Path -LiteralPath $pluginDst) {
    if ($KeepPlugin) {
        Out "  plugin conservato: $pluginDst" "Yellow"
    } else {
        try {
            Remove-Item -LiteralPath $pluginDst -Force
            Out "  rimosso plugin opencode: $pluginDst" "Green"
        } catch { Out "  impossibile rimuovere il plugin" "Yellow" }
    }
}

if (Test-Path -LiteralPath $settingsDir) {
    if ($KeepSettings) {
        Out "  impostazioni conservate: $settingsDir" "Yellow"
    } else {
        try {
            Remove-Item -LiteralPath $settingsDir -Recurse -Force
            Out "  rimosse le impostazioni: $settingsDir" "Green"
        } catch { Out "  impossibile rimuovere le impostazioni" "Yellow" }
    }
}

if (Test-Path -LiteralPath (Join-Path $installDir $ExeName)) {
    try {
        Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction Stop
    } catch {
        Start-Sleep -Milliseconds 400
        try { Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction Stop } catch { }
    }
    if (Test-Path -LiteralPath $installDir) {
        Out "  cartella non vuota, rimuovila a mano: $installDir" "Yellow"
    } else {
        Out "  rimossa la cartella di installazione: $installDir" "Green"
    }
} else {
    Out "  nessun eseguibile da rimuovere in $installDir" "DarkGray"
}

if (Test-Path -LiteralPath $uninstallKey) {
    try {
        Remove-Item -LiteralPath $uninstallKey -Recurse -Force
        Out "  rimossa la voce da Programmi e caratteristiche" "Green"
    } catch { Out "  impossibile rimuovere la chiave di registro" "Yellow" }
}

Out "  disinstallazione completata." "White"
Out "  RIAVVIA opencode per scaricare il plugin dalla sessione corrente." "DarkGray"
