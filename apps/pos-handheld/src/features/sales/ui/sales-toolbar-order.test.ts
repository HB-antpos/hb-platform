import assert from "node:assert/strict";
import test from "node:test";

import {
  DEFAULT_SALES_TOOLBAR_ORDER,
  mergeVisibleSalesToolbarOrder,
  reconcileSalesToolbarOrder,
} from "./sales-toolbar-order";

const LEGACY_DEFAULT_SALES_TOOLBAR_ORDER = [
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
] as const;

test("销售工具栏顺序会去除未知和重复 ID，并补齐默认项", () => {
  const reconciled = reconcileSalesToolbarOrder([
    "lock",
    "unknown-action",
    "hold",
    "lock",
    "returns",
  ]);

  assert.deepEqual(
    reconciled.filter((actionId) =>
      ["lock", "hold", "returns"].includes(actionId),
    ),
    ["lock", "hold", "returns"],
  );
  assert.deepEqual([...new Set(reconciled)], reconciled);
  assert.deepEqual(new Set(reconciled), new Set(DEFAULT_SALES_TOOLBAR_ORDER));
});

test("默认顺序优先展示交易功能，再展示低频终端功能", () => {
  assert.deepEqual(DEFAULT_SALES_TOOLBAR_ORDER, [
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
  ]);
});

test("空的持久化值会恢复完整默认顺序", () => {
  assert.deepEqual(reconcileSalesToolbarOrder(null), [
    ...DEFAULT_SALES_TOOLBAR_ORDER,
  ]);
  assert.deepEqual(reconcileSalesToolbarOrder(undefined), [
    ...DEFAULT_SALES_TOOLBAR_ORDER,
  ]);
});

test("缺失的新操作会补回其默认相邻操作之间", () => {
  assert.deepEqual(
    reconcileSalesToolbarOrder(
      DEFAULT_SALES_TOOLBAR_ORDER.filter(
        (actionId) => actionId !== "cash-drawer",
      ),
    ),
    [...DEFAULT_SALES_TOOLBAR_ORDER],
  );
});

test("旧版默认持久化值会迁移到交易功能优先的新默认顺序", () => {
  assert.deepEqual(
    reconcileSalesToolbarOrder(LEGACY_DEFAULT_SALES_TOOLBAR_ORDER),
    [...DEFAULT_SALES_TOOLBAR_ORDER],
  );
});

test("更早含特殊商品的旧版默认值也会迁移", () => {
  assert.deepEqual(
    reconcileSalesToolbarOrder(
      LEGACY_DEFAULT_SALES_TOOLBAR_ORDER.flatMap((actionId) =>
        actionId === "returns"
          ? [actionId, "special-products"]
          : [actionId],
      ),
    ),
    [...DEFAULT_SALES_TOOLBAR_ORDER],
  );
});

test("旧版真实自定义顺序继续保留已知操作的相对顺序", () => {
  const customized = [
    "hold",
    ...LEGACY_DEFAULT_SALES_TOOLBAR_ORDER.filter(
      (actionId) => actionId !== "hold",
    ),
  ];
  const reconciled = reconcileSalesToolbarOrder(customized);

  assert.deepEqual(
    reconciled.filter((actionId) => customized.includes(actionId)),
    customized,
  );
});

test("重排可见操作时，隐藏操作保留其原来的槽位", () => {
  assert.deepEqual(
    mergeVisibleSalesToolbarOrder(DEFAULT_SALES_TOOLBAR_ORDER, [
      "returns",
      "held-orders",
      "hold",
    ]),
    [
      "returns",
      "merge-cart",
      "held-orders",
      "payment-recovery",
      "local-history",
      "hold",
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
    ],
  );
});

test("可见顺序中的无效或重复值不会挤占有效操作槽位", () => {
  assert.deepEqual(
    mergeVisibleSalesToolbarOrder(DEFAULT_SALES_TOOLBAR_ORDER, [
      "hold",
      "not-an-action",
      "hold",
      "held-orders",
    ]),
    [...DEFAULT_SALES_TOOLBAR_ORDER],
  );
});
