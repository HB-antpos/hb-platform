import test from 'node:test';
import assert from 'node:assert/strict';
import {
  BUILTIN_CATEGORY_EXCLUDE_PATTERNS,
  DEFAULT_PROMOTIONAL_PATTERNS,
} from '../src/lib/category-path.js';
import {
  CRAWL_STATUSES,
  STALE_RUNNING_JOB_MS,
  buildNavTree,
  createCrawlJob,
  createCrawlRunner,
  detectLoginPage,
  finalizeCrawlJob,
  inferParentByPrefix,
  isCrawlJobStale,
  mergeCrawlProgress,
  resolvePathForNode,
  summarizeProgress,
  toCrawlHistoryEntry,
  toTreeSnapshotNodes,
} from '../src/lib/category-crawl.js';

const ORIGIN = 'https://www.dats.com.au';
const CONFIG = {
  enabled: true,
  crawlEnabled: true,
  keySource: 'pathname',
  keyQueryParams: [],
  breadcrumbSkip: 1,
  categoryPagePatterns: ['https://www.dats.com.au/*'],
  categoryExcludePatterns: [...BUILTIN_CATEGORY_EXCLUDE_PATTERNS],
  promotionalPatterns: [...DEFAULT_PROMOTIONAL_PATTERNS],
  maxPages: 20,
  maxDepth: 4,
  maxCategories: 400,
  crawlDelayMs: 1500,
};

const anchor = (path, name, domParentPath = null) => ({
  name,
  url: `${ORIGIN}${path}`,
  domParentUrl: domParentPath ? `${ORIGIN}${domParentPath}` : null,
});

test('URL 前缀推断父级，查询参数型 key 不推断', () => {
  const keys = new Set(['/a', '/a/b']);
  assert.equal(inferParentByPrefix('/a/b/c', keys), '/a/b');
  assert.equal(inferParentByPrefix('/a/x/y', keys), '/a');
  assert.equal(inferParentByPrefix('/z', keys), null);
  assert.equal(inferParentByPrefix('/a/b?cat=1', keys), null);
});

test('导航树：DOM 父级优先、URL 前缀兜底、按 key 去重、剔除促销子树与站外链接', () => {
  const { nodes, droppedPromotional } = buildNavTree([
    anchor('/clearance', 'Clearance'),
    anchor('/clearance/clearance-items', 'Clearance Items', '/clearance'),
    anchor('/office-stationery', 'Office Stationery'),
    anchor('/office-stationery/mailing', 'Mailing'),
    anchor('/everyday-dinnerware', 'Everyday Dinnerware'),
    // DATS 真实结构：子分类 URL 不在父分类路径下，只能靠 DOM 推父级。
    anchor('/disposable-dinnerware/serviettes', 'Serviettes', '/everyday-dinnerware'),
    anchor('/office-stationery/', 'Office Stationery duplicate'),
    { name: 'Evil', url: 'https://evil.example/office' },
    { name: 'Search', url: `${ORIGIN}/search?q=a` },
    { name: 'Home', url: `${ORIGIN}/` },
    { name: 'JS', url: 'javascript:void(0)' },
  ], { config: CONFIG, origin: ORIGIN });
  assert.deepEqual(nodes.map((node) => [node.key, node.parentKey, node.depth]), [
    ['/office-stationery', null, 0],
    ['/everyday-dinnerware', null, 0],
    ['/office-stationery/mailing', '/office-stationery', 1],
    ['/disposable-dinnerware/serviettes', '/everyday-dinnerware', 1],
  ]);
  assert.equal(droppedPromotional, 1, '促销根节点被剔除，其子树随之丢弃');
  assert.equal(nodes[0].name, 'Office Stationery', '重复链接保留首个名称');
  assert.deepEqual(nodes.map((node) => node.sortOrder), [1, 2, 0, 0]);
});

test('导航树：maxDepth 丢弃过深节点，maxCategories 按 BFS 截断且父节点先于子节点', () => {
  const anchors = [
    anchor('/a', 'A'),
    anchor('/a/b', 'B'),
    anchor('/a/b/c', 'C'),
    anchor('/x', 'X'),
    anchor('/x/y', 'Y'),
  ];
  const shallow = buildNavTree(anchors, { config: { ...CONFIG, maxDepth: 2 }, origin: ORIGIN });
  assert.deepEqual(shallow.nodes.map((node) => node.key), ['/a', '/x', '/a/b', '/x/y']);
  assert.equal(shallow.droppedDepth, 1);

  const truncated = buildNavTree(anchors, { config: { ...CONFIG, maxCategories: 3 }, origin: ORIGIN });
  assert.deepEqual(truncated.nodes.map((node) => node.key), ['/a', '/x', '/a/b']);
  assert.equal(truncated.truncated, true);
});

test('导航树：异常 DOM 父级形成环时断开', () => {
  const { nodes } = buildNavTree([
    anchor('/p', 'P', '/q'),
    anchor('/q', 'Q', '/p'),
  ], { config: CONFIG, origin: ORIGIN });
  assert.equal(nodes.length, 2);
  assert.ok(nodes.some((node) => node.parentKey === null));
});

test('节点路径回溯与快照节点形状', () => {
  const { nodes } = buildNavTree([
    anchor('/a', 'A'),
    anchor('/a/b', 'B'),
    anchor('/a/b/c', 'C'),
  ], { config: CONFIG, origin: ORIGIN });
  const byKey = new Map(nodes.map((node) => [node.key, node]));
  assert.deepEqual(resolvePathForNode(byKey, '/a/b/c').map((node) => node.key), ['/a', '/a/b', '/a/b/c']);
  assert.deepEqual(toTreeSnapshotNodes(nodes)[2], {
    key: '/a/b/c',
    name: 'C',
    parentKey: '/a/b',
    url: `${ORIGIN}/a/b/c`,
    sortOrder: 0,
  });
});

test('登录页识别：重定向到登录路径，或只有密码框没有商品与面包屑', () => {
  assert.equal(detectLoginPage({ requestedUrl: `${ORIGIN}/pens`, finalUrl: `${ORIGIN}/login?returnUrl=/pens` }), true);
  assert.equal(detectLoginPage({ requestedUrl: `${ORIGIN}/pens`, finalUrl: `${ORIGIN}/my-account/` }), true);
  assert.equal(detectLoginPage({ requestedUrl: `${ORIGIN}/pens`, finalUrl: `${ORIGIN}/pens` }), false);
  assert.equal(detectLoginPage({ requestedUrl: `${ORIGIN}/pens`, finalUrl: `${ORIGIN}/pens`, hasPasswordField: true }), true);
  assert.equal(
    detectLoginPage({ requestedUrl: `${ORIGIN}/pens`, finalUrl: `${ORIGIN}/pens`, hasPasswordField: true, cardCount: 3 }),
    false,
    '页头登录框 + 有商品卡片不是登录页',
  );
});

test('任务状态：进度合并、终态锁定、过期判定与历史摘要', () => {
  let clock = Date.parse('2026-09-23T00:00:00Z');
  const now = () => clock;
  const job = createCrawlJob({ jobId: 'j1', supplierCode: '240', tabId: 7, origin: ORIGIN, now, completedKeys: ['/a', 'bad'] });
  assert.equal(job.status, CRAWL_STATUSES.RUNNING);
  assert.deepEqual(job.completedKeys, ['/a']);

  assert.equal(mergeCrawlProgress(job, { jobId: 'other', done: 9 }, { now }), job, '其他任务的进度被忽略');
  clock += 1000;
  const running = mergeCrawlProgress(job, {
    jobId: 'j1',
    status: 'running',
    total: 10,
    done: 2,
    completedKeys: ['/b'],
    failedNodes: [{ key: '/c', name: 'C', url: `${ORIGIN}/c` }],
    current: { key: '/d', name: 'D' },
  }, { now });
  assert.deepEqual(running.completedKeys, ['/a', '/b']);
  assert.equal(running.failedNodes.length, 1);
  assert.equal(running.current.name, 'D');
  assert.deepEqual(summarizeProgress(running), { total: 10, done: 2, failed: 0, remaining: 8, percent: 20 });

  const finished = mergeCrawlProgress(running, { jobId: 'j1', status: 'failed', errorCode: 'SITE_BLOCKING' }, { now });
  assert.equal(finished.status, 'failed');
  assert.equal(finished.errorCode, 'SITE_BLOCKING');
  assert.equal(finished.current, null);
  const late = mergeCrawlProgress(finished, { jobId: 'j1', status: 'running', done: 3 }, { now });
  assert.equal(late.status, 'failed', '终态后不再回到运行中');
  assert.equal(late.done, 3);
  assert.equal(finalizeCrawlJob(finished, 'aborted'), finished);

  const history = toCrawlHistoryEntry(late);
  assert.equal(history.status, 'failed');
  assert.deepEqual(history.completedKeys, ['/a', '/b']);

  assert.equal(isCrawlJobStale(running, now), false);
  clock += STALE_RUNNING_JOB_MS + 1;
  assert.equal(isCrawlJobStale(running, now), true);
  assert.equal(isCrawlJobStale(finished, now), false);
});

// ---------- 执行器 ----------

function page({ items = [], next = null, breadcrumb = null, title = '', subcategories = [], password = false } = {}) {
  return { items, next, breadcrumb, title, subcategories, password };
}

function createSite(pages) {
  return {
    fetches: [],
    async fetchHtml(url) {
      this.fetches.push(url);
      const entry = pages[url];
      if (!entry) return { ok: false, status: 404, finalUrl: url };
      if (entry.status) return { ok: false, status: entry.status, finalUrl: url, retryAfter: entry.retryAfter };
      if (entry.redirect) return { ok: true, status: 200, html: 'login', finalUrl: entry.redirect };
      return { ok: true, status: 200, html: url, finalUrl: url };
    },
    parsePage(html) {
      const entry = pages[html] || {};
      return {
        breadcrumbItems: entry.breadcrumb || [],
        title: entry.title || '',
        itemNumbers: entry.items || [],
        nextUrl: entry.next,
        subcategoryLinks: entry.subcategories || [],
        hasPasswordField: !!entry.password,
      };
    },
  };
}

function createRunnerHarness(pages, options = {}) {
  const site = createSite(pages);
  let clock = 0;
  const sleeps = [];
  const captures = [];
  const progress = [];
  const runner = createCrawlRunner({
    config: { ...CONFIG, ...(options.config || {}) },
    origin: ORIGIN,
    supplierCode: '240',
    fetchHtml: (url) => site.fetchHtml(url),
    parsePage: (html, url) => site.parsePage(html, url),
    onCapture: async (payload) => {
      captures.push(payload);
      return options.onCapture ? options.onCapture(payload) : { ok: true };
    },
    onProgress: (value) => progress.push(value),
    sleep: async (ms) => {
      sleeps.push(ms);
      clock += ms;
      options.onSleep?.(ms);
    },
    now: () => clock,
    signal: options.signal || null,
    progressIntervalMs: 1000,
  });
  return { site, runner, sleeps, captures, progress, tick: (ms) => { clock += ms; } };
}

const node = (path, name, parentPath = null, depth = parentPath ? 1 : 0) => ({
  key: path,
  name,
  url: `${ORIGIN}${path}`,
  parentKey: parentPath,
  depth,
  sortOrder: 0,
});

test('执行器：逐分类抓取、跟随同分类分页、限速并按 100 分块回传', async () => {
  const items = Array.from({ length: 130 }, (_, index) => `A${index}`);
  const pages = {
    [`${ORIGIN}/a`]: page({ items: items.slice(0, 100), next: `${ORIGIN}/a?PageProduct=2` }),
    [`${ORIGIN}/a?PageProduct=2`]: page({ items: items.slice(100), next: `${ORIGIN}/b` }),
    [`${ORIGIN}/b`]: page({
      items: ['B1'],
      breadcrumb: [{ name: 'Home', url: `${ORIGIN}/` }, { name: 'Bee', url: `${ORIGIN}/b` }],
    }),
  };
  const harness = createRunnerHarness(pages);
  const result = await harness.runner.run({ nodes: [node('/a', 'A'), node('/b', 'B')] });
  assert.equal(result.status, 'completed');
  assert.deepEqual(result.completedKeys, ['/a', '/b']);
  assert.deepEqual(harness.site.fetches, [`${ORIGIN}/a`, `${ORIGIN}/a?PageProduct=2`, `${ORIGIN}/b`]);
  assert.deepEqual(harness.sleeps, [1500, 1500], '请求之间按 crawlDelayMs 限速');
  assert.deepEqual(harness.captures.map((payload) => [payload.pageNumber, payload.itemNumbers.length]), [[1, 100], [2, 30], [1, 1]]);
  assert.ok(harness.captures.every((payload) => payload.mode === 'crawl'));
  assert.deepEqual(harness.captures[2].categoryPath, [{ name: 'Bee', key: '/b', url: `${ORIGIN}/b` }], '面包屑叶子匹配时优先面包屑');
  assert.equal(result.itemsSent, 131);
  const last = harness.progress[harness.progress.length - 1];
  assert.equal(last.status, 'completed');
  assert.equal(last.done, 2);
  assert.equal(last.total, 2);
});

test('执行器：分页不超过 maxPages，重复页面不再抓取', async () => {
  const pages = {
    [`${ORIGIN}/a`]: page({ items: ['1'], next: `${ORIGIN}/a?p=2` }),
    [`${ORIGIN}/a?p=2`]: page({ items: ['2'], next: `${ORIGIN}/a?p=3` }),
    [`${ORIGIN}/a?p=3`]: page({ items: ['3'], next: `${ORIGIN}/a` }),
  };
  const limited = createRunnerHarness(pages, { config: { maxPages: 2 } });
  await limited.runner.run({ nodes: [node('/a', 'A')] });
  assert.equal(limited.site.fetches.length, 2);

  const looping = createRunnerHarness(pages);
  await looping.runner.run({ nodes: [node('/a', 'A')] });
  assert.equal(looping.site.fetches.length, 3, '回到第一页时停止');
});

test('执行器：续跑跳过已完成分类；重试只抓失败节点', async () => {
  const pages = {
    [`${ORIGIN}/a`]: page({ items: ['A'] }),
    [`${ORIGIN}/b`]: page({ items: ['B'] }),
  };
  const resume = createRunnerHarness(pages);
  const resumed = await resume.runner.run({ nodes: [node('/a', 'A'), node('/b', 'B')], completedKeys: ['/a'] });
  assert.deepEqual(resume.site.fetches, [`${ORIGIN}/b`]);
  assert.equal(resumed.done, 2);

  const retry = createRunnerHarness(pages);
  const retried = await retry.runner.run({ nodes: [node('/a', 'A')], onlyNodes: [node('/b', 'B')] });
  assert.deepEqual(retry.site.fetches, [`${ORIGIN}/b`]);
  assert.equal(retried.total, 1);
  assert.equal(retried.discovered.length, 0);
});

test('执行器：429 按 Retry-After（≤60 秒）退避重试', async () => {
  let attempts = 0;
  const pages = {
    [`${ORIGIN}/a`]: page({ items: ['A'] }),
  };
  const harness = createRunnerHarness(pages);
  const original = harness.site.fetchHtml.bind(harness.site);
  harness.site.fetchHtml = async (url) => {
    attempts += 1;
    if (attempts === 1) return { ok: false, status: 429, finalUrl: url, retryAfter: '120' };
    return original(url);
  };
  const result = await harness.runner.run({ nodes: [node('/a', 'A')] });
  assert.equal(result.status, 'completed');
  assert.ok(harness.sleeps.includes(60_000), 'Retry-After 120 秒被截断到 60 秒');
});

test('执行器：连续 5 个分类失败熔断为 SITE_BLOCKING，并记录失败节点', async () => {
  const nodes = ['/a', '/b', '/c', '/d', '/e', '/f'].map((path) => node(path, path.slice(1).toUpperCase()));
  const pages = Object.fromEntries(nodes.map((item) => [item.url, { status: 403 }]));
  const harness = createRunnerHarness(pages);
  const result = await harness.runner.run({ nodes });
  assert.equal(result.status, 'failed');
  assert.equal(result.errorCode, 'SITE_BLOCKING');
  assert.equal(result.failedNodes.length, 5);
  assert.equal(harness.site.fetches.length, 5, '第 6 个分类不再请求');
});

test('执行器：跳转登录页立即停止为 LOGIN_REQUIRED', async () => {
  const pages = {
    [`${ORIGIN}/a`]: { redirect: `${ORIGIN}/login?returnUrl=%2Fa` },
    [`${ORIGIN}/b`]: page({ items: ['B'] }),
  };
  const harness = createRunnerHarness(pages);
  const result = await harness.runner.run({ nodes: [node('/a', 'A'), node('/b', 'B')] });
  assert.equal(result.status, 'failed');
  assert.equal(result.errorCode, 'LOGIN_REQUIRED');
  assert.equal(harness.site.fetches.length, 1);
});

test('执行器：回传致命错误整体停止；可重试错误耗尽只记该分类失败', async () => {
  const pages = {
    [`${ORIGIN}/a`]: page({ items: ['A'] }),
    [`${ORIGIN}/b`]: page({ items: ['B'] }),
  };
  const fatal = createRunnerHarness(pages, {
    onCapture: () => ({ ok: false, httpStatus: 404, errorCode: 'FEATURE_DISABLED' }),
  });
  const fatalResult = await fatal.runner.run({ nodes: [node('/a', 'A'), node('/b', 'B')] });
  assert.equal(fatalResult.errorCode, 'FEATURE_DISABLED');
  assert.equal(fatal.site.fetches.length, 1);

  const busy = createRunnerHarness(pages, {
    onCapture: (payload) => (payload.itemNumbers[0] === 'A'
      ? { ok: false, httpStatus: 503 }
      : { ok: true }),
  });
  const busyResult = await busy.runner.run({ nodes: [node('/a', 'A'), node('/b', 'B')] });
  assert.equal(busyResult.status, 'completed');
  assert.deepEqual(busyResult.completedKeys, ['/b']);
  assert.deepEqual(busyResult.failedNodes.map((item) => item.key), ['/a']);
});

test('执行器：中止信号立即停止后续请求', async () => {
  const controller = new AbortController();
  const pages = {
    [`${ORIGIN}/a`]: page({ items: ['A'] }),
    [`${ORIGIN}/b`]: page({ items: ['B'] }),
  };
  const harness = createRunnerHarness(pages, {
    signal: controller.signal,
    onCapture: () => {
      controller.abort();
      return { ok: true };
    },
  });
  const result = await harness.runner.run({ nodes: [node('/a', 'A'), node('/b', 'B')] });
  assert.equal(result.status, 'aborted');
  assert.deepEqual(harness.site.fetches, [`${ORIGIN}/a`]);
  assert.equal(harness.progress[harness.progress.length - 1].status, 'aborted');
});

test('执行器：子分类链接只接受当前分类路径下的新节点，并计入总数', async () => {
  const pages = {
    [`${ORIGIN}/a`]: page({
      items: ['A'],
      subcategories: [
        { name: 'Child', url: `${ORIGIN}/a/child` },
        { name: 'Other', url: `${ORIGIN}/other` },
        { name: 'Sale', url: `${ORIGIN}/a/sale` },
        { name: 'Known', url: `${ORIGIN}/a/known` },
      ],
    }),
    [`${ORIGIN}/a/known`]: page({ items: ['K'] }),
    [`${ORIGIN}/a/child`]: page({ items: ['C'] }),
  };
  const harness = createRunnerHarness(pages);
  const result = await harness.runner.run({ nodes: [node('/a', 'A'), node('/a/known', 'Known', '/a')] });
  assert.deepEqual(result.discovered.map((item) => [item.key, item.parentKey, item.depth]), [['/a/child', '/a', 1]]);
  assert.equal(result.total, 3);
  assert.deepEqual(harness.captures[2].categoryPath.map((item) => item.key), ['/a', '/a/child']);
});

test('执行器：进度上报节流为每秒一次，但开始与结束总会上报', async () => {
  const nodes = ['/a', '/b', '/c'].map((path) => node(path, path));
  const pages = Object.fromEntries(nodes.map((item) => [item.url, page({ items: ['X'] })]));
  const harness = createRunnerHarness(pages, { config: { crawlDelayMs: 500 } });
  await harness.runner.run({ nodes });
  assert.equal(harness.progress[0].status, 'running');
  assert.equal(harness.progress[harness.progress.length - 1].status, 'completed');
  for (let index = 1; index < harness.progress.length - 1; index += 1) {
    assert.ok(harness.progress[index].status === 'running');
  }
  assert.ok(harness.progress.length <= 1 + 3 + 1, '节流后上报次数有限');
});
