/**
 * 商品陈列参数「批量修改」的纯逻辑（附录 D.2 / D.3）：不依赖 React / antd，界面形态另行设计，这里只管数据。
 *
 * 内容：
 * 1) 跨页勾选（货号 → 是否已设参数）与 2000 上限；
 * 2) 批量编辑表单状态 → 补丁（只含被勾选的字段）→ 请求体；
 * 3) 提交前的本地校验（范围与后端 ProductDisplaySpecService.ValidateAsync 一致，后端仍是最终裁判）；
 * 4) 预览（dryRun）结果的有效性判定：预览之后只要「将提交的请求」变了，预览就失效，必须重新预览；
 * 5) 失败响应（PLANOGRAM_VALIDATION / PLANOGRAM_BATCH_TOO_MANY / PLANOGRAM_BATCH_EMPTY）→ 界面提示；
 * 6) 跳过原因 → 文案键。
 */
import type {
  PlanogramHangHoleType,
  PlanogramSpecSource,
  ProductSpecBatchChangeValues,
  ProductSpecBatchFilter,
  ProductSpecBatchPatch,
  ProductSpecBatchRequest,
  ProductSpecBatchResultDto,
  ProductSpecBatchTarget,
} from '../../../../types/planogram'
import { normalizeQtyOverride } from '../engine/hookQty'
import { planogramErrorText, resolvePlanogramError } from '../planogramLogic'

// ---------------------------------------------------------------------------
// 常量（与后端 ProductDisplaySpecService / 附录 D.2 一致）
// ---------------------------------------------------------------------------

/** 一次批量最多处理的商品数（勾选货号 ≤ 2000；按筛选匹配数 > 2000 后端返回 PLANOGRAM_BATCH_TOO_MANY）。 */
export const BATCH_MAX_TARGETS = 2000
/** 后端 skippedItems 最多返回的条数（skipped 才是真实总数）。 */
export const BATCH_SKIPPED_ITEMS_LIMIT = 200
/** 宽 / 高 / 厚上限（mm）。 */
export const BATCH_MAX_MM = 10000
/** 单件重范围（克）。 */
export const BATCH_MIN_WEIGHT_G = 1
export const BATCH_MAX_WEIGHT_G = 1_000_000
/** 每钩挂数上限。 */
export const BATCH_MAX_HOOK_QTY = 9999
/** 备注长度上限。 */
export const BATCH_MAX_NOTES_LENGTH = 500

// ---------------------------------------------------------------------------
// 1) 跨页勾选
// ---------------------------------------------------------------------------

/** 勾选集合：货号 → 该商品勾选时是否已设参数（用来在目标摘要里算「已设 X / 未设 Y」，翻页后仍可得）。保持勾选顺序。 */
export type BatchSelection = ReadonlyMap<string, boolean>

export interface SelectableRow {
  productCode: string
  hasSpec: boolean
}

export const EMPTY_BATCH_SELECTION: BatchSelection = new Map<string, boolean>()

export interface SelectionAddResult {
  selection: BatchSelection
  added: number
  /** 因达到上限没能加入的行数（界面据此提示「最多 2000 个」）。 */
  rejected: number
}

/** 勾选一批行：已在集合里的只刷新其「是否已设」标记；超过上限的行不加入并计入 rejected。无变化时返回原对象。 */
export function selectionAdd(selection: BatchSelection, rows: readonly SelectableRow[], max = BATCH_MAX_TARGETS): SelectionAddResult {
  let next: Map<string, boolean> | null = null
  let added = 0
  let rejected = 0
  for (const row of rows) {
    const current = (next ?? selection).get(row.productCode)
    if (current !== undefined) {
      if (current !== row.hasSpec) {
        next ??= new Map(selection)
        next.set(row.productCode, row.hasSpec)
      }
      continue
    }
    if ((next ?? selection).size >= max) {
      rejected += 1
      continue
    }
    next ??= new Map(selection)
    next.set(row.productCode, row.hasSpec)
    added += 1
  }
  return { selection: next ?? selection, added, rejected }
}

/** 取消勾选；没有变化时返回原对象。 */
export function selectionRemove(selection: BatchSelection, productCodes: readonly string[]): BatchSelection {
  if (!productCodes.some((code) => selection.has(code))) return selection
  const next = new Map(selection)
  for (const code of productCodes) next.delete(code)
  return next
}

/**
 * 列表刷新后用当前页的行同步「是否已设」标记（例如在抽屉里给某个已勾选的商品补了参数）。
 * 只改已勾选且出现在当前页的行；没有变化时返回原对象。
 */
export function selectionSyncRows(selection: BatchSelection, rows: readonly SelectableRow[]): BatchSelection {
  let next: Map<string, boolean> | null = null
  for (const row of rows) {
    const current = selection.get(row.productCode)
    if (current !== undefined && current !== row.hasSpec) {
      next ??= new Map(selection)
      next.set(row.productCode, row.hasSpec)
    }
  }
  return next ?? selection
}

export interface SelectionCounts {
  total: number
  setCount: number
  unsetCount: number
}

export function selectionCounts(selection: BatchSelection): SelectionCounts {
  let setCount = 0
  for (const hasSpec of selection.values()) if (hasSpec) setCount += 1
  return { total: selection.size, setCount, unsetCount: selection.size - setCount }
}

export function selectionKeys(selection: BatchSelection): string[] {
  return [...selection.keys()]
}

// ---------------------------------------------------------------------------
// 2) 批量编辑表单状态
// ---------------------------------------------------------------------------

/** 数字类字段：勾选「本次修改」才进入补丁。 */
export interface BatchNumberField {
  enabled: boolean
  value: number | null
}

export type BatchHookQtyMode = 'merge' | 'replace' | 'clearAll'

export interface BatchEditState {
  width: BatchNumberField
  height: BatchNumberField
  depth: BatchNumberField
  hangOffset: BatchNumberField
  hangType: { enabled: boolean; value: PlanogramHangHoleType }
  /** 单件重：clear = 清空（补丁传 null）。 */
  weight: { enabled: boolean; clear: boolean; value: number | null }
  /** 默认挂钩：clear = 清空。 */
  defaultHook: { enabled: boolean; clear: boolean; value: string | null }
  source: { enabled: boolean; value: PlanogramSpecSource }
  /** 备注：clear = 清空。 */
  notes: { enabled: boolean; clear: boolean; value: string }
  /**
   * 每钩挂数覆盖：merge 只覆盖填写的挂钩、其余保留；replace 用填写的整体替换；clearAll 清空全部覆盖。
   * values 以挂钩型号 guid 为键，空 / null = 没填。
   */
  hookQty: { enabled: boolean; mode: BatchHookQtyMode; values: Record<string, number | null> }
  /** 目标里未设参数的商品：false = 跳过（默认），true = 创建（必须同时修改宽、高、厚）。 */
  createMissing: boolean
}

/** 初始状态：什么都不改。数值字段的预填值与新建商品的缺省一致（吊孔偏移 10、欧式挂孔、人工量取）。 */
export function createBatchEditState(): BatchEditState {
  return {
    width: { enabled: false, value: null },
    height: { enabled: false, value: null },
    depth: { enabled: false, value: null },
    hangOffset: { enabled: false, value: 10 },
    hangType: { enabled: false, value: 2 },
    weight: { enabled: false, clear: false, value: null },
    defaultHook: { enabled: false, clear: false, value: null },
    source: { enabled: false, value: 1 },
    notes: { enabled: false, clear: false, value: '' },
    hookQty: { enabled: false, mode: 'merge', values: {} },
    createMissing: false,
  }
}

/** 表单字段键（界面行与错误提示的归属）。 */
export type BatchFieldKey =
  | 'width' | 'height' | 'depth' | 'hangOffset' | 'hangType' | 'weight' | 'defaultHook' | 'source' | 'notes' | 'hookQty' | 'createMissing'

/** 字段键 → 后端字段名（附录 D.2 patch 白名单键 / hookQtyOverrides / createMissing）。 */
export const BATCH_FIELD_WIRE_NAMES: Readonly<Record<BatchFieldKey, string>> = {
  width: 'widthMm',
  height: 'heightMm',
  depth: 'depthMm',
  hangOffset: 'hangHoleOffsetMm',
  hangType: 'hangHoleType',
  weight: 'unitWeightG',
  defaultHook: 'defaultHookGuid',
  source: 'source',
  notes: 'notes',
  hookQty: 'hookQtyOverrides',
  createMissing: 'createMissing',
}

export interface BatchContext {
  /** 可用于设置的挂钩型号 guid（启用的型号）；不传 = 不限制。 */
  hookGuids?: ReadonlySet<string>
}

/** 「本次修改」勾选的项数（补丁字段 + 每钩挂数覆盖）。 */
export function countBatchChanges(state: BatchEditState): number {
  return [
    state.width.enabled, state.height.enabled, state.depth.enabled, state.hangOffset.enabled, state.hangType.enabled,
    state.weight.enabled, state.defaultHook.enabled, state.source.enabled, state.notes.enabled, state.hookQty.enabled,
  ].filter(Boolean).length
}

/** 某个字段「本次修改」勾选开关的归一化写入：改勾选以外的值时不动勾选。 */
export function setBatchField<K extends Exclude<BatchFieldKey, 'createMissing'>>(
  state: BatchEditState,
  key: K,
  patch: Partial<BatchEditState[K]>,
): BatchEditState {
  return { ...state, [key]: { ...state[key], ...patch } }
}

// ---------------------------------------------------------------------------
// 补丁与请求体
// ---------------------------------------------------------------------------

function isNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

/**
 * 表单 → 补丁：只含「本次修改」勾选且取值有效的字段；可空字段勾选「清空」传 null。
 * 取值无效（没填、不是数字）的勾选字段这里不进补丁，由 validateBatchEdit 报错、阻止预览。键按固定顺序写入，保证签名稳定。
 */
export function buildBatchPatch(state: BatchEditState): ProductSpecBatchPatch {
  const patch: ProductSpecBatchPatch = {}
  if (state.width.enabled && isNumber(state.width.value)) patch.widthMm = state.width.value
  if (state.height.enabled && isNumber(state.height.value)) patch.heightMm = state.height.value
  if (state.depth.enabled && isNumber(state.depth.value)) patch.depthMm = state.depth.value
  if (state.hangOffset.enabled && isNumber(state.hangOffset.value)) patch.hangHoleOffsetMm = state.hangOffset.value
  if (state.hangType.enabled) patch.hangHoleType = state.hangType.value
  if (state.weight.enabled) {
    if (state.weight.clear) patch.unitWeightG = null
    else if (isNumber(state.weight.value)) patch.unitWeightG = state.weight.value
  }
  if (state.defaultHook.enabled) {
    if (state.defaultHook.clear) patch.defaultHookGuid = null
    else if (state.defaultHook.value) patch.defaultHookGuid = state.defaultHook.value
  }
  if (state.source.enabled) patch.source = state.source.value
  if (state.notes.enabled) {
    if (state.notes.clear) patch.notes = null
    else if (state.notes.value.trim()) patch.notes = state.notes.value.trim()
  }
  return patch
}

/**
 * 每钩挂数覆盖：未勾选 → undefined（不动）；clearAll → replace + 空对象；
 * merge / replace → 只取「型号可用且值是 1 以上整数」的填写项；没有任何有效项 → undefined（由校验报错，不会悄悄变成清空）。
 */
export function buildBatchHookQty(state: BatchEditState, context: BatchContext = {}): ProductSpecBatchRequest['hookQtyOverrides'] {
  const { hookQty } = state
  if (!hookQty.enabled) return undefined
  if (hookQty.mode === 'clearAll') return { mode: 'replace', values: {} }
  const values: Record<string, number> = {}
  for (const guid of Object.keys(hookQty.values).sort()) {
    if (context.hookGuids && !context.hookGuids.has(guid)) continue
    const qty = normalizeQtyOverride(hookQty.values[guid])
    if (qty !== null && qty <= BATCH_MAX_HOOK_QTY) values[guid] = qty
  }
  return Object.keys(values).length > 0 ? { mode: hookQty.mode, values } : undefined
}

// ---------------------------------------------------------------------------
// 目标：勾选 / 按当前筛选
// ---------------------------------------------------------------------------

export type BatchTargetSpec =
  | {
      kind: 'selected'
      productCodes: string[]
      setCount: number
      unsetCount: number
    }
  | {
      kind: 'filter'
      filter: ProductSpecBatchFilter
      /** 列表总数（当前筛选匹配的商品数）。 */
      total: number
      /** 已设 / 未设数：状态筛选为「已设 / 未设」时可直接得出，「全部」时需另查一次；未知为 null。 */
      setCount: number | null
      unsetCount: number | null
    }

export function batchTargetFromSelection(selection: BatchSelection): BatchTargetSpec {
  const counts = selectionCounts(selection)
  return { kind: 'selected', productCodes: selectionKeys(selection), setCount: counts.setCount, unsetCount: counts.unsetCount }
}

export function batchTargetFromFilter(filter: ProductSpecBatchFilter, total: number, setCount: number | null = null): BatchTargetSpec {
  // 状态筛选为已设 / 未设时，已设 / 未设数不用再查
  if (filter.status === 'set') return { kind: 'filter', filter, total, setCount: total, unsetCount: 0 }
  if (filter.status === 'unset') return { kind: 'filter', filter, total, setCount: 0, unsetCount: total }
  const known = setCount !== null && setCount >= 0 && setCount <= total
  return { kind: 'filter', filter, total, setCount: known ? setCount : null, unsetCount: known ? total - setCount : null }
}

export function batchTargetCount(target: BatchTargetSpec): number {
  return target.kind === 'selected' ? target.productCodes.length : target.total
}

/** 目标数超过 2000（界面提示缩小筛选，并禁用预览 / 确认）。 */
export function batchTargetTooMany(target: BatchTargetSpec): boolean {
  return batchTargetCount(target) > BATCH_MAX_TARGETS
}

/** 目标里已知没有未设参数的商品（「未设参数的商品」选项无意义）。 */
export function batchTargetHasNoUnset(target: BatchTargetSpec): boolean {
  return target.unsetCount === 0
}

export function toBatchTarget(target: BatchTargetSpec): ProductSpecBatchTarget {
  return target.kind === 'selected' ? { productCodes: [...target.productCodes] } : { filter: target.filter }
}

export function buildBatchRequest(
  target: BatchTargetSpec,
  state: BatchEditState,
  dryRun: boolean,
  context: BatchContext = {},
): ProductSpecBatchRequest {
  const request: ProductSpecBatchRequest = {
    target: toBatchTarget(target),
    patch: buildBatchPatch(state),
    createMissing: state.createMissing,
    dryRun,
  }
  const hookQtyOverrides = buildBatchHookQty(state, context)
  if (hookQtyOverrides) request.hookQtyOverrides = hookQtyOverrides
  return request
}

// ---------------------------------------------------------------------------
// 3) 本地校验
// ---------------------------------------------------------------------------

export type BatchErrorCode =
  | 'required'
  | 'rangeInt'
  | 'nonNegativeInt'
  | 'notesTooLong'
  | 'offsetExceedsHeight'
  | 'unknownHook'
  | 'qtyInvalid'
  | 'qtyNone'
  | 'needDimensions'

export interface BatchFieldError {
  code: BatchErrorCode
  params?: Record<string, number>
}

export type BatchFieldErrors = Partial<Record<BatchFieldKey, BatchFieldError>>

export interface BatchValidation {
  errors: BatchFieldErrors
  /** 勾选的项数。 */
  changeCount: number
  /** 至少勾选了一项要修改的内容。 */
  hasChanges: boolean
  /** 可以预览：有改动且没有任何字段错误。目标数是否超限由调用方另行判断（batchTargetTooMany）。 */
  valid: boolean
}

function isInteger(value: number | null): value is number {
  return isNumber(value) && Number.isInteger(value)
}

/**
 * 提交前校验。规则与后端一致：宽高厚 1～10000 整数、吊孔偏移 ≥ 0 整数、单件重 1～1000000、备注 ≤ 500、每钩挂数 1～9999 整数；
 * 选择「创建未设参数的商品」时必须同时修改宽、高、厚；
 * 同时改了吊孔偏移与高、且吊孔类型明确改成非「无」时，偏移不得大于高（否则全部商品都会被跳过）。
 * 只改其中一个而另一个取商品原值的情形无法在本地判断，交给后端逐商品跳过（原因 OFFSET_EXCEEDS_HEIGHT）。
 */
export function validateBatchEdit(state: BatchEditState, context: BatchContext = {}): BatchValidation {
  const errors: BatchFieldErrors = {}

  for (const key of ['width', 'height', 'depth'] as const) {
    const field = state[key]
    if (!field.enabled) continue
    if (!isNumber(field.value)) errors[key] = { code: 'required' }
    else if (!isInteger(field.value) || field.value < 1 || field.value > BATCH_MAX_MM) {
      errors[key] = { code: 'rangeInt', params: { min: 1, max: BATCH_MAX_MM } }
    }
  }

  if (state.hangOffset.enabled) {
    const { value } = state.hangOffset
    if (!isNumber(value)) errors.hangOffset = { code: 'required' }
    else if (!isInteger(value) || value < 0) errors.hangOffset = { code: 'nonNegativeInt' }
  }

  if (state.weight.enabled && !state.weight.clear) {
    const { value } = state.weight
    if (!isNumber(value)) errors.weight = { code: 'required' }
    else if (!isInteger(value) || value < BATCH_MIN_WEIGHT_G || value > BATCH_MAX_WEIGHT_G) {
      errors.weight = { code: 'rangeInt', params: { min: BATCH_MIN_WEIGHT_G, max: BATCH_MAX_WEIGHT_G } }
    }
  }

  if (state.defaultHook.enabled && !state.defaultHook.clear) {
    const { value } = state.defaultHook
    if (!value) errors.defaultHook = { code: 'required' }
    else if (context.hookGuids && !context.hookGuids.has(value)) errors.defaultHook = { code: 'unknownHook' }
  }

  if (state.notes.enabled && !state.notes.clear) {
    const trimmed = state.notes.value.trim()
    if (!trimmed) errors.notes = { code: 'required' }
    else if (trimmed.length > BATCH_MAX_NOTES_LENGTH) errors.notes = { code: 'notesTooLong', params: { max: BATCH_MAX_NOTES_LENGTH } }
  }

  if (state.hookQty.enabled && state.hookQty.mode !== 'clearAll') {
    let filled = 0
    let invalid = false
    for (const [guid, raw] of Object.entries(state.hookQty.values)) {
      if (raw === null || raw === undefined) continue
      if (context.hookGuids && !context.hookGuids.has(guid)) continue
      filled += 1
      const qty = normalizeQtyOverride(raw)
      if (qty === null || qty > BATCH_MAX_HOOK_QTY) invalid = true
    }
    if (invalid) errors.hookQty = { code: 'qtyInvalid', params: { max: BATCH_MAX_HOOK_QTY } }
    else if (filled === 0) errors.hookQty = { code: 'qtyNone' }
  }

  // 同时改偏移与高、且吊孔类型明确改成非「无」：偏移不能大于高
  if (
    !errors.hangOffset && !errors.height
    && state.hangOffset.enabled && state.height.enabled
    && state.hangType.enabled && state.hangType.value !== 0
    && isNumber(state.hangOffset.value) && isNumber(state.height.value)
    && state.hangOffset.value > state.height.value
  ) {
    errors.hangOffset = { code: 'offsetExceedsHeight' }
  }

  // 创建未设参数的商品：宽、高、厚必须同时勾选并填齐
  if (state.createMissing) {
    const complete = (['width', 'height', 'depth'] as const).every((key) => state[key].enabled && isNumber(state[key].value) && !errors[key])
    if (!complete) errors.createMissing = { code: 'needDimensions' }
  }

  const changeCount = countBatchChanges(state)
  return { errors, changeCount, hasChanges: changeCount > 0, valid: changeCount > 0 && Object.keys(errors).length === 0 }
}

/** 校验错误码 → 文案键（字面量登记，契约测试按字面量扫描；占位符 {{min}} / {{max}} 由调用方传 params）。 */
const BATCH_ERROR_KEYS: Record<BatchErrorCode, string> = {
  required: 'planogram.spec.batch.error.required',
  rangeInt: 'planogram.spec.batch.error.rangeInt',
  nonNegativeInt: 'planogram.spec.batch.error.nonNegativeInt',
  notesTooLong: 'planogram.spec.batch.error.notesTooLong',
  offsetExceedsHeight: 'planogram.spec.batch.error.offsetExceedsHeight',
  unknownHook: 'planogram.spec.batch.error.unknownHook',
  qtyInvalid: 'planogram.spec.batch.error.qtyInvalid',
  qtyNone: 'planogram.spec.batch.error.qtyNone',
  needDimensions: 'planogram.spec.batch.error.needDimensions',
}

export function batchErrorKey(code: BatchErrorCode): string {
  return BATCH_ERROR_KEYS[code]
}

// ---------------------------------------------------------------------------
// 4) 预览（dryRun）有效性
// ---------------------------------------------------------------------------

/**
 * 「将提交的请求」的签名（不含 dryRun）：目标 + 补丁 + 每钩挂数覆盖 + 是否创建。
 * 预览的有效性按它判定：预览之后只要签名变了（改了任何会进请求的输入，包括勾选 / 取值 / 模式 / 创建开关 / 目标），预览立刻失效。
 * 没进请求的输入（例如「合并」模式下又切到「清空全部」前填的数值被忽略）不影响签名——请求没变，预览仍然代表将要执行的内容。
 */
export function batchRequestSignature(target: BatchTargetSpec, state: BatchEditState, context: BatchContext = {}): string {
  const request = buildBatchRequest(target, state, false, context)
  const targetPart = 'productCodes' in request.target
    ? { productCodes: [...request.target.productCodes].sort() }
    : { filter: request.target.filter }
  return JSON.stringify({
    target: targetPart,
    patch: request.patch,
    hookQtyOverrides: request.hookQtyOverrides ?? null,
    createMissing: request.createMissing,
  })
}

export type BatchPreviewState =
  | { status: 'idle' }
  | { status: 'loading'; signature: string }
  | { status: 'ready'; signature: string; result: ProductSpecBatchResultDto }
  | { status: 'error'; signature: string; error: unknown }

export const IDLE_BATCH_PREVIEW: BatchPreviewState = { status: 'idle' }

/** 当前请求签名下仍有效的预览结果；预览过期（签名不同）、还在加载、失败时返回 null。 */
export function usableBatchPreview(preview: BatchPreviewState, signature: string): ProductSpecBatchResultDto | null {
  return preview.status === 'ready' && preview.signature === signature && preview.result.dryRun ? preview.result : null
}

/** 预览是否过期：有预览（成功或失败）但签名与当前不同——界面应隐藏它并提示「内容已修改，请重新预览」。 */
export function isBatchPreviewStale(preview: BatchPreviewState, signature: string): boolean {
  return preview.status !== 'idle' && preview.status !== 'loading' && preview.signature !== signature
}

/** 预览请求在途中且签名已变：返回的结果应当丢弃（调用方据此忽略过期响应）。 */
export function isBatchPreviewObsolete(preview: BatchPreviewState, signature: string): boolean {
  return preview.status === 'loading' && preview.signature !== signature
}

export type BatchConfirmBlock =
  | 'executing'
  | 'done'
  | 'invalid'
  | 'tooMany'
  | 'noPreview'
  | 'nothingToDo'

/**
 * 能否「确认执行」：必须有当前签名下的有效预览、表单校验通过、目标数没超限、没有正在执行，
 * 并且预览显示至少有一个商品会被创建或更新（全部不变 / 全部跳过时没有可执行的内容）。返回阻止原因，可执行时返回 null。
 */
export function batchConfirmBlock(input: {
  preview: BatchPreviewState
  signature: string
  validation: BatchValidation
  tooMany: boolean
  executing: boolean
  /** 已经执行成功过：同一个弹窗里不再允许重复执行（要再改，重新打开）。 */
  executed?: boolean
}): BatchConfirmBlock | null {
  if (input.executing) return 'executing'
  if (input.executed) return 'done'
  if (!input.validation.valid) return 'invalid'
  if (input.tooMany) return 'tooMany'
  const result = usableBatchPreview(input.preview, input.signature)
  if (!result) return 'noPreview'
  if (result.willCreate + result.willUpdate <= 0) return 'nothingToDo'
  return null
}

/** 能否发起预览：校验通过、目标数没超限、没有在途的预览 / 执行。 */
export function canStartBatchPreview(input: { validation: BatchValidation; tooMany: boolean; previewing: boolean; executing: boolean }): boolean {
  return input.validation.valid && !input.tooMany && !input.previewing && !input.executing
}

// ---------------------------------------------------------------------------
// 5) 失败响应 → 提示
// ---------------------------------------------------------------------------

export type BatchErrorKind = 'abort' | 'validation' | 'tooMany' | 'empty' | 'conflict' | 'other'

export interface BatchErrorView {
  kind: BatchErrorKind
  /** 顶部提示（已翻译；中文界面优先用服务端的具体中文 message）。 */
  message: string
  /** 能对应到具体表单字段的错误（后端 details 里带字段名的项）。 */
  fieldErrors: Partial<Record<BatchFieldKey, string>>
  /** PLANOGRAM_BATCH_TOO_MANY 的匹配数（details 里取得到时）。 */
  matchedCount: number | null
}

/** PLANOGRAM_BATCH_TOO_MANY 的 details 带匹配数，形状附录没定：兼容数字、数字字符串、{ matched | count | total | matchedCount } 对象。 */
export function extractBatchMatchedCount(details: unknown): number | null {
  const toCount = (value: unknown): number | null => {
    if (typeof value === 'number' && Number.isFinite(value) && value >= 0) return Math.floor(value)
    if (typeof value === 'string' && /^\d+$/.test(value.trim())) return Number(value.trim())
    return null
  }
  const direct = toCount(details)
  if (direct !== null) return direct
  if (details && typeof details === 'object' && !Array.isArray(details)) {
    const record = details as Record<string, unknown>
    for (const name of ['matched', 'matchedCount', 'count', 'total']) {
      const found = toCount(record[name])
      if (found !== null) return found
    }
  }
  return null
}

/** 后端字段名（可带 patch. 前缀，大小写不敏感）→ 表单字段键。 */
export function batchFieldFromWireName(name: string | null): BatchFieldKey | null {
  if (!name) return null
  // patch 字段是裸名（widthMm），也兼容带 patch. 前缀；筛选里的错误带 target.filter. 前缀，不属于任何表单字段（返回 null，进顶部提示）
  const lower = name.trim().toLowerCase().replace(/^patch[./]/, '')
  for (const [key, wire] of Object.entries(BATCH_FIELD_WIRE_NAMES) as Array<[BatchFieldKey, string]>) {
    if (wire.toLowerCase() === lower) return key
  }
  return null
}

type Translate = (key: string, params?: Record<string, unknown>) => string

/**
 * 预览 / 执行失败 → 界面提示：
 * - PLANOGRAM_VALIDATION：details 里带字段名的落到对应字段，其余并入顶部提示；
 * - PLANOGRAM_BATCH_TOO_MANY：「匹配 N 个，超过 2000 个上限，请缩小筛选」；
 * - PLANOGRAM_BATCH_EMPTY：「没有匹配的商品」；
 * - PLANOGRAM_BATCH_CONFLICT：执行时有人同时改了这些商品（整批已回滚），提示重新预览（kind=conflict，调用方让预览失效）；
 * - 其他沿用通用错误文案；中止（AbortError）返回 kind=abort，界面不提示。
 */
export function resolveBatchError(error: unknown, language: string, translate: Translate): BatchErrorView {
  const resolved = resolvePlanogramError(error)
  if (resolved.kind === 'abort') return { kind: 'abort', message: '', fieldErrors: {}, matchedCount: null }

  if (resolved.code === 'PLANOGRAM_BATCH_TOO_MANY') {
    const matchedCount = extractBatchMatchedCount(resolved.details)
    const message = matchedCount === null
      ? translate('planogram.errors.batchTooMany', { max: BATCH_MAX_TARGETS })
      : translate('planogram.errors.batchTooManyCount', { count: matchedCount, max: BATCH_MAX_TARGETS })
    return { kind: 'tooMany', message, fieldErrors: {}, matchedCount }
  }
  if (resolved.code === 'PLANOGRAM_BATCH_CONFLICT') {
    // 真正执行时有人同时改了这些商品：整批已回滚，调用方据此让预览失效、引导重新预览
    return { kind: 'conflict', message: translate('planogram.errors.batchConflict'), fieldErrors: {}, matchedCount: null }
  }
  if (resolved.code === 'PLANOGRAM_BATCH_EMPTY') {
    return { kind: 'empty', message: translate('planogram.errors.batchEmpty'), fieldErrors: {}, matchedCount: null }
  }

  const fallback = planogramErrorText(resolved, language, (key) => translate(key))
  if (resolved.code === 'PLANOGRAM_VALIDATION') {
    const fieldErrors: Partial<Record<BatchFieldKey, string>> = {}
    const unmatched: string[] = []
    for (const item of resolved.fieldErrors) {
      const key = batchFieldFromWireName(item.field)
      if (key) fieldErrors[key] = fieldErrors[key] ?? item.message
      else unmatched.push(item.field ? `${item.field}: ${item.message}` : item.message)
    }
    // 有字段错误时顶部只放没能对上字段的内容；一条也没有就用通用的校验失败文案
    const message = unmatched.length > 0 ? unmatched.join('；') : Object.keys(fieldErrors).length > 0 ? translate('planogram.errors.validation') : fallback
    return { kind: 'validation', message, fieldErrors, matchedCount: null }
  }
  return { kind: 'other', message: fallback, fieldErrors: {}, matchedCount: null }
}

// ---------------------------------------------------------------------------
// 6) 跳过原因
// ---------------------------------------------------------------------------

// 文案键必须以带引号的字面量出现：契约测试按这个形式静态扫描，才能发现缺失键。
const SKIP_REASON_KEYS: Record<string, string> = {
  NO_SPEC: 'planogram.spec.batch.skip.noSpec',
  MISSING_DIMENSIONS: 'planogram.spec.batch.skip.missingDimensions',
  OFFSET_EXCEEDS_HEIGHT: 'planogram.spec.batch.skip.offsetExceedsHeight',
  PRODUCT_NOT_FOUND: 'planogram.spec.batch.skip.productNotFound',
  OVERRIDES_TOO_LONG: 'planogram.spec.batch.skip.overridesTooLong',
}

/** 跳过原因 → 文案键；后端新增的、这里不认识的原因返回 null（界面原样显示原因码）。 */
export function skipReasonKey(reason: string): string | null {
  return SKIP_REASON_KEYS[reason] ?? null
}

/** 结果是否有跳过明细被截断（后端最多返回 200 条，skipped 是真实总数）。 */
export function skippedItemsTruncated(result: Pick<ProductSpecBatchResultDto, 'skipped' | 'skippedItems'>): boolean {
  return result.skipped > result.skippedItems.length
}

// ---------------------------------------------------------------------------
// 7) 改前改后明细（后端 changedItems）的文字化
// ---------------------------------------------------------------------------

export type ChangeFieldKey =
  | 'dims' | 'widthMm' | 'heightMm' | 'depthMm' | 'hangHoleOffsetMm' | 'hangHoleType'
  | 'unitWeightG' | 'defaultHookGuid' | 'source' | 'notes' | 'hookQtyOverrides'

export interface ChangePart {
  key: ChangeFieldKey
  value: number | string | null | Record<string, number>
}

/**
 * 明细一侧 → 有序的「字段 + 值」。宽高厚三个都在时合并成一项 dims（「110×155×12」，与设计图一致），
 * 否则分别列出（update 的 before / after 只含改变的字段，常常只有其中一两个）。null 侧（create 的 before）返回空数组。
 */
export function changeParts(values: ProductSpecBatchChangeValues | null | undefined): ChangePart[] {
  if (!values) return []
  const parts: ChangePart[] = []
  const hasDims = typeof values.widthMm === 'number' && typeof values.heightMm === 'number' && typeof values.depthMm === 'number'
  if (hasDims) parts.push({ key: 'dims', value: `${values.widthMm}×${values.heightMm}×${values.depthMm}` })
  else {
    for (const key of ['widthMm', 'heightMm', 'depthMm'] as const) {
      if (key in values && typeof values[key] === 'number') parts.push({ key, value: values[key] as number })
    }
  }
  if ('hangHoleOffsetMm' in values && typeof values.hangHoleOffsetMm === 'number') parts.push({ key: 'hangHoleOffsetMm', value: values.hangHoleOffsetMm })
  if ('hangHoleType' in values && values.hangHoleType !== undefined) parts.push({ key: 'hangHoleType', value: values.hangHoleType })
  if ('unitWeightG' in values) parts.push({ key: 'unitWeightG', value: values.unitWeightG ?? null })
  if ('defaultHookGuid' in values) parts.push({ key: 'defaultHookGuid', value: values.defaultHookGuid ?? null })
  if ('source' in values && values.source !== undefined) parts.push({ key: 'source', value: values.source })
  if ('notes' in values) parts.push({ key: 'notes', value: values.notes ?? null })
  if ('hookQtyOverrides' in values && values.hookQtyOverrides) parts.push({ key: 'hookQtyOverrides', value: values.hookQtyOverrides })
  return parts
}

// 字段简称的文案键（字面量登记，契约测试按字面量扫描）
const CHANGE_LABEL_KEYS: Record<Exclude<ChangeFieldKey, 'dims'>, string> = {
  widthMm: 'planogram.spec.batch.short.width',
  heightMm: 'planogram.spec.batch.short.height',
  depthMm: 'planogram.spec.batch.short.depth',
  hangHoleOffsetMm: 'planogram.spec.batch.short.hangOffset',
  hangHoleType: 'planogram.spec.batch.short.hangType',
  unitWeightG: 'planogram.spec.batch.short.weight',
  defaultHookGuid: 'planogram.spec.batch.short.defaultHook',
  source: 'planogram.spec.batch.short.source',
  notes: 'planogram.spec.batch.short.notes',
  hookQtyOverrides: 'planogram.spec.batch.short.hookQty',
}

export function changeLabelKey(key: Exclude<ChangeFieldKey, 'dims'>): string {
  return CHANGE_LABEL_KEYS[key]
}

/** 文字化用到的查表函数（界面语言相关的部分由调用方给）。 */
export interface ChangeTextLookup {
  /** 字段简称（宽 / 高 / 厚 / 吊孔 / 单件重…）；dims 不需要标签。 */
  label: (key: Exclude<ChangeFieldKey, 'dims'>) => string
  hangType: (value: PlanogramHangHoleType) => string
  source: (value: PlanogramSpecSource) => string
  hookName: (guid: string) => string
  /** 空值（改前原本就没有）。 */
  none: string
  /** after 里被清空的可空字段。 */
  cleared: string
  /** 空的挂数覆盖。 */
  noOverrides: string
}

/** 一项 → 文字。after 侧的 null = 已清空；before 侧的 null = 原本没有。 */
export function formatChangePart(part: ChangePart, lookup: ChangeTextLookup, side: 'before' | 'after'): string {
  if (part.key === 'dims') return String(part.value)
  const label = lookup.label(part.key)
  const empty = side === 'after' ? lookup.cleared : lookup.none
  if (part.value === null) return `${label} ${empty}`
  switch (part.key) {
    case 'hangHoleType':
      return `${label} ${lookup.hangType(part.value as PlanogramHangHoleType)}`
    case 'source':
      return `${label} ${lookup.source(part.value as PlanogramSpecSource)}`
    case 'defaultHookGuid':
      return `${label} ${lookup.hookName(String(part.value))}`
    case 'unitWeightG':
      return `${label} ${part.value} g`
    case 'hookQtyOverrides': {
      const entries = Object.entries(part.value as Record<string, number>)
      return `${label} ${entries.length === 0 ? lookup.noOverrides : entries.map(([guid, qty]) => `${lookup.hookName(guid)} ×${qty}`).join('、')}`
    }
    default:
      return `${label} ${part.value}`
  }
}

/** 新建商品的缺省值（后端：吊孔偏移 10、吊孔类型 2 欧式挂孔、来源 1 人工量取）：新建明细里与缺省相同的项可以省略。 */
const CREATE_DEFAULTS: Partial<ProductSpecBatchChangeValues> = { hangHoleOffsetMm: 10, hangHoleType: 2, source: 1 }

/**
 * 明细一侧 → 每个字段一行的文字。没有任何项（create 的 before）返回空数组，界面显示「未设」。
 * omitCreateDefaults：新建的 after 省掉与缺省值相同的吊孔偏移 / 类型 / 来源，避免每个新建商品都刷出一屏相同的缺省。
 */
export function formatChangeLines(
  values: ProductSpecBatchChangeValues | null | undefined,
  lookup: ChangeTextLookup,
  side: 'before' | 'after',
  options: { omitCreateDefaults?: boolean } = {},
): string[] {
  return changeParts(values)
    .filter((part) => !(options.omitCreateDefaults && part.key in CREATE_DEFAULTS && part.value === CREATE_DEFAULTS[part.key as keyof ProductSpecBatchChangeValues]))
    .flatMap((part) => {
      // 挂数覆盖有多个挂钩型号时，每个型号一行（「挂数 名称 ×12」），比挤在一行里好读
      if (part.key === 'hookQtyOverrides') {
        const entries = Object.entries(part.value as Record<string, number>)
        if (entries.length > 1) return entries.map(([guid, qty]) => `${lookup.label('hookQtyOverrides')} ${lookup.hookName(guid)} ×${qty}`)
      }
      return [formatChangePart(part, lookup, side)]
    })
}

/** 明细一侧的整段文字（各项用「 · 」连接）；没有任何项（create 的 before）返回 null。 */
export function formatChangeSide(
  values: ProductSpecBatchChangeValues | null | undefined,
  lookup: ChangeTextLookup,
  side: 'before' | 'after',
): string | null {
  const lines = formatChangeLines(values, lookup, side)
  return lines.length === 0 ? null : lines.join(' · ')
}
