# MYOB 历史工资单查看（Web「工资单」页签）· 方案

> 状态：**方案已确认（2026-10-29）**。第 10 节问题 1 选 A（先上探针）；其余按第 10 节推荐项执行：新增两个权限码；店长看整张工资单；历史默认 3 个月、最长 24 个月；不做导出；留查看记录；未绑定 / 无法确定分店仅财务可见；抽屉显示我方考勤工时（仅参考）；页签叫「工资单」放第五位。
> 进度：第一个提交只含 `GetPayrollAdvicesAsync` + 只读探针接口（见第 1.3 节、第 8 节第 1 步）；**未部署**，探针结果出来前不写匹配规则与页面。
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
| **最大未知** | `EmployeePayrollAdvice` 的**真实响应与筛选写法尚未用已授权公司文件核对**（见第 1.3 节）。动手写代码的第一步必须是只读探针，探针结果可能改动第 3 节的拉取策略，但不改变产品形态。 |

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
- 分页：文档写明默认每页 400、最多 1000；沿用现有 `GetAllPagesAsync` 的 `$top=1000` + `NextPageLink`（只跟随同主机，最多 50 页）。

### 1.2 其他相关接口（本方案不用，仅说明为什么）

| 接口 | 结论 |
|---|---|
| `Report/PayrollCategorySummary` | 按类别汇总，不是单张工资单，不用 |
| `Payroll/Timesheet/{uid}` | 是工时记录（推送预览已在用），不含已发薪金额 |
| `Contact/EmployeePayrollDetails` | 当前设置与假期累计，不是历史；本方案不拉 |

### 1.3 必须在写代码前用真实文件核对的点（**目前没有核对，不能当事实**）

文档页面没有列出任何查询参数，且不说明 scope。我们的 MYOB 读取是只读，所以核对只用 GET，对象是已授权的两个公司文件（Hot Bargain Investment Pty Ltd、Hot Bargain Australian Pty Ltd）：

1. 不带参数 GET 一次：看真实字段名大小写、`Lines` 的 `Type` 全部取值、`Items` / `NextPageLink` 结构、是否返回历史所有期还是仅最近若干期。
2. 日期筛选：试 `?$filter=PaymentDate ge datetime'2026-07-01' and PaymentDate lt datetime'2026-08-01'`（AccountRight OData 写法；社区有按支付日期筛选的说法，但官方页面没写）。
3. 员工筛选：试 `?$filter=Employee/UID eq guid'…'`。
4. `$top`、响应大小、单次耗时、一年大约多少张。
5. 用 `sme-payroll` 现有 token 是否 200（预期 200，若 401/403 才需要补 scope 重新授权）。
6. 两个公司文件的 `Employer`、`Lines` 命名是否一致；已发薪后被「撤销 / 重发」的工资单如何出现（是否多一条负数记录）。

**怎么做**：访问令牌只在服务器端经 `IMyobTokenProvider` 取得（刷新令牌会轮换并落库，不能拿到本机去直接调）。需要用户同意以下二选一（见第 10 节问题 1）：
- **已选 A**：第一个提交只加 `IMyobApiClient.GetPayrollAdvicesAsync` + `GET api/react/v1/myob/payslip-probe?connectionGuid=`（仅 `Myob.Connection.Manage`，只读，响应 `Cache-Control: no-store`）。
- 探针依次发 7 组固定的只读 GET：无筛选 `$top=5`、无筛选翻页全量（≤12 页）、日期下界、日期区间、按员工 `guid'…'`、`$skip` 跳到末尾、`$orderby=PaymentDate desc`；响应体在客户端方法内部消化，只返回**字段路径与取值种类、计数、日期范围、类别名称与 Type 取值、MYOB 错误类型名**，不含任何姓名、金额、员工 UID 取值。
- 同时给出 5 个结论位（日期筛选 / 员工筛选 / `$top` / `$skip` / `$orderby`）：true 已证实、false 已证实不可用、null 数据不足不猜。
- 核对完成、结论写回本节后，探针控制器与服务整体删除。

**探针结果若与文档不符的退路**：不支持服务端筛选 → 按公司整段拉全量（一年约几千张、数 MB），内存里按日期 / 员工过滤并缓存；响应没有 `Items` 包装 → 在客户端层适配，不影响上层。

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
| GET | `{connectionGuid}/{employeeUid}/{paymentDate}` | 单张工资单明细（抽屉）。写一条查看记录 |
| GET | `api/react/v1/myob/payslip-probe?connectionGuid=`（临时，核对完删除；路径不在 `payslips` 下） | 第 1.3 节的结构探针，仅 `Myob.Connection.Manage` |

- `keyword`：员工姓名 / MYOB DisplayID（后端内存过滤，不透传给 MYOB）。
- 列表默认：最近 3 个月、所有可见公司；单次区间上限 24 个月。财年快捷项按澳洲财年（7 月 1 日–次年 6 月 30 日）。
- **鉴权（每个接口都做，不只靠前端隐藏）**：
  1. `[Authorize]` 要求任一新权限码（自定义 policy，任一满足即可，参考 `SeasonalCards.Remaining` 的写法）；
  2. `ViewAllStores` → 全部；仅 `ViewManagedStore` → 经 `ICurrentUserManageableStoreScopeService.GetScopeAsync()` 取 `StoreCodes`，只返回「分店集合与管辖分店有交集」的员工；`IsAllowed=false` 直接 403；
  3. 抽屉接口同样重新计算该员工分店集合再校验，防止直接猜 URL。
- 店长看到「员工有交集分店」的**整张**工资单（包含其它分店挣的钱）这一点是个隐私取舍，见第 10 节问题 3。

### 3.2 MYOB 客户端

- `IMyobApiClient` 新增 `GetPayrollAdvicesAsync(businessId, accessToken, MyobAdviceQuery, ct)`；`GetAllPagesAsync` 增加可选 `query` 参数（现在只拼 `?$top=1000`）。
- 复用 `SendAsync` 的进程级 `RateGate`（`MinRequestIntervalMs=130ms`，≈8 次/秒的上限留余量），与员工名册 / 工时推送共用同一个闸，所以并发使用不会超 MYOB 限额。
- 只对 **GET** 开放；本功能的所有调用不会进入任何 PUT / POST 路径。
- 错误只带状态码，沿用 `MyobApiException`；`401`→提示 scope / 授权问题，`403`→公司文件无权限，`invalid_grant`→连接标 NeedsReauth（现有 token provider 已处理），`429/5xx`→「MYOB 暂时不可用，请稍后重试」。**绝不**回显 MYOB 响应体（含薪资）。

### 3.3 缓存与限流

- 缓存：`IMemoryCache`，键 `myob:advice:{connectionGuid}:{from}:{to}`，TTL 10 分钟（与 `MyobEmployeeDirectory.CacheLifetime` 同口径）；只放进程内存，不落盘不落库；「刷新」按钮 `refresh=true` 绕过缓存。
- 同一连接的并发请求串行（同 `MyobEmployeeDirectory` 的 `Locks`），只打一轮 MYOB。
- 缓存只存**展示用精简对象**（已丢弃 DOB / 年薪 / 支票号），避免敏感字段长期驻留内存。
- 估算量：员工约百余人，双周发薪，一年每公司约几千张；`$top=1000` 一年 ≈ 4–6 次请求，约 1 秒内（受 130ms 间隔限制）。24 个月上限最坏约 12 次请求。
- 不做后台定时拉取（本阶段是「查看」，不是对账）；日后做对账再引入快照表。

### 3.4 查看记录

- 新表 `MyobPayslipViewLog(LogGuid, ActorUserGuid, ConnectionGuid, TargetUserGuid, PaymentDate, ViewedAt)`，**只记谁看了谁的哪一期**，不记金额与姓名文本。仅抽屉打开时写；列表不写。
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

- `/pos-admin/myob` 新增第五个页签「**工资单**」（key `payslips`），排在「推送与对账」后；`getVisibleMyobTabs` 里按新权限码显示。理由：与员工绑定、发薪公司同一页面，「未绑定」行可直接跳到「员工绑定」；比新开独立页面少一个菜单入口。
- 新文件：`PayslipsTab.tsx`（列表 + 筛选）、`PayslipDrawer.tsx`（明细）、`myobPayslipLogic.ts`（区间预设、财年、过滤、分组纯函数，配 `.test.ts`）、`myobService.ts` 加 `normalizeMyobPayslip*` 与请求函数、`myobMessages.zh/en.json` 加 `payslips` 节、`myob.css` 加 `myob-payslip-*`。
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
2. **服务测试**：用假 `IMyobApiClient` 返回固定 JSON（Items + NextPageLink 两页、空体、404、401、5xx）；断言缓存命中不重复调用、`refresh` 会重拉、并发只打一轮、日志与异常消息不含金额 / 姓名。
3. **控制器鉴权测试**：无权限 403；店长查别人分店员工 403 / 空；`ViewAllStores` 看到未绑定；抽屉越权。
4. **前端**：`myobPayslipLogic.test.ts`、`normalize*` 对缺字段 / 大小写变体不崩；用现有「免登录目测 Web 组件」办法（esbuild + 造数服务）目测列表 / 抽屉 / 各状态。
5. **真实核对（生产只读）**：先跑第 1.3 节探针；上线后选 1–2 名已绑定员工，把 Web 抽屉里的税前 / 税 / 实发与 MYOB 官方 Pay Slip 对一遍（用户自己核对，我不碰他人真实薪资数据做截图）。
6. 门禁：后端相关测试、`tsc`、web lint、web 首屏包体预算（新页签 lazy 加载，文案放页面级，避免撑破首屏预算）。
7. 回滚：只读功能、无结构性改动；回滚 = 回滚主 API + Web；新表与权限行留着无害。

## 8. 实施顺序（确认方案后）

| 步 | 内容 | 发布 |
|---|---|---|
| 1 | `GetPayrollAdvicesAsync` + 探针接口 + 迁移（权限码、查看记录表）+ 主 API | 迁移 → 主 API |
| 2 | 用户同意后跑探针，核对第 1.3 节；修订本方案第 1、3 节 | — |
| 3 | 匹配规则 + 列表 / 抽屉接口 + 测试 | 主 API |
| 4 | Web 页签 + 抽屉 + 状态 | Web |
| 5 | 去掉探针；管理员授权角色；抽查核对 | 主 API |

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
