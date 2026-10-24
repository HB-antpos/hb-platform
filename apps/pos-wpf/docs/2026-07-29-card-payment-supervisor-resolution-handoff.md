# WPF 主管付款结案与审计上传交接

> **2026-10-24 更新（PR #976，WPF 1.0.91）：本文第 2 节起关于「证据必填」与「实时核验」的描述已被取代，其余内容保留为 07-29 的历史记录。**
>
> - 结案改为居中弹窗，只有选填的主管备注（含预设）和选填的付款参考号，**不再有证据输入框**。
>   备注预设：已付款=刷卡机显示支付成功 / 顾客提供支付记录；未付款=刷卡机显示失败 / 刷卡机离线 / 刷卡机卡住了。
> - 「确认未付款」不再要求银行证据（付款与退款的「确认未退款」同理）。仍保留的必填：
>   「确认已退款」要退款参考号或备注、「继续等待」（退款）要备注、顾客已被扣款的「已退款或另行处理，关闭」出口要备注。
> - **主管结案以人工确认为准，不再向 Linkly / 刷卡机实时核验**：查询失败、仍在处理、与终端结论矛盾都不再拒绝主管的决定。
>   仅保留本机数据的保险：已保存与本笔一致的批准小票时仍拒绝「确认未付款」；只有 TxnRef 的后端会话会尽力认领真实 SessionId，结果不影响决定。
> - Linkly 回包 `TI`（OPERATOR TIMEOUT）视为终端明确拒绝。
> - iPad / 手持端同步对齐：人工核实表单去掉证据、备注选填并带预设；「已收款」前的服务商只读查询改为尽力而为（查不到时按人工结论入账，支付方明确给出批准 / 拒绝终态时仍沿用原逻辑）。

## 1. 交接摘要

- 日期：2026-07-29
- 仓库：`D:\DevRepos\hb-platform`
- 分支：`main`
- 云端闭环基线：`bb5ee1b9`，已推送至 `origin/main`
- 审查修复：本文随本轮后续修复提交更新，具体提交见第 10.3 节
- 工作区：`AGENTS.md`、`CLAUDE.md` 为未提交的工具规则漂移，不属于本轮修复
- 提交结构：云端闭环基线、五个审查修复提交，以及本文档与 Web 验证提交

本轮已完成“主管付款结案备注改为选填”的客户端修改与验证。金融安全条件没有放宽：

- 确认已付款：付款参考号或银行证据至少填写一项。
- 确认未付款：银行证据仍必填。
- 继续等待：备注和证据均可为空，付款保持 `Recovering` 和锁定状态。
- 备注仍限制为最多 500 个字符。
- 刷卡退款和分期退款的主管原因仍必填。

云端闭环已在基线 `bb5ee1b9` 完成。第 4-8 节保留为实施前诊断快照，当前实现、恢复结果和剩余生产验收统一以第 10 节为准。

## 2. 本轮已完成内容

### 2.1 付款结案校验

文件：

- `apps/pos-wpf/src/Hbpos.Client.Wpf/Services/CardPaymentRecoveryService.cs`

`CardPaymentSupervisorResolutionRules.TryNormalize` 已移除付款结案备注的统一必填规则，同时保留：

- 主管身份校验。
- 备注、证据和付款参考号长度限制。
- `ConfirmPaid` 的参考号或证据要求。
- `ConfirmNotPaid` 的银行证据要求。

空白备注会被归一化为 `string.Empty`，不会改变现有 record/interface。

### 2.2 SQLite 状态和主管日志

文件：

- `apps/pos-wpf/src/Hbpos.Client.Wpf/Services/LocalCardPaymentAttemptRepository.cs`
- `apps/pos-wpf/src/Hbpos.Client.Wpf/Services/LocalFinancialSupervisorResolutionRepository.cs`

行为：

- `ActiveSession` 允许 `Reason = ""`。
- `CardRefund` 和 `InstallmentRefund` 仍拒绝空白原因。
- `Reason TEXT NOT NULL` 保持不变，无数据库迁移。
- attempt 状态 CAS 更新与 `LocalFinancialSupervisorResolutions` INSERT 仍在同一 SQLite transaction。
- 只有证据、没有备注时，`ResponseText` 保存为 `Evidence: <证据>`，无前导空格。
- 备注和证据均为空时，`ResponseText` 保存空字符串；决定仍保存在 `ResponseCode`。

### 2.3 UI 文案

文件：

- `apps/pos-wpf/src/Hbpos.Client.Wpf/Resources/Strings.resx`
- `apps/pos-wpf/src/Hbpos.Client.Wpf/Resources/Strings.zh-CN.resx`

文案：

- 英文：`Supervisor note (optional)`
- 中文：`主管备注（选填）`

仅修改资源文案，没有修改弹窗布局、字段顺序或按钮。

## 3. 已完成验证

新增或调整的测试文件：

- `apps/pos-wpf/tests/Hbpos.Client.Tests/CardPaymentRecoveryServiceTests.cs`
- `apps/pos-wpf/tests/Hbpos.Client.Tests/CardRefundRecoveryPresenterTests.cs`
- `apps/pos-wpf/tests/Hbpos.Client.Tests/LocalFinancialSupervisorResolutionRepositoryTests.cs`
- `apps/pos-wpf/tests/Hbpos.Client.Tests/LocalizationAndSettingsTests.cs`

覆盖范围：

- 空备注 + 付款参考号可确认已付款。
- 空备注 + 银行证据可确认未付款。
- 空备注可继续等待，attempt 保持 `Recovering`，付款锁不解除。
- 已付款缺少参考号和证据仍失败。
- 未付款缺少银行证据仍失败。
- 超长备注仍失败。
- 真实 SQLite 双主管 CAS 竞争只有一个成功。
- 状态更新和主管日志 INSERT 原子提交。
- 空备注保存为 `""`。
- 刷卡退款、分期退款的空原因仍被拒绝。
- 中英文选填文案正确。

验证结果：

```text
定向测试：108/108 通过
WPF Release 构建：0 警告，0 错误
排除既有基线失败后的完整测试：2004/2004 通过
```

完整套件仍有一个既有失败，与本轮修改无关：

```text
TransactionHistoryViewModelTests.Suspended_order_labels_follow_localization_culture
Expected: "Suspended"
Actual: null
```

任务文件的 `git diff --check` 已通过。全工作区检查仍被无关文件阻断：

```text
apps/pos-wpf/src/Hbpos.Client.Wpf/Services/VoucherApiClient.cs:166
trailing whitespace
```

## 4. 实施前审计数据链路快照

> 本节至第 8 节记录 `bb5ee1b9` 实施前的诊断与待办，不代表当前实现状态。当前结论以第 10 节为准。

主管付款结案成功后：

1. `BuildPaymentSupervisorJournal` 创建稳定的 `AuditEventId` 和审计 payload。
2. attempt CAS 与 `LocalFinancialSupervisorResolutions` INSERT 同事务提交。
3. `FinancialSupervisorAuditReplayService.PersistAfterCommitAsync` 将 payload 写入 `OperationAuditOutbox`。
4. `OperationAuditUploadService` 向以下接口发送批次：

```text
POST api/v1/operation-audits/batch
```

客户端 payload 当前包含：

```text
OperationType = CARD_PAYMENT_SUPERVISOR_RESOLUTION
Outcome       = ConfirmPaid | ConfirmNotPaid | ContinueWaiting
SafeMessage   = 主管备注
Properties:
  attemptGuid
  operationGuid
  sessionId
  evidence
  financialReference
```

注意：`LocalFinancialSupervisorResolutions.AuditPersistedAt` 只表示 payload 已写入本地 outbox，不表示云端已经接收。

## 5. 实施前已确认的云端审计阻断

服务端文件：

- `apps/pos-wpf/src/Hbpos.Api/Services/OperationAuditIngestService.cs`

当前服务端会拒绝上述事件：

1. `AllowedOperationTypes` 不包含 `CARD_PAYMENT_SUPERVISOR_RESOLUTION`。
2. `AllowedOutcomes` 只允许 `Succeeded`、`Denied`、`Failed`，不允许三态决定名称。
3. `AllowedPropertyKeys` 不包含 `attemptGuid`、`operationGuid`、`sessionId`、`evidence`、`financialReference`。

实际联网上传时，事件首先会收到：

```text
INVALID_OPERATION_TYPE
operationType is not supported
```

客户端随后会将该 outbox 事件标记为 `Rejected`，不会自动重试。即使只放行 operation type，仍会继续遇到 `INVALID_OUTCOME`；即使再修复 outcome，未加入白名单的证据和金融参考号仍会被服务端丢弃。

## 6. 实施前查看路径

### 6.1 POS 同步中心

路径：

```text
同步中心 -> 审计日志
```

当前只能看到 EventId、状态、时间和上传错误，不显示备注或证据。联网上传后，该事件预计显示为 `Rejected / INVALID_OPERATION_TYPE`。

### 6.2 本地主管结案记录

数据库：

```text
%LOCALAPPDATA%\Hbpos.Client\hbpos_client.db
```

表：

```text
LocalFinancialSupervisorResolutions
```

只读查询：

```sql
SELECT
    ResolvedAt,
    Target,
    Decision,
    OperatorCashierId,
    OperatorUserGuid,
    OperatorName,
    Reason,
    Evidence,
    FinancialReference,
    AuditEventId,
    AuditPersistedAt
FROM LocalFinancialSupervisorResolutions
ORDER BY ResolvedAt DESC;
```

### 6.3 本地上传队列

数据库：

```text
%LOCALAPPDATA%\Hbpos.Client\hbpos_logs.db
```

表：

```text
OperationAuditOutbox
```

只读查询：

```sql
SELECT
    OccurredAtUtc,
    State,
    AttemptCount,
    LastErrorCode,
    LastErrorMessage,
    PayloadJson
FROM OperationAuditOutbox
ORDER BY OccurredAtUtc DESC;
```

查看本地数据库时建议先关闭 POS，或复制数据库及对应的 `-wal`、`-shm` 文件后只读查看，避免工具持有写锁。

### 6.4 Web 员工操作日志

目标页面：

```text
/pos-admin/operation-logs
```

权限：

```text
Permissions.PosTerminal.Audit.View
```

相关文件：

- `apps/web/src/pages/PosAdmin/OperationLogs/index.tsx`
- `apps/web/src/pages/PosAdmin/OperationLogs/operationLogsLogic.ts`
- `services/backend/BlazorApp.Api/Controllers/React/PosOperationAuditController.cs`

实施前由于服务端拒绝，云端页面查不到这些主管付款结案事件。

实施前 Web 的 `OPERATION_TYPE_KEYS` 尚未包含新事件类型。即使服务端放行，页面也会显示原始代码 `CARD_PAYMENT_SUPERVISOR_RESOLUTION`，并且操作类型下拉框不能直接选择该类型。

## 7. 实施前最小修复清单（已完成）

建议按以下顺序完成，不修改金融状态机：

1. 客户端审计事件：
   - `OperationType` 保持稳定代码 `CARD_PAYMENT_SUPERVISOR_RESOLUTION`。
   - `Outcome` 改为服务端允许的 `Succeeded`。
   - 三态决定继续放在 `ReasonCode`。
2. Hbpos.Api：
   - 将 `CARD_PAYMENT_SUPERVISOR_RESOLUTION` 加入 `AllowedOperationTypes`。
   - 将主管结案所需属性加入 `AllowedPropertyKeys`。
   - 保留现有敏感信息清洗和长度限制。
3. Web：
   - 为新 operation type 增加中英文显示名称。
   - 加入操作类型筛选下拉。
   - 详情中将 `SafeMessage` 作为主管备注展示。
   - 从 `PropertiesJson` 展示证据和金融参考号。
4. 测试：
   - 客户端 payload 合约测试。
   - Hbpos.Api 接收并保存备注、证据、参考号测试。
   - 非白名单属性仍被过滤测试。
   - Web 映射、筛选和详情展示测试。
   - 真实 SQLite outbox 从 Pending 到成功确认的集成测试。

## 8. 实施前验收清单

- [ ] 主管付款结案事件不再出现 `INVALID_OPERATION_TYPE`。
- [ ] 云端 `pos_operation_audit` 存在对应 `AuditEventId`。
- [ ] Web 员工操作日志可按“主管付款结案”筛选。
- [ ] 详情页显示决定、主管身份、备注、证据和付款参考号。
- [ ] 空备注显示为 `-`，不影响证据和参考号显示。
- [ ] ContinueWaiting 仍保持付款锁。
- [ ] 上传失败或离线时事件保持可恢复，不丢失本地主管日志。
- [ ] 重启重放使用原 `AuditEventId`，服务端重复接收为幂等 duplicate。
- [ ] 刷卡退款和分期退款的原因必填规则未改变。

## 9. 工作区注意事项

- `AGENTS.md`、`CLAUDE.md` 是本轮开始前已存在的工具规则漂移，不得纳入修复提交。
- 不得使用 `git reset --hard`、`git checkout --` 或批量覆盖清理。
- 后续提交必须按任务路径精确暂存，并继续使用中文提交信息和 reasonix。
- `.artifacts/**`、`apps/web/tmp/**`、`bin/**`、`obj/**`、本地日志和 SQLite 文件不得提交。

## 10. 2026-07-29 云端闭环续作

### 10.1 已实现

- 客户端 `CARD_PAYMENT_SUPERVISOR_RESOLUTION` 事件统一发送 `Outcome = Succeeded`。
- `ConfirmPaid`、`ConfirmNotPaid`、`ContinueWaiting` 继续保存在 `ReasonCode`，支付状态机和锁定规则未修改。
- Hbpos.Api 已放行该 operation type，以及 `attemptGuid`、`operationGuid`、`sessionId`、`evidence`、`financialReference` 五个属性。
- 服务端仍只持久化通用 Outcome；现有清洗、长度限制、未知属性过滤和 EventId 幂等逻辑保持不变。
- Web 操作类型映射和中英文文案已补齐，下拉框、列表和详情继续复用现有 `OPERATION_TYPE_KEYS`，未修改页面结构。
- 本轮没有新增 API、DTO、数据库表或 schema 迁移。

### 10.2 旧队列兼容与恢复

实施前只读检查发现，本机已有 3 条该类型事件处于 `Rejected / INVALID_OPERATION_TYPE`。

- 服务端仅针对 `CARD_PAYMENT_SUPERVISOR_RESOLUTION` 兼容旧的三态 Outcome，并在入库时规范化为 `Succeeded`。
- 不扩大全局 `AllowedOutcomes`，其他 operation type 仍只能使用 `Succeeded`、`Denied`、`Failed`。
- 不自动修改本机 `hbpos_logs.db`，也不批量重排其他永久拒绝事件。
- 2026-07-29 本地 API 恢复后已在 POS 中手动重试这 3 条记录；只读复制检查 `hbpos_logs.db` 时，`OperationAuditOutbox` 已清空。
- POSM 只读精确查询已按原 EventId 找到这 3 条记录：`cb48082e-0745-4ee2-bbcc-81f43a96cff2`、`64cf8c8d-87f3-4591-9bcd-e20939e733b6`、`982a1917-f7d0-4b81-9b9b-670da7c64897`。三条均为 `CARD_PAYMENT_SUPERVISOR_RESOLUTION / Succeeded / ConfirmNotPaid`。

### 10.3 验证结果

本轮代码审查修复提交：

```text
9190ab6a 修复退出审计与超时日志 reasonix
05b8dcf5 修复扫码关闭线程归属 reasonix
5c837727 恢复文件日志瞬时故障写入 reasonix
d4562ff9 修复 Linkly 回滚凭据判代 reasonix
c3cbfe58 修复设备注册迟到结果污染 reasonix
```

```text
指定 7 组客户端聚焦回归：282/282 通过
Hbpos.Client.Tests：2013/2013 通过
OperationAuditIngestServiceTests：16/16 通过
Web operation-logs 测试：通过
WPF Release 构建：0 警告，0 错误
Web production 构建：通过（仅在当前进程提供构建校验占位配置，未写入配置文件）
git diff --check：通过
```

补充烟测证据：

- 本地 `OperationAuditOutbox` 只读快照为 0；三个旧 EventId 已在 POSM 中确认，Outcome 和 ReasonCode 均符合预期。
- Web 浏览器已打开 `/pos-admin/operation-logs`，但当前浏览器会话停在登录页，因此没有绕过认证执行真实下拉交互；代码全集、映射键和中英文文案测试均已通过。
- 独立只读代码复核发现的“相同时间戳回归缺口”和文档结论冲突均已修复，生产实现无剩余 correctness finding。

本地 API 验证不等同于生产部署。生产环境仍需独立完成以下验收：

- [ ] 发布生产 Hbpos.Api 与 Web，并完成健康检查和回滚路径确认。
- [ ] 通过生产 API 新增一条主管付款结案事件，确认不再出现 `INVALID_OPERATION_TYPE` 或 `INVALID_OUTCOME`，重复提交原 EventId 为幂等 duplicate。
- [ ] 在已认证 Web 会话中确认下拉存在“主管付款结案”和“Card Payment Supervisor Resolution”，并可筛选查看安全业务属性。
