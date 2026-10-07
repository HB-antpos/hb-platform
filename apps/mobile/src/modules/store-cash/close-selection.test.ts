import assert from "node:assert/strict";
import {
  buildDefaultSelectionRequest,
  buildManualSelectionRequest,
  getIncludedCloseIds,
  hasOverlappingPeriods,
  hasSelectableArchives,
  isSameSelection,
  pruneSelection,
  resolveDeviceNotices,
  sumSelectedCountedCash,
  toggleCloseId,
  validateManualSelection,
} from "./close-selection";
import type { CashCloseArchive, CashDailyDevice } from "./types";

const archive = (closeId: string, patch: Partial<CashCloseArchive> = {}): CashCloseArchive => ({
  closeId,
  savedAtUtc: "2026-10-06T12:00:00Z",
  periodFromUtc: "2026-10-06T00:00:00Z",
  periodToUtc: "2026-10-06T12:00:00Z",
  countedCash: 100,
  expectedCash: 100,
  variance: 0,
  included: false,
  ...patch,
});

const device = (patch: Partial<CashDailyDevice> = {}): CashDailyDevice => ({
  deviceCode: "POS-1",
  selectionMode: "Default",
  selectionStale: false,
  selectionOverlapWarning: false,
  selectionReason: null,
  selectedByName: null,
  selectedAtUtc: null,
  includedCash: 100,
  archives: [archive("c2", { included: true }), archive("c1")],
  ...patch,
});

// ───────── 默认纳入最新一份：以服务端 included 为准 ─────────
assert.deepEqual(getIncludedCloseIds(device()), ["c2"]);
assert.deepEqual(getIncludedCloseIds(device({ archives: [] })), []);

// ───────── 勾选 / 取消 ─────────
assert.deepEqual(toggleCloseId([], "c1"), ["c1"]);
assert.deepEqual(toggleCloseId(["c1"], "c2"), ["c1", "c2"]);
assert.deepEqual(toggleCloseId(["c1", "c2"], "c1"), ["c2"]);
assert.equal(isSameSelection(["a", "b"], ["b", "a"]), true, "与顺序无关");
assert.equal(isSameSelection(["a"], ["a", "b"]), false);
assert.equal(isSameSelection([], []), true);
assert.deepEqual(pruneSelection(["c1", "gone"], [archive("c1")]), ["c1"], "刷新后不存在的存档被剔除");

// ───────── 求和：按分累加 ─────────
{
  const archives = [archive("a", { countedCash: 0.1 }), archive("b", { countedCash: 0.2 }), archive("c", { countedCash: 500 })];
  assert.equal(sumSelectedCountedCash(archives, ["a", "b"]), 0.3, "浮点精确");
  assert.equal(sumSelectedCountedCash(archives, ["a", "b", "c"]), 500.3);
  assert.equal(sumSelectedCountedCash(archives, []), 0);
  assert.equal(sumSelectedCountedCash(archives, ["missing"]), 0);
}

// ───────── 统计区间重叠 ─────────
{
  const morning = archive("m", { periodFromUtc: "2026-10-06T00:00:00Z", periodToUtc: "2026-10-06T08:00:00Z" });
  const noon = archive("n", { periodFromUtc: "2026-10-06T08:00:00Z", periodToUtc: "2026-10-06T14:00:00Z" });
  const overlapping = archive("o", { periodFromUtc: "2026-10-06T06:00:00Z", periodToUtc: "2026-10-06T10:00:00Z" });
  const all = [morning, noon, overlapping];
  assert.equal(hasOverlappingPeriods(all, ["m", "n"]), false, "首尾恰好相接不算重叠");
  assert.equal(hasOverlappingPeriods(all, ["m", "o"]), true, "区间交叉算重叠");
  assert.equal(hasOverlappingPeriods(all, ["n", "o"]), true);
  assert.equal(hasOverlappingPeriods(all, ["m"]), false, "只选一份不会重叠");
  assert.equal(hasOverlappingPeriods(all, []), false);
  assert.equal(hasOverlappingPeriods(all, ["m", "n", "o"]), true);
  // 一个区间完全包含另一个
  const wide = archive("w", { periodFromUtc: "2026-10-06T00:00:00Z", periodToUtc: "2026-10-06T23:00:00Z" });
  const inner = archive("i", { periodFromUtc: "2026-10-06T09:00:00Z", periodToUtc: "2026-10-06T10:00:00Z" });
  assert.equal(hasOverlappingPeriods([wide, inner], ["w", "i"]), true, "包含也是重叠");
  // 无法解析的时间忽略，不抛错
  const broken = archive("b", { periodFromUtc: "oops", periodToUtc: "oops" });
  assert.equal(hasOverlappingPeriods([morning, broken], ["m", "b"]), false);
}

// ───────── 设备提示 ─────────
assert.deepEqual(resolveDeviceNotices(device()), { stale: false, overlap: false });
assert.deepEqual(resolveDeviceNotices(device({ selectionStale: true, selectionOverlapWarning: true })), {
  stale: true,
  overlap: true,
});
assert.equal(hasSelectableArchives(device()), true);
assert.equal(hasSelectableArchives(device({ archives: [archive("only")] })), false, "只有一份时没有可选项");

// ───────── 手选校验：勾选存档 + 必填原因 ─────────
assert.deepEqual(validateManualSelection({ device: device(), selected: [], reason: "" }), ["noneSelected", "reasonRequired"]);
assert.deepEqual(validateManualSelection({ device: device(), selected: ["c1", "c2"], reason: "  " }), ["reasonRequired"]);
assert.deepEqual(validateManualSelection({ device: device(), selected: ["c1", "c2"], reason: "班" }), ["reasonRequired"], "原因至少两个字");
assert.deepEqual(validateManualSelection({ device: device(), selected: ["c1", "c2"], reason: "两次交班都要算" }), []);
assert.deepEqual(
  validateManualSelection({ device: device(), selected: ["c1"], reason: "x".repeat(501) }),
  ["reasonTooLong"],
);
// 已是同一批手选且没有新存档：不必重复提交
assert.deepEqual(
  validateManualSelection({
    device: device({ selectionMode: "Manual", archives: [archive("c2", { included: true }), archive("c1", { included: true })] }),
    selected: ["c1", "c2"],
    reason: "同上",
  }),
  ["unchanged"],
);
// 选择之后又有新存档（stale）时，即使勾选相同也允许确认提交
assert.deepEqual(
  validateManualSelection({
    device: device({
      selectionMode: "Manual",
      selectionStale: true,
      archives: [archive("c2", { included: true }), archive("c1", { included: true })],
    }),
    selected: ["c1", "c2"],
    reason: "确认",
  }),
  [],
);

// ───────── 请求体 ─────────
assert.deepEqual(
  buildManualSelectionRequest({
    storeCode: "S01",
    businessDate: "2026-10-06",
    deviceCode: "POS-1",
    selected: ["c1", "c2"],
    reason: " 早晚班都要算 ",
  }),
  {
    storeCode: "S01",
    businessDate: "2026-10-06",
    deviceCode: "POS-1",
    mode: "Manual",
    closeIds: ["c1", "c2"],
    reason: "早晚班都要算",
  },
);
assert.equal(
  buildManualSelectionRequest({ storeCode: "S01", businessDate: "2026-10-06", deviceCode: "POS-1", selected: [], reason: "x" }),
  null,
);
assert.equal(
  buildManualSelectionRequest({ storeCode: "S01", businessDate: "2026-10-06", deviceCode: "POS-1", selected: ["c1"], reason: "  " }),
  null,
  "手选必须有原因",
);
assert.deepEqual(buildDefaultSelectionRequest({ storeCode: "S01", businessDate: "2026-10-06", deviceCode: "POS-1" }), {
  storeCode: "S01",
  businessDate: "2026-10-06",
  deviceCode: "POS-1",
  mode: "Default",
  closeIds: [],
});

console.log("close-selection.test.ts: ok");
