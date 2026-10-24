import assert from "node:assert/strict";
import { defaultCurve, fixedRules, fmtRate } from "./pricing-curve";
import {
  MAX_INTERVALS,
  actualPrice,
  addInterval,
  appendInterval,
  bendPercent,
  breakLink,
  chartRange,
  convertLegacy,
  endPrice,
  intervalAt,
  intervalsError,
  isEditable,
  isLegalTail,
  linkedFlags,
  makeCurve,
  makeFixed,
  priceDrops,
  relink,
  removeInterval,
  setBend,
  setBoundary,
  setEndpoint,
  setEndpointRate,
  setKind,
  setRate,
  setShape,
  shiftBrokenAfterAdd,
  shiftBrokenAfterRemove,
  startPrice,
  visibleMaxPrice,
  type Result,
  type Rule,
} from "./pricing-intervals";

/** 移动端区间逻辑的单元测试：断言与 Web pricingIntervals.test.ts 相同，错误改为断言错误码。 */
const NONE = new Set<number>();
const must = (r: Result) => {
  assert.equal(r.error, undefined, JSON.stringify(r.error));
  return r.rules!;
};
// 与后端测试同一组数据：≤10 固定 2.5×，10～50 曲线（25.00 → 100.00），>50 固定 2.0×
const mixed = (): Rule[] => [makeFixed(0.1, 10, 2.5), makeCurve(10, 50, 25, 100), makeFixed(50, 99999, 2)];

// ---- 校验 ----
assert.equal(intervalsError(defaultCurve()), null);
assert.equal(intervalsError(mixed()), null, "曲线端点跟随固定交界价时不要求合法尾数");
assert.equal(intervalsError([makeFixed(0.1, 10, 2.5), makeCurve(10, 50, 26, 100)])?.code, "startTail");
assert.equal(intervalsError([makeCurve(1, 10, 2.5, 24.99), makeCurve(10, 50, 25, 100)])?.code, "startTail");
assert.deepEqual(intervalsError([makeFixed(0.1, 10, 3), makeFixed(11, 50, 2.5)]), { code: "notContiguous", params: { n: 2 } });
assert.equal(intervalsError([makeFixed(0.1, 10, 3), makeFixed(9, 50, 2.5)])?.code, "notContiguous");
assert.equal(intervalsError([makeFixed(0.1, 10, 3), makeFixed(10, 50, 2.5)]), null, "阶梯定价允许交界价差");
assert.equal(intervalsError([makeFixed(0.1, 50, 5.1)])?.code, "rateRange");
assert.equal(intervalsError([makeFixed(0.1, 50, 1.4)])?.code, "rateRange");
assert.equal(intervalsError([makeCurve(10, 50, 24.99, 24.5)])?.code, "rateRange", "终点成率 0.49 先触发成率范围");
assert.equal(intervalsError([])?.code, "noIntervals");
const tens = (n: number) => Array.from({ length: n }, (_, i) => makeFixed(0.1 + i * 10, 10.1 + i * 10, 2.5));
assert.equal(intervalsError(tens(MAX_INTERVALS)), null);
assert.deepEqual(intervalsError(tens(MAX_INTERVALS + 1)), { code: "tooMany", params: { max: 10 } });
assert.ok(isEditable(mixed()));
assert.ok(!isEditable([{ ...makeFixed(0.1, 10, 2), algorithm: "Step" }]));
assert.ok(!isEditable([{ ...makeFixed(0.1, 10, 2), algorithm: "Linear" }]), "没有节点价的旧版 Linear 不可直接编辑");
assert.ok(!isEditable([]));

// ---- 交界归属与实际售价 ----
assert.equal(intervalAt(mixed(), 10), 0);
assert.equal(intervalAt(mixed(), 10.01), 1);
assert.equal(intervalAt(mixed(), 50), 1);
assert.equal(intervalAt(mixed(), 50.01), 2);
assert.equal(intervalAt(mixed(), 0.05), -1);
assert.equal(actualPrice(mixed()[0], 10), 24.99, "整数 25.00 → 24.99");
assert.equal(actualPrice(makeFixed(0.1, 99999, 2.5), 8.94), 22.5, "用户反馈的实例：22.35 → 22.50");
assert.equal(actualPrice(makeFixed(0.1, 99999, 2.5), 0.4), 1, "0.99 归一到 1");
assert.equal(actualPrice(mixed()[1], 50), 99.99);

// ---- 回落与联动 ----
assert.deepEqual(priceDrops(mixed()), []);
const stepDown = [makeFixed(0.1, 10, 3), makeFixed(10, 50, 2.5)];
assert.deepEqual(priceDrops(stepDown).map((d) => [d.at, d.from, d.to]), [[10, 30, 25]]);
assert.deepEqual(linkedFlags(mixed(), NONE), [true, true]);
assert.deepEqual(linkedFlags(mixed(), new Set([0])), [false, true]);
assert.deepEqual(linkedFlags(stepDown, NONE), [false]);
assert.deepEqual([...shiftBrokenAfterAdd(new Set([0, 2]), 1)].sort(), [0, 3]);
assert.deepEqual([...shiftBrokenAfterRemove(new Set([0, 1, 3]), 2)].sort(), [0, 2]);

// ---- 固定成率联动 / 断开 / 重新联动 ----
{
  const r = must(setRate(mixed(), 0, 3, linkedFlags(mixed(), NONE)));
  assert.equal(startPrice(r[1]), 30, "联动时曲线起点跟随 交界成本 × 成率");
  const detached = must(breakLink(mixed(), 0));
  assert.equal(startPrice(detached[1]), 24.99, "断开时吸附到合法尾数");
  const broken = must(setRate(detached, 0, 3, linkedFlags(detached, new Set([0]))));
  assert.deepEqual(priceDrops(broken).map((d) => [d.from, d.to]), [[30, 24.99]]);
  assert.equal(startPrice(must(relink(broken, 0, "left"))[1]), 30);
  assert.deepEqual(setRate(mixed(), 0, 5.5, [true, true]).error, { code: "rateOutOfRange", params: { min: 1.5, max: 5 } });
  assert.equal(setRate(mixed(), 1, 2.5, [true, true]).error?.code, "notFixed");
}

// ---- 交界成本 ----
{
  const flags = linkedFlags(mixed(), NONE);
  const r = must(setBoundary(mixed(), 0, 8, flags));
  assert.equal(startPrice(r[1]), 20);
  assert.deepEqual(setBoundary(mixed(), 0, 60, flags).error, { code: "boundaryOutOfRange", params: { min: "0.10", max: "50.00" } });
  assert.equal(setBoundary(mixed(), 0, Number.NaN, flags).error?.code, "boundaryRequired");
}

// ---- 曲线端点（按成率输入，显示零售价） ----
{
  const flags = linkedFlags(defaultCurve(), NONE);
  const r = must(setEndpointRate(defaultCurve(), 0, "end", 3.2, flags));
  assert.equal(endPrice(r[0]), 159.99);
  assert.equal(startPrice(r[1]), 159.99);
  assert.equal(r[0].endRate, 3.2, "保存用户输入的成率");
  assert.equal(r[1].startRate, 3.2);
  assert.equal(endPrice(must(setEndpointRate(defaultCurve(), 0, "end", 3, flags))[0]), 150.5);
  assert.equal(setEndpointRate(mixed(), 1, "start", 3, linkedFlags(mixed(), NONE)).error?.code, "endpointLocked");
  assert.equal(setEndpointRate(mixed(), 0, "end", 3, linkedFlags(mixed(), NONE)).error?.code, "fixedUseRate");
  assert.equal(setEndpointRate(defaultCurve(), 0, "end", Number.NaN, flags).error?.code, "rateRequired");
  assert.equal(startPrice(must(setEndpoint(mixed(), 1, "start", 24.99, linkedFlags(mixed(), new Set([0]))))[1]), 24.99);
}

// ---- 类型切换、形状、弧度 ----
{
  const flags = linkedFlags(mixed(), NONE);
  const toCurve = must(setKind(mixed(), 0, "curve", flags));
  assert.ok(isLegalTail(endPrice(toCurve[0])));
  assert.equal(must(setKind(mixed(), 1, "fixed", flags))[1].startRate, 2.25);
  assert.equal(setKind(mixed(), 2, "curve", flags).error?.code, "unboundedCurve");
  const bent = must(setBend(must(setShape(defaultCurve(), 1, "ArcUp")), 1, 50));
  assert.ok(Math.abs(bendPercent(bent[1]) - 50) < 0.01, "滑块位置按占最大弧度的比例回显");
  assert.equal(setShape(mixed(), 0, "ArcUp").error?.code, "fixedNoShape");
}

// ---- 添加（末尾追加）/ 拆分 / 删除 ----
{
  assert.deepEqual(
    must(appendInterval([makeFixed(1, 10, 2.5)])).map((r) => [r.minPrice, r.maxPrice, r.algorithm]),
    [
      [1, 10, "Fixed"],
      [10, 20, "Fixed"],
    ],
    "1～10 之后接 10～20，新区间不在已有区间内部",
  );
  assert.equal(appendInterval([makeFixed(0.1, 99999, 2.5)]).error?.code, "unboundedAppend");
  assert.equal(appendInterval(tens(MAX_INTERVALS)).error?.code, "tooMany");
  assert.equal(must(addInterval(defaultCurve(), 0)).length, 4);
  assert.equal(must(removeInterval(mixed(), 1))[0].maxPrice, 50);
  assert.equal(removeInterval(fixedRules(2.5), 0).error?.code, "keepOne");
}

// ---- 图表范围 ----
assert.deepEqual(chartRange(mixed()), { first: 0.1, end: 100 });
assert.equal(chartRange(defaultCurve()).end, 999);
assert.ok(visibleMaxPrice(mixed(), 100) >= 200);

// ---- 旧版规则转换：说明改为错误码 + 参数 ----
function legacy(min: number, max: number, sr: number, er: number, algorithm: string): Rule {
  return { minPrice: min, maxPrice: max, startRate: sr, endRate: er, algorithm, startRetailPrice: null, endRetailPrice: null, curveBend: null } as Rule;
}
{
  const ws = convertLegacy([legacy(0, 2.5, 3.5, 3.5, "Step"), legacy(2.5, 10, 3.5, 2.8, "Step"), legacy(10, 9999, 2.5, 2.5, "Step")]);
  assert.deepEqual(must(ws).map((r) => [r.algorithm, r.startRate]), [
    ["Fixed", 3.5],
    ["Fixed", 3.5],
    ["Fixed", 2.5],
  ]);
  assert.deepEqual(ws.notes, [
    { code: "raisedMin", params: { n: 1, from: "0.00" } },
    { code: "stepToFixed", params: { n: 1, rate: 3.5 } },
    { code: "stepToFixedEndIgnored", params: { n: 2, rate: 3.5, end: 2.8 } },
    { code: "stepToFixed", params: { n: 3, rate: 2.5 } },
    { code: "drops", params: { count: 1 } },
  ]);
  assert.ok(convertLegacy([legacy(0, 99, 3, 2, "Exponential")]).notes.some((n) => n.code === "expToCurve"));
  assert.equal(convertLegacy([]).error?.code, "convertEmpty");
}

assert.equal(fmtRate(2.5), "2.50");
assert.equal(fmtRate(2.5125), "2.5125");

console.log("pricing-intervals.test.ts: ok");
