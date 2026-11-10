import assert from "node:assert/strict";
import test from "node:test";

import {
  DEFAULT_PINNED_DESTINATIONS,
  MAX_PINNED_DESTINATIONS,
  POS_DESTINATION_IDS,
  POS_DESTINATION_META,
  reconcilePinnedDestinations,
  togglePinnedDestination,
} from "./pos-navigation";

test("每个入口都登记了分组与图标，默认固定项都是已知入口", () => {
  for (const id of POS_DESTINATION_IDS) {
    assert.ok(POS_DESTINATION_META[id].icon, id);
    assert.ok(POS_DESTINATION_META[id].group, id);
  }
  assert.ok(DEFAULT_PINNED_DESTINATIONS.length <= MAX_PINNED_DESTINATIONS);
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

  const added = togglePinnedDestination(pinned, "settings");
  assert.equal(added.outcome, "pinned");
  assert.deepEqual(added.pinned, ["returns", "settings"]);

  const removed = togglePinnedDestination(added.pinned, "returns");
  assert.equal(removed.outcome, "unpinned");
  assert.deepEqual(removed.pinned, ["settings"]);

  const full = reconcilePinnedDestinations(null);
  assert.equal(full.length, MAX_PINNED_DESTINATIONS);
  const rejected = togglePinnedDestination(full, "settings");
  assert.equal(rejected.outcome, "limit");
  assert.deepEqual(rejected.pinned, full);
});
