import { buildPlaced, computePlacement, validateLayout, type LayoutContext, type LayoutHook, type LayoutShelf, type LayoutSpec } from '../engine/geometry'
import { createDesignerItem, planBatchAdd, type DesignerItem, type DropHint } from './designerLogic'
import {
  arrangeFadedGuids,
  arrangePreviewBoxes,
  ARRANGE_INSERT_RATE,
  ARRANGE_SWAP_RATE,
  decideArrange,
  overlapRate,
  planDragArrange,
  ratePercent,
  type ArrangePlan,
} from './dragArrange'

// 拖拽对调 / 插入 + 多选添加的纯逻辑测试。货架同设计器测试：节宽 900、立柱 30（板面 870）、孔距 / 首孔 25、33 列 × 41 行；间距 10。
// 100 宽商品（含价签的占位宽也是 100）：左缘 = 25 × 列 − 37.5、右缘 = 25 × 列 + 62.5；同一行紧挨着排时每隔 5 列一个（列 2、7、12…），一行最多 6 个（列 2～27）。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
}

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
const hook: LayoutHook = {
  hasPriceTagArm: true,
  armLengthMm: 200,
  dropMm: 50,
  tipReserveMm: 20,
  tagWidthMm: 52,
  tagHeightMm: 26,
  tagBottomAboveArmMm: 10,
  holeSpanX: 2,
  holeSpanY: 1,
  loadKg: null,
}
const spec = (widthMm: number): LayoutSpec => ({ widthMm, heightMm: 150, depthMm: 20, hangHoleOffsetMm: 10, unitWeightG: null, hookQtyOverrides: {}, retailPrice: 2 })
const ctx: LayoutContext = {
  shelf,
  hooks: { H: hook },
  specs: { P: spec(100), W2: spec(200), W3: spec(300) },
  bayCount: 2,
  gaps: { gapXMm: 10, gapYMm: 10 },
}
const item = (guid: string, holeCol: number, holeRow = 2, productCode = 'P', bayIndex = 1): DesignerItem =>
  createDesignerItem({ guid, productCode, hookModelGuid: 'H', bayIndex, holeCol, holeRow })

/** 吸附后的虚影：直接给出孔位（等同于 buildDropHint 的结果，免得每个用例都换算指针坐标）。 */
function hintAt(holeCol: number, holeRow = 2, productCode = 'P', bayIndex = 1): DropHint {
  const geometry = computePlacement(shelf, hook, ctx.specs[productCode], holeCol, holeRow)
  return { bayIndex, holeCol, holeRow, ok: false, conflicts: [], geometry, productCode, hookModelGuid: 'H' }
}

/** 对调 / 插入后的整份明细（不是这两种结果时直接报错）。 */
function itemsOf(plan: ArrangePlan): readonly DesignerItem[] {
  assert(plan.kind === 'swap' || plan.kind === 'insert', `应当是对调 / 插入，实际是 ${plan.kind}`)
  return plan.items
}

const cols = (plan: ArrangePlan, guids: string[]) => {
  assert(plan.kind === 'swap' || plan.kind === 'insert', `应当是对调 / 插入，实际是 ${plan.kind}`)
  return guids.map((guid) => {
    const found = plan.items.find((entry) => entry.guid === guid)
    return found ? `${found.holeRow}:${found.holeCol}` : '-'
  }).join(',')
}

// ---------- 重叠率：相交面积 ÷ 较小方块 ----------
assertEqual(overlapRate({ left: 0, right: 100, top: 0, bottom: 100 }, { left: 0, right: 100, top: 0, bottom: 100 }), 1, '完全重合 100%')
assertEqual(overlapRate({ left: 0, right: 100, top: 0, bottom: 100 }, { left: 50, right: 150, top: 0, bottom: 100 }), 0.5, '错开一半 50%')
assertEqual(overlapRate({ left: 0, right: 200, top: 0, bottom: 190 }, { left: 20, right: 60, top: 20, bottom: 60 }), 1, '小方块整个落进大方块：按较小的算 100%')
assertEqual(Math.round(overlapRate({ left: 0, right: 200, top: 0, bottom: 190 }, { left: 130, right: 250, top: 30, bottom: 180 }) * 100), 58, '设计图例子：70×150 ÷ (120×150) ≈ 58%')
assertEqual(overlapRate({ left: 0, right: 100, top: 0, bottom: 100 }, { left: 100, right: 200, top: 0, bottom: 100 }), 0, '只碰边不算重叠')
assertEqual(ARRANGE_SWAP_RATE, 0.5, '对调阈值 50%')
assertEqual(ARRANGE_INSERT_RATE, 0.2, '插入阈值 20%')
assertEqual(ratePercent(0.4783), 48, '百分比取整')

// ---------- 判定：阈值边界（直接给虚影矩形） ----------
{
  // b 放远一点，虚影只和 a 相交
  const placed = buildPlaced(ctx, [item('a', 2), item('b', 20)])
  const a = placed[0].geometry!
  const ghostShift = (dx: number) => ({ bayIndex: 1, rect: { left: a.left + dx, right: a.right + dx, top: a.top, bottom: a.bottom } })
  assertEqual(decideArrange(placed, ghostShift(80), 'x', true).kind, 'none', '只压住 20%：普通移动（> 20% 才插入）')
  assertEqual(decideArrange(placed, ghostShift(79), 'x', true).kind, 'insert', '压住 21%：插入')
  assertEqual(decideArrange(placed, ghostShift(50), 'x', true).kind, 'swap', '压住正好 50%：对调')
  assertEqual(decideArrange(placed, ghostShift(50), 'x', false).kind, 'insert', '新商品（不允许对调）压住 50%：插入')
  assertEqual(decideArrange(placed, ghostShift(0), 'a', true).kind, 'none', '被拖动的条目自己不参与')
  assertEqual(decideArrange(placed, { bayIndex: 2, rect: a }, 'x', true).kind, 'none', '别的节不参与')
  const before = decideArrange(placed, ghostShift(-70), 'x', true)
  assert(before.kind === 'insert' && before.position === 'before' && before.index === 0, '虚影中心在 T 左半边：插在 T 前面')
}

// ---------- 对调（同宽）：最右的 c 拖到 a 上 ----------
{
  const items = [item('a', 2), item('b', 7), item('c', 12)]
  const plan = planDragArrange({ ctx, items, hint: hintAt(2), dragged: items[2], isNew: false })
  assertEqual(plan.kind, 'swap', '压住 a 100%：对调')
  assertEqual(cols(plan, ['a', 'b', 'c']), '2:12,2:7,2:2', 'a、c 交换，b 不动')
  assertEqual(validateLayout(ctx, itemsOf(plan)).stats.conflictCount, 0, '对调后没有冲突')
  assertEqual(arrangeFadedGuids(plan).has('a') && !arrangeFadedGuids(plan).has('c'), true, '预览淡化被换走的 a，不淡化正在拖的 c')
  assertEqual(arrangePreviewBoxes(ctx, items, plan).length, 1, '预览框只画被换走的 a')
}

// ---------- 对调（同一行、宽度不同）：左槽按左缘、右槽按右缘对齐，间距不变 ----------
{
  // n 宽 100 在列 2（12.5～112.5）；w 宽 200 在列 9（137.5～337.5），两者净间距 25
  const items = [item('n', 2), item('w', 9, 2, 'W2')]
  assertEqual(validateLayout(ctx, items).stats.conflictCount, 0, '对调前是干净的')
  const plan = planDragArrange({ ctx, items, hint: hintAt(11), dragged: items[0], isNew: false })
  assertEqual(plan.kind, 'swap', '窄的拖到宽的上：对调')
  // w 左缘对齐到 12.5 → 列 4；n 右缘对齐到 337.5 → 列 11
  assertEqual(cols(plan, ['n', 'w']), '2:11,2:4', '宽的换到左边按左缘对齐，窄的换到右边按右缘对齐')
  assertEqual(validateLayout(ctx, itemsOf(plan)).stats.conflictCount, 0, '对调后没有冲突')
}

// ---------- 对调后与邻居冲突：拒绝 ----------
{
  // b 两边各留了空：200 宽的虚影只压住 b（100%）→ 对调；可 200 宽的换进 b 的槽位会压到 c → 拒绝
  const items = [item('a', 2), item('b', 9), item('c', 16), item('wide', 9, 14, 'W2')]
  assertEqual(validateLayout(ctx, items).stats.conflictCount, 0, '拒绝用例的初始布局是干净的')
  const plan = planDragArrange({ ctx, items, hint: hintAt(9, 2, 'W2'), dragged: items[3], isNew: false })
  assert(plan.kind === 'reject' && plan.action === 'swap' && plan.reason === 'conflict', `对调会冲突：拒绝（实际 ${plan.kind}）`)
  // 宽虚影同时压住相邻两个（各 > 20%）时，相邻插入规则优先
  const wideOverPair = planDragArrange({ ctx, items: [item('a', 2), item('b', 7), item('c', 12), item('wide', 9, 14, 'W2')], hint: hintAt(8, 2, 'W2'), dragged: item('wide', 9, 14, 'W2'), isNew: false })
  assertEqual(wideOverPair.kind === 'insert' && wideOverPair.position, 'between', '同时压住相邻两个：插入优先于对调')
}

// ---------- 插入（紧挨着的一行）：新商品压住 a 25%、b 50% → 插到两者之间，后面整体右移 ----------
{
  const items = [item('a', 2), item('b', 7), item('c', 12), item('d', 17)]
  const dragged = item('new', 5)
  const plan = planDragArrange({ ctx, items, hint: hintAt(5), dragged, isNew: true })
  assert(plan.kind === 'insert', `相邻两个都超过 20%：插入（实际 ${plan.kind}）`)
  assertEqual(plan.position, 'between', '插到两者之间')
  assertEqual(plan.targets.map((target) => `${target.guid}:${ratePercent(target.rate)}`).join(','), 'a:25,b:50', '压住的两个商品与重叠率')
  assertEqual(cols(plan, ['a', 'new', 'b', 'c', 'd']), '2:2,2:7,2:12,2:17,2:22', '新商品占 b 的原位，b、c、d 各右移 5 列（间距不变）')
  assertEqual(plan.shiftCount, 3, '后移 3 个')
  assertEqual(plan.wrapCount, 0, '没有换行')
  assertEqual(plan.items.length, 5, '新商品追加到明细')
  assertEqual(validateLayout(ctx, plan.items).stats.conflictCount, 0, '插入后没有冲突')
  assert(plan.line.xMm > 112.5 && plan.line.xMm < 137.5, '插入线画在 a、b 之间的间隙里')
  assertEqual(arrangePreviewBoxes(ctx, items, plan).filter((box) => box.tone === 'shift').length, 3, '预览：3 个蓝色后移框')
}

// ---------- 插入点前面本来就有空位：放进空位，不推后面的商品 ----------
{
  const items = [item('a', 2), item('b', 12)]
  const plan = planDragArrange({ ctx, items, hint: hintAt(9), dragged: item('new', 9), isNew: true })
  assert(plan.kind === 'insert', '压住 b 25%：插入')
  assertEqual(plan.position, 'before', '虚影中心在 b 左边：插在 b 前面')
  assertEqual(cols(plan, ['a', 'new', 'b']), '2:2,2:7,2:12', '放进 a、b 之间的空位，b 不动')
  assertEqual(plan.shiftCount, 0, '没有后移')
}

// ---------- 同一行往右拖：中间的商品左移补位（列表拖动排序） ----------
{
  const items = [item('a', 2), item('b', 7), item('c', 12), item('d', 17)]
  // a 拖到 c、d 之间：虚影列 15 压住 c 25%、d 50%
  const plan = planDragArrange({ ctx, items, hint: hintAt(15), dragged: items[0], isNew: false })
  assert(plan.kind === 'insert', `同行往右插入（实际 ${plan.kind}）`)
  assertEqual(cols(plan, ['a', 'b', 'c', 'd']), '2:12,2:2,2:7,2:17', 'b、c 左移补上 a 的空当，a 落在 c 后面，d 不动')
  assertEqual(validateLayout(ctx, plan.items).stats.conflictCount, 0, '没有冲突')
}

// ---------- 同一行往左拖：插入点到原位置之间的商品右移，原空当吸收推移 ----------
{
  const items = [item('a', 2), item('b', 7), item('c', 12), item('d', 17), item('e', 27)]
  // d 拖到 a、b 之间：虚影列 5 压住 a 25%、b 50%
  const plan = planDragArrange({ ctx, items, hint: hintAt(5), dragged: items[3], isNew: false })
  assert(plan.kind === 'insert', `同行往左插入（实际 ${plan.kind}）`)
  assertEqual(cols(plan, ['a', 'd', 'b', 'c', 'e']), '2:2,2:7,2:12,2:17,2:27', 'b、c 右移到 d 空出来的位置为止，e 不动')
  assertEqual(plan.shiftCount, 2, '只后移 2 个')
}

// ---------- 拖回自己原来的位置：什么都不做 ----------
{
  const items = [item('a', 2), item('b', 7), item('c', 12)]
  assertEqual(planDragArrange({ ctx, items, hint: hintAt(5), dragged: items[1], isNew: false }).kind, 'noop', '插入点就是自己原来的位置')
}

// ---------- 插入 → 末尾换到下一行左侧空位（只找空位，不推下一行） ----------
const fullRow = ['r1', 'r2', 'r3', 'r4', 'r5', 'r6'].map((guid, index) => item(guid, 2 + index * 5))
{
  const nextRow = [item('n1', 22, 12), item('n2', 27, 12)]
  const items = [...fullRow, ...nextRow]
  assertEqual(validateLayout(ctx, items).stats.conflictCount, 0, '两行初始布局是干净的')
  const plan = planDragArrange({ ctx, items, hint: hintAt(5), dragged: item('new', 5), isNew: true })
  assert(plan.kind === 'insert', `本行满：插入并换行（实际 ${plan.kind}）`)
  assertEqual(plan.wrapCount, 1, '末尾 1 个换行')
  assertEqual(plan.shiftCount, 4, '后移 4 个')
  assertEqual(cols(plan, ['r6', 'n1', 'n2']), '12:2,12:22,12:27', 'r6 换到下一行左侧空位，下一行原有商品不动')
  assertEqual(validateLayout(ctx, plan.items).stats.conflictCount, 0, '换行后没有冲突')
  const boxes = arrangePreviewBoxes(ctx, items, plan)
  assertEqual(boxes.filter((box) => box.tone === 'wrap').length, 1, '预览：1 个橙色换行框')
}

// ---------- 下一行也满了：整次拒绝 ----------
{
  const nextRowFull = ['n1', 'n2', 'n3', 'n4', 'n5', 'n6'].map((guid, index) => item(guid, 2 + index * 5, 12))
  const items = [...fullRow, ...nextRowFull]
  const plan = planDragArrange({ ctx, items, hint: hintAt(5), dragged: item('new', 5), isNew: true })
  assert(plan.kind === 'reject' && plan.action === 'insert' && plan.reason === 'noRoom', `本节放不下：拒绝（实际 ${plan.kind}）`)
  assert(plan.line !== null, '拒绝时仍给出插入线位置（画成红色）')
}

// ---------- 下面还没有商品：换到本行下方第一个放得下的位置 ----------
{
  const plan = planDragArrange({ ctx, items: fullRow, hint: hintAt(5), dragged: item('new', 5), isNew: true })
  assert(plan.kind === 'insert' && plan.wrapCount === 1, '换到下方新的一行')
  const wrapped = plan.items.find((entry) => entry.guid === 'r6')!
  assert(wrapped.holeRow > 2 && wrapped.holeCol === 2, '在本行下方、最左边')
  assertEqual(validateLayout(ctx, plan.items).stats.conflictCount, 0, '没有冲突')
}

// ---------- 普通移动：没压住别人时返回 none（沿用原来的落点逻辑） ----------
assertEqual(planDragArrange({ ctx, items: [item('a', 2)], hint: hintAt(20), dragged: item('a', 2), isNew: false }).kind, 'none', '空白处：普通移动')
assertEqual(planDragArrange({ ctx, items: [], hint: { ...hintAt(20), geometry: null }, dragged: item('x', 20), isNew: true }).kind, 'none', '虚影没有几何（孔位越界）：普通移动')

// ---------- 多选添加：按顺序逐个找第一个空位，前面放好的会占位 ----------
{
  const outcomes = planBatchAdd(ctx, [], [
    { productCode: 'P', hookModelGuid: 'H' },
    { productCode: 'P', hookModelGuid: 'H' },
    { productCode: 'P', hookModelGuid: null },
    { productCode: 'P', hookModelGuid: 'H' },
  ], { makeGuid: (index) => `batch-${index}` })
  assertEqual(outcomes.map((outcome) => outcome.status).join(','), 'ok,ok,noHook,ok', '没有挂钩的记为 noHook，其余放好')
  const placedCols = outcomes.filter((outcome) => outcome.status === 'ok').map((outcome) => (outcome as { slot: { holeCol: number } }).slot.holeCol)
  assert(placedCols[0] < placedCols[1] && placedCols[1] < placedCols[2], '依次往右放，不会叠在一起')
  const okItems = outcomes.flatMap((outcome) => (outcome.status === 'ok' ? [outcome.item] : []))
  assertEqual(okItems[0].guid, 'batch-0', 'guid 由调用方生成')
  assertEqual(validateLayout(ctx, okItems).stats.conflictCount, 0, '整批放好后没有冲突')
  const limited = planBatchAdd(ctx, [item('a', 2)], [{ productCode: 'P', hookModelGuid: 'H' }, { productCode: 'P', hookModelGuid: 'H' }], { limit: 2 })
  assertEqual(limited.map((outcome) => outcome.status).join(','), 'ok,tooMany', '到方案明细上限后不再添加')
  const oneBay: LayoutContext = { ...ctx, bayCount: 1 }
  const crowded = Array.from({ length: 70 }, () => ({ productCode: 'P', hookModelGuid: 'H' as string | null }))
  const crowdedOutcomes = planBatchAdd(oneBay, [], crowded)
  assert(crowdedOutcomes.some((outcome) => outcome.status === 'noRoom'), '一节放满后其余记为 noRoom')
  const twoBays = planBatchAdd(ctx, [], crowded)
  assert(twoBays.some((outcome) => outcome.status === 'ok' && outcome.slot.bayIndex === 2), '本节放满会接着放到下一节')
}

console.log('dragArrange.test: ok')
