import type {
  StoreOrderDocumentType,
  StoreOrderReturnDisposition,
  StoreOrderReturnReason,
} from '../../../types/storeOrder'

/**
 * 分店订单「负数发货 = 退货」的纯逻辑（口径见 docs/plans/2026-10-21-store-order-returns-negative-qty.md）：
 * - 退货行 = 发货数量（allocQuantity）< 0，且订货数量恒为 0；订货数量永远 ≥ 0；
 * - 汇总拆成 发货金额（正数行）/ 退货冲减（负数行）/ 净额；GST 按净额算；
 * - 单据按净额切换：净额 < 0 开退款单（creditNote），否则仍是发票。
 * 这里只放无副作用的函数，页面与发票页共用，保证两处金额、单据类型同一口径。
 */

/** 退货原因码（与后端一致）：破损 / 发错货 / 多发 / 质量问题 / 临期 / 其他。 */
export const STORE_ORDER_RETURN_REASONS: readonly StoreOrderReturnReason[] = [
  'damaged',
  'wrongItem',
  'overShipped',
  'quality',
  'nearExpiry',
  'other',
]

/** 处置：0 回仓可售，1 报损不回库；仅作记录，不联动库存。 */
export const STORE_ORDER_RETURN_DISPOSITIONS: readonly StoreOrderReturnDisposition[] = [0, 1]

/** 退货备注与后端 ReturnNote 列（nvarchar(200)）等长。 */
export const STORE_ORDER_RETURN_NOTE_MAX_LENGTH = 200

/** 澳洲 GST 税率；与原页面 / 发票页的 0.1 同值，集中在这里避免各处硬编码。 */
export const STORE_ORDER_GST_RATE = 0.1

export function isStoreOrderReturnReason(value: unknown): value is StoreOrderReturnReason {
  return typeof value === 'string' && (STORE_ORDER_RETURN_REASONS as readonly string[]).includes(value)
}

export function isStoreOrderReturnDisposition(value: unknown): value is StoreOrderReturnDisposition {
  return value === 0 || value === 1
}

/** 退货行判定：只看发货数量是否为负（不另设布尔字段，方案 §9）。 */
export function isStoreOrderReturnLine(line: { allocQuantity?: number | null }): boolean {
  return Number(line.allocQuantity ?? 0) < 0
}

/**
 * 发货格能否录入负数：需要退货权限，且这一行订货为 0（订货 > 0 的行只能 ≥ 0，
 * 否则「少发」和「退货」会混成一行）。已经是退货行的行订货必为 0，自然满足。
 */
export function canEnterNegativeAllocQuantity(canReturnGoods: boolean, orderedQuantity: number | null | undefined): boolean {
  return canReturnGoods && Number(orderedQuantity ?? 0) <= 0
}

function toAmount(value: unknown) {
  const numeric = Number(value ?? 0)
  return Number.isFinite(numeric) ? numeric : 0
}

/** 避免 -0 显示成 “-0.00”。 */
function normalizeZero(value: number) {
  return value === 0 ? 0 : value
}

function roundCents(value: number) {
  return normalizeZero(Number(value.toFixed(2)))
}

/** GST 按净额算（同向冲减）：净额为负时 GST 也是负数。 */
export function computeStoreOrderGst(netAmount: number | null | undefined): number {
  return roundCents(toAmount(netAmount) * STORE_ORDER_GST_RATE)
}

/** 单据类型按净额（ex GST）判定，不按「有没有退货行」判定：净额 < 0 → 退款单，= 0 与 > 0 → 发票。 */
export function resolveStoreOrderDocumentType(netAmount: number | null | undefined): StoreOrderDocumentType {
  return Math.round(toAmount(netAmount) * 100) < 0 ? 'creditNote' : 'invoice'
}

/** 订单上的最终单据类型：优先用后端给的 documentType，旧后端没有时按净额推导。 */
export function resolveStoreOrderDocumentTypeOf(order: {
  documentType?: StoreOrderDocumentType | null
  totalAllocatedImportAmount?: number | null
  totalImportAmount?: number | null
}): StoreOrderDocumentType {
  if (order.documentType === 'creditNote' || order.documentType === 'invoice') {
    return order.documentType
  }
  return resolveStoreOrderDocumentType(order.totalAllocatedImportAmount ?? order.totalImportAmount ?? 0)
}

export interface StoreOrderDocumentTotals {
  /** 净额 ex GST（发货金额 + 退货冲减）。 */
  subTotal: number
  gst: number
  freight: number
  /** 含 GST 与运费的合计；退款单时为负数（应退分店）。 */
  total: number
  documentType: StoreOrderDocumentType
}

/** 发票 / 退款单页脚金额：与后端同一净额口径。 */
export function computeStoreOrderDocumentTotals(
  netAmount: number | null | undefined,
  freight: number | null | undefined,
): StoreOrderDocumentTotals {
  const subTotal = roundCents(toAmount(netAmount))
  const gst = computeStoreOrderGst(subTotal)
  const shipping = toAmount(freight)
  return {
    subTotal,
    gst,
    freight: shipping,
    total: roundCents(subTotal + gst + shipping),
    documentType: resolveStoreOrderDocumentType(subTotal),
  }
}

/** 发票明细拆成「发货」与「退货（调整项）」两段；发货 0 的行仍留在发货段（沿用原排序规则）。 */
export function splitStoreOrderInvoiceLines<T extends { allocQuantity?: number | null }>(items: readonly T[]) {
  const supplied: T[] = []
  const returned: T[] = []
  for (const item of items) {
    ;(isStoreOrderReturnLine(item) ? returned : supplied).push(item)
  }
  return { supplied, returned }
}

export interface StoreOrderSavedTotals {
  /** 净额（totalAllocatedImportAmount）。 */
  netAmount: number
  /** 正发货行金额；旧后端没有时回落为 净额 − 退货冲减。 */
  shipAmount?: number | null
  /** 退货行金额（≤ 0）；旧后端没有时视为 0。 */
  returnAmount?: number | null
  /** 净件数（totalAllocQuantity，含退货负数）。 */
  netQuantity: number
  /** 退回件数（正数）。 */
  returnQuantity?: number | null
  returnLineCount?: number | null
}

export interface StoreOrderDraftLine {
  detailGUID: string
  allocQuantity?: number | null
  importPrice?: number | null
  allocatedImportAmount?: number | null
  volume?: number | null
}

export interface StoreOrderDraftEdit {
  allocQuantity?: number
  importPrice?: number
}

export interface StoreOrderDraftTotals {
  shipAmount: number
  /** 退货冲减，≤ 0。 */
  returnAmount: number
  netAmount: number
  gst: number
  /** 发出去的件数（只统计正数）。 */
  shipQuantity: number
  /** 退回件数（正数）。 */
  returnQuantity: number
  returnLineCount: number
  /** 退回体积（m³，正数）；只有整单明细都已加载时才能算出，否则为 null。 */
  returnVolume: number | null
}

interface LineContribution {
  shipAmount: number
  returnAmount: number
  shipQuantity: number
  returnQuantity: number
  returnLineCount: number
  returnVolume: number
}

function lineContribution(alloc: number, amount: number, volume: number): LineContribution {
  const isReturn = alloc < 0
  const isShip = alloc > 0
  return {
    shipAmount: isShip ? amount : 0,
    returnAmount: isReturn ? amount : 0,
    shipQuantity: isShip ? alloc : 0,
    returnQuantity: isReturn ? -alloc : 0,
    returnLineCount: isReturn ? 1 : 0,
    returnVolume: isReturn ? volume * -alloc : 0,
  }
}

/**
 * 汇总卡口径：已保存的整单合计 + 当前页明细里未保存修改带来的差额，并按发货正负拆成
 * 发货金额 / 退货冲减 / 净额。GST 按净额。draft 只改本页已加载的行，其余行的数值已包含在 saved 里。
 * allLinesLoaded 表示本页是否就是整单全部明细（无筛选、未分页），只有这时才能从明细算退回体积。
 */
export function computeStoreOrderDraftTotals({
  saved,
  lines,
  edits,
  allLinesLoaded = false,
}: {
  saved: StoreOrderSavedTotals
  lines: readonly StoreOrderDraftLine[]
  edits: Readonly<Record<string, StoreOrderDraftEdit | undefined>>
  allLinesLoaded?: boolean
}): StoreOrderDraftTotals {
  const savedReturnAmount = toAmount(saved.returnAmount)
  const savedShipAmount = saved.shipAmount == null ? toAmount(saved.netAmount) - savedReturnAmount : toAmount(saved.shipAmount)
  const savedReturnQuantity = Math.max(0, toAmount(saved.returnQuantity))
  const savedShipQuantity = toAmount(saved.netQuantity) + savedReturnQuantity

  let shipAmount = savedShipAmount
  let returnAmount = savedReturnAmount
  let shipQuantity = savedShipQuantity
  let returnQuantity = savedReturnQuantity
  let returnLineCount = Math.max(0, toAmount(saved.returnLineCount))
  let draftReturnVolume = 0

  for (const line of lines) {
    const savedAlloc = toAmount(line.allocQuantity)
    const savedPrice = toAmount(line.importPrice)
    const savedAmount = line.allocatedImportAmount == null ? savedAlloc * savedPrice : toAmount(line.allocatedImportAmount)
    const volume = toAmount(line.volume)
    const edit = edits[line.detailGUID]
    const before = lineContribution(savedAlloc, savedAmount, volume)

    if (!edit || (edit.allocQuantity === undefined && edit.importPrice === undefined)) {
      draftReturnVolume += before.returnVolume
      continue
    }
    const draftAlloc = edit.allocQuantity ?? savedAlloc
    const draftPrice = edit.importPrice ?? savedPrice
    const after = lineContribution(draftAlloc, draftAlloc * draftPrice, volume)
    draftReturnVolume += after.returnVolume
    shipAmount += after.shipAmount - before.shipAmount
    returnAmount += after.returnAmount - before.returnAmount
    shipQuantity += after.shipQuantity - before.shipQuantity
    returnQuantity += after.returnQuantity - before.returnQuantity
    returnLineCount += after.returnLineCount - before.returnLineCount
  }

  const netAmount = roundCents(shipAmount + returnAmount)
  return {
    shipAmount: roundCents(shipAmount),
    returnAmount: roundCents(returnAmount),
    netAmount,
    gst: computeStoreOrderGst(netAmount),
    shipQuantity: Math.max(0, shipQuantity),
    returnQuantity: Math.max(0, returnQuantity),
    returnLineCount: Math.max(0, returnLineCount),
    // 退回体积按明细单件体积算，只有本页就是整单时才可靠；否则宁可不显示也不显示不完整的数。
    returnVolume: allLinesLoaded ? draftReturnVolume : null,
  }
}

export type StoreOrderReturnBannerTone = 'credit' | 'invoice' | 'zero'

export interface StoreOrderReturnBanner {
  tone: StoreOrderReturnBannerTone
  returnLineCount: number
  returnQuantity: number
  netAmount: number
}

/**
 * 整单提示条：只要有退货行才出现（没有退货行时返回 null，页面与改动前完全一致）。
 * tone 按净额：< 0 红色退款单，> 0 发票含调整项，= 0 提示「净额为 0」。
 */
export function buildStoreOrderReturnBanner(totals: Pick<StoreOrderDraftTotals, 'returnLineCount' | 'returnQuantity' | 'netAmount'>): StoreOrderReturnBanner | null {
  if (totals.returnLineCount <= 0) {
    return null
  }
  const cents = Math.round(totals.netAmount * 100)
  return {
    tone: cents < 0 ? 'credit' : cents === 0 ? 'zero' : 'invoice',
    returnLineCount: totals.returnLineCount,
    returnQuantity: totals.returnQuantity,
    netAmount: totals.netAmount,
  }
}

export interface StoreOrderReturnLinePayloadFields {
  returnReason?: StoreOrderReturnReason
  returnDisposition?: StoreOrderReturnDisposition
  returnNote?: string
}

/**
 * 写入时的退货三字段：只有发货数为负才带，且备注去空白后为空则不传；
 * 发货数 ≥ 0 一律不带（后端只允许退货行携带退货字段）。
 */
export function buildReturnPayloadFields(
  allocQuantity: number | null | undefined,
  info: {
    returnReason?: StoreOrderReturnReason | null
    returnDisposition?: StoreOrderReturnDisposition | null
    returnNote?: string | null
  },
): StoreOrderReturnLinePayloadFields {
  if (Number(allocQuantity ?? 0) >= 0) {
    return {}
  }
  const note = info.returnNote?.trim()
  return {
    ...(info.returnReason ? { returnReason: info.returnReason } : {}),
    ...(isStoreOrderReturnDisposition(info.returnDisposition) ? { returnDisposition: info.returnDisposition } : {}),
    ...(note ? { returnNote: note.slice(0, STORE_ORDER_RETURN_NOTE_MAX_LENGTH) } : {}),
  }
}

/** 整单保存前的拦截：返回第一条「发货为负却没有有效退货原因」的行；没有问题返回 undefined。 */
export function findPayloadMissingReturnReason<T extends { quantity?: number; returnReason?: unknown }>(
  payloads: readonly T[],
): T | undefined {
  return payloads.find((item) => item.quantity !== undefined && item.quantity < 0 && !isStoreOrderReturnReason(item.returnReason))
}

export type StoreOrderReturnLineConflict = 'none' | 'ordered' | 'shipped' | 'existingReturn'

/**
 * 「添加退货商品」时该商品在本单是否已有行：
 * 一个订单一个商品一行（现有明细按订单 + 商品唯一定位），所以
 * - 有订货（quantity > 0）→ ordered：禁用提交，提示退到另一张订单或新建退货单；
 * - 没订货但已有正发货（主动配货）→ shipped：同样禁用，否则会把已发的货覆盖成退货；
 * - 已经是退货行 → existingReturn：允许，提交会覆盖这一行的数量与原因；
 * - 本单没有这个商品 → none。
 */
export function resolveReturnLineConflict(
  existing: { quantity?: number | null; allocQuantity?: number | null } | null | undefined,
): StoreOrderReturnLineConflict {
  if (!existing) {
    return 'none'
  }
  if (Number(existing.quantity ?? 0) > 0) {
    return 'ordered'
  }
  const alloc = Number(existing.allocQuantity ?? 0)
  if (alloc > 0) {
    return 'shipped'
  }
  return alloc < 0 ? 'existingReturn' : 'none'
}

export interface StoreOrderReturnPreview {
  /** 本行冲减 ex GST（≤ 0）。 */
  amount: number
  gst: number
  /** 本行含 GST 合计（≤ 0）。 */
  total: number
  /** 加入后整单净额 ex GST。 */
  netAfter: number
}

/**
 * 「添加退货商品」弹窗的冲减金额预览。
 * 若该商品在本单已有退货行（existingReturnAmount ≤ 0），提交会覆盖它，所以整单净额先扣掉旧金额。
 */
export function computeReturnLinePreview({
  quantity,
  unitPrice,
  currentNetAmount,
  existingReturnAmount = 0,
}: {
  quantity: number | null | undefined
  unitPrice: number | null | undefined
  currentNetAmount: number | null | undefined
  existingReturnAmount?: number | null
}): StoreOrderReturnPreview {
  const qty = Math.max(0, Math.floor(toAmount(quantity)))
  const amount = roundCents(-qty * Math.max(0, toAmount(unitPrice)))
  const gst = computeStoreOrderGst(amount)
  return {
    amount,
    gst,
    total: roundCents(amount + gst),
    netAfter: roundCents(toAmount(currentNetAmount) - toAmount(existingReturnAmount) + amount),
  }
}

/** 退货数量输入是正数，写入时记为 发货 −N；非法或 ≤ 0 返回 null（不能提交）。 */
export function toReturnAllocQuantity(quantity: number | null | undefined): number | null {
  const qty = Math.floor(toAmount(quantity))
  return qty > 0 ? -qty : null
}

/**
 * 配货单（拣货）只列要拣的货：去掉退货行，总发货数量改成只统计正数（接口的 totalAllocQuantity 是含退货的净件数，
 * 加回 totalReturnQuantity）。没有退货行时原样返回同一个对象，配货单与改动前完全一致。
 */
export function withoutStoreOrderReturnLines<
  T extends { items: readonly { allocQuantity?: number | null }[]; totalAllocQuantity?: number | null; totalReturnQuantity?: number | null; returnLineCount?: number | null },
>(order: T): T {
  const items = order.items.filter((item) => !isStoreOrderReturnLine(item))
  if (items.length === order.items.length && !(Number(order.returnLineCount ?? 0) > 0)) {
    return order
  }
  const returnedQuantity = Math.max(0, toAmount(order.totalReturnQuantity))
  return {
    ...order,
    items,
    ...(order.totalAllocQuantity == null ? {} : { totalAllocQuantity: toAmount(order.totalAllocQuantity) + returnedQuantity }),
  }
}
