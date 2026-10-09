# 银行卡原路退款“额度预占（refund-claim）”设计说明

**状态：** 仅设计，未实现。来源：《Linkly 云支付链路审查》M20。
**结论先行：** 最小可落地版本仍需新增 claim 表 + 状态机 + 3 个端点，并同时改 WPF、iPad、手持三端的退款流程，工作量约等于一套 `InstallmentRepaymentClaim*`（现有同类实现约 4400 行，不含客户端），超过单个 PR 合理范围，且有 4 个需要产品拍板的问题（见第 6 节），所以本 PR 只交付设计，不硬做。

## 1. 问题

原单可退额度只在“退货单同步到服务端”时校验（`OrderSyncService` → `SalesReturnRecordPersistence.PrepareValidatedInsertAsync`：行级数量/金额由 `OrderReturnRecordValidator` 校验，卡付款额度由 `ValidateRefundPaymentCapacitiesAsync` 校验，二者都在 Serializable 事务内、对原单加 UPDLOCK/HOLDLOCK）。但卡退款发生在同步**之前**：

1. 设备 A、B 先后查询同一原单，各自看到“全部可退”；
2. 两台设备各自完成 Linkly 退款（钱已退回卡上）；
3. 先同步的退货单通过校验，后同步的被拒绝（`Card refund amount exceeds ...` 或行数量超额）；
4. 结果：卡上已超额退款，POS 侧订单永久同步失败，只能人工对账追回。

服务端校验本身是对的（并发安全），缺的是**资金流出之前**的占用。

## 2. 现状与已有的缓解

| 层 | 现状 |
|---|---|
| 同步时校验 | 行级（数量+金额）与卡付款级（按原卡引用）都有，串行化正确，但发生在退款之后 |
| 查询时校验 | `GetReturnContextAsync` 返回行/付款可退额度，仅供客户端展示，不占用 |
| Linkly R 创建（PR #678） | 服务端按 RFN 查同店原销售会话，校验原销售已批准，且 `原金额 ≥ 已批准/在途退款累计 + 本次`。**这是按卡交易的软上限**：非原子（两设备同时创建仍可能都通过）、不含行级、原交易查不到（直连/历史）时不拦截 |
| WPF 本地降级（本 PR 的 M21） | 合并本机未同步退货；记录可能过期时禁止原路退卡 |

## 3. 目标与非目标

目标：卡退款请求到达终端之前，服务端原子地为“这次退款”预占原单的行额度与卡付款额度；预占成功才允许创建 Linkly R 会话。
非目标：离线退卡（Linkly 云退款本来就必须联网）；现金/礼券退款（有各自校验，另议）；改变同步时的最终校验（保留作为兜底）。

## 4. 方案

沿用 `InstallmentRepaymentClaim*` 的成熟模式：持久状态机 + 部分唯一索引做原子闸门 + 幂等键 + 客户端恢复。

### 4.1 数据模型（新表，不改旧表）

`POSM_ReturnRefundClaim`（一次退货单对应一条）
- `ClaimGuid`（主键，= 客户端退货单 `ReturnOrderGuid`，天然幂等）、`StoreCode`、`ClaimantDeviceCode`、`CashierId`
- `OriginalOrderGuid`、`OriginalCardReference`（原卡付款引用，与同步校验 `originalReference` 同口径）
- `CardAmount`（本次卡退款总额）、`Fingerprint`（请求指纹，防同 key 不同内容）
- `Status`：`Prepared` → `ProviderPending` → `Committed`；终态 `Released` / `Declined`；异常 `Unknown`
- `IsBlocking`（持久列，状态机维护；`Prepared/ProviderPending/Unknown` 为 1）
- `ExpiresAtUtc`（仅 `Prepared` 有，如 10 分钟）、`LinklySessionId`、`LinklyTxnRef`、`Revision`、时间戳

`POSM_ReturnRefundClaimLine`（子表，每个被退行一条）：`ClaimGuid`、`OriginalOrderDetailGuid`、`Quantity`、`Amount`。

原子闸门：不靠“唯一索引挡并发”（同一原单允许多笔不同退货单并存，只要额度够），而是在事务内 `UPDLOCK, HOLDLOCK` 读取该原单的 **已落库退货记录 + 所有 blocking claim**，做与同步校验完全一致的额度计算，通过才插入 claim。这样“预占”和“同步校验”共用同一套额度算法，避免两套口径漂移。建议把现有校验里“已消耗额度”的读取抽成可注入“额外占用（blocking claims）”的形式，而不是复制一份。

### 4.2 端点（`Returns` 策略；与 M3 的 `CardRefund` 同权限口径）

- `POST /api/v1/orders/{originalOrderGuid}/refund-claims`：请求体含 `ClaimGuid`、行（`OriginalOrderDetailGuid` + 数量 + 金额）、`OriginalCardReference`、`CardAmount`。事务内校验额度并插入 `Prepared`；幂等重放返回同一 claim。额度不足返回 409 与剩余额度。
- `POST .../refund-claims/{claimGuid}/release`：未发起终端交易（`Prepared`）或主管确认“未退款”时释放。
- `GET .../refund-claims/{claimGuid}`：恢复/重启后确认状态。

### 4.3 与 Linkly R 的衔接

`LinklyCloudBackendTransactionRequest` 增加可选 `RefundClaimGuid`。`StartTransactionAsync` 对 R：
1. 开关 `ReturnRefundClaim:Mode` = `Off`（现状）/ `Audit`（缺 claim 只记告警）/ `Enforce`（缺 claim 拒绝，400）；
2. 有 claim 时校验：同店同设备、`Status=Prepared` 未过期、`CardAmount ≥ 本次 R 金额`、`OriginalCardReference` 与 RFN 一致；
3. 创建 Linkly 会话与 claim → `ProviderPending` 置于同一事务（或先置 `ProviderPending` 再创建会话，失败则回退 `Released`）。会话终态回写：成功 → 保持 blocking 直到退货单同步；`Failed/NotSubmitted/Cancelled` → `Released`；结果未知 → `Unknown`（继续占额度，等主管结案，对应审查 H9 的结案出口）。

### 4.4 与退货单同步的衔接

`OrderSyncService.InsertAsync` 在写入退货记录的同一事务里，把对应 `ClaimGuid` 的 claim 置 `Committed`（`IsBlocking=0`）。此后额度由真实退货记录承担，不再重复计算。同步校验保留，作为 claim 之外的兜底（例如无 claim 的旧客户端）。

### 4.5 过期与清理

- `Prepared` 超过 `ExpiresAtUtc` 自动视为释放（读取时过滤，后台任务物理清理）；
- `ProviderPending/Unknown` **不自动过期**——钱可能已退出，只能由主管结案或会话终态回写释放；
- 设备被重置/换绑时，其名下 blocking claim 需要在恢复中心可见并可结案。

### 4.6 客户端改动清单

- WPF：`CashPaymentWorkflowService` 卡退款分支在调用 `RefundAsync` 前先 claim；失败（额度不足/网络不可达）不发起终端交易；`CardRecoveryPresenter` 结案时调用 release/commit 状态。
- iPad / 手持（两端代码对应，必须同步）：`production-return-refund-adapter.ts` 在 `refund` 前 claim；`linkly-cloud-backend.ts` 的 create 请求带 `refundClaimGuid`；恢复/结案路径带 claim 状态。更新 `packages/pos-api-client` OpenAPI 快照与 generated schema，跑 `scripts/pos-shared` 一致性检查。
- 全部端：claim API 不可达时**不允许原路退卡**（fail-closed），这与 M21 在 WPF 本地降级时禁止原路退卡的策略一致。

## 5. 规模估算

| 模块 | 估算行数 |
|---|---|
| claim 表/子表 schema 初始化 + 迁移脚本 | ~250 |
| claim 仓储 + 状态机服务（含幂等、指纹、过期、Unknown） | ~1200 |
| 控制器、契约、OpenAPI | ~300 |
| 额度算法抽取并与同步校验共用 | ~300 |
| Linkly R 衔接 + 会话终态回写 + 同步 commit | ~400 |
| WPF 客户端 | ~500 |
| iPad + 手持 + api-client + 共享清单 | ~900 |
| 测试（服务/仓储/并发/三端） | ~1500 |
| **合计** | **~5000+** |

## 6. 需要产品/业务拍板的问题

1. **claim API 不可达时是否一律禁止原路退卡？** 技术上最安全；业务上断网时顾客只能退现金/礼券。（本 PR 的 M21 在 WPF 本地降级时已采取禁止策略。）
2. **`Unknown` 状态的 claim 占额度多久、谁能结案？** 建议沿用主管三态结案（已退款/未退款/继续等待）；需要确认哪些角色有结案权限。
3. **是否允许多设备同时对同一原单的不同行并发退货？** 技术上可以（行级额度独立）；若业务想“一单同时只允许一台设备在退款中”，可在 4.1 的原子闸门里加按原单的 blocking 唯一约束，实现更简单但体验更保守。
4. **灰度策略：** 建议先上服务端 + `Mode=Audit`，再发三端客户端，最后切 `Enforce`。需要确认三端强制升级的节奏（手持/iPad 走 OTA，WPF 走安装包更新）。

## 7. 建议的分期落地

1. 服务端：claim 表、端点、额度算法抽取、`Mode=Off`；
2. WPF + iPad + 手持接入，`Mode=Audit` 观察日志；
3. 切 `Enforce`，同时把 M3 里“原交易查不到不拦截”的口径收紧。

每一期可独立发布，回滚只需把 `Mode` 调回 `Off`。

## 8. 测试要点（落地时）

- 并发：两线程同时对同一原单预占，总额超过时恰好一个成功（SQL Server 集成测试，参照 `LinklySettlementSqlServerIntegrationTests`）；
- 幂等：同 `ClaimGuid` 重放返回同一结果，不同指纹拒绝；
- 状态机：`Prepared` 过期自动释放，`ProviderPending/Unknown` 不过期；
- 同步 commit：退货单同步成功后 claim 不再重复占额度；
- Linkly 衔接：无 claim / 过期 claim / 金额超出 claim / RFN 与 claim 不一致均拒绝；
- 三端：claim API 超时、5xx、409 时不发起终端交易。
