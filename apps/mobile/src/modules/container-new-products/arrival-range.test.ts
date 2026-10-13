import assert from "node:assert/strict";
import { deviceLocalToday, formatArrivalDateRange, matchesArrivalRange, resolveListDate } from "./arrival-range";

const today = "2026-10-01";

// ───────── 新版后端：按货柜到仓库日期分过去 / 未来 ─────────
// 到仓库日期早于门店本地今天算过去；今天到仓算未来；到店日期完全不参与判断（这里故意给得很偏）
const warehouse = (warehouseArrivalDate: string) => ({ warehouseArrivalDate, estimatedStoreArrivalDate: "2026-12-01", estimatedStoreArrivalDateEnd: "2026-12-05" });
assert.equal(matchesArrivalRange(warehouse("2026-09-30"), "past", today), true);
assert.equal(matchesArrivalRange(warehouse("2026-09-30"), "upcoming", today), false);
assert.equal(matchesArrivalRange(warehouse("2026-10-01"), "past", today), false);
assert.equal(matchesArrivalRange(warehouse("2026-10-01"), "upcoming", today), true);
assert.equal(matchesArrivalRange(warehouse("2026-10-14"), "upcoming", today), true);
assert.equal(matchesArrivalRange(warehouse("2026-10-14"), "all", today), true);
// 到仓库日期早、到店日期晚（或相反）时，以到仓库日期为准
assert.equal(matchesArrivalRange({ warehouseArrivalDate: "2026-09-25", estimatedStoreArrivalDate: "2026-10-10", estimatedStoreArrivalDateEnd: "2026-10-14" }, "past", today), true);
assert.equal(matchesArrivalRange({ warehouseArrivalDate: "2026-10-12", estimatedStoreArrivalDate: "2026-09-20", estimatedStoreArrivalDateEnd: "2026-09-24" }, "upcoming", today), true);
// 门店本地今天带时间部分也只比较日期
assert.equal(matchesArrivalRange(warehouse("2026-10-01"), "past", "2026-10-01T23:59:00"), false);

// ───────── 旧版后端：没有到仓库日期，保持原来按预计到店区间的判断 ─────────
const legacy = (estimatedStoreArrivalDate: string, estimatedStoreArrivalDateEnd: string | null) => ({ warehouseArrivalDate: null, estimatedStoreArrivalDate, estimatedStoreArrivalDateEnd });
// 不返回结束日时，按起始日当作单一到店日
const yesterday = legacy("2026-09-30", null);
const sameDay = legacy("2026-10-01", null);
const later = legacy("2026-10-14", null);
assert.equal(matchesArrivalRange(yesterday, "past", today), true);
assert.equal(matchesArrivalRange(yesterday, "upcoming", today), false);
// 今天的归入「未来 3 周」
assert.equal(matchesArrivalRange(sameDay, "past", today), false);
assert.equal(matchesArrivalRange(sameDay, "upcoming", today), true);
assert.equal(matchesArrivalRange(later, "all", today), true);
// 后端若带时间部分也只比较日期
assert.equal(matchesArrivalRange(legacy("2026-10-01T00:00:00", null), "past", today), false);
// 到店区间：结束日早于今天才算过去；跨过今天的区间仍算未来（还可能今天或以后到）
const endedYesterday = legacy("2026-09-25", "2026-09-30");
const spansToday = legacy("2026-09-28", "2026-10-01");
assert.equal(matchesArrivalRange(endedYesterday, "past", today), true);
assert.equal(matchesArrivalRange(endedYesterday, "upcoming", today), false);
assert.equal(matchesArrivalRange(spansToday, "past", today), false);
assert.equal(matchesArrivalRange(spansToday, "upcoming", today), true);

// ───────── 排序 / 面板排序用的日期 ─────────
// 优先到仓库日期；旧版后端没有时退回预计到店起始日（只取前 10 位）
assert.equal(resolveListDate({ warehouseArrivalDate: "2026-09-28", estimatedStoreArrivalDate: "2026-10-05" }), "2026-09-28");
assert.equal(resolveListDate({ warehouseArrivalDate: null, estimatedStoreArrivalDate: "2026-10-05" }), "2026-10-05");
assert.equal(resolveListDate({ warehouseArrivalDate: null, estimatedStoreArrivalDate: "2026-10-05T00:00:00" }), "2026-10-05");

assert.equal(deviceLocalToday(new Date(2026, 0, 5, 23, 30)), "2026-01-05");

// 日期显示：单日只有起始日；结束日缺失或与起始日相同只显示单日；区间显示起止两段，跨年显示两个年份
assert.deepEqual(formatArrivalDateRange("2026-10-02T00:00:00", null), { start: "02/10", end: null, year: "2026" });
assert.deepEqual(formatArrivalDateRange("2026-10-02", "2026-10-02"), { start: "02/10", end: null, year: "2026" });
assert.deepEqual(formatArrivalDateRange("2026-10-02", "2026-10-07"), { start: "02/10", end: "07/10", year: "2026" });
assert.deepEqual(formatArrivalDateRange("2026-12-29", "2027-01-05"), { start: "29/12", end: "05/01", year: "2026–2027" });

console.log("container-new-products arrival range tests passed");
