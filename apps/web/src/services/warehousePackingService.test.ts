import {
  addCartonToPackPallet,
  listPackCarriers,
  listPackCartons,
  readPackingError,
  removeCartonFromPackPallet,
  reopenPackCarton,
  resolvePackCode,
  savePackCarrier,
  scanIntoPackCarton,
  sealPackCarton,
  setPackCartonItemQuantity,
  updatePackPalletHeader,
} from './warehousePackingService'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  assertEqual(JSON.stringify(actual), JSON.stringify(expected), message)
}

interface Captured {
  url: string
  method: string
  body: unknown
}

const originalFetch = globalThis.fetch
let captured: Captured = { url: '', method: '', body: undefined }
let nextResponse: { status: number; body: unknown } = { status: 200, body: { success: true, data: {} } }

function respondWith(status: number, body: unknown) {
  nextResponse = { status, body }
}

async function capture(run: () => Promise<unknown>): Promise<Captured> {
  await run()
  return captured
}

try {
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    captured = {
      url: String(input),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
    }
    return new Response(JSON.stringify(nextResponse.body), {
      status: nextResponse.status,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  // 箱列表：筛选、关键字、只看我的；mine=false 时不带参数
  respondWith(200, { success: true, data: { items: [], packingCount: 1, sealedCount: 2, looseSealedCount: 1 } })
  let request = await capture(() => listPackCartons({ filter: 'loose', keyword: ' HB23817 ', mine: true }))
  assertEqual(
    request.url.endsWith('/api/react/v1/warehouse-packing/cartons?filter=loose&keyword=HB23817&mine=true'),
    true,
    `箱列表地址与参数：${request.url}`,
  )
  request = await capture(() => listPackCartons({ filter: 'all' }))
  assertEqual(request.url.endsWith('/cartons?filter=all'), true, '不看自己的时不带 mine')

  // 扫码：带件数；一码多品选定后带 productCode
  respondWith(200, { success: true, data: { cartonGuid: 'c-1', items: [], changedProductCode: 'P-1' } })
  request = await capture(() => scanIntoPackCarton('c/1', { code: '9345678123457', quantity: 12 }))
  assertEqual(request.method, 'POST', '扫码用 POST')
  assertEqual(request.url.endsWith('/cartons/c%2F1/scan'), true, '箱 GUID 需转义')
  assertDeepEqual(request.body, { code: '9345678123457', quantity: 12 }, '扫码请求体')
  request = await capture(() => scanIntoPackCarton('c-1', { productCode: 'P-2', quantity: 1 }))
  assertDeepEqual(request.body, { productCode: 'P-2', quantity: 1 }, '选定商品后带 productCode')

  // 改数量 / 删除（0）、封箱、重新开箱
  request = await capture(() => setPackCartonItemQuantity('c-1', 'P 1', 0))
  assertEqual(request.method, 'PUT', '改数量用 PUT')
  assertEqual(request.url.endsWith('/cartons/c-1/items/P%201'), true, '商品编码需转义')
  assertDeepEqual(request.body, { quantity: 0 }, '数量 0 表示移除')
  request = await capture(() => sealPackCarton('c-1', 4.2))
  assertEqual(request.url.endsWith('/cartons/c-1/seal'), true, '封箱地址')
  assertDeepEqual(request.body, { weightKg: 4.2 }, '封箱带重量')
  request = await capture(() => sealPackCarton('c-1'))
  assertDeepEqual(request.body, { weightKg: null }, '不填重量传 null')
  request = await capture(() => reopenPackCarton('c-1'))
  assertEqual(`${request.method} ${request.url.endsWith('/cartons/c-1/reopen')}`, 'POST true', '重新开箱')

  // 托盘：加箱（去向不一致放行）、移出箱、改箱头
  request = await capture(() => addCartonToPackPallet('p-1', 'BX2611130042', true))
  assertEqual(request.url.endsWith('/pallets/p-1/cartons'), true, '加箱地址')
  assertDeepEqual(request.body, { code: 'BX2611130042', allowDestinationMismatch: true }, '加箱请求体')
  request = await capture(() => removeCartonFromPackPallet('p-1', 'c-1'))
  assertEqual(`${request.method} ${request.url.endsWith('/pallets/p-1/cartons/c-1')}`, 'DELETE true', '移出箱')
  request = await capture(() =>
    updatePackPalletHeader('p-1', {
      destinationType: 2,
      destinationStoreCode: '1020',
      carrierGuid: 'carrier-1',
      consignmentNo: 'BEX 4471',
      weightKg: null,
      remark: null,
    }),
  )
  assertEqual(`${request.method} ${request.url.endsWith('/pallets/p-1/header')}`, 'PUT true', '改托盘信息')

  // 运输公司：新增 POST、修改 PUT；列表默认不含停用
  request = await capture(() => savePackCarrier(null, { name: 'Toll', remark: null, isActive: true }))
  assertEqual(`${request.method} ${request.url.endsWith('/carriers')}`, 'POST true', '新增运输公司')
  request = await capture(() => savePackCarrier('carrier-1', { name: 'Toll', remark: null, isActive: false }))
  assertEqual(`${request.method} ${request.url.endsWith('/carriers/carrier-1')}`, 'PUT true', '停用即 isActive=false 保存')
  respondWith(200, { success: true, data: [] })
  request = await capture(() => listPackCarriers())
  assertEqual(request.url.endsWith('/carriers'), true, '默认只列启用的')
  request = await capture(() => listPackCarriers(true))
  assertEqual(request.url.endsWith('/carriers?includeInactive=true'), true, '主数据页包含停用的')

  // resolve：命中返回结果；404 转成 null（按商品码处理）；其他错误照常抛出
  respondWith(200, { success: true, data: { kind: 'pallet', guid: 'p-1', no: 'PL261113007' } })
  assertDeepEqual(await resolvePackCode(' PL261113007 '), { kind: 'pallet', guid: 'p-1', no: 'PL261113007' }, '识别托盘号')
  assertEqual(captured.url.endsWith('/resolve?code=PL261113007'), true, 'resolve 参数去空格')
  respondWith(404, { success: false, errorCode: 'CODE_NOT_FOUND', message: '不是箱号或托盘号' })
  assertEqual(await resolvePackCode('BX0000000000'), null, '404 返回 null')
  respondWith(400, { success: false, errorCode: 'INVALID_REQUEST', message: '条码不能为空' })
  let rejected = false
  await resolvePackCode('BX1').catch(() => {
    rejected = true
  })
  assertEqual(rejected, true, '非 404 错误应抛出')

  // 一码多品：409 + CODE_AMBIGUOUS + 候选商品
  respondWith(409, {
    success: false,
    errorCode: 'CODE_AMBIGUOUS',
    message: '这个码对应多个商品，请选择',
    data: { cartonGuid: 'c-1', candidates: [{ productCode: 'P-1' }, { productCode: 'P-2' }] },
  })
  let ambiguous: unknown
  await scanIntoPackCarton('c-1', { code: 'SET-01', quantity: 1 }).catch((error) => {
    ambiguous = error
  })
  const ambiguousInfo = readPackingError<{ candidates: { productCode: string }[] }>(ambiguous)
  assertEqual(ambiguousInfo.status, 409, '状态码 409')
  assertEqual(ambiguousInfo.errorCode, 'CODE_AMBIGUOUS', '错误码')
  assertEqual(ambiguousInfo.message, '这个码对应多个商品，请选择', '提示取后端 message')
  assertEqual(ambiguousInfo.data?.candidates.map((candidate) => candidate.productCode).join(','), 'P-1,P-2', '候选商品')

  // 403：非经理重新开箱，照实提示后端原因
  respondWith(403, { success: false, errorCode: 'PACKING_NOT_ALLOWED', message: '只有仓库经理可以重新开箱' })
  let denied: unknown
  await reopenPackCarton('c-1').catch((error) => {
    denied = error
  })
  const deniedInfo = readPackingError(denied)
  assertEqual(deniedInfo.status, 403, '状态码 403')
  assertEqual(deniedInfo.errorCode, 'PACKING_NOT_ALLOWED', '403 错误码')
  assertEqual(deniedInfo.message, '只有仓库经理可以重新开箱', '403 提示原样透传')

  // 非接口错误
  assertDeepEqual(readPackingError(new Error('网络断开')), { status: 0, message: '网络断开' }, '普通异常')
} finally {
  globalThis.fetch = originalFetch
}

console.log('warehousePackingService.test: ok')
