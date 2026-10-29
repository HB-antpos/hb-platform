import assert from "node:assert/strict";
import { SCAN_HIGHLIGHT_HAPTICS, resolveScanHighlight } from "./scan-highlight";
import { BELL_PATTERNS } from "./scan-bell-tones";

const base = { discountRate: 0, activePromotionCount: 0, multiCodeCount: 0 };

// 都没有 -> 普通「找到」提示音。
assert.equal(resolveScanHighlight(base), null);
assert.equal(resolveScanHighlight({ discountRate: null, activePromotionCount: 0, multiCodeCount: undefined }), null);

// 单项命中。
assert.equal(resolveScanHighlight({ ...base, discountRate: 0.2 }), "discount");
assert.equal(resolveScanHighlight({ ...base, activePromotionCount: 1 }), "promotion");
assert.equal(resolveScanHighlight({ ...base, multiCodeCount: 3 }), "multi_code");

// 折扣率口径与「折扣标签」一致：0 / 负数 / NaN 都不算有折扣。
for (const discountRate of [0, -0.1, Number.NaN, null, undefined]) {
  assert.equal(resolveScanHighlight({ ...base, discountRate }), null, `折扣率 ${String(discountRate)}`);
}

// 同时命中时只响一个：促销 > 折扣 > 多码。
assert.equal(resolveScanHighlight({ discountRate: 0.2, activePromotionCount: 2, multiCodeCount: 5 }), "promotion");
assert.equal(resolveScanHighlight({ discountRate: 0.2, activePromotionCount: 0, multiCodeCount: 5 }), "discount");

// 每种提示都有震动节奏，起点为 0，延迟递增，并与铃音起音时刻一一对齐。
for (const kind of Object.keys(BELL_PATTERNS) as Array<keyof typeof BELL_PATTERNS>) {
  const pulses = SCAN_HIGHLIGHT_HAPTICS[kind];
  assert.ok(pulses.length >= 1, `${kind} 缺少震动`);
  assert.equal(pulses[0].delayMs, 0);
  assert.deepEqual(
    pulses.map((pulse) => pulse.delayMs),
    BELL_PATTERNS[kind].map((note) => note.startMs).filter((_, index) => index < pulses.length),
    `${kind} 震动要对齐铃音起音`,
  );
}

console.log("scan-highlight.test.ts: ok");
