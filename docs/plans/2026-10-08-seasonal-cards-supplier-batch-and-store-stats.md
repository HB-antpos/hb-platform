# 节日贺卡：供应商 + 批量填报 + 后台分店统计 实现方案

- 日期：2026-10-08
- 设计稿：<https://claude.ai/artifact/77TMqPprKL3tKUpkJh1rve>（移动端填报、覆盖确认、后台分店统计、单店明细抽屉）
- 范围：后端（迁移 + 接口 + 权限）、移动端 `seasonal-cards` 页面改版、Web 新增「分店填报统计」页

## 1. 已确认的业务口径

| 项 | 口径 |
|---|---|
| 填报内容 | 节日过后的**剩余数量**（库存快照），不是进货量 |
| 填报维度 | 分店 + 年份（默认今年）+ 节日 + 供应商 + 卡片价格类型（$1/$2/$3/其他） |
| 一次提交 | 一个「分店 + 年份 + 节日 + 供应商」组合的 4 个价格**整组提交**；空着的价格按 0 记录 |
| 重复提交 | **提醒 + 覆盖**：进入页面预填上次数量；未修改不能提交；修改后弹「上次 → 本次」确认；新记录生效，旧记录保留为历史 |
| 统计口径 | 每个「分店 + 年份 + 节日 + 供应商」取**最新一批**作为当前生效值；分店合计 = 各供应商当前生效值之和 |
| 供应商来源 | 复用 `LocalSupplier`（本地/澳洲供应商，实体 `HBLocalSupplier`），下拉走已有 `GET /api/react/v1/local-suppliers/active`（仅需登录） |

## 2. 生产现状（2026-10-08 只读核对 HBweb）

- `SeasonalCardCatalog`：20 行、全部启用（5 节日 × 4 价格），目录无需补数据。
- `SeasonalCardRemainingSubmission`：**仅 5 行**，1 家分店，全部是 2026-05-27 两分钟内提交（疑似测试）；不存在同组合重复行。
  → 历史行没有供应商、没有批次，影响可以忽略：新列可空，旧行在统计中归入「未指定供应商」。
- `LocalSupplier` 启用中 122 家 → 供应商选择必须带搜索。
- 启用分店 `Store(IsDeleted=0, IsActive=1)` 28 家。
- 权限表已有 `SeasonalCards.Remaining.ViewManagedStore` / `SubmitManagedStore`。

## 3. 数据模型与迁移

### 3.1 `SeasonalCardRemainingSubmission` 新增 3 个可空列

| 列 | 类型 | 说明 |
|---|---|---|
| `LocalSupplierCode` | `nvarchar(64) NULL` | 对齐 `LocalSupplier.LocalSupplierCode` 长度 |
| `SupplierName` | `nvarchar(128) NULL` | 名称快照，服务端按编码查表写入，不信任客户端 |
| `BatchGuid` | `nvarchar(50) NULL` | 同一次整组提交的 4 行共用一个批次号；覆盖判定、历史时间线都以批次为单位 |

`SeasonalCardCatalog` **不加**供应商：目录仍是「节日 × 价格」，供应商在每次填报时选择。

索引：`IX_SeasonalCardRemainingSubmission_Year_Type_Store`
`(SeasonYear, CardType, StoreCode, LocalSupplierCode, SubmittedAt DESC) INCLUDE (BatchGuid, PriceOption, RemainingQuantity, UnitPrice)`，
用 `IF NOT EXISTS (sys.indexes …)` 守护。

### 3.2 迁移 `20261009.001-seasonal-card-supplier-batch`

照抄 `20261008.002`（`StoreReceiptTermsSchema.cs`）：

1. 新建 `Data/SchemaMigrations/SeasonalCardSupplierBatchSchema.cs`，错误号区间 **52400–52401**：
   - `ApplySql`：表不存在就 `THROW 52400`；事务内 `COL_LENGTH(...) IS NULL` 再 `ALTER TABLE ADD`；建索引；
     以 `WHERE NOT EXISTS` 幂等插入新权限码 `SeasonalCards.Remaining.ViewAllStores` 到 `HbwebSysPermissions`（参照 `StoreCashManagementSchema.cs:269-291`）。
   - `VerifySql`：只读核对 3 列的类型、长度（nvarchar 的 `max_length` 按字节计 = 声明长度 × 2）、可空性，以及索引和权限行是否存在。
2. `SchemaMigrationCoordinator.cs` 加常量，并在 `MainMigrationSteps` 末尾追加一步。
3. `SchemaMigrationRuntime.cs` 的接口和实现各加 `Apply…Async`、`Verify…Async`。
4. 实体加 `[SugarColumn]` 属性（SQLite 测试靠 CodeFirst 自动带出新列）。贺卡表本来就在 `tableTypes` 里，**保持原样，不移出**。

> 生产必须显式执行 `--schema=migrate`，并且**先于**新版后端上线（新实体会查这些列）。

## 4. 后端接口

路由前缀不变：`api/react/v1/seasonal-card-remaining`。

### 4.1 门店侧（移动端用）

| 接口 | 权限 | 说明 |
|---|---|---|
| `GET overview?storeCode&seasonYear&localSupplierCode` | `SubmitManagedStore`（属于填报流程，与目录同权限） | **新增**。返回 5 个节日各自的当前生效批次（`batchGuid`、`submittedAt`、`submittedByName`、4 个价格的数量/单价）；没填过的返回 null。一次调用同时支撑节日网格的「已填/待填」标记和预填 |
| `POST submissions/batch` | `SubmitManagedStore` | **新增**。见 4.2 |
| `GET submissions` / `GET submissions/{guid}` | `ViewManagedStore` | 保留；DTO 增加 `localSupplierCode`、`supplierName`、`batchGuid`；查询可按供应商筛 |
| `POST submissions`（单条） | `SubmitManagedStore` | **保留兼容**：OTA 普及前旧版 App 还会调用；旧调用不带供应商、不写批次号，统计按「历史行」口径处理 |
| `GET catalog` | `SubmitManagedStore` | 不变 |

### 4.2 `POST submissions/batch` 规则

请求体：

```json
{
  "storeCode": "1013",
  "seasonYear": 2026,
  "cardType": 1,
  "localSupplierCode": "SUP-A01",
  "expectedPreviousBatchGuid": "…或 null",
  "remark": "可选",
  "items": [
    { "catalogGuid": "…", "remainingQuantity": 120 },
    { "catalogGuid": "…", "remainingQuantity": 0, "customUnitPrice": 4.5 }
  ]
}
```

服务端校验，全部通过才写入：

1. 店铺范围沿用 `ResolveManagedStoreAccessAsync`（店长只认主分店，与现有提交口径一致）。
2. 供应商：重新查 `HBLocalSupplier`，要求 `!IsDeleted && Status == 1`，名称快照由服务端写入。
3. `items` 必须**恰好覆盖**该节日全部启用的目录项（4 项，不多不少，不重复），保证每批都是完整快照；数量为大于等于 0 的整数；「其他」价格沿用 `ResolveUnitPrice`（数量大于 0 时必须填大于 0 的单价）。
4. **并发保护**：查出该组合当前的最新批次，它的 `BatchGuid` 与 `expectedPreviousBatchGuid` 不一致时返回错误码 `SEASONAL_CARD_STALE`，`details` 带回最新批次。前端提示「已被 X 于 HH:mm 更新」，并刷新对比。（沿用本控制器惯例：HTTP 200 + `success=false` + `errorCode`，下同。）
5. **无变化拒绝**：4 项数量和单价都与当前生效批次相同时，返回错误码 `SEASONAL_CARD_NO_CHANGES`，避免刷新「最后提交时间」。
6. 4 行在**同一事务**里插入，共用一个 `BatchGuid`（参照 `StoreCashService.Deposits.cs:141-160`）。

### 4.3 后台统计（Web 用）

新权限码 `SeasonalCards.Remaining.ViewAllStores`（「查看全部分店贺卡填报」）。按惯例**不写入任何角色模板**，上线后在角色管理里授权。

访问范围：由控制器策略 `ViewAllStores` 把关（管理员隐含），服务内不再按分店收口；不复用 `ResolveManagedStoreAccessAsync`，因为它会拒绝非店长角色。

应填报分店：启用分店排除测试店与仓库，名单读配置 `SeasonalCards:StatsExcludedStoreCodes`，缺省 `1006`（HB Warehouse）、`1042`（TestStore）；汇总接口返回 `excludedStores` 供页面注明口径。

| 接口 | 说明 |
|---|---|
| `GET admin/summary?seasonYear&cardType&localSupplierCode?&priceOption?&storeCodes?` | 指标（已填/未填分店数、合计数量、合计金额、平均每店）；分店行（各价格数量、合计、供应商列表、最后提交时间与提交人、是否已填）；按价格合计；按供应商合计；未填报分店名单 |
| `GET admin/stores/{storeCode}?seasonYear&cardType` | 单店明细：供应商 × 价格的当前生效矩阵；全部批次时间线（标「当前生效 / 已被取代」）；去年同节日的合计 |

计算方式：先按筛选条件把明细行全部取出，再在内存里 `GroupBy(StoreCode, LocalSupplierCode)`，每组取 `SubmittedAt` 最新的批次（参照 `StoreCashService.Pool.cs:74-77`），便于 SQLite 测试覆盖。数据量很小（28 店 × 少量供应商 × 4 价格），不需要写原生 SQL。

口径细节：

- 「已填报」：只要该分店在这个年份 + 节日下**任一供应商**有生效批次就算已填。供应商筛选只过滤数量，不改变填报状态，否则不卖某供应商的店会被误判为未填。
- 历史行（没有供应商、没有批次号）：按价格目录项各自取最新一条，归入「未指定供应商」。
- 金额 = Σ 数量 × 实际单价；「其他」价格的单价可能不同，统计页「其他」列只显示数量，金额计入合计。
- 时间：库里存 UTC，接口返回 ISO 字符串，前端按门店时区（AEST/AEDT）显示。

## 5. 移动端改版（`apps/mobile/src/modules/seasonal-cards`）

纯 JS 改动，走 **OTA**，不需要重建原生包。

拆分现有 1154 行的 `seasonal-cards-screen.tsx`：

| 新文件 | 内容 |
|---|---|
| `components/YearChips.tsx` | 去年 / 今年 / 明年，默认今年 |
| `components/HolidayGrid.tsx` | 5 个节日，带「已填报 / 待填报」，数据来自 `overview` |
| `components/SupplierField.tsx` | 供应商选择框和「最近使用」（`AppAsyncStorage`，按分店记最多 3 个）；弹层用 `OptionPickerSheet`（`searchable`，本地按编码或名称搜索 122 家） |
| `components/PriceQuantityRow.tsx` | 44px 加减按钮和数字输入；「其他」展开实际单价；改过的行高亮并显示「上次 → 本次」。模块内自写，不复用和下单耦合的 `OrderStepper` |
| `components/OverwriteConfirmSheet.tsx` | Paper `Dialog` / `Portal` 承载，不放进 `BusinessSheet`（会被盖住） |
| `components/SubmitFooter.tsx` | 底部固定栏，样式用 `BUSINESS_UI.footer`；按钮三态：提交填报 / 覆盖提交 / 未修改 |
| `submit-draft.ts`（+ `.test.ts`） | 纯函数：由 overview 生成预填草稿、比较差异、算合计、组装批量请求、判断按钮状态 |

其余改动：

- `api.ts`：新增 `fetchSeasonalCardOverview`、`submitSeasonalCardBatch` 及对应的 normalize 函数；**保留** `normalizeSeasonalCardCatalogResponse` / `normalizeSeasonalCardSubmissionsResponse` 这两个导出名（iOS 审核测试依赖）。
- `hooks.ts`：`useSeasonalCardOverview`、`useSubmitSeasonalCardBatch`；提交成功后让 overview 和 submissions 失效。409 时自动重拉 overview，并保留用户已改的数量。
- 历史页：列表增加供应商；同一批次合并成一张卡片显示 4 个价格。
- `types.ts`、`form.ts`：增加供应商和批次字段；单条草稿校验可以删掉（新页面不再用）。
- 文案：`locales/{zh,en}/screens/seasonalCards.json` 同步，需通过 `test:i18n-locales`。
- iOS 审核假接口 `ios-review/app-routes.ts:3134-3190` 增加 overview、batch 两个 mock，并同步 `app-routes.test.ts`。
- 路由、菜单、权限都不变（`NavigationService` FullAppMenu、`role-menu-catalog`、`expoRoleMenuPreview` 无需修改）。

## 6. Web 新页面「节日贺卡 · 分店填报统计」

模板：`pages/PosAdmin/DailyCloses/`（列表 + 抽屉 + `?store=` 深链）+ `MonthlyDailySalesDownload/export.ts`（exceljs 动态导入）。

新增：

- `pages/PosAdmin/SeasonalCardStats/`：`index.tsx`、`StoreDetailDrawer.tsx`、`logic.ts`（+test）、`export.ts`、`messages.zh.json` / `messages.en.json`、`messagesContract.test.ts`、`sourceContract.test.ts`、样式文件。
- `services/seasonalCardStatsService.ts`（+test）、`types/seasonalCardStats.ts`。
- 筛选栏：年份、节日、供应商（`getActiveLocalSuppliers` + 带搜索的 Select）、价格类型、分店、填报状态切换；表格用 `MeasuredTable`；页头用 `PageContainer compact`；导出走 `await import('exceljs')`。

登记点（对照 #599 的改动清单）：

| 位置 | 改动 |
|---|---|
| `router/routes.tsx` | 懒加载路由，`meta.accessKey = canViewSeasonalCardStats` |
| `types/permissions.ts` | 加 `SeasonalCards.Remaining.ViewAllStores` |
| `types/auth.ts`、`utils/access.ts` | 新访问键 |
| `utils/webMenuPreview.ts`、`utils/webPortalAccess.ts` | 菜单预览；只有该权限的账号也能进后台 |
| `locales/zh.json`、`en.json` | 只加菜单键 `menu.seasonalCardStats`；页面文案放页面级消息文件（首屏预算余量只有约 8 KB） |
| 后端 `NavigationService.cs` FullMenu | 加菜单叶子（缺了会让整份菜单回退成本地菜单） |
| 移动端 `role-menu-catalog.ts` `WEB_MENU` | 加一项，`role-logic.test.ts` 的 web 菜单数 48 改为 49 |
| 测试 | `router/seasonalCardStatsRoute.test.ts`、`utils/access.permission.test.ts`、`package.json` 加 `test:seasonal-card-stats` |

## 7. 测试清单

后端（新测试文件要在 `BlazorApp.Api.Tests.csproj` 手工加 `<Compile Include>`）：

- `SeasonalCardRemainingReactServiceTests`：批量提交成功（4 行同批次、名称快照）；items 缺项、多项或重复被拒；停用或删除的供应商被拒；`expectedPreviousBatchGuid` 过期返回 409；无变化返回 400；事务中途失败整批回滚；旧单条接口仍可用。
- 新 `SeasonalCardStatsServiceTests`：多批次取最新；多供应商合计；供应商筛选不改变填报状态；未填报名单；历史无批次行的兼容；权限（Admin / ViewAllStores / 无权限 403）。
- 迁移：`SchemaMigrationCoordinatorTests` 中的步骤清单、顺序和末位断言；`SchemaMigrationSqlServerIntegrationTests` 的幂等和签名漂移；`StartupSchemaMigratorStartupContractTests` 断言新列不出现在启动迁移里。
- `ControllerAuthorizationMetadataTests`（新 action 的 Policy）、`NavigationServiceTests`、`SeedDataServiceTests` / `PermissionSeedData`。

移动端：`submit-draft.test.ts`、`api.test.ts`、`hooks.test.ts`、`app-routes.test.ts`、`test:i18n-locales`、`tsc`。

Web：`logic.test.ts`、service 测试、两个契约测试、路由测试、`verify:bundle`。

## 8. PR 拆分与上线顺序

| PR | 内容 | 依赖 | 发布方式 |
|---|---|---|---|
| **PR1 后端** | 迁移 20261009.001、实体、batch / overview / admin 接口、新权限码、测试 | — | `--schema=migrate` → 部署主 API |
| **PR2 移动端** | 填报页改版 + 历史页供应商 + iOS 审核 mock | PR1 已上线 | OTA（纯 JS，标题不加原生重建标注） |
| **PR3 Web** | 分店填报统计页 + 抽屉 + 导出 + 前端登记点 + 后端 `NavigationService` Web 菜单叶子（与前端路由同一个 PR，避免后端先给出指向不存在页面的菜单） | PR1 已上线 | 部署主 API + Web |

PR2 和 PR3 可以在 PR1 合入后并行开发。上线顺序：**migrate → 主 API → Web → 移动端 OTA**。最后在角色管理里把 `ViewAllStores` 授给总部角色。

旧版 App 在 OTA 普及前仍走单条接口，不受影响：数据记为「未指定供应商」，统计照常显示。

## 9. 待确认

1. ~~哪些分店算「应填报」~~：已定，排除测试店与仓库（见 4.3）。
2. **`ViewAllStores` 授给哪些角色**：方案默认不进角色模板，上线后手工授权。
3. 统计页是否也给店长看本店：当前方案**不给**，店长在移动端历史页查看即可。
