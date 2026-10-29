# 考勤：审核口径、带薪假、工时统计与工资（规格）

> 2026-10-20 ~ 26 与业务方逐条确认。设计图在 `docs/design/attendance-payroll-20261021/`（PNG，源文件在其 `src/`，`bash src/shot.sh <名> <宽>` 重出图）。
> 本文是实现的唯一口径来源；标「待确认」的按这里写的默认值做，并在 PR 描述里列出。

## 0. 已定口径（全部）

### 0.1 审核与打卡（第一轮）
1. **店长审核本人申请**：新权限 `Attendance.Approval.ReviewSelf`，**默认不授予任何角色**；没有它时维持「申请人不能审自己」。有它时审批记录标记「本人审批」，Web 可筛选。
2. **有打卡的排班允许删除 / 作废，但只能软删除，能找回**：提供「已删除排班」列表与恢复接口；恢复后重新对账派生审批。
3. **漏下班、加班审批在店长打开审核列表时生成**：`GET approvals/pending` 先对该店近 14 天排班做一次对账（加锁，失败不阻断列表）。
4. **打卡请求带客户端期望类型**：`AttendancePunchRequestDto.ExpectedPunchType`（`ClockIn` / `ClockOut`，可空＝旧客户端，按原逻辑）。与服务端判定不一致 → 拒绝，错误码 `PUNCH_TYPE_MISMATCH`，消息告诉员工「系统记录你已上班 / 已下班」。
5. **午夜自动下班**：门店时区 00:00 时，仍未闭合的班段由系统补一条下班卡（`IsSystemAutoClockOut = true`，时间 = 当天 24:00），**该自动时段不计薪**，并生成漏下班待处理；店长「填实际下班时间」＝补卡并结案，结案后不再重开。
6. **审批类权限在生产手动授权**，不靠角色种子下发。
7. **每天打卡段数上限：员工 4、店长 6**（原 `ResolveSegmentLimitAsync` 写死 2 / 3）。

### 0.2 工时统计、带薪假、工资（第二、三轮）
- 统计表工时一律**十进制小时，最多 2 位小数**（7:59 → 7.98）。
- 统计周期**默认 1 周，可切 2 周**；两周起点由设置「双周起始日」决定（示例 2026-10-05），保证与发薪周期对齐。
- **只计已审核**；**加班只算已批准的**。计薪 = 排班内工时 + 已批准加班 − 扣餐 + 已批准加回。
- **周末、公假按实际工时**，不乘系数；**转账时薪**分 **平日 / 周六 / 周日 / 公假** 四档；**现金时薪只有一个价，不分周末和公假**（10-26 定）。
- **全职 / 兼职**可申请带薪假，临时工（Casual）不可：
  - **带薪病假**：只能选有排班、没打卡的日子；生病当天起 **4 天内**提交；必须拍医嘱（≤3 张）。带薪工时 = 当天排班工时扣餐。
  - **带薪年假 · 休息**：提前 **≥ 4 周**；单次 **≤ 2 周**；批准后这些天自动「不能上班」，排班被拦。**申请工时 = 区间内工作日（周一至周五，排除公共假期）× 8 h**，周末和公假不算（10-26 定）。
  - **带薪年假 · 发放（照常上班）**：提前 **≥ 2 天**；单次 **≤ 4 周**；照常排班，另计年假工时（**合规需会计确认**，系统只提示）。
  - **年假（休息、发放）最终发多少由财务核定**：系统不累计余额；工资表里每人每期有「年假核定工时」，默认等于申请工时，持 `Attendance.Payroll.Edit` 的人可改，提交锁定后不可改。工时统计显示申请工时，工资表按核定工时算钱。
- **年假 / 病假余额暂不累计**。
- **公共假期角标**；全职 / 兼职公假当天没排班，但**过去 10 周同一星期几排班 ≥ 7 次**（参数可设）→ 生成「带薪公假确认」待处理，店长确认工时或标「不适用」。
- 未成年显示**周岁**；**生日角标**，提前 3 天提醒；未成年人生日提示核对时薪。
- 店长或员工在**其他分店也有打卡**时，统计表角标 + 提示「是否合并」；合并是查看参数，不改数据。
- **个人时薪表**按「生效日期」留历史；工资按工作日当天有效的那条算。
- **发放方式**三种：全转账 / 全现金 / 转账 + 现金。
  - 带薪假期：全转账、转账+现金按**平日转账时薪**；全现金员工所有工时（含带薪假）都按**现金时薪**。
  - 转账 + 现金：转账容量 = 固定转账工时/周 × 周数，按 **周日 → 周六 → 带薪假 → 公假 → 平日** 依次装入（10-26 定），每档用该档转账时薪（带薪假用平日转账时薪）；剩余工时合计 × 现金时薪；再 ± 每期现金补贴（负数＝扣回）。
  - **先把各档工时舍到 2 位小数再乘时薪，金额舍到分**。
- 工资表**备注列**，每人每期一条。
- **提交并锁定**：本期待处理清零后才能提交；提交后该店该期的排班、打卡、补卡、审批、请假、时薪、备注都不能再改；解锁需单独权限，留操作记录。
- **新权限，默认谁都没有，手动授权**：`Attendance.Payroll.View`（看工资表、时薪表）、`Attendance.Payroll.Edit`（改时薪、备注、提交锁定）、`Attendance.Payroll.Unlock`（解锁）。新码须入库，并在 `apps/mobile/src/locales/{zh,en}/accessPermissions.json` 补文案。

## 1. 数据模型（全部走显式迁移，实体不进 tableTypes）

| 表 / 字段 | 说明 |
|---|---|
| `AttendancePunch.IsSystemAutoClockOut bit` | 午夜自动下班标记 |
| `AttendanceSchedule` 软删除恢复 | 已有 `IsDeleted`；新增 `DeletedAt / DeletedBy`（若无） |
| `EmployeePayRate` | `RateGuid, UserGuid, PayMode(Transfer/Cash/Mixed), FixedTransferHoursPerWeek decimal(6,2), Transfer{Weekday,Saturday,Sunday,PublicHoliday}Rate decimal(8,2)?, CashRate decimal(8,2)?（单一价）, CashAllowancePerPeriod decimal(8,2), MinimumRate?, EffectiveFrom date, Remark, CreatedAt/By`；按 `(UserGuid, EffectiveFrom)` 唯一 |
| `AttendancePayPeriod` | `PeriodGuid, StoreCode, PeriodStart date, Weeks tinyint, Status(Open/Submitted), SubmittedAt/By, UnlockedAt/By, UnlockReason` |
| `AttendancePayPeriodNote` | `StoreCode, PeriodStart, Weeks, UserGuid, Note`，唯一 `(StoreCode, PeriodStart, Weeks, UserGuid)` |
| `AttendancePayPeriodLeaveApproval` | 财务核定：`StoreCode, PeriodStart, Weeks, UserGuid, LeaveType(PaidAnnual/AnnualCashOut), RequestedHours, ApprovedHours, ApprovedBy/At`；没有记录时按申请工时 |
| `AttendanceLeaveRequest` 扩展 | `LeaveType` 增加 `PaidSick / PaidAnnual / AnnualCashOut`；`PaidHours decimal(6,2)?`、`AttachmentUrls nvarchar(max)`（JSON 数组，兼容旧单字段）、`ScheduleGuids nvarchar(max)`（病假所选排班） |
| `AttendancePublicHolidayPay` | `UserGuid, StoreCode, HolidayDate, OrdinaryCount, LookbackWeeks, Hours decimal?, Status(Pending/Confirmed/NotApplicable), ReviewedBy/At`；进审批中心，SourceType `PublicHolidayPay` |
| `AttendanceSettings` 扩展 | `DefaultPeriodWeeks(1)`、`FortnightAnchorDate`、`PublicHolidayLookbackWeeks(10)`、`PublicHolidayMinCount(7)`、`SickLeaveSubmitDays(4)`、`AnnualLeaveLeadWeeks(4)`、`AnnualLeaveMaxWeeks(2)`、`AnnualCashOutLeadDays(2)`、`AnnualCashOutMaxWeeks(4)`、`BirthdayRemindDays(3)`、`EmployeeSegmentLimit(4)`、`ManagerSegmentLimit(6)`、`AutoClockOutAtMidnight(true)` |

## 2. 接口（`api/react/v1/attendance/...`，信封沿用 `ApiResponse`）

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET | `payroll/hours?storeCode&periodStart&weeks&includePending&mergeStores` | `Attendance.Punch.ViewManagedStore` | 员工 × 日：`paidHours / byTier{weekday,saturday,sunday,publicHoliday} / leave{type,hours,hasCertificate} / publicHolidayPay{state,hours,count} / pending[] / approvedMarks[] / otherStores[{storeCode,hours}] / isBirthday`；员工：`employmentType, ageYears, birthday, segmentLimit, hasOtherStores`；提醒列表 `alerts[]` |
| GET | `payroll/pay?storeCode&periodStart&weeks&mergeStores` | `Attendance.Payroll.View` | 每人：各档工时、带薪假工时、转账桶 / 现金桶（按档）、金额、补贴、应发、备注；期状态 |
| GET / POST | `payroll/rates`、`payroll/rates/{userGuid}/history` | View / Edit | POST 只新增「生效记录」，不改历史 |
| PUT | `payroll/periods/notes` | Edit | 每人每期备注 |
| PUT | `payroll/periods/leave-approvals` | Edit | 每人每期年假（休息 / 发放）核定工时 |
| POST | `payroll/periods/submit`、`payroll/periods/unlock` | Edit / Unlock | 提交校验待处理为 0；锁定后各写接口返回 `PERIOD_LOCKED` |
| POST | `my/leave-requests`（扩展） | `Attendance.Leave.ApplySelf` | 按类型校验第 0.2 节规则；临时工拒绝带薪类型 `PAID_LEAVE_NOT_ELIGIBLE` |
| POST | `leave-attachments/upload-signature`（放开员工本人） | `Attendance.Leave.ApplySelf` | 对象键限制在本人目录 |
| GET / POST | `schedules/deleted`、`schedules/{guid}/restore` | `Attendance.Schedule.EditManagedStore` | 软删恢复 |

## 3. 计算口径（后端为准，前端只展示）
- 当天计薪分钟 = Σ 已闭合段与排班窗口的重叠 + 已批准加班 − 扣餐 + 已批准加回；自动下班时段不计；待审补卡不计。
- 扣餐 = max(0, min(排班有效次数, ⌊实际工时 / 4h⌋) − 段间满 30 分钟空档数) × 30。
- 档位：门店时区的工作日；公假 = `AttendanceStoreHoliday` 当天（`IsPaidHoliday` 或公共假期来源）。
- 年假休息申请工时 = 区间内（周一至周五且不是公共假期）的天数 × 8；病假 = 当天排班工时扣餐；带薪公假 = 店长确认的工时。
- 工资里的年假工时 = 财务核定工时（无记录时 = 申请工时）。
- 小时 = round(分钟 / 60, 2)；金额 = round(小时 × 时薪, 2)，逐档相加。
- 验收样例（设计图两周数据）：Jack Nguyen 应付 65.88 h，转账 60 h = 日 3.5 + 六 8 + 假 15.6 + 平 32.9 → $1,816.92；现金 平 5.88 h → $167.58，补贴 −$20，应发 **$1,964.50**。Emma Walsh 年假申请 24 h、核定 20 h，应发 $1,702.96。Liam Patel 现金 31.98 h × $27 = $863.46，应发 $1,612.71；Mia Robinson 全现金 48.48 h × $23.10 = $1,119.89。

## 4. 分期与依赖

| 卡 | 范围 | 依赖 |
|---|---|---|
| A 后端·审核与打卡 | 14 条缺陷修复 + 0.1 节 7 条 | 无 |
| B 后端·带薪假与工资 | 第 1、2、3 节 | A 合并后开始（同改 `AttendanceReactService.cs`、迁移登记） |
| C Web | 审核工作台、工时与工资三页签、设置页 | 按本文接口先做，A / B 合并后联调 |
| D 移动端 | 审核、带薪假申请与审核、工时统计 | 同 C；纯 JS 可 OTA |

上线顺序：`--schema=migrate` → 后端 → Web → 移动端 OTA。
