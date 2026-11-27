/**
 * 员工移动端「订货助手」的分店选择（纯逻辑，tsx 可直接测）。
 *
 * 中文注释：
 * 1. 分店列表来自 GET /api/react/v1/browser-extension/stores：后端已按员工可访问的分店过滤
 *    （与浏览器插件的分店下拉同一个接口），这里不再自己判断权限。
 * 2. 默认分店的优先顺序：上次在本机选过的（仍在列表里）→ 账号的主分店 → 列表只有一家时就是它；
 *    都不满足（多店且没选过）时返回 null，页面要求员工先选分店。
 * 3. 记住的分店按账号分开存，换账号不会带上一个人的选择。
 */
export type SupplierAssistantStore = Readonly<{ storeCode: string; storeName: string }>;

export function toSupplierAssistantStores(raw: unknown): SupplierAssistantStore[] {
  const stores = raw && typeof raw === "object" ? (raw as { stores?: unknown }).stores : null;
  if (!Array.isArray(stores)) return [];
  const seen = new Set<string>();
  const out: SupplierAssistantStore[] = [];
  for (const item of stores) {
    if (!item || typeof item !== "object") continue;
    const record = item as Record<string, unknown>;
    const storeCode = typeof record.storeCode === "string" ? record.storeCode.trim() : "";
    if (!storeCode || seen.has(storeCode.toUpperCase())) continue;
    seen.add(storeCode.toUpperCase());
    const storeName = typeof record.storeName === "string" && record.storeName.trim()
      ? record.storeName.trim()
      : storeCode;
    out.push(Object.freeze({ storeCode, storeName }));
  }
  return out;
}

export function resolveDefaultSupplierAssistantStore(
  stores: readonly SupplierAssistantStore[],
  options: Readonly<{
    rememberedStoreCode?: string | null;
    primaryStoreCode?: string | null;
  }>,
): SupplierAssistantStore | null {
  const find = (code: string | null | undefined) => {
    const normalized = code?.trim().toUpperCase();
    if (!normalized) return null;
    return stores.find((store) => store.storeCode.toUpperCase() === normalized) ?? null;
  };
  return (
    find(options.rememberedStoreCode)
    ?? find(options.primaryStoreCode)
    ?? (stores.length === 1 ? stores[0]! : null)
  );
}

/** 账号的主分店：stores 里 isPrimary 的那一家；没有标记时不猜。 */
export function resolvePrimaryStoreCode(
  userStores: readonly Readonly<{ storeCode?: string | null; isPrimary?: boolean }>[] | null | undefined,
): string | null {
  const primary = userStores?.find((store) => store.isPrimary === true && store.storeCode?.trim());
  return primary?.storeCode?.trim() ?? null;
}

const STORAGE_KEY_PREFIX = "supplier_assistant_store:";

export function supplierAssistantStoreStorageKey(userGuid: string | null | undefined): string | null {
  const normalized = userGuid?.trim().toLowerCase();
  return normalized ? `${STORAGE_KEY_PREFIX}${normalized}` : null;
}
