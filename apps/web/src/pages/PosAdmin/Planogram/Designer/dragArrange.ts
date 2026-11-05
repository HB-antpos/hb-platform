import { buildPlaced, checkPlacement, computePlacement, type LayoutContext, type PlacedItem, type PlacementGeometry } from '../engine/geometry'
import type { DesignerItem, DropHint, Slot } from './designerLogic'

/**
 * 拖拽时的「对调 / 插入」判定与重排（纯函数，不依赖 React；设计图 docs/design/planogram-20261104/designer-enhance）。
 *
 * 重叠率 = 拖动虚影与某个商品的相交面积 ÷ 两者中较小方块的面积（方块 = 占位矩形，含价签）。
 * 判定顺序：
 * 1. 同时压住同一行相邻的两个商品，且各自重叠率 > 20% → 插入到它们之间；
 * 2. 否则取重叠率最大的商品 T：≥ 50% → 对调（只对已放的商品；从商品库拖入的新商品一律插入）；
 *    20%～50% → 插入到 T 之前或之后（虚影中心在 T 左半边就插在前面）；≤ 20% → 普通移动（沿用原来的吸附 / 冲突逻辑）。
 * 插入：插入点之后的同行商品整体右移，彼此间距不变；被推出本节右边的末尾商品换到下一行左侧的空位
 * （只找空位，不推下一行的商品）；同一行里拖动时，原位置留下的空当会吸收推移（和列表拖动排序一样）。
 * 只在本节内后移或换行，放不下或会产生冲突就整次拒绝。
 * 对调：两者交换位置后吸附孔位（对齐方式见 applySwap）；对调后与邻居冲突则拒绝。
 * 结果是一份新的 items 数组，调用方一次 replaceItems 提交，撤销一步回到拖动前。
 */

/** 对调阈值（≥）与插入阈值（>）：两个数集中在这里，界面文案里的百分比也按它们换算。 */
export const ARRANGE_SWAP_RATE = 0.5
export const ARRANGE_INSERT_RATE = 0.2
/** 浮点比较容差：重叠率与毫米坐标都按它放宽一点，避免恰好相等时因舍入误差判错。 */
const EPS = 1e-6

export interface ArrangeRect {
  left: number
  right: number
  top: number
  bottom: number
}

/** 重叠率：相交面积 ÷ 两者中较小的面积；不相交（含只碰边）为 0。 */
export function overlapRate(a: ArrangeRect, b: ArrangeRect): number {
  const width = Math.min(a.right, b.right) - Math.max(a.left, b.left)
  const height = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top)
  if (width <= 0 || height <= 0) return 0
  const smaller = Math.min((a.right - a.left) * (a.bottom - a.top), (b.right - b.left) * (b.bottom - b.top))
  return smaller > 0 ? (width * height) / smaller : 0
}

export interface ArrangeTarget {
  guid: string
  /** 重叠率 0～1。 */
  rate: number
}

export type InsertPosition = 'between' | 'before' | 'after'

export type ArrangeIntent =
  | { kind: 'none' }
  | { kind: 'swap'; target: ArrangeTarget }
  | { kind: 'insert'; bayIndex: number; holeRow: number; index: number; position: InsertPosition; targets: ArrangeTarget[] }

type GeometryPlaced = PlacedItem & { geometry: PlacementGeometry }

function hasGeometry(entry: PlacedItem): entry is GeometryPlaced {
  return entry.geometry !== null
}

/** 一行 = 同节、同锚点行（一钩一行）；按挂钩中心从左到右排序，相同时保持原有先后。 */
function rowEntries(placed: readonly PlacedItem[], bayIndex: number, holeRow: number, excludeGuid: string | null): GeometryPlaced[] {
  return placed
    .filter(hasGeometry)
    .filter((entry) => entry.item.bayIndex === bayIndex && entry.item.holeRow === holeRow && entry.item.guid !== excludeGuid)
    .sort((a, b) => a.geometry.centerX - b.geometry.centerX || a.index - b.index)
}

/**
 * 只做判定（不改数据）：虚影压住了谁、各压了多少，应该对调、插入还是普通移动。
 * ghost 是吸附后的虚影占位矩形（板面坐标，所在节 bayIndex）；draggedGuid 是被拖动的条目（新商品传占位 guid），它自己不参与计算。
 */
export function decideArrange(
  placed: readonly PlacedItem[],
  ghost: { bayIndex: number; rect: ArrangeRect },
  draggedGuid: string,
  allowSwap: boolean,
): ArrangeIntent {
  const candidates = placed
    .filter(hasGeometry)
    .filter((entry) => entry.item.bayIndex === ghost.bayIndex && entry.item.guid !== draggedGuid)
    .map((entry) => ({ entry, rate: overlapRate(ghost.rect, entry.geometry) }))
    .filter((candidate) => candidate.rate > 0)
  if (candidates.length === 0) return { kind: 'none' }
  const rateOf = new Map(candidates.map((candidate) => [candidate.entry.item.guid, candidate.rate]))
  const overInsert = (rate: number) => rate > ARRANGE_INSERT_RATE + EPS

  // 1. 同一行相邻的两个都超过 20%：插到它们之间（两边都压得多时取重叠率之和最大的一对）
  let pair: { holeRow: number; index: number; sum: number; targets: ArrangeTarget[] } | null = null
  const rows = new Set(candidates.filter((candidate) => overInsert(candidate.rate)).map((candidate) => candidate.entry.item.holeRow))
  for (const holeRow of rows) {
    const row = rowEntries(placed, ghost.bayIndex, holeRow, draggedGuid)
    for (let j = 0; j + 1 < row.length; j += 1) {
      const left = rateOf.get(row[j].item.guid) ?? 0
      const right = rateOf.get(row[j + 1].item.guid) ?? 0
      if (overInsert(left) && overInsert(right) && (!pair || left + right > pair.sum)) {
        pair = { holeRow, index: j + 1, sum: left + right, targets: [{ guid: row[j].item.guid, rate: left }, { guid: row[j + 1].item.guid, rate: right }] }
      }
    }
  }
  if (pair) return { kind: 'insert', bayIndex: ghost.bayIndex, holeRow: pair.holeRow, index: pair.index, position: 'between', targets: pair.targets }

  // 2. 重叠率最大的那个（相同时取先出现的）
  let top = candidates[0]
  for (const candidate of candidates) if (candidate.rate > top.rate + EPS) top = candidate
  if (!overInsert(top.rate)) return { kind: 'none' }
  const target = { guid: top.entry.item.guid, rate: top.rate }
  if (allowSwap && top.rate >= ARRANGE_SWAP_RATE - EPS) return { kind: 'swap', target }
  const row = rowEntries(placed, ghost.bayIndex, top.entry.item.holeRow, draggedGuid)
  const index = row.findIndex((entry) => entry.item.guid === target.guid)
  const before = (ghost.rect.left + ghost.rect.right) / 2 < top.entry.geometry.centerX
  return { kind: 'insert', bayIndex: ghost.bayIndex, holeRow: top.entry.item.holeRow, index: before ? index : index + 1, position: before ? 'before' : 'after', targets: [target] }
}

// ---------------------------------------------------------------------------
// 重排结果
// ---------------------------------------------------------------------------

export type ArrangeMoveRole = 'dragged' | 'swap' | 'shift' | 'wrap'

export interface ArrangeMove {
  guid: string
  role: ArrangeMoveRole
  /** 新商品没有原位置。 */
  from: Slot | null
  to: Slot
}

/** 插入点竖线（板面坐标）：画布上标出「插到这里」。 */
export interface ArrangeLine {
  bayIndex: number
  xMm: number
  topMm: number
  bottomMm: number
}

export type ArrangeSwapPlan = { kind: 'swap'; target: ArrangeTarget; items: readonly DesignerItem[]; moves: ArrangeMove[] }
export type ArrangeInsertPlan = {
  kind: 'insert'
  position: InsertPosition
  targets: ArrangeTarget[]
  items: readonly DesignerItem[]
  moves: ArrangeMove[]
  line: ArrangeLine
  shiftCount: number
  wrapCount: number
}
export type ArrangeRejectPlan = {
  kind: 'reject'
  action: 'swap' | 'insert'
  /** noRoom：本节放不下（末尾没有可换的空位 / 插入的商品本身超出板面）；conflict：重排后会与其他商品冲突。 */
  reason: 'noRoom' | 'conflict'
  targets: ArrangeTarget[]
  line: ArrangeLine | null
}
export type ArrangeCommitPlan = ArrangeSwapPlan | ArrangeInsertPlan

export type ArrangePlan =
  | { kind: 'none' }
  /** 在同一行里拖回自己原来的位置：什么都不做，也不提示。 */
  | { kind: 'noop' }
  | ArrangeCommitPlan
  | ArrangeRejectPlan

const NONE: ArrangePlan = { kind: 'none' }

function slotOf(item: Pick<DesignerItem, 'bayIndex' | 'holeCol' | 'holeRow'>): Slot {
  return { bayIndex: item.bayIndex, holeCol: item.holeCol, holeRow: item.holeRow }
}

/** 某条目放在 (col, row) 时的占位几何（只用到挂钩 / 参数，与它当前在哪无关）。 */
function geometryAt(ctx: LayoutContext, entry: PlacedItem, col: number, row: number): PlacementGeometry {
  return computePlacement(ctx.shelf, entry.hook, entry.spec, col, row)
}

/** 让条目左缘落在 left 时的列号（实数）：左缘随列号线性变化，斜率是孔距。 */
function colForLeft(ctx: LayoutContext, entry: PlacedItem, left: number): number {
  return (left - geometryAt(ctx, entry, 1, 1).left) / ctx.shelf.holePitchXMm + 1
}

function maxColOf(ctx: LayoutContext, entry: PlacedItem): number {
  return ctx.shelf.holeCols - entry.hook.holeSpanX + 1
}

/** 吸附候选列：四舍五入、向下、向上取整，夹在合法范围内去重，按离理想位置由近到远排。 */
function colOptions(real: number, maxCol: number): number[] {
  const raw = [Math.round(real), Math.floor(real + EPS), Math.ceil(real - EPS)]
  const unique = [...new Set(raw.map((col) => Math.min(maxCol, Math.max(1, col))))]
  return unique.sort((a, b) => Math.abs(a - real) - Math.abs(b - real))
}

/** 挪动过的条目在新布局里都没有 error 级冲突（成对冲突是对称的，所以没动的条目也不会多出冲突）。 */
function movesValid(ctx: LayoutContext, items: readonly DesignerItem[], guids: Iterable<string>): boolean {
  const placed = buildPlaced(ctx, items)
  for (const guid of guids) {
    const entry = placed.find((candidate) => candidate.item.guid === guid)
    if (!entry || !entry.geometry) return false
    const others = placed.filter((candidate) => candidate.item.guid !== guid)
    if (!checkPlacement(ctx, others, entry.item).ok) return false
  }
  return true
}

/** 让条目右缘落在 right 时的列号（实数）。 */
function colForRight(ctx: LayoutContext, entry: PlacedItem, right: number): number {
  return (right - geometryAt(ctx, entry, 1, 1).right) / ctx.shelf.holePitchXMm + 1
}

/**
 * 对调：A 进 B 的槽位、B 进 A 的槽位，吸附孔位时在相邻几列里找第一个两边都不冲突的组合。
 * 对齐方式：不同行 / 不同节时各自左缘对齐到对方原位（设计图口径）；同一行里左边的槽位按左缘、右边的槽位按右缘对齐——
 * 两个宽度不同的相邻商品对调后，外侧边缘和两者之间的间距都不变（都按左缘对齐的话，宽的换到右边必然压到对方）。
 */
function applySwap(ctx: LayoutContext, items: readonly DesignerItem[], a: GeometryPlaced, b: GeometryPlaced, target: ArrangeTarget): ArrangePlan {
  const sameRow = a.item.bayIndex === b.item.bayIndex && a.item.holeRow === b.item.holeRow
  const aIsLeft = a.geometry.centerX < b.geometry.centerX
  const idealA = sameRow && aIsLeft ? colForRight(ctx, a, b.geometry.right) : colForLeft(ctx, a, b.geometry.left)
  const idealB = sameRow && !aIsLeft ? colForRight(ctx, b, a.geometry.right) : colForLeft(ctx, b, a.geometry.left)
  const optionsA = colOptions(idealA, maxColOf(ctx, a))
  const optionsB = colOptions(idealB, maxColOf(ctx, b))
  const combos = optionsA
    .flatMap((colA) => optionsB.map((colB) => ({ colA, colB, distance: Math.abs(colA - idealA) + Math.abs(colB - idealB) })))
    .sort((x, y) => x.distance - y.distance)
  const toA: Slot = { bayIndex: b.item.bayIndex, holeCol: 0, holeRow: b.item.holeRow }
  const toB: Slot = { bayIndex: a.item.bayIndex, holeCol: 0, holeRow: a.item.holeRow }
  for (const combo of combos) {
    const nextA = { ...toA, holeCol: combo.colA }
    const nextB = { ...toB, holeCol: combo.colB }
    const next = items.map((item) => {
      if (item.guid === a.item.guid) return { ...item, ...nextA }
      if (item.guid === b.item.guid) return { ...item, ...nextB }
      return item
    })
    if (movesValid(ctx, next, [a.item.guid, b.item.guid])) {
      return {
        kind: 'swap',
        target,
        items: next,
        moves: [
          { guid: a.item.guid, role: 'dragged', from: slotOf(a.item), to: nextA },
          { guid: b.item.guid, role: 'swap', from: slotOf(b.item), to: nextB },
        ],
      }
    }
  }
  return { kind: 'reject', action: 'swap', reason: 'conflict', targets: [target], line: null }
}

/** 插入点竖线：插在两者之间取两者间隙的中线；只有一边时贴着那一边外侧半个间距。 */
function insertLine(ctx: LayoutContext, row: readonly GeometryPlaced[], index: number, bayIndex: number): ArrangeLine | null {
  if (row.length === 0) return null
  const prev = row[index - 1]
  const next = row[index]
  const half = ctx.gaps.gapXMm / 2
  const xMm = prev && next ? (prev.geometry.right + next.geometry.left) / 2 : next ? next.geometry.left - half : prev.geometry.right + half
  return {
    bayIndex,
    xMm,
    topMm: Math.min(...row.map((entry) => entry.geometry.top)),
    bottomMm: Math.max(...row.map((entry) => entry.geometry.bottom)),
  }
}

function applyInsert(
  ctx: LayoutContext,
  items: readonly DesignerItem[],
  placed: readonly PlacedItem[],
  /** ghostCol：虚影吸附到的列（插入点前后有空位时尽量放在这一列附近）。 */
  dragged: { item: DesignerItem; entry: PlacedItem; isNew: boolean; ghostCol: number },
  intent: Extract<ArrangeIntent, { kind: 'insert' }>,
): ArrangePlan {
  const { bayIndex, holeRow, index, targets } = intent
  const pitch = ctx.shelf.holePitchXMm
  const gapX = ctx.gaps.gapXMm
  const rightLimit = ctx.shelf.bayWidthMm - ctx.shelf.uprightWidthMm - gapX + EPS
  const draggedGuid = dragged.item.guid
  const rowAll = rowEntries(placed, bayIndex, holeRow, null)
  const row = rowAll.filter((entry) => entry.item.guid !== draggedGuid)
  const line = insertLine(ctx, row, index, bayIndex)
  const reject = (reason: 'noRoom' | 'conflict'): ArrangePlan => ({ kind: 'reject', action: 'insert', reason, targets, line })

  // 拖动的条目原来就在这一行：它在行里的位置（= 左边有几个商品）
  const oldIndex = dragged.isNew ? -1 : rowAll.findIndex((entry) => entry.item.guid === draggedGuid)
  const inRow = oldIndex >= 0
  if (inRow && index === oldIndex) return { kind: 'noop' }

  const newCol = new Map<string, number>()
  const colOf = (entry: PlacedItem) => newCol.get(entry.item.guid) ?? entry.item.holeCol
  const leftAt = (entry: PlacedItem, col: number) => geometryAt(ctx, entry, col, holeRow).left
  const rightAt = (entry: PlacedItem, col: number) => geometryAt(ctx, entry, col, holeRow).right
  const fits = (entry: PlacedItem, col: number) => col >= 1 && col <= maxColOf(ctx, entry) && rightAt(entry, col) <= rightLimit

  // 同一行里往右拖：夹在原位置与插入点之间的商品整体左移，填上它留下的空当（彼此间距不变）
  const movingRightInRow = inRow && index > oldIndex
  if (movingRightInRow && dragged.entry.geometry) {
    const firstBetween = row[oldIndex]
    const pull = Math.max(0, Math.floor((firstBetween.geometry.left - dragged.entry.geometry.left) / pitch + EPS))
    if (pull > 0) for (const entry of row.slice(oldIndex, index)) newCol.set(entry.item.guid, entry.item.holeCol - pull)
  }

  // 被插入的商品：左缘不小于前一个商品右缘 + 间距（第一个就是板边间距）
  const prev = row[index - 1]
  const lowerBound = prev ? rightAt(prev, colOf(prev)) + gapX : gapX
  const next = row[index]
  const minCol = Math.max(1, Math.ceil(colForLeft(ctx, dragged.entry, lowerBound) - EPS))
  let col: number
  if (movingRightInRow) {
    // 同行往右：紧跟在左移后的商品后面（与列表拖动排序一致）
    col = minCol
  } else {
    // 插入点前后本来就有足够空位：放进空位里离虚影最近的孔位，不推后面的商品；没有空位才占插入点后那个商品的原位并后移
    const maxNoPush = next
      ? Math.floor(colForRight(ctx, dragged.entry, leftAt(next, colOf(next)) - gapX) + EPS)
      : maxColOf(ctx, dragged.entry)
    if (minCol <= maxNoPush && fits(dragged.entry, minCol)) {
      col = Math.min(maxNoPush, Math.max(minCol, dragged.ghostCol))
      while (col > minCol && !fits(dragged.entry, col)) col -= 1
    } else {
      const targetLeft = next ? Math.max(lowerBound, leftAt(next, colOf(next))) : lowerBound
      col = Math.round(colForLeft(ctx, dragged.entry, targetLeft))
      if (col < minCol) col = minCol
    }
  }
  if (!fits(dragged.entry, col)) return reject('noRoom')

  // 后移：插入点之后的商品统一右移 k 列（第一个被推的决定 k），遇到原有空当足够就停；推出板面的末尾商品连同其后的都换行
  let prevRight = rightAt(dragged.entry, col)
  let shift: number | null = null
  let overflowFrom = -1
  for (let j = index; j < row.length; j += 1) {
    const entry = row[j]
    const current = colOf(entry)
    const minCol = Math.ceil(colForLeft(ctx, entry, prevRight + gapX) - EPS)
    if (current >= minCol) break
    const moved = shift === null ? minCol : Math.max(minCol, current + shift)
    if (shift === null) shift = minCol - current
    if (!fits(entry, moved)) {
      overflowFrom = j
      break
    }
    newCol.set(entry.item.guid, moved)
    prevRight = rightAt(entry, moved)
  }
  const overflow = overflowFrom >= 0 ? row.slice(overflowFrom) : []
  const overflowGuids = new Set(overflow.map((entry) => entry.item.guid))

  const draggedNext: DesignerItem = { ...dragged.item, bayIndex, holeRow, holeCol: col }
  let nextItems: DesignerItem[] = items.map((item) => {
    if (item.guid === draggedGuid) return draggedNext
    const moved = newCol.get(item.guid)
    return moved !== undefined && moved !== item.holeCol && !overflowGuids.has(item.guid) ? { ...item, holeCol: moved } : item
  })
  if (dragged.isNew) nextItems = [...nextItems, draggedNext]

  // 换行：只在本节、只找空位。下面已有一行商品就挂到那一行的挂钩行上；下面没有商品就从本行下方逐行找
  const wrapped = new Map<string, DesignerItem>()
  if (overflow.length > 0) {
    let current = nextItems.filter((item) => !overflowGuids.has(item.guid))
    const below = buildPlaced(ctx, current).filter((entry) => entry.geometry !== null && entry.item.bayIndex === bayIndex && entry.item.holeRow > holeRow)
    const nextRow = below.length > 0 ? Math.min(...below.map((entry) => entry.item.holeRow)) : null
    for (const entry of overflow) {
      const source = items.find((item) => item.guid === entry.item.guid)
      if (!source) return reject('noRoom')
      const others = buildPlaced(ctx, current)
      const maxRow = ctx.shelf.holeRows - entry.hook.holeSpanY + 1
      const rows = nextRow !== null ? [nextRow] : Array.from({ length: Math.max(0, maxRow - holeRow) }, (_, k) => holeRow + 1 + k)
      let found: DesignerItem | null = null
      for (const r of rows) {
        for (let c = 1; c <= maxColOf(ctx, entry) && !found; c += 1) {
          const candidate: DesignerItem = { ...source, bayIndex, holeRow: r, holeCol: c }
          if (checkPlacement(ctx, others, candidate).ok) found = candidate
        }
        if (found) break
      }
      if (!found) return reject('noRoom')
      wrapped.set(found.guid, found)
      current = [...current, found]
    }
    nextItems = nextItems.map((item) => wrapped.get(item.guid) ?? item)
  }

  const moves: ArrangeMove[] = [{ guid: draggedGuid, role: 'dragged', from: dragged.isNew ? null : slotOf(dragged.item), to: slotOf(draggedNext) }]
  for (const item of items) {
    const after = nextItems.find((candidate) => candidate.guid === item.guid)
    if (!after || item.guid === draggedGuid) continue
    if (wrapped.has(item.guid)) moves.push({ guid: item.guid, role: 'wrap', from: slotOf(item), to: slotOf(after) })
    else if (after.holeCol !== item.holeCol) moves.push({ guid: item.guid, role: 'shift', from: slotOf(item), to: slotOf(after) })
  }
  if (!movesValid(ctx, nextItems, moves.map((move) => move.guid))) return reject('conflict')
  return {
    kind: 'insert',
    position: intent.position,
    targets,
    items: nextItems,
    moves,
    line: line ?? { bayIndex, xMm: geometryAt(ctx, dragged.entry, col, holeRow).left, topMm: 0, bottomMm: 0 },
    shiftCount: moves.filter((move) => move.role === 'shift').length,
    wrapCount: wrapped.size,
  }
}

/**
 * 拖拽重排的总入口：hint 是吸附后的落点（拖动已放条目时须用 ignoreGuid 算出的那个），dragged 是被拖动的条目。
 * 新商品（isNew）：dragged 放在 hint 的位置、guid 由调用方生成（预览时可以用占位 guid）；它不参与对调。
 * 返回 none 时调用方按原来的「普通移动」处理（hint.ok 才放下，自由吸附照放并标红）。
 */
export function planDragArrange(args: { ctx: LayoutContext; items: readonly DesignerItem[]; hint: DropHint; dragged: DesignerItem; isNew: boolean }): ArrangePlan {
  const { ctx, items, hint, dragged, isNew } = args
  if (!hint.geometry) return NONE
  const placed = buildPlaced(ctx, items)
  const draggedEntry = isNew ? buildPlaced(ctx, [dragged])[0] : placed.find((entry) => entry.item.guid === dragged.guid)
  if (!draggedEntry) return NONE
  const intent = decideArrange(placed, { bayIndex: hint.bayIndex, rect: hint.geometry }, dragged.guid, !isNew)
  if (intent.kind === 'none') return NONE
  if (intent.kind === 'swap') {
    const target = placed.find((entry) => entry.item.guid === intent.target.guid)
    if (!target || !hasGeometry(target) || !hasGeometry(draggedEntry)) return NONE
    return applySwap(ctx, items, draggedEntry, target, intent.target)
  }
  return applyInsert(ctx, items, placed, { item: dragged, entry: draggedEntry, isNew, ghostCol: hint.holeCol }, intent)
}

// ---------------------------------------------------------------------------
// 画布预览
// ---------------------------------------------------------------------------

export type PreviewTone = 'ghost' | 'shift' | 'wrap'

/** 画布上的虚线预览框（板面坐标，商品包装矩形，不含价签）。 */
export interface CanvasPreviewBox {
  key: string
  bayIndex: number
  leftMm: number
  topMm: number
  widthMm: number
  heightMm: number
  tone: PreviewTone
}

/** 商品包装在某个孔位时的矩形（画虚线框用）；缺参数 / 缺挂钩返回 null。 */
export function productBoxAt(ctx: LayoutContext, item: Pick<DesignerItem, 'productCode' | 'hookModelGuid'>, slot: Slot): Omit<CanvasPreviewBox, 'key' | 'tone'> | null {
  const entry = buildPlaced(ctx, [{ guid: 'preview', productCode: item.productCode, hookModelGuid: item.hookModelGuid, bayIndex: slot.bayIndex, holeCol: slot.holeCol, holeRow: slot.holeRow, perHookQty: null }])[0]
  if (!entry || !entry.geometry) return null
  return {
    bayIndex: slot.bayIndex,
    leftMm: entry.geometry.centerX - entry.spec.widthMm / 2,
    topMm: entry.geometry.productTop,
    widthMm: entry.spec.widthMm,
    heightMm: entry.spec.heightMm,
  }
}

/** 重排预览：被挪动的商品（不含正在拖的那个，它由拖动虚影表示）在新位置的虚线框；换行的用橙色。 */
export function arrangePreviewBoxes(ctx: LayoutContext, items: readonly DesignerItem[], plan: ArrangePlan): CanvasPreviewBox[] {
  if (plan.kind !== 'swap' && plan.kind !== 'insert') return []
  const boxes: CanvasPreviewBox[] = []
  for (const move of plan.moves) {
    if (move.role === 'dragged') continue
    const item = items.find((candidate) => candidate.guid === move.guid)
    if (!item) continue
    const box = productBoxAt(ctx, item, move.to)
    if (box) boxes.push({ ...box, key: `arrange-${move.guid}`, tone: move.role === 'wrap' ? 'wrap' : 'shift' })
  }
  return boxes
}

/** 预览期间原位置要淡化的条目（被挪动的，不含正在拖的那个：它自己已经按拖动态淡化）。 */
export function arrangeFadedGuids(plan: ArrangePlan): ReadonlySet<string> {
  if (plan.kind !== 'swap' && plan.kind !== 'insert') return EMPTY_SET
  return new Set(plan.moves.filter((move) => move.role !== 'dragged').map((move) => move.guid))
}

const EMPTY_SET: ReadonlySet<string> = new Set<string>()

/** 重叠率换成整数百分比（界面显示用）。 */
export function ratePercent(rate: number): number {
  return Math.round(rate * 100)
}
