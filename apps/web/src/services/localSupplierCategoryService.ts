import type { ApiResponse } from '../types/api'
import {
  HOT_BARGAIN_SUPPLIER_CODE,
  type LocalSupplierCategoryNode,
  type LocalSupplierCategoryPromotionalResult,
  type LocalSupplierCategoryPromotionalSource,
  type LocalSupplierCategoryResolveResult,
  type LocalSupplierCategorySourceKind,
  type LocalSupplierCategorySummary,
} from '../types/localSupplierCategory'
import request, { RequestError, unwrapApiData } from '../utils/request'

const API_BASE = '/api/react/v1/local-supplier-categories'

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function readString(source: Record<string, unknown>, aliases: string[]): string | undefined {
  for (const alias of aliases) {
    const value = source[alias]
    if (typeof value === 'string' && value.trim()) {
      return value.trim()
    }
  }
  return undefined
}

function readNumber(source: Record<string, unknown>, aliases: string[], fallback = 0): number {
  for (const alias of aliases) {
    const value = source[alias]
    if (value === undefined || value === null || value === '') continue
    const parsed = Number(value)
    if (Number.isFinite(parsed)) return parsed
  }
  return fallback
}

function readOptionalNumber(source: Record<string, unknown>, aliases: string[]): number | undefined {
  const value = readNumber(source, aliases, Number.NaN)
  return Number.isNaN(value) ? undefined : value
}

function readBoolean(source: Record<string, unknown>, aliases: string[], fallback: boolean): boolean {
  for (const alias of aliases) {
    const value = source[alias]
    if (typeof value === 'boolean') return value
    if (value === 'true' || value === 1) return true
    if (value === 'false' || value === 0) return false
  }
  return fallback
}

function normalizeSourceKind(value: string | undefined, supplierCode: string): LocalSupplierCategorySourceKind {
  const normalized = value?.toLowerCase()
  if (normalized === 'warehouse' || normalized === 'website') return normalized
  // 旧后端没回 sourceKind 时，按 200 = 仓库分类兜底。
  return supplierCode === HOT_BARGAIN_SUPPLIER_CODE ? 'warehouse' : 'website'
}

function normalizePromotionalSource(value: string | undefined): LocalSupplierCategoryPromotionalSource | undefined {
  const normalized = value?.toLowerCase()
  return normalized === 'pattern' || normalized === 'manual' ? normalized : undefined
}

/** summary 响应归一化：数值字段缺失按 0，缺编码的行丢弃。 */
export function normalizeLocalSupplierCategorySummary(raw: unknown): LocalSupplierCategorySummary[] {
  if (!Array.isArray(raw)) return []
  return raw.flatMap((item): LocalSupplierCategorySummary[] => {
    if (!isRecord(item)) return []
    const supplierCode = readString(item, ['supplierCode', 'SupplierCode', 'localSupplierCode', 'LocalSupplierCode'])
    if (!supplierCode) return []
    return [{
      supplierCode,
      supplierName: readString(item, ['supplierName', 'SupplierName']),
      sourceKind: normalizeSourceKind(readString(item, ['sourceKind', 'SourceKind']), supplierCode),
      categoryCount: readNumber(item, ['categoryCount', 'CategoryCount']),
      promotionalCount: readNumber(item, ['promotionalCount', 'PromotionalCount']),
      productCount: readNumber(item, ['productCount', 'ProductCount']),
      assignedCount: readNumber(item, ['assignedCount', 'AssignedCount']),
      manualCount: readNumber(item, ['manualCount', 'ManualCount']),
      unassignedCount: readNumber(item, ['unassignedCount', 'UnassignedCount']),
      lastCapturedAt: readString(item, ['lastCapturedAt', 'LastCapturedAt']),
      lastSnapshotAt: readString(item, ['lastSnapshotAt', 'LastSnapshotAt']),
    }]
  })
}

type FlatCategoryNode = Omit<LocalSupplierCategoryNode, 'children'> & { order: number }

function readChildren(item: Record<string, unknown>): unknown[] {
  const children = item.children ?? item.Children
  return Array.isArray(children) ? children : []
}

/**
 * 分类树归一化，兼容两种形状：
 * - 嵌套 children（契约形状）；
 * - 扁平列表只带 parentGuid（兜底）。
 * 先把两种输入都拍平成「节点 + 父 GUID」，再按 parentGuid 重建树：父节点不存在的当作根，
 * 同一 GUID 重复出现只保留第一次，避免循环引用或重复节点把树撑坏。
 */
export function normalizeLocalSupplierCategoryTree(raw: unknown): LocalSupplierCategoryNode[] {
  const flat = new Map<string, FlatCategoryNode>()
  let order = 0

  const visit = (items: unknown[], nestedParentGuid: string | undefined, depth: number) => {
    for (const item of items) {
      if (!isRecord(item)) continue
      const categoryGuid = readString(item, ['categoryGuid', 'categoryGUID', 'CategoryGUID', 'guid'])
      if (!categoryGuid || flat.has(categoryGuid)) continue
      const name = readString(item, ['name', 'categoryName', 'CategoryName', 'Name']) ?? categoryGuid
      flat.set(categoryGuid, {
        categoryGuid,
        // 嵌套形状下以所在层级为准，扁平形状只能读 parentGuid。
        parentGuid: nestedParentGuid ?? readString(item, ['parentGuid', 'parentGUID', 'ParentGUID']),
        name,
        externalKey: readString(item, ['externalKey', 'ExternalKey']),
        fullPath: readString(item, ['fullPath', 'FullPath']),
        depth: readNumber(item, ['depth', 'Depth'], depth),
        isPromotional: readBoolean(item, ['isPromotional', 'IsPromotional'], false),
        promotionalSource: normalizePromotionalSource(readString(item, ['promotionalSource', 'PromotionalSource'])),
        isActive: readBoolean(item, ['isActive', 'IsActive'], true),
        sortOrder: readOptionalNumber(item, ['sortOrder', 'SortOrder']),
        sourceUrl: readString(item, ['sourceUrl', 'SourceUrl']),
        productCount: readNumber(item, ['productCount', 'ProductCount']),
        lastSeenAt: readString(item, ['lastSeenAt', 'LastSeenAt']),
        order: order++,
      })
      visit(readChildren(item), categoryGuid, depth + 1)
    }
  }
  visit(Array.isArray(raw) ? raw : [], undefined, 0)

  const childrenByParent = new Map<string | undefined, FlatCategoryNode[]>()
  for (const node of flat.values()) {
    const parentKey = node.parentGuid && flat.has(node.parentGuid) && node.parentGuid !== node.categoryGuid
      ? node.parentGuid
      : undefined
    const siblings = childrenByParent.get(parentKey) ?? []
    siblings.push(node)
    childrenByParent.set(parentKey, siblings)
  }

  const built = new Set<string>()
  const buildNode = (node: FlatCategoryNode): LocalSupplierCategoryNode[] => {
    // 防御 parentGuid 成环：同一节点只构建一次。
    if (built.has(node.categoryGuid)) return []
    built.add(node.categoryGuid)
    const { order: _order, ...rest } = node
    void _order
    return [{ ...rest, children: build(node.categoryGuid) }]
  }
  const build = (parentKey: string | undefined): LocalSupplierCategoryNode[] =>
    (childrenByParent.get(parentKey) ?? [])
      .sort((left, right) => left.order - right.order)
      .flatMap(buildNode)

  const roots = build(undefined)
  // 扁平数据里互为父子的环没有根可达，按出现顺序补成根节点，保证节点不丢。
  for (const node of flat.values()) {
    if (!built.has(node.categoryGuid)) roots.push(...buildNode(node))
  }
  return roots
}

function normalizePromotionalResult(raw: unknown): LocalSupplierCategoryPromotionalResult {
  const source = isRecord(raw) ? raw : {}
  return {
    reassigned: readNumber(source, ['reassigned', 'Reassigned']),
    cleared: readNumber(source, ['cleared', 'Cleared']),
  }
}

function normalizeResolveResult(raw: unknown): LocalSupplierCategoryResolveResult {
  const source = isRecord(raw) ? raw : {}
  return {
    productsScanned: readNumber(source, ['productsScanned', 'ProductsScanned']),
    assigned: readNumber(source, ['assigned', 'Assigned']),
    updated: readNumber(source, ['updated', 'Updated']),
    cleared: readNumber(source, ['cleared', 'Cleared']),
    unchanged: readNumber(source, ['unchanged', 'Unchanged']),
    manualSkipped: readNumber(source, ['manualSkipped', 'ManualSkipped']),
    staleRemoved: readNumber(source, ['staleRemoved', 'StaleRemoved']),
  }
}

export async function getLocalSupplierCategorySummary(): Promise<LocalSupplierCategorySummary[]> {
  const response = await request.get<ApiResponse<unknown>>(`${API_BASE}/summary`)
  return normalizeLocalSupplierCategorySummary(unwrapApiData(response))
}

export async function getLocalSupplierCategoryTree(supplierCode: string): Promise<LocalSupplierCategoryNode[]> {
  const response = await request.get<ApiResponse<unknown>>(`${API_BASE}/tree`, { params: { supplierCode } })
  return normalizeLocalSupplierCategoryTree(unwrapApiData(response))
}

export async function setLocalSupplierCategoryPromotional(
  categoryGuid: string,
  isPromotional: boolean,
): Promise<LocalSupplierCategoryPromotionalResult> {
  const response = await request.patch<ApiResponse<unknown>>(
    `${API_BASE}/${encodeURIComponent(categoryGuid)}/promotional`,
    { isPromotional },
  )
  return normalizePromotionalResult(unwrapApiData(response))
}

export async function resolveLocalSupplierCategories(supplierCode: string): Promise<LocalSupplierCategoryResolveResult> {
  const response = await request.post<ApiResponse<unknown>>(`${API_BASE}/${encodeURIComponent(supplierCode)}/resolve`)
  return normalizeResolveResult(unwrapApiData(response))
}

/** 从请求异常里读取后端业务错误码（HTTP 4xx 的响应体或 HTTP 200 的 success=false 响应）。 */
export function readRequestErrorCode(error: unknown): string | undefined {
  if (!(error instanceof RequestError) || !isRecord(error.payload)) return undefined
  return readString(error.payload, ['errorCode', 'ErrorCode', 'code', 'Code'])
}
