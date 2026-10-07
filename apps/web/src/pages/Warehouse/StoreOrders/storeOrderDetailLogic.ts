import { StoreOrderFlowStatus } from '../../../types/storeOrder'
import type { StoreOrderDetail, StoreOrderDetailColumnFilters } from '../../../types/storeOrder'

/**
 * 订货明细页重设计的纯展示逻辑：概况卡主操作、三步进度、订单信息草稿差异、
 * 明细行发货差异、未保存修改统计、已生效筛选条等。全部只用接口已有字段在前端推导，
 * 不改任何保存、权限、请求守卫语义。
 */

export type StoreOrderDetailPrimaryAction = 'startPicking' | 'completeOrder'

export interface StoreOrderDetailFlowActions {
  /** 概况卡右侧唯一的主按钮；为空表示当前状态没有主操作。 */
  primary: StoreOrderDetailPrimaryAction | null
  /** 「完成订单」不是主按钮但仍可用时（已提交可直接完成），收进 ⋯ 菜单保留原入口。 */
  completeInMoreMenu: boolean
}

/**
 * 概况卡按状态只给一个主操作：已提交 → 开始配货，配货中 → 完成订单，其余状态没有主操作。
 * 原页面已提交时「开始配货」「完成订单」并排可点，这里把已提交的「完成订单」收进 ⋯ 菜单，入口不丢。
 * 只有订货管理权限（M）才有这些写操作。
 */
export function resolveStoreOrderDetailFlowActions(
  flowStatus: StoreOrderFlowStatus | null | undefined,
  canManage: boolean,
): StoreOrderDetailFlowActions {
  if (!canManage) {
    return { primary: null, completeInMoreMenu: false }
  }
  if (flowStatus === StoreOrderFlowStatus.Submitted) {
    return { primary: 'startPicking', completeInMoreMenu: true }
  }
  if (flowStatus === StoreOrderFlowStatus.Picking) {
    return { primary: 'completeOrder', completeInMoreMenu: false }
  }
  return { primary: null, completeInMoreMenu: false }
}

export type StoreOrderProgressStepKey = 'submitted' | 'picking' | 'completed'
export type StoreOrderProgressStepState = 'done' | 'active' | 'pending'

export interface StoreOrderProgressStep {
  key: StoreOrderProgressStepKey
  state: StoreOrderProgressStepState
  /** 订单当前所处的步骤（aria-current="step"）。 */
  current: boolean
}

const PROGRESS_STEP_KEYS: StoreOrderProgressStepKey[] = ['submitted', 'picking', 'completed']

/**
 * 三步进度：已提交 → 配货中 → 已完成。只表达订单当前处在哪一步；
 * 接口没有各步骤的发生时间，页面上只配订货日期/出库日期与固定说明，不编造时间。
 * 购物车（未提交）或未知状态时三步都未开始。
 */
export function buildStoreOrderProgressSteps(flowStatus: StoreOrderFlowStatus | null | undefined): StoreOrderProgressStep[] {
  const currentIndex =
    flowStatus === StoreOrderFlowStatus.Submitted
      ? 0
      : flowStatus === StoreOrderFlowStatus.Picking
        ? 1
        : flowStatus === StoreOrderFlowStatus.Completed
          ? 2
          : -1
  const isCompleted = flowStatus === StoreOrderFlowStatus.Completed

  return PROGRESS_STEP_KEYS.map((key, index) => ({
    key,
    // 已完成是终态：当前步也算「已完成」（绿色），不再显示为进行中。
    state:
      index < currentIndex || (isCompleted && index === currentIndex)
        ? 'done'
        : index === currentIndex
          ? 'active'
          : 'pending',
    current: index === currentIndex,
  }))
}

/** 订单信息卡可编辑字段的草稿，与原「订单头」表单一一对应。 */
export interface StoreOrderHeaderDraft {
  storeCode?: string
  orderDate?: string
  outboundDate?: string
  shippingFee?: number
  address: string
  contactEmail: string
  remarks: string
}

export type StoreOrderHeaderDraftField = keyof StoreOrderHeaderDraft

type StoreOrderHeaderSource = Pick<
  StoreOrderDetail,
  'storeCode' | 'orderDate' | 'outboundDate' | 'shippingFee' | 'storeAddress' | 'storeContactEmail' | 'remarks'
>

/** 由明细接口结果生成订单信息草稿；加载成功与「撤销」都用它，保证两处口径一致。 */
export function buildStoreOrderHeaderDraft(detail: StoreOrderHeaderSource | null | undefined): StoreOrderHeaderDraft {
  return {
    storeCode: detail?.storeCode,
    orderDate: detail?.orderDate,
    outboundDate: detail?.outboundDate,
    shippingFee: detail?.shippingFee,
    address: detail?.storeAddress || '',
    contactEmail: detail?.storeContactEmail || '',
    remarks: detail?.remarks || '',
  }
}

function normalizeDateValue(value?: string) {
  // 订货日期改动后会存成 ISO 字符串、出库日期存 YYYY-MM-DD；两者都只比较日期部分。
  return value ? value.slice(0, 10) : ''
}

function normalizeOptionalNumber(value?: number | null) {
  return value === undefined || value === null || Number.isNaN(Number(value)) ? null : Number(value)
}

/** 找出订单信息里改过但未保存的字段；为空表示没有未保存修改（不显示保存按钮）。 */
export function diffStoreOrderHeaderDraft(
  draft: StoreOrderHeaderDraft,
  baseline: StoreOrderHeaderDraft,
): StoreOrderHeaderDraftField[] {
  const dirty: StoreOrderHeaderDraftField[] = []
  if ((draft.storeCode || '') !== (baseline.storeCode || '')) dirty.push('storeCode')
  if (normalizeDateValue(draft.orderDate) !== normalizeDateValue(baseline.orderDate)) dirty.push('orderDate')
  if (normalizeDateValue(draft.outboundDate) !== normalizeDateValue(baseline.outboundDate)) dirty.push('outboundDate')
  if (normalizeOptionalNumber(draft.shippingFee) !== normalizeOptionalNumber(baseline.shippingFee)) dirty.push('shippingFee')
  if ((draft.address || '') !== (baseline.address || '')) dirty.push('address')
  if ((draft.contactEmail || '') !== (baseline.contactEmail || '')) dirty.push('contactEmail')
  if ((draft.remarks || '') !== (baseline.remarks || '')) dirty.push('remarks')
  return dirty
}

export type StoreOrderDetailAllocDiff =
  | { kind: 'match' }
  | { kind: 'unshipped' }
  | { kind: 'short'; amount: number }
  | { kind: 'extra'; amount: number }

/**
 * 发货数输入框下方的小字：有订货却发 0 → 未发；少于订货 → 少发 N；多于订货（含没订货的主动配货）→ 主动配货 +N。
 * 「未发」「主动配货」与页签统计同一口径（订货 > 0 且发货 = 0；订货 ≤ 0 且发货 > 0 也落在 extra 里）。
 */
export function describeStoreOrderDetailAllocDiff(
  quantity: number | null | undefined,
  allocQuantity: number | null | undefined,
): StoreOrderDetailAllocDiff {
  const ordered = Number(quantity ?? 0)
  const allocated = Number(allocQuantity ?? 0)
  if (ordered > 0 && allocated === 0) {
    return { kind: 'unshipped' }
  }
  if (allocated < ordered) {
    return { kind: 'short', amount: ordered - allocated }
  }
  if (allocated > ordered) {
    return { kind: 'extra', amount: allocated - Math.max(0, ordered) }
  }
  return { kind: 'match' }
}

export interface StoreOrderEditedLineDraftSummary {
  lineCount: number
  allocQuantityCount: number
  importPriceCount: number
}

/** 吸底「未保存修改」条的统计：N 行（发货数 a 处 · 进口价 b 处），输入为整单保存将提交的 payload。 */
export function summarizeStoreOrderEditedLines(
  payloads: readonly { quantity?: number; importPriceChanged: boolean }[],
): StoreOrderEditedLineDraftSummary {
  return {
    lineCount: payloads.length,
    allocQuantityCount: payloads.filter((item) => item.quantity !== undefined).length,
    importPriceCount: payloads.filter((item) => item.importPriceChanged).length,
  }
}

export interface StoreOrderShipProgress {
  /** 发货数量 / 订货数量 的百分比；订货数量为 0 时无法计算，返回 null。 */
  percent: number | null
  /** 进度条宽度（0–100）；主动配货可能超过 100%，进度条封顶。 */
  barPercent: number
}

export function computeStoreOrderShipProgress(
  totalQuantity: number | null | undefined,
  totalAllocQuantity: number | null | undefined,
): StoreOrderShipProgress {
  const ordered = Number(totalQuantity ?? 0)
  const allocated = Number(totalAllocQuantity ?? 0)
  if (!(ordered > 0)) {
    return { percent: null, barPercent: allocated > 0 ? 100 : 0 }
  }
  const percent = Math.round((allocated / ordered) * 100)
  return { percent, barPercent: Math.min(100, Math.max(0, percent)) }
}

export type StoreOrderDetailFilterChipField =
  | 'itemNumber'
  | 'productName'
  | 'barcode'
  | 'locationCode'
  | 'quantity'
  | 'allocQuantity'
  | 'importPrice'
  | 'isActive'

export interface StoreOrderDetailFilterChip {
  field: StoreOrderDetailFilterChipField
  text?: string
  min?: number
  max?: number
  isActive?: boolean
  /** 移除这个条件时要清掉的列头筛选键。 */
  removeKeys: (keyof StoreOrderDetailColumnFilters)[]
}

const TEXT_FILTER_FIELDS = ['itemNumber', 'productName', 'barcode', 'locationCode'] as const
const RANGE_FILTER_FIELDS = [
  { field: 'quantity', min: 'quantityMin', max: 'quantityMax' },
  { field: 'allocQuantity', min: 'allocQuantityMin', max: 'allocQuantityMax' },
  { field: 'importPrice', min: 'importPriceMin', max: 'importPriceMax' },
] as const

/** 把列头里生效的条件汇总成已生效筛选条的标签（原先列头放大镜里的条件在界面上看不见）。 */
export function buildStoreOrderDetailFilterChips(
  filters: StoreOrderDetailColumnFilters | null | undefined,
): StoreOrderDetailFilterChip[] {
  if (!filters) {
    return []
  }
  const chips: StoreOrderDetailFilterChip[] = []
  for (const field of TEXT_FILTER_FIELDS) {
    const text = filters[field]?.trim()
    if (text) {
      chips.push({ field, text, removeKeys: [field] })
    }
  }
  for (const range of RANGE_FILTER_FIELDS) {
    const min = filters[range.min]
    const max = filters[range.max]
    const hasMin = typeof min === 'number' && Number.isFinite(min)
    const hasMax = typeof max === 'number' && Number.isFinite(max)
    if (hasMin || hasMax) {
      chips.push({
        field: range.field,
        min: hasMin ? min : undefined,
        max: hasMax ? max : undefined,
        removeKeys: [range.min, range.max],
      })
    }
  }
  if (typeof filters.isActive === 'boolean') {
    chips.push({ field: 'isActive', isActive: filters.isActive, removeKeys: ['isActive'] })
  }
  return chips
}

/** 数值区间的可读摘要：1 – 5 / ≥ 1 / ≤ 5。 */
export function formatStoreOrderDetailRange(min?: number, max?: number) {
  if (min !== undefined && max !== undefined) {
    return `${min} – ${max}`
  }
  if (min !== undefined) {
    return `≥ ${min}`
  }
  if (max !== undefined) {
    return `≤ ${max}`
  }
  return ''
}

export type StoreOrderLineAssigneeState = 'assigned' | 'unassigned' | 'none'

/**
 * 「货位 · 拣货」第二行：已分配显示负责人；整单做过分配但这一行不在任何分段里（加行后未重新分配）显示「未分配」；
 * 整单还没分配过时不显示，避免每行都是「未分配」的噪音。
 */
export function resolveStoreOrderLineAssigneeState(
  lineAssignees: Readonly<Record<string, unknown>>,
  detailGUID: string,
): StoreOrderLineAssigneeState {
  if (Object.prototype.hasOwnProperty.call(lineAssignees, detailGUID)) {
    return 'assigned'
  }
  return Object.keys(lineAssignees).length > 0 ? 'unassigned' : 'none'
}

/**
 * 预计销售额只按当前已加载的这一页明细求和（原逻辑不变）；有筛选或整单超过一页时，
 * 它不再是整单数，文案必须标成「本页」。
 */
export function isStoreOrderDetailPageScopedSum({
  itemsTotal,
  pageItemCount,
  hasActiveFilters,
}: {
  itemsTotal: number
  pageItemCount: number
  hasActiveFilters: boolean
}) {
  return hasActiveFilters || itemsTotal > pageItemCount
}

/**
 * 「全部」页签计数：没有任何筛选时就是当前结果总行数；有筛选（关键字/页签/列头）时结果总数变成筛选后的行数，
 * 改用整单 SKU 数，与另外两个页签的整单统计口径一致；具体筛到多少行由「当前显示 N 行」给出。
 */
export function resolveStoreOrderDetailAllCount({
  itemsTotal,
  totalSKU,
  hasActiveFilters,
}: {
  itemsTotal: number
  totalSKU?: number | null
  hasActiveFilters: boolean
}) {
  if (!hasActiveFilters) {
    return itemsTotal
  }
  return typeof totalSKU === 'number' ? totalSKU : itemsTotal
}

/**
 * 列显示/隐藏（订货体积列）后决定列顺序的合并基准：已有列的相对顺序与默认一致（用户没拖过）时直接用新的默认顺序，
 * 避免新出现的列被追加到末尾、又被误判为「已自定义列布局」；拖过列序的保留原顺序，新列由合并逻辑补到末尾。
 */
export function resolveStoreOrderDetailColumnOrderBase<T extends string>(base: unknown, defaultOrder: readonly T[]): unknown {
  if (!Array.isArray(base)) {
    return base
  }
  const defaultSet = new Set<string>(defaultOrder)
  const baseShared: string[] = []
  for (const key of base) {
    if (typeof key === 'string' && defaultSet.has(key) && !baseShared.includes(key)) {
      baseShared.push(key)
    }
  }
  const defaultShared = defaultOrder.filter((key) => baseShared.includes(key))
  const followsDefault = baseShared.every((key, index) => key === defaultShared[index])
  return followsDefault ? [...defaultOrder] : base
}
