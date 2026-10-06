$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$envPath = Join-Path $projectRoot '.env'
if (!(Test-Path -LiteralPath $envPath)) { throw '请先运行 scripts/init-local.ps1。' }
$localSettings = @{}
Get-Content -LiteralPath $envPath | ForEach-Object {
    if ($_ -match '^([A-Z_]+)=(.*)$') { $localSettings[$matches[1]] = $matches[2] }
}
$env:ConnectionStrings__Procurement = "Host=127.0.0.1;Port=54329;Database=scm_procurement;Username=scm_procurement;Password=$($localSettings['PROCUREMENT_PASSWORD']);GSS Encryption Mode=Disable"
$env:ConnectionStrings__Production = "Host=127.0.0.1;Port=54329;Database=scm_production;Username=scm_production;Password=$($localSettings['PRODUCTION_PASSWORD']);GSS Encryption Mode=Disable"
$env:Messaging__Host = '127.0.0.1'; $env:Messaging__Port = '56729'; $env:Messaging__VirtualHost = 'scm'
$env:Messaging__Username = 'scm_procurement'; $env:Messaging__Password = $localSettings['RABBITMQ_PROCUREMENT_PASSWORD']
$env:MessagingAdmin__Url = 'http://127.0.0.1:15679/api/'
$env:MessagingAdmin__Username = 'scm_mq_admin'; $env:MessagingAdmin__Password = $localSettings['RABBITMQ_ADMIN_PASSWORD']
$env:MessagingAdmin__ProcurementPassword = $localSettings['RABBITMQ_PROCUREMENT_PASSWORD']
$env:MessagingAdmin__ProductionPassword = $localSettings['RABBITMQ_PRODUCTION_PASSWORD']
$env:Auth__SigningKey = $localSettings['JWT_KEY']
$env:DemoAuth__Password = $localSettings['DEMO_PASSWORD']
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5180'
