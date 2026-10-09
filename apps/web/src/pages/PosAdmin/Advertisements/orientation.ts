import type { AdvertisementOrientation } from '../../../types/advertisement'

/**
 * 客显（1366×768 副屏）上的两个广告位，像素尺寸按 WPF 客显实际布局取值：
 * - idle：空闲时全屏广告位
 * - checkout：收银时右侧广告位
 * 素材在广告位里等比缩放、不裁切，比例不符时会留边。
 */
export type AdvertisementSlot = 'idle' | 'checkout'

export const ADVERTISEMENT_SLOTS: Readonly<Record<AdvertisementSlot, { width: number; height: number }>> = {
  idle: { width: 1330, height: 732 },
  checkout: { width: 514, height: 580 },
}

/** 与目标广告位宽高比的相对偏差超过该阈值，视为「不适配」（会明显留边）。 */
export const SLOT_FIT_TOLERANCE = 0.15

/** 各版式会在哪些广告位播放。 */
const ORIENTATION_SLOTS: Readonly<Record<AdvertisementOrientation, readonly AdvertisementSlot[]>> = {
  Landscape: ['idle'],
  Portrait: ['checkout'],
  Any: ['idle', 'checkout'],
}

export interface MediaSize {
  width: number
  height: number
}

/** 宽高都是正的有限数才算读到了尺寸；旧广告 / 读取失败的 null 一律视为未知。 */
export function toMediaSize(width?: number | null, height?: number | null): MediaSize | null {
  if (
    typeof width !== 'number' ||
    typeof height !== 'number' ||
    !Number.isFinite(width) ||
    !Number.isFinite(height) ||
    width <= 0 ||
    height <= 0
  ) {
    return null
  }
  return { width, height }
}

/**
 * 按素材宽高自动预选版式：宽 > 高 → 横版，否则（含正方形）→ 竖版。
 * 尺寸未知时返回 null，由管理员手动选择。
 */
export function suggestOrientationFromSize(
  width?: number | null,
  height?: number | null,
): Exclude<AdvertisementOrientation, 'Any'> | null {
  const size = toMediaSize(width, height)
  if (!size) return null
  return size.width > size.height ? 'Landscape' : 'Portrait'
}

/** 该版式会在哪些广告位播放。 */
export function getOrientationSlots(orientation: AdvertisementOrientation): readonly AdvertisementSlot[] {
  return ORIENTATION_SLOTS[orientation] ?? ORIENTATION_SLOTS.Any
}

export function isSlotPlayed(orientation: AdvertisementOrientation, slot: AdvertisementSlot) {
  return getOrientationSlots(orientation).includes(slot)
}

/**
 * 素材放进某广告位的适配结果：
 * - fits：与广告位宽高比相对偏差 ≤ 15%
 * - letterbox：不适配时留边的方向。素材比广告位「更窄」→ 两侧留边（sides）；「更宽」→ 上下留边（topBottom）
 * 尺寸未知时返回 null。
 */
export interface SlotFit {
  fits: boolean
  deviation: number
  letterbox: 'sides' | 'topBottom' | null
}

export function evaluateSlotFit(
  width: number | null | undefined,
  height: number | null | undefined,
  slot: AdvertisementSlot,
): SlotFit | null {
  const size = toMediaSize(width, height)
  if (!size) return null
  const slotSize = ADVERTISEMENT_SLOTS[slot]
  const mediaRatio = size.width / size.height
  const slotRatio = slotSize.width / slotSize.height
  const deviation = Math.abs(mediaRatio - slotRatio) / slotRatio
  const fits = deviation <= SLOT_FIT_TOLERANCE
  return {
    fits,
    deviation,
    letterbox: fits ? null : mediaRatio < slotRatio ? 'sides' : 'topBottom',
  }
}

/**
 * 版式与素材比例不符的提示（列表黄色提示 / 编辑弹窗黄色 Alert 共用）。
 * 只有当素材与「它将要播放的所有广告位」都不接近时才提示：
 * - 通用两处都不适配 → both（两处都会留边）
 * - 横版 / 竖版唯一的广告位不适配 → 该广告位 + 留边方向
 * 通用但其中一处适配（如 16:9 素材）不提示；尺寸未知不提示。
 */
export type OrientationMismatch =
  | { kind: 'both' }
  | { kind: 'single'; slot: AdvertisementSlot; letterbox: 'sides' | 'topBottom' }

export function getOrientationMismatch(
  orientation: AdvertisementOrientation,
  width?: number | null,
  height?: number | null,
): OrientationMismatch | null {
  const slots = getOrientationSlots(orientation)
  const fits = slots.map((slot) => ({ slot, fit: evaluateSlotFit(width, height, slot) }))
  if (fits.some((item) => item.fit == null || item.fit.fits)) {
    return null
  }
  if (fits.length > 1) {
    return { kind: 'both' }
  }
  const [{ slot, fit }] = fits
  return { kind: 'single', slot, letterbox: fit!.letterbox ?? 'sides' }
}

function greatestCommonDivisor(a: number, b: number): number {
  return b === 0 ? a : greatestCommonDivisor(b, a % b)
}

/**
 * 宽高比的展示文本：约分后两项都不超过 32 时显示「4:3」「16:9」这类整数比，
 * 否则显示「0.89:1」这类小数比，避免出现「386:435」这种难读的写法。
 */
export function formatAspectRatio(width?: number | null, height?: number | null): string | null {
  const size = toMediaSize(width, height)
  if (!size) return null
  if (Number.isInteger(size.width) && Number.isInteger(size.height)) {
    const divisor = greatestCommonDivisor(size.width, size.height)
    const ratioWidth = size.width / divisor
    const ratioHeight = size.height / divisor
    if (ratioWidth <= 32 && ratioHeight <= 32) {
      return `${ratioWidth}:${ratioHeight}`
    }
  }
  return `${(size.width / size.height).toFixed(2)}:1`
}
