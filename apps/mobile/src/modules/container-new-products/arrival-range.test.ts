import assert from "node:assert/strict";
import { deviceLocalToday, matchesArrivalRange } from "./arrival-range";

const today = "2026-10-01";
const yesterday = { estimatedStoreArrivalDate: "2026-09-30" };
const sameDay = { estimatedStoreArrivalDate: "2026-10-01" };
const later = { estimatedStoreArrivalDate: "2026-10-14" };

assert.equal(matchesArrivalRange(yesterday, "past", today), true);
assert.equal(matchesArrivalRange(yesterday, "upcoming", today), false);
// 今天到店的归入「未来 2 周」
assert.equal(matchesArrivalRange(sameDay, "past", today), false);
assert.equal(matchesArrivalRange(sameDay, "upcoming", today), true);
assert.equal(matchesArrivalRange(later, "all", today), true);
// 后端若带时间部分也只比较日期
assert.equal(matchesArrivalRange({ estimatedStoreArrivalDate: "2026-10-01T00:00:00" }, "past", today), false);

assert.equal(deviceLocalToday(new Date(2026, 0, 5, 23, 30)), "2026-01-05");

console.log("container-new-products arrival range tests passed");
