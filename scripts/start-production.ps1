param([switch]$InitializeOnly)
. "$PSScriptRoot/load-local.ps1"
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5181'
$env:Messaging__Username = 'scm_production'; $env:Messaging__Password = $localSettings['RABBITMQ_PRODUCTION_PASSWORD']
Push-Location $projectRoot
try {
    dotnet run --project src/Production.Api --no-launch-profile -- --initialize
    if ($LASTEXITCODE -ne 0) { throw '生产初始化失败。' }
    if (!$InitializeOnly) { dotnet run --project src/Production.Api --no-launch-profile }
} finally { Pop-Location }
