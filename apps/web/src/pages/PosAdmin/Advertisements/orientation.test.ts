import {
  ADVERTISEMENT_SLOTS,
  SLOT_FIT_TOLERANCE,
  evaluateSlotFit,
  formatAspectRatio,
  getOrientationMismatch,
  getOrientationSlots,
  isSlotPlayed,
  suggestOrientationFromSize,
  toMediaSize,
} from './orientation'

function assertEqual(actual: unknown, expected: unknown, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)

  if (actualJson !== expectedJson) {
    throw new Error(`${message}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

// ---- 尺寸合法性 ----
assertEqual(toMediaSize(null, null), null, '宽高为 null 视为尺寸未知')
assertEqual(toMediaSize(1366, undefined), null, '只有宽没有高视为尺寸未知')
assertEqual(toMediaSize(0, 768), null, '宽为 0 视为尺寸未知')
assertEqual(toMediaSize(Number.NaN, 768), null, 'NaN 视为尺寸未知')
assertDeepEqual(toMediaSize(1366, 768), { width: 1366, height: 768 }, '正常宽高原样返回')

// ---- 宽高 → 自动版式 ----
assertEqual(suggestOrientationFromSize(1366, 768), 'Landscape', '宽 > 高 自动选横版')
assertEqual(suggestOrientationFromSize(772, 870), 'Portrait', '宽 < 高 自动选竖版')
assertEqual(suggestOrientationFromSize(1000, 1000), 'Portrait', '正方形按「否则竖版」处理')
assertEqual(suggestOrientationFromSize(null, null), null, '读不到尺寸不自动选择')
assertEqual(suggestOrientationFromSize(1366, 0), null, '非法尺寸不自动选择')

// ---- 版式 → 播放位置 ----
assertDeepEqual(getOrientationSlots('Landscape'), ['idle'], '横版只在空闲全屏播放')
assertDeepEqual(getOrientationSlots('Portrait'), ['checkout'], '竖版只在收银右侧播放')
assertDeepEqual(getOrientationSlots('Any'), ['idle', 'checkout'], '通用两处都播')
assertEqual(isSlotPlayed('Landscape', 'checkout'), false, '横版不在收银右侧播放')
assertEqual(isSlotPlayed('Any', 'checkout'), true, '通用在收银右侧播放')

// ---- 比例是否适配某广告位（阈值 15%）----
assertEqual(SLOT_FIT_TOLERANCE, 0.15, '适配阈值为相对偏差 15%')
assertEqual(evaluateSlotFit(1366, 768, 'idle')?.fits, true, '建议横版尺寸 1366×768 适配空闲全屏')
assertEqual(evaluateSlotFit(772, 870, 'checkout')?.fits, true, '建议竖版尺寸 772×870 适配收银右侧')
assertEqual(evaluateSlotFit(1920, 1080, 'idle')?.fits, true, '16:9 适配空闲全屏')
assertEqual(evaluateSlotFit(1000, 1000, 'checkout')?.fits, true, '正方形与收银右侧偏差约 13%，仍算适配')
assertEqual(evaluateSlotFit(null, null, 'idle'), null, '尺寸未知时不判断适配')

const idleRatio = ADVERTISEMENT_SLOTS.idle.width / ADVERTISEMENT_SLOTS.idle.height
// 恰好 15% 偏差仍算适配，超过才算不适配（用整数宽高避免浮点误差影响边界）
assertEqual(
  evaluateSlotFit(Math.round(idleRatio * 1.149 * 10000), 10000, 'idle')?.fits,
  true,
  '偏差略低于 15% 仍算适配',
)
assertEqual(
  evaluateSlotFit(Math.round(idleRatio * 1.151 * 10000), 10000, 'idle')?.fits,
  false,
  '偏差略高于 15% 视为不适配',
)

assertDeepEqual(
  evaluateSlotFit(772, 870, 'idle')?.letterbox,
  'sides',
  '竖版素材放进空闲全屏会两侧留边',
)
assertDeepEqual(
  evaluateSlotFit(1366, 768, 'checkout')?.letterbox,
  'topBottom',
  '横版素材放进收银右侧会上下留边',
)
assertEqual(evaluateSlotFit(1366, 768, 'idle')?.letterbox, null, '适配时没有留边方向')

// ---- 版式与素材比例不符的提示 ----
assertDeepEqual(
  getOrientationMismatch('Any', 1600, 1200),
  { kind: 'both' },
  '通用 + 4:3 素材：两处都会留边',
)
assertDeepEqual(
  getOrientationMismatch('Landscape', 772, 870),
  { kind: 'single', slot: 'idle', letterbox: 'sides' },
  '横版但素材是竖的：空闲全屏会两侧留边',
)
assertDeepEqual(
  getOrientationMismatch('Portrait', 1366, 768),
  { kind: 'single', slot: 'checkout', letterbox: 'topBottom' },
  '竖版但素材很宽：收银右侧会上下留边',
)
assertDeepEqual(
  getOrientationMismatch('Landscape', 3000, 1000),
  { kind: 'single', slot: 'idle', letterbox: 'topBottom' },
  '横版但素材比空闲全屏还宽：空闲全屏上下留边',
)
assertEqual(getOrientationMismatch('Any', 1366, 768), null, '通用 + 16:9：空闲全屏适配，不提示')
assertEqual(getOrientationMismatch('Any', 772, 870), null, '通用 + 竖版尺寸：收银右侧适配，不提示')
assertEqual(getOrientationMismatch('Landscape', 1366, 768), null, '横版 + 横版素材不提示')
assertEqual(getOrientationMismatch('Portrait', 772, 870), null, '竖版 + 竖版素材不提示')
assertEqual(getOrientationMismatch('Any', null, null), null, '尺寸未知（旧广告）不提示')

// ---- 宽高比展示 ----
assertEqual(formatAspectRatio(1600, 1200), '4:3', '可约分的小整数比显示为 4:3')
assertEqual(formatAspectRatio(1920, 1080), '16:9', '1920×1080 显示为 16:9')
assertEqual(formatAspectRatio(772, 870), '0.89:1', '难读的整数比改为小数比')
assertEqual(formatAspectRatio(null, 870), null, '尺寸未知不显示比例')

console.log('advertisement orientation.test: ok')
