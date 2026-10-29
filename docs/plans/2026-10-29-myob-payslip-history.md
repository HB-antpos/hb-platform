# MYOB 历史工资单查看（Web「工资单」页签）· 方案

> 状态：**方案已确认（2026-10-29）**。第 10 节问题 1 选 A（先上探针）；其余按第 10 节推荐项执行：新增两个权限码；店长看整张工资单；历史默认 3 个月、最长 24 个月；不做导出；留查看记录；未绑定 / 无法确定分店仅财务可见；抽屉显示我方考勤工时（仅参考）；页签叫「工资单」放第五位。
> 进度：第一批（#1067）已于 2026-10-29 部署主 API，并在 Investment、Australian 两个公司文件上跑过探针（结果见第 1.3 节）。第二批（后端）已写完：翻页改为窗口 + `$skip`、匹配规则、列表 / 明细接口、两个权限码、查看记录表、移除探针；第三批（Web + 我方考勤工时接口）已写完：「工时与工资」页面里新增「工资单」页签（与「工时与工资」「MYOB 对接」并列）、明细抽屉、未归属视图、各种状态，以及点击后才加载的 `GET payslips/hours`；**迁移 20261029.003 与第二、三批都还没部署**。
> 范围：只读。读 MYOB 里已发薪的工资单，按我方分店过滤后在 Web 展示；不写 MYOB。
> 与总方案关系：落实 `2026-10-27-myob-payroll-integration.md` 第 9 / 10 节里「拉取并展示工资单」的 Web 部分；本方案**不含**员工本人在手机端看自己的工资单、不含对账、不含余额。

## 0. 结论速览

| 问题 | 结论 |
|---|---|
| 数据从哪来 | `GET /{cf_uri}/Report/Payroll/EmployeePayrollAdvice`（MYOB 文档里**唯一**能读已发薪工资单的接口）。Payroll 下没有 PayRun / PayrollPayment / PaySlip 资源。 |
| 要重新授权吗 | 不需要。该接口归在 `sme-payroll` 下（文档核对），现有 scope 已含 `sme-payroll`。 |
| 工资单有分店吗 | **没有**。只能从我方数据推：员工绑定 → 发薪公司 → 该周期员工在哪些分店有工时 / 排班。 |
| 权限 | 新增两个码：`Myob.Payslip.ViewManagedStore`（店长，按管辖分店）、`Myob.Payslip.ViewAllStores`（财务 / 管理员）。默认不授予任何角色。 |
| 要建表吗 | 工资单本身**不落库**（进程内存缓存 10 分钟）。只新增一张「查看记录」表（谁在何时看了谁的哪一期，不含金额）+ 两条权限入库迁移。 |
| 发布 | 迁移 → 主 API → Web。无移动端改动、无 OTA、无原生包。 |
| 真实响应已核对 | 2026-10-29 在 Investment、Australian 两个文件上用探针核对：字段与文档一致；`sme-payroll` 够用（200）；日期区间、员工 UID、`$top`、`$skip` 筛选都在服务端生效。**`$top=1000` 到顶后 `NextPageLink` 为空，不能靠它翻页**，须按日期窗口 + `$skip` 取数（第 1.3、3.2 节）。产品形态不变。 |

## 1. 数据来源（只读）

### 1.1 接口

- 路径：`GET https://api.myob.com/accountright/{cf_uri}/Report/Payroll/EmployeePayrollAdvice/`（与现有 `ApiBaseUrl/{businessId}/{资源}` 一致）。
- 请求头沿用现有：`Authorization: Bearer`、`x-myobapi-key`、`x-myobapi-version: v2`。
- 文档（developer.myob.com，Business API v2）列出的字段：

| 字段 | 含义 | 展示？ |
|---|---|---|
| `Employer.CompanyName / ABNOrTFN` | 雇主 | 公司名展示；ABN 展示（公司标识，非个人信息） |
| `Employee.UID / Name / DisplayID` | 员工卡 | 展示姓名、DisplayID；UID 只后端用于匹配绑定 |
| `PayPeriodStartDate / PayPeriodEndDate / PaymentDate` | 周期与支付日 | 展示 |
| `PayFrequency` | Weekly / Fortnightly / TwiceAMonth / Monthly | 展示 |
| `GrossPay / NetPay` | 税前 / 税后 | 展示 |
| `Lines[]`：`PayrollCategory{UID,Name,Type}`、`Hours`、`CalculationRate`、`Amount`、`YearToDate` | 明细行，`Type` ∈ Wage / Deduction / Expense / Tax / Superannuation / Entitlement 等 | 展示（分区块） |
| `AnnualSalary`、`HourlyRate` | 调用时的年薪 / 时薪 | **不展示**（见第 5 节）；明细行里的 `CalculationRate` 展示 |
| `DateOfBirth` | 生日 | **不展示、不缓存** |
| `ChequeNumber` | 支票号 / 参考 | 不展示 |
| `SuperannuationFund{Name}`、`EmploymentClassification{Name}` | 基金名 / 雇佣分类 | 基金名展示；分类不展示 |

- 该报表**没有银行字段**，我方也**不**去读 `Contact/EmployeePaymentDetails`（与资料同步用的银行接口分开）：工资单页完全不接触银行账号。
- 分页：文档写明默认每页 400、最多 1000。**真实情况**：`$top=1000` 能拿到 1000 条，但这时 `NextPageLink` 是空的，数据其实还有更多（`$skip` 能读到更早的 1000 条）。所以**不能沿用 `GetAllPagesAsync` 跟 `NextPageLink` 的做法**，改用日期窗口 + `$skip`（第 3.2 节）。响应根上还有 `Count` 字段，含义未核对（探针只记了字段名没记取值），本方案不依赖它。

### 1.2 其他相关接口（本方案不用，仅说明为什么）

| 接口 | 结论 |
|---|---|
| `Report/PayrollCategorySummary` | 按类别汇总，不是单张工资单，不用 |
| `Payroll/Timesheet/{uid}` | 是工时记录（推送预览已在用），不含已发薪金额 |
| `Contact/EmployeePayrollDetails` | 当前设置与假期累计，不是历史；本方案不拉 |

### 1.3 真实核对结果（2026-10-29，探针只读；对象：Investment、Australian）

**做法**：第一批部署后，用管理员已登录的会话调 `GET api/react/v1/myob/payslip-probe`，每个公司文件发 7 组固定的只读 GET；响应体在服务端客户端方法内消化，只返回字段路径、取值种类、计数、日期范围、类别名称与 Type、MYOB 错误类型名，**没有姓名、金额、员工 UID 取值**。Australian 重跑了一次。原始结构摘要存档在共享笔记目录（不含敏感取值）。

| 核对点 | 结果 |
|---|---|
| scope | `sme-payroll` 够用：全部请求 200，**不需要重新授权**。 |
| 响应形状 | `{ Count, Items, NextPageLink }`；`Items` 里每项字段与官方文档一致，没有文档之外的字段。`Employer.URI`、`EmploymentClassification` 在这两个文件里为 null。 |
| 字段路径 | `Employer{CompanyName,ABNOrTFN,URI}`、`Employee{UID,Name,DisplayID,URI}`、`PayPeriodStartDate/EndDate`、`PaymentDate`、`PayFrequency`、`GrossPay`、`NetPay`、`SuperannuationFund{UID,Name,URI}`、`Lines[]{PayrollCategory{UID,Name,Type,URI},Hours,CalculationRate,Amount,YearToDate}`；**另有 `AnnualSalary`、`HourlyRate`、`DateOfBirth`、`ChequeNumber` 真实存在**（即第 5 节要丢弃的敏感字段，响应里确实带着）。`Lines[].Hours / CalculationRate / Amount` 可为 null（如税行）。日期是字符串（ISO 格式）。 |
| `Lines[].PayrollCategory.Type` 取值 | 最新 1000 条里只出现 `Wage`、`Entitlement`、`Superannuation`、`Tax`；没有出现 `Deduction`、`Expense`（更早的历史里可能有，实现时按未知类型兜底显示在「其他」区块）。 |
| 类别名称 | Investment 13 个、Australian 18 个，都是公司工资类别名（如 `Base Hourly`、`SATURDAY (x1.25)`、`PUBLIC HOLIDAY (x2)`、`Annual Leave Accrual`、`PAYG Withholding`、`Superannuation Guarantee`），不含个人信息；两个公司的命名不完全一致（Australian 另有 `Base Salary`、`Bonus`、`Pay in lieu of notice`，且有拼写 `SATUDAY (x 1.2)`），**展示时按名称原样显示，不能按名称做业务判断**。 |
| 日期区间筛选 | **服务端生效**：`PaymentDate ge datetime'yyyy-MM-dd' and PaymentDate lt datetime'yyyy-MM-dd'`，近 45 天窗口 Investment 66 张 / 11 人、Australian 81 张 / 15 人，返回的支付日期全部落在窗口内。 |
| 仅下界筛选 | `PaymentDate ge datetime'…'` 在 Investment 200；Australian 第一次返回 **500**，重跑 200，判断为 MYOB **偶发 5xx**，实现时对 5xx 做一次重试。 |
| 员工筛选 | **服务端生效**：`Employee/UID eq guid'…'` 返回的全部记录只属于这一名员工。 |
| `$top` | 生效（`$top=5` 返回 5 条，`$top=1000` 返回 1000 条）。 |
| `$skip` | **生效**：探针的 `skipWorks=false` 是探针自己的判断错了（把「全量恰好 1000 条」当成总数，去跳 998 条），实际读到的是更早的 1000 条（Investment 2023-02-27 ~ 2024-11-04，Australian 2021-11-28 ~ 2024-07-15），并与前一页的最后一条日期衔接——证明跳过生效。 |
| 默认顺序 | 观测为**支付日期倒序（最新在前）**，但不是文档保证；实现里一律在内存里重新排序，不依赖服务端顺序。 |
| `$orderby=PaymentDate desc` | 被接受（200），但返回的 3 条同一天，**无法证明排序生效**，本方案不依赖它。 |
| 数据量 | 每个公司文件的历史工资单**远多于 1000 张**（`$skip` 能读到 2021~2023 年的）；最新 1000 张大约覆盖 2 年多（Investment 2024-11 起、Australian 2024-07 起），最新 1000 张里分别有 26 / 39 名员工。 |
| **翻页陷阱** | `$top=1000` 返回满 1000 条时，`NextPageLink` **为空**。若沿用现有 `GetAllPagesAsync`（只跟 `NextPageLink`），会**悄悄漏掉 1000 条以外的数据**。必须按第 3.2 节翻页。 |
| 耗时 | 5 条约 0.9~1.5 秒；66~81 条窗口约 1.8~2.9 秒；1000 条约 2.3~5.1 秒（单次请求）。整套探针 15~17 秒。 |
| 没核对到的 | ① `Count` 的含义；② `Deduction` / `Expense` 类型的真实样子；③ 已撤销 / 重发的工资单怎么出现（最新 1000 条里没有观察，不排除有）；④ 另外 4 个已连接公司文件（Kawana、Orion、Peninsula Fair、Robinson Road）没有跑，本功能第一版只对已知结构的 Investment、Australian 验证，其它文件上线前再各跑一次探针；⑤ 单张工资单上的 `PayFrequency` 取值（探针不记取值）。 |

核对完成后，探针控制器与服务应整体删除（第 8 节第 5 步），在第二批实现提交里一并移除。

## 2. 工资单 ↔ 员工 ↔ 分店

### 2.1 已有关系

| 关系 | 位置 |
|---|---|
| 员工 ↔ MYOB 员工卡 | `EmployeeMyobLink(UserGuid, ConnectionGuid, MyobEmployeeUid, UnboundAt)`：每个公司文件各一条 |
| 分店 → 默认发薪公司 | `MyobStoreMapping(StoreCode → ConnectionGuid)` |
| 员工 × 分店 → 发薪公司覆盖 | `EmployeePayrollEmployer`（带 `EffectiveFrom`），`MyobPayrollEmployerRules.Resolve(默认公司, 覆盖行, 日期)` |
| 员工 → 分店 | `UserStore`（现在状态，软删除后丢历史） |
| 某周期员工在哪些分店有数据 | `IAttendancePayrollPushSource.FindDataPairsAsync(userGuids, periodStart, weeks)`：返回 (员工, 分店) 对（有排班 / 请假 / 带薪公假） |

### 2.2 匹配规则（每张工资单算一次，纯函数，便于单测）

输入：一张工资单 `(ConnectionGuid, Employee.UID, PayPeriodStart..End, PaymentDate)`。

1. **找员工**：`EmployeeMyobLink` 中 `ConnectionGuid` + `MyobEmployeeUid` 匹配（含已解绑的历史绑定，否则解绑后历史工资单会消失）→ `UserGuid`。找不到 → 归入「**未绑定**」。
2. **找分店**（有序回退）：
   1. 该周期（`PayPeriodStart..End`）内，`FindDataPairsAsync` 返回的该员工分店集合 S₁；
   2. 再过滤：只保留「**按 `PaymentDate` 当天 `Resolve` 出来的发薪公司 = 本工资单所在公司**」的分店（用历史生效日，而不是今天）；
   3. 若 S₁ 过滤后为空（该周期没打卡数据，例如手工补发 / 纯带薪假）→ 回退到该员工**当前** `UserStore` 里发薪公司 = 本公司的分店，行上标「按当前分店推断」；
   4. 仍为空 → 归入「**无法确定分店**」。
3. **分店过滤**：选中分店 X → 保留「分店集合包含 X」的工资单。

### 2.3 特殊情况

| 情况 | 处理 |
|---|---|
| 员工在 A 店（Investment）、B 店（Australian）两店工作 | 绑定两条链接，每个公司文件各一张工资单。选 A 店只看到 Investment 那张；选 B 店只看到 Australian 那张；选「全部分店」两张都在。工资单行标注发薪公司。 |
| 员工在 A、B 两店，且都由同一公司发薪 | **一张**工资单，同时出现在 A 店和 B 店的过滤结果里；**不拆分金额**（MYOB 里是一张合并的单，我方不重算）。抽屉里用我方考勤给出「本期工时：A 店 38h / B 店 12h」仅作参考。 |
| 员工中途换发薪公司（覆盖 `EffectiveFrom`） | 用 `PaymentDate` 当天的覆盖规则判断，老工资单仍按当时的公司归属。 |
| 员工已离职 / 账号停用 | 照常能查（走绑定链接 + 历史考勤）；现有 `MyobWorkforceLoader` 只加载在职账号，**本功能不复用它的用户过滤**，另做一个包含停用账号的轻量查询。 |
| MYOB 卡没有绑定 | 只有 `ViewAllStores` 能看到，放在「未绑定」分组，行上给「去绑定」按钮；店长看不到。 |
| 绑定名单里「忽略」的卡（`MyobEmployeeIgnore`） | 不展示。 |
| 过滤用的分店下拉 | 管理员 / `ViewAllStores`：所有分店 + 「未绑定 / 无法确定」；店长：仅自己管辖分店。 |

## 3. 后端

### 3.1 接口（均在 `api/react/v1/myob/payslips` 下，统一 `ApiResponse<T>`）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `companies` | 当前账号可见的公司文件下拉（只列状态 Active 的连接；店长只列自己管辖分店涉及的公司） |
| GET | `?connectionGuid=&storeCode=&from=&to=&keyword=&page=&pageSize=` | 列表；`from/to` 按 **PaymentDate**；`connectionGuid` 为空 = 所有可见公司；返回每家公司各自的读取状态（部分失败时不拖垮其它公司） |
| GET | `detail?connectionGuid=&employeeUid=&paymentDate=&periodStart=` | 单张工资单明细（抽屉）。参数走查询串而不是路径（日期与 UID 都做了格式校验）；`periodStart` 可空，同一支付日有多张时用它区分。先写一条查看记录再返回内容，记不下就拒绝 |

> 探针接口与服务已随第二批移除。实现中的差异：列表参数里的 `onlyUnassigned=true` 用来只看「未绑定 / 无法确定分店」的工资单（仅全部分店权限）；越权与不存在统一返回 404，不暴露这张工资单是否存在。

- `keyword`：员工姓名 / MYOB DisplayID（后端内存过滤，不透传给 MYOB）。
- 列表默认：最近 3 个月、所有可见公司；单次区间上限 24 个月。财年快捷项按澳洲财年（7 月 1 日–次年 6 月 30 日）。
- **鉴权（每个接口都做，不只靠前端隐藏）**：
  1. `[Authorize]` 要求任一新权限码（自定义 policy，任一满足即可，参考 `SeasonalCards.Remaining` 的写法）；
  2. `ViewAllStores` → 全部；仅 `ViewManagedStore` → 经 `ICurrentUserManageableStoreScopeService.GetScopeAsync()` 取 `StoreCodes`，只返回「分店集合与管辖分店有交集」的员工；`IsAllowed=false` 直接 403；
  3. 抽屉接口同样重新计算该员工分店集合再校验，防止直接猜 URL。
- 店长看到「员工有交集分店」的**整张**工资单（包含其它分店挣的钱）这一点是个隐私取舍，见第 10 节问题 3。

### 3.2 MYOB 客户端

- `IMyobApiClient.GetPayrollAdvicesAsync` 已存在（第一批），**但现在的实现跟 `NextPageLink` 翻页，会漏掉 1000 条以外的数据（第 1.3 节「翻页陷阱」），第二批必须改**：
  1. 把请求区间 `[from, to)` 按**最多 90 天**切成若干窗口（默认 3 个月 = 1~2 个窗口；24 个月上限 ≈ 8 个窗口）；每个窗口一次 `$top=1000&$filter=PaymentDate ge … and PaymentDate lt …`。
  2. 某窗口返回**恰好 1000 条**（到顶）→ 在这个窗口内继续 `$skip=1000、2000…`，直到返回不足 1000 条；最多翻 20 页兜底，超过就报错而不是静默截断。
  3. 不跟 `NextPageLink`（服务端到顶后它是空的）；探针里的 `Count` 也不依赖。
  4. 各窗口并发请求（`RateGate` 只约束相邻请求的发起间隔 130ms，不串行化整个请求，所以并发是安全的），合并后在内存里按 `PaymentDate` 倒序、员工姓名重新排序，不依赖服务端顺序；按「员工 UID + 支付日期 + 周期起始日」去重（窗口边界与 `$skip` 翻页期间有新工资单插入时，防止重复）。
  5. 单次请求遇到 **5xx 重试 1 次**（Australian 的 `dateGe` 请求出现过偶发 500，重跑正常）；4xx 与再次失败按 `MyobApiException`（只含状态码）抛出。
- 员工筛选用 `Employee/UID eq guid'…'`（服务端生效），用于抽屉里「该员工同一周期的其它工资单」之类的按人查询；列表页不用，列表是先取区间再在内存里按员工 / 分店过滤（分店本来就要靠我方数据推，MYOB 端无法筛）。
- 复用 `SendAsync` 的进程级 `RateGate`（`MinRequestIntervalMs=130ms`，≈8 次/秒的上限留余量），与员工名册 / 工时推送共用同一个闸。
- 只对 **GET** 开放；本功能的所有调用不会进入任何 PUT / POST 路径。
- 错误只带状态码，沿用 `MyobApiException`；`401`→提示 scope / 授权问题，`403`→公司文件无权限，`invalid_grant`→连接标 NeedsReauth（现有 token provider 已处理），`429/5xx`→「MYOB 暂时不可用，请稍后重试」。**绝不**回显 MYOB 响应体（含薪资）。
- 载荷类型已丢弃 `AnnualSalary / HourlyRate / DateOfBirth / ChequeNumber / EmploymentClassification`；第 1.3 节确认这些字段在真实响应里确实存在，所以这条隐私边界是实际起作用的，测试里有断言。

### 3.3 缓存与限流

- 缓存：`IMemoryCache`，键 `myob:advice:{connectionGuid}:{区间起}:{区间止}`（**实现按「整个查询区间」缓存，窗口切分在客户端内部做**；分店、关键字等过滤不影响缓存键），TTL 10 分钟（与 `MyobEmployeeDirectory.CacheLifetime` 同口径）；只放进程内存，不落盘不落库；「刷新」按钮 `refresh=true` 绕过缓存。按窗口缓存、历史窗口放宽到 60 分钟留作以后的优化。
- 同一公司文件的并发请求串行（锁按公司文件，不按查询键，避免键里带日期导致字典无限增长），同键的后到请求直接用先到请求的结果，只打一轮 MYOB。明细接口不走缓存。
- 缓存只存**展示用精简对象**（已丢弃 DOB / 年薪 / 时薪 / 支票号），避免敏感字段长期驻留内存。
- 估算量（按真实数据）：近 45 天 Investment 66 张、Australian 81 张，即每个公司每周约 10~13 张；3 个月窗口约 100~190 张，1 次请求约 2~3 秒；24 个月约 1,000~1,400 张，8 个窗口并发约 5~8 秒（单窗口耗时 + 130ms 间隔）。双公司并发。内存：每张工资单约十几行明细，24 个月一个公司几 MB，可接受。
- 不做后台定时拉取（本阶段是「查看」，不是对账）；日后做对账再引入快照表。

### 3.4 查看记录

- 新表 `MyobPayslipViewLog(LogGuid, ActorUserGuid, ConnectionGuid, TargetUserGuid, MyobEmployeeUid, PaymentDate, ViewedAt)`（迁移 `20261029.003-myob-payslip`，同一迁移里幂等写入两个权限码、不授予任何角色），**只记谁看了谁的哪一期**，不记金额与姓名文本。`TargetUserGuid` 在员工卡未绑定时为空，所以多记一列 `MyobEmployeeUid`（员工卡 UID，不是姓名），保证未绑定员工的查看也能追溯。仅抽屉打开时写；列表不写；**写不进去就拒绝显示工资单**。
- 用途：员工问「谁看过我的工资单」时可查；Web 不提供查询页面（先不做），需要时由管理员查库。

## 4. 权限

| 权限码 | 谁 | 范围 |
|---|---|---|
| `Myob.Payslip.ViewAllStores` | 财务、管理员 | 所有分店、所有公司文件，含「未绑定」员工 |
| `Myob.Payslip.ViewManagedStore` | 店长 | 只看与自己管辖分店有交集的员工；不含未绑定 |

- 为什么不沿用现有码：`Myob.Connection.Manage` 管连接与映射、`Attendance.Payroll.Edit` 管工时推送，都不代表「可以看每个人的实发工资」；而且 `Attendance.Payroll.Edit` 会被授给店长，直接沿用会让店长默认看到工资。新码默认不授予任何角色，由管理员在生产手动授权。
- 码名与已有的 `SeasonalCards.Remaining.ViewManagedStore / ViewAllStores` 一致。
- 落地清单（新权限码约束）：`Permissions.cs` 常量、`PermissionSurfaceCatalog`、迁移入库（`schema-migrate` 显式执行）、移动端 `accessPermissions.json` 文案（即使移动端没有入口，文件需与种子一致）、Web 访问判断 `getVisibleMyobTabs` 新增 `payslips` 页签、`NavigationService` 菜单无需新增（复用 `/pos-admin/myob`，但页面入口权限要把新码算进去）。
- 员工本人不通过本功能看工资单（总方案阶段 4 的移动端「我的工资单」另做）。

## 5. 隐私：展示 / 不展示 / 日志

| 项目 | 列表 | 抽屉 | 缓存 | 日志 |
|---|---|---|---|---|
| 员工姓名 | ✔ | ✔ | ✔ | ✘ |
| DisplayID | ✔ | ✔ | ✔ | ✘ |
| 支付日期 / 周期 / 频率 | ✔ | ✔ | ✔ | ✘（仅计数） |
| 税前 / 税 / 实发 | ✔（Gross、Net） | ✔（全部） | ✔ | ✘ |
| 明细行（类别、小时、费率、金额、YTD） | ✘ | ✔ | ✔ | ✘ |
| 退休金金额与基金名 | ✘ | ✔ | ✔ | ✘ |
| 年薪 `AnnualSalary`、时薪 `HourlyRate` | ✘ | ✘ | ✘（丢弃） | ✘ |
| 生日、支票号、雇佣分类 | ✘ | ✘ | ✘（丢弃） | ✘ |
| 银行 BSB / 账号、TFN | **不读取** | **不读取** | — | ✘ |
| 员工 UID / 连接 GUID | 不出现在页面 | 路径参数里有，不显示 | — | 连接 GUID 可记 |

- 日志里只允许：接口、连接 GUID、区间、返回条数、MYOB 状态码、耗时、操作者 GUID。**不得出现**姓名、金额、MYOB 响应体、令牌。异常消息沿用 `MyobApiException` 的「只含状态码」约束。
- 浏览器：响应头 `Cache-Control: no-store`；前端不写 localStorage / sessionStorage；不提供「导出」（见第 10 节问题 5）；抽屉页脚固定声明「仅供查看，以 MYOB 发出的工资单为准」。
- 测试夹具、截图、设计稿一律用虚构姓名与金额。

## 6. 前端

### 6.1 入口

- **位置（2026-10-29 按用户要求调整）**：放在「工时与工资」页面里，作为页面组顶部页签 `工时与工资 | 工资单 | MYOB 对接` 的第二个，路由 `/pos-admin/payslips`、访问键 `canViewMyobPayslips`（`Myob.Payslip.ViewManagedStore` / `ViewAllStores` 任一）。不再是 MYOB 对接页的第五个页签，所以 MYOB 页的页签逻辑不用改；工资单权限不连带 MYOB 对接页，MYOB 权限也不连带工资单。
- 只有工资单权限的账号登录后落到工资单页；同时有 MYOB 权限的沿用原落点（MYOB 页）。不保活（离开页签即丢掉内存里的工资数据）。
- 新文件（`pages/PosAdmin/Payslips/`）：`index.tsx`（列表 + 筛选 + 状态）、`PayslipDrawer.tsx`（明细 + 我方工时按需加载）、`payslipLogic.ts`（日期预设 / 澳洲财年、金额与周期格式化、错误归类、分店下拉；配 `.test.ts`）、`payslipMessages.zh/en.json`、`payslips.css`；`services/myobPayslipService.ts`、`types/myobPayslip.ts`；全局语言包只加菜单标题 `menu.payslips`。
- 「最近一个周期」预设改为「最近两周」：MYOB 有周薪也有双周薪，无法统一成一个周期。
- 「去绑定」「查看发薪公司」目前只跳到 `/pos-admin/myob`（落到该账号第一个可见页签），不带预选公司。
- 手机端：本期不做。

### 6.2 列表页

- 筛选条：发薪公司（默认「全部公司」）、分店（带「未绑定 / 无法确定」两个特殊项，仅 `ViewAllStores` 可见）、支付日期区间（预设：最近一个周期 / 近 3 个月 / 本财年 / 上财年 / 自定义）、员工搜索、刷新。
- 表格列：员工（姓名 + DisplayID）、分店（标签；「按当前分店推断」加虚线）、发薪公司、支付日期、工资周期、税前、PAYG、实发。行点击打开抽屉。
- 汇总条：当前筛选下的张数 / 税前合计 / 实发合计。
- 默认排序支付日期倒序；分页前端分页（数据已在内存）。

### 6.3 抽屉

- 页首：员工 + 公司 + 周期 + 频率；两个大数：税前 / 实发。
- 区块：Wages（描述 / 小时 / 费率 / 本期 / YTD）、Deductions & Taxes、Superannuation（含基金名）、Leave accrual（Entitlement 行，小时）。英文用词保持 MYOB 原文（与总方案一致）。
- 「分店归属」卡：命中的分店 + 本期我方工时（来自考勤，仅供参考），说明「工资单按公司发放，不分分店」。
- 页脚：声明 + 「已记录本次查看」。

### 6.4 状态

- 空状态：筛选无结果（给「清除筛选」）；该公司该区间 MYOB 没有已发薪记录。
- 无权限：整页（没有任何新权限码）；以及单店越权（接口 403）。
- MYOB 读取失败：按错误类型分文案（需要重新授权 → 引导去「连接与映射」；无权限；暂时不可用 → 重试）；多公司时**逐公司**显示状态，一家失败不遮住另一家的数据。

## 7. 验证办法

1. **纯函数单测**：分店匹配（含同公司多店、跨公司、历史覆盖生效日、回退到当前分店、未绑定、已解绑历史链接）；区间 / 财年预设；店长范围裁剪。
2. **服务测试**：用假 `IMyobApiClient` / 假 HTTP 返回固定 JSON（**窗口恰好 1000 条且 `NextPageLink` 为空 → 必须继续 `$skip` 翻页**、窗口切分与去重、空体、404、401、5xx 重试 1 次）；断言缓存命中不重复调用、`refresh` 会重拉、并发只打一轮、日志与异常消息不含金额 / 姓名。
3. **控制器鉴权测试**：无权限 403；店长查别人分店员工 403 / 空；`ViewAllStores` 看到未绑定；抽屉越权。
4. **前端**：`myobPayslipLogic.test.ts`、`normalize*` 对缺字段 / 大小写变体不崩；用现有「免登录目测 Web 组件」办法（esbuild + 造数服务）目测列表 / 抽屉 / 各状态。
5. **真实核对（生产只读）**：先跑第 1.3 节探针；上线后选 1–2 名已绑定员工，把 Web 抽屉里的税前 / 税 / 实发与 MYOB 官方 Pay Slip 对一遍（用户自己核对，我不碰他人真实薪资数据做截图）。
6. 门禁：后端相关测试、`tsc`、web lint、web 首屏包体预算（新页签 lazy 加载，文案放页面级，避免撑破首屏预算）。
7. 回滚：只读功能、无结构性改动；回滚 = 回滚主 API + Web；新表与权限行留着无害。

## 8. 实施顺序（确认方案后）

| 步 | 内容 | 状态 / 发布 |
|---|---|---|
| 1 | `GetPayrollAdvicesAsync` + 探针接口（#1067） | **已完成并部署主 API（2026-10-29，无迁移）** |
| 2 | 跑探针，核对第 1.3 节；修订本方案第 1、3 节 | **已完成（Investment、Australian）** |
| 3 | 第二批（后端）：`GetPayrollAdvicesAsync` 改窗口 + `$skip` + 5xx 重试 + 去重；匹配规则；列表 / 明细接口；迁移 `20261029.003-myob-payslip`（两个权限码入库、`MyobPayslipViewLog` 表）；去掉探针 + 测试 | **已写完，待部署**：迁移 → 主 API |
| 4 | Web 页签 + 抽屉 + 状态 + 我方考勤工时接口（第三批） | **已写完，待部署**：迁移 → 主 API → Web |
| 5 | 管理员授权角色；上线前对其余 4 个公司文件各跑一次结构核对；抽查核对 | — |

## 9. 范围外

员工本人手机端工资单、PDF 导出、对账（金额 / 费率）、假期余额、定时拉取与快照落库、写 MYOB 的任何操作。

## 10. 需要用户拍板的问题

1. 第 1.3 节核对方式：A（推荐，先上探针接口）还是 B（你在服务器上一次性只读调用）？
2. 权限：同意新增 `Myob.Payslip.ViewManagedStore` / `Myob.Payslip.ViewAllStores`？还是你希望店长一律不能看（只给财务 / 管理员）？
3. 店长看到的范围：员工在多家分店工作时，店长是否能看到**整张**工资单（含别店挣的钱）？可选：A 看整张（推荐，MYOB 单本来就是一张，拆不了）；B 只看税前 / 实发两个数；C 只有「员工所有分店都在自己管辖内」才可见。
4. 可查历史多久？默认近 3 个月、最长 24 个月；是否要限制得更短（例如只给财务看超过 12 个月的）？
5. 是否允许导出（CSV / PDF）？推荐本期不做，减少薪资外流面。
6. 是否需要「查看记录」表，还是不留痕？推荐留（只记谁看了谁哪期，不含金额）。
7. 「未绑定」「无法确定分店」的工资单是否只让财务看？推荐是。
8. 同一员工同公司多店时，抽屉里是否显示我方考勤分店工时（仅参考）？推荐显示。
9. 页签名称：「工资单」，放在 MYOB 页面第五个页签，可以吗？

## 11. 修订（2026-10-29 晚）：MYOB 默认样式 + 下载 PDF + 邮件发送 + 手机端查看

> 用户给了一张真实的 MYOB 默认工资单 PDF，按它还原；本节**覆盖**第 5、6.3、9、10（问题 5）节里与导出、手机端相冲突的部分。

- **版式**：MYOB 默认 Pay Slip 是「一张通栏表 + TYPE 列」，不是分区块。页头含 ABN、Pay Slip For、Reference Number、Payment Date、Hourly Rate、Pay Period From/To、Superannuation Fund、GROSS PAY / NET PAY、Memo/payslip message；日期 d/M/yyyy 不补零；右下 Page x of y。Web 抽屉、PDF、邮件附件是同一份渲染逻辑（Web / 手机端纸面组件与后端 `MyobPayslipPdfRenderer` 对照同一设计稿）。
- **字段放开**：`HourlyRate`、`ChequeNumber`（页头的 Reference Number）不再丢弃，进 `MyobPayrollAdviceRecord`（会随 10 分钟缓存驻留内存）；只出现在明细 DTO 与 PDF，列表 DTO 不含，不写日志。年薪、生日、雇佣分类仍然丢弃。
- **Memo**：报表接口没有对应字段，PDF 里保留标签与空行。**Pay Slip For**：接口只给整段姓名，原样显示（MYOB 原版是 “Surname, GIVEN”）。**Entitlement 行**的小时数放 HOURS 列（真实原版未见，待用带假期累计的真实 PDF 核对）。
- **接口**（`api/react/v1/myob/payslips`）：`GET pdf`（文件流）、`GET email-target`（档案邮箱 / 主题 / 文件名 / 当前登录人邮箱）、`POST email`；`GET detail` 新增 `hourlyRate`、`referenceNumber`、扁平 `lines`（含 `typeLabel`）、`canDownloadPdf`、`canSendEmail`。
- **权限**：新增 `Myob.Payslip.Download`、`Myob.Payslip.Email`（默认不授予任何角色；须同时具备原有两个查看码之一）。两个码在 `PermissionSurfaceCatalog` 登记为 Web + 手机端；原有两个查看码也从「仅 Web」改成 Web + 手机端。
- **留痕**：`MyobPayslipViewLog` 增加 `Action`（View / Download / Email / EmailFailed）与 `Recipient`（邮件收件邮箱）；邮件先写记录再发送，失败改成 EmailFailed。迁移 `20261029.004-myob-payslip-export`（加两列 + 写两个权限码），**必须先于新版主 API 部署**。
- **邮件**：复用发票邮箱后台的公司 SMTP（同监护人邮件做法）；正文中英双语、不含金额；PDF **不加密码**（用户已定）；每次一个员工、一个收件人；档案邮箱存在且收件人不同时须确认，档案没有邮箱时手填只用于本次。
- **手机端**：新增「工资单 Payslips」页面（人员管理分组，路由 `payslips`），列表 + 详情（MYOB 样式）+ 下载 PDF（系统分享）+ 发邮件（底部弹层）；只做管理者查看，员工看自己的工资单仍不做。纯 JS，不涉及原生包。
- **发布顺序**：迁移 `20261029.004` → 主 API → Web → 手机端 OTA（用户之前说「下次一起发」）。
