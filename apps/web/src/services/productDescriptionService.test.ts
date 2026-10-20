import {
  DESCRIBE_FROM_IMAGES_MAX_ITEMS,
  describeProductsFromImages,
  getDescribedEnglishName,
} from './productDescriptionService'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

const originalFetch = globalThis.fetch
const requestSizes: number[] = []
const requestUrls: string[] = []
let lastBody: { items?: Array<{ key: string; imageUrl?: string; chineseName?: string }> } = {}

try {
  // 模拟后端：超过 20 条返回 400；有图的看图成功，没图的走文字翻译，key 以 fail 开头的判失败。
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requestUrls.push(String(input))
    lastBody = JSON.parse(String(init?.body ?? '{}'))
    const items = lastBody.items ?? []
    requestSizes.push(items.length)
    if (items.length > DESCRIBE_FROM_IMAGES_MAX_ITEMS) {
      return new Response(JSON.stringify({ success: false, message: '单次最多处理 20 个商品' }), {
        status: 400,
        headers: { 'Content-Type': 'application/json' },
      })
    }
    return new Response(JSON.stringify({
      success: true,
      data: {
        results: items.map((item) => item.key.startsWith('fail')
          ? { key: item.key, description: null, source: 'failed', reason: '没有图片也没有中文名' }
          : { key: item.key, description: `Name ${item.key}`, source: item.imageUrl ? 'image' : 'text' }),
      },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }) as typeof fetch

  const items = Array.from({ length: 45 }, (_, index) => ({
    key: `row-${index}`,
    imageUrl: index % 3 === 0 ? undefined : `https://img.example.com/${index}.jpg`,
    chineseName: `商品${index}`,
  }))
  const progress: string[] = []
  const summary = await describeProductsFromImages(items, (done, total) => progress.push(`${done}/${total}`))

  assertEqual(requestSizes.join(','), '20,20,5', '超过 20 条应按 20 条一批串行请求')
  assertEqual(progress.join(','), '20/45,40/45,45/45', '每批完成后都应回调进度')
  assertEqual(Object.keys(summary.results).length, 45, '分批结果应合并为完整结果')
  assertEqual(summary.textCount, 15, '没图的条目计入文字翻译')
  assertEqual(summary.imageCount, 30, '有图的条目计入看图')
  assertEqual(getDescribedEnglishName(summary, 'row-44'), 'Name row-44', '最后一批的结果也应可按 key 取到')
  assertEqual(requestUrls[0].endsWith('/api/react/v1/product-descriptions/from-images'), true, '应调用看图接口')

  requestSizes.length = 0
  const withFailure = await describeProductsFromImages([
    { key: 'fail-1', imageUrl: '  ', chineseName: '  ' },
    { key: 'ok-1', imageUrl: ' https://img.example.com/1.jpg ', chineseName: ' 陶瓷杯 ' },
  ])
  assertEqual(lastBody.items?.[0].imageUrl, undefined, '空白图片地址不应发给后端')
  assertEqual(lastBody.items?.[1].imageUrl, 'https://img.example.com/1.jpg', '图片地址应去掉首尾空白')
  assertEqual(lastBody.items?.[1].chineseName, '陶瓷杯', '中文名应去掉首尾空白')
  assertEqual(withFailure.failedCount, 1, '失败条目应计入失败数')
  assertEqual(getDescribedEnglishName(withFailure, 'fail-1'), undefined, '失败条目取不到英文名')

  requestSizes.length = 0
  assertEqual(Object.keys((await describeProductsFromImages([])).results).length, 0, '空列表返回空结果')
  assertEqual(requestSizes.length, 0, '空列表不应请求后端')
} finally {
  globalThis.fetch = originalFetch
}

console.log('productDescriptionService.test: ok')
