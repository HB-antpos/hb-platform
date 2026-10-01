# 多端中心日志接入说明

## 写入接口

外部项目统一调用：

```http
POST /api/system/logs/ingest
X-Log-Project: hbweb_rv
X-Log-Key: <项目日志密钥>
Content-Type: application/json
```

请求体：

```json
{
  "logs": [
    {
      "level": "Error",
      "message": "请求失败",
      "timestampUtc": "2026-06-05T00:00:00Z",
      "projectCode": "hbweb_rv",
      "environment": "Production",
      "sourceType": "Web",
      "serviceName": "hbweb_rv",
      "traceId": "trace-001",
      "requestPath": "/api/example",
      "requestMethod": "GET",
      "statusCode": 500,
      "userId": "user-guid",
      "userName": "sean",
      "exceptionType": "RequestError",
      "exceptionMessage": "服务器内部错误",
      "stackTrace": "stack...",
      "properties": {
        "screen": "System.Users"
      }
    }
  ]
}
```

必填字段：`level`、`message`、`timestampUtc`、`projectCode`、`environment`、`sourceType`。

单次最多写入 200 条。公开入口的原始请求体上限为 4 MiB；默认单字段为 32 KiB、单条日志为 64 KiB、单批聚合为 1 MiB。字段按 JSON 解码后的 UTF-8 计量；单条按保留 `null`、直接编码非 ASCII 且保留必要 JSON 转义的规范 JSON UTF-8 计量；单批在有效 `Content-Length` 存在时按实际请求体字节计量，分块请求则回退到整份规范 JSON。客户端应在超出这些边界前拆分或截断。日志客户端必须异步旁路上报，不能阻塞用户请求、移动端 PDA 操作、收银支付或订单同步。

后端会按项目做每分钟写入限流，默认 `MaxIngestRequestsPerMinute=120`、`MaxIngestLogsPerMinute=5000`、`MaxIngestBytesPerMinute=16777216`。MVC 资源过滤器会在模型绑定读取请求体前完成项目头鉴权；鉴权失败直接返回 401，鉴权成功则先扣 request 和 bytes 额度。bytes 优先使用有效 `Content-Length`，无法预知长度的分块请求保守按 4 MiB 扣除，避免畸形 JSON 获得免费解析预算。字段、单条和单批校验随后执行，只有验证通过的日志才扣 log 数量额度，因此不会双扣 request/bytes，也不会让无效请求消耗 log 额度。字段和聚合预算分别由 `MaxIngestFieldBytes`、`MaxIngestItemBytes`、`MaxIngestBatchBytes` 配置；这些预算仅适用于公开 HTTP 入口，不改变后端本地队列的批处理契约。浏览器和移动端的写入 key 会进入运行包，只能视为公开写入凭据；生产环境需要配合限流、项目级保留天数和异常审计使用，不能把它当作长期强密钥。

## 项目配置

后端从 `ApplicationLogging:Projects` 读取允许接入的项目：

配置骨架见 `BlazorApp.Api/appsettings.ApplicationLogging.example.json`，部署时合并到实际 `appsettings` 或环境变量中。

```json
{
  "ProjectCode": "hbweb_rv",
  "DisplayName": "Web 后台",
  "ApiKeyHash": "<sha256-lower-hex>",
  "Enabled": true,
  "RetentionDays": 30
}
```

`ApiKeyHash` 是项目日志密钥的 SHA-256 小写十六进制摘要。明文密钥只放部署环境变量或服务器配置，不提交仓库。

生产 compose 已连续配置七个项目。只有已启用的外部项目需要注入合法的 64 位十六进制 SHA-256 摘要：

```bash
CENTER_LOG_HBWEB_RV_KEY_SHA256=<sha256-lower-hex>
CENTER_LOG_HBPOS_API_KEY_SHA256=<sha256-lower-hex>
CENTER_LOG_HBPOS_IPAD_KEY_SHA256=<sha256-lower-hex>
CENTER_LOG_HBPOS_HANDHELD_KEY_SHA256=<sha256-lower-hex>
CENTER_LOG_HBPOS_WIN_KEY_SHA256=<sha256-lower-hex>
```

WPF 客户端的明文写入 Key 存在 GitHub 仓库 Secret `HBPOS_WIN_CENTER_LOG_KEY`，只在 `wpf-inno-smoke-build.yml` 构建安装包时以环境变量 `HbposCenterLogApiKey` 注入，写进主程序集元数据（与上传地址 `https://hotbargain.vip/api/system/logs/ingest` 一起）作为客户端默认配置；本地与 PR 构建没有该值，客户端不上传。缺少该 Secret 时工作流直接失败。门店电脑仍可用环境变量 `HBPOS_LOG_CENTER_ENABLED=false` 关闭上传，或用 `HBPOS_LOG_CENTER_API_KEY` / `HBPOS_LOG_CENTER_INGEST_URL` 覆盖。

项目清单和默认保留期：

- `HBBBackend`：内部项目，启用，7 天，不配置外部写入摘要。
- `hbweb_rv`：Web 前端，启用，7 天。
- `HbwebExpo`：移动端，禁用，7 天。
- `hbpos_win`：WPF 客户端，启用，30 天，`sourceType=POS`。
- `hbpos_api`：WPF 收银后端，启用，7 天。
- `hbpos_ipad`：iPad 客户端，启用，30 天，`sourceType=POS`。
- `hbpos_handheld`：手持收银客户端，启用，30 天，`sourceType=POS`。

清理任务会覆盖 `Projects` 中的全部项目，包括已禁用项目，避免停用后遗留日志无限保留。

## 发布门槛：POSM 审计迁移先行

`pos_operation_audit` 的 schema 迁移唯一所有者是 `Hbpos.Api`；`services/backend` 的 `BlazorApp.Api` 只查询同一 POSM 数据，不得增加第二个迁移器。

本次及后续包含 POSM 审计字段的发布必须按以下顺序执行：

1. 先部署并启动单一 `Hbpos.Api` migration owner；当前 [`apps/pos-wpf/docker-compose.hotbargain.yml`](../../apps/pos-wpf/docker-compose.hotbargain.yml) 只声明一个 `hbpos-api` 实例。
2. 在 POSM 验证 `pos_operation_audit.device_system` 为 nullable，且 `IX_pos_operation_audit_device_system_time` 已存在。
3. 仅在上述结构检查通过后，依次部署 `BlazorApp.Api`、Web 后台和 iPad 客户端。

当前初始化逻辑是 check-then-DDL，不能并发运行。未来如扩容 `Hbpos.Api` 多实例，必须先引入分布式迁移锁或独立 migration job；未完成前禁止多个实例同时执行这段 schema 初始化。

## 查询接口

查询接口需要后台权限 `System.ViewLogs`：

```http
GET /api/system/logs?projectCode=hbweb_rv&level=Error&pageNumber=1&pageSize=50
GET /api/system/logs/{id}
GET /api/system/logs/summary?startUtc=2026-06-05T00:00:00Z
```

常用查询参数：`projectCode`、`environment`、`sourceType`、`level`、`category`、`requestPath`、`traceId`、`userId`、`userName`、`keyword`、`startUtc`、`endUtc`。

`summary` 在原有统计和 `pipeline` 指标之外返回 `status`：后端采集开关、最低等级、默认项目/环境、服务名，以及各项目的启用状态、配置状态、有效保留天数和最后接收时间。内部项目的 `credentialConfigured` 固定为 `null`；外部项目只有启用且摘要合法时为 `Ready`。响应绝不返回密钥、摘要或摘要片段。`lastReceivedAtUtc` 按服务端 `CreatedAt` 的项目最大值计算，不受当前统计筛选影响。

## 各端项目码

- 后端：`HBBBackend`，`sourceType=Backend`
- Web 后台：`hbweb_rv`，`sourceType=Web`
- 移动端：`HbwebExpo`，`sourceType=Mobile`
- 收银端：`hbpos_win`，`sourceType=POS`
- 收银后端：`hbpos_api`，`sourceType=Backend`
- iPad 收银端：`hbpos_ipad`，`sourceType=POS`
- 手持收银端：`hbpos_handheld`，`sourceType=POS`

## 上报原则

- 错误、异常、关键失败全量上报；高频成功日志采样或不上报。
- 日志接口失败必须吞掉或进入本地小队列，不能影响主业务。
- 不上报 token、密码、完整银行卡信息、授权码、敏感图片 URL。
- API 层记录技术失败，页面/业务层只记录关键业务失败，避免重复上报同一错误。
- `sourceType` 只使用 `Backend`、`Web`、`Mobile`、`POS`；请求错误、页面异常、支付同步等细分来源写入 `category`。
