import assert from "node:assert/strict";
import {
  buildRecordsListParams,
  DEFAULT_RECORDS_FILTER,
  getNextOffset,
  mergeUniqueByKey,
  resolveRecordsRange,
} from "./records";

// ───────── 日期预设 ─────────
assert.equal(resolveRecordsRange("all", "2026-10-07"), null);
assert.deepEqual(resolveRecordsRange("last30", "2026-10-07"), { from: "2026-09-08", to: "2026-10-07" });
assert.deepEqual(resolveRecordsRange("last90", "2026-10-07"), { from: "2026-07-10", to: "2026-10-07" });

// ───────── 分页 ─────────
assert.equal(getNextOffset(20, 45), 20);
assert.equal(getNextOffset(40, 45), 40);
assert.equal(getNextOffset(45, 45), undefined, "已加载完不再翻页");
assert.equal(getNextOffset(0, 0), undefined);

assert.deepEqual(
  mergeUniqueByKey([[{ id: "a" }, { id: "b" }], [{ id: "b" }, { id: "c" }]], (item) => item.id).map((item) => item.id),
  ["a", "b", "c"],
  "翻页期间有新记录插入导致重复行时去重，保留先出现的",
);
assert.deepEqual(mergeUniqueByKey([], (item: { id: string }) => item.id), []);

// ───────── 查询参数 ─────────
assert.deepEqual(DEFAULT_RECORDS_FILTER, { range: "last30", includeVoided: false, category: "all" });
assert.deepEqual(buildRecordsListParams("S01", "2026-10-07", DEFAULT_RECORDS_FILTER, 0), {
  storeCode: "S01",
  from: "2026-09-08",
  to: "2026-10-07",
  includeVoided: false,
  category: undefined,
  limit: 20,
  offset: 0,
});
assert.deepEqual(
  buildRecordsListParams("S01", "2026-10-07", { range: "all", includeVoided: true, category: "T2" }, 40),
  { storeCode: "S01", from: undefined, to: undefined, includeVoided: true, category: "T2", limit: 20, offset: 40 },
);

console.log("records.test.ts: ok");
