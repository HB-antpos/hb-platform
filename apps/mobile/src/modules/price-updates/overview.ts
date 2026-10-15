import { getManageableStoresForSession } from "@/modules/shop/store-scope";
import type { Store } from "@/modules/shop/types";
import type { StorePriceUpdateStoreSummary, StorePriceUpdateSummary } from "./types";

/** 后端没返回时限时的兜底，与后端默认值（StorePriceUpdateTasks:OverdueDays）一致。 */
export const DEFAULT_OVERDUE_DAYS = 7;

/**
 * 价格和标签任务的「概览」：布局层用汇总接口算一次，工作台角标、每小时提醒、分店切换条、
 * 报表锁定都读它，保证各处数字与锁定判断一致。
 */
export interface PriceTaskOverview {
  /** 汇总已拿到。没拿到（加载中 / 接口失败）时一律不提醒、不锁定：接口故障不能把人挡在报表外。 */
  ready: boolean;
  /** 账号 / 设备可访问的全部分店（后端口径）。 */
  rows: StorePriceUpdateStoreSummary[];
  overdueDays: number;
  /** 全部可访问分店的未完成合计，用于工作台角标。 */
  totalPending: number;
  /** 本人「管理分店」里已超期的任务合计（锁报表只看它，不含只能查看的关联分店）。 */
  manageableOverdueCount: number;
  manageableStoreCodes: string[];
  /** 店长账号：管理分店有超期任务 → 不能进入报表。设备没有报表入口，永不锁定。 */
  reportsLocked: boolean;
  /** 管理分店里超期最多的一家，被锁后跳转到价格和标签时默认选中它。 */
  firstOverdueStoreCode: string | null;
}

export const EMPTY_PRICE_TASK_OVERVIEW: PriceTaskOverview = {
  ready: false,
  rows: [],
  overdueDays: DEFAULT_OVERDUE_DAYS,
  totalPending: 0,
  manageableOverdueCount: 0,
  manageableStoreCodes: [],
  reportsLocked: false,
  firstOverdueStoreCode: null,
};

export interface PriceTaskOverviewInput {
  summary: StorePriceUpdateSummary | null | undefined;
  /** 账号当前可见的分店（带 isPrimary 标记）；设备模式下无意义。 */
  stores: readonly Store[];
  isDeviceMode: boolean;
  deviceBoundStore: Store | null;
  isAdmin: boolean;
}

export function buildPriceTaskOverview(input: PriceTaskOverviewInput): PriceTaskOverview {
  if (!input.summary) {
    return EMPTY_PRICE_TASK_OVERVIEW;
  }

  // 后端对店长（Manager）、仓库角色和超管不限分店范围，汇总会返回全部启用分店；
  // 但账号在 App 里能看、能切换的只有自己的分店。角标、提醒弹窗和分店条只算名下分店，
  // 否则店长会看到「自己点不开」的别家店。设备由后端按绑定分店返回，不需要再过滤。
  const visibleStoreCodes = new Set(input.stores.map((store) => store.storeCode));
  const rows = input.isDeviceMode
    ? input.summary.stores
    : input.summary.stores.filter((row) => visibleStoreCodes.has(row.storeCode));
  // 「管理分店」与商品查询、用户管理等页面同一口径：设备 = 绑定分店；管理员 = 全部；其余 = isPrimary 的分店。
  // 关联但只能查看的分店不算：它们有超期不应该锁住这位店长的报表。
  const manageableStoreCodes = getManageableStoresForSession({
    stores: [...input.stores],
    isDeviceMode: input.isDeviceMode,
    deviceBoundStore: input.deviceBoundStore,
    isAdmin: input.isAdmin,
  }).map((store) => store.storeCode);
  const manageable = new Set(manageableStoreCodes);

  const manageableRows = rows.filter((row) => manageable.has(row.storeCode));
  const manageableOverdueCount = manageableRows.reduce((sum, row) => sum + row.overdueCount, 0);
  const firstOverdue = manageableRows
    .filter((row) => row.overdueCount > 0)
    .sort((a, b) => b.overdueCount - a.overdueCount || a.storeCode.localeCompare(b.storeCode))[0];

  return {
    ready: true,
    rows,
    overdueDays: input.summary.overdueDays > 0 ? input.summary.overdueDays : DEFAULT_OVERDUE_DAYS,
    totalPending: rows.reduce((sum, row) => sum + row.pendingCount, 0),
    manageableOverdueCount,
    manageableStoreCodes,
    reportsLocked: !input.isDeviceMode && manageableOverdueCount > 0,
    firstOverdueStoreCode: firstOverdue?.storeCode ?? null,
  };
}

/** 该分店是否要在界面上标红（超期标签 / 红点）：只有管理分店的超期才与「锁报表」挂钩，才值得催办。 */
export function isStoreOverdueFlagged(
  row: Pick<StorePriceUpdateStoreSummary, "storeCode" | "overdueCount">,
  manageableStoreCodes: readonly string[]
): boolean {
  return row.overdueCount > 0 && manageableStoreCodes.includes(row.storeCode);
}

/**
 * 弹窗与分店条的排序：先放管理分店里有超期的（超期多的在前），再按未完成数从多到少，
 * 全部完成的排最后；同级按分店代码，保证每次渲染顺序稳定。
 */
export function sortStoreSummaries(
  rows: readonly StorePriceUpdateStoreSummary[],
  manageableStoreCodes: readonly string[]
): StorePriceUpdateStoreSummary[] {
  const rank = (row: StorePriceUpdateStoreSummary) =>
    isStoreOverdueFlagged(row, manageableStoreCodes) ? 0 : row.pendingCount > 0 ? 1 : 2;
  return [...rows].sort((a, b) => {
    const rankDiff = rank(a) - rank(b);
    if (rankDiff !== 0) return rankDiff;
    if (rank(a) === 0 && a.overdueCount !== b.overdueCount) return b.overdueCount - a.overdueCount;
    if (a.pendingCount !== b.pendingCount) return b.pendingCount - a.pendingCount;
    return a.storeCode.localeCompare(b.storeCode);
  });
}
