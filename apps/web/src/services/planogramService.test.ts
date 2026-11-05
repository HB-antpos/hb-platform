import assert from 'node:assert/strict'
import { resolvePlanogramError } from '../pages/PosAdmin/Planogram/planogramLogic'
import {
  archivePlan,
  batchUpdateProductSpecs,
  createHookModel,
  createPlan,
  createShelfModel,
  deleteHookModel,
  deletePlan,
  deleteProductSpec,
  deleteShelfModel,
  duplicatePlan,
  exportPlan,
  getPlan,
  getPlanVersion,
  getProductSpec,
  importPlan,
  listAllHookModels,
  listHookModels,
  listPlans,
  listPlanVersions,
  listProductSpecs,
  listShelfModels,
  normalizePlanListItem,
  normalizeBatchResult,
  PLANOGRAM_API_BASE,
  productSpecScopeParams,
  publishPlan,
  restorePlan,
  savePlan,
  saveProductSpec,
  updateHookModel,
  updateShelfModel,
  validatePlan,
} from './planogramService'

// 商品陈列图接口封装（附录 A）：路径、查询参数、请求体、信封解包、字段兜底、失败时带 errorCode 抛错。

interface Captured { url: URL; init?: RequestInit }
const originalFetch = globalThis.fetch
const requests: Captured[] = []
let nextStatus = 200
let nextBody: unknown = {}

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = new URL(String(input), 'http://localhost')
  // 失败请求会异步上报中心日志，这些请求不计入断言。
  if (!url.pathname.startsWith(PLANOGRAM_API_BASE)) {
    return new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } })
  }
  requests.push({ url, init })
  return new Response(JSON.stringify(nextBody), { status: nextStatus, headers: { 'content-type': 'application/json' } })
}) as typeof fetch

function last(): Captured {
  const request = requests[requests.length - 1]
  assert.ok(request, '应发出请求')
  return request
}

function ok(data: unknown) {
  nextStatus = 200
  nextBody = { success: true, data }
}

function bodyOf(request: Captured): Record<string, unknown> {
  return JSON.parse(String(request.init?.body)) as Record<string, unknown>
}

try {
  assert.equal(PLANOGRAM_API_BASE, '/api/react/v1/planogram', '前缀照附录 A')

  // 1) 货架型号列表：路径与查询参数；isActive=false 要显式传；关键字去首尾空格；空关键字不传。
  ok({
    items: [{
      guid: 'g1', code: 'G-S-1', name: '单面 900', sideType: 1,
      bayWidthMm: '900', totalHeightMm: 1200, baseDepthMm: 300, baseTopHeightMm: 150, uprightWidthMm: 30,
      holePitchXMm: 25.4, holePitchYMm: '25', holeDiameterMm: 6, firstHoleLeftMm: 25, firstHoleTopMm: 25,
      holeCols: 34, holeRows: 41, rowNumberingFrom: 1,
    }],
    total: 1, page: 1, pageSize: 20,
  })
  const controller = new AbortController()
  const shelves = await listShelfModels({ keyword: '  洞洞  ', isActive: false, page: 2, pageSize: 20 }, controller.signal)
  assert.equal(last().url.pathname, '/api/react/v1/planogram/shelf-models')
  assert.equal(last().url.searchParams.get('keyword'), '洞洞')
  assert.equal(last().url.searchParams.get('isActive'), 'false', 'isActive=false 要显式传，不能被当成空值丢掉')
  assert.equal(last().url.searchParams.get('page'), '2')
  assert.equal(last().url.searchParams.get('pageSize'), '20')
  assert.equal(last().init?.signal, controller.signal, 'AbortSignal 应透传')
  assert.equal(shelves.items[0].bayWidthMm, 900, '字符串数字转成数字')
  assert.equal(shelves.items[0].holePitchXMm, 25.4, '小数孔距保持原值')
  assert.equal(shelves.items[0].holePitchYMm, 25)
  assert.equal(shelves.items[0].isActive, true, '缺 isActive 按启用处理')
  assert.equal(shelves.items[0].referencedPlanCount, 0, '缺 referencedPlanCount 按 0 处理')
  assert.equal(shelves.items[0].color, null, '缺颜色是 null')
  assert.equal(shelves.total, 1)

  await listShelfModels({ keyword: '   ', page: 1, pageSize: 50 })
  assert.equal(last().url.searchParams.has('keyword'), false, '空关键字不传')
  assert.equal(last().url.searchParams.has('isActive'), false, '没选状态不传 isActive')

  // 2) 货架型号 增 / 改 / 删：方法、路径（guid 要编码）、请求体原样。
  const shelfInput = {
    code: 'G-S-1', name: '单面 900', sideType: 1 as const, color: null, source: null,
    bayWidthMm: 900, totalHeightMm: 1200, baseDepthMm: 300, baseTopHeightMm: 150, uprightWidthMm: 30,
    holePitchXMm: 25.4, holePitchYMm: 25.4, holeDiameterMm: 6, firstHoleLeftMm: 25.4, firstHoleTopMm: 25.4,
    holeCols: 33, holeRows: 40, rowNumberingFrom: 1 as const, isActive: true,
  }
  ok({ ...shelfInput, guid: 'new-guid' })
  const created = await createShelfModel(shelfInput)
  assert.equal(last().init?.method, 'POST')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/shelf-models')
  assert.deepEqual(bodyOf(last()), shelfInput, '请求体原样提交（孔距允许小数）')
  assert.equal(created.guid, 'new-guid')

  await updateShelfModel('a/b c', shelfInput)
  assert.equal(last().init?.method, 'PUT')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/shelf-models/a%2Fb%20c', 'guid 要做 URL 编码')

  ok(null)
  await deleteShelfModel('g1')
  assert.equal(last().init?.method, 'DELETE')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/shelf-models/g1')

  // 3) 被引用的型号不能删：HTTP 4xx + errorCode，错误解析认得 PLANOGRAM_MODEL_IN_USE
  nextStatus = 409
  nextBody = { success: false, errorCode: 'PLANOGRAM_MODEL_IN_USE', message: '该型号已被方案引用，请改为停用' }
  await assert.rejects(deleteShelfModel('g1'), (error: unknown) => {
    const resolved = resolvePlanogramError(error)
    assert.equal(resolved.kind, 'error')
    if (resolved.kind === 'error') {
      assert.equal(resolved.code, 'PLANOGRAM_MODEL_IN_USE')
      assert.equal(resolved.key, 'planogram.errors.modelInUse')
      assert.equal(resolved.serverMessage, '该型号已被方案引用，请改为停用')
    }
    return true
  })

  // HTTP 200 但 success=false（业务失败）同样要抛错，不能被当成保存成功；code 字段也认
  nextStatus = 200
  nextBody = { success: false, code: 'PLANOGRAM_CODE_DUPLICATE', message: '编码已存在' }
  await assert.rejects(createShelfModel(shelfInput), (error: unknown) => {
    const resolved = resolvePlanogramError(error)
    return resolved.kind === 'error' && resolved.code === 'PLANOGRAM_CODE_DUPLICATE'
  })

  // 校验失败带 details 字段列表
  nextStatus = 400
  nextBody = {
    success: false,
    errorCode: 'PLANOGRAM_VALIDATION',
    message: '填写不合法',
    details: [{ field: 'holePitchXMm', message: '孔距必须在 0.5–200 之间' }],
  }
  await assert.rejects(updateShelfModel('g1', shelfInput), (error: unknown) => {
    const resolved = resolvePlanogramError(error)
    return resolved.kind === 'error' && resolved.fieldErrors[0]?.field === 'holePitchXMm'
  })

  // 4) 挂钩型号：loadKg 缺失是 null；tagBottomAboveArmMm 缺省 10、明确给 0 保持 0（无价签臂）
  ok({
    items: [
      { guid: 'h1', code: 'HK-1', name: '双钩 200', prongCount: 2, hasPriceTagArm: true, armLengthMm: 200, dropMm: 50, wireDiameterMm: 5, tipReserveMm: 20, tagWidthMm: 52, tagHeightMm: 26, holeSpanX: 2, holeSpanY: 1, loadKg: '3' },
      { guid: 'h2', code: 'HK-2', name: '单钩 200', prongCount: 1, hasPriceTagArm: false, armLengthMm: 200, dropMm: 50, wireDiameterMm: 5, tipReserveMm: 20, tagWidthMm: 0, tagHeightMm: 0, tagBottomAboveArmMm: 0, isActive: false },
    ],
    total: 2, page: 1, pageSize: 100,
  })
  const hooks = await listHookModels({ page: 1, pageSize: 100 })
  assert.equal(last().url.pathname, '/api/react/v1/planogram/hook-models')
  assert.equal(hooks.items[0].loadKg, 3, '承重字符串转数字')
  assert.equal(hooks.items[0].tagBottomAboveArmMm, 10, '缺省 10')
  assert.equal(hooks.items[0].holeSpanX, 2)
  assert.equal(hooks.items[1].loadKg, null, '缺承重是 null')
  assert.equal(hooks.items[1].tagBottomAboveArmMm, 0, '明确给 0 保持 0')
  assert.equal(hooks.items[1].isActive, false)

  const all = await listAllHookModels()
  assert.equal(last().url.searchParams.get('pageSize'), '100', '挂钩目录一次取一页 100 条')
  assert.equal(last().url.searchParams.has('isActive'), false, '目录含停用型号')
  assert.equal(all.length, 2)

  const hookInput = {
    code: 'HK-1', name: '双钩 200', color: null, prongCount: 2 as const, hasPriceTagArm: true, armLengthMm: 200, dropMm: 50,
    wireDiameterMm: 5, tipReserveMm: 20, tagWidthMm: 52, tagHeightMm: 26, tagBottomAboveArmMm: 10, holeSpanX: 2, holeSpanY: 1,
    loadKg: 3, isActive: true,
  }
  ok({ ...hookInput, guid: 'h9' })
  await createHookModel(hookInput)
  assert.equal(last().init?.method, 'POST')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/hook-models')
  await updateHookModel('h9', hookInput)
  assert.equal(last().init?.method, 'PUT')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/hook-models/h9')
  ok(null)
  await deleteHookModel('h9')
  assert.equal(last().init?.method, 'DELETE')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/hook-models/h9')

  // 5) 商品陈列参数列表：status 参数；hasSpec 缺失时以 spec 是否存在为准；覆盖表只保留数字
  ok({
    items: [
      {
        productCode: 'XD0001', productName: '笔记本', retailPrice: '2.5',
        spec: { productCode: 'XD0001', widthMm: 110, heightMm: 155, depthMm: 12, hookQtyOverrides: { h1: 16, h2: 'x', h3: null }, source: 9 },
      },
      { productCode: 'XD0002', productName: '文件夹', retailPrice: 3, hasSpec: false, spec: null },
    ],
    total: 2, page: 1, pageSize: 20,
  })
  const specs = await listProductSpecs({ keyword: 'XD', status: 'unset', page: 1, pageSize: 20 })
  assert.equal(last().url.pathname, '/api/react/v1/planogram/product-specs')
  assert.equal(last().url.searchParams.get('status'), 'unset')
  assert.equal(last().url.searchParams.get('keyword'), 'XD')
  assert.equal(specs.items[0].hasSpec, true, '缺 hasSpec 但有 spec → 已设')
  assert.equal(specs.items[0].retailPrice, 2.5)
  assert.deepEqual(specs.items[0].spec?.hookQtyOverrides, { h1: 16 }, '覆盖表丢掉非数字项')
  assert.equal(specs.items[0].spec?.hangHoleOffsetMm, 10, '吊孔偏移缺省 10')
  assert.equal(specs.items[0].spec?.source, 1, '未知来源回落到人工量取')
  assert.equal(specs.items[1].hasSpec, false)
  assert.equal(specs.items[1].spec, null)

  ok({ productCode: 'XD 01', productName: 'x', retailPrice: 1, hasSpec: false })
  await getProductSpec('XD 01')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/product-specs/XD%2001', '商品编码要编码')

  const spec = {
    productCode: 'XD0001', widthMm: 110, heightMm: 155, depthMm: 12, hangHoleOffsetMm: 10, hangHoleType: 2 as const,
    unitWeightG: 95, defaultHookGuid: 'h1', hookQtyOverrides: { h1: 16 }, source: 1 as const, notes: null,
  }
  ok(null)
  await saveProductSpec('XD0001', spec)
  assert.equal(last().init?.method, 'PUT')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/product-specs/XD0001')
  assert.deepEqual(bodyOf(last()), spec, '参数请求体原样提交')
  await deleteProductSpec('XD0001')
  assert.equal(last().init?.method, 'DELETE')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/product-specs/XD0001')

  // 6) 分页字段缺失时兜底：items 缺失给空数组，total 缺失给 0
  ok({})
  const empty = await listShelfModels({ page: 1, pageSize: 20 })
  assert.deepEqual(empty.items, [])
  assert.equal(empty.total, 0)

  // 7) 陈列方案（批 2，附录 B.2）：列表查询参数
  ok({
    items: [{
      guid: 'p1', name: '文具挂板 A1', notes: '开学季', shelfModelGuid: 's1', shelfModelName: '单面 900', bayCount: '2', gapXMm: 10, gapYMm: 0,
      status: 1, version: 3, publishedVersion: 2, publishedAt: '2026-11-03T01:00:00Z', itemCount: 51, skuCount: 12, unitCount: 433, fillPercent: 39, conflictCount: 1,
      updatedAt: '2026-11-04T00:12:00Z', updatedBy: 'admin',
    }],
    total: 1, page: 1, pageSize: 20,
  })
  const plans = await listPlans({ keyword: '  XD0001 ', shelfModelGuid: 's1', status: 0, page: 2, pageSize: 20 })
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms')
  assert.equal(last().init?.method, 'GET')
  assert.equal(last().url.searchParams.get('keyword'), 'XD0001', '关键字去首尾空格')
  assert.equal(last().url.searchParams.get('shelfModelGuid'), 's1')
  assert.equal(last().url.searchParams.get('status'), '0', '草稿 status=0 要显式传，不能被当成空值丢掉')
  assert.equal(last().url.searchParams.get('page'), '2')
  assert.equal(last().url.searchParams.has('storeCode'), false, '方案不绑定分店，不传分店参数')
  assert.equal(plans.items[0].bayCount, 2, '字符串数字转成数字')
  assert.equal(plans.items[0].gapYMm, 0, '后端明确给 0 的间距保持 0（不被默认 10 覆盖）')
  assert.equal(plans.items[0].status, 1)
  assert.equal(plans.items[0].publishedVersion, 2)
  assert.equal(plans.items[0].conflictCount, 1)
  await listPlans({ page: 1, pageSize: 20 })
  assert.equal(last().url.searchParams.has('status'), false, '不筛状态时不传 status')
  assert.equal(last().url.searchParams.has('keyword'), false)
  assert.equal(last().url.searchParams.has('shelfModelGuid'), false)

  // 8) 详情：header 嵌套（后端实际形态）与平铺（附录文字的另一种读法）都能解析；缺字段兜底
  const detailRaw = {
    rowVersion: '2026-11-04T00:12:00.0000000Z',
    items: [
      { guid: 'i2', bayIndex: 1, face: 0, itemType: 1, productCode: 'XD1', hookModelGuid: 'h1', holeCol: '5', holeRow: 3, perHookQty: 0, sortOrder: 2 },
      { guid: 'i1', bayIndex: 1, productCode: 'XD2', hookModelGuid: 'h1', holeCol: 9, holeRow: 3, perHookQty: 12, sortOrder: 1, specChanged: true },
    ],
    shelfModel: { guid: 's1', code: 'G1', name: '单面 900', bayWidthMm: 900, totalHeightMm: 1200, uprightWidthMm: 30, baseTopHeightMm: 150, holePitchXMm: 25, holePitchYMm: 25, firstHoleLeftMm: 25, firstHoleTopMm: 25, holeCols: 33, holeRows: 41 },
    hookModels: [{ guid: 'h1', code: 'H1', name: '双钩', prongCount: 2, hasPriceTagArm: true, armLengthMm: 200, dropMm: 50, tipReserveMm: 20, tagWidthMm: 52, tagHeightMm: 26 }],
    productSpecs: [{ productCode: 'XD1', productName: '胶棒', retailPrice: '2.5', hasSpec: true, spec: { productCode: 'XD1', widthMm: 70, heightMm: 150, depthMm: 25 } }],
    conflicts: [{ itemGuid: 'i1', otherItemGuid: 'i2', code: 'OVERLAP', severity: 'error', message: '间距不足' }, { itemGuid: 'i2', code: 'OVERLOAD', severity: 'weird', message: 'x' }],
    stats: { itemCount: 2, skuCount: 2, unitCount: 22, retailValue: '55.5', fillPercent: 4, conflictCount: 1 },
  }
  ok({ ...detailRaw, header: { guid: 'p1', name: '文具挂板 A1', shelfModelGuid: 's1', shelfModelName: '单面 900', bayCount: 2, gapXMm: 10, gapYMm: 10, status: 0, version: 1 } })
  const nested = await getPlan('p/1 x')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms/p%2F1%20x', '方案 guid 要编码')
  assert.equal(last().init?.method, 'GET')
  assert.equal(nested.header.guid, 'p1')
  assert.equal(nested.header.version, 1)
  assert.equal(nested.rowVersion, '2026-11-04T00:12:00.0000000Z', 'rowVersion 原样保留（字符串，不转数字）')
  assert.equal(nested.items.length, 2)
  assert.equal(nested.items[0].holeCol, 5, '字符串孔位转数字')
  assert.equal(nested.items[0].perHookQty, null, '每钩挂数 0 视为没填（null = 按规则）')
  assert.equal(nested.items[1].perHookQty, 12)
  assert.equal(nested.items[1].specChanged, true)
  assert.equal(nested.items[0].specChanged, false, '缺 specChanged 按 false')
  assert.equal(nested.items[1].face, 0, 'face 恒 0')
  assert.equal(nested.items[1].itemType, 1, 'itemType 恒 1')
  assert.equal(nested.shelfModel.holeCols, 33)
  assert.equal(nested.hookModels[0].tagBottomAboveArmMm, 10, '缺价签底边高度按默认 10')
  assert.equal(nested.productSpecs[0].retailPrice, 2.5)
  assert.equal(nested.productSpecs[0].spec?.widthMm, 70)
  assert.equal(nested.conflicts[0].otherItemGuid, 'i2')
  assert.equal(nested.conflicts[1].severity, 'error', '未知严重度按 error 处理（宁可多拦）')
  assert.equal(nested.stats.retailValue, 55.5)

  ok({ ...detailRaw, guid: 'p2', name: '平铺', shelfModelGuid: 's1', shelfModelName: '单面 900', bayCount: 3, status: 2, version: 4 })
  const flat = await getPlan('p2')
  assert.equal(flat.header.guid, 'p2', 'header 平铺在顶层时也能解析')
  assert.equal(flat.header.status, 2)
  assert.equal(flat.header.gapXMm, 10, '缺间距按系统默认 10')

  ok({})
  const blank = await getPlan('p3')
  assert.deepEqual(blank.items, [], '缺 items 给空数组')
  assert.deepEqual(blank.conflicts, [])
  assert.equal(blank.header.bayCount, 1)
  assert.equal(blank.stats.conflictCount, 0)

  // 9) 新建：请求体原样提交，返回详情
  ok({ ...detailRaw, header: { guid: 'p9', name: '新方案', shelfModelGuid: 's1', shelfModelName: '单面 900', bayCount: 2, gapXMm: 10, gapYMm: 10, status: 0, version: 1 } })
  const createdPlan = await createPlan({ name: '新方案', notes: null, shelfModelGuid: 's1', bayCount: 2, gapXMm: 10, gapYMm: 10 })
  assert.equal(last().init?.method, 'POST')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms')
  assert.deepEqual(bodyOf(last()), { name: '新方案', notes: null, shelfModelGuid: 's1', bayCount: 2, gapXMm: 10, gapYMm: 10 })
  assert.equal(createdPlan.header.guid, 'p9')

  // 10) 整体保存：PUT + rowVersion；校验：POST validate（不带 rowVersion）
  const saveBody = {
    name: '新方案', notes: null, shelfModelGuid: 's1', bayCount: 2, gapXMm: 10, gapYMm: 10, rowVersion: 'rv1',
    items: [{ guid: 'i1', bayIndex: 1, face: 0 as const, itemType: 1 as const, productCode: 'XD1', hookModelGuid: 'h1', holeCol: 5, holeRow: 3, perHookQty: null, sortOrder: 0 }],
  }
  ok({ ...detailRaw, header: { guid: 'p9', name: '新方案', shelfModelGuid: 's1', shelfModelName: '单面 900', bayCount: 2, gapXMm: 10, gapYMm: 10, status: 0, version: 2 } })
  const saved = await savePlan('p9', saveBody)
  assert.equal(last().init?.method, 'PUT')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms/p9')
  assert.deepEqual(bodyOf(last()), saveBody, '保存请求体原样提交（含 rowVersion）')
  assert.equal(saved.header.version, 2)

  ok({ conflicts: [{ itemGuid: 'i1', code: 'EDGE_GAP', severity: 'error', message: 'x' }], stats: { itemCount: 1, skuCount: 1, unitCount: 9, retailValue: 31.5, fillPercent: 1, conflictCount: 1 } })
  const { rowVersion: _unused, ...validateBody } = saveBody
  const validated = await validatePlan('p9', validateBody)
  assert.equal(last().init?.method, 'POST')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms/p9/validate')
  assert.equal('rowVersion' in bodyOf(last()), false, '校验请求不带 rowVersion')
  assert.equal(validated.conflicts[0].code, 'EDGE_GAP')
  assert.equal(validated.stats.conflictCount, 1)
  assert.equal(_unused, 'rv1')

  // 11) 复制 / 归档 / 恢复 / 删除
  ok({ ...detailRaw, header: { guid: 'p10', name: '副本', shelfModelGuid: 's1', shelfModelName: '单面 900', bayCount: 2, gapXMm: 10, gapYMm: 10, status: 0, version: 1 } })
  const duplicated = await duplicatePlan('p9', '副本')
  assert.equal(last().init?.method, 'POST')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms/p9/duplicate')
  assert.deepEqual(bodyOf(last()), { name: '副本' })
  assert.equal(duplicated.header.guid, 'p10')
  ok(null)
  await archivePlan('p9')
  assert.equal(last().init?.method, 'POST')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms/p9/archive')
  await restorePlan('p9')
  assert.equal(last().init?.method, 'POST')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms/p9/restore')
  await deletePlan('p9')
  assert.equal(last().init?.method, 'DELETE')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms/p9')

  // 12) 方案接口失败：错误码（含批 2 新增的）能被页面层识别
  for (const code of ['PLANOGRAM_STALE', 'PLANOGRAM_ARCHIVED', 'PLANOGRAM_NOT_DELETABLE']) {
    nextStatus = 400
    nextBody = { success: false, message: '服务端中文', errorCode: code }
    let thrown: unknown
    try {
      await savePlan('p9', saveBody)
    } catch (error) {
      thrown = error
    }
    const resolved = resolvePlanogramError(thrown)
    assert.ok(resolved.kind === 'error' && resolved.code === code, `${code} 被识别`)
  }
  nextStatus = 200

  // 13) 批 3（附录 C）：hasUnpublishedChanges 归一化——缺省按 false；后端给了就照用
  assert.equal(normalizePlanListItem({ guid: 'p', status: 0, publishedVersion: 2 }).hasUnpublishedChanges, false, '字段缺省 = false（不自行推断）')
  assert.equal(normalizePlanListItem({ guid: 'p', status: 0, publishedVersion: 2, hasUnpublishedChanges: true }).hasUnpublishedChanges, true)
  assert.equal(normalizePlanListItem({ guid: 'p', hasUnpublishedChanges: 'true' as unknown as boolean }).hasUnpublishedChanges, false, '不是布尔值一律当 false')
  ok({ items: [{ guid: 'p1', name: 'A', status: 0, version: 3, publishedVersion: 2, hasUnpublishedChanges: true }, { guid: 'p2', name: 'B', status: 1, version: 1, publishedVersion: 1 }], total: 2, page: 1, pageSize: 20 })
  const flagged = await listPlans({ page: 1, pageSize: 20 })
  assert.deepEqual(flagged.items.map((row) => row.hasUnpublishedChanges), [true, false], '列表行带上 hasUnpublishedChanges')
  ok({ header: { guid: 'p1', name: 'A', status: 0, version: 3, publishedVersion: 2, hasUnpublishedChanges: true }, rowVersion: 'rv', items: [], conflicts: [] })
  assert.equal((await getPlan('p1')).header.hasUnpublishedChanges, true, '详情 header 里也带上')

  // 14) 发布：POST planograms/{guid}/publish，请求体 { rowVersion }，返回详情
  ok({ header: { guid: 'p1', name: 'A', status: 1, version: 3, publishedVersion: 3, publishedAt: '2026-11-04T01:00:00Z' }, rowVersion: 'rv-2', items: [], conflicts: [] })
  const published = await publishPlan('p/1', 'rv-1')
  assert.equal(last().init?.method, 'POST')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms/p%2F1/publish', '路径里的 guid 做 URL 编码')
  assert.deepEqual(bodyOf(last()), { rowVersion: 'rv-1' })
  assert.equal(published.header.status, 1)
  assert.equal(published.header.publishedVersion, 3)
  assert.equal(published.rowVersion, 'rv-2', '返回新的 rowVersion')

  // 发布失败：错误码与 details（冲突列表）原样带出
  for (const code of ['PLANOGRAM_HAS_CONFLICTS', 'PLANOGRAM_EMPTY', 'PLANOGRAM_ALREADY_PUBLISHED', 'PLANOGRAM_STALE', 'PLANOGRAM_ARCHIVED']) {
    nextStatus = 400
    nextBody = { success: false, message: '服务端中文', errorCode: code, details: code === 'PLANOGRAM_HAS_CONFLICTS' ? [{ itemGuid: 'i1', code: 'OVERLAP', severity: 'error', message: 'm' }] : null }
    let thrown: unknown
    try {
      await publishPlan('p1', 'rv')
    } catch (error) {
      thrown = error
    }
    const resolved = resolvePlanogramError(thrown)
    assert.ok(resolved.kind === 'error' && resolved.code === code, `${code} 被识别`)
    if (code === 'PLANOGRAM_HAS_CONFLICTS') assert.ok(resolved.kind === 'error' && Array.isArray(resolved.details) && resolved.details.length === 1, '冲突列表在 details 里')
  }
  nextStatus = 200

  // 15) 版本列表：GET planograms/{guid}/versions，倒序（不依赖后端顺序）、数字兜底
  ok([{ version: 1, publishedAt: '2026-10-01T00:00:00Z', publishedBy: 'a', itemCount: 5, skuCount: 3, unitCount: 40 }, { version: '3', publishedAt: '2026-11-01T00:00:00Z', publishedBy: null, itemCount: '52', skuCount: 12, unitCount: 433 }, { version: 2 }])
  const versions = await listPlanVersions('p1')
  assert.equal(last().init?.method, 'GET')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms/p1/versions')
  assert.deepEqual(versions.map((row) => row.version), [3, 2, 1], '按版本倒序')
  assert.equal(versions[0].itemCount, 52, '字符串数字转成数字')
  assert.equal(versions[0].publishedBy, null)
  assert.equal(versions[1].publishedAt, null, '缺字段给 null / 0')
  assert.equal(versions[1].unitCount, 0)
  ok(null)
  assert.deepEqual(await listPlanVersions('p1'), [], '返回 null 也给空数组')

  // 16) 历史版本快照：GET planograms/{guid}/versions/{version}
  ok({
    version: 2, publishedAt: '2026-11-01T00:00:00Z', publishedBy: 'admin',
    plan: { guid: 'p1', name: '文具挂板', notes: null, bayCount: 2, gapXMm: 10, gapYMm: 10 },
    shelfModel: { guid: 's1', code: 'S', name: '货架', bayWidthMm: 900, totalHeightMm: 1200, holeCols: 34, holeRows: 41, holePitchXMm: 25, holePitchYMm: 25, firstHoleLeftMm: 25, firstHoleTopMm: 25, uprightWidthMm: 30, baseTopHeightMm: 150 },
    hookModels: [{ guid: 'h1', code: 'H', name: '双钩', hasPriceTagArm: true, armLengthMm: 200, dropMm: 50, tagWidthMm: 52, tagHeightMm: 26, holeSpanX: 2 }],
    products: [
      { productCode: 'A', itemNumber: 'XD1', barcode: '93', productName: '笔', englishName: 'Pen', productImage: 'https://img/x.png', retailPrice: '3.5', spec: { productCode: 'A', widthMm: 90, heightMm: 185, depthMm: 20, hookQtyOverrides: { h1: '9' } } },
      { productCode: 'BROKEN', productName: '没有规格的脏数据' },
    ],
    items: [{ guid: 'i1', bayIndex: 1, productCode: 'A', hookModelGuid: 'h1', holeCol: 2, holeRow: 3, perHookQty: 0, sortOrder: 0 }],
    stats: { itemCount: 1, skuCount: 1, unitCount: 9, retailValue: 31.5, fillPercent: 4, conflictCount: 0 },
  })
  const snapshot = await getPlanVersion('p1', 2)
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms/p1/versions/2')
  assert.equal(snapshot.version, 2)
  assert.equal(snapshot.plan.bayCount, 2)
  assert.equal(snapshot.shelfModel.holeCols, 34)
  assert.equal(snapshot.hookModels[0].holeSpanX, 2)
  assert.equal(snapshot.products.length, 1, '缺陈列参数的脏数据商品丢掉')
  assert.equal(snapshot.products[0].retailPrice, 3.5, '价格字符串转数字')
  assert.equal(snapshot.products[0].englishName, 'Pen')
  assert.equal(snapshot.products[0].spec.hookQtyOverrides.h1, 9, '规格覆盖值转数字')
  assert.equal(snapshot.products[0].spec.productCode, 'A')
  assert.equal(snapshot.items[0].perHookQty, null, '每钩挂数 0 视为「没填」')
  assert.equal(snapshot.stats.retailValue, 31.5)
  ok({})
  const emptySnapshot = await getPlanVersion('p1', 1)
  assert.deepEqual([emptySnapshot.products.length, emptySnapshot.items.length, emptySnapshot.hookModels.length], [0, 0, 0], '空对象也给出安全的空数组')
  assert.equal(emptySnapshot.plan.gapXMm, 10, '缺间距按默认 10')
  nextStatus = 400
  nextBody = { success: false, message: '快照已损坏', errorCode: 'PLANOGRAM_SNAPSHOT_INVALID' }
  let invalid: unknown
  try {
    await getPlanVersion('p1', 9)
  } catch (error) {
    invalid = error
  }
  assert.ok(resolvePlanogramError(invalid).kind === 'error' && (resolvePlanogramError(invalid) as { code: string }).code === 'PLANOGRAM_SNAPSHOT_INVALID', '快照损坏有明确错误码')
  nextStatus = 200

  // 17) 导出 JSON：GET planograms/{guid}/export；用编码而不是 guid
  ok({ formatVersion: 1, exportedAt: '2026-11-04T01:00:00Z', plan: { name: 'A', bayCount: 2, gapXMm: 10, gapYMm: 10 }, shelfModelCode: 'S', items: [{ bayIndex: 1, productCode: 'XD1', hookModelCode: 'H', holeCol: 2, holeRow: 3, perHookQty: null }] })
  const exported = await exportPlan('p1')
  assert.equal(last().init?.method, 'GET')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms/p1/export')
  assert.equal(exported.formatVersion, 1)
  assert.equal(exported.shelfModelCode, 'S')
  assert.equal(exported.items[0].hookModelCode, 'H')
  assert.equal('perHookQty' in exported.items[0], false, 'null 的每钩挂数不写进文件')

  // 18) 导入 JSON：POST planograms/import，请求体 = 导出文件 + 可选 name；返回新草稿详情
  ok({ header: { guid: 'new1', name: '改名后', status: 0, version: 1 }, rowVersion: 'rv', items: [], conflicts: [] })
  const imported = await importPlan({ ...exported, name: '改名后' })
  assert.equal(last().init?.method, 'POST')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/planograms/import')
  assert.equal(bodyOf(last()).name, '改名后')
  assert.equal(bodyOf(last()).shelfModelCode, 'S')
  assert.equal(imported.header.guid, 'new1')
  assert.equal(imported.header.version, 1)
  // 导入失败：缺失编码列表在 details 里
  nextStatus = 400
  nextBody = { success: false, message: '有 2 个编码找不到', errorCode: 'PLANOGRAM_IMPORT_UNRESOLVED', details: [{ kind: 'shelf', code: 'S' }, { kind: 'product', code: 'XD9' }] }
  let unresolvedError: unknown
  try {
    await importPlan(exported)
  } catch (error) {
    unresolvedError = error
  }
  const unresolved = resolvePlanogramError(unresolvedError)
  assert.ok(unresolved.kind === 'error' && unresolved.code === 'PLANOGRAM_IMPORT_UNRESOLVED' && Array.isArray(unresolved.details) && unresolved.details.length === 2, '缺失编码的 details 带出')
  nextStatus = 200

  // 19) 商品陈列参数列表的范围过滤（附录 D.1 + 商品等级）：数组用重复键，空条件不出现，等级只认 A/B/C/D/none
  ok({ items: [], total: 0, page: 1, pageSize: 20 })
  await listProductSpecs({ status: 'all', page: 1, pageSize: 20 })
  for (const name of ['warehouseCategoryGuids', 'localSupplierCode', 'supplierCategoryGuids', 'supplierCategoryUnassignedOnly', 'grades']) {
    assert.equal(last().url.searchParams.has(name), false, `没选条件时不带 ${name}`)
  }
  await listProductSpecs({
    keyword: ' 笔 ',
    status: 'set',
    warehouseCategoryGuids: ['wh-a', ' wh-b ', 'wh-a', ''],
    localSupplierCode: ' S1 ',
    supplierCategoryGuids: ['cat-1'],
    supplierCategoryUnassignedOnly: false,
    grades: ['A', 'none', 'Z' as never, 'A'],
    page: 2,
    pageSize: 50,
  })
  assert.deepEqual(last().url.searchParams.getAll('warehouseCategoryGuids'), ['wh-a', 'wh-b'], '仓库分类用重复键，去空去重')
  assert.equal(last().url.searchParams.get('localSupplierCode'), 'S1')
  assert.deepEqual(last().url.searchParams.getAll('supplierCategoryGuids'), ['cat-1'])
  assert.equal(last().url.searchParams.has('supplierCategoryUnassignedOnly'), false, 'false 不出现在查询串里')
  assert.deepEqual(last().url.searchParams.getAll('grades'), ['A', 'none'], '等级重复键：只认 A/B/C/D/none，去重')
  assert.equal(last().url.searchParams.get('keyword'), '笔')
  assert.equal(last().url.searchParams.get('status'), 'set')
  assert.equal(last().url.searchParams.get('page'), '2')
  assert.equal(last().url.search.includes('warehouseCategoryGuids=wh-a&warehouseCategoryGuids=wh-b'), true, '重复键形式 a=1&a=2')
  await listProductSpecs({ status: 'unset', localSupplierCode: 'S1', supplierCategoryUnassignedOnly: true, page: 1, pageSize: 20 })
  assert.equal(last().url.searchParams.get('supplierCategoryUnassignedOnly'), 'true')
  assert.deepEqual(productSpecScopeParams({}), {
    warehouseCategoryGuids: undefined, localSupplierCode: undefined, supplierCategoryGuids: undefined, supplierCategoryUnassignedOnly: undefined, grades: undefined,
  })

  // 20) 批量修改：POST product-specs/batch，请求体原样提交；响应数字兜底、跳过明细归一化
  const batchRequest = {
    target: { productCodes: ['XD0001', 'XD0002'] },
    patch: { widthMm: 70, unitWeightG: null },
    hookQtyOverrides: { mode: 'merge' as const, values: { h1: 12 } },
    createMissing: false,
    dryRun: true,
  }
  // 列表行新增字段：等级只认 A～D（大小写不敏感，其余为 null）、仓库分类路径按原样
  ok({
    items: [
      { productCode: 'G1', productName: 'a', retailPrice: 1, grade: 'b', warehouseCategoryPath: '百货 > 收纳 > 收纳盒', itemNumber: 'HB038-158' },
      { productCode: 'G2', productName: 'b', retailPrice: 1, grade: 'none', warehouseCategoryPath: null },
      { productCode: 'G3', productName: 'c', retailPrice: 1, grade: 'E' },
    ],
    total: 3, page: 1, pageSize: 20,
  })
  const graded = await listProductSpecs({ status: 'all', page: 1, pageSize: 20 })
  assert.deepEqual(graded.items.map((item) => item.grade), ['B', null, null], '等级字母归一化成大写，其余为 null（未分级）')
  assert.equal(graded.items[0].warehouseCategoryPath, '百货 > 收纳 > 收纳盒', '仓库分类路径按原样（分隔符「 > 」）')
  assert.equal(graded.items[1].warehouseCategoryPath, null)
  assert.equal(graded.items[0].itemNumber, 'HB038-158')

  ok({
    dryRun: true, matched: '120', willCreate: 30, willUpdate: 85, willStayUnchanged: 2, skipped: 3, changedTotal: '115',
    changedItems: [
      { productCode: 'XD1', itemNumber: 'HB038-158', productName: '笔', action: 'update', before: { widthMm: 120, notes: '旧备注', hookQtyOverrides: { h1: 6 } }, after: { widthMm: '70', notes: null, hookQtyOverrides: { h1: 6, h2: 12 } } },
      { productCode: 'XD2', itemNumber: 'HB038-159', action: 'create', before: null, after: { widthMm: 70, heightMm: 150, depthMm: 25, hangHoleOffsetMm: 10, hangHoleType: 2, source: 1, hookQtyOverrides: { h2: 12 } } },
      { productCode: 'XD3', action: 'delete', before: null, after: {} },
      { action: 'update', before: {}, after: {} },
    ],
    skippedItems: [
      { productCode: 'XD0003', itemNumber: 'HB038-158', productName: '笔', reason: 'NO_SPEC' },
      { productCode: '', reason: 'NO_SPEC' },
      { productCode: 'XD0004', reason: 'FUTURE_REASON' },
    ],
  })
  const preview = await batchUpdateProductSpecs(batchRequest)
  assert.equal(last().init?.method, 'POST')
  assert.equal(last().url.pathname, '/api/react/v1/planogram/product-specs/batch')
  assert.deepEqual(bodyOf(last()), batchRequest, '批量请求体原样提交（null 表示清空，保留）')
  assert.equal(preview.dryRun, true)
  assert.equal(preview.matched, 120, '字符串数字转成数字')
  assert.equal(preview.created + preview.updated + preview.unchanged, 0, 'dryRun 时执行数缺省为 0')
  assert.equal(preview.willCreate, 30)
  assert.equal(preview.skippedItems.length, 2, '缺货号的明细丢掉')
  assert.equal(preview.skippedItems[0].itemNumber, 'HB038-158')
  assert.equal(preview.skippedItems[1].reason, 'FUTURE_REASON', '不认识的原因原样保留')
  assert.equal(preview.changedTotal, 115, '改前改后明细的真实总数（字符串转数字）')
  assert.equal(preview.changedItems.length, 2, '动作不认识 / 缺货号的明细丢掉')
  assert.deepEqual(preview.changedItems[0].before, { widthMm: 120, notes: '旧备注', hookQtyOverrides: { h1: 6 } }, 'update 的 before 只含改变的字段')
  assert.deepEqual(preview.changedItems[0].after, { widthMm: 70, notes: null, hookQtyOverrides: { h1: 6, h2: 12 } }, 'after 里被清空的字段显式为 null')
  assert.equal(preview.changedItems[1].action, 'create')
  assert.equal(preview.changedItems[1].before, null, 'create 的 before 恒为 null')
  assert.equal(preview.changedItems[1].after.hangHoleType, 2)
  const filterBatch = {
    target: { filter: { keyword: '', status: 'all' as const, warehouseCategoryGuids: ['wh-a'], localSupplierCode: null, supplierCategoryGuids: [], supplierCategoryUnassignedOnly: false, grades: ['A' as const] } },
    patch: { hangHoleType: 1 as const },
    createMissing: true,
    dryRun: false,
  }
  ok({ dryRun: false, matched: 5, created: 1, updated: 3, unchanged: 1, skipped: 0 })
  const executed = await batchUpdateProductSpecs(filterBatch)
  assert.deepEqual(bodyOf(last()), filterBatch, '按筛选：请求体带 filter，不带 productCodes')
  assert.equal('hookQtyOverrides' in bodyOf(last()), false, '没勾选每钩挂数就没有这个键')
  assert.deepEqual([executed.created, executed.updated, executed.unchanged, executed.willCreate, executed.skippedItems.length], [1, 3, 1, 0, 0])
  assert.equal(executed.dryRun, false)
  assert.equal(normalizeBatchResult(null).matched, 0, '空响应全部兜底为 0')
  assert.deepEqual([normalizeBatchResult(null).changedTotal, normalizeBatchResult(null).changedItems.length], [0, 0], '没有改前改后明细时兜底为空')
  assert.deepEqual(normalizeBatchResult({ skippedItems: 'bad' as never }).skippedItems, [])

  // 批量失败：错误码带出（超限 / 目标为空 / 字段校验）
  for (const [code, details] of [['PLANOGRAM_BATCH_TOO_MANY', 2300], ['PLANOGRAM_BATCH_EMPTY', null], ['PLANOGRAM_BATCH_CONFLICT', null], ['PLANOGRAM_VALIDATION', ['foo']]] as const) {
    nextStatus = 400
    nextBody = { success: false, message: '服务端中文', errorCode: code, details }
    let batchError: unknown
    try {
      await batchUpdateProductSpecs(batchRequest)
    } catch (error) {
      batchError = error
    }
    const resolved = resolvePlanogramError(batchError)
    assert.ok(resolved.kind === 'error' && resolved.code === code, `${code} 带出错误码`)
    assert.equal(resolved.kind === 'error' && resolved.serverMessage, '服务端中文')
    if (code === 'PLANOGRAM_BATCH_TOO_MANY') assert.equal(resolved.kind === 'error' && resolved.details, 2300, '匹配数在 details 里')
    if (code !== 'PLANOGRAM_VALIDATION') assert.notEqual(resolved.kind === 'error' && resolved.key, 'planogram.errors.unknown', `${code} 有专门的文案键`)
  }
  nextStatus = 200


  console.log('planogramService.test: ok')
} finally {
  globalThis.fetch = originalFetch
}
