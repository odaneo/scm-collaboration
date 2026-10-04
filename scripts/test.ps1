. "$PSScriptRoot/load-local.ps1"
$env:SCM_TEST_CONNECTION = $env:ConnectionStrings__Procurement.Replace('Database=scm_procurement;', 'Database=scm_procurement_test;')
Push-Location $projectRoot
try {
    dotnet test ScmCollaboration.slnx --logger 'trx;LogFileName=1b.trx' --results-directory artifacts/tests
    if ($LASTEXITCODE -ne 0) { throw '后端测试失败。' }
    Push-Location web
    try { npm run build; if ($LASTEXITCODE -ne 0) { throw '前端构建失败。' } } finally { Pop-Location }
} finally { Pop-Location }
