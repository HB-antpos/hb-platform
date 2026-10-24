import assert from "node:assert/strict";
import { counts, dayOverview, monthOverview, weekOverview, hoursFrom } from "./fixtures";
import {
  activeCompletedDays,
  buildDailyChart,
  buildHourlyChart,
  buildProfileChart,
  busiestDay,
  completedHourCount,
  dailyAverage,
  findPeak,
  formatAverage,
  formatCount,
  formatHourRange,
  resolveCompare,
  resolveHourWindow,
  toPercent,
  weekdayAverages,
  weekendAverage,
} from "./metrics";

// 数字格式
assert.equal(formatCount(1536), "1,536");
assert.equal(formatCount(999), "999");
assert.equal(formatAverage(2.44), "2.4");
assert.equal(formatAverage(36), "36");
assert.equal(formatHourRange(9), "09:00–10:00");
assert.equal(toPercent(110, 100), 10);
assert.equal(toPercent(5, 0), null);

// ── 今天：只比到上一个完整小时（14:32 → 0–13 点），不插值 ──
{
  const overview = dayOverview();
  assert.equal(completedHourCount(overview), 14);
  const compare = resolveCompare(overview);
  assert.ok(compare);
  // 今天 0–13 点：6+21+38+55+64+50 = 234；昨天 0–13 点：8+20+31+49+66+55 = 229
  assert.equal(compare.current, 234);
  assert.equal(compare.previous, 229);
  assert.equal(compare.percent, 2);
  assert.deepEqual(compare.basis, { kind: "hours", throughHour: 14 });
}
// 刚开门（00:10）还没有完整小时：不给同比
assert.equal(resolveCompare(dayOverview({ asOfLocal: "2026-10-24T00:10" })), null);
// 历史日：整天对整天
{
  const compare = resolveCompare(dayOverview({ isCurrent: false, from: "2026-10-23", to: "2026-10-23" }));
  assert.ok(compare);
  assert.deepEqual(compare.basis, { kind: "full" });
  assert.equal(compare.current, 6 + 21 + 38 + 55 + 64 + 50 + 31);
}
// 对照期为 0：百分比为 null（不显示无穷大）
{
  const overview = dayOverview();
  overview.previous = { ...overview.previous, hours: hoursFrom([]) };
  assert.equal(resolveCompare(overview)?.percent, null);
}

// ── 本周：周一到周五（周六是今天，进行中，不计入）──
{
  const overview = weekOverview();
  const compare = resolveCompare(overview);
  assert.ok(compare);
  assert.equal(compare.current, 231 + 214 + 246 + 268 + 312);
  assert.equal(compare.previous, 218 + 225 + 232 + 251 + 298);
  assert.deepEqual(compare.basis, { kind: "days", days: 5 });
  assert.equal(compare.percent, 4);
  // 日均 / 最忙一天只含已结束的营业日
  assert.equal(activeCompletedDays(overview).length, 5);
  assert.equal(dailyAverage(overview), (231 + 214 + 246 + 268 + 312) / 5);
  assert.equal(busiestDay(overview)?.date, "2026-10-23");
  assert.equal(weekendAverage(overview), null); // 周末还没有已结束的营业日
}
// 周一当天：本周还没有已结束的日子
{
  const overview = weekOverview({ today: "2026-10-19", asOfLocal: "2026-10-19T09:00" });
  assert.equal(resolveCompare(overview), null);
  assert.equal(dailyAverage(overview), null);
}
// 没有抓拍的日子不算营业日（摄像机离线 / 歇业不拉低平均）
{
  const overview = weekOverview();
  overview.current.days[1] = { date: "2026-10-20", counts: counts(0) };
  assert.equal(activeCompletedDays(overview).length, 4);
}

// ── 本月：1 号对 1 号，对照期没有这一天就两边都不比 ──
{
  const overview = monthOverview();
  const compare = resolveCompare(overview);
  assert.ok(compare);
  assert.deepEqual(compare.basis, { kind: "days", days: 23 });
  assert.ok(compare.percent !== null);
  const history = resolveCompare(monthOverview({ isCurrent: false, from: "2026-10-01", to: "2026-10-31", today: "2026-11-15" }));
  assert.ok(history);
  // 10 月 31 天、9 月 30 天：31 号没有对照，只比前 30 天
  assert.deepEqual(history.basis, { kind: "full" });
}
// 按星期平均：只用已结束的日子；周末日均
{
  const overview = monthOverview();
  const byWeekday = weekdayAverages(overview);
  assert.equal(byWeekday.length, 7);
  assert.equal(byWeekday[0], 100); // 周一
  assert.equal(byWeekday[6], 160); // 周日
  assert.equal(weekendAverage(overview), (150 + 160) / 2);
}

// ── 峰值 ──
assert.deepEqual(findPeak([0, 3, 9, 9, 2]), { hour: 2, value: 9 });
assert.equal(findPeak([0, 0, null]), null);

// ── 小时窗口：默认 8–20，数据超出向外扩 ──
assert.deepEqual(resolveHourWindow([]), { start: 8, end: 20 });
{
  const early = Array(24).fill(0);
  early[6] = 3;
  early[22] = 1;
  assert.deepEqual(resolveHourWindow(early), { start: 6, end: 22 });
  const edge = Array(24).fill(0);
  edge[0] = 2;
  edge[23] = 2;
  assert.deepEqual(resolveHourWindow(edge), { start: 0, end: 23 });
}

// ── 今天分时图：今天在 14 点之后为 null，14 点是进行中，现在线按分钟定位 ──
{
  const model = buildHourlyChart(dayOverview());
  assert.deepEqual(model.window, { start: 8, end: 20 });
  assert.equal(model.current.length, 13);
  assert.equal(model.current[0], 6); // 8 点
  assert.equal(model.current[6], 31); // 14 点（进行中）
  assert.equal(model.current[7], null); // 15 点还没到
  assert.equal(model.previous[4], 66); // 昨天 12 点
  assert.equal(model.partialIndex, 6);
  assert.ok(model.nowIndex !== null && Math.abs(model.nowIndex - (6 + 32 / 60)) < 1e-9);
  assert.equal(model.nowLabel, "14:32");
}
// 历史日：没有进行中的小时，也没有「现在」线
{
  const model = buildHourlyChart(dayOverview({ isCurrent: false, from: "2026-10-23", to: "2026-10-23" }));
  assert.equal(model.partialIndex, null);
  assert.equal(model.nowIndex, null);
  assert.equal(model.current.every((value) => value !== null), true);
}

// ── 周图：周日还没到为 null，周六是进行中，对照期按下标对齐 ──
{
  const model = buildDailyChart(weekOverview());
  assert.equal(model.dates.length, 7);
  assert.deepEqual(model.current, [231, 214, 246, 268, 312, 265, null]);
  assert.deepEqual(model.previous, [218, 225, 232, 251, 298, 389, 341]);
  assert.equal(model.partialIndex, 5);
  assert.deepEqual(model.weekend, [false, false, false, false, false, true, true]);
}
// 月图：31 天，对照期（9 月）只有 30 天，31 号对照为 null
{
  const model = buildDailyChart(monthOverview());
  assert.equal(model.dates.length, 31);
  assert.equal(model.previous[29] !== null, true);
  assert.equal(model.previous[30], null);
  assert.equal(model.current[24], null); // 25 号还没到
  assert.equal(model.partialIndex, 23);
}

// ── 日均分时（周 / 月）：合计 ÷ 参与天数 ──
{
  const profile = buildProfileChart(weekOverview());
  assert.ok(profile);
  assert.equal(profile.peak?.value, 36 * 5 / 5);
  assert.equal(profile.values.length, profile.window.end - profile.window.start + 1);
  assert.equal(buildProfileChart(weekOverview({ current: { ...weekOverview().current, hourlyDays: 0 } })), null);
}

console.log("traffic-stats metrics tests passed");
