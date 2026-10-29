# MYOB 工资对接方案：员工绑定 · 推送工时 · 工资单与假期余额 · 时薪合规

> 状态：方案稿（2026-10-27 第三版，口径已全部确认），未开始实现。

## 1. 目标

1. 我方员工与 MYOB AccountRight 员工卡一一绑定（按发薪公司）。
2. 工资周期锁定后，把**转账部分**工时推送到 MYOB Timesheet，财务在 MYOB 内照常跑 Pay Run。
3. 从 MYOB 拉回工资单（结构化数据）与年假 / 病假余额，在我方系统按 MYOB 默认 Pay Slip 版式展示，注明「以 MYOB 发出的工资单为准」。
4. 「时薪是否合规」的检查放在我方系统做，再与 MYOB 联动（推送前提醒、发薪后核对费率）。

MYOB API 不能跑 Pay Run、不能报 STP、不提供工资单 PDF —— 发薪动作始终在 MYOB 内完成。

## 2. 已确认口径

| 项 | 结论 |
|---|---|
| MYOB 产品 | AccountRight，公司文件在 **MYOB 云端** |
| MYOB Team | 未使用，Timesheet 只由我方写入 |
| 公司文件 | 按 ABN 分多个，每个 ABN 一个连接；授权人必须是该文件的 Administrator |
| 推送范围 | 只推转账部分，现金部分不进 MYOB |
| MYOB 工资类别 | 平日 / 周六 / 周日；公假上班、公假带薪 → 平日；**病假 → Personal Leave Pay，年假 → Holiday Leave Pay** |
| 公假转账时薪 | **改成与平日转账时薪一致**；现有不一致的记录列清单，由财务新建时薪记录 |
| 发薪公司 | 按「员工 × 上班分店」决定：**默认由上班分店所属公司发**（一对一）；可为某员工的某分店改由另一家公司发（如 A 店工时由 B 发）；**同一分店的工时不能拆给两家发薪公司** |
| 固定转账工时跨公司 | 按各发薪公司的工时比例分摊（只分额度，不拆分店工时） |
| 平日时薪写回 MYOB | **暂不做** |
| 假期累计 | 用 MYOB 默认算法；**只有长期员工（全职 / 兼职）有**，临时工没有 |
| 请假超过余额 | 只提示，不禁止提交 |
| 员工工资单 | 按 MYOB 默认 Pay Slip 版式，注明以 MYOB 为准 |
| 关联公司代发薪的协议 / 开票 | 不在本方案范围 |

## 3. 总体流程

```
我方考勤 / 工资表（按分店）
   │  周期提交锁定
   ▼
按「发薪公司 × 员工 × 周期」汇总 → PayrollCalculator 算转账 / 现金拆分
   │  跨公司时按比例分摊固定转账工时 → 转账工时按日拆分
   │  推送前：时薪合规检查（生日换档、最低时薪）
   ▼
PUT Payroll/Timesheet（发薪公司的 MYOB 文件）
   │  财务在 MYOB 跑 Pay Run、发正式工资单、报 STP
   ▼
每日拉取 EmployeePayrollAdvice + EmployeePayrollDetails.Entitlements
   │  存工资单快照 + 余额快照；对账金额 + 核对费率
   ▼
员工在 App / Web 查看工资单与余额（以 MYOB 为准）
```

## 4. 数据模型（全部新表，走显式迁移）

| 表 | 用途 | 关键约束 |
|---|---|---|
| `MyobConnection` | 一个 ABN 一条：BusinessId（`cf_uri`）、BusinessName、ABN、加密 refresh token、授权人、状态 | BusinessId 唯一 |
| `MyobStoreMapping` | 分店 → 连接（分店所属公司 = 默认发薪公司），按 `Store.ABN` 自动建议 | StoreCode 唯一 |
| `EmployeePayrollEmployer` | 员工 × 上班分店 的发薪公司覆盖：`StoreCode` 必填，`EffectiveFrom` 必须是工资周期起始日，按生效日期留历史 | (UserGuid, StoreCode, EffectiveFrom) 唯一 |
| `EmployeeMyobLink` | 员工 × 连接 → MYOB 员工卡 UID，含匹配方式 / 分数 / 绑定人，软解绑 | (UserGuid, ConnectionId) 唯一；(ConnectionId, MyobEmployeeUid) 唯一 |
| `MyobPayCategoryMapping` | 每个连接：平日 / 周六 / 周日 / Holiday Leave Pay / Personal Leave Pay 工资类别 UID；年假 / 病假余额类别 UID | ConnectionId 唯一 |
| `MyobTimesheetPush` | 推送日志：周期、员工、请求哈希、状态、MYOB 错误原文 | — |
| `MyobPayslip` | 工资单快照：Advice 原文 JSON、Gross / Net、支付日期、周期、当时余额快照、对账结果（金额 + 费率） | (LinkId, PaymentDate, PeriodStart) 唯一 |
| `MyobLeaveBalance` | 最新余额：Kind（Annual / Personal）、CarryOver、YearToDate、Total、AsOfPaymentDate、FetchedAt | (LinkId, Kind) 唯一 |
| `PayrollAnnualReview` | 每财年最低时薪核对记录：财年、核对人、核对时间（第 11.4 节） | 财年唯一 |

token 加密沿用 `Security/LinklyCloudTerminalCredentialDataProtection.cs` 的做法；access token 只在内存缓存。

## 5. 阶段 0：连接 MYOB

- 开发者应用已注册。Redirect Uri 填 `https://hotbargain.vip/api/myob/oauth/callback`，必须与代码逐字一致。
- Secret 曾在截图中露出明文，**上线前在 MYOB 后台重新生成**，只放服务器 `.env`。
- OAuth2 授权码模式，scope：`sme-company-file sme-company-settings sme-contacts-employee sme-payroll`；回调参数 `businessId` 即 `cf_uri`；授权码 2~5 分钟过期，回调里立即换 token。
- 每个 ABN 的公司文件由该文件 **Administrator** 授权一次。
- 连接后读公司信息的 ABN，与 `Store.ABN` 比对，自动建议分店映射，人工确认。
- refresh 失败 → 连接标「需重新授权」，Web 提示。客户端限流 8 次 / 秒并重试。

## 6. 阶段 1：员工绑定

- 按连接拉 `Contact/Employee`（IsActive）+ `EmployeePayrollDetails`（DateOfBirth、Entitlements）。
- 打分建议，**人工确认才生效**：

| 信号 | 我方 | MYOB | 权重 |
|---|---|---|---|
| 邮箱一致 | `User.Email` | `Addresses[].Email` | 高 |
| 生日一致 | `EmployeeProfile.Birthday` | `DateOfBirth` | 高 |
| 姓名一致（规范化） | `GivenName` + `FamilyName` | `FirstName` + `LastName` | 中 |
| 手机后 8 位 | `EmployeeProfile.Phone` | `Addresses[].Phone1` | 中 |

- 高分（邮箱 + 生日）可批量确认；中分逐条确认；未匹配手动搜索。
- 列出两边「对方没有」的人员，便于在 MYOB 补建员工卡。
- 绑定页按**发薪公司**列员工（含「在 A 店上班」标注）。
- **用工类型一致性检查**：我方 `EmployeeType` 为全职 / 兼职，但 MYOB 员工卡没有挂年假 / 病假累计；或我方为临时工，MYOB 却挂了累计 → 标黄提示财务在 MYOB 里改。
- 暂不往 MYOB 员工卡写任何数据（含我方员工编号）；对 MYOB 的写操作只有 Timesheet。

### 6.1 阶段 1 实现说明（2026-10-27）

- **数据**：迁移 `20261027.003-myob-employee-binding`（`EmployeeMyobLink`、`EmployeePayrollEmployer`、`MyobEmployeeIgnore`，权限码 `Myob.Employee.Bind`）。绑定是软解绑，靠两个**过滤唯一索引**（`UnboundAt IS NULL`）保证「一个员工一个公司文件一张有效卡 / 一张卡只绑一个人」，解绑后可重新绑定。
- **拉取**：`Contact/Employee` + `Contact/EmployeePayrollDetails`（各自分页到全量，只跟随同主机的 `NextPageLink`），按连接缓存 10 分钟，页面「刷新」绕过缓存。只读，不往 MYOB 写任何东西。
- **打分**（`MyobEmployeeMatcher`，纯函数）：邮箱一致 +45、生日一致 +30、姓名一致 +15（写法相近如 Tom / Thomas 只 +6）、手机末 8 位一致 +10；生日两边都有却不同 −10、邮箱两边都有却不同 −5。**高分 = 邮箱与生日都一致且 ≥ 85 且卡片启用**，只有高分可批量确认；批量确认时服务端会重新打分，不再是高分的条目被拒绝。
- **分配**：所有（员工, 空闲卡）组合按得分降序贪心分配，一张卡只建议给一个人；最像的卡已被别人绑定 = 冲突。
- **员工范围**：至少有一个上班分店今天由该公司发薪（分店映射 + 发薪公司覆盖）的在职员工，加上已绑定该公司卡的员工。
- **用工类型检查**：只看已绑定的人；全职 / 兼职要同时有年假与病假累计，临时工不应有。累计类别按 MYOB 默认名称识别（Holiday / Annual = 年假，Sick / Personal = 病假），公司改名后会识别不到（页面上表现为「没挂累计」）。
- **发薪公司**：覆盖行只新增；生效日期必须是工资周期起始日且不早于当前期（周期长度与双周锚点取考勤设置）；选的公司等于分店默认公司时记为「改回按分店」；没有变化不写行。非管理员只能看 / 改自己可管理分店的行。
- **公假价 = 平日价**：保存时后端强制相等（全现金模式存空）；历史记录不改，列表标出不一致并可筛选。
- **仍待线上只读核对**：`EmployeePayrollDetails` 的字段命名（按官方文档实现）、假期累计类别的实际名称、`Contact/Employee` 的 `Addresses` 邮箱手机填写习惯。

## 7. 发薪公司

**规则：发薪公司按「员工 × 上班分店」决定，一个分店只对应一个发薪公司。**

```
某员工本期在某分店的全部工时由哪家公司发：
1. 该员工 + 该分店 有生效中的覆盖设置 → 用设置的公司（如 A 店 → B 公司）
2. 否则 → 该分店所属公司（MyobStoreMapping，一对一默认）
```

- **同一分店的工时不拆给两家公司**：解析按工资周期起始日取生效设置，整期同一个结果；`EffectiveFrom` 只能选工资周期起始日，避免周期中途切换把一个分店的工时拆开。
- 入口：Web「时薪表」新增「发薪公司」列：默认显示「按上班分店」；「+ 改由其他公司发」按分店逐行设置；按生效日期留历史；需 `Attendance.Payroll.Edit`，留审计。
- 改发薪公司时，新公司未绑定该员工则提示先绑定。
- 门店工时表对「由其他公司发薪」的员工加角标。

**跨公司时固定转账工时的分摊**（员工在不同分店上班、对应不同发薪公司时；只影响混合发放 `Mixed` 的员工，全转账和全现金不需要分摊）：

- **按各发薪公司的工时比例分摊**本期固定转账工时。只分「转账额度」，每个分店的工时仍整体归它的发薪公司。
  - 例：固定转账 20 h，本期 A 店 30 h（A 店 → B 公司）、C 店 10 h（C 店 → C 公司）→ B 分 15 h 转账额度、C 分 5 h。
  - 四舍五入到 0.01 h，尾差归工时最多的那家公司。
- 每家公司分到的额度内，仍按 周日 → 周六 → 带薪假 → 公假 → 平日 装填。
- 同一家发薪公司下的多个分店（如 A 店、D 店都由 B 发），工时先合并再装填。

## 8. 阶段 2：推送 Timesheet

**汇总单位**：发薪公司 × 员工 × 周期。

**推送前检查**（不满足即拒绝并列原因）：
- 涉及的所有分店本期已提交锁定；
- 这些分店的周期起始日 / 周数一致；
- 员工已在发薪公司绑定；
- MYOB 中该周期 `Entries[].Processed=true`（已发薪）→ 不覆盖。

**推送前提示**（不阻止推送，需勾选「已知晓」）：见第 11.1 节时薪合规检查。

**按日拆分**：转账按 周日 → 周六 → 带薪假 → 公假 → 平日 装填（现有 `TransferFillOrder`）；某档全进转账则按实际日期原样填，部分进入则按日期从早到晚填满；推送合计必须等于该公司分到的转账工时。

**类别映射**：

| 我方转账工时 | MYOB 类别 |
|---|---|
| 平日、公假上班、公假带薪 | 平日 |
| 周六 / 周日 | 周六 / 周日 |
| 病假 | Personal Leave Pay（按平日价，MYOB 扣病假余额） |
| 年假（财务核定工时） | Holiday Leave Pay（按平日价，MYOB 扣年假余额） |
| 年假折现 | 不推送，财务手工处理 |

**调用**：每员工一次 `PUT /{cf_uri}/Payroll/Timesheet/{EmployeeUID}?StartDate=…&EndDate=…`，`Lines[]` = 类别，`Entries[]` = 按日 `{Date, Hours}`。

**触发**：周期锁定后，由 `Attendance.Payroll.Edit` 人工点「推送到 MYOB」，按发薪公司分组展示结果；未绑定员工列出不静默跳过；周期解锁 → 推送记录标「需重新推送」。

**MYOB 端前置配置**：工资设置开启「使用 Timesheet 记录工资」；周起始日与我方双周起始日对齐；员工工资明细挂上对应工资类别（含两个 Leave Pay 类别）。

## 9. 阶段 3：拉取工资单与假期余额

- 每天定时 + Web「立即拉取」：按连接拉 `Report/Payroll/EmployeePayrollAdvice`（**按支付日期**筛选，最近 14 天）。
- 同时拉 `EmployeePayrollDetails.Entitlements`：`CarryOver`、`YearToDate`、`Total`（= 当前可用小时）。
- 每张工资单入库时同时存**当时的余额快照**（MYOB 只能查当前余额，事后补拉会失真）。
- **对账**（按 发薪公司 × 员工，只给财务看）：
  - 金额：MYOB `GrossPay` 对我方该公司的转账金额，差额 > $0.01 标黄；
  - 费率：见第 11.2 节，不合规标红。

**假期余额**：

- 只有长期员工（全职 / 兼职）显示余额；临时工不显示余额区块。
- 累计用 MYOB 默认算法。连接后读出累计类别的设置（累计比例、按哪些工资类别计），在 Web 上给财务看一眼；周六 / 周日工资类别是否计入累计，由财务在 MYOB 里确认。
- 只推转账部分，MYOB 只按转账工时累计（全现金员工累计为 0）。
- **预计可用**（App 请假页、工资表用）：

```
预计可用 = MYOB Total − 我方已批准、但尚未被 MYOB 发薪处理的同类假期工时
```

- 单位为小时（可附「约 N 天」，按年假每工作日 8 h）。
- 请假超过预计可用：**只提示**，不禁止提交，最终由财务核定。
- 余额属于发薪公司；员工在多家公司都有余额时分别列出。

## 10. 阶段 4：员工查看工资单

**原则**：数据全来自 MYOB，我方不重算；版式照 **MYOB 默认 Pay Slip**（栏目顺序、名称、英文用词一致）。

**声明**（屏幕页首页尾 + PDF 页首页尾 + PDF 浅色水印）：
> 本工资单仅供查看，以 MYOB 发出的工资单为准。
> For reference only. The official payslip is the one issued from MYOB.

| 区块 | 内容 | 来源 |
|---|---|---|
| 页首 | 雇主名称、ABN、地址 | Advice `Employer` + 公司信息 |
| 员工 | 姓名、员工编号、地址 | Advice `Employee` + 员工卡 |
| 周期 | Pay Period、Payment Date、Pay Frequency、Hourly Rate | Advice |
| Wages | Description / Hours / Rate / This Pay / YTD | `Lines` Type=Wage |
| Deductions / Taxes | PAYG 等 | Type=Deduction / Tax |
| Gross / Net | 应发 / 实发 | `GrossPay` / `NetPay` |
| Superannuation | 本期金额、YTD、基金名称 | Type=Superannuation + `SuperannuationFund` |
| Leave（仅长期员工） | 年假 / 病假：本期累计 / 本期已休 / 余额（截至某日） | Entitlement 行 + Leave Pay 行 + 余额快照 |
| 付款方式 | BSB + 账号后 3 位 | MYOB 付款明细；拿不到则不显示，不用我方银行资料代替 |

**实现**：
- 后端把 Advice 整理成已分区块的 `PayslipViewDto`，Web / 移动端 / PDF 只负责显示。
- PDF 后端生成（已有 `iTextSharp.LGPLv2.Core`）；移动端下载后用 `expo-sharing` 保存 / 分享。
- 移动端「我的工资单」：列表 → 详情（原生组件，顶部固定声明横幅）→ 下载 PDF；附余额卡片。
- Web：工资表点员工名打开同版式工资单。
- 正文英文（与 MYOB 原件一致），声明中英双语。
- 员工看不到工资单的情况：MYOB 未发薪；未绑定（提示联系店长）。
- 一个人由多家公司发薪时，同一周期有多张工资单，按雇主分组。

## 11. 时薪合规检查（我方做，与 MYOB 联动）

### 11.1 推送前：生日换档提醒

- 推送工时前，如果本期内有**未成年员工过了生日**，在推送确认框里提示：
  > 「XX 已于 某日 满 N 岁，请先在我方时薪表新增一条生效记录，并**同时在 MYOB 里改时薪**。」
- 复用现有判断：时薪表的 `BirthdayReviewHint`（时薪记录生效日早于最近一次生日）与工资表的 `IsBirthday`。
- 推送确认框同时列出本期 `BELOW_MINIMUM_RATE`（低于最低时薪）警告。

### 11.2 发薪后：对账时核对费率

- 拉回的工资单里，每行有实际费率（`CalculationRate`）和员工 `HourlyRate`。
- 以下情况**标红**给财务：
  - MYOB 平日行费率 < 我方时薪表在该周期生效的**转账平日价**；
  - MYOB 平日行费率 < 我方**最低时薪**（`MinimumRate`）。
- 周六 / 周日行费率与我方周六 / 周日转账价不一致 → 标黄（可能是 MYOB 倍数设置与我方不一致）。
- 作用：MYOB 里忘了改时薪的情况，发薪后第一次拉取就能发现。
- `CalculationRate` 的确切含义（金额还是倍数）待沙箱确认（第 14 节）。

### 11.3 平日时薪写回 MYOB：暂不做

- MYOB 里的时薪仍由财务手工维护；靠 11.1 推送前提醒和 11.2 发薪后费率核对兜底。
- 以后如要做：通过 `EmployeePayrollDetails.Wage.HourlyRate` 写回，需要写权限，并先核对 MYOB 周六 / 周日倍数与我方周末时薪是否一致。

### 11.4 公假转账时薪 = 平日转账时薪

- 时薪表：公假转账价改为**只读、自动等于平日转账价**；保存时后端也强制相等。
- `PayrollCalculator` 不用改：仍按公假档取价，数值与平日相同。
- **现有记录不改**（保证已锁定周期重算结果不变）：时薪表增加筛选「公假价与平日价不一致」，列出清单；由财务给这些员工各**新建一条时薪记录**，从下一个未锁定的工资周期起生效。清单清零前，推送确认框提示仍有不一致的员工。

### 11.5 每年 6 月提醒更新最低时薪

- 我方系统不会自动知道新的最低工资标准（每年 7 月 1 日起生效），需要人工更新各员工时薪记录里的 `MinimumRate`（以及受影响的时薪）。
- 每年 **6 月 1 日起**，Web 工时与工资页面顶部对有 `Attendance.Payroll.Edit` 权限的人显示横幅：
  > 「新财年最低工资 7 月 1 日生效，请核对并更新最低时薪。」
- 点「本财年已核对」后写入 `PayrollAnnualReview`（核对人、时间），横幅消失；7 月 1 日后仍未核对，横幅变红。
- 横幅同时提示「请同时在 MYOB 里改时薪」；7 月后第一次发薪拉回工资单时，11.2 的费率核对会标出 MYOB 里漏改的员工。

## 12. 权限（新权限码默认不授予，须入库并补移动端文案）

| 权限 | 用途 |
|---|---|
| `Myob.Connection.Manage` | 连接 / 断开 MYOB、分店映射、工资类别映射 |
| `Myob.Employee.Bind` | 员工绑定 |
| `Myob.Payslip.ViewAll` | 查看所有员工工资单与余额、对账结果 |
| `Attendance.Payroll.Edit`（已有） | 发薪公司设置、推送 Timesheet、最低时薪年度核对 |
| — | 员工本人查看自己的工资单与余额，无需额外权限 |

## 13. 确认状态

口径已全部确认，无待定项。按默认执行的设计选择：推送人工触发（锁定后）；MYOB 已发薪的周期不覆盖；员工只看自己；工资单正文英文 + 双语声明；余额以小时显示；临时工不显示余额。

## 14. 待沙箱验证（第 0 阶段连接后先只读确认）

- `PUT Payroll/Timesheet` 是整段替换还是合并；
- `EmployeePayrollAdvice` 的筛选参数写法，`CalculationRate` 是金额还是倍数；
- 各 scope 覆盖的接口（尤其 `EmployeePayrollDetails`、付款明细、PayrollCategory）；
- 公司信息接口能否读到 ABN；
- `EmployeePayrollDetails` 只读即可满足本方案（时薪写回暂不做）；
- MYOB 默认假期累计类别的计算基数是否包含周六 / 周日工资类别。

## 15. 交付顺序

| 阶段 | 内容 | 端 | 发布 |
|---|---|---|---|
| 0 | 连接、token 加密、分店映射 | 主 API + Web | 迁移 → 主 API → Web |
| 1 | 员工拉取、打分、绑定页；发薪公司设置（按分店覆盖）；公假价 = 平日价 + 不一致清单 | 主 API + Web | 同上 |
| 2 | 工资类别映射、跨公司分摊、推送 Timesheet、推送前合规提示 | 主 API + Web | 同上 |
| 3 | 工资单 + 余额拉取、金额与费率对账；6 月最低时薪提醒 | 主 API + Web | 同上 |
| 4 | 我的工资单、余额卡片、请假页余额提示 | 移动端（纯 JS） | iOS / 安卓 OTA，无需重建原生包 |

## 参考

- [Payroll/Timesheet](https://developer.myob.com/api/myob-business-api/v2/payroll/timesheet)
- [Report/Payroll/EmployeePayrollAdvice](https://developer.myob.com/api/myob-business-api/v2/report/payroll_employee_pay_advice/)
- [Contact/Employee](https://developer.myob.com/api/myob-business-api/v2/contact/employee/)
- [Contact/EmployeePayrollDetails](https://developer.myob.com/api/myob-business-api/v2/contact/employee-payroll-details)
- [Granular Data Scopes](https://developer.myob.com/api/myob-business-api/api-overview/granular_data_scopes/)
- [MYOB 社区：Payroll Advice 按支付日期筛选](https://myob.lithium.com/discussions/staffing_and_payroll/unable-to-generate-payroll-advice-report/868043/replies/868625)
