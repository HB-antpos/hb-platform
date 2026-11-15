# 从 MYOB 同步转账平日时薪（个人时薪表）方案

日期：2026-11-16　状态：**待确认**（见第 10 节）；分支 `claude/myob-salary-sync`

## 1. 目标与边界

**目标**：时薪表里大量员工没有时薪记录（截图里「有工时的档缺时薪，这部分没算钱」），财务要逐人手填。
MYOB 工资卡里已有基础时薪，希望一键从 MYOB 带过来，减少手填和填错。

**只做**：
1. MYOB `EmployeePayrollDetails.Wage.HourlyRate` → 我方 `EmployeePayRate.TransferWeekdayRate`（转账平日价）；
2. **周六 / 周日转账价建议值** = 该员工 MYOB 基础时薪 × 其所属档（临时工 / 非临时工）映射类别的倍数（第 3.4 节），**默认不勾**，勾了才写入。

**不做**（理由）：

| 项 | 为什么不做 |
|---|---|
| 现金时薪、现金补贴、固定转账 h/周 | MYOB 没有，现金不进 MYOB |
| 最低时薪 `MinimumRate` | 我方自填的合规线，MYOB 没有 |
| 公假转账价 | 公司规定恒等于平日转账价（总方案 11.4），后端保存时已强制相等 |
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
6. **工资类别的倍数字段**（批 3 用）：`GET Payroll/PayrollCategory/Wage` 的真实响应里，`PayRate`、`RegularRateMultiplier`（或新版 `HourlyDetails`）叫什么、`SATURDAY (x1.25)` 的值是不是 1.25、`FixedHourly` 类别有没有。探针目前只覆盖 `EmployeePayrollDetails`，批 0 需要补一个同样只回字段路径和汇总、不回个人值的类别探测（或直接只读调用一次接口人工核对）。

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

**仍未核对（批 0 剩余）**

- **工资类别的倍数字段**：现有类别映射接口（`GET /myob/pay-categories`）对 Australian 返回 26 个类别，每条只有 `uid / name / wageType`，**没有倍数**。要核对 `PayRate`（RegularRate / FixedHourly）和 `RegularRateMultiplier` 的真实字段名与值，需要给探针**新增一个只读的类别探测**（读 `Payroll/PayrollCategory/Wage` 原始 JSON；类别名与倍数是公司级数据，不是个人数据，可以回显），这需要改代码并部署主 API。因此批 1 的第一个提交就是这个类别探测，用它的结果再定周末建议值的解析。
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

- **有旧记录**：整条复制当前记录（`PayMode`、周六/周日价、现金价、补贴、最低时薪、固定转账工时都原样带过来），只改 `TransferWeekdayRate`，并把 `TransferPublicHolidayRate` 同步成同一个值（11.4）。**若该员工勾选了周末建议值**，再把对应的 `TransferSaturdayRate` / `TransferSundayRate` 改成建议值。
- **没有旧记录（`Create`）**：`PayMode = Transfer`（全转账），`TransferWeekdayRate` = `TransferPublicHolidayRate` = MYOB 值；周六 / 周日价默认**留空**，勾选了建议值才写入。
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

- AccountRight v2：`Payroll/PayrollCategory/Wage` 的 `PayRate`（`RegularRate` / `FixedHourly`）与 `RegularRateMultiplier`；
- 新版 Business API：`HourlyDetails.FixedHourlyRate` 与倍数字段。

注意两种情形：`PayRate = FixedHourly` 的类别是固定时薪、没有倍数，不能乘；以及员工卡的 `Wage.WageCategories` 里必须真的挂着该类别（没挂的类别推送时 MYOB 也不会按它发薪）。**字段名以探针的真实响应为准**（本仓库的 `WageCategoryPayload` 目前只取了 `Uid / Name / WageType`，没取倍数）。

**公假不用这个**：映射里公假类别是 `PUBLIC HOLIDAY (x2.25)` / `(x2)`，但我方规则是公假转账价 = 平日转账价（11.4），所以公假价继续等于平日价，不随 MYOB 倍数走。

**建议值规则（第一期，纯函数 `MyobPayRateSyncRules.SuggestWeekend`）**：

- 取员工 `employmentType`：Casual（我方 `EmployeeType.Temporary`）用「临时工周六 / 周日」映射，其余用全职兼职那一对；没填类型按全职兼职处理（与现有 Timesheet 规划器 `CategoryOf` 一致）。
- 建议值 = `RoundRate(基础价 × 倍数)`；倍数取自**映射类别的结构化字段**，不解析名称。
- **无法推算**（该员工该列显示原因，不可勾选）：类别 `PayRate = FixedHourly`；类别没挂在该员工卡的 `Wage.WageCategories` 上；映射未配置（临时工周末映射是**可选**的，没配则无法推算）；倍数字段缺失或 ≤ 0。
- **与我方现值不同**时在预览里标出对照（我方现价 → 建议值）；我方没有周末价则标「补空」。有旧记录且平日价变了、周末价没变的，标「周末价可能已过时」，方便财务勾选。
- 周六 / 周日两格**各自独立勾选**，只写勾了的格。
- 隐私与平日价一致：只返回我方员工名和价格，不含 MYOB UID / 年薪。

## 4. 后端契约

放在 `Services/Myob/`，风格对齐 `MyobEmployeeSyncService`。全部 `ApiResponse<T>`，JSON camelCase。

```
POST /api/react/v1/myob/pay-rate-sync/preview   Body { connectionGuid, userGuids?, effectiveFrom }
POST /api/react/v1/myob/pay-rate-sync/apply     Body { connectionGuid, effectiveFrom, items:[{ userGuid, hash }] }
```

- **preview**：只读。逐员工返回 `kind`（Create/Fill/Differ/Same/Skip）、我方当前价、MYOB 价、`PayMode`、原因码、`hash`，以及周六 / 周日各一组 `weekend{saturday,sunday}`：`{ ourRate, suggested, canApply, unavailableReason }`。不写库。
- **apply**：前端只传「员工 + 预览时的 `hash` + 勾选的格（`weekday`、`saturday`、`sunday`）」，**价格由服务端重新向 MYOB 取并重新推算**，`hash`（覆盖平日价与两个建议值）不一致该员工失败 `MYOB_RATE_SYNC_VALUE_CHANGED`，防止写入与屏幕上不同的数。只勾周末、不勾平日的情形：仍新增一条记录，平日价取我方现值。
- 写入逐员工调用现有 `CreatePayRateAsync` 的校验与持久化路径（抽成内部方法复用，**不复制校验**），一个员工失败不影响其他人；返回成功 / 失败清单（含原因码）。
- 访问范围沿用 `ResolvePayRateUserAccessAsync`：只能处理当前用户有权限的分店的员工。
- 新增错误码段（`Myob` 现有 5436x 之后，实现时 `rg 5437` 确认不冲突）：`MYOB_RATE_SYNC_NO_PERMISSION`、`..._VALUE_CHANGED`、`..._NOT_HOURLY`、`..._NOT_BOUND`、`..._CARD_MISSING`。

### 4.1 MYOB 读取

`MyobApiClient` 已有 `GetEmployeePayrollDetailsAsync`（`MyobEmployeePayrollRecord`）。需要给该记录**新增只读字段** `PayBasis`、`HourlyRate`、`WageCategoryUids`（员工卡挂的工资类别 UID 列表）、`TerminationDate`（**不要**加 `AnnualSalary`；以上字段路径均已在 2.1 用真实响应证实）。其它调用方（绑定、类型检查、资料同步）不受影响，因为是新增可空字段。

工资类别：`GetWageCategoriesAsync` 返回的 `MyobWageCategoryRecord` 目前只有 `Uid / Name / WageType`，要新增 `PayRate`（RegularRate / FixedHourly）和倍数（字段名以探针为准，见第 2 节第 6 项）。类别映射取自 `MyobPayCategoryService` 现有的读取路径，**不另建映射**；缓存沿用其现有策略，避免每次预览都多打一轮 MYOB（共用进程级 `RateGate`）。

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
- 点开抽屉：① 选择分店 / 公司文件（当前页已选分店，默认只处理该店员工）+ 生效日期；② 预览表：员工 / 我方现价 / MYOB 价 / 类型标签 / 平日价勾选，另有「周六」「周日」两列各显示「我方现值 → 建议值」和各自的勾选框（默认不勾；无法推算的灰显并写原因）；③ 确认写入 → 结果（成功 N、失败 N 及原因）。
- 抽屉布局复用 `MyobSyncDrawer` 的三步结构与样式，不重造。
- 逻辑拆成 `myobPayRateSyncLogic.ts`（纯函数 + `assert` 测试，风格同 `myobSyncLogic.ts`）。
- 文案中英两份：`myobMessages.zh.json` / `myobMessages.en.json`，英文术语遵循「移动端英文术语定案」。

### 6.2 为什么第一期不做「撤销」

- `EmployeePayRate` 的设计是**只新增不改、不删**；撤销就要删行，等于破坏这个不变量，还要判断「那一期有没有被锁定 / 已引用」。
- 同步前已有逐项预览，且 `Remark` 标了来源；写错了的补救办法 = 财务再新增一条更新生效日的记录（和手填错了的处理完全一样）。
- 因此**无迁移、无批次表**，上线只要部署主 API + Web，没有发布顺序坑。

### 6.3 顺手修一个现有问题

没有时薪记录的行，发放方式列渲染成 `attendancePayroll.payMode.undefined`（`RatesTab.tsx:193`，`rate.payMode` 为空时仍拼 key）。改成与 `PayTab.tsx:246` 一致，显示 `attendancePayroll.payMode.none`。

## 7. 测试计划

后端（`BlazorApp.Api.Tests`，**新文件必须手动加到 csproj 的 `<Compile Include>`**，否则静默不跑）：

1. `MyobPayRateSyncRules`：Create / Fill / Differ / Same / Skip(年薪 / ≤0 / 全现金) 各一例；2 位小数比较；`Mixed` 记录只改平日价与公假价、其余字段原样复制。
2. 生效日期：已锁定周期失败；同日已有记录失败；下一个未锁定周期默认值。
3. `hash` 不一致失败；一个失败不影响其他人。
3a. 周末建议值：全职兼职与临时工各用各的映射；倍数 × 基础价后 2 位小数；`FixedHourly` / 类别没挂在卡上 / 映射未配置 / 倍数缺失 → 「无法推算」且 apply 拒绝勾选；只勾周六不勾周日只写周六；只勾周末不勾平日则平日价取我方现值；公假价始终等于平日价。
4. 隐私：响应序列化后不含年薪 / UID。
5. 权限：缺任一权限码 → 403。

前端：`myobPayRateSyncLogic` 的 `assert` 测试；组件目测走「免登录目测 Web 组件」那套造数服务。

**未验证项要在 PR 里如实写**：真实 MYOB 联调（取决于第 2 节探针）、Casual 是否含加成。

## 8. 发布

- 无迁移、无新权限码、不涉及移动端原生包（**PR 标题不加原生包标注**）。
- 顺序：先主 API 后 Web（Web 入口调用新接口，只发 Web 会 404）。
- 本方案文档与「删除探针」放同一个实现 PR；**部署主 API 时预演会出现「删 2 个文件」（探针控制器 + 服务）**，这是预期，不要当成异常。

## 9. 分批

| 批 | 内容 | 风险 |
|---|---|---|
| 0 | 用探针核对真实字段，结论写回本文第 2 节 | 只读 |
| 1 | `MyobEmployeePayrollRecord` 加 `PayBasis / HourlyRate / WageCategoryUids`，工资类别记录加 `PayRate` / 倍数 + `MyobPayRateSyncRules`（含周末建议值）+ preview/apply + 测试 + 删探针 | 中 |
| 2 | 前端抽屉（含周六 / 周日建议值两列）+ 入口 + `payMode.undefined` 修复 | 低 |

（原「批 3 周末建议值」已并入第一期，见 3.4。**批 0 已完成员工卡部分（2.1）；剩余的工资类别倍数字段，要靠批 1 的第一个提交——给探针加只读类别探测并部署主 API——核对后才写倍数解析**，否则倍数解析只有公开文档背书。平日价部分的字段已有真实响应背书，可先开发。）

## 10. 决定与待确认

**已定（用户 2026-11-16）**

1. 新员工的周六 / 周日价：**默认留空**；**周末建议值并进第一期**（基础价 × 映射类别倍数，默认不勾，勾了才写，见 3.4）。
3. 默认生效日期：**下一个未锁定周期起始日**（预览页仍可改）。
4. 新员工默认发放方式：**全转账**。

**待确认**

2. **权限**：「`Attendance.Payroll.Edit` + `Myob.Employee.Bind` 两者都要」是否合适？管理时薪的财务是否通常同时有 `Myob.Employee.Bind`？不合适就改成单独的新权限码。
5. 是否同意**第一期不做撤销**（理由见 6.2）。
6. ~~周末建议值是否并进第一期~~：**已定，并进第一期**（用户 2026-11-16）。
