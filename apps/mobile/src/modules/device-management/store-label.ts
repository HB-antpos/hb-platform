export interface DeviceStoreFields {
  storeCode?: string | null;
  storeName?: string | null;
}

/** 分店代码统一成去空格、大写的形式做查找键，避免 "bri " 与 "BRI" 对不上。 */
export function normalizeStoreCodeKey(storeCode?: string | null) {
  return storeCode?.trim().toUpperCase() ?? "";
}

/** 用页面已加载的分店列表建「代码 → 名称」表，供后端没回填名称时兜底。 */
export function buildStoreNameLookup(stores: readonly DeviceStoreFields[]): ReadonlyMap<string, string> {
  const lookup = new Map<string, string>();
  for (const store of stores) {
    const key = normalizeStoreCodeKey(store.storeCode);
    const name = store.storeName?.trim();
    if (key && name && !lookup.has(key)) lookup.set(key, name);
  }
  return lookup;
}

/**
 * 设备卡片上的分店文字：优先后端回填的名称，其次本地分店列表，最后只显示代码。
 * 有名称时附上代码（「Oran Park · 1023」），名称与代码相同则只显示一次。
 */
export function formatDeviceStoreLabel(
  device: DeviceStoreFields,
  lookup: ReadonlyMap<string, string>,
  fallback: string
) {
  const code = device.storeCode?.trim() || "";
  const name = device.storeName?.trim() || (code ? lookup.get(normalizeStoreCodeKey(code)) : undefined) || "";
  if (name && code && name.toUpperCase() !== code.toUpperCase()) return `${name} · ${code}`;
  return name || code || fallback;
}
