import type { PlanogramDetailDto } from '../../../../types/planogram'
import { validateLayout, type LayoutContext, type LayoutHook, type LayoutShelf, type LayoutSpec } from '../engine/geometry'
import {
  buildDropHint,
  buildSaveInput,
  conflictLocateList,
  createDesignerItem,
  distributeRow,
  docFromDetail,
  findCopyRightSlot,
  findFirstFreeSlot,
  HISTORY_LIMIT,
  itemFootLabel,
  itemFootLabelPrint,
  nextLocateGuid,
  patchItem,
  pickHookForProduct,
  primaryConflict,
  productColor,
  pushHistory,
  removeItemByGuid,
  sameConflictSet,
  sortChecklistItems,
  wrapLabel,
  type DesignerDoc,
  type DesignerItem,
} from './designerLogic'

// 设计器纯逻辑测试：数字取自设计稿样例（900×1200、孔距 / 首孔 25、双钩带价签 200 / 50 / 52×26 / 底边高 10、圆珠笔 90×185×20）。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
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
  loadKg: 3,
}
const pen: LayoutSpec = { widthMm: 90, heightMm: 185, depthMm: 20, hangHoleOffsetMm: 10, unitWeightG: 20, hookQtyOverrides: {}, retailPrice: 3.5 }
const ctx: LayoutContext = {
  shelf,
  hooks: { H: hook },
  specs: { PEN: pen },
  bayCount: 2,
  gaps: { gapXMm: 10, gapYMm: 10 },
}
const item = (guid: string, bayIndex: number, holeCol: number, holeRow: number, productCode = 'PEN'): DesignerItem =>
  createDesignerItem({ guid, productCode, hookModelGuid: 'H', bayIndex, holeCol, holeRow })

// ---------- 撤销栈：上限 50，超出丢最早的 ----------
const emptyDoc: DesignerDoc = { name: 'a', notes: '', shelfModelGuid: 's', bayCount: 2, gapXMm: 10, gapYMm: 10, items: [] }
let past: DesignerDoc[] = []
for (let i = 0; i < 60; i += 1) past = pushHistory(past, { ...emptyDoc, name: `v${i}` })
assertEqual(past.length, HISTORY_LIMIT, '撤销栈上限 50')
assertEqual(HISTORY_LIMIT, 50, '上限常量是 50')
assertEqual(past[0].name, 'v10', '丢掉最早的 10 条')
assertEqual(past[past.length - 1].name, 'v59', '最新的在末尾')

// ---------- 文档与保存请求 ----------
const detail = {
  header: { guid: 'p1', name: ' 文具挂板 ', notes: null, shelfModelGuid: 's', shelfModelName: '', bayCount: 2, gapXMm: 10, gapYMm: 8, status: 0, version: 3, itemCount: 0, skuCount: 0, unitCount: 0, fillPercent: 0, conflictCount: 0, publishedVersion: null, publishedAt: null, updatedAt: null, updatedBy: null },
  rowVersion: '2026-11-04T10:12:00.0000000Z',
  items: [
    { guid: 'b', bayIndex: 1, face: 0, itemType: 1, productCode: 'PEN', hookModelGuid: 'H', holeCol: 9, holeRow: 2, perHookQty: 12, sortOrder: 5, specChanged: true },
    { guid: 'a', bayIndex: 1, face: 0, itemType: 1, productCode: 'PEN', hookModelGuid: 'H', holeCol: 3, holeRow: 2, perHookQty: null, sortOrder: 1 },
  ],
  shelfModel: {},
  hookModels: [],
  productSpecs: [],
  conflicts: [],
  stats: { itemCount: 2, skuCount: 1, unitCount: 0, retailValue: 0, fillPercent: 0, conflictCount: 0 },
} as unknown as PlanogramDetailDto
const doc = docFromDetail(detail)
assertEqual(doc.items.map((entry) => entry.guid).join(','), 'a,b', '明细按 sortOrder 排序')
assertEqual(doc.items[1].specChanged, true, '保留「参数已变」标记')
assertEqual(doc.notes, '', '没有备注时用空串')
assertEqual(doc.gapYMm, 8, '上下间距取方案值')
const save = buildSaveInput(doc, 'rv-1')
assertEqual(save.name, '文具挂板', '名称去首尾空格')
assertEqual(save.notes, null, '空备注提交 null')
assertEqual(save.rowVersion, 'rv-1', '带 rowVersion')
assertEqual(save.items.map((entry) => entry.sortOrder).join(','), '0,1', 'sortOrder 按当前顺序重排')
assert(save.items.every((entry) => entry.face === 0 && entry.itemType === 1), 'face 恒 0、itemType 恒 1')
assert(save.items.every((entry) => !('specChanged' in entry)), '请求里不带 specChanged')
assertEqual(save.items[1].perHookQty, 12, '手填挂数原样提交')

// ---------- 条目增删改 ----------
const base = [item('a', 1, 3, 2), item('b', 1, 9, 2)]
assertEqual(patchItem(base, 'a', { holeCol: 3 }), base, '值没变时返回原数组（不产生新快照）')
assertEqual(patchItem(base, 'zzz', { holeCol: 4 }), base, '找不到时返回原数组')
const patched = patchItem(base, 'a', { holeCol: 4 })
assert(patched !== base && patched[0].holeCol === 4 && base[0].holeCol === 3, '改值产生新数组且不改原数组')
assert(patched[1] === base[1], '未改动的条目保持引用')
assertEqual(removeItemByGuid(base, 'a').length, 1, '删除条目')
assertEqual(removeItemByGuid(base, 'x'), base, '删除不存在的条目返回原数组')

// ---------- 默认挂钩 ----------
const activeHooks = [{ guid: 'H' }, { guid: 'H2' }]
assertEqual(pickHookForProduct('H2', activeHooks, 'H'), 'H2', '默认挂钩优先')
assertEqual(pickHookForProduct('GONE', activeHooks, 'H'), 'H', '默认挂钩已停用 / 不存在：取左栏当前挂钩')
assertEqual(pickHookForProduct(null, activeHooks, 'H2'), 'H2', '没有默认挂钩：取当前挂钩')
assertEqual(pickHookForProduct(null, activeHooks, null), 'H', '都没有：取第一个启用的挂钩')
assertEqual(pickHookForProduct(null, [], null), null, '没有任何启用挂钩')

// ---------- 拖拽落点：吸附 + 可放判定 ----------
// 货架整体坐标：第 1 节板面左缘 15；第 2 节 915。指针在第 1 节 (x=15+275=290, y=51) → 第 11 列、第 2 行
const hintOk = buildDropHint(ctx, [], { xMm: 290, yMm: 51 }, { productCode: 'PEN', hookModelGuid: 'H' })
assert(hintOk !== null, '有参数有挂钩可以出提示')
assertEqual(`${hintOk.bayIndex}/${hintOk.holeCol}/${hintOk.holeRow}`, '1/11/2', '吸附到最近孔位')
assertEqual(hintOk.ok, true, '空板面可放')
const hintSecondBay = buildDropHint(ctx, [], { xMm: 915 + 275, yMm: 51 }, { productCode: 'PEN', hookModelGuid: 'H' })
assertEqual(hintSecondBay?.bayIndex, 2, '指针在第 2 节 → 第 2 节')
assertEqual(hintSecondBay?.holeCol, 11, '第 2 节内的列号从该节板面起算')
assertEqual(buildDropHint(ctx, [], { xMm: 290, yMm: 51 }, { productCode: 'NOPE', hookModelGuid: 'H' }), null, '没有陈列参数不出提示')
assertEqual(buildDropHint(ctx, [], { xMm: 290, yMm: 51 }, { productCode: 'PEN', hookModelGuid: 'NOPE' }), null, '没有挂钩型号不出提示')

import { buildPlaced } from '../engine/geometry'
const placedOne = buildPlaced(ctx, [item('x', 1, 11, 2)])
const hintClash = buildDropHint(ctx, placedOne, { xMm: 290, yMm: 51 }, { productCode: 'PEN', hookModelGuid: 'H' })
assertEqual(hintClash?.ok, false, '落在已有商品上不可放')
assert(hintClash?.conflicts.some((entry) => entry.code === 'HOOK_HOLE_CLASH' && entry.otherItemGuid === 'x'), '同孔：HOOK_HOLE_CLASH 且指向对方')
assertEqual(buildDropHint(ctx, placedOne, { xMm: 290, yMm: 51 }, { productCode: 'PEN', hookModelGuid: 'H' }, 'x')?.ok, true, '拖动已有条目时排除它自己')
const hintEdge = buildDropHint(ctx, [], { xMm: 15 + 25, yMm: 51 }, { productCode: 'PEN', hookModelGuid: 'H' })
assertEqual(hintEdge?.holeCol, 1, '最左一列')
assertEqual(hintEdge?.ok, false, '第 1 列左缘 −7.5 mm 越过板面')
assertEqual(hintEdge?.conflicts[0].code, 'OUT_OF_BOARD', '越界')

// ---------- 向右复制 ----------
// 圆珠笔占位宽 90、间距 10：右缘 + 10 + 半宽 = 中心 + 100 → 向右 4 列
const itemA = item('a', 1, 5, 2)
const copySlot = findCopyRightSlot(ctx, [itemA], 'a')
assertEqual(`${copySlot?.bayIndex}/${copySlot?.holeCol}/${copySlot?.holeRow}`, '1/9/2', '向右复制：净间距恰好等于 10 的第一个孔位')
const occupied = [itemA, item('b', 1, 9, 2)]
assertEqual(findCopyRightSlot(ctx, occupied, 'a')?.holeCol, 13, '右边一个位置被占了，继续向右找下一个可放孔位')
assertEqual(findCopyRightSlot(ctx, [item('r', 1, 32, 2)], 'r'), null, '右侧没有位置返回 null')
assertEqual(findCopyRightSlot(ctx, [itemA], 'missing'), null, '条目不存在返回 null')

// ---------- 第一个空位（键盘 / 按钮添加） ----------
// 第 1 列中心 37.5、左缘 −7.5 越界；第 2 列左缘 17.5 ≥ 间距 10 才可放；行 1 顶 = 价签顶 39 ≥ 10
const firstFree = findFirstFreeSlot(ctx, [], { productCode: 'PEN', hookModelGuid: 'H' })
assertEqual(`${firstFree?.bayIndex}/${firstFree?.holeCol}/${firstFree?.holeRow}`, '1/2/1', '空方案的第一个空位')
// 第 2 列已占（17.5～107.5）：下一件左缘 ≥ 117.5 → 中心 ≥ 162.5 → 第 6 列（第 3～5 列间距不足）
assertEqual(findFirstFreeSlot(ctx, [item('x', 1, 2, 1)], { productCode: 'PEN', hookModelGuid: 'H' })?.holeCol, 6, '被占后挑下一个可放位置')
assertEqual(findFirstFreeSlot(ctx, [], { productCode: 'NOPE', hookModelGuid: 'H' }), null, '没有参数找不到')

// ---------- 整行均分 ----------
// 初始间距 4 列（100 mm）：净间距恰好 10，布局本身是干净的
const rowItems = [item('a', 1, 3, 1), item('b', 1, 7, 1), item('c', 1, 11, 1)]
const distributed = distributeRow(ctx, rowItems, 'b')
assertEqual(validateLayout(ctx, rowItems).stats.conflictCount, 0, '均分前是干净的')
assert(distributed.ok, '三件可以均分')
assert(distributed.moved > 0, '有条目被挪动')
const afterRow = [...distributed.items].sort((x, y) => x.holeCol - y.holeCol)
assertEqual(afterRow.length, 3, '条目数不变')
assert(afterRow[0].holeCol < afterRow[1].holeCol && afterRow[1].holeCol < afterRow[2].holeCol, '顺序不变')
assertEqual(validateLayout(ctx, distributed.items).stats.conflictCount, 0, '均分后没有冲突')
// 理想：870 − 270 = 600，等距 150 → 左缘 150 / 390 / 630 → 中心 195 / 435 / 675 → 列 8（195−12.5−25 = 157.5 → 6.3+1）…按最近孔位取整，误差不超过半个孔距
const centers = afterRow.map((entry) => 25 + (entry.holeCol - 1) * 25 + 12.5)
for (const [index, expected] of [195, 435, 675].entries()) {
  assert(Math.abs(centers[index] - expected) <= 12.5 + 1e-9, `第 ${index + 1} 件中心 ${centers[index]} 距理想位置 ${expected} 不超过半个孔距`)
}
assertEqual(distributeRow(ctx, [item('a', 1, 3, 1)], 'a').ok, false, '只有一件不用均分')
assertEqual((distributeRow(ctx, [item('a', 1, 3, 1)], 'a') as { reason: string }).reason, 'tooFew', '原因 tooFew')
assertEqual((distributeRow(ctx, rowItems, 'zzz') as { reason: string }).reason, 'invalid', '条目不存在')
// 不同节、不同行的条目不参与同一行
const mixed = [...rowItems, item('other-row', 1, 20, 30), item('other-bay', 2, 5, 1)]
const mixedResult = distributeRow(ctx, mixed, 'a')
assert(mixedResult.ok, '混有别行别节的条目时只均分本行')
assertEqual(mixedResult.items.find((entry) => entry.guid === 'other-row')?.holeCol, 20, '别行条目不动')
assertEqual(mixedResult.items.find((entry) => entry.guid === 'other-bay')?.holeCol, 5, '别节条目不动')
// 放不下：10 件 90 宽商品 + 间距超过 870
const tooMany = Array.from({ length: 10 }, (_, i) => item(`m${i}`, 1, 2 + i * 3, 1))
assertEqual((distributeRow(ctx, tooMany, 'm0') as { reason: string }).reason, 'noRoom', '总宽 + 最小间距超出板面：放不下')
// 均分会撞上下面几行的高商品：被拒绝，不改动
const blockedRow = [item('a', 1, 3, 1), item('b', 1, 7, 1), item('d', 1, 11, 1), item('c', 1, 16, 4)]
assertEqual(validateLayout(ctx, blockedRow).stats.conflictCount, 0, '均分前是干净的')
const blocked = distributeRow(ctx, blockedRow, 'a')
assertEqual(blocked.ok, false, '均分后第 2 件会压到第 4 行的商品：拒绝')
assertEqual((blocked as { reason: string }).reason, 'conflict', '原因 conflict')

// ---------- 冲突定位 ----------
const conflicts = [
  { itemGuid: 'c', severity: 'error' as const, code: 'OVERLAP' },
  { itemGuid: 'a', severity: 'warn' as const, code: 'OVERLOAD' },
  { itemGuid: 'b', severity: 'error' as const, code: 'EDGE_GAP' },
]
const locateItems = [item('c', 2, 1, 1), item('a', 1, 1, 1), item('b', 1, 5, 1)]
const locate = conflictLocateList(conflicts, locateItems)
assertEqual(locate.join(','), 'b,c', '只定位 error 级，按 节 → 行 → 列 排序')
assertEqual(nextLocateGuid(locate, null), 'b', '没选中时取第一个')
assertEqual(nextLocateGuid(locate, 'b'), 'c', '下一个')
assertEqual(nextLocateGuid(locate, 'c'), 'b', '循环回到第一个')
assertEqual(nextLocateGuid(locate, 'zzz'), 'b', '选中的不在列表里取第一个')
assertEqual(nextLocateGuid([], 'b'), null, '没有冲突返回 null')
assertEqual(primaryConflict([{ code: 'OVERLOAD', severity: 'warn' }, { code: 'OVERLAP', severity: 'error' }, { code: 'OUT_OF_BOARD', severity: 'error' }])?.code, 'OUT_OF_BOARD', '主要冲突：error 优先，再按判定顺序')
assertEqual(primaryConflict([]), null, '没有冲突')

// ---------- 本地 / 服务端冲突集合比较 ----------
const setA = [{ code: 'OVERLAP', itemGuid: 'a', otherItemGuid: 'b', severity: 'error' as const }, { code: 'OVERLAP', itemGuid: 'b', otherItemGuid: 'a', severity: 'error' as const }]
assertEqual(sameConflictSet(setA, [...setA].reverse()), true, '顺序无关')
assertEqual(sameConflictSet(setA, setA.slice(0, 1)), false, '少一条就不同')
assertEqual(sameConflictSet(setA, [{ ...setA[0], severity: 'warn' as const }, setA[1]]), false, '严重度不同')

// ---------- 画布文字折行 ----------
assertEqual(wrapLabel('', 100, 8, 2).length, 0, '空串没有行')
assertEqual(wrapLabel('圆珠笔10支', 40, 8, 3).join('|'), '圆珠笔10|支', '中文逐字折行')
assertEqual(wrapLabel('胶棒 2 支装', 80, 8, 3).join('|'), '胶棒 2 支装', '放得下不折')
assertEqual(wrapLabel('Ballpoint Pen 10pk', 50, 8, 3).join('|'), 'Ballpoint|Pen 10pk', '拉丁文按单词折行')
const clipped = wrapLabel('一二三四五六七八九十', 24, 8, 2)
assertEqual(clipped.length, 2, '超出最大行数截断')
assert(clipped[1].endsWith('…'), '末行加省略号')
assertEqual(wrapLabel('Superlongwordwithoutspaces', 30, 8, 3).length > 1, true, '超长单词按字符截成多行')
assertEqual(wrapLabel('abc', 4, 8, 2).length, 0, '连一个字都放不下时不画文字')

// ---------- 其它小工具 ----------
assertEqual(itemFootLabel('XD0001211', 15), '1211·×15', '货号后四位 + 每钩挂数（设计稿格式）')
assertEqual(productColor('XD0001211'), productColor('XD0001211'), '同货号颜色稳定')
assert(/^#[0-9a-f]{6}$/.test(productColor('anything')), '颜色是十六进制色值')

// ---------- 批 3：清单顺序（设计器 Tab 选择与 PDF 序号共用） ----------
assertEqual(itemFootLabelPrint('XD0001211', 15), '1211 ×15', '打印版货号后四位 + 空格 + ×每钩（设计稿 print 模式）')
const unsorted = [
  { guid: 'e', bayIndex: 2, holeRow: 3, holeCol: 2 },
  { guid: 'c', bayIndex: 1, holeRow: 13, holeCol: 2 },
  { guid: 'a', bayIndex: 1, holeRow: 3, holeCol: 7 },
  { guid: 'b', bayIndex: 1, holeRow: 3, holeCol: 2 },
  { guid: 'd', bayIndex: 1, holeRow: 13, holeCol: 2 },
]
assertEqual(sortChecklistItems(unsorted).map((item) => item.guid).join(','), 'b,a,c,d,e', '按 节 → 行 → 列 排序；完全相同位置保持原有先后（稳定）')
assertEqual(unsorted[0].guid, 'e', '不改动传入的数组')
assertEqual(sortChecklistItems([]).length, 0, '空数组')

console.log('designerLogic.test: ok')
