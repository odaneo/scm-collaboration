. "$PSScriptRoot/load-local.ps1"
Push-Location $projectRoot
try {
    docker compose up -d --wait postgres rabbitmq
    if ($LASTEXITCODE -ne 0) { throw '基础设施启动失败。' }
    docker compose exec -T postgres sh -c 'psql -v ON_ERROR_STOP=1 -U scm_admin -d postgres -v production_password="$PRODUCTION_PASSWORD" -f /opt/scm/upgrade-1c.sql'
    if ($LASTEXITCODE -ne 0) { throw '增量建库失败。' }
    dotnet run --project src/Procurement.Api --no-launch-profile -- --initialize
    if ($LASTEXITCODE -ne 0) { throw '采购迁移失败。' }
    dotnet run --project src/Production.Api --no-launch-profile -- --initialize
    if ($LASTEXITCODE -ne 0) { throw '生产迁移失败。' }
    dotnet run --project src/Procurement.Api --no-launch-profile -- --initialize-messaging
    if ($LASTEXITCODE -ne 0) { throw '消息拓扑初始化失败。' }
} finally { Pop-Location }
