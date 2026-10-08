import assert from 'node:assert/strict'

import { classifyStatsError } from '../pages/PosAdmin/SeasonalCardStats/logic'

import {
  buildSummaryParams,
  getSeasonalCardStatsStoreDetail,
  getSeasonalCardStatsSummary,
  normalizeBatch,
  normalizePriceQuantities,
  normalizeSeasonalCardStatsStoreDetail,
  normalizeSeasonalCardStatsSummary,
  normalizeStoreRow,
} from './seasonalCardStatsService'

const originalFetch = globalThis.fetch
const calls: { url: string; init?: RequestInit }[] = []
let responseFactory: () => Response

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  calls.push({ url: String(input), init })
  return responseFactory()
}) as typeof fetch

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
}

try {
  // ───────── 汇总：URL、数组参数序列化、signal、归一化 ─────────
  responseFactory = () =>
    json({
      success: true,
      data: {
        seasonYear: 2026,
        cardType: 1,
        cardTypeName: '圣诞节',
        storeCount: 2,
        filledStoreCount: 1,
        unfilledStoreCount: 1,
        totalQuantity: 248,
        totalAmount: 442,
        stores: [
          {
            storeCode: '1013',
            storeName: 'Bankstown',
            isFilled: true,
            prices: [
              { priceOption: 4, priceLabel: '其他', quantity: 12, amount: 54 },
              { priceOption: 1, priceLabel: '$1', quantity: 120, amount: 120 },
            ],
            totalQuantity: 132,
            totalAmount: 174,
            suppliers: [{ localSupplierCode: null, supplierName: '未指定供应商' }],
            lastSubmittedAt: '2026-10-08T03:32:00',
            lastSubmittedByName: 'Wang',
          },
          { storeCode: '1014', storeName: 'Greenhills', isFilled: false, prices: [], suppliers: [] },
        ],
        priceTotals: [{ priceOption: 2, priceLabel: '$2', quantity: 80, amount: 160 }],
        supplierTotals: [{ localSupplierCode: 'SUP-A', supplierName: 'Alpha', storeCount: 1, quantity: 248, amount: 442 }],
        unfilledStores: [{ storeCode: '1014', storeName: 'Greenhills' }],
        excludedStores: [
          { storeCode: '1006', storeName: 'HB Warehouse' },
          { storeCode: '1042', storeName: 'TestStore' },
        ],
      },
    })
  const controller = new AbortController()
  const summary = await getSeasonalCardStatsSummary(
    { seasonYear: 2026, cardType: 1, localSupplierCode: 'SUP-A', priceOption: 4, storeCodes: ['1013', '1014'] },
    controller.signal,
  )
  assert.equal(calls.length, 1)
  const url = new URL(calls[0].url, 'http://localhost')
  assert.equal(url.pathname, '/api/react/v1/seasonal-card-remaining/admin/summary')
  assert.equal(url.searchParams.get('seasonYear'), '2026')
  assert.equal(url.searchParams.get('cardType'), '1')
  assert.equal(url.searchParams.get('localSupplierCode'), 'SUP-A')
  assert.equal(url.searchParams.get('priceOption'), '4')
  // ASP.NET Core 的 List<string> 只认重复键（storeCodes=a&storeCodes=b），不认 storeCodes[]=a 或逗号拼接。
  assert.deepEqual(url.searchParams.getAll('storeCodes'), ['1013', '1014'])
  assert.equal(calls[0].url.includes('storeCodes%5B%5D'), false)
  assert.equal(calls[0].url.includes('storeCodes=1013&storeCodes=1014'), true)
  assert.equal(calls[0].init?.signal, controller.signal, 'signal 透传给 fetch')
  assert.equal(calls[0].init?.method, 'GET')

  assert.equal(summary.stores.length, 2)
  assert.deepEqual(summary.stores[0].prices.map((item) => item.priceOption), [1, 2, 3, 4], '价格补齐 4 档并排序')
  assert.equal(summary.stores[0].prices[1].quantity, 0)
  assert.equal(summary.stores[0].suppliers[0].localSupplierCode, null)
  assert.equal(summary.stores[1].isFilled, false)
  assert.equal(summary.stores[1].totalQuantity, 0, '缺合计时由价格推算')
  assert.equal(summary.stores[1].lastSubmittedAt, null)
  assert.deepEqual(summary.priceTotals.map((item) => item.quantity), [0, 80, 0, 0])
  assert.equal(summary.excludedStores.length, 2)
  assert.equal(summary.unfilledStores[0].storeCode, '1014')

  // 默认条件：不带供应商、价格、分店
  calls.length = 0
  await getSeasonalCardStatsSummary({ seasonYear: 2026, cardType: 3 })
  const plain = new URL(calls[0].url, 'http://localhost')
  assert.deepEqual([...plain.searchParams.keys()].sort(), ['cardType', 'seasonYear'])

  // ───────── 业务错误：HTTP 200 + success=false 必须变成异常并能归类 ─────────
  responseFactory = () => json({ success: false, message: '节日无效', errorCode: 'INVALID_CARD_TYPE', data: null })
  await assert.rejects(
    () => getSeasonalCardStatsSummary({ seasonYear: 2026, cardType: 1 }),
    (error: unknown) => classifyStatsError(error) === 'invalidQuery',
  )

  responseFactory = () => json({ success: false, message: 'Forbidden' }, 403)
  await assert.rejects(
    () => getSeasonalCardStatsSummary({ seasonYear: 2026, cardType: 1 }),
    (error: unknown) => classifyStatsError(error) === 'forbidden',
  )

  // ───────── 单店明细 ─────────
  responseFactory = () =>
    json({
      success: true,
      data: {
        storeCode: '1013',
        storeName: 'Bankstown',
        seasonYear: 2026,
        cardType: 1,
        cardTypeName: '圣诞节',
        isFilled: true,
        totalQuantity: 248,
        totalAmount: 442,
        currentBatches: [
          {
            batchGuid: 'b2',
            storeCode: '1013',
            localSupplierCode: 'SUP-A',
            supplierName: 'Alpha',
            submittedByName: 'Wang',
            submittedAt: '2026-10-08T03:32:00',
            isCurrent: true,
            lines: [
              { priceOption: 4, unitPrice: 4.5, remainingQuantity: 12 },
              { priceOption: 1, unitPrice: 1, remainingQuantity: 120 },
              { priceOption: 9, unitPrice: 1, remainingQuantity: 1 },
            ],
          },
        ],
        history: [
          { batchGuid: 'b1', submittedAt: '2026-10-02T09:10:00', isCurrent: false, lines: [] },
          { batchGuid: 'b2', submittedAt: '2026-10-08T03:32:00', isCurrent: true, lines: [] },
        ],
        previousYearTotalQuantity: 344,
      },
    })
  calls.length = 0
  const detail = await getSeasonalCardStatsStoreDetail('10 13', { seasonYear: 2026, cardType: 1 })
  const detailUrl = new URL(calls[0].url, 'http://localhost')
  assert.equal(detailUrl.pathname, '/api/react/v1/seasonal-card-remaining/admin/stores/10%2013', '分店编码做 URL 编码')
  assert.equal(detailUrl.searchParams.get('seasonYear'), '2026')
  assert.equal(detailUrl.searchParams.get('cardType'), '1')
  assert.equal(detail.currentBatches[0].lines.length, 2, '未知价格类型丢弃')
  assert.deepEqual(detail.currentBatches[0].lines.map((line) => line.priceOption), [1, 4], '按价格类型排序')
  assert.equal(detail.currentBatches[0].totalQuantity, 132, '缺合计时由明细推算')
  assert.equal(detail.currentBatches[0].totalAmount, 120 + 54)
  assert.deepEqual(detail.history.map((item) => item.batchGuid), ['b2', 'b1'], '时间线按提交时间倒序')
  assert.equal(detail.previousYearTotalQuantity, 344)
} finally {
  globalThis.fetch = originalFetch
}

// ───────── 纯归一化兜底 ─────────

assert.deepEqual(normalizePriceQuantities(null).map((item) => [item.priceOption, item.quantity]), [[1, 0], [2, 0], [3, 0], [4, 0]])
const emptyRow = normalizeStoreRow(null)
assert.equal(emptyRow.storeCode, '')
assert.equal(emptyRow.isFilled, false)
assert.equal(emptyRow.prices.length, 4)

const emptySummary = normalizeSeasonalCardStatsSummary(null, { seasonYear: 2026, cardType: 2 })
assert.equal(emptySummary.seasonYear, 2026, '缺年份时回退到请求条件')
assert.equal(emptySummary.cardType, 2)
assert.equal(emptySummary.storeCount, 0)
assert.deepEqual(emptySummary.excludedStores, [])

const derived = normalizeSeasonalCardStatsSummary({
  stores: [
    { storeCode: '1', isFilled: true },
    { storeCode: '2', isFilled: false },
    { storeCode: '3', isFilled: 'yes' as unknown as boolean },
  ],
})
assert.equal(derived.storeCount, 3, '缺分店数时按行数')
assert.equal(derived.filledStoreCount, 1, 'isFilled 只认 true')
assert.equal(derived.unfilledStoreCount, 2)
assert.equal(normalizeSeasonalCardStatsSummary({ cardType: 99 }).cardType, 1, '未知节日回退圣诞节')

const historic = normalizeBatch({ batchGuid: null, localSupplierCode: '', supplierName: null, totalQuantity: '7' as unknown as number })
assert.equal(historic.batchGuid, null)
assert.equal(historic.localSupplierCode, null, '空串供应商按没有处理')
assert.equal(historic.totalQuantity, 7, '数字字符串能解析')

assert.equal(normalizeSeasonalCardStatsStoreDetail(null).previousYearTotalQuantity, null)

assert.deepEqual(
  buildSummaryParams({ seasonYear: 2026, cardType: 1, localSupplierCode: ' ', storeCodes: [' ', '1013', '1013'] }),
  { seasonYear: 2026, cardType: 1, localSupplierCode: undefined, priceOption: undefined, storeCodes: ['1013'] },
)

console.log('seasonalCardStatsService.test: ok')
