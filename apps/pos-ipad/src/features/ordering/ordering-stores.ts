/** 订货工作台里与界面无关的小工具：订货助手分店列表、默认分店、闲置倒计时显示。 */
export type OrderingStore = Readonly<{ storeCode: string; storeName: string }>;

export function toOrderingStores(data: unknown): OrderingStore[] {
  const list = data && typeof data === "object" ? (data as Record<string, unknown>).stores : null;
  if (!Array.isArray(list)) return [];
  const seen = new Set<string>();
  const result: OrderingStore[] = [];
  for (const item of list) {
    if (!item || typeof item !== "object") continue;
    const record = item as Record<string, unknown>;
    const storeCode = typeof record.storeCode === "string" ? record.storeCode.trim() : "";
    if (!storeCode || seen.has(storeCode.toLowerCase())) continue;
    seen.add(storeCode.toLowerCase());
    const storeName = typeof record.storeName === "string" && record.storeName.trim() ? record.storeName.trim() : storeCode;
    result.push({ storeCode, storeName });
  }
  return result;
}

/** 订货助手默认分店：本机绑定的分店（账号能访问时）→ 只有一家时直接用 → 否则让员工选。 */
export function resolveDefaultOrderingStore(
  stores: readonly OrderingStore[],
  deviceStoreCode: string | null,
): OrderingStore | null {
  const device = deviceStoreCode?.trim().toLowerCase();
  if (device) {
    const match = stores.find((store) => store.storeCode.toLowerCase() === device);
    if (match) return match;
  }
  return stores.length === 1 ? stores[0]! : null;
}

export function formatIdleRemaining(ms: number): string {
  const totalSeconds = Math.max(0, Math.ceil(ms / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${seconds.toString().padStart(2, "0")}`;
}
