# MYOB Timesheet 推送（PUT）· 方案

> 状态：**方案已确认（2026-10-29）；第 3 节实测已完成（2026-10-30，结论见 3.4）；批 1 后端已实现（本 PR）**。第 10 节：实测环境**改为用一个生产公司文件（B）**（用户 2026-10-29 改选，不再新建测试文件）；其余按推荐项：新增权限码 `Myob.Timesheet.Push`；MYOB 已有未发薪记录时允许覆盖但须单独勾选；可挑人推。第 3 节的「实测」未做之前，任何写 MYOB 的正式推送代码都不合入、不开总闸。
> 进度：已选 Robinson Road；测试卡 `test test` 已在该生产文件里建好，连接已授权。探针 #1080 / #1081 已合并并于 2026-10-30 上线实测一次，`finalClean=true`，测试卡已清空；实测结论见 3.4。批 1（后端推送 / 撤销、迁移 `20261030.001-myob-timesheet-push`、权限码 `Myob.Timesheet.Push`）随本 PR 实现，**探针代码同 PR 删除**；总闸仍默认关，Web（批 2）未做。
> 范围：把「工资周期锁定后的转账工时」写进发薪公司的 MYOB Timesheet，并能**按批次撤销**。不跑 Pay Run、不报 STP、不写员工卡、不碰现金部分。
> 与总方案关系：落实 `2026-10-27-myob-payroll-integration.md` 第 8 节「阶段 2：推送 Timesheet」。已有的预览、类别映射、员工绑定、发薪公司规则全部沿用，本方案只新增「写」与「撤销」。

## 0. 结论速览

| 问题 | 结论 |
|---|---|
| 要做什么 | 在预览页「推送到 MYOB」按钮背后，实现 `PUT /{cf_uri}/Payroll/Timesheet/{EmployeeUID}`，按员工逐个写；每次写入前后各读一次 MYOB 校验。 |
| 能不能撤销 | **发薪前能，发薪后不能**（Processed 的记录 API 不能改）。撤销 = 用推送前留下的快照再 PUT 回去；原来没有记录就 PUT 空 `Lines`（**实测可清空；DELETE 返回 405 不可用**）。 |
| 最大风险 | PUT 的语义（覆盖 / 合并 / 能否清空）官方文档没写透，我无法保证。所以**先做实测，再写代码**。 |
| 安全阀 | ① 总闸 `Myob__TimesheetPushEnabled` 默认关（已存在）；② 新增灰度白名单（按公司文件）；③ 新权限码 `Myob.Timesheet.Push`，默认不授予任何角色；④ 每次推送重新服务端校验，不信任预览结果。 |
| 要建表吗 | 要：`MyobTimesheetPush`，迁移 **`20261030.001-myob-timesheet-push`**（`20261029.005` 已被工资单导出占用）；同一迁移幂等写入权限码 `Myob.Timesheet.Push`。 |
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

### 3.0 官方文档核对（2026-10-30，读 [Timesheet 文档页](https://developer.myob.com/api/myob-business-api/v2/payroll/timesheet/) 原文）

| 文档写了 | 文档**没写** |
|---|---|
| 支持 `GET \| PUT \| POST \| DELETE`；`Employee.UID` 在 PUT 创建时必填、`PayrollCategory.UID` 在 PUT 时必填；`Entries[].Processed` 表示「已被发薪使用」；字段 `StartDate` / `EndDate` / `Lines[]`（含 `Notes` ≤255、`Job` 等）/ `Entries[].UID / Date / Hours / Processed`；分页默认 400、最多 1000 | PUT 是覆盖还是合并；**DELETE 删什么**（整个区间 / 一行 / 一条）、已发薪的记录能不能删；Processed 记录能不能改；Hours 的精度（类型写 Integer，示例却是 `7.6`）；日期格式（文字写 `YYYY-MM-DD HH:MM:SS`，示例是 `yyyy-MM-ddT00:00:00`）；区间外的 Entries 怎么处理 |

另据 MYOB 错误码页（较旧）：条目日期落在 StartDate~EndDate 之外 = 220；StartDate 晚于 EndDate = 221；类别没挂到员工 = 34001；公司文件的 Timesheet 设置未开 = 34003。**实测：区间外日期返回 400 `DateOutOfRange`，ErrorCode 是 221（不是 220）**，见 3.4。

**更正**：此前方案与对话里说「MYOB 没有 DELETE Timesheet」是错的——文档列了 DELETE。**但实测带 StartDate / EndDate 的 DELETE 返回 405（Method Not Allowed），不能用**；清空靠 PUT 空 `Lines`（见 3.4）。

以下几点官方文档没有明确写，凭记忆不可靠，**每一条都要用真实请求确认**，结果写回本节：

| # | 要确认的 | 为什么重要 | 怎么测 |
|---|---|---|---|
| 1 | PUT 是**整体覆盖**该员工该日期区间，还是按 Line 合并？ | 决定重推是否安全、撤销能否恢复 | 先 PUT A（类别 X，2 天），再 PUT B（类别 Y，1 天），GET 看结果里有没有 X |
| 2 | **DELETE**（带 StartDate / EndDate）、`Lines: []`、每行空 `Entries`、全部改 0 小时，四种办法各自能否**清空**？ | 撤销「原来没有」的办法；选最干净的 | 每种办法各自在刚写好的记录上测一遍，再读回 |
| 3 | `Entries[].Date` 的接受格式（`yyyy-MM-dd` / `yyyy-MM-ddT00:00:00` / `yyyy-MM-dd 00:00:00`）、`Hours` 精度（几位小数） | 请求体能否被接受；分钟 → 小时取整误差 | 三种写法依次试；Hours 发 7.5、7.333 看读回 |
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
5. 该公司文件的 Administrator 已在「连接与映射」里授权，连接状态 Active（已确认）。**已定：Robinson Road，测试卡 `test test`、Employee ID `0086`。**配置用 Employee ID，探针自己在名册里查出 UID；还需要 Robinson Road 的 **BusinessId**（MYOB 公司文件 ID，一个 GUID；已提供，不写进仓库，只放服务器 `.env`）。
6. 探针运行时间选财务不在 MYOB 里跑 Pay Run 的时段，并提前告知财务「`test test` 这张卡在做测试」。
7. 测试卡当前状态（2026-10-29 截图）：Profile 已填；「Employee Self-Onboarding」状态为 **Sent**（MYOB 已向卡上的邮箱发过自助填写邀请，可忽略或在 MYOB 里取消）；**Payroll Details 还没确认**，见 3.3。

### 3.2 实测方式：一次性探针（沿用 `payslip-probe` 的做法，测完整体删除）

- 临时接口 `POST api/react/v1/myob/timesheet/write-probe`，仅管理员；**只对环境变量 `Myob__WriteProbeBusinessId` 指定的那一个公司文件、`Myob__WriteProbeEmployeeDisplayId`（MYOB 的 Employee ID）指定的那一张员工卡生效**（任一未配置或不符直接 404）；员工卡还要在名册里恰好匹配一张，并且必须是启用中、姓和名都是 `test` 的专用测试卡，否则拒绝（`MYOB_WRITE_PROBE_TEST_CARD_NOT_ALLOWED`）。所以被误调或配错，也碰不到其他公司文件和真人员工。探针期间该连接是**生产文件**，所以这两个变量只在实测窗口内配置，测完立刻删除。
- 按第 3 节 9 条各发一组固定的请求，响应体只在服务端消化，只回传：状态码、字段路径、Hours 与日期的取值种类 / 小数位、是否被拒绝、耗时。**不回显响应体。**
- 写请求只发在测试文件的测试员工上，且每一组测完就清空（或恢复），最后再 GET 确认测试员工回到初始状态。
- 探针代码单独一个 PR（标题注明「临时探针，实测后删除」），部署到主 API；`WriteProbeBusinessId` 只配 Robinson Road 这一个公司文件。结果记入本节，探针在第 1 批后端实现提交里一并移除。
- 与总闸 `TimesheetPushEnabled` 无关：探针不走推送服务，也不会让正式推送接口开放。
- 实际执行顺序（代码里的步骤键）：`precheck`（整个 21 天区间必须为空，否则中止、一次不写）→ `put-date-format`（日期写法与最小请求体，被拒则回退带时间的写法）→ `put-hours-scale`（7.333 小时）→ `put-other-category`（覆盖还是合并）→ `put-out-of-range`（越界一天）→ `clear-delete` / `clear-empty-lines` / `clear-empty-entries` / `clear-zero-hours`（**四种清空办法各自在刚写好的记录上独立测一遍**，不是第一个有效就停）→ 可选 `put-unassigned-category` → 无论成败都执行清理并 `final-check`。
- 探针没有覆盖第 3 节的第 5 条（生产不测）和第 8 条里的限流 / 重试部分；耗时在每步 `ElapsedMs` 里。

### 3.3 运行步骤（探针合并并部署主 API 之后）

1. 在 MYOB 里补全 `test test` 卡的 Payroll Details：时薪制；工资类别挂 `Base Hourly`、`SATURDAY…`、`SUNDAY…`（以及可选的 `Personal Leave Pay`、`Holiday Leave Pay`）；不要放进任何 Pay Run。若 MYOB 有「使用 Timesheet」之类的开关，打开它（探针也会顺带验证第 9 条是否需要）。
2. 在「连接与映射」里确认 Robinson Road 的工资类别映射已保存（平日 / 周六至少两项）；没保存也可以在请求里直接给类别 UID。
3. ~~服务器 `.env` 临时加 `Myob__WriteProbeBusinessId` / `Myob__WriteProbeEmployeeDisplayId`~~ **（实际执行时更正）**：服务器 compose 没有 `env_file`，也没映射这两个变量，只改 `.env` 传不进容器。实际做法是 `.env` 加 `MYOB_WRITE_PROBE_*` 两行（先备份）+ 服务器端临时 `docker-compose.override.yml` 做映射，重建容器；测完两处都删并再重建。另外 **`test test` 卡在 MYOB 里没填 Employee ID，读到的是 `*None`，不是 `0086`**，配置用 `*None`（探针要求匹配恰好一张、姓名 test test、启用中，校验照旧生效）。
4. 财务不在 MYOB 跑 Pay Run 的时段，管理员登录后调用 `POST /api/react/v1/myob/timesheet/write-probe`，Body：`{"businessId":"<Robinson Road 的 BusinessId>","periodStart":"<今天或之后的某个周一，yyyy-MM-dd>"}`。
5. 把返回结果（只含状态码 / 错误类型名 / 该测试卡自己的日期与小时）记入第 3 节表格；确认 `verdicts.finalClean=true`。若为 false，按提示在 MYOB 的 Enter Timesheet 里手工清掉 `test test` 卡的记录。
6. 立刻从 `.env` 删掉两个变量并重启；探针代码在第 1 批实现提交里移除。

### 3.4 实测结果（2026-10-30 08:3x 悉尼，Robinson Road，`test test` 卡，周期 2026-11-09 ~ 11-29）

一次运行，所有写入均 200，结束时 `finalClean=true`、残留 0，测试卡已清空。

| # | 结论 |
|---|---|
| 1 | **PUT 是整体覆盖**该员工该日期区间内的全部行：先写平日两天，再只发周六一行，读回只剩周六，平日行消失。所以重推 / 撤销必须按「整个区间的完整内容」发。 |
| 2 | **DELETE（带 StartDate / EndDate）返回 405，不可用。** 三种清空办法都有效：`Lines` 空数组（记录消失）、每行 `Entries` 空数组（记录消失）、全部工时改 0（记录还在、工时为 0）。撤销采用 `Lines: []`。 |
| 3 | 日期 `yyyy-MM-dd` 被接受，读回为 `2026-11-09T00:00:00`；7.333 小时读回 `7.333000`（**保留 3 位小数**）。 |
| 4 | 区间外的一天：**HTTP 400，`DateOutOfRange`，ErrorCode 221**，且不会存下。 |
| 5 | 含 Processed 区间的 PUT：**生产不测、不依赖**；正式推送在发 PUT 前先 GET，有 Processed 就不发。 |
| 6 | 最小请求体（`Employee.UID`、`StartDate` / `EndDate`、`Lines[].PayrollCategory.UID`、`Entries[].Date / Hours`，不带 Job / Notes / Processed）被接受。 |
| 7 | 给卡没有的类别写入：**未测**（没提供 UnassignedCategoryUid）；错误文案以首次灰度为准。 |
| 8 | 单步耗时：PUT 约 0.7–3.4 秒，GET 约 0.5–1.9 秒；未触发限流。 |
| 9 | 测试卡上类别（平日 / 周六）挂好后所有 PUT 都 200；「使用 Timesheet」开关对 Process Payroll 是否必需仍须财务在灰度时核对（第 9 节）。 |

## 4. 后端

### 4.1 MYOB 客户端

`IMyobApiClient` 新增（接口默认实现抛 `NotSupportedException`，既有测试替身不用改，沿用 `GetPayrollAdvicesAsync` 的做法）：

- `GetTimesheetLinesAsync(businessId, token, employeeUid, start, end, ct)` → `List<MyobTimesheetLineRecord>`（已随探针 PR 加入）：只保留 `Lines[] { PayrollCategory.UID, Entries[] { Date, Hours, Processed } }`，不保留 Job / Notes；404 或空体 = 没有记录。
- `PutTimesheetAsync(businessId, token, employeeUid, start, end, lines, ct)`：`PUT /{cf_uri}/Payroll/Timesheet/{uid}?StartDate=&EndDate=`，请求体按第 3 节实测定稿。走现有 `SendAsync`（共用 130ms 限流闸）。
- 5xx 与网络错误**重试 1 次**（实测 PUT 是整体覆盖，幂等），4xx 不重试；重试放在推送服务里（`PutWithRetryAsync`），客户端本身只发一次。
- 错误只带状态码，**不回显 MYOB 响应体**（沿用 `MyobApiException` 约束）。
- 所有写路径只此一处（`DeleteTimesheetAsync` 已随探针删除），并且在客户端内再校验一次总闸（双保险，见 4.6）：总闸关闭时 `PutTimesheetAsync` 不发请求、返回状态码 0 / `PUSH_DISABLED`。客户端拿不到连接 GUID，所以只看总闸，灰度白名单在控制器 / 服务层。

### 4.2 推送服务 `MyobTimesheetPushService`

```
PushAsync(connectionGuid, periodStart, userGuids, batchGuid?, overwriteUserGuids, acknowledgedHintCodes, actor, ct)
UndoBatchAsync(batchGuid, actor, ct)
GetStatesAsync(connectionGuid, periodStart)     // 供预览行显示「已推送 / 需重新推送」
ListBatchesAsync(connectionGuid, periodStart)
```

**与预览共用同一段规划代码。** 实现时没有把 `BuildRow` 整体搬走，而是让预览服务多暴露一个内部入口 `IMyobTimesheetPlanSource.ComputeAsync`（返回预览 DTO + 每个员工在本公司名下的分钟级 `MyobCompanyPlan` + 员工卡绑定 + 类别映射），`PreviewAsync` 变成它的薄包装，推送服务调用同一个入口，保证「预览看到什么就推什么」，且推送**不信任客户端传来的任何工时数字**，客户端只传「公司、周期、要推的员工」。（影响面：`MyobTimesheetPreviewService` 只被 DI 注册、`MyobTimesheetController` 与 `MyobTimesheetTests` 引用，改动为新增入口，公开签名不变；GitNexus 索引未收录该类，按全局约定退回 grep 核对。）

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
| 1 落库持锁 | **先**写一条 `Running`（过滤唯一索引保证同一周期同时只有一个），**写不进去就不推**。先加锁再读，保证下一步读到的就是「推送前快照」，不会被并发的另一次推送改旧 |
| 2 GET 现状 | 取快照 `Before`（只存 类别 UID / 日期 / 小时，不含金额；Job / Notes 不保存，只标记「带了它们」；负工时保留，0 小时丢弃） |
| 3 判定 | `Processed` 有 → `SKIPPED_PROCESSED`；MYOB 内容已等于目标 → `UP_TO_DATE`；有未发薪内容（含 Job / Notes、负工时）且该员工**不在 `overwriteUserGuids` 里** → `SKIPPED_HAS_ENTRIES`（覆盖**按人**确认，预览之后才出现记录的员工不会被静默覆盖）；否则把 `Before` / `After` 写进记录，继续。跳过 / 已是最新也是把这一行收成结局，不另插记录 |
| 4 PUT | 请求体 = 规划行按 `(类别, 日期)` 合并，分钟 → 小时保留 3 位小数。**从这一步起不再响应调用方取消**（用户关页签不能让 MYOB 写了一半而记录卡在 Running） |
| 5 校验 | 再 GET，对比：每个 `(类别, 日期)` 小时数一致（容差 0.0005）。一致 → `PUSHED`；不一致 → `MISMATCH`（记下实际内容，提示人工核对，**不自动再写**） |
| 6 失败 | 4xx（MYOB 明确拒绝）→ `FAILED`（只记状态码与错误码，错误码截到 80 字），可手动重试。**网络 / 5xx 结果不确定**：重试 1 次仍失败则先回读——等于目标按 `PUSHED` 收尾（保留快照，可撤销）；等于推送前才记 `FAILED`；其余或回读失败记 `MISMATCH`，绝不把「可能已写入」的记录记成 `FAILED`（`FAILED` 够不着撤销）。`401/403` → 本块剩余员工全部标 `FAILED` 并停止 |

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

表 `MyobTimesheetPush`（迁移 `20261030.001-myob-timesheet-push`，错误号 54400–54405；不进 `SqlSugarContext` 自动建表，由迁移创建，幂等、`Verify` 与 `Apply` 分段，沿用 `MyobPayslipExportSchema` 的写法与测试，并在本机隔离 SQL Server 上跑过集成测试）：

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

索引：`(ConnectionGuid, PeriodStart, UserGuid, StartedAt DESC)` 查最近一次；`(BatchGuid)`；过滤唯一索引 `UX_MyobTimesheetPush_Running (ConnectionGuid, PeriodStart) WHERE Status = N'Running'` 保证「同 `(ConnectionGuid, PeriodStart)` 同一时刻最多一行 `Running`」——员工逐个处理，每行只在「落库到写完」之间是 Running，所以第二个并发请求的插入会被索引挡下，服务回 `PUSH_IN_PROGRESS`；另有请求入口的预检。`Running` 超过 10 分钟视为陈旧，下次推送时自动标 `Failed`（`MYOB_PUSH_STALE`）后放行。

状态值：`Running / Pushed / UpToDate / SkippedProcessed / SkippedHasEntries / Failed / Mismatch / Undone`；`Blocked`（被拦截项挡住）只在返回里出现、不落库；撤销被拒绝的 `UndoConflict / UndoBlockedProcessed / UndoBlockedNewer` 也只在返回里出现，记录保持 `Pushed` 并写 `UndoNote`，修复原因后可重试。

同一迁移幂等写入权限码 `Myob.Timesheet.Push`，**不授予任何角色**（与 `20261029.003` 同一做法：`WHERE NOT EXISTS`，不 `UPDATE` / `DELETE` / `DROP`）。权限码落地清单已做：`Permissions.cs`、`PermissionSeedData`、`PermissionSurfaceCatalog`（Web 专用）、移动端 `accessPermissions.json` 中英文案；Web 访问判断随批 2。

### 4.5 撤销（按批次）

`POST timesheet/batches/{batchGuid}/undo`，只对 `Pushed` / `UpToDate` 之外的、**本批次真正写过的员工**执行：

1. GET 当前 MYOB 内容；
2. 若有 `Processed` → `UNDO_BLOCKED_PROCESSED`，**不改**，提示财务在 MYOB 里反向处理发薪；
3. 若当前内容 ≠ 本批次的 `AfterJson`（说明有人在 MYOB 里手工改过）→ `UndoConflict`，**不覆盖**，提示人工处理；
4. 否则把 `BeforeJson` PUT 回去：
   - 原来有记录 → PUT 原内容；
   - 原来没有记录 → PUT 空 `Lines`（实测可清空，见 3.4）；
5. 再 GET 校验，记 `Undone`。

撤销同样需要推送权限，同样受总闸控制；只能撤销**本批次**，且该员工在该周期**没有更新的批次**时才允许（防止撤销覆盖掉后来的重推）。

### 4.6 控制器与路由

均在 `api/react/v1/myob/timesheet` 下，统一 `ApiResponse<T>`，权限 `Myob.Timesheet.Push`（预览仍用 `Attendance.Payroll.Edit`，见第 10 节问题 2）：

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `push` | Body：`connectionGuid, periodStart, userGuids[≤10], batchGuid?, overwriteUserGuids[], acknowledgedHintCodes[]`；返回 `batchGuid` 与逐员工结果。`batchGuid` 必须是 GUID，已存在的批次必须是同一公司文件、同一周期 |
| GET | `batches?connectionGuid=&periodStart=` | 该周期的批次列表与每批汇总（成功 / 跳过 / 失败 / 已撤销数） |
| GET | `batches/{batchGuid}` | 批次明细（逐员工状态、错误码，不含金额） |
| POST | `batches/{batchGuid}/undo` | 撤销，返回逐员工结果 |

预览接口返回的行里新增 `push: { state, lastBatchGuid, lastPushedAt, needsRepush }`；`PushEnabled` 继续由总闸 + 白名单 + 权限共同决定。

**总闸与灰度**：`MyobOptions.TimesheetPushEnabled`（已有）+ 新增 `TimesheetPushConnectionGuids`（逗号分隔；**空 = 全部禁止**，`*` = 全部公司文件；默认空且总闸关——只开总闸忘了配白名单也不会写任何公司文件）。检查放三处：推送服务入口、撤销入口、`MyobApiClient.PutTimesheetAsync` 内（客户端层拿 `IOptions` 再看一眼，防止将来别的代码路径绕过服务层；它拿不到连接 GUID，只看总闸）。

### 4.6.1 撤销与批次查询的访问范围

管理员可看 / 撤销所有批次；其他有 `Myob.Timesheet.Push` 的账号只能看 / 撤销**自己推送**的批次（批次是按推送者当时的分店范围算出来的，别人的批次可能含他管不到的分店员工）。撤销时每条记录按**自己的**公司文件 / 周期 / 周数处理；同一批次里同一员工写过多次时**最新的先撤**；后来的 `UpToDate` 没有改动 MYOB，不挡撤销；撤销写成功但没来得及记账时，重试会发现「MYOB 已是推送前的内容」并直接补记。

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
| 1 | 后端：推送服务 + 撤销 + 批次查询、预览入口 `ComputeAsync` 与预览行 `push` 状态、迁移 `20261030.001`、权限码、删除探针、测试（**本 PR**） | 迁移 → 主 API；**总闸关** |
| 2 | Web：确认弹窗、分块进度、结果、状态列、记录抽屉、撤销 | Web |
| 3 | 灰度：管理员授权 `Myob.Timesheet.Push` 给 1 个财务账号 → 白名单只放 1 个公司文件 → **1 个员工、1 个未发薪周期**，在 MYOB 核对 → 同公司全员 → 再放其他公司文件 | 开总闸（服务器 `.env`），不需要发版 |
| 4 | 撤销演练：在灰度员工上推 → 撤销 → 确认 MYOB 恢复 | — |

PR 标题不需要「需重建原生包」标注（无移动端改动）。

## 8.1 已知限制（批 1）

- **快照只保存工资类别 / 日期 / 小时**：现有行里的 Job / Notes 不保存，覆盖后撤销也还原不了；带它们的行会被当作「有内容」，必须勾选覆盖，界面（批 2）要注明这一点。
- **工时全部消失时推不了空**：员工已推送、之后转账计划变成空（全改现金）时，预览显示「需重新推送」，但推送会被拒（没有要推送的工时）；MYOB 里的旧工时请用「撤销」清掉。
- 「周期已结束」的判断按固定 UTC+10 算「今天」，夏令时偏保守；推送还要求涉及分店已锁定，实际风险低。
- 并发撤销没有加锁（只检查有没有 Running 的推送），极低概率；批次列表只取最近 2000 行记录。
- 预览里的「已推送」只比对我方记录的哈希，不反映 MYOB 事后被人改；勾选 `checkMyob` 才会读 MYOB 现状。

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
3. **MYOB 里已有未发薪记录时：** 允许覆盖，但必须在确认弹窗里**按人**单独勾选（`overwriteUserGuids`）。（`Processed` 的记录无论如何不覆盖。）
4. **推送粒度：** 按公司 × 周期，预览里可挑人推，也可全选。

## 11. 范围外

写员工卡 / 时薪 / 假期余额；创建员工；Process Payroll / 发薪 / STP；现金部分；年假折现（仍由财务在 MYOB 手工处理）；定时自动推送；撤销已发薪的数据；拉取工资单与对账（见 `2026-10-29-myob-payslip-history.md` 与总方案第 9 节）。
