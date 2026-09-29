import request from '../../../utils/request'
import type { ReportPeriod } from '../ReportWorkbench/logic'
import type { ReportSnapshot } from '../ReportWorkbench/useReportQuery'

export type SupplierKind = 'australia' | 'china'
export type ReportSection = 'suppliers' | 'branches' | 'products' | 'summary'
export interface SalesDetailRow {
  code: string
  name: string
  itemNumber?: string
  productImage?: string
  revenue: number
  compareRevenue: number | null
  quantity: number
  compareQuantity: number | null
  orderCount: number | null
  compareOrderCount: number | null
  averageTransaction: number | null
  compareAverageTransaction: number | null
  averageUnitPrice: number | null
  compareAverageUnitPrice: number | null
  grossProfit: number | null
  compareGrossProfit: number | null
  grossMarginRate: number | null
  compareGrossMarginRate: number | null
  share: number | null
  compareShare: number | null
  chinaShare: number | null
  compareChinaShare: number | null
}
export interface SalesDetailPage { rows: SalesDetailRow[]; total: number; summary?: SalesDetailRow }
export interface SalesDetailReport {
  summary?: SalesDetailPage
  suppliers?: SalesDetailPage
  branches?: SalesDetailPage
  products?: SalesDetailPage
}
export interface SalesDetailQuery extends ReportPeriod {
  kind: SupplierKind
  branchCodes?: string[]
  selectedBranchCode?: string
  selectedSupplierCode?: string
  selectedSupplierCodes?: string[]
  supplierCategoryGuids?: string[]
  warehouseCategoryGuids?: string[]
  selectedProductCode?: string
  search?: string
  pageIndex: number
  pageSize: number
}

function record(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
}
function field(raw: Record<string, unknown>, key: string) { return raw[key] ?? raw[key[0].toUpperCase() + key.slice(1)] }
function nullable(value: unknown): number | null {
  if (typeof value !== 'number' && typeof value !== 'string') return null
  if (typeof value === 'string' && !value.trim()) return null
  const number = Number(value)
  return Number.isFinite(number) ? number : null
}
export function normalizeSalesDetailRow(value: unknown): SalesDetailRow {
  const raw = record(value)
  const number = (key: string) => nullable(field(raw, key))
  const string = (key: string) => String(field(raw, key) ?? '')
  return { code: string('code'), name: string('name'), itemNumber: string('itemNumber'), productImage: string('productImage'),
    revenue: number('revenue') ?? 0, compareRevenue: number('compareRevenue'), quantity: number('quantity') ?? 0,
    compareQuantity: number('compareQuantity'), orderCount: number('orderCount'), compareOrderCount: number('compareOrderCount'),
    averageTransaction: number('averageTransaction'), compareAverageTransaction: number('compareAverageTransaction'),
    averageUnitPrice: number('averageUnitPrice'), compareAverageUnitPrice: number('compareAverageUnitPrice'),
    grossProfit: number('grossProfit'), compareGrossProfit: number('compareGrossProfit'),
    grossMarginRate: number('grossMarginRate'), compareGrossMarginRate: number('compareGrossMarginRate'),
    share: number('share'), compareShare: number('compareShare'), chinaShare: number('chinaShare'), compareChinaShare: number('compareChinaShare') }
}

/** 将每块候选的查询显式隔离，避免自身筛选条件收窄候选或分页触发全部面板重查。 */
export function sectionQuery(section: ReportSection, query: SalesDetailQuery) {
  const params: Record<string, unknown> = { ...query, section }
  if (section === 'suppliers') delete params.selectedSupplierCode
  if (section === 'branches') delete params.selectedBranchCode
  if (section === 'products') delete params.selectedProductCode
  if (section !== 'products' && section !== 'summary') delete params.search
  if (section !== 'products') { delete params.pageIndex; delete params.pageSize }
  return params
}

function normalizeSalesDetailPage(value: unknown): SalesDetailPage | undefined {
  if (value == null) return undefined
  const data = record(value)
  const rows = field(data, 'rows')
  const summary = field(data, 'summary')
  return { rows: Array.isArray(rows) ? rows.map(normalizeSalesDetailRow) : [],
    total: nullable(field(data, 'total')) ?? 0, summary: summary ? normalizeSalesDetailRow(summary) : undefined }
}

/** 完整页面一次读取同一快照；商品翻页时仅请求 products，避免重算其他三栏。 */
export async function fetchSalesDetailReport(query: SalesDetailQuery, signal: AbortSignal,
  sections?: ReportSection[]): Promise<ReportSnapshot<SalesDetailReport>> {
  const params: Record<string, unknown> = { ...query }
  if (sections?.length) params.sections = sections
  const raw = record(await request<unknown>('/api/react/v1/dashboard/sales-detail-report', {
    signal, params,
  }))
  if (field(raw, 'success') === false) throw new Error(String(field(raw, 'message') || '报表加载失败'))
  const statisticStatus = String(field(raw, 'statisticStatus') ?? 'Pending')
  const data = record(field(raw, 'data'))
  const requested = sections?.length ? sections : ['summary', 'suppliers', 'branches', 'products'] satisfies ReportSection[]
  const report: SalesDetailReport = {}
  if (statisticStatus.toLowerCase() === 'fresh') {
    for (const section of requested) {
      const page = normalizeSalesDetailPage(field(data, section))
      if (page) report[section] = page
      else if (field(raw, 'cacheVersion') === 'no-access') report[section] = { rows: [], total: 0 }
      else throw new Error(`报表响应缺少 ${section} 数据`)
    }
  }
  return { data: report,
    statisticStatus,
    statisticMessage: field(raw, 'statisticMessage') as string | undefined,
    statisticUpdatedAt: field(raw, 'statisticUpdatedAt') as string | undefined,
    cacheVersion: field(raw, 'cacheVersion') as string | undefined }
}
