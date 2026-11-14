import {
  addCartonToPackPallet,
  dispatchPackCarton,
  dispatchPackPallet,
  getPackScope,
  listPackCarriers,
  listPackCartons,
  listPackDestinations,
  listPackIncoming,
  listPackLocations,
  listPackPallets,
  locatePackProduct,
  lookupPackReceive,
  readPackingError,
  receivePackCarton,
  receivePackPallet,
  removeCartonFromPackPallet,
  reopenPackCarton,
  resolvePackCode,
  savePackCarrier,
  savePackLocation,
  scanIntoPackCarton,
  sealPackCarton,
  setPackCartonItemQuantity,
  setPackCartonLocation,
  updatePackPalletHeader,
} from './packingService'

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

const BASE = '/api/react/v1/packing'

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

  // 所属地点
  respondWith(200, { success: true, data: { owners: [{ code: '1008', name: 'Sunnybank', isWarehouse: false }], isManager: true, isAdmin: false } })
  let request = await capture(() => getPackScope())
  assertEqual(request.url.endsWith(`${BASE}/scope`), true, '地点接口地址')
  assertEqual((await getPackScope()).isManager, true, '读到 isManager')

  // 箱列表：地点、筛选、关键字、只看我的；mine=false 时不带参数
  respondWith(200, { success: true, data: { items: [], packingCount: 1, storedCount: 2, outgoingCount: 1, incomingCount: 0 } })
  request = await capture(() => listPackCartons({ ownerCode: '1008', filter: 'outgoing', keyword: ' HB23817 ', mine: true }))
  assertEqual(
    request.url.endsWith(`${BASE}/cartons?ownerCode=1008&filter=outgoing&keyword=HB23817&mine=true`),
    true,
    `箱列表地址与参数：${request.url}`,
  )
  request = await capture(() => listPackCartons({ ownerCode: '1008', filter: 'all' }))
  assertEqual(request.url.endsWith('/cartons?ownerCode=1008&filter=all'), true, '不看自己的时不带 mine')
  respondWith(200, { success: true, data: { items: [], buildingCount: 0, storedCount: 0 } })
  request = await capture(() => listPackPallets({ ownerCode: 'WAREHOUSE', filter: 'stored' }))
  assertEqual(request.url.endsWith(`${BASE}/pallets?ownerCode=WAREHOUSE&filter=stored`), true, '托盘列表带地点')

  // 扫码：带件数；一码多品选定后带 productCode
  respondWith(200, { success: true, data: { cartonGuid: 'c-1', items: [], changedProductCode: 'P-1' } })
  request = await capture(() => scanIntoPackCarton('c/1', { code: '9345678123457', quantity: 12 }))
  assertEqual(request.method, 'POST', '扫码用 POST')
  assertEqual(request.url.endsWith(`${BASE}/cartons/c%2F1/scan`), true, '箱 GUID 需转义')
  assertDeepEqual(request.body, { code: '9345678123457', quantity: 12 }, '扫码请求体')
  request = await capture(() => scanIntoPackCarton('c-1', { productCode: 'P-2', quantity: 1 }))
  assertDeepEqual(request.body, { productCode: 'P-2', quantity: 1 }, '选定商品后带 productCode')

  // 改数量 / 删除（0）、封箱、重新开箱、存放位置
  request = await capture(() => setPackCartonItemQuantity('c-1', 'P 1', 0))
  assertEqual(request.method, 'PUT', '改数量用 PUT')
  assertEqual(request.url.endsWith(`${BASE}/cartons/c-1/items/P%201`), true, '商品编码需转义')
  assertDeepEqual(request.body, { quantity: 0 }, '数量 0 表示移除')
  request = await capture(() => sealPackCarton('c-1', 4.2))
  assertDeepEqual(request.body, { weightKg: 4.2 }, '封箱带重量')
  request = await capture(() => sealPackCarton('c-1'))
  assertDeepEqual(request.body, { weightKg: null }, '不填重量传 null')
  request = await capture(() => reopenPackCarton('c-1'))
  assertEqual(`${request.method} ${request.url.endsWith('/cartons/c-1/reopen')}`, 'POST true', '重新开箱')
  request = await capture(() => setPackCartonLocation('c-1', '后仓 A 架'))
  assertEqual(`${request.method} ${request.url.endsWith('/cartons/c-1/location')}`, 'PUT true', '设置箱存放位置')
  assertDeepEqual(request.body, { locationCode: '后仓 A 架' }, '位置名称或条码')
  request = await capture(() => setPackCartonLocation('c-1', null))
  assertDeepEqual(request.body, { locationCode: null }, '清除位置传 null')

  // 发出
  request = await capture(() => dispatchPackCarton('c-1'))
  assertEqual(`${request.method} ${request.url.endsWith('/cartons/c-1/dispatch')}`, 'POST true', '发出散箱')
  request = await capture(() => dispatchPackPallet('p-1'))
  assertEqual(`${request.method} ${request.url.endsWith('/pallets/p-1/dispatch')}`, 'POST true', '发出托盘')

  // 托盘：加箱（去向不一致放行）、移出箱、改托盘信息（新的去向请求体）
  request = await capture(() => addCartonToPackPallet('p-1', 'BX2611130042', true))
  assertDeepEqual(request.body, { code: 'BX2611130042', allowDestinationMismatch: true }, '加箱请求体')
  request = await capture(() => removeCartonFromPackPallet('p-1', 'c-1'))
  assertEqual(`${request.method} ${request.url.endsWith('/pallets/p-1/cartons/c-1')}`, 'DELETE true', '移出箱')
  request = await capture(() =>
    updatePackPalletHeader('p-1', {
      destinationType: 2,
      destinationCode: '1020',
      carrierGuid: 'carrier-1',
      consignmentNo: 'BEX 4471',
      weightKg: null,
      remark: null,
    }),
  )
  assertEqual(`${request.method} ${request.url.endsWith('/pallets/p-1/header')}`, 'PUT true', '改托盘信息')
  assertEqual(
    JSON.stringify(request.body).includes('"destinationCode":"1020"') && !JSON.stringify(request.body).includes('storeCode'),
    true,
    '去向用 destinationCode，不再有 storeCode',
  )

  // 签收：待签收、扫码查找、整托（带实到箱数）、逐箱、散箱
  respondWith(200, { success: true, data: { pending: [], recent: [] } })
  request = await capture(() => listPackIncoming('1008'))
  assertEqual(request.url.endsWith(`${BASE}/incoming?ownerCode=1008`), true, '待签收带地点')
  respondWith(200, { success: true, data: { kind: 'pallet', viaCartonLabel: true, canReceive: true, inTransitCartonCount: 5 } })
  request = await capture(() => lookupPackReceive(' BX2611130039 '))
  assertEqual(request.url.endsWith(`${BASE}/receive/lookup?code=BX2611130039`), true, '扫码查找去空白')
  assertEqual((await lookupPackReceive('BX2611130039')).viaCartonLabel, true, '扫箱标签找到整个托盘')
  respondWith(200, { success: true, data: { palletGuid: 'p-1', cartons: [] } })
  request = await capture(() => receivePackPallet('p-1', { expectedCartonCount: 5, locationCode: 'A 架', viaCartonNo: 'BX2611130039' }))
  assertEqual(`${request.method} ${request.url.endsWith('/pallets/p-1/receive')}`, 'POST true', '签收托盘')
  assertDeepEqual(
    request.body,
    { expectedCartonCount: 5, locationCode: 'A 架', viaCartonNo: 'BX2611130039' },
    '整托签收带实到箱数和扫到的箱号',
  )
  request = await capture(() => receivePackPallet('p-1', { cartonGuids: ['c-1', 'c-2'], locationCode: null }))
  assertDeepEqual(request.body, { cartonGuids: ['c-1', 'c-2'], locationCode: null }, '逐箱核对只传 cartonGuids')
  request = await capture(() => receivePackCarton('c-1', 'A 架'))
  assertEqual(`${request.method} ${request.url.endsWith('/cartons/c-1/receive')}`, 'POST true', '签收散箱')
  assertDeepEqual(request.body, { locationCode: 'A 架' }, '签收散箱带位置')
  request = await capture(() => receivePackCarton('c-1'))
  assertDeepEqual(request.body, { locationCode: null }, '不放位置传 null')

  // 整托签收数量对不上：409 COUNT_MISMATCH，data 里带实际在路上的箱数
  respondWith(409, {
    success: false,
    errorCode: 'COUNT_MISMATCH',
    message: '实到 4 箱，系统记录是 5 箱，请逐箱扫码核对',
    data: { palletGuid: 'p-1', actualInTransitCartonCount: 5 },
  })
  let mismatch: unknown
  await receivePackPallet('p-1', { expectedCartonCount: 4 }).catch((error) => {
    mismatch = error
  })
  const mismatchInfo = readPackingError<{ actualInTransitCartonCount?: number }>(mismatch)
  assertEqual(mismatchInfo.status, 409, '状态码 409')
  assertEqual(mismatchInfo.errorCode, 'COUNT_MISMATCH', '错误码 COUNT_MISMATCH')
  assertEqual(mismatchInfo.data?.actualInTransitCartonCount, 5, '带回系统记录的在路上箱数')

  // 运输公司：新增 POST（任何人）、修改 PUT（管理员）；列表默认不含停用
  respondWith(200, { success: true, data: [] })
  request = await capture(() => savePackCarrier(null, { name: 'Toll', remark: null, isActive: true }))
  assertEqual(`${request.method} ${request.url.endsWith(`${BASE}/carriers`)}`, 'POST true', '新增运输公司')
  request = await capture(() => savePackCarrier('carrier-1', { name: 'Toll', remark: null, isActive: false }))
  assertEqual(`${request.method} ${request.url.endsWith('/carriers/carrier-1')}`, 'PUT true', '停用即 isActive=false 保存')
  request = await capture(() => listPackCarriers())
  assertEqual(request.url.endsWith(`${BASE}/carriers`), true, '默认只列启用的')
  request = await capture(() => listPackCarriers(true))
  assertEqual(request.url.endsWith('/carriers?includeInactive=true'), true, '主数据页包含停用的')

  // 去向候选：打包自己的接口（店员没有查看分店权限也能用），带发出地点；不再走通用分店列表
  respondWith(200, {
    success: true,
    data: [
      { code: 'WAREHOUSE', name: 'HB Warehouse', isWarehouse: true },
      { code: '1011', name: 'Inala', isWarehouse: false },
    ],
  })
  request = await capture(() => listPackDestinations('1008'))
  assertEqual(request.url.endsWith(`${BASE}/destinations?ownerCode=1008`), true, '候选地点带发出地点')
  assertEqual(request.url.includes('/api/stores'), false, '不再调用通用分店列表')
  const destinations = await listPackDestinations('1008')
  assertEqual(destinations.map((place) => place.code).join(','), 'WAREHOUSE,1011', '总仓和其他分店都在同一个列表里')
  assertEqual(destinations.filter((place) => place.isWarehouse).length, 1, '总仓用 isWarehouse 区分')
  respondWith(403, { success: false, errorCode: 'OWNER_NOT_ALLOWED', message: '你不能操作这个分店' })
  let ownerDenied: unknown
  await listPackDestinations('9999').catch((error: unknown) => {
    ownerDenied = error
  })
  assertEqual(readPackingError(ownerDenied).errorCode, 'OWNER_NOT_ALLOWED', '不能操作的分店 403')
  respondWith(200, { success: true, data: [] })

  // 存放位置
  request = await capture(() => listPackLocations('1008', true))
  assertEqual(request.url.endsWith(`${BASE}/locations?ownerCode=1008&includeInactive=true`), true, '位置列表带地点')
  request = await capture(() => listPackLocations('1008'))
  assertEqual(request.url.endsWith(`${BASE}/locations?ownerCode=1008`), true, '默认只列启用的位置')
  request = await capture(() => savePackLocation(null, { ownerCode: '1008', name: '后仓 A 架', isActive: true }))
  assertEqual(`${request.method} ${request.url.endsWith(`${BASE}/locations`)}`, 'POST true', '新增位置')
  assertDeepEqual(request.body, { ownerCode: '1008', name: '后仓 A 架', isActive: true }, '新增位置请求体')
  request = await capture(() => savePackLocation('loc-1', { ownerCode: '1008', name: '后仓 B 架', isActive: false }))
  assertEqual(`${request.method} ${request.url.endsWith('/locations/loc-1')}`, 'PUT true', '改名 / 停用位置')

  // 商品位置：带地点；只有店长可带 allStores
  respondWith(200, { success: true, data: { product: { productCode: 'P-1' }, rows: [] } })
  request = await capture(() => locatePackProduct({ ownerCode: '1008', code: ' HB23817 ' }))
  assertEqual(request.url.endsWith(`${BASE}/locate?ownerCode=1008&code=HB23817`), true, '位置查询不带 allStores')
  request = await capture(() => locatePackProduct({ ownerCode: '1008', code: 'HB23817', allStores: true }))
  assertEqual(request.url.endsWith('&allStores=true'), true, '查全部分店带 allStores')

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
  await scanIntoPackCarton('c-1', { code: 'SET-01', quantity: 1 }).catch((error: unknown) => {
    ambiguous = error
  })
  const ambiguousInfo = readPackingError<{ candidates: { productCode: string }[] }>(ambiguous)
  assertEqual(ambiguousInfo.status, 409, '状态码 409')
  assertEqual(ambiguousInfo.errorCode, 'CODE_AMBIGUOUS', '错误码')
  assertEqual(ambiguousInfo.message, '这个码对应多个商品，请选择', '提示取后端 message')
  assertEqual(ambiguousInfo.data?.candidates.map((candidate) => candidate.productCode).join(','), 'P-1,P-2', '候选商品')

  // 403：店员重新开箱，照实提示后端原因
  respondWith(403, { success: false, errorCode: 'PACKING_NOT_ALLOWED', message: '只有店长或仓库经理可以重新开箱' })
  let denied: unknown
  await reopenPackCarton('c-1').catch((error: unknown) => {
    denied = error
  })
  const deniedInfo = readPackingError(denied)
  assertEqual(deniedInfo.status, 403, '状态码 403')
  assertEqual(deniedInfo.errorCode, 'PACKING_NOT_ALLOWED', '403 错误码')
  assertEqual(deniedInfo.message, '只有店长或仓库经理可以重新开箱', '403 提示原样透传')

  // 发出后再改：409 CARTON_IN_TRANSIT
  respondWith(409, { success: false, errorCode: 'CARTON_IN_TRANSIT', message: '这只箱已经发出，不能再修改' })
  let inTransit: unknown
  await setPackCartonItemQuantity('c-1', 'P-1', 2).catch((error: unknown) => {
    inTransit = error
  })
  assertEqual(readPackingError(inTransit).errorCode, 'CARTON_IN_TRANSIT', '发出后不能再改')

  // 非接口错误
  assertDeepEqual(readPackingError(new Error('网络断开')), { status: 0, message: '网络断开' }, '普通异常')
} finally {
  globalThis.fetch = originalFetch
}

console.log('packingService.test: ok')
