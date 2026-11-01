$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
# Stable release locations: keep these names for subsequent releases and shortcuts.
$publishDirectory = Join-Path $projectRoot 'artifacts/LiveCaptions-WinUI3'
$archivePath = Join-Path $projectRoot 'artifacts/LiveCaptions-WinUI3-win-x64.zip'
$executablePath = Join-Path $publishDirectory 'LiveCaptions.Enhanced.exe'
Push-Location -LiteralPath $projectRoot
try {
    dotnet publish src/LiveCaptions.Desktop -c Release -r win-x64 --self-contained true -p:UseSharedCompilation=false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if (-not (Test-Path -LiteralPath artifacts/LiveCaptions-WinUI3/resources.pri)) { throw 'WinUI application resources missing from publish output.' }
    Copy-Item -LiteralPath README.md -Destination artifacts/LiveCaptions-WinUI3/README.md
    if (-not (Test-Path -LiteralPath $executablePath)) { throw 'Published executable missing.' }
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -Force
    Write-Output $executablePath
}
finally { Pop-Location }
