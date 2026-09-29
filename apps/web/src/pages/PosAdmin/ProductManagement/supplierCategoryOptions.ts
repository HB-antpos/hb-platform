/**
 * 供应商分类级联框选项、树索引与批量编辑范围判定的纯逻辑。
 * 不依赖 React/antd，文案由调用方传入。
 */
import type { WarehouseCategoryNode } from '../../../services/warehouseCategoryService'
import { HOT_BARGAIN_SUPPLIER_CODE, type LocalSupplierCategoryNode } from '../../../types/localSupplierCategory'
import { SUPPLIER_CATEGORY_PSEUDO_PREFIX, SUPPLIER_CATEGORY_UNASSIGNED_VALUE } from './supplierCategoryFilter'
import type { SupplierCategoryTreeEntry } from './supplierCategoryTreeCache'

/** 空树提示伪节点（禁用）。 */
export const SUPPLIER_CATEGORY_EMPTY_VALUE = `${SUPPLIER_CATEGORY_PSEUDO_PREFIX}empty__`
/** 加载失败后可点击重试的伪节点。 */
export const SUPPLIER_CATEGORY_RETRY_VALUE = `${SUPPLIER_CATEGORY_PSEUDO_PREFIX}retry__`

export type SupplierCategoryOptionKind = 'supplier' | 'unassigned' | 'category' | 'empty' | 'retry'

export interface SupplierCategoryCascaderOption {
  value: string
  label: string
  kind: SupplierCategoryOptionKind
  disabled?: boolean
  /** false + 无 children 时由 antd Cascader 触发 loadData（按供应商懒加载分类树）。 */
  isLeaf?: boolean
  children?: SupplierCategoryCascaderOption[]
}

export interface SupplierCategorySearchResult {
  valuePath: string[]
  labelPath: string[]
}

/** 只检索已加载的分类；未加载的供应商仍可按名称找到并展开。 */
export function searchSupplierCategoryOptions(
  options: SupplierCategoryCascaderOption[],
  query: string,
  limit = 50,
): SupplierCategorySearchResult[] {
  const keyword = query.trim().toLocaleLowerCase()
  if (!keyword) return []
  const results: SupplierCategorySearchResult[] = []
  const visit = (items: SupplierCategoryCascaderOption[], valuePath: string[], labelPath: string[]) => {
    for (const option of items) {
      if (results.length >= limit) return
      if (option.disabled || option.kind === 'empty' || option.kind === 'retry') continue
      const nextValuePath = [...valuePath, option.value]
      const nextLabelPath = [...labelPath, option.label]
      if (option.label.toLocaleLowerCase().includes(keyword)) {
        results.push({ valuePath: nextValuePath, labelPath: nextLabelPath })
      }
      if (option.children?.length) visit(option.children, nextValuePath, nextLabelPath)
    }
  }
  visit(options, [], [])
  return results
}

export interface SupplierCascaderSource {
  value: string
  label: string
}

export interface SupplierCategoryCascaderLabels {
  unassigned: string
  empty: string
  retry: string
}

/** 供应商排序：200 置顶，其余保持原顺序。 */
export function sortSuppliersForCascader<T extends { value: string }>(
  suppliers: T[],
  hotBargainCode = HOT_BARGAIN_SUPPLIER_CODE,
): T[] {
  const hot = suppliers.filter((supplier) => supplier.value === hotBargainCode)
  return [...hot, ...suppliers.filter((supplier) => supplier.value !== hotBargainCode)]
}

/** 供应商分类树 → 级联选项；停用节点不参与筛选与指定。 */
export function mapSupplierTreeToCascaderOptions(nodes: LocalSupplierCategoryNode[]): SupplierCategoryCascaderOption[] {
  return nodes
    .filter((node) => node.isActive !== false)
    .map((node) => {
      const children = mapSupplierTreeToCascaderOptions(node.children ?? [])
      return {
        value: node.categoryGuid,
        label: node.name,
        kind: 'category' as const,
        children: children.length ? children : undefined,
      }
    })
}

/** 仓库分类树 → 级联选项（200 的供应商分类）。 */
export function mapWarehouseTreeToCascaderOptions(nodes: WarehouseCategoryNode[]): SupplierCategoryCascaderOption[] {
  return nodes.map((node) => {
    const children = mapWarehouseTreeToCascaderOptions(node.children ?? [])
    return {
      value: node.categoryGUID,
      label: node.categoryName,
      kind: 'category' as const,
      children: children.length ? children : undefined,
    }
  })
}

function buildUnassignedOption(labels: SupplierCategoryCascaderLabels): SupplierCategoryCascaderOption {
  return { value: SUPPLIER_CATEGORY_UNASSIGNED_VALUE, label: labels.unassigned, kind: 'unassigned', isLeaf: true }
}

export interface BuildSupplierCategoryCascaderOptionsInput {
  suppliers: SupplierCascaderSource[]
  warehouseTree: WarehouseCategoryNode[]
  getTree: (supplierCode: string) => SupplierCategoryTreeEntry | undefined
  labels: SupplierCategoryCascaderLabels
  hotBargainCode?: string
}

/**
 * 顶部供应商分类级联框的选项：
 * - 第一层是供应商（200 置顶）；
 * - 每个供应商下首个伪叶子「未归类」；
 * - 200 下挂仓库分类树；其他供应商已加载则挂网站分类树，空树给禁用提示，失败给重试节点，
 *   未加载则不给 children 并标 isLeaf=false，交给 loadData 懒加载。
 */
export function buildSupplierCategoryCascaderOptions(input: BuildSupplierCategoryCascaderOptionsInput): SupplierCategoryCascaderOption[] {
  const hotBargainCode = input.hotBargainCode ?? HOT_BARGAIN_SUPPLIER_CODE
  const warehouseOptions = mapWarehouseTreeToCascaderOptions(input.warehouseTree)
  return sortSuppliersForCascader(input.suppliers, hotBargainCode).map((supplier): SupplierCategoryCascaderOption => {
    const base = { value: supplier.value, label: supplier.label, kind: 'supplier' as const }
    const unassigned = buildUnassignedOption(input.labels)
    if (supplier.value === hotBargainCode) {
      return { ...base, children: [unassigned, ...warehouseOptions] }
    }
    const entry = input.getTree(supplier.value)
    if (entry?.loaded) {
      const categories = mapSupplierTreeToCascaderOptions(entry.nodes)
      return {
        ...base,
        children: categories.length
          ? [unassigned, ...categories]
          : [unassigned, { value: SUPPLIER_CATEGORY_EMPTY_VALUE, label: input.labels.empty, kind: 'empty', disabled: true, isLeaf: true }],
      }
    }
    if (entry?.status === 'error') {
      // 失败时必须给出 children，否则 antd 会一直显示加载图标；重试节点由组件拦截后重新加载。
      return {
        ...base,
        children: [unassigned, { value: SUPPLIER_CATEGORY_RETRY_VALUE, label: input.labels.retry, kind: 'retry', isLeaf: true }],
      }
    }
    return { ...base, isLeaf: false }
  })
}

export interface SupplierCategoryIndexEntry {
  node: LocalSupplierCategoryNode
  guidPath: string[]
  namePath: string[]
}

/** GUID（不区分大小写）→ 节点与从根开始的 GUID/名称路径。 */
export function buildSupplierCategoryIndex(nodes: LocalSupplierCategoryNode[]): Map<string, SupplierCategoryIndexEntry> {
  const index = new Map<string, SupplierCategoryIndexEntry>()
  const visit = (items: LocalSupplierCategoryNode[], guidPath: string[], namePath: string[]) => {
    for (const node of items) {
      const nextGuidPath = [...guidPath, node.categoryGuid]
      const nextNamePath = [...namePath, node.name]
      const key = node.categoryGuid.toLowerCase()
      if (!index.has(key)) {
        index.set(key, { node, guidPath: nextGuidPath, namePath: nextNamePath })
      }
      visit(node.children ?? [], nextGuidPath, nextNamePath)
    }
  }
  visit(nodes, [], [])
  return index
}

/** 在供应商分类树里找 GUID 路径（不区分大小写）。 */
export function findSupplierCategoryGuidPath(nodes: LocalSupplierCategoryNode[], guid: string | undefined): string[] | undefined {
  if (!guid) return undefined
  const target = guid.toLowerCase()
  const find = (items: LocalSupplierCategoryNode[], path: string[]): string[] | undefined => {
    for (const node of items) {
      const currentPath = [...path, node.categoryGuid]
      if (node.categoryGuid.toLowerCase() === target) return currentPath
      const found = find(node.children ?? [], currentPath)
      if (found) return found
    }
    return undefined
  }
  return find(nodes, [])
}

/** 在供应商分类树里找名称路径，用于筛选标签。 */
export function findSupplierCategoryNamePath(nodes: LocalSupplierCategoryNode[], guid: string | undefined): string[] | undefined {
  if (!guid) return undefined
  return buildSupplierCategoryIndex(nodes).get(guid.toLowerCase())?.namePath
}

/** 在仓库分类树里找 GUID 路径（200 的级联值）。 */
export function findWarehouseCategoryGuidPath(nodes: WarehouseCategoryNode[], guid: string | undefined): string[] | undefined {
  if (!guid) return undefined
  const find = (items: WarehouseCategoryNode[], path: string[]): string[] | undefined => {
    for (const node of items) {
      const currentPath = [...path, node.categoryGUID]
      if (node.categoryGUID === guid) return currentPath
      const found = find(node.children ?? [], currentPath)
      if (found) return found
    }
    return undefined
  }
  return find(nodes, [])
}

/** 服务端完整路径 "A > B > C" → ["A", "B", "C"]。 */
export function splitSupplierCategoryPath(path: string | undefined): string[] {
  if (!path) return []
  return path.split('>').map((segment) => segment.trim()).filter(Boolean)
}

export type BatchSupplierCategoryScopeStatus =
  | 'enabled'
  | 'mixedSuppliers'
  | 'hotBargain'
  | 'noSupplier'
  | 'unknownRows'
  | 'supplierChanging'

export interface BatchSupplierCategoryScope {
  status: BatchSupplierCategoryScopeStatus
  /** 所选商品共同的供应商（只有 enabled/hotBargain/supplierChanging 时有值）。 */
  supplierCode?: string
}

export interface ResolveBatchSupplierCategoryScopeInput {
  selectedKeys: string[]
  rows: Array<{ productCode: string; localSupplierCode?: string }>
  /** 批量表单里填写的新供应商（留空表示不修改供应商）。 */
  nextSupplierCode?: string
  hotBargainCode?: string
}

/**
 * 批量编辑能否设置供应商分类：只有所选商品都在当前页、同属一个非 200 供应商、且本次不改供应商时才可设置。
 * 分类树按供应商独立，跨供应商或换供应商时无法给出同一棵树。
 */
export function resolveBatchSupplierCategoryScope(input: ResolveBatchSupplierCategoryScopeInput): BatchSupplierCategoryScope {
  const hotBargainCode = input.hotBargainCode ?? HOT_BARGAIN_SUPPLIER_CODE
  const rowByCode = new Map(input.rows.map((row) => [row.productCode, row]))
  const selectedRows = input.selectedKeys.map((key) => rowByCode.get(key))
  if (!selectedRows.length || selectedRows.some((row) => !row)) return { status: 'unknownRows' }

  const supplierCodes = new Set(selectedRows.map((row) => row?.localSupplierCode?.trim() || ''))
  if (supplierCodes.size > 1) return { status: 'mixedSuppliers' }
  const [supplierCode] = [...supplierCodes]
  if (!supplierCode) return { status: 'noSupplier' }

  const nextSupplierCode = input.nextSupplierCode?.trim()
  if (nextSupplierCode && nextSupplierCode !== supplierCode) return { status: 'supplierChanging', supplierCode }
  if (supplierCode === hotBargainCode) return { status: 'hotBargain', supplierCode }
  return { status: 'enabled', supplierCode }
}
