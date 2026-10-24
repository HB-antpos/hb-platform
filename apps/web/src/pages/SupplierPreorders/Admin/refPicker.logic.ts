// 参考商品（PR 2）的纯逻辑：参考列显示状态、得分与进价对比格式化、弹窗可用操作、
// 导入校对页人工指定 → refOverrides 组装、导入时找候选所需的季节上下文、去年历史轮询判定。
// 不依赖 React / antd，便于用 node 直接跑单测。
import type {
  SupplierPreorderFormItem,
  SupplierPreorderHeaderInfo,
  SupplierPreorderHistoryState,
  SupplierPreorderHistoryStatus,
  SupplierPreorderHolidayType,
  SupplierPreorderItemRef,
  SupplierPreorderPreviewItem,
  SupplierPreorderRefCandidate,
  SupplierPreorderRefOverride,
  SupplierPreorderRefSource,
} from '../../../types/supplierPreorder'

import { normalizeDateOnly } from './supplierPreorderAdmin.logic'

// ---------- 去年历史状态 ----------

/** 排队中 / 计算中时每 15 秒刷新一次订单表详情 */
export const HISTORY_POLL_INTERVAL_MS = 15_000

export function shouldPollHistory(status: SupplierPreorderHistoryState | null | undefined): boolean {
  return status === 'Queued' || status === 'Running'
}

/** 重新计算按钮：排队 / 计算中时不再重复排队 */
export function canRecomputeHistory(status: SupplierPreorderHistoryState | null | undefined): boolean {
  return !shouldPollHistory(status)
}

/** 历史窗口：优先快照记下的窗口，没有时退回订单表信息里设的历史对比季；两端缺一个就不显示。 */
export function getHistoryWindow(
  history: Pick<SupplierPreorderHistoryStatus, 'windowFrom' | 'windowTo'> | null | undefined,
  fallbackFrom?: string | null,
  fallbackTo?: string | null,
): { from: string; to: string } | null {
  const from = normalizeDateOnly(history?.windowFrom) ?? normalizeDateOnly(fallbackFrom)
  const to = normalizeDateOnly(history?.windowTo) ?? normalizeDateOnly(fallbackTo)
  return from && to ? { from, to } : null
}

/**
 * 商品页签据此判断要不要重新拉商品行：状态变化或完成时间变化 = 快照换了一版（去年销量、系统推荐都可能变）。
 * 订单表详情别的字段变化（例如人工指定后刷新计数）不触发商品表重拉。
 */
export function getHistoryVersionKey(history: Pick<SupplierPreorderHistoryStatus, 'status' | 'computedAtUtc'> | null | undefined): string {
  if (!history) return 'none'
  return `${history.status}|${history.computedAtUtc ?? ''}`
}

// ---------- 参考列显示状态 ----------

/**
 * - auto / manual：有参考商品（系统推荐 / 人工指定）
 * - cleared：人工清除，系统不再自动带上
 * - own：本品自己去年有销量，不需要参考
 * - pending：快照还没算出（ownSalesQty 为 null）
 * - none：需要参考但没有（突出显示）
 */
export type RefCellState = 'auto' | 'manual' | 'cleared' | 'own' | 'pending' | 'none'

export function getRefCellState(item: Pick<SupplierPreorderFormItem, 'ownSalesQty' | 'ref'>): RefCellState {
  const ref = item.ref
  // 有参考就优先显示参考（人工也可能给有历史的商品指定参考）
  if (ref && ref.productCode && (ref.source === 'Auto' || ref.source === 'Manual')) return ref.source === 'Auto' ? 'auto' : 'manual'
  if (ref && ref.source === 'Cleared') return 'cleared'
  const own = item.ownSalesQty
  if (own === null || own === undefined) return 'pending'
  return own > 0 ? 'own' : 'none'
}

/** 参考标签上显示的编号：优先我方货号，没有时用商品编码 */
export function getRefDisplayCode(ref: Pick<SupplierPreorderItemRef, 'itemNumber' | 'productCode'> | null | undefined): string {
  return ref?.itemNumber?.trim() || ref?.productCode?.trim() || ''
}

// ---------- 格式化 ----------

/** 得分保留 2 位小数：0.723 → "0.72"；空值 → "--" */
export function formatRefScore(score: number | null | undefined): string {
  if (score === null || score === undefined || !Number.isFinite(score)) return '--'
  return score.toFixed(2)
}

/** 本品进价相对参考进价的变化比例：(本品 − 参考) / 参考；任一缺失或参考为 0 返回 null */
export function getRefPriceDiffRate(refPrice: number | null | undefined, selfPrice: number | null | undefined): number | null {
  if (refPrice === null || refPrice === undefined || selfPrice === null || selfPrice === undefined) return null
  if (!Number.isFinite(refPrice) || !Number.isFinite(selfPrice) || refPrice <= 0) return null
  return (selfPrice - refPrice) / refPrice
}

// ---------- 弹窗可用操作 ----------

/**
 * - 清除并不再自动带上：已经是「已清除」时不再显示
 * - 交回系统推荐：只有人工指定或人工清除过才有意义（系统推荐 / 未设置时本来就由系统决定）
 */
export function getRefPickerActions(currentSource: SupplierPreorderRefSource | null | undefined): { canClear: boolean; canAuto: boolean } {
  return {
    canClear: currentSource !== 'Cleared',
    canAuto: currentSource === 'Manual' || currentSource === 'Cleared',
  }
}

// ---------- 导入校对页：人工指定 ----------

/** 导入校对页按原表行号记下的人工选择：选定某个商品，或「不要参考」 */
export type ImportRefChoice =
  | {
      kind: 'manual'
      productCode: string
      itemNumber?: string | null
      name: string
      purchasePrice?: number | null
      imageUrl?: string | null
      salesQty: number
    }
  | { kind: 'cleared' }

export function toImportRefChoice(candidate: SupplierPreorderRefCandidate): ImportRefChoice {
  return {
    kind: 'manual',
    productCode: candidate.productCode,
    itemNumber: candidate.itemNumber ?? null,
    name: candidate.name,
    purchasePrice: candidate.purchasePrice ?? null,
    imageUrl: candidate.imageUrl ?? null,
    salesQty: candidate.salesQty,
  }
}

/** 把本地选择转成弹窗认识的「当前参考」，便于复用同一个弹窗 */
export function importChoiceToItemRef(choice: ImportRefChoice | null | undefined): SupplierPreorderItemRef | null {
  if (!choice) return null
  if (choice.kind === 'cleared') return { source: 'Cleared', productCode: null }
  return {
    source: 'Manual',
    productCode: choice.productCode,
    itemNumber: choice.itemNumber ?? null,
    name: choice.name,
    purchasePrice: choice.purchasePrice ?? null,
    imageUrl: choice.imageUrl ?? null,
    salesQty: choice.salesQty,
  }
}

/**
 * 提交建表时的 refOverrides：只带校对预览里还存在、且能导入的行（换了映射后行状态可能变），按行号排序。
 * 「交回系统推荐」的行在本地直接删掉了，不会出现在这里。
 */
export function buildRefOverrides(
  choices: Record<number, ImportRefChoice | undefined>,
  previewItems: Pick<SupplierPreorderPreviewItem, 'rowNo' | 'status'>[],
): SupplierPreorderRefOverride[] {
  const importable = new Set(previewItems.filter((item) => item.status !== 'Invalid').map((item) => item.rowNo))
  return Object.entries(choices)
    .map(([rowNo, choice]) => ({ rowNo: Number(rowNo), choice }))
    .filter((entry): entry is { rowNo: number; choice: ImportRefChoice } => Boolean(entry.choice) && importable.has(entry.rowNo))
    .sort((a, b) => a.rowNo - b.rowNo)
    .map(({ rowNo, choice }) => ({ rowNo, productCode: choice.kind === 'manual' ? choice.productCode : null }))
}

/** 已匹配给本表各行的我方商品（去重）：不作参考候选 */
export function collectMatchedProductCodes(previewItems: Pick<SupplierPreorderPreviewItem, 'matchedProductCode' | 'status'>[]): string[] {
  const codes = new Set<string>()
  for (const item of previewItems) {
    if (item.status === 'Invalid') continue
    const code = item.matchedProductCode?.trim()
    if (code) codes.add(code)
  }
  return [...codes]
}

export interface ImportRefContext {
  localSupplierCode: string
  holidayType: SupplierPreorderHolidayType
  seasonYear: number
  historyFrom: string | null
  historyTo: string | null
}

export type ImportRefContextResult = { ok: true; context: ImportRefContext } | { ok: false; reason: 'supplier' | 'season' }

/**
 * 导入第 2 步找参考候选需要供应商、节日、年份：
 * 先取向导表单里的值（解析后已从表头回填，第 3 步改了也会跟着变），没有再退回解析结果的表头信息；
 * 节日或年份都拿不到时禁用「指定」并提示先选节日 / 年份。历史窗口两端齐了才传，否则让后端按默认算。
 */
export function resolveImportRefContext(input: {
  supplierCode?: string | null
  holidayType?: SupplierPreorderHolidayType | null
  seasonYear?: number | null
  historyFrom?: string | null
  historyTo?: string | null
  headerInfo?: SupplierPreorderHeaderInfo | null
}): ImportRefContextResult {
  const localSupplierCode = input.supplierCode?.trim()
  if (!localSupplierCode) return { ok: false, reason: 'supplier' }
  const holidayType = input.holidayType ?? input.headerInfo?.holidayType ?? null
  const rawYear = input.seasonYear ?? input.headerInfo?.seasonYear ?? null
  const seasonYear = typeof rawYear === 'number' && Number.isInteger(rawYear) && rawYear > 2000 ? rawYear : null
  if (!holidayType || !seasonYear) return { ok: false, reason: 'season' }
  const from = normalizeDateOnly(input.historyFrom)
  const to = normalizeDateOnly(input.historyTo)
  return {
    ok: true,
    context: {
      localSupplierCode,
      holidayType,
      seasonYear,
      historyFrom: from && to ? from : null,
      historyTo: from && to ? to : null,
    },
  }
}
