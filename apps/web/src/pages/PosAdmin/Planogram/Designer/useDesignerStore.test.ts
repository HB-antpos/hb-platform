import type { PlanogramDetailDto, ShelfModelDto } from '../../../../types/planogram'
import { HISTORY_LIMIT, createDesignerItem } from './designerLogic'
import { createDesignerStore, selectDirty } from './useDesignerStore'

// 设计器 store：撤销 / 重做 / dirty / 合并快照 / 只读守卫 / 保存（成功、失败、并发冲突、保存途中又改了）。
// 保存走真实的 planogramService + request，fetch 用内存替身。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

interface Captured { method: string; pathname: string; body: Record<string, any> | null }
const captured: Captured[] = []
let responder: (request: Captured) => Promise<Response> | Response = () => new Response('{}', { status: 200 })
const originalFetch = globalThis.fetch
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = new URL(String(input), 'http://localhost')
  if (!url.pathname.startsWith('/api/react/v1/planogram')) return new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } })
  const request: Captured = { method: (init?.method || 'GET').toUpperCase(), pathname: url.pathname, body: init?.body ? JSON.parse(String(init.body)) : null }
  captured.push(request)
  return responder(request)
}) as typeof fetch

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })

const shelf = {
  guid: 's1', code: 'G1', name: '单面 900', sideType: 1, bayWidthMm: 900, totalHeightMm: 1200, baseDepthMm: 300, baseTopHeightMm: 150, uprightWidthMm: 30,
  holePitchXMm: 25, holePitchYMm: 25, holeDiameterMm: 6, firstHoleLeftMm: 25, firstHoleTopMm: 25, holeCols: 33, holeRows: 41, rowNumberingFrom: 1, isActive: true, referencedPlanCount: 1,
} as unknown as ShelfModelDto

function makeDetail(overrides: Partial<PlanogramDetailDto> = {}): PlanogramDetailDto {
  return {
    header: { guid: 'p1', name: '文具挂板', notes: null, shelfModelGuid: 's1', shelfModelName: '单面 900', bayCount: 2, gapXMm: 10, gapYMm: 10, status: 0, version: 1, itemCount: 2, skuCount: 1, unitCount: 18, fillPercent: 1, conflictCount: 0, hasUnpublishedChanges: false, publishedVersion: null, publishedAt: null, updatedAt: '2026-11-04T00:00:00Z', updatedBy: 'admin' },
    rowVersion: 'rv-1',
    items: [
      { guid: 'a', bayIndex: 1, face: 0, itemType: 1, productCode: 'PEN', hookModelGuid: 'H', holeCol: 3, holeRow: 2, perHookQty: null, sortOrder: 0 },
      { guid: 'b', bayIndex: 1, face: 0, itemType: 1, productCode: 'PEN', hookModelGuid: 'H', holeCol: 9, holeRow: 2, perHookQty: null, sortOrder: 1, specChanged: true },
    ],
    shelfModel: shelf,
    hookModels: [],
    productSpecs: [],
    conflicts: [],
    stats: { itemCount: 2, skuCount: 1, unitCount: 18, retailValue: 63, fillPercent: 1, conflictCount: 0 },
    ...overrides,
  }
}

const savedResponse = (version: number, rowVersion: string, extra: Partial<PlanogramDetailDto> = {}) =>
  json(200, { success: true, data: { ...makeDetail(), rowVersion, header: { ...makeDetail().header, version }, ...extra } })

try {
  // ---------- 初始状态 ----------
  const store = createDesignerStore(makeDetail())
  let state = store.getState()
  assertEqual(state.doc.items.length, 2, '载入 2 条明细')
  assertEqual(selectDirty(state), false, '刚载入不是脏的')
  assertEqual(state.past.length, 0, '没有撤销记录')
  assertEqual(state.doc.items[1].specChanged, true, '保留「参数已变」标记')
  assertEqual(state.serverResult?.fromSave, false, '载入时的服务端结果标记为非保存')
  assertEqual(state.shelfModels.length, 1, '当前方案的货架型号进了下拉目录')
  assertEqual(state.stale, false, '没有并发冲突')

  // ---------- 增删改 + 撤销 / 重做 / dirty ----------
  const added = createDesignerItem({ guid: 'c', productCode: 'PEN', hookModelGuid: 'H', bayIndex: 1, holeCol: 15, holeRow: 2 })
  state.addItem(added)
  state = store.getState()
  assertEqual(state.doc.items.length, 3, '添加一条')
  assertEqual(state.selectedGuid, 'c', '添加后选中新条目')
  assertEqual(selectDirty(state), true, '有改动')
  state.undo()
  state = store.getState()
  assertEqual(state.doc.items.length, 2, '撤销添加')
  assertEqual(selectDirty(state), false, '撤销回保存点就不再算有改动')
  assertEqual(state.selectedGuid, null, '被撤销的条目不再选中')
  state.redo()
  assertEqual(store.getState().doc.items.length, 3, '重做添加')
  assertEqual(selectDirty(store.getState()), true, '重做后又是脏的')
  store.getState().removeItem('c')
  assertEqual(store.getState().doc.items.length, 2, '删除')
  assertEqual(store.getState().selectedGuid, null, '删除选中项后取消选中')
  assertEqual(store.getState().future.length, 0, '新操作清空重做栈')
  store.getState().undo(); store.getState().undo(); store.getState().undo()
  assertEqual(store.getState().doc.items.length, 2, '连续撤销到头：回到载入时的 2 条')
  assertEqual(selectDirty(store.getState()), false, '撤销到头不脏')
  assertEqual(store.getState().past.length, 0, '撤销栈清空')
  store.getState().undo()
  assertEqual(store.getState().doc.items.length, 2, '没有可撤销时无操作')

  // ---------- 撤销栈上限 50 ----------
  const capped = createDesignerStore(makeDetail())
  for (let i = 0; i < 70; i += 1) capped.getState().moveItem('a', { bayIndex: 1, holeCol: 3 + ((i + 1) % 2 === 0 ? 0 : 5), holeRow: 2 + (i % 7) })
  assert(capped.getState().past.length <= HISTORY_LIMIT, `撤销栈不超过 ${HISTORY_LIMIT}：${capped.getState().past.length}`)
  assertEqual(capped.getState().past.length, 50, '操作超过 50 次，栈恰好保持 50')

  // ---------- 连续同类编辑合并快照 ----------
  const merged = createDesignerStore(makeDetail())
  merged.getState().setHeader({ name: '文' })
  merged.getState().setHeader({ name: '文具' })
  merged.getState().setHeader({ name: '文具挂' })
  assertEqual(merged.getState().past.length, 1, '连续敲名称只占一个撤销快照')
  merged.getState().setHeader({ gapXMm: 12 })
  merged.getState().setHeader({ gapXMm: 14 })
  assertEqual(merged.getState().past.length, 2, '间距与名称是不同键，各自一个快照，且连续改间距合并')
  merged.getState().undo()
  assertEqual(merged.getState().doc.gapXMm, 10, '撤销一次回到改间距之前')
  assertEqual(merged.getState().doc.name, '文具挂', '名称改动还在')
  merged.getState().undo()
  assertEqual(merged.getState().doc.name, '文具挂板', '再撤销回到原名')

  // ---------- 头字段范围与「没变化不入栈」 ----------
  const header = createDesignerStore(makeDetail())
  header.getState().setHeader({ bayCount: 99 })
  assertEqual(header.getState().doc.bayCount, 12, '节数最多 12')
  header.getState().setHeader({ bayCount: 0 })
  assertEqual(header.getState().doc.bayCount, 1, '节数最少 1')
  header.getState().setHeader({ gapXMm: -5, gapYMm: 9999 })
  assertEqual(header.getState().doc.gapXMm, 0, '间距下限 0')
  assertEqual(header.getState().doc.gapYMm, 200, '间距上限 200')
  const before = header.getState().past.length
  header.getState().setHeader({ bayCount: 1 })
  assertEqual(header.getState().past.length, before, '值没变不产生快照')

  // ---------- 只读守卫 ----------
  const readOnly = createDesignerStore(makeDetail(), { readOnly: true })
  readOnly.getState().addItem(added)
  readOnly.getState().setHeader({ name: 'x' })
  readOnly.getState().removeItem('a')
  assertEqual(readOnly.getState().doc.items.length, 2, '只读时拒绝增删改')
  assertEqual(readOnly.getState().doc.name, '文具挂板', '只读时拒绝改头字段')
  const skipped = await readOnly.getState().save()
  assertEqual(skipped.kind, 'skipped', '只读时保存被跳过')
  assertEqual(captured.length, 0, '只读时没有发请求')
  readOnly.getState().setReadOnly(false)
  readOnly.getState().addItem(added)
  assertEqual(readOnly.getState().doc.items.length, 3, '解除只读后可以编辑')

  // ---------- 保存前置校验 ----------
  const blank = createDesignerStore(makeDetail())
  blank.getState().setHeader({ name: '   ' })
  assertEqual((await blank.getState().save()).kind, 'skipped', '名称为空不能保存')
  const noShelf = createDesignerStore(makeDetail())
  noShelf.getState().setHeader({ shelfModelGuid: 'gone' })
  const noShelfOutcome = await noShelf.getState().save()
  assert(noShelfOutcome.kind === 'skipped' && noShelfOutcome.reason === 'noShelf', '货架型号不在目录里不能保存')
  assertEqual(captured.length, 0, '前置校验失败不发请求')

  // ---------- 保存成功 ----------
  responder = () => savedResponse(2, 'rv-2', { items: makeDetail().items.map((entry) => ({ ...entry, specChanged: false })), conflicts: [{ itemGuid: 'a', otherItemGuid: null, code: 'EDGE_GAP', severity: 'error', message: 'x' }], stats: { itemCount: 3, skuCount: 1, unitCount: 27, retailValue: 94.5, fillPercent: 1, conflictCount: 1 } })
  const ok = createDesignerStore(makeDetail())
  ok.getState().addItem(added)
  const sent = ok.getState().doc
  const outcome = await ok.getState().save([])
  assert(outcome.kind === 'ok', '保存成功')
  assertEqual(outcome.differs, true, '本地没有冲突、服务端有一条：提示不一致')
  assertEqual(captured.length, 1, '发了一次保存请求')
  assertEqual(captured[0].method, 'PUT', '整体保存用 PUT')
  assertEqual(captured[0].pathname, '/api/react/v1/planogram/planograms/p1', '路径')
  assertEqual(captured[0].body?.rowVersion, 'rv-1', '保存带上载入时的 rowVersion')
  assertEqual(captured[0].body?.items.length, 3, '明细整体提交')
  assertEqual(captured[0].body?.items.map((entry: any) => entry.sortOrder).join(','), '0,1,2', 'sortOrder 按顺序')
  state = ok.getState()
  assertEqual(state.rowVersion, 'rv-2', 'rowVersion 更新为服务端新值')
  assertEqual(state.header.version, 2, '头信息取服务端返回')
  assertEqual(selectDirty(state), false, '保存后不脏')
  assertEqual(state.serverResult?.fromSave, true, '保存后冲突以服务端返回为准')
  assertEqual(state.serverResult?.doc, state.doc, '服务端结果对应当前文档')
  assertEqual(state.doc.items.some((entry) => entry.specChanged), false, '服务端回填：参数已变标记清零')
  assertEqual(state.saveStatus, 'idle', '保存状态复位')
  assert(sent !== state.doc, '回填标记后 doc 是新对象')
  ok.getState().addItem(createDesignerItem({ guid: 'd', productCode: 'PEN', hookModelGuid: 'H', bayIndex: 1, holeCol: 20, holeRow: 2 }))
  assertEqual(selectDirty(ok.getState()), true, '保存后继续编辑又是脏的')
  ok.getState().undo()
  assertEqual(selectDirty(ok.getState()), false, '撤销回保存点不脏')

  // ---------- 保存途中又改了：保存点推进，但当前 doc 仍是脏的 ----------
  captured.length = 0
  let release: (() => void) | null = null
  responder = () => new Promise<Response>((resolve) => { release = () => resolve(savedResponse(3, 'rv-3')) })
  const racing = createDesignerStore(makeDetail())
  racing.getState().moveItem('a', { bayIndex: 1, holeCol: 4, holeRow: 2 })
  const pendingSave = racing.getState().save()
  assertEqual(racing.getState().saveStatus, 'saving', '保存中')
  assertEqual((await racing.getState().save()).kind, 'skipped', '保存中再点保存被跳过')
  racing.getState().moveItem('a', { bayIndex: 1, holeCol: 5, holeRow: 2 })
  release!()
  await pendingSave
  assertEqual(racing.getState().doc.items[0].holeCol, 5, '保存期间的新改动保留在 doc 里')
  assertEqual(selectDirty(racing.getState()), true, '保存期间又改了：仍然有未保存改动')
  assertEqual(racing.getState().savedDoc.items[0].holeCol, 4, '保存点是刚发出去的那一版')
  assertEqual(racing.getState().serverResult?.doc === racing.getState().doc, false, '服务端结果不再对应当前文档（回到本地复算）')

  // ---------- 保存失败：本地改动不丢 ----------
  responder = () => json(400, { success: false, message: '填写内容不合法', errorCode: 'PLANOGRAM_VALIDATION', details: [{ field: 'items[0].holeCol', message: '孔位超出范围' }] })
  const failing = createDesignerStore(makeDetail())
  failing.getState().addItem(added)
  const docBefore = failing.getState().doc
  const failed = await failing.getState().save()
  assertEqual(failed.kind, 'error', '校验失败')
  assertEqual(failing.getState().doc, docBefore, '失败后 doc 原样保留')
  assertEqual(selectDirty(failing.getState()), true, '失败后仍然有未保存改动')
  assertEqual(failing.getState().saveStatus, 'idle', '失败后保存状态复位，可以重试')
  assertEqual(failing.getState().rowVersion, 'rv-1', '失败后 rowVersion 不变')
  responder = () => json(500, { success: false, message: '服务器错误' })
  assertEqual((await failing.getState().save()).kind, 'error', '服务器错误')
  assertEqual(failing.getState().doc, docBefore, '服务器错误后 doc 也保留')

  // ---------- 并发冲突 ----------
  responder = () => json(400, { success: false, message: '方案已被他人更新', errorCode: 'PLANOGRAM_STALE' })
  const stale = createDesignerStore(makeDetail())
  stale.getState().addItem(added)
  const staleOutcome = await stale.getState().save()
  assertEqual(staleOutcome.kind, 'stale', 'PLANOGRAM_STALE 单独成类')
  assertEqual(stale.getState().stale, true, '标记方案已被他人更新')
  assertEqual(selectDirty(stale.getState()), true, '并发冲突后本地改动保留')
  stale.getState().dismissStale()
  assertEqual(stale.getState().stale, false, '「稍后处理」关闭提示')

  // ---------- 已归档：服务端拒绝保存 ----------
  responder = () => json(400, { success: false, message: '方案已归档', errorCode: 'PLANOGRAM_ARCHIVED' })
  const archived = createDesignerStore(makeDetail())
  archived.getState().addItem(added)
  const archivedOutcome = await archived.getState().save()
  assert(archivedOutcome.kind === 'error' && archivedOutcome.resolved.kind === 'error' && archivedOutcome.resolved.code === 'PLANOGRAM_ARCHIVED', 'PLANOGRAM_ARCHIVED 被识别')
  assertEqual(selectDirty(archived.getState()), true, '归档拒绝后本地改动保留')

  // ---------- 规格 / 货架目录合并 ----------
  const registry = createDesignerStore(makeDetail())
  registry.getState().registerSpecs([{ productCode: 'X', itemNumber: 'X', barcode: 'x', productName: 'x', retailPrice: 1, hasSpec: true, referencedPlanCount: 0, spec: null }])
  assertEqual(Object.keys(registry.getState().specs).join(','), 'X', '拖入过的商品参数登记进 store')
  registry.getState().setShelfModels([{ ...shelf, guid: 's2', name: '另一个' }])
  assertEqual(registry.getState().shelfModels.map((model) => model.guid).sort().join(','), 's1,s2', '货架目录合并：启用列表 + 当前方案在用的')

  // ---------- 批 3：发布成功只刷新服务端信息，不动编辑内容；发布时发现并发冲突标记 stale ----------
  const publishable = createDesignerStore(makeDetail())
  const beforeDoc = publishable.getState().doc
  publishable.getState().applyServerDetail(makeDetail({
    header: { ...makeDetail().header, status: 1, version: 1, publishedVersion: 1, publishedAt: '2026-11-04T01:00:00Z' },
    rowVersion: 'rv-published',
    conflicts: [{ itemGuid: 'a', code: 'OVERLOAD', severity: 'warn', message: 'm' }],
    stats: { itemCount: 2, skuCount: 1, unitCount: 18, retailValue: 63, fillPercent: 1, conflictCount: 0 },
  }))
  assertEqual(publishable.getState().header.status, 1, '头状态更新为已发布')
  assertEqual(publishable.getState().header.publishedVersion, 1, '已发布版本号')
  assertEqual(publishable.getState().rowVersion, 'rv-published', 'rowVersion 换成发布后的新值（之后保存才不会被当成过期）')
  assertEqual(publishable.getState().doc, beforeDoc, '编辑内容引用不变')
  assertEqual(selectDirty(publishable.getState()), false, '发布不会把设计器弄成脏的')
  assertEqual(publishable.getState().serverResult?.doc, publishable.getState().savedDoc, '服务端结果对应已保存的那一版')
  assertEqual(publishable.getState().serverResult?.fromSave, true, '界面以服务端返回为准')
  assertEqual(publishable.getState().serverResult?.conflicts.length, 1, '冲突列表刷新')
  publishable.getState().markStale()
  assertEqual(publishable.getState().stale, true, '发布遇到 PLANOGRAM_STALE：标记方案已被他人更新')
  publishable.getState().dismissStale()
  assertEqual(publishable.getState().stale, false, '可以关闭提示继续看当前内容')

  console.log('useDesignerStore.test: ok')
} finally {
  globalThis.fetch = originalFetch
}
