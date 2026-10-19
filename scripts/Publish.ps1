$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    dotnet publish src/LiveCaptions.Desktop -c Release -r win-x64 --self-contained true -p:UseSharedCompilation=false -o artifacts/LiveCaptions-WinUI3
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if (-not (Test-Path -LiteralPath artifacts/LiveCaptions-WinUI3/resources.pri)) { throw 'WinUI application resources missing from publish output.' }
    Copy-Item -LiteralPath README.md -Destination artifacts/LiveCaptions-WinUI3/README.md
    Compress-Archive -Path artifacts/LiveCaptions-WinUI3/* -DestinationPath artifacts/LiveCaptions-WinUI3-win-x64.zip -Force
    Write-Output (Join-Path $projectRoot 'artifacts/LiveCaptions-WinUI3/LiveCaptions.Enhanced.exe')
}
finally { Pop-Location }
