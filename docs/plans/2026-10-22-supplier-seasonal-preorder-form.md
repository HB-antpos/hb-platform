# 供应商节日预订单（KFC / Dats / Brazco 圣诞、万圣节 Preorder Form）方案

> 状态：PR 1（#956）已合并；PR 2（去年历史 + 参考商品）实现中，见 7.1。设计图 4 张：店长下单页、导入向导、分店授权与汇总、下单人员授权。

## 1. 现状与要解决的问题

- 每年 2 月左右，KFC（本地供应商 257 Uncle Bills）、Dats（240）、Brazco（243）会发来圣诞、万圣节等节日的订单表 Excel。2026 年 KFC 圣诞这一张就有 **1,845 款**。
- 现在的做法：每家分店拿一份副本自己填（`ORION …`、`KAWANA …`、`PENINSULA …`、`QLD-…`、`SP- …`），再由人工把各店的箱数合到一张表里，按分店分列（Orion A / Kawana / Peninsula / Robinson）。合表时已经出现大量 `#REF!`。
- 店长填表时看不到去年本店的进货和销售，只能凭印象下单。

### 三家订单表的格式差异（都已逐份读过原文件）

| 字段 | KFC（QT0013074） | Dats | Brazco |
|---|---|---|---|
| 表头位置 | 第 14–15 行，两行拼成一个表头 | 第 2 行 | 第 7 行附近 |
| 货号 | Stock Code（XD0001815） | 2026 Item Code，另有 **2025 Item Code**（新品写 NEW） | ITEM #（XM178369） |
| 条码 | Product Barcode | Barcode | BARCODE |
| 箱规（起订量） | Ctn Qty | Inner / Outer | CTN QTY |
| 价格 | Product Price | PNP PRICE / BULK PRICE 两列 | PRICE |
| 体积 | Carton Cub. Mtr. | 无 | CTN CBM |
| 分类 | 无 | Category | 无 |
| 图片 | 公式 `HYPERLINK("https://unclebills.hosted-by-files.com/Christmas/2026/"&货号&".jpg")`，单张约 700 KB | Image 列 | 嵌入图片 |
| 尾部合计行 | Total / Subtotal / GST / Grand Total | 无 | 无 |
| 订货条件 | 40GP 整柜 | 无 | 满 1 托盘免运费 |

结论：**以「列映射」方式导入**，自动识别表头，每个供应商的映射记住后，下一年自动套用。不为某一家写死格式。

## 2. 为什么不复用现有模块

| 现有模块 | 不直接复用的原因 | 可以借用的部分 |
|---|---|---|
| 总仓预订单 `Warehouse/Preorders`（`PreorderModels.cs`） | 商品必须已经在 `Product` 表里（`ProductCode` 必填）；它是总仓现货预订。供应商订单表里大部分是商品表里还没有的新品（2026 KFC 圣诞实测约 9.5%） | 粘贴解析 `preorderPaste.ts`、选择分店 `activationStoreSelection.ts`、进度卡片样式 |
| `HolidayProduct`（后端已有，前端从未接入） | 它是按节日维护的商品清单，没有订单表、分店授权和下单这些概念 | `HolidayType` 枚举，以及按节日推算备货窗口的 `GetDateRange` |
| #886 商品抽屉 / #882 批量货号销量 | 都是报表 | 日销曲线 `GetSalesDetailProductTrendAsync`、逐批进货 `GetSalesDetailProductPurchasesAsync`、折扣判定规则 `batch-product-sales-discount-v3`、分店范围 `ResolveBranchScopeAsync` |

另外，订单表商品要能脱离商品表独立存在、按订单表快照价格和箱规。所以做法是**新建一个模块**，读数据和画图尽量复用上面这些已有实现。

## 3. 页面

### 3.1 店长下单页（设计图 1）
- **页头**：供应商 · 节日 · 年份、报价单号、柜型、价格条款、下单截止倒计时（剩 ≤3 天变琥珀色）、分店下拉、导出本店 Excel、提交本店订单。
- **指标条**：本店已选几款、几箱、几件、金额（不含 GST）、保存状态；另有**全部分店体积占 40GP 的比例**（按每箱 m³ 累加，各分店分段显示）。
- **筛选**：搜索货号/条码/名称、分类、去年有售 / 新品 / 去年打过折、只看本店已下单、只看其他分店已下单；历史口径（默认去年同季）；排序（默认按去年本店销量从高到低）。
- **表格列**：图片、商品（名称、货号、条码、分类、新品标记）、进价、箱规、**去年本店进货**（首批→末批、批数、合计件数）、**去年本店销售**（首次销售→最后销售、件数、售罄率）、**去年日销曲线**（紫色虚线 = 节日关键日期，粉色底 = 折扣期）、**折扣**（有/无，折扣销量占比）、**其他分店已下单**（店名 + 箱数）、**本店下单**（按箱加减，下方显示件数和金额）。
- **展开行**：
  - 大图：上半部分是日销量，下半部分是进货柱（蓝 = 总仓配货，绿 = 供应商直送）；节日关键日期有标签和底色，折扣期铺底色，并标出峰值。
  - 右侧「去年本店小结」：进货、销售、售罄率、折扣、零售价变化，以及「去年销量 ÷ 箱规」作为参考箱数（只作参考，不自动填）。
  - 右侧「各分店去年对比」：各店的进货、销量、售罄率、是否打折，以及今年已订箱数。
- **分页**：50 / 100 / 200 / 500，默认 50。选择记在本机（localStorage），与 KFC 补货信号报告的做法一致。
- **保存与截止**：箱数输入框失焦即自动保存；过了截止时间整页只读（有管理权限的人仍可改）。

### 3.2 导入向导（设计图 2）
三步：上传或粘贴 → 识别列与校对 → 授权分店并发布。
- **上传**：支持 `.xlsx` / `.xlsm`，后端用 ClosedXML 解析（图片名超过 31 个字符的原表，如 Dats 2027 日记本，会先把图片名截短再打开）。**`.xls` 解析不了**（ClosedXML 和前端的 exceljs 都不支持），会提示另存为 `.xlsx`。
- **粘贴**：直接从 Excel 复制带表头的整块区域，前端按制表符拆分。
- **自动识别**：
  - 在前 40 行里找命中同义词最多的一行作为表头（Stock Code / ITEM # / 2026 Item Code、Description、Ctn Qty / CTN QTY / Outer、Price、Barcode）。两行表头会拼起来识别。
  - 跳过 Total / Subtotal / GST / Grand Total 这类合计行。
  - 图片读单元格公式而不是显示值，从 `HYPERLINK` 里取出地址。
- **备用列**：进价、箱规各有一个备用列，主列为空时按行退回（Dats：PNP 为空用 BULK、Inner 为空用 Outer；灯饰类整段只填了 BULK/Outer）。
- **匹配商品**：顺序是 条码 → 货号（`Product.ItemNumber`）→ 去年货号（Dats 有这一列）。结果分四种：已匹配、新品、**有提示**（条码对上但货号不同、货号对上但条码不同、表内货号重复）、进价变动（和商品表进价比，≥0.5% 算变动）。有提示的不拦截发布：下单和导出都只用供应商自己的货号与箱规，匹配只影响图片和历史数据；详情页「商品」页签可按「有提示」筛出来核对。不能导入（缺货号/名称/进价/箱规）的行建表时跳过。
- **订单表信息**：供应商、节日、年份、报价单号、报价日期、截止时间、柜型和容量、历史对比季，尽量从表头自动带出。
- **图片**（PR 1 先不转存：已匹配商品用我方 COS 图，新品直接懒加载原表图片地址；转存放到后续 PR）：后台把原图下载下来，转成 WebP 缩略图存到 COS。页面不直接引用供应商的图片地址，一是单张 700 KB 太大，二是防盗链或外站失效。
- **重新上传**（供应商发来修订版）：按货号对齐。已下的箱数保留；被删掉的商品标为「已下架」，不删数据；改了价的商品高亮提示。

### 3.3 分店授权与汇总（设计图 3）
- **分店授权卡片**：每家店一张，开关决定这张订单表有哪些分店参与，并显示状态（未开始 / 草稿 / 已提交）、最后操作时间和人、**可下单人员**（来自 3.4，没有人时红字提醒「无人 · 去授权」）、款数、箱数、金额、体积。可一键「从去年同供应商同节日的订单表复制授权」。
- **下单矩阵**：行是商品，列是分店，可在箱 / 件 / 金额之间切换，底部有合计，同样分页 50–500。
- **操作**：提醒未提交的分店、截止并锁定、**导出汇总（供应商原格式）**。导出时以原文件为底，把各店箱数填进去：每店一列，再加一列合计。`.xlsm` 原表经 ClosedXML 写回后可能丢失宏，所以同时提供一份通用格式的导出作为后备。

### 3.4 下单人员授权（设计图 4，独立权限）
- 单独一个页面，**只认这里的授权**：不看人员管理里的「可管理分店」，也不看是否店长。店长换店、调岗不会自动改这里；采购、区域经理没有主分店也能被授权。
- 每个人一行：可替哪些分店下单（多选），以及「可查看其他分店」开关（关掉时，下单页隐藏「其他分店已下单」列和「各分店去年对比」）。
- 对所有供应商节日预订单生效，不必每张订单表重新授权。
- 能替某家店下单，必须同时满足两层：**本页授权了这个人和这家店**，并且**这张订单表开启了这家店**。缺任一层，页面只读并说明原因。
- 只有持有 `SupplierPreorder.Manage` 的人能进入本页、修改授权；每次修改都写操作记录（谁、什么时候、加了或减了哪家店）。

## 4. 数据模型

新建表一律走 `Data/SchemaMigrations`，实体**不加进** `tableTypes`。

| 表 | 关键字段 |
|---|---|
| `SupplierPreorderForm` | FormGuid、LocalSupplierCode、SupplierNameSnapshot、HolidayType、Year、QuoteNo、OfferDate、DeadlineAtUtc、ContainerType、CapacityCbm、HistoryFrom、HistoryTo、Status（Draft / Open / Closed）、SourceFileName、SourceFileKey（COS 上的原件）、ColumnMappingJson、Revision |
| `SupplierPreorderFormItem` | ItemGuid、FormGuid、Seq、SupplierItemCode、Barcode、Name、Unit、CartonQty、CartonCbm、Price、Category、PrevYearItemCode、ImageSourceUrl、ImageThumbKey、MatchedProductCode（可空）、MatchMethod、RefProductCode（新品的参考商品）、RefSource（系统 / 人工）、IsActive |
| `SupplierPreorderFormStore` | FormGuid、StoreGuid、StoreCode、StoreName、AuthorizedBy、AuthorizedAtUtc |
| `SupplierPreorderStoreOrder` | OrderGuid、FormGuid、StoreGuid、Status（Draft / Submitted）、SubmittedBy、SubmittedAtUtc、Revision（防止两人同时改互相覆盖） |
| `SupplierPreorderStoreOrderLine` | OrderGuid、ItemGuid、Cartons、UpdatedBy、UpdatedAtUtc（件数和金额按商品当前的箱规、价格实时计算） |
| `SupplierPreorderOrderer` | UserGuid、CanViewOtherStores、GrantedBy、GrantedAtUtc（独立的下单人员授权） |
| `SupplierPreorderOrdererStore` | UserGuid、StoreGuid、StoreCode、GrantedBy、GrantedAtUtc |
| `SupplierPreorderAuthLog` | 操作人、对象用户、动作（加分店 / 减分店 / 开关查看其他店）、时间 |
| `SupplierPreorderImportProfile` | LocalSupplierCode、HeaderSignature、MappingJson（记住该供应商的列映射） |
| `SupplierPreorderHistorySnapshot` | FormGuid、StoreCode、ProductCode、PayloadJson（汇总数字 + 日销数组 + 折扣区间）、ComputedAtUtc、RuleVersion |

## 5. 历史数据口径

- **匹配到的商品**：`MatchedProductCode`；新品如果指定了参考商品，就用 `RefProductCode`；两者都没有的显示「无去年记录」。
- **时间窗**：默认取去年同一节日的窗口，可在订单表上改。圣诞是 9/1 到次年 1 月中（含节后清货），万圣节是 8/1 到 11/7。
- **进货**：总仓配货（`AllocQuantity`，排除草稿，日期取出库日期，没有就依次退到订单日期、创建日期）加上分店供应商进货单（日期用 `EffectivePurchaseDate`）。与 #882、#886 口径完全一致，复用它们的 SQL。
- **销量**：取 `ProductStoreDailySalesStatistic`。
- **折扣**：日统计表没有折扣列，所以复用 `batch-product-sales-discount-v3` 的规则，从销售明细里的 `DiscountAmount` / `DiscountRate` 判定。输出折扣销量占比和折扣日期区间（连续的折扣日合并成一段）。
- **节日关键日期**：新写一个辅助类，覆盖万圣节 10/31、黑五（美国感恩节的次日）、平安夜、圣诞、节礼日，以及复活节（复用现有算法）。现有代码里没有黑五。
- **性能**：1,845 款 × 6 家以上分店 × 140 天，不能每次翻页都现算。
  - 导入发布后排一个后台任务，生成 `SupplierPreorderHistorySnapshot`；截止前每天夜里刷新一次。
  - 列表接口只读快照。
  - 展开大图时才实时调用 #886 的趋势和进货接口。
  - 500 行一页、每行一条迷你曲线（纯 SVG），图片懒加载，在可接受范围内。

### 5.1 参考商品（新品借用历史）

**定义**：订单表里的商品自己在历史窗口内没有销售记录时（不论商品表里有没有它），指定一个**我们商品表里已有、并且在历史窗口内有销售或进货记录**的商品，用它的去年数据给店长参考。参考数据只是借来看的，永远标明「参考 XXX」，不算作这个新品自己的历史，也不参与汇总。

**和「已匹配」的区别**
- **已匹配**：同一个商品，条码、货号或去年货号对得上。比如 Dats 表里的 2025 Item Code 能对上，就是供应商换了编号的老品，按已匹配处理，不算参考。
- **参考**：不同的商品，只是相似，比如同一系列换了颜色、尺寸，或者换了款式。

**来源，按优先级**
1. **人工指定**（采购，需要 Manage 权限）：在导入校对页或商品页按货号、条码、名称搜索后选定，也可以清除。人工指定的结果不会被系统覆盖。
2. **系统推荐**：从候选里按打分自动挑一个，标「系统推荐」，采购可以改。
   - 候选范围：同一个本地供应商；在去年同节日的窗口里有销售或进货；不能是本订单表里已经匹配上的商品。
   - 打分：名称相似度占 70%（先去掉颜色、Asst、尺寸数字、Pk/Pack 这类词，再用 Dice 系数比较词的重合），进价接近程度占 30%（1 − 差幅 / 30%）。有分类时，要求分类相同。
   - 硬门槛：名称相似度 ≥ 0.5，并且进价相差 ≤ 30%，不满足的直接排除，不靠另一项凑分。
   - 不用箱规：我方商品表的 `MiddlePackageQuantity` 实测全部为空。以后可以改用往年订单表里的箱规。
   - 得分 ≥ 0.6 自动带上；低于 0.6 的不带，只在「选择参考商品」弹窗里列出前 3 个候选。
3. 都没有的，显示「无去年记录」。

**显示规则**
- 表格里的进货区间、销售区间、售罄率、折扣和曲线都改用参考商品的数据，整格用紫色、标「参考 XD0001342」。悬停时显示参考商品的名称、图片，以及「参考进价 $1.02 → 本品 $1.10」。
- 展开大图的标题写「参考商品 XD0001342 的去年日销量」。
- 按「去年本店销量」排序时，参考数据也参与排序；筛选「去年有售」不包括参考，筛选「新品」包括参考。
- 「参考箱数」也会算，但写明「按参考商品估算」。
- 每个新品只能挂一个参考商品，不做多个商品合并，免得口径说不清。
- 参考关系只属于这张订单表的这个商品。明年这个新品自己有了历史，就自动改用它自己的数据。

**数据**：`SupplierPreorderFormItem.RefProductCode`、`RefSource`（Manual / Auto）、`RefScore`、`RefSetBy`、`RefSetAtUtc`；历史快照按「本品 → 参考」顺序取数，并记下取的是哪一个。

### 5.2 用生产数据核对的结果（10-23，只读）

拿 2026 KFC 圣诞订单表 1,845 款对生产库：
- **匹配**：按条码对上 1,669 款，按货号对上 1 款，新品（商品表里没有）175 款。条码对上但货号不同的 1 款（XB4393 ↔ XB4393W），导入时提示。
- **原表解析**：有 32 行的货号不是 X 开头，解析时不能按前缀判断是不是商品行，要按「有货号且有价格」判断。
- **去年自己有历史的只有 419 款（23%）**。商品表里有、但 2025 圣诞季没卖过的有 1,251 款：其中 533 款 2026 年 9、10 月才开卖（就是今年到的货），718 款还没卖。**KFC 每年约 77% 是新款**，所以参考商品是核心功能，从 PR 3 提前到 PR 2。
- **参考商品（按 5.1 规则）**：需要参考的 1,426 款里，得分 ≥ 0.8 的 118 款，0.7–0.8 的 189 款，0.6–0.7 的 306 款，合计 613 款（43%）自动带上。抽查 0.6–0.7 档，基本是同类商品，例如 Bottle Bag Embossed ↔ Bottle Bag Gold Foil、Metal Gingerbread Bakery Sign ↔ Metal Gingerbread Plaque。
- **先前规则的问题**：没有硬门槛时，「Squeeze Screaming Chicken ↔ Party Earrings」只凭进价相同就能拿到 0.5 分，「Nutcracker 60cm $17.14 ↔ Nutcracker 25cm $4.71」只凭名称就能拿到 0.67 分。加了两个硬门槛后这两类都被排除。

## 6. 权限

| 控制项 | 在哪里设置 | 说明 |
|---|---|---|
| `SupplierPreorder.Manage`（角色权限码） | 角色管理 | 导入、编辑、开启分店、截止锁定、导出汇总、截止后修改，以及**维护下单人员授权**。默认给超管和采购 |
| 能替哪些分店下单 | 下单人员授权页（3.4） | 独立授权，按人逐店勾选，**不依赖人员管理里的可管理分店或店长身份** |
| 能否查看其他分店 | 下单人员授权页（3.4） | 逐人开关 |

- 判定一律在服务器端完成，每次请求都重新读取：
  - **能编辑某店订单** = 下单人员授权里有（本人, 该店），并且这张订单表开启了该店，并且未过截止时间（有 Manage 的人不受截止限制）。
  - **能看其他店** = 本人的 CanViewOtherStores 为真，或者持有 Manage。即使能看，也只读。
- **菜单可见**：持有 Manage，或者在下单人员授权里至少有一家分店。`NavigationService` 需要为此加一个自定义条件，因为目前菜单只按权限码判断。
- 只新增 `SupplierPreorder.Manage` 这一个权限码，要写入库 SQL（`Data/Migrations/yyyyMMdd_AddSupplierPreorderPermissions.sql`），并补移动端的权限文案（`accessPermissions.json`）。
- 菜单、路由要在三处登记：`NavigationService`、`routes.tsx`、`access.ts`，以及 `webMenuPreview` / `webPortalAccess`。

## 7. 分期与上线

| 期 | 内容 | 上线需要 |
|---|---|---|
| PR 1 骨架 | 建表迁移、权限、下单人员授权页、导入（上传 + 粘贴 + 列映射 + 匹配 + 图片转存）、订单表列表、分店授权、店长下单（按箱、自动保存、截止锁定）、汇总矩阵、导出 | 部署主 API + Web；执行 `--schema=migrate` 和权限 SQL。不涉及移动端原生包和 WPF |
| PR 2 历史 | 历史快照任务、表格里的进货/销售区间、售罄率、折扣、迷你曲线、展开大图 + 节日关键日期、各分店去年对比、**参考商品（系统推荐 + 人工指定）** | 部署主 API + Web |
| PR 3 增强 | 修订版重新上传的差异对比、截止前提醒未提交分店、移动端只读查看（可选） | 移动端部分可 OTA |

### 7.1 PR 2 实现要点（10-23）

- **快照表** `SupplierPreorderHistorySnapshot`（迁移 `20261023.002-supplier-preorder-history`，错误号 54003–54005）：一行 = 订单表 × 分店 × 我方商品；另有 `StoreCode = "*"` 的全部分店合计行（判定本品有没有历史、管理端显示销量），以及 `StoreCode = ProductCode = "*"` 的表头行，记计算状态（Queued / Running / Ready / Failed / NoWindow）、实际计算窗口与时间。
- **后台任务** `SupplierPreorderHistoryWorker` 每分钟检查：排队中的立即算；导入建表、发布、改窗口、「重新计算」都会排队；进行中且未截止的订单表每天悉尼 03:00 后刷新一次；计算中超过 2 小时视为卡住重算；失败的当天不反复重试。配置 `SupplierPreorders:HistoryWorkerEnabled=false` 可关。
- **折扣**：读调度器按 `batch-product-sales-discount-v3` 发布的日快照（与批量货号销量页同一读取器）；某天没有快照或对不上日统计时，那天的销量记为「未判定」，页面如实标出，不猜。
- **参考商品**：每次整表计算时，对「本品在窗口内全部分店净销量为 0」的商品按 5.1 打分；人工指定（Manual）与人工清除（Cleared）不会被覆盖，「交回系统推荐」在下次计算时重挑。人工指定后立即只补算该商品的快照。候选（同供应商、窗口内有销量或进货）在进程内缓存 30 分钟：KFC 约 4,900 个商品，生产库服务器端合计约十几秒。
- **权限**：下单列表只读本店与全部分店合计行；展开行的「各分店去年对比」只在可查看其他分店（或 Manage）时读取并返回。
- **筛选**：「去年有售」「去年打过折」只看本品自己的本店数据；「去年本店销量」排序含参考商品。

## 8. 已定口径（10-22）

1. 只按整箱下单：起订量 = 1 箱 = 箱规，不允许零散件数。
2. 导出时优先填回供应商原表，同时提供一份通用格式。
3. Dats 的进价取 **PNP PRICE**。
4. 「谁能替哪家店下单」用独立授权（见 3.4）；分店开启按每张订单表设置，可从去年复制。
5. 参考商品的定义见 5.1。
6. Dats 的箱规取 **Inner**（10-23 定）：下单单位是 1 个 Inner。Dats 原表各店列填的是**件数**（例：X005 Inner 24，Orion 填 240 = 10 个 Inner），所以填回原表时要写「箱数 × Inner」，不能写箱数。
7. 参考商品系统推荐：得分 ≥ 0.6 **自动带上**，标「系统推荐」，采购可改（10-23 定）。

## 9. 仍待确认

无。可按第 7 节分期开工。
