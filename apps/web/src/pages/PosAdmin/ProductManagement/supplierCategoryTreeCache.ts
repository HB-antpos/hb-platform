/**
 * 按供应商懒加载的分类树缓存（与 React 无关，便于单元测试）：
 * - 同一供应商并发请求合并成一个 in-flight；
 * - 强制刷新时保留旧数据（loaded 仍为 true），避免界面闪空；
 * - 每次发起请求递增代次，过期响应（被 invalidate 或更新的刷新取代）直接丢弃；
 * - patchNode 用于切换促销标记时的乐观更新。
 */
import type { LocalSupplierCategoryNode } from '../../../types/localSupplierCategory'

export type SupplierCategoryTreeStatus = 'loading' | 'ready' | 'error'

export interface SupplierCategoryTreeEntry {
  status: SupplierCategoryTreeStatus
  /** 是否至少成功加载过一次；刷新或刷新失败时仍可展示旧数据。 */
  loaded: boolean
  nodes: LocalSupplierCategoryNode[]
  error?: unknown
}

export type SupplierCategoryTreeFetcher = (supplierCode: string) => Promise<LocalSupplierCategoryNode[]>

export interface SupplierCategoryTreeCache {
  get: (supplierCode: string | undefined) => SupplierCategoryTreeEntry | undefined
  /** 已加载直接返回；加载中复用同一个请求；否则发起请求。 */
  ensure: (supplierCode: string) => Promise<LocalSupplierCategoryNode[]>
  /** 强制重新加载（例如切换促销、重新解析后刷新商品数）。 */
  reload: (supplierCode: string) => Promise<LocalSupplierCategoryNode[]>
  /** 丢弃某供应商（或全部）缓存，进行中的请求结果也会被忽略。 */
  invalidate: (supplierCode?: string) => void
  patchNode: (supplierCode: string, categoryGuid: string, patch: Partial<Omit<LocalSupplierCategoryNode, 'children'>>) => void
  subscribe: (listener: () => void) => () => void
}

export function normalizeSupplierCodeKey(supplierCode: string | undefined): string {
  return (supplierCode ?? '').trim()
}

function patchTree(
  nodes: LocalSupplierCategoryNode[],
  targetGuid: string,
  patch: Partial<Omit<LocalSupplierCategoryNode, 'children'>>,
): { nodes: LocalSupplierCategoryNode[]; changed: boolean } {
  let changed = false
  const next = nodes.map((node) => {
    if (node.categoryGuid.toLowerCase() === targetGuid) {
      changed = true
      return { ...node, ...patch, children: node.children }
    }
    const patchedChildren = patchTree(node.children ?? [], targetGuid, patch)
    if (!patchedChildren.changed) return node
    changed = true
    return { ...node, children: patchedChildren.nodes }
  })
  return { nodes: changed ? next : nodes, changed }
}

export function createSupplierCategoryTreeCache(fetcher: SupplierCategoryTreeFetcher): SupplierCategoryTreeCache {
  const entries = new Map<string, SupplierCategoryTreeEntry>()
  const inflight = new Map<string, Promise<LocalSupplierCategoryNode[]>>()
  const generations = new Map<string, number>()
  const listeners = new Set<() => void>()

  const notify = () => {
    listeners.forEach((listener) => listener())
  }

  const nextGeneration = (key: string) => {
    const generation = (generations.get(key) ?? 0) + 1
    generations.set(key, generation)
    return generation
  }

  const load = (key: string): Promise<LocalSupplierCategoryNode[]> => {
    const generation = nextGeneration(key)
    const previous = entries.get(key)
    entries.set(key, { status: 'loading', loaded: previous?.loaded ?? false, nodes: previous?.nodes ?? [] })
    notify()

    const promise = fetcher(key).then(
      (nodes) => {
        if (generations.get(key) === generation) {
          entries.set(key, { status: 'ready', loaded: true, nodes })
          inflight.delete(key)
          notify()
        }
        return nodes
      },
      (error: unknown) => {
        if (generations.get(key) === generation) {
          const current = entries.get(key)
          entries.set(key, { status: 'error', loaded: current?.loaded ?? false, nodes: current?.nodes ?? [], error })
          inflight.delete(key)
          notify()
        }
        throw error
      },
    )
    inflight.set(key, promise)
    return promise
  }

  return {
    get: (supplierCode) => {
      const key = normalizeSupplierCodeKey(supplierCode)
      return key ? entries.get(key) : undefined
    },
    ensure: (supplierCode) => {
      const key = normalizeSupplierCodeKey(supplierCode)
      if (!key) return Promise.resolve([])
      const current = entries.get(key)
      if (current?.status === 'ready') return Promise.resolve(current.nodes)
      const pending = inflight.get(key)
      if (pending) return pending
      return load(key)
    },
    reload: (supplierCode) => {
      const key = normalizeSupplierCodeKey(supplierCode)
      if (!key) return Promise.resolve([])
      return load(key)
    },
    invalidate: (supplierCode) => {
      const keys = supplierCode === undefined ? [...new Set([...entries.keys(), ...inflight.keys()])] : [normalizeSupplierCodeKey(supplierCode)]
      for (const key of keys) {
        nextGeneration(key)
        entries.delete(key)
        inflight.delete(key)
      }
      notify()
    },
    patchNode: (supplierCode, categoryGuid, patch) => {
      const key = normalizeSupplierCodeKey(supplierCode)
      const current = entries.get(key)
      if (!current) return
      const result = patchTree(current.nodes, categoryGuid.toLowerCase(), patch)
      if (!result.changed) return
      entries.set(key, { ...current, nodes: result.nodes })
      notify()
    },
    subscribe: (listener) => {
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
      }
    },
  }
}
