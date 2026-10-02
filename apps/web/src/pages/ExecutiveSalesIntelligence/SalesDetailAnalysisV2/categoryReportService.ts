import request from '../../../utils/request'
import type { ReportPeriod } from '../ReportWorkbench/logic'
import type { ReportSnapshot } from '../ReportWorkbench/useReportQuery'
import { normalizeSalesDetailRow, type SalesDetailPage } from './reportService'

/** 后端「未归类」节点的保留键，与 SalesDetailCategorySources.UnassignedKey 一致。 */
export const UNASSIGNED_CATEGORY_KEY = '__unassigned__'

export interface CategoryMetrics {
  revenue: number
  compareRevenue: number | null
  quantity: number
  compareQuantity: number | null
  grossProfit: number | null
  compareGrossProfit: number | null
  grossMarginRate: number | null
  compareGrossMarginRate: number | null
  productCount: number
  compareProductCount: number | null
}
export interface CategoryNode extends CategoryMetrics {
  categoryGuid: string
  name: string
  depth: number
  isActive: boolean
  children: CategoryNode[]
}
export interface CategorySupplier extends CategoryMetrics {
  supplierCode: string
  supplierName: string
  /** supplier：供应商网站分类；warehouse：200 使用的仓库分类。 */
  categorySource: 'supplier' | 'warehouse'
  categories: CategoryNode[]
  unassigned?: CategoryNode
}
export interface CategoryReport {
  summary: CategoryMetrics
  unassigned: CategoryMetrics
  suppliers: CategorySupplier[]
  products?: SalesDetailPage
}
export interface CategoryReportQuery extends ReportPeriod {
  supplierCodes: string[]
  branchCodes?: string[]
  selectedBranchCode?: string
  nodeSupplierCode?: string
  /** 空 = 该供应商全部商品；UNASSIGNED_CATEGORY_KEY = 未归类。 */
  nodeCategoryGuid?: string
  search?: string
  pageIndex?: number
  pageSize?: number
  includeTree?: boolean
}
export interface CategorySupplierOption {
  supplierCode: string
  supplierName: string
  categorySource: 'supplier' | 'warehouse'
  categoryCount: number
  assignedProductCount: number
}
export interface CategoryStoreOption { storeCode: string; storeName: string }
export interface CategoryOptions { suppliers: CategorySupplierOption[]; stores: CategoryStoreOption[] }

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
function list(value: unknown): unknown[] { return Array.isArray(value) ? value : [] }

function normalizeMetrics(raw: Record<string, unknown>): CategoryMetrics {
  const number = (key: string) => nullable(field(raw, key))
  return { revenue: number('revenue') ?? 0, compareRevenue: number('compareRevenue'), quantity: number('quantity') ?? 0,
    compareQuantity: number('compareQuantity'), grossProfit: number('grossProfit'), compareGrossProfit: number('compareGrossProfit'),
    grossMarginRate: number('grossMarginRate'), compareGrossMarginRate: number('compareGrossMarginRate'),
    productCount: number('productCount') ?? 0, compareProductCount: number('compareProductCount') }
}
function normalizeNode(value: unknown): CategoryNode {
  const raw = record(value)
  return { ...normalizeMetrics(raw), categoryGuid: String(field(raw, 'categoryGuid') ?? ''), name: String(field(raw, 'name') ?? ''),
    depth: nullable(field(raw, 'depth')) ?? 0, isActive: field(raw, 'isActive') !== false,
    children: list(field(raw, 'children')).map(normalizeNode) }
}
export function normalizeCategoryReport(value: unknown): CategoryReport {
  const raw = record(value)
  const products = field(raw, 'products')
  const page = products == null ? undefined : record(products)
  return {
    summary: normalizeMetrics(record(field(raw, 'summary'))),
    unassigned: normalizeMetrics(record(field(raw, 'unassigned'))),
    suppliers: list(field(raw, 'suppliers')).map(item => {
      const supplier = record(item)
      const unassigned = field(supplier, 'unassigned')
      return { ...normalizeMetrics(supplier), supplierCode: String(field(supplier, 'supplierCode') ?? ''),
        supplierName: String(field(supplier, 'supplierName') ?? ''),
        categorySource: field(supplier, 'categorySource') === 'warehouse' ? 'warehouse' as const : 'supplier' as const,
        categories: list(field(supplier, 'categories')).map(normalizeNode),
        unassigned: unassigned ? normalizeNode(unassigned) : undefined }
    }),
    products: page && { rows: list(field(page, 'rows')).map(normalizeSalesDetailRow), total: nullable(field(page, 'total')) ?? 0,
      summary: field(page, 'summary') ? normalizeSalesDetailRow(field(page, 'summary')) : undefined },
  }
}

/** 分类树与节点商品共用一个接口：只要树时不带节点，只翻商品时 includeTree=false，避免重复汇总。 */
export async function fetchSalesDetailCategoryReport(query: CategoryReportQuery, signal: AbortSignal): Promise<ReportSnapshot<CategoryReport>> {
  const raw = record(await request<unknown>('/api/react/v1/dashboard/sales-detail-category-report', { signal, params: { ...query } }))
  if (field(raw, 'success') === false) throw new Error(String(field(raw, 'message') || '报表加载失败'))
  return { data: normalizeCategoryReport(field(raw, 'data')),
    statisticStatus: String(field(raw, 'statisticStatus') ?? 'Pending'),
    statisticMessage: field(raw, 'statisticMessage') as string | undefined,
    statisticUpdatedAt: field(raw, 'statisticUpdatedAt') as string | undefined,
    cacheVersion: field(raw, 'cacheVersion') as string | undefined }
}

export async function fetchSalesDetailCategoryOptions(signal?: AbortSignal): Promise<CategoryOptions> {
  const raw = record(await request<unknown>('/api/react/v1/dashboard/sales-detail-category-options', { signal }))
  if (field(raw, 'success') === false) throw new Error(String(field(raw, 'message') || '供应商加载失败'))
  const data = record(field(raw, 'data'))
  return {
    suppliers: list(field(data, 'suppliers')).map(item => {
      const supplier = record(item)
      return { supplierCode: String(field(supplier, 'supplierCode') ?? ''), supplierName: String(field(supplier, 'supplierName') ?? ''),
        categorySource: field(supplier, 'categorySource') === 'warehouse' ? 'warehouse' as const : 'supplier' as const,
        categoryCount: nullable(field(supplier, 'categoryCount')) ?? 0, assignedProductCount: nullable(field(supplier, 'assignedProductCount')) ?? 0 }
    }).filter(item => item.supplierCode),
    stores: list(field(data, 'stores')).map(item => {
      const store = record(item)
      return { storeCode: String(field(store, 'storeCode') ?? ''), storeName: String(field(store, 'storeName') ?? '') }
    }).filter(item => item.storeCode),
  }
}
