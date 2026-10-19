param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    dotnet build LiveCaptions.sln -c $Configuration -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/LiveCaptions.Tests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Transcript regressions failed.' }
    New-Item -ItemType Directory -Force artifacts | Out-Null
    $testReport = Join-Path $projectRoot 'artifacts/winui-test.json'
    if (Test-Path -LiteralPath $testReport) { Remove-Item -LiteralPath $testReport }
    & (Join-Path $PSScriptRoot 'Test-WinUI.ps1') -Configuration $Configuration -Report $testReport
    $report = Get-Content -LiteralPath $testReport -Raw | ConvertFrom-Json
    if ($report.Framework -ne 'WinUI 3' -or $report.Passed -le 0) { throw 'Invalid WinUI regression report.' }
    Write-Output ("WinUI 3 regression: {0} passed" -f $report.Passed)
    $report.Checks
}
finally { Pop-Location }
