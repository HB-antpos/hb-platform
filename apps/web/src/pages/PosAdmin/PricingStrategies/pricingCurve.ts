import type { PricingStrategyRuleDto } from '../../../types/pricingStrategy'

export interface PriceNode { cost: number; price: number }
export const safeBend = (n: number) => Math.trunc(n * 1e6) / 1e6
export const roundMoney = (n: number) => Math.round((n + Number.EPSILON) * 100) / 100
export function tailFloor(value: number): number {
  if (value >= 2 && value < 2.5) return 2
  if (value >= 1 && value < 1.5) return 1
  const n = Math.floor(value), cents = Math.round((value - n) * 1e8) / 1e8
  return cents >= .99 ? n + .99 : cents >= .5 ? n + .5 : n - .01
}
export function tailCeil(value: number): number {
  if (value > .99 && value <= 1) return 1
  if (value > 1.99 && value <= 2) return 2
  const n = Math.floor(value), cents = Math.round((value - n) * 1e8) / 1e8
  return cents <= .5 ? n + .5 : cents <= .99 ? n + .99 : n + 1.5
}
export function boundedPrice(cost: number, raw: number): number {
  const lo = tailCeil(1.5 * cost), hi = tailFloor(5 * cost)
  if (lo > hi) return NaN
  // 仅消除二进制浮点在尾数边界的误差，保留真实的分以下价格变化。
  const integer = Math.floor(raw)
  for (const boundary of [integer, integer + .5, integer + .99, integer + 1]) {
    if (Math.abs(raw - boundary) < 1e-10) { raw = boundary; break }
  }
  const n = Math.floor(raw), fraction = raw - n
  const adjusted = raw <= .5 ? .5 : raw === 1 || raw === 2 ? raw : fraction === 0 ? raw - .01 : fraction <= .5 ? n + .5 : n + .99
  return roundMoney(Math.max(lo, Math.min(hi, adjusted)))
}
export function finalPrice(cost: number, raw: number): number {
  const bounded = boundedPrice(cost, raw)
  // 自动价格最终仅将 0.99 和 1.99 归一到 1 和 2；旧尾数分档及 1.5x/5x 限幅保持不变。
  // 因此上限或下限卡出这两档时也允许多 1 分钱，其他尾数仍原样返回。
  return bounded === .99 ? 1 : bounded === 1.99 ? 2 : bounded
}
export function rawPrice(rule: PricingStrategyRuleDto, cost: number): number {
  const a = rule.startRetailPrice ?? rule.minPrice * rule.startRate
  const b = rule.endRetailPrice ?? rule.maxPrice * rule.endRate
  if (cost === rule.minPrice) return a
  if (cost === rule.maxPrice) return b
  const t = (cost - rule.minPrice) / (rule.maxPrice - rule.minPrice)
  const bend = rule.algorithm === 'Linear' ? 0 : (rule.curveBend ?? 0)
  return a + (b - a) * t + (b - a) * bend * t * (1 - t)
}
export function curveError(rules: PricingStrategyRuleDto[]): string | null {
  if (!rules.length) return '至少需要两个定价节点'
  for (let i = 0; i < rules.length; i++) {
    const r = rules[i], a = r.minPrice, b = r.maxPrice
    const pa = r.startRetailPrice ?? a * r.startRate, pb = r.endRetailPrice ?? b * r.endRate
    if (![a, b, pa, pb, r.curveBend ?? 0].every(Number.isFinite) || a < .1 || b <= a) return '成本从 0.10 元起，节点成本必须严格递增'
    if (pa < 1.5 * a - 1e-8 || pa > 5 * a + 1e-8 || pb < 1.5 * b - 1e-8 || pb > 5 * b + 1e-8) return '成率必须在 1.5～5 之间'
    if (pb < pa || pb / b > pa / a + 1e-8) return '成本增加时，售价不能下降，理论成率不能上升'
    if (i && (rules[i - 1].maxPrice !== a || Math.abs((rules[i - 1].endRetailPrice ?? rules[i - 1].maxPrice * rules[i - 1].endRate) - pa) > 1e-8)) return '相邻区间必须共用同一个节点'
    const d = pb - pa, B = d * (r.algorithm === 'Linear' ? 0 : r.curveBend ?? 0)
    if (Math.abs(r.curveBend ?? 0) > 1 || (r.algorithm === 'ArcUp' && B < 0) || (r.algorithm === 'ArcDown' && B > 0)) return '弯曲方向或弧度无效'
    // 二次曲线的斜率及 xP′−P 在区间端点校验，确保全区间单调。
    const slopeA = (d + B) / (b - a), slopeB = (d - B) / (b - a)
    if (Math.min(slopeA, slopeB) < -1e-8 || Math.max(a * slopeA - pa, b * slopeB - pb) > 1e-8) return '弧度过大，会使售价下降或理论成率上升，请减小弧度'
  }
  return null
}
export function nodesFromRules(rules: PricingStrategyRuleDto[]): PriceNode[] {
  return rules.length ? [{ cost: rules[0].minPrice, price: rules[0].startRetailPrice ?? rules[0].minPrice * rules[0].startRate }, ...rules.map(r => ({ cost: r.maxPrice, price: r.endRetailPrice ?? r.maxPrice * r.endRate }))] : []
}
export function rulesFromNodes(nodes: PriceNode[], old: PricingStrategyRuleDto[] = []): PricingStrategyRuleDto[] {
  return nodes.slice(0, -1).map((a, i) => {
    const b = nodes[i + 1], previous = old[i]
    return { minPrice: a.cost, maxPrice: b.cost, startRate: Math.round(a.price / a.cost * 1e4) / 1e4, endRate: Math.round(b.price / b.cost * 1e4) / 1e4, startRetailPrice: a.price, endRetailPrice: b.price, algorithm: previous?.algorithm === 'ArcUp' || previous?.algorithm === 'ArcDown' ? previous.algorithm : 'Linear', curveBend: safeBend(previous?.curveBend ?? 0) }
  })
}

// ---- 固定成率（Fixed）：理论价 = 进货价 × 成率；实际售价与其他区间一样按 .50/.99 尾数调整 ----
export const FIXED_MIN_COST = 0.1
export const FIXED_MAX_COST = 99999
export const FIXED_RATE_MIN = 1.5
export const FIXED_RATE_MAX = 5

export function fixedRateError(rate: unknown): string | null {
  if (typeof rate !== 'number' || !Number.isFinite(rate)) return '请填写成率'
  if (rate < FIXED_RATE_MIN || rate > FIXED_RATE_MAX) return `成率必须在 ${FIXED_RATE_MIN}～${FIXED_RATE_MAX} 之间`
  return null
}

/** 固定成率只生成一条覆盖全部进货价的区间，起止成率相同，不带节点零售价和弧度。 */
export function fixedRules(rate: number): PricingStrategyRuleDto[] {
  const fixed = Math.round(rate * 1e4) / 1e4
  return [{ minPrice: FIXED_MIN_COST, maxPrice: FIXED_MAX_COST, startRate: fixed, endRate: fixed, startRetailPrice: null, endRetailPrice: null, curveBend: null, algorithm: 'Fixed' }]
}

export const isFixedRules = (rules: PricingStrategyRuleDto[]) => rules.length === 1 && rules[0].algorithm === 'Fixed'

/** 成率显示：保留 4 位后去掉末尾多余的 0，但至少保留 2 位小数（2.5 → 2.50，2.5125 → 2.5125）。 */
export const fmtRate = (rate: number) => rate.toFixed(4).replace(/0{1,2}$/, '')

export const defaultCurve = () => rulesFromNodes([{ cost: 1, price: 4.5 }, { cost: 50, price: 174.99 }, { cost: 200, price: 599.99 }, { cost: 999, price: 1997.99 }])
