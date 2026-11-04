/**
 * 商品陈列图「商品范围过滤」的纯逻辑（商品陈列参数列表、设计器商品库、批量修改「按当前筛选」共用，附录 D.1）。
 * 不依赖 React / antd，便于单元测试；状态 hook 在 useCategoryFilter.ts，控件形态另行设计。
 *
 * 过滤条件三组：
 * - 仓库分类（多选，服务端展开含全部子分类）；
 * - 本地供应商分类（供应商 → 分类级联，含「未归类」伪叶子）。**隐藏供应商 200**：200 的分类就是仓库分类，已由「仓库分类」覆盖；
 * - 商品等级（A / B / C / D / none=未分级，多选取并集）。
 *
 * 供应商级联复用商品管理页的纯函数（supplierCategoryFilter.ts / supplierCategoryOptions.ts），语义与商品列表一致；
 * 这里不复制其逻辑，只在外面包一层「隐藏 200 + 多选仓库分类 + 等级」。
 */
import type { WarehouseCategoryNode } from '../../../../services/warehouseCategoryService'
import { HOT_BARGAIN_SUPPLIER_CODE } from '../../../../types/localSupplierCategory'
import {
  PLANOGRAM_GRADE_VALUES,
  type PlanogramProductGrade,
  type ProductSpecBatchFilter,
  type ProductSpecListQuery,
  type ProductSpecScopeFilter,
  type ProductSpecStatusFilter,
} from '../../../../types/planogram'
import {
  applySupplierCategoryCascaderChange,
  toSupplierCategoryCascaderValue,
} from '../../ProductManagement/supplierCategoryFilter'
import {
  buildSupplierCategoryCascaderOptions,
  findSupplierCategoryGuidPath,
  type SupplierCategoryCascaderLabels,
  type SupplierCategoryCascaderOption,
} from '../../ProductManagement/supplierCategoryOptions'
import type { SupplierCategoryTreeEntry } from '../../ProductManagement/supplierCategoryTreeCache'

// ---------------------------------------------------------------------------
// 过滤状态
// ---------------------------------------------------------------------------

export interface ProductFilterState {
  /** 仓库分类 GUID（多选；服务端按分类树展开含子分类）。 */
  warehouseCategoryGuids: string[]
  /** 本地供应商编码（不会是 200：级联里隐藏）。 */
  supplierCode?: string
  /** 本地供应商分类 GUID（含子分类）；与 supplierCategoryUnassignedOnly 互斥。 */
  supplierCategoryGuid?: string
  /** 只看该供应商「未归类」的商品。 */
  supplierCategoryUnassignedOnly: boolean
  /** 商品等级（取并集）；已按 A→D→none 的固定顺序排好。 */
  grades: PlanogramProductGrade[]
}

export function createEmptyFilterState(): ProductFilterState {
  return {
    warehouseCategoryGuids: [],
    supplierCode: undefined,
    supplierCategoryGuid: undefined,
    supplierCategoryUnassignedOnly: false,
    grades: [],
  }
}

/** 在本页隐藏的供应商：200（Hot Bargain）的供应商分类就是仓库分类。 */
export const HIDDEN_SUPPLIER_CODES: readonly string[] = [HOT_BARGAIN_SUPPLIER_CODE]

function sameList<T>(left: readonly T[], right: readonly T[]): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index])
}

/** 去空白、去空串、去重，保持首次出现的顺序。 */
function uniqueTrimmed(values: readonly string[]): string[] {
  return [...new Set(values.map((value) => (typeof value === 'string' ? value.trim() : '')).filter(Boolean))]
}

/** 仓库分类多选变更；内容没变时返回原对象（避免无意义的重新请求）。 */
export function withWarehouseCategories(state: ProductFilterState, guids: readonly string[]): ProductFilterState {
  const next = uniqueTrimmed(guids)
  return sameList(next, state.warehouseCategoryGuids) ? state : { ...state, warehouseCategoryGuids: next }
}

/** 等级多选变更：只认 A / B / C / D / none，去重，并按固定顺序排列（保证状态与请求键稳定）。 */
export function withGrades(state: ProductFilterState, grades: readonly string[]): ProductFilterState {
  const wanted = new Set<string>(grades)
  const next = PLANOGRAM_GRADE_VALUES.filter((grade) => wanted.has(grade))
  return sameList(next, state.grades) ? state : { ...state, grades: next }
}

/**
 * 供应商分类级联变更（路径 = [供应商, ...分类 GUID 路径] 或 [供应商, '__unassigned__']；undefined / 空 = 清空）。
 * 直接复用商品管理页的 applySupplierCategoryCascaderChange：传入的 warehouseCategoryGuid 恒为空，
 * 因为 200 在这里被隐藏，级联里不会出现「200 下第二层就是仓库分类」的分支。
 * 路径第一项是被隐藏的供应商（不应出现）时按清空处理，宁可不过滤也不带一个看不见的条件。
 */
export function withSupplierCascader(state: ProductFilterState, value: readonly string[] | undefined): ProductFilterState {
  const path = (value ?? []).filter(Boolean)
  const effective = path.length > 0 && HIDDEN_SUPPLIER_CODES.includes(path[0]) ? [] : path
  const next = applySupplierCategoryCascaderChange(
    {
      supplierCode: state.supplierCode,
      warehouseCategoryGuid: undefined,
      supplierCategoryGuid: state.supplierCategoryGuid,
      supplierCategoryUnassignedOnly: state.supplierCategoryUnassignedOnly,
    },
    effective,
  )
  if (
    next.supplierCode === state.supplierCode
    && next.supplierCategoryGuid === state.supplierCategoryGuid
    && next.supplierCategoryUnassignedOnly === state.supplierCategoryUnassignedOnly
  ) return state
  return {
    ...state,
    supplierCode: next.supplierCode,
    supplierCategoryGuid: next.supplierCategoryGuid,
    supplierCategoryUnassignedOnly: next.supplierCategoryUnassignedOnly,
  }
}

/** 状态 → 级联框显示值；分类树还没加载到时只显示到供应商层，不把 GUID 当标签露出。 */
export function toSupplierCascaderValue(
  state: ProductFilterState,
  getTreeNodes: (supplierCode: string) => SupplierCategoryTreeEntry['nodes'] | undefined,
): string[] | undefined {
  return toSupplierCategoryCascaderValue(
    {
      supplierCode: state.supplierCode,
      warehouseCategoryGuid: undefined,
      supplierCategoryGuid: state.supplierCategoryGuid,
      supplierCategoryUnassignedOnly: state.supplierCategoryUnassignedOnly,
    },
    {
      findWarehouseGuidPath: () => undefined,
      findSupplierGuidPath: (code, guid) => findSupplierCategoryGuidPath(getTreeNodes(code) ?? [], guid),
    },
  )
}

// ---------------------------------------------------------------------------
// 状态 → 附录 D.1 查询参数
// ---------------------------------------------------------------------------

/**
 * 状态 → 过滤条件（附录 D.1 的字段名）。**只输出有值的条件**：没选的条件不出现，请求键与查询串都保持干净。
 * - 仓库分类 → warehouseCategoryGuids；
 * - 选了供应商 → localSupplierCode；选了该供应商下的某个分类 → supplierCategoryGuids=[guid]；选了「未归类」→ supplierCategoryUnassignedOnly=true；
 * - 等级 → grades。
 */
export function toScopeFilter(state: ProductFilterState): ProductSpecScopeFilter {
  const filter: ProductSpecScopeFilter = {}
  if (state.warehouseCategoryGuids.length > 0) filter.warehouseCategoryGuids = [...state.warehouseCategoryGuids]
  if (state.supplierCode) {
    filter.localSupplierCode = state.supplierCode
    if (state.supplierCategoryUnassignedOnly) filter.supplierCategoryUnassignedOnly = true
    else if (state.supplierCategoryGuid) filter.supplierCategoryGuids = [state.supplierCategoryGuid]
  }
  if (state.grades.length > 0) filter.grades = [...state.grades]
  return filter
}

/** 过滤条件的稳定键（数组排序后序列化）：同一组条件无论选择顺序如何都得到同一个键。 */
export function scopeFilterKey(filter: ProductSpecScopeFilter): string {
  return JSON.stringify({
    w: [...(filter.warehouseCategoryGuids ?? [])].sort(),
    s: filter.localSupplierCode ?? '',
    c: [...(filter.supplierCategoryGuids ?? [])].sort(),
    u: filter.supplierCategoryUnassignedOnly === true,
    g: [...(filter.grades ?? [])].sort(),
  })
}

/** 已生效的条件组数（用于「筛选」按钮上的条数）：仓库分类 / 供应商分类 / 等级，每组最多算 1。 */
export function countActiveFilters(state: ProductFilterState): number {
  return (state.warehouseCategoryGuids.length > 0 ? 1 : 0)
    + (state.supplierCode ? 1 : 0)
    + (state.grades.length > 0 ? 1 : 0)
}

export function hasActiveFilter(state: ProductFilterState): boolean {
  return countActiveFilters(state) > 0
}

/** 批量修改「按当前筛选」的目标：搜索词、状态与全部范围条件原样带给服务端（空条件给空数组 / null / false，形状固定）。 */
export function toBatchFilter(
  base: { keyword?: string; status: ProductSpecStatusFilter },
  scope: ProductSpecScopeFilter,
): ProductSpecBatchFilter {
  return {
    keyword: base.keyword?.trim() ?? '',
    status: base.status,
    warehouseCategoryGuids: [...(scope.warehouseCategoryGuids ?? [])],
    localSupplierCode: scope.localSupplierCode ?? null,
    supplierCategoryGuids: [...(scope.supplierCategoryGuids ?? [])],
    supplierCategoryUnassignedOnly: scope.supplierCategoryUnassignedOnly === true,
    grades: [...(scope.grades ?? [])],
  }
}

// ---------------------------------------------------------------------------
// 仓库分类树 / 供应商选项
// ---------------------------------------------------------------------------

export interface WarehouseTreeNode {
  /** 分类 GUID。 */
  value: string
  /** 下拉里显示的名称。 */
  title: string
  /** 完整路径「一级 / 二级 / 三级」：已选标签用它区分重名分类。 */
  pathLabel: string
  /** 有子分类：选它等于选了全部子分类（服务端展开），标签上会注明「含子类」。 */
  hasChildren: boolean
  children?: WarehouseTreeNode[]
}

/** 仓库分类树 → TreeSelect 数据。停用分类也保留（历史商品仍可能挂在上面），与商品管理页一致。 */
export function buildWarehouseTreeData(nodes: readonly WarehouseCategoryNode[] | undefined, ancestors: readonly string[] = []): WarehouseTreeNode[] {
  return (nodes ?? []).map((node) => {
    const path = [...ancestors, node.categoryName]
    const children = buildWarehouseTreeData(node.children, path)
    return {
      value: node.categoryGUID,
      title: node.categoryName,
      pathLabel: path.join(' / '),
      hasChildren: children.length > 0,
      children: children.length > 0 ? children : undefined,
    }
  })
}

export interface SupplierFilterOption {
  /** 「名称 (编码)」，与商品管理页一致。 */
  label: string
  value: string
  /** 供应商名称（没有名称时为编码）：条件标签用。 */
  name: string
}

/** 本地供应商列表 → 级联第一层：隐藏 200，按编码去重，标签「名称 (编码)」与商品管理页一致。 */
export function buildSupplierFilterOptions(
  suppliers: ReadonlyArray<{ localSupplierCode: string; name?: string | null }> | undefined,
): SupplierFilterOption[] {
  const seen = new Set<string>()
  const result: SupplierFilterOption[] = []
  for (const supplier of suppliers ?? []) {
    const code = supplier.localSupplierCode?.trim()
    if (!code || HIDDEN_SUPPLIER_CODES.includes(code) || seen.has(code)) continue
    seen.add(code)
    result.push({ value: code, label: supplier.name ? `${supplier.name} (${code})` : code, name: supplier.name || code })
  }
  return result
}

/**
 * 级联选项：复用商品管理页的 buildSupplierCategoryCascaderOptions（供应商 → 「未归类」伪叶子 + 该供应商分类树，
 * 未加载的供应商不给 children 由 loadData 懒加载；加载失败给可点的重试节点，空树给禁用提示）。
 * 传入的供应商已不含 200，仓库分类树传空（这里不需要 200 的仓库分类分支）。
 */
export function buildSupplierCascaderOptions(input: {
  suppliers: readonly SupplierFilterOption[]
  getTree: (supplierCode: string) => SupplierCategoryTreeEntry | undefined
  labels: SupplierCategoryCascaderLabels
}): SupplierCategoryCascaderOption[] {
  return buildSupplierCategoryCascaderOptions({
    suppliers: input.suppliers.filter((supplier) => !HIDDEN_SUPPLIER_CODES.includes(supplier.value)),
    warehouseTree: [],
    getTree: input.getTree,
    labels: input.labels,
  })
}

// ---------------------------------------------------------------------------
// 生效条件的标签（设计器商品库「可逐个删除的条件标签」）
// ---------------------------------------------------------------------------

export type FilterChipId = 'warehouse' | 'supplier' | 'grades'

export interface FilterChip {
  id: FilterChipId
  label: string
}

/** 生成标签文字需要的查名函数与文案（树没加载到时查不到名称，该项退回到编码 / 省略）。 */
export interface FilterChipLookups {
  /** 仓库分类 GUID → 路径名称。 */
  warehouseLabel: (guid: string) => string | undefined
  /** 供应商编码 → 显示名（不带编码括号）。 */
  supplierLabel: (code: string) => string | undefined
  /** 供应商分类 GUID → 名称路径（一级 / 二级）。 */
  supplierCategoryLabel: (code: string, guid: string) => string | undefined
  unassigned: string
  /** 等级 → 简短名（A / B / 未分级）。 */
  gradeLabel: (grade: PlanogramProductGrade) => string
  /** 仓库分类选了多个时的省略写法：「手工、圣诞 等 3 项」里的「等 N 项」。 */
  more: (count: number) => string
}

/**
 * 状态 → 条件标签（每组条件一个标签，可单独删除）：
 * - 仓库分类：前两个名称用「、」连接，多于两个时追加「等 N 项」；
 * - 供应商分类：「供应商 / 分类路径」，只选供应商时只有供应商名，「未归类」写成「供应商 / 未归类」；
 * - 等级：「A、B、未分级」。
 */
export function buildFilterChips(state: ProductFilterState, lookups: FilterChipLookups): FilterChip[] {
  const chips: FilterChip[] = []
  if (state.warehouseCategoryGuids.length > 0) {
    const names = state.warehouseCategoryGuids.map((guid) => lookups.warehouseLabel(guid) ?? guid)
    const shown = names.slice(0, 2).join('、')
    chips.push({ id: 'warehouse', label: names.length > 2 ? `${shown} ${lookups.more(names.length)}` : shown })
  }
  if (state.supplierCode) {
    const supplier = lookups.supplierLabel(state.supplierCode) ?? state.supplierCode
    const tail = state.supplierCategoryUnassignedOnly
      ? lookups.unassigned
      : state.supplierCategoryGuid
        ? lookups.supplierCategoryLabel(state.supplierCode, state.supplierCategoryGuid)
        : undefined
    chips.push({ id: 'supplier', label: tail ? `${supplier} / ${tail}` : supplier })
  }
  if (state.grades.length > 0) {
    chips.push({ id: 'grades', label: state.grades.map((grade) => lookups.gradeLabel(grade)).join('、') })
  }
  return chips
}

/** 批量修改「按当前筛选」的目标 → 列表查询（状态筛选 / 分页由调用方给；用来再查一次「已设参数」数）。 */
export function batchFilterToQuery(
  filter: ProductSpecBatchFilter,
  override: { status: ProductSpecStatusFilter; page: number; pageSize: number },
): ProductSpecListQuery {
  return {
    keyword: filter.keyword || undefined,
    status: override.status,
    ...(filter.warehouseCategoryGuids.length > 0 ? { warehouseCategoryGuids: filter.warehouseCategoryGuids } : {}),
    ...(filter.localSupplierCode ? { localSupplierCode: filter.localSupplierCode } : {}),
    ...(filter.supplierCategoryGuids.length > 0 ? { supplierCategoryGuids: filter.supplierCategoryGuids } : {}),
    ...(filter.supplierCategoryUnassignedOnly ? { supplierCategoryUnassignedOnly: true } : {}),
    ...(filter.grades.length > 0 ? { grades: filter.grades } : {}),
    page: override.page,
    pageSize: override.pageSize,
  }
}
