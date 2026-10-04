# 1A API 契约

此文保留 1A 的基础契约；当前已增加提交与工厂决定能力，见 [1B 契约](API-1B.md)。

业务接口统一使用 ASP.NET Core Controller。运行时契约：`GET /openapi/v1.json`（Development）。数据库和 HTTP 测试见验证记录。

| 方法及路径 | 权限 / 返回 |
| --- | --- |
| `POST /api/demo-auth/login` | Development 演示登录；成功 200，错误凭证 401；每来源每分钟最多 20 次，超限 429 |
| `GET /api/factories` | Buyer；200，服务端预置有效工厂 |
| `GET /api/skus` | Buyer；200，服务端预置有效商品组合 |
| `POST /api/purchase-orders` | Buyer；需要 Idempotency-Key；成功或同内容重放 201 + Location |
| `GET /api/purchase-orders?page=1&pageSize=20` | Buyer；200；page 1–100000，pageSize 1–100 |
| `GET /api/purchase-orders/{id}` | Buyer；200；不存在或当前身份不可见 404 |
| `GET /health/live` | 匿名；200 |
| `GET /health/ready` | 匿名；数据库连通时 200，否则 503 |

登录输入为 `{ "username": "buyer", "password": "你的本地演示密码" }`。响应包含 token、username、role、factoryId、expiresAt。JWT 30 分钟到期，验证签名、issuer、audience、有效期以及必要身份声明；业务接口使用 `Authorization: Bearer <token>`。工厂归属来自签名验证后的 `factory_id`，请求体不承担调用者身份。

创建输入示例：

```json
{
  "factoryId": "10000000-0000-0000-0000-000000000001",
  "deliveryDate": "2026-10-31",
  "lines": [
    { "skuId": "20000000-0000-0000-0000-000000000001", "quantity": 100 },
    { "skuId": "20000000-0000-0000-0000-000000000002", "quantity": 50 }
  ]
}
```

请求头 `Idempotency-Key` 为 1–128 字符，每项创建意图保持一个稳定键。服务端规范化明细顺序，因此只调整同一组明细的排列可以重放原结果。键按已验证 SubjectId 和 CreateDraft 操作隔离；同键异内容 409。不同键相同内容允许创建不同订单。

创建响应包含 `orderId`、`status: "Draft"`、`revision: 1`、`createdAt`。重放返回保存的原响应和相同 Location，不新增订单或审计。数量为 int32 正整数、最多 100 个明细；服务端解析并保存商品款式/颜色/尺码，客户端描述不会成为业务事实。草稿阶段不限制交期相对今天的先后，提交规则留到 1B。

非法输入 400、身份无效 401、禁止操作 403、资源不可见 404、幂等冲突 409、未预期服务错误 500。错误返回 `application/problem+json`，包含 status、title、适用时的 detail / errors，以及 traceId。5xx 和网络超时不能作为“未创建”的证明，应保留键和原内容重试。

本阶段没有领域/集成事件或其他服务的契约。创建订单与响应幂等结果、业务审计仅使用采购数据库的本地事务。
