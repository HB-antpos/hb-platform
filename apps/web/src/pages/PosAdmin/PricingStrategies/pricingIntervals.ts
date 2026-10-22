import type { PricingStrategyRuleDto as Rule } from '../../../types/pricingStrategy'
import { FIXED_MAX_COST, FIXED_RATE_MAX, FIXED_RATE_MIN, boundedPrice, finalPrice, rawPrice, roundMoney, safeBend, tailCeil, tailFloor } from './pricingCurve'

/**
 * 区间式定价的纯逻辑：每个区间（一条规则）自己设置进货价的上下限，并选择「固定成率」或「曲线」。
 * 与后端 PricingCurveMath.Validate 保持同一套口径：
 * - 区间首尾相接；进货价等于交界值时属于上一个区间（上限含、下限不含）。
 * - 每个区间内部要求成本增加时售价不降、理论成率不升；区间交界允许价差（阶梯定价，由界面标红并二次确认）。
 * - 最多 10 个区间。
 */
export type { Rule }
export const MAX_INTERVALS = 10
/** 「不限」在库里存为该上限（与固定成率一致）。 */
export const UNBOUNDED_COST = FIXED_MAX_COST

export type Result = { rules: Rule[]; error?: undefined } | { rules?: undefined; error: string }
const ok = (rules: Rule[]): Result => ({ rules })
const fail = (error: string): Result => ({ error })

const r4 = (n: number) => Math.round(n * 1e4) / 1e4
/** 判断「两个价格相同」的容差（分）。 */
const CENT = 0.005 + 1e-9
const EPS = 1e-8

export const isFixed = (r: Rule) => r.algorithm === 'Fixed'
export const isCurveRule = (r: Rule) => ['Linear', 'ArcUp', 'ArcDown'].includes(r.algorithm) && r.startRetailPrice != null && r.endRetailPrice != null
/** 新编辑器只能编辑「固定 / 曲线」区间；含旧版 Step、Exponential 或没有节点价的规则要走原有转换入口。 */
export const isEditable = (rules: Rule[]) => rules.length > 0 && rules.every(r => isFixed(r) || isCurveRule(r))

export const startPrice = (r: Rule) => isFixed(r) ? roundMoney(r.minPrice * r.startRate) : (r.startRetailPrice ?? r.minPrice * r.startRate)
export const endPrice = (r: Rule) => isFixed(r) ? roundMoney(r.maxPrice * r.endRate) : (r.endRetailPrice ?? r.maxPrice * r.endRate)

export const makeFixed = (min: number, max: number, rate: number): Rule => ({
  minPrice: min, maxPrice: max, startRate: r4(rate), endRate: r4(rate),
  startRetailPrice: null, endRetailPrice: null, curveBend: null, algorithm: 'Fixed',
})
export const makeCurve = (min: number, max: number, p0: number, p1: number, algorithm: 'Linear' | 'ArcUp' | 'ArcDown' = 'Linear', bend = 0): Rule => ({
  minPrice: min, maxPrice: max, startRate: r4(p0 / min), endRate: r4(p1 / max),
  startRetailPrice: p0, endRetailPrice: p1, curveBend: algorithm === 'Linear' ? 0 : safeBend(bend), algorithm,
})
const withCurveEnds = (r: Rule, p0: number, p1: number) => makeCurve(r.minPrice, r.maxPrice, p0, p1, r.algorithm as 'Linear' | 'ArcUp' | 'ArcDown', r.curveBend ?? 0)

/** 合法尾数：.50 / .99 或整数 1、2（且不低于 0.50）。 */
export function isLegalTail(v: number): boolean {
  const c = Math.round(v * 100)
  if (Math.abs(v * 100 - c) > 1e-6 || c < 50) return false
  return c === 100 || c === 200 || c % 100 === 50 || c % 100 === 99
}

/** 理论价（固定成率恰好是 进货价 × 成率）。 */
export const theoryPrice = (r: Rule, cost: number) => rawPrice(r, cost)
/** 实际售价：所有区间（含固定成率）都按尾数规则调整——小数进位到 .50 / .99，整数取到 .99，1 与 2 保留整数。 */
export const actualPrice = (r: Rule, cost: number) => finalPrice(cost, rawPrice(r, cost))

/** 命中的区间下标：进货价等于交界值时属于上一个区间。 */
export function intervalAt(rules: Rule[], cost: number): number {
  if (!rules.length || cost < rules[0].minPrice - EPS) return -1
  const i = rules.findIndex(r => cost <= r.maxPrice + EPS)
  return i === -1 ? rules.length - 1 : i
}

/** 曲线端点是否「跟随」相邻固定区间的交界价（此时不要求合法尾数）。 */
function followsFixed(rules: Rule[], i: number, which: 'start' | 'end'): boolean {
  const r = rules[i]
  if (which === 'start') {
    const p = rules[i - 1]
    return !!p && isFixed(p) && Math.abs(startPrice(r) - endPrice(p)) <= CENT
  }
  const n = rules[i + 1]
  return !!n && isFixed(n) && Math.abs(endPrice(r) - startPrice(n)) <= CENT
}

/** 曲线区间的最大弧度（0～1），超过会使区间内售价下降或成率上升。 */
export function arcLimit(r: Rule): number {
  const d = (r.endRetailPrice ?? 0) - (r.startRetailPrice ?? 0)
  const c = r.minPrice * (r.endRetailPrice ?? 0) - r.maxPrice * (r.startRetailPrice ?? 0)
  return d > 0 ? Math.max(0, Math.min(1, r.algorithm === 'ArcDown' ? -c / r.maxPrice / d : -c / r.minPrice / d)) : 0
}

/** 与后端 Validate 同口径的校验；返回第一条错误文案。 */
export function intervalsError(rules: Rule[]): string | null {
  if (!rules.length) return '请至少设置一个定价区间'
  if (rules.length > MAX_INTERVALS) return `最多 ${MAX_INTERVALS} 个定价区间`
  for (let i = 0; i < rules.length; i++) {
    const r = rules[i], prev = rules[i - 1], tag = `区间 ${i + 1}：`
    const a = r.minPrice, b = r.maxPrice
    if (![a, b, r.startRate, r.endRate].every(Number.isFinite)) return `${tag}上下限和成率必须是数字`
    if (a < 0.1 - EPS || b <= a) return `${tag}进货价从 0.10 起，且上限必须大于下限`
    if (prev && prev.maxPrice !== a) return `${tag}相邻区间必须首尾相接`
    if (r.startRate < FIXED_RATE_MIN - EPS || r.startRate > FIXED_RATE_MAX + EPS || r.endRate < FIXED_RATE_MIN - EPS || r.endRate > FIXED_RATE_MAX + EPS) return `${tag}成率必须在 1.5～5 之间`
    if (isFixed(r)) {
      if (r.startRate !== r.endRate) return `${tag}固定成率的起止成率必须相同`
      if (r.startRetailPrice != null || r.endRetailPrice != null || (r.curveBend ?? 0) !== 0) return `${tag}固定成率不能带节点售价或弧度`
      continue
    }
    if (!isCurveRule(r)) return `${tag}只支持固定成率或带节点售价的曲线`
    const pa = startPrice(r), pb = endPrice(r), d = pb - pa, h = b - a
    if (pa < 1.5 * a - EPS || pa > 5 * a + EPS || pb < 1.5 * b - EPS || pb > 5 * b + EPS) return `${tag}端点售价对应的成率必须在 1.5～5 之间`
    if (d < -EPS || pa * b < pb * a - EPS) return `${tag}成本增加时，售价不能下降，理论成率不能上升`
    if (!followsFixed(rules, i, 'start') && !isLegalTail(pa)) return `${tag}起点售价必须使用 .50 / .99 尾数`
    if (!followsFixed(rules, i, 'end') && !isLegalTail(pb)) return `${tag}终点售价必须使用 .50 / .99 尾数`
    const bend = r.curveBend ?? 0
    if (Math.abs(bend) > 1 || (r.algorithm === 'Linear' && bend !== 0) || (r.algorithm === 'ArcUp' && bend < 0) || (r.algorithm === 'ArcDown' && bend > 0)) return `${tag}弯曲方向或弧度无效`
    const B = d * bend, slopeA = (d + B) / h, slopeB = (d - B) / h
    if (Math.min(slopeA, slopeB) < -EPS || Math.max(a * slopeA - pa, b * slopeB - pb) > EPS) return `${tag}弧度过大，会使售价下降或理论成率上升，请减小弧度`
  }
  return null
}

export interface PriceDrop { index: number; at: number; from: number; to: number }
/** 交界处「成本变大、售价反而降低」的位置（阶梯定价）；保存前需要用户二次确认。 */
export function priceDrops(rules: Rule[]): PriceDrop[] {
  const drops: PriceDrop[] = []
  for (let i = 0; i < rules.length - 1; i++) {
    const from = endPrice(rules[i]), to = startPrice(rules[i + 1])
    if (to < from - 0.004) drops.push({ index: i, at: rules[i].maxPrice, from, to })
  }
  return drops
}

// ---- 交界联动 ----
/** 交界两侧至少有一个曲线区间时才存在「联动」概念；两个固定区间相邻只有成率之分。 */
export const hasCurveSide = (rules: Rule[], i: number) => !isFixed(rules[i]) || !isFixed(rules[i + 1])
export const isContinuous = (rules: Rule[], i: number) => Math.abs(endPrice(rules[i]) - startPrice(rules[i + 1])) <= CENT
/** 每个交界是否联动：有曲线一侧、当前售价连续，并且用户没有主动断开。 */
export const linkedFlags = (rules: Rule[], broken: ReadonlySet<number>): boolean[] =>
  rules.slice(0, -1).map((_, i) => hasCurveSide(rules, i) && isContinuous(rules, i) && !broken.has(i))
export const shiftBrokenAfterAdd = (broken: ReadonlySet<number>, i: number) => new Set([...broken].map(b => (b < i ? b : b + 1)))
export const shiftBrokenAfterRemove = (broken: ReadonlySet<number>, i: number) =>
  new Set([...broken].filter(b => b !== i - 1 && b !== i).map(b => (b < i - 1 ? b : b - 1)))

/** 交界售价由哪一侧决定：固定区间优先（价格 = 交界成本 × 成率），其次是 driver 指定的一侧。 */
function syncBoundary(rules: Rule[], i: number, driver: 'left' | 'right'): void {
  const left = rules[i], right = rules[i + 1], at = left.maxPrice
  if (isFixed(left) && !isFixed(right)) rules[i + 1] = withCurveEnds(right, roundMoney(at * left.endRate), endPrice(right))
  else if (isFixed(right) && !isFixed(left)) rules[i] = withCurveEnds(left, startPrice(left), roundMoney(at * right.startRate))
  else if (!isFixed(left) && !isFixed(right)) {
    if (driver === 'left') rules[i + 1] = withCurveEnds(right, endPrice(left), endPrice(right))
    else rules[i] = withCurveEnds(left, startPrice(left), startPrice(right))
  }
}

/** 断开交界联动：曲线一侧不再跟随，端点必须是合法尾数，所以把非尾数的端点吸附到最近的合法尾数。 */
export function breakLink(rules: Rule[], i: number): Result {
  const next = rules.map(r => ({ ...r })), at = next[i].maxPrice
  if (!isFixed(next[i])) {
    const p = snap(at, endPrice(next[i]))
    if (Number.isNaN(p)) return fail('该交界没有满足 1.5～5 倍的尾数售价')
    next[i] = withCurveEnds(next[i], startPrice(next[i]), p)
  }
  if (!isFixed(next[i + 1])) {
    const p = snap(at, startPrice(next[i + 1]))
    if (Number.isNaN(p)) return fail('该交界没有满足 1.5～5 倍的尾数售价')
    next[i + 1] = withCurveEnds(next[i + 1], p, endPrice(next[i + 1]))
  }
  return finish(next)
}

/** 重新联动：固定侧优先；两侧都是曲线时以 driver 指定的一侧为准。 */
export function relink(rules: Rule[], i: number, driver: 'left' | 'right'): Result {
  const next = rules.map(r => ({ ...r }))
  syncBoundary(next, i, driver)
  return finish(next)
}

const finish = (rules: Rule[]): Result => {
  const error = intervalsError(rules)
  return error ? fail(error) : ok(rules)
}
const snap = (cost: number, price: number) => boundedPrice(cost, price)

/** 修改交界成本：上一个区间的上限与下一个区间的下限同时变化。 */
export function setBoundary(rules: Rule[], i: number, cost: number, linked: boolean[]): Result {
  if (!Number.isFinite(cost)) return fail('请填写交界成本')
  const c = roundMoney(cost), left = rules[i], right = rules[i + 1], oldAt = left.maxPrice
  if (c <= left.minPrice + EPS || c >= right.maxPrice - EPS) return fail(`交界成本必须大于 ${left.minPrice.toFixed(2)} 且小于 ${right.maxPrice.toFixed(2)}`)
  const next = rules.map(r => ({ ...r }))
  next[i] = { ...left, maxPrice: c }
  next[i + 1] = { ...right, minPrice: c }
  // 曲线端点保持原来的成率（而不是原来的售价），再吸附到新成本下的合法尾数，曲线形状才不会被拉歪；
  // 联动时再由固定侧或左侧决定交界售价。
  const fit = (r: Rule, which: 'start' | 'end'): number => snap(c, c * (which === 'start' ? startPrice(r) : endPrice(r)) / oldAt)
  let p0: number | undefined, p1: number | undefined
  if (!isFixed(next[i])) p1 = fit(next[i], 'end')
  if (!isFixed(next[i + 1])) p0 = fit(next[i + 1], 'start')
  if ((p1 !== undefined && Number.isNaN(p1)) || (p0 !== undefined && Number.isNaN(p0))) return fail('该交界成本下没有满足 1.5～5 倍的尾数售价')
  if (p1 !== undefined) next[i] = withCurveEnds(next[i], startPrice(next[i]), p1)
  if (p0 !== undefined) next[i + 1] = withCurveEnds(next[i + 1], p0, endPrice(next[i + 1]))
  if (linked[i]) syncBoundary(next, i, 'left')
  return finish(next)
}

/** 修改整段的下限（仅第一个区间）或上限（仅最后一个区间）。 */
export function setEdge(rules: Rule[], edge: 'min' | 'max', cost: number): Result {
  if (!Number.isFinite(cost)) return fail('请填写进货价')
  const c = roundMoney(cost), next = rules.map(r => ({ ...r })), idx = edge === 'min' ? 0 : rules.length - 1, r = next[idx]
  if (edge === 'min' ? c >= r.maxPrice - EPS : c <= r.minPrice + EPS) return fail(edge === 'min' ? '下限必须小于本区间上限' : '上限必须大于本区间下限')
  if (isFixed(r)) next[idx] = { ...r, [edge === 'min' ? 'minPrice' : 'maxPrice']: c }
  else {
    const moved = { ...r, [edge === 'min' ? 'minPrice' : 'maxPrice']: c }
    // 同样保持端点成率。
    const p = snap(c, c * (edge === 'min' ? startPrice(r) / r.minPrice : endPrice(r) / r.maxPrice))
    if (Number.isNaN(p)) return fail('该成本下没有满足 1.5～5 倍的尾数售价')
    next[idx] = edge === 'min' ? withCurveEnds(moved, p, endPrice(r)) : withCurveEnds(moved, startPrice(r), p)
  }
  return finish(next)
}

/** 修改固定成率；与其联动的曲线端点跟随 交界成本 × 成率。 */
export function setRate(rules: Rule[], i: number, rate: number, linked: boolean[]): Result {
  if (!Number.isFinite(rate)) return fail('请填写成率')
  if (rate < FIXED_RATE_MIN || rate > FIXED_RATE_MAX) return fail(`成率必须在 ${FIXED_RATE_MIN}～${FIXED_RATE_MAX} 之间`)
  if (!isFixed(rules[i])) return fail('只有固定成率区间可以设置成率')
  const next = rules.map(r => ({ ...r }))
  next[i] = makeFixed(next[i].minPrice, next[i].maxPrice, rate)
  if (i > 0 && linked[i - 1]) syncBoundary(next, i - 1, 'right')
  if (i < next.length - 1 && linked[i]) syncBoundary(next, i, 'left')
  return finish(next)
}

/** 切换区间类型。固定→曲线：端点吸附到合法尾数；曲线→固定：取两端成率的平均值。 */
export function setKind(rules: Rule[], i: number, kind: 'fixed' | 'curve', linked: boolean[]): Result {
  const r = rules[i]
  if ((kind === 'fixed') === isFixed(r)) return ok(rules)
  const next = rules.map(x => ({ ...x }))
  if (kind === 'fixed') {
    const rate = Math.min(FIXED_RATE_MAX, Math.max(FIXED_RATE_MIN, r4((startPrice(r) / r.minPrice + endPrice(r) / r.maxPrice) / 2)))
    next[i] = makeFixed(r.minPrice, r.maxPrice, rate)
  } else {
    if (r.maxPrice >= UNBOUNDED_COST) return fail('上限为「不限」的区间不能改成曲线，请先填写一个具体的上限')
    const p0 = snap(r.minPrice, startPrice(r)), p1 = snap(r.maxPrice, endPrice(r))
    if (Number.isNaN(p0) || Number.isNaN(p1)) return fail('该区间没有满足 1.5～5 倍的尾数售价')
    next[i] = makeCurve(r.minPrice, r.maxPrice, p0, p1)
  }
  // 原本联动或售价连续的交界重新对齐：固定侧优先；两侧都是曲线时以被切换的这个区间为准。
  if (i > 0 && (linked[i - 1] || isContinuous(rules, i - 1))) syncBoundary(next, i - 1, 'right')
  if (i < next.length - 1 && (linked[i] || isContinuous(rules, i))) syncBoundary(next, i, 'left')
  return finish(next)
}

/** 修改曲线区间某个端点的售价；与曲线邻居联动时两侧同步，与固定邻居联动时需先断开。 */
export function setEndpoint(rules: Rule[], i: number, which: 'start' | 'end', wanted: number, linked: boolean[]): Result {
  const r = rules[i]
  if (isFixed(r)) return fail('固定成率区间没有端点售价')
  if (!Number.isFinite(wanted)) return fail('请填写售价')
  const bIdx = which === 'start' ? i - 1 : i, isLinked = bIdx >= 0 && bIdx < linked.length && linked[bIdx]
  const nb = which === 'start' ? rules[i - 1] : rules[i + 1]
  if (isLinked && nb && isFixed(nb)) return fail('该端点跟随相邻固定区间，请先断开后再单独设置')
  const cost = which === 'start' ? r.minPrice : r.maxPrice
  const other = which === 'start' ? endPrice(r) : startPrice(r)
  let lo = 1.5 * cost, hi = 5 * cost
  if (which === 'start') { lo = Math.max(lo, other * r.minPrice / r.maxPrice); hi = Math.min(hi, other) }
  else { lo = Math.max(lo, other); hi = Math.min(hi, other * r.maxPrice / r.minPrice) }
  if (isLinked && nb && !isFixed(nb)) {
    // 邻居区间的端点也是同一个价格，同样要满足它自己的区间内约束。
    if (which === 'start') { lo = Math.max(lo, startPrice(nb)); hi = Math.min(hi, startPrice(nb) * nb.maxPrice / nb.minPrice) }
    else { lo = Math.max(lo, endPrice(nb) * nb.minPrice / nb.maxPrice); hi = Math.min(hi, endPrice(nb)) }
  }
  const bottom = tailCeil(lo), top = tailFloor(hi)
  if (bottom > top + EPS) return fail('该位置没有满足相邻约束的尾数售价')
  const bounded = snap(cost, wanted)
  const price = roundMoney(Math.min(top, Math.max(bottom, Number.isNaN(bounded) ? bottom : bounded)))
  const next = rules.map(x => ({ ...x }))
  next[i] = which === 'start' ? withCurveEnds(r, price, endPrice(r)) : withCurveEnds(r, startPrice(r), price)
  if (isLinked && nb && !isFixed(nb)) {
    const j = which === 'start' ? i - 1 : i + 1
    next[j] = which === 'start' ? withCurveEnds(nb, startPrice(nb), price) : withCurveEnds(nb, price, endPrice(nb))
  }
  return finish(next)
}

/**
 * 按成率设置曲线端点：目标价 = 端点成本 × 成率，吸附到合法尾数后作为零售价，页面显示的是算出来的零售价。
 * 保存用户输入的成率（价格取整后的实际成率可能略有出入）；若相邻约束迫使价格偏离太多，则保存实际成率。
 */
export function setEndpointRate(rules: Rule[], i: number, which: 'start' | 'end', rate: number, linked: boolean[]): Result {
  if (!Number.isFinite(rate)) return fail('请填写成率')
  if (rate < FIXED_RATE_MIN || rate > FIXED_RATE_MAX) return fail(`成率必须在 ${FIXED_RATE_MIN}～${FIXED_RATE_MAX} 之间`)
  const r = rules[i]
  if (isFixed(r)) return fail('固定成率区间请直接设置成率')
  const cost = which === 'start' ? r.minPrice : r.maxPrice
  const moved = setEndpoint(rules, i, which, cost * rate, linked)
  if (!moved.rules) return moved
  const next = moved.rules.map(x => ({ ...x }))
  const price = which === 'start' ? startPrice(next[i]) : endPrice(next[i])
  // 合法尾数相邻档最多差 0.51 元，超出这个范围说明价格是被相邻约束压过来的，此时记录实际成率。
  const stored = Math.abs(price / cost - rate) <= 0.51 / cost + 1e-6 ? r4(rate) : r4(price / cost)
  next[i] = which === 'start' ? { ...next[i], startRate: stored } : { ...next[i], endRate: stored }
  const bIdx = which === 'start' ? i - 1 : i
  if (bIdx >= 0 && bIdx < linked.length && linked[bIdx]) {
    // 联动的曲线邻居共用同一个交界，成率一并记录
    const j = which === 'start' ? i - 1 : i + 1
    if (next[j] && !isFixed(next[j])) next[j] = which === 'start' ? { ...next[j], endRate: stored } : { ...next[j], startRate: stored }
  }
  return finish(next)
}

export function setShape(rules: Rule[], i: number, algorithm: 'Linear' | 'ArcUp' | 'ArcDown'): Result {
  const r = rules[i]
  if (isFixed(r)) return fail('固定成率区间没有曲线形状')
  const next = rules.map(x => ({ ...x }))
  next[i] = makeCurve(r.minPrice, r.maxPrice, startPrice(r), endPrice(r), algorithm, 0)
  return finish(next)
}

/** percent：0～100，占当前区间最大弧度的比例。 */
export function setBend(rules: Rule[], i: number, percent: number): Result {
  const r = rules[i], limit = arcLimit(r)
  if (isFixed(r) || r.algorithm === 'Linear') return ok(rules)
  const next = rules.map(x => ({ ...x }))
  next[i] = { ...r, curveBend: safeBend((r.algorithm === 'ArcDown' ? -1 : 1) * limit * percent / 100) }
  return finish(next)
}

/** 在区间中点拆成两个区间；固定保持同一成率，曲线在中点取当前曲线价并重置为直线。 */
export function addInterval(rules: Rule[], i: number): Result {
  if (rules.length >= MAX_INTERVALS) return fail(`最多 ${MAX_INTERVALS} 个定价区间`)
  const r = rules[i]
  // 上限为「不限」时取下限的两倍作为拆分点，避免落在几万这种没有意义的位置。
  const mid = r.maxPrice >= UNBOUNDED_COST ? roundMoney(Math.max(r.minPrice * 2, r.minPrice + 10)) : roundMoney((r.minPrice + r.maxPrice) / 2)
  if (mid <= r.minPrice + EPS || mid >= r.maxPrice - EPS) return fail('该区间太窄，无法再拆分')
  const next = rules.map(x => ({ ...x }))
  if (isFixed(r)) next.splice(i, 1, makeFixed(r.minPrice, mid, r.startRate), makeFixed(mid, r.maxPrice, r.startRate))
  else {
    const p = snap(mid, rawPrice(r, mid))
    if (Number.isNaN(p)) return fail('中点没有满足 1.5～5 倍的尾数售价')
    next.splice(i, 1, makeCurve(r.minPrice, mid, startPrice(r), p), makeCurve(mid, r.maxPrice, p, endPrice(r)))
  }
  return finish(next)
}

/**
 * 在末尾追加一个新区间：从最后一个区间的上限接着往上（上限翻倍），而不是把已有区间从中间切开。
 * 固定成率沿用最后一个区间的成率；曲线从上一区间的终点售价接着走，终点保持同样的成率（再限制成率不上升）。
 */
export function appendInterval(rules: Rule[]): Result {
  if (rules.length >= MAX_INTERVALS) return fail(`最多 ${MAX_INTERVALS} 个定价区间`)
  const last = rules[rules.length - 1]
  if (last.maxPrice >= UNBOUNDED_COST) return fail('最后一个区间的上限已是「不限」，后面没有可追加的范围；请用「拆分区间」，或先给它设一个具体的上限')
  const min = last.maxPrice, doubled = roundMoney(min * 2)
  const next = rules.map(x => ({ ...x }))
  if (isFixed(last)) {
    next.push(makeFixed(min, Math.min(doubled, UNBOUNDED_COST), last.startRate))
    return finish(next)
  }
  const max = Math.min(doubled, UNBOUNDED_COST - 1)
  if (max <= min + EPS) return fail('上限已接近系统最大值，无法再追加区间')
  const p0 = endPrice(last), p1raw = snap(max, max * p0 / min)
  if (Number.isNaN(p1raw)) return fail('新区间没有满足 1.5～5 倍的尾数售价')
  next.push(makeCurve(min, max, p0, Math.min(p1raw, tailFloor(p0 * max / min + 1e-9))))
  return finish(next)
}

/** 删除区间：并入相邻区间（优先并入上一个）。 */
export function removeInterval(rules: Rule[], i: number): Result {
  if (rules.length <= 1) return fail('至少保留一个定价区间')
  const r = rules[i], next = rules.map(x => ({ ...x }))
  if (i > 0) {
    const prev = next[i - 1]
    next[i - 1] = isFixed(prev) ? makeFixed(prev.minPrice, r.maxPrice, prev.startRate) : (() => {
      const p = snap(r.maxPrice, endPrice(r))
      return Number.isNaN(p) ? prev : makeCurve(prev.minPrice, r.maxPrice, startPrice(prev), p)
    })()
  } else {
    const nxt = next[1]
    next[1] = isFixed(nxt) ? makeFixed(r.minPrice, nxt.maxPrice, nxt.startRate) : (() => {
      const p = snap(r.minPrice, startPrice(r))
      return Number.isNaN(p) ? nxt : makeCurve(r.minPrice, nxt.maxPrice, p, endPrice(nxt))
    })()
  }
  next.splice(i, 1)
  return finish(next)
}

// ---- 旧版区间规则 → 区间编辑器 ----
export type Conversion = { rules: Rule[]; notes: string[]; error?: undefined } | { rules?: undefined; notes: string[]; error: string }

/**
 * 把旧版区间规则（阶梯 Step、指数 Exponential、没有节点售价的 Linear）转换成区间编辑器能编辑的「固定 / 曲线」区间。
 * - 阶梯 → 固定成率（线上对阶梯只按起始成率计价；两者都走同一套尾数规则，售价不变）。
 * - 线性 / 指数 → 曲线：两端售价 = 端点成本 × 端点成率，再吸附到合法尾数；指数的弯曲形状会变成直线。
 * - 第一个区间下限不足 0.10 时抬到 0.10（进货价低于 0.10 本来就无法计价）。
 * - 相邻区间不要求售价连续（新模型允许阶梯定价），保存时若有回落会二次确认。
 * 已经是固定 / 曲线节点的规则原样保留。转换只发生在编辑器里，保存之前线上策略不变。
 */
export function convertLegacy(source: Rule[]): Conversion {
  const notes: string[] = []
  if (!source.length) return { error: '没有可转换的区间', notes }
  const sorted = [...source].sort((a, b) => a.minPrice - b.minPrice)
  const out: Rule[] = []
  for (let i = 0; i < sorted.length; i++) {
    const r = sorted[i], tag = `区间 ${i + 1}`
    const min = i === 0 && r.minPrice < 0.1 ? 0.1 : r.minPrice, max = r.maxPrice
    if (i === 0 && r.minPrice < 0.1) notes.push(`${tag}：下限 ${r.minPrice.toFixed(2)} 调整为 0.10（进货价低于 0.10 本来就无法计价）`)
    if (isFixed(r) || isCurveRule(r)) { out.push({ ...r, minPrice: min }); continue }
    if (!Number.isFinite(min) || !Number.isFinite(max) || max <= min) return { error: `${tag}：上下限无效，请先在原有规则里修正`, notes }
    if (r.algorithm === 'Step') {
      // 线上对阶梯只按起始成率计价（结束成率不生效），转换要和线上行为一致。
      out.push(makeFixed(min, max, r.startRate))
      notes.push(`${tag}：阶梯 → 固定成率 ${r.startRate}×` + (r.endRate !== r.startRate ? `（原结束成率 ${r.endRate} 在计价里本来就不生效）` : ''))
      continue
    }
    // 起点取最近的合法尾数；终点再限制在「不超过起点成率」的合法尾数内，避免两端各自吸附后成率反而上升。
    const p0 = snap(min, min * r.startRate), p1raw = snap(max, max * r.endRate)
    if (Number.isNaN(p0) || Number.isNaN(p1raw)) return { error: `${tag}：端点没有满足 1.5～5 倍的尾数售价，无法转换`, notes }
    const p1 = Math.min(p1raw, tailFloor(p0 * max / min + 1e-9))
    out.push(makeCurve(min, max, p0, p1))
    if (r.algorithm === 'Exponential') notes.push(`${tag}：指数 → 曲线，两端售价不变，中间价格会与原指数曲线有差异，请对照图形检查`)
  }
  const error = intervalsError(out)
  if (error) return { error, notes }
  const drops = priceDrops(out).length
  if (drops) notes.push(`${drops} 处交界售价回落（阶梯定价），保存时需确认`)
  return { rules: out, notes }
}

// ---- 上限输入框的显示 / 解析 ----
/**
 * 区间上限输入框的显示文本。注意：antd InputNumber 在用户**正在输入**时传给 formatter 的是字符串（而不是数字），
 * 此时必须原样显示用户键入的内容，否则像 "5." 这样的中间状态会被打乱，直接对字符串调用 toFixed 还会让整页崩溃。
 */
export function formatBound(value: unknown, info?: { userTyping?: boolean; input?: string }): string {
  if (info?.userTyping) return info.input ?? String(value ?? '')
  if (value === undefined || value === null || value === '') return ''
  const n = Number(value)
  if (!Number.isFinite(n)) return String(value)
  return n >= UNBOUNDED_COST ? '不限' : n.toFixed(2)
}
/** 解析上限输入：「不限」= 99999；空或无法识别返回 NaN（输入框当作空值处理）。 */
export function parseBound(text: string | undefined): number {
  if (text === '不限') return UNBOUNDED_COST
  const t = (text ?? '').trim()
  return t === '' ? Number.NaN : Number(t)
}

// ---- 图表范围 ----
export function chartRange(rules: Rule[], maxCost: number | null) {
  const first = rules[0].minPrice, last = rules[rules.length - 1]
  const finiteBoundary = rules.length > 1 ? rules[rules.length - 2].maxPrice : first
  // 最后一个区间不限上限时，图上只展示到「最后一个交界的两倍」，避免被 99999 压扁。
  const fullEnd = last.maxPrice >= UNBOUNDED_COST ? Math.max(finiteBoundary * 2, first * 10, 100) : last.maxPrice
  const presets = rules.slice(0, -1).map(r => r.maxPrice)
  const end = maxCost !== null && maxCost > first && maxCost < fullEnd ? maxCost : fullEnd
  return { first, fullEnd, end, presets }
}

/** 可见范围内的最高售价，用来决定纵轴（再留 20% 余量）。 */
export function visibleMaxPrice(rules: Rule[], end: number): number {
  let top = 0
  for (const r of rules) {
    if (r.minPrice >= end) continue
    top = Math.max(top, theoryPrice(r, Math.min(r.maxPrice, end)), actualPrice(r, Math.min(r.maxPrice, end)))
  }
  return top
}
