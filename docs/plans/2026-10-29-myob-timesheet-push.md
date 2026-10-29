# MYOB Timesheet 推送（PUT）· 方案

> 状态：**方案已确认（2026-10-29），未开始实现**。第 10 节：实测环境**改为用一个生产公司文件（B）**（用户 2026-10-29 改选，不再新建测试文件）；其余按推荐项：新增权限码 `Myob.Timesheet.Push`；MYOB 已有未发薪记录时允许覆盖但须单独勾选；可挑人推。第 3 节的「实测」未做之前，任何写 MYOB 的正式推送代码都不合入、不开总闸。
> 进度：已选 Robinson Road；测试卡 `test test`（Employee ID `0086`）已在该生产文件里建好，连接已授权。探针代码已写好（本 PR），待在测试卡上补全 Payroll Details、合并并部署主 API 后运行（第 3.3 节）。
> 范围：把「工资周期锁定后的转账工时」写进发薪公司的 MYOB Timesheet，并能**按批次撤销**。不跑 Pay Run、不报 STP、不写员工卡、不碰现金部分。
> 与总方案关系：落实 `2026-10-27-myob-payroll-integration.md` 第 8 节「阶段 2：推送 Timesheet」。已有的预览、类别映射、员工绑定、发薪公司规则全部沿用，本方案只新增「写」与「撤销」。

## 0. 结论速览

| 问题 | 结论 |
|---|---|
| 要做什么 | 在预览页「推送到 MYOB」按钮背后，实现 `PUT /{cf_uri}/Payroll/Timesheet/{EmployeeUID}`，按员工逐个写；每次写入前后各读一次 MYOB 校验。 |
| 能不能撤销 | **发薪前能，发薪后不能**（Processed 的记录 API 不能改）。撤销 = 用推送前留下的快照再 PUT 回去（原来没有就清空）。**撤销的具体做法取决于第 3 节实测**。 |
| 最大风险 | PUT 的语义（覆盖 / 合并 / 能否清空）官方文档没写透，我无法保证。所以**先做实测，再写代码**。 |
| 安全阀 | ① 总闸 `Myob__TimesheetPushEnabled` 默认关（已存在）；② 新增灰度白名单（按公司文件）；③ 新权限码 `Myob.Timesheet.Push`，默认不授予任何角色；④ 每次推送重新服务端校验，不信任预览结果。 |
| 要建表吗 | 要：`MyobTimesheetPush`（总方案第 4 节已规划），迁移 `20261029.005-myob-timesheet-push`（当前最大是 `.004`）。 |
| 发布顺序 | 迁移 → 主 API（总闸关）→ Web → 灰度打开总闸。无移动端改动、无 OTA、无原生包。 |
| 回滚 | 关总闸立刻停止所有写入；已写入的批次用「撤销」恢复；代码可整体回滚，新表留着无害。 |

## 1. 现状（代码核对，2026-10-29）

| 已有 | 位置 |
|---|---|
| 推送规划（纯函数）：按公司分摊固定转账工时、按日装填、映射 MYOB 类别 | `Services/Myob/MyobTimesheetPlanner.cs` → `MyobCompanyPlan.Pushed`（`MyobPlanLine(ConnectionGuid, Date, CategoryKey, Minutes)`） |
| 预览（只读）：拦截项、提示项、MYOB 现状（`Clear / HasEntries / Processed / CheckFailed`） | `Services/Myob/MyobTimesheetPreviewService.cs`；`GET api/react/v1/myob/timesheet/preview` |
| 只读客户端：`GetTimesheetStatusAsync`（GET Timesheet，只判断有无记录 / Processed） | `Services/Myob/MyobApiClient.cs` |
| 总闸与 DTO 字段 | `MyobOptions.TimesheetPushEnabled`（默认 false）、`MyobTimesheetPreviewDto.PushEnabled` |
| 前端按钮（禁用占位） | `apps/web/src/pages/PosAdmin/Myob/PushPreviewTab.tsx` |
| 类别映射、员工绑定、发薪公司覆盖 | `MyobPayCategoryService`、`EmployeeMyobLink`、`MyobPayrollEmployerRules` |
| 「批次 + 撤销」的先例 | `MyobEmployeeSyncService` / `MyobEmployeeSyncController`（迁移 `20261029.001`），本方案的批次与撤销沿用同一思路 |
| **没有的** | 任何对 MYOB 的 PUT / POST 数据写入（客户端里只有换 token 的 POST）；推送日志表；推送权限码；撤销。 |

## 2. 总体流程

```
Web「推送到 MYOB」
  │  勾选员工 → 确认弹窗（张数 / 工时 / 覆盖数 / 已知晓的提示）
  ▼
POST timesheet/push（≤10 人一块，同一个 batchGuid，前端按块循环并显示进度）
  │  ① 总闸、灰度白名单、权限、分店范围
  │  ② 服务端重新规划（与预览同一段代码），重新校验拦截项（锁定 / 绑定 / 类别映射 / 时薪 / 分店映射）
  │  ③ 对每个员工：
  │     a. GET MYOB Timesheet（现状 + 取快照）
  │     b. Processed → 跳过；已有未发薪记录且未确认覆盖 → 跳过
  │     c. 请求哈希与上次一致、且 MYOB 内容已等于我们要写的 → 记「已是最新」，不写
  │     d. 落库（Pending，含快照 BeforeJson）→ PUT → 再 GET 校验合计
  │     e. 更新记录（Pushed / Failed / Mismatch）
  ▼
返回逐员工结果；预览行状态刷新为「已推送 / 需重新推送 / 已发薪跳过 / 失败」
  ▼
财务在 MYOB 跑 Process Payroll（我们不碰）
```

## 3. 第 0 步：实测（写代码之前，必做）

以下几点官方文档没有明确写，凭记忆不可靠，**每一条都要用真实请求确认**，结果写回本节：

| # | 要确认的 | 为什么重要 | 怎么测 |
|---|---|---|---|
| 1 | PUT 是**整体覆盖**该员工该日期区间，还是按 Line 合并？ | 决定重推是否安全、撤销能否恢复 | 先 PUT A（类别 X，2 天），再 PUT B（类别 Y，1 天），GET 看结果里有没有 X |
| 2 | Lines 里传空 `Entries`，或整个 `Lines: []`，能否**清空**？ | 撤销「原来没有」的唯一办法 | PUT 清空后 GET |
| 3 | `Entries[].Date` 的接受格式（`yyyy-MM-dd` 还是带时间）、`Hours` 精度（几位小数） | 请求体能否被接受；分钟 → 小时取整误差 | 分别发 `2026-10-23`、`2026-10-23T00:00:00`；Hours 发 7.5、7.33、7.333 |
| 4 | 请求体 `StartDate` / `EndDate` 与查询串的关系；区间外的 Entries 会怎样 | 防止写出周期之外 | 发区间外的一天，看 400 还是被忽略 |
| 5 | 对含 `Processed=true` 的区间 PUT 返回什么（状态码 / 错误体）；是否会改动已处理那几天 | 我们的「不覆盖」依赖它，不能只靠自己的前置检查 | 需要一个已发薪的历史周期（只对 1 个测试员工，且**只做会被拒绝的那种 PUT**，见下） |
| 6 | 同一员工同周期不同 Line 的 `Notes` / `Job` 是否必填 | 请求体最小集 | 省略后看返回 |
| 7 | 员工没有被挂上该工资类别时 PUT 的返回 | 解释「类别未挂到员工卡」的错误 | 用一个没挂该类别的测试员工 |
| 8 | 单次 PUT 耗时、限流（8 次 / 秒）、5xx 重试后是否幂等 | 决定每块人数与重试策略 | 记录耗时 |
| 9 | 员工卡没勾「使用 Timesheet」时，推送成功但 Process Payroll 里 Hours 是否仍为 0 | 上线前置检查清单 | 在 MYOB 里实际点开 Process Payroll 看 |

产出：本节表格补上「实测结果」列，并据此定稿第 4、6 节（覆盖语义、撤销做法）。

### 3.1 生产文件实测：约束与前置（已定：用一个生产公司文件）

写生产数据，所以实测范围压到最小，且**探针代码里硬性限制**，不靠自觉：

1. **只用一个公司文件、一个测试员工卡。** 推荐在该生产文件里新建一张虚构员工卡（如 `ZZ TEST Probe`）：不挂入任何 Pay Run、不绑定我方员工；这样即使清空失败，也不会被误发工资。测完在 MYOB 里把它设为停用。若你坚持用真实员工，须是**当期没有任何 Timesheet 记录**的人，并且知道清空失败时要去 MYOB 的 Enter Timesheet 手工清掉。
2. 测试卡要挂 `Base Hourly`、`SATURDAY`、`SUNDAY`、`Personal Leave Pay`、`Holiday Leave Pay`（用来测第 1~4、6、8 条）；**第 7 条（没挂类别时 PUT 返回什么）**用该卡暂时不挂 `Holiday Leave Pay` 的状态各测一次，或略过（不影响主流程，只影响错误文案）。
3. 测试日期：只用**当前尚未发薪、且该卡 GET 为空**的周期；探针先 GET，发现该区间已有任何记录就**直接中止**，一次写入都不发。
4. **第 5 条（对含 `Processed` 的区间 PUT 的返回）在生产不测。** 理由：若 MYOB 实际不拒绝，会改动历史已发薪数据，后果不可逆。正式推送的保护只靠我方预检（先 GET，有 `Processed` 就不发 PUT，见 4.2），该路径不会向含已发薪记录的区间发任何 PUT；因此这一条留作「未验证，不依赖」，写入第 3 节表格并在上线 Runbook 里标明。
5. 该公司文件的 Administrator 已在「连接与映射」里授权，连接状态 Active（已确认）。**已定：Robinson Road，测试卡 `test test`、Employee ID `0086`。**配置用 Employee ID，探针自己在名册里查出 UID；还需要 Robinson Road 的**连接 GUID**（「连接与映射」页可看）。
6. 探针运行时间选财务不在 MYOB 里跑 Pay Run 的时段，并提前告知财务「`test test` 这张卡在做测试」。
7. 测试卡当前状态（2026-10-29 截图）：Profile 已填；「Employee Self-Onboarding」状态为 **Sent**（MYOB 已向卡上的邮箱发过自助填写邀请，可忽略或在 MYOB 里取消）；**Payroll Details 还没确认**，见 3.3。

### 3.2 实测方式：一次性探针（沿用 `payslip-probe` 的做法，测完整体删除）

- 临时接口 `POST api/react/v1/myob/timesheet/write-probe`，仅管理员；**只对环境变量 `Myob__WriteProbeConnectionGuid` 指定的那一个连接、`Myob__WriteProbeEmployeeDisplayId`（MYOB 的 Employee ID）指定的那一张员工卡生效**（任一未配置或不符直接 404）；员工卡还要在名册里恰好匹配一张，并且必须是启用中、姓和名都是 `test` 的专用测试卡，否则拒绝（`MYOB_WRITE_PROBE_TEST_CARD_NOT_ALLOWED`）。所以被误调或配错，也碰不到其他公司文件和真人员工。探针期间该连接是**生产文件**，所以这两个变量只在实测窗口内配置，测完立刻删除。
- 按第 3 节 9 条各发一组固定的请求，响应体只在服务端消化，只回传：状态码、字段路径、Hours 与日期的取值种类 / 小数位、是否被拒绝、耗时。**不回显响应体。**
- 写请求只发在测试文件的测试员工上，且每一组测完就清空（或恢复），最后再 GET 确认测试员工回到初始状态。
- 探针代码单独一个 PR（标题注明「临时探针，实测后删除」），部署到主 API；`WriteProbeConnectionGuid` 只配测试文件。结果记入本节，探针在第 1 批后端实现提交里一并移除。
- 与总闸 `TimesheetPushEnabled` 无关：探针不走推送服务，也不会让正式推送接口开放。
- 实际执行顺序（代码里的步骤键）：`precheck`（整个 21 天区间必须为空，否则中止、一次不写）→ `put-date-format`（日期写法与最小请求体，被拒则回退带时间的写法）→ `put-hours-scale`（7.333 小时）→ `put-other-category`（覆盖还是合并）→ `put-out-of-range`（越界一天）→ `clear-empty-lines` / `clear-empty-entries` / `clear-zero-hours`（逐个降级，找到有效的清空办法就停）→ 可选 `put-unassigned-category` → 无论成败都执行清理并 `final-check`。
- 探针没有覆盖第 3 节的第 5 条（生产不测）和第 8 条里的限流 / 重试部分；耗时在每步 `ElapsedMs` 里。

### 3.3 运行步骤（探针合并并部署主 API 之后）

1. 在 MYOB 里补全 `test test` 卡的 Payroll Details：时薪制；工资类别挂 `Base Hourly`、`SATURDAY…`、`SUNDAY…`（以及可选的 `Personal Leave Pay`、`Holiday Leave Pay`）；不要放进任何 Pay Run。若 MYOB 有「使用 Timesheet」之类的开关，打开它（探针也会顺带验证第 9 条是否需要）。
2. 在「连接与映射」里确认 Robinson Road 的工资类别映射已保存（平日 / 周六至少两项）；没保存也可以在请求里直接给类别 UID。
3. 服务器 `.env` 临时加：`Myob__WriteProbeConnectionGuid=<Robinson Road 的连接 GUID>`、`Myob__WriteProbeEmployeeDisplayId=0086`，重启主 API。
4. 财务不在 MYOB 跑 Pay Run 的时段，管理员登录后调用 `POST /api/react/v1/myob/timesheet/write-probe`，Body：`{"connectionGuid":"…","periodStart":"<今天或之后的某个周一，yyyy-MM-dd>"}`。
5. 把返回结果（只含状态码 / 错误类型名 / 该测试卡自己的日期与小时）记入第 3 节表格；确认 `verdicts.finalClean=true`。若为 false，按提示在 MYOB 的 Enter Timesheet 里手工清掉 `test test` 卡的记录。
6. 立刻从 `.env` 删掉两个变量并重启；探针代码在第 1 批实现提交里移除。

## 4. 后端

### 4.1 MYOB 客户端

`IMyobApiClient` 新增（接口默认实现抛 `NotSupportedException`，既有测试替身不用改，沿用 `GetPayrollAdvicesAsync` 的做法）：

- `GetTimesheetAsync(businessId, token, employeeUid, start, end, ct)` → `MyobTimesheetSnapshot`：只保留 `Lines[] { PayrollCategory.UID, Entries[] { Date, Hours, Processed } }`，**不保留** Job / Notes 以外的敏感内容；`GetTimesheetStatusAsync` 内部改为复用它。
- `PutTimesheetAsync(businessId, token, employeeUid, start, end, lines, ct)`：`PUT /{cf_uri}/Payroll/Timesheet/{uid}?StartDate=&EndDate=`，请求体按第 3 节实测定稿。走现有 `SendAsync`（共用 130ms 限流闸）。
- 5xx 与超时**重试 1 次**（PUT 整体覆盖是幂等的；若实测结果是「合并」，则不重试，改为失败后 GET 再判断）。4xx 不重试。
- 错误只带状态码，**不回显 MYOB 响应体**（沿用 `MyobApiException` 约束）。
- 所有写路径只此一处，并且在客户端内再校验一次总闸（双保险，见 4.6）。

### 4.2 推送服务 `MyobTimesheetPushService`

```
PushAsync(connectionGuid, periodStart, userGuids, batchGuid?, overwriteExisting, acknowledgedHintCodes, actor, ct)
UndoBatchAsync(batchGuid, actor, ct)
GetStatesAsync(connectionGuid, periodStart)     // 供预览行显示「已推送 / 需重新推送」
ListBatchesAsync(connectionGuid, periodStart)
```

**与预览共用同一段规划代码。** 现在规划逻辑在 `MyobTimesheetPreviewService.BuildRow` 里，只把结果摊成 DTO 网格。实现时抽出一个内部的 `MyobTimesheetPlanBuilder`（输入公司 / 周期 / 员工，输出每员工的 `MyobCompanyPlan` + 拦截项 + 提示项），预览和推送都调用它，保证「预览看到什么就推什么」，且推送**不信任客户端传来的任何工时数字**，客户端只传「公司、周期、要推的员工」。（改动 `BuildRow` 属于编辑现有符号，实现前按 CLAUDE.md 要求先跑 `impact` 并汇报影响面。）

**服务端重新校验（每块都做）**：

1. 总闸开 + 该公司文件在灰度白名单 + 有推送权限；
2. 账号对涉及分店有管理权限（与预览同一个 `GetScopeAsync`）；
3. 重新算拦截项，只要有一项就拒绝该员工（`BLOCKED`，列原因），不拖累同块其他员工；
4. 周期起始日合法、周期已结束（不推未结束的周期）；
5. 提示项（生日换档、低于最低时薪等）：请求里的 `acknowledgedHintCodes` 必须覆盖该员工命中的全部提示，否则拒绝（对应方案「需勾选已知晓」）；
6. 并发：同一 `(公司, 周期)` 同时只允许一个运行中的批次（见 4.4 的 Running 状态），第二个请求返回 `PUSH_IN_PROGRESS`。

**逐员工动作**（顺序执行，不并发，天然满足 MYOB 限流）：

| 步骤 | 说明 |
|---|---|
| 1 GET 现状 | 取快照 `Before`（只存 类别 UID / 日期 / 小时 / Processed，不含金额） |
| 2 判定 | `Processed` 有 → `SKIPPED_PROCESSED`；有未发薪记录且 `overwriteExisting=false` → `SKIPPED_HAS_ENTRIES`；MYOB 内容已等于目标且请求哈希与最近一次成功记录一致 → `UP_TO_DATE`；否则继续 |
| 3 落库 | 写一条 `Pending`（含 `BeforeJson`、`RequestHash`、`PushedMinutes`），**写不进去就不推** |
| 4 PUT | 请求体 = 规划行按 `(类别, 日期)` 合并，分钟 → 小时按实测精度取整 |
| 5 校验 | 再 GET，对比：每个 `(类别, 日期)` 小时数一致（容差 0.005）、合计 = 规划合计。一致 → `PUSHED`；不一致 → `MISMATCH`（记下差异，提示人工核对，**不自动再写**） |
| 6 失败 | 网络 / 5xx / 4xx → `FAILED`（只记状态码与错误码），可手动重试；`401/invalid_grant` → 连接标 `NeedsReauth`（沿用 token provider），本块剩余员工全部标 `FAILED` 并停止 |

**取整**：规划是分钟，MYOB 是小时。按 `(类别, 日期)` 汇总分钟后再换算，保留实测允许的小数位；若 MYOB 只收 2 位小数，则合计可能与分钟精确值差 ≤ 0.005 h × 条数，校验容差按这个算，并在结果里提示「合计差 x h」，不当作错误。

### 4.3 请求哈希与「需重新推送」

`RequestHash = SHA256(公司 | 员工卡 UID | 周期起止 | 按 类别, 日期 排序后的 (类别 UID, 日期, 分钟))`。

预览行新增状态：

| 状态 | 条件 |
|---|---|
| 未推送 | 没有成功记录 |
| 已推送 | 最近一次成功记录的 `RequestHash` = 现在规划的哈希，且周期仍是锁定状态 |
| **需重新推送** | 有成功记录，但哈希变了（工时 / 时薪拆分 / 映射改过），或周期已被解锁 |
| 已发薪跳过 | 最近一次是 `SKIPPED_PROCESSED` |
| 失败 / 不一致 | 最近一次 `FAILED` / `MISMATCH` |

**周期解锁不会自动改 MYOB**：只把这些员工标「需重新推送」，并在预览页显示提示「MYOB 里还留着旧工时」。

### 4.4 数据：迁移 `20261029.005-myob-timesheet-push`

表 `MyobTimesheetPush`（总方案第 4 节已定名，这里补全；不进 `SqlSugarContext` 自动建表，由迁移创建，幂等、`Verify` 与 `Apply` 分段，沿用 `MyobPayslipSchema` 的写法与测试）：

| 列 | 类型 | 说明 |
|---|---|---|
| `PushGuid` | PK | |
| `BatchGuid` | 索引 | 一次「点推送」的所有记录同一个批次（分块也共用） |
| `ConnectionGuid` / `PeriodStart` / `Weeks` | | |
| `UserGuid` / `MyobEmployeeUid` | | 员工卡 UID 是 MYOB 的标识，不是姓名 |
| `Status` | `Running / Pushed / UpToDate / SkippedProcessed / SkippedHasEntries / Blocked / Failed / Mismatch / Undone / UndoConflict` | |
| `RequestHash` | | 见 4.3 |
| `PushedMinutes` | int | 合计分钟（用于对账展示） |
| `BeforeJson` / `AfterJson` | nvarchar(max) | 推送前快照 / 我们写入的内容；**只含类别 UID、日期、小时**，不含姓名和金额 |
| `ErrorCode` / `ErrorStatus` | | 只存错误码与 HTTP 状态码，不存响应体 |
| `ActorUserGuid` / `StartedAt` / `FinishedAt` | | |
| `UndoneAt` / `UndoneBy` / `UndoNote` | | |

索引：`(ConnectionGuid, PeriodStart, UserGuid, StartedAt DESC)` 查最近一次；`(BatchGuid)`；过滤唯一索引保证「同 `(ConnectionGuid, PeriodStart)` 最多一个 `Running` 批次」。`Running` 超过 10 分钟视为陈旧，下次推送时自动标 `Failed` 后放行。

同一迁移幂等写入权限码 `Myob.Timesheet.Push`，**不授予任何角色**（与 `20261029.003` 同一做法：`WHERE NOT EXISTS`，不 `UPDATE` / `DELETE` / `DROP`）。

### 4.5 撤销（按批次）

`POST timesheet/batches/{batchGuid}/undo`，只对 `Pushed` / `UpToDate` 之外的、**本批次真正写过的员工**执行：

1. GET 当前 MYOB 内容；
2. 若有 `Processed` → `UNDO_BLOCKED_PROCESSED`，**不改**，提示财务在 MYOB 里反向处理发薪；
3. 若当前内容 ≠ 本批次的 `AfterJson`（说明有人在 MYOB 里手工改过）→ `UndoConflict`，**不覆盖**，提示人工处理；
4. 否则把 `BeforeJson` PUT 回去：
   - 原来有记录 → PUT 原内容；
   - 原来没有记录 → 按第 3 节实测的清空办法；若实测发现 API 无法清空，则撤销降级为「在 MYOB 的 Enter Timesheet 里手工清理」，界面只给出要清理的员工 / 日期清单，不假装能自动撤销；
5. 再 GET 校验，记 `Undone`。

撤销同样需要推送权限，同样受总闸控制；只能撤销**本批次**，且该员工在该周期**没有更新的批次**时才允许（防止撤销覆盖掉后来的重推）。

### 4.6 控制器与路由

均在 `api/react/v1/myob/timesheet` 下，统一 `ApiResponse<T>`，权限 `Myob.Timesheet.Push`（预览仍用 `Attendance.Payroll.Edit`，见第 10 节问题 2）：

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `push` | Body：`connectionGuid, periodStart, userGuids[≤10], batchGuid?, overwriteExisting, acknowledgedHintCodes[]`；返回 `batchGuid` 与逐员工结果 |
| GET | `batches?connectionGuid=&periodStart=` | 该周期的批次列表与每批汇总（成功 / 跳过 / 失败 / 已撤销数） |
| GET | `batches/{batchGuid}` | 批次明细（逐员工状态、错误码，不含金额） |
| POST | `batches/{batchGuid}/undo` | 撤销，返回逐员工结果 |

预览接口返回的行里新增 `push: { state, lastBatchGuid, lastPushedAt, needsRepush }`；`PushEnabled` 继续由总闸 + 白名单 + 权限共同决定。

**总闸与灰度**：`MyobOptions.TimesheetPushEnabled`（已有）+ 新增 `TimesheetPushConnectionGuids`（逗号分隔，空 = 不限，**默认空且总闸关**）。检查放三处：控制器入口、推送服务、`MyobApiClient.PutTimesheetAsync` 内（客户端层拿 `IOptions` 再看一眼，防止将来别的代码路径绕过服务层）。

### 4.7 日志与隐私

- 日志只允许：接口、连接 GUID、周期、批次 GUID、人数、状态码、耗时、操作者 GUID；**不得出现**姓名、MYOB 响应体、令牌。
- 错误消息沿用 `MyobApiException`「只含状态码」。
- 响应头 `Cache-Control: no-store`。
- 快照里只有工时，没有金额；但仍不在日志里打印。

## 5. 前端（`pages/PosAdmin/Myob/PushPreviewTab.tsx` 等）

- 「推送到 MYOB」按钮：`pushEnabled && 权限 && readyCount>0` 才可点；不可点时的原因提示沿用已有的 tooltip 与说明文案。
- **确认弹窗**：公司、周期、将推送人数 / 合计小时；若有 `HasEntries` 的员工，单独一行红字「将覆盖 N 人在 MYOB 里已有的未发薪工时」+ 必须勾选；列出命中提示项的员工并要求勾选「已知晓」；底部声明「推送只写 Timesheet，不会发薪；发薪请在 MYOB 里 Process Payroll」。
- **进度**：前端按每块 ≤10 人循环调用 `push`，同一个 `batchGuid`；显示 `已处理 x / n`，中途失败可「继续」或「停止」；刷新页面后可按批次接着看。
- **结果**：每行显示状态标签（已推送 / 已是最新 / 已发薪跳过 / 有记录未覆盖 / 失败 / 不一致），失败行有「重试」；顶部汇总条。
- **推送记录抽屉**：按批次列出时间、操作人、成功 / 失败数；每批有「撤销」按钮，撤销前二次确认并说明「已发薪的员工不会被改动」。
- 预览行新增列「MYOB 状态」：未推送 / 已推送 / 需重新推送，并保留现有的 `Clear / HasEntries / Processed` 判断。
- 文案进 `myobMessages.zh.json / en.json`；逻辑放 `myobTimesheetLogic.ts`（分块、状态归类、确认条件），配 `.test.ts`；英文用词与 MYOB 一致（Timesheet、Pay Run、Processed）。
- 手机端：本期不做。

## 6. 权限

| 权限码 | 谁 | 用途 |
|---|---|---|
| `Myob.Timesheet.Push` | 财务 / 管理员（由管理员在生产手动授权） | 推送、撤销 |
| `Attendance.Payroll.Edit`（已有） | 店长 / 财务 | 只看预览 |

不沿用 `Attendance.Payroll.Edit` 做推送：它会被授给店长，等于让店长能直接写 MYOB。新码落地清单（新权限码约束）：`Permissions.cs` 常量、`PermissionSurfaceCatalog`、迁移入库、移动端 `accessPermissions.json` 文案（文件需与种子一致，即使移动端没有入口）、Web 访问判断。

## 7. 验证办法

1. **纯函数单测**：请求哈希稳定性（顺序无关、改一分钟就变）；分钟 → 小时取整与校验容差；状态归类（已推送 / 需重新推送）。
2. **服务测试**（假 `IMyobApiClient`）：
   - 总闸关 / 不在白名单 / 无权限 → 拒绝，且不产生任何 PUT 调用；
   - 有拦截项（未锁定、未绑定、类别未映射）→ 该员工被拒、同块其他员工照常；
   - `Processed` → 跳过；`HasEntries` 未确认覆盖 → 跳过，确认后写；
   - 哈希一致且 MYOB 已是目标内容 → `UpToDate`、不写；
   - PUT 成功但回读不一致 → `Mismatch`，不自动重写；
   - 落库失败 → 不推；
   - 5xx 重试一次；401 / `invalid_grant` → 连接 `NeedsReauth` 且本块停止；
   - 并发两个批次 → 第二个 `PUSH_IN_PROGRESS`；`Running` 超时自动放行；
   - 撤销：内容被人手改 → `UndoConflict`；有 `Processed` → 拒绝；有更新批次 → 拒绝；原来无记录 → 走清空；
   - 日志与异常消息不含姓名 / 响应体。
3. **请求体形状测试**：用假 HTTP 处理器断言 URL、查询串、JSON 结构、类别 UID、日期格式（以第 3 节实测为准）。
4. **控制器鉴权测试**：无权限 403；店长对非管辖分店员工 403 / 拦截。
5. **迁移测试**：`Verify` / `Apply` 幂等、错误号自成一段、不含 `DROP / UPDATE / DELETE`、不授予角色（对齐 `SchemaMigrationCoordinatorTests` 里 `MyobPayslipSchema` 的写法）。
6. **前端**：`myobTimesheetLogic.test.ts`；用现有「免登录目测 Web 组件」办法（esbuild + 造数服务）目测确认弹窗、进度、结果、记录抽屉、各种状态。
7. **真实核对**：第 3 节实测；上线后灰度（第 8 节）；每个灰度阶段都由你在 MYOB 里打开 Process Payroll 对一遍 Hours。
8. 门禁：后端相关测试（对照本机既有失败基线）、`tsc`、web lint、web 首屏包体预算。

## 8. 实施与发布顺序

| 批 | 内容 | 发布 |
|---|---|---|
| 0 | 你指定生产公司文件与测试员工卡（3.1）→ 临时探针 PR（3.2，只认该连接 + 该员工卡）→ 跑完把结果写回本方案，定稿 4.1 / 4.2 / 4.5 | 探针部署主 API（无迁移）；**无正式推送代码** |
| 1 | 后端：客户端 GET / PUT、`MyobTimesheetPlanBuilder` 抽取、推送服务、撤销、迁移 `20261029.005`、权限码、测试 | 迁移 → 主 API；**总闸关** |
| 2 | Web：确认弹窗、分块进度、结果、状态列、记录抽屉、撤销 | Web |
| 3 | 灰度：管理员授权 `Myob.Timesheet.Push` 给 1 个财务账号 → 白名单只放 1 个公司文件 → **1 个员工、1 个未发薪周期**，在 MYOB 核对 → 同公司全员 → 再放其他公司文件 | 开总闸（服务器 `.env`），不需要发版 |
| 4 | 撤销演练：在灰度员工上推 → 撤销 → 确认 MYOB 恢复 | — |

PR 标题不需要「需重建原生包」标注（无移动端改动）。

## 9. 上线前置检查（MYOB 端，财务确认）

- 公司文件的工资设置开启「用 Timesheet 记录工资」，员工卡上也启用（第 3 节第 9 条实测确认到底要哪一层）。
- MYOB 的周起始日与我方周期起始日对齐。
- 员工卡的工资明细里已挂上要推送的类别，包括 Personal Leave Pay、Holiday Leave Pay；临时工的周末类别另外挂。
- 该公司文件的类别映射（预览页「类别映射」卡）5 项必填已完成。
- 授权人仍是该文件 Administrator，连接状态为 Active。

## 10. 需要你拍板的问题

已全部按推荐项确认（2026-10-29）：

1. **实测环境：B（用户 2026-10-29 改选）**，用一个生产公司文件，只对 1 张测试员工卡、未发薪且为空的周期；第 3 节第 5 条在生产不测（约束与理由见第 3.1 节）。
2. **权限：** 新增 `Myob.Timesheet.Push`，默认不授予任何角色；预览仍用 `Attendance.Payroll.Edit`。
3. **MYOB 里已有未发薪记录时：** 允许覆盖，但必须在确认弹窗里单独勾选。（`Processed` 的记录无论如何不覆盖。）
4. **推送粒度：** 按公司 × 周期，预览里可挑人推，也可全选。

## 11. 范围外

写员工卡 / 时薪 / 假期余额；创建员工；Process Payroll / 发薪 / STP；现金部分；年假折现（仍由财务在 MYOB 手工处理）；定时自动推送；撤销已发薪的数据；拉取工资单与对账（见 `2026-10-29-myob-payslip-history.md` 与总方案第 9 节）。
