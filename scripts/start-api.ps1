param([switch]$InitializeOnly)
. "$PSScriptRoot/load-local.ps1"
Push-Location $projectRoot
try {
    dotnet run --project src/Procurement.Api --no-launch-profile -- --initialize
    if ($LASTEXITCODE -ne 0) { throw '数据库初始化失败。' }
    if (!$InitializeOnly) { dotnet run --project src/Procurement.Api --no-launch-profile }
} finally { Pop-Location }
