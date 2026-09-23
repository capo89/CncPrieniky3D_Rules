# Dump CncExporter Excel (Kusovník WCS + Znacenie CNC na bokoch).
# Použitie:
#   .\tools\dump-export.ps1 "C:\cesta\Export_....xlsx"
#   .\tools\dump-export.ps1 "C:\cesta\_moje"                 # najnovší Export_*.xlsx
#   .\tools\dump-export.ps1 "C:\cesta\Export.xlsx" -CncAll

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Path,

    [switch] $CncAll
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$py = Join-Path $scriptDir "dump-export.py"

if (-not (Test-Path -LiteralPath $py)) {
    Write-Error "Chýba $py"
}

$env:PYTHONIOENCODING = "utf-8"
$argsList = @($py, $Path)
if ($CncAll) { $argsList += "--cnc-all" }

& python @argsList
exit $LASTEXITCODE
