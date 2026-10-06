$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$envPath = Join-Path $projectRoot '.env'
function New-HexSecret([int]$bytes) {
    $buffer = New-Object byte[] $bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    return ([BitConverter]::ToString($buffer)).Replace('-', '').ToLowerInvariant()
}
$existing = if (Test-Path -LiteralPath $envPath) { @(Get-Content -LiteralPath $envPath) } else { @() }
$required = @{
    POSTGRES_PASSWORD = 24; PROCUREMENT_PASSWORD = 24; PRODUCTION_PASSWORD = 24; DEMO_PASSWORD = 16
    RABBITMQ_ADMIN_PASSWORD = 24; RABBITMQ_PROCUREMENT_PASSWORD = 24; RABBITMQ_PRODUCTION_PASSWORD = 24
}
foreach ($name in $required.Keys) { if (!($existing -match "^$name=")) { $existing += "$name=$(New-HexSecret $required[$name])" } }
if (!($existing -match '^JWT_KEY=')) { $existing += "JWT_KEY=$([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(64)))" }
$existing | Set-Content -LiteralPath $envPath -Encoding ascii
Write-Host '保留原配置并补齐缺少的本地密钥；.env 被 Git 忽略。演示账号继续共用 DEMO_PASSWORD。'
