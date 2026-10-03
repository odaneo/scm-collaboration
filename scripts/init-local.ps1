$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$envPath = Join-Path $projectRoot '.env'
if (Test-Path -LiteralPath $envPath) { Write-Host '.env 已存在，保留原配置。'; return }
function New-HexSecret([int]$bytes) {
    $buffer = New-Object byte[] $bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    return ([BitConverter]::ToString($buffer)).Replace('-', '').ToLowerInvariant()
}
@(
    "POSTGRES_PASSWORD=$(New-HexSecret 24)"
    "PROCUREMENT_PASSWORD=$(New-HexSecret 24)"
    "DEMO_PASSWORD=$(New-HexSecret 16)"
    "JWT_KEY=$( [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((New-HexSecret 32))) )"
) | Set-Content -LiteralPath $envPath -Encoding ascii
Write-Host '已生成忽略于 Git 的 .env；演示密码查看 DEMO_PASSWORD，四个账号共用此本地密码。'
