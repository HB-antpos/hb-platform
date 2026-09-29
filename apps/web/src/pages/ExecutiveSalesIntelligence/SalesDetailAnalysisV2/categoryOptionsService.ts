import request from '../../../utils/request'
export interface SalesDetailCategoryOption { guid: string; name: string; label: string; parentGuid?: string; supplierCode?: string; supplierName?: string }
export interface SalesDetailCategoryGroup { supplierCode?: string; supplierName?: string; options: SalesDetailCategoryOption[] }
export interface SalesDetailCategoryTreeNode { key: string; value: string; title: string; searchText: string; selectable?: boolean; disableCheckbox?: boolean; children?: SalesDetailCategoryTreeNode[] }
const record = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
const read = (value: Record<string, unknown>, ...keys: string[]) => keys.map(key => value[key] ?? value[key[0].toUpperCase() + key.slice(1)]).find(item => item != null)
const string = (value: unknown) => typeof value === 'string' ? value.trim() : String(value ?? '').trim()
function option(value: unknown, supplierCode?: string, supplierName?: string): SalesDetailCategoryOption | undefined {
  const raw = record(value)
  if (read(raw, 'isActive') === false) return undefined
  const guid = string(read(raw, 'guid', 'categoryGuid', 'categoryGUID', 'supplierCategoryGuid', 'supplierCategoryGUID', 'warehouseCategoryGuid', 'warehouseCategoryGUID'))
  if (!guid) return undefined
  const name = string(read(raw, 'name', 'categoryName', 'supplierCategoryName', 'warehouseCategoryName')) || guid
  return { guid, name, label: name, supplierCode: string(read(raw, 'supplierCode')) || supplierCode, supplierName: string(read(raw, 'supplierName')) || supplierName }
}
export function normalizeSalesDetailCategoryOptions(value: unknown): SalesDetailCategoryGroup[] {
  const raw = record(value), data = read(raw, 'data') ?? value, root = record(data), groups: SalesDetailCategoryGroup[] = []
  const add = (items: unknown, supplierCode?: string, supplierName?: string) => {
    if (!Array.isArray(items)) return
    const options: SalesDetailCategoryOption[] = []
    const visit = (nodes: unknown[], path: string[], parentGuid?: string) => nodes.forEach(node => {
      const category = option(node, supplierCode, supplierName)
      const children = read(record(node), 'children')
      const names = category ? [...path, category.name] : path
      if (category) options.push({ ...category, name: names.join(' / '), parentGuid })
      // 停用父分类不显示；启用子分类挂到最近的可见父节点。
      if (Array.isArray(children)) visit(children, names, category?.guid ?? parentGuid)
    })
    visit(items, [])
    if (options.length) groups.push({ supplierCode, supplierName, options })
  }
  const grouped = read(root, 'supplierCategories', 'supplierCategoryGroups', 'groups')
  if (Array.isArray(grouped)) grouped.forEach(group => { const item = record(group); add(read(item, 'categories', 'options', 'items'), string(read(item, 'supplierCode')), string(read(item, 'supplierName'))) })
  add(read(root, 'warehouseCategories', 'categories', 'options'))
  if (!groups.length) add(Array.isArray(data) ? data : undefined)
  return groups
}
export function buildSalesDetailSupplierCategoryTree(groups: SalesDetailCategoryGroup[], supplierLabel: (code: string) => string): SalesDetailCategoryTreeNode[] {
  return groups.map((group, index) => {
    const groupTitle = group.supplierName || supplierLabel(group.supplierCode || '')
    const nodes = new Map(group.options.map(option => [option.guid, {
      key: option.guid, value: option.guid, title: option.label,
      searchText: `${groupTitle} ${option.name}`, children: [] as SalesDetailCategoryTreeNode[],
    }]))
    const roots: SalesDetailCategoryTreeNode[] = []
    group.options.forEach(option => {
      const node = nodes.get(option.guid)!
      const parent = option.parentGuid ? nodes.get(option.parentGuid) : undefined
      if (parent) parent.children.push(node)
      else roots.push(node)
    })
    // 供应商只用于分组，不能作为分类 GUID 提交。
    return { key: `supplier-group-${index}`, value: `supplier-group-${index}`, title: groupTitle,
      searchText: groupTitle, selectable: false, disableCheckbox: true, children: roots }
  })
}
export async function fetchSalesDetailCategoryOptions(kind: 'australia' | 'china', supplierCodes: string[], signal: AbortSignal): Promise<SalesDetailCategoryGroup[]> {
  const raw = await request<unknown>('/api/react/v1/dashboard/sales-detail-view/category-options', { signal, params: { kind, supplierCodes } }), response = record(raw)
  if (response.success === false || response.Success === false) throw new Error(string(read(response, 'message')) || '分类选项加载失败')
  return normalizeSalesDetailCategoryOptions(raw)
}
