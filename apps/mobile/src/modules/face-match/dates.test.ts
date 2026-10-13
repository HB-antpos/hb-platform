import assert from "node:assert/strict";
import {
  addDaysYmd,
  addMonthsYmd,
  expiryToUtcIso,
  formatLocalDate,
  formatLocalDateTime,
  maxExpiryDay,
  minExpiryDay,
  parseYmd,
  todayYmd,
} from "./dates";

// 本测试用「本地时间」构造 now，保证在任何 CI 时区下断言一致。
const local = (y: number, m: number, d: number, h = 12, min = 0) => new Date(y, m - 1, d, h, min, 0, 0);

// ───────── 解析与加减 ─────────
assert.deepEqual(parseYmd("2026-10-13"), { year: 2026, month: 10, day: 13 });
assert.equal(parseYmd("2026-02-30"), null, "不存在的日期");
assert.equal(parseYmd("2026-2-3"), null);
assert.equal(parseYmd(""), null);
assert.equal(parseYmd(null), null);
assert.equal(addDaysYmd("2026-12-31", 1), "2027-01-01");
assert.equal(addDaysYmd("2026-03-01", -1), "2026-02-28");
// 月底截断：与 C# AddMonths 一致
assert.equal(addMonthsYmd("2026-01-31", 1), "2026-02-28");
assert.equal(addMonthsYmd("2027-02-28", 12), "2028-02-28");
assert.equal(addMonthsYmd("2028-02-29", 12), "2029-02-28", "闰日 +12 个月取 2 月最后一天");
assert.equal(addMonthsYmd("2026-10-13", 12), "2027-10-13");
assert.equal(addMonthsYmd("2026-11-30", 3), "2027-02-28");
assert.equal(addMonthsYmd("2026-01-15", -1), "2025-12-15");

// ───────── 到期日范围：明天 ~ 今天起 12 个月 ─────────
{
  const now = local(2026, 10, 13);
  assert.equal(todayYmd(now), "2026-10-13");
  assert.equal(minExpiryDay(now), "2026-10-14", "最早明天");
  assert.equal(maxExpiryDay(now), "2027-10-13", "最晚今天起 12 个月");
  assert.equal(maxExpiryDay(local(2028, 2, 29)), "2029-02-28");
  assert.equal(minExpiryDay(local(2026, 12, 31)), "2027-01-01");
}

// ───────── 提交给服务端的 UTC 时刻 ─────────
{
  const now = local(2026, 10, 13, 9, 30);
  // 普通日期：所选日期当天本地 0 点
  assert.equal(expiryToUtcIso("2026-12-25", now), new Date(2026, 11, 25, 0, 0, 0, 0).toISOString());
  assert.equal(expiryToUtcIso("2026-10-14", now), new Date(2026, 9, 14, 0, 0, 0, 0).toISOString());
  assert.throws(() => expiryToUtcIso("2026-13-01", now), /INVALID_EXPIRY_DATE/);

  // 最晚一天：永远不超过「现在起 12 个月」（服务端上限），哪怕夏令时让本地 0 点更晚
  const latest = maxExpiryDay(now);
  const iso = expiryToUtcIso(latest, now);
  const serverCap = Date.UTC(now.getUTCFullYear() + 1, now.getUTCMonth(), now.getUTCDate(), now.getUTCHours(), now.getUTCMinutes(), 0, 0);
  assert.ok(Date.parse(iso) <= serverCap, "不得超过服务端上限");
  assert.ok(Date.parse(iso) > now.getTime(), "必须晚于现在");

  // 收敛分支：现在是当天凌晨 0:30，最晚一天的本地 0 点（可能因夏令时偏移）被夹到上限前一分钟
  const early = local(2026, 3, 31, 0, 30);
  const earlyIso = expiryToUtcIso(maxExpiryDay(early), early);
  const earlyCap = Date.UTC(early.getUTCFullYear() + 1, early.getUTCMonth(), early.getUTCDate(), early.getUTCHours(), early.getUTCMinutes(), 0, 0);
  assert.ok(Date.parse(earlyIso) <= earlyCap);
}

// ───────── 显示格式 ─────────
{
  const iso = new Date(2026, 9, 13, 8, 5, 0).toISOString();
  assert.equal(formatLocalDateTime(iso), "2026-10-13 08:05");
  assert.equal(formatLocalDate(iso), "2026-10-13");
  assert.equal(formatLocalDateTime(null), "-");
  assert.equal(formatLocalDateTime("not-a-date"), "-");
  assert.equal(formatLocalDate(undefined), "-");
}

console.log("dates.test.ts: ok");
