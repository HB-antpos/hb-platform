# 整班未打卡（缺勤）生成待审给店长 —— 方案

> 状态：2026-11-25 已拍板（见 §5）并已实现（未合并、未部署）。
>
> 实现要点：核心规则在 `AttendanceReactService.NoShow.cs`；派生对账（打卡 / 补卡 / 手动补录 / 审核列表 14 天）、
> 后台 `AttendanceAutoClockOutWorker`、打开工时统计与提交工时三处生成；请假提交 / 批准、补录带薪假、删排班时取消；
> 确认缺勤后工时格显示「缺勤」（`NoShowConfirmed`），该员工即使零工时也列在工时表上。同批已实现：工时表 / 工资表员工姓名显示「全名（昵称）」。

## 1. 现状与问题

- 现有待审类型只有 `Punch / PunchAdjustment / Overtime / MissingClockOut / MealBreak / Leave / PublicHolidayPay`，**没有「整班一次卡都没打」**。
- 待审都由「打卡事件」触发（打卡 / 补卡后重算班段 → `ReconcileMissingClockOutApprovalAsync` 等）；午夜自动下班 worker 只处理 `HasOpenSegment`（打了上班卡没下班）的班。零打卡的班没有任何触发点。
- 工时页的「未打卡 ＋」是 `ComputeSchedulePayAsync` 查询时现算的 `MissingPunch`（排班已结束且零打卡），不落库：不算「待处理」、不挡提交，店长审核列表（Web / 手机）也看不到。

## 2. 目标

排班结束后仍零打卡的班，自动生成一条待审「缺勤」给店长，店长必须处理（确认缺勤 / 补录打卡 / 转带薪假）后才能提交该期工时。

## 3. 设计

### 3.1 新待审类型

- `SourceType = "NoShow"`，`SourceGuid = ScheduleGuid`，`ApplicantUserGuid = 排班员工`，`StoreCode = 排班分店`。
- 复用 `AttendanceApproval` 表，审核本身不用加表（SourceType 是字符串）；但 §3.8 的设置项需要给 `AttendanceSettings` 加列。
- 结案规则同 `MissingClockOut`：已批准 / 已拒绝即结案、不再重开；系统自动取消（Cancelled）不算结案，条件再次成立可重新生成（加入 `BuildPendingApprovalExistenceQuery` 的 `is "MissingClockOut" or "Punch"` 分支）。

### 3.2 生成条件（全部满足）

1. 排班 `Status = Active`、未删除；
2. 门店本地时间已过 **排班结束 + 宽限分钟**（考勤设置 `NoShowGraceMinutes`，默认 60 分钟）；且总开关 `NoShowApprovalEnabled` 打开；
3. 该排班 **零条有效打卡**，且该员工当天在本店也没有未挂排班的打卡（否则属于「未关联排班打卡」场景，交给现有收编逻辑，避免误报）；
4. 当天没有覆盖该班的 **已批准或待审** 请假（病假 / 年假等）；
5. 排班日 ≥ **启用日 2026-11-16**（后端常量，回溯上一周），且所在工资期 **未提交 / 未锁定**。

### 3.3 生成时机

在现有 `AttendanceAutoClockOutWorker` 循环里（每几分钟一轮）追加一步 `RunNoShowDetectionAsync`：

- 回看 2 天排班，逐店按门店时区判断 §3.2；
- 与打卡 / 补卡共用「员工锁」，锁内重读打卡，避免与员工刚好补卡并发；
- 幂等：已有 Pending / Approved / Rejected 的不重复生成。
- 另在店长打开工时统计时顺手跑一次本店本期（类似 `GeneratePublicHolidayPayApprovalsAsync`），worker 停机时也不漏。
- **回溯 11-16 那周**：worker 只回看 2 天，旧周由「打开工时统计」这一步补生成。店长打开 11-16～11-22 那周时，未提交的话会一次生成该周全部缺勤（截图那周约二三十条）；已提交 / 锁定的期跳过。

### 3.4 自动撤销（Cancelled）

以下任一发生，自动取消 Pending 的 NoShow（新增 `ReconcileNoShowApprovalAsync`，挂在 `ReconcileMissingClockOutApprovalAsync` 同一调用点）：

- 该班出现打卡（员工补卡、店长手动补录 `ManualEntry`、未关联打卡被收编）；
- 当天批了覆盖该班的请假（含「补录带薪假」）；
- 排班被删除 / 取消。

批准时再做一次「状态仍成立」校验（同 `MISSING_CLOCK_OUT_RESOLVED` 写法），不成立就自动取消并提示 `NO_SHOW_RESOLVED`。

### 3.5 店长处理方式

| 操作 | 结果 |
| --- | --- |
| **确认缺勤** | NoShow → Approved；该班 0 工时，工时格由琥珀色「未打卡 ＋」改为灰色「缺勤」，不再出现在顶部未打卡提示条 |
| **补录打卡** | 打开现有补录抽屉（`ManualEntryDrawer`，预填排班时间）；保存后 NoShow 自动取消 |
| **转带薪假** | 打开现有补录带薪假抽屉（`ManualLeaveDrawer`）；保存后自动取消 |

不提供「拒绝」：缺勤是事实确认，不存在驳回语义。

### 3.6 对工时统计 / 工资表的影响

- Pending 的 NoShow 计入 `PendingCount`，**`SubmitPayPeriodAsync` 现有的 `PendingCount > 0` 门槛自动生效** → 清零后才能提交。
- `AttendancePayrollEmployeeDayDto.PendingTypes` 增加 `NoShow`，格子显示「待审」角标；已确认的 `MissingPunch = false` + 新字段 `NoShowConfirmed = true`。
- 待处理卡片文案：「含 N 条缺勤确认」（与现有「含 N 条公假确认」并列）。

### 3.7 各端改动

| 端 | 改动 |
| --- | --- |
| 主 API | 新类型常量、检测 + 撤销 + 批准校验、worker 追加一步、工时快照计数与日格字段、审核列表 DTO（排班时段、门店） |
| Web | 考勤审核页新类型卡片（显示排班时段，三个按钮）、工时格「缺勤」状态与气泡、i18n 中英 |
| 手机 | `ManagerApprovalList` / `ApprovalCard` 新增 `NoShowDetail`；`payroll-hours` 类型；英文术语沿用 Roster / Clock in → 「No-show」 |
| 通知 | 不逐条推送（一天可能几十条），沿用审核入口红点；如需推送另议每日汇总 |

### 3.8 考勤设置项

`AttendanceSettings` 新增两列（迁移写法同 `AttendanceReviewAutoClockOutSchema`，`COL_LENGTH IS NULL` 才加）：

| 列 | 类型 / 默认 | 说明 |
| --- | --- | --- |
| `NoShowApprovalEnabled` | `bit NOT NULL DEFAULT 1` | 总开关；关掉后不再生成，已有 Pending 保留 |
| `NoShowGraceMinutes` | `int NOT NULL DEFAULT 60` | 班结束后多少分钟仍零打卡算缺勤，限 15～720 |

Web 设置页 `AttendancePayrollSettingsFields.tsx` 加一个开关 + 分钟数输入，中英文案；设置 DTO / 保存校验同步。

部署：**先 `--schema=migrate`**，再主 API + Web + 手机 OTA；无原生包改动。

## 4. 测试

- 后端（`BlazorApp.Api.Tests`，新文件须手动加 `<Compile Include>`）：
  - 班结束 + 宽限前不生成、之后生成；有任一打卡 / 有覆盖请假 / 已锁定工资期 / 启用日之前不生成；
  - 重复跑幂等；补录打卡、补录带薪假、删排班均自动取消；Cancelled 后条件再成立可重新生成，Approved 后不重开；
  - 批准时状态已消除 → `NO_SHOW_RESOLVED`；
  - 有 Pending NoShow 时 `SubmitPayPeriodAsync` 被拦；确认后可提交且该班 0 工时。
- Web：`manualEntryLogic` / `hoursCellDetailLogic` 对「缺勤」格的单测；审核页类型渲染契约。
- 手机：`approval-board` / `attendance-approval` 归一化单测。

## 5. 已拍板（2026-11-25）

1. **挡提交**：Pending 的缺勤计入待处理，清零后才能提交。
2. **生成时机**：班结束后 1 小时（设置项默认 60 分钟，可调）。
3. **回溯**：从 2026-11-16 那周起；已提交 / 锁定的工资期跳过。
4. **宽限做成考勤设置项**：开关 + 分钟数，见 §3.8。
