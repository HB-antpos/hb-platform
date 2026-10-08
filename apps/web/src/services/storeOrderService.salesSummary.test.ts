import { getStoreOrderProductSalesSummary } from './storeOrderService'

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

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedMethod = String(init?.method)
    capturedBody = init?.body ? JSON.parse(String(init.body)) : null
    return new Response(
      JSON.stringify({
        success: true,
        data: [
          { productCode: 'P-1', salesQuantitySinceLastArrival: 8, lastArrivalDate: '2026-09-30T00:00:00', lastArrivalQuantity: 24, lastArrivalOrderQuantity: 0, lastArrivalOrderDate: '2026-09-28T09:00:00', salesStartDate: '2026-09-30T00:00:00' },
          { productCode: 'P-2', salesQuantitySinceLastArrival: 0, lastArrivalDate: '2024-06-10T00:00:00', lastArrivalQuantity: 1.5, lastArrivalOrderQuantity: 0, lastArrivalOrderDate: null, salesStartDate: null },
          { productCode: 'P-3', salesQuantitySinceLastArrival: -2, lastArrivalDate: 20260901, lastArrivalQuantity: '12', lastArrivalOrderQuantity: '3', lastArrivalOrderDate: 20260901, salesStartDate: 20260901 },
          { productCode: 'P-4', salesQuantitySinceLastArrival: null, lastArrivalDate: null, lastArrivalQuantity: null, lastArrivalOrderQuantity: null, lastArrivalOrderDate: null, salesStartDate: null },
        ],
      }),
      { status: 200, headers: { 'Content-Type': 'application/json' } },
    )
  }) as typeof fetch

  const query = { storeCode: 'STORE-1', productCodes: ['P-1', 'P-2', 'P-3', 'P-4'] }
  const result = await getStoreOrderProductSalesSummary(query)

  assertEqual(capturedUrl, '/api/react/v1/store-order/sales-since-last-arrival/summary', 'summary route')
  assertEqual(capturedMethod, 'POST', 'summary method')
  assertDeepEqual(capturedBody, query, 'summary payload')
  assertDeepEqual(
    result,
    [
      { productCode: 'P-1', salesQuantitySinceLastArrival: 8, lastArrivalDate: '2026-09-30T00:00:00', lastArrivalQuantity: 24, lastArrivalOrderQuantity: 0, lastArrivalOrderDate: '2026-09-28T09:00:00', salesStartDate: '2026-09-30T00:00:00' },
      { productCode: 'P-2', salesQuantitySinceLastArrival: 0, lastArrivalDate: '2024-06-10T00:00:00', lastArrivalQuantity: 1.5, lastArrivalOrderQuantity: 0, lastArrivalOrderDate: null, salesStartDate: null },
      { productCode: 'P-3', salesQuantitySinceLastArrival: -2, lastArrivalDate: null, lastArrivalQuantity: null, lastArrivalOrderQuantity: null, lastArrivalOrderDate: null, salesStartDate: null },
      { productCode: 'P-4', salesQuantitySinceLastArrival: null, lastArrivalDate: null, lastArrivalQuantity: null, lastArrivalOrderQuantity: null, lastArrivalOrderDate: null, salesStartDate: null },
    ],
    'summary 应保留正数、0、负数与 null，来货日期/数量类型不对时归一为 null',
  )

  console.log('storeOrderService.salesSummary.test: ok')
} finally {
  globalThis.fetch = originalFetch
}
