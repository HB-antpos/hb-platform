import assert from "node:assert/strict";
import { buildPriceTaskOverview, EMPTY_PRICE_TASK_OVERVIEW } from "../price-updates/overview";
import type { Store } from "../shop/types";
import type { StorePriceUpdateStoreSummary } from "../price-updates/types";
import { buildPriceReminderViewModel } from "./view-model";

function row(storeCode: string, patch: Partial<StorePriceUpdateStoreSummary> = {}): StorePriceUpdateStoreSummary {
  const price = patch.pendingPriceUpdateCount ?? 0;
  const label = patch.pendingLabelOnlyCount ?? 0;
  return {
    storeCode,
    storeName: `Store ${storeCode}`,
    pendingPriceUpdateCount: price,
    pendingLabelOnlyCount: label,
    pendingCount: price + label,
    overdueCount: 0,
    ...patch,
  };
}

const managerStores: Store[] = [
  { storeCode: "A", storeName: "Store A", isPrimary: true },
  { storeCode: "B", storeName: "Store B", isPrimary: false },
  { storeCode: "C", storeName: "Store C", isPrimary: true },
  { storeCode: "D", storeName: "Store D", isPrimary: true },
];

function manager(rows: StorePriceUpdateStoreSummary[]) {
  return buildPriceTaskOverview({
    summary: { stores: rows, overdueDays: 7 },
    stores: managerStores,
    isDeviceMode: false,
    deviceBoundStore: null,
    isAdmin: false,
  });
}

// ---- 没有可提醒的内容 ----
assert.equal(buildPriceReminderViewModel({ overview: EMPTY_PRICE_TASK_OVERVIEW, currentStoreCode: null, isDeviceMode: false }), null);
assert.equal(
  buildPriceReminderViewModel({ overview: manager([row("A"), row("C")]), currentStoreCode: "A", isDeviceMode: false }),
  null,
  "全部分店都没有未完成任务：不提醒"
);

// ---- 店长多分店 + 管理分店有超期：列表 + 锁定框 + 主按钮去处理超期 ----
const locked = buildPriceReminderViewModel({
  overview: manager([
    row("A", { pendingPriceUpdateCount: 86, pendingLabelOnlyCount: 42, overdueCount: 15 }),
    row("B", { pendingLabelOnlyCount: 20, overdueCount: 9 }), // 只能查看的关联分店
    row("C", { pendingPriceUpdateCount: 30, pendingLabelOnlyCount: 12, overdueCount: 13 }),
    row("D"), // 全部完成
  ]),
  currentStoreCode: "A",
  isDeviceMode: false,
});
assert.ok(locked);
assert.equal(locked.variant, "multi");
assert.equal(locked.totalPending, 170, "合计只算管理分店 A 128 + C 42；B 是只能查看的关联分店，不计入");
assert.equal(locked.storeCount, 3, "「共管理 N 家分店」的 N 只数管理分店（A、C、D）");
assert.deepEqual(locked.lock, { overdueCount: 28 }, "锁定框的超期数只算管理分店（15 + 13），不含 B 的 9");
assert.deepEqual(
  locked.rows.map((item) => item.storeCode),
  ["A", "C", "D"],
  "弹窗只列管理分店；有超期的先（超期多的在前），全部完成的最后；只能查看的 B 不出现"
);
assert.equal(locked.rows[0].isCurrent, true);
assert.equal(locked.rows[2].done, true);
assert.deepEqual(
  locked.primaryAction,
  { kind: "overdue", storeCode: "A", overdueOnly: true },
  "被锁时主按钮直达超期任务，默认选中超期最多的管理分店"
);

// ---- 没有超期：没有锁定框，主按钮变回「去处理」，去第一家有待办的分店 ----
const unlocked = buildPriceReminderViewModel({
  overview: manager([row("A"), row("B", { pendingLabelOnlyCount: 3 }), row("C", { pendingPriceUpdateCount: 9 })]),
  currentStoreCode: "A",
  isDeviceMode: false,
});
assert.ok(unlocked);
assert.equal(unlocked.lock, null);
assert.deepEqual(unlocked.primaryAction, { kind: "default", storeCode: "C", overdueOnly: false });
assert.deepEqual(unlocked.rows.map((item) => item.storeCode), ["C", "A"], "B 是只能查看的分店，不进弹窗");
assert.equal(unlocked.totalPending, 9);

// ---- 只有关联分店超期：不锁，也不标红 ----
const viewOnlyOverdue = buildPriceReminderViewModel({
  overview: manager([row("A", { pendingPriceUpdateCount: 1 }), row("B", { pendingLabelOnlyCount: 2, overdueCount: 8 })]),
  currentStoreCode: "A",
  isDeviceMode: false,
});
assert.ok(viewOnlyOverdue);
assert.equal(viewOnlyOverdue.lock, null);
assert.equal(viewOnlyOverdue.primaryAction.kind, "default");
assert.ok(viewOnlyOverdue.rows.every((item) => item.overdueCount === 0));
assert.deepEqual(viewOnlyOverdue.rows.map((item) => item.storeCode), ["A"], "只能查看的 B 不出现在弹窗里");
assert.equal(viewOnlyOverdue.variant, "single", "只剩一家管理分店时用单店布局，不必列一行的列表");
assert.equal(viewOnlyOverdue.single?.storeCode, "A");

// ---- 账号只有只能查看的关联分店：就算有待办也不提醒 ----
assert.equal(
  buildPriceReminderViewModel({
    overview: buildPriceTaskOverview({
      summary: { stores: [row("B", { pendingLabelOnlyCount: 5, overdueCount: 4 })], overdueDays: 7 },
      stores: [{ storeCode: "B", storeName: "Store B", isPrimary: false }],
      isDeviceMode: false,
      deviceBoundStore: null,
      isAdmin: false,
    }),
    currentStoreCode: "B",
    isDeviceMode: false,
  }),
  null,
  "没有任何管理分店：这些待办不归这位账号负责，不弹提醒"
);

// ---- 单分店账号：用单店布局，标题口径按两类数量决定 ----
function singleAccount(patch: Partial<StorePriceUpdateStoreSummary>) {
  return buildPriceReminderViewModel({
    overview: buildPriceTaskOverview({
      summary: { stores: [row("A", patch)], overdueDays: 7 },
      stores: [{ storeCode: "A", storeName: "Store A", isPrimary: true }],
      isDeviceMode: false,
      deviceBoundStore: null,
      isAdmin: false,
    }),
    currentStoreCode: "A",
    isDeviceMode: false,
  });
}
assert.equal(singleAccount({ pendingPriceUpdateCount: 2, pendingLabelOnlyCount: 3 })?.variant, "single");
assert.equal(singleAccount({ pendingPriceUpdateCount: 2, pendingLabelOnlyCount: 3 })?.single?.headline, "both");
assert.equal(singleAccount({ pendingLabelOnlyCount: 3 })?.single?.headline, "labelOnly");
assert.equal(singleAccount({ pendingPriceUpdateCount: 2 })?.single?.headline, "priceOnly");

// ---- PDA（设备模式）：单店；超期只多一行提示，不锁任何功能 ----
const pda = buildPriceReminderViewModel({
  overview: buildPriceTaskOverview({
    summary: { stores: [row("A", { pendingPriceUpdateCount: 86, pendingLabelOnlyCount: 42, overdueCount: 15 })], overdueDays: 7 },
    stores: [],
    isDeviceMode: true,
    deviceBoundStore: { storeCode: "A", storeName: "Store A" },
    isAdmin: false,
  }),
  currentStoreCode: "A",
  isDeviceMode: true,
});
assert.ok(pda);
assert.equal(pda.variant, "single");
assert.equal(pda.lock, null, "PDA 永远没有锁定框");
assert.equal(pda.single?.overdueCount, 15, "PDA 的超期数用于红色提示行");
assert.equal(pda.single?.priceUpdateCount, 86);
assert.equal(pda.single?.labelOnlyCount, 42);
assert.equal(pda.overdueDays, 7);
assert.deepEqual(pda.primaryAction, { kind: "default", storeCode: "A", overdueOnly: false });

console.log("price-reminder/view-model.test.ts: ok");
