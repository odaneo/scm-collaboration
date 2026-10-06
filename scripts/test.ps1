. "$PSScriptRoot/load-local.ps1"
$env:SCM_TEST_CONNECTION = $env:ConnectionStrings__Procurement.Replace('Database=scm_procurement;', 'Database=scm_procurement_test;')
$env:SCM_PRODUCTION_TEST_CONNECTION = $env:ConnectionStrings__Production.Replace('Database=scm_production;', 'Database=scm_production_test;')
$env:SCM_TEST_MQ_ADMIN_PASSWORD = $localSettings['RABBITMQ_ADMIN_PASSWORD']
$env:SCM_TEST_MQ_PROCUREMENT_PASSWORD = $localSettings['RABBITMQ_PROCUREMENT_PASSWORD']
$env:SCM_TEST_MQ_PRODUCTION_PASSWORD = $localSettings['RABBITMQ_PRODUCTION_PASSWORD']
Push-Location $projectRoot
try {
    dotnet test ScmCollaboration.slnx --logger 'trx;LogFileName=1c.trx' --results-directory artifacts/tests
    if ($LASTEXITCODE -ne 0) { throw '后端测试失败。' }
    Push-Location web
    try { npm run build; if ($LASTEXITCODE -ne 0) { throw '前端构建失败。' } } finally { Pop-Location }
} finally { Pop-Location }
