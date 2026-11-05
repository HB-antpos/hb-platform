/**
 * 陈列方案几何引擎（纯函数，不依赖 React / 请求）。
 *
 * 规则逐条对应 docs/plans/2026-11-04-planogram-shelf-display-designer.md 附录 B.1，
 * 与后端 Services/Planogram/PlanogramLayoutValidator.cs 必须逐条一致；
 * 两端共用 engine/fixtures/layout-cases.json 跑测试（geometry.test.ts），防止口径漂移。
 *
 * 坐标系：每一节（bay）独立，原点是该节「板面」左上角，x 向右、y 向下，单位 mm；各节之间不互通，商品不能跨节。
 * 浮点比较容差 0.001 mm：「a < b」写成「a < b − 容差」，「a > b」写成「a > b + 容差」，所以恰好等于间距不算冲突。
 */
import type { ConflictDto, PlanogramConflictCode, PlanogramConflictSeverity, PlanogramStatsDto } from '../../../../types/planogram'
import { boardSize, holeCenter, type ShelfGeometryLike } from './holeGrid'
import { checkHookLoad, normalizeQtyOverride, suggestHookQty } from './hookQty'

/** 浮点比较容差（mm）。 */
export const LAYOUT_TOLERANCE_MM = 0.001

// ---------------------------------------------------------------------------
// 输入类型（只含几何 / 统计需要的字段，设计器与夹具测试共用）
// ---------------------------------------------------------------------------

export type LayoutShelf = ShelfGeometryLike

export interface LayoutHook {
  hasPriceTagArm: boolean
  armLengthMm: number
  dropMm: number
  tipReserveMm: number
  tagWidthMm: number
  tagHeightMm: number
  tagBottomAboveArmMm: number
  holeSpanX: number
  holeSpanY: number
  loadKg?: number | null
}

export interface LayoutSpec {
  widthMm: number
  heightMm: number
  depthMm: number
  hangHoleOffsetMm: number
  unitWeightG?: number | null
  /** { hookGuid: 每钩挂数 }；值不是 ≥ 1 的整数视为没有覆盖。 */
  hookQtyOverrides?: Readonly<Record<string, number | null | undefined>> | null
  /** Product.RetailPrice，统计零售总值用。 */
  retailPrice: number
}

export interface LayoutItem {
  guid: string
  bayIndex: number
  productCode: string
  hookModelGuid: string
  holeCol: number
  holeRow: number
  perHookQty?: number | null
}

export interface LayoutGaps {
  gapXMm: number
  gapYMm: number
}

export interface LayoutContext {
  shelf: LayoutShelf
  /** 以挂钩型号 Guid 为键；停用的型号仍在字典里，缺失的型号就是不在字典里。 */
  hooks: Readonly<Record<string, LayoutHook>>
  /** 以 ProductCode 为键；没设陈列参数的商品不在字典里。 */
  specs: Readonly<Record<string, LayoutSpec>>
  bayCount: number
  gaps: LayoutGaps
}

// ---------------------------------------------------------------------------
// 输出类型
// ---------------------------------------------------------------------------

/** 冲突的补充数字（界面文案插值用；后端不返回，本地复算时才有）。 */
export type ConflictDetail = Partial<Record<'bayIndex' | 'bayCount' | 'col' | 'row' | 'spanX' | 'spanY' | 'cols' | 'rows' | 'needX' | 'needY' | 'clearX' | 'clearY' | 'qty' | 'totalKg' | 'limitKg', number>>

export interface LayoutConflict extends ConflictDto {
  code: PlanogramConflictCode
  /** 中性英文说明（仅作兜底与调试，界面按 code 走 i18n）。 */
  message: string
  detail?: ConflictDetail
}

export type LayoutStats = PlanogramStatsDto

export interface LayoutResult {
  conflicts: LayoutConflict[]
  stats: LayoutStats
}

/** 一个条目的占位几何（板面坐标，mm）。 */
export interface PlacementGeometry {
  /** 锚点孔孔心。 */
  holeX: number
  holeY: number
  /** 挂钩中心（双钩居于两孔之间）。 */
  centerX: number
  /** 有效宽 = max(包装宽, 带价签臂时的价签宽)。 */
  effectiveWidth: number
  left: number
  right: number
  /** 占位顶：带价签臂时取「价签顶」与「商品顶」中较高（数值较小）者。 */
  top: number
  bottom: number
  productTop: number
  productBottom: number
  /** 价签顶（仅带价签臂）。 */
  tagTop: number | null
}

/** 参数与挂钩型号都存在的条目 + 占孔区间与占位几何（孔位超出孔阵时 geometry 为 null）。 */
export interface PlacedItem {
  index: number
  item: LayoutItem
  hook: LayoutHook
  spec: LayoutSpec
  colFrom: number
  colTo: number
  rowFrom: number
  rowTo: number
  geometry: PlacementGeometry | null
}

// ---------------------------------------------------------------------------
// 基础公式
// ---------------------------------------------------------------------------

/** 字典取值只认自有属性：商品货号叫 "constructor" / "toString" 之类时不会取到原型上的函数。 */
export function lookupRecord<T>(record: Readonly<Record<string, T>>, key: string): T | undefined {
  return Object.prototype.hasOwnProperty.call(record, key) ? record[key] : undefined
}

/** 每钩挂数（B.1 第 5 条）：条目上的值 > 规格里按挂钩型号的覆盖 > 建议值 ⌊(臂长 − 臂端预留) ÷ 厚⌋；整数 ≥ 1 才算填了。 */
export function resolvePerHookQty(item: Pick<LayoutItem, 'hookModelGuid' | 'perHookQty'>, hook: LayoutHook, spec: LayoutSpec): number {
  const own = normalizeQtyOverride(item.perHookQty)
  if (own !== null) return own
  const override = normalizeQtyOverride(spec.hookQtyOverrides?.[item.hookModelGuid])
  if (override !== null) return override
  return suggestHookQty(hook, spec.depthMm)
}

/** 占位矩形（B.1 第 1、3、4 条）。孔位本身是否在孔阵内由调用方先判（HOLE_OUT_OF_RANGE）。 */
export function computePlacement(shelf: LayoutShelf, hook: LayoutHook, spec: LayoutSpec, holeCol: number, holeRow: number): PlacementGeometry {
  const hole = holeCenter(shelf, holeCol, holeRow)
  const centerX = hole.x + ((hook.holeSpanX - 1) * shelf.holePitchXMm) / 2
  const effectiveWidth = Math.max(spec.widthMm, hook.hasPriceTagArm ? hook.tagWidthMm : 0)
  const productTop = hole.y + hook.dropMm - spec.hangHoleOffsetMm
  const productBottom = productTop + spec.heightMm
  // 价签顶 = 孔心 + 钩高 − 价签底边高于挂货臂 − 价签高（与设计稿 plano.js 同式）
  const tagTop = hook.hasPriceTagArm ? hole.y + hook.dropMm - hook.tagBottomAboveArmMm - hook.tagHeightMm : null
  return {
    holeX: hole.x,
    holeY: hole.y,
    centerX,
    effectiveWidth,
    left: centerX - effectiveWidth / 2,
    right: centerX + effectiveWidth / 2,
    top: tagTop === null ? productTop : Math.min(productTop, tagTop),
    bottom: productBottom,
    productTop,
    productBottom,
    tagTop,
  }
}

/** 孔位是否落在孔阵内（含占孔区间，B.1 第 2、5 条 HOLE_OUT_OF_RANGE）。 */
export function isHoleInRange(shelf: LayoutShelf, hook: Pick<LayoutHook, 'holeSpanX' | 'holeSpanY'>, col: number, row: number): boolean {
  return !(col < 1 || row < 1 || col + hook.holeSpanX - 1 > shelf.holeCols || row + hook.holeSpanY - 1 > shelf.holeRows)
}

/** 把条目解析成 PlacedItem；缺参数 / 缺挂钩型号返回 null。 */
function resolvePlaced(ctx: LayoutContext, item: LayoutItem, index: number): PlacedItem | null {
  const spec = lookupRecord(ctx.specs, item.productCode)
  const hook = lookupRecord(ctx.hooks, item.hookModelGuid)
  if (!spec || !hook) return null
  const inRange = isHoleInRange(ctx.shelf, hook, item.holeCol, item.holeRow)
  return {
    index,
    item,
    hook,
    spec,
    colFrom: item.holeCol,
    colTo: item.holeCol + hook.holeSpanX - 1,
    rowFrom: item.holeRow,
    rowTo: item.holeRow + hook.holeSpanY - 1,
    geometry: inRange ? computePlacement(ctx.shelf, hook, spec, item.holeCol, item.holeRow) : null,
  }
}

/** 批量解析：设计器里按 items 引用缓存，拖拽中的实时判定复用它，不必每次指针移动都重算全部几何。 */
export function buildPlaced(ctx: LayoutContext, items: readonly LayoutItem[]): PlacedItem[] {
  const placed: PlacedItem[] = []
  items.forEach((item, index) => {
    const resolved = resolvePlaced(ctx, item, index)
    if (resolved) placed.push(resolved)
  })
  return placed
}

// ---------------------------------------------------------------------------
// 成对判定
// ---------------------------------------------------------------------------

/** 占孔区间在行、列上都相交（同节）。 */
function clashes(a: PlacedItem, b: PlacedItem): boolean {
  return a.colFrom <= b.colTo && b.colFrom <= a.colTo && a.rowFrom <= b.rowTo && b.rowFrom <= a.rowTo
}

/** 两矩形的净间隙（负数 = 重叠量）：横向 / 纵向。 */
export function clearanceBetween(a: PlacementGeometry, b: PlacementGeometry): { x: number; y: number } {
  return {
    x: Math.max(a.left, b.left) - Math.min(a.right, b.right),
    y: Math.max(a.top, b.top) - Math.min(a.bottom, b.bottom),
  }
}

/** 各向外扩半个间距后相交面积 > 0，即横向、纵向净间隙都小于对应间距（B.1 第 5 条 OVERLAP）。 */
function overlaps(a: PlacementGeometry, b: PlacementGeometry, gaps: LayoutGaps): boolean {
  const clear = clearanceBetween(a, b)
  return clear.x < gaps.gapXMm - LAYOUT_TOLERANCE_MM && clear.y < gaps.gapYMm - LAYOUT_TOLERANCE_MM
}

/** 越界（B.1 第 5 条 OUT_OF_BOARD）。 */
function outOfBoard(g: PlacementGeometry, boardW: number, boardH: number): boolean {
  return g.left < -LAYOUT_TOLERANCE_MM || g.right > boardW + LAYOUT_TOLERANCE_MM || g.top < -LAYOUT_TOLERANCE_MM || g.bottom > boardH + LAYOUT_TOLERANCE_MM
}

/** 未越界但离板面边缘的间隙小于对应间距（B.1 第 5 条 EDGE_GAP）。 */
function edgeGapTooSmall(g: PlacementGeometry, boardW: number, boardH: number, gaps: LayoutGaps): boolean {
  return (
    g.left < gaps.gapXMm - LAYOUT_TOLERANCE_MM ||
    boardW - g.right < gaps.gapXMm - LAYOUT_TOLERANCE_MM ||
    g.top < gaps.gapYMm - LAYOUT_TOLERANCE_MM ||
    boardH - g.bottom < gaps.gapYMm - LAYOUT_TOLERANCE_MM
  )
}

function groupByBay(placed: readonly PlacedItem[]): Map<number, PlacedItem[]> {
  const groups = new Map<number, PlacedItem[]>()
  for (const entry of placed) {
    const list = groups.get(entry.item.bayIndex)
    if (list) list.push(entry)
    else groups.set(entry.item.bayIndex, [entry])
  }
  return groups
}

/** 占孔相交的条目对（同节）：按左端列排序后扫描，列区间不再相交即停止，避免 O(n²)。 */
function findClashPairs(geometric: readonly PlacedItem[]): Array<[PlacedItem, PlacedItem]> {
  const pairs: Array<[PlacedItem, PlacedItem]> = []
  for (const group of groupByBay(geometric).values()) {
    const sorted = [...group].sort((a, b) => a.colFrom - b.colFrom || a.index - b.index)
    for (let i = 0; i < sorted.length; i += 1) {
      for (let j = i + 1; j < sorted.length && sorted[j].colFrom <= sorted[i].colTo; j += 1) {
        if (clashes(sorted[i], sorted[j])) pairs.push(sorted[i].index <= sorted[j].index ? [sorted[i], sorted[j]] : [sorted[j], sorted[i]])
      }
    }
  }
  return pairs.sort((a, b) => a[0].index - b[0].index || a[1].index - b[1].index)
}

/** 外扩后相交的条目对（同节）：按左边缘排序后扫描，后一个的左边缘 ≥ 前一个右边缘 + 间距时横向不可能再相交，直接停止。 */
function findOverlapPairs(geometric: readonly PlacedItem[], gaps: LayoutGaps): Array<[PlacedItem, PlacedItem]> {
  const pairs: Array<[PlacedItem, PlacedItem]> = []
  for (const group of groupByBay(geometric).values()) {
    const sorted = [...group].sort((a, b) => a.geometry!.left - b.geometry!.left || a.index - b.index)
    for (let i = 0; i < sorted.length; i += 1) {
      const a = sorted[i].geometry!
      for (let j = i + 1; j < sorted.length && sorted[j].geometry!.left < a.right + gaps.gapXMm - LAYOUT_TOLERANCE_MM; j += 1) {
        if (overlaps(a, sorted[j].geometry!, gaps)) pairs.push(sorted[i].index <= sorted[j].index ? [sorted[i], sorted[j]] : [sorted[j], sorted[i]])
      }
    }
  }
  return pairs.sort((a, b) => a[0].index - b[0].index || a[1].index - b[1].index)
}

// ---------------------------------------------------------------------------
// 整体校验与统计
// ---------------------------------------------------------------------------

function conflict(
  code: PlanogramConflictCode,
  severity: PlanogramConflictSeverity,
  itemGuid: string,
  message: string,
  otherItemGuid?: string,
  detail?: ConflictDetail,
): LayoutConflict {
  return { itemGuid, ...(otherItemGuid === undefined ? {} : { otherItemGuid }), code, severity, message, ...(detail ? { detail } : {}) }
}

/** 统计（B.1 第 6 条）：itemCount / skuCount 覆盖全部条目；unitCount / retailValue / fillPercent 只统计参数与型号都在的条目。 */
export function computeStats(ctx: LayoutContext, items: readonly LayoutItem[], placed: readonly PlacedItem[], conflictCount: number): LayoutStats {
  let unitCount = 0
  let retailValue = 0
  let productArea = 0
  for (const entry of placed) {
    const qty = resolvePerHookQty(entry.item, entry.hook, entry.spec)
    unitCount += qty
    retailValue += qty * entry.spec.retailPrice
    productArea += entry.spec.widthMm * entry.spec.heightMm
  }
  const board = boardSize(ctx.shelf)
  const boardArea = board.widthMm * board.heightMm * ctx.bayCount
  // 四舍五入（0.5 向上）：加极小量避免 0.4999999999 这类浮点误差把整数边界判错
  const fillPercent = boardArea > 0 ? Math.floor((productArea / boardArea) * 100 + 0.5 + 1e-9) : 0
  return {
    itemCount: items.length,
    skuCount: new Set(items.map((item) => item.productCode)).size,
    unitCount,
    retailValue: Math.round(retailValue * 100 + 1e-7) / 100,
    fillPercent,
    conflictCount,
  }
}

/** 整体校验：判定顺序与后端一致（B.1 第 5 条）。 */
export function validateLayout(ctx: LayoutContext, items: readonly LayoutItem[]): LayoutResult {
  const conflicts: LayoutConflict[] = []
  const errorItems = new Set<number>()
  const board = boardSize(ctx.shelf)
  const { gaps } = ctx
  const add = (index: number, entry: LayoutConflict) => {
    if (entry.severity === 'error') errorItems.add(index)
    conflicts.push(entry)
  }

  // ① 节号：只报告，不阻止该条目继续按自己的节号参与后续判定
  items.forEach((item, index) => {
    if (item.bayIndex < 1 || item.bayIndex > ctx.bayCount) {
      add(index, conflict('BAY_OUT_OF_RANGE', 'error', item.guid, `Bay ${item.bayIndex} is outside 1..${ctx.bayCount}`, undefined, { bayIndex: item.bayIndex, bayCount: ctx.bayCount }))
    }
  })

  // ② 缺商品参数 / 缺挂钩型号：缺失的条目不再参与后续任何判定
  const resolved: PlacedItem[] = []
  items.forEach((item, index) => {
    const spec = lookupRecord(ctx.specs, item.productCode)
    const hook = lookupRecord(ctx.hooks, item.hookModelGuid)
    if (!spec) add(index, conflict('PRODUCT_SPEC_MISSING', 'error', item.guid, `Product ${item.productCode} has no display spec`))
    if (!hook) add(index, conflict('HOOK_MODEL_MISSING', 'error', item.guid, 'Hook model not found'))
    if (spec && hook) {
      const entry = resolvePlaced(ctx, item, index)
      if (entry) resolved.push(entry)
    }
  })

  // ③ 孔位超出孔阵：不再参与几何判定（占孔冲突、板边、重叠），但承重与统计照常
  const geometric: PlacedItem[] = []
  for (const entry of resolved) {
    if (entry.geometry === null) {
      add(entry.index, conflict('HOLE_OUT_OF_RANGE', 'error', entry.item.guid, `Hole col ${entry.item.holeCol} / row ${entry.item.holeRow} is outside the hole grid`, undefined, {
        col: entry.item.holeCol,
        row: entry.item.holeRow,
        spanX: entry.hook.holeSpanX,
        spanY: entry.hook.holeSpanY,
        cols: ctx.shelf.holeCols,
        rows: ctx.shelf.holeRows,
      }))
    } else {
      geometric.push(entry)
    }
  }

  // ④ 占孔相交：成对报，每个条目各一条
  for (const [a, b] of findClashPairs(geometric)) {
    add(a.index, conflict('HOOK_HOLE_CLASH', 'error', a.item.guid, 'Hook holes overlap another hook', b.item.guid))
    add(b.index, conflict('HOOK_HOLE_CLASH', 'error', b.item.guid, 'Hook holes overlap another hook', a.item.guid))
  }

  // ⑤ 越界
  const outside = new Set<number>()
  for (const entry of geometric) {
    if (outOfBoard(entry.geometry!, board.widthMm, board.heightMm)) {
      outside.add(entry.index)
      add(entry.index, conflict('OUT_OF_BOARD', 'error', entry.item.guid, 'Product (with price tag) is outside the board'))
    }
  }

  // ⑥ 板边间隙不足（已越界的不再重复报）
  for (const entry of geometric) {
    if (!outside.has(entry.index) && edgeGapTooSmall(entry.geometry!, board.widthMm, board.heightMm, gaps)) {
      add(entry.index, conflict('EDGE_GAP', 'error', entry.item.guid, 'Gap to the board edge is smaller than the required gap', undefined, { needX: gaps.gapXMm, needY: gaps.gapYMm }))
    }
  }

  // ⑦ 商品间距不足：成对报
  for (const [a, b] of findOverlapPairs(geometric, gaps)) {
    const clear = clearanceBetween(a.geometry!, b.geometry!)
    const detail: ConflictDetail = { needX: gaps.gapXMm, needY: gaps.gapYMm, clearX: Math.round(clear.x * 10) / 10, clearY: Math.round(clear.y * 10) / 10 }
    add(a.index, conflict('OVERLAP', 'error', a.item.guid, 'Gap to another product is too small', b.item.guid, detail))
    add(b.index, conflict('OVERLAP', 'error', b.item.guid, 'Gap to another product is too small', a.item.guid, detail))
  }

  // ⑧ 承重（warn）：只要参数与型号都在就判，与节号 / 孔位是否有效无关
  for (const entry of resolved) {
    const qty = resolvePerHookQty(entry.item, entry.hook, entry.spec)
    const check = checkHookLoad(qty, entry.spec.unitWeightG, entry.hook.loadKg)
    if (check.status === 'over' && check.totalG !== null && check.limitG !== null) {
      add(entry.index, conflict('OVERLOAD', 'warn', entry.item.guid, `${qty} units weigh more than the hook load limit`, undefined, { qty, totalKg: check.totalG / 1000, limitKg: check.limitG / 1000 }))
    }
  }

  return { conflicts, stats: computeStats(ctx, items, resolved, errorItems.size) }
}

// ---------------------------------------------------------------------------
// 拖拽可放判定（单个候选条目，复用同一套判定）
// ---------------------------------------------------------------------------

export interface PlacementCheck {
  /** 没有 error 级冲突即可放。 */
  ok: boolean
  /** 候选条目的 error 级冲突（按判定顺序）；others 里涉及的对方在 otherItemGuid。 */
  conflicts: LayoutConflict[]
  /** 候选条目的占位几何；孔位超出孔阵 / 缺参数 / 缺挂钩时为 null。 */
  geometry: PlacementGeometry | null
}

/**
 * 判定一个候选条目放进「当前已放置的条目」后是否有 error 级冲突（B.1 第 7 条拖拽判定）。
 * others 用 buildPlaced(ctx, items) 的结果（拖动已有条目时先剔除它自己）；
 * 与 validateLayout 同一套谓词，结果与「把候选加进 items 后整体校验、取候选条目的 error」完全一致（测试里有这条性质）。
 */
export function checkPlacement(ctx: LayoutContext, others: readonly PlacedItem[], candidate: LayoutItem): PlacementCheck {
  const conflicts: LayoutConflict[] = []
  const board = boardSize(ctx.shelf)
  if (candidate.bayIndex < 1 || candidate.bayIndex > ctx.bayCount) {
    conflicts.push(conflict('BAY_OUT_OF_RANGE', 'error', candidate.guid, 'Bay is out of range', undefined, { bayIndex: candidate.bayIndex, bayCount: ctx.bayCount }))
  }
  const spec = lookupRecord(ctx.specs, candidate.productCode)
  const hook = lookupRecord(ctx.hooks, candidate.hookModelGuid)
  if (!spec) conflicts.push(conflict('PRODUCT_SPEC_MISSING', 'error', candidate.guid, 'Product has no display spec'))
  if (!hook) conflicts.push(conflict('HOOK_MODEL_MISSING', 'error', candidate.guid, 'Hook model not found'))
  if (!spec || !hook) return { ok: false, conflicts, geometry: null }

  if (!isHoleInRange(ctx.shelf, hook, candidate.holeCol, candidate.holeRow)) {
    conflicts.push(conflict('HOLE_OUT_OF_RANGE', 'error', candidate.guid, 'Hole is outside the hole grid', undefined, {
      col: candidate.holeCol,
      row: candidate.holeRow,
      spanX: hook.holeSpanX,
      spanY: hook.holeSpanY,
      cols: ctx.shelf.holeCols,
      rows: ctx.shelf.holeRows,
    }))
    return { ok: false, conflicts, geometry: null }
  }

  const self = resolvePlaced(ctx, candidate, -1)!
  const geometry = self.geometry!
  const sameBay = others.filter((entry) => entry.geometry !== null && entry.item.bayIndex === candidate.bayIndex)
  for (const other of sameBay) {
    if (clashes(self, other)) conflicts.push(conflict('HOOK_HOLE_CLASH', 'error', candidate.guid, 'Hook holes overlap another hook', other.item.guid))
  }
  const isOutside = outOfBoard(geometry, board.widthMm, board.heightMm)
  if (isOutside) {
    conflicts.push(conflict('OUT_OF_BOARD', 'error', candidate.guid, 'Product (with price tag) is outside the board'))
  } else if (edgeGapTooSmall(geometry, board.widthMm, board.heightMm, ctx.gaps)) {
    conflicts.push(conflict('EDGE_GAP', 'error', candidate.guid, 'Gap to the board edge is smaller than the required gap', undefined, { needX: ctx.gaps.gapXMm, needY: ctx.gaps.gapYMm }))
  }
  for (const other of sameBay) {
    if (overlaps(geometry, other.geometry!, ctx.gaps)) {
      const clear = clearanceBetween(geometry, other.geometry!)
      conflicts.push(conflict('OVERLAP', 'error', candidate.guid, 'Gap to another product is too small', other.item.guid, {
        needX: ctx.gaps.gapXMm,
        needY: ctx.gaps.gapYMm,
        clearX: Math.round(clear.x * 10) / 10,
        clearY: Math.round(clear.y * 10) / 10,
      }))
    }
  }
  return { ok: conflicts.length === 0, conflicts, geometry }
}

// ---------------------------------------------------------------------------
// 吸附与坐标换算（画布与拖拽用）
// ---------------------------------------------------------------------------

/** 货架整体宽 = 节数 × 节宽（各节紧挨着，立柱在节与节的交界处各占半根）。 */
export function totalShelfWidth(shelf: Pick<LayoutShelf, 'bayWidthMm'>, bayCount: number): number {
  return shelf.bayWidthMm * bayCount
}

/** 第 bayIndex 节板面左边缘在「货架整体坐标」（左端立柱外缘为 0）里的 x：节左缘 + 半根立柱。 */
export function bayBoardOriginX(shelf: Pick<LayoutShelf, 'bayWidthMm' | 'uprightWidthMm'>, bayIndex: number): number {
  return (bayIndex - 1) * shelf.bayWidthMm + shelf.uprightWidthMm / 2
}

/** 货架整体坐标 → 所在节与该节板面坐标；超出整体范围时节号夹在 1..bayCount。 */
export function pointToBoard(
  shelf: Pick<LayoutShelf, 'bayWidthMm' | 'uprightWidthMm'>,
  bayCount: number,
  xMm: number,
  yMm: number,
): { bayIndex: number; x: number; y: number } {
  const bayIndex = Math.min(bayCount, Math.max(1, Math.floor(xMm / shelf.bayWidthMm) + 1))
  return { bayIndex, x: xMm - bayBoardOriginX(shelf, bayIndex), y: yMm }
}

/**
 * 吸附（B.1 第 7 条）：取最近的孔位，使挂钩锚点孔心距指针最近；
 * 结果夹在「能放下占孔区间」的范围内（col ∈ [1, 孔列数 − 占孔X + 1]），所以指针在孔阵外时也给出最近的合法孔位。
 */
export function snapAnchorToPoint(
  shelf: LayoutShelf,
  hook: Pick<LayoutHook, 'holeSpanX' | 'holeSpanY'>,
  boardX: number,
  boardY: number,
): { col: number; row: number } {
  const rawCol = Math.round((boardX - shelf.firstHoleLeftMm) / shelf.holePitchXMm) + 1
  const rawRow = Math.round((boardY - shelf.firstHoleTopMm) / shelf.holePitchYMm) + 1
  const maxCol = Math.max(1, shelf.holeCols - hook.holeSpanX + 1)
  const maxRow = Math.max(1, shelf.holeRows - hook.holeSpanY + 1)
  return { col: Math.min(maxCol, Math.max(1, rawCol)), row: Math.min(maxRow, Math.max(1, rawRow)) }
}
