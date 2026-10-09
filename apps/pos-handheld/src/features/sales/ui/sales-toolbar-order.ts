export type SalesToolbarActionId =
  | "hold"
  | "merge-cart"
  | "returns"
  | "payment-recovery"
  | "local-history"
  | "held-orders"
  | "reprint-receipt"
  | "cash-drawer"
  | "daily-close"
  | "remote-history"
  | "installments"
  | "settings"
  | "attendance-audit"
  | "sync-history"
  | "catalog-maintenance"
  | "language"
  | "lock";

export const DEFAULT_SALES_TOOLBAR_ORDER = [
  "hold",
  "merge-cart",
  "returns",
  "payment-recovery",
  "local-history",
  "held-orders",
  "reprint-receipt",
  "cash-drawer",
  "daily-close",
  "remote-history",
  "installments",
  "sync-history",
  "catalog-maintenance",
  "attendance-audit",
  "settings",
  "language",
  "lock",
] as const satisfies readonly SalesToolbarActionId[];

const LEGACY_DEFAULT_SALES_TOOLBAR_ORDERS: readonly (readonly string[])[] = [
  [
    "held-orders",
    "daily-close",
    "returns",
    "remote-history",
    "installments",
    "sync-history",
    "catalog-maintenance",
    "attendance-audit",
    "settings",
    "hold",
    "language",
    "lock",
  ],
  [
    "held-orders",
    "daily-close",
    "returns",
    "special-products",
    "remote-history",
    "installments",
    "sync-history",
    "catalog-maintenance",
    "attendance-audit",
    "settings",
    "hold",
    "language",
    "lock",
  ],
];

const salesToolbarActionIds = new Set<string>(DEFAULT_SALES_TOOLBAR_ORDER);

/**
 * 清洗持久化顺序：保留已知项目的用户顺序，新增项目插回默认相邻项目之间。
 */
export function reconcileSalesToolbarOrder(
  stored: readonly string[] | null | undefined,
): SalesToolbarActionId[] {
  // 仅迁移旧版原始默认值；任何真实用户重排仍按原相对顺序保留。
  if (
    stored &&
    LEGACY_DEFAULT_SALES_TOOLBAR_ORDERS.some((legacyOrder) =>
      hasSameStringOrder(stored, legacyOrder),
    )
  ) {
    return [...DEFAULT_SALES_TOOLBAR_ORDER];
  }

  const seen = new Set<SalesToolbarActionId>();
  const reconciled: SalesToolbarActionId[] = [];

  for (const candidate of stored ?? []) {
    if (!isSalesToolbarActionId(candidate) || seen.has(candidate)) continue;
    seen.add(candidate);
    reconciled.push(candidate);
  }

  for (const actionId of DEFAULT_SALES_TOOLBAR_ORDER) {
    if (seen.has(actionId)) continue;
    seen.add(actionId);
    const defaultIndex = DEFAULT_SALES_TOOLBAR_ORDER.indexOf(actionId);
    const precedingIndex = findReconciliationAnchor(
      reconciled,
      defaultIndex,
      -1,
    );
    if (precedingIndex >= 0) {
      reconciled.splice(precedingIndex + 1, 0, actionId);
      continue;
    }

    const followingIndex = findReconciliationAnchor(
      reconciled,
      defaultIndex,
      1,
    );
    if (followingIndex >= 0) {
      reconciled.splice(followingIndex, 0, actionId);
      continue;
    }
    reconciled.push(actionId);
  }

  return reconciled;
}

/**
 * 仅改写当前可见操作原本占用的槽位，让暂时隐藏的操作保持原位。
 */
export function mergeVisibleSalesToolbarOrder(
  canonicalOrder: readonly string[],
  reorderedVisibleIds: readonly string[],
): SalesToolbarActionId[] {
  const canonical = reconcileSalesToolbarOrder(canonicalOrder);
  const visibleSet = new Set<SalesToolbarActionId>();
  const reordered: SalesToolbarActionId[] = [];

  for (const candidate of reorderedVisibleIds) {
    if (
      !isSalesToolbarActionId(candidate) ||
      visibleSet.has(candidate) ||
      !canonical.includes(candidate)
    ) {
      continue;
    }
    visibleSet.add(candidate);
    reordered.push(candidate);
  }

  let visibleIndex = 0;
  return canonical.map((actionId) => {
    if (!visibleSet.has(actionId)) return actionId;
    const reorderedActionId = reordered[visibleIndex];
    visibleIndex += 1;
    return reorderedActionId ?? actionId;
  });
}

export function isSalesToolbarActionId(
  value: string,
): value is SalesToolbarActionId {
  return salesToolbarActionIds.has(value);
}

function findReconciliationAnchor(
  currentOrder: readonly SalesToolbarActionId[],
  defaultIndex: number,
  direction: -1 | 1,
): number {
  for (
    let index = defaultIndex + direction;
    index >= 0 && index < DEFAULT_SALES_TOOLBAR_ORDER.length;
    index += direction
  ) {
    const anchor = DEFAULT_SALES_TOOLBAR_ORDER[index];
    if (!anchor) continue;
    const currentIndex = currentOrder.indexOf(anchor);
    if (currentIndex >= 0) return currentIndex;
  }
  return -1;
}

function hasSameStringOrder(
  first: readonly string[],
  second: readonly string[],
): boolean {
  return (
    first.length === second.length &&
    first.every((value, index) => value === second[index])
  );
}
