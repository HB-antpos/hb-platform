import {
  FIXED_MAX_COST,
  FIXED_RATE_MAX,
  FIXED_RATE_MIN,
  boundedPrice,
  finalPrice,
  rawPrice,
  roundMoney,
  safeBend,
  tailCeil,
  tailFloor,
} from "./pricing-curve";
import type { PricingStrategyRule as Rule } from "./types";

/**
 * 区间式定价的纯逻辑，逐个函数对应 Web pricingIntervals.ts（后端 PricingCurveMath.Validate 同口径）：
 * - 区间首尾相接；进货价等于交界值时属于上一个区间（上限含、下限不含）。
 * - 每个区间内部要求成本增加时售价不降、理论成率不升；交界允许价差（阶梯定价，保存前二次确认）。
 * - 最多 10 个区间。
 * 与 Web 唯一的差别：错误与转换说明返回「错误码 + 参数」，由界面按中英文翻译（Web 直接返回中文）。
 * parity.test.ts 用同一批输入比对两边的区间结果，改动时两边要一起改。
 */
export type { Rule };
export const MAX_INTERVALS = 10;
/** 「不限」在库里存为该上限（与固定成率一致）。 */
export const UNBOUNDED_COST = FIXED_MAX_COST;

export type PricingErrorCode =
  | "noIntervals"
  | "tooMany"
  | "notNumber"
  | "rangeInvalid"
  | "notContiguous"
  | "rateRange"
  | "fixedRatesDiffer"
  | "fixedHasCurve"
  | "unsupported"
  | "endpointRateRange"
  | "monotonic"
  | "startTail"
  | "endTail"
  | "bendInvalid"
  | "bendTooLarge"
  | "noTailAtBoundary"
  | "boundaryRequired"
  | "boundaryOutOfRange"
  | "costRequired"
  | "minTooHigh"
  | "maxTooLow"
  | "noTailAtCost"
  | "rateRequired"
  | "rateOutOfRange"
  | "notFixed"
  | "unboundedCurve"
  | "noTailInInterval"
  | "fixedNoEndpoint"
  | "endpointLocked"
  | "noTailForNeighbors"
  | "fixedUseRate"
  | "fixedNoShape"
  | "tooNarrow"
  | "noTailAtMid"
  | "unboundedAppend"
  | "nearMax"
  | "noTailForNew"
  | "keepOne"
  | "convertEmpty"
  | "convertBounds"
  | "convertNoTail";

export interface PricingIssue {
  code: PricingErrorCode;
  params?: Record<string, string | number>;
}

export type Result = { rules: Rule[]; error?: undefined } | { rules?: undefined; error: PricingIssue };
const ok = (rules: Rule[]): Result => ({ rules });
const fail = (code: PricingErrorCode, params?: Record<string, string | number>): Result => ({ error: { code, params } });
const issue = (code: PricingErrorCode, params?: Record<string, string | number>): PricingIssue => ({ code, params });

const r4 = (n: number) => Math.round(n * 1e4) / 1e4;
/** 判断「两个价格相同」的容差（分）。 */
const CENT = 0.005 + 1e-9;
const EPS = 1e-8;

export const isFixed = (r: Rule) => r.algorithm === "Fixed";
export const isCurveRule = (r: Rule) =>
  ["Linear", "ArcUp", "ArcDown"].includes(r.algorithm) && r.startRetailPrice != null && r.endRetailPrice != null;
/** 区间编辑器只能编辑「固定 / 曲线」区间；含旧版 Step、Exponential 或没有节点价的规则要先转换。 */
export const isEditable = (rules: Rule[]) => rules.length > 0 && rules.every((r) => isFixed(r) || isCurveRule(r));

export const startPrice = (r: Rule) => (isFixed(r) ? roundMoney(r.minPrice * r.startRate) : (r.startRetailPrice ?? r.minPrice * r.startRate));
export const endPrice = (r: Rule) => (isFixed(r) ? roundMoney(r.maxPrice * r.endRate) : (r.endRetailPrice ?? r.maxPrice * r.endRate));

export const makeFixed = (min: number, max: number, rate: number): Rule => ({
  minPrice: min,
  maxPrice: max,
  startRate: r4(rate),
  endRate: r4(rate),
  startRetailPrice: null,
  endRetailPrice: null,
  curveBend: null,
  algorithm: "Fixed",
});
export type CurveShape = "Linear" | "ArcUp" | "ArcDown";
export const makeCurve = (min: number, max: number, p0: number, p1: number, algorithm: CurveShape = "Linear", bend = 0): Rule => ({
  minPrice: min,
  maxPrice: max,
  startRate: r4(p0 / min),
  endRate: r4(p1 / max),
  startRetailPrice: p0,
  endRetailPrice: p1,
  curveBend: algorithm === "Linear" ? 0 : safeBend(bend),
  algorithm,
});
const withCurveEnds = (r: Rule, p0: number, p1: number) => makeCurve(r.minPrice, r.maxPrice, p0, p1, r.algorithm as CurveShape, r.curveBend ?? 0);

/** 合法尾数：.50 / .99 或整数 1、2（且不低于 0.50）。 */
export function isLegalTail(v: number): boolean {
  const c = Math.round(v * 100);
  if (Math.abs(v * 100 - c) > 1e-6 || c < 50) return false;
  return c === 100 || c === 200 || c % 100 === 50 || c % 100 === 99;
}

/** 理论价（固定成率恰好是 进货价 × 成率）。 */
export const theoryPrice = (r: Rule, cost: number) => rawPrice(r, cost);
/** 实际售价：所有区间（含固定成率）都按尾数规则调整。 */
export const actualPrice = (r: Rule, cost: number) => finalPrice(cost, rawPrice(r, cost));

/** 命中的区间下标：进货价等于交界值时属于上一个区间。 */
export function intervalAt(rules: Rule[], cost: number): number {
  if (!rules.length || cost < rules[0].minPrice - EPS) return -1;
  const i = rules.findIndex((r) => cost <= r.maxPrice + EPS);
  return i === -1 ? rules.length - 1 : i;
}

/** 曲线端点是否「跟随」相邻固定区间的交界价（此时不要求合法尾数）。 */
function followsFixed(rules: Rule[], i: number, which: "start" | "end"): boolean {
  const r = rules[i];
  if (which === "start") {
    const p = rules[i - 1];
    return !!p && isFixed(p) && Math.abs(startPrice(r) - endPrice(p)) <= CENT;
  }
  const n = rules[i + 1];
  return !!n && isFixed(n) && Math.abs(endPrice(r) - startPrice(n)) <= CENT;
}

/** 曲线区间的最大弧度（0～1），超过会使区间内售价下降或成率上升。 */
export function arcLimit(r: Rule): number {
  const d = (r.endRetailPrice ?? 0) - (r.startRetailPrice ?? 0);
  const c = r.minPrice * (r.endRetailPrice ?? 0) - r.maxPrice * (r.startRetailPrice ?? 0);
  return d > 0 ? Math.max(0, Math.min(1, r.algorithm === "ArcDown" ? -c / r.maxPrice / d : -c / r.minPrice / d)) : 0;
}

/** 与后端 Validate 同口径的校验；返回第一条问题（n 为从 1 起的区间序号）。 */
export function intervalsError(rules: Rule[]): PricingIssue | null {
  if (!rules.length) return issue("noIntervals");
  if (rules.length > MAX_INTERVALS) return issue("tooMany", { max: MAX_INTERVALS });
  for (let i = 0; i < rules.length; i++) {
    const r = rules[i];
    const prev = rules[i - 1];
    const n = i + 1;
    const a = r.minPrice;
    const b = r.maxPrice;
    if (![a, b, r.startRate, r.endRate].every(Number.isFinite)) return issue("notNumber", { n });
    if (a < 0.1 - EPS || b <= a) return issue("rangeInvalid", { n });
    if (prev && prev.maxPrice !== a) return issue("notContiguous", { n });
    if (
      r.startRate < FIXED_RATE_MIN - EPS ||
      r.startRate > FIXED_RATE_MAX + EPS ||
      r.endRate < FIXED_RATE_MIN - EPS ||
      r.endRate > FIXED_RATE_MAX + EPS
    )
      return issue("rateRange", { n });
    if (isFixed(r)) {
      if (r.startRate !== r.endRate) return issue("fixedRatesDiffer", { n });
      if (r.startRetailPrice != null || r.endRetailPrice != null || (r.curveBend ?? 0) !== 0) return issue("fixedHasCurve", { n });
      continue;
    }
    if (!isCurveRule(r)) return issue("unsupported", { n });
    const pa = startPrice(r);
    const pb = endPrice(r);
    const d = pb - pa;
    const h = b - a;
    if (pa < 1.5 * a - EPS || pa > 5 * a + EPS || pb < 1.5 * b - EPS || pb > 5 * b + EPS) return issue("endpointRateRange", { n });
    if (d < -EPS || pa * b < pb * a - EPS) return issue("monotonic", { n });
    if (!followsFixed(rules, i, "start") && !isLegalTail(pa)) return issue("startTail", { n });
    if (!followsFixed(rules, i, "end") && !isLegalTail(pb)) return issue("endTail", { n });
    const bend = r.curveBend ?? 0;
    if (
      Math.abs(bend) > 1 ||
      (r.algorithm === "Linear" && bend !== 0) ||
      (r.algorithm === "ArcUp" && bend < 0) ||
      (r.algorithm === "ArcDown" && bend > 0)
    )
      return issue("bendInvalid", { n });
    const B = d * bend;
    const slopeA = (d + B) / h;
    const slopeB = (d - B) / h;
    if (Math.min(slopeA, slopeB) < -EPS || Math.max(a * slopeA - pa, b * slopeB - pb) > EPS) return issue("bendTooLarge", { n });
  }
  return null;
}

export interface PriceDrop {
  index: number;
  at: number;
  from: number;
  to: number;
}
/** 交界处「成本变大、售价反而降低」的位置（阶梯定价）；保存前需要用户二次确认。 */
export function priceDrops(rules: Rule[]): PriceDrop[] {
  const drops: PriceDrop[] = [];
  for (let i = 0; i < rules.length - 1; i++) {
    const from = endPrice(rules[i]);
    const to = startPrice(rules[i + 1]);
    if (to < from - 0.004) drops.push({ index: i, at: rules[i].maxPrice, from, to });
  }
  return drops;
}

// ---- 交界联动 ----
/** 交界两侧至少有一个曲线区间时才存在「联动」概念；两个固定区间相邻只有成率之分。 */
export const hasCurveSide = (rules: Rule[], i: number) => !isFixed(rules[i]) || !isFixed(rules[i + 1]);
export const isContinuous = (rules: Rule[], i: number) => Math.abs(endPrice(rules[i]) - startPrice(rules[i + 1])) <= CENT;
/** 每个交界是否联动：有曲线一侧、当前售价连续，并且用户没有主动断开（库里不存联动状态）。 */
export const linkedFlags = (rules: Rule[], broken: ReadonlySet<number>): boolean[] =>
  rules.slice(0, -1).map((_, i) => hasCurveSide(rules, i) && isContinuous(rules, i) && !broken.has(i));
export const shiftBrokenAfterAdd = (broken: ReadonlySet<number>, i: number) => new Set([...broken].map((b) => (b < i ? b : b + 1)));
export const shiftBrokenAfterRemove = (broken: ReadonlySet<number>, i: number) =>
  new Set([...broken].filter((b) => b !== i - 1 && b !== i).map((b) => (b < i - 1 ? b : b - 1)));

/** 交界售价由哪一侧决定：固定区间优先（价格 = 交界成本 × 成率），其次是 driver 指定的一侧。 */
function syncBoundary(rules: Rule[], i: number, driver: "left" | "right"): void {
  const left = rules[i];
  const right = rules[i + 1];
  const at = left.maxPrice;
  if (isFixed(left) && !isFixed(right)) rules[i + 1] = withCurveEnds(right, roundMoney(at * left.endRate), endPrice(right));
  else if (isFixed(right) && !isFixed(left)) rules[i] = withCurveEnds(left, startPrice(left), roundMoney(at * right.startRate));
  else if (!isFixed(left) && !isFixed(right)) {
    if (driver === "left") rules[i + 1] = withCurveEnds(right, endPrice(left), endPrice(right));
    else rules[i] = withCurveEnds(left, startPrice(left), startPrice(right));
  }
}

const finish = (rules: Rule[]): Result => {
  const error = intervalsError(rules);
  return error ? { error } : ok(rules);
};
const snap = (cost: number, price: number) => boundedPrice(cost, price);

/** 断开交界联动：曲线一侧不再跟随，端点必须是合法尾数，所以把非尾数的端点吸附到最近的合法尾数。 */
export function breakLink(rules: Rule[], i: number): Result {
  const next = rules.map((r) => ({ ...r }));
  const at = next[i].maxPrice;
  if (!isFixed(next[i])) {
    const p = snap(at, endPrice(next[i]));
    if (Number.isNaN(p)) return fail("noTailAtBoundary");
    next[i] = withCurveEnds(next[i], startPrice(next[i]), p);
  }
  if (!isFixed(next[i + 1])) {
    const p = snap(at, startPrice(next[i + 1]));
    if (Number.isNaN(p)) return fail("noTailAtBoundary");
    next[i + 1] = withCurveEnds(next[i + 1], p, endPrice(next[i + 1]));
  }
  return finish(next);
}

/** 重新联动：固定侧优先；两侧都是曲线时以 driver 指定的一侧为准。 */
export function relink(rules: Rule[], i: number, driver: "left" | "right"): Result {
  const next = rules.map((r) => ({ ...r }));
  syncBoundary(next, i, driver);
  return finish(next);
}

/** 修改交界成本：上一个区间的上限与下一个区间的下限同时变化。 */
export function setBoundary(rules: Rule[], i: number, cost: number, linked: boolean[]): Result {
  if (!Number.isFinite(cost)) return fail("boundaryRequired");
  const c = roundMoney(cost);
  const left = rules[i];
  const right = rules[i + 1];
  const oldAt = left.maxPrice;
  if (c <= left.minPrice + EPS || c >= right.maxPrice - EPS)
    return fail("boundaryOutOfRange", { min: left.minPrice.toFixed(2), max: right.maxPrice.toFixed(2) });
  const next = rules.map((r) => ({ ...r }));
  next[i] = { ...left, maxPrice: c };
  next[i + 1] = { ...right, minPrice: c };
  // 曲线端点保持原来的成率（而不是原来的售价），再吸附到新成本下的合法尾数；联动时再由固定侧或左侧决定交界售价。
  const fit = (r: Rule, which: "start" | "end"): number => snap(c, (c * (which === "start" ? startPrice(r) : endPrice(r))) / oldAt);
  let p0: number | undefined;
  let p1: number | undefined;
  if (!isFixed(next[i])) p1 = fit(next[i], "end");
  if (!isFixed(next[i + 1])) p0 = fit(next[i + 1], "start");
  if ((p1 !== undefined && Number.isNaN(p1)) || (p0 !== undefined && Number.isNaN(p0))) return fail("noTailAtCost");
  if (p1 !== undefined) next[i] = withCurveEnds(next[i], startPrice(next[i]), p1);
  if (p0 !== undefined) next[i + 1] = withCurveEnds(next[i + 1], p0, endPrice(next[i + 1]));
  if (linked[i]) syncBoundary(next, i, "left");
  return finish(next);
}

/** 修改整段的下限（仅第一个区间）或上限（仅最后一个区间）。 */
export function setEdge(rules: Rule[], edge: "min" | "max", cost: number): Result {
  if (!Number.isFinite(cost)) return fail("costRequired");
  const c = roundMoney(cost);
  const next = rules.map((r) => ({ ...r }));
  const idx = edge === "min" ? 0 : rules.length - 1;
  const r = next[idx];
  if (edge === "min" ? c >= r.maxPrice - EPS : c <= r.minPrice + EPS) return fail(edge === "min" ? "minTooHigh" : "maxTooLow");
  if (isFixed(r)) next[idx] = { ...r, [edge === "min" ? "minPrice" : "maxPrice"]: c };
  else {
    const moved = { ...r, [edge === "min" ? "minPrice" : "maxPrice"]: c };
    // 同样保持端点成率。
    const p = snap(c, c * (edge === "min" ? startPrice(r) / r.minPrice : endPrice(r) / r.maxPrice));
    if (Number.isNaN(p)) return fail("noTailAtCost");
    next[idx] = edge === "min" ? withCurveEnds(moved, p, endPrice(r)) : withCurveEnds(moved, startPrice(r), p);
  }
  return finish(next);
}

const rateRangeParams = { min: FIXED_RATE_MIN, max: FIXED_RATE_MAX };

/** 修改固定成率；与其联动的曲线端点跟随 交界成本 × 成率。 */
export function setRate(rules: Rule[], i: number, rate: number, linked: boolean[]): Result {
  if (!Number.isFinite(rate)) return fail("rateRequired");
  if (rate < FIXED_RATE_MIN || rate > FIXED_RATE_MAX) return fail("rateOutOfRange", rateRangeParams);
  if (!isFixed(rules[i])) return fail("notFixed");
  const next = rules.map((r) => ({ ...r }));
  next[i] = makeFixed(next[i].minPrice, next[i].maxPrice, rate);
  if (i > 0 && linked[i - 1]) syncBoundary(next, i - 1, "right");
  if (i < next.length - 1 && linked[i]) syncBoundary(next, i, "left");
  return finish(next);
}

/** 切换区间类型。固定→曲线：端点吸附到合法尾数；曲线→固定：取两端成率的平均值。 */
export function setKind(rules: Rule[], i: number, kind: "fixed" | "curve", linked: boolean[]): Result {
  const r = rules[i];
  if ((kind === "fixed") === isFixed(r)) return ok(rules);
  const next = rules.map((x) => ({ ...x }));
  if (kind === "fixed") {
    const rate = Math.min(FIXED_RATE_MAX, Math.max(FIXED_RATE_MIN, r4((startPrice(r) / r.minPrice + endPrice(r) / r.maxPrice) / 2)));
    next[i] = makeFixed(r.minPrice, r.maxPrice, rate);
  } else {
    if (r.maxPrice >= UNBOUNDED_COST) return fail("unboundedCurve");
    const p0 = snap(r.minPrice, startPrice(r));
    const p1 = snap(r.maxPrice, endPrice(r));
    if (Number.isNaN(p0) || Number.isNaN(p1)) return fail("noTailInInterval");
    next[i] = makeCurve(r.minPrice, r.maxPrice, p0, p1);
  }
  // 原本联动或售价连续的交界重新对齐：固定侧优先；两侧都是曲线时以被切换的这个区间为准。
  if (i > 0 && (linked[i - 1] || isContinuous(rules, i - 1))) syncBoundary(next, i - 1, "right");
  if (i < next.length - 1 && (linked[i] || isContinuous(rules, i))) syncBoundary(next, i, "left");
  return finish(next);
}

/** 修改曲线区间某个端点的售价；与曲线邻居联动时两侧同步，与固定邻居联动时需先断开。 */
export function setEndpoint(rules: Rule[], i: number, which: "start" | "end", wanted: number, linked: boolean[]): Result {
  const r = rules[i];
  if (isFixed(r)) return fail("fixedNoEndpoint");
  if (!Number.isFinite(wanted)) return fail("rateRequired");
  const bIdx = which === "start" ? i - 1 : i;
  const isLinked = bIdx >= 0 && bIdx < linked.length && linked[bIdx];
  const nb = which === "start" ? rules[i - 1] : rules[i + 1];
  if (isLinked && nb && isFixed(nb)) return fail("endpointLocked");
  const cost = which === "start" ? r.minPrice : r.maxPrice;
  const other = which === "start" ? endPrice(r) : startPrice(r);
  let lo = 1.5 * cost;
  let hi = 5 * cost;
  if (which === "start") {
    lo = Math.max(lo, (other * r.minPrice) / r.maxPrice);
    hi = Math.min(hi, other);
  } else {
    lo = Math.max(lo, other);
    hi = Math.min(hi, (other * r.maxPrice) / r.minPrice);
  }
  if (isLinked && nb && !isFixed(nb)) {
    // 邻居区间的端点也是同一个价格，同样要满足它自己的区间内约束。
    if (which === "start") {
      lo = Math.max(lo, startPrice(nb));
      hi = Math.min(hi, (startPrice(nb) * nb.maxPrice) / nb.minPrice);
    } else {
      lo = Math.max(lo, (endPrice(nb) * nb.minPrice) / nb.maxPrice);
      hi = Math.min(hi, endPrice(nb));
    }
  }
  const bottom = tailCeil(lo);
  const top = tailFloor(hi);
  if (bottom > top + EPS) return fail("noTailForNeighbors");
  const bounded = snap(cost, wanted);
  const price = roundMoney(Math.min(top, Math.max(bottom, Number.isNaN(bounded) ? bottom : bounded)));
  const next = rules.map((x) => ({ ...x }));
  next[i] = which === "start" ? withCurveEnds(r, price, endPrice(r)) : withCurveEnds(r, startPrice(r), price);
  if (isLinked && nb && !isFixed(nb)) {
    const j = which === "start" ? i - 1 : i + 1;
    next[j] = which === "start" ? withCurveEnds(nb, startPrice(nb), price) : withCurveEnds(nb, price, endPrice(nb));
  }
  return finish(next);
}

/**
 * 按成率设置曲线端点：目标价 = 端点成本 × 成率，吸附到合法尾数后作为零售价，页面显示的是算出来的零售价。
 * 保存用户输入的成率（价格取整后的实际成率可能略有出入）；若相邻约束迫使价格偏离太多，则保存实际成率。
 */
export function setEndpointRate(rules: Rule[], i: number, which: "start" | "end", rate: number, linked: boolean[]): Result {
  if (!Number.isFinite(rate)) return fail("rateRequired");
  if (rate < FIXED_RATE_MIN || rate > FIXED_RATE_MAX) return fail("rateOutOfRange", rateRangeParams);
  const r = rules[i];
  if (isFixed(r)) return fail("fixedUseRate");
  const cost = which === "start" ? r.minPrice : r.maxPrice;
  const moved = setEndpoint(rules, i, which, cost * rate, linked);
  if (!moved.rules) return moved;
  const next = moved.rules.map((x) => ({ ...x }));
  const price = which === "start" ? startPrice(next[i]) : endPrice(next[i]);
  // 合法尾数相邻档最多差 0.51 元，超出这个范围说明价格是被相邻约束压过来的，此时记录实际成率。
  const stored = Math.abs(price / cost - rate) <= 0.51 / cost + 1e-6 ? r4(rate) : r4(price / cost);
  next[i] = which === "start" ? { ...next[i], startRate: stored } : { ...next[i], endRate: stored };
  const bIdx = which === "start" ? i - 1 : i;
  if (bIdx >= 0 && bIdx < linked.length && linked[bIdx]) {
    // 联动的曲线邻居共用同一个交界，成率一并记录
    const j = which === "start" ? i - 1 : i + 1;
    if (next[j] && !isFixed(next[j])) next[j] = which === "start" ? { ...next[j], endRate: stored } : { ...next[j], startRate: stored };
  }
  return finish(next);
}

export function setShape(rules: Rule[], i: number, algorithm: CurveShape): Result {
  const r = rules[i];
  if (isFixed(r)) return fail("fixedNoShape");
  const next = rules.map((x) => ({ ...x }));
  next[i] = makeCurve(r.minPrice, r.maxPrice, startPrice(r), endPrice(r), algorithm, 0);
  return finish(next);
}

/** percent：0～100，占当前区间最大弧度的比例。 */
export function setBend(rules: Rule[], i: number, percent: number): Result {
  const r = rules[i];
  const limit = arcLimit(r);
  if (isFixed(r) || r.algorithm === "Linear") return ok(rules);
  const next = rules.map((x) => ({ ...x }));
  next[i] = { ...r, curveBend: safeBend(((r.algorithm === "ArcDown" ? -1 : 1) * limit * percent) / 100) };
  return finish(next);
}

/** 当前弧度占最大弧度的百分比（0～100），用于弧度滑块的位置。 */
export const bendPercent = (r: Rule) => {
  const limit = arcLimit(r);
  return limit ? (Math.abs(r.curveBend ?? 0) / limit) * 100 : 0;
};

/** 在区间中点拆成两个区间；固定保持同一成率，曲线在中点取当前曲线价并重置为直线。 */
export function addInterval(rules: Rule[], i: number): Result {
  if (rules.length >= MAX_INTERVALS) return fail("tooMany", { max: MAX_INTERVALS });
  const r = rules[i];
  // 上限为「不限」时取下限的两倍作为拆分点，避免落在几万这种没有意义的位置。
  const mid =
    r.maxPrice >= UNBOUNDED_COST ? roundMoney(Math.max(r.minPrice * 2, r.minPrice + 10)) : roundMoney((r.minPrice + r.maxPrice) / 2);
  if (mid <= r.minPrice + EPS || mid >= r.maxPrice - EPS) return fail("tooNarrow");
  const next = rules.map((x) => ({ ...x }));
  if (isFixed(r)) next.splice(i, 1, makeFixed(r.minPrice, mid, r.startRate), makeFixed(mid, r.maxPrice, r.startRate));
  else {
    const p = snap(mid, rawPrice(r, mid));
    if (Number.isNaN(p)) return fail("noTailAtMid");
    next.splice(i, 1, makeCurve(r.minPrice, mid, startPrice(r), p), makeCurve(mid, r.maxPrice, p, endPrice(r)));
  }
  return finish(next);
}

/**
 * 在末尾追加一个新区间：从最后一个区间的上限接着往上（上限翻倍），而不是把已有区间从中间切开。
 * 固定成率沿用最后一个区间的成率；曲线从上一区间的终点售价接着走，终点保持同样的成率（再限制成率不上升）。
 */
export function appendInterval(rules: Rule[]): Result {
  if (rules.length >= MAX_INTERVALS) return fail("tooMany", { max: MAX_INTERVALS });
  const last = rules[rules.length - 1];
  if (last.maxPrice >= UNBOUNDED_COST) return fail("unboundedAppend");
  const min = last.maxPrice;
  const doubled = roundMoney(min * 2);
  const next = rules.map((x) => ({ ...x }));
  if (isFixed(last)) {
    next.push(makeFixed(min, Math.min(doubled, UNBOUNDED_COST), last.startRate));
    return finish(next);
  }
  const max = Math.min(doubled, UNBOUNDED_COST - 1);
  if (max <= min + EPS) return fail("nearMax");
  const p0 = endPrice(last);
  const p1raw = snap(max, (max * p0) / min);
  if (Number.isNaN(p1raw)) return fail("noTailForNew");
  next.push(makeCurve(min, max, p0, Math.min(p1raw, tailFloor((p0 * max) / min + 1e-9))));
  return finish(next);
}

/** 删除区间：并入相邻区间（优先并入上一个）。 */
export function removeInterval(rules: Rule[], i: number): Result {
  if (rules.length <= 1) return fail("keepOne");
  const r = rules[i];
  const next = rules.map((x) => ({ ...x }));
  if (i > 0) {
    const prev = next[i - 1];
    if (isFixed(prev)) next[i - 1] = makeFixed(prev.minPrice, r.maxPrice, prev.startRate);
    else {
      const p = snap(r.maxPrice, endPrice(r));
      next[i - 1] = Number.isNaN(p) ? prev : makeCurve(prev.minPrice, r.maxPrice, startPrice(prev), p);
    }
  } else {
    const nxt = next[1];
    if (isFixed(nxt)) next[1] = makeFixed(r.minPrice, nxt.maxPrice, nxt.startRate);
    else {
      const p = snap(r.minPrice, startPrice(r));
      next[1] = Number.isNaN(p) ? nxt : makeCurve(r.minPrice, nxt.maxPrice, p, endPrice(nxt));
    }
  }
  next.splice(i, 1);
  return finish(next);
}

// ---- 旧版区间规则 → 区间编辑器 ----
export type ConversionNoteCode = "raisedMin" | "stepToFixed" | "stepToFixedEndIgnored" | "expToCurve" | "drops";
export interface ConversionNote {
  code: ConversionNoteCode;
  params: Record<string, string | number>;
}
export type Conversion =
  | { rules: Rule[]; notes: ConversionNote[]; error?: undefined }
  | { rules?: undefined; notes: ConversionNote[]; error: PricingIssue };

/**
 * 把旧版区间规则（阶梯 Step、指数 Exponential、没有节点售价的 Linear）转换成「固定 / 曲线」区间，规则与 Web convertLegacy 相同：
 * - 阶梯 → 固定成率（线上对阶梯只按起始成率计价）。
 * - 线性 / 指数 → 曲线：两端售价 = 端点成本 × 端点成率，再吸附到合法尾数；指数的弯曲形状会变成直线。
 * - 第一个区间下限不足 0.10 时抬到 0.10。相邻区间不要求售价连续，保存时若有回落会二次确认。
 * 转换只发生在编辑页里，保存之前线上策略不变。
 */
export function convertLegacy(source: Rule[]): Conversion {
  const notes: ConversionNote[] = [];
  if (!source.length) return { error: issue("convertEmpty"), notes };
  const sorted = [...source].sort((a, b) => a.minPrice - b.minPrice);
  const out: Rule[] = [];
  for (let i = 0; i < sorted.length; i++) {
    const r = sorted[i];
    const n = i + 1;
    const min = i === 0 && r.minPrice < 0.1 ? 0.1 : r.minPrice;
    const max = r.maxPrice;
    if (i === 0 && r.minPrice < 0.1) notes.push({ code: "raisedMin", params: { n, from: r.minPrice.toFixed(2) } });
    if (isFixed(r) || isCurveRule(r)) {
      out.push({ ...r, minPrice: min });
      continue;
    }
    if (!Number.isFinite(min) || !Number.isFinite(max) || max <= min) return { error: issue("convertBounds", { n }), notes };
    if (r.algorithm === "Step") {
      // 线上对阶梯只按起始成率计价（结束成率不生效），转换要和线上行为一致。
      out.push(makeFixed(min, max, r.startRate));
      notes.push(
        r.endRate !== r.startRate
          ? { code: "stepToFixedEndIgnored", params: { n, rate: r.startRate, end: r.endRate } }
          : { code: "stepToFixed", params: { n, rate: r.startRate } },
      );
      continue;
    }
    // 起点取最近的合法尾数；终点再限制在「不超过起点成率」的合法尾数内，避免两端各自吸附后成率反而上升。
    const p0 = snap(min, min * r.startRate);
    const p1raw = snap(max, max * r.endRate);
    if (Number.isNaN(p0) || Number.isNaN(p1raw)) return { error: issue("convertNoTail", { n }), notes };
    const p1 = Math.min(p1raw, tailFloor((p0 * max) / min + 1e-9));
    out.push(makeCurve(min, max, p0, p1));
    if (r.algorithm === "Exponential") notes.push({ code: "expToCurve", params: { n } });
  }
  const error = intervalsError(out);
  if (error) return { error, notes };
  const drops = priceDrops(out).length;
  if (drops) notes.push({ code: "drops", params: { count: drops } });
  return { rules: out, notes };
}

// ---- 图表范围（只读图，不做拖动） ----
export function chartRange(rules: Rule[]) {
  const first = rules[0].minPrice;
  const last = rules[rules.length - 1];
  const finiteBoundary = rules.length > 1 ? rules[rules.length - 2].maxPrice : first;
  // 最后一个区间不限上限时，图上只展示到「最后一个交界的两倍」，避免被 99999 压扁。
  const end = last.maxPrice >= UNBOUNDED_COST ? Math.max(finiteBoundary * 2, first * 10, 100) : last.maxPrice;
  return { first, end };
}

/** 可见范围内的最高售价，用来决定纵轴。 */
export function visibleMaxPrice(rules: Rule[], end: number): number {
  let top = 0;
  for (const r of rules) {
    if (r.minPrice >= end) continue;
    top = Math.max(top, theoryPrice(r, Math.min(r.maxPrice, end)), actualPrice(r, Math.min(r.maxPrice, end)));
  }
  return top;
}
