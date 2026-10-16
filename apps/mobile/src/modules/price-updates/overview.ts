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
  /**
   * 账号名下全部可见分店（含只能查看的关联分店）的汇总行。「价格和标签」页顶部的分店条用它：
   * 在页面里切到只能查看的分店，数量仍然要看得到。
   */
  rows: StorePriceUpdateStoreSummary[];
  /**
   * 其中「管理分店」的汇总行。每小时提醒弹窗只列这些分店：只能查看的关联分店不是这位店长的责任，
   * 不该打扰他（用户 10-16 明确：弹窗也只显示管理分店）。
   */
  managedRows: StorePriceUpdateStoreSummary[];
  overdueDays: number;
  /** 管理分店的未完成合计：「要不要弹提醒」看它，与弹窗里显示的数字一致。 */
  totalPending: number;
  /**
   * 名下全部可见分店（含只关联的）的未完成合计：工作台「价格和标签」入口的角标看它（用户 10-16 决定）。
   * 角标只是被动提示、点进去能看到这些分店的任务，所以不像弹窗那样只算管理分店；
   * 否则只关联分店的账号（仓库管理员等）会整个看不到角标。
   */
  visiblePending: number;
  /** 本人「管理分店」里已超期的任务合计（锁报表只看它，不含只能查看的关联分店）。 */
  manageableOverdueCount: number;
  manageableStoreCodes: string[];
  /**
   * 只有店长（既不是管理员、也不是仓库管理员）：管理分店有超期任务 → 不能进入报表。
   * 管理员、仓库管理员（含两者兼任）会弹提醒、会标红超期，但**不锁**；
   * 设备没有报表入口，永不锁定（用户 10-16 决定）。
   */
  reportsLocked: boolean;
  /** 管理分店里超期最多的一家，被锁后跳转到价格和标签时默认选中它。 */
  firstOverdueStoreCode: string | null;
}

export const EMPTY_PRICE_TASK_OVERVIEW: PriceTaskOverview = {
  ready: false,
  rows: [],
  managedRows: [],
  overdueDays: DEFAULT_OVERDUE_DAYS,
  totalPending: 0,
  visiblePending: 0,
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
  /**
   * 仓库管理员（WarehouseManager / 仓库经理 / 仓库管理员 …）。用户 10-16 决定：他们的「管理分店」
   * 与店长同口径（只算 isPrimary 的分店），只关联、不管理的分店（含仓库）不弹也不计角标；
   * 同时和管理员一样**不锁报表**。所以这个标志只用来免锁，不会放宽管理分店范围。
   */
  isWarehouseManager?: boolean;
  /**
   * 订货员（只持有 Order 角色）。他们的分店几乎全是「只关联」（isPrimary=false），按店长口径一家管理分店都没有，
   * 既不弹窗也没有角标。用户 10-16 决定订货员也要弹窗提醒：名下全部分店都算他们的管理分店；
   * 同样**不锁报表**（只有店长锁）。
   */
  isOrderUser?: boolean;
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
  // 关联但只能查看的分店不算：它们不进弹窗，也不锁这位账号的报表（工作台角标例外，见 visiblePending）。
  // 仓库管理员不在这里放宽：他们和店长一样只算 isPrimary 的分店（用户 10-16 决定，不要弹只关联的店）。
  // 订货员相反：他们的分店几乎都是只关联，用户 10-16 决定也要弹窗，所以按「名下全部分店」算管理分店。
  const manageableStoreCodes = getManageableStoresForSession({
    stores: [...input.stores],
    isDeviceMode: input.isDeviceMode,
    deviceBoundStore: input.deviceBoundStore,
    isAdmin: input.isAdmin || Boolean(input.isOrderUser),
  }).map((store) => store.storeCode);
  // 弹窗范围和锁定范围是两件事：管理员、仓库管理员、订货员都按各自的管理分店被提醒，但都不被锁报表；
  // 只有店长口径（不是管理员、仓库管理员、订货员）才锁。设备由下面 reportsLocked 里的 isDeviceMode 排除。
  const reportsLockExempt = input.isAdmin || Boolean(input.isWarehouseManager) || Boolean(input.isOrderUser);
  const manageable = new Set(manageableStoreCodes);

  const manageableRows = rows.filter((row) => manageable.has(row.storeCode));
  const manageableOverdueCount = manageableRows.reduce((sum, row) => sum + row.overdueCount, 0);
  const firstOverdue = manageableRows
    .filter((row) => row.overdueCount > 0)
    .sort((a, b) => b.overdueCount - a.overdueCount || a.storeCode.localeCompare(b.storeCode))[0];

  return {
    ready: true,
    rows,
    managedRows: manageableRows,
    overdueDays: input.summary.overdueDays > 0 ? input.summary.overdueDays : DEFAULT_OVERDUE_DAYS,
    totalPending: manageableRows.reduce((sum, row) => sum + row.pendingCount, 0),
    visiblePending: rows.reduce((sum, row) => sum + row.pendingCount, 0),
    manageableOverdueCount,
    manageableStoreCodes,
    reportsLocked: !input.isDeviceMode && !reportsLockExempt && manageableOverdueCount > 0,
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
