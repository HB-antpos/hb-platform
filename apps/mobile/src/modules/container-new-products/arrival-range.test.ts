import assert from "node:assert/strict";
import { deviceLocalToday, formatArrivalDateRange, matchesArrivalRange } from "./arrival-range";

const today = "2026-10-01";
// 旧版后端不返回结束日时，按起始日当作单一到店日
const yesterday = { estimatedStoreArrivalDate: "2026-09-30", estimatedStoreArrivalDateEnd: null };
const sameDay = { estimatedStoreArrivalDate: "2026-10-01", estimatedStoreArrivalDateEnd: null };
const later = { estimatedStoreArrivalDate: "2026-10-14", estimatedStoreArrivalDateEnd: null };

assert.equal(matchesArrivalRange(yesterday, "past", today), true);
assert.equal(matchesArrivalRange(yesterday, "upcoming", today), false);
// 今天到店的归入「未来 3 周」
assert.equal(matchesArrivalRange(sameDay, "past", today), false);
assert.equal(matchesArrivalRange(sameDay, "upcoming", today), true);
assert.equal(matchesArrivalRange(later, "all", today), true);
// 后端若带时间部分也只比较日期
assert.equal(matchesArrivalRange({ estimatedStoreArrivalDate: "2026-10-01T00:00:00", estimatedStoreArrivalDateEnd: null }, "past", today), false);

// 到店区间：结束日早于今天才算过去；跨过今天的区间仍算未来（还可能今天或以后到）
const endedYesterday = { estimatedStoreArrivalDate: "2026-09-25", estimatedStoreArrivalDateEnd: "2026-09-30" };
const spansToday = { estimatedStoreArrivalDate: "2026-09-28", estimatedStoreArrivalDateEnd: "2026-10-01" };
assert.equal(matchesArrivalRange(endedYesterday, "past", today), true);
assert.equal(matchesArrivalRange(endedYesterday, "upcoming", today), false);
assert.equal(matchesArrivalRange(spansToday, "past", today), false);
assert.equal(matchesArrivalRange(spansToday, "upcoming", today), true);

assert.equal(deviceLocalToday(new Date(2026, 0, 5, 23, 30)), "2026-01-05");

// 日期显示：区间显示起止两段；结束日缺失或与起始日相同只显示单日；跨年显示两个年份
assert.deepEqual(formatArrivalDateRange("2026-10-02", "2026-10-07"), { start: "02/10", end: "07/10", year: "2026" });
assert.deepEqual(formatArrivalDateRange("2026-10-02T00:00:00", null), { start: "02/10", end: null, year: "2026" });
assert.deepEqual(formatArrivalDateRange("2026-10-02", "2026-10-02"), { start: "02/10", end: null, year: "2026" });
assert.deepEqual(formatArrivalDateRange("2026-12-29", "2027-01-05"), { start: "29/12", end: "05/01", year: "2026–2027" });

console.log("container-new-products arrival range tests passed");
