/**
 * 供应商分类（分类数据来自供应商网站）的前端契约。
 * 后端 System.Text.Json 全局 camelCase；服务层把响应归一化成这里的形状。
 */

/** Hot Bargain 自营供应商：它的供应商分类就是仓库分类，不走网站采集。 */
export const HOT_BARGAIN_SUPPLIER_CODE = '200'

/** 商品上的供应商分类来源：网站采集自动归类 / 人工指定并锁定 / 200 随仓库分类。 */
export type SupplierCategorySource = 'website' | 'manual' | 'warehouse'

export const SUPPLIER_CATEGORY_SOURCES: readonly SupplierCategorySource[] = ['website', 'manual', 'warehouse']

export type LocalSupplierCategorySourceKind = 'warehouse' | 'website'

export type LocalSupplierCategoryPromotionalSource = 'pattern' | 'manual'

/** 管理弹窗左侧的每供应商统计（GET summary）。 */
export interface LocalSupplierCategorySummary {
  supplierCode: string
  supplierName?: string
  sourceKind: LocalSupplierCategorySourceKind
  categoryCount: number
  promotionalCount: number
  productCount: number
  assignedCount: number
  manualCount: number
  unassignedCount: number
  lastCapturedAt?: string
  lastSnapshotAt?: string
}

/** 供应商分类树节点（GET tree?supplierCode=）。200 时由服务端把仓库分类树映射成同一结构。 */
export interface LocalSupplierCategoryNode {
  categoryGuid: string
  parentGuid?: string
  name: string
  externalKey?: string
  fullPath?: string
  depth: number
  isPromotional: boolean
  promotionalSource?: LocalSupplierCategoryPromotionalSource
  isActive: boolean
  sortOrder?: number
  sourceUrl?: string
  productCount: number
  lastSeenAt?: string
  children: LocalSupplierCategoryNode[]
}

/** PATCH {categoryGuid}/promotional 的结果。 */
export interface LocalSupplierCategoryPromotionalResult {
  reassigned: number
  cleared: number
}

/** POST {supplierCode}/resolve 的结果。 */
export interface LocalSupplierCategoryResolveResult {
  productsScanned: number
  assigned: number
  updated: number
  cleared: number
  unchanged: number
  manualSkipped: number
  staleRemoved: number
}

/**
 * 商品单个/批量更新时的供应商分类三态：
 * - 带 supplierCategoryGUID：人工指定并锁定；
 * - clearSupplierCategory=true：删除人工/自动归属并按采集数据重新自动归类；
 * - 两者都不带：不变。
 */
export interface SupplierCategoryUpdatePayload {
  supplierCategoryGUID?: string
  clearSupplierCategory?: boolean
}

/** 人工指定分类与所选商品供应商不一致时后端返回的错误码。 */
export const SUPPLIER_CATEGORY_MISMATCH_ERROR_CODE = 'CATEGORY_SUPPLIER_MISMATCH'
