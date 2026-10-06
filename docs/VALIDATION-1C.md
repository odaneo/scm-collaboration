# 1C 实际验证记录

首次验证日期：2026-10-05，Asia/Shanghai。**已实现、已在本机运行；等待用户验收。** 首次验证时尚未提交/推送代码，未创建云资源，Articles 未修改。下面区分版本核实、实际运行和未验证能力；不是性能报告。

## 提交前复验（2026-10-06）

针对学习期间保存过的当前源码，重新执行 `dotnet test ScmCollaboration.slnx --no-restore --logger 'trx;LogFileName=1c-precommit-20261006.trx' --results-directory artifacts/tests`，使用本机真实 PostgreSQL 专用测试数据库和 RabbitMQ 测试 vhost。结果：**74 通过、0 失败、0 跳过**；本次测试运行显示 29 秒，仅表示这组本机测试用时。原始记录为 Git 忽略的 `artifacts/tests/1c-precommit-20261006.trx`。

重新执行前端 `npm run build`，TypeScript 检查与 Vite 构建通过。`git diff --check` 及待提交文件的本地凭证检查通过；`.env`、数据库备份、测试结果和构建产物保持忽略。本次复验没有重新构建容器镜像或执行远端 CI。

## 环境与版本

Windows 主机，PowerShell 7.6.5，Docker Desktop Linux 引擎 / Docker Server 29.8.1；.NET SDK 10.0.401、运行时 10.0.12；Node 22.20.0 / npm 10.9.3。真实 PostgreSQL 18.6 与 RabbitMQ 4.3.6 单节点，同机运行，业务端口仅 loopback。没有负载规模/硬件性能基准。

| 对象 | 来源核实 | 实际验证 |
| --- | --- | --- |
| RabbitMQ 4.3.6 | [官方发布信息](https://www.rabbitmq.com/release-information)，镜像 tag/digest 固定在 Compose | 拉取、启动、拓扑、confirm、消费、停机恢复 |
| RabbitMQ.Client 7.2.2 | [NuGet 包页](https://www.nuget.org/packages/RabbitMQ.Client/7.2.2)、[官方客户端说明](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) | .NET 10 锁定还原、构建、真实 broker 通信 |
| 原 SDK/EF/Npgsql/前端组合 | 沿用 1B 的 global.json、集中包版本和锁文件，未顺带升级 | 主机构建/测试，两个 API 的 Linux 镜像构建/运行，前端类型检查/构建 |
| Npgsql 10 Kerberos 探测 | [官方安全说明](https://www.npgsql.org/doc/security.html)说明缺少 libgssapi 时可回退及关闭探测 | 本地密码连接指定 GSS Encryption Mode=Disable，重启后已成功连库；不代表已配置生产 TLS |

镜像固定：PostgreSQL 18.6-bookworm、RabbitMQ 4.3.6-management、SDK 10.0.401-noble、ASP.NET 10.0.12-noble、Node 22.20.0-bookworm-slim；准确 digest 见 compose.yaml / 两个 Dockerfile。来源支持信息与构建结果分别核实，没有用包页代替运行验证。

## 构建、测试与容器

已执行 `dotnet restore ScmCollaboration.slnx --locked-mode`、解决方案构建、EF 工具还原和迁移；两个 API Dockerfile 也执行 locked restore / Release publish。前端 `npm ci`、`npm run build` 在镜像中真实执行，包含 TypeScript 检查。最终前端容器保持非 root 用户，修复缓存写入权限；API 仅允许各自本地/Compose 主机名，修复代理 Host 的 400。

最终后端测试命令：加载 scripts/load-local.ps1 后指定两个专用测试连接及 MQ 测试凭证，执行：

```powershell
dotnet test ScmCollaboration.slnx --no-restore --logger 'trx;LogFileName=1c.trx' --results-directory artifacts/tests
```

**74 通过，0 失败，0 跳过，包含原 49 项和新增 25 项。** 最终 scripts/test.ps1 运行显示后端 13 秒且前端构建通过，仅表示这组本机测试用时，不能推导吞吐/业务收益。原始记录 [1c.trx](../artifacts/tests/1c.trx) 被 Git 忽略；可用同一脚本重跑。

测试用真实 PostgreSQL 的独立 `_test` 数据库，工厂归属通过真实 HTTP/JWT；真实 RabbitMQ 使用独立 `scm_1c_tests` vhost 和测试服务凭证。迁移、唯一约束和并发行为没有用内存数据库代替。

| 自动化场景 | 实际断言 / 故障方法 |
| --- | --- |
| 接单领域事实与任务规则 | 稳定 DecisionId、正数量、完整/唯一明细、最多 100；迟到但合法接受保留原交期 |
| 接单同事务与 HTTP 重放 | 一份事实/Outbox/投影；SQL 写入后提交前注入异常，全组回滚，原请求可重试 |
| 接受与撤回/拒绝竞争 | 两请求读取相同 Revision，只有一个提交；最终 Accepted 才有一份 Outbox/投影，其他结果为零 |
| 消息与业务幂等 | 同 MessageId 重投；换 MessageId 同 DecisionId 仍一任务、一审计、一回执；异内容显式失败、不覆盖成功 Inbox |
| 并发创建任务 | 8 个新消息编号同一决定，独立 DbContext 并发；唯一约束仲裁，Pending 恢复后全部安全处理 |
| 消费保存失败 | SQL 拦截器注入失败；没有半任务/成功 Inbox/回执，保管 Pending 后恢复 |
| 回执与前置事实 | 重复不推进订单 Revision；错工厂/摘要/版本不能覆盖；缺前置持久 Pending，补录后继续 |
| 失败乱序 | 合法 Created 可解除 Blocked；迟到失败不能降级 Created |
| 不可路由 | 真实 broker 移除绑定，mandatory return 抛错、SentAt 保持空；恢复绑定后发送 |
| confirm 后标记失败 | 真正取得 broker confirm，再由 SQL 拦截器使本地标记失败；真实重复发布后仍一任务 |
| 提交后 ack 前丢连接 | 任务已提交，关闭真实消费通道且不 ack；broker Redelivered 后不重复任务/审计 |
| 消费库不可连接 | 测试连接指向不可达端口，不能落 Pending 时抛错且不 ack；恢复真实连接后处理 broker 重投 |
| 首次 broker 不可连接 | worker 初始端口不可达，再恢复实际端口；HTTP 主机仍能启动、自动恢复 |
| 维护命令 | 非法版本原文隔离，重试仍隔离且不改原文；Processed 不会被重置 |
| 边界与权限 | B 查 A 的列表/已知任务/投影均隔离；匿名 401、Quality 403；两数据库账号跨库连接被 PostgreSQL 42501 拒绝 |

源码：TaskDomainTests.cs、TaskCoordinationTests.cs，原规则回归仍在原测试类中。故障注入采用 SQL 拦截器、通道关闭和不可达连接端口，**不是操作系统 kill、磁盘损坏或高可用故障测试**。

完整 `docker compose build` 和 `docker compose up -d` 已执行：PostgreSQL/RabbitMQ healthy，两个 API 及 web 运行，一次性初始化任务 Exited(0)。两个 `/health/ready` 实际返回 ready / connected。前端 5173、采购 5180、生产 5181 已连通。

## 旧库保留与历史补建

升级前备份：`artifacts/backups/scm-procurement-before-1c-20261005.sql`，38,456 字节，被 Git 忽略且不包含于 Docker 构建上下文。备份曾恢复到临时验证数据库，核对升级/补建后的原表行数和规范化行内容 MD5；验证结束只删除该临时库。

在本轮新建演示订单前，以下八张原表内容全部相同；记录见 [1c-preservation.json](../artifacts/backups/1c-preservation.json)：

| 表 | 原行数 |
| --- | --- |
| purchase_orders | 6 |
| order_lines | 14 |
| order_versions | 8 |
| order_version_lines | 19 |
| http_request_results | 26 |
| demo_users | 4 |
| factories | 2 |
| skus | 3 |

这包括原订单 Revision、接受决定/快照、原响应及密码哈希。另按备份 COPY 列顺序逐行比较原业务审计：25 条全部保留，补建及三张新演示单后当前 36 条，见 [审计保留记录](../artifacts/backups/1c-audit-preservation.json)。没有拿包含新行的整表摘要宣称完全不变。迁移为采购 `ReliableTaskCoordination`、生产 `InitialProductionTasks`，没有删除旧卷。

补建先列候选、apply 新增 2 个请求，再次 apply 新增 0；两份投影实际 Created，生产有对应两张任务。历史单：

- `aad15613-f2a9-475f-8819-6a7006522173`，接受 V1。
- `c604667f-94dd-4a04-8bab-2e21ba96b606`，接受 V3；保留先前 V1 撤回、V2 拒绝等记录。

补建依据原接受快照/DecisionId，不重新执行 AcceptVersion，不改订单 Revision 或原 HTTP 结果。

## 正常流程及异常演示

实际执行 `.\scripts\demo-1c.ps1 -Faults`，结果 [1c.json](../artifacts/demo/1c.json)。脚本新建三单，未修改原六单；每次都会新增演示订单。

| 场景 | 实际结果 |
| --- | --- |
| 正常订单 `065ac0e4-b08a-4958-860d-5a483d71f9f7` | V1 接受 → 任务 `9d9d0cb8-80ee-42e9-bf74-220bd900ac6f`；L100 / M50，总 150 件 |
| 同键原接单请求重试 | DecisionId / Revision 相同；没有第二次接受 |
| 工厂 B 查询上述任务 | HTTP 404 |
| 实际停止 RabbitMQ，订单 `9f2fbf52-9e77-4f1b-9c6a-29f22be99392` 接单 | 接单成功、Pending；启动 broker 后 Created，任务 `c56fc341-ab26-4ae9-93fa-8cd68d95d237` |
| 实际停止 Production，订单 `72b8d176-f374-4ff3-8fc8-079b349b369b` 接单 | Pending；启动 Production 后 Created，任务 `b152a9f5-5eb3-434e-a9a0-b389fca575a0` |

品牌与工厂 A 的浏览器页面已分别查看正常单，两端同一 TaskId、确认量和交期；页面控制台最终未见 error/warn。截图：[品牌](../artifacts/screenshots/1c-buyer.png)、[工厂](../artifacts/screenshots/1c-factory.png)。HTTP 脚本覆盖停机期间 Pending；没有将这些 API 检查冒充停机期间的浏览器截图。

消息重复、消费提交失败、提交后 ack 故障另由上表的自动化测试复现。契约 JSON 取自新演示单的真实 Outbox，没有把消息已发作为下游业务完成凭据。

## 未验证和下一步

GitHub Actions 已修改配置，**尚未推送或远端执行**。本机 Windows 测试和 Linux API 镜像成功不能证明 CI runner 已通过。未运行多实例/压力测试、broker 高可用、硬件故障、追踪采集后端或 AWS 部署；没有性能与生产收益数据。

教学范围为单 worker、固定重试、手动原消息命令、原文/幂等保留及 Vite 开发服务器。质量、放行、发货、ERP 与完成量规则尚未实现。下一用例是 1D 的增量完成报告及并发数量约束，须先写计划。
