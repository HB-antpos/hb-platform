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

/**
 * 侧栏格子尺寸（pt）：侧栏样式和「能固定几个」的计算共用这一份，改尺寸时两边一起变。
 * accessoryHeight 是收银页「考勤」二维码格的高度（attendance-qr-sales-panels 的 railTile.minHeight）。
 */
export const POS_RAIL_METRICS = Object.freeze({
  accessoryHeight: 96,
  dividerHeight: 1,
  gap: 8,
  paddingVertical: 10,
  tileHeight: 76,
});

/** 侧栏至少能固定几个：矮屏（11 寸、mini）保持原来的 5 个，放不下时侧栏自己可以上下滑。 */
export const MIN_PINNED_DESTINATIONS = 5;

/** 侧栏最多固定几个：再高的屏也不超过这个数，本机保存的固定列表也截到这个数。 */
export const MAX_PINNED_DESTINATIONS = 10;

/**
 * 按侧栏可用高度算出能固定几个入口，结果夹在 [MIN, MAX] 之间。
 *
 * 侧栏从上到下：收银、N 个固定项（同在一个滚动区里）、考勤格、分隔线、更多、锁屏。
 * 一律按「有考勤格」的收银页来算：各页面的侧栏因此完全一样，不会换页就少一个入口。
 * 13 寸 iPad 横屏约 936pt → 6 个；11 寸约 724～738pt → 算出 4，按下限给 5。
 */
export function resolvePinnedCapacity(railHeight: number): number {
  if (!Number.isFinite(railHeight) || railHeight <= 0) {
    return MIN_PINNED_DESTINATIONS;
  }
  const m = POS_RAIL_METRICS;
  // 常驻部分：上下内边距 + 收银 / 更多 / 锁屏三格 + 考勤格 + 分隔线，
  // 再加侧栏五个子块（滚动区、考勤格、分隔线、更多、锁屏）之间的四个间距。
  const fixed =
    m.paddingVertical * 2 +
    m.tileHeight * 3 +
    m.accessoryHeight +
    m.dividerHeight +
    m.gap * 4;
  // 滚动区里每多一个固定项，就多一格加一个间距。
  const perEntry = m.tileHeight + m.gap;
  const fits = Math.floor((railHeight - fixed) / perEntry);
  return Math.min(
    MAX_PINNED_DESTINATIONS,
    Math.max(MIN_PINNED_DESTINATIONS, fits),
  );
}

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
 * 清洗持久化的固定列表：去掉未知和重复的 ID，截到 MAX_PINNED_DESTINATIONS。
 * 不按本机屏幕截短：侧栏只显示前 capacity 个，多出来的仍然留着，不会被丢掉。
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

/**
 * 固定或取消固定一个入口；到上限时不做静默替换。
 * capacity 是这台设备侧栏能放下的数量（resolvePinnedCapacity 的结果）。
 */
export function togglePinnedDestination(
  pinned: readonly PosDestinationId[],
  destinationId: PosDestinationId,
  capacity: number,
): TogglePinnedResult {
  if (pinned.includes(destinationId)) {
    return {
      outcome: "unpinned",
      pinned: pinned.filter((id) => id !== destinationId),
    };
  }
  if (pinned.length >= Math.min(capacity, MAX_PINNED_DESTINATIONS)) {
    return { outcome: "limit", pinned: [...pinned] };
  }
  return { outcome: "pinned", pinned: [...pinned, destinationId] };
}
