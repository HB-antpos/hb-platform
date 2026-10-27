import {
  getMainApiEntryUsage,
  getPosApiEntryUsage,
  normalizeMainApiClientKind,
  normalizeMainApiEntryUsage,
  normalizePosApiEntryUsage,
} from './posApiEntryService'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const left = JSON.stringify(actual)
  const right = JSON.stringify(expected)
  if (left !== right) {
    throw new Error(`${label}。Expected: ${right}, received: ${left}`)
  }
}

const query = { startDate: '2026-10-01', endDate: '2026-10-31', granularity: 'day' as const }

// ───────── 规整：缺什么补什么，永远给出固定四项 ─────────

const empty = normalizePosApiEntryUsage(undefined, query)
assertEqual(empty.startDate, '2026-10-01', '后端没给起止时回落到请求值')
assertEqual(empty.granularity, 'day', '后端没给粒度时回落到请求值')
assertEqual(empty.timeZone, 'Australia/Sydney', '缺时区时按悉尼')
assertDeepEqual(empty.total, { requests: 0, status4xx: 0, status5xx: 0 }, '缺合计按 0')
assertDeepEqual(empty.entries.map((item) => item.entry), ['edge', 'aws', 'direct', 'unknown'], '空响应也输出固定四项')
assertEqual(empty.buckets.length, 0, '缺桶为空数组')
assertEqual(empty.stores.length, 0, '缺分店为空数组')

const normalized = normalizePosApiEntryUsage(
  {
    startDate: '2026-10-19',
    endDate: '2026-10-25',
    granularity: 'week',
    timeZone: 'Australia/Sydney',
    storeCode: 'S001',
    total: { requests: 100, status4xx: 4, status5xx: 2 },
    entries: [
      { entry: 'AWS', requests: 30, status4xx: 1, status5xx: 0, share: 0.3 },
      { entry: 'edge', requests: 60, status4xx: 3, status5xx: 2, share: 0.6 },
      // 未知线路并入 unknown，保证请求总数守恒；后端给的 share 不可信，前端按规整后的数据重算
      { entry: 'tencent', requests: 6, status4xx: 0, status5xx: 0, share: 9 },
      { entry: 'unknown', requests: 4, status4xx: 0, status5xx: 0, share: 0.04 },
      null,
    ],
    buckets: [
      {
        bucketStart: '2026-10-19',
        bucketEnd: '2026-10-25',
        partial: true,
        total: { requests: 10, status4xx: 0, status5xx: 0 },
        entries: [{ entry: 'direct', requests: '10', status4xx: 0, status5xx: 0 }],
      },
      { bucketEnd: '2026-10-26' }, // 缺 bucketStart：整条丢弃
      'garbage',
    ],
    stores: [
      { storeCode: '', storeName: '', total: { requests: 7 }, entries: [] },
      { storeCode: 'S001', storeName: '旗舰店', total: { requests: 3, status4xx: -5 }, entries: [] },
      42,
    ],
  },
  query,
)
assertEqual(normalized.granularity, 'week', '采用后端粒度')
assertEqual(normalized.storeCode, 'S001', '分店筛选回显')
assertEqual(normalized.entries.find((item) => item.entry === 'aws')?.requests, 30, '线路大小写不敏感')
assertEqual(normalized.entries.find((item) => item.entry === 'unknown')?.requests, 10, '未知线路并入 unknown（4 + 6）')
assertEqual(
  normalized.entries.reduce((sum, item) => sum + item.requests, 0),
  100,
  '规整前后请求总数守恒',
)
assertEqual(Math.round((normalized.entries.find((item) => item.entry === 'edge')?.share ?? 0) * 1000), 600, '占比按规整后的数据重算')
assertEqual(normalized.buckets.length, 1, '缺 bucketStart 的桶被丢弃')
assertEqual(normalized.buckets[0].partial, true, '不完整标记')
assertEqual(normalized.buckets[0].entries.find((item) => item.entry === 'direct')?.requests, 10, '字符串数字也能解析')
assertEqual(normalized.buckets[0].entries.find((item) => item.entry === 'direct')?.share, 1, '单线路桶占比 100%')
assertEqual(normalized.buckets[0].entries.length, 4, '桶内同样固定四项')
assertEqual(normalized.stores.length, 2, '非对象的分店被丢弃')
assertEqual(normalized.stores[0].storeCode, '', '空串分店（未归属）保留')
assertEqual(normalized.stores[1].total.status4xx, 0, '负数计数按 0')
assertEqual(normalizePosApiEntryUsage({ granularity: 'year' }, query).granularity, 'day', '未知粒度回落到请求值')

// ───────── 请求：路径与参数 ─────────

const originalFetch = globalThis.fetch
try {
  const calls: string[] = []
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    calls.push(String(input))
    return new Response(
      JSON.stringify({
        success: true,
        data: {
          startDate: '2026-10-01',
          endDate: '2026-10-31',
          granularity: 'month',
          total: { requests: 5, status4xx: 1, status5xx: 0 },
          entries: [{ entry: 'edge', requests: 5, status4xx: 1, status5xx: 0, share: 1 }],
          buckets: [],
          stores: [],
        },
      }),
      { status: 200, headers: { 'Content-Type': 'application/json' } },
    )
  }) as typeof fetch

  const result = await getPosApiEntryUsage({ ...query, granularity: 'month', storeCode: '1042' })
  assertEqual(result.total.requests, 5, '读取合计')
  assertEqual(result.granularity, 'month', '读取粒度')
  const url = calls[0] ?? ''
  assertEqual(url.includes('/api/react/v1/pos-api-entry/usage'), true, '请求路径')
  assertEqual(url.includes('startDate=2026-10-01'), true, '带起始日期')
  assertEqual(url.includes('endDate=2026-10-31'), true, '带结束日期')
  assertEqual(url.includes('granularity=month'), true, '带粒度')
  assertEqual(url.includes('storeCode=1042'), true, '带分店')

  await getPosApiEntryUsage(query)
  assertEqual((calls[1] ?? '').includes('storeCode'), false, '不筛分店时不带 storeCode 参数')
} finally {
  globalThis.fetch = originalFetch
}

// ───────── 主 API 用量：客户端类别固定三项，未知值并入 other，请求总数守恒 ─────────

assertEqual(normalizeMainApiClientKind(' WEB '), 'web', '类别大小写 / 空白不敏感')
assertEqual(normalizeMainApiClientKind('robot'), 'other', '未知类别并入 other')
assertEqual(normalizeMainApiClientKind(undefined), 'other', '缺类别并入 other')

const mainEmpty = normalizeMainApiEntryUsage(undefined, query)
assertDeepEqual(mainEmpty.clientKinds.map((item) => item.clientKind), ['web', 'app', 'other'], '空响应也输出固定三个类别')
assertDeepEqual(mainEmpty.entries.map((item) => item.entry), ['edge', 'aws', 'direct', 'unknown'], '空响应也输出固定四项线路')
assertEqual(mainEmpty.clientKind, '', '缺类别筛选回显为空串')

const mainNormalized = normalizeMainApiEntryUsage(
  {
    startDate: '2026-10-20',
    endDate: '2026-10-21',
    granularity: 'week',
    clientKind: '',
    total: { requests: 110, status4xx: 5, status5xx: 1 },
    entries: [
      { entry: 'direct', requests: 70, status4xx: 3, status5xx: 1, share: 0.7 },
      { entry: 'edge', requests: 40, status4xx: 2, status5xx: 0, share: 0.4 },
    ],
    buckets: [{ bucketStart: '2026-10-19', bucketEnd: '2026-10-25', partial: true, total: { requests: 110 }, entries: [] }],
    clientKinds: [
      {
        clientKind: 'web',
        total: { requests: 70, status4xx: 3, status5xx: 1 },
        share: 9,
        entries: [{ entry: 'direct', requests: 70, status4xx: 3, status5xx: 1 }],
      },
      { clientKind: 'app', total: { requests: 30 }, entries: [{ entry: 'edge', requests: 30 }] },
      // 未知类别并入 other，保证请求总数守恒
      { clientKind: 'tablet', total: { requests: 6 }, entries: [{ entry: 'edge', requests: 6 }] },
      { clientKind: 'other', total: { requests: 4 }, entries: [{ entry: 'unknown', requests: 4 }] },
      null,
    ],
  },
  query,
)
assertEqual(mainNormalized.granularity, 'week', '主 API 采用后端粒度')
assertEqual(
  mainNormalized.clientKinds.reduce((sum, item) => sum + item.total.requests, 0),
  110,
  '类别规整前后请求总数守恒',
)
assertEqual(mainNormalized.clientKinds.find((item) => item.clientKind === 'other')?.total.requests, 10, '未知类别并入 other（6 + 4）')
assertEqual(
  Math.round((mainNormalized.clientKinds.find((item) => item.clientKind === 'web')?.share ?? 0) * 1000),
  636,
  '类别占比按规整后的数据重算',
)
assertEqual(
  mainNormalized.clientKinds.find((item) => item.clientKind === 'app')?.entries.find((item) => item.entry === 'edge')?.share,
  1,
  '类别内线路占比以该类别自身请求数为分母',
)
assertEqual(mainNormalized.buckets[0]?.partial, true, '主 API 桶沿用不完整标记')

const originalFetchMain = globalThis.fetch
try {
  const mainCalls: string[] = []
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    mainCalls.push(String(input))
    return new Response(
      JSON.stringify({ success: true, data: { total: { requests: 9, status4xx: 0, status5xx: 0 }, clientKinds: [] } }),
      { status: 200, headers: { 'Content-Type': 'application/json' } },
    )
  }) as typeof fetch

  const mainResult = await getMainApiEntryUsage({ ...query, granularity: 'week', clientKind: 'app' })
  assertEqual(mainResult.total.requests, 9, '主 API 读取合计')
  const mainUrl = mainCalls[0] ?? ''
  assertEqual(mainUrl.includes('/api/react/v1/main-api-entry/usage'), true, '主 API 请求路径')
  assertEqual(mainUrl.includes('granularity=week'), true, '主 API 带粒度')
  assertEqual(mainUrl.includes('clientKind=app'), true, '主 API 带客户端类别')

  await getMainApiEntryUsage(query)
  assertEqual((mainCalls[1] ?? '').includes('clientKind'), false, '不筛类别时不带 clientKind 参数')
} finally {
  globalThis.fetch = originalFetchMain
}

console.log('posApiEntryUsageService tests passed')
