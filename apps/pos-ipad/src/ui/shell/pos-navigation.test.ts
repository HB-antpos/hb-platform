import assert from "node:assert/strict";
import test from "node:test";

import {
  DEFAULT_PINNED_DESTINATIONS,
  MAX_PINNED_DESTINATIONS,
  MIN_PINNED_DESTINATIONS,
  POS_DESTINATION_IDS,
  POS_DESTINATION_META,
  reconcilePinnedDestinations,
  resolvePinnedCapacity,
  togglePinnedDestination,
} from "./pos-navigation";

test("每个入口都登记了分组与图标，默认固定项都是已知入口", () => {
  for (const id of POS_DESTINATION_IDS) {
    assert.ok(POS_DESTINATION_META[id].icon, id);
    assert.ok(POS_DESTINATION_META[id].group, id);
  }
  assert.ok(DEFAULT_PINNED_DESTINATIONS.length <= MIN_PINNED_DESTINATIONS);
  for (const id of DEFAULT_PINNED_DESTINATIONS) {
    assert.ok(POS_DESTINATION_IDS.includes(id), id);
  }
});

test("从未保存过固定项时用默认值，保存过空列表则保持为空", () => {
  assert.deepEqual(reconcilePinnedDestinations(null), [
    ...DEFAULT_PINNED_DESTINATIONS,
  ]);
  assert.deepEqual(reconcilePinnedDestinations(undefined), [
    ...DEFAULT_PINNED_DESTINATIONS,
  ]);
  assert.deepEqual(reconcilePinnedDestinations([]), []);
});

test("清洗固定项：去掉未知和重复的 ID，并截到上限", () => {
  assert.deepEqual(
    reconcilePinnedDestinations(["settings", "nope", "settings", "returns"]),
    ["settings", "returns"],
  );
  assert.equal(
    reconcilePinnedDestinations([...POS_DESTINATION_IDS]).length,
    MAX_PINNED_DESTINATIONS,
  );
});

test("固定与取消固定；到上限时不替换已有项", () => {
  const pinned = reconcilePinnedDestinations(["returns"]);

  const added = togglePinnedDestination(pinned, "settings", 5);
  assert.equal(added.outcome, "pinned");
  assert.deepEqual(added.pinned, ["returns", "settings"]);

  const removed = togglePinnedDestination(added.pinned, "returns", 5);
  assert.equal(removed.outcome, "unpinned");
  assert.deepEqual(removed.pinned, ["settings"]);

  const full = reconcilePinnedDestinations(null);
  assert.equal(full.length, 5);
  const rejected = togglePinnedDestination(full, "settings", 5);
  assert.equal(rejected.outcome, "limit");
  assert.deepEqual(rejected.pinned, full);

  // 13 寸能放 6 个：默认 5 个之外还能再固定 1 个，第 7 个才提示上限。
  const sixth = togglePinnedDestination(full, "settings", 6);
  assert.equal(sixth.outcome, "pinned");
  assert.equal(sixth.pinned.length, 6);
  assert.equal(
    togglePinnedDestination(sixth.pinned, "sync-history", 6).outcome,
    "limit",
  );
});

test("侧栏能固定几个按可用高度算：13 寸 6 个、11 寸和 mini 保底 5 个、最多 10 个", () => {
  // 可用高度 = 横屏高 − 状态栏 24 − 底部安全区 20 − 顶栏 52。
  assert.equal(resolvePinnedCapacity(1032 - 96), 6); // iPad Pro 13"
  assert.equal(resolvePinnedCapacity(1024 - 96), 6); // iPad Air 13"
  assert.equal(resolvePinnedCapacity(834 - 96), 5); // iPad Pro 11"
  assert.equal(resolvePinnedCapacity(820 - 96), 5); // iPad Air 11" / iPad 10
  assert.equal(resolvePinnedCapacity(744 - 96), 5); // iPad mini
  // 边界：377 常驻 + 每个 84；刚好放下 7 个 / 差 1pt 放不下。
  assert.equal(resolvePinnedCapacity(377 + 84 * 7), 7);
  assert.equal(resolvePinnedCapacity(377 + 84 * 7 - 1), 6);
  assert.equal(resolvePinnedCapacity(5000), MAX_PINNED_DESTINATIONS);
  assert.equal(MAX_PINNED_DESTINATIONS, 10);
  // 尺寸还没拿到或异常时退回下限，不能让侧栏一个都放不下。
  assert.equal(resolvePinnedCapacity(0), MIN_PINNED_DESTINATIONS);
  assert.equal(resolvePinnedCapacity(Number.NaN), MIN_PINNED_DESTINATIONS);
});
