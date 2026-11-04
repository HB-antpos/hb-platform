/**
 * 每钩挂数与承重核对（纯函数，批 2 设计器同样使用）。
 *
 * 口径（方案第 2 节「每钩挂数」）：
 * - 自动换算 = ⌊(臂长 − 臂端预留) ÷ 包装厚⌋；
 * - 可按挂钩型号手填覆盖（整数 ≥ 1），覆盖值优先；
 * - 挂数 × 单件重 > 承重 时标黄提示（只提示，不阻止保存）。
 */

/** 浮点容差：避免 180 ÷ 0.6 这类恰好整除却差一点点的除法被 floor 少算 1。 */
const EPSILON = 1e-9

export interface HookArmLike {
  armLengthMm: number
  tipReserveMm: number
}

function isPositive(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0
}

/** 自动换算每钩挂数；厚度或可用臂长不是正数时返回 0（界面显示「—」，不提示可挂）。 */
export function suggestHookQty(hook: HookArmLike, depthMm: number): number {
  if (!isPositive(depthMm)) return 0
  const usable = hook.armLengthMm - hook.tipReserveMm
  if (!isPositive(usable)) return 0
  return Math.floor(usable / depthMm + EPSILON)
}

/** 手填覆盖值：只认整数 ≥ 1，其余（空、0、小数、负数、NaN）视为没有覆盖。 */
export function normalizeQtyOverride(value: unknown): number | null {
  if (typeof value !== 'number' || !Number.isFinite(value)) return null
  return Number.isInteger(value) && value >= 1 ? value : null
}

export interface HookQtyResult {
  /** 自动换算值（可能为 0：臂太短或厚度未填）。 */
  auto: number
  /** 手填覆盖值；没有覆盖为 null。 */
  override: number | null
  /** 生效值 = 覆盖值优先，否则自动换算值。 */
  effective: number
  source: 'override' | 'auto'
}

/** overrides 的值允许为 null / undefined：表单里「清空手填框」得到的就是 null，按没有覆盖处理。 */
export function resolveHookQty(
  hook: HookArmLike & { guid: string },
  depthMm: number,
  overrides: Readonly<Record<string, number | null | undefined>> | null | undefined,
): HookQtyResult {
  const auto = suggestHookQty(hook, depthMm)
  const override = normalizeQtyOverride(overrides?.[hook.guid])
  return override === null
    ? { auto, override: null, effective: auto, source: 'auto' }
    : { auto, override, effective: override, source: 'override' }
}

export type HookLoadStatus =
  /** 在承重范围内。 */
  | 'ok'
  /** 超过承重：标黄提示。 */
  | 'over'
  /** 没填单件重，无法核对。 */
  | 'noWeight'
  /** 挂钩型号没填承重，无法核对（只显示总重）。 */
  | 'noLimit'

export interface HookLoadCheck {
  status: HookLoadStatus
  /** 总重（克）；缺单件重或挂数为 0 时为 null。 */
  totalG: number | null
  /** 承重（克）；挂钩型号没填承重时为 null。 */
  limitG: number | null
}

/** 承重核对：用克做整数比较，避免 3.15 kg 这类小数乘 1000 的浮点误差把「刚好等于承重」判成超重。 */
export function checkHookLoad(
  qty: number,
  unitWeightG: number | null | undefined,
  loadKg: number | null | undefined,
): HookLoadCheck {
  const limitG = isPositive(loadKg) ? Math.round(loadKg * 1000) : null
  if (!isPositive(unitWeightG) || !Number.isFinite(qty) || qty <= 0) {
    return { status: 'noWeight', totalG: null, limitG }
  }
  const totalG = qty * unitWeightG
  if (limitG === null) return { status: 'noLimit', totalG, limitG }
  return { status: totalG > limitG ? 'over' : 'ok', totalG, limitG }
}

/** 克 → 千克文本：固定两位小数；先把克四舍五入到 0.01 kg 再除，用整数运算避免 1.425 这类浮点误差（1425 g → 1.43）。 */
export function formatKgFromGrams(grams: number): string {
  const hundredths = Math.round(grams / 10)
  return (hundredths / 100).toFixed(2)
}

/** 承重（克）→ 千克文本，去掉多余尾零：3000 → "3"，3500 → "3.5"，3150 → "3.15"。 */
export function formatLimitKg(grams: number): string {
  return String(Math.round(grams) / 1000)
}

/** 价签架顶边距挂孔 = 钩高 H − 价签底边高于挂货臂 − 价签高（设计图：50 − 10 − 26 = 14；负数表示价签架顶边在挂孔之上）。 */
export function tagTopBelowHoleMm(hook: { dropMm: number; tagBottomAboveArmMm: number; tagHeightMm: number }): number {
  return hook.dropMm - hook.tagBottomAboveArmMm - hook.tagHeightMm
}
