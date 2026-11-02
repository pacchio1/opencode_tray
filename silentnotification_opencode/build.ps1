# Compila OpencodeTray.exe con il compilatore del .NET Framework (nessuna dipendenza esterna).
# Uso:  powershell -ExecutionPolicy Bypass -File build.ps1 [-OutputDir cartella]
[CmdletBinding()]
param(
    [string] $OutputDir
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutputDir) { $OutputDir = Join-Path $Root "dist" }

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path -LiteralPath $csc)) {
    throw "Compilatore csc.exe non trovato (.NET Framework 4.x mancante)."
}

# NOTA: niente /optimize+ : su alcune postazioni (Sophos Intercept X) il binario
# ottimizzato viene bloccato dall'antivirus e risulta illeggibile subito dopo la
# compilazione. Il binario non ottimizzato funziona ed e' equivalente qui.
# In piu' il modello ML di Sophos classifica come "Generic ML PUA" un eseguibile
# senza identita': servono manifest e AssemblyInfo (vedi sotto) perche' il
# binario appena compilato resti accessibile/eseguibile.
$src = Join-Path $Root "src\OpencodeTray.cs"
$info = Join-Path $Root "src\AssemblyInfo.cs"
$manifest = Join-Path $Root "src\app.manifest"
if (-not (Test-Path -LiteralPath $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }
$exe = Join-Path $OutputDir "OpencodeTray.exe"

Write-Host "Compilo $src ..." -ForegroundColor Cyan
& $csc `
    /nologo `
    /target:winexe `
    /codepage:65001 `
    /platform:anycpu `
    /win32manifest:"$manifest" `
    /out:"$exe" `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    "$src" `
    "$info"

if ($LASTEXITCODE -ne 0) { throw "Compilazione fallita (exit $LASTEXITCODE)." }
if (-not (Test-Path -LiteralPath $exe)) { throw "L'eseguibile non e' stato prodotto." }

$readable = $false
for ($i = 0; $i -lt 5 -and -not $readable; $i++) {
    try { $null = [System.IO.File]::ReadAllBytes($exe); $readable = $true }
    catch { Start-Sleep -Seconds 1 }
}
if (-not $readable) {
    throw "L'antivirus ha bloccato $exe : aggiungi la cartella nelle esclusioni e rilancia."
}

Write-Host "OK -> $exe" -ForegroundColor Green