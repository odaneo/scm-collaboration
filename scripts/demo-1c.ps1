param([switch]$Faults)
. "$PSScriptRoot/load-local.ps1"
$procurement = 'http://127.0.0.1:5180'
$production = 'http://127.0.0.1:5181'
function Login([string]$username) {
    $result = Invoke-RestMethod "$procurement/api/demo-auth/login" -Method Post -ContentType 'application/json' -Body (@{
        username = $username; password = $localSettings['DEMO_PASSWORD']
    } | ConvertTo-Json)
    return @{ Authorization = "Bearer $($result.token)" }
}
$buyer = Login 'buyer'; $factory = Login 'factory-a'; $otherFactory = Login 'factory-b'
$factories = Invoke-RestMethod "$procurement/api/factories" -Headers $buyer
$skus = Invoke-RestMethod "$procurement/api/skus" -Headers $buyer
function NewSubmittedOrder {
    $payload = @{ factoryId = $factories[0].id; deliveryDate = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow, 'China Standard Time').AddDays(14).ToString('yyyy-MM-dd')
        lines = @(@{ skuId = $skus[0].id; quantity = 100 }, @{ skuId = $skus[1].id; quantity = 50 }) }
    $headers = $buyer.Clone(); $headers['Idempotency-Key'] = [guid]::NewGuid().ToString()
    $draft = Invoke-RestMethod "$procurement/api/purchase-orders" -Method Post -Headers $headers -ContentType 'application/json' -Body ($payload | ConvertTo-Json -Depth 5)
    $headers['Idempotency-Key'] = [guid]::NewGuid().ToString()
    $submitted = Invoke-RestMethod "$procurement/api/purchase-orders/$($draft.orderId)/submissions" -Method Post -Headers $headers -ContentType 'application/json' -Body (@{expectedRevision=$draft.revision} | ConvertTo-Json)
    return $submitted
}
function Accept($order) {
    $headers = $factory.Clone(); $headers['Idempotency-Key'] = [guid]::NewGuid().ToString()
    $body = @{expectedRevision=$order.revision} | ConvertTo-Json
    $url = "$procurement/api/purchase-orders/$($order.orderId)/versions/$($order.orderVersion)/accept"
    $first = Invoke-RestMethod $url -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    $replay = Invoke-RestMethod $url -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    if ($first.decisionId -ne $replay.decisionId -or $first.revision -ne $replay.revision) { throw 'HTTP 重放改变了接单结果。' }
    return $first
}
function Status($order) { return Invoke-RestMethod "$procurement/api/purchase-orders/$($order.orderId)/production-task" -Headers $buyer }
function WaitCreated($order) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    do {
        $status = Status $order
        if ($status.status -eq 'Created') { return $status }
        if ($status.status -eq 'Blocked') { throw '建任务受到业务阻塞，请检查消息。' }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw '45 秒内尚未收到成功回执。'
}
function Compose([string[]]$composeArgs) {
    docker compose @composeArgs
    if ($LASTEXITCODE -ne 0) { throw 'Compose 演示操作失败。' }
}
Push-Location $projectRoot
try {
    $normal = Accept (NewSubmittedOrder); $created = WaitCreated $normal
    $task = Invoke-RestMethod "$production/api/production-tasks/$($created.taskId)" -Headers $factory
    if (($task.lines | Measure-Object confirmedQuantity -Sum).Sum -ne 150) { throw '确认件数不符合演示订单。' }
    $denied = Invoke-WebRequest "$production/api/production-tasks/$($created.taskId)" -Headers $otherFactory -SkipHttpErrorCheck
    if ([int]$denied.StatusCode -ne 404) { throw '工厂隔离检查失败。' }
    $results = [ordered]@{ occurredAt = [DateTimeOffset]::UtcNow.ToString('o'); normalOrder = $normal.orderId; normalTask = $created.taskId
        confirmedQuantity = 150; httpReplay = 'sameDecisionAndRevision'; otherFactoryHttpStatus = 404 }
    if ($Faults) {
        try {
            Compose @('stop', 'rabbitmq')
            $offline = Accept (NewSubmittedOrder); $pending = Status $offline
            if ($pending.status -notin @('Pending', 'DeliveryDelayed')) { throw 'Broker 离线时任务状态不正确。' }
            $results.brokerOffline = @{ orderId = $offline.orderId; before = $pending.status }
        } finally { Compose @('start', 'rabbitmq') }
        $recovered = WaitCreated $offline
        $results.brokerOffline.after = $recovered.status; $results.brokerOffline.taskId = $recovered.taskId
        try {
            Compose @('stop', 'production-api')
            $waiting = Accept (NewSubmittedOrder); Start-Sleep -Seconds 2; $pending = Status $waiting
            if ($pending.status -notin @('Pending', 'DeliveryDelayed')) { throw 'Production 离线时不能显示已建立。' }
            $results.productionOffline = @{ orderId = $waiting.orderId; before = $pending.status }
        } finally { Compose @('start', 'production-api') }
        $recovered = WaitCreated $waiting
        $results.productionOffline.after = $recovered.status; $results.productionOffline.taskId = $recovered.taskId
    }
    New-Item -ItemType Directory -Force -Path artifacts/demo | Out-Null
    $results | ConvertTo-Json -Depth 5 | Set-Content artifacts/demo/1c.json -Encoding utf8
    Write-Host '演示通过：接单 → 生产任务；HTTP 重放；跨厂 404。结果：artifacts/demo/1c.json'
    if ($Faults) { Write-Host 'RabbitMQ / Production 实际停止后的恢复均通过。' }
} finally { Pop-Location }
