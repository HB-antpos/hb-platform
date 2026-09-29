import assert from 'node:assert/strict'
import { batchProductSalesApi } from './batchProductSalesAnalysisService'
import { RequestError } from '../utils/request'

function jsonResponse(payload: unknown, status = 200): Response {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

async function assertRejects(
  execute: () => Promise<unknown>,
  expectedMessage: string | RegExp,
  label: string,
): Promise<void> {
  try {
    await execute()
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    if (typeof expectedMessage === 'string') {
      assert.equal(message, expectedMessage, label)
    } else {
      assert.match(message, expectedMessage, label)
    }
    return
  }
  throw new Error(label)
}

const metrics = {
  Quantity: 1.5,
  RegularQuantity: 1,
  DiscountQuantity: 0.5,
  UnknownQuantity: 0,
  ReturnQuantity: 0,
  SalesAmount: 14.75,
  DiscountStatus: 'Partial',
  OriginalPriceMin: 10,
  OriginalPriceMax: 12,
  DiscountPriceMin: null,
  DiscountPriceMax: null,
}
const query = {
  startDate: '2026-08-01T00:00:00.000Z',
  endDate: '2026-08-18T00:00:00.000Z',
  storeCodes: ['S1'],
  itemNumbers: ['00123'],
}
const detailRequest = { ...query, productCode: 'P-1' }
const coverage = { Status: 'Partial', ReadyDates: ['2026-08-18'], PendingDates: [{ Date: '2026-08-17', Reason: 'queued' }], Version: 'coverage-v1' }
const originalFetch = globalThis.fetch
let captured: Array<{ url: string; init?: RequestInit }> = []
let responseMode: 'normal' | 'missingMetrics' | 'missingNullablePrices' | 'invalidNullablePrice' | 'emptyPayload' | 'businessFailure' | 'forbidden' | 'unauthorized' | 'coverageConflict' | 'abort' | 'returns' | 'netZero' | 'missingScope' | 'pending' = 'normal'

try {
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    captured.push({ url, init })
    const pathname = new URL(url, 'http://localhost').pathname

    if (pathname.endsWith('/refresh')) {
      return jsonResponse({ message: 'refresh rejected' }, 401)
    }
    if (responseMode === 'abort') {
      return Promise.reject(new DOMException('cancelled', 'AbortError'))
    }
    if (responseMode === 'forbidden') {
      return jsonResponse({ message: '无权限' }, 403)
    }
    if (responseMode === 'unauthorized') {
      return jsonResponse({ message: '登录已过期' }, 401)
    }
    if (responseMode === 'coverageConflict' && pathname.endsWith('/detail')) {
      return jsonResponse({ errorCode: 'BATCH_PRODUCT_SALES_COVERAGE_VERSION_CONFLICT', message: '统计覆盖范围已更新' }, 409)
    }
    if (responseMode === 'businessFailure') {
      return jsonResponse({ success: false, message: '统计未就绪' })
    }
    if (pathname.endsWith('/options')) {
      return jsonResponse({
        Success: true,
        Data: responseMode === 'emptyPayload'
          ? {}
          : { Stores: [{ Code: 'S1', Name: 'Sunnybank' }], MaxItemNumbers: 3000, MaxDays: 366 },
      })
    }
    if (pathname.endsWith('/query')) {
      return jsonResponse({
        success: true,
        data: {
          startDate: query.startDate, endDate: query.endDate, storeCodes: responseMode === 'missingScope' ? undefined : ['S1', 'S2'],
          matches: [{ itemNumber: '00123', status: 'Matched', productCodes: ['P-1'] }],
          products: [{ productCode: 'P-1', itemNumber: '00123', productName: '测试商品', quantity: 1.5, salesAmount: 14.75 }],
          warnings: [],
          statisticStatus: 'Fresh',
          statisticUpdatedAt: '2026-08-18T02:00:00Z',
          coverage,
          overview: { metrics, daily: [{ date: '2026-08-18T13:00:00+10:00', metrics }], branches: [{ branchCode: 'S1', branchName: 'Sunnybank', metrics, daily: [], contributingProductCount: 1 }] },
        },
      })
    }
    if (pathname.endsWith('/detail')) {
      const detailMetrics = responseMode === 'missingMetrics'
        ? { ...metrics, ReturnQuantity: undefined }
        : responseMode === 'missingNullablePrices'
          ? {
            ...metrics,
            OriginalPriceMin: undefined,
            OriginalPriceMax: undefined,
            DiscountPriceMin: undefined,
            DiscountPriceMax: undefined,
          }
          : responseMode === 'invalidNullablePrice'
            ? { ...metrics, OriginalPriceMin: 'not-a-number' }
            : responseMode === 'pending'
              ? { ...metrics, Quantity: 7, RegularQuantity: 0, DiscountQuantity: 0, UnknownQuantity: 7, DiscountStatus: 'pending' }
              : responseMode === 'returns' ? { ...metrics, Quantity: -2, RegularQuantity: -1, DiscountQuantity: -1, ReturnQuantity: 2 }
              : responseMode === 'netZero' ? { ...metrics, Quantity: 0, RegularQuantity: 1, DiscountQuantity: -1, ReturnQuantity: 1 } : metrics
      return jsonResponse({
        Success: true,
        Data: {
          StartDate: query.startDate, EndDate: query.endDate, StoreCodes: ['S1', 'S2'],
          ProductCodes: ['P-1'], Product: { ProductCode: 'P-1', ItemNumber: '00123', ProductName: '测试商品', EnglishName: 'Test item' },
          Metrics: detailMetrics,
          StatisticStatus: 'Fresh', DiscountStatisticStatus: responseMode === 'pending' ? 'Queued' : 'Fresh',
          Daily: [{ Date: '2026-08-18T13:00:00+10:00', Metrics: metrics }],
          Branches: [{ BranchCode: 'S1', BranchName: 'Sunnybank', Metrics: metrics, Daily: [] }],
          Warnings: [],
          Coverage: coverage,
        },
      })
    }
    throw new Error(`unexpected request ${url}`)
  }) as typeof fetch

  const optionsController = new AbortController()
  const options = await batchProductSalesApi.getOptions(optionsController.signal)
  assert.equal(captured[0]?.url, '/api/react/v1/dashboard/batch-product-sales-analysis/options', 'options 必须请求固定 GET 路径')
  assert.equal(captured[0]?.init?.method, 'GET', 'options 必须使用 GET')
  assert.equal(captured[0]?.init?.signal, optionsController.signal, 'options 必须透传 AbortSignal')
  assert.deepEqual(options, {
    stores: [{ code: 'S1', name: 'Sunnybank' }], maxItemNumbers: 3000, maxDays: 366,
  }, 'options 必须解包 PascalCase 的真实信封')

  const queryController = new AbortController()
  const queryResult = await batchProductSalesApi.query(query, queryController.signal)
  assert.equal(captured[1]?.url, '/api/react/v1/dashboard/batch-product-sales-analysis/query', 'query 必须请求固定 POST 路径')
  assert.equal(captured[1]?.init?.method, 'POST', 'query 必须使用 POST')
  assert.equal(captured[1]?.init?.signal, queryController.signal, 'query 必须透传 AbortSignal')
  assert.deepEqual(JSON.parse(String(captured[1]?.init?.body)), query, '查询请求必须原样保留调用方日期范围')
  assert.equal(queryResult.matches[0]?.status, 'matched', 'PascalCase 匹配状态必须归一化')
  assert.equal(queryResult.startDate, '2026-08-01', '查询结果日期归一化')
  assert.equal(queryResult.endDate, '2026-08-18', '查询结果日期归一化')
  assert.deepEqual(queryResult.storeCodes, ['S1', 'S2'], '必须保留服务端实际有效门店')
  assert.equal(queryResult.products[0]?.quantity, 1.5, '数量必须保留 decimal，不得取整或回退为零')
  assert.equal(queryResult.coverage.version, 'coverage-v1', '日期覆盖版本必须保留')

  const detail = await batchProductSalesApi.getDetail(detailRequest)
  assert.equal(captured[2]?.url, '/api/react/v1/dashboard/batch-product-sales-analysis/detail', 'detail 必须请求固定 POST 路径')
  assert.equal(detail.metrics.discountStatus, 'partial', 'PascalCase 折扣状态必须归一化')
  assert.equal(detail.daily[0]?.date, '2026-08-18', '日期必须归一化为合法前十位')
  assert.equal(detail.metrics.originalPriceMin, 10, '原价区间必须保留')
  assert.equal(detail.metrics.discountPriceMin, null, '可空折扣价必须保留 null')

  const lockedDetail = await batchProductSalesApi.getDetail({ ...detailRequest, coverageVersion: 'coverage-v1', readyDates: ['2026-08-18'] })
  assert.equal(lockedDetail.coverage.readyDates[0], '2026-08-18', '详情必须保留锁定后的日期覆盖')
  assert.deepEqual(JSON.parse(String(captured[captured.length - 1]?.init?.body)).readyDates, ['2026-08-18'], '详情必须发送已完成日期集合')

  await batchProductSalesApi.getDetail({ ...detailRequest, includeDiscounts: false })
  assert.equal(JSON.parse(String(captured[captured.length - 1]?.init?.body)).includeDiscounts, false, '单品可靠详情必须明确关闭折扣分类，避免等待 SnapshotReader')

  responseMode = 'pending'
  const pendingDetail = await batchProductSalesApi.getDetail(detailRequest)
  assert.equal(pendingDetail.metrics.quantity, 7)
  assert.equal(pendingDetail.metrics.discountStatus, 'pending')
  assert.equal(pendingDetail.discountStatisticStatus, 'Queued')
  responseMode = 'normal'

  await assertRejects(
    () => batchProductSalesApi.query({ ...query, startDate: '2026-02-31' }),
    '缺少或非法开始日期',
    '非法日期范围不得向后端请求',
  )
  await assertRejects(
    () => batchProductSalesApi.query({ ...query, startDate: '2026-08-19', endDate: '2026-08-18' }),
    '开始日期不能晚于结束日期',
    '反向日期范围不得向后端请求',
  )

  assert.deepEqual(detail.storeCodes, ['S1', 'S2'], '详情必须保留服务端实际门店')
  const allStoresResult = await batchProductSalesApi.query({ ...query, storeCodes: [] })
  assert.deepEqual(allStoresResult.storeCodes, ['S1', 'S2'], '全部门店请求必须取得服务端已解析的快照')
  responseMode = 'returns'
  assert.equal((await batchProductSalesApi.getDetail(detailRequest)).metrics.quantity, -2, '退货已在三类净销量扣减，不得再次相加')
  responseMode = 'netZero'
  assert.equal((await batchProductSalesApi.getDetail(detailRequest)).metrics.quantity, 0, '净销量为0仍可有退货数量')
  responseMode = 'missingScope'
  await assertRejects(() => batchProductSalesApi.query(query), '缺少或非法门店编码', '缺少实际范围必须拒绝')

  responseMode = 'missingMetrics'
  await assertRejects(
    () => batchProductSalesApi.getDetail(detailRequest),
    '缺少或非法退货数量',
    '缺少关键指标不得回退为零',
  )

  responseMode = 'missingNullablePrices'
  const omittedPriceDetail = await batchProductSalesApi.getDetail(detailRequest)
  assert.equal(omittedPriceDetail.metrics.originalPriceMin, null, '省略的原价最小值必须按空价格范围处理')
  assert.equal(omittedPriceDetail.metrics.originalPriceMax, null, '省略的原价最大值必须按空价格范围处理')
  assert.equal(omittedPriceDetail.metrics.discountPriceMin, null, '省略的折扣价最小值必须按空价格范围处理')
  assert.equal(omittedPriceDetail.metrics.discountPriceMax, null, '省略的折扣价最大值必须按空价格范围处理')

  responseMode = 'invalidNullablePrice'
  await assertRejects(
    () => batchProductSalesApi.getDetail(detailRequest),
    '缺少或非法原价最小值',
    '可空价格字段存在但不是数值时仍必须拒绝',
  )

  responseMode = 'emptyPayload'
  await assertRejects(
    () => batchProductSalesApi.getOptions(),
    '缺少或非法门店选项',
    '空的异常 payload 不得伪造默认选项',
  )

  responseMode = 'businessFailure'
  await assertRejects(
    () => batchProductSalesApi.query(query),
    '统计未就绪',
    'HTTP 200 的业务失败必须由单次 ApiResponse 解包抛出',
  )

  responseMode = 'forbidden'
  try {
    await batchProductSalesApi.getOptions()
    throw new Error('403 必须拒绝')
  } catch (error) {
    assert(error instanceof RequestError, '403 必须保留 request 层 RequestError')
    assert.equal(error.status, 403, '403 状态码不得被服务层改写')
  }

  responseMode = 'unauthorized'
  try {
    await batchProductSalesApi.getOptions()
    throw new Error('401 必须拒绝')
  } catch (error) {
    assert(error instanceof RequestError, '401 必须保留 request 层 RequestError')
    assert.equal(error.status, 401, '401 状态码不得被服务层改写')
  }

  responseMode = 'coverageConflict'
  try {
    await batchProductSalesApi.getDetail({ ...detailRequest, coverageVersion: 'coverage-v1', readyDates: ['2026-08-18'] })
    throw new Error('覆盖范围冲突必须拒绝')
  } catch (error) {
    assert(error instanceof RequestError, '409 覆盖范围冲突必须保留 RequestError')
    assert.equal(error.status, 409, '覆盖范围冲突必须保留 HTTP 409')
    assert.equal((error.payload as { errorCode?: string }).errorCode, 'BATCH_PRODUCT_SALES_COVERAGE_VERSION_CONFLICT', '覆盖范围冲突码必须保留给页面原子刷新逻辑')
  }

  responseMode = 'abort'
  const abortController = new AbortController()
  try {
    await batchProductSalesApi.query(query, abortController.signal)
    throw new Error('取消必须拒绝')
  } catch (error) {
    assert.equal((error as { name?: string }).name, 'AbortError', '取消必须原样保留 AbortError')
    assert.equal(captured[captured.length - 1]?.init?.signal, abortController.signal, '取消信号必须传至 fetch')
  }
} finally {
  globalThis.fetch = originalFetch
}

console.log('batchProductSalesAnalysisService.test: ok')
