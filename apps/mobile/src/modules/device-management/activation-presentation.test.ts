import { getGrantLifetime, splitRemainingMinutes } from "./activation-presentation";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a !== e) {
    throw new Error(`${label}: expected ${e}, got ${a}`);
  }
}

const created = "2026-10-30T00:00:00Z";
const expires = "2026-10-30T10:00:00Z";

// 总共 10 小时，已过 7 小时：剩 3 小时、占 30%。
assertEqual(getGrantLifetime(created, expires, new Date("2026-10-30T07:00:00Z")), { remainingMinutes: 180, fraction: 0.3, expired: false }, "mid life");
// 刚创建：剩余 100%。
assertEqual(getGrantLifetime(created, expires, new Date("2026-10-30T00:00:00Z"))?.fraction, 1, "just created");
// 已过期：剩余 0，进度 0。
assertEqual(getGrantLifetime(created, expires, new Date("2026-10-30T11:00:00Z")), { remainingMinutes: 0, fraction: 0, expired: true }, "expired");
// 创建时间之前（时钟偏差）：比例不超过 1。
assertEqual(getGrantLifetime(created, expires, new Date("2026-10-29T23:00:00Z"))?.fraction, 1, "clock skew is clamped");
// 不足一分钟按一分钟算，不显示「剩余 0 分钟」却还没过期。
assertEqual(getGrantLifetime(created, expires, new Date("2026-10-30T09:59:30Z"))?.remainingMinutes, 1, "partial minute rounds up");
// 无时区后缀的时间按 UTC 处理，与带 Z 的结果一致。
assertEqual(
  getGrantLifetime("2026-10-30T00:00:00", "2026-10-30T10:00:00", new Date("2026-10-30T07:00:00Z")),
  getGrantLifetime(created, expires, new Date("2026-10-30T07:00:00Z")),
  "naive timestamps are UTC",
);
// 无法解析或过期早于创建：返回 null，界面退化成只显示有效期至。
assertEqual(getGrantLifetime("bad", expires), null, "bad created");
assertEqual(getGrantLifetime(created, null), null, "no expiry");
assertEqual(getGrantLifetime(expires, created), null, "expiry before creation");

assertEqual(splitRemainingMinutes(192), { hours: 3, minutes: 12 }, "split");
assertEqual(splitRemainingMinutes(45), { hours: 0, minutes: 45 }, "under an hour");
assertEqual(splitRemainingMinutes(-5), { hours: 0, minutes: 0 }, "negative is clamped");

console.log("activation-presentation ok");
