import { lookupStoreOrderProductsByBarcode } from './storeOrderService'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${label}。Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

const originalFetch = globalThis.fetch

try {
  let capturedUrl = ''
  let capturedMethod = ''
  let capturedBody: unknown = null
  let capturedSignal: AbortSignal | null | undefined

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedMethod = String(init?.method)
    capturedBody = init?.body ? JSON.parse(String(init.body)) : null
    capturedSignal = init?.signal
    return new Response(
      JSON.stringify({
        success: true,
        data: {
          barcode: '9300000000001',
          items: [{ productCode: 'P-1', productName: 'Tree' }],
        },
      }),
      { status: 200, headers: { 'Content-Type': 'application/json' } },
    )
  }) as typeof fetch

  const controller = new AbortController()
  const result = await lookupStoreOrderProductsByBarcode('9300000000001', '1024', controller.signal)

  assertEqual(capturedUrl, '/api/react/v1/store-order/products/scan-lookup', 'scan-lookup route')
  assertEqual(capturedMethod, 'POST', 'scan-lookup method')
  // 回归：普通分店账号不带 storeCode 会被后端分店授权拒绝（403），请求体必须带当前分店。
  assertDeepEqual(
    capturedBody,
    { barcode: '9300000000001', storeCode: '1024' },
    'scan-lookup 请求体必须携带当前分店 storeCode',
  )
  assertEqual(capturedSignal, controller.signal, 'scan-lookup 应透传超时取消 signal')
  assertEqual(result.barcode, '9300000000001', 'scan-lookup 结果条码')
  assertEqual(result.items.length, 1, 'scan-lookup 结果商品数')
  assertEqual(result.items[0]?.productCode, 'P-1', 'scan-lookup 结果商品编码')

  console.log('storeOrderService.scanLookup.test: ok')
} finally {
  globalThis.fetch = originalFetch
}
