/**
 * 左侧栏与「全部功能」面板共用的导航模型（纯数据与纯函数，不依赖 React Native）。
 *
 * 页面入口只在这里登记一次：图标、分组、默认是否固定到侧栏。
 * 是否对当前收银员可见由路由层按权限决定，这里不做权限判断。
 */
export const POS_DESTINATION_IDS = [
  "held-orders",
  "returns",
  "installments",
  "payment-recovery",
  "local-history",
  "remote-history",
  "sync-history",
  "daily-close",
  "attendance-audit",
  "catalog-maintenance",
  "settings",
] as const;

export type PosDestinationId = (typeof POS_DESTINATION_IDS)[number];

/** 侧栏高亮用：收银页本身不是「入口」，但需要能被标成当前页。 */
export type PosActiveDestination = PosDestinationId | "sales" | "none";

export type PosNavGroup = "transaction" | "records" | "store" | "device";

export const POS_NAV_GROUP_ORDER: readonly PosNavGroup[] = [
  "transaction",
  "records",
  "store",
  "device",
];

/** 图标名取自 MaterialCommunityIcons；这里用 string，避免纯数据模块依赖图标包类型。 */
export const POS_DESTINATION_META: Readonly<
  Record<PosDestinationId, Readonly<{ group: PosNavGroup; icon: string }>>
> = Object.freeze({
  "held-orders": { group: "transaction", icon: "clipboard-clock-outline" },
  returns: { group: "transaction", icon: "cash-refund" },
  installments: { group: "transaction", icon: "calendar-month-outline" },
  "payment-recovery": {
    group: "transaction",
    icon: "credit-card-refresh-outline",
  },
  "local-history": { group: "records", icon: "history" },
  "remote-history": { group: "records", icon: "cloud-search-outline" },
  "sync-history": { group: "records", icon: "cloud-upload-outline" },
  "daily-close": { group: "store", icon: "calendar-check-outline" },
  "attendance-audit": { group: "store", icon: "account-clock-outline" },
  "catalog-maintenance": { group: "store", icon: "database-sync-outline" },
  settings: { group: "device", icon: "cog-outline" },
});

/** 侧栏最多固定几个入口：再多就放不下「收银 / 更多 / 锁屏」三个常驻项。 */
export const MAX_PINNED_DESTINATIONS = 5;

export const DEFAULT_PINNED_DESTINATIONS: readonly PosDestinationId[] = [
  "held-orders",
  "returns",
  "installments",
  "local-history",
  "daily-close",
];

const destinationIds = new Set<string>(POS_DESTINATION_IDS);

export function isPosDestinationId(value: unknown): value is PosDestinationId {
  return typeof value === "string" && destinationIds.has(value);
}

/**
 * 清洗持久化的固定列表：去掉未知和重复的 ID，截到上限。
 * 从未保存过（null）时用默认值；保存过空数组表示用户主动清空，原样保留。
 */
export function reconcilePinnedDestinations(
  stored: readonly string[] | null | undefined,
): PosDestinationId[] {
  if (stored == null) return [...DEFAULT_PINNED_DESTINATIONS];
  const seen = new Set<PosDestinationId>();
  for (const candidate of stored) {
    if (!isPosDestinationId(candidate) || seen.has(candidate)) continue;
    seen.add(candidate);
    if (seen.size >= MAX_PINNED_DESTINATIONS) break;
  }
  return [...seen];
}

export type TogglePinnedResult = Readonly<{
  pinned: PosDestinationId[];
  /** limit：已到上限，没有固定成功，需要提示用户先取消一个。 */
  outcome: "pinned" | "unpinned" | "limit";
}>;

/** 固定或取消固定一个入口；到上限时不做静默替换。 */
export function togglePinnedDestination(
  pinned: readonly PosDestinationId[],
  destinationId: PosDestinationId,
): TogglePinnedResult {
  if (pinned.includes(destinationId)) {
    return {
      outcome: "unpinned",
      pinned: pinned.filter((id) => id !== destinationId),
    };
  }
  if (pinned.length >= MAX_PINNED_DESTINATIONS) {
    return { outcome: "limit", pinned: [...pinned] };
  }
  return { outcome: "pinned", pinned: [...pinned, destinationId] };
}
