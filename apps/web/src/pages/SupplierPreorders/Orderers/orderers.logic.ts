// 下单人员授权页的纯逻辑：筛选、显示名、分店勾选。
import type { SupplierPreorderOrderer, SupplierPreorderStoreRef, SupplierPreorderUserCandidate } from '../../../types/supplierPreorder'

export type OrdererViewFilter = 'all' | 'canView'

export interface OrdererFilters {
  keyword: string
  /** 只看能替这家店下单的人；空 = 全部 */
  storeGuid?: string | null
  view: OrdererViewFilter
}

export function getOrdererDisplayName(person: Pick<SupplierPreorderOrderer, 'fullName' | 'userName'>): string {
  return person.fullName?.trim() || person.userName
}

/** 头像里的单字：取显示名第一个字符（中文名取姓，英文名取首字母大写）。 */
export function getPersonInitial(name: string): string {
  const first = Array.from(name.trim())[0]
  return first ? first.toUpperCase() : '?'
}

export function filterOrderers(orderers: SupplierPreorderOrderer[], filters: OrdererFilters): SupplierPreorderOrderer[] {
  const keyword = filters.keyword.trim().toLowerCase()
  return orderers.filter((person) => {
    if (filters.view === 'canView' && !person.canViewOtherStores) return false
    if (filters.storeGuid && !person.stores.some((store) => store.storeGuid === filters.storeGuid)) return false
    if (!keyword) return true
    return [person.fullName, person.userName, person.primaryStoreName, ...person.stores.map((store) => store.storeName)]
      .filter((value): value is string => Boolean(value))
      .some((value) => value.toLowerCase().includes(keyword))
  })
}

export function countCanViewOtherStores(orderers: SupplierPreorderOrderer[]): number {
  return orderers.filter((person) => person.canViewOtherStores).length
}

/** 勾选结果按「启用分店列表」的顺序排列，并丢掉已停用 / 不存在的分店。 */
export function normalizeStoreSelection(selected: string[], stores: SupplierPreorderStoreRef[]): string[] {
  const set = new Set(selected)
  return stores.filter((store) => set.has(store.storeGuid)).map((store) => store.storeGuid)
}

/**
 * 编辑时已有的分店里可能有已停用分店（不在启用列表里）：保存时要保留，避免编辑一次就把它悄悄删掉。
 * 返回 [启用列表里勾选的 ..., 不在启用列表里但原本就有的 ...]。
 */
export function mergeStoreSelection(
  selected: string[],
  stores: SupplierPreorderStoreRef[],
  originalStoreGuids: string[],
): string[] {
  const activeGuids = new Set(stores.map((store) => store.storeGuid))
  const keptInactive = originalStoreGuids.filter((guid) => !activeGuids.has(guid))
  return [...normalizeStoreSelection(selected, stores), ...keptInactive.filter((guid, index, list) => list.indexOf(guid) === index)]
}

/** 候选人显示：姓名（账号）· 主分店 */
export function formatCandidateLabel(candidate: Pick<SupplierPreorderUserCandidate, 'fullName' | 'userName' | 'primaryStoreName'>): string {
  const name = candidate.fullName?.trim()
  const base = name && name !== candidate.userName ? `${name} (${candidate.userName})` : candidate.userName
  return candidate.primaryStoreName ? `${base} · ${candidate.primaryStoreName}` : base
}
