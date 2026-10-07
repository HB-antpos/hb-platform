import assert from 'node:assert/strict'

import { classifyDailyCloseError } from '../pages/PosAdmin/DailyCloses/logic'
import {
  getDailyCloseDetail,
  getDailyCloses,
  normalizeDailyCloseDetail,
  normalizeDailyCloseItem,
  normalizeDailyCloseList,
} from './dailyCloseService'

const originalFetch = globalThis.fetch
const calls: Array<{ url: string; init?: RequestInit }> = []
let responseFactory: () => Response

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  calls.push({ url: String(input), init })
  return responseFactory()
}) as typeof fetch

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
}

const guid = 'a3f29c01-7b4e-4d2a-9c3e-5f1d8e60b274'

try {
  // ───────── 列表：URL、参数、signal、归一化 ─────────
  responseFactory = () =>
    json({
      success: true,
      data: {
        items: [
          {
            dailyCloseGuid: guid,
            storeCode: '1008',
            storeName: 'Bankstown',
            storeTimeZoneId: 'Australia/Sydney',
            deviceCode: 'POS01',
            clientKind: 'Wpf',
            detailLevel: 'Full',
            dataSource: 'ClientUpload',
            businessDate: '2026-10-06',
            businessDateInferred: false,
            cashierId: '7',
            cashierName: 'Mei Lin',
            savedAtUtc: '2026-10-06T10:48:12Z',
            orderCount: 142,
            expectedCashAmount: 1842.3,
            countedCashAmount: 1838.8,
            cashDifference: -3.5,
            cardNetAmount: 5206.15,
            differenceKind: 'short',
            saveSequence: 1,
            saveCountInDay: 2,
          },
        ],
        total: 58,
        page: 2,
        pageSize: 20,
        counts: { all: 58, short: 9, over: 4, even: 43, none: 2 },
        totals: { expectedCash: 71356.2, countedCash: 71314.7, difference: -41.5 },
      },
    })

  const controller = new AbortController()
  const list = await getDailyCloses(
    {
      businessDateFrom: '2026-10-01',
      businessDateTo: '2026-10-07',
      storeCodes: '1008,1015',
      deviceCode: undefined,
      clientKind: 'Wpf',
      keyword: 'Mei',
      status: 'short',
      page: 2,
      pageSize: 20,
    },
    controller.signal,
  )
  const listCall = calls[0]
  assert.ok(listCall.url.startsWith('/api/react/v1/pos-daily-closes?'), '列表应走 /api/react/v1/pos-daily-closes')
  for (const expected of [
    'businessDateFrom=2026-10-01',
    'businessDateTo=2026-10-07',
    'storeCodes=1008%2C1015',
    'clientKind=Wpf',
    'keyword=Mei',
    'status=short',
    'page=2',
    'pageSize=20',
  ]) {
    assert.ok(listCall.url.includes(expected), `列表参数缺少 ${expected}，实际 ${listCall.url}`)
  }
  assert.ok(!listCall.url.includes('deviceCode'), '未设置的参数不进查询串')
  assert.equal(listCall.init?.method, 'GET')
  assert.equal(listCall.init?.credentials, 'include', '列表请求应携带 cookie')
  assert.equal(listCall.init?.signal, controller.signal, '列表请求应传递 AbortSignal')
  assert.equal(list.total, 58)
  assert.equal(list.page, 2)
  assert.deepEqual(list.counts, { all: 58, short: 9, over: 4, even: 43, none: 2 })
  assert.deepEqual(list.totals, { expectedCash: 71356.2, countedCash: 71314.7, difference: -41.5 })
  assert.equal(list.items[0].dailyCloseGuid, guid)
  assert.equal(list.items[0].cashDifference, -3.5)
  assert.equal(list.items[0].differenceKind, 'short')
  assert.equal(list.items[0].saveCountInDay, 2)

  // ───────── 详情：路径与归一化 ─────────
  responseFactory = () =>
    json({
      success: true,
      data: {
        dailyCloseGuid: guid,
        storeCode: '1008',
        deviceCode: 'POS01',
        clientKind: 'Wpf',
        detailLevel: 'Full',
        dataSource: 'ClientUpload',
        businessDate: '2026-10-06T00:00:00',
        cashierId: '7',
        cashierName: 'Mei Lin',
        savedAtUtc: '2026-10-06T10:48:12Z',
        expectedCashAmount: 1842.3,
        countedCashAmount: 1838.8,
        cashDifference: -3.5,
        periodFromUtc: '2026-10-05T13:00:00Z',
        periodToUtc: '2026-10-06T13:00:00Z',
        appVersion: '1.0.47',
        returnQuantity: 6,
        tenders: [{ method: 'Cash', salesAmount: 1905.8, refundAmount: 63.5, netAmount: 1842.3 }],
        cashCounts: [{ denominationCents: 10000, quantity: 8, subtotalAmount: 800, kind: 'Note' }],
        noteSubtotal: 1815,
        coinSubtotal: 23.8,
        receivedAtUtc: '2026-10-06T10:48:15Z',
      },
    })
  const detail = await getDailyCloseDetail(guid)
  assert.equal(calls[1].url, `/api/react/v1/pos-daily-closes/${guid}`, '详情路径带日结 GUID')
  assert.equal(detail.businessDate, '2026-10-06', '营业日只取日期部分')
  assert.equal(detail.differenceKind, 'short', '后端缺 differenceKind 时按差额符号推算')
  assert.equal(detail.tenders[0].netAmount, 1842.3)
  assert.equal(detail.cashCounts[0].denominationCents, 10000)
  assert.equal(detail.noteSubtotal, 1815)
  assert.equal(detail.appVersion, '1.0.47')
  assert.equal(detail.returnQuantity, 6)
  assert.equal(detail.storeName, null)
  assert.equal(detail.storeTimeZoneId, null)

  // GUID 一律 encode，不能被拼成别的路径
  await getDailyCloseDetail('../x')
  assert.equal(calls[2].url, '/api/react/v1/pos-daily-closes/..%2Fx')

  // ───────── 错误：状态码与错误码要能被分类 ─────────
  responseFactory = () =>
    json({ success: false, message: '营业日期区间最长 93 天，请缩小范围。', errorCode: 'INVALID_QUERY', code: 'INVALID_QUERY' }, 400)
  const invalid = await getDailyCloses({ businessDateFrom: '2026-01-01', businessDateTo: '2026-10-07', page: 1, pageSize: 20 }).catch(
    (error: unknown) => error,
  )
  assert.equal(classifyDailyCloseError(invalid), 'invalidQuery', '400 + INVALID_QUERY 归为区间/查询条件问题')

  responseFactory = () => json({ success: false, message: '日结记录不存在', errorCode: 'NOT_FOUND' }, 404)
  const notFound = await getDailyCloseDetail(guid).catch((error: unknown) => error)
  assert.equal(classifyDailyCloseError(notFound), 'notFound')

  responseFactory = () => json({ message: 'forbidden' }, 403)
  const forbidden = await getDailyCloses({ businessDateFrom: '2026-10-01', businessDateTo: '2026-10-07', page: 1, pageSize: 20 }).catch(
    (error: unknown) => error,
  )
  assert.equal(classifyDailyCloseError(forbidden), 'forbidden')

  // 业务失败但 HTTP 200（success=false）也必须抛错，不能当成空列表
  responseFactory = () => json({ success: false, message: '服务异常', errorCode: 'X' })
  const businessFailure = await getDailyCloses({
    businessDateFrom: '2026-10-01',
    businessDateTo: '2026-10-07',
    page: 1,
    pageSize: 20,
  }).catch((error: unknown) => error)
  assert.ok(businessFailure instanceof Error, 'success=false 必须抛错')
  assert.equal(classifyDailyCloseError(businessFailure), 'failed')

  // ───────── 归一化兜底 ─────────
  const empty = normalizeDailyCloseList(null)
  assert.deepEqual(empty.items, [])
  assert.equal(empty.total, 0)
  assert.deepEqual(empty.counts, { all: 0, short: 0, over: 0, even: 0, none: 0 })
  assert.deepEqual(empty.totals, { expectedCash: 0, countedCash: 0, difference: 0 })
  // counts.all 缺失时按四类求和
  assert.equal(normalizeDailyCloseList({ counts: { short: 1, over: 2, even: 3, none: 4 } as never }).counts.all, 10)

  const bare = normalizeDailyCloseItem({})
  assert.equal(bare.differenceKind, 'none', '没有差额按无金额')
  assert.equal(bare.cashDifference, null)
  assert.equal(bare.saveSequence, 1)
  assert.equal(bare.saveCountInDay, 1)
  assert.equal(bare.storeName, null)
  assert.equal(normalizeDailyCloseItem({ cashDifference: 0 }).differenceKind, 'even')
  assert.equal(normalizeDailyCloseItem({ cashDifference: 2 }).differenceKind, 'over')
  assert.equal(
    normalizeDailyCloseItem({ cashDifference: 2, differenceKind: 'short' }).differenceKind,
    'short',
    '后端给了合法状态就以后端为准',
  )
  assert.equal(normalizeDailyCloseItem({ differenceKind: 'weird' as never }).differenceKind, 'none')
  // 字符串数字也能识别；空字符串/非数字当作缺失而不是 0
  assert.equal(normalizeDailyCloseItem({ orderCount: '12' as never }).orderCount, 12)
  assert.equal(normalizeDailyCloseItem({ orderCount: '' as never }).orderCount, null)
  assert.equal(normalizeDailyCloseItem({ cardNetAmount: 'abc' as never }).cardNetAmount, null)

  const bareDetail = normalizeDailyCloseDetail(undefined)
  assert.deepEqual(bareDetail.tenders, [])
  assert.deepEqual(bareDetail.cashCounts, [])
  assert.equal(bareDetail.periodFromUtc, null)
  assert.equal(bareDetail.noteSubtotal, null)
} finally {
  globalThis.fetch = originalFetch
}

console.log('dailyCloseService.test: ok')
