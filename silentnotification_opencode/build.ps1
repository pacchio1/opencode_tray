# Compiles OpencodeTray.exe with the .NET Framework compiler (no external dependencies).
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
    throw "csc.exe compiler not found (.NET Framework 4.x missing)."
}

# NOTE: no /optimize+ : on some workstations (Sophos Intercept X) the binary
# optimized is blocked by antivirus and becomes unreadable right after
# compilation. The unoptimized binary works and is equivalent here.
# Additionally the Sophos ML model classifies as "Generic ML PUA" an executable
# without identity: manifest and AssemblyInfo are needed (see below) so that the
# newly compiled binary stays accessible/executable.
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
    throw "Antivirus blocked $exe : add the folder to exclusions and retry."
}

Write-Host "OK -> $exe" -ForegroundColor Green