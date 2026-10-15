import assert from "node:assert/strict";
import {
  DEFAULT_OVERDUE_DAYS,
  EMPTY_PRICE_TASK_OVERVIEW,
  buildPriceTaskOverview,
  isStoreOverdueFlagged,
  sortStoreSummaries,
} from "./overview";
import type { Store } from "../shop/types";
import type { StorePriceUpdateStoreSummary, StorePriceUpdateSummary } from "./types";

function row(
  storeCode: string,
  patch: Partial<StorePriceUpdateStoreSummary> = {}
): StorePriceUpdateStoreSummary {
  const pendingPriceUpdateCount = patch.pendingPriceUpdateCount ?? 0;
  const pendingLabelOnlyCount = patch.pendingLabelOnlyCount ?? 0;
  return {
    storeCode,
    storeName: `Store ${storeCode}`,
    pendingPriceUpdateCount,
    pendingLabelOnlyCount,
    pendingCount: pendingPriceUpdateCount + pendingLabelOnlyCount,
    overdueCount: 0,
    ...patch,
  };
}

function summary(rows: StorePriceUpdateStoreSummary[], overdueDays = 7): StorePriceUpdateSummary {
  return { stores: rows, overdueDays };
}

// 店长：A、C 是管理分店（isPrimary），B 只是关联、只能查看
const stores: Store[] = [
  { storeCode: "A", storeName: "Store A", isPrimary: true },
  { storeCode: "B", storeName: "Store B", isPrimary: false },
  { storeCode: "C", storeName: "Store C", isPrimary: true },
];
const base = { stores, isDeviceMode: false, deviceBoundStore: null, isAdmin: false } as const;

// ---- 汇总没拿到：未就绪，不提醒、不锁定 ----
assert.equal(buildPriceTaskOverview({ ...base, summary: null }), EMPTY_PRICE_TASK_OVERVIEW);
assert.equal(EMPTY_PRICE_TASK_OVERVIEW.ready, false);
assert.equal(EMPTY_PRICE_TASK_OVERVIEW.reportsLocked, false, "接口故障时不能把人挡在报表外");

// ---- 锁定只看「管理分店」的超期，关联但只能查看的分店不算 ----
const mixed = buildPriceTaskOverview({
  ...base,
  summary: summary([
    row("A", { pendingPriceUpdateCount: 3, overdueCount: 0 }),
    row("B", { pendingLabelOnlyCount: 4, overdueCount: 5 }),
    row("C", { pendingPriceUpdateCount: 2, overdueCount: 2 }),
  ]),
});
assert.equal(mixed.ready, true);
assert.deepEqual(mixed.manageableStoreCodes, ["A", "C"], "非管理员 = isPrimary 的分店");
assert.equal(mixed.manageableOverdueCount, 2, "B 店的 5 项超期不计入：它不是这位店长管理的分店");
assert.equal(mixed.reportsLocked, true);
assert.equal(mixed.firstOverdueStoreCode, "C");
assert.deepEqual(mixed.rows.map((item) => item.storeCode), ["A", "B", "C"], "rows 仍是名下全部可见分店：价格页分店条要看到 B 的数量");
assert.deepEqual(mixed.managedRows.map((item) => item.storeCode), ["A", "C"], "managedRows 只含管理分店，弹窗只列它们");
assert.equal(mixed.totalPending, 5, "角标 / 提醒合计只算管理分店（A 3 + C 2）；只能查看的 B 的 4 项不算");

const onlyViewOnlyOverdue = buildPriceTaskOverview({
  ...base,
  summary: summary([row("A", { pendingPriceUpdateCount: 1 }), row("B", { pendingLabelOnlyCount: 1, overdueCount: 9 })]),
});
assert.equal(onlyViewOnlyOverdue.manageableOverdueCount, 0);
assert.equal(onlyViewOnlyOverdue.reportsLocked, false, "只有关联分店超期：不锁报表");
assert.equal(onlyViewOnlyOverdue.firstOverdueStoreCode, null);

// 管理分店里超期最多的排第一；同数量按分店代码
const multiOverdue = buildPriceTaskOverview({
  ...base,
  summary: summary([row("A", { overdueCount: 3, pendingLabelOnlyCount: 3 }), row("C", { overdueCount: 3, pendingLabelOnlyCount: 3 })]),
});
assert.equal(multiOverdue.firstOverdueStoreCode, "A");
assert.equal(multiOverdue.manageableOverdueCount, 6);

// ---- 只有只读关联分店：没有任何管理分店 → 不提醒、不计角标，但分店条的数据还在 ----
const viewOnly = buildPriceTaskOverview({
  ...base,
  stores: [{ storeCode: "B", storeName: "Store B", isPrimary: false }],
  summary: summary([row("B", { pendingLabelOnlyCount: 6, overdueCount: 3 })]),
});
assert.equal(viewOnly.ready, true);
assert.deepEqual(viewOnly.managedRows, []);
assert.equal(viewOnly.totalPending, 0, "全是只能查看的分店：没有要这位账号负责的待办");
assert.equal(viewOnly.reportsLocked, false);
assert.equal(viewOnly.rows.length, 1, "价格页的分店条仍能看到这家分店的数量");

// ---- 管理员：管理分店 = 他可见的全部分店，弹窗 / 角标覆盖它们，但不锁报表 ----
const admin = buildPriceTaskOverview({
  ...base,
  isAdmin: true,
  summary: summary([row("B", { pendingLabelOnlyCount: 2, overdueCount: 2 })]),
});
assert.deepEqual(admin.manageableStoreCodes, ["A", "B", "C"]);
assert.deepEqual(admin.managedRows.map((item) => item.storeCode), ["B"], "管理员的管理分店 = 可见全部，汇总里有哪家就列哪家");
assert.equal(admin.manageableOverdueCount, 2, "超期数照常统计（用于红色超期标签），但不用来锁");
assert.equal(admin.reportsLocked, false, "管理员弹提醒但不锁报表（用户 10-16 决定）");

// ---- 仓库管理员：关联分店全算管理分店（弹窗、角标要覆盖它们），但不锁报表 ----
// 他们关联的多是仓库（isPrimary=false），按店长口径会一家管理分店都没有、永远收不到提醒。
const warehouseManager = buildPriceTaskOverview({
  ...base,
  isWarehouseManager: true,
  summary: summary([
    row("A", { pendingPriceUpdateCount: 3 }),
    row("B", { pendingLabelOnlyCount: 4, overdueCount: 5 }), // isPrimary=false 的关联分店
    row("C", { pendingPriceUpdateCount: 2, overdueCount: 2 }),
  ]),
});
assert.deepEqual(
  warehouseManager.manageableStoreCodes,
  ["A", "B", "C"],
  "仓库管理员：关联分店全算管理分店（含 isPrimary=false 的 B）"
);
assert.equal(warehouseManager.managedRows.length, 3);
assert.equal(warehouseManager.totalPending, 9, "角标 / 提醒按全部关联分店合计");
assert.equal(warehouseManager.manageableOverdueCount, 7, "超期数照常统计（用于红色超期标签），但不用来锁");
assert.equal(warehouseManager.reportsLocked, false, "仓库管理员弹提醒，但不锁报表");

// 只关联了仓库（isPrimary=false）：店长口径没有管理分店，仓库管理员口径有
const warehouseOnly: Store[] = [{ storeCode: "W", storeName: "HB WARE HOUSE", isPrimary: false }];
const warehouseSummary = summary([row("W", { pendingLabelOnlyCount: 3, overdueCount: 3 })]);
const warehouseOnlyAsWarehouseManager = buildPriceTaskOverview({
  stores: warehouseOnly,
  isDeviceMode: false,
  deviceBoundStore: null,
  isAdmin: false,
  isWarehouseManager: true,
  summary: warehouseSummary,
});
assert.equal(warehouseOnlyAsWarehouseManager.totalPending, 3, "只关联仓库的仓库管理员也要收到提醒");
assert.equal(warehouseOnlyAsWarehouseManager.reportsLocked, false);
const warehouseOnlyAsStoreManager = buildPriceTaskOverview({
  stores: warehouseOnly,
  isDeviceMode: false,
  deviceBoundStore: null,
  isAdmin: false,
  isWarehouseManager: false,
  summary: warehouseSummary,
});
assert.equal(warehouseOnlyAsStoreManager.totalPending, 0, "对照：不是仓库管理员时没有管理分店 → 不提醒");

// 既是管理员又是仓库管理员：弹提醒（管理分店口径），同样不锁
const adminAndWarehouseManager = buildPriceTaskOverview({
  ...base,
  isAdmin: true,
  isWarehouseManager: true,
  summary: summary([row("B", { pendingLabelOnlyCount: 2, overdueCount: 2 })]),
});
assert.equal(adminAndWarehouseManager.totalPending, 2, "既是管理员又是仓库管理员：照常收到提醒");
assert.equal(adminAndWarehouseManager.reportsLocked, false, "既是管理员又是仓库管理员：弹提醒但不锁报表");

// ---- 设备（PDA）：没有报表入口，永不锁定；管理分店 = 绑定分店 ----
const bound: Store = { storeCode: "A", storeName: "Store A" };
const device = buildPriceTaskOverview({
  stores: [],
  isDeviceMode: true,
  deviceBoundStore: bound,
  isAdmin: false,
  summary: summary([row("A", { pendingPriceUpdateCount: 5, overdueCount: 3 })]),
});
assert.deepEqual(device.manageableStoreCodes, ["A"]);
assert.equal(device.manageableOverdueCount, 3, "PDA 的超期要统计出来（弹窗里显示红色提示行）");
assert.equal(device.totalPending, 5, "PDA 绑定分店就是它的管理分店，待办照常计入");
assert.deepEqual(device.managedRows.map((item) => item.storeCode), ["A"]);
assert.equal(device.reportsLocked, false, "设备永不锁定任何功能");

// ---- 分店列表还没加载：没有管理分店，不会误锁 ----
const noStores = buildPriceTaskOverview({
  ...base,
  stores: [],
  summary: summary([row("A", { pendingPriceUpdateCount: 1, overdueCount: 4 })]),
});
assert.equal(noStores.reportsLocked, false);
assert.equal(noStores.rows.length, 0, "分店列表还没加载：先不显示任何行（也就不提醒），加载后会重新计算");
assert.equal(noStores.totalPending, 0);

// ---- 后端对店长/仓库角色不限分店，汇总会带上全部启用分店：只算账号名下可见的分店 ----
const withStrangers = buildPriceTaskOverview({
  ...base,
  summary: summary([
    row("A", { pendingPriceUpdateCount: 2 }),
    row("Z1", { pendingPriceUpdateCount: 50, overdueCount: 20 }), // 不是这位店长的分店
    row("Z2", { pendingLabelOnlyCount: 30 }),
  ]),
});
assert.deepEqual(withStrangers.rows.map((item) => item.storeCode), ["A"], "只保留名下可见分店");
assert.equal(withStrangers.totalPending, 2, "角标不能把别家店的待办算进来");
assert.equal(withStrangers.manageableOverdueCount, 0, "别家店的超期更不能锁这位店长的报表");
assert.equal(withStrangers.reportsLocked, false);

// ---- 超期时限：后端没给就回退默认 7 天 ----
assert.equal(DEFAULT_OVERDUE_DAYS, 7);
assert.equal(buildPriceTaskOverview({ ...base, summary: summary([], 0) }).overdueDays, 7);
assert.equal(buildPriceTaskOverview({ ...base, summary: summary([], 5) }).overdueDays, 5);

// ---- 标红与排序 ----
const manageable = ["A", "C"];
assert.equal(isStoreOverdueFlagged(row("A", { overdueCount: 1 }), manageable), true);
assert.equal(isStoreOverdueFlagged(row("B", { overdueCount: 1 }), manageable), false, "只能查看的分店不标红");
assert.equal(isStoreOverdueFlagged(row("A", { overdueCount: 0 }), manageable), false);

const sorted = sortStoreSummaries(
  [
    row("D", { pendingLabelOnlyCount: 0 }), // 全部完成 → 最后
    row("B", { pendingLabelOnlyCount: 50, overdueCount: 9 }), // 非管理分店的超期不提前
    row("A", { pendingPriceUpdateCount: 4, overdueCount: 1 }),
    row("C", { pendingPriceUpdateCount: 6, overdueCount: 3 }),
    row("E", { pendingPriceUpdateCount: 7 }),
  ],
  manageable
);
assert.deepEqual(
  sorted.map((item) => item.storeCode),
  ["C", "A", "B", "E", "D"],
  "管理分店有超期的先（超期多的在前），其次按未完成数从多到少，全部完成的最后"
);

console.log("price-updates/overview.test.ts: ok");
