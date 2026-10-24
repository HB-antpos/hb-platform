# 移动端「客流统计」页 — 方案（2026-10-24，设计阶段，未写码）

设计图：`main.png`（今天 / 本周 / 本月三个视图）、`states.png`（选店、按住曲线、空态、错误与无权限）。
需求：移动端新增客流量统计页，带分时曲线图；默认「今天」，可切换「周 / 月」。

## 1. 现状（已核对源码）

| 项 | 事实 |
|---|---|
| 数据 | 后端已有按门店的客流计数：原始桶 `FaceTrafficBucket`（UTC 整点），以及 10 分钟一次投影的 `FaceTrafficHourly`（门店本地日期+小时，含派生列 `VisitCount`，永久保留）。**只有 Match 模式门店才有数据**。 |
| 接口 | `GET /api/react/v1/face-match/traffic?store&from&to`（`FaceMatchController.Traffic`，权限 `FaceMatch.Traffic.View`）。按门店时区归日；**仅 from==to 时才返回分小时**；最长 92 天；非管理员只能看自己所属门店。 |
| 接口缺口 | ① `FaceTrafficCountsDto` 没有「进店人次」(`VisitCount`)，只有抓拍/员工/低质量/失败/独立访客/重复；② 多天范围没有分时汇总；③ 没有「门店今天是几号」，客户端按手机日期算“今天”会在跨时区/夜间出错。 |
| Web | `apps/web/src/pages/System/FaceMatch/TrafficTab.tsx`：表格形式，无曲线。 |
| 移动端 | 无客流页。`face-hub` 只有 抓拍/名单/设置 三页签，且入口权限里**没有** `Traffic.View`；移动端 `PERMISSIONS` 里也没有这个码。移动端已有手绘 SVG 图表先例（`reports/CumulativeRevenueChart.tsx`，react-native-svg），不引入图表库。 |
| 合规 | 独立访客是**估算值**，页面必须常驻标注；本页只展示计数，不含任何生物信息、照片。 |

## 2. 页面设计（见设计图）

**结构（自上而下）**：门店胶囊（点开选店弹层）+ 更新时间 → 周期分段 `今天 | 本周 | 本月` → 日期步进 `◀ 标题 ▶`（不能越过当前周期）→ 汇总卡 → 曲线卡 → 辅助卡 → 明细表 → 估算说明 → 数据口径。

**主指标 = 进店人次**（= 抓拍 − 员工 − 低质量 − 识别失败，含当日重复到访）。独立访客（估算）、重复到访作为次要指标；独立访客只在该门店开启去重时显示。

| 视图 | 汇总卡 | 主曲线 | 辅助 |
|---|---|---|---|
| 今天（默认） | 进店人次 + 较昨日同时段；独立访客/重复/高峰时段 | **分时曲线**（24h 取有数据的营业时段±1，默认 8–20）：今天实线、昨天虚线、14:32「现在」竖线，进行中的小时画虚线+空心点 | 客流构成条；分时明细表（今天/昨天/差，可展开） |
| 本周 | 本周至今合计 + 较上周同期（只比已完整结束的日子）；日均/最忙一天/日均访客 | **每日曲线**（周一–周日，上周虚线对照；未到的日子留空） | **各时段平均进店曲线**（分时）+ 每日明细表 |
| 本月 | 本月至今 + 较上月同期；日均/最忙一天/周末日均 | 每日曲线（31 天，周末灰底，上月按日期对齐虚线） | 按星期平均柱状 + 各时段平均进店曲线 |

**交互**：按住曲线左右拖动，竖线吸附到整点并弹出该小时明细（松手保留，点空白取消）；下拉刷新；「今天」前台时每 5 分钟自动刷新；`◀ ▶` 翻看历史日 / 周 / 月；读屏用户走明细表（图本身只给汇总性 `accessibilityLabel`）；虚实线区分当前/对照，不靠颜色单独表意。

**状态**：加载骨架、门店未开启计数（说明如何开启）、今天暂无数据（灰虚线显示昨天作参照）、加载失败（保留旧数据 + 重试）、无权限、iOS 审核壳不显示入口。

## 3. 数据口径（定死，避免页面各说各话）

- 日期/小时一律门店本地时间（服务端按门店时区换算，含夏令时：开始日缺 02 点，结束日 02 点两个 UTC 小时合并）。页面副标题显示门店时区。
- 同比只比**已完整结束**的时间：今天比昨天「截至上一个完整小时」；周/月不含今天。设计图里“+5%”按线性插值做了示意，**实现按完整小时比，文案写明截止时刻**，不做假精度。
- 周/月的「独立访客」= 各日去重人数相加（同一个人跨天会重复计），所以周/月只展示「日均访客」并带「估算」标，不展示“周独立访客”。
- 周起点：周一（实现时对照报表中心的周口径，保持一致）。

## 4. 后端改动（additive，不影响 Web 现有表格）

新增 `GET /api/react/v1/face-match/traffic/overview?store=&period=day|week|month&anchor=yyyy-MM-dd`，同权限 `FaceMatch.Traffic.View`，复用 `FaceTrafficService` 的门店权限校验与时区换算：

- `anchor` 缺省 = 门店本地今天；服务端算出周期 `from/to`、对照周期 `prevFrom/prevTo`、`today`、`asOfLocal`（取数时刻门店本地时间）。
- 返回 `current` 与 `previous` 两段，各含 `days[]`（带 `visitCount`）、`total`；`period=day` 时 `hours[24]`，`week/month` 时 `hourlyAverage[24]`（已完成日的日均）+ `daysWithData`。
- `FaceTrafficCountsDto` 增加 `VisitCount`（派生，口径同 `FaceTrafficHourly.VisitCount`）。老接口只加这一个字段，Web 归一化会忽略未知字段。
- 为什么新增而不改老接口：「今天是几号」「上周同期」「周起点」这些口径放服务端一处，客户端不会因手机时区/语言各算各的；一次请求取回当期+对照期，避免手机端两次请求拼装。
- 数据源：今天走原始桶（近实时）；历史走已存在的计数桶按本地小时汇总即可，**不新读 `FaceTrafficHourly`**（避免两套口径）。上线前核对：生产库计数桶最早日期（代码里未见对桶的清理，只清抓拍事件/图片，仍需实查），决定月视图能回看多久。

## 5. 移动端实现（纯 JS，无原生改动）

新模块 `apps/mobile/src/modules/traffic-stats/`：

- `api.ts` / `normalize.ts`：请求 overview，防御式归一化（缺字段按 0，未知门店/空数据给空态）。
- `period.ts`：周期切换与步进（不超过当前周期）、标题文案；纯函数，Node 测试。
- `chart-model.ts` + `monotone-path.ts`：数据→坐标、好读刻度、营业时段自适应、单调三次插值（不过冲，设计图同一算法）；纯函数，Node 测试。
- `TrafficLineChart.tsx`（react-native-svg + `PanResponder` 做按住吸附）、`TrafficBarChart.tsx`、`TrafficStatsScreen.tsx` 及卡片部件，样式沿用 `HB_COLORS` 与报表卡片语言。
- 门店选项：复用报表中心 / 人脸设置页现有的门店选项来源，不新增接口；默认主门店；记住上次选择。
- i18n：`zh-CN` / `en` 各一份并过 `check-locale-parity`；英文用 Foot traffic / Visits / Unique visitors (est.)。
- 新增 `PERMISSIONS.FaceMatch.Traffic.View` 常量。

**入口（推荐独立路由 `traffic-stats`，工作台「报表」分组）**：客流统计是经营数据，不是人脸管理；放进 `face-hub` 会让只有 `Traffic.View` 的店长先被“人脸比对”的入口守卫挡住。代价是三端菜单登记（见 `mobile-menu-registration-checklist`）：后端 `NavigationService.FullAppMenu` + 测试、移动端 `default-route / workbench / ios-review/menu / role-menu-catalog` + 中英文文案、Web `expoRoleMenuPreview`。备选：作为 `face-hub` 第 4 页签（登记少，但要放宽 hub 的入口权限与后端 `face-hub` 菜单 AnyPermissions）。

## 6. 测试与验证

- 后端：`FaceTrafficTests` 补 overview（今天/周/月边界、夏令时日、跨年周、`from>to`/超限、无权限门店、未开启门店返回空）；`VisitCount` 与 `FaceTrafficHourly` 同口径对拍；菜单测试 `menu.Count` +1。
- 移动端：period/chart-model/normalize 的 Node 测试；源码契约测试（估算标注常驻、不请求/不渲染任何图片、审核壳不进入、权限码）；`npm run test:ci`、`test:navigation`、`tsc`、locale parity。
- 目测：模拟器用临时预览路由灌样例数据（沿用报表重设计的办法，英文设备同看一遍，防文字被挤）；曲线拖动、翻周/月、空态/错误态逐个过。真机/生产数据未验证前不在 PR 里写“已验证”。
- 不做：把 Web 表格改成图表（范围外）。

## 7. 发布

- 后端新接口 → 先部署主 API，再 OTA（顺序反了旧接口没有 overview，页面会进错误态）。无迁移、无新权限码入库（`FaceMatch.Traffic.View` 已存在；新开放的是移动端入口，授权由用户在角色里做）。
- 纯 JS：可走 iOS / 安卓 OTA，无需重建原生包（PR 标题不加原生标注）。发 OTA 需用户明确说。

## 8. 分期

1. 后端 overview + `VisitCount` + 测试（约 0.5 天）
2. 移动端模块：period/chart-model → 图表 → 页面三视图（约 1.5 天）
3. 菜单三端登记、i18n、选店弹层、状态页（约 0.5 天）
4. 模拟器目测 + 修正 + PR（约 0.5 天）
- 二期（本次不做）：全部门店汇总/门店排行（需新接口与“按门店可见范围汇总”规则）；与销售额叠加算转化率（`FaceTrafficHourly` 已与营业额同口径，可直接按 门店+日期+小时 JOIN）；摄像机离线提示。

## 9. 待你拍板

1. **入口位置**：独立路由放「报表」分组（推荐）还是 `face-hub` 第 4 页签？
2. **范围**：一期只做单门店（推荐），还是要「全部门店汇总」一起上？
3. **对照口径**：今天比昨天、周比上周、月比上月，是否符合你的看法？
4. **独立访客**：作为次要指标保留（仅去重门店显示，推荐），还是整页只显示进店人次、不展示估算值？

## 10. 实施记录（2026-10-24，按上面 4 项推荐落地，未提交）

与方案/设计图的差异：
- 后端：没有改 `FaceTrafficCountsDto`，`VisitCount` 放在全新的 `FaceTrafficOverview*Dto`（老接口与 Web 表格零改动）；overview 另加 `countingEnabled`（档案 Mode==Match）。
- 选店弹层：不再给每家店标「已开启计数」徽标（没有按店列出模式的接口），选到未开启的店后显示说明页。
- 曲线交互：点一下选中最近的点、再点同一处取消；左右拖动吸附。小时窗口取「至少 8–20 点，数据超出就外扩」，不再额外 ±1。
- 纵轴最多 5 格（原 4 格）。
- 本周 / 本月第三项指标：开启去重的门店周视图显示「日均访客（估算）」，其余显示「周末日均」。

验证：移动端 tsc 干净、eslint 干净、tsx 套件 1042 项全过（含新增 6 个测试文件）、locale parity 过；后端相关 562 项过；Web `expoRoleMenuPreview` 过；iPhone 15 Pro 模拟器用样例数据目测过 今天/本周/本月/选店/未开启空态（英文设备）。
未验证：真实门店数据、SQL Server 实库（后端测试用 SQLite）、真手指拖动手感、安卓、中文设备排版。
