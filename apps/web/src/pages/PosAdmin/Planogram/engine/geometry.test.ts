import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import {
  bayBoardOriginX,
  buildPlaced,
  checkPlacement,
  computePlacement,
  isHoleInRange,
  pointToBoard,
  resolvePerHookQty,
  snapAnchorToPoint,
  validateLayout,
  type LayoutContext,
  type LayoutHook,
  type LayoutItem,
  type LayoutShelf,
  type LayoutSpec,
} from './geometry'

// 几何引擎测试：读取与后端共用的夹具 layout-cases.json，对全部用例逐条对照（冲突按 (code, itemGuid, otherItemGuid, severity) 排序后逐项比，统计逐字段比）；
// 再补几条夹具之外的性质：拖拽判定 checkPlacement 与整体校验口径一致、吸附与坐标换算。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertNear(actual: number, expected: number, tolerance: number, message: string) {
  if (Math.abs(actual - expected) > tolerance) {
    throw new Error(`${message}. Expected: ${expected} ± ${tolerance}, received: ${actual}`)
  }
}

interface FixtureConflict {
  code: string
  itemGuid: string
  otherItemGuid?: string
  severity: string
}

interface FixtureCase {
  name: string
  shelf: LayoutShelf
  hooks: Record<string, LayoutHook>
  specs: Record<string, LayoutSpec>
  bayCount: number
  gaps: { gapXMm: number; gapYMm: number }
  items: LayoutItem[]
  expectedConflicts: FixtureConflict[]
  expectedStats: { itemCount: number; skuCount: number; unitCount: number; retailValue: number; fillPercent: number; conflictCount: number }
}

const fixture = JSON.parse(readFileSync(resolve('src/pages/PosAdmin/Planogram/engine/fixtures/layout-cases.json'), 'utf8')) as {
  version: number
  toleranceMm: number
  cases: FixtureCase[]
}

assertEqual(fixture.version, 1, '夹具版本')
assert(fixture.cases.length >= 20, `夹具用例数量不应少于 20 个，实际 ${fixture.cases.length}`)

const conflictKey = (c: { code: string; itemGuid: string; otherItemGuid?: string | null; severity: string }) =>
  `${c.code}|${c.itemGuid}|${c.otherItemGuid ?? ''}|${c.severity}`

function contextOf(testCase: FixtureCase): LayoutContext {
  return { shelf: testCase.shelf, hooks: testCase.hooks, specs: testCase.specs, bayCount: testCase.bayCount, gaps: testCase.gaps }
}

// ---------------------------------------------------------------------------
// ① 夹具：冲突与统计逐项对照
// ---------------------------------------------------------------------------
for (const testCase of fixture.cases) {
  const result = validateLayout(contextOf(testCase), testCase.items)
  const actual = result.conflicts.map(conflictKey).sort()
  const expected = testCase.expectedConflicts.map(conflictKey).sort()
  assertEqual(actual.join('\n'), expected.join('\n'), `${testCase.name}：冲突列表`)
  const stats = result.stats
  const want = testCase.expectedStats
  assertEqual(stats.itemCount, want.itemCount, `${testCase.name}：itemCount`)
  assertEqual(stats.skuCount, want.skuCount, `${testCase.name}：skuCount`)
  assertEqual(stats.unitCount, want.unitCount, `${testCase.name}：unitCount`)
  assertNear(stats.retailValue, want.retailValue, 0.005, `${testCase.name}：retailValue`)
  assertEqual(stats.fillPercent, want.fillPercent, `${testCase.name}：fillPercent`)
  assertEqual(stats.conflictCount, want.conflictCount, `${testCase.name}：conflictCount`)
}

// ---------------------------------------------------------------------------
// ② 性质：拖拽判定 checkPlacement ≡ 整体校验里「该条目有没有 error 级冲突」
//    做法：每个条目拿出来当候选，others 是其余条目；结果（是否可放 + 冲突 code / 对方）必须与整体校验里该条目的 error 冲突完全一致。
// ---------------------------------------------------------------------------
let checkedItems = 0
for (const testCase of fixture.cases) {
  const ctx = contextOf(testCase)
  const whole = validateLayout(ctx, testCase.items)
  for (const [index, item] of testCase.items.entries()) {
    const others = buildPlaced(ctx, testCase.items.filter((_, i) => i !== index))
    const check = checkPlacement(ctx, others, item)
    const wholeKeys = whole.conflicts
      .filter((c) => c.severity === 'error' && c.itemGuid === item.guid)
      .map((c) => `${c.code}|${c.otherItemGuid ?? ''}`)
      .sort()
    const checkKeys = check.conflicts.map((c) => `${c.code}|${c.otherItemGuid ?? ''}`).sort()
    assertEqual(checkKeys.join(','), wholeKeys.join(','), `${testCase.name} / ${item.guid}：拖拽判定与整体校验一致`)
    assertEqual(check.ok, wholeKeys.length === 0, `${testCase.name} / ${item.guid}：ok 标志`)
    checkedItems += 1
  }
}
assert(checkedItems > 50, '一致性性质应覆盖足够多的条目')

// ---------------------------------------------------------------------------
// ③ 手算几何：设计稿样例（900×1200、孔距 25、首孔 25、双钩带价签 200/50/52×26/底边高 10）
// ---------------------------------------------------------------------------
const shelf: LayoutShelf = {
  bayWidthMm: 900,
  totalHeightMm: 1200,
  uprightWidthMm: 30,
  baseTopHeightMm: 150,
  holePitchXMm: 25,
  holePitchYMm: 25,
  firstHoleLeftMm: 25,
  firstHoleTopMm: 25,
  holeCols: 33,
  holeRows: 41,
}
const doubleHook: LayoutHook = {
  hasPriceTagArm: true,
  armLengthMm: 200,
  dropMm: 50,
  tipReserveMm: 20,
  tagWidthMm: 52,
  tagHeightMm: 26,
  tagBottomAboveArmMm: 10,
  holeSpanX: 2,
  holeSpanY: 1,
  loadKg: 3,
}
const spec: LayoutSpec = { widthMm: 90, heightMm: 185, depthMm: 20, hangHoleOffsetMm: 10, unitWeightG: 100, hookQtyOverrides: {}, retailPrice: 3.5 }

const placement = computePlacement(shelf, doubleHook, spec, 11, 2)
assertEqual(placement.holeX, 275, '孔心 x = 25 + (11−1)×25')
assertEqual(placement.holeY, 50, '孔心 y = 25 + (2−1)×25')
assertEqual(placement.centerX, 287.5, '双钩中心 = 孔心 + 12.5')
assertEqual(placement.left, 242.5, '左 = 中心 − 45')
assertEqual(placement.right, 332.5, '右 = 中心 + 45')
assertEqual(placement.productTop, 90, '商品顶 = 50 + 50 − 10')
assertEqual(placement.bottom, 275, '商品底 = 90 + 185')
// 价签顶 = 孔心 + 钩高 − 价签底边高 − 价签高 = 50 + 50 − 10 − 26 = 64，高于商品顶 90 → 占位顶取 64
assertEqual(placement.tagTop, 64, '价签顶（与 plano.js 同式）')
assertEqual(placement.top, 64, '占位顶取价签顶与商品顶中较高者')

const noTag = computePlacement(shelf, { ...doubleHook, hasPriceTagArm: false, tagWidthMm: 999, tagHeightMm: 999 }, spec, 11, 2)
assertEqual(noTag.tagTop, null, '不带价签臂没有价签顶')
assertEqual(noTag.effectiveWidth, 90, '不带价签臂时价签宽不参与有效宽')
assertEqual(noTag.top, 90, '不带价签臂时占位顶 = 商品顶')

const wideTag = computePlacement(shelf, { ...doubleHook, tagWidthMm: 130 }, spec, 11, 2)
assertEqual(wideTag.effectiveWidth, 130, '价签比商品宽时取价签宽')

assertEqual(isHoleInRange(shelf, doubleHook, 32, 1), true, '双钩占 32、33 列：在孔阵内')
assertEqual(isHoleInRange(shelf, doubleHook, 33, 1), false, '双钩锚点在第 33 列会占到第 34 列：超出')
assertEqual(isHoleInRange(shelf, doubleHook, 0, 1), false, '列号从 1 起')
assertEqual(isHoleInRange(shelf, doubleHook, 1, 42), false, '行号超出 41 行')

// 每钩挂数：条目值 > 规格覆盖 > 建议值（⌊(200−20)÷20⌋ = 9）
assertEqual(resolvePerHookQty({ hookModelGuid: 'h', perHookQty: null }, doubleHook, spec), 9, '建议值')
assertEqual(resolvePerHookQty({ hookModelGuid: 'h', perHookQty: null }, doubleHook, { ...spec, hookQtyOverrides: { h: 12 } }), 12, '规格覆盖优先于建议值')
assertEqual(resolvePerHookQty({ hookModelGuid: 'h', perHookQty: 15 }, doubleHook, { ...spec, hookQtyOverrides: { h: 12 } }), 15, '条目值优先于规格覆盖')
assertEqual(resolvePerHookQty({ hookModelGuid: 'h', perHookQty: 0 }, doubleHook, spec), 9, '条目值 0 视为没填')
assertEqual(resolvePerHookQty({ hookModelGuid: 'h', perHookQty: null }, doubleHook, { ...spec, hookQtyOverrides: { h: 0 } }), 9, '覆盖值 0 视为没填')

// ---------------------------------------------------------------------------
// ④ 吸附与坐标换算
// ---------------------------------------------------------------------------
assertEqual(bayBoardOriginX(shelf, 1), 15, '第 1 节板面左缘 = 半根立柱')
assertEqual(bayBoardOriginX(shelf, 2), 915, '第 2 节板面左缘 = 900 + 15')
const p1 = pointToBoard(shelf, 2, 300, 120)
assertEqual(p1.bayIndex, 1, '300 落在第 1 节')
assertEqual(p1.x, 285, '节内板面 x = 300 − 15')
const p2 = pointToBoard(shelf, 2, 1000, 120)
assertEqual(p2.bayIndex, 2, '1000 落在第 2 节')
assertEqual(p2.x, 85, '1000 − 915')
assertEqual(pointToBoard(shelf, 2, 5000, 0).bayIndex, 2, '超出右端夹在最后一节')
assertEqual(pointToBoard(shelf, 2, -50, 0).bayIndex, 1, '超出左端夹在第一节')

const snapped = snapAnchorToPoint(shelf, doubleHook, 276, 51)
assertEqual(snapped.col, 11, '最近孔列：(276−25)/25 = 10.04 → 11 列')
assertEqual(snapped.row, 2, '最近孔行')
assertEqual(snapAnchorToPoint(shelf, doubleHook, 9999, 9999).col, 32, '双钩锚点最右只能到第 32 列（占 32、33）')
assertEqual(snapAnchorToPoint(shelf, doubleHook, 9999, 9999).row, 41, '最下夹在 41 行')
assertEqual(snapAnchorToPoint(shelf, doubleHook, -500, -500).col, 1, '最左夹在第 1 列')
assertEqual(snapAnchorToPoint({ ...shelf, holePitchXMm: 25.4, firstHoleLeftMm: 12.7 }, doubleHook, 12.7 + 25.4 * 4 + 3, 0).col, 5, '25.4 孔距的小数坐标吸附')

console.log('geometry.test: ok')
