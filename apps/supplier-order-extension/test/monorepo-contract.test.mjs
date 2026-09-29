import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const read = (path) => readFileSync(path, 'utf8');

test('Web 与扩展 /shop 消息常量保持一致', () => {
  const web = read('../web/src/components/SupplierOrderingExtensionEntry/supplierOrderingExtensionLogic.ts');
  const extension = read('src/lib/handshake.js');
  const constants = [
    'hb-platform',
    'hb-supplier-ordering-extension',
    'HB_SUPPLIER_ASSISTANT_PING',
    'HB_SUPPLIER_ASSISTANT_OPEN',
    'HB_SUPPLIER_ASSISTANT_STATUS',
  ];

  for (const value of constants) {
    assert.ok(web.includes(value), `Web 缺少消息常量 ${value}`);
    assert.ok(extension.includes(value), `扩展缺少消息常量 ${value}`);
  }
});

test('扩展归一化字段覆盖后端正式摘要和采购周期 DTO', () => {
  const dto = read('../../services/backend/BlazorApp.Shared/DTOs/BrowserExtensionDtos.cs');
  const summary = read('src/lib/dats-state.js');
  const cycles = read('src/lib/pagination.js');

  for (const field of [
    'MatchStatus',
    'LatestPurchaseDate',
    'LatestPurchaseQuantity',
    'SalesSinceLatestPurchase',
  ]) {
    assert.ok(dto.includes(field), `后端 DTO 缺少 ${field}`);
    const camel = field[0].toLowerCase() + field.slice(1);
    assert.ok(summary.includes(camel), `扩展摘要归一化缺少 ${camel}`);
  }

  for (const field of [
    'PurchaseDate',
    'InvoiceNumbers',
    'PurchaseQuantity',
    'AveragePurchasePrice',
    'SalesStartDate',
    'SalesEndDate',
    'SalesQuantity',
    'AverageSalePrice',
  ]) {
    assert.ok(dto.includes(field), `后端 DTO 缺少 ${field}`);
    const camel = field[0].toLowerCase() + field.slice(1);
    assert.ok(cycles.includes(camel), `扩展周期归一化缺少 ${camel}`);
  }
});

test('扩展源码无远程代码执行入口且 manifest 不申请 tabs 权限', () => {
  const manifest = read('src/manifest.template.json');
  const profiles = read('src/lib/profiles.js');
  const transforms = read('src/lib/transforms.js');

  assert.ok(!manifest.includes('"tabs"'));
  assert.ok(!profiles.includes('eval('));
  assert.ok(!profiles.includes('new Function'));
  assert.ok(!transforms.includes('eval('));
  assert.ok(!transforms.includes('new Function'));
});

test('构建分别记录 HB Web 与 API 源，网站会话交接显式执行同源校验', () => {
  const build = read('build.mjs');
  const config = read('src/config.template.js');
  const manifest = read('src/manifest.template.json');
  const sessionHandoff = read('src/lib/session-handoff.js');

  assert.ok(build.includes('HB_WEB_ORIGIN'));
  assert.ok(build.includes('HB_API_ORIGIN'));
  assert.ok(config.includes('__HB_WEB_ORIGIN__'));
  assert.ok(config.includes('__HB_API_ORIGIN__'));
  assert.ok(manifest.includes('__WEB_ORIGIN__/*'));
  assert.ok(manifest.includes('__API_ORIGIN__/*'));
  assert.ok(sessionHandoff.includes("reason: 'API_ORIGIN_MISMATCH'"));
});

test('Safari 构建使用 16.4+ 兼容 manifest 且不声明 Chrome Side Panel', () => {
  const build = read('build.mjs');
  const manifest = read('src/manifest.safari.template.json');

  assert.ok(build.includes("'safari'"));
  assert.ok(build.includes('safari16.4'));
  assert.ok(manifest.includes('"strict_min_version": "16.4"'));
  assert.ok(build.includes("join('content', 'list.js')"));
  assert.ok(build.includes("join('content', 'shop-bridge.js')"));
  assert.ok(manifest.includes('"options_ui"'));
  assert.ok(manifest.includes('"page": "sidepanel/sidepanel.html"'));
  assert.ok(!manifest.includes('"sidePanel"'));
  assert.ok(!manifest.includes('"side_panel"'));
  assert.ok(!manifest.includes('"type": "module"'));
  assert.ok(!manifest.includes('"minimum_chrome_version"'));
  assert.ok(!manifest.includes('"web_accessible_resources"'));
});

test('供应商目录请求携带扩展版本，旧客户端可由后端安全降级', () => {
  const worker = read('src/background/service-worker.js');
  assert.ok(worker.includes("'X-HB-Extension-Version': EXTENSION_VERSION"));
});

test('供应商页注入按钮保留可见键盘焦点', () => {
  const list = read('src/content/list.js');
  assert.ok(list.includes('.hb-btn:focus-visible'));
  assert.ok(list.includes("attachShadow({ mode: 'closed' })"));
});

test('供应商页摘要按钮完整显示并自动换行', () => {
  const list = read('src/content/list.js');
  const buttonStyle = list.match(/'\.hb-btn\{([^']+)\}'/)?.[1] ?? '';

  assert.ok(buttonStyle.includes('white-space:normal'));
  assert.ok(buttonStyle.includes('overflow-wrap:anywhere'));
  assert.ok(!buttonStyle.includes('white-space:nowrap'));
  assert.ok(!buttonStyle.includes('text-overflow:ellipsis'));
  assert.ok(!buttonStyle.includes('overflow:hidden'));
});

test('GFA 商品行会为摘要扩展高度并保持商品明细与操作区互不遮挡', () => {
  const list = read('src/content/list.js');
  const mountHost = list.match(/function mountHost\(card\) \{[\s\S]*?\n  \}/)?.[0] ?? '';
  const buttonStyle = list.match(/'\.hb-btn\{([^']+)\}'/)?.[1] ?? '';

  assert.ok(mountHost.includes("profile.supplierCode === '236'"));
  assert.ok(mountHost.includes("card.matches('.list-row[data-product]')"));
  assert.ok(mountHost.includes('ensureGfaLayoutStyle()'));
  assert.ok(list.includes('height: auto !important'));
  assert.ok(list.includes('a[href*="/product/view?id="] > .list-content'));
  assert.ok(list.includes('> .list-content .list-detail'));
  assert.ok(!list.includes('a.list-content[href*="/product/view?id="]'));
  assert.ok(list.includes('@media (max-width: 500px)'));
  assert.ok(list.includes('padding-bottom: 46px !important'));
  assert.ok(mountHost.includes('margin:4px 235px 0 0'));
  assert.ok(!mountHost.includes('transform:translateY(-100%)'));
  assert.ok(mountHost.includes('z-index:2'));
  assert.ok(mountHost.includes('pointer-events:none'));
  assert.ok(buttonStyle.includes('pointer-events:auto'));
  assert.ok(mountHost.includes("'display:block;margin:4px 0;'"));
});

test('侧栏商品请求使用 generation guard，旧响应不能覆盖新商品', () => {
  const sidepanel = read('src/sidepanel/sidepanel.js');
  assert.ok(sidepanel.includes('createGenerationGuard'));
  assert.ok(sidepanel.includes('itemRequestGeneration.isCurrent'));
});

test('升级会清理旧版长期凭据，后续网站会话不再读取或写入 refresh token', () => {
  const worker = read('src/background/service-worker.js');
  assert.ok(worker.includes("const LEGACY_ACCESS_KEY = 'accessToken'"));
  assert.ok(worker.includes("const LEGACY_REFRESH_KEY = 'refreshToken'"));
  assert.ok(worker.includes('removeSession([LEGACY_ACCESS_KEY])'));
  assert.ok(worker.includes('removeLocal([LEGACY_REFRESH_KEY])'));
  assert.ok(!worker.includes('getRefreshToken'));
  assert.ok(!worker.includes('setLocal({ [LEGACY_REFRESH_KEY]'));
});

test('正式侧栏隐藏环境切换入口，同时保留构建与开发调试契约', () => {
  const html = read('src/sidepanel/sidepanel.html');
  const sidepanel = read('src/sidepanel/sidepanel.js');
  const worker = read('src/background/service-worker.js');
  const manifest = read('src/manifest.template.json');

  for (const id of ['apiOriginInput', 'apiRemoteBtn', 'apiLocalBtn', 'apiSaveBtn']) {
    assert.ok(html.includes(`id="${id}"`), `侧栏缺少 ${id}`);
  }
  assert.match(html, /<section id="apiSection"[^>]* hidden>/u);
  assert.ok(sidepanel.includes("type: 'GET_API_ORIGIN'"));
  assert.ok(sidepanel.includes("type: 'SET_API_ORIGIN'"));
  assert.ok(worker.includes("case 'GET_API_ORIGIN':"));
  assert.ok(worker.includes("case 'SET_API_ORIGIN':"));
  assert.ok(worker.includes('await clearAccessSession()'));
  assert.ok(worker.includes("reason: 'API_ORIGIN_MISMATCH'"));
  assert.ok(manifest.includes('http://localhost/*'));
  assert.ok(manifest.includes('http://127.0.0.1/*'));
});

test('TXK 明文 HTTP 权限仅开放给已核验的精确域名', () => {
  const manifest = read('src/manifest.template.json');

  assert.ok(manifest.includes('http://txkorders.inzantsales.com/*'));
  assert.ok(!manifest.includes('"http://*/*"'));
});

test('Top 30% 商品图片、名称、货号和销量档位完整显示并允许窄屏换行', () => {
  const css = read('src/sidepanel/sidepanel.css');
  const sidepanel = read('src/sidepanel/sidepanel.js');
  const nameRule = css.match(/\.ranking-product strong\s*\{([^}]+)\}/)?.[1] ?? '';
  const codeRule = css.match(/\.ranking-code\s*\{([^}]+)\}/)?.[1] ?? '';
  const imageRule = css.match(/\.ranking-image\s*\{([^}]+)\}/)?.[1] ?? '';

  assert.ok(!nameRule.includes('-webkit-line-clamp'));
  assert.ok(!nameRule.includes('overflow: hidden'));
  assert.ok(nameRule.includes('white-space: normal'));
  assert.ok(codeRule.includes('white-space: normal'));
  assert.ok(codeRule.includes('overflow-wrap: anywhere'));
  assert.ok(!codeRule.includes('text-overflow: ellipsis'));
  assert.ok(imageRule.includes('width: 72px'));
  assert.ok(imageRule.includes('height: 72px'));
  assert.ok(sidepanel.includes("placeholder.className = 'ranking-image-placeholder'"));
  assert.ok(sidepanel.includes('placeholder.hidden = false'));
  assert.ok(sidepanel.includes("band.className = 'ranking-band'"));
});

test('Top 30% 支持供应商切换、服务端分页、每页条数和旧 TOP 10 提示', () => {
  const html = read('src/sidepanel/sidepanel.html');
  const sidepanel = read('src/sidepanel/sidepanel.js');
  const worker = read('src/background/service-worker.js');

  for (const id of [
    'rankingSupplierSelect',
    'rankingPageSizeSelect',
    'rankingPrevBtn',
    'rankingPageInfo',
    'rankingNextBtn',
    'rankingLegacyHint',
    'rankingState',
    'rankingRetryBtn',
    'rankingAnnouncement',
  ]) {
    assert.ok(html.includes(`id="${id}"`), `排名区缺少 ${id}`);
  }

  assert.ok(sidepanel.includes('chrome.tabs.onActivated.addListener'));
  assert.ok(sidepanel.includes('chrome.tabs.onUpdated.addListener'));
  assert.ok(sidepanel.includes("el('rankingSupplierSelect').addEventListener('change'"));
  assert.ok(sidepanel.includes('formatAverageSellingPrice(item.averageSellingPrice'));
  assert.ok(sidepanel.includes('normalizeTopSalesPage'));
  assert.ok(sidepanel.includes('topPercent: 30'));
  assert.ok(sidepanel.includes('pageSize: rankingPageSize'));
  assert.ok(sidepanel.includes('totalRankedCount'));
  assert.ok(sidepanel.includes('totalPages'));
  assert.ok(sidepanel.includes("chrome.storage.local.set({ salesRankingPageSize"));
  const pageSizeHandler = sidepanel.slice(
    sidepanel.indexOf("el('rankingPageSizeSelect').addEventListener('change'"),
    sidepanel.indexOf("el('rankingRetryBtn').addEventListener('click'"),
  );
  assert.ok(
    pageSizeHandler.indexOf('chrome.storage.local.set({ salesRankingPageSize')
      < pageSizeHandler.indexOf('renderLegacyRankingPage'),
    '每页条数必须先持久化，再发起可能失败的榜单请求',
  );
  assert.ok(!sidepanel.includes('stored.rankingPageSize'));
  assert.ok(!sidepanel.includes('changes.rankingPageSize'));
  assert.ok(sidepanel.includes("chrome.storage.local.set({ salesRankingDays"));
  assert.ok(sidepanel.includes('scrollRankingToTop'));
  assert.ok(sidepanel.includes("formatMessage('rankingPageChanged'"));
  assert.ok(sidepanel.includes('restoreRankingLoad'));
  assert.ok(sidepanel.includes('const showPager = !rankingError'));
  assert.ok(sidepanel.includes('rankingRetryTarget'));
  assert.ok(sidepanel.includes('resolveRankingRetryTarget'));
  assert.ok(worker.includes('topPercent'));
  assert.ok(worker.includes('pageSize'));
  assert.ok(sidepanel.includes('const activeSupplierRequestGeneration = createGenerationGuard(0)'));
  assert.ok(sidepanel.includes('activeSupplierRequestGeneration.isCurrent(requestGeneration)'));
  assert.ok(sidepanel.includes("placeholder.value = ''"));
});

test('总排行榜销量可进入同周期分店明细并返回原滚动位置', () => {
  const html = read('src/sidepanel/sidepanel.html');
  const sidepanel = read('src/sidepanel/sidepanel.js');
  const worker = read('src/background/service-worker.js');
  const dto = read('../../services/backend/BlazorApp.Shared/DTOs/BrowserExtensionDtos.cs');

  for (const id of [
    'storeSalesSection',
    'storeSalesBackBtn',
    'storeSalesProduct',
    'storeSalesTotal',
    'storeSalesSearch',
    'storeSalesList',
    'storeSalesRetryBtn',
    'storeSalesFooter',
  ]) {
    assert.ok(html.includes(`id="${id}"`), `分店销量区缺少 ${id}`);
  }
  assert.ok(sidepanel.includes("sales.className = 'ranking-sales ranking-sales-button'"));
  assert.ok(sidepanel.includes("type: 'SUPPLIER_PRODUCT_STORE_SALES'"));
  assert.ok(sidepanel.includes('startDate: selection.startDate'));
  assert.ok(sidepanel.includes('endDate: selection.endDate'));
  assert.ok(sidepanel.includes('snapshotVersion: selection.snapshotVersion'));
  assert.ok(sidepanel.includes('totalSalesQuantity: selection.totalSalesQuantity'));
  assert.ok(sidepanel.includes('normalizeStoreSalesResponse'));
  assert.ok(sidepanel.includes('filterStoreSales'));
  assert.ok(sidepanel.includes("sales.dataset.productCode = item.productCode"));
  assert.ok(sidepanel.includes('focusRankingSalesButton'));
  assert.ok(sidepanel.includes("el('storeSalesBackBtn').focus()"));
  assert.ok(sidepanel.includes('rankingScrollTop = globalThis.scrollY'));
  assert.ok(sidepanel.includes("globalThis.scrollTo({ top: scrollTop, behavior: 'auto' })"));
  assert.ok(worker.includes("case 'SUPPLIER_PRODUCT_STORE_SALES':"));
  assert.ok(worker.includes("'/api/react/v1/browser-extension/supplier-product-store-sales'"));
  assert.ok(worker.includes('expectedTotalSalesQuantity: Number(totalSalesQuantity)'));
  assert.ok(worker.includes('snapshotVersion'));
  assert.ok(dto.includes('BrowserExtensionSupplierProductStoreSalesDto'));
  assert.ok(dto.includes('ExpectedTotalSalesQuantity'));
  assert.ok(dto.includes('SnapshotVersion'));
});

test('供应商商品摘要携带并隔离 60/90 天销量排名周期', () => {
  const list = read('src/content/list.js');
  const worker = read('src/background/service-worker.js');

  assert.ok(list.includes("'salesRankingDays'"));
  assert.ok(list.includes('buildSummaryCacheKey'));
  assert.ok(list.includes('salesRankingDays,'));
  assert.ok(list.includes('changes.salesRankingDays'));
  assert.ok(worker.includes('salesRankingDays'));
  assert.ok(list.includes("rankLine.className = 'hb-rank-line'"));
  assert.ok(list.includes("formatMessage('salesRankBand'"));
  assert.ok(!list.includes("btn.appendChild(document.createTextNode(' · '));\n      btn.appendChild(band)"));
});

test('没有当前商品时仍可切换到商品记录空状态', () => {
  const sidepanel = read('src/sidepanel/sidepanel.js');
  const tabRenderStart = sidepanel.indexOf('function renderDataTabs()');
  const tabRenderEnd = sidepanel.indexOf('async function copyText', tabRenderStart);
  const tabClickStart = sidepanel.indexOf("el('historyTab').addEventListener");
  const tabClickEnd = sidepanel.indexOf("el('rankingTab').addEventListener", tabClickStart);
  const tabRender = sidepanel.slice(tabRenderStart, tabRenderEnd);
  const tabClick = sidepanel.slice(tabClickStart, tabClickEnd);

  assert.ok(tabRenderStart >= 0 && tabRenderEnd > tabRenderStart);
  assert.ok(tabClickStart >= 0 && tabClickEnd > tabClickStart);
  assert.ok(!tabRender.includes("el('historyTab').disabled = !hasHistory"));
  assert.ok(!tabClick.includes('if (!currentItem) return'));
  assert.ok(sidepanel.includes("t(locale, 'historyNoItem')"));
});

const CATEGORY_LIB_FILES = [
  'src/lib/item-number.js',
  'src/lib/category-path.js',
  'src/lib/category-capture.js',
  'src/lib/category-crawl.js',
  'src/lib/category-dom.js',
];

test('分类采集：service worker 路由五类消息、两个回传端点与任务状态持久化', () => {
  const worker = read('src/background/service-worker.js');
  for (const type of [
    'CATEGORY_CAPTURE',
    'CATEGORY_TREE_SNAPSHOT',
    'CATEGORY_CRAWL_START',
    'CATEGORY_CRAWL_ABORT',
    'CATEGORY_CRAWL_PROGRESS',
  ]) {
    assert.ok(worker.includes(`case '${type}':`), `service worker 缺少 ${type}`);
  }
  assert.ok(worker.includes("'/api/react/v1/browser-extension/supplier-categories/captures'"));
  assert.ok(worker.includes("'/api/react/v1/browser-extension/supplier-categories/tree-snapshot'"));
  assert.ok(worker.includes("const CATEGORY_JOB_KEY = 'categoryCrawlJob'"));
  assert.ok(worker.includes("const CATEGORY_HISTORY_KEY = 'categoryCrawlHistory'"));
  assert.ok(worker.includes('setSession({ [CATEGORY_JOB_KEY]: job })'));
  assert.ok(worker.includes('chrome.tabs.onRemoved.addListener'));
  assert.ok(worker.includes("changeInfo?.status === 'loading'"));
  // 来源校验：顶层 frame、页面 origin 属于供应商、载荷 URL 同源、任务与标签页绑定。
  assert.ok(worker.includes('isTopFrameSender(sender)'));
  assert.ok(worker.includes('senderOrigin: source.origin'));
  assert.ok(worker.includes('requireRunningCrawlJob(message.jobId, source.tabId)'));
  assert.ok(worker.includes('isExtensionPageSender(sender)'));
  assert.ok(worker.includes('captureDedupe.has(dedupeKey)'));
  assert.ok(worker.includes('warnings,'));
});

test('分类采集：内容脚本隔离失败、复用货号读取并只接受后台下发的采集指令', () => {
  const list = read('src/content/list.js');
  assert.ok(list.includes('return readItemNumberFrom(card, itemCfg);'));
  assert.ok(list.includes("import(chrome.runtime.getURL('lib/item-number.js'))"));
  assert.ok(list.includes('.catch(() => null);'), '分类模块加载失败不得影响按钮注入');
  const scan = list.slice(list.indexOf('  function scan() {'), list.indexOf('  // 仅可见（含 600px 缓冲）卡片才请求摘要'));
  assert.ok(scan.indexOf('ensureCard(card)') < scan.indexOf('notifyCategoryCapture(cards)'), '按钮注入先于分类采集');
  assert.ok(scan.includes('resetCategoryCapture()'));
  assert.ok(list.includes("type !== 'CATEGORY_CRAWL_RUN' && type !== 'CATEGORY_CRAWL_STOP'"));
  assert.ok(list.includes('sender?.id !== chrome.runtime.id || sender?.tab'));
  assert.ok(list.includes("credentials: 'include'"));
  assert.ok(list.includes('target.origin !== location.origin'), '主动采集只允许同源请求');
  assert.ok(list.includes("window.addEventListener('pagehide', handleCategoryPageHide)"));
  assert.ok(list.includes('changes.categoryCaptureSettings'));
});

test('分类采集：侧栏区块位于供应商列表之后并通过后台启动/中止任务', () => {
  const html = read('src/sidepanel/sidepanel.html');
  const sidepanel = read('src/sidepanel/sidepanel.js');
  assert.ok(html.indexOf('id="supplierSection"') < html.indexOf('id="categorySection"'));
  assert.ok(html.indexOf('id="categorySection"') < html.indexOf('id="dataTabs"'));
  for (const id of [
    'categoryPassiveToggle',
    'categoryCrawlBtn',
    'categoryAbortBtn',
    'categoryResumeBtn',
    'categoryRetryBtn',
    'categoryProgress',
    'categoryStatus',
    'categoryLastRun',
    'categorySafariHint',
  ]) {
    assert.ok(html.includes(`id="${id}"`), `分类采集区缺少 ${id}`);
  }
  assert.ok(sidepanel.includes("type: 'CATEGORY_CRAWL_START'"));
  assert.ok(sidepanel.includes("type: 'CATEGORY_CRAWL_ABORT'"));
  assert.ok(sidepanel.includes('chrome.storage.local.set({ categoryCaptureSettings: next })'));
  assert.ok(sidepanel.includes("BUILD_TARGET !== 'safari'"));
});

test('分类采集不新增 manifest 权限', () => {
  const chromeManifest = JSON.parse(read('src/manifest.template.json').replaceAll('__API_ORIGIN__', 'https://x').replaceAll('__WEB_ORIGIN__', 'https://x'));
  const safariManifest = JSON.parse(read('src/manifest.safari.template.json').replaceAll('__API_ORIGIN__', 'https://x').replaceAll('__WEB_ORIGIN__', 'https://x'));
  assert.deepEqual(chromeManifest.permissions, ['storage', 'sidePanel', 'scripting']);
  assert.deepEqual(safariManifest.permissions, ['storage', 'scripting']);
  for (const manifest of [chromeManifest, safariManifest]) {
    assert.deepEqual(manifest.host_permissions, ['https://x/*']);
    assert.ok(!('externally_connectable' in manifest));
  }
});

test('分类采集新模块不执行远程代码、不写入 HTML', () => {
  for (const path of CATEGORY_LIB_FILES) {
    const source = read(path);
    for (const forbidden of ['eval(', 'new Function', 'innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write']) {
      assert.ok(!source.includes(forbidden), `${path} 不得包含 ${forbidden}`);
    }
  }
  // DOMParser 文档的 baseURI 是当前页，链接必须显式按被抓取页面解析。
  const dom = read('src/lib/category-dom.js');
  assert.ok(dom.includes("getAttribute('href')"));
  assert.ok(dom.includes('new URL(trimmed, baseUrl)'));
  assert.ok(!/\b(?:anchor|el|element|link|previous)\.href\b/u.test(dom), '不得读取元素的 .href 属性');
});

test('分类采集载荷字段与后端 DTO 保持一致', () => {
  const dto = read('../../services/backend/BlazorApp.Shared/DTOs/BrowserExtensionDtos.cs');
  const capture = read('src/lib/category-capture.js');
  const crawl = read('src/lib/category-crawl.js');
  const profiles = read('src/lib/profiles.js');
  for (const className of [
    'BrowserExtensionCategoryCaptureRequestDto',
    'BrowserExtensionCategoryPathNodeDto',
    'BrowserExtensionCategoryCaptureResultDto',
    'BrowserExtensionCategoryTreeSnapshotRequestDto',
    'BrowserExtensionCategoryTreeNodeDto',
    'BrowserExtensionCategoryTreeSnapshotResultDto',
    'BrowserExtensionSupplierCategoryProfileDto',
  ]) {
    assert.ok(dto.includes(`class ${className}`), `后端缺少 ${className}`);
  }
  const camel = (field) => field[0].toLowerCase() + field.slice(1);
  for (const field of ['SupplierCode', 'PageUrl', 'CategoryPath', 'ItemNumbers', 'CapturedAt', 'Mode', 'PageNumber']) {
    assert.ok(dto.includes(` ${field} `), `后端采集请求缺少 ${field}`);
    assert.ok(capture.includes(camel(field)), `扩展采集载荷缺少 ${camel(field)}`);
  }
  for (const field of [
    'CategoryGuid',
    'FullPath',
    'Depth',
    'IsPromotional',
    'CategoriesCreated',
    'MatchedProducts',
    'AssignedProducts',
    'UnchangedProducts',
    'SkippedManual',
    'UnmatchedItemNumberCount',
    'UnmatchedSamples',
    'SourceUrl',
    'Nodes',
    'ParentKey',
    'SortOrder',
    'Created',
    'Updated',
    'Unchanged',
    'OrphanCount',
    'PromotionalCount',
  ]) {
    assert.ok(dto.includes(` ${field} `), `后端 DTO 缺少 ${field}`);
    assert.ok(capture.includes(camel(field)) || crawl.includes(camel(field)), `扩展缺少 ${camel(field)}`);
  }
  const categoryDto = dto.slice(
    dto.indexOf('class BrowserExtensionSupplierCategoryProfileDto'),
    dto.indexOf('class BrowserExtensionSupplierProfilesDto'),
  );
  const fields = [...categoryDto.matchAll(/public [\w<>?]+ (\w+) \{ get; set; \}/gu)].map((match) => match[1]);
  assert.equal(fields.length, 19, `分类 profile DTO 字段数变化：${fields.join(',')}`);
  for (const field of fields) {
    assert.ok(profiles.includes(camel(field)), `扩展 normalizeCategoryConfig 未处理 ${camel(field)}`);
  }
});

// 以下断言依赖后端同一 PR 中的 Options 与控制器实现。
test('分类采集后端 Options 与控制器端点已登记', () => {
  const options = read('../../services/backend/BlazorApp.Api/Models/BrowserExtensionOptions.cs');
  const controller = read('../../services/backend/BlazorApp.Api/Controllers/React/ReactBrowserExtensionController.cs');
  assert.ok(options.includes('class BrowserExtensionSupplierCategoryOptions'));
  assert.ok(options.includes('CategoryCaptureEnabled'));
  assert.ok(controller.includes('supplier-categories/captures'));
  assert.ok(controller.includes('supplier-categories/tree-snapshot'));
});
