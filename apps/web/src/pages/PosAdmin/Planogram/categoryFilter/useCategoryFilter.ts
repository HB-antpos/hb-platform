import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getActiveLocalSuppliers } from '../../../../services/localSupplierService'
import { getCategoryTree as getWarehouseCategoryTree, type WarehouseCategoryNode } from '../../../../services/warehouseCategoryService'
import type { LocalSupplierDto } from '../../../../types/localSupplier'
import type { LocalSupplierCategoryNode } from '../../../../types/localSupplierCategory'
import type { ProductSpecScopeFilter } from '../../../../types/planogram'
import { findSupplierCategoryNamePath, type SupplierCategoryCascaderOption } from '../../ProductManagement/supplierCategoryOptions'
import { gradeShortKey } from '../gradeDisplay'
import { useSupplierCategoryTrees } from '../../ProductManagement/useSupplierCategoryTrees'
import {
  buildFilterChips,
  buildSupplierCascaderOptions,
  buildSupplierFilterOptions,
  buildWarehouseTreeData,
  countActiveFilters,
  createEmptyFilterState,
  scopeFilterKey,
  toScopeFilter,
  toSupplierCascaderValue,
  withGrades,
  withSupplierCascader,
  withWarehouseCategories,
  type FilterChip,
  type FilterChipId,
  type ProductFilterState,
  type SupplierFilterOption,
  type WarehouseTreeNode,
} from './categoryFilterLogic'

/** 懒加载状态：idle = 还没人需要；loading / ready / error 与一般请求一致。 */
export type FilterLoadStatus = 'idle' | 'loading' | 'ready' | 'error'

interface LoadState<T> {
  status: FilterLoadStatus
  data: T
}

/** 哪一类基础数据加载失败（供界面各自给提示；失败不影响商品列表本身）。 */
export type CategoryFilterLoadKind = 'warehouse' | 'suppliers' | 'supplierTree'

export interface UseCategoryFilterOptions {
  /** 仓库分类树取数；默认复用商品管理页的 warehouseCategoryService.getCategoryTree。 */
  loadWarehouseTree?: () => Promise<WarehouseCategoryNode[]>
  /** 启用的本地供应商取数；默认复用 localSupplierService.getActiveLocalSuppliers。 */
  loadSuppliers?: () => Promise<LocalSupplierDto[]>
  /** 某个供应商的分类树取数；默认复用 localSupplierCategoryService.getLocalSupplierCategoryTree（经 useSupplierCategoryTrees 缓存）。 */
  loadSupplierTree?: (supplierCode: string) => Promise<LocalSupplierCategoryNode[]>
  /** 基础数据加载失败时回调（界面可弹一条提示）；失败状态同时体现在各自的 status 上。 */
  onLoadError?: (kind: CategoryFilterLoadKind, error: unknown) => void
}

export interface CategoryFilterApi {
  state: ProductFilterState
  /** 附录 D.1 的过滤条件；按状态记忆，条件不变时引用不变（可直接放进 useMemo / 请求键依赖）。 */
  scope: ProductSpecScopeFilter
  /** 过滤条件的稳定键（选择顺序无关），用来做请求键与「条件变了回第 1 页」。 */
  scopeKey: string
  /** 已生效的条件组数（仓库分类 / 供应商分类 / 等级，各算 1）。 */
  activeCount: number
  hasActive: boolean

  setWarehouseCategories: (guids: readonly string[]) => void
  /** 供应商分类级联变更（路径或 undefined=清空）。 */
  setSupplierPath: (path: readonly string[] | undefined) => void
  setGrades: (grades: readonly string[]) => void
  /** 清除全部过滤条件（不动已加载好的分类树）。 */
  clear: () => void
  /** 条件标签（每组条件一个，可单独删除）。 */
  chips: FilterChip[]
  /** 删除某一组条件（对应标签上的 ×）。 */
  removeGroup: (id: FilterChipId) => void

  /**
   * 首次需要分类数据时调用（用户打开下拉、打开筛选弹层）：idle 才会发起请求，已在加载 / 已成功 / 已失败都不重复发起
   * （失败后由 reload* 显式重试）。分类树不在页面挂载时加载，不拖慢列表。
   */
  ensureLoaded: () => void
  warehouse: {
    status: FilterLoadStatus
    treeData: WarehouseTreeNode[]
    reload: () => void
  }
  suppliers: {
    status: FilterLoadStatus
    options: SupplierFilterOption[]
    reload: () => void
  }
  cascader: {
    options: SupplierCategoryCascaderOption[]
    value: string[] | undefined
    /** 展开某个供应商且其分类树未加载时调用（懒加载）。 */
    loadSupplier: (supplierCode: string) => void
    /** 点击「加载失败，点击重试」节点时调用，不改变筛选条件。 */
    retrySupplier: (supplierCode: string) => void
  }
}

const IDLE_WAREHOUSE: LoadState<WarehouseCategoryNode[]> = { status: 'idle', data: [] }
const IDLE_SUPPLIERS: LoadState<SupplierFilterOption[]> = { status: 'idle', data: [] }

/**
 * 商品范围过滤的状态 + 基础数据（仓库分类树、本地供应商、供应商分类树）加载。
 * - 状态与「状态 → 查询参数」转换在 categoryFilterLogic.ts（纯函数，已单测）；
 * - 供应商分类树按供应商懒加载并缓存（复用商品管理页的 useSupplierCategoryTrees）；
 * - 过期响应（重试后旧请求才回来）和卸载后的响应一律丢弃。
 */
export function useCategoryFilter(options: UseCategoryFilterOptions = {}): CategoryFilterApi {
  const { t } = useTranslation()
  const loadWarehouse = options.loadWarehouseTree ?? getWarehouseCategoryTree
  const loadSupplierList = options.loadSuppliers ?? getActiveLocalSuppliers
  const trees = useSupplierCategoryTrees(options.loadSupplierTree)

  const [state, setState] = useState<ProductFilterState>(createEmptyFilterState)
  const [warehouseState, setWarehouseState] = useState(IDLE_WAREHOUSE)
  const [supplierState, setSupplierState] = useState(IDLE_SUPPLIERS)

  // 回调与取数函数放 ref：对外暴露的方法引用保持稳定，调用方不必为它们重建 memo
  const latest = useRef({ loadWarehouse, loadSupplierList, onLoadError: options.onLoadError, trees })
  latest.current = { loadWarehouse, loadSupplierList, onLoadError: options.onLoadError, trees }
  const statusRef = useRef({ warehouse: warehouseState.status, suppliers: supplierState.status })
  statusRef.current = { warehouse: warehouseState.status, suppliers: supplierState.status }
  const aliveRef = useRef(true)
  const runRef = useRef({ warehouse: 0, suppliers: 0 })

  useEffect(() => {
    aliveRef.current = true
    return () => {
      aliveRef.current = false
    }
  }, [])

  const loadWarehouseTree = useCallback(() => {
    runRef.current.warehouse += 1
    const run = runRef.current.warehouse
    statusRef.current = { ...statusRef.current, warehouse: 'loading' }
    setWarehouseState((current) => ({ status: 'loading', data: current.data }))
    latest.current.loadWarehouse().then(
      (nodes) => {
        if (!aliveRef.current || run !== runRef.current.warehouse) return
        setWarehouseState({ status: 'ready', data: nodes ?? [] })
      },
      (error: unknown) => {
        if (!aliveRef.current || run !== runRef.current.warehouse) return
        setWarehouseState((current) => ({ status: 'error', data: current.data }))
        latest.current.onLoadError?.('warehouse', error)
      },
    )
  }, [])

  const loadSuppliers = useCallback(() => {
    runRef.current.suppliers += 1
    const run = runRef.current.suppliers
    statusRef.current = { ...statusRef.current, suppliers: 'loading' }
    setSupplierState((current) => ({ status: 'loading', data: current.data }))
    latest.current.loadSupplierList().then(
      (list) => {
        if (!aliveRef.current || run !== runRef.current.suppliers) return
        setSupplierState({ status: 'ready', data: buildSupplierFilterOptions(list) })
      },
      (error: unknown) => {
        if (!aliveRef.current || run !== runRef.current.suppliers) return
        setSupplierState((current) => ({ status: 'error', data: current.data }))
        latest.current.onLoadError?.('suppliers', error)
      },
    )
  }, [])

  const ensureLoaded = useCallback(() => {
    if (statusRef.current.warehouse === 'idle') loadWarehouseTree()
    if (statusRef.current.suppliers === 'idle') loadSuppliers()
  }, [loadSuppliers, loadWarehouseTree])

  const loadSupplier = useCallback((supplierCode: string) => {
    latest.current.trees.ensure(supplierCode).catch((error: unknown) => {
      if (aliveRef.current) latest.current.onLoadError?.('supplierTree', error)
    })
  }, [])
  const retrySupplier = useCallback((supplierCode: string) => {
    latest.current.trees.reload(supplierCode).catch((error: unknown) => {
      if (aliveRef.current) latest.current.onLoadError?.('supplierTree', error)
    })
  }, [])

  const setWarehouseCategories = useCallback((guids: readonly string[]) => setState((current) => withWarehouseCategories(current, guids)), [])
  const setSupplierPath = useCallback((path: readonly string[] | undefined) => setState((current) => withSupplierCascader(current, path)), [])
  const setGrades = useCallback((grades: readonly string[]) => setState((current) => withGrades(current, grades)), [])
  const clear = useCallback(() => setState((current) => (countActiveFilters(current) === 0 ? current : createEmptyFilterState())), [])

  const scope = useMemo(() => toScopeFilter(state), [state])
  const scopeKey = useMemo(() => scopeFilterKey(scope), [scope])
  const activeCount = countActiveFilters(state)

  const treeData = useMemo(() => buildWarehouseTreeData(warehouseState.data), [warehouseState.data])
  const removeGroup = useCallback((id: FilterChipId) => {
    if (id === 'warehouse') setWarehouseCategories([])
    else if (id === 'supplier') setSupplierPath(undefined)
    else setGrades([])
  }, [setGrades, setSupplierPath, setWarehouseCategories])
  const labels = useMemo(() => ({
    unassigned: t('planogram.categoryFilter.unassigned'),
    empty: t('planogram.categoryFilter.emptyTree'),
    retry: t('planogram.categoryFilter.loadFailedRetry'),
  }), [t])
  // trees 的引用随缓存版本变化：某个供应商的分类树加载完 / 失败后选项会重建
  const cascaderOptions = useMemo(
    () => buildSupplierCascaderOptions({ suppliers: supplierState.data, getTree: trees.get, labels }),
    [labels, supplierState.data, trees],
  )
  const cascaderValue = useMemo(
    () => toSupplierCascaderValue(state, (code) => trees.get(code)?.nodes),
    [state, trees],
  )

  // 条件标签：名称取自已加载的分类树 / 供应商列表；没加载到的退回编码（选中过就说明加载过，只会出现在加载失败重试期间）
  const chips = useMemo(() => {
    const warehouseNames = new Map<string, string>()
    const collect = (nodes: WarehouseTreeNode[]) => nodes.forEach((node) => {
      warehouseNames.set(node.value, node.pathLabel)
      if (node.children) collect(node.children)
    })
    collect(treeData)
    return buildFilterChips(state, {
      warehouseLabel: (guid) => warehouseNames.get(guid),
      supplierLabel: (code) => supplierState.data.find((option) => option.value === code)?.name,
      supplierCategoryLabel: (code, guid) => findSupplierCategoryNamePath(trees.get(code)?.nodes ?? [], guid)?.join(' / '),
      unassigned: t('planogram.categoryFilter.unassigned'),
      gradeLabel: (grade) => t(gradeShortKey(grade)),
      more: (count) => t('planogram.categoryFilter.more', { count }),
    })
  }, [state, supplierState.data, t, treeData, trees])

  return {
    state,
    scope,
    scopeKey,
    activeCount,
    hasActive: activeCount > 0,
    setWarehouseCategories,
    setSupplierPath,
    setGrades,
    clear,
    chips,
    removeGroup,
    ensureLoaded,
    warehouse: { status: warehouseState.status, treeData, reload: loadWarehouseTree },
    suppliers: { status: supplierState.status, options: supplierState.data, reload: loadSuppliers },
    cascader: { options: cascaderOptions, value: cascaderValue, loadSupplier, retrySupplier },
  }
}
