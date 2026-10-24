import assert from "node:assert/strict";
import { isUsableTrafficOverview, normalizeTrafficCounts, normalizeTrafficOverview } from "./normalize";

// 计数：负数 / NaN / 字符串一律当 0，小数取整
assert.deepEqual(normalizeTrafficCounts({ visitCount: -3, captureCount: "9", staffMatchCount: 2.6, uniqueVisitorCount: NaN }), {
  visitCount: 0,
  captureCount: 0,
  staffMatchCount: 3,
  uniqueVisitorCount: 0,
  repeatCount: 0,
  lowQualityCount: 0,
  apiFailureCount: 0,
});
assert.equal(normalizeTrafficCounts(null).visitCount, 0);

// 空响应：不崩，开关一律 false，hours 补齐 24 项
{
  const overview = normalizeTrafficOverview(null, "week");
  assert.equal(overview.period, "week");
  assert.equal(overview.countingEnabled, false);
  assert.equal(overview.trafficDedupEnabled, false);
  assert.equal(overview.current.hours.length, 24);
  assert.equal(isUsableTrafficOverview(overview), false);
}

// 正常响应
{
  const overview = normalizeTrafficOverview(
    {
      storeCode: "1008",
      storeName: " Bankstown ",
      timeZoneId: "Australia/Sydney",
      period: "day",
      from: "2026-10-24",
      to: "2026-10-24",
      prevFrom: "2026-10-23",
      prevTo: "2026-10-23",
      today: "2026-10-24",
      asOfLocal: "2026-10-24T14:32",
      isCurrent: true,
      countingEnabled: true,
      trafficDedupEnabled: true,
      current: {
        days: [{ date: "2026-10-24", counts: { visitCount: 265 } }, { date: "bad", counts: {} }],
        // 乱序、重复、越界的小时
        hours: [{ hour: 12, counts: { visitCount: 64 } }, { hour: 99, counts: { visitCount: 5 } }, { hour: 8, counts: { visitCount: 6 } }],
        total: { visitCount: 265 },
        hourlyDays: 1,
      },
      previous: { days: [], hours: [], total: {}, hourlyDays: 0 },
    },
    "day",
  );
  assert.equal(overview.storeName, "Bankstown");
  assert.equal(overview.current.days.length, 1, "格式不对的日期被丢弃");
  assert.equal(overview.current.hours[12].counts.visitCount, 64);
  assert.equal(overview.current.hours[8].counts.visitCount, 6);
  assert.equal(overview.current.hours.reduce((sum, item) => sum + item.counts.visitCount, 0), 70, "越界小时不计入");
  assert.equal(overview.isCurrent, true);
  assert.equal(isUsableTrafficOverview(overview), true);
}

// period 非法时回退到请求的周期
assert.equal(normalizeTrafficOverview({ period: "year" }, "month").period, "month");

console.log("traffic-stats normalize tests passed");
