import test from 'node:test';
import assert from 'node:assert/strict';
import {
  BUILTIN_CATEGORY_EXCLUDE_PATTERNS,
  DEFAULT_PROMOTIONAL_PATTERNS,
} from '../src/lib/category-path.js';
import {
  CAPTURE_CHUNK_SIZE,
  CAPTURE_DEDUPE_TTL_MS,
  buildCaptureDedupeKey,
  buildCapturePayload,
  classifyCaptureFailure,
  createCaptureDedupeStore,
  createCaptureScheduler,
  createPassiveCaptureController,
  createRetryPolicy,
  createSlidingWindowLimiter,
  diffNewItems,
  hashItemNumbers,
  normalizeCaptureResponse,
  normalizeTreeSnapshotResponse,
  parseRetryAfter,
  runWithRetry,
  splitIntoChunks,
  truncatePageUrl,
  validateCapturePayload,
  validateTreeSnapshotPayload,
} from '../src/lib/category-capture.js';

const ORIGIN = 'https://www.dats.com.au';
const CONFIG = {
  enabled: true,
  passiveEnabled: true,
  keySource: 'pathname',
  keyQueryParams: [],
  breadcrumbSkip: 1,
  categoryPagePatterns: ['https://www.dats.com.au/*'],
  categoryExcludePatterns: [...BUILTIN_CATEGORY_EXCLUDE_PATTERNS],
  promotionalPatterns: [...DEFAULT_PROMOTIONAL_PATTERNS],
};

// 可控时钟与定时器：测试中手动推进时间。
function createFakeClock(start = 1_000_000) {
  let current = start;
  let nextId = 1;
  const timers = new Map();
  return {
    now: () => current,
    setTimer(fn, ms) {
      const id = nextId;
      nextId += 1;
      timers.set(id, { fn, at: current + ms });
      return id;
    },
    clearTimer(id) {
      timers.delete(id);
    },
    advance(ms) {
      current += ms;
      for (const [id, timer] of [...timers]) {
        if (timer.at <= current) {
          timers.delete(id);
          timer.fn();
        }
      }
    },
  };
}

test('货号哈希与顺序无关，去重键包含供应商、路径与货号集合', () => {
  assert.equal(hashItemNumbers(['b', 'a']), hashItemNumbers(['A', 'B', 'a']));
  assert.notEqual(hashItemNumbers(['a']), hashItemNumbers(['b']));
  const path = [{ key: '/office' }, { key: '/office/pens' }];
  const key = buildCaptureDedupeKey({ supplierCode: '240', categoryPath: path, itemNumbers: ['X', 'Y'] });
  assert.equal(key, buildCaptureDedupeKey({ supplierCode: '240', categoryPath: path, itemNumbers: ['y', 'x'] }));
  assert.notEqual(key, buildCaptureDedupeKey({ supplierCode: '241', categoryPath: path, itemNumbers: ['X', 'Y'] }));
  assert.notEqual(key, buildCaptureDedupeKey({ supplierCode: '240', categoryPath: [{ key: '/office' }], itemNumbers: ['X', 'Y'] }));
});

test('去重存储：6 小时 TTL、最多 500 条、串行读写', async () => {
  const clock = createFakeClock();
  let stored = [];
  const store = createCaptureDedupeStore({
    read: async () => stored,
    write: async (entries) => {
      stored = entries;
    },
    now: clock.now,
    maxEntries: 3,
  });
  await Promise.all(['a', 'b', 'c', 'd'].map((key) => store.add(key)));
  assert.deepEqual(stored.map(([key]) => key), ['b', 'c', 'd'], '超出上限淘汰最旧记录');
  assert.equal(await store.has('a'), false);
  assert.equal(await store.has('d'), true);
  clock.advance(CAPTURE_DEDUPE_TTL_MS + 1);
  assert.equal(await store.has('d'), false, '超过 TTL 视为未发送');
});

test('去重存储读取失败或数据损坏时按空处理', async () => {
  const store = createCaptureDedupeStore({
    read: async () => {
      throw new Error('storage unavailable');
    },
    write: async () => undefined,
  });
  assert.equal(await store.has('x'), false);
  const corrupt = createCaptureDedupeStore({ read: async () => ({ bad: true }), write: async () => undefined });
  assert.equal(await corrupt.has('x'), false);
});

test('稳定检测：同一签名至少两次且静默 1.5 秒才触发一次，签名变化重新计时', () => {
  const clock = createFakeClock();
  const fired = [];
  const scheduler = createCaptureScheduler({
    quietMs: 1500,
    now: clock.now,
    setTimer: clock.setTimer,
    clearTimer: clock.clearTimer,
    onStable: (payload) => fired.push(payload),
  });
  scheduler.notify('s1', 1);
  clock.advance(1600);
  assert.deepEqual(fired, [], '只出现一次的签名不触发');
  scheduler.notify('s1', 2);
  assert.deepEqual(fired, [2], '第二次相同签名且已静默满 1.5 秒立即触发');
  scheduler.notify('s1', 3);
  clock.advance(5000);
  assert.deepEqual(fired, [2], '同一签名只触发一次');

  scheduler.notify('s2', 4);
  clock.advance(500);
  scheduler.notify('s2', 5);
  clock.advance(999);
  assert.deepEqual(fired, [2]);
  clock.advance(1);
  assert.deepEqual(fired, [2, 5], '计时到期且已确认两次时触发');

  scheduler.notify('s3', 6);
  scheduler.notify('s4', 7);
  scheduler.reset();
  clock.advance(5000);
  assert.deepEqual(fired, [2, 5], 'reset 后不再触发');
});

test('增量货号与 100 分块', () => {
  assert.deepEqual(diffNewItems(['A', 'B', 'A', 'C'], new Set(['B'])), ['A', 'C']);
  const items = Array.from({ length: 250 }, (_, index) => `I${index}`);
  const chunks = splitIntoChunks(items, CAPTURE_CHUNK_SIZE);
  assert.deepEqual(chunks.map((chunk) => chunk.length), [100, 100, 50]);
  assert.equal(CAPTURE_CHUNK_SIZE, 100);
  assert.deepEqual(splitIntoChunks([], 100), []);
});

test('回传载荷构建：字段形状与后端 DTO 一致，pageUrl 超长时截断', () => {
  const payload = buildCapturePayload({
    supplierCode: '240',
    pageUrl: `${ORIGIN}/pens?PageProduct=2`,
    categoryPath: [{ name: 'Pens', key: '/pens', url: `${ORIGIN}/pens`, extra: 1 }],
    itemNumbers: ['a1', 'A1', ' b2 '],
    capturedAt: Date.UTC(2026, 8, 23, 1, 2, 3),
    mode: 'passive',
    pageNumber: 2,
  });
  assert.deepEqual(payload, {
    supplierCode: '240',
    pageUrl: `${ORIGIN}/pens?PageProduct=2`,
    categoryPath: [{ name: 'Pens', key: '/pens', url: `${ORIGIN}/pens` }],
    itemNumbers: ['A1', 'B2'],
    capturedAt: '2026-09-23T01:02:03.000Z',
    mode: 'passive',
    pageNumber: 2,
  });
  assert.equal('pageNumber' in buildCapturePayload({ ...payload, pageNumber: 0 }), false);
  const long = `${ORIGIN}/pens?${'q=1&'.repeat(400)}`;
  assert.equal(truncatePageUrl(long), `${ORIGIN}/pens`);
});

test('SW 载荷校验：页面与路径节点必须与来源同源，形状非法拒绝', () => {
  const valid = {
    supplierCode: '240',
    pageUrl: `${ORIGIN}/pens`,
    categoryPath: [{ name: 'Pens', key: '/pens', url: `${ORIGIN}/pens` }],
    itemNumbers: ['A1'],
    capturedAt: '2026-09-23T00:00:00.000Z',
    mode: 'crawl',
  };
  const ok = validateCapturePayload(valid, { senderOrigin: ORIGIN, expectedSupplierCode: '240' });
  assert.equal(ok.ok, true);
  assert.deepEqual(ok.payload.categoryPath, valid.categoryPath);

  const cases = [
    { ...valid, supplierCode: '241' },
    { ...valid, mode: 'push' },
    { ...valid, pageUrl: 'https://evil.example/pens' },
    { ...valid, categoryPath: [] },
    { ...valid, categoryPath: Array.from({ length: 9 }, (_, index) => ({ name: `C${index}`, key: `/c${index}` })) },
    { ...valid, categoryPath: [{ name: 'Pens', key: 'pens' }] },
    { ...valid, categoryPath: [{ name: '', key: '/pens' }] },
    { ...valid, categoryPath: [{ name: 'Pens', key: '/pens', url: 'https://evil.example/pens' }] },
    { ...valid, itemNumbers: [] },
    { ...valid, itemNumbers: Array.from({ length: 101 }, (_, index) => `I${index}`) },
    { ...valid, itemNumbers: 'A1' },
    null,
  ];
  for (const payload of cases) {
    const result = validateCapturePayload(payload, { senderOrigin: ORIGIN, expectedSupplierCode: '240' });
    assert.equal(result.ok, false, JSON.stringify(payload)?.slice(0, 120));
    assert.equal(result.errorCode, 'INVALID_CAPTURE');
  }
});

test('SW 导航树快照校验：节点去重、父级 key 合法、URL 同源', () => {
  const payload = {
    supplierCode: '240',
    sourceUrl: `${ORIGIN}/`,
    nodes: [
      { key: '/a', name: 'A', parentKey: null, url: `${ORIGIN}/a`, sortOrder: 0 },
      { key: '/a/b', name: 'B', parentKey: '/a', url: null, sortOrder: 1 },
    ],
  };
  const ok = validateTreeSnapshotPayload(payload, { senderOrigin: ORIGIN, expectedSupplierCode: '240' });
  assert.equal(ok.ok, true);
  assert.equal(ok.payload.nodes.length, 2);
  for (const nodes of [
    [],
    [{ key: '/a', name: 'A' }, { key: '/a', name: 'A2' }],
    [{ key: '/a', name: 'A', parentKey: '/a' }],
    [{ key: '/a', name: 'A', url: 'https://evil.example/a' }],
  ]) {
    assert.equal(
      validateTreeSnapshotPayload({ ...payload, nodes }, { senderOrigin: ORIGIN, expectedSupplierCode: '240' }).ok,
      false,
    );
  }
  assert.equal(
    validateTreeSnapshotPayload({ ...payload, sourceUrl: 'https://evil.example/' }, { senderOrigin: ORIGIN }).ok,
    false,
  );
});

test('响应归一化容忍缺失字段并限制样本数量', () => {
  const normalized = normalizeCaptureResponse({
    categoryGuid: 'g',
    matchedProducts: '3',
    unmatchedSamples: Array.from({ length: 20 }, (_, index) => `U${index}`),
  });
  assert.equal(normalized.categoryGuid, 'g');
  assert.equal(normalized.matchedProducts, 3);
  assert.equal(normalized.assignedProducts, 0);
  assert.equal(normalized.unmatchedSamples.length, 10);
  assert.deepEqual(normalizeTreeSnapshotResponse(null), {
    created: 0,
    updated: 0,
    unchanged: 0,
    orphanCount: 0,
    promotionalCount: 0,
  });
});

test('失败分类：429/409/5xx/网络可重试；401/403/404/停用为致命；400 仅跳过', () => {
  assert.deepEqual(classifyCaptureFailure({ httpStatus: 429 }), { retryable: true, fatal: false, code: 'HTTP_429' });
  assert.equal(classifyCaptureFailure({ httpStatus: 409, errorCode: 'SUPPLIER_CATEGORY_BUSY' }).retryable, true);
  assert.equal(classifyCaptureFailure({ httpStatus: 503 }).retryable, true);
  assert.equal(classifyCaptureFailure({ networkError: true }).retryable, true);
  assert.equal(classifyCaptureFailure({ httpStatus: 404, errorCode: 'FEATURE_DISABLED' }).fatal, true);
  assert.equal(classifyCaptureFailure({ httpStatus: 404, errorCode: 'NOT_FOUND' }).fatal, true);
  assert.equal(classifyCaptureFailure({ httpStatus: 403 }).fatal, true);
  assert.equal(classifyCaptureFailure({ httpStatus: 401, errorCode: 'WEBSITE_SESSION_REQUIRED' }).fatal, true);
  const badRequest = classifyCaptureFailure({ httpStatus: 400, errorCode: 'INVALID_REQUEST' });
  assert.equal(badRequest.retryable, false);
  assert.equal(badRequest.fatal, false);
  assert.equal(classifyCaptureFailure({ httpStatus: 400, errorCode: 'SUPPLIER_NOT_CAPTURABLE' }).fatal, true);
});

test('重试策略：3 次指数退避，Retry-After 优先且不超过 60 秒', () => {
  const policy = createRetryPolicy({ maxAttempts: 3, baseDelayMs: 1000 });
  assert.equal(policy.shouldRetry(1, { retryable: true }), true);
  assert.equal(policy.shouldRetry(3, { retryable: true }), false);
  assert.equal(policy.shouldRetry(1, { retryable: false }), false);
  assert.equal(policy.delayFor(1), 1000);
  assert.equal(policy.delayFor(2), 2000);
  assert.equal(policy.delayFor(1, 5000), 5000);
  assert.equal(policy.delayFor(1, 120_000), 60_000);
  assert.equal(parseRetryAfter('7'), 7000);
  assert.equal(parseRetryAfter('9999'), 60_000);
  assert.equal(parseRetryAfter('Wed, 23 Sep 2026 00:00:10 GMT', () => Date.parse('2026-09-23T00:00:00Z')), 10_000);
  assert.equal(parseRetryAfter('soon'), null);
  assert.equal(parseRetryAfter(null), null);
});

test('runWithRetry 按分类重试并在耗尽后返回分类结果', async () => {
  const waits = [];
  const sleep = async (ms) => {
    waits.push(ms);
  };
  let calls = 0;
  const success = await runWithRetry(async () => {
    calls += 1;
    return calls < 3 ? { ok: false, httpStatus: 409, errorCode: 'SUPPLIER_CATEGORY_BUSY' } : { ok: true };
  }, { sleep });
  assert.equal(success.ok, true);
  assert.equal(success.attempts, 3);
  assert.deepEqual(waits, [1000, 2000]);

  const exhausted = await runWithRetry(async () => ({ ok: false, httpStatus: 503, retryAfterMs: 3000 }), { sleep });
  assert.equal(exhausted.ok, false);
  assert.equal(exhausted.attempts, 3);
  assert.equal(exhausted.classification.retryable, true);

  let fatalCalls = 0;
  const fatal = await runWithRetry(async () => {
    fatalCalls += 1;
    return { ok: false, httpStatus: 404, errorCode: 'FEATURE_DISABLED' };
  }, { sleep });
  assert.equal(fatalCalls, 1, '致命错误不重试');
  assert.equal(fatal.classification.fatal, true);

  const thrown = await runWithRetry(async () => {
    throw new Error('port closed');
  }, { sleep, policy: createRetryPolicy({ maxAttempts: 1 }) });
  assert.equal(thrown.ok, false);
  assert.equal(thrown.classification.code, 'NETWORK_ERROR');
});

test('本地滑动窗口限流', () => {
  const clock = createFakeClock(0);
  const limiter = createSlidingWindowLimiter({ limit: 2, windowMs: 1000, now: clock.now });
  assert.equal(limiter.tryAcquire().ok, true);
  assert.equal(limiter.tryAcquire().ok, true);
  const blocked = limiter.tryAcquire();
  assert.equal(blocked.ok, false);
  assert.equal(blocked.retryAfterMs, 1000);
  clock.advance(1000);
  assert.equal(limiter.tryAcquire().ok, true);
});

function createPassiveHarness({ responses = [], config = CONFIG, context } = {}) {
  const clock = createFakeClock();
  const sent = [];
  let href = `${ORIGIN}/office-stationery`;
  const controller = createPassiveCaptureController({
    supplierCode: '240',
    getConfig: () => config,
    readPageContext: () => context || {
      breadcrumbItems: [
        { name: 'Home', url: `${ORIGIN}/` },
        { name: 'Office Stationery', url: `${ORIGIN}/office-stationery` },
      ],
      title: 'Office Stationery',
    },
    getCurrentHref: () => href,
    sendCapture: async (payload) => {
      sent.push(payload);
      return responses.length > 0 ? responses.shift() : { ok: true, data: {} };
    },
    now: clock.now,
    setTimer: clock.setTimer,
    clearTimer: clock.clearTimer,
    sleep: async () => undefined,
  });
  return {
    clock,
    sent,
    controller,
    setHref(value) {
      href = value;
    },
    get href() {
      return href;
    },
    async settle(itemNumbers) {
      controller.notify({ href, itemNumbers });
      clock.advance(1500);
      controller.notify({ href, itemNumbers });
      await controller.whenIdle();
    },
  };
}

test('被动采集：页面稳定后回传一次，翻页/滚动只发新增货号', async () => {
  const harness = createPassiveHarness();
  const firstPage = Array.from({ length: 150 }, (_, index) => `P${index}`);
  await harness.settle(firstPage);
  assert.equal(harness.sent.length, 2, '150 个货号拆成 100 + 50 两块');
  assert.deepEqual(harness.sent.map((payload) => payload.itemNumbers.length), [100, 50]);
  assert.equal(harness.sent[0].mode, 'passive');
  assert.deepEqual(harness.sent[0].categoryPath, [
    { name: 'Office Stationery', key: '/office-stationery', url: `${ORIGIN}/office-stationery` },
  ]);

  await harness.settle(firstPage);
  assert.equal(harness.sent.length, 2, '同一批货号不重复回传');

  await harness.settle([...firstPage, 'NEW1', 'NEW2']);
  assert.equal(harness.sent.length, 3);
  assert.deepEqual(harness.sent[2].itemNumbers, ['NEW1', 'NEW2']);
});

test('被动采集：促销分类、非分类页、无路径时不回传', async () => {
  const promo = createPassiveHarness({
    context: {
      breadcrumbItems: [{ name: 'Home' }, { name: 'Clearance', url: `${ORIGIN}/clearance` }],
      title: 'Clearance',
    },
  });
  promo.setHref(`${ORIGIN}/clearance`);
  await promo.settle(['A']);
  assert.equal(promo.sent.length, 0);

  const search = createPassiveHarness();
  search.setHref(`${ORIGIN}/search?q=pen`);
  assert.equal(search.controller.notify({ href: search.href, itemNumbers: ['A'] }), 'not-category');

  const home = createPassiveHarness({ context: { breadcrumbItems: [], title: '' } });
  home.setHref(`${ORIGIN}/`);
  await home.settle(['A']);
  assert.equal(home.sent.length, 0);
});

test('被动采集：致命错误熔断，可重试错误退避后成功', async () => {
  const fatal = createPassiveHarness({
    responses: [{ ok: false, httpStatus: 404, errorCode: 'FEATURE_DISABLED' }],
  });
  await fatal.settle(['A']);
  assert.equal(fatal.sent.length, 1);
  assert.equal(fatal.controller.isDisabled(), true);
  assert.equal(fatal.controller.notify({ href: fatal.href, itemNumbers: ['B'] }), 'disabled');

  const busy = createPassiveHarness({
    responses: [{ ok: false, httpStatus: 409, errorCode: 'SUPPLIER_CATEGORY_BUSY' }, { ok: true }],
  });
  await busy.settle(['A']);
  assert.equal(busy.sent.length, 2, '409 退避后重试成功');
  await busy.settle(['A']);
  assert.equal(busy.sent.length, 2, '成功后记入已发送');
});

test('被动采集：页面已导航离开时丢弃旧快照；停用配置不调度', async () => {
  const harness = createPassiveHarness();
  harness.controller.notify({ href: harness.href, itemNumbers: ['A'] });
  harness.clock.advance(1500);
  const staleHref = harness.href;
  harness.setHref(`${ORIGIN}/pens`);
  harness.controller.notify({ href: staleHref, itemNumbers: ['A'] });
  await harness.controller.whenIdle();
  assert.equal(harness.sent.length, 0);

  const disabled = createPassiveHarness({ config: { ...CONFIG, passiveEnabled: false } });
  assert.equal(disabled.controller.notify({ href: disabled.href, itemNumbers: ['A'] }), 'disabled');
});
