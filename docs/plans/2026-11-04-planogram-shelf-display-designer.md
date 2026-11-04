# 商品陈列图（Planogram）设计器 — 功能方案

日期：2026-11-04 · 状态：决策点已于 11-04 确认（见第 11 节），待进入批 1 · 设计图见 `docs/design/planogram-20261104/`

> 需求原文：「商品展示排班」—— 维护货架型号（高度、宽度、孔距、孔数）与挂钩型号（双钩带价签，长度、高度），给商品补包装长宽与每钩挂数，在页面上按真实尺寸拖拽排列、可切换货架，方案独立保存并导出版面图 PDF。
> 本文把它命名为「商品陈列图」（英文 Planogram），菜单挂在「商品与价格」下。

---

## 1. 目标与范围

**一期（本方案）**

| 模块 | 内容 |
|---|---|
| 基础资料 | 货架型号（洞洞板孔阵）、挂钩型号（双钩 / 单钩、带价签臂）、商品陈列参数（包装宽 × 高 × 厚、吊孔偏移、单件重、每钩挂数） |
| 设计器 | 按毫米等比绘制货架（节、立柱、底板、孔阵、价签），商品从左栏拖入并吸附孔位，孔位内拖动、复制、删除、撤销重做；越界 / 重叠即时标红；切换货架型号与节数后重新校验 |
| 方案管理 | 方案列表（货架型号、状态、关键字筛选，**不绑定分店**）、新建 / 另存为 / 复制 / 归档；草稿 → 发布生成版本快照 |
| 导出 | 版面图 + 执行清单 PDF（A3 / A4 横向），按同一序号对应，**中文 / English / 双语**三种版本（门店用英文版）；方案 JSON 导入导出 |

**二期（本方案只预留，不实现）**：底板 / 层板放置盒装商品、双面货架按面编辑、门店执行记录（拍照回传）、Excel 批量导入陈列参数、手机端只读查看、服务端批量出 PDF。

**不做**：3D 展示、自动排版（按销量自动生成版面）、与进货 / 补货数量联动。

## 2. 术语与坐标约定

- **节（Bay）**：一个 900 / 1200 mm 宽的货架单元；方案由 1～N 节横向拼成，节号自左起 1。
- **板面**：立柱之间、底板面以上可挂区域。板面宽 = 节宽 − 立柱宽；板面高 = 总高 − 底板面离地高。
- **孔位（col / row）**：列自左起 1，行自板顶起 1（已确认为默认；型号上保留「从底板起算」开关，只影响显示编号，内部仍存自顶行号）。
- **孔距与首孔边距**：都在货架型号上按实板量取后填写并可随时修改（decimal，支持 25.4 这类英寸换算值）；型号改动后，引用它的方案下次打开按新孔阵重算坐标并重新校验。
  - 孔心坐标（mm，板面左上为原点）：`x = firstHoleLeft + (col − 1) × pitchX`，`y = firstHoleTop + (row − 1) × pitchY`。
- **挂钩锚点**：双钩取左脚所在孔；横向占 `holeSpanX` 孔（双钩 = 2），纵向占 1 孔。
- **挂货基准**：挂货臂在挂孔下方 `dropMm`（钩高 H，实物约 50）；商品顶边 `y_top = y_hole + dropMm − hangHoleOffset`。
- **商品占位矩形**：中心 `x_c = x_hole + (holeSpanX − 1) × pitchX / 2`；宽 `max(包装宽, 价签架宽)`；高 = 包装高。
- **价签位置**：价签架是挂钩的固定部件，位置只随挂钩变：底边在挂货臂上方 `TagBottomAboveArmMm`（默认 10），即 `y_tagBottom = y_hole + dropMm − TagBottomAboveArmMm`，顶边再往上一个价签高。按默认值价签底边正好压在吊孔偏移 10 的商品顶边上，吊孔偏移更大的商品会被盖住一截（与实物一致）。价签计入占位：有价签臂的挂钩，占位矩形纵向从「价签顶边与商品顶边中较高者」起算，这样上一排商品底边到下一排价签顶边之间也要留满上下间距。
- **商品间距**：方案级设置 `GapXMm`（左右）与 `GapYMm`（上下），**默认各 10 mm**。占位矩形四周各外扩半个间距后再做相交判定，所以相邻两件商品之间至少留满一个间距；与板面边缘、底板面之间同样留满一个间距。设计器「方案信息」里可改，改动后整体重新校验。
- **每钩挂数（建议值）**：`⌊(臂长 − 臂端预留) ÷ 包装厚⌋`，可按挂钩型号手填覆盖；`挂数 × 单件重 > 承重` 时标黄提示。

## 3. 数据模型（5 张新表 + 1 张快照表，方案不绑定分店）

全部为新表，由迁移 `20261104.001-planogram` 创建；实体不进 `SqlSugarContext` 的 `tableTypes`（见 `docs/plans` 既有约束与 [`PosReceiptShareLinkSchema.cs`](../../services/backend/BlazorApp.Api/Data/SchemaMigrations/PosReceiptShareLinkSchema.cs) 写法）。错误号段用 54440–54449。

### 3.1 `PlanogramShelfModel` 货架型号

| 列 | 类型 | 说明 |
|---|---|---|
| Guid PK | nvarchar(50) | uuid7 |
| Code | nvarchar(50) 唯一 | 如 `G-S-PEG-90x30x120` |
| Name | nvarchar(100) | 单面洞洞板 900×1200 · 白 |
| SideType | tinyint | 1 单面 / 2 双面 |
| Color / Source | nvarchar(50/100) | 可空 |
| BayWidthMm, TotalHeightMm, BaseDepthMm, BaseTopHeightMm, UprightWidthMm | int | 外形尺寸 |
| HolePitchXMm, HolePitchYMm | decimal(6,2) | 孔距（允许 25.4） |
| HoleDiameterMm, FirstHoleLeftMm, FirstHoleTopMm | decimal(6,2) | 孔阵起点 |
| HoleCols, HoleRows | int | 可自动换算、允许手改 |
| RowNumberingFrom | tinyint | 1 自板顶 / 2 自底板 |
| IsActive + BaseEntity 审计列 | | 停用后已有方案可看不可新建 |

### 3.2 `PlanogramHookModel` 挂钩型号

| 列 | 说明 |
|---|---|
| Guid, Code 唯一, Name, Color | |
| ProngCount tinyint | 1 单钩 / 2 双钩 |
| HasPriceTagArm bit | 上臂带价签架 |
| ArmLengthMm, DropMm(H), WireDiameterMm, TipReserveMm | 尺寸；`TipReserveMm` 为臂端不挂货的预留 |
| TagWidthMm, TagHeightMm | 价签架 52 × 26 |
| TagBottomAboveArmMm | 价签架底边高于挂货臂的距离，默认 10；决定价签画在商品顶边何处 |
| HoleSpanX, HoleSpanY | 占孔 2 × 1 |
| LoadKg decimal(5,2) | 承重，可空 |
| IsActive + 审计列 | |

### 3.3 `ProductDisplaySpec` 商品陈列参数（按 `ProductCode` 一对一）

| 列 | 说明 |
|---|---|
| ProductCode PK nvarchar(50) | 关联 `Product.ProductCode`（业务键，不用 UUID，与全库 join 习惯一致） |
| WidthMm, HeightMm, DepthMm | int，必填 |
| HangHoleOffsetMm | int，默认 10 |
| HangHoleType | tinyint：1 圆孔 / 2 欧式挂孔 / 0 无（只能放层板，二期） |
| UnitWeightG | int，可空 |
| DefaultHookGuid | 可空，默认挂钩 |
| HookQtyOverridesJson | nvarchar(1000)：`{"<hookGuid>": 16}` 按挂钩型号手填覆盖 |
| Source | tinyint：1 人工量取 / 2 供应商资料 / 3 同套装主档 |
| Notes + 审计列 | |

> 商品主档现有 `Product / WarehouseProduct / DomesticProduct` 都没有长宽高字段（只有 `UnitVolume` 乘积与规格文本），所以单独建表，不动商品表；套装按套装自身 `ProductCode` 维护，多码子码默认沿用主档参数。

### 3.4 `Planogram` 方案头

| 列 | 说明 |
|---|---|
| Guid, Name, Notes, Tags | |
| GapXMm, GapYMm | int，商品左右 / 上下间距，默认 10 / 10（新建时取系统默认，可按方案改） |
| ShelfModelGuid, BayCount | 货架型号与节数 |
| Status | 0 草稿 / 1 已发布 / 2 已归档 |
| Version | 当前编辑版本号（每次发布 +1） |
| PublishedVersion, PublishedAt, PublishedBy | 最近发布 |
| ConflictCount, LastValidatedAt | 保存时服务端复算 |
| 审计列 | |

### 3.5 `PlanogramItem` 明细（一钩一行）

| 列 | 说明 |
|---|---|
| Guid, PlanogramGuid（索引） | |
| BayIndex, Face | 节号（1 起）、面（单面恒 0；双面二期） |
| ItemType | 1 挂钩商品 / 2 层板商品（二期） |
| ProductCode, HookModelGuid | |
| HoleCol, HoleRow | 锚点孔位 |
| PerHookQty | 保存时的每钩挂数快照（可空 = 用参数表） |
| SpecSnapshotJson | 保存时商品宽高厚快照，用于「参数改了」提示 |
| SortOrder | |

> 不设「面位」字段：同一商品横向连排就是多条明细（设计器提供「向右复制 N 个」「整行均分」）。执行清单一钩一行，店员照着上货最直观。

### 3.6 `PlanogramVersion` 发布快照

`Guid, PlanogramGuid, Version, LayoutJson(nvarchar(max)), PublishedBy, PublishedAt`。`LayoutJson` 含头、明细、所用货架 / 挂钩型号与商品参数的完整快照 —— 以后型号或参数改了，历史版本的 PDF 仍能按当时数据重出。

## 4. 布局引擎与校验（前后端同一套规则）

前端 `apps/web/src/pages/PosAdmin/Planogram/engine/geometry.ts` 为纯函数；后端 `Services/Planogram/PlanogramLayoutValidator.cs` 复算同样规则，保存接口返回冲突列表并写 `ConflictCount`。两边共用一份 JSON 夹具（`engine/fixtures/*.json`）跑测试，防止口径漂移。

```
toMm(shelf, bay, col, row)           → 孔心 mm 坐标（含节偏移）
footprint(shelf, hook, spec, item)   → {x1,y1,x2,y2}  商品占位矩形（含价签宽）
validate(shelf, items, gaps)         → [{itemGuid, code, detail}]   gaps = {x: GapXMm, y: GapYMm}
  OUT_OF_BOARD     左右越过板面 / 底边越过底板面 / 顶边越过板顶
  HOLE_OUT_OF_RANGE col+span-1 > HoleCols 或 row > HoleRows（切换货架后常见）
  OVERLAP          外扩半个间距后的矩形相交（即实际间隙 < GapXMm / GapYMm，默认 10 mm）
  EDGE_GAP         与板面左右边缘或底板面的间隙 < 对应间距
  HOOK_HOLE_CLASH  两个挂钩锚点占用同一孔
  OVERLOAD(warn)   挂数 × 单件重 > 承重
suggestQty(hook, spec)               → ⌊(ArmLength − TipReserve) ÷ Depth⌋，覆盖值优先
stats(items)                         → SKU 数、挂钩数、挂货件数、零售总值、板面利用率
```

设计器交互规则：
- 左栏只列出**已设陈列参数**的商品；未设的灰显并给「补充参数」入口。
- 拖入 / 拖动时实时吸附最近孔位，绿色 = 可放，红色 = 不可放（不可放时松手回弹）。
- 切换货架型号或减少节数：不自动挪动，逐项重新校验并标红，右上角「冲突 N · 定位」逐个跳转。
- 商品参数改动后，已引用的方案下次打开重新校验并提示「参数已变」（用 `SpecSnapshotJson` 比较）。
- 未保存离页用 `useBlocker` 拦截（仓库已用 Data Router）。

## 5. 接口（`api/react/v1`，`ApiResponse<T>` 包装）

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET/POST/PUT/DELETE | `planogram/shelf-models` | Manage（GET 为 View） | 货架型号；DELETE 仅未被引用时允许，否则只能停用 |
| GET/POST/PUT/DELETE | `planogram/hook-models` | 同上 | 挂钩型号 |
| GET | `planogram/product-specs?keyword&categoryGuid&supplier&status&page` | View | 联 `Product` 返回图片、名称、零售价、被引用数 |
| PUT | `planogram/product-specs/{productCode}` | Edit | 保存参数（含覆盖挂数） |
| GET | `planograms?shelfModelGuid&status&keyword&page` | View | 列表 |
| POST | `planograms` | Edit | 新建（名称、货架、节数、间距默认 10 / 10） |
| GET | `planograms/{guid}` | View | 头 + 明细 + 所引用型号与商品参数（设计器一次拿齐） |
| PUT | `planograms/{guid}` | Edit | 整体保存头 + 明细；服务端复算校验，返回 `conflicts` |
| POST | `planograms/{guid}/validate` | View | 只校验不保存 |
| POST | `planograms/{guid}/publish` | Manage | 有冲突拒绝；Version+1，写 `PlanogramVersion` |
| POST | `planograms/{guid}/duplicate` · `/archive` · `/restore` | Edit / Manage | |
| GET | `planograms/{guid}/versions` · `/versions/{v}` | View | 历史版本（供 PDF 重出） |
| GET/POST | `planograms/{guid}/export` · `planograms/import` | Edit | 方案 JSON |

分店范围：方案不绑定分店，持有 `Planogram.View` 的账号可见全部方案；不接 `ICurrentUserManageableStoreScopeService`。

## 6. 前端结构（`apps/web`）

```
src/pages/PosAdmin/Planogram/
  index.tsx                      页签：陈列方案 / 商品陈列参数 / 货架与挂钩型号
  PlanList/                      列表、导出 PDF 弹窗
  ProductSpecs/                  参数列表 + 编辑抽屉（尺寸示意图、按挂钩型号的挂数表）
  Models/ShelfModels.tsx, HookModels.tsx   型号列表 + 抽屉（孔阵 / 侧视预览 SVG）
  Designer/
    index.tsx                    路由 /pos-admin/planograms/:guid/design，keepAlive，useBlocker
    BoardCanvas.tsx              SVG 画布：孔阵 pattern、节 / 立柱 / 底板、标尺、商品与价签；
                                 画布内拖动用 pointer capture（同 PricingStrategies/IntervalEditor 写法）
    ProductLibraryPanel.tsx      左栏商品 / 挂钩；dnd-kit DndContext 把卡片拖到画布（已有依赖）
    InspectorPanel.tsx           右栏属性与统计；「方案信息」里含左右 / 上下间距两个输入（默认 10）
    useDesignerStore.ts          zustand：items、选中、undo/redo（快照栈，上限 50）
  engine/geometry.ts (+ .test.ts, fixtures/)
  export/PlanogramPrintPages.tsx 打印页 DOM（版面图 SVG 打印模式 + 执行清单表），文案走 i18n，lang 参数 zh / en / both
  export/exportPlanogramPdf.ts   动态 import jspdf / html2canvas，复用 StoreOrders/printUtils 的分页与打印
  planogramMessages.zh.json / .en.json  + sourceContract.test.ts
src/services/planogramService.ts
```

登记清单（照 MYOB 页的做法）：`router/routes.tsx` 懒加载 + `NAV_GROUPS`、`i18n/locales/{zh,en}.json` 的 `menu.planogram`、`types/permissions.ts`、`types/auth.ts` + `utils/access.ts`、`utils/webMenuPreview.ts`、`utils/webPortalAccess.ts`、`access.permission.test.ts`、新增 `router/planogramRoute.test.ts`、`pageMessagesContract.test.ts`；表格一律用 `MeasuredTable`。

包体：设计器为独立 lazy 页，不进首屏；PDF 依赖已固定在 `pdf` 异步 chunk（上限 665,600 B），本功能不新增依赖。

## 7. PDF 导出

**一期：前端出图**（零新依赖，和进货单 / 拣货单一致）
1. 用 `BoardCanvas` 同一渲染函数以「打印模式」出 SVG：白底、序号气泡、孔位列 / 行号、价签价格；执行清单为普通表格。
2. 组装成 `.planogram-pdf-page` 分页 DOM（A3 横向默认，每页 2 节；A4 横向每页 1 节），走 `printUtils.downloadElementPagesAsPdf` / `printElementPagesAsPdf`（html2canvas 2× → jsPDF）。
3. 商品实拍图为可选项，开启时经 `/api/react/v1/image-proxy` 加载避免画布跨域污染。
4. 语言：**中文**（默认，内部用）、**English**（给门店）、**双语**（标题与列名双语、商品名两行）。英文版商品名取 `Product.EnglishName`，为空时回退中文名并在清单里加星号；表头、图例、上货要点全部走 i18n 文案文件，不在组件里写死。
5. 商品实拍图**默认不印**，弹窗里可勾选。
6. 导出默认取**已保存版本**；有未保存改动时提示先保存。文件名 `Planogram_<方案>_v<版本>_<语言>_<日期>.pdf`。

**二期（按需）：服务端矢量 PDF** —— 后端已有 iTextSharp + `PosterCanvas` + `PdfCjkFontProvider`，若要批量导出、邮件下发或更清晰的矢量线条再做，布局几何沿用 `PlanogramLayoutValidator` 的同一套公式。

## 8. 权限与菜单

- 权限码：`Planogram.View`（看方案 / 参数 / 型号）、`Planogram.Edit`（建改方案、维护商品参数）、`Planogram.Manage`（型号维护、发布 / 归档）。`PermissionAliases` 设 Manage ⊇ Edit ⊇ View。
- 登记：`Permissions.cs`、`PermissionSeedData.cs`（中文名称 / 分类 / 描述）、`PermissionSurfaceCatalog.WebOnlyCodes`、迁移 `ApplySql` 幂等写入 `HbwebSysPermissions`（默认不授予任何角色，生产手动授权）、`NavigationService.FullMenu` 与 `HasBackendNavigationAccess`、移动端 `accessPermissions.json` 与 `role-menu-catalog.ts`（Web 菜单对照）。
- 菜单：「商品与价格 → 商品陈列图」，设计器不作为菜单项，只从列表进入。

## 9. 迁移、发布与测试

- 迁移：`Data/SchemaMigrations/PlanogramSchema.cs`（ApplySql + VerifySql），`SchemaMigrationRuntime` 接口与实现、`SchemaMigrationCoordinator.MainMigrationSteps` 末尾登记，`SchemaMigrationCoordinatorTests` 账本与下标、`SchemaMigrationSqlServerIntegrationTests` 用例。
- 发布顺序：`--schema=migrate` → 主 API → Web。纯后端 + Web，不涉及移动端原生包与 OTA。
- 后端测试（`BlazorApp.Api.Tests`，新文件记得加 `<Compile Include>`）：`PlanogramLayoutValidatorTests`（与前端共用夹具）、`PlanogramServiceSqliteTests`（CRUD、发布快照、有冲突拒绝发布、停用型号不可新建）、`SeedDataServiceTests` / `PermissionSurfaceCatalogTests` / `NavigationServiceTests` / `ControllerAuthorizationMetadataTests` 自动覆盖权限登记。
- 前端测试：`geometry.test.ts`（吸附、越界、重叠、建议挂数、切换货架）、`planogramRoute.test.ts`、`sourceContract.test.ts`、`pageMessagesContract.test.ts`；设计器目测用 esbuild 造数页（`docs/plans` 既有做法）。

## 10. 分批与估算

| 批次 | 内容 | 估算 |
|---|---|---|
| 批 1 | 迁移 + 5 张表 + 权限 + 货架 / 挂钩型号页 + 商品陈列参数页 | 后端 2.5 d · 前端 2 d |
| 批 2 | 设计器（画布、拖拽吸附、校验、属性栏、撤销重做）+ 保存 / 校验接口 + 方案列表 | 前端 5 d · 后端 1.5 d |
| 批 3 | 发布与版本快照、PDF 导出（版面图 + 执行清单，中 / 英 / 双语）、JSON 导入导出 | 前端 2.5 d · 后端 1 d |
| 合计 | 三个 PR 串行，每批可独立上线 | 约 14.5 人日 |

## 11. 已确认的决策（2026-11-04）

| # | 事项 | 结论 |
|---|---|---|
| 1 | 孔距与首孔边距 | 不定死，货架型号上按实板量取后填写，随时可改；默认值仍填 25 / 25 / 25 |
| 2 | 行号方向 | 默认自板顶 1 起 |
| 3 | 方案与分店 | **不绑定分店**，去掉 `StoreCode` 与分店筛选 |
| 4 | 明细粒度 | **一钩一行**，连排靠「向右复制 / 整行均分」 |
| 5 | 底板 / 层板 | 一期只画出底板，不放商品 |
| 6 | PDF 语言 | 默认中文；**需要英文版给门店**，另提供双语 |
| 7 | 命名 | 「商品陈列图 / Planogram」 |
| 8 | PDF 商品实拍图 | 默认不印，可勾选 |
| 9 | 商品间距（新增） | 方案级「左右间距 / 上下间距」，**默认各 10 mm**，校验与吸附都按它算 |

仍待实现阶段顺手确认的小项：英文版里「节 / 列 / 行 / 挂钩 / 每钩挂数」的英文用词（建议 Bay / Col / Row / Hook / Per hook），以及英文名为空的商品如何显示（建议回退中文名并加星号）。

## 附：设计图

| 文件 | 内容 |
|---|---|
| `01-designer.png` | 设计器主界面（拖拽中、选中项、冲突、吸附提示、统计） |
| `02-shelf-model.png` | 货架型号编辑抽屉（外形尺寸、孔阵、正视预览） |
| `02-hook-model.png` | 挂钩型号编辑抽屉（臂长 / 钩高 / 价签 / 占孔、侧视预览） |
| `03-product-spec.png` | 商品陈列参数列表 + 编辑抽屉（尺寸示意、按挂钩型号的挂数表） |
| `04-plan-list-export.png` | 方案列表 + 导出 PDF 弹窗 |
| `05-pdf-pages.png` | PDF 第 1 页版面图、第 2 页执行清单（中文） |
| `05b-pdf-pages-en.png` | 同上英文版（门店用） |

同目录的 `*.html` + `plano.js` 是设计稿源码（纯静态，含样例数据与几何换算），实现时可直接搬运画布渲染逻辑。

## 附录 A：批 1 接口契约（后端与前端共同依据）

前缀 `api/react/v1/planogram`，返回 `ApiResponse<T>`，列表用 `PagedResult<T>`（`items/total/page/pageSize`），JSON camelCase，时间为 ISO 字符串。权限：GET 需 `Planogram.View`；商品参数 PUT 需 `Planogram.Edit`；型号 POST/PUT/DELETE 需 `Planogram.Manage`。

**ShelfModelDto**：`guid, code, name, sideType(1单面/2双面), color?, source?, bayWidthMm, totalHeightMm, baseDepthMm, baseTopHeightMm, uprightWidthMm, holePitchXMm, holePitchYMm, holeDiameterMm, firstHoleLeftMm, firstHoleTopMm, holeCols, holeRows, rowNumberingFrom(1自板顶/2自底板), isActive, referencedPlanCount, updatedAt, updatedBy`（`referencedPlanCount` 只读，批 1 恒为 0）。
- `GET /shelf-models?keyword&isActive&page&pageSize`；`POST /shelf-models`；`PUT /shelf-models/{guid}`；`DELETE /shelf-models/{guid}`（被方案引用时返回错误码 `PLANOGRAM_MODEL_IN_USE`，提示改为停用）。
- 校验：code 必填且唯一（`PLANOGRAM_CODE_DUPLICATE`）；全部尺寸 > 0；孔距 0.5–200；`holeCols/holeRows ≥ 1`。孔数缺省或为 0 时后端按 `⌊(板面宽 − 2×首孔边距) ÷ 孔距⌋ + 1` 自动换算（板面宽 = bayWidthMm − uprightWidthMm，板面高 = totalHeightMm − baseTopHeightMm）。

**HookModelDto**：`guid, code, name, color?, prongCount(1/2), hasPriceTagArm, armLengthMm, dropMm, wireDiameterMm, tipReserveMm, tagWidthMm, tagHeightMm, tagBottomAboveArmMm(默认10), holeSpanX, holeSpanY, loadKg?, isActive, referencedPlanCount, updatedAt, updatedBy`。接口同上，路径 `/hook-models`。`hasPriceTagArm=false` 时 tag 三个字段可为 0。

**ProductSpecListItemDto**（列表行，联 Product）：`productCode, itemNumber, barcode, productName, englishName?, productImage?, retailPrice, categoryName?, hasSpec, referencedPlanCount, spec?(ProductDisplaySpecDto), updatedAt?, updatedBy?`。
**ProductDisplaySpecDto**：`productCode, widthMm, heightMm, depthMm, hangHoleOffsetMm(默认10), hangHoleType(0无/1圆孔/2欧式), unitWeightG?, defaultHookGuid?, hookQtyOverrides(Record<hookGuid, int>), source(1人工/2供应商/3套装主档), notes?`。
- `GET /product-specs?keyword&status(all|set|unset)&page&pageSize`（keyword 匹配货号/条码/名称/英文名，按 ProductCode 业务键联 Product，仅 IsActive 且未软删商品）。
- `GET /product-specs/{productCode}` → ProductSpecListItemDto；`PUT /product-specs/{productCode}` 保存 ProductDisplaySpecDto（宽高厚 > 0；hookQtyOverrides 中的值 ≥ 1 且 hookGuid 必须存在）；`DELETE /product-specs/{productCode}` 清除参数（Manage 权限，批 1 恒可执行，批 2 起被方案引用时拒绝）。

**错误码**：`PLANOGRAM_VALIDATION`（字段不合法，`details` 给字段列表）、`PLANOGRAM_NOT_FOUND`。

## 附录 B：批 2 几何规则与方案接口契约

### B.1 几何规则（前端 `engine/geometry.ts` 与后端 `PlanogramLayoutValidator` 必须逐条一致，共用 `apps/web/src/pages/PosAdmin/Planogram/engine/fixtures/layout-cases.json`）

坐标系：每一节（bay）独立，原点为该节「板面」左上角；板面宽 `boardW = bayWidthMm − uprightWidthMm`，板面高 `boardH = totalHeightMm − baseTopHeightMm`；x 向右、y 向下，单位 mm，浮点比较容差 0.001。各节之间不互通，商品不能跨节。

1. 孔心：`x(col) = firstHoleLeftMm + (col − 1) × holePitchXMm`，`y(row) = firstHoleTopMm + (row − 1) × holePitchYMm`（col、row 均从 1 起，行号始终自板顶，`rowNumberingFrom` 只影响显示）。
2. 挂钩占孔：列区间 `[col, col + holeSpanX − 1]`，行区间 `[row, row + holeSpanY − 1]`。
3. 挂钩中心 `xc = x(col) + (holeSpanX − 1) × holePitchXMm / 2`。
4. 商品占位矩形：
   - 有效宽 `ew = max(spec.widthMm, hook.hasPriceTagArm ? hook.tagWidthMm : 0)`，`left = xc − ew/2`，`right = xc + ew/2`；
   - 商品顶 `productTop = y(row) + hook.dropMm − spec.hangHoleOffsetMm`，商品底 `bottom = productTop + spec.heightMm`；
   - 价签顶 `tagTop = y(row) + hook.dropMm − hook.tagBottomAboveArmMm − hook.tagHeightMm`（仅 hasPriceTagArm）；`top = hasPriceTagArm ? min(productTop, tagTop) : productTop`。
5. 冲突（每条含 `itemGuid`、可选 `otherItemGuid`、`code`、`severity`、`message`），按此顺序判定：
   - `BAY_OUT_OF_RANGE`(error)：`bayIndex < 1` 或 `> bayCount`。
   - `PRODUCT_SPEC_MISSING` / `HOOK_MODEL_MISSING`(error)：参数或挂钩型号不存在（停用不算缺失）；缺失的条目不再参与后续几何判定。
   - `HOLE_OUT_OF_RANGE`(error)：`col < 1`、`row < 1`、`col + holeSpanX − 1 > holeCols`、`row + holeSpanY − 1 > holeRows`。该条目不再参与后续几何判定。
   - `HOOK_HOLE_CLASH`(error)：同一节内两条目的占孔区间在行、列上都相交（成对报，每条目各报一条，带 otherItemGuid）。
   - `OUT_OF_BOARD`(error)：`left < 0` 或 `right > boardW` 或 `top < 0` 或 `bottom > boardH`。
   - `EDGE_GAP`(error)：未越界但 `left < gapXMm` 或 `boardW − right < gapXMm` 或 `top < gapYMm` 或 `boardH − bottom < gapYMm`。
   - `OVERLAP`(error)：同一节内两矩形各向外扩 `gapXMm/2`（水平）、`gapYMm/2`（垂直）后相交面积 > 0（即净间隙 < 间距）；成对报，各带 otherItemGuid。
   - `OVERLOAD`(warn)：`perHookQty × spec.unitWeightG / 1000 > hook.loadKg`（二者齐全才判），`perHookQty` 缺省取 `hookQtyOverrides[hookGuid]`，再缺省取 `⌊(armLengthMm − tipReserveMm) ÷ depthMm⌋`。
6. 统计：`itemCount`（条数）、`skuCount`（不同 ProductCode）、`unitCount`（Σ 每钩挂数，口径同 OVERLOAD 的 perHookQty）、`retailValue`（Σ 每钩挂数 × Product.RetailPrice）、`fillPercent = round(Σ 商品 widthMm×heightMm ÷ (boardW × boardH × bayCount) × 100)`（不计价签）、`conflictCount`（仅 error 级的条目数，成对冲突按涉及条目去重计数）。
7. 吸附（仅前端）：拖动时取最近的孔位，使挂钩锚点孔心距指针最近，再按 4 判定可放或不可放。

### B.2 方案接口（前缀 `api/react/v1/planogram`，`ApiResponse<T>`；GET 需 View，POST/PUT 保存与复制需 Edit，归档/恢复/删除需 Manage）

**PlanogramListItemDto**：`guid, name, notes?, shelfModelGuid, shelfModelName, bayCount, gapXMm, gapYMm, status(0草稿/1已发布/2已归档), version, publishedVersion?, publishedAt?, itemCount, skuCount, unitCount, fillPercent, conflictCount, updatedAt, updatedBy`。
**PlanogramItemDto**：`guid, bayIndex, face(恒0), itemType(恒1), productCode, hookModelGuid, holeCol, holeRow, perHookQty?(null=按规则), sortOrder`；服务端保存时另存 `specSnapshotJson`（宽高厚）并在读取时返回 `specChanged: bool`（快照与当前参数不一致）。
**ConflictDto**：`itemGuid, otherItemGuid?, code, severity('error'|'warn'), message`。
**PlanogramDetailDto**：`header(同 ListItem 字段) + rowVersion(string, 取 updatedAt 的 ISO 刻度) + items[] + shelfModel(ShelfModelDto) + hookModels[](全部启用 + 被引用的停用型号) + productSpecs[](ProductSpecListItemDto，仅方案内商品) + conflicts[] + stats{itemCount,skuCount,unitCount,retailValue,fillPercent,conflictCount}`。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/planograms?shelfModelGuid&status&keyword&page&pageSize` | 列表，keyword 匹配名称与明细商品货号/名称 |
| POST | `/planograms` | 新建：`{name, notes?, shelfModelGuid, bayCount(1–12), gapXMm?(默认10), gapYMm?(默认10)}` → Detail（status=0、version=1） |
| GET | `/planograms/{guid}` | Detail（读取时复算 conflicts 与 specChanged） |
| PUT | `/planograms/{guid}` | 整体保存：`{name, notes?, shelfModelGuid, bayCount, gapXMm, gapYMm, items[], rowVersion}`；rowVersion 不一致返回 `PLANOGRAM_STALE`；items 整体替换；服务端复算 conflicts 写 conflictCount；**已发布(1)的方案被保存后 version+1、status 回到 0**；已归档(2)不可保存（`PLANOGRAM_ARCHIVED`）；明细上限 2000 条 |
| POST | `/planograms/{guid}/validate` | 请求体同 PUT（不含 rowVersion），不落库，返回 `{conflicts[], stats}` |
| POST | `/planograms/{guid}/duplicate` | `{name}` → 新 Detail（草稿，version=1，明细全复制，guid 全新） |
| POST | `/planograms/{guid}/archive`、`/restore` | 归档/恢复为草稿 |
| DELETE | `/planograms/{guid}` | 仅草稿且从未发布可删，否则 `PLANOGRAM_NOT_DELETABLE` |

批 2 同时补强批 1：商品参数清除、货架/挂钩型号删除遇到被方案引用时按 `PLANOGRAM_MODEL_IN_USE`/`PLANOGRAM_SPEC_IN_USE` 拒绝（批 1 的 `referencedPlanCount` 已是实际计数）；货架型号被已存在方案引用时，**不允许**改动 `bayWidthMm/totalHeightMm/uprightWidthMm/baseTopHeightMm/孔距/首孔边距/孔数`（返回 `PLANOGRAM_MODEL_LOCKED`，提示复制为新型号），停用与改名称/颜色不受限——避免已排版方案被悄悄改坏。

## 附录 C：批 3 发布、版本快照、JSON 导入导出契约（前缀同附录 B）

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| POST | `/planograms/{guid}/publish` | Manage | 请求体 `{rowVersion}`；服务端先按当前存库内容复算冲突，**有 error 级冲突**返回 `PLANOGRAM_HAS_CONFLICTS`（details 带冲突列表）；明细为空返回 `PLANOGRAM_EMPTY`；已归档返回 `PLANOGRAM_ARCHIVED`；当前 version 已有快照（即未改动再次发布）返回 `PLANOGRAM_ALREADY_PUBLISHED`；rowVersion 不一致 `PLANOGRAM_STALE`。成功：写 `PlanogramVersion`（Version = 头的 version，同 (PlanogramGuid, Version) 唯一），头 `status=1, publishedVersion=version, publishedAt/By`，返回 PlanogramDetailDto |
| GET | `/planograms/{guid}/versions` | View | `[{version, publishedAt, publishedBy, itemCount, skuCount, unitCount}]` 按版本倒序 |
| GET | `/planograms/{guid}/versions/{version}` | View | PlanogramSnapshotDto（见下），用于按当时数据重出 PDF |
| GET | `/planograms/{guid}/export` | Edit | 返回 PlanogramExportDto（JSON 对象，前端存为文件） |
| POST | `/planograms/import` | Edit | 请求体 PlanogramExportDto，另可带 `name`（覆盖名称）；创建新草稿方案（version=1），返回 PlanogramDetailDto |

**PlanogramSnapshotDto**：`{version, publishedAt, publishedBy, plan:{guid,name,notes,bayCount,gapXMm,gapYMm}, shelfModel:ShelfModelDto, hookModels:HookModelDto[](仅方案用到的), products:[{productCode, itemNumber, barcode, productName, englishName?, productImage?, retailPrice, spec:ProductDisplaySpecDto}], items:PlanogramItemDto[], stats:{itemCount,skuCount,unitCount,retailValue,fillPercent,conflictCount}}`。快照内容在发布时固化为 `LayoutJson`，之后型号、参数、商品名、价格再变，历史版本读出来仍是当时的值。
**PlanogramExportDto**：`{formatVersion:1, exportedAt, plan:{name,notes?,bayCount,gapXMm,gapYMm}, shelfModelCode, items:[{bayIndex,productCode,hookModelCode,holeCol,holeRow,perHookQty?}]}`，用编码而不是 guid，便于跨环境搬运。导入时 `shelfModelCode`、各 `hookModelCode`、各 `productCode` 解析不到返回 `PLANOGRAM_IMPORT_UNRESOLVED`，details 为 `[{kind:'shelf'|'hook'|'product', code}]`（去重）；`formatVersion` 不认识返回 `PLANOGRAM_VALIDATION`；明细上限 2000。
**列表补充**：`PlanogramListItemDto` 增加 `hasUnpublishedChanges: bool`（`status==0 && publishedVersion != null`，批 2 之后保存已发布方案会使其回草稿）。

## 附录 D：商品陈列参数的分类过滤与批量修改（11-04 追加，用户需求）

需求：商品陈列参数页要能按**仓库分类**和**本地供应商分类**过滤，并能**批量修改参数**；设计器左栏的商品库同样要有这两个分类过滤。

### D.1 过滤条件（`GET /product-specs`、设计器商品库、批量修改的「按当前筛选」共用）

在原有 `keyword`、`status` 之外新增（均可选，查询串里数组用重复键，如 `warehouseCategoryGuids=a&warehouseCategoryGuids=b`）：

| 参数 | 语义（与商品管理页保持一致） |
|---|---|
| `warehouseCategoryGuids[]` | 仓库分类 GUID，**含全部子分类**（按仓库分类树展开）；商品的 `WarehouseCategoryGUID` 落在展开集合内；未设仓库分类的商品不参与。任何供应商的商品都可匹配 |
| `localSupplierCode` | 单个本地供应商；与商品列表的 `LocalSupplierCodes` 过滤一致（编码 `200` 视为 Hot Bargain，含供应商为空的商品） |
| `supplierCategoryGuids[]` | 本地供应商分类 GUID，**含子分类**；语义与商品列表 `SupplierCategoryGUIDs` 完全一致：`LocalSupplierCategoryFilter.ExpandAsync` 按所属树分流，供应商分类走 `LocalSupplierCategoryProductAssignment`（且归属供应商须等于商品当前供应商），200 的供应商分类就是仓库分类 |
| `supplierCategoryUnassignedOnly` | 只看「未归类」：语义与商品列表一致 |

后端实现要求：把 `ProductReactService.ApplySupplierCategoryFiltersAsync` 的主体**抽成** `LocalSupplierCategoryFilter` 里的共用静态方法（行为逐字不变），商品列表与陈列参数列表共用，不要复制一份；`ProductReactService` 改为调用它，并用既有的 `LocalSupplierCategoryServiceTests` / SQL Server 集成测试证明商品列表行为不变。

### D.2 批量修改：`POST /product-specs/batch`（权限 `Planogram.Edit`）

请求 `ProductSpecBatchRequest`：

```json
{
  "target": { "productCodes": ["..."], "filter": { "keyword": "", "status": "all", "warehouseCategoryGuids": [], "localSupplierCode": null, "supplierCategoryGuids": [], "supplierCategoryUnassignedOnly": false } },
  "patch": { "widthMm": 70, "heightMm": 150, "depthMm": 25, "hangHoleOffsetMm": 10, "hangHoleType": 2, "unitWeightG": null, "defaultHookGuid": "…", "source": 1, "notes": null },
  "hookQtyOverrides": { "mode": "merge", "values": { "<hookGuid>": 12 } },
  "createMissing": false,
  "dryRun": true
}
```

- `target` 二选一：`productCodes`（勾选的货号，≤ 2000）或 `filter`（按当前筛选，服务端解析出全部匹配商品，匹配数 > 2000 返回 `PLANOGRAM_BATCH_TOO_MANY`，details 带匹配数）。`filter` 里 `status` 对批量无意义时按原值照常过滤（前端传 `all`）。
- `patch` 是**只含被勾选字段**的对象：键存在才修改，缺省 = 不动；可空字段（`unitWeightG`、`defaultHookGuid`、`notes`）传 `null` = 清空；非空字段（宽高厚、吊孔偏移、吊孔类型、来源）传 `null` 视为校验错误。后端用 `Dictionary<string, JsonElement>` 解析，只接受白名单键，出现未知键返回 `PLANOGRAM_VALIDATION`（details 列字段名）。
- `hookQtyOverrides`：`mode` 取 `replace`（整体替换为 `values`；`values` 为空对象 = 清空全部覆盖）或 `merge`（只覆盖给出的挂钩，其余保留）；缺省 = 不动。挂数 ≥ 1，hookGuid 必须存在。
- `createMissing`：目标里**未设参数**的商品是否创建。创建要求 `patch` 同时含 `widthMm`、`heightMm`、`depthMm`（吊孔偏移缺省 10、吊孔类型缺省 2、来源缺省 1），否则这些商品计入 `skipped`（原因 `MISSING_DIMENSIONS`）。`false` 时未设参数的商品计入 `skipped`（原因 `NO_SPEC`）。
- 逐商品校验，不合法的商品跳过而不是整批失败：改后 `hangHoleOffsetMm > heightMm`（吊孔类型非「无」时）→ 跳过，原因 `OFFSET_EXCEEDS_HEIGHT`；商品不存在或已停用 / 已删除 → `PRODUCT_NOT_FOUND`。补丁本身不合法（范围、未知键、`values` 非法）→ `PLANOGRAM_VALIDATION`，整批不执行。
- 写库：所有通过校验的商品在**一个事务**里写入；写入的行 `UpdatedBy` 为当前用户、`UpdatedAt` 更新；`Source` 缺省保持原值（新建取 1）。与现有单个保存一致：`SpecSnapshot` 之类的方案侧快照不在这里动，已引用的方案下次打开自然标「参数已变」。
- `dryRun: true`：完整走一遍校验与计数，**不写库**。

响应 `ProductSpecBatchResultDto`：

```json
{ "dryRun": true, "matched": 120, "created": 0, "updated": 0, "unchanged": 0,
  "willCreate": 30, "willUpdate": 85, "willStayUnchanged": 2, "skipped": 3,
  "skippedItems": [ { "productCode": "…", "itemNumber": "HB038-158", "productName": "…", "reason": "NO_SPEC" } ],
  "changedTotal": 115,
  "changedItems": [
    { "productCode": "…", "itemNumber": "HB038-158", "productName": "…", "action": "update",
      "before": { "widthMm": 120, "notes": "旧备注", "hookQtyOverrides": { "<hookGuid>": 6 } },
      "after":  { "widthMm": 70,  "notes": null,      "hookQtyOverrides": { "<hookGuid>": 6, "<hookGuid2>": 12 } } },
    { "productCode": "…", "itemNumber": "HB038-159", "productName": "…", "action": "create",
      "before": null,
      "after":  { "widthMm": 70, "heightMm": 150, "depthMm": 25, "hangHoleOffsetMm": 10, "hangHoleType": 2, "source": 1,
                  "hookQtyOverrides": { "<hookGuid2>": 12 } } }
  ] }
```

`dryRun` 时 `created/updated/unchanged` 恒为 0，计划数在 `will*`；真正执行时 `will*` 恒为 0，结果在 `created/updated/unchanged`。`skippedItems` 最多返回 200 条（`skipped` 是真实总数）。「unchanged」= 补丁应用后与现有值完全相同。

改前改后明细 `changedItems`（预览表用；dryRun 与真正执行都返回，内容由同一份计算得出）：

- 只收将创建 / 将更新（执行时为已创建 / 已更新）的商品，**不变与跳过的不进这张表**；按货号排序取前 **50** 条，`changedTotal` 是真实总数（= 创建数 + 更新数）。
- `action`：`create` / `update`。`before` / `after` 的键名同 `patch` 白名单（`widthMm`、`heightMm`、`depthMm`、`hangHoleOffsetMm`、`hangHoleType`、`unitWeightG`、`defaultHookGuid`、`source`、`notes`），挂数覆盖用对象键 `hookQtyOverrides`（整份覆盖，键排序；没有覆盖时为 `{}`）。
- `update`：`before` / `after` **只含本次实际改变的字段**（补丁给了但值没变的字段不出现）；清空的可空字段在 `after` 里显式为 `null`。
- `create`：`before` 恒为 `null`（该键始终输出，不受全局「null 不输出」影响）；`after` 给出宽高厚、吊孔偏移 / 吊孔类型 / 来源（含缺省值）与其他已设置的字段，没设置的可空字段（单件重、默认挂钩、备注）与空的挂数覆盖不出现。

错误码新增：`PLANOGRAM_BATCH_TOO_MANY`、`PLANOGRAM_BATCH_EMPTY`（目标为空或没有任何匹配）、`PLANOGRAM_BATCH_CONFLICT`（真正执行时的并发冲突，整批已回滚，提示「有其他人同时修改了这些商品，请重新预览后再执行」，前端应引导重新预览）：① 创建撞了主键 / 唯一约束（别人同时创建了同一货号的参数）；② 任一批更新的实际影响行数少于该批应更新的行数（别人同时清除了参数），不会记成「已更新」。其他数据库错误仍按 500 处理（同样整批回滚）。注：执行请求会按当时的数据重新计算，所以「预览之后、点确认之前」被清除参数的商品在执行结果里如实计为跳过（`NO_SPEC`，或勾选创建时计为新建），只有执行请求自身「计算之后、写入之前」被改动才报冲突。

### D.3 前端

- 商品陈列参数列表：筛选行增加「仓库分类」（树选择，可多选）与「本地供应商分类」（供应商 → 分类级联，含「未归类」；供应商 200 的分类就是仓库分类，这里隐藏 200，避免与「仓库分类」重复）；分类树与供应商列表复用商品管理页已有的 `warehouseCategoryService`、`localSupplierCategoryService` 与 `PosAdmin/ProductManagement/supplierCategory*` 纯函数。
- 列表增加行选择；选中后工具栏出现「批量修改参数」；也提供「按当前筛选的全部 N 个」入口（N 为列表总数，超过 2000 提示缩小筛选）。
- 批量修改弹窗：目标摘要（选中 N / 筛选 N，已设 X、未设 Y）；每个字段一行带「本次修改」勾选；「未设参数的商品」选项（跳过 / 创建，创建时必须勾选并填齐宽高厚）；每钩挂数覆盖按挂钩型号设置（合并 / 整体替换）；先「预览」（dryRun）看计划数与跳过明细，再「确认执行」；执行后显示结果并刷新列表。
- 设计器左栏商品库：同样的两个分类过滤，窄栏里收进一个「筛选」按钮（弹层里放两个控件和「清除」），有条件时按钮高亮并显示条数。

### D.4 追加：商品等级过滤与选定的设计（11-04 用户确认）

- 过滤新增 `grades[]`：取值 `A`/`B`/`C`/`D` 与 `none`（未分级），多选取并集；语义按 `ProductGrade`（`ProductCode` 关联、未删除）。同样适用于列表、设计器商品库与批量修改的 `target.filter`。`ProductSpecListItemDto` 增加只读 `grade`。
- 用户选定设计（图在 `docs/design/planogram-20261104/filter-batch/`）：
  - 陈列参数页 = **方案 A 一排筛选条**（`1-list-A.png`）：搜索、状态、仓库分类、本地供应商分类、等级标签（A 核心 / B 关注 / C 停产 / D 清仓 / 未分级）、清除筛选；勾选后表格上方出现蓝色操作条（已选 N · 批量修改参数 · 取消选择 · 按当前筛选的全部 N 个批量修改）；表格第一列显示**货号（itemNumber）为主、条码为辅**，新增「等级」「仓库分类」两列。
  - 批量修改 = **方案 1 一个弹窗**（`2-batch-1.png`）：顶部目标切换（已选 / 当前筛选）与已设、未设计数；左边字段开关列表，右边每钩挂数覆盖、未设参数商品的处理、预览结果（将创建 / 将更新 / 不变 / 跳过与改前改后明细）；底部「重新预览」「确认执行（N 个）」，改了任何输入预览失效。
  - 设计器商品库 = **方案 a「筛选」按钮 + 弹层**（`3-designer-library.png` 左）：仓库分类、本地供应商分类、商品等级，弹层底部显示符合数、清除、完成；有条件时按钮高亮显示条数，列表上方显示可逐个删除的条件标签。
  - 方案 3「表格内批量录入（每行值不同）」本次不做。
