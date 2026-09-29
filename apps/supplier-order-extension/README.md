# HB Supplier Order 扩展

Chrome / Edge / macOS Safari Manifest V3 扩展，在已配置的供应商列表页为每个商品注入“上次订货日期/数量、至今销量”按钮，点击后在 Chrome/Edge 侧栏或 Safari 浮动助手窗口查看该商品 12 个月内最多 6 次采购周期（订货/销售混排）。

1.5.0 起同时采集“供应商分类”：员工浏览供应商分类列表页时自动回传“分类路径 → 本页货号”（被动），或在侧栏点击“采集全部分类”后，在当前已登录的供应商标签页内按导航菜单逐分类逐页限速抓取（主动）。回传数据写入 HB 后端的供应商分类表，供 POS 商品管理页展示与筛选；Hot Bargain（200）的供应商分类即仓库分类，扩展不采集。

## 目录

- `src/`：扩展源码（不含构建产物与凭据）
- `src/lib/`：可测试的纯逻辑模块（版本、transform、profile 校验、微批、节点状态、分页/混排、握手、PKCE 网站会话交接、i18n）
- `src/lib/item-number.js`：卡片货号读取（按钮注入与分类采集共用）
- `src/lib/category-path.js`：分类 key 归一化、名称整理、分类页/促销判定、面包屑 → URL 段 → 标题三级路径解析
- `src/lib/category-capture.js`：回传去重（6 小时、500 条）、页面稳定检测、100 货号分块、载荷构建与校验、失败分类与退避重试、被动采集控制器
- `src/lib/category-crawl.js`：导航树构建（DOM 父级优先、URL 前缀兜底）、主动采集执行器（限速、分页、Retry-After、连续失败熔断、中止、续跑）、任务状态合并
- `src/lib/category-dom.js`：分类采集的 DOM 薄适配层（面包屑、标题、导航/子分类/分页链接；链接一律按被抓取页面 URL 解析）
- `src/background/service-worker.js`：统一请求、短期令牌存储、single-flight 网站会话授权、消息路由、动态内容脚本注册
- `src/content/shop-bridge.js`：`/shop` 页面桥接（PING/OPEN/网站会话授权）
- `src/content/list.js`：供应商列表页注入、被动分类采集与主动分类采集执行
- `src/sidepanel/`：侧栏 UI
- `test/`：Node 原生测试
- `build.mjs`：三浏览器构建脚本；Safari 后台由 esbuild 打成 classic service worker
- `dist/chrome`、`dist/edge`、`dist/safari`：同版本构建产物
- `../supplier-order-safari-extension/`：Safari Web Extension 的 Xcode 宿主项目

## 环境要求

- Node.js 20+（测试使用原生 `node:test`，构建仅依赖 esbuild）

## 本地加载

先构建：

```bash
npm ci
npm test
npm run build
```

### Chrome

1. 打开 `chrome://extensions`。
2. 打开右上角“开发者模式”。
3. 点击“加载已解压的扩展程序”，选择 `dist/chrome`。

### Edge

1. 打开 `edge://extensions`。
2. 打开左下角“开发人员模式”。
3. 点击“加载解压缩的扩展”，选择 `dist/edge`。

### Safari

Safari 使用相邻的独立 Xcode 宿主项目，构建、签名和启用步骤见 `../supplier-order-safari-extension/README.md`。Safari 16.4+ 没有 Chrome Side Panel API，因此会打开并复用一个 440×800 的浮动助手窗口，其余业务功能和权限流程保持一致。

加载后：

1. 先在 `https://hotbargain.vip/shop` 登录 HB 账号，再从网页或扩展图标打开助手；扩展会直接识别当前网站账号，不再要求输入账号密码。
2. 正式助手固定连接构建时的 `HB_WEB_ORIGIN` / `HB_API_ORIGIN`；网站会话自动授权要求两者同源。
3. 若当前用户返回了 `stores`，选择门店；否则手动输入门店编码并保存。
4. 在“供应商”列表对 DATS origin 点击“授权”（需用户手势，仅申请该供应商 origin 的可选权限）。
5. 打开 `https://www.dats.com.au/` 的列表页，商品卡片下方会出现按钮；点击按钮在侧栏定位到该 `supplierCode/itemNumber`。
6. 分类采集验证（需后端已部署 `supplier-categories` 接口且未关闭 `BrowserExtension:CategoryCaptureEnabled`）：
   - 被动：打开公开分类页 `https://www.dats.com.au/office-stationery`，页面稳定约 1.5 秒后 DevTools Network 出现一条 `supplier-categories/captures` POST（路径 `Home > Office Stationery` → 单节点 `/office-stationery`）；翻页只回传新增货号，6 小时内重访同一页不重复回传；侧栏“浏览分类页时自动采集”关闭后不再回传。
   - 主动：保持 DATS 标签页为当前页，在侧栏“供应商分类采集”点击“采集全部分类”，先出现一次 `tree-snapshot`，随后约每 1.5 秒抓取一个分类页并回传 `captures`；关闭或跳转该标签页显示“已中断”并可“继续”，点击“中止”立即停止，失败分类可“重试失败”。

正式商店界面不再提供本地或自定义 API 切换，避免在无密码模式下进入无法同源授权的死路径。开发/测试构建如需其他环境，必须在构建时把 `HB_WEB_ORIGIN` 与 `HB_API_ORIGIN` 同时设为该环境的同一 origin。

## 权限边界

- `host_permissions` 仅包含 HB API 正式源；`/shop` 桥接脚本仅注入 HB Web 正式源。
- manifest 不申请 `cookies` 权限；内容脚本只在受信顶层 `/shop` 页面以现有 HttpOnly 网站 Cookie 请求一次性授权码，不读取、复制或保存 Cookie。
- `optional_host_permissions` 默认覆盖 HTTPS 供应商；TXK 因现站仅提供 HTTP，额外只允许精确的 `http://txkorders.inzantsales.com/*`，仍需在侧栏由用户逐供应商授权。
- 配置只解释声明式 selector / attribute / text，以及内置固定 transform（包括 GFA 的下划线转斜线、TXK 的固定 SKU 前缀提取）；绝不 `eval` / `Function` / 后台任意正则 / 远程 JS。
- PKCE verifier 与一次性授权码只通过扩展内部 runtime 消息传递；短期 access token 只存 `chrome.storage.session`。扩展不保存账号、密码或 refresh token，也不会退出网站会话；仅受信同源 `/shop` 内容脚本在 access cookie 过期时调用一次网站 session refresh，且不读取或保存 refresh cookie。
- 分类采集只读取已授权供应商页面的面包屑、页面标题、导航/子分类/分页链接与商品卡片货号，回传内容仅为“分类路径（名称、站点路径 key、同源 URL）+ 货号 + 页面 URL + 采集时间”，不读取价格、供应商账户、订单或表单内容；后台校验来源标签页顶层 frame、页面与路径 URL 必须与该供应商同源。
- 主动采集仅在员工于侧栏点击后，在其已登录的供应商标签页内以同源 GET 逐页请求：单任务串行、默认每 1.5 秒一次（后台可配 0.5–15 秒）、遵守 `Retry-After`（≤60 秒）、连续 5 个分类失败即熔断、识别到登录页立即停止；不读取或复制 Cookie、不申请任何新增浏览器权限（两份 manifest 与 1.4.x 相同）。

## 三浏览器构建

`build.mjs` 生成 `dist/chrome`、`dist/edge` 与 `dist/safari`，三个 manifest 的 `version` 相同（当前 1.5.0）。Web 与 API 同源时只需设置 API 源；构建仍允许分别声明 Web/API 来源，但网站会话自动授权只在两者同源时启用：

```bash
HB_WEB_ORIGIN=https://staging.example.com HB_API_ORIGIN=https://staging.example.com npm run build
```

构建会做 manifest JSON 语法校验，并在最后校验三个包版本一致；Safari manifest 额外锁定最低 Safari 16.4。

## HB 后端配置

参考 `services/backend/BlazorApp.Api/appsettings.BrowserExtension.example.json` 配置最新版、最低支持版、三个浏览器商店链接和声明式供应商 profile。部署时也可使用 ASP.NET Core 环境变量，例如：

```bash
BrowserExtension__LatestVersion=1.5.0
BrowserExtension__MinimumVersion=1.1.0
BrowserExtension__ChromeStoreUrl=https://chromewebstore.google.com/detail/...
BrowserExtension__EdgeStoreUrl=https://microsoftedge.microsoft.com/addons/detail/...
BrowserExtension__SafariStoreUrl=https://apps.apple.com/app/...
```

紧急停用内置 DATS 时将 `UseBuiltInDatsProfile` 设为 `false`；停用其余内置供应商时将 `UseBuiltInSupplierProfiles` 设为 `false`。单个供应商也可在 `SupplierProfiles` 中用相同 `SupplierCode` 配置 `Enabled: false` 覆盖。每次变更后递增 `ConfigVersion`，扩展下一次同步配置后会移除对应域名脚本，无需发新版。

分类采集配置位于每个供应商 profile 的 `Category` 子对象（仅下发给 1.5.0 及以上客户端）：`Enabled`、`PassiveEnabled`、`CrawlEnabled`、分类页/排除路径模式、面包屑/标题/导航/子分类/分页选择器、`KeySource`（`pathname`/`hash`）、`KeyQueryParams`、`NavRootUrl`、`MaxPages`（1–50）、`MaxDepth`（1–6）、`MaxCategories`（10–2000）、`CrawlDelayMs`（500–15000）与促销分类 glob。扩展对该块逐项校验：任一字段非法只停用该供应商的分类采集并在 `GET_PROFILES` 的 `warnings` 中说明原因，不影响按钮注入与其他供应商。登录站点的选择器若与实际页面不符，直接修改后端配置并递增 `ConfigVersion` 即可热修正；全局紧急停用设 `BrowserExtension__CategoryCaptureEnabled=false`（采集接口返回 `FEATURE_DISABLED`，扩展自动熔断）。

## 发布

### Chrome Unlisted

1. 从构建目录内部打包，确保 `manifest.json` 位于 zip 根目录：`cd dist/chrome && zip -r ../hb-supplier-order-chrome.zip .`。
2. 在 Chrome Web Store 开发者后台创建/选择项目，上传 zip。
3. “可见性”选择“不公开（Unlisted）”，提交审核。
4. 审核通过后按后台给出的 Unlisted 链接分发。

### Edge Hidden

1. 从 `dist/edge` 目录内部打包，确保 `manifest.json` 位于 zip 根目录。
2. 在 Microsoft Partner Center 的扩展页面上传包。
3. 可见性选择“隐藏（Hidden）”，按需配置组织内分发或隐藏链接。

### Safari

使用 `../supplier-order-safari-extension` 的 iOS-only Xcode 工程为 iPhone 和 iPad 创建 Release Archive，再通过 App Store Connect 内部 TestFlight 和 iOS App Store 分发。项目不包含 macOS target，并显式关闭 Apple Silicon Mac 的 “Designed for iPhone/iPad” 兼容分发；App Store Connect 中也必须取消 Mac availability。正式 Unlisted 链接生成前，后端 `SafariStoreUrl` 保持空值。

正式图标源稿位于 `assets/icon-master.svg`，浏览器所需的 16/32/48/128px PNG 位于 `src/icons/`，构建时会复制到三个浏览器产物；本仓库不包含任何凭据或商店密钥。

## 版本与更新策略

- 普通供应商（仅新增 `supplierCode/displayName/origins/选择器` 等声明式配置）通过服务端 `GET /api/react/v1/browser-extension/supplier-profiles` 下发，**无需发布新版**；当前后端默认目录包含 DATS、Brazco、Malmar、Meteor Party、Yatsal、Windragon、MNB、PJ SAS、Jemark、GFA、TXK 和 Boom Up，扩展离线回退仍只保留 DATS（内置配置版本 3，含 DATS 分类采集配置）。
- 分类采集的选择器、路径模式、限速与上限同样是声明式配置，调整后递增 `ConfigVersion` 即可生效，无需发版；1.5.0 起的新行为（被动/主动采集、侧栏采集区、两个回传接口）需要升级扩展，后端对低于 1.5.0 的客户端不下发 `Category`。
- 需要新增特殊逻辑（新的解析行为、交互或 API 契约变化）时，才需要修改扩展代码并发布新版。
- 三个浏览器包始终保持同一版本号（`build.mjs` 会校验）。

## 自动更新与补丁回滚

- 商店安装版由 Chrome Web Store / Edge Add-ons 自动推送更新，无需用户操作。
- 补丁流程：先递增共享 `package.json` 语义版本；Safari TestFlight 重传只递增独立 Xcode build number。然后运行 `npm test`、`npm run build` 重新生成三包、同步 Safari Resources、执行 Safari 测试与 Release Archive，最后上传对应商店审核。
- 商店版不能降低版本号。回滚流程是恢复上一版本代码，递增一个更高的补丁版本，重新测试、构建并提交审核；本地加载版可直接重新加载旧构建目录。

## 测试

```bash
npm test
```

覆盖：semver/版本状态、profile 校验与安全 transform、微批去重与分页上限、动态节点状态纯逻辑、分页/筛选/混排、握手校验、PKCE/S256、同源 `/shop` 校验、single-flight 网站会话交接、401 清理、三浏览器源码/manifest 安全契约与 i18n；分类采集覆盖 key 归一化与路径解析、去重/稳定检测/分块/重试、导航树与主动采集执行器（分页、续跑、Retry-After、熔断、中止）、category 配置降级，以及 `category-dom.browser.test.mjs` 用无头 Edge/Chrome 验证 DATS、WooCommerce、GFA 三类页面结构（未安装 Chromium 内核浏览器时跳过）。
