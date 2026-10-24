import type { PricingStrategyRule } from "./types";

/**
 * 尾数与曲线的基础换算，逐行对应 Web apps/web/src/pages/PosAdmin/PricingStrategies/pricingCurve.ts
 * （后端 PricingCurveMath.AdjustTail 同口径）。两端没有共享包，parity.test.ts 用同一批输入逐个比对结果，改动时两边要一起改。
 */
export const safeBend = (n: number) => Math.trunc(n * 1e6) / 1e6;
export const roundMoney = (n: number) => Math.round((n + Number.EPSILON) * 100) / 100;

export function tailFloor(value: number): number {
  if (value >= 2 && value < 2.5) return 2;
  if (value >= 1 && value < 1.5) return 1;
  const n = Math.floor(value);
  const cents = Math.round((value - n) * 1e8) / 1e8;
  return cents >= 0.99 ? n + 0.99 : cents >= 0.5 ? n + 0.5 : n - 0.01;
}

export function tailCeil(value: number): number {
  if (value > 0.99 && value <= 1) return 1;
  if (value > 1.99 && value <= 2) return 2;
  const n = Math.floor(value);
  const cents = Math.round((value - n) * 1e8) / 1e8;
  return cents <= 0.5 ? n + 0.5 : cents <= 0.99 ? n + 0.99 : n + 1.5;
}

/** 按尾数规则取价并限制在 1.5～5 倍之间；该成本下没有合法尾数时返回 NaN。 */
export function boundedPrice(cost: number, raw: number): number {
  const lo = tailCeil(1.5 * cost);
  const hi = tailFloor(5 * cost);
  if (lo > hi) return Number.NaN;
  // 仅消除二进制浮点在尾数边界的误差，保留真实的分以下价格变化。
  const integer = Math.floor(raw);
  for (const boundary of [integer, integer + 0.5, integer + 0.99, integer + 1]) {
    if (Math.abs(raw - boundary) < 1e-10) {
      raw = boundary;
      break;
    }
  }
  const n = Math.floor(raw);
  const fraction = raw - n;
  const adjusted =
    raw <= 0.5 ? 0.5 : raw === 1 || raw === 2 ? raw : fraction === 0 ? raw - 0.01 : fraction <= 0.5 ? n + 0.5 : n + 0.99;
  return roundMoney(Math.max(lo, Math.min(hi, adjusted)));
}

/** 实际售价：小数进位到 .50 / .99，整数取 .99，0.99 / 1.99 归一到 1 / 2。 */
export function finalPrice(cost: number, raw: number): number {
  const bounded = boundedPrice(cost, raw);
  return bounded === 0.99 ? 1 : bounded === 1.99 ? 2 : bounded;
}

/** 理论价：固定成率 = 成本 × 成率；曲线按两端售价线性插值再叠加弧度。 */
export function rawPrice(rule: PricingStrategyRule, cost: number): number {
  const a = rule.startRetailPrice ?? rule.minPrice * rule.startRate;
  const b = rule.endRetailPrice ?? rule.maxPrice * rule.endRate;
  if (cost === rule.minPrice) return a;
  if (cost === rule.maxPrice) return b;
  const t = (cost - rule.minPrice) / (rule.maxPrice - rule.minPrice);
  const bend = rule.algorithm === "Linear" ? 0 : (rule.curveBend ?? 0);
  return a + (b - a) * t + (b - a) * bend * t * (1 - t);
}

export interface PriceNode {
  cost: number;
  price: number;
}

export function rulesFromNodes(nodes: PriceNode[]): PricingStrategyRule[] {
  return nodes.slice(0, -1).map((a, i) => {
    const b = nodes[i + 1];
    return {
      minPrice: a.cost,
      maxPrice: b.cost,
      startRate: Math.round((a.price / a.cost) * 1e4) / 1e4,
      endRate: Math.round((b.price / b.cost) * 1e4) / 1e4,
      startRetailPrice: a.price,
      endRetailPrice: b.price,
      algorithm: "Linear",
      curveBend: 0,
    };
  });
}

export const FIXED_MIN_COST = 0.1;
export const FIXED_MAX_COST = 99999;
export const FIXED_RATE_MIN = 1.5;
export const FIXED_RATE_MAX = 5;

/** 整段固定成率：一条覆盖全部进货价的区间，起止成率相同，不带节点零售价和弧度。 */
export function fixedRules(rate: number): PricingStrategyRule[] {
  const fixed = Math.round(rate * 1e4) / 1e4;
  return [
    {
      minPrice: FIXED_MIN_COST,
      maxPrice: FIXED_MAX_COST,
      startRate: fixed,
      endRate: fixed,
      startRetailPrice: null,
      endRetailPrice: null,
      curveBend: null,
      algorithm: "Fixed",
    },
  ];
}

/** 成率显示：保留 4 位后去掉末尾多余的 0，但至少保留 2 位小数（2.5 → 2.50，2.5125 → 2.5125）。 */
export const fmtRate = (rate: number) => rate.toFixed(4).replace(/0{1,2}$/, "");

/** 新建策略的默认曲线（与 Web 新建时相同）。 */
export const defaultCurve = () =>
  rulesFromNodes([
    { cost: 1, price: 4.5 },
    { cost: 50, price: 174.99 },
    { cost: 200, price: 599.99 },
    { cost: 999, price: 1997.99 },
  ]);
