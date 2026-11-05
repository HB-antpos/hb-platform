import type {
  ConflictDto,
  HookModelDto,
  HookModelInput,
  PlanogramHangHoleType,
  PlanogramImportUnresolvedItem,
  PlanogramProngCount,
  PlanogramRowNumberingFrom,
  PlanogramSideType,
  PlanogramSpecSource,
  ProductDisplaySpecDto,
  ProductSpecListItemDto,
  ShelfModelDto,
  ShelfModelInput,
} from '../../../types/planogram'
import { normalizeQtyOverride, resolveHookQty, type HookQtyResult } from './engine/hookQty'
import { autoHoleGrid } from './engine/holeGrid'

/**
 * 分页状态记着「是在哪组筛选条件下翻到的这一页」：筛选条件（关键字 / 状态）变了，有效页码立刻回到第 1 页。
 * 用派生代替「条件变了再用 effect 改页码」：后者会先用旧页码 + 新条件发一次无用请求，再用第 1 页发第二次。
 */
export interface PagingState {
  page: number
  pageSize: number
  filterKey: string
}

export function effectivePage(paging: PagingState, filterKey: string): number {
  return paging.filterKey === filterKey ? paging.page : 1
}

/** 页面路由：本页是「商品与价格」合并页的页签，地址写进地址栏时只在本页激活且地址确实是本页时才同步。 */
export const PLANOGRAM_PATH = '/pos-admin/planograms'
/** 设计器子页面（批 2）：不进菜单，只从方案列表「打开」进入；每个方案一个页签（保活缓存键 = 路径）。 */
export const PLANOGRAM_DESIGNER_PATTERN = '/pos-admin/planograms/:guid/design'

export function planogramDesignerPath(guid: string): string {
  return `${PLANOGRAM_PATH}/${encodeURIComponent(guid)}/design`
}

export const DEFAULT_PAGE_SIZE = 20
export const PAGE_SIZE_OPTIONS = [20, 50, 100]
/** 搜索框输入后多久才真正发请求（毫秒）。 */
export const SEARCH_DEBOUNCE_MS = 350
/** 分类 / 等级条件变化后多久才真正发请求（毫秒）：连点几个分类时只发最后一次，比搜索框短，因为是离散的点选。 */
export const FILTER_DEBOUNCE_MS = 250
export const EMPTY_CELL = '—'

// ---------------------------------------------------------------------------
// 页签与地址栏
// ---------------------------------------------------------------------------

export type PlanogramTab = 'plans' | 'specs' | 'models'
export type ModelView = 'shelf' | 'hook'

export interface PlanogramPageQuery {
  tab: PlanogramTab
  view: ModelView
}

/** 从地址栏参数还原页签；未知值回到默认（陈列方案 / 货架型号）。 */
export function parsePlanogramSearch(search: string): PlanogramPageQuery {
  const params = new URLSearchParams(search.startsWith('?') ? search.slice(1) : search)
  const tab = params.get('tab')
  return {
    tab: tab === 'models' ? 'models' : tab === 'specs' ? 'specs' : 'plans',
    view: params.get('view') === 'hook' ? 'hook' : 'shelf',
  }
}

/** 默认值（陈列方案）不写进地址栏，地址保持干净；只有「货架与挂钩型号」页签才带 view。 */
export function serializePlanogramQuery(query: PlanogramPageQuery): string {
  const params = new URLSearchParams()
  if (query.tab === 'specs') {
    params.set('tab', 'specs')
  } else if (query.tab === 'models') {
    params.set('tab', 'models')
    if (query.view === 'hook') params.set('view', 'hook')
  }
  return params.toString()
}

/** 型号抽屉的打开方式：新建 / 编辑（行内数据）/ 复制为新型号（带入行内数据，编码留空）。 */
export type ModelDrawerTarget<T> =
  | { mode: 'create' }
  | { mode: 'edit'; model: T }
  | { mode: 'copy'; model: T }

/**
 * 抽屉「打开编号」分配器：同一个 target 对象始终得到同一个编号，换了对象（再次打开）就得到新的递增编号，null（关闭）保持上一个。
 * 抽屉用它当表单的 key：关闭动画期间（约 0.3 秒）立刻再打开另一个型号 / 商品时，Drawer 的 destroyOnHidden 还没来得及销毁表单，
 * 表单会残留上一次的输入，提交时却带着新目标的 guid，等于把旧数据写到别的记录上；换 key 强制整张表单（含手改孔数等内部状态）重建。
 */
export function createOpenKeyAllocator(): (target: object | null) => number {
  let counter = 0
  const ids = new WeakMap<object, number>()
  return (target) => {
    if (target === null) return counter
    let id = ids.get(target)
    if (id === undefined) {
      counter += 1
      id = counter
      ids.set(target, id)
    }
    return id
  }
}

// ---------------------------------------------------------------------------
// 错误解析：附录 A 的错误码 → 界面文案
// ---------------------------------------------------------------------------

// 文案键必须以带引号的字面量出现：契约测试按这个形式静态扫描，才能发现缺失键。
const ERROR_CODE_KEYS: Record<string, string> = {
  PLANOGRAM_VALIDATION: 'planogram.errors.validation',
  PLANOGRAM_NOT_FOUND: 'planogram.errors.notFound',
  PLANOGRAM_CODE_DUPLICATE: 'planogram.errors.codeDuplicate',
  PLANOGRAM_MODEL_IN_USE: 'planogram.errors.modelInUse',
  PLANOGRAM_SPEC_IN_USE: 'planogram.errors.specInUse',
  PLANOGRAM_MODEL_LOCKED: 'planogram.errors.modelLocked',
  PLANOGRAM_STALE: 'planogram.errors.stale',
  PLANOGRAM_ARCHIVED: 'planogram.errors.archived',
  PLANOGRAM_NOT_DELETABLE: 'planogram.errors.notDeletable',
  // 批 3（附录 C）
  PLANOGRAM_HAS_CONFLICTS: 'planogram.errors.hasConflicts',
  PLANOGRAM_EMPTY: 'planogram.errors.empty',
  PLANOGRAM_ALREADY_PUBLISHED: 'planogram.errors.alreadyPublished',
  PLANOGRAM_IMPORT_UNRESOLVED: 'planogram.errors.importUnresolved',
  PLANOGRAM_SNAPSHOT_INVALID: 'planogram.errors.snapshotInvalid',
  // 附录 D（商品陈列参数批量修改）
  PLANOGRAM_BATCH_TOO_MANY: 'planogram.errors.batchTooMany',
  PLANOGRAM_BATCH_EMPTY: 'planogram.errors.batchEmpty',
  PLANOGRAM_BATCH_CONFLICT: 'planogram.errors.batchConflict',
}

export interface FieldError {
  /** 后端给出的字段名（camelCase，大小写不敏感匹配表单字段）；没有字段名时为 null。 */
  field: string | null
  message: string
}

export type ResolvedPlanogramError =
  | { kind: 'abort' }
  | {
      kind: 'error'
      key: string
      code: string | null
      status: number | null
      serverMessage: string | null
      fieldErrors: FieldError[]
      /** 响应里原样的 details（冲突列表 / 解析不到的编码等结构化内容由调用方按错误码再解析）。 */
      details: unknown
    }

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

/**
 * PLANOGRAM_VALIDATION 的 details「给字段列表」，具体形状附录 A 没有定：
 * 这里兼容三种常见写法——字符串数组、{ field | name | property, message | error } 对象数组、{ 字段: 消息 } 字典。
 */
export function extractFieldErrors(details: unknown): FieldError[] {
  if (Array.isArray(details)) {
    return details.flatMap((item): FieldError[] => {
      if (typeof item === 'string') return item.trim() ? [{ field: null, message: item.trim() }] : []
      if (!isRecord(item)) return []
      const messageRaw = item.message ?? item.error ?? item.detail
      const fieldRaw = item.field ?? item.name ?? item.property
      const message = typeof messageRaw === 'string' ? messageRaw.trim() : ''
      if (!message) return []
      return [{ field: typeof fieldRaw === 'string' && fieldRaw ? fieldRaw : null, message }]
    })
  }
  if (isRecord(details)) {
    return Object.entries(details).flatMap(([field, value]): FieldError[] => {
      if (typeof value === 'string' && value.trim()) return [{ field, message: value.trim() }]
      if (Array.isArray(value) && typeof value[0] === 'string') return [{ field, message: value[0] }]
      return []
    })
  }
  return []
}

export function resolvePlanogramError(error: unknown): ResolvedPlanogramError {
  if (isRecord(error) && error.name === 'AbortError') return { kind: 'abort' }
  if (isRecord(error) && typeof error.status === 'number') {
    const payload = isRecord(error.payload) ? error.payload : {}
    const rawCode = payload.errorCode ?? payload.code
    const code = typeof rawCode === 'string' && rawCode ? rawCode : null
    const message = typeof payload.message === 'string' && payload.message.trim() ? payload.message.trim() : null
    const status = error.status as number
    let key = (code && ERROR_CODE_KEYS[code]) || ''
    if (!key) key = status === 403 ? 'planogram.errors.forbidden' : 'planogram.errors.unknown'
    return { kind: 'error', key, code, status, serverMessage: message, fieldErrors: extractFieldErrors(payload.details), details: payload.details ?? null }
  }
  // fetch 本身失败（断网、跨域、DNS）是 TypeError，没有响应。
  if (error instanceof TypeError) {
    return { kind: 'error', key: 'planogram.errors.network', code: null, status: null, serverMessage: null, fieldErrors: [], details: null }
  }
  return { kind: 'error', key: 'planogram.errors.unknown', code: null, status: null, serverMessage: null, fieldErrors: [], details: null }
}

/** 中文界面优先用服务端的具体中文 message；英文界面用错误码对应的英文文案。 */
export function planogramErrorText(
  resolved: Extract<ResolvedPlanogramError, { kind: 'error' }>,
  language: string,
  translate: (key: string) => string,
): string {
  if (language.toLowerCase().startsWith('zh') && resolved.serverMessage) return resolved.serverMessage
  return translate(resolved.key)
}

/** PLANOGRAM_HAS_CONFLICTS 的 details：冲突列表（后端最多带前 200 条）；形状不对的项丢掉。 */
export function extractConflictDetails(details: unknown): ConflictDto[] {
  if (!Array.isArray(details)) return []
  return details.flatMap((item): ConflictDto[] => {
    if (!isRecord(item) || typeof item.itemGuid !== 'string' || typeof item.code !== 'string') return []
    return [{
      itemGuid: item.itemGuid,
      otherItemGuid: typeof item.otherItemGuid === 'string' && item.otherItemGuid ? item.otherItemGuid : null,
      code: item.code,
      severity: item.severity === 'warn' ? 'warn' : 'error',
      message: typeof item.message === 'string' ? item.message : '',
    }]
  })
}

/** PLANOGRAM_IMPORT_UNRESOLVED 的 details：[{ kind: 'shelf' | 'hook' | 'product', code }]（去重）。 */
export function extractImportUnresolved(details: unknown): PlanogramImportUnresolvedItem[] {
  if (!Array.isArray(details)) return []
  const seen = new Set<string>()
  const result: PlanogramImportUnresolvedItem[] = []
  for (const item of details) {
    if (!isRecord(item) || typeof item.code !== 'string' || !item.code) continue
    const kind = item.kind === 'shelf' || item.kind === 'hook' || item.kind === 'product' ? item.kind : null
    if (!kind) continue
    const key = `${kind}:${item.code}`
    if (seen.has(key)) continue
    seen.add(key)
    result.push({ kind, code: item.code })
  }
  return result
}

// ---------------------------------------------------------------------------
// 时间与数字展示
// ---------------------------------------------------------------------------

/** ISO 时间不带时区标记时按 UTC 解析（后端审计列是 UTC）；带 Z / 偏移的原样解析。 */
export function parseIsoAsUtc(value: string): Date | null {
  const trimmed = value.trim()
  if (!trimmed) return null
  const hasZone = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(trimmed)
  const normalized = hasZone ? trimmed : `${trimmed.replace(' ', 'T')}Z`
  const date = new Date(normalized)
  return Number.isNaN(date.getTime()) ? null : date
}

const SYDNEY_PARTS = new Intl.DateTimeFormat('en-AU', {
  timeZone: 'Australia/Sydney',
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
})

/** 悉尼时间：列表用 MM-DD HH:mm，抽屉用 YYYY-MM-DD HH:mm；缺失 / 解析失败显示「—」。 */
export function formatUpdatedAt(value: string | null | undefined, withYear = false): string {
  if (!value) return EMPTY_CELL
  const date = parseIsoAsUtc(value)
  if (!date) return EMPTY_CELL
  const parts: Record<string, string> = {}
  for (const part of SYDNEY_PARTS.formatToParts(date)) parts[part.type] = part.value
  const day = `${parts.month}-${parts.day}`
  return `${withYear ? `${parts.year}-` : ''}${day} ${parts.hour}:${parts.minute}`
}

/** 数字去掉多余的尾零：25 → "25"，25.4 → "25.4"，25.40 → "25.4"。 */
export function formatNumber(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return EMPTY_CELL
  return String(Math.round(value * 100) / 100)
}

/** 价格：$4.00；千分位逗号。 */
export function formatMoney(value: number): string {
  const sign = value < 0 ? '-' : ''
  const [integer, fraction] = Math.abs(value).toFixed(2).split('.')
  return `${sign}$${integer.replace(/\B(?=(\d{3})+(?!\d))/g, ',')}.${fraction}`
}

/** 合计金额：整数金额不带小数（$1,291），否则两位（$1,291.50）。 */
export function formatMoneyTotal(value: number): string {
  const text = formatMoney(value)
  return text.endsWith('.00') ? text.slice(0, -3) : text
}

export function formatDimensions(values: Array<number | null | undefined>, unit = 'mm'): string {
  if (values.some((value) => typeof value !== 'number' || !Number.isFinite(value))) return EMPTY_CELL
  return `${values.map((value) => formatNumber(value as number)).join(' × ')} ${unit}`
}

// ---------------------------------------------------------------------------
// 货架型号表单
// ---------------------------------------------------------------------------

export interface ShelfFormValues {
  name: string
  code: string
  sideType: PlanogramSideType
  color: string
  source: string
  bayWidthMm: number | null
  totalHeightMm: number | null
  baseDepthMm: number | null
  baseTopHeightMm: number | null
  uprightWidthMm: number | null
  holePitchXMm: number | null
  holePitchYMm: number | null
  holeDiameterMm: number | null
  firstHoleLeftMm: number | null
  firstHoleTopMm: number | null
  holeCols: number | null
  holeRows: number | null
  rowNumberingFrom: PlanogramRowNumberingFrom
  isActive: boolean
}

/** 新建默认值：900 × 1200 单面洞洞板，孔距 / 首孔边距按决策 #1 默认 25（量取后随时可改）。 */
export function defaultShelfFormValues(): ShelfFormValues {
  return {
    name: '',
    code: '',
    sideType: 1,
    color: '',
    source: '',
    bayWidthMm: 900,
    totalHeightMm: 1200,
    baseDepthMm: 300,
    baseTopHeightMm: 150,
    uprightWidthMm: 30,
    holePitchXMm: 25,
    holePitchYMm: 25,
    holeDiameterMm: 6,
    firstHoleLeftMm: 25,
    firstHoleTopMm: 25,
    holeCols: 33,
    holeRows: 41,
    rowNumberingFrom: 1,
    isActive: true,
  }
}

export function shelfToFormValues(shelf: ShelfModelDto): ShelfFormValues {
  return {
    name: shelf.name,
    code: shelf.code,
    sideType: shelf.sideType,
    color: shelf.color ?? '',
    source: shelf.source ?? '',
    bayWidthMm: shelf.bayWidthMm,
    totalHeightMm: shelf.totalHeightMm,
    baseDepthMm: shelf.baseDepthMm,
    baseTopHeightMm: shelf.baseTopHeightMm,
    uprightWidthMm: shelf.uprightWidthMm,
    holePitchXMm: shelf.holePitchXMm,
    holePitchYMm: shelf.holePitchYMm,
    holeDiameterMm: shelf.holeDiameterMm,
    firstHoleLeftMm: shelf.firstHoleLeftMm,
    firstHoleTopMm: shelf.firstHoleTopMm,
    holeCols: shelf.holeCols,
    holeRows: shelf.holeRows,
    rowNumberingFrom: shelf.rowNumberingFrom,
    isActive: shelf.isActive,
  }
}

/** 复制为新型号：名称加副本后缀，编码留空（编码必须唯一，由使用者重新填写），其余尺寸照搬，复制出来的型号默认启用。 */
export function shelfCopyFormValues(shelf: ShelfModelDto, copySuffix: string): ShelfFormValues {
  return { ...shelfToFormValues(shelf), name: `${shelf.name}${copySuffix}`, code: '', isActive: true }
}

function numberOr(value: number | null | undefined, fallback = 0): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function trimmedOrNull(value: string | null | undefined): string | null {
  const trimmed = (value ?? '').trim()
  return trimmed ? trimmed : null
}

/** 孔数没填时按尺寸自动换算后一并提交（界面与后端口径一致，保存后不会看到两个数）。 */
export function shelfFormToInput(values: ShelfFormValues): ShelfModelInput {
  // 外形尺寸后端是整数列（int）：提交前取整兜底（粘贴 900.4 之类时不让整次保存因反序列化失败被拒）
  const geometry = {
    bayWidthMm: Math.round(numberOr(values.bayWidthMm)),
    totalHeightMm: Math.round(numberOr(values.totalHeightMm)),
    baseTopHeightMm: Math.round(numberOr(values.baseTopHeightMm)),
    uprightWidthMm: Math.round(numberOr(values.uprightWidthMm)),
    holePitchXMm: numberOr(values.holePitchXMm),
    holePitchYMm: numberOr(values.holePitchYMm),
    firstHoleLeftMm: numberOr(values.firstHoleLeftMm),
    firstHoleTopMm: numberOr(values.firstHoleTopMm),
    holeCols: 0,
    holeRows: 0,
  }
  const auto = autoHoleGrid(geometry)
  return {
    code: values.code.trim(),
    name: values.name.trim(),
    sideType: values.sideType,
    color: trimmedOrNull(values.color),
    source: trimmedOrNull(values.source),
    bayWidthMm: geometry.bayWidthMm,
    totalHeightMm: geometry.totalHeightMm,
    baseDepthMm: Math.round(numberOr(values.baseDepthMm)),
    baseTopHeightMm: geometry.baseTopHeightMm,
    uprightWidthMm: geometry.uprightWidthMm,
    holePitchXMm: geometry.holePitchXMm,
    holePitchYMm: geometry.holePitchYMm,
    holeDiameterMm: numberOr(values.holeDiameterMm),
    firstHoleLeftMm: geometry.firstHoleLeftMm,
    firstHoleTopMm: geometry.firstHoleTopMm,
    holeCols: numberOr(values.holeCols) >= 1 ? numberOr(values.holeCols) : auto.cols,
    holeRows: numberOr(values.holeRows) >= 1 ? numberOr(values.holeRows) : auto.rows,
    rowNumberingFrom: values.rowNumberingFrom,
    isActive: values.isActive,
  }
}

/** 启用 / 停用：用列表行里的完整数据原样提交，只翻转 isActive（PUT 是整体保存）。 */
export function shelfToggleInput(shelf: ShelfModelDto): ShelfModelInput {
  // 解构出来丢掉的是只读字段（TypeScript 对带 rest 的解构不报未使用变量）
  const { guid: _guid, referencedPlanCount: _count, updatedAt: _at, updatedBy: _by, ...rest } = shelf
  return { ...rest, isActive: !shelf.isActive }
}

// ---------------------------------------------------------------------------
// 挂钩型号表单
// ---------------------------------------------------------------------------

export interface HookFormValues {
  name: string
  code: string
  color: string
  prongCount: PlanogramProngCount
  hasPriceTagArm: boolean
  armLengthMm: number | null
  dropMm: number | null
  wireDiameterMm: number | null
  tipReserveMm: number | null
  tagWidthMm: number | null
  tagHeightMm: number | null
  tagBottomAboveArmMm: number | null
  holeSpanX: number | null
  holeSpanY: number | null
  loadKg: number | null
  isActive: boolean
}

/** 价签架默认尺寸与底边高度（附录 A：tagBottomAboveArmMm 默认 10；设计图价签架 52 × 26）。 */
export const DEFAULT_TAG = { widthMm: 52, heightMm: 26, bottomAboveArmMm: 10 } as const

/** 占孔默认：双钩横向占 2 孔，单钩 1 孔；纵向 1 孔。 */
export function defaultHoleSpanX(prongCount: PlanogramProngCount): number {
  return prongCount === 2 ? 2 : 1
}

export function defaultHookFormValues(): HookFormValues {
  return {
    name: '',
    code: '',
    color: '',
    prongCount: 2,
    hasPriceTagArm: true,
    armLengthMm: 200,
    dropMm: 50,
    wireDiameterMm: 5,
    tipReserveMm: 20,
    tagWidthMm: DEFAULT_TAG.widthMm,
    tagHeightMm: DEFAULT_TAG.heightMm,
    tagBottomAboveArmMm: DEFAULT_TAG.bottomAboveArmMm,
    holeSpanX: 2,
    holeSpanY: 1,
    loadKg: null,
    isActive: true,
  }
}

export function hookToFormValues(hook: HookModelDto): HookFormValues {
  return {
    name: hook.name,
    code: hook.code,
    color: hook.color ?? '',
    prongCount: hook.prongCount,
    hasPriceTagArm: hook.hasPriceTagArm,
    armLengthMm: hook.armLengthMm,
    dropMm: hook.dropMm,
    wireDiameterMm: hook.wireDiameterMm,
    tipReserveMm: hook.tipReserveMm,
    // 无价签臂的型号后端存 0；表单里回填默认值，使用者切到「有」时直接可用。
    tagWidthMm: hook.tagWidthMm > 0 ? hook.tagWidthMm : DEFAULT_TAG.widthMm,
    tagHeightMm: hook.tagHeightMm > 0 ? hook.tagHeightMm : DEFAULT_TAG.heightMm,
    tagBottomAboveArmMm: hook.tagBottomAboveArmMm > 0 ? hook.tagBottomAboveArmMm : DEFAULT_TAG.bottomAboveArmMm,
    holeSpanX: hook.holeSpanX,
    holeSpanY: hook.holeSpanY,
    loadKg: hook.loadKg ?? null,
    isActive: hook.isActive,
  }
}

export function hookCopyFormValues(hook: HookModelDto, copySuffix: string): HookFormValues {
  return { ...hookToFormValues(hook), name: `${hook.name}${copySuffix}`, code: '', isActive: true }
}

/** 没有价签臂时三个价签字段提交 0（附录 A：tag 三个字段可为 0）。 */
export function hookFormToInput(values: HookFormValues): HookModelInput {
  const hasTag = values.hasPriceTagArm
  return {
    code: values.code.trim(),
    name: values.name.trim(),
    color: trimmedOrNull(values.color),
    prongCount: values.prongCount,
    hasPriceTagArm: hasTag,
    armLengthMm: numberOr(values.armLengthMm),
    dropMm: numberOr(values.dropMm),
    wireDiameterMm: numberOr(values.wireDiameterMm),
    tipReserveMm: numberOr(values.tipReserveMm),
    tagWidthMm: hasTag ? numberOr(values.tagWidthMm) : 0,
    tagHeightMm: hasTag ? numberOr(values.tagHeightMm) : 0,
    tagBottomAboveArmMm: hasTag ? numberOr(values.tagBottomAboveArmMm) : 0,
    holeSpanX: numberOr(values.holeSpanX, defaultHoleSpanX(values.prongCount)),
    holeSpanY: numberOr(values.holeSpanY, 1),
    loadKg: typeof values.loadKg === 'number' && values.loadKg > 0 ? values.loadKg : null,
    isActive: values.isActive,
  }
}

export function hookToggleInput(hook: HookModelDto): HookModelInput {
  const { guid: _guid, referencedPlanCount: _count, updatedAt: _at, updatedBy: _by, ...rest } = hook
  return { ...rest, isActive: !hook.isActive }
}

// ---------------------------------------------------------------------------
// 商品陈列参数表单
// ---------------------------------------------------------------------------

export interface SpecFormValues {
  widthMm: number | null
  heightMm: number | null
  depthMm: number | null
  hangHoleOffsetMm: number | null
  hangHoleType: PlanogramHangHoleType
  unitWeightG: number | null
  source: PlanogramSpecSource
  defaultHookGuid: string | null
  /** 手填覆盖：{ hookGuid: 件数 | null }，空值 = 没有覆盖。 */
  overrides: Record<string, number | null>
}

/** 新补充参数时的默认：吊孔偏移 10（附录 A）、欧式挂孔（设计图示意）、人工量取。 */
export function defaultSpecFormValues(): SpecFormValues {
  return {
    widthMm: null,
    heightMm: null,
    depthMm: null,
    hangHoleOffsetMm: 10,
    hangHoleType: 2,
    unitWeightG: null,
    source: 1,
    defaultHookGuid: null,
    overrides: {},
  }
}

export function specToFormValues(spec: ProductDisplaySpecDto | null | undefined): SpecFormValues {
  if (!spec) return defaultSpecFormValues()
  return {
    widthMm: spec.widthMm > 0 ? spec.widthMm : null,
    heightMm: spec.heightMm > 0 ? spec.heightMm : null,
    depthMm: spec.depthMm > 0 ? spec.depthMm : null,
    hangHoleOffsetMm: spec.hangHoleOffsetMm,
    hangHoleType: spec.hangHoleType,
    unitWeightG: spec.unitWeightG ?? null,
    source: spec.source,
    defaultHookGuid: spec.defaultHookGuid ?? null,
    overrides: { ...spec.hookQtyOverrides },
  }
}

/**
 * 表单 → 保存请求。覆盖表只保留「型号还存在且值是 ≥ 1 的整数」的项（后端要求 hookGuid 必须存在、值 ≥ 1，
 * 否则整次保存被拒）；默认挂钩指向已不存在的型号时清空。备注界面上没有输入框，原样带回，避免保存时把它抹掉。
 */
export function specFormToDto(
  productCode: string,
  values: SpecFormValues,
  hooks: ReadonlyArray<Pick<HookModelDto, 'guid'>>,
  previous: ProductDisplaySpecDto | null | undefined,
): ProductDisplaySpecDto {
  const known = new Set(hooks.map((hook) => hook.guid))
  const hookQtyOverrides: Record<string, number> = {}
  for (const [guid, value] of Object.entries(values.overrides)) {
    const override = normalizeQtyOverride(value)
    if (override !== null && known.has(guid)) hookQtyOverrides[guid] = override
  }
  return {
    productCode,
    widthMm: numberOr(values.widthMm),
    heightMm: numberOr(values.heightMm),
    depthMm: numberOr(values.depthMm),
    hangHoleOffsetMm: numberOr(values.hangHoleOffsetMm, 10),
    hangHoleType: values.hangHoleType,
    unitWeightG: typeof values.unitWeightG === 'number' && values.unitWeightG > 0 ? values.unitWeightG : null,
    defaultHookGuid: values.defaultHookGuid && known.has(values.defaultHookGuid) ? values.defaultHookGuid : null,
    hookQtyOverrides,
    source: values.source,
    notes: previous?.notes ?? null,
  }
}

// ---------------------------------------------------------------------------
// 商品参数列表展示
// ---------------------------------------------------------------------------

/** 列表「每钩挂数」：按默认挂钩换算；没有默认挂钩 / 默认挂钩已不存在时返回 null（显示「—」）。 */
export function listHookQty(
  item: ProductSpecListItemDto,
  hooksByGuid: ReadonlyMap<string, HookModelDto>,
): { hook: HookModelDto; result: HookQtyResult } | null {
  const spec = item.spec
  if (!spec?.defaultHookGuid) return null
  const hook = hooksByGuid.get(spec.defaultHookGuid)
  if (!hook) return null
  return { hook, result: resolveHookQty(hook, spec.depthMm, spec.hookQtyOverrides) }
}

/** 抽屉里挂数表要列出的型号：全部启用的型号，加上本商品已经引用的停用型号（默认挂钩 / 有手填覆盖），避免已有设置被悄悄藏起来。 */
export function specTableHooks(
  hooks: readonly HookModelDto[],
  spec: Pick<ProductDisplaySpecDto, 'defaultHookGuid' | 'hookQtyOverrides'> | null | undefined,
): HookModelDto[] {
  const referenced = new Set<string>(Object.keys(spec?.hookQtyOverrides ?? {}))
  if (spec?.defaultHookGuid) referenced.add(spec.defaultHookGuid)
  return hooks.filter((hook) => hook.isActive || referenced.has(hook.guid))
}
