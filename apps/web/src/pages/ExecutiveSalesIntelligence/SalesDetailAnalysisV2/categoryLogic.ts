import { UNASSIGNED_CATEGORY_KEY, type CategoryMetrics, type CategoryNode, type CategoryReport, type CategorySupplier } from './categoryReportService'

/** 当前查看的节点：没有 categoryGuid 表示该供应商全部商品。 */
export interface CategorySelection { supplierCode: string; categoryGuid?: string }

export const MAX_CATEGORY_SUPPLIERS = 100
/** 每一层默认只列营业额最高的若干项，其余收成「另外 N 个」一行，金额照样显示。 */
export const CATEGORY_ROW_LIMIT = 8

export function nodeKey(supplierCode: string, categoryGuid?: string): string {
  return categoryGuid ? `${supplierCode}::${categoryGuid}` : supplierCode
}

export interface CategoryTreeRow {
  key: string
  kind: 'supplier' | 'category' | 'unassigned' | 'more'
  depth: number
  supplierCode: string
  categoryGuid?: string
  name: string
  metrics?: CategoryMetrics
  /** 占上一级营业额：供应商占所选供应商合计，一级分类占供应商，子分类占父分类。 */
  share: number | null
  expandable: boolean
  expanded: boolean
  inactive?: boolean
  source?: CategorySupplier['categorySource']
  /** 未归类占该供应商营业额，用于在供应商行上提示。 */
  unassignedShare?: number | null
  /** kind=more：被收起的那一层的父键、隐藏项数和合计营业额。 */
  parentKey?: string
  hiddenCount?: number
  hiddenRevenue?: number
}

function ratio(value: number, total: number): number | null {
  return total > 0 ? value / total : null
}

function matches(name: string, filter: string) {
  return name.toLocaleLowerCase().includes(filter)
}

/** 节点自身或任一子孙名称命中筛选词。 */
function subtreeMatches(node: CategoryNode, filter: string): boolean {
  return matches(node.name, filter) || node.children.some(child => subtreeMatches(child, filter))
}

/**
 * 把「供应商 → 分类树」展开成表格行。筛选分类名时忽略折叠与收起，只保留命中的节点及其祖先。
 */
export function flattenCategoryTree(report: CategoryReport, options: {
  expanded: ReadonlySet<string>; showAll: ReadonlySet<string>; filter?: string; limit?: number
}): CategoryTreeRow[] {
  const filter = options.filter?.trim().toLocaleLowerCase() ?? ''
  const limit = options.limit ?? CATEGORY_ROW_LIMIT
  const rows: CategoryTreeRow[] = []

  const pushList = (supplier: CategorySupplier, parentKey: string, nodes: CategoryNode[], parentRevenue: number, depth: number, filtering: boolean) => {
    const visible = filtering ? nodes.filter(node => subtreeMatches(node, filter)) : nodes
    const limited = !filtering && !options.showAll.has(parentKey) && visible.length > limit + 1
    const shown = limited ? visible.slice(0, limit) : visible
    for (const node of shown) {
      const key = nodeKey(supplier.supplierCode, node.categoryGuid)
      // 筛选时命中节点本身就展示它的全部子孙，只命中子孙时沿路径展开。
      const childFiltering = filtering && !matches(node.name, filter)
      const expanded = node.children.length > 0 && (filtering ? childFiltering || options.expanded.has(key) : options.expanded.has(key))
      rows.push({ key, kind: 'category', depth, supplierCode: supplier.supplierCode, categoryGuid: node.categoryGuid, name: node.name,
        metrics: node, share: ratio(node.revenue, parentRevenue), expandable: node.children.length > 0, expanded,
        inactive: !node.isActive })
      if (expanded) pushList(supplier, key, node.children, node.revenue, depth + 1, childFiltering)
    }
    if (limited) {
      const hidden = visible.slice(limit)
      rows.push({ key: `${parentKey}::more`, kind: 'more', depth, supplierCode: supplier.supplierCode, name: '', share: null,
        expandable: false, expanded: false, parentKey, hiddenCount: hidden.length,
        hiddenRevenue: hidden.reduce((sum, node) => sum + node.revenue, 0) })
    }
  }

  for (const supplier of report.suppliers) {
    const key = nodeKey(supplier.supplierCode)
    const supplierMatched = !filter || matches(`${supplier.supplierName} ${supplier.supplierCode}`, filter)
    const filtering = !supplierMatched
    if (filtering && !supplier.categories.some(node => subtreeMatches(node, filter))) continue
    const hasChildren = supplier.categories.length > 0 || !!supplier.unassigned
    const expanded = hasChildren && (filtering || options.expanded.has(key))
    rows.push({ key, kind: 'supplier', depth: 0, supplierCode: supplier.supplierCode, name: supplier.supplierName || supplier.supplierCode,
      metrics: supplier, share: ratio(supplier.revenue, report.summary.revenue), expandable: hasChildren, expanded,
      source: supplier.categorySource, unassignedShare: supplier.unassigned ? ratio(supplier.unassigned.revenue, supplier.revenue) : null })
    if (!expanded) continue
    pushList(supplier, key, supplier.categories, supplier.revenue, 1, filtering)
    // 未归类固定排在该供应商最后，筛选分类名时不显示。
    if (supplier.unassigned && !filtering)
      rows.push({ key: nodeKey(supplier.supplierCode, UNASSIGNED_CATEGORY_KEY), kind: 'unassigned', depth: 1,
        supplierCode: supplier.supplierCode, categoryGuid: UNASSIGNED_CATEGORY_KEY, name: '', metrics: supplier.unassigned,
        share: ratio(supplier.unassigned.revenue, supplier.revenue), expandable: false, expanded: false })
  }
  return rows
}

export interface CategoryPathItem { label: string; selection?: CategorySelection }
export interface CategoryFocus {
  supplier: CategorySupplier
  node?: CategoryNode
  metrics: CategoryMetrics
  /** 占上一级营业额。 */
  share: number | null
  path: CategoryPathItem[]
}

function findPath(nodes: CategoryNode[], guid: string, trail: CategoryNode[] = []): CategoryNode[] | undefined {
  for (const node of nodes) {
    const next = [...trail, node]
    if (node.categoryGuid.toLowerCase() === guid.toLowerCase()) return next
    const found = findPath(node.children, guid, next)
    if (found) return found
  }
  return undefined
}

/** 当前节点在树里的位置、指标与面包屑；节点已不在结果中（换了日期或供应商）时返回 undefined。 */
export function resolveCategoryFocus(report: CategoryReport, selection: CategorySelection | undefined,
  labels: { all: string; unassigned: string }): CategoryFocus | undefined {
  if (!selection) return undefined
  const supplier = report.suppliers.find(item => item.supplierCode === selection.supplierCode)
  if (!supplier) return undefined
  const root: CategoryPathItem[] = [{ label: labels.all },
    { label: supplier.supplierName || supplier.supplierCode, selection: { supplierCode: supplier.supplierCode } }]
  if (!selection.categoryGuid)
    return { supplier, metrics: supplier, share: ratio(supplier.revenue, report.summary.revenue), path: root }
  if (selection.categoryGuid === UNASSIGNED_CATEGORY_KEY) {
    if (!supplier.unassigned) return undefined
    return { supplier, node: supplier.unassigned, metrics: supplier.unassigned, share: ratio(supplier.unassigned.revenue, supplier.revenue),
      path: [...root, { label: labels.unassigned, selection }] }
  }
  const trail = findPath(supplier.categories, selection.categoryGuid)
  if (!trail) return undefined
  const node = trail[trail.length - 1]
  const parentRevenue = trail.length > 1 ? trail[trail.length - 2].revenue : supplier.revenue
  return { supplier, node, metrics: node, share: ratio(node.revenue, parentRevenue),
    path: [...root, ...trail.map(item => ({ label: item.name, selection: { supplierCode: supplier.supplierCode, categoryGuid: item.categoryGuid } }))] }
}

/** 包含当前节点的所有祖先键，选中深层节点后确保它在树里可见。 */
export function ancestorKeys(report: CategoryReport, selection: CategorySelection): string[] {
  const keys = [nodeKey(selection.supplierCode)]
  const supplier = report.suppliers.find(item => item.supplierCode === selection.supplierCode)
  if (!supplier || !selection.categoryGuid || selection.categoryGuid === UNASSIGNED_CATEGORY_KEY) return keys
  const trail = findPath(supplier.categories, selection.categoryGuid) ?? []
  return [...keys, ...trail.slice(0, -1).map(node => nodeKey(supplier.supplierCode, node.categoryGuid))]
}

/** 展开供应商时一起展开的键：分类树只有一个根（如 200 仓库分类的 ALL）时连根一起展开，省一次点击。 */
export function supplierOpenKeys(report: CategoryReport, supplierCode: string): string[] {
  const supplier = report.suppliers.find(item => item.supplierCode === supplierCode)
  const only = supplier?.categories.length === 1 && supplier.categories[0].children.length ? supplier.categories[0] : undefined
  return only ? [nodeKey(supplierCode), nodeKey(supplierCode, only.categoryGuid)] : [nodeKey(supplierCode)]
}
