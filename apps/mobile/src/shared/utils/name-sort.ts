/**
 * 选择列表（分店、供应商等）统一的「按名字排序」比较器。
 *
 * 规则：按名字排，名字为空才退回编号；忽略大小写与重音，数字按数值比（"Store 2" 在 "Store 10" 之前）；
 * 名字相同再按编号，仍相同则保持原有顺序（稳定排序）。中文界面用 zh-CN 排序，其余用 en。
 * 只用于「选一个」的列表，不要用在按金额 / 逾期数等业务含义排名的报表上。
 */
export type NameSortKey = Readonly<{ name?: string | null; code?: string | null }>;

function collatorLocale(language?: string): string {
  return language?.toLowerCase().startsWith("zh") ? "zh-CN" : "en";
}

function displayName(key: NameSortKey): string {
  return (key.name ?? "").trim() || (key.code ?? "").trim();
}

export function compareByName(left: NameSortKey, right: NameSortKey, language?: string): number {
  const byName = displayName(left).localeCompare(displayName(right), collatorLocale(language), {
    numeric: true,
    sensitivity: "base",
  });
  if (byName !== 0) return byName;
  return (left.code ?? "").localeCompare(right.code ?? "", "en", { numeric: true, sensitivity: "base" });
}

/** 返回按名字排序的新数组，不修改入参；名字与编号都相同的项保持原顺序。 */
export function sortByName<T>(
  items: readonly T[],
  pick: (item: T) => NameSortKey,
  language?: string,
): T[] {
  return items
    .map((item, index) => ({ item, index, key: pick(item) }))
    .sort((a, b) => compareByName(a.key, b.key, language) || a.index - b.index)
    .map((entry) => entry.item);
}

export function sortStoresByName<T extends { storeCode: string; storeName?: string | null }>(
  stores: readonly T[],
  language?: string,
): T[] {
  return sortByName(stores, (store) => ({ name: store.storeName, code: store.storeCode }), language);
}

export function sortSuppliersByName<T extends { supplierCode: string; supplierName?: string | null }>(
  suppliers: readonly T[],
  language?: string,
): T[] {
  return sortByName(suppliers, (supplier) => ({ name: supplier.supplierName, code: supplier.supplierCode }), language);
}
