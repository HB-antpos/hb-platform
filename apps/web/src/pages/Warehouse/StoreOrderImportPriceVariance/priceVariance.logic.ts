import type {
  StoreOrderImportPriceVarianceSummary,
  StoreOrderImportPriceVarianceSupplierSummary,
} from '../../../types/storeOrder'

// 首次货柜价差异统计页的派生显示逻辑：只用接口已有字段在前端计算。

/** 供应商卡默认只显示前几个，其余点「展开全部」。 */
export const SUPPLIER_PREVIEW_COUNT = 6

/** 金额用真正的减号（−）而不是连字符，和「+」等宽对齐。 */
export const MINUS_SIGN = '−'

/** 差额方向配色：多收用橙、少收用蓝（明度不同，色弱也能区分），替代原来的红 / 绿。 */
export const VARIANCE_TONE_COLORS = {
  over: '#b4400a',
  under: '#1d4ed8',
} as const

export type VarianceTone = 'over' | 'under' | 'none'

/** 绝对值小于半分钱视为零，避免浮点误差显示成「−$0.00」。 */
function isZeroAmount(value: number) {
  return Math.abs(value) < 0.005
}

function formatAbsolute(value: number) {
  return Math.abs(value).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}

/** 「$1,234.56」，负数为「−$1,234.56」；空值显示「--」。 */
export function formatAmount(value: number | null | undefined, symbol = '$') {
  if (value === null || value === undefined || !Number.isFinite(value)) return '--'
  return `${!isZeroAmount(value) && value < 0 ? MINUS_SIGN : ''}${symbol}${formatAbsolute(value)}`
}

/** 带符号的差额：「+$1,234.56」/「−$1,234.56」/「$0.00」。 */
export function formatSignedAmount(value: number | null | undefined, symbol = '$') {
  const amount = value ?? 0
  if (!Number.isFinite(amount) || isZeroAmount(amount)) return `${symbol}0.00`
  return `${amount > 0 ? '+' : MINUS_SIGN}${symbol}${formatAbsolute(amount)}`
}

/** 数量：整数不带小数，非整数保留两位，带千分位。 */
export function formatQuantity(value: number | null | undefined) {
  const amount = value ?? 0
  return Number.isInteger(amount)
    ? amount.toLocaleString('en-US')
    : amount.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}

export function getVarianceTone(value: number | null | undefined): VarianceTone {
  const amount = value ?? 0
  if (!Number.isFinite(amount) || isZeroAmount(amount)) return 'none'
  return amount > 0 ? 'over' : 'under'
}

export function getMaxAbsVariance(values: ReadonlyArray<number | null | undefined>) {
  return values.reduce<number>((max, value) => Math.max(max, Math.abs(value ?? 0) || 0), 0)
}

/**
 * 双向条宽度（百分比，各自占条的一半）：正数画在中线右侧（多收），负数画在左侧（少收），
 * 长度按当前列表里绝对值最大的一项归一。
 */
export function getVarianceBarWidths(value: number | null | undefined, maxAbs: number) {
  const amount = value ?? 0
  if (!(maxAbs > 0) || !Number.isFinite(amount) || isZeroAmount(amount)) return { over: 0, under: 0 }
  const percent = Math.min(100, Math.round((Math.abs(amount) / maxAbs) * 1000) / 10)
  return amount > 0 ? { over: percent, under: 0 } : { over: 0, under: percent }
}

export interface SupplierTotals {
  /** 明细行数（各供应商明细数之和）。商品汇总的 totalRows 是商品数，不是明细行数。 */
  detailRows: number
  increaseTotal: number
  /** 少收合计，正数（接口返回的就是少收金额的绝对值）。 */
  decreaseTotal: number
}

/**
 * 用供应商汇总推出全局的明细行 / 多收 / 少收合计。
 * 接口返回的是当前筛选下的全部供应商（不分页），但仍核对一遍：
 * 各供应商的原始金额与净差额之和必须与总汇总一致，否则说明供应商列表不是全量，返回 null（界面不显示这几格）。
 */
export function summarizeSupplierTotals(
  summary: Pick<StoreOrderImportPriceVarianceSummary, 'originalImportAmountTotal' | 'varianceAmountTotal'>,
  suppliers: readonly StoreOrderImportPriceVarianceSupplierSummary[],
): SupplierTotals | null {
  const tolerance = 0.01 * Math.max(1, suppliers.length)
  const originalSum = suppliers.reduce((sum, row) => sum + (row.originalImportAmountTotal || 0), 0)
  const netSum = suppliers.reduce((sum, row) => sum + (row.varianceAmountTotal || 0), 0)
  if (
    Math.abs(originalSum - (summary.originalImportAmountTotal || 0)) > tolerance ||
    Math.abs(netSum - (summary.varianceAmountTotal || 0)) > tolerance
  ) {
    return null
  }
  return {
    detailRows: suppliers.reduce((sum, row) => sum + (row.detailCount || 0), 0),
    increaseTotal: suppliers.reduce((sum, row) => sum + (row.increaseVarianceAmountTotal || 0), 0),
    decreaseTotal: suppliers.reduce((sum, row) => sum + (row.decreaseVarianceAmountTotal || 0), 0),
  }
}

export type SupplierSortKey = 'supplier' | 'productCount' | 'detailCount' | 'increase' | 'decrease' | 'net'
export type SupplierSort = { key: SupplierSortKey; order: 'ascend' | 'descend' } | null

function compareSupplierText(left?: string, right?: string) {
  return (left || '').localeCompare(right || '', 'zh-Hans-CN', { numeric: true, sensitivity: 'base' })
}

function readSupplierSortValue(row: StoreOrderImportPriceVarianceSupplierSummary, key: Exclude<SupplierSortKey, 'supplier'>) {
  switch (key) {
    case 'productCount':
      return row.productCount ?? 0
    case 'detailCount':
      return row.detailCount ?? 0
    case 'increase':
      return row.increaseVarianceAmountTotal ?? 0
    case 'decrease':
      return row.decreaseVarianceAmountTotal ?? 0
    default:
      return row.varianceAmountTotal ?? 0
  }
}

/**
 * 供应商排序（本地，接口一次返回全部供应商）。
 * 默认按净差额绝对值从大到小，与接口顺序一致；点列头后按该列排序，相同值再按供应商编码稳定排序。
 */
export function sortSupplierSummaries(
  rows: readonly StoreOrderImportPriceVarianceSupplierSummary[],
  sort: SupplierSort,
) {
  const byCode = (left: StoreOrderImportPriceVarianceSupplierSummary, right: StoreOrderImportPriceVarianceSupplierSummary) =>
    compareSupplierText(left.supplierCode, right.supplierCode)
  return [...rows].sort((left, right) => {
    if (!sort) {
      return Math.abs(right.varianceAmountTotal ?? 0) - Math.abs(left.varianceAmountTotal ?? 0) || byCode(left, right)
    }
    const direction = sort.order === 'ascend' ? 1 : -1
    const compared = sort.key === 'supplier'
      ? compareSupplierText(left.supplierName || left.supplierCode, right.supplierName || right.supplierCode)
      : readSupplierSortValue(left, sort.key) - readSupplierSortValue(right, sort.key)
    return compared * direction || byCode(left, right)
  })
}

export function getSupplierRowKey(row: Pick<StoreOrderImportPriceVarianceSupplierSummary, 'supplierCode' | 'supplierName'>) {
  return row.supplierCode || row.supplierName || 'unknown-supplier'
}

/**
 * 供应商卡收起时显示的行：前 N 个；已选中的供应商排在 N 名以外时补在末尾，保证选中态始终可见。
 */
export function getVisibleSupplierRows(
  sortedRows: readonly StoreOrderImportPriceVarianceSupplierSummary[],
  expanded: boolean,
  selectedSupplierCode?: string,
) {
  if (expanded || sortedRows.length <= SUPPLIER_PREVIEW_COUNT) return [...sortedRows]
  const preview = sortedRows.slice(0, SUPPLIER_PREVIEW_COUNT)
  if (!selectedSupplierCode || preview.some((row) => row.supplierCode === selectedSupplierCode)) return preview
  const selected = sortedRows.find((row) => row.supplierCode === selectedSupplierCode)
  return selected ? [...preview, selected] : preview
}

/** 已生效筛选的签名：忽略空值、键顺序，用于判断条件是否真的变了。 */
export function getFilterSignature(filters: object, omitKeys: readonly string[] = []) {
  const record = filters as Record<string, unknown>
  return JSON.stringify(
    Object.keys(record)
      .filter((key) => !omitKeys.includes(key) && record[key] !== undefined && record[key] !== null && record[key] !== '')
      .sort()
      .map((key) => [key, record[key]]),
  )
}

/**
 * 供应商排行的缓存键：除国内供应商以外的全部筛选条件。
 * 点选某个供应商后接口只返回该供应商的汇总，排行仍要显示同条件下的全部供应商，所以按这个键复用。
 */
export function getSupplierRankingKey(filters: object) {
  return getFilterSignature(filters, ['supplierCode'])
}
