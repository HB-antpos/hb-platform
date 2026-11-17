# 从 MYOB 同步转账平日时薪（个人时薪表）方案

日期：2026-11-16　状态：**待确认**（见第 10 节）；分支 `claude/myob-salary-sync`

## 1. 目标与边界

**目标**：时薪表里大量员工没有时薪记录（截图里「有工时的档缺时薪，这部分没算钱」），财务要逐人手填。
MYOB 工资卡里已有基础时薪，希望一键从 MYOB 带过来，减少手填和填错。

**只做**：
1. MYOB `EmployeePayrollDetails.Wage.HourlyRate` → 我方 `EmployeePayRate.TransferWeekdayRate`（转账平日价）；
2. **周六 / 周日转账价建议值** = 该员工 MYOB 基础时薪 × 其所属档（临时工 / 非临时工）映射类别的倍数（第 3.4 节），**默认不勾**，勾了才写入。
3. **公假转账价按 MYOB 走**（用户 2026-11-17 决定）= 该员工 MYOB 基础时薪 × 公假类别倍数（全职、兼职 2.25；临时工 2.0），**默认随平日价一起写入**（第 3.4、3.5 节）。这**取代**总方案 11.4「公假价恒等于平日价」，需要同时改后端与表单。

**不做**（理由）：

| 项 | 为什么不做 |
|---|---|
| 现金时薪、现金补贴、固定转账 h/周 | MYOB 没有，现金不进 MYOB |
| 最低时薪 `MinimumRate` | 我方自填的合规线，MYOB 没有 |
| 周六 / 周日转账价（自动写入） | MYOB 里没有独立字段，是「工资类别」上的倍数（如 `SATURDAY (x1.25)`）。只给**建议值**、默认不勾，不会静默写入；规则见 3.4 |
| 年薪制员工 | `HourlyRate` 为 0，`AnnualSalary` 属敏感数据，一律不读、不写 |
| 我方 → MYOB 写回 | 总方案 11.3 的结论不变，MYOB 时薪仍由财务手工维护 |

方向是**单向、只读 MYOB、只写我方**，不会改 MYOB 任何数据。

## 2. 前置：先用探针核对真实字段（第 0 批）

`Wage` 节字段名目前只是按官方文档写的，没对过真实响应（总方案 11.3、探针注释都这么说）。
只读探针 #1085 已随 10-30 整包部署上线，入口：

`GET /api/react/v1/myob/employee-payroll/wage-probe?connectionGuid=&employeeDisplayId=`（权限 `Myob.Connection.Manage`）

需要核对并把结论写回本文件：

1. `Wage.HourlyRate` 是否真实存在、类型是数字；
2. `Wage.PayBasis` 的取值（`Hourly` / `Salary`）与各自数量，年薪制的 `HourlyRate` 是否为 0；
3. 用 `employeeDisplayId` 指定一个员工，把 `HourlyRate` 与 MYOB 界面里的「Hourly Rate」比一眼；
4. **临时工（Casual）的 `HourlyRate` 是否已含 25% 加成**：对照该员工最近工资单的 `Lines[].CalculationRate`（工资单方案 1.3 节已确认该字段存在）；
5. 6 个公司文件里实际有绑定员工的只有 `e49f2d94…`（8 人），同步范围只会落在已绑定员工上。
6. ✅ 已核对（见 2.2）。**工资类别的倍数字段**（批 3 用）：`GET Payroll/PayrollCategory/Wage` 的真实响应里，`PayRate`、`RegularRateMultiplier`（或新版 `HourlyDetails`）叫什么、`SATURDAY (x1.25)` 的值是不是 1.25、`FixedHourly` 类别有没有。探针目前只覆盖 `EmployeePayrollDetails`，批 0 需要补一个同样只回字段路径和汇总、不回个人值的类别探测（或直接只读调用一次接口人工核对）。

探针要生产登录态，由用户触发或授权我读取；结论落文档后，**正式实现 PR 里删除探针代码**（注释里写明了「核对完整体删除」）。

### 2.1 探测结果（2026-11-16，用户授权，只读；用 ego 浏览器在生产登录态里调用，只取字段路径与汇总，没取任何个人值）

对 7 个已连接的公司文件各调了一次 `wage-probe`，全部 HTTP 200：

| 公司文件 | 卡数 | 有 Wage 节 | PayBasis | HourlyRate 为 0 的卡 | 中位 / 最大 |
|---|---|---|---|---|---|
| Australian | 58 | 58 | Hourly 54 / Salary 4 | 1 | 28.45 / 79.6105 |
| Investment | 63 | 63 | Hourly 63 | 0 | 26.30 / 36.11 |
| Kawana | 49 | 49 | Hourly 49 | 0 | 23.11 / 34.76 |
| Kippa Ring | 41 | 41 | Hourly 23 / Salary 18 | 18 | 13.91 / 34.76 |
| Orion | 63 | 63 | Hourly 63 | 0 | 24.73 / 34.76 |
| Peninsula Fair | 53 | 53 | Hourly 53 | 0 | 29.23 / 36.00 |
| Robinson Road | 56 | 56 | Hourly 56 | 0 | 24.34 / 34.76 |

**已证实**

1. `Wage.HourlyRate` 真实存在、类型 Number、每张卡都有；`Wage.PayBasis` 取值 `Hourly` / `Salary`；`Wage.PayFrequency` 全部 `Weekly`。
2. **年薪制不能只看 `HourlyRate` 是不是 0**：Kippa Ring 的 18 张 Salary 卡时薪恰好都是 0，但 Australian 有 4 张 Salary 卡却只有 1 张是 0，说明有年薪制卡带着非零的折算时薪。所以判定必须**以 `PayBasis == Hourly` 为准**，`PayBasis` 非 Hourly 一律 Skip（3.1 已按此写，保留）。
3. **`HourlyRate` 有 4 位小数**（Australian 最大值 79.6105），而 `EmployeePayRate` 的时薪列是 2 位小数。预览里显示 MYOB 原值，比较与写入前按 `RoundRate` 取 2 位；原值与取整后不同的，预览里标「已取整」。
4. 员工卡里 **`Wage.WageCategories[]` 带 `Name / Type / UID / URI`**，所以「该员工卡有没有挂某个类别」可以直接按 UID 判断（3.4 的 `WageCategoryUids`）。
5. 卡里还有 `TerminationDate`（Australian 58 张里 15 张非空）和 `EmploymentStatus`：已离职的卡不应再生成新时薪，列为 Skip `MYOB_EMPLOYEE_TERMINATED`（3.1 已补）。
6. `Wage.AnnualSalary`（全部卡都有）、`Wage.HoursInWeeklyPayPeriod`、`DateOfBirth`、`Tax.TaxFileNumber` 等字段真实存在，印证 4.2 的隐私约束：只读 `PayBasis` 与 `HourlyRate`，其余一律丢弃。

### 2.2 工资类别探测结果（2026-11-17，用户授权，只读；PR #1320 的类别探测接口）

`GET /api/react/v1/myob/employee-payroll/wage-probe/categories` 对 7 个公司文件各调一次，全部 HTTP 200（Australian 26 个类别、Investment 26、Kawana 27、Kippa Ring 25、Orion 27、Peninsula 25、Robinson Road 27）。该接口是 #1320 合并后由另一个会话在 11-17 16:46 整包部署 main 时一并上线的，运行中的容器 DLL 里确认含 `RunCategoriesAsync`，本会话没有重复部署。

**已证实（以 Australian 的 26 个类别字段路径为准）**

| 字段 | 类型 | 说明 |
|---|---|---|
| `HourlyDetails` | Object 或 Null（21/26 有） | 时薪类别才有；`Base Salary`、`Bonus` 等 Salary 类别为 Null |
| `HourlyDetails.PayRate` | String（21/21） | 取值见到的全是 `RegularRate`，即「员工基础时薪 × 倍数」 |
| **`HourlyDetails.RegularRateMultiplier`** | **Number（21/21）** | **倍数就在这里**，值以 6 位小数的原文出现，如 `1.250000` |
| `HourlyDetails.FixedHourlyRate` | Null（0/21 有值） | 固定时薪，Australian 没有任何类别用 |
| `HourlyDetails.AutomaticallyAdjustBaseAmounts` | Boolean | 与本需求无关 |
| `WageType` / `Type` / `StpCategory` | String | `WageType` 为 `Hourly` / `Salary` |

即：真实字段是新版风格的 **`HourlyDetails` 嵌套对象**，**不是**顶层的 `RegularRateMultiplier`（文档里 AccountRight v2 的写法）。

**各公司文件的周末 / 公假类别与倍数（值取自 `RegularRateMultiplier`，名称仅供对照）**

| 档 | 倍数 | 各公司里的叫法（节选） |
|---|---|---|
| 周六（全职、兼职） | 1.25 | `SATURDAY (x1.25)`、`FT/PT SATURDAY (X1.25)`、`SATURDAY HOURS (x1.25)` |
| 周日（全职、兼职） | 1.5 | `SUNDAY (x1.5)`、`FT/PT SUNDAY (X1.5)`、`SUNDAY HOURS (x1.5)` |
| 周六（临时工） | 1.2 | `SATUDAY (x 1.2)`（Australian，拼写错误）、`SATURDAY (x1.2)`、`SATURDAY HOURS (x1.2)`、`SATURDAY HOURS CASUAL (x1.2)` |
| 周日（临时工） | 1.4 | `SUNDAY (x1.4)`、`SUNDAY HOURS (x1.4)`、`SUNDAY HOURS CASUAL (x1.4)` |
| 公假（全职、兼职） | 2.25 | `PUBLIC HOLIDAY (x2.25)`、`FT/PT PUBLIC (X2.25)`、`PUBLIC HOILDAY HOURS (x2.25)`（Robinson Road，拼写错误） |
| 公假（临时工） | 2.0 | `PUBLIC HOLIDAY (x2)`、`PUBLIC HOLIDAY CASUAL (X2)` |

**结论**

1. **周末建议值的倍数必须取结构化字段 `HourlyDetails.RegularRateMultiplier`，不能按名称解析**：7 个公司文件里同一档有 3 种以上叫法，还有拼写错误（`SATUDAY`、`HOILDAY`）、大小写不一致，Peninsula 的临时工类别名里甚至没有 `casual` 一词（`SATURDAY HOURS (x1.2)`）。类别靠已保存的映射（`MyobPayCategoryMapping` 的 `saturdayUid` 等）按 UID 取，不靠名称。
2. **倍数在各公司文件里是一致的**（周六 1.25 / 1.2，周日 1.5 / 1.4），所以建议值与公司无关，只取决于「临时工 / 非临时工」。
3. **公假倍数与我方原规则不同**：MYOB 公假是 2.25 / 2，我方原规则是公假转账价 = 平日转账价（11.4）。**用户 2026-11-17 决定公假价按 MYOB 走**，即公假价 = 基础价 × 公假类别倍数；原规则 11.4 作废，改法见 3.5。
4. 本次打印只覆盖名称含 sat / sun / public / holiday / base / overtime 的类别；其余类别（假期、预支等）没逐条看，也就没有核对它们是否出现 `FixedHourly`。周末建议值只用到映射里选中的那几个类别，因此遇到 `PayRate != RegularRate` 的类别按「无法推算」处理即可（3.4 已写）。

**仍未核对**

- **与 MYOB 界面对一眼**：传 `employeeDisplayId` 会返回某个人的时薪，属于个人值，这次没做。
- **临时工的 `HourlyRate` 是否已含 25% 加成**：需要一名 Casual 员工的 `HourlyRate` 与其工资单 `CalculationRate` 对照，同样涉及个人值。要做的话请你指定一名员工（或让我用测试卡），再另行授权。

## 3. 数据映射与规则

只处理「已绑定（`EmployeeMyobLink` 有效）且 MYOB 卡仍存在」的员工，沿用员工资料同步的绑定口径。

### 3.1 每个员工的判定（纯函数，放 `MyobPayRateSyncRules`，无 IO）

输入：MYOB `PayBasis` / `HourlyRate`，我方「生效日 ≤ 目标生效日的最新一条」`EmployeePayRate`。

| 情形 | 类型 | 默认勾选 | 说明 |
|---|---|---|---|
| MYOB `PayBasis` 非 Hourly，或 `HourlyRate` ≤ 0 | `Skip` `MYOB_RATE_NOT_HOURLY` | — | 年薪制 / 没填，只列原因（**以 `PayBasis` 为准**，年薪制卡可能带非零折算时薪，见 2.1） |
| MYOB 卡 `TerminationDate` 已过 | `Skip` `MYOB_EMPLOYEE_TERMINATED` | — | 已离职，不再生成新时薪 |
| 我方没有任何时薪记录 | `Create` | ✔ | 新建一条，见 3.2 |
| 我方有记录，`PayMode` = Cash（全现金） | `Skip` `PAY_MODE_CASH` | — | 转账价不参与计算，MYOB 值无意义 |
| 我方转账平日价与 MYOB 相等（按 2 位小数） | `Same` | — | 不列差异 |
| 我方平日价为空 | `Fill` | ✔ | 补空 |
| 我方平日价与 MYOB 不同 | `Differ` | ✘ | 默认不勾，逐项勾才覆盖，同「只补空」默认 |

比较一律先 `RoundRate`（2 位小数）再比，避免 28.4 vs 28.40 的假差异。

### 3.2 写入语义：**只新增一条新记录，从不改历史**

`EmployeePayRate` 是「按生效日期留历史、只新增不改」的表，且 `(UserGuid, EffectiveFrom)` 唯一。所以同步 = 新增一条：

- **有旧记录**：整条复制当前记录（`PayMode`、周六/周日价、现金价、补贴、最低时薪、固定转账工时都原样带过来），只改 `TransferWeekdayRate`；**若勾选了公假价**（默认随平日价勾上），再把 `TransferPublicHolidayRate` 改成按公假类别倍数推算的值（3.4）；**若勾选了周末建议值**，再把对应的 `TransferSaturdayRate` / `TransferSundayRate` 改成建议值。公假价没勾或无法推算时沿用旧值（不再强制等于平日价）。
- **没有旧记录（`Create`）**：`PayMode = Transfer`（全转账），`TransferWeekdayRate` = MYOB 值；`TransferPublicHolidayRate` 默认写按公假倍数推算的值（无法推算则留空）；周六 / 周日价默认**留空**，勾选了建议值才写入。
- `Remark` 固定写 `来自 MYOB 同步 yyyy-MM-dd`，方便财务识别来源；`CreatedBy` 为当前操作人。
- 不新建迁移、不新建批次表（见 6.2 关于撤销的取舍）。

### 3.3 生效日期

- 默认值 = **下一个未锁定周期的起始日**；预览页给日期选择器，财务可改。
- 沿用 `CreatePayRateAsync` 现有校验：员工所在**任一分店**只要有「已锁定且结束在生效日之后」的周期，该员工失败并提示 `PERIOD_LOCKED`；同一生效日已有记录则失败 `RATE_EFFECTIVE_DATE_EXISTS`。
- 这样可以保证**已锁定周期的工资重算结果不变**。

### 3.4 周六 / 周日价（已定：并进第一期，建议值默认不勾，不勾则留空）

计算器里周末价为空 = 该档工时不计钱并告警。所以不勾建议值时，新员工周末价留空，工资表会如实标「缺时薪」，**不会**静默默认成平日价；勾了就按下面的规则写入。

**周末倍数的来源（已与用户确认）**：就是 Web「MYOB 工资类别映射」页里我方工时档 → MYOB 类别的那张表（`MyobPayCategoryMapping`，每个公司文件一份）。例如 Hot Bargain Australian：

| 我方档 | MYOB 类别 | 倍数 |
|---|---|---|
| 周六（全职、兼职） | `SATURDAY (x1.25)` | 1.25 |
| 周日（全职、兼职） | `SUNDAY (x1.5)` | 1.5 |
| 周六（临时工） | `SATUDAY (x 1.2)`（原名拼错） | 1.2 |
| 周日（临时工） | `SUNDAY (x1.4)` | 1.4 |

所以周末价 = **该员工的 MYOB 基础时薪 × 他所属档（临时工 / 非临时工）映射类别的倍数**。

**不按名字里的 `(x1.25)` 去解析**（名称不统一、有拼写错误，工资单方案 1.3 节已明确「不能按名称做业务判断」）。MYOB 工资类别接口有结构化字段，据公开文档：

**已用真实响应证实（2.2）**：倍数在 `HourlyDetails.RegularRateMultiplier`（Number，值如 `1.250000`），配合 `HourlyDetails.PayRate`（`RegularRate` 才是「基础时薪 × 倍数」）与 `HourlyDetails.FixedHourlyRate`（固定时薪，目前都是 null）。`Salary` 类别的 `HourlyDetails` 为 Null。

注意两种情形：`PayRate = FixedHourly` 的类别是固定时薪、没有倍数，不能乘；以及员工卡的 `Wage.WageCategories` 里必须真的挂着该类别（没挂的类别推送时 MYOB 也不会按它发薪）。本仓库的 `WageCategoryPayload` 目前只取了 `Uid / Name / WageType`，没取 `HourlyDetails`，批 1 要补上。

**公假价同样按这个算（用户 2026-11-17 决定）**：公假价 = `RoundRate(基础价 × 公假类别倍数)`，类别取映射里的 `publicHolidayUid`（全职、兼职）/ `publicHolidayCasualUid`（临时工，**可选**映射，没配则无法推算）。真实倍数：全职兼职 2.25，临时工 2.0（2.2 节）。无法推算的情形、取整与「各自独立勾选」规则与周末一致；**与周末不同的是公假价默认随平日价勾上**（原来公假价就是自动跟着平日价变的，现在平日价写入而公假价不动会留下过期的值）。


**建议值规则（第一期，纯函数 `MyobPayRateSyncRules.SuggestWeekend`）**：

- 取员工 `employmentType`：Casual（我方 `EmployeeType.Temporary`）用「临时工周六 / 周日」映射，其余用全职兼职那一对；没填类型按全职兼职处理（与现有 Timesheet 规划器 `CategoryOf` 一致）。
- 建议值 = `RoundRate(基础价 × 倍数)`；倍数取自**映射类别的结构化字段**，不解析名称。
- **无法推算**（该员工该列显示原因，不可勾选）：类别 `PayRate = FixedHourly`；类别没挂在该员工卡的 `Wage.WageCategories` 上；映射未配置（临时工周末映射是**可选**的，没配则无法推算）；倍数字段缺失或 ≤ 0。
- **与我方现值不同**时在预览里标出对照（我方现价 → 建议值）；我方没有周末价则标「补空」。有旧记录且平日价变了、周末价没变的，标「周末价可能已过时」，方便财务勾选。
- 周六 / 周日两格**各自独立勾选**，只写勾了的格。
- 隐私与平日价一致：只返回我方员工名和价格，不含 MYOB UID / 年薪。

### 3.5 对总方案 11.4「公假价恒等于平日价」的修改（用户 2026-11-17 决定）

现状（代码里三处都在执行这条规则）：

| 位置 | 现在的行为 | 改成 |
|---|---|---|
| `AttendanceReactService.Payroll.cs:1103`（`CreatePayRateAsync`） | 保存时**强制** `TransferPublicHolidayRate = TransferWeekdayRate`，前端传什么都忽略 | 改为：**请求没带公假价时才默认等于平日价**（保持手填的旧体验），带了就按带的值保存；全现金模式仍为 null |
| `HasPublicHolidayRateMismatch` / 列表筛选 `PublicHolidayRateMismatch` | 公假价 ≠ 平日价就标「不一致」，列表顶部有横幅、行上有黄标签、有「只看不一致」开关 | 公假价与平日价不同现在是**合法**的：**退役**该标签、横幅与筛选开关（后端字段保留并恒为 false，避免旧 Web 缓存报错；下个版本再删） |
| `RatesTab.tsx` 表单 | 公假价输入框只读、自动等于平日价 | 改为**可编辑**，留空则保存时按平日价；提示文案改成「公假价按 MYOB 公假类别倍数设置」 |

**不改的**：已有记录不动（保证已锁定周期重算结果不变）；`PayrollCalculator` 不用改，本来就按 `TransferPublicHolidayRate` 取价；推送预览里 `PUBLIC_HOLIDAY_RATE_MISMATCH` 提示本来就已不产生（`MyobTimesheetPreviewService.cs` 注释），保持现状。

**这条改动让我方与 MYOB 的实发对齐**：推送时公假工时本来就走 MYOB 的公假类别（×2.25 / ×2），而我方工资表原来按平日价算，两边口径不同；现在公假上班的转账价与 MYOB 一致。

**一个要留意、不是这次引入的口径**：映射页写明「公假上班」和「带薪公假」**都**推送到同一个 MYOB 公共假期类别，但我方计算器里带薪假（含带薪公假）用的是**平日**转账价（`PayrollCalculator` 注释：带薪假用平日转账时薪）。所以改完之后，**公假上班**两边一致，**带薪公假（没上班）**仍是我方按平日价、MYOB 按 ×2.25，口径不同的情况没变。要不要一并对齐，见 10 的待确认。

## 4. 后端契约

放在 `Services/Myob/`，风格对齐 `MyobEmployeeSyncService`。全部 `ApiResponse<T>`，JSON camelCase。

**批 1 已按下面的契约实现**（`MyobPayRateSyncDtos.cs` / `MyobPayRateSyncRules.cs` / `MyobPayRateSyncService.cs` / `MyobPayRateSyncController.cs`）：

```
POST /api/react/v1/myob/pay-rate-sync/preview   Body { connectionGuid, storeCode, effectiveFrom?, refresh? }
POST /api/react/v1/myob/pay-rate-sync/apply     Body { connectionGuid, storeCode, effectiveFrom, items:[{ userGuid, hash, cells:[..] }] }
```

- **范围 = 一个分店的员工**（`storeCode` 必填）：员工列表与「现行时薪」直接取自现有 `GetPayRatesAsync`，所以分店权限与时薪表完全同口径；再与该公司文件的绑定求交集，没绑定的只计数（`notBoundCount`），不出现在行里。
- **权限**：类级同时挂 `Attendance.Payroll.Edit` 与 `Myob.Employee.Bind` 两个 `[Authorize]`（「且」的关系）；响应 `Cache-Control: no-store`。
- **preview**：只读。逐员工返回 `kind`（Create / Fill / Differ / Same / Skip）、`skipReason`、`payMode`、`myobHourlyRate`（原值，年薪制不带）、`rounded`、`hash`，以及 `weekday / saturday / sunday / publicHoliday` 四个格：`{ ourRate, suggested, multiplier, canApply, unavailableReason, defaultSelected }`。不写库。`effectiveFrom` 不传则用「该分店最后一个已提交周期结束的次日；一个都没提交过取本周周一」。
- **apply**：前端只传「员工 + 预览时的 `hash` + 勾选的格 `cells`（`weekday` / `saturday` / `sunday` / `publicHoliday`）」，**价格由服务端绕过缓存重新向 MYOB 取并重新推算**；`hash`（覆盖我方四个现值、MYOB 基础时薪原值、三个建议值）不一致该员工失败 `MYOB_RATE_SYNC_VALUE_CHANGED`。勾的格里有「没有可写内容」的整个员工拒绝（`MYOB_RATE_SYNC_NOT_APPLICABLE`），不写一部分；只勾周末、不勾平日的情形：有旧记录仍新增一条（平日价取我方现值）；没有旧记录则必须勾平日价。
- 写入逐员工调用现有 `IAttendanceReactService.CreatePayRateAsync`（分店权限、已锁定周期、同日重复、金额范围等校验与手填同一条路径，**没有复制任何校验**）；一个员工失败不影响其他人；失败原因码沿用 `CreatePayRateAsync` 的（`PERIOD_LOCKED`、`RATE_EFFECTIVE_DATE_EXISTS` 等）。返回 `written[]` 与 `failed[]`。
- 错误码是字符串常量（不是 5436x 那类 SQL 迁移错误号）：`MYOB_RATE_SYNC_INVALID_REQUEST`、`..._NO_ITEMS`、`..._NOT_IN_PREVIEW`、`..._NOT_BOUND`、`..._VALUE_CHANGED`、`..._NOT_APPLICABLE`；Skip / 无法推算原因码见 `MyobPayRateSyncReasons`（`MYOB_RATE_NOT_HOURLY`、`MYOB_EMPLOYEE_TERMINATED`、`MYOB_CARD_MISSING`、`PAY_MODE_CASH`、`CATEGORY_NOT_MAPPED / _NOT_FOUND / _NOT_ON_CARD / _NOT_RATE_MULTIPLE`）。

### 4.1 MYOB 读取

`MyobApiClient` 已有 `GetEmployeePayrollDetailsAsync`（`MyobEmployeePayrollRecord`）。需要给该记录**新增只读字段** `PayBasis`、`HourlyRate`、`WageCategoryUids`（员工卡挂的工资类别 UID 列表）、`TerminationDate`（**不要**加 `AnnualSalary`；以上字段路径均已在 2.1 用真实响应证实）。其它调用方（绑定、类型检查、资料同步）不受影响，因为是新增可空字段。

工资类别：`GetWageCategoriesAsync` 返回的 `MyobWageCategoryRecord` 目前只有 `Uid / Name / WageType`，要新增三个可空字段，取自嵌套对象 `HourlyDetails`（2.2 已证实）：`PayRate`（`HourlyDetails.PayRate`）、`RegularRateMultiplier`（`HourlyDetails.RegularRateMultiplier`，Number）、`FixedHourlyRate`（`HourlyDetails.FixedHourlyRate`）；`Salary` 类别 `HourlyDetails` 为 Null，三个字段都为 null。类别映射取自 `MyobPayCategoryService` 现有的读取路径，**不另建映射**；缓存沿用其现有策略，避免每次预览都多打一轮 MYOB（共用进程级 `RateGate`）。

### 4.2 隐私硬约束（写进测试）

- 响应与日志**不含**年薪、MYOB 员工 UID；
- 只返回我方员工名 + 两个时薪数；
- 请求日志不打印 body；
- 响应头 `Cache-Control: no-store`（与探针一致）。

## 5. 权限

时薪是敏感数据，页面本来就要 `Attendance.Payroll.Edit`。方案：

- 预览与写入都要求 **`Attendance.Payroll.Edit`**（写时薪）**且** **`Myob.Employee.Bind`**（读 MYOB 卡）；
- **不新增权限码**，避免「新码必须入库 + 补移动端文案」那一套（见 `new-permission-code-needs-mobile-label`）。
- 是否真有人同时持有这两个码，上线前要只读核对（见 10.2）。

## 6. 前端

### 6.1 入口与交互

页面：Web「工时与工资」→「个人时薪表」（`apps/web/src/pages/PosAdmin/AttendancePayroll/RatesTab.tsx`）。

- 表头工具栏加按钮「**从 MYOB 获取时薪**」，仅对有上述两个权限的人显示。
- 点开抽屉：① 选择分店 / 公司文件（当前页已选分店，默认只处理该店员工）+ 生效日期；② 预览表：员工 / 我方现价 / MYOB 价 / 类型标签 / 平日价勾选，另有「周六」「周日」「公假」三列各显示「我方现值 → 建议值」和各自的勾选框（周六 / 周日默认不勾，**公假默认随平日价勾上**；无法推算的灰显并写原因）；③ 确认写入 → 结果（成功 N、失败 N 及原因）。
- 抽屉布局复用 `MyobSyncDrawer` 的三步结构与样式，不重造。
- 逻辑拆成 `myobPayRateSyncLogic.ts`（纯函数 + `assert` 测试，风格同 `myobSyncLogic.ts`）。
- 文案中英两份：`myobMessages.zh.json` / `myobMessages.en.json`，英文术语遵循「移动端英文术语定案」。

### 6.2 为什么第一期不做「撤销」

- `EmployeePayRate` 的设计是**只新增不改、不删**；撤销就要删行，等于破坏这个不变量，还要判断「那一期有没有被锁定 / 已引用」。
- 同步前已有逐项预览，且 `Remark` 标了来源；写错了的补救办法 = 财务再新增一条更新生效日的记录（和手填错了的处理完全一样）。
- 因此**无迁移、无批次表**，上线只要部署主 API + Web，没有发布顺序坑。

### 6.3 顺手修一个现有问题

另外按 3.5 改时薪表单：公假价输入框改为可编辑，并退役「公假价与平日价不一致」的横幅、行标签和筛选开关（`RatesTab.tsx` 的 `mismatchBanner / mismatchTag` 与 `attendancePayrollMessages` 对应文案、`payrollLogic.ts` 的 `countPublicHolidayRateMismatch / hasPublicHolidayRateMismatch`）。

没有时薪记录的行，发放方式列渲染成 `attendancePayroll.payMode.undefined`（`RatesTab.tsx:193`，`rate.payMode` 为空时仍拼 key）。改成与 `PayTab.tsx:246` 一致，显示 `attendancePayroll.payMode.none`。

## 7. 测试计划

后端（`BlazorApp.Api.Tests`，**新文件必须手动加到 csproj 的 `<Compile Include>`**，否则静默不跑）：

1. `MyobPayRateSyncRules`：Create / Fill / Differ / Same / Skip(年薪 / ≤0 / 全现金) 各一例；2 位小数比较；`Mixed` 记录只改平日价与（勾选的）公假价、其余字段原样复制。
2. 生效日期：已锁定周期失败；同日已有记录失败；下一个未锁定周期默认值。
3. `hash` 不一致失败；一个失败不影响其他人。
3a. 周末建议值：全职兼职与临时工各用各的映射；倍数 × 基础价后 2 位小数；`FixedHourly` / 类别没挂在卡上 / 映射未配置 / 倍数缺失 → 「无法推算」且 apply 拒绝勾选；只勾周六不勾周日只写周六；只勾周末不勾平日则平日价取我方现值。
3b. **公假价**：全职兼职用 `publicHolidayUid`、临时工用 `publicHolidayCasualUid`（可选，没配则无法推算）；倍数取 `HourlyDetails.RegularRateMultiplier`（2.25 / 2.0）；默认随平日价勾上；无法推算时有旧记录沿用旧值、`Create` 留空；与周末互不影响。
3c. **改 11.4 的回归**（`AttendanceReactServicePayrollTests` 里现有「公假价强制等于平日价」的用例要改）：请求带公假价 → 按带的值保存；没带 → 默认等于平日价；全现金 → null；`PublicHolidayRateMismatch` 恒为 false；已有记录与已锁定周期的重算结果不变。
4. 隐私：响应序列化后不含年薪 / UID。
5. 权限：缺任一权限码 → 403。

前端：`myobPayRateSyncLogic` 的 `assert` 测试；组件目测走「免登录目测 Web 组件」那套造数服务。

**未验证项要在 PR 里如实写**：真实 MYOB 联调（取决于第 2 节探针）、Casual 是否含加成。

## 8. 发布

- 无迁移、无新权限码、不涉及移动端原生包（**PR 标题不加原生包标注**）。
- 顺序：先主 API 后 Web（Web 入口调用新接口，只发 Web 会 404）。
- **改 11.4 影响手动录入时薪**：`CreatePayRateAsync` 不再强制公假价等于平日价，所以后端与 Web 要成对上线（只发后端：旧 Web 表单公假价仍只读、不传值，后端按「没带则等于平日价」处理，行为不变；只发 Web：表单可编辑但旧后端会忽略公假价，所以**先发后端再发 Web**）。
- 本方案文档与「删除探针」放同一个实现 PR；**部署主 API 时预演会出现「删 2 个文件」（探针控制器 + 服务）**，这是预期，不要当成异常。

## 9. 分批

| 批 | 内容 | 风险 |
|---|---|---|
| 0 | 用探针核对真实字段，结论写回本文第 2 节 | 只读 |
| 1 | `MyobEmployeePayrollRecord` 加 `PayBasis / HourlyRate / WageCategoryUids`，工资类别记录加 `PayRate` / 倍数 + `MyobPayRateSyncRules`（含周末与公假推算）+ preview/apply + **改 `CreatePayRateAsync` 的公假规则（3.5）** + 测试 + 删探针 | 中 |
| 2 | 前端抽屉（含周六 / 周日 / 公假三列）+ 入口 + 时薪表单公假价可编辑并退役不一致提示（3.5）+ `payMode.undefined` 修复 | 低 |

（原「批 3 周末建议值」已并入第一期，见 3.4。**批 0 已全部完成：员工卡（2.1）与工资类别（2.2）的真实字段都已用生产响应核对，批 1 可直接开发**，倍数取 `HourlyDetails.RegularRateMultiplier`，不按名称解析。）

## 10. 决定与待确认

**已定（用户 2026-11-16）**

1. 新员工的周六 / 周日价：**默认留空**；**周末建议值并进第一期**（基础价 × 映射类别倍数，默认不勾，勾了才写，见 3.4）。
3. 默认生效日期：**下一个未锁定周期起始日**（预览页仍可改）。
4. 新员工默认发放方式：**全转账**。

**已定（用户 2026-11-17）**

7. **公假价按 MYOB 走**：公假价 = 基础价 × 公假类别倍数（全职兼职 2.25、临时工 2.0），默认随平日价一起写入；总方案 11.4「公假价恒等于平日价」作废，改法见 3.5。

**待确认**

2. **权限**：「`Attendance.Payroll.Edit` + `Myob.Employee.Bind` 两者都要」是否合适？管理时薪的财务是否通常同时有 `Myob.Employee.Bind`？不合适就改成单独的新权限码。
5. 是否同意**第一期不做撤销**（理由见 6.2）。
6. ~~周末建议值是否并进第一期~~：**已定，并进第一期**（用户 2026-11-16）。
8. **带薪公假（没上班）要不要也按公假价算**：映射页把「公假上班」和「带薪公假」都推送到 MYOB 的公假类别（×2.25 / ×2），但我方计算器里带薪假（含带薪公假）用的是**平日**转账价。公假上班改完后两边一致，带薪公假仍不一致（3.5 末段）。本方案**不改**计算器；要对齐就是另一项独立改动，影响已锁定周期之外的所有新周期工资，需要财务确认口径。
9. **手动录入表单的公假价**：本方案取「可编辑，留空则等于平日价」。若你希望表单里公假价也**必须**填、或仍然只读，请说一声。
