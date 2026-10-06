param([switch]$Apply)
. "$PSScriptRoot/load-local.ps1"
Push-Location $projectRoot
try {
    $commandArgs = @('run', '--project', 'src/Procurement.Api', '--no-launch-profile', '--', '--backfill-production')
    if ($Apply) { $commandArgs += '--apply' }
    dotnet @commandArgs
    if ($LASTEXITCODE -ne 0) { throw '历史任务补录失败。' }
} finally { Pop-Location }
