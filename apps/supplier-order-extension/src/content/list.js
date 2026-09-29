// 供应商列表页注入：MutationObserver + IntersectionObserver + WeakMap + generation guard，
// 微批请求商品摘要并注入 shadow DOM 按钮，点击定位到侧栏采购周期。
// 1.5.0 起同时负责供应商分类采集：被动（分类列表页稳定后回传“分类路径 → 货号”）
// 与主动（后台下发 CATEGORY_CRAWL_RUN 后在本标签页内同源限速逐分类逐页抓取）。
// 分类采集的任何失败都被隔离在 try/catch 内，绝不影响按钮注入。
(async () => {
  // 分类采集模块单独加载：加载失败只停用分类采集，不影响按钮注入。
  const categoryModulesPromise = Promise.all([
    import(chrome.runtime.getURL('lib/category-path.js')),
    import(chrome.runtime.getURL('lib/category-capture.js')),
    import(chrome.runtime.getURL('lib/category-crawl.js')),
    import(chrome.runtime.getURL('lib/category-dom.js')),
  ])
    .then(([path, capture, crawl, dom]) => ({ path, capture, crawl, dom }))
    .catch(() => null);
  const [
    profilesMod,
    batchMod,
    itemNumberMod,
    stateMod,
    i18nMod,
    recoveryMod,
    storageCompatMod,
    rankingMod,
  ] = await Promise.all([
    import(chrome.runtime.getURL('lib/profiles.js')),
    import(chrome.runtime.getURL('lib/batch.js')),
    import(chrome.runtime.getURL('lib/item-number.js')),
    import(chrome.runtime.getURL('lib/dats-state.js')),
    import(chrome.runtime.getURL('lib/i18n.js')),
    import(chrome.runtime.getURL('lib/list-recovery.js')),
    import(chrome.runtime.getURL('lib/storage-compat.js')),
    import(chrome.runtime.getURL('lib/ranking.js')),
  ]);
  const { matchProfile, normalizeCategoryConfig } = profilesMod;
  const { createBatchQueue } = batchMod;
  const { readItemNumberFrom, readItemNumbersFromCards } = itemNumberMod;
  const {
    createGenerationGuard,
    createNodeStateRegistry,
    shouldInjectList,
    computeButtonState,
    buildSummaryCacheKey,
    normalizeSummaryMap,
  } = stateMod;
  const { normalizeLocale, t } = i18nMod;
  const {
    markSummaryRequestFailed,
    needsHostRemount,
    resetSummaryRetry,
    shouldRequestVisibleSummary,
  } = recoveryMod;
  const { matchesStorageArea } = storageCompatMod;
  const { formatSalesRankBand, normalizeRankingDays } = rankingMod;

  const origin = location.origin;

  const stored = await chrome.storage.local.get([
    'supplierProfiles',
    'selectedStoreCode',
    'locale',
    'salesRankingDays',
    'categoryCaptureSettings',
  ]);
  const { supplierProfiles } = stored;
  let selectedStoreCode = stored.selectedStoreCode || null;
  let locale = normalizeLocale(stored.locale);
  let salesRankingDays = normalizeRankingDays(stored.salesRankingDays);
  const profiles = (supplierProfiles && supplierProfiles.profiles) || [];
  const profile = matchProfile(profiles, { origin, pathname: location.pathname });
  if (!profile) return;

  function formatMessage(key, values = {}) {
    return Object.entries(values).reduce(
      (message, [name, value]) => message.replaceAll(`{${name}}`, String(value)),
      t(locale, key),
    );
  }

  const cardSelector = profile.cardSelector;
  const itemCfg = profile.itemNumber;
  const mountSelector = profile.mountSelector;
  const mountPosition = profile.mountPosition;

  try {
    document.querySelector(cardSelector);
    if (itemCfg.selector) document.querySelector(itemCfg.selector);
    if (mountSelector) document.querySelector(mountSelector);
  } catch {
    // 后台 selector 配置有语法错误时 fail closed，避免影响供应商页面。
    return;
  }

  const generation = createGenerationGuard(0);
  const registry = createNodeStateRegistry();
  const trackedCards = new Set();
  let active = true;
  let cardObserver = null;
  let visibilityObserver = null;
  let scanTimer = null;
  let scanInterval = null;
  let gfaLayoutStyle = null;

  // 读取卡片商品号：attribute/text + 声明式 transforms（与分类采集共用 lib/item-number.js）
  function readItemNumber(card) {
    return readItemNumberFrom(card, itemCfg);
  }

  // ---------- 供应商分类采集（被动 + 主动） ----------
  const categoryModules = await categoryModulesPromise;
  const CATEGORY_SELECTOR_FIELDS = [
    'breadcrumbSelector',
    'titleSelector',
    'navSelector',
    'subcategoryLinkSelector',
    'paginationNextSelector',
  ];
  // 被抓取页面 HTML 上限：异常大的响应不解析，按失败处理。
  const MAX_CATEGORY_HTML_LENGTH = 5 * 1024 * 1024;
  let categoryConfig = null;
  let categorySettings = stored.categoryCaptureSettings || {};
  let passiveCapture = null;
  let crawlSession = null;

  // 归一化 + 逐个选择器语法探测：非法选择器单项置 null，不让整个分类采集失效。
  function resolveCategoryConfig(sourceProfile) {
    try {
      if (!categoryModules || !sourceProfile) return null;
      const { config } = normalizeCategoryConfig(sourceProfile.category, sourceProfile);
      if (!config.enabled) return null;
      const probed = { ...config };
      for (const field of CATEGORY_SELECTOR_FIELDS) {
        if (probed[field]) probed[field] = categoryModules.dom.probeSelector(document, probed[field]);
      }
      if (!probed.titleSelector) probed.titleSelector = 'h1';
      return probed;
    } catch {
      return null;
    }
  }

  function isPassiveCaptureEnabled() {
    return !!categoryConfig?.enabled
      && categoryConfig.passiveEnabled
      && categorySettings?.[profile.supplierCode]?.passiveEnabled !== false;
  }

  function isCrawlEnabled() {
    return !!categoryConfig?.enabled && categoryConfig.crawlEnabled;
  }

  // 可被 AbortSignal 提前唤醒的等待。
  function abortableSleep(ms, signal) {
    return new Promise((resolve) => {
      if (signal?.aborted) {
        resolve();
        return;
      }
      const timer = setTimeout(done, Math.max(0, ms));
      function done() {
        clearTimeout(timer);
        signal?.removeEventListener('abort', done);
        resolve();
      }
      signal?.addEventListener('abort', done, { once: true });
    });
  }

  async function sendCategoryMessage(message) {
    try {
      const response = await chrome.runtime.sendMessage(message);
      return response || { ok: false, networkError: true };
    } catch (error) {
      // 扩展重载后旧内容脚本的消息通道失效，按可重试网络错误处理。
      return { ok: false, networkError: true, error: String(error?.message || error) };
    }
  }

  function rebuildPassiveCapture() {
    try {
      passiveCapture?.dispose();
      passiveCapture = null;
      if (!active || !categoryModules || !isPassiveCaptureEnabled()) return;
      passiveCapture = categoryModules.capture.createPassiveCaptureController({
        supplierCode: profile.supplierCode,
        getConfig: () => (isPassiveCaptureEnabled() ? categoryConfig : null),
        readPageContext: () => categoryModules.dom.readPageContext(document, categoryConfig, location.href),
        getCurrentHref: () => location.href,
        sendCapture: (payload) => sendCategoryMessage({ type: 'CATEGORY_CAPTURE', payload }),
        sleep: abortableSleep,
      });
    } catch {
      passiveCapture = null;
    }
  }

  // scan() 完成按钮注入后调用：只用已登记卡片的货号，不重复读取 DOM。
  function notifyCategoryCapture(cards) {
    if (!passiveCapture) return;
    try {
      const itemNumbers = [];
      for (const card of cards) {
        const entry = registry.get(card);
        if (entry?.itemNumber) itemNumbers.push(entry.itemNumber);
      }
      passiveCapture.notify({ href: location.href, itemNumbers });
    } catch {
      // 分类采集失败不影响按钮注入。
    }
  }

  function resetCategoryCapture() {
    try {
      passiveCapture?.reset();
    } catch {
      // 忽略
    }
  }

  // 同源抓取分类页：自带当前登录 Cookie；只接受 HTML，异常大的响应丢弃。
  async function fetchCategoryHtml(url, { signal } = {}) {
    const target = new URL(url, location.href);
    if (target.origin !== location.origin) return { ok: false, status: 0, finalUrl: target.href };
    const response = await fetch(target.href, {
      method: 'GET',
      credentials: 'include',
      redirect: 'follow',
      signal,
      headers: { Accept: 'text/html,application/xhtml+xml' },
    });
    const contentType = response.headers.get('content-type') || '';
    let html = '';
    if (response.ok && /html|xml/iu.test(contentType)) {
      html = await response.text();
      if (html.length > MAX_CATEGORY_HTML_LENGTH) html = '';
    }
    return {
      ok: response.ok && !!html,
      status: response.status,
      html,
      finalUrl: response.url || target.href,
      retryAfter: response.headers.get('Retry-After'),
    };
  }

  function parseCrawlPage(html, pageUrl, config) {
    const { dom } = categoryModules;
    const doc = dom.parseHtml(html);
    return {
      breadcrumbItems: config.breadcrumbSelector
        ? dom.readBreadcrumbItems(doc, config.breadcrumbSelector, pageUrl)
        : [],
      title: dom.readTitle(doc, config.titleSelector),
      itemNumbers: readItemNumbersFromCards(dom.readCards(doc, cardSelector), itemCfg),
      nextUrl: config.paginationNextSelector
        ? dom.readNextPageUrl(doc, config.paginationNextSelector, pageUrl)
        : null,
      subcategoryLinks: config.subcategoryLinkSelector
        ? dom.readSubcategoryLinks(doc, config.subcategoryLinkSelector, pageUrl)
        : [],
      hasPasswordField: dom.hasPasswordField(doc),
    };
  }

  // 枚举导航：当前页命中 navSelector 直接读 DOM；否则抓取 navRootUrl；再退化为当前页子分类链接。
  async function loadCategoryNavigation(config, signal) {
    const { dom, crawl } = categoryModules;
    if (config.navSelector && document.querySelector(config.navSelector)) {
      const anchors = dom.readNavAnchors(document, config.navSelector, location.href);
      if (anchors.length > 0) return { anchors, sourceUrl: location.href };
    }
    if (config.navSelector && config.navRootUrl) {
      const target = new URL(config.navRootUrl, location.href);
      if (target.origin !== location.origin) return { errorCode: 'NAV_NOT_FOUND' };
      let response;
      try {
        response = await fetchCategoryHtml(target.href, { signal });
      } catch {
        return { errorCode: signal?.aborted ? 'ABORTED' : 'NAV_NOT_FOUND' };
      }
      if (crawl.detectLoginPage({ requestedUrl: target.href, finalUrl: response.finalUrl })) {
        return { errorCode: 'LOGIN_REQUIRED' };
      }
      if (response.status === 401) return { errorCode: 'LOGIN_REQUIRED' };
      if (response.ok) {
        const doc = dom.parseHtml(response.html);
        const anchors = dom.readNavAnchors(doc, config.navSelector, response.finalUrl);
        if (anchors.length > 0) return { anchors, sourceUrl: response.finalUrl };
        if (dom.hasPasswordField(doc)) return { errorCode: 'LOGIN_REQUIRED' };
      }
    }
    if (config.subcategoryLinkSelector) {
      const anchors = dom.readSubcategoryLinks(document, config.subcategoryLinkSelector, location.href);
      if (anchors.length > 0) return { anchors, sourceUrl: location.href };
    }
    return { errorCode: 'NAV_NOT_FOUND' };
  }

  async function sendTreeSnapshot(jobId, sourceUrl, nodes, signal) {
    const { capture, crawl } = categoryModules;
    const payload = {
      supplierCode: profile.supplierCode,
      sourceUrl,
      nodes: crawl.toTreeSnapshotNodes(nodes),
    };
    if (payload.nodes.length === 0) return { ok: true, skipped: true };
    return capture.runWithRetry(
      () => sendCategoryMessage({ type: 'CATEGORY_TREE_SNAPSHOT', jobId, payload }),
      { sleep: abortableSleep, signal },
    );
  }

  async function runCategoryCrawl({ jobId, completedKeys, onlyNodes }, controller) {
    const { crawl } = categoryModules;
    const config = categoryConfig;
    const signal = controller.signal;
    const report = (progress) => {
      void sendCategoryMessage({
        type: 'CATEGORY_CRAWL_PROGRESS',
        jobId,
        progress: { ...progress, jobId },
      });
    };
    const fail = (errorCode) => report({ status: 'failed', errorCode });
    try {
      const nav = await loadCategoryNavigation(config, signal);
      if (signal.aborted) {
        report({ status: 'aborted' });
        return;
      }
      const retryOnly = Array.isArray(onlyNodes) && onlyNodes.length > 0;
      if (nav.errorCode && (!retryOnly || nav.errorCode === 'LOGIN_REQUIRED')) {
        fail(nav.errorCode);
        return;
      }
      const tree = nav.anchors
        ? crawl.buildNavTree(nav.anchors, { config, origin: location.origin })
        : { nodes: [] };
      if (tree.nodes.length === 0 && !retryOnly) {
        fail('NAV_NOT_FOUND');
        return;
      }
      if (!retryOnly) {
        // 先提交导航树快照，让后台在采集前就能看到完整分类（含暂无商品的空分类）。
        const snapshot = await sendTreeSnapshot(jobId, nav.sourceUrl, tree.nodes, signal);
        if (!snapshot.ok && snapshot.classification?.fatal) {
          fail(snapshot.classification.code);
          return;
        }
      }
      const runner = crawl.createCrawlRunner({
        config,
        origin: location.origin,
        supplierCode: profile.supplierCode,
        fetchHtml: fetchCategoryHtml,
        parsePage: (html, pageUrl) => parseCrawlPage(html, pageUrl, config),
        onCapture: (payload) => sendCategoryMessage({ type: 'CATEGORY_CAPTURE', jobId, payload }),
        onProgress: report,
        sleep: abortableSleep,
        signal,
      });
      const result = await runner.run({
        nodes: tree.nodes,
        completedKeys: Array.isArray(completedKeys) ? completedKeys : [],
        onlyNodes: retryOnly ? onlyNodes : null,
      });
      if (!retryOnly && result.discovered.length > 0 && !signal.aborted) {
        // 子分类链接发现的新节点补一次快照（服务端按 key 幂等 upsert）。
        await sendTreeSnapshot(jobId, nav.sourceUrl, [...tree.nodes, ...result.discovered], signal);
      }
    } catch {
      if (signal.aborted) report({ status: 'aborted' });
      else fail('CRAWL_FAILED');
    }
  }

  function startCategoryCrawl(message) {
    if (!active || !categoryModules) return { ok: false, errorCode: 'CONTENT_SCRIPT_UNAVAILABLE' };
    if (message.supplierCode !== profile.supplierCode || !isCrawlEnabled()) {
      return { ok: false, errorCode: 'CRAWL_DISABLED' };
    }
    if (crawlSession) return { ok: false, errorCode: 'CRAWL_ALREADY_RUNNING' };
    if (typeof message.jobId !== 'string' || !message.jobId) return { ok: false, errorCode: 'INVALID_JOB' };
    const controller = new AbortController();
    crawlSession = { jobId: message.jobId, controller };
    void runCategoryCrawl(message, controller)
      .catch(() => undefined)
      .finally(() => {
        if (crawlSession?.controller === controller) crawlSession = null;
      });
    return { ok: true, accepted: true };
  }

  // 页面卸载时尽力上报中断；service worker 另有 tabs.onRemoved/onUpdated 兜底。
  function handleCategoryPageHide() {
    if (!crawlSession) return;
    const { jobId, controller } = crawlSession;
    try {
      void chrome.runtime.sendMessage({
        type: 'CATEGORY_CRAWL_PROGRESS',
        jobId,
        progress: { jobId, status: 'interrupted' },
      }).catch(() => undefined);
    } catch {
      // 扩展上下文已失效时忽略
    }
    controller.abort();
  }

  categoryConfig = resolveCategoryConfig(profile);
  rebuildPassiveCapture();
  if (categoryModules) {
    chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
      const type = message && message.type;
      if (type !== 'CATEGORY_CRAWL_RUN' && type !== 'CATEGORY_CRAWL_STOP') return false;
      // 只接受扩展后台（不来自任何标签页）下发的采集指令。
      if (sender?.id !== chrome.runtime.id || sender?.tab) {
        sendResponse({ ok: false, errorCode: 'FORBIDDEN' });
        return false;
      }
      try {
        if (type === 'CATEGORY_CRAWL_STOP') {
          if (crawlSession && (!message.jobId || message.jobId === crawlSession.jobId)) {
            crawlSession.controller.abort();
          }
          sendResponse({ ok: true });
          return false;
        }
        sendResponse(startCategoryCrawl(message));
      } catch {
        sendResponse({ ok: false, errorCode: 'CONTENT_SCRIPT_UNAVAILABLE' });
      }
      return false;
    });
    window.addEventListener('pagehide', handleCategoryPageHide);
  }

  function ensureGfaLayoutStyle() {
    if (gfaLayoutStyle?.isConnected) return;
    const existing = document.querySelector('style[data-hb-sro-gfa-layout]');
    if (existing) {
      gfaLayoutStyle = existing;
      return;
    }
    gfaLayoutStyle = document.createElement('style');
    gfaLayoutStyle.setAttribute('data-hb-sro-gfa-layout', '');
    // GFA 的 100px 小列表行容不下商品明细和两行摘要；让内容按摘要高度自然扩展。
    gfaLayoutStyle.textContent = `
.list-row[data-product]:has(> .content > [data-hb-sro-host]) > .content {
  height: auto !important;
  min-height: 100px;
}
.list-row[data-product]:has(> .content > [data-hb-sro-host]) > .content > a[href*="/product/view?id="] > .list-content {
  height: auto !important;
}
.list-row[data-product]:has(> .content > [data-hb-sro-host]) > .content > a[href*="/product/view?id="] > .list-content .list-detail {
  height: auto !important;
}
@media (max-width: 500px) {
  .list-row[data-product]:has(> .content > [data-hb-sro-host]) > .content {
    padding-bottom: 46px !important;
  }
  .list-row[data-product] > .content > [data-hb-sro-host] {
    margin-right: 0 !important;
  }
}`;
    (document.head || document.documentElement).appendChild(gfaLayoutStyle);
  }

  function mountHost(card) {
    let mountEl = card;
    let pos = 'beforeend';
    if (mountSelector) {
      const found = card.querySelector(mountSelector);
      if (found) {
        mountEl = found;
        pos = mountPosition || 'afterend';
      }
    }
    const host = document.createElement('div');
    host.setAttribute('data-hb-sro-host', '');
    const isGfaFixedHeightRow =
      profile.supplierCode === '236' && card.matches('.list-row[data-product]');
    if (isGfaFixedHeightRow) ensureGfaLayoutStyle();
    host.style.cssText = isGfaFixedHeightRow
      ? 'display:block;margin:4px 235px 0 0;position:relative;z-index:2;pointer-events:none;'
      : 'display:block;margin:4px 0;';
    mountEl.insertAdjacentElement(pos, host);
    return host;
  }

  function createShadowButton(host) {
    // 供应商页面只获得一个不可读的宿主节点，不能遍历本店销售摘要文本。
    const root = host.attachShadow({ mode: 'closed' });
    const style = document.createElement('style');
    style.textContent = [
      '.hb-btn{all:unset;box-sizing:border-box;display:inline-block;max-width:100%;padding:4px 8px;border-radius:4px;border:1px solid #d5d5d5;background:#fafafa;color:#333;cursor:pointer;font:12px/1.5 system-ui,sans-serif;white-space:normal;overflow-wrap:anywhere;pointer-events:auto;}',
      '.hb-btn:focus-visible{outline:2px solid #2563eb;outline-offset:2px;}',
      '.hb-order{color:#c62828;font-weight:600;}',
      '.hb-sales{color:#1565c0;font-weight:600;}',
      '.hb-muted{color:#757575;}',
      '.hb-rank-line{display:block;width:max-content;max-width:100%;box-sizing:border-box;margin-top:2px;padding:1px 6px;border:1px solid #b8d8ff;border-radius:999px;background:#eaf3ff;color:#1565c0;font-size:10px;font-weight:700;line-height:1.5;overflow-wrap:anywhere;white-space:normal;}',
      '.hb-rank-line-top-20{border-color:#c7e3ca;background:#eef7ef;color:#2e7d32;}',
      '.hb-rank-line-top-30{border-color:#ddd0ef;background:#f5f1fb;color:#6f3cc3;}',
    ].join('');
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'hb-btn';
    root.appendChild(style);
    root.appendChild(btn);
    return btn;
  }

  function renderButton(entry, state) {
    const btn = entry.btn;
    btn.replaceChildren();
    if (state.kind === 'loading') {
      btn.textContent = t(locale, 'loading');
    } else if (state.kind === 'none' || state.kind === 'error') {
      const span = document.createElement('span');
      span.className = 'hb-muted';
      span.textContent = shortStatus(state);
      btn.appendChild(span);
    } else if (state.kind === 'noStore') {
      const span = document.createElement('span');
      span.className = 'hb-muted';
      span.textContent = t(locale, 'noStore');
      btn.appendChild(span);
    } else {
      const order = document.createElement('span');
      order.className = 'hb-order';
      order.textContent = `${t(locale, 'lastOrder')} ${state.lastOrderDate || '—'} × ${state.lastOrderQuantity ?? 0}`;
      const sales = document.createElement('span');
      sales.className = 'hb-sales';
      sales.textContent = `${t(locale, 'salesToDate')} ${state.salesToDate ?? 0}`;
      btn.appendChild(order);
      btn.appendChild(document.createTextNode(' · '));
      btn.appendChild(sales);
    }
    const rankLabel = formatSalesRankBand(state.salesRankBand);
    if ((state.kind === 'ok' || state.reason === 'noPurchase') && rankLabel) {
      const rankLine = document.createElement('span');
      rankLine.className = 'hb-rank-line';
      rankLine.classList.add(`hb-rank-line-${state.salesRankBand}`);
      rankLine.textContent = formatMessage('salesRankBand', {
        days: state.salesRankingDays,
        band: rankLabel,
      });
      btn.appendChild(rankLine);
    }
  }

  function shortStatus(state) {
    if (state.kind === 'error') return t(locale, 'error');
    if (state.reason === 'noPurchase') return t(locale, 'noPurchase');
    return t(locale, 'noMatch');
  }

  function requestSummary(entry) {
    if (!active || entry.requested) return;
    if (entry.state?.kind === 'loading') resetSummaryRetry(entry);
    entry.requested = true;
    const requestedGeneration = entry.generation;
    const requestedItemNumber = entry.itemNumber;
    const requestedCard = entry.card;
    const requestedRankingDays = salesRankingDays;
    batch
      .enqueue(
        buildSummaryCacheKey(selectedStoreCode, requestedItemNumber, salesRankingDays),
        requestedItemNumber,
      )
      .then((summary) => {
        if (
          !active
          || !generation.isCurrent(requestedGeneration)
          || registry.get(requestedCard) !== entry
          || entry.itemNumber !== requestedItemNumber
          || !requestedCard.isConnected
        ) {
          return;
        }
        const state = summary && summary.storeMissing
          ? { kind: 'noStore' }
          : computeButtonState({ ...summary, salesRankingDays: requestedRankingDays });
        resetSummaryRetry(entry);
        entry.state = state;
        renderButton(entry, state);
      })
      .catch(() => {
        if (
          !active
          || !generation.isCurrent(requestedGeneration)
          || registry.get(requestedCard) !== entry
          || entry.itemNumber !== requestedItemNumber
        ) {
          return;
        }
        const state = markSummaryRequestFailed(entry);
        renderButton(entry, state);
      });
  }

  // 每个队列固定绑定门店与排名周期；上下文变化时换代，旧请求即使晚返回也会被 generation 丢弃。
  function createSummaryBatch(storeCode, rankingDays) {
    return createBatchQueue({
      maxSize: 100,
      delayMs: 150,
      cacheTtlMs: 60000,
      flush: async (entries) => {
        if (!storeCode) {
          const out = {};
          for (const e of entries) out[e.key] = { storeMissing: true };
          return out;
        }
        const itemNumbers = entries.map((e) => e.item);
        const resp = await chrome.runtime.sendMessage({
          type: 'SUMMARY_BATCH',
          storeCode,
          supplierCode: profile.supplierCode,
          itemNumbers,
          salesRankingDays: rankingDays,
        });
        if (!resp || !resp.ok) {
          throw new Error((resp && resp.error) || 'summary request failed');
        }
        const map = normalizeSummaryMap(resp && resp.data);
        const out = {};
        for (const e of entries) out[e.key] = map[e.item] || { hasMatch: false };
        return out;
      },
    });
  }

  let batch = createSummaryBatch(selectedStoreCode, salesRankingDays);

  function attachEntryButton(entry) {
    entry.host?.remove();
    entry.card.querySelector('[data-hb-sro-host]')?.remove();
    entry.host = mountHost(entry.card);
    entry.btn = createShadowButton(entry.host);
    entry.btn.addEventListener('click', () => {
      chrome.runtime.sendMessage({
        type: 'LOCATE_ITEM',
        storeCode: selectedStoreCode || null,
        supplierCode: profile.supplierCode,
        itemNumber: entry.itemNumber,
      });
    });
  }

  function ensureCard(card) {
    const itemNumber = readItemNumber(card);
    const existing = registry.get(card);
    if (!itemNumber) {
      if (existing) {
        visibilityObserver?.unobserve(card);
        existing.host?.remove();
        registry.delete(card);
        trackedCards.delete(card);
      }
      return null;
    }

    let entry = existing;
    if (!entry) {
      entry = {
        generation: generation.current(),
        card,
        itemNumber,
        host: null,
        btn: null,
        state: { kind: 'loading' },
        requested: false,
        isVisible: false,
      };
      // 扩展更新或脚本重载后，清理失去事件处理器的旧 host，再重新挂载。
      attachEntryButton(entry);
      registry.set(card, entry);
      trackedCards.add(card);
      if (visibilityObserver) visibilityObserver.observe(card);
    } else {
      entry.generation = generation.current();
      if (needsHostRemount(entry)) attachEntryButton(entry);
      if (entry.itemNumber !== itemNumber) {
        entry.itemNumber = itemNumber;
        entry.requested = false;
        entry.state = { kind: 'loading' };
        if (entry.isVisible) requestSummary(entry);
      }
    }
    renderButton(entry, entry.state);
    if (shouldRequestVisibleSummary(entry)) requestSummary(entry);
    return entry;
  }

  function scan() {
    if (!active) return;
    for (const card of trackedCards) {
      if (card.isConnected) continue;
      visibilityObserver?.unobserve(card);
      registry.delete(card);
      trackedCards.delete(card);
    }
    const cards = Array.from(document.querySelectorAll(cardSelector));
    const pageEligible = shouldInjectList({
      href: location.href,
      listPagePatterns: profile.listPagePatterns,
      cardCount: cards.length,
      isDetailPage:
        document.body.classList.contains('catalog-product-view')
        || document.body.classList.contains('page-ProductDetail')
        || !!document.querySelector('.product-info-main, [data-role="product-info-main"]'),
    });
    if (!pageEligible) {
      for (const card of trackedCards) {
        const entry = registry.get(card);
        visibilityObserver?.unobserve(card);
        entry?.host?.remove();
        registry.delete(card);
      }
      trackedCards.clear();
      resetCategoryCapture();
      return;
    }
    for (const card of cards) {
      ensureCard(card);
    }
    // 按钮注入完成后再通知分类采集；内部全程 try/catch。
    notifyCategoryCapture(cards);
  }

  // 仅可见（含 600px 缓冲）卡片才请求摘要
  visibilityObserver = new IntersectionObserver(
    (entries) => {
      for (const e of entries) {
        const entry = registry.get(e.target);
        if (!entry) continue;
        entry.isVisible = e.isIntersecting;
        if (e.isIntersecting && shouldRequestVisibleSummary(entry)) requestSummary(entry);
      }
    },
    { rootMargin: '600px' },
  );

  // 监听新增节点与 data-* 属性变化
  const attributeFilter = itemCfg.source === 'attribute' && itemCfg.attribute ? [itemCfg.attribute] : [];
  cardObserver = new MutationObserver((mutations) => {
    let shouldScan = false;
    for (const m of mutations) {
      if (m.type === 'childList') {
        const target = m.target?.nodeType === 1 ? m.target : m.target?.parentElement;
        if (target && (target.matches?.(cardSelector) || target.closest?.(cardSelector))) {
          shouldScan = true;
        }
        for (const node of m.addedNodes) {
          if (node && node.nodeType === 1) {
            const el = node;
            if (typeof el.matches === 'function' && (el.matches(cardSelector) || el.querySelector(cardSelector))) {
              shouldScan = true;
              break;
            }
          }
        }
      } else if (m.type === 'attributes' || m.type === 'characterData') {
        const target = m.target?.nodeType === 1 ? m.target : m.target?.parentElement;
        if (target && (target.matches?.(cardSelector) || target.closest?.(cardSelector))) {
          shouldScan = true;
        }
      }
      if (shouldScan) break;
    }
    if (shouldScan) scheduleScan();
  });
  const observerOptions = {
    childList: true,
    subtree: true,
  };
  if (attributeFilter.length > 0) {
    observerOptions.attributes = true;
    observerOptions.attributeFilter = attributeFilter;
  }
  if (itemCfg.source === 'text') {
    observerOptions.characterData = true;
  }
  cardObserver.observe(document.body, observerOptions);

  function scheduleScan() {
    if (scanTimer !== null) return;
    scanTimer = setTimeout(() => {
      scanTimer = null;
      scan();
    }, 50);
  }

  // SPA 导航/过滤：代次守卫 + 重扫；周期扫描作为兜底（幂等，不重复按钮）
  const handleNavigation = () => {
    generation.advance();
    // SPA 导航后重新等待新页面稳定再采集。
    resetCategoryCapture();
    for (const card of trackedCards) {
      const entry = registry.get(card);
      if (entry) {
        entry.generation = generation.current();
        entry.requested = false;
        entry.state = { kind: 'loading' };
      }
    }
    scan();
  };
  window.addEventListener('popstate', handleNavigation);
  window.addEventListener('hashchange', handleNavigation);

  function refreshForStore(storeCode) {
    refreshSummaryContext({ storeCode });
  }

  function refreshSummaryContext({
    storeCode = selectedStoreCode,
    rankingDays = salesRankingDays,
  } = {}) {
    selectedStoreCode = storeCode || null;
    salesRankingDays = normalizeRankingDays(rankingDays);
    generation.advance();
    resetCategoryCapture();
    batch.clearCache();
    batch = createSummaryBatch(selectedStoreCode, salesRankingDays);
    for (const card of trackedCards) {
      const entry = registry.get(card);
      if (!entry || !card.isConnected) continue;
      entry.generation = generation.current();
      entry.requested = false;
      entry.state = { kind: 'loading' };
      renderButton(entry, entry.state);
      if (entry.isVisible) requestSummary(entry);
    }
  }

  function teardown() {
    if (!active) return;
    active = false;
    cardObserver?.disconnect();
    visibilityObserver?.disconnect();
    if (scanTimer !== null) clearTimeout(scanTimer);
    if (scanInterval !== null) clearInterval(scanInterval);
    window.removeEventListener('popstate', handleNavigation);
    window.removeEventListener('hashchange', handleNavigation);
    for (const card of trackedCards) {
      registry.get(card)?.host?.remove();
    }
    trackedCards.clear();
    try {
      passiveCapture?.dispose();
      passiveCapture = null;
      crawlSession?.controller.abort();
      window.removeEventListener('pagehide', handleCategoryPageHide);
    } catch {
      // 忽略
    }
  }

  chrome.storage.onChanged.addListener((changes, areaName) => {
    if (!matchesStorageArea(areaName, 'local') || !active) return;
    if (changes.selectedStoreCode && !changes.salesRankingDays) {
      refreshForStore(changes.selectedStoreCode.newValue);
    } else if (changes.selectedStoreCode || changes.salesRankingDays) {
      refreshSummaryContext({
        storeCode: changes.selectedStoreCode
          ? changes.selectedStoreCode.newValue
          : selectedStoreCode,
        rankingDays: changes.salesRankingDays
          ? changes.salesRankingDays.newValue
          : salesRankingDays,
      });
    }
    if (changes.locale) {
      locale = normalizeLocale(changes.locale.newValue);
      for (const card of trackedCards) {
        const entry = registry.get(card);
        if (entry) renderButton(entry, entry.state);
      }
    }
    if (changes.supplierProfiles) {
      const updatedProfiles = changes.supplierProfiles.newValue?.profiles || [];
      const updatedProfile = matchProfile(updatedProfiles, { origin, pathname: location.pathname });
      if (!updatedProfile || updatedProfile.supplierCode !== profile.supplierCode) {
        teardown();
      } else {
        // 后台热更新分类配置（selector 修正、停用等）立即生效，无需刷新页面。
        categoryConfig = resolveCategoryConfig(updatedProfile);
        if (!isCrawlEnabled()) crawlSession?.controller.abort();
        rebuildPassiveCapture();
      }
    }
    if (changes.categoryCaptureSettings && active) {
      // 侧栏被动采集开关热更新。
      categorySettings = changes.categoryCaptureSettings.newValue || {};
      rebuildPassiveCapture();
    }
  });

  scanInterval = setInterval(scan, 2000);

  scan();
})();
