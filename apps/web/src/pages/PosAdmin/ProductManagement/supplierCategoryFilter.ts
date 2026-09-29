/**
 * 商品管理「供应商分类」筛选与保存的纯逻辑，不依赖 React/antd，便于单元测试。
 *
 * 筛选采用「一份状态、两个视图」：
 * - 顶部澳洲供应商 Select 与供应商分类级联框共享 supplierCode；
 * - 200（Hot Bargain）的供应商分类就是仓库分类，级联框在 200 下的第二层直接读写 warehouseCategoryGuid；
 * - 其他供应商的分类写 supplierCategoryGuid，「未归类」是每个供应商下的伪叶子；
 * - 级联框显示值完全由状态派生，不禁用任何控件。
 */
import { HOT_BARGAIN_SUPPLIER_CODE, type SupplierCategoryUpdatePayload } from '../../../types/localSupplierCategory'

/** 级联框里每个供应商下的首个伪叶子：只看该供应商未归类的商品。 */
export const SUPPLIER_CATEGORY_UNASSIGNED_VALUE = '__unassigned__'
/** 批量编辑的伪选项：清除人工指定，恢复按采集数据自动归类。 */
export const SUPPLIER_CATEGORY_RESTORE_AUTO_VALUE = '__auto__'
/** 伪节点统一前缀：空树提示、加载失败重试等不代表真实分类。 */
export const SUPPLIER_CATEGORY_PSEUDO_PREFIX = '__'

export interface SupplierCategoryFilterState {
  supplierCode?: string
  warehouseCategoryGuid?: string
  /** 非 200 供应商的分类 GUID；200 的分类走 warehouseCategoryGuid。 */
  supplierCategoryGuid?: string
  supplierCategoryUnassignedOnly: boolean
}

export type SupplierCategorySelection = Pick<SupplierCategoryFilterState, 'supplierCategoryGuid' | 'supplierCategoryUnassignedOnly'>

export function isSupplierCategoryPseudoValue(value: string | undefined): boolean {
  return Boolean(value && value.startsWith(SUPPLIER_CATEGORY_PSEUDO_PREFIX))
}

function normalizeCascaderPath(value: unknown): string[] {
  if (!Array.isArray(value)) return []
  return value
    .map((item) => (item === undefined || item === null ? '' : String(item)))
    .filter(Boolean)
}

/**
 * 级联框变更 → 新状态。
 * - 清空：清掉级联框当前展示的全部条件（在 200 下时连同仓库分类）；
 * - 200：第二层即仓库分类；只点到 200 本层时，若原本就在 200 下视为「回到上层」清掉仓库分类，
 *   从别的供应商切过来则保留独立设置的仓库分类筛选；
 * - 200 下选「未归类」是唯一需要主动清理的矛盾组合：同时清掉仓库分类；
 * - 其他供应商：只改供应商分类，仓库分类是独立筛选保持不变。
 */
export function applySupplierCategoryCascaderChange(
  state: SupplierCategoryFilterState,
  value: unknown,
  hotBargainCode = HOT_BARGAIN_SUPPLIER_CODE,
): SupplierCategoryFilterState {
  const path = normalizeCascaderPath(value)
  if (!path.length) {
    return {
      supplierCode: undefined,
      warehouseCategoryGuid: state.supplierCode === hotBargainCode ? undefined : state.warehouseCategoryGuid,
      supplierCategoryGuid: undefined,
      supplierCategoryUnassignedOnly: false,
    }
  }

  const [supplierCode, ...rest] = path
  const leaf = rest[rest.length - 1]
  const unassigned = leaf === SUPPLIER_CATEGORY_UNASSIGNED_VALUE
  const categoryGuid = leaf && !isSupplierCategoryPseudoValue(leaf) ? leaf : undefined

  if (supplierCode === hotBargainCode) {
    if (unassigned) {
      return { supplierCode, warehouseCategoryGuid: undefined, supplierCategoryGuid: undefined, supplierCategoryUnassignedOnly: true }
    }
    const keepIndependentWarehouseCategory = state.supplierCode !== hotBargainCode
    return {
      supplierCode,
      warehouseCategoryGuid: categoryGuid ?? (keepIndependentWarehouseCategory ? state.warehouseCategoryGuid : undefined),
      supplierCategoryGuid: undefined,
      supplierCategoryUnassignedOnly: false,
    }
  }

  return {
    supplierCode,
    warehouseCategoryGuid: state.warehouseCategoryGuid,
    supplierCategoryGuid: unassigned ? undefined : categoryGuid,
    supplierCategoryUnassignedOnly: unassigned,
  }
}

/** 顶部供应商 Select 变更：换供应商时清掉只属于旧供应商的分类条件，仓库分类保持不变。 */
export function applySupplierSelectChange(
  state: SupplierCategoryFilterState,
  supplierCode: string | undefined,
): SupplierCategoryFilterState {
  if ((supplierCode || undefined) === (state.supplierCode || undefined)) return state
  return {
    supplierCode: supplierCode || undefined,
    warehouseCategoryGuid: state.warehouseCategoryGuid,
    supplierCategoryGuid: undefined,
    supplierCategoryUnassignedOnly: false,
  }
}

/** 「更多筛选」里的仓库分类变更：200 下「未归类」与具体仓库分类互斥，以最后一次选择为准。 */
export function applyWarehouseCategoryFilterChange(
  state: SupplierCategoryFilterState,
  warehouseCategoryGuid: string | undefined,
  hotBargainCode = HOT_BARGAIN_SUPPLIER_CODE,
): SupplierCategoryFilterState {
  const conflictsWithUnassigned = state.supplierCode === hotBargainCode && Boolean(warehouseCategoryGuid)
  return {
    ...state,
    warehouseCategoryGuid,
    supplierCategoryUnassignedOnly: conflictsWithUnassigned ? false : state.supplierCategoryUnassignedOnly,
  }
}

/** 移除「供应商分类」标签：只清供应商分类与未归类，供应商和仓库分类保持不变。 */
export function clearSupplierCategoryFilter(state: SupplierCategoryFilterState): SupplierCategoryFilterState {
  return { ...state, supplierCategoryGuid: undefined, supplierCategoryUnassignedOnly: false }
}

export interface SupplierCategoryPathLookups {
  /** 仓库分类 GUID → 从根到该节点的 GUID 路径。 */
  findWarehouseGuidPath: (guid: string) => string[] | undefined
  /** 供应商分类 GUID → 该供应商树里从根到该节点的 GUID 路径（树未加载时返回 undefined）。 */
  findSupplierGuidPath: (supplierCode: string, guid: string) => string[] | undefined
}

/** 状态 → 级联框显示值。找不到分类路径时只显示到供应商层，避免把 GUID 当标签露出。 */
export function toSupplierCategoryCascaderValue(
  state: SupplierCategoryFilterState,
  lookups: SupplierCategoryPathLookups,
  hotBargainCode = HOT_BARGAIN_SUPPLIER_CODE,
): string[] | undefined {
  const { supplierCode } = state
  if (!supplierCode) return undefined
  if (state.supplierCategoryUnassignedOnly) return [supplierCode, SUPPLIER_CATEGORY_UNASSIGNED_VALUE]
  if (supplierCode === hotBargainCode) {
    const guidPath = state.warehouseCategoryGuid ? lookups.findWarehouseGuidPath(state.warehouseCategoryGuid) : undefined
    return guidPath?.length ? [supplierCode, ...guidPath] : [supplierCode]
  }
  const guidPath = state.supplierCategoryGuid ? lookups.findSupplierGuidPath(supplierCode, state.supplierCategoryGuid) : undefined
  return guidPath?.length ? [supplierCode, ...guidPath] : [supplierCode]
}

/** 状态 → 列表请求参数（200 的分类已由 warehouseCategoryGuid 表达，这里不重复发送）。 */
export function toSupplierCategoryQueryParams(
  state: SupplierCategoryFilterState,
): { supplierCategoryGuid?: string; supplierCategoryUnassignedOnly?: boolean } {
  const params: { supplierCategoryGuid?: string; supplierCategoryUnassignedOnly?: boolean } = {}
  if (state.supplierCategoryGuid) params.supplierCategoryGuid = state.supplierCategoryGuid
  if (state.supplierCategoryUnassignedOnly) params.supplierCategoryUnassignedOnly = true
  return params
}

/** 级联框显示文本：超过 maxSegments 段时折叠成「首 / … / 末」，避免 220px 宽的输入框被撑满。 */
export function formatCascaderDisplayLabels(labels: string[], maxSegments = 3): string {
  const segments = labels.map((label) => label.trim()).filter(Boolean)
  if (segments.length > maxSegments) {
    return [segments[0], '…', segments[segments.length - 1]].join(' / ')
  }
  return segments.join(' / ')
}

function normalizeCode(value: string | undefined): string | undefined {
  const trimmed = value?.trim()
  return trimmed || undefined
}

function sameGuid(left: string | undefined, right: string | undefined): boolean {
  return (left ?? '').trim().toLowerCase() === (right ?? '').trim().toLowerCase()
}

export interface SupplierCategoryUpdateInput {
  originalSupplierCode?: string
  nextSupplierCode?: string
  originalGuid?: string
  nextGuid?: string
}

/**
 * 单个商品保存时的供应商分类三态：
 * - 保存后是 200，或供应商与分类都没变 → 都不带；
 * - 选了新分类 → 带 supplierCategoryGUID（人工指定并锁定）；
 * - 原来有值现在清空，或换了供应商且没选新分类 → clearSupplierCategory: true（恢复自动归类）。
 */
export function resolveSupplierCategoryUpdate(
  input: SupplierCategoryUpdateInput,
  hotBargainCode = HOT_BARGAIN_SUPPLIER_CODE,
): SupplierCategoryUpdatePayload {
  const nextSupplierCode = normalizeCode(input.nextSupplierCode)
  if (nextSupplierCode === hotBargainCode) return {}

  const nextGuid = isSupplierCategoryPseudoValue(input.nextGuid) ? undefined : normalizeCode(input.nextGuid)
  const originalGuid = normalizeCode(input.originalGuid)
  const supplierChanged = nextSupplierCode !== normalizeCode(input.originalSupplierCode)

  if (supplierChanged) {
    return nextGuid ? { supplierCategoryGUID: nextGuid } : { clearSupplierCategory: true }
  }
  if (nextGuid) {
    return sameGuid(nextGuid, originalGuid) ? {} : { supplierCategoryGUID: nextGuid }
  }
  return originalGuid ? { clearSupplierCategory: true } : {}
}

/** 批量编辑：留空不修改；「恢复自动归类」伪选项 → 清除；其余 → 人工指定。 */
export function resolveBatchSupplierCategoryUpdate(value: string | undefined): SupplierCategoryUpdatePayload {
  if (value === SUPPLIER_CATEGORY_RESTORE_AUTO_VALUE) return { clearSupplierCategory: true }
  const guid = isSupplierCategoryPseudoValue(value) ? undefined : normalizeCode(value)
  return guid ? { supplierCategoryGUID: guid } : {}
}
