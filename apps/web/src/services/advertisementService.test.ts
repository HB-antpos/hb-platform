import {
  buildAdvertisementUpsertPayload,
  createAdvertisement,
  getAdvertisementGrid,
  getAdvertisementStoreOptions,
  normalizeAdvertisementMediaSize,
  normalizeAdvertisementOrientation,
  resolveAdvertisementMediaType,
  stripAdvertisementMediaUrlQuery,
} from './advertisementService'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)

  if (actualJson !== expectedJson) {
    throw new Error(`${message}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

async function assertRejectsWithMessage(
  action: () => Promise<unknown>,
  expectedParts: string[],
  message: string,
) {
  try {
    await action()
  } catch (error) {
    if (!(error instanceof Error)) {
      throw new Error(`${message}. Expected Error instance, received: ${String(error)}`)
    }

    for (const part of expectedParts) {
      if (!error.message.includes(part)) {
        throw new Error(`${message}. Expected error message to include: ${part}, received: ${error.message}`)
      }
    }
    return
  }

  throw new Error(`${message}. Expected promise to reject`)
}

assertEqual(
  stripAdvertisementMediaUrlQuery(
    'https://cdn.example.com/ads/banner.png?signature=abc123&expires=123',
  ),
  'https://cdn.example.com/ads/banner.png',
  'Signed advertisement media URLs should drop query parameters before persistence',
)

assertEqual(
  resolveAdvertisementMediaType({ type: 'video/mp4', name: 'demo.mp4' }),
  'Video',
  'Video uploads should resolve to Video media type',
)

assertEqual(
  resolveAdvertisementMediaType({ type: 'image/png', name: 'banner.png' }),
  'Image',
  'Image uploads should resolve to Image media type',
)

assertDeepEqual(
  buildAdvertisementUpsertPayload({
    title: '  首页横幅  ',
    description: '  新店开业  ',
    mediaType: 'Image',
    mediaUrl: 'https://cdn.example.com/ads/banner.png?signature=abc',
    thumbnailUrl: ' https://cdn.example.com/ads/thumb.png ',
    objectKey: ' ads/banner.png ',
    originalFileName: ' banner.png ',
    contentType: ' image/png ',
    fileSize: 2048,
    orientation: 'Portrait',
    mediaWidth: 772,
    mediaHeight: 870,
    effectiveStart: { toISOString: () => '2026-05-27T10:00:00.000Z' },
    effectiveEnd: '2026-06-01T10:00:00.000Z',
    isEnabled: false,
    sortOrder: 12,
    stores: ['S001', { storeCode: 'S002' }],
  }),
  {
    title: '首页横幅',
    description: '新店开业',
    mediaType: 'Image',
    mediaUrl: 'https://cdn.example.com/ads/banner.png',
    thumbnailUrl: 'https://cdn.example.com/ads/thumb.png',
    objectKey: 'ads/banner.png',
    originalFileName: 'banner.png',
    contentType: 'image/png',
    fileSize: 2048,
    orientation: 'Portrait',
    mediaWidth: 772,
    mediaHeight: 870,
    effectiveStart: '2026-05-27T10:00:00.000Z',
    effectiveEnd: '2026-06-01T10:00:00.000Z',
    isEnabled: false,
    sortOrder: 12,
    stores: [{ storeCode: 'S001' }, { storeCode: 'S002' }],
  },
  'Advertisement payload helper should normalize trimmed fields and store scope shape',
)

// 版式兜底：旧数据 / 缺省 / 未知值一律按 Any
assertEqual(normalizeAdvertisementOrientation('Landscape'), 'Landscape', 'Landscape should be kept')
assertEqual(normalizeAdvertisementOrientation('portrait'), 'Portrait', 'Orientation should be case-insensitive')
assertEqual(normalizeAdvertisementOrientation(undefined), 'Any', 'Missing orientation should fall back to Any')
assertEqual(normalizeAdvertisementOrientation('Square'), 'Any', 'Unknown orientation should fall back to Any')
assertEqual(normalizeAdvertisementOrientation(2), 'Any', 'Non-string orientation should fall back to Any')

// 素材宽高：要么都有、要么都为 null
assertDeepEqual(
  normalizeAdvertisementMediaSize(1366, 768),
  { mediaWidth: 1366, mediaHeight: 768 },
  'Valid media size should be kept',
)
assertDeepEqual(
  normalizeAdvertisementMediaSize(1366, null),
  { mediaWidth: null, mediaHeight: null },
  'Partial media size should be cleared together',
)
assertDeepEqual(
  normalizeAdvertisementMediaSize(0, 768),
  { mediaWidth: null, mediaHeight: null },
  'Non-positive media size should be cleared together',
)

assertDeepEqual(
  (({ orientation, mediaWidth, mediaHeight }) => ({ orientation, mediaWidth, mediaHeight }))(
    buildAdvertisementUpsertPayload({
      title: '旧广告',
      mediaType: 'Video',
      mediaUrl: 'https://cdn.example.com/ads/legacy.mp4',
      effectiveStart: '2026-05-27T10:00:00.000Z',
      effectiveEnd: '2026-06-01T10:00:00.000Z',
    }),
  ),
  { orientation: 'Any', mediaWidth: null, mediaHeight: null },
  'Payload without orientation or size should submit Any with null size',
)

const originalFetch = globalThis.fetch
const createFailureCalls: Array<{ url: string; method?: string; body?: unknown }> = []

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  createFailureCalls.push({
    url: String(input),
    method: init?.method,
    body: init?.body ? JSON.parse(String(init.body)) : undefined,
  })

  return new Response(JSON.stringify({
    success: false,
    code: 'ADVERTISEMENT_STORE_SCOPE_INVALID',
    message: '分店不存在或未启用: 1042',
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

try {
  await assertRejectsWithMessage(
    () => createAdvertisement({
      title: '首页横幅',
      mediaType: 'Image',
      mediaUrl: 'https://cdn.example.com/ads/banner.png',
      objectKey: 'ads/banner.png',
      originalFileName: 'banner.png',
      contentType: 'image/png',
      fileSize: 2048,
      orientation: 'Landscape',
      mediaWidth: 1366,
      mediaHeight: 768,
      effectiveStart: '2026-05-27T10:00:00.000Z',
      effectiveEnd: '2026-06-01T10:00:00.000Z',
      isEnabled: true,
      sortOrder: 1,
      stores: [{ storeCode: '1042' }],
    }),
    ['ADVERTISEMENT_STORE_SCOPE_INVALID', '分店不存在或未启用: 1042'],
    'Create advertisement should throw backend code and message when ApiResponse.success is false',
  )
  assertEqual(
    createFailureCalls[0]?.url,
    '/api/react/v1/advertisements',
    'Create advertisement failure request should use the advertisements API contract',
  )
} finally {
  globalThis.fetch = originalFetch
}

// 列表返回：旧数据没有 orientation / 宽高时，service 层兜底为 Any + null 尺寸
globalThis.fetch = (async () => new Response(JSON.stringify({
  success: true,
  data: {
    total: 2,
    items: [
      { id: 'legacy', title: '旧广告', mediaType: 'Image', mediaUrl: 'https://cdn.example.com/a.png' },
      {
        id: 'portrait',
        title: '竖版',
        mediaType: 'Video',
        mediaUrl: 'https://cdn.example.com/b.mp4',
        orientation: 'Portrait',
        mediaWidth: 772,
        mediaHeight: 870,
      },
    ],
  },
}), { status: 200, headers: { 'Content-Type': 'application/json' } })) as typeof fetch

try {
  const grid = await getAdvertisementGrid({ orientation: 'Portrait' })
  assertDeepEqual(
    grid.items.map((item) => [item.id, item.orientation, item.mediaWidth, item.mediaHeight]),
    [['legacy', 'Any', null, null], ['portrait', 'Portrait', 772, 870]],
    'Grid items should normalize missing orientation to Any and keep valid size',
  )
} finally {
  globalThis.fetch = originalFetch
}

// 分店选项：走广告自己的 store-options 接口（不依赖 Stores.View），并映射品牌、按名称排序、丢弃无编码项
let requestedUrl = ''
globalThis.fetch = (async (input: RequestInfo | URL) => {
  requestedUrl = String(input)
  return new Response(JSON.stringify({
    success: true,
    data: [
      { storeCode: '1003', storeName: 'Peninsula Fair', brandName: ' Hot Bargain ' },
      { storeCode: '1002', storeName: 'Robinson Road', brandName: null },
      { storeCode: '', storeName: 'No code' },
    ],
  }), { status: 200, headers: { 'Content-Type': 'application/json' } })
}) as typeof fetch

try {
  const options = await getAdvertisementStoreOptions()
  assertDeepEqual(
    options.map((item) => [item.value, item.label, item.brandName]),
    [['1003', 'Peninsula Fair', 'Hot Bargain'], ['1002', 'Robinson Road', undefined]],
    'Store options should map brand, drop codeless rows and sort by name',
  )
  if (!requestedUrl.includes('/api/react/v1/advertisements/store-options')) {
    throw new Error(`Store options must use the advertisement endpoint, got ${requestedUrl}`)
  }
} finally {
  globalThis.fetch = originalFetch
}

console.log('advertisementService.test: ok')
