import type {
  ConflictDto,
  HookModelDto,
  PlanogramDetailDto,
  PlanogramItemInput,
  PlanogramSaveInput,
  ProductSpecListItemDto,
  ShelfModelDto,
} from '../../../../types/planogram'
import {
  buildPlaced,
  checkPlacement,
  pointToBoard,
  snapAnchorToPoint,
  validateLayout,
  type LayoutConflict,
  type LayoutContext,
  type LayoutHook,
  type LayoutItem,
  type LayoutSpec,
  type PlacedItem,
  type PlacementGeometry,
} from '../engine/geometry'

/**
 * 设计器的纯逻辑（不依赖 React / 请求）：文档模型、撤销栈、条目增删改、向右复制、整行均分、拖拽落点判定、冲突定位等。
 * 画布与 store 只做接线，规则都在这里，便于单测。几何口径一律来自 engine/geometry.ts（与后端校验器同一套）。
 */

// ---------------------------------------------------------------------------
// 常量
// ---------------------------------------------------------------------------

/** 撤销 / 重做快照栈上限（方案第 6 节）。 */
export const HISTORY_LIMIT = 50
/** 单个方案明细上限（附录 B.2：PUT 时超出会被拒）。 */
export const MAX_PLAN_ITEMS = 2000
export const MIN_BAYS = 1
export const MAX_BAYS = 12
/** 商品间距默认值（决策 #9）与输入范围。 */
export const DEFAULT_GAP_MM = 10
export const MAX_GAP_MM = 200
/** 缩放范围（画布 1 mm = zoom px；100% 即 1 mm = 1 px）。 */
export const MIN_ZOOM = 25
export const MAX_ZOOM = 100
export const ZOOM_STEP = 5
/** 设计器可编辑的最小页面宽度；更窄只读并提示（方案第 6 节 / 任务约定）。 */
export const DESIGNER_MIN_EDIT_WIDTH = 1100

// ---------------------------------------------------------------------------
// 文档模型
// ---------------------------------------------------------------------------

export interface DesignerItem extends LayoutItem {
  perHookQty: number | null
  /** 服务端标记：保存时的商品尺寸快照与当前参数不一致（「参数已变」）。 */
  specChanged: boolean
}

/** 可撤销的整份编辑内容：头字段 + 明细。items 数组按不可变方式更新，快照只是引用，很便宜。 */
export interface DesignerDoc {
  name: string
  notes: string
  shelfModelGuid: string
  bayCount: number
  gapXMm: number
  gapYMm: number
  items: readonly DesignerItem[]
}

export function docFromDetail(detail: PlanogramDetailDto): DesignerDoc {
  const items = [...detail.items]
    .sort((a, b) => a.sortOrder - b.sortOrder)
    .map<DesignerItem>((item) => ({
      guid: item.guid,
      bayIndex: item.bayIndex,
      productCode: item.productCode,
      hookModelGuid: item.hookModelGuid,
      holeCol: item.holeCol,
      holeRow: item.holeRow,
      perHookQty: item.perHookQty,
      specChanged: item.specChanged === true,
    }))
  return {
    name: detail.header.name,
    notes: detail.header.notes ?? '',
    shelfModelGuid: detail.header.shelfModelGuid,
    bayCount: detail.header.bayCount,
    gapXMm: detail.header.gapXMm,
    gapYMm: detail.header.gapYMm,
    items,
  }
}

/** 保存请求体：明细 sortOrder 按当前顺序重排；specChanged 不提交。 */
export function buildSaveInput(doc: DesignerDoc, rowVersion: string): PlanogramSaveInput {
  const items = doc.items.map<PlanogramItemInput>((item, index) => ({
    guid: item.guid,
    bayIndex: item.bayIndex,
    face: 0,
    itemType: 1,
    productCode: item.productCode,
    hookModelGuid: item.hookModelGuid,
    holeCol: item.holeCol,
    holeRow: item.holeRow,
    perHookQty: item.perHookQty,
    sortOrder: index,
  }))
  return {
    name: doc.name.trim(),
    notes: doc.notes.trim() ? doc.notes.trim() : null,
    shelfModelGuid: doc.shelfModelGuid,
    bayCount: doc.bayCount,
    gapXMm: doc.gapXMm,
    gapYMm: doc.gapYMm,
    items,
    rowVersion,
  }
}

/** 撤销栈：把当前文档压入 past，超出上限丢最早的一条。 */
export function pushHistory(past: readonly DesignerDoc[], doc: DesignerDoc, limit = HISTORY_LIMIT): DesignerDoc[] {
  const next = [...past, doc]
  return next.length > limit ? next.slice(next.length - limit) : next
}

export function clampInt(value: number, min: number, max: number): number {
  if (!Number.isFinite(value)) return min
  return Math.min(max, Math.max(min, Math.round(value)))
}

export function clampZoom(value: number): number {
  return clampInt(value, MIN_ZOOM, MAX_ZOOM)
}

// ---------------------------------------------------------------------------
// 条目增删改（纯函数，返回新数组；未命中时返回原数组引用，调用方据此判断是否真的改了）
// ---------------------------------------------------------------------------

/** 客户端生成条目 guid（后端保留非空 guid，≤ 50 字符）。 */
export function newItemGuid(): string {
  const cryptoApi = typeof globalThis !== 'undefined' ? (globalThis as { crypto?: { randomUUID?: () => string } }).crypto : undefined
  if (cryptoApi?.randomUUID) return cryptoApi.randomUUID()
  // 没有 crypto.randomUUID 的环境（老浏览器 / 测试）：时间戳 + 随机数，足够在一份方案内唯一
  return `i${Date.now().toString(36)}${Math.random().toString(36).slice(2, 10)}`
}

export function createDesignerItem(params: {
  productCode: string
  hookModelGuid: string
  bayIndex: number
  holeCol: number
  holeRow: number
  perHookQty?: number | null
  guid?: string
}): DesignerItem {
  return {
    guid: params.guid ?? newItemGuid(),
    bayIndex: params.bayIndex,
    productCode: params.productCode,
    hookModelGuid: params.hookModelGuid,
    holeCol: params.holeCol,
    holeRow: params.holeRow,
    perHookQty: params.perHookQty ?? null,
    specChanged: false,
  }
}

export function patchItem(items: readonly DesignerItem[], guid: string, patch: Partial<Omit<DesignerItem, 'guid'>>): readonly DesignerItem[] {
  const index = items.findIndex((item) => item.guid === guid)
  if (index < 0) return items
  const current = items[index]
  const changed = (Object.keys(patch) as Array<keyof typeof patch>).some((key) => current[key] !== patch[key])
  if (!changed) return items
  const next = items.slice()
  next[index] = { ...current, ...patch }
  return next
}

export function removeItemByGuid(items: readonly DesignerItem[], guid: string): readonly DesignerItem[] {
  const next = items.filter((item) => item.guid !== guid)
  return next.length === items.length ? items : next
}

/** 新增条目的默认挂钩：商品参数的默认挂钩（须是启用中的型号）优先，否则用左栏当前挂钩。 */
export function pickHookForProduct(
  defaultHookGuid: string | null | undefined,
  activeHooks: readonly Pick<HookModelDto, 'guid'>[],
  currentHookGuid: string | null,
): string | null {
  if (defaultHookGuid && activeHooks.some((hook) => hook.guid === defaultHookGuid)) return defaultHookGuid
  if (currentHookGuid && activeHooks.some((hook) => hook.guid === currentHookGuid)) return currentHookGuid
  return activeHooks[0]?.guid ?? null
}

// ---------------------------------------------------------------------------
// 布局上下文（DTO → 引擎输入）
// ---------------------------------------------------------------------------

export function toLayoutHook(hook: HookModelDto): LayoutHook {
  return {
    hasPriceTagArm: hook.hasPriceTagArm,
    armLengthMm: hook.armLengthMm,
    dropMm: hook.dropMm,
    tipReserveMm: hook.tipReserveMm,
    tagWidthMm: hook.tagWidthMm,
    tagHeightMm: hook.tagHeightMm,
    tagBottomAboveArmMm: hook.tagBottomAboveArmMm,
    holeSpanX: hook.holeSpanX,
    holeSpanY: hook.holeSpanY,
    loadKg: hook.loadKg ?? null,
  }
}

export function toLayoutSpec(item: ProductSpecListItemDto): LayoutSpec | null {
  const spec = item.spec
  if (!item.hasSpec || !spec) return null
  return {
    widthMm: spec.widthMm,
    heightMm: spec.heightMm,
    depthMm: spec.depthMm,
    hangHoleOffsetMm: spec.hangHoleOffsetMm,
    unitWeightG: spec.unitWeightG ?? null,
    hookQtyOverrides: spec.hookQtyOverrides,
    retailPrice: item.retailPrice,
  }
}

export function buildLayoutContext(args: {
  shelf: ShelfModelDto | null | undefined
  hooks: readonly HookModelDto[]
  specs: Readonly<Record<string, ProductSpecListItemDto>>
  bayCount: number
  gapXMm: number
  gapYMm: number
}): LayoutContext | null {
  if (!args.shelf) return null
  const hooks: Record<string, LayoutHook> = {}
  for (const hook of args.hooks) hooks[hook.guid] = toLayoutHook(hook)
  const specs: Record<string, LayoutSpec> = {}
  for (const [code, item] of Object.entries(args.specs)) {
    const layoutSpec = toLayoutSpec(item)
    if (layoutSpec) specs[code] = layoutSpec
  }
  return { shelf: args.shelf, hooks, specs, bayCount: args.bayCount, gaps: { gapXMm: args.gapXMm, gapYMm: args.gapYMm } }
}

// ---------------------------------------------------------------------------
// 清单顺序（设计器 Tab 选择、PDF 序号气泡 / 执行清单共用同一个排序）
// ---------------------------------------------------------------------------

/**
 * 执行清单顺序：按「节 → 行 → 列」排序（自上而下、自左而右上货），完全相同时保持原有先后（稳定）。
 * PDF 的序号 = 排序后的 1..N；设计器里 Tab 切换选中项的顺序也用它，所以画布上看到的顺序与清单序号一致。
 */
export function sortChecklistItems<T extends { bayIndex: number; holeRow: number; holeCol: number }>(items: readonly T[]): T[] {
  return items
    .map((item, index) => ({ item, index }))
    .sort((a, b) => a.item.bayIndex - b.item.bayIndex || a.item.holeRow - b.item.holeRow || a.item.holeCol - b.item.holeCol || a.index - b.index)
    .map((entry) => entry.item)
}

// ---------------------------------------------------------------------------
// 冲突：索引、定位顺序、本地 / 服务端差异
// ---------------------------------------------------------------------------

export function conflictsByItem<T extends Pick<ConflictDto, 'itemGuid'>>(conflicts: readonly T[]): Map<string, T[]> {
  const map = new Map<string, T[]>()
  for (const entry of conflicts) {
    const list = map.get(entry.itemGuid)
    if (list) list.push(entry)
    else map.set(entry.itemGuid, [entry])
  }
  return map
}

/** 有 error 级冲突的条目 guid，按「节 → 行 → 列」排序（「冲突 N · 定位」逐个跳转的顺序）。 */
export function conflictLocateList(conflicts: readonly Pick<ConflictDto, 'itemGuid' | 'severity'>[], items: readonly DesignerItem[]): string[] {
  const errorGuids = new Set(conflicts.filter((entry) => entry.severity === 'error').map((entry) => entry.itemGuid))
  return items
    .filter((item) => errorGuids.has(item.guid))
    .sort((a, b) => a.bayIndex - b.bayIndex || a.holeRow - b.holeRow || a.holeCol - b.holeCol)
    .map((item) => item.guid)
}

/** 下一个要定位的条目：当前选中的不在列表里就取第一个，在列表里就取它后面一个（循环）。 */
export function nextLocateGuid(list: readonly string[], currentGuid: string | null): string | null {
  if (list.length === 0) return null
  const index = currentGuid ? list.indexOf(currentGuid) : -1
  return list[(index + 1) % list.length]
}

function conflictSetKey(conflicts: readonly Pick<ConflictDto, 'code' | 'itemGuid' | 'otherItemGuid' | 'severity'>[]): string {
  return conflicts
    .map((entry) => `${entry.severity}|${entry.code}|${entry.itemGuid}|${entry.otherItemGuid ?? ''}`)
    .sort()
    .join('\n')
}

/** 本地复算与服务端返回的冲突集合是否一致（按 严重度 / code / 条目 / 对方 比，不比 message）。 */
export function sameConflictSet(
  a: readonly Pick<ConflictDto, 'code' | 'itemGuid' | 'otherItemGuid' | 'severity'>[],
  b: readonly Pick<ConflictDto, 'code' | 'itemGuid' | 'otherItemGuid' | 'severity'>[],
): boolean {
  return conflictSetKey(a) === conflictSetKey(b)
}

/** 一个条目的主要冲突（拖拽提示 / 条目角标用）：按严重度与判定顺序取第一条。 */
const CONFLICT_PRIORITY: readonly string[] = [
  'BAY_OUT_OF_RANGE',
  'PRODUCT_SPEC_MISSING',
  'HOOK_MODEL_MISSING',
  'HOLE_OUT_OF_RANGE',
  'HOOK_HOLE_CLASH',
  'OUT_OF_BOARD',
  'EDGE_GAP',
  'OVERLAP',
  'OVERLOAD',
]

export function primaryConflict<T extends Pick<ConflictDto, 'code' | 'severity'>>(conflicts: readonly T[]): T | null {
  let best: T | null = null
  for (const entry of conflicts) {
    if (!best) {
      best = entry
      continue
    }
    const bestRank = (best.severity === 'error' ? 0 : 100) + CONFLICT_PRIORITY.indexOf(best.code)
    const rank = (entry.severity === 'error' ? 0 : 100) + CONFLICT_PRIORITY.indexOf(entry.code)
    if (rank < bestRank) best = entry
  }
  return best
}

// ---------------------------------------------------------------------------
// 拖拽落点
// ---------------------------------------------------------------------------

export interface DropHint {
  bayIndex: number
  holeCol: number
  holeRow: number
  ok: boolean
  conflicts: LayoutConflict[]
  geometry: PlacementGeometry | null
  productCode: string
  hookModelGuid: string
}

/** 拖拽候选条目的占位 guid（不会真的进 items）。 */
export const DROP_CANDIDATE_GUID = '__drop-candidate__'

/**
 * 货架整体坐标（mm，左端立柱外缘为 0、板顶为 0）的指针位置 → 吸附孔位 + 可放判定（B.1 第 7 条）。
 * placed 是 buildPlaced(ctx, items) 的结果；拖动已有条目时用 ignoreGuid 把它自己排除在外。
 * 没有陈列参数 / 挂钩型号缺失时返回 null（界面提示「没有陈列参数」而不是画一个无效的幽灵）。
 */
export function buildDropHint(
  ctx: LayoutContext,
  placed: readonly PlacedItem[],
  point: { xMm: number; yMm: number },
  target: { productCode: string; hookModelGuid: string; perHookQty?: number | null },
  ignoreGuid?: string,
): DropHint | null {
  const hook = ctx.hooks[target.hookModelGuid]
  const spec = ctx.specs[target.productCode]
  if (!hook || !spec) return null
  const board = pointToBoard(ctx.shelf, ctx.bayCount, point.xMm, point.yMm)
  const anchor = snapAnchorToPoint(ctx.shelf, hook, board.x, board.y)
  const candidate: LayoutItem = {
    guid: DROP_CANDIDATE_GUID,
    bayIndex: board.bayIndex,
    productCode: target.productCode,
    hookModelGuid: target.hookModelGuid,
    holeCol: anchor.col,
    holeRow: anchor.row,
    perHookQty: target.perHookQty ?? null,
  }
  const others = ignoreGuid ? placed.filter((entry) => entry.item.guid !== ignoreGuid) : placed
  const check = checkPlacement(ctx, others, candidate)
  return {
    bayIndex: candidate.bayIndex,
    holeCol: candidate.holeCol,
    holeRow: candidate.holeRow,
    ok: check.ok,
    conflicts: check.conflicts,
    geometry: check.geometry,
    productCode: target.productCode,
    hookModelGuid: target.hookModelGuid,
  }
}

// ---------------------------------------------------------------------------
// 找空位：向右复制、键盘 / 按钮「添加到方案」
// ---------------------------------------------------------------------------

export interface Slot {
  bayIndex: number
  holeCol: number
  holeRow: number
}

/**
 * 向右复制（方案第 6 节）：按本商品有效宽度 + 左右间距，从原位置右侧第一个「左缘 ≥ 原右缘 + 间距」的孔列起，
 * 逐列向右找第一个能放（无 error 级冲突）的孔位；同节同行，一直找到孔阵右端，找不到返回 null。
 */
export function findCopyRightSlot(ctx: LayoutContext, items: readonly DesignerItem[], guid: string): Slot | null {
  const placed = buildPlaced(ctx, items)
  const source = placed.find((entry) => entry.item.guid === guid)
  if (!source || !source.geometry) return null
  const { shelf, gaps } = ctx
  const pitch = shelf.holePitchXMm
  const half = ((source.hook.holeSpanX - 1) * pitch) / 2
  const minCenter = source.geometry.right + gaps.gapXMm + source.geometry.effectiveWidth / 2
  // 中心 x = firstLeft + (col − 1) × pitch + half ≥ minCenter；减去极小量避免恰好相等时被向上取整多跳一列
  const firstCol = Math.max(source.item.holeCol + 1, Math.ceil((minCenter - half - shelf.firstHoleLeftMm) / pitch - 1e-9) + 1)
  const maxCol = shelf.holeCols - source.hook.holeSpanX + 1
  for (let col = firstCol; col <= maxCol; col += 1) {
    const candidate: LayoutItem = { ...source.item, guid: DROP_CANDIDATE_GUID, holeCol: col, perHookQty: source.item.perHookQty }
    if (checkPlacement(ctx, placed, candidate).ok) return { bayIndex: source.item.bayIndex, holeCol: col, holeRow: source.item.holeRow }
  }
  return null
}

/** 第一个能放下的空位（按 节 → 行 → 列 扫描）：键盘 / 「＋」按钮添加商品用，不依赖拖拽。 */
export function findFirstFreeSlot(ctx: LayoutContext, items: readonly DesignerItem[], target: { productCode: string; hookModelGuid: string }): Slot | null {
  const hook = ctx.hooks[target.hookModelGuid]
  if (!hook || !ctx.specs[target.productCode]) return null
  const placed = buildPlaced(ctx, items)
  const maxCol = ctx.shelf.holeCols - hook.holeSpanX + 1
  const maxRow = ctx.shelf.holeRows - hook.holeSpanY + 1
  for (let bay = 1; bay <= ctx.bayCount; bay += 1) {
    for (let row = 1; row <= maxRow; row += 1) {
      for (let col = 1; col <= maxCol; col += 1) {
        const candidate: LayoutItem = { guid: DROP_CANDIDATE_GUID, bayIndex: bay, productCode: target.productCode, hookModelGuid: target.hookModelGuid, holeCol: col, holeRow: row, perHookQty: null }
        if (checkPlacement(ctx, placed, candidate).ok) return { bayIndex: bay, holeCol: col, holeRow: row }
      }
    }
  }
  return null
}

// ---------------------------------------------------------------------------
// 整行均分
// ---------------------------------------------------------------------------

export type DistributeResult =
  | { ok: true; items: readonly DesignerItem[]; moved: number }
  | { ok: false; reason: 'invalid' | 'tooFew' | 'noRoom' | 'conflict' }

/**
 * 整行均分：把「同节、同锚点行」的整行商品在板面宽度内从左到右等间距排开
 * （左右板边与相邻商品之间的净间距相等，且不小于方案的左右间距）。孔位是离散的，每件取最近孔位，误差 ≤ 半个孔距。
 * 做法：先从右往左算出每件「最右能放到哪一列」，再从左往右按理想位置取值并夹在 [最小列, 最右列] 内，保证后面的商品一定还放得下。
 * 放不下（总宽 + 最小间距超出板面、或均分后产生了新的冲突）返回失败，不改动任何条目。
 */
export function distributeRow(ctx: LayoutContext, items: readonly DesignerItem[], guid: string): DistributeResult {
  const placed = buildPlaced(ctx, items)
  const anchor = placed.find((entry) => entry.item.guid === guid)
  if (!anchor || !anchor.geometry) return { ok: false, reason: 'invalid' }
  const row = placed
    .filter((entry) => entry.geometry !== null && entry.item.bayIndex === anchor.item.bayIndex && entry.item.holeRow === anchor.item.holeRow)
    .sort((a, b) => a.geometry!.centerX - b.geometry!.centerX || a.index - b.index)
  if (row.length < 2) return { ok: false, reason: 'tooFew' }

  const { shelf, gaps } = ctx
  const pitch = shelf.holePitchXMm
  const boardW = shelf.bayWidthMm - shelf.uprightWidthMm
  const EPS = 1e-9
  const half = (entry: PlacedItem) => ((entry.hook.holeSpanX - 1) * pitch) / 2
  const width = (entry: PlacedItem) => entry.geometry!.effectiveWidth
  const centerAt = (entry: PlacedItem, col: number) => shelf.firstHoleLeftMm + (col - 1) * pitch + half(entry)
  const minColFor = (entry: PlacedItem, leftBound: number) => Math.max(1, Math.ceil((leftBound + width(entry) / 2 - half(entry) - shelf.firstHoleLeftMm) / pitch - EPS) + 1)
  const maxColFor = (entry: PlacedItem, rightBound: number) =>
    Math.min(shelf.holeCols - entry.hook.holeSpanX + 1, Math.floor((rightBound - width(entry) / 2 - half(entry) - shelf.firstHoleLeftMm) / pitch + EPS) + 1)

  const totalWidth = row.reduce((sum, entry) => sum + width(entry), 0)
  const evenGap = (boardW - totalWidth) / (row.length + 1)
  if (evenGap < gaps.gapXMm - EPS) return { ok: false, reason: 'noRoom' }

  // 从右往左：每件在「后面的都贴右边放」时最右能放到哪一列
  const maxCols: number[] = new Array(row.length)
  let rightBound = boardW - gaps.gapXMm
  for (let i = row.length - 1; i >= 0; i -= 1) {
    const col = maxColFor(row[i], rightBound)
    maxCols[i] = col
    rightBound = centerAt(row[i], col) - width(row[i]) / 2 - gaps.gapXMm
  }

  // 从左往右：理想位置（等间距）夹在 [最小列, 最右列] 内
  const cols: number[] = new Array(row.length)
  let leftBound = gaps.gapXMm
  let idealLeft = evenGap
  for (let i = 0; i < row.length; i += 1) {
    const entry = row[i]
    const minCol = minColFor(entry, leftBound)
    if (minCol > maxCols[i]) return { ok: false, reason: 'noRoom' }
    const idealCenter = idealLeft + width(entry) / 2
    const idealCol = Math.round((idealCenter - half(entry) - shelf.firstHoleLeftMm) / pitch) + 1
    const col = Math.min(maxCols[i], Math.max(minCol, idealCol))
    cols[i] = col
    leftBound = centerAt(entry, col) + width(entry) / 2 + gaps.gapXMm
    idealLeft += width(entry) + evenGap
  }

  const colByGuid = new Map<string, number>(row.map((entry, i) => [entry.item.guid, cols[i]]))
  let moved = 0
  const next = items.map((item) => {
    const col = colByGuid.get(item.guid)
    if (col === undefined || col === item.holeCol) return item
    moved += 1
    return { ...item, holeCol: col }
  })
  if (moved === 0) return { ok: true, items, moved: 0 }

  // 均分后不能比之前多出冲突条目（例如跨行的高商品被挤到别的商品上）
  const before = validateLayout(ctx, items).stats.conflictCount
  const after = validateLayout(ctx, next).stats.conflictCount
  if (after > before) return { ok: false, reason: 'conflict' }
  return { ok: true, items: next, moved }
}

// ---------------------------------------------------------------------------
// 画布渲染辅助
// ---------------------------------------------------------------------------

/** 商品包装色板（沿用设计稿 plano.js 的 11 色）：没有实拍图时按货号哈希取色，同一商品在左栏与画布颜色一致。 */
export const PRODUCT_PALETTE = ['#ffd8bf', '#bae0ff', '#d9f7be', '#fff1b8', '#ffccc7', '#d3adf7', '#b5f5ec', '#ffd6e7', '#f4ffb8', '#ffe7ba', '#d6e4ff'] as const

export function productColor(productCode: string): string {
  let hash = 0
  for (let i = 0; i < productCode.length; i += 1) hash = (hash * 31 + productCode.charCodeAt(i)) >>> 0
  return PRODUCT_PALETTE[hash % PRODUCT_PALETTE.length]
}

// 中日韩字符范围：CJK 标点、CJK 统一表意文字（含扩展 A）、全角形式（用转义写，源码里不出现中文字符）
const CJK_PATTERN = /[\u3000-\u303f\u3400-\u9fff\uff00-\uffef]/

/** 估算一个字符的宽度（以字号为单位）：中日韩字符 1 倍字号，其余 0.56 倍。 */
function charWidth(char: string): number {
  return CJK_PATTERN.test(char) ? 1 : 0.56
}

/**
 * 商品名折行（画布里的文字标签）：宽度不够就折行，最多 maxLines 行，超出的末行加省略号。
 * 切分单位：中日韩字符逐字，拉丁字母 / 数字按「单词」整体（优先在空格处断开）；单词比一行还长时按字符截断。
 */
export function wrapLabel(text: string, maxWidthPx: number, fontPx: number, maxLines: number): string[] {
  const clean = text.trim()
  if (!clean || maxLines < 1 || maxWidthPx < fontPx) return []
  const widthOf = (value: string) => [...value].reduce((sum, char) => sum + charWidth(char) * fontPx, 0)
  const fits = (value: string) => widthOf(value) <= maxWidthPx

  const tokens: Array<{ text: string; spaceBefore: boolean }> = []
  for (const match of clean.matchAll(/[\u3000-\u303f\u3400-\u9fff\uff00-\uffef]|[^\s\u3000-\u303f\u3400-\u9fff\uff00-\uffef]+/gu)) {
    const index = match.index ?? 0
    tokens.push({ text: match[0], spaceBefore: index > 0 && /\s/.test(clean[index - 1]) })
  }

  const lines: string[] = []
  let current = ''
  for (const token of tokens) {
    const piece = (current && token.spaceBefore ? ' ' : '') + token.text
    if (fits(current + piece)) {
      current += piece
      continue
    }
    if (current) lines.push(current)
    current = ''
    if (fits(token.text)) {
      current = token.text
      continue
    }
    // 单个「单词」比一行还长：按字符截成多行（空行首总是至少放一个字符，避免死循环 / 空行）
    for (const char of token.text) {
      if (!current || fits(current + char)) current += char
      else {
        lines.push(current)
        current = char
      }
    }
  }
  if (current) lines.push(current)

  if (lines.length <= maxLines) return lines
  const kept = lines.slice(0, maxLines)
  let last = kept[maxLines - 1]
  while ([...last].length > 1 && !fits(`${last}…`)) last = [...last].slice(0, -1).join('')
  kept[maxLines - 1] = `${last}…`
  return kept
}

/** 文字的估算宽度（px）：画布里的提示气泡 / 标签定宽用。 */
export function estimateTextWidth(text: string, fontPx: number): number {
  return [...text].reduce((sum, char) => sum + charWidth(char) * fontPx, 0)
}

/** 货号后四位 + 每钩挂数，画在商品包装底部（设计稿「1211·×15」）。 */
export function itemFootLabel(code: string, qty: number): string {
  return `${code.slice(-4)}·×${qty}`
}

/** 打印版（设计稿 print 模式「1211 ×15」）：中间是空格，不带间隔点。 */
export function itemFootLabelPrint(code: string, qty: number): string {
  return `${code.slice(-4)} ×${qty}`
}
