# 商品查询页：扫到套装 / 多码子码时显示并打印该码的标签；自动打印下调价免确认

日期：2026-11-03　范围：`apps/mobile`（扫码查询页）+ 少量主 API　状态：方案，待确认后实施

## 0. 需求原话与拆解

1. 「商品查询页到对应套装或者多码条码，显示对应多码标签」→ 扫到的是套装 / 多码子码时，页面要把**这个码**的条码和价格摆出来，打印入口默认打**这个码**的标签，而不是主档条码 + 主档价。
2. 「自动打印开着，扫码后有价格更新需求的，直接更新 + 标签打印」→ 自动打印模式下，自动定价评估说要调价时，不弹「更新价格并更新标签」确认框，直接落库并打标签（按扫到的码）。
3. 补充：「自动打印开着，扫到套装条码，打印对应套装条码的价格和条码，不是商品主条码和价格」→ 与 1 同一诉求，强调自动打印链路。

## 1. 现状（代码事实，截至 main e75d95083）

页面：`apps/mobile/app/(shell)/product-query.tsx`（6031 行）；纯函数：`apps/mobile/src/modules/product-maintenance/scan-label.ts`。

### 1.1 自动打印扫子码打子码标签：链路已经存在（PR #634，2026-10-08）

| 环节 | 位置 | 行为 |
|---|---|---|
| 在线快路径 | `handleLookup` → `scanProductLabel`（后端 `ScanLabelAsync`，`StoreProductMaintenanceReactService.cs:165`） | 一次返回候选 + 门店详情（不含码表）+ `printTarget` |
| 服务端打印目标 | `BuildScanLabelPrintTargetAsync`（同文件 :250） | `SetBarcode` 命中 → 查 `ProductSetCode` 唯一行 → 主档 `ProductType=2` 走多码（多码条码 + 主档门店价 + 折扣），否则走套装（套装条码 + 门店套装价，不印折扣） |
| 客户端核对 | `getVerifiedScanPrintTarget`（`scan-label.ts`） | 候选唯一、店码一致、主档在本店有 `StorePrice` 行、`matchValue` 等于扫到的码、价格有效，任一不满足返回 `null` |
| 自动打印 | `smartAutoPrint`（:2135） | 有 `printTarget` 用它；没有则 `resolveScannedCodeLabel` 按已加载码表本地匹配；都没有 → 提示「条码列表加载失败」且**不打** |
| 调价后打印 | `sendAutoPricingLabel`（:2089） | `printTarget` 为 set / multi 走 `sendCodeLabel`；否则用 `dialog.detail` 的码表本地匹配 |

结论：**正常路径下扫套装码不会打主档标签**。线上仍出现主档标签，说明走的是下面的退化路径。

### 1.2 退化路径：自动打印「静默不打」，而页面上唯一的大按钮永远打主档

`autoPrintEnabled = trigger === "scan" && continuousPrintEnabled && (!fastResult || !!printTarget)`（:2486）。快路径一旦 `printTarget` 为空就直接关掉自动打印，只弹一条 snackbar；之后页面处于「价格」页签，底部 `LabelPrintCard` 的 普通 / 小标签 / 折扣 / 大标签 四个按钮（`handlePrint`，:4156）和「保存并打印」全部打**主条码 + 主档价**。店员扫了套装码没出纸，顺手按「普通」，出来的就是主档标签。要打套装码只能切到「编码」页签在 45 行里找那一行的打印图标。

`printTarget` 为空的已知触发条件：

| 层 | 条件 | 后果 |
|---|---|---|
| 后端 | 同一商品下同一套装条码有多行 `ProductSetCode`（`setRows.Count != 1`）；行 `IsActive = 0`；套装价 ≤ 0 | `null` |
| 后端 | 主档 `ProductType = 2` 但本店 `StoreMultiCodeProduct` 投影不唯一或 `MultiBarcode` 不等 | `null` |
| 客户端 | 候选 > 1（同一条码既是别的商品的主条码、又是本商品的套装码；或挂在两个套装下） | 走候选表，确认后没有 `printTarget` |
| 客户端 | 主档在本店没有 `StorePrice` 行（快路径不走 `ensureStorePrice` 补建） | `getVerifiedScanPrintTarget` 返回 `null`，提示「当前门店售价无法确认」 |
| 客户端 | EAN 12 / 13 位前导 0 变体回退：`matchValue` 是变体不是扫到的原码 | `null` |
| 客户端 | 候选表确认 / 快路径后本地匹配：快路径 `detail.setCodes` 为空、普通路径只有第一页 50 条（`CODE_PAGE_SIZE`，:435） | `resolveScannedCodeLabel` 返回 `null` → 不打 |
| 客户端 | 快路径 + 无 `printTarget` + 需调价：`sendAutoPricingLabel` 用 `dialog.detail.setCodes`，而快路径的 detail 不含码表 | 确认调价后打印失败，提示「条码列表加载失败」 |

### 1.3 扫到的码在界面上没有任何痕迹

`ProductHeroCard` 显示货号 + 主条码 + 主档价；`lastHitLabel` 也是主档；码表行没有「本次扫到」高亮。店员看不出系统识别的是哪一个码、将要打什么价。

### 1.4 自动定价确认框

`maybeHandleAutoPricing`（:2205）评估 `shouldUpdate` 后一律 `openAutoPricingDialog`，用户点「更新价格并更新标签」才 `persistStorePrice` + 打印。自动打印开关（`continuousPrintEnabled`，:659）目前只决定「确认后要不要补打折扣标签」，不跳过弹窗。四个打印设置开关都是会话级 `useState`，进页面重置。

### 1.5 定位建议（实施前先跑，确认线上到底踩的哪条）

- 服务端：`docker logs hb-platform-vite-api --since 72h | grep "scan-label completed"` 看 `printable=False` 的占比；`grep "lookup timing" | grep "set_hits=[1-9]"` 看套装码扫描时 `product_hits` / `deduped_result_count` 是否 > 1（多候选）。
- 客户端：`[product-query] sendProductLabel { action }` 日志里 `action` 是 `set:` / `multi:` 还是 `product`，能直接证明出纸的是哪条路径。
- 本会话没有生产 ssh 公钥，这步需要本机或有权限的会话执行。

## 2. 目标

A. 扫到子码后，页面明确显示「本次扫到的码」及其应打价格；价格页签的主打印入口和「保存并打印」默认打这个码，想打主档要显式切换。
B. 自动打印开着时，需调价 → 不弹框，直接更新门店价并按扫到的码打标签；关着时维持现有确认框。
C. 1.2 的退化点收口：拿不到服务端 `printTarget` 时按需解析单个码，而不是放弃；自动打印失败时留一键重打该码的入口。

## 3. 方案

### 3.1 数据层：引入「扫码焦点码」`scannedCodeFocus`

```ts
interface ScannedCodeFocus {
  kind: "set" | "multi" | "product" | "clearance";
  codeId: string | null;
  barcode: string;
  retailPrice: number | null;   // 套装：门店套装价；多码：主档门店价；product：主档价
  discountRate: number;         // 套装恒 0；多码 / product 取主档
  source: "server" | "local" | "fetched";
}
```

解析顺序（放进 `scan-label.ts` 的纯函数 `resolveScannedCodeFocus`，可单测）：

1. `getVerifiedScanPrintTarget` 返回的服务端目标（`source: "server"`）；
2. `resolveScannedCodeLabel` 按已加载码表本地命中（`source: "local"`）；
3. 都没有且是扫码触发 → 调用 3.5 的单码解析接口，回填（`source: "fetched"`）；接口 404（旧后端）或失败则焦点为空，退回现状提示。

生命周期：随 `detail.productCode` 变化清空；手动查询（`trigger = "manual"`，关键字可能是商品名）不设焦点；码表行上点打印不改焦点。多码焦点的价格始终取**当前** `detail.storePrice`（保存调价后自动跟新价），不缓存数值。

### 3.2 展示层：焦点码条 `ScannedCodeBanner`

设计判断：这是 PDA 上的作业页，沿用 `HB_COLORS / HB_RADIUS / HB_SPACING` 与 `Card` 语汇，不做视觉翻新；信息架构不动（价格 / 编码两个页签保留）。

- 位置：`ProductHeroCard` 之下、页签之上，价格 / 编码页签都可见；只有焦点码为 set / multi / clearance 时显示（扫主条码不显示，避免噪音）。
- 内容一行：左「扫到 · 套装码 / 多码 / 清货码」小标签 + 条码（沿用 `CodeTableCard` 的整串显示规则：常规长度单行自动缩字号，超长两行）；右侧价格（套装：`$2.00`；多码：主档价，有折扣再加折扣 chip）；末尾文字按钮「打主条码」，点后 banner 切成「主条码 VARIOUS · $1167.99 ｜ 打套装码」可切回。
- 不自动切到「编码」页签：价格页签承载调价 / 促销 / 清货，自动切走会打断「扫一下看价」的主流程；焦点信息已经提到首屏。但在「编码」页签内把该行高亮（`CodeTableCard` 新增 `highlightedId`，行底色用现有 `rowDirty` 同级的浅色，首次渲染 `scrollTo` 到该行）；该码不在已加载分页里时，banner 价格仍来自焦点码，列表不强制翻页。
- 文案：zh `scanned.title = 本次扫到`、`scanned.kind.set = 套装码`、`scanned.kind.multi = 多码`、`scanned.kind.clearance = 清货码`、`scanned.switchToMain = 打主条码`、`scanned.switchToCode = 打扫到的码`；en 同步到 `locales/en/screens/productQuery.json`。
- 360dp（Zebra TC26）预算：banner 单行 44dp，条码区 `flex: 1`，价格区不收缩，切换按钮 compact。

### 3.3 打印入口跟随焦点码

| 入口 | 焦点 = 套装码 | 焦点 = 多码 | 焦点 = 主条码 / 无焦点 |
|---|---|---|---|
| 普通 / 小标签 | `sendCodeLabel`：套装条码 + 套装价，不补折扣 | `sendCodeLabel`：多码条码 + 主档价 + 主档折扣，有折扣补打折扣标签（与现有多码行一致） | 现状 |
| 折扣 | 禁用（套装不印折扣，#634 规则） | `printDiscountLabel(detail, type, { barcode })`（已支持 overrides） | 现状 |
| 大标签 | 禁用 | `printBigDiscountLabel` 需补 overrides 参数（现签名只收 detail） | 现状 |
| 保存并打印 | 同普通 | 同普通 | 现状 |
| 自动打印 / 调价后打印 | 已按焦点码（现状 + 3.1 第 3 级回退） | 同左 | 现状 |

按钮禁用时在 banner 上说明原因（「套装标签不印折扣」），避免以为坏了。`printingAction` 继续用 `set:<id>` / `multi:<id>` 区分，码表行的转圈不受影响。

### 3.4 自动打印开着：调价免确认

推荐口径：**自动打印开 = 免确认**，不新增开关。理由：自动打印是「走货架连续贴标」模式，确认框是纯打断；`continuousPrintEnabled` 是会话级状态，每次进页面重置为关，不存在「忘了关导致静默改价」的长期风险。若你希望留安全阀，可改成「自动打印」下的子开关「调价免确认」（样式同「单次有效」），默认开。

实现（`maybeHandleAutoPricing`）：

```
evaluation.shouldUpdate && recalculatedRetailPrice != null
  ├─ options.autoPrintEnabled
  │    → playQueryFeedback("price_update_required")   // 保留提示音，让店员知道这次改了价
  │    → persistStorePrice(detail, { retailPrice, discountRate, isAutoPricing: true })  // 与弹窗确认完全同一组参数
  │    → 失败：status "failed"，snackbar「自动价格更新失败」，不打标签
  │    → 成功：sendAutoPricingLabel(savedDetail, { ...同弹窗 state, autoPrintEnabled: true })
  │    → snackbar「自动价已更新 $1167.99 → $1150.00，已打印」；返回 status "auto_confirmed"
  └─ 否则 → 现有 openAutoPricingDialog
```

- 展示顺序：需调价的商品维持「先展示详情再打印」（`presentAfterPrint` 条件不变），店员能看到价格跳变。
- 多码焦点：打印用保存后的新主档价（`resolveMultiCodeLabelPrice(savedDetail)`，现有逻辑）。套装焦点：套装价不随主档自动价变化，照打套装价。
- 价格任务卡（`ProductPriceTaskCard`）：`persistStorePrice` 之后的任务刷新沿用现有 `refreshAfterCommittedProductMutation`，不额外处理。
- 离线、供应商 200、`isAutoPricing` 关：维持现状（不评估 / 不弹）。
- 相机连续扫码：免确认后不再有弹窗阻塞相机 sheet，`restoreScanAbility` 的调用点要从弹窗按钮移到静默分支结束处，否则相机不恢复。

### 3.5 退化点收口（可靠性）

a. **单码解析接口**（主 API）：`GET /api/react/store-products/{productCode}/codes/lookup?barcode=&storeCode=`，在 `StoreProductMaintenanceReactService` 复用 `BuildScanLabelPrintTargetAsync` 的取价逻辑，返回 `StoreProductPrintTargetDto`。客户端在候选表确认后、快路径无 `printTarget`、码表分页未含该码三种情况调用；404 按 `scanLabelUnsupportedUntilRef` 同样的 60 秒降级，兼容旧后端。
b. **后端多行容错**：`setRows.Count > 1` 时先过滤 `IsActive && SetRetailPrice > 0`，仍多行才返回 `null` 并记 `LogWarning`（带 productCode / barcode），方便清重复数据。
c. **套装码不再要求主档门店价**：`getVerifiedScanPrintTarget` 对 `kind = "set"` 放宽 `detail.storePrice` 检查（套装标签不用主档价；后端 set 分支本就不依赖 `StorePrice`）；`multi` 维持要求。
d. **调价后打印不再依赖 `dialog.detail.setCodes`**：统一用 3.1 的焦点码。
e. **失败提示带动作**：自动打印没打出但焦点码已解析时，snackbar 加「打印该码」动作；文案按原因区分（码表未含 / 门店价缺失 / 打印机）。
f. 新增客户端日志 `[product-query] scanned-code focus { kind, source }`，配合现有 `sendProductLabel { action }`，下次再报「打成主档」能直接对日志。

### 3.6 不做的事

- 不改收银端（WPF / iPad / 手持）与 Web 后台。
- 不改套装价 / 多码价的计算与存储规则。
- 不自动切换到「编码」页签。
- 不新增原生模块：前端部分纯 JS，可 OTA；3.5a / 3.5b 需部署主 API，PR 标题不加原生包标注。

## 4. 分批与验收

### 批 1（纯前端，可 OTA，先交付）

3.1 焦点码（第 1、2 级）+ 3.2 banner + 码表行高亮 + 3.3 打印入口跟随 + 3.4 免确认 + 3.5c / 3.5d / 3.5e / 3.5f + zh / en 文案。
测试：`scan-label.test.ts` 新增 `resolveScannedCodeFocus` 用例（server / local 优先级、manual 不设焦点、多码价跟随 storePrice）；`product-query-presentation` 若抽出 banner 文案纯函数一并补；`npm run typecheck` / `lint` / 现有 `test:ci`。
目测：iPhone + Zebra 360dp 两档，价格 / 编码两个页签，banner 不溢出、按钮禁用态有说明。

### 批 2（主 API + 前端）

3.5a 单码解析接口 + 客户端第 3 级回退 + 3.5b 后端容错。
测试：`StoreProductMaintenanceLookupTests` 补多行容错与单码接口用例（新测试文件要手动加进 `BlazorApp.Api.Tests` 的 Compile Include）；部署顺序 主 API → OTA。

### 验收矩阵

| 场景 | 自动打印开 | 自动打印关 |
|---|---|---|
| 扫套装子码（前 50 条内 / 之外） | 出套装条码 + 套装价；banner 显示该码；无折扣标签 | 不自动打；banner 显示该码；按「普通」出套装标签 |
| 扫多码子码 | 多码条码 + 主档价，有折扣补折扣标签 | 同左，手动 |
| 扫主条码 / 货号 | 现状 | 现状 |
| 扫清货码 | 现状 | 现状 |
| 同条码两个商品（候选表） | 确认后按焦点码出纸（批 2 前：码在首页才行） | 手动 |
| 需调价 + 套装子码 | 无弹窗；主档价更新；出套装标签 | 弹窗如旧；确认后出套装标签 |
| 需调价 + 多码子码 | 无弹窗；出多码条码 + 新主档价 | 弹窗如旧 |
| 主档本店无门店价 + 套装子码 | 批 1 后可出套装标签 | 手动可出 |
| 离线 | 现状（快照含全码表） | 现状 |

## 5. 待你确认的点

1. 免确认口径：自动打印开即免确认（推荐），还是加一个子开关。
2. 套装焦点下「折扣 / 大标签」按钮禁用，是否符合门店习惯（套装不印折扣是 #634 定的规则）。
3. banner 默认不自动切「编码」页签，是否接受。
4. 批 2 的单码接口要不要做；不做的话候选表路径和超过 50 条的码表仍可能「不打」。

## 6. 实施记录：批 1（2026-11-03，本 worktree，未提交）

落地范围与 3.x 的差异：

- 焦点码条只在焦点为**套装 / 多码**时显示；扫到清货码仍走原有清货标签逻辑，不显示条（避免条上写着「打印按钮打此码」却与清货按钮口径不一致）。
- 3.5e 的「失败 snackbar 带『打印该码』动作」没做：码表加载后焦点会补解析出来，焦点码条本身就是一键重打入口，snackbar 动作重复。
- 3.4 按推荐口径实现：自动打印开 = 需调价免确认（`applyAutoPricingWithoutPrompt`，写库参数与弹窗完全一致，保存期间借用 `autoPricingDialogSaving` 闸门）；没有新增开关。
- `getVerifiedScanPrintTarget` 对 `kind = "set"` 放宽了主档门店价行检查（3.5c）；`multi` / `product` 不变。
- 价格页签四按钮：套装焦点下「折扣」「大标签」禁用并在点击时提示 `messages.setFocusNoDiscount`；多码焦点下折扣 / 大标签换成多码条码（`printBigDiscountLabel` 新增 overrides 参数）。「保存并打印」同口径。
- 码表行高亮按**条码**匹配（服务端多码目标的 `codeId` 是门店投影 UUID，与行 id 不一定一致）；切到「编码」页签时同一焦点只自动滚动一次。
- 日志：`[product-query] scanned-code focus { trigger, kind, source }`（码表加载后补解析的带 `late: true`）、`[product-query] auto pricing applied without prompt { from, to, labelPrinted }`。隐私契约测试禁止日志里出现 `barcode / keyword / productCode` 字样，新日志都没有。

改动文件：`apps/mobile/app/(shell)/product-query.tsx`、`src/modules/product-maintenance/scan-label.ts`（`ScannedCodeFocus` / `resolveScannedCodeFocus` / `resolveFocusLabelTarget` / `isSubCodeFocus`）、新增 `src/components/product-maintenance/ScannedCodeBanner.tsx`、`CodeTableCard.tsx`（`highlightedId` / `highlightedRowRef`）、`SetCodeCompactSection.tsx`、`MultiCodeCompactList.tsx`、`src/modules/printer/api.ts`、zh / en `productQuery.json`。

验证：`tsc --noEmit` 通过；eslint 只剩 8 条既有 `exhaustive-deps` 警告；`scan-label.test.ts` 新增焦点解析 / 取价 / 套装放宽用例；`warehouse-price-sync.test.ts` 补焦点在码表之外的自动打印与价格页签按钮跟随焦点的回归；四个源码契约测试（小标签、键盘、隐私日志、AST 回调）调整后通过。模拟器「HB 扫码焦点验证 8307」（1.0.11 开发包）用免登录临时路由目测 360dp 与整宽：焦点码条、多码折扣标、长码两行、码表高亮行、切回主条码态均正常。**整页真机 / 模拟器 E2E（真实扫码 → 焦点 → 自动打印 / 免确认调价）未做**，需要登录生产账号与蓝牙标签机。

批 2（单码解析接口 + 后端多行容错）未开始。

## 7. 用户拍板后的调整（2026-11-03，经分叉会话转述）

对第 5 节三个待确认点的决定与随之的改动：

1. **「套装子码的可以有折扣」** → 推翻 #634 的「套装标签不印折扣」。套装子码改为与多码同一口径：**套装条码 + 套装价 + 主档门店折扣**（套装行没有自己的折扣字段）。影响面：
   - 扫码焦点为套装时，价格页签「折扣」「大标签」按钮可用，打套装条码 + 套装价 + 主档折扣率；`messages.setFocusNoDiscount` 文案删除。
   - 「普通」按钮、码表行打印、自动打印、调价后打印：普通套装标签带主档折扣率（原生普通标签在折扣率 > 0 时会印「NN%OFF」角标），之后有折扣再补打折扣标签，与多码行一致。
   - `resolveScannedCodeLabel` / `resolveScannedCodeFocus` / `resolveFocusLabelTarget` 的套装分支折扣改取 `storePrice.discountRate`（无门店价行时为 0）；服务端 `BuildScanLabelPrintTargetAsync` 的套装目标仍不带折扣，由客户端按主档补，后端不改。
   - 自动打印补打折扣标签对套装一并放开（按主档折扣率判定）。这一点与手动按钮同口径实现，未单独设开关；若门店实际不希望套装自动出折扣标签，只需把 `smartAutoPrint` / `sendAutoPricingLabel` 套装分支的 `withDiscountLabel` 关掉。
2. **「如果可以打印子码对应条码和价格就不需要自动跳过去」** → 维持：焦点码条 + 价格页签按钮跟随焦点，不自动切「编码」页签。
3. **「单码接口不要做，使用本地码表，以后超过 50 个再说」** → 批 2 **暂缓**：焦点码只用服务端 `printTarget` 与本地码表两级；码表超过 50 条且扫到的码在第一页之外时维持现状（提示「条码列表加载失败」，码表「加载更多」后焦点会补解析出来）。3.5a / 3.5b 不实施。

验证：`scan-label.test.ts`、`warehouse-price-sync.test.ts` 的套装断言改为主档折扣口径并通过；tsc / eslint / 移动端 `test:ci` 复跑通过（见会话记录）。

