import assert from "node:assert/strict";
import {
  PRICE_REMINDER_INTERVAL_MS,
  buildPriceReminderStorageKey,
  parseLastShownAt,
  shouldShowPriceReminder,
  type PriceReminderGateInput,
} from "./schedule";

const NOW = Date.UTC(2026, 9, 16, 9, 0, 0);
const MINUTE = 60_000;

function gate(patch: Partial<PriceReminderGateInput> = {}): PriceReminderGateInput {
  return {
    nowMs: NOW,
    lastShownAtMs: null,
    overviewReady: true,
    totalPending: 12,
    appActive: true,
    onPriceUpdatesScreen: false,
    suppressed: false,
    isAdmin: false,
    busy: false,
    ...patch,
  };
}

assert.equal(PRICE_REMINDER_INTERVAL_MS, 60 * MINUTE, "每小时一次");

// ---- 存储键：店长按账号，PDA 按「设备 + 分店」 ----
assert.equal(buildPriceReminderStorageKey({ kind: "account", userGuid: "u-1" }), "priceReminder:lastShown:account:u-1");
assert.equal(
  buildPriceReminderStorageKey({ kind: "device", hardwareId: "hw-9", storeCode: "1004" }),
  "priceReminder:lastShown:device:hw-9:1004"
);
assert.notEqual(
  buildPriceReminderStorageKey({ kind: "device", hardwareId: "hw-9", storeCode: "1004" }),
  buildPriceReminderStorageKey({ kind: "device", hardwareId: "hw-9", storeCode: "1005" }),
  "同一台 PDA 换绑分店后是另一份待办，要重新提醒"
);

// ---- 读出来的值不可信 ----
assert.equal(parseLastShownAt(null), null);
assert.equal(parseLastShownAt(undefined), null);
assert.equal(parseLastShownAt(""), null);
assert.equal(parseLastShownAt("abc"), null, "被写坏的值当作从没弹过");
assert.equal(parseLastShownAt("-5"), null);
assert.equal(parseLastShownAt("0"), null);
assert.equal(parseLastShownAt(String(NOW)), NOW);

// ---- 每小时节奏 ----
assert.equal(shouldShowPriceReminder(gate()), true, "从没弹过：立刻弹");
assert.equal(shouldShowPriceReminder(gate({ lastShownAtMs: NOW - 59 * MINUTE })), false, "不满 1 小时不弹");
assert.equal(shouldShowPriceReminder(gate({ lastShownAtMs: NOW - 60 * MINUTE })), true, "刚好满 1 小时就弹");
assert.equal(shouldShowPriceReminder(gate({ lastShownAtMs: NOW - 3 * 60 * MINUTE })), true);
assert.equal(shouldShowPriceReminder(gate({ lastShownAtMs: NOW - 1 })), false, "刚弹过不会连弹");

// ---- 本机时钟被往回拨：上次弹出时间跑到「未来」 ----
assert.equal(shouldShowPriceReminder(gate({ lastShownAtMs: NOW + 2 * MINUTE })), false, "几分钟以内的偏差属于正常抖动，不算已到点");
assert.equal(
  shouldShowPriceReminder(gate({ lastShownAtMs: NOW + 3 * 60 * MINUTE })),
  true,
  "时钟被拨回导致上次弹出在很远的未来：当作已到点，不能让提醒一小时内永远不再出现"
);

// ---- 不该弹的场合 ----
assert.equal(shouldShowPriceReminder(gate({ overviewReady: false })), false, "汇总没拿到不弹（不拿旧数据或空数据弹窗）");
assert.equal(shouldShowPriceReminder(gate({ totalPending: 0 })), false, "全部处理完就不再提醒");
assert.equal(shouldShowPriceReminder(gate({ appActive: false })), false, "App 在后台不弹");
assert.equal(shouldShowPriceReminder(gate({ onPriceUpdatesScreen: true })), false, "人已经停在价格和标签页，不再打扰");
assert.equal(shouldShowPriceReminder(gate({ suppressed: true })), false, "iOS 审核模式等不弹");
assert.equal(
  shouldShowPriceReminder(gate({ isAdmin: true })),
  false,
  "管理员完全不弹（即使有待办、从没弹过）"
);
assert.equal(
  shouldShowPriceReminder(gate({ isAdmin: true, lastShownAtMs: NOW - 5 * 60 * MINUTE })),
  false,
  "管理员即使距上次已过很久也不弹"
);
assert.equal(shouldShowPriceReminder(gate({ busy: true })), false, "正在打印不能被弹窗打断");

console.log("price-reminder/schedule.test.ts: ok");
