# App 系统推送（Expo 推送服务）运维手册

通知平台（`Features/Notifications`）原来有两条投递通道：站内消息（以库为准）与 SignalR 实时通道。本功能加第三条：**App 系统推送**，让手机在 App 不在前台、甚至没打开时也能收到通知。

> 状态：后端已实现，**默认关闭**（`Push:Expo:Enabled=false`）。App 端（`expo-notifications`、取令牌并上报）由另外的改动完成。本手册只讲后端：架构、接口约定、配置、上线与回滚、排障、隐私规则、已知限制。

## 1. 架构

推送走 **Expo 推送服务**（`POST https://exp.host/--/api/v2/push/send` 与 `/getReceipts`），**后端不直连 APNs / FCM，也不碰 `.p8` 与 FCM 私钥**——它们已登记在 EAS，由 Expo 服务器代为发给苹果 / 谷歌。后端只持有两样东西：每台设备的 **Expo 推送令牌**（`ExponentPushToken[...]`），以及可选的 **Expo Access Token**。

```
NotificationPublisher.PublishAsync
  │  ① 同一个事务里写：AppNotification + AppNotificationRecipient + AppPushDelivery（通知 × 接收人的每台活动设备）
  ▼
AppPushDelivery（发件箱，State = Pending）                         ← 进程重启不丢
  │
  ▼  ExpoPushBackgroundService（每 PollIntervalSeconds 秒一个节拍）
ExpoPushSender
  ├─ 领取：逐行条件 UPDATE 抢租约 → 跳过已撤回 / 已过期 / 设备已撤销 / 过于陈旧的
  ├─ 发送：每批 ≤ 100 条 POST /send → 按 ticket 逐条落状态（ok → Sent；DeviceNotRegistered → 撤销设备；限流 / 5xx / 网络 → 退避重试）
  ├─ 回执：Sent 且到期（默认 15 分钟后）→ 每批 ≤ 1000 个 ticket POST /getReceipts → Delivered / Failed
  └─ 清理：每小时一次，分批删过期记录
```

要点：

- **发件箱与通知同事务**：通知发布成功 = 推送一定入箱（有活动设备的话）；推送入箱失败 = 整个发布失败回滚，不会出现「有通知没推送」的半截数据。SqlSugar 不支持事务嵌套，所以发布器必须在业务事务之外调用（原有约束，没变）。
- **推送开关关闭（默认）时发布器不查设备表、不写发件箱，后台任务不发任何请求**，行为与没有这个功能时完全一致；令牌注册接口照常工作，App 可以先上报令牌。
- **设备表不存在（开关打开但迁移没跑）**：发布器的设备查询失败只记一条 Warning（`App 推送设备查询失败…SchemaMissing=True`），通知照常发布、不写发件箱，不会拖垮任何通知；后台任务记一条限频 Error（`Category=schema-missing`），见第 7 节。
- **至少一次**语义：Expo 已受理、但进程在写回结果前崩溃时，租约（5 分钟）到期后会被重新领取再发一次，用户可能收到重复推送。多实例安全靠逐行的条件 UPDATE 领取，同一条记录同一时刻只会被一个实例发送。
- **后台任务遵守 `ScheduledTasks:Enabled`**：该开关为 `false`（临时实例、本机联调）时本任务启动即退出。注意：人脸比对 / 门店改价 / 新品到店这三个现有后台任务并**不**检查这个开关，本任务自己检查。

### 1.1 数据表（迁移 `20261017.001-app-push`）

| 表 | 作用 | 关键约束 |
| --- | --- | --- |
| `dbo.AppPushDevice` | 推送设备：账号 × 令牌 | 主键 `PushDeviceId`；**活动令牌全库唯一**：过滤唯一索引 `UX_AppPushDevice_ActiveToken`（`ExpoPushToken` WHERE `RevokedAtUtc IS NULL`）；索引 `(UserGUID, RevokedAtUtc)` |
| `dbo.AppPushDelivery` | 发件箱：通知 × 设备 | 主键 `DeliveryId`；唯一 `(NotificationId, PushDeviceId)`；索引 `(State, NextAttemptAtUtc)` |

不建外键；两张表都不在 `SqlSugarContext` 的自动建表清单里（`tableTypes`），表结构以迁移为准。THROW 错误号区间 `53500–53599`（本迁移用 `53500` 缺表、`53501` 列签名、`53502` 主键 / 索引签名、`53503` 活动令牌索引过滤条件）。迁移幂等，`VerifySql` 在登记账本前校验列、主键、索引键列顺序与唯一性、过滤条件。

### 1.2 发件箱状态

| State | 名称 | 含义 | `NextAttemptAtUtc` 的含义 |
| ---: | --- | --- | --- |
| 0 | Pending | 待发送（含退避等待重试） | 下次尝试时间 |
| 1 | Sent | Expo 已受理（拿到 ticket），等回执 | **回执到期时间**（默认发送后 15 分钟） |
| 2 | Delivered | 回执 ok；或回执窗口（24 小时）已过不再追 | — |
| 3 | Failed | 重试用尽、令牌失效、Expo 拒绝 | — |
| 4 | Skipped | 主动跳过（`LastError` 里是原因） | — |

`LastError` **只存短错误码**，不存令牌、通知正文、异常消息或 Expo 回包文本：

- Expo 的 ticket / receipt 错误码：`DeviceNotRegistered`、`MessageRateExceeded`、`MessageTooBig`、`InvalidCredentials`、`MismatchSenderId` 等；含非法字符或取不到时记 `unknown`。
- HTTP 层：`http-429`、`http-500`、`http-503`、`http-401` …；`network-error`（连接失败）、`timeout`（30 秒超时）、`bad-response`（2xx 但回包结构不对）。
- 跳过原因（State=4）：`revoked`（通知已撤回）、`expired`（通知已过期）、`device-revoked`（设备已被撤销）、`device-missing`、`notification-missing`、`stale`（记录创建超过 `StaleAfterHours` 仍没发出）。
- `receipt-expired` / `no-ticket`（State=2 时的收尾说明）。

### 1.3 发送规则

- **消息字段**：`to`、`title`、`body`、`data`、`sound: "default"`、`channelId: "default"`、`priority`（通知优先级 High → `"high"`，其余 `"default"`）、`ttl`（有 `ExpiresAtUtc` 则取剩余秒数，至少 1 秒、至多 24 小时；没有则 24 小时）。`data` 恒含 `notificationId` 与 `type`；非敏感类型再合并通知的 `DataJson`（序列化后 ≤ 4KB，超过只保留这两项）。
- **请求头**：`Accept: application/json`、`Content-Type: application/json`；配置了 AccessToken 时带 `Authorization: Bearer …`。命名 HttpClient `expo-push`，超时 30 秒，不跟随重定向，启用 gzip / deflate 解压。`Endpoint` 必须是 https（本机回环地址可用 http 联调），否则后台任务报配置错误、不发请求——避免 AccessToken 走明文。
- **领取**：先查候选（`State=0 AND NextAttemptAtUtc<=now AND (LockedUntilUtc IS NULL OR LockedUntilUtc<now)`），再对每一行单独做条件 UPDATE 抢 5 分钟租约，受影响行数为 1 才算领到；按 `BatchSize` 分批发送，一轮最多 20 批。
- **重试**：`MessageRateExceeded`、整批 429 / 408 / 5xx、网络异常、回包不可解析 → `Attempts+1`，按 5 秒、30 秒、2 分钟、10 分钟、1 小时（第 n 次失败等第 n 项，±10% 抖动）退避；`Attempts >= MaxAttempts` 标 Failed。`MessageTooBig`、`InvalidCredentials`、其他 4xx、未知错误**不重试**直接 Failed。
- **设备失效**：ticket 或 receipt 报 `DeviceNotRegistered` → 撤销该设备（`device-not-registered`），该设备之后排队的记录发送时标 Skipped（`device-revoked`）。
- **凭据问题**：`InvalidCredentials` 与 HTTP 401 / 403 各记一条 Error 日志（不含令牌与 AccessToken），提示检查 `Push:Expo:AccessToken` 与 EAS 上登记的 APNs / FCM 凭据。
- **混批**：Expo 要求同一请求里的令牌属于同一个 Expo 项目；本仓库有 `hbweb-expo` 与 `hb-pos-handheld` 两个 EAS 项目。若 Expo 返回 `PUSH_TOO_MANY_EXPERIENCE_IDS`，发送器自动退化成逐条发送，不丢消息。
- **清理**（每小时一次，分批、每批 500 条，`DELETE` 一律带状态与时间条件）：超过 `DeliveryRetentionDays` 的已结束（Delivered / Failed / Skipped）发件箱记录；撤销超过 90 天、且已无任何发件箱记录引用的设备行。Pending / Sent 永不被清理。

## 2. 接口约定（供 App 端对接）

两个接口都在 `NotificationsController`（`/api/react/v1/notifications`），需要登录（Bearer）。响应沿用统一信封 `ApiResponse<T>`。**推送总开关关闭时两个接口照常工作。**

### 2.1 `POST /api/react/v1/notifications/push-devices` 注册 / 刷新令牌

请求体：

```json
{
  "expoPushToken": "ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]",
  "platform": "ios",
  "appVersion": "1.0.7",
  "runtimeVersion": "1.0.7"
}
```

- `expoPushToken`：必填，格式 `^(Exponent|Expo)PushToken\[[A-Za-z0-9_-]{10,200}\]$`，**且整串不超过 200 个字符**（内层字符集最长 200 加前缀会超过列宽，所以另有整串长度检查）。
- `platform`：必填，`ios` 或 `android`（忽略大小写与首尾空白）。
- `appVersion` / `runtimeVersion`：可选，各不超过 50 个字符。

成功（200）：

```json
{
  "success": true,
  "isSuccess": true,
  "message": "操作成功",
  "data": { "pushDeviceId": "3f2c9a1e5b7d4c8aa1b2c3d4e5f60718" },
  "timestamp": "2026-10-17T01:00:00Z"
}
```

校验失败（400）——消息是固定文案，不回显令牌；未登录 / 缺用户标识（401）：

```json
{
  "success": false,
  "isSuccess": false,
  "message": "expoPushToken 格式不正确。",
  "errorCode": "BAD_REQUEST",
  "code": "BAD_REQUEST",
  "timestamp": "2026-10-17T01:00:00Z"
}
```

语义（upsert）：

- 同账号同令牌：只刷新 `LastSeenAtUtc` 与版本，`pushDeviceId` 不变。
- **同一令牌已被别的账号占用 → 转给当前账号**：旧关联软撤销（`RevokedAtUtc` + `reassigned`），为当前账号新建一行。门店手持会换人登录，这是关键：上一个人的通知不会再发到下一个人手里（该设备上之前排队的推送也会因 `device-revoked` 被跳过）。
- 每个账号最多保留 **10 个活动设备**，超出的按最久未活跃撤销（`limit`），当前这台一定保留。

建议调用时机：登录成功且取到令牌后；每次 App 启动 / 回到前台时刷新（让 `LastSeenAtUtc` 保持新鲜）；令牌变化时再调一次。

### 2.2 `DELETE /api/react/v1/notifications/push-devices` 撤销令牌（退出登录时）

**只收请求体，不支持 query / 路径传令牌**（令牌放进 URL 会写进 nginx 访问日志）：

```http
DELETE /api/react/v1/notifications/push-devices
Content-Type: application/json

{ "expoPushToken": "ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]" }
```

成功（200）：

```json
{ "success": true, "isSuccess": true, "message": "操作成功", "data": { "revoked": 1 }, "timestamp": "2026-10-17T01:00:00Z" }
```

- 只撤销**属于当前账号**的活动令牌（原因 `logout`）。令牌不存在、已撤销、或属于别的账号都返回 200 且 `revoked: 0`，别人的令牌原样不动——所以接口幂等，可以放心重试。
- 令牌格式不合法 / 没带请求体 → 400。

### 2.3 与 App 端的其他约定

- 推送的 `data` 恒含 `notificationId` 与 `type`；点开推送后，App 凭登录态用 `notificationId` 从收件箱接口取完整内容（敏感类型的推送本身不带任何细节，见第 6 节）。
- `channelId` 固定为 `default`：Android 8+ 需要 App 创建 id 为 `default` 的通知渠道（`setNotificationChannelAsync('default', …)`）。
- 本文档不规定 App 端实现；App 端的权限申请、前台展示策略、角标不在后端范围内。

## 3. 配置项（配置节 `Push:Expo`，环境变量 `Push__Expo__*`）

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `Enabled` | `false` | 总开关。关闭时发布器不写发件箱、后台任务不发请求 |
| `Endpoint` | `https://exp.host/--/api/v2/push` | Expo 推送服务地址（不含 `/send`、`/getReceipts`）；必须是 https（本机回环地址除外） |
| `AccessToken` | 空 | Expo Access Token，**日志绝不打印**。EAS 项目开启「增强推送安全」时必填，否则可留空。只从环境变量注入 |
| `MaxAttempts` | `5` | 单条最多发送次数（含首次），达到后标 Failed |
| `BatchSize` | `100` | 每次 `/send` 的消息数；Expo 上限 100，超过按 100 处理 |
| `ReceiptDelayMinutes` | `15` | 受理后多久再取回执 |
| `DeliveryRetentionDays` | `14` | 已结束的发件箱记录保留天数 |
| `PollIntervalSeconds` | `5` | 后台任务轮询间隔 |
| `SensitiveTypePrefixes` | `["face.", "approval."]` | 敏感通知类型前缀，命中的只发通用文案；一旦配置就**完全替换**默认值 |
| `StaleAfterHours` | `24` | 发件箱记录创建超过该时长仍没发出就标 Skipped（`stale`），避免关了又开推送时补发陈旧通知 |

非敏感默认值的示例文件：`services/backend/BlazorApp.Api/appsettings.PushExpo.example.json`（不含 AccessToken）。**不要写 `appsettings.Development.json`**。

`docker-compose.yml` 只映射两个环境变量，都带 `:-` 默认值（没有它们的部署不会失败）：

```yaml
- Push__Expo__Enabled=${EXPO_PUSH_ENABLED:-false}
- Push__Expo__AccessToken=${EXPO_ACCESS_TOKEN:-}
```

其余配置项需要调整时，在 `.env` 里直接加 `Push__Expo__MaxAttempts=…` 这类变量并在 compose 里补映射（目前没有这个需要）。`ScheduledTasks:Enabled=false` 会让本任务整体不运行（见第 1 节）。

## 4. 上线步骤

> 默认关闭，**上线顺序不能乱：先迁移、再部署、最后才打开开关**。涉及生产的步骤须先取得明确授权；部署细节遵守 `hotbargain-vip-deploy` 的 runbook。

1. **前置确认**（只读）：EAS 项目里已登记 iOS 的 APNs Key（`.p8`）与 Android 的 FCM 凭据；App 端新版本（带 `expo-notifications`）已发布，至少有一批内测设备会上报令牌。
2. **只读预检**：用候选镜像执行 `--schema=check`。此时账本里还没有 `20261017.001-app-push`，预期以退出码 `20`（`SCHEMA_MAIN_MIGRATION_MISSING`）结束；同时核对账本：
   ```sql
   SELECT MigrationId, AppliedAtUtc FROM dbo.HBWebSchemaMigrationHistory ORDER BY MigrationId;
   ```
   通用 `--schema=migrate` 会执行镜像里**全部**缺失的迁移，必须先确认待执行清单恰好是你预期的（本功能只有 `20261017.001-app-push`）。
3. **显式迁移**（数据库先有恢复点）：
   ```bash
   sudo docker compose --env-file .env --profile schema run --rm --no-deps hb-api-migrate
   sudo docker compose --env-file .env run --rm --no-deps hb-api --schema=check   # 退出码必须是 0
   ```
   迁移只新建 `AppPushDevice` 与 `AppPushDelivery` 两张表，不改任何现有表与数据；旧版 API 不认识新表，所以迁移先于部署对线上无害。**新版 API 启动时会做只读 schema 检查，账本里缺这条迁移就拒绝启动**，所以不能跳过。
4. **部署新版主 API**（开关仍是默认关闭）。验证：健康检查通过；`POST /api/react/v1/notifications/push-devices` 用测试账号注册一个令牌返回 200 并在 `AppPushDevice` 里能查到；日志里没有 `schema-missing` 之类的错误。
5. **小范围灰度**：在服务器 `.env` 里设置 `EXPO_PUSH_ENABLED=true`（需要时设 `EXPO_ACCESS_TOKEN`，**输入不回显**，不进 shell 历史 / 聊天 / 工单；改前备份 `.env`），按常规方式重建 `hb-api` 容器。用测试账号调 `POST /api/react/v1/notifications/test` 给自己发一条测试通知（`system.test`，非敏感，推送会带原文），确认手机收到、`AppPushDelivery` 先 Sent、约 15 分钟后 Delivered。
6. **观察 24 小时**：看第 7 节的 State 分布与日志计数；重点是 `Failed` / `Skipped` 的错误码分布与积压。

## 5. 回滚

- **关推送**：把 `.env` 里 `EXPO_PUSH_ENABLED` 改回 `false`（或删掉），重建 `hb-api` 容器。发布器立刻不再写发件箱，后台任务不再发请求；令牌注册接口继续工作。**表保留**，不需要也不建议回滚 schema。
- 已入箱但没发出的 Pending 记录：保持关闭期间原样躺在表里；之后重新打开时，创建超过 `StaleAfterHours` 的会被标 Skipped（`stale`），不会补发陈旧通知。
- **回滚代码**：旧版镜像可以直接跑在已迁移的库上（账本里多出的迁移记录不影响旧版检查，旧版也不引用新表）。
- 如确需清空推送数据，属于数据删除，须单独授权并先备份，不属于常规回滚。

## 6. 隐私规则

推送的内容会显示在**锁屏**上（旁人看得到），并且会**经过第三方 Expo 服务器**（再到苹果 / 谷歌）。因此敏感类型不得带具体内容：

- 通知类型以 `SensitiveTypePrefixes`（默认 `face.`、`approval.`）开头的，推送的 `title` 固定为 `HB`，`body` 固定为 `You have a new notification. Open the app to view it.`，**不带门店名、分数、姓名、日期、编号**；`data` 只放 `notificationId` 与 `type`，**不合并**通知的 `DataJson`（里面有 `taskId` / `storeCode` / `entryId` 等标识）。
- 说明：`data.type`（如 `face.unwelcome-hit`）不会显示在锁屏上，但会随请求到达 Expo 服务器——它只暴露「通知类别」，不含任何门店、人员或事件细节；因此这两项（`notificationId`、`type`）是敏感类型推送里唯一保留的结构化字段。
- 现有类型的归属：`face.unwelcome-hit`（含相似度、任务 / 门店标识）、`face.unwelcome-expiring`（含名单条目标识）、`approval.submitted`（含员工姓名与请假日期）、`approval.decided`（含个人请假信息）是敏感类型；`store.price_updates`、`container.new_products`（只有数量）、`system.test` 不敏感，推送用原文。
- 新增通知类型时，先判断它的标题 / 正文 / `DataJson` 是否含个人信息或业务敏感标识；含的话要么用 `face.` / `approval.` 之外的敏感前缀并配进 `SensitiveTypePrefixes`，要么直接按这两个前缀命名。
- 实现上是纯函数 `ExpoPushContent.Build`，有单测固定：敏感类型的请求体里不会出现原始文案与 `DataJson` 内容。

日志与审计的纪律：

- 令牌是敏感值（持有它即可向该设备发推送）：日志只写**长度与 SHA-256 前 8 位**（形如 `len=41 sha256=1a2b3c4d`），不写明文；令牌只出现在请求体里，不进 URL。
- 发送器日志只写汇总计数与短错误码；**不写令牌、通知标题与正文、AccessToken，不写异常的 `Message`**（可能带地址 / SQL / 路径；网络与 JSON 异常只记类型名），Expo 回包 body 一律不记，日志里也不挂异常对象。
- `AppPushDelivery.LastError` 只存短错误码。

## 7. 排障

### 7.1 看日志

中心日志的最低级别是 Warning，所以下面的 Information 汇总只在容器日志里（`docker logs hb-platform-vite-api`）。

| 日志（关键词） | 级别 | 含义 |
| --- | --- | --- |
| `Expo 推送一轮完成: requests=…, claimed=…, accepted=…, retried=…, failed=…, skipped=…, delivered=…, receiptFailed=…, devicesRevoked=…, codes=[…]` | Information | 有动作的那一轮的汇总计数；`codes` 是各错误码出现次数。没有动作的轮次不记 |
| `App 推送后台任务本轮失败，稍后重试: Category=schema-missing` | Error | 开关打开但迁移没跑（表不存在）。去执行迁移；同类失败 10 分钟最多记一条，且每次失败后至少等 30 秒 |
| `Category=configuration` | Error | `Push:Expo:Endpoint` 不是 https 地址（详情是固定文案，不含配置值） |
| `Category=<异常类型名>` | Error | 其他未预期失败（数据库不可用等）；异常消息不入日志，需要时在容器内复现 |
| `Expo 推送凭据被拒绝（HTTP 401/403）` / `Expo 推送凭据无效（InvalidCredentials）` | Error | AccessToken 缺失或错误，或 EAS 上的 APNs / FCM 凭据有问题 |
| `App 推送设备查询失败，通知照常发布但不写推送发件箱 … SchemaMissing=True` | Warning | 发布器侧的同一问题（每条通知一条）；通知本身没受影响 |
| `推送令牌已转给新账号` / `推送令牌已撤销` / `账号推送设备超过上限` | Information | 令牌生命周期事件（只含长度 + 哈希前缀） |
| `App 推送后台任务已跳过：本进程未参与定时任务调度（ScheduledTasks:Enabled=false）` | Information | 该实例按设计不跑推送 |

### 7.2 查库（只读）

```sql
-- 发件箱状态分布与最老一条
SELECT State, COUNT(*) AS Cnt, MIN(CreatedAtUtc) AS OldestCreatedAtUtc
FROM dbo.AppPushDelivery GROUP BY State ORDER BY State;

-- 最近 1 小时的错误码分布
SELECT State, LastError, COUNT(*) AS Cnt
FROM dbo.AppPushDelivery
WHERE CreatedAtUtc > DATEADD(HOUR, -1, SYSUTCDATETIME()) AND LastError IS NOT NULL
GROUP BY State, LastError ORDER BY Cnt DESC;

-- 积压：已到期但还没发出的待发送记录（正常应接近 0）
SELECT COUNT(*) AS DuePending, MIN(NextAttemptAtUtc) AS OldestDue
FROM dbo.AppPushDelivery WHERE State = 0 AND NextAttemptAtUtc <= SYSUTCDATETIME();

-- 活动设备数（按平台）与撤销原因分布
SELECT Platform, COUNT(*) AS ActiveDevices FROM dbo.AppPushDevice WHERE RevokedAtUtc IS NULL GROUP BY Platform;
SELECT RevokeReason, COUNT(*) AS Cnt FROM dbo.AppPushDevice WHERE RevokedAtUtc IS NOT NULL GROUP BY RevokeReason;
```

### 7.3 常见现象

| 现象 | 可能原因 / 处理 |
| --- | --- |
| 某人收不到推送，但站内消息有 | 该账号没有活动设备（App 没上报令牌 / 已退出登录 / 被转给别人 / 超过 10 台被挤掉）：查 `AppPushDevice`；手机系统里关了通知权限或 App 没取到令牌 |
| `Pending` 一直不动 | 开关没开 / 本实例 `ScheduledTasks:Enabled=false` / 后台任务在报错（看日志）；或 `NextAttemptAtUtc` 在未来（退避中） |
| 大量 `Failed` 且 `LastError=DeviceNotRegistered` | 用户卸载 / 重装 / 重置了 App；设备已自动撤销，下次 App 启动会重新上报。批量出现要核对是不是 EAS 项目或凭据换过 |
| `http-401` / `InvalidCredentials` | EAS 项目开了「增强推送安全」但没配 `EXPO_ACCESS_TOKEN`，或令牌失效；或 APNs / FCM 凭据在 EAS 上过期 |
| `MessageRateExceeded` / `http-429` | 触发 Expo 限流，会按退避自动重试；持续出现再看是否有批量广播 |
| 大量 `Skipped` + `stale` | 推送关闭期间积压的记录在重新打开时被丢弃，符合设计 |
| 大量 `Skipped` + `device-revoked` | 手持换人登录或用户退出登录后，之前排队的推送被丢弃，符合设计（防止发到下一个人手里） |
| `Sent` 很久不变成 `Delivered` | 回执延迟默认 15 分钟；超过 24 小时查不到回执的会被收尾为 Delivered（`receipt-expired`）。长时间停在 `Sent` 说明取回执在持续失败，看 `receipt-http-…` 错误码计数 |
| iOS 收不到、Android 收得到 | 先看 `Failed` 的错误码；再查 EAS 上的 APNs Key 是否有效、App 的 bundle id 是否与凭据匹配 |

## 8. 已知限制

- **退出登录时没网**：App 调不通 `DELETE push-devices`，令牌会残留在该账号名下，直到这台设备被下一个账号登录（令牌转移）或同账号再次登录刷新。残留期间该账号的通知仍会推到这台设备——所以手持必须在换人登录时由新账号注册令牌来完成转移。
- **Expo 回执只保留约 24 小时**：超过窗口仍查不到回执的记录按 Delivered 收尾（`receipt-expired`），无法区分「真的送达」与「回执过期」。
- **被撤销设备的行 90 天后才清理**（且要求已无发件箱引用）；已结束的发件箱记录默认保留 14 天。
- **至少一次**：崩溃恢复后同一条推送可能重复；服务端无法撤回已发出的推送。租约是 5 分钟，覆盖「一次 HTTP 请求（≤ 30 秒）+ 写回结果」；唯一会超出的情形是 Expo 返回 `PUSH_TOO_MANY_EXPERIENCE_IDS`（混批）后退化成逐条发送、且 Expo 同时很慢——此时租约可能中途到期被别的实例重复领取，表现为个别重复推送，状态不会错乱（结果写回都带 `State=Pending` 条件）。
- 推送是**尽力而为**的补充通道，站内消息才是准确来源（未读数与收件箱不依赖推送）。通知被撤回 / 过期后，未发出的推送会被跳过，但**已经发到手机上的推送无法撤回**。
- 一个令牌同一时刻只属于一个账号；同一台设备上多个账号同时在线的场景不支持（手持是换人登录，不受影响）。
- 目前没有按通知类型单独关闭推送的配置；要整体关闭用 `EXPO_PUSH_ENABLED=false`。
