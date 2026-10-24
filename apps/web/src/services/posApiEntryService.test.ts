import {
  getPosApiEntryStores,
  normalizePosApiEntryMode,
  normalizePosApiEntryStores,
  updatePosApiEntryStores,
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

assertEqual(normalizePosApiEntryMode('AWS'), 'aws', '大小写不敏感')
assertEqual(normalizePosApiEntryMode(' direct '), 'direct', '忽略空白')
assertEqual(normalizePosApiEntryMode('cdn'), null, '未知线路按 null')
assertEqual(normalizePosApiEntryMode(undefined), null, '缺失按 null')

const normalized = normalizePosApiEntryStores([
  {
    storeGuid: 'g-1', storeCode: '1042', storeName: '测试店', mode: 'aws', effectiveMode: 'aws',
    updatedAt: '2026-10-24T01:00:00', updatedBy: 'admin',
    devices: { total: 4, online: 3, byEntry: { edge: 1, aws: 2, direct: 0, unknown: 1 }, fallbackCount: 1, unsupportedCount: 0 },
  },
  { storeGuid: 'g-2', storeCode: 'B', storeName: 'B 店', mode: null },
  { storeCode: 'no-guid' },
  null,
])
assertEqual(normalized.length, 2, '没有 storeGuid 的行被丢弃')
assertEqual(normalized[0]?.devices.byEntry.aws, 2, '入口分布')
assertEqual(normalized[1]?.effectiveMode, 'edge', '缺 effectiveMode 且 mode 为 null 时按 edge')
assertEqual(normalized[1]?.devices.total, 0, '缺 devices 时全部按 0')
assertEqual(normalized[1]?.updatedAt, null, '缺更新时间按 null')
assertDeepEqual(normalizePosApiEntryStores({ not: 'array' }), [], '非数组按空列表')

const originalFetch = globalThis.fetch
try {
  const calls: Array<{ url: string; method: string; body: unknown }> = []
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), method: String(init?.method), body: init?.body ? JSON.parse(String(init.body)) : null })
    const isPut = init?.method === 'PUT'
    return new Response(
      JSON.stringify({
        success: true,
        data: isPut
          ? { updated: 2, skipped: 1 }
          : [{ storeGuid: 'g-1', storeCode: '1042', storeName: '测试店', mode: 'direct', effectiveMode: 'direct' }],
      }),
      { status: 200, headers: { 'Content-Type': 'application/json' } },
    )
  }) as typeof fetch

  const stores = await getPosApiEntryStores()
  assertEqual(stores[0]?.mode, 'direct', '读取分店设置')
  assertEqual(calls[0]?.method, 'GET', '读取用 GET')
  assertEqual(calls[0]?.url.includes('/api/react/v1/pos-api-entry/stores'), true, '读取路径')

  const result = await updatePosApiEntryStores(['g-1', 'g-2'], 'aws')
  assertDeepEqual(result, { updated: 2, skipped: 1 }, '更新返回值')
  assertEqual(calls[1]?.method, 'PUT', '更新用 PUT')
  assertEqual(calls[1]?.url.includes('/api/react/v1/pos-api-entry/stores'), true, '更新路径')
  assertDeepEqual(calls[1]?.body, { storeGuids: ['g-1', 'g-2'], mode: 'aws' }, '更新请求体')
} finally {
  globalThis.fetch = originalFetch
}

console.log('posApiEntryService tests passed')
