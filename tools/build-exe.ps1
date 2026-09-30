<#
.SYNOPSIS
  Baut das Programm als eine einzige, eigenstaendige Datei (self-contained, single file).

.EXAMPLE
  .\tools\build-exe.ps1                    # dist\win-x64\Berichtsheft.exe
  .\tools\build-exe.ps1 win-x64 -Zip       # + dist\Berichtsheft-win-x64.zip
  .\tools\build-exe.ps1 all                # win-x64 und win-arm64

  Falls die Ausfuehrung von Skripten gesperrt ist:
  powershell -ExecutionPolicy Bypass -File .\tools\build-exe.ps1
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'all')] [string]$Target = 'win-x64',
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Das .NET 8 SDK wurde nicht gefunden: https://dotnet.microsoft.com/download/dotnet/8.0'
}

$rids = if ($Target -eq 'all') { @('win-x64', 'win-arm64') } else { @($Target) }

foreach ($rid in $rids) {
    $out = "dist/$rid"
    Write-Host "`n=== Baue Berichtsheft fuer $rid ==="
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish src/Berichtsheft.Desktop/Berichtsheft.Desktop.csproj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:DebugSymbols=false -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ist fehlgeschlagen (Exit-Code $LASTEXITCODE)" }
    Copy-Item README.md $out
    $exe = Join-Path $out 'Berichtsheft.exe'
    Write-Host ("  -> {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
    if ($Zip) {
        $zipPath = "dist/Berichtsheft-$rid.zip"
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Compress-Archive -Path $out -DestinationPath $zipPath
        Write-Host "  -> $zipPath"
    }
}
