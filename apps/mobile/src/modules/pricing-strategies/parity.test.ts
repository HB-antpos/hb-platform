import assert from "node:assert/strict";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import * as curve from "./pricing-curve";
import * as mobile from "./pricing-intervals";
import type { PricingStrategyRule as Rule } from "./types";

/**
 * 两端一致性：移动端的计价逻辑是 Web 版的移植（两个包之间没有共享代码）。
 * 这里直接加载 Web 的 pricingCurve.ts / pricingIntervals.ts，对同一批输入逐个比对：
 * 实际售价、校验通过与否、每个编辑操作得到的区间。任何一边改了口径而另一边没改，这里会失败。
 */
const moduleDir = dirname(fileURLToPath(import.meta.url));
const webDir = resolve(moduleDir, "../../../../web/src/pages/PosAdmin/PricingStrategies");

async function run() {
// 路径在运行时拼出，tsc 不会把 Web 源码拉进移动端的类型检查；tsx 以 CommonJS 运行，所以不用顶层 await。
const webCurve = (await import(pathToFileURL(resolve(webDir, "pricingCurve.ts")).href)) as typeof curve;
const web = (await import(pathToFileURL(resolve(webDir, "pricingIntervals.ts")).href)) as Record<string, (...args: unknown[]) => unknown>;

type AnyResult = { rules?: Rule[]; error?: unknown };
const same = (label: string, a: AnyResult, b: AnyResult) => {
  assert.equal(a.error === undefined, b.error === undefined, `${label}：成功 / 失败不一致（移动端 ${JSON.stringify(a.error)}，Web ${JSON.stringify(b.error)}）`);
  assert.deepEqual(a.rules, b.rules, `${label}：区间结果不一致`);
};

// —— 尾数：0.01～300 每隔 0.37 取一个成本，2 种成率 ——
for (let cost = 0.1; cost < 300; cost += 0.37) {
  for (const rate of [1.5, 2.5, 3.333, 5]) {
    assert.equal(curve.finalPrice(cost, cost * rate), webCurve.finalPrice(cost, cost * rate), `finalPrice(${cost}, ×${rate})`);
  }
  assert.equal(curve.tailFloor(cost), webCurve.tailFloor(cost));
  assert.equal(curve.tailCeil(cost), webCurve.tailCeil(cost));
}
assert.deepEqual(curve.defaultCurve(), webCurve.defaultCurve());
assert.deepEqual(curve.fixedRules(2.5), webCurve.fixedRules(2.5));

// —— 一组有代表性的策略（固定 / 曲线 / 混合 / 阶梯回落 / 弧线 / 十个区间） ——
const m = mobile;
const bases: Record<string, () => Rule[]> = {
  fixed: () => curve.fixedRules(2.5),
  curve: () => curve.defaultCurve(),
  mixed: () => [m.makeFixed(0.1, 10, 2.5), m.makeCurve(10, 50, 25, 100), m.makeFixed(50, 99999, 2)],
  stepDown: () => [m.makeFixed(0.1, 10, 3), m.makeFixed(10, 50, 2.5)],
  arc: () => [m.makeCurve(1, 50, 4.5, 174.99, "ArcUp", 0.2), m.makeCurve(50, 200, 174.99, 599.99, "ArcDown", -0.1)],
  tens: () => Array.from({ length: 10 }, (_, i) => m.makeFixed(0.1 + i * 10, 10.1 + i * 10, 2.5)),
};

for (const [name, make] of Object.entries(bases)) {
  const rules = make();
  const n = rules.length;
  assert.equal(m.intervalsError(rules) === null, web.intervalsError(rules) === null, `${name}：校验结论不一致`);
  assert.deepEqual(m.priceDrops(rules), web.priceDrops(rules), `${name}：回落`);
  const flags = m.linkedFlags(rules, new Set());
  assert.deepEqual(flags, web.linkedFlags(rules, new Set()), `${name}：联动标记`);
  for (const r of rules) {
    for (const t of [0, 0.13, 0.5, 0.77, 1]) {
      const cost = r.minPrice + (Math.min(r.maxPrice, 500) - r.minPrice) * t;
      assert.equal(m.actualPrice(r, cost), web.actualPrice(r, cost), `${name}：实际售价 @${cost}`);
    }
  }
  same(`${name} append`, m.appendInterval(rules), web.appendInterval(rules) as AnyResult);
  same(`${name} convert`, m.convertLegacy(rules), web.convertLegacy(rules) as AnyResult);
  same(`${name} edge min`, m.setEdge(rules, "min", 0.5), web.setEdge(rules, "min", 0.5) as AnyResult);
  same(`${name} edge max`, m.setEdge(rules, "max", 1234), web.setEdge(rules, "max", 1234) as AnyResult);
  for (let i = 0; i < n; i++) {
    same(`${name} split ${i}`, m.addInterval(rules, i), web.addInterval(rules, i) as AnyResult);
    same(`${name} remove ${i}`, m.removeInterval(rules, i), web.removeInterval(rules, i) as AnyResult);
    for (const kind of ["fixed", "curve"] as const) same(`${name} kind ${i} ${kind}`, m.setKind(rules, i, kind, flags), web.setKind(rules, i, kind, flags) as AnyResult);
    for (const rate of [1.5, 2, 2.75, 3.2, 5, 6]) {
      same(`${name} rate ${i} ${rate}`, m.setRate(rules, i, rate, flags), web.setRate(rules, i, rate, flags) as AnyResult);
      for (const which of ["start", "end"] as const)
        same(`${name} endpointRate ${i} ${which} ${rate}`, m.setEndpointRate(rules, i, which, rate, flags), web.setEndpointRate(rules, i, which, rate, flags) as AnyResult);
    }
    for (const shape of ["Linear", "ArcUp", "ArcDown"] as const) same(`${name} shape ${i} ${shape}`, m.setShape(rules, i, shape), web.setShape(rules, i, shape) as AnyResult);
    for (const pct of [0, 40, 100]) same(`${name} bend ${i} ${pct}`, m.setBend(rules, i, pct), web.setBend(rules, i, pct) as AnyResult);
    if (i < n - 1) {
      const at = rules[i].maxPrice;
      for (const cost of [at * 0.8, at * 1.1, rules[i].minPrice, Number.NaN])
        same(`${name} boundary ${i} ${cost}`, m.setBoundary(rules, i, cost, flags), web.setBoundary(rules, i, cost, flags) as AnyResult);
      same(`${name} break ${i}`, m.breakLink(rules, i), web.breakLink(rules, i) as AnyResult);
      for (const driver of ["left", "right"] as const) same(`${name} relink ${i} ${driver}`, m.relink(rules, i, driver), web.relink(rules, i, driver) as AnyResult);
    }
  }
}

// —— 旧版规则转换（线上真实形态：下限 0 的阶梯 / 线性 / 指数） ——
const legacy = (min: number, max: number, sr: number, er: number, algorithm: string) =>
  ({ minPrice: min, maxPrice: max, startRate: sr, endRate: er, algorithm, startRetailPrice: null, endRetailPrice: null, curveBend: null }) as Rule;
const legacySets: Rule[][] = [
  [legacy(0, 5, 2.625, 2.625, "Step"), legacy(5, 20, 2.625, 2, "Linear")],
  [legacy(0, 2.5, 3.5, 3.5, "Step"), legacy(2.5, 10, 3.5, 2.8, "Step"), legacy(10, 9999, 2.5, 2.5, "Step")],
  [legacy(0, 5, 2.5, 2.5, "Step"), legacy(5, 20, 2.5, 2.3, "Linear"), legacy(20, 50, 2.3, 2, "Linear"), legacy(50, 9999, 2, 2, "Linear")],
  [legacy(0, 99, 3, 2, "Exponential")],
];
for (const [i, set] of legacySets.entries()) {
  const a = m.convertLegacy(set);
  const b = web.convertLegacy(set) as AnyResult & { notes: string[] };
  same(`legacy ${i}`, a, b);
  assert.equal(a.notes.length, b.notes.length, `legacy ${i}：说明条数不一致`);
}

console.log("pricing-strategies parity.test.ts: ok");
}

void run().catch((error: unknown) => {
  console.error(error);
  process.exit(1);
});
