import assert from 'node:assert/strict'
import type { ProductSpecBatchFilter, ProductSpecBatchResultDto } from '../../../../types/planogram'
import type { ChangeTextLookup } from './batchLogic'
import {
  BATCH_MAX_TARGETS,
  batchConfirmBlock,
  batchErrorKey,
  batchFieldFromWireName,
  batchRequestSignature,
  batchTargetCount,
  batchTargetFromFilter,
  batchTargetFromSelection,
  batchTargetHasNoUnset,
  batchTargetTooMany,
  buildBatchHookQty,
  buildBatchPatch,
  buildBatchRequest,
  canStartBatchPreview,
  countBatchChanges,
  createBatchEditState,
  EMPTY_BATCH_SELECTION,
  extractBatchMatchedCount,
  changeLabelKey,
  changeParts,
  formatChangeLines,
  formatChangeSide,
  IDLE_BATCH_PREVIEW,
  isBatchPreviewObsolete,
  isBatchPreviewStale,
  resolveBatchError,
  selectionAdd,
  selectionCounts,
  selectionKeys,
  selectionRemove,
  selectionSyncRows,
  setBatchField,
  skippedItemsTruncated,
  skipReasonKey,
  usableBatchPreview,
  validateBatchEdit,
  type BatchEditState,
  type BatchPreviewState,
  type BatchTargetSpec,
} from './batchLogic'

// 批量修改的纯逻辑（附录 D.2 / D.3）：跨页勾选与 2000 上限、补丁构造、本地校验、预览失效规则、失败提示、跳过原因。

const rows = (prefix: string, count: number, hasSpec = true) =>
  Array.from({ length: count }, (_, index) => ({ productCode: `${prefix}${index}`, hasSpec }))

// ---------- 跨页勾选 ----------
{
  const page1 = selectionAdd(EMPTY_BATCH_SELECTION, [{ productCode: 'A', hasSpec: true }, { productCode: 'B', hasSpec: false }])
  assert.equal(page1.added, 2)
  assert.equal(page1.rejected, 0)
  const page2 = selectionAdd(page1.selection, [{ productCode: 'C', hasSpec: false }, { productCode: 'A', hasSpec: true }])
  assert.deepEqual(selectionKeys(page2.selection), ['A', 'B', 'C'], '翻页后再勾选：之前的勾选保留，且保持勾选顺序')
  assert.equal(page2.added, 1, '已在集合里的不重复计数')
  assert.deepEqual(selectionCounts(page2.selection), { total: 3, setCount: 1, unsetCount: 2 }, '跨页汇总已设 / 未设')

  const same = selectionAdd(page2.selection, [{ productCode: 'A', hasSpec: true }])
  assert.equal(same.selection, page2.selection, '没有变化返回原对象')

  // 取消勾选只影响指定货号（翻页 / 筛选不会触发这个函数，所以不丢已选）
  const removed = selectionRemove(page2.selection, ['B', 'ZZ'])
  assert.deepEqual(selectionKeys(removed), ['A', 'C'])
  assert.equal(selectionRemove(removed, ['ZZ']), removed, '取消不存在的货号返回原对象')

  // 同步当前页的「是否已设」标记：在抽屉里给已勾选的 B 补了参数
  const synced = selectionSyncRows(page2.selection, [{ productCode: 'B', hasSpec: true }, { productCode: 'NOT-SELECTED', hasSpec: true }])
  assert.deepEqual(selectionCounts(synced), { total: 3, setCount: 2, unsetCount: 1 })
  assert.equal(synced.has('NOT-SELECTED'), false, '同步不会把没勾选的行加进来')
  assert.equal(selectionSyncRows(synced, [{ productCode: 'B', hasSpec: true }]), synced, '无变化返回原对象')
}

// 2000 上限：超出的行不加入并计数，界面据此提示
{
  const first = selectionAdd(EMPTY_BATCH_SELECTION, rows('P', 1990))
  assert.equal(first.added, 1990)
  const second = selectionAdd(first.selection, rows('Q', 100))
  assert.equal(second.selection.size, BATCH_MAX_TARGETS, '最多 2000 个')
  assert.equal(second.added, 10)
  assert.equal(second.rejected, 90, '放不下的行数用于提示')
  const third = selectionAdd(second.selection, [{ productCode: 'P0', hasSpec: true }, { productCode: 'X', hasSpec: true }])
  assert.equal(third.rejected, 1, '已满时新货号被拒，已在集合里的不算被拒')
  assert.equal(third.selection, second.selection)
  assert.equal(BATCH_MAX_TARGETS, 2000)
}

// ---------- 补丁：只含被勾选的字段 ----------
{
  const state = createBatchEditState()
  assert.deepEqual(buildBatchPatch(state), {}, '什么都没勾选：空补丁')
  assert.equal(countBatchChanges(state), 0)

  let edited: BatchEditState = setBatchField(state, 'width', { enabled: true, value: 70 })
  edited = setBatchField(edited, 'height', { enabled: true, value: 150 })
  edited = setBatchField(edited, 'depth', { value: 25 }) // 填了值但没勾选 → 不进补丁
  edited = setBatchField(edited, 'hangType', { enabled: true, value: 1 })
  edited = setBatchField(edited, 'source', { enabled: true, value: 3 })
  assert.deepEqual(buildBatchPatch(edited), { widthMm: 70, heightMm: 150, hangHoleType: 1, source: 3 }, '没勾选的字段即使有值也不进补丁')
  assert.equal(countBatchChanges(edited), 4)

  // 可空字段：勾选「清空」传 null
  const cleared = setBatchField(setBatchField(setBatchField(state, 'weight', { enabled: true, clear: true }), 'defaultHook', { enabled: true, clear: true }), 'notes', { enabled: true, clear: true })
  assert.deepEqual(buildBatchPatch(cleared), { unitWeightG: null, defaultHookGuid: null, notes: null }, '清空 = null（键存在）')

  const filled = setBatchField(setBatchField(setBatchField(state, 'weight', { enabled: true, value: 95 }), 'defaultHook', { enabled: true, value: 'hook-1' }), 'notes', { enabled: true, value: '  新品陈列  ' })
  assert.deepEqual(buildBatchPatch(filled), { unitWeightG: 95, defaultHookGuid: 'hook-1', notes: '新品陈列' }, '备注去首尾空白')

  // 非空字段不会出现 null
  const missing = setBatchField(state, 'width', { enabled: true, value: null })
  assert.deepEqual(buildBatchPatch(missing), {}, '勾选了但没填：不进补丁（由校验报错），绝不会发 null')
}

// ---------- 每钩挂数覆盖 ----------
{
  const base = setBatchField(createBatchEditState(), 'hookQty', { enabled: true })
  assert.equal(buildBatchHookQty(createBatchEditState()), undefined, '没勾选：不动')

  const merge = setBatchField(base, 'hookQty', { mode: 'merge', values: { h2: 8, h1: 12, h3: null, h4: 0, h5: 2.5 } })
  assert.deepEqual(buildBatchHookQty(merge), { mode: 'merge', values: { h1: 12, h2: 8 } }, '只取 ≥1 的整数，按 guid 排序')
  const replace = setBatchField(merge, 'hookQty', { mode: 'replace' })
  assert.deepEqual(buildBatchHookQty(replace), { mode: 'replace', values: { h1: 12, h2: 8 } })
  const clearAll = setBatchField(merge, 'hookQty', { mode: 'clearAll' })
  assert.deepEqual(buildBatchHookQty(clearAll), { mode: 'replace', values: {} }, '清空全部覆盖 = replace + 空对象')

  assert.deepEqual(buildBatchHookQty(merge, { hookGuids: new Set(['h1']) }), { mode: 'merge', values: { h1: 12 } }, '不可用的挂钩型号被忽略')
  const none = setBatchField(base, 'hookQty', { mode: 'replace', values: { h1: null } })
  assert.equal(buildBatchHookQty(none), undefined, 'replace 但没有有效值：不会悄悄变成清空')
  const over = setBatchField(base, 'hookQty', { mode: 'merge', values: { h1: 10000 } })
  assert.equal(buildBatchHookQty(over), undefined, '超过 9999 的值不进请求')
}

// ---------- 校验 ----------
{
  const state = createBatchEditState()
  let v = validateBatchEdit(state)
  assert.equal(v.hasChanges, false)
  assert.equal(v.valid, false, '什么都没勾选不能预览')

  // 勾选但没填
  v = validateBatchEdit(setBatchField(state, 'width', { enabled: true }))
  assert.equal(v.errors.width?.code, 'required')
  assert.equal(v.valid, false)

  // 范围 / 整数
  for (const [value, ok] of [[0, false], [1, true], [10000, true], [10001, false], [70.5, false], [-3, false]] as const) {
    v = validateBatchEdit(setBatchField(state, 'height', { enabled: true, value }))
    assert.equal(v.errors.height === undefined, ok, `高 ${value} → ${ok ? '合法' : '不合法'}`)
  }
  assert.deepEqual(validateBatchEdit(setBatchField(state, 'depth', { enabled: true, value: 0 })).errors.depth, { code: 'rangeInt', params: { min: 1, max: 10000 } })

  // 吊孔偏移 ≥ 0 的整数
  assert.equal(validateBatchEdit(setBatchField(state, 'hangOffset', { enabled: true, value: 0 })).valid, true, '偏移 0 合法')
  assert.equal(validateBatchEdit(setBatchField(state, 'hangOffset', { enabled: true, value: -1 })).errors.hangOffset?.code, 'nonNegativeInt')
  assert.equal(validateBatchEdit(setBatchField(state, 'hangOffset', { enabled: true, value: 3.5 })).errors.hangOffset?.code, 'nonNegativeInt')

  // 单件重 1～1000000；勾选清空时不校验取值
  assert.equal(validateBatchEdit(setBatchField(state, 'weight', { enabled: true, value: 0 })).errors.weight?.code, 'rangeInt')
  assert.equal(validateBatchEdit(setBatchField(state, 'weight', { enabled: true, value: 1_000_001 })).errors.weight?.code, 'rangeInt')
  assert.equal(validateBatchEdit(setBatchField(state, 'weight', { enabled: true, clear: true, value: -5 })).valid, true, '清空单件重不看取值')

  // 默认挂钩
  assert.equal(validateBatchEdit(setBatchField(state, 'defaultHook', { enabled: true })).errors.defaultHook?.code, 'required')
  assert.equal(validateBatchEdit(setBatchField(state, 'defaultHook', { enabled: true, value: 'gone' }), { hookGuids: new Set(['h1']) }).errors.defaultHook?.code, 'unknownHook')
  assert.equal(validateBatchEdit(setBatchField(state, 'defaultHook', { enabled: true, clear: true })).valid, true, '清空默认挂钩合法')

  // 备注
  assert.equal(validateBatchEdit(setBatchField(state, 'notes', { enabled: true, value: '   ' })).errors.notes?.code, 'required', '备注全空白：要么填写要么勾选清空')
  assert.equal(validateBatchEdit(setBatchField(state, 'notes', { enabled: true, value: 'x'.repeat(501) })).errors.notes?.code, 'notesTooLong')
  assert.equal(validateBatchEdit(setBatchField(state, 'notes', { enabled: true, value: 'x'.repeat(500) })).valid, true)

  // 每钩挂数
  const qty = setBatchField(state, 'hookQty', { enabled: true, mode: 'merge', values: {} })
  assert.equal(validateBatchEdit(qty).errors.hookQty?.code, 'qtyNone', '合并 / 替换至少填一个')
  assert.equal(validateBatchEdit(setBatchField(qty, 'hookQty', { values: { h1: 0 } })).errors.hookQty?.code, 'qtyInvalid')
  assert.equal(validateBatchEdit(setBatchField(qty, 'hookQty', { values: { h1: 2.5 } })).errors.hookQty?.code, 'qtyInvalid')
  assert.equal(validateBatchEdit(setBatchField(qty, 'hookQty', { values: { h1: 12 } })).valid, true)
  assert.equal(validateBatchEdit(setBatchField(qty, 'hookQty', { mode: 'clearAll' })).valid, true, '清空全部覆盖不需要填值')
  assert.equal(validateBatchEdit(setBatchField(qty, 'hookQty', { values: { gone: 5 } }), { hookGuids: new Set(['h1']) }).errors.hookQty?.code, 'qtyNone', '只填了不可用型号 = 没填')

  // 同时改偏移与高：偏移不得大于高（吊孔类型明确改成非「无」时）
  let combo = setBatchField(setBatchField(state, 'height', { enabled: true, value: 100 }), 'hangOffset', { enabled: true, value: 120 })
  assert.equal(validateBatchEdit(combo).errors.hangOffset, undefined, '没明确改吊孔类型：取决于商品原类型，交给后端逐商品跳过')
  combo = setBatchField(combo, 'hangType', { enabled: true, value: 2 })
  assert.equal(validateBatchEdit(combo).errors.hangOffset?.code, 'offsetExceedsHeight')
  assert.equal(validateBatchEdit(setBatchField(combo, 'hangType', { value: 0 })).errors.hangOffset, undefined, '吊孔类型改成「无」不限制偏移')
  assert.equal(validateBatchEdit(setBatchField(combo, 'hangOffset', { value: 100 })).valid, true, '偏移等于高合法')

  // 创建未设参数的商品：宽高厚必须同时勾选并填齐
  const create = { ...state, createMissing: true }
  assert.equal(validateBatchEdit(setBatchField(create, 'width', { enabled: true, value: 70 })).errors.createMissing?.code, 'needDimensions')
  const withDims = setBatchField(setBatchField(setBatchField(create, 'width', { enabled: true, value: 70 }), 'height', { enabled: true, value: 150 }), 'depth', { enabled: true, value: 25 })
  assert.equal(validateBatchEdit(withDims).valid, true)
  assert.equal(validateBatchEdit(setBatchField(withDims, 'depth', { value: null })).errors.createMissing?.code, 'needDimensions', '厚没填')
  assert.equal(validateBatchEdit(setBatchField(withDims, 'depth', { enabled: false })).errors.createMissing?.code, 'needDimensions', '厚没勾选')

  // 错误码 → 文案键
  assert.equal(batchErrorKey('needDimensions'), 'planogram.spec.batch.error.needDimensions')
}

// ---------- 目标与请求体 ----------
const filter: ProductSpecBatchFilter = {
  keyword: '', status: 'all', warehouseCategoryGuids: ['wh-a'], localSupplierCode: null, supplierCategoryGuids: [], supplierCategoryUnassignedOnly: false, grades: ['A', 'none'],
}
{
  const selection = selectionAdd(EMPTY_BATCH_SELECTION, [{ productCode: 'B', hasSpec: false }, { productCode: 'A', hasSpec: true }]).selection
  const selected = batchTargetFromSelection(selection)
  assert.deepEqual(selected, { kind: 'selected', productCodes: ['B', 'A'], setCount: 1, unsetCount: 1 })
  assert.equal(batchTargetCount(selected), 2)

  let state = setBatchField(createBatchEditState(), 'width', { enabled: true, value: 70 })
  state = setBatchField(state, 'weight', { enabled: true, clear: true })
  const request = buildBatchRequest(selected, state, true)
  assert.deepEqual(request, {
    target: { productCodes: ['B', 'A'] },
    patch: { widthMm: 70, unitWeightG: null },
    createMissing: false,
    dryRun: true,
  }, '勾选目标：只带 productCodes；没勾选每钩挂数时不出现 hookQtyOverrides')
  assert.equal('hookQtyOverrides' in request, false)

  const filterTarget = batchTargetFromFilter(filter, 120)
  assert.deepEqual(buildBatchRequest(filterTarget, state, false).target, { filter }, '按当前筛选：只带 filter，不同时带 productCodes')
  assert.equal(buildBatchRequest(filterTarget, state, false).dryRun, false)

  const withQty = setBatchField(state, 'hookQty', { enabled: true, mode: 'merge', values: { h1: 12 } })
  assert.deepEqual(buildBatchRequest(selected, withQty, false).hookQtyOverrides, { mode: 'merge', values: { h1: 12 } })

  // 按筛选的已设 / 未设数
  assert.deepEqual(
    [batchTargetFromFilter({ ...filter, status: 'set' }, 40).setCount, batchTargetFromFilter({ ...filter, status: 'set' }, 40).unsetCount],
    [40, 0], '状态=已设：不用再查',
  )
  assert.deepEqual(
    [batchTargetFromFilter({ ...filter, status: 'unset' }, 40).setCount, batchTargetFromFilter({ ...filter, status: 'unset' }, 40).unsetCount],
    [0, 40],
  )
  const allKnown = batchTargetFromFilter(filter, 120, 85)
  assert.deepEqual([allKnown.setCount, allKnown.unsetCount], [85, 35], '状态=全部：另查到已设数后推出未设数')
  const allUnknown = batchTargetFromFilter(filter, 120)
  assert.deepEqual([allUnknown.setCount, allUnknown.unsetCount], [null, null], '还没查到时为 null，界面显示「—」')
  assert.equal(batchTargetFromFilter(filter, 120, 500).setCount, null, '不合理的已设数（大于总数）当作未知')
  assert.equal(batchTargetHasNoUnset(batchTargetFromFilter({ ...filter, status: 'set' }, 5)), true)
  assert.equal(batchTargetHasNoUnset(allUnknown), false)

  // 2000 上限
  assert.equal(batchTargetTooMany(batchTargetFromFilter(filter, 2000)), false)
  assert.equal(batchTargetTooMany(batchTargetFromFilter(filter, 2001)), true, '> 2000：提示缩小筛选并禁用预览 / 确认')
}

// ---------- 预览失效规则 ----------
{
  const target: BatchTargetSpec = { kind: 'selected', productCodes: ['A', 'B'], setCount: 2, unsetCount: 0 }
  const state = setBatchField(createBatchEditState(), 'width', { enabled: true, value: 70 })
  const signature = batchRequestSignature(target, state)

  assert.equal(batchRequestSignature(target, state), signature, '同样的输入签名相同')
  assert.equal(batchRequestSignature({ ...target, productCodes: ['B', 'A'] }, state), signature, '货号顺序不影响签名')
  const changes: Array<[string, BatchEditState]> = [
    ['改了取值', setBatchField(state, 'width', { value: 71 })],
    ['多勾选一项', setBatchField(state, 'height', { enabled: true, value: 150 })],
    ['取消勾选', setBatchField(state, 'width', { enabled: false })],
    ['改了吊孔类型', setBatchField(state, 'hangType', { enabled: true, value: 0 })],
    ['勾选清空', setBatchField(state, 'weight', { enabled: true, clear: true })],
    ['改了每钩挂数', setBatchField(state, 'hookQty', { enabled: true, mode: 'merge', values: { h1: 3 } })],
    ['改了创建开关', { ...state, createMissing: true }],
    ['改了备注', setBatchField(state, 'notes', { enabled: true, value: 'x' })],
  ]
  for (const [name, next] of changes) {
    assert.notEqual(batchRequestSignature(target, next), signature, `${name}：预览必须失效`)
  }
  assert.notEqual(batchRequestSignature({ ...target, productCodes: ['A'] }, state), signature, '目标变了：预览失效')
  assert.notEqual(batchRequestSignature({ kind: 'filter', filter, total: 2, setCount: null, unsetCount: null }, state), signature)

  // 每钩挂数：merge → replace 请求不同；但「清空全部」模式下之前填的数值被忽略，改它们不改变请求
  const merge = setBatchField(state, 'hookQty', { enabled: true, mode: 'merge', values: { h1: 3 } })
  assert.notEqual(batchRequestSignature(target, setBatchField(merge, 'hookQty', { mode: 'replace' })), batchRequestSignature(target, merge))
  const clearA = setBatchField(merge, 'hookQty', { mode: 'clearAll' })
  const clearB = setBatchField(clearA, 'hookQty', { values: { h1: 99 } })
  assert.equal(batchRequestSignature(target, clearA), batchRequestSignature(target, clearB), '请求没变预览就仍有效')

  const result: ProductSpecBatchResultDto = {
    dryRun: true, matched: 2, created: 0, updated: 0, unchanged: 0, willCreate: 0, willUpdate: 2, willStayUnchanged: 0, skipped: 0, skippedItems: [], changedTotal: 2, changedItems: [],
  }
  const ready: BatchPreviewState = { status: 'ready', signature, result }
  assert.equal(usableBatchPreview(ready, signature), result)
  assert.equal(usableBatchPreview(ready, 'other'), null, '签名变了：预览不可用')
  assert.equal(usableBatchPreview({ status: 'ready', signature, result: { ...result, dryRun: false } }, signature), null, '执行结果不能当预览用')
  assert.equal(usableBatchPreview(IDLE_BATCH_PREVIEW, signature), null)
  assert.equal(isBatchPreviewStale(ready, 'other'), true)
  assert.equal(isBatchPreviewStale(ready, signature), false)
  assert.equal(isBatchPreviewStale(IDLE_BATCH_PREVIEW, signature), false, '没预览过谈不上过期')
  assert.equal(isBatchPreviewStale({ status: 'error', signature, error: new Error('x') }, 'other'), true, '失败的预览同样过期')
  assert.equal(isBatchPreviewObsolete({ status: 'loading', signature }, 'other'), true, '在途预览的输入又变了：响应要丢弃')
  assert.equal(isBatchPreviewObsolete({ status: 'loading', signature }, signature), false)

  // 确认执行的前置条件
  const valid = validateBatchEdit(state)
  const base = { preview: ready, signature, validation: valid, tooMany: false, executing: false }
  assert.equal(batchConfirmBlock(base), null, '有效预览 + 校验通过：可以执行')
  assert.equal(batchConfirmBlock({ ...base, signature: 'edited' }), 'noPreview', '预览之后改了输入：不能直接执行')
  assert.equal(batchConfirmBlock({ ...base, preview: IDLE_BATCH_PREVIEW }), 'noPreview')
  assert.equal(batchConfirmBlock({ ...base, preview: { status: 'loading', signature } }), 'noPreview')
  assert.equal(batchConfirmBlock({ ...base, executing: true }), 'executing', '执行中禁用重复点击')
  assert.equal(batchConfirmBlock({ ...base, tooMany: true }), 'tooMany')
  assert.equal(batchConfirmBlock({ ...base, validation: validateBatchEdit(createBatchEditState()) }), 'invalid')
  assert.equal(
    batchConfirmBlock({ ...base, preview: { status: 'ready', signature, result: { ...result, willUpdate: 0, willStayUnchanged: 2 } } }),
    'nothingToDo', '预览显示全部不变 / 全部跳过：没有可执行的内容',
  )
  assert.equal(batchConfirmBlock({ ...base, preview: { status: 'ready', signature, result: { ...result, willUpdate: 0, willCreate: 1 } } }), null, '只有创建也可执行')

  assert.equal(canStartBatchPreview({ validation: valid, tooMany: false, previewing: false, executing: false }), true)
  assert.equal(canStartBatchPreview({ validation: valid, tooMany: true, previewing: false, executing: false }), false)
  assert.equal(canStartBatchPreview({ validation: valid, tooMany: false, previewing: true, executing: false }), false)
  assert.equal(canStartBatchPreview({ validation: valid, tooMany: false, previewing: false, executing: true }), false)
  assert.equal(canStartBatchPreview({ validation: validateBatchEdit(createBatchEditState()), tooMany: false, previewing: false, executing: false }), false)
}

// ---------- 失败提示 ----------
{
  const translate = (key: string, params?: Record<string, unknown>) => (params ? `${key}${JSON.stringify(params)}` : key)
  const failure = (status: number, errorCode: string, message: string, details?: unknown) => ({ status, payload: { errorCode, message, details } })

  // 匹配数的几种形状
  assert.equal(extractBatchMatchedCount(2300), 2300)
  assert.equal(extractBatchMatchedCount('2300'), 2300)
  assert.equal(extractBatchMatchedCount({ matched: 2301 }), 2301)
  assert.equal(extractBatchMatchedCount({ count: 5 }), 5)
  assert.equal(extractBatchMatchedCount({ total: '77' }), 77)
  assert.equal(extractBatchMatchedCount([1, 2]), null)
  assert.equal(extractBatchMatchedCount(null), null)
  assert.equal(extractBatchMatchedCount(undefined), null)

  const tooMany = resolveBatchError(failure(400, 'PLANOGRAM_BATCH_TOO_MANY', '匹配商品过多', 2300), 'zh-CN', translate)
  assert.equal(tooMany.kind, 'tooMany')
  assert.equal(tooMany.matchedCount, 2300)
  assert.equal(tooMany.message, 'planogram.errors.batchTooManyCount{"count":2300,"max":2000}', '带匹配数的提示')
  const tooManyNoCount = resolveBatchError(failure(400, 'PLANOGRAM_BATCH_TOO_MANY', '匹配商品过多'), 'en', translate)
  assert.equal(tooManyNoCount.message, 'planogram.errors.batchTooMany{"max":2000}')

  const empty = resolveBatchError(failure(400, 'PLANOGRAM_BATCH_EMPTY', '没有匹配的商品'), 'zh-CN', translate)
  assert.equal(empty.kind, 'empty')
  assert.equal(empty.message, 'planogram.errors.batchEmpty')

  // PLANOGRAM_VALIDATION：带字段名的落到字段，其余进顶部提示
  const validation = resolveBatchError(failure(400, 'PLANOGRAM_VALIDATION', '参数不合法', [
    { field: 'patch.widthMm', message: '包装宽必须在 1～10000 之间' },
    { field: 'hookQtyOverrides', message: '挂钩型号「x」不存在' },
    { field: 'weirdKey', message: '不认识的字段' },
    '裸字符串提示',
  ]), 'zh-CN', translate)
  assert.equal(validation.kind, 'validation')
  assert.equal(validation.fieldErrors.width, '包装宽必须在 1～10000 之间', 'patch. 前缀与大小写不敏感')
  assert.equal(validation.fieldErrors.hookQty, '挂钩型号「x」不存在')
  assert.equal(validation.message, 'weirdKey: 不认识的字段；裸字符串提示', '对不上字段的内容并入顶部提示')

  // 未知键：details 只列字段名（附录 D.2）
  const unknownKeys = resolveBatchError(failure(400, 'PLANOGRAM_VALIDATION', '补丁里有不支持的字段', ['foo', 'bar']), 'zh-CN', translate)
  assert.deepEqual(unknownKeys.fieldErrors, {})
  assert.equal(unknownKeys.message, 'foo；bar')

  // 只有字段错误：顶部放通用校验失败文案
  const onlyFields = resolveBatchError(failure(400, 'PLANOGRAM_VALIDATION', '不合法', [{ field: 'notes', message: '备注不能超过 500 个字符' }]), 'en', translate)
  assert.equal(onlyFields.message, 'planogram.errors.validation')
  assert.equal(onlyFields.fieldErrors.notes, '备注不能超过 500 个字符')

  // 并发冲突：整批已回滚，提示重新预览
  const conflict = resolveBatchError(failure(409, 'PLANOGRAM_BATCH_CONFLICT', '有其他人同时修改了这些商品'), 'zh-CN', translate)
  assert.equal(conflict.kind, 'conflict')
  assert.equal(conflict.message, 'planogram.errors.batchConflict', '冲突用专门的文案，不走通用文案')

  // 真实后端：TOO_MANY 的 details 是 { matched, max }；筛选里的校验错误带 target.filter. 前缀
  assert.equal(extractBatchMatchedCount({ matched: 2300, max: 2000 }), 2300)
  const filterErr = resolveBatchError(failure(400, 'PLANOGRAM_VALIDATION', '筛选不合法', [
    { field: 'target.filter.grades', message: '商品等级只能是 A/B/C/D/none' },
    { field: 'widthMm', message: '包装宽必须在 1～10000 之间' },
  ]), 'zh-CN', translate)
  assert.equal(filterErr.fieldErrors.width, '包装宽必须在 1～10000 之间', 'patch 字段是裸名')
  assert.equal(filterErr.message, 'target.filter.grades: 商品等级只能是 A/B/C/D/none', '筛选里的错误不属于任何表单字段，进顶部提示')

  // 中文界面优先用服务端 message；英文界面走错误码文案
  assert.equal(resolveBatchError(failure(403, 'X', '没有权限'), 'zh-CN', translate).message, '没有权限')
  assert.equal(resolveBatchError(failure(403, 'X', '没有权限'), 'en', translate).message, 'planogram.errors.forbidden')
  assert.equal(resolveBatchError(failure(500, 'X', ''), 'zh-CN', translate).kind, 'other')
  assert.equal(resolveBatchError(new TypeError('network'), 'en', translate).message, 'planogram.errors.network')
  assert.equal(resolveBatchError(Object.assign(new Error('aborted'), { name: 'AbortError' }), 'en', translate).kind, 'abort', '中止不提示')

  assert.equal(batchFieldFromWireName('hangHoleOffsetMm'), 'hangOffset')
  assert.equal(batchFieldFromWireName('Patch/UnitWeightG'), 'weight')
  assert.equal(batchFieldFromWireName('createMissing'), 'createMissing')
  assert.equal(batchFieldFromWireName(null), null)
  assert.equal(batchFieldFromWireName('nope'), null)
}

// ---------- 跳过原因 ----------
{
  assert.equal(skipReasonKey('NO_SPEC'), 'planogram.spec.batch.skip.noSpec')
  assert.equal(skipReasonKey('MISSING_DIMENSIONS'), 'planogram.spec.batch.skip.missingDimensions')
  assert.equal(skipReasonKey('OFFSET_EXCEEDS_HEIGHT'), 'planogram.spec.batch.skip.offsetExceedsHeight')
  assert.equal(skipReasonKey('PRODUCT_NOT_FOUND'), 'planogram.spec.batch.skip.productNotFound')
  assert.equal(skipReasonKey('OVERRIDES_TOO_LONG'), 'planogram.spec.batch.skip.overridesTooLong')
  assert.equal(skipReasonKey('SOMETHING_NEW'), null, '不认识的原因：界面原样显示原因码')
  assert.equal(skippedItemsTruncated({ skipped: 250, skippedItems: new Array(200).fill({ productCode: 'x', reason: 'NO_SPEC' }) }), true, '明细最多 200 条')
  assert.equal(skippedItemsTruncated({ skipped: 3, skippedItems: new Array(3).fill({ productCode: 'x', reason: 'NO_SPEC' }) }), false)
}

// ---------- 改前改后明细的文字化 ----------
{
  const lookup: ChangeTextLookup = {
    label: (key) => `<${key}>`,
    hangType: (value) => `type${value}`,
    source: (value) => `src${value}`,
    hookName: (guid) => `hook:${guid}`,
    none: '—',
    cleared: '已清空',
    noOverrides: '无覆盖',
  }
  // 宽高厚都在：合并成「W×H×T」
  assert.deepEqual(changeParts({ widthMm: 110, heightMm: 155, depthMm: 12 }), [{ key: 'dims', value: '110×155×12' }])
  // update：只含改变的字段，分别列出
  assert.deepEqual(changeParts({ widthMm: 120 }).map((part) => part.key), ['widthMm'])
  assert.equal(changeParts(null).length, 0, 'create 的 before 为 null：没有任何项')
  assert.equal(formatChangeSide(null, lookup, 'before'), null, '界面显示「未设」')
  assert.equal(formatChangeSide({ widthMm: 120, notes: '旧备注' }, lookup, 'before'), '<widthMm> 120 · <notes> 旧备注')
  assert.equal(formatChangeSide({ widthMm: 70, notes: null }, lookup, 'after'), '<widthMm> 70 · <notes> 已清空', 'after 里的 null = 已清空')
  assert.equal(formatChangeSide({ unitWeightG: null }, lookup, 'before'), '<unitWeightG> —', 'before 里的 null = 原本没有')
  assert.equal(
    formatChangeSide({ widthMm: 70, heightMm: 150, depthMm: 25, hangHoleOffsetMm: 10, hangHoleType: 2, source: 1, hookQtyOverrides: { h2: 12, h1: 6 } }, lookup, 'after'),
    '70×150×25 · <hangHoleOffsetMm> 10 · <hangHoleType> type2 · <source> src1 · <hookQtyOverrides> hook:h2 ×12 · <hookQtyOverrides> hook:h1 ×6',
    'create 的 after：宽高厚合并，其余按固定顺序',
  )
  assert.equal(formatChangeSide({ hookQtyOverrides: {} }, lookup, 'after'), '<hookQtyOverrides> 无覆盖')
  assert.equal(formatChangeSide({ defaultHookGuid: 'h1', unitWeightG: 95 }, lookup, 'after'), '<unitWeightG> 95 g · <defaultHookGuid> hook:h1')
  assert.equal(changeLabelKey('widthMm'), 'planogram.spec.batch.short.width')

  // 每个字段一行；新建的 after 省掉与缺省值相同的吊孔偏移 / 类型 / 来源
  const created = { widthMm: 70, heightMm: 150, depthMm: 25, hangHoleOffsetMm: 10, hangHoleType: 2 as const, source: 1 as const, defaultHookGuid: 'h1' }
  assert.deepEqual(formatChangeLines(created, lookup, 'after'), ['70×150×25', '<hangHoleOffsetMm> 10', '<hangHoleType> type2', '<defaultHookGuid> hook:h1', '<source> src1'])
  assert.deepEqual(formatChangeLines(created, lookup, 'after', { omitCreateDefaults: true }), ['70×150×25', '<defaultHookGuid> hook:h1'], '与缺省值相同的项省略')
  assert.deepEqual(
    formatChangeLines({ ...created, hangHoleOffsetMm: 8, source: 2 }, lookup, 'after', { omitCreateDefaults: true }),
    ['70×150×25', '<hangHoleOffsetMm> 8', '<defaultHookGuid> hook:h1', '<source> src2'],
    '不同于缺省值的项照常显示',
  )
  assert.deepEqual(formatChangeLines(null, lookup, 'before'), [])
  assert.deepEqual(
    formatChangeLines({ hookQtyOverrides: { h2: 12, h1: 6 } }, lookup, 'after'),
    ['<hookQtyOverrides> hook:h2 ×12', '<hookQtyOverrides> hook:h1 ×6'],
    '多个挂钩型号的挂数覆盖：每个型号一行',
  )
  assert.deepEqual(formatChangeLines({ hookQtyOverrides: { h1: 6 } }, lookup, 'after'), ['<hookQtyOverrides> hook:h1 ×6'])
}

console.log('batchLogic.test: ok')
