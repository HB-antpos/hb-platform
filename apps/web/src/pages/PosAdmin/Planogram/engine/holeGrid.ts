/**
 * 货架孔阵换算与几何（纯函数）。批 1 的货架型号抽屉用它换算孔数、画正视预览；
 * 批 2 的设计器会复用同一套孔位坐标，所以这里只放与 UI 框架无关的数字计算。
 *
 * 坐标约定（方案第 2 节）：以「板面」左上角为原点，单位 mm；
 * 孔心 x = firstHoleLeft + (col − 1) × pitchX，y = firstHoleTop + (row − 1) × pitchY，col / row 都从 1 起。
 */

const EPSILON = 1e-9

export interface ShelfGeometryLike {
  bayWidthMm: number
  totalHeightMm: number
  baseTopHeightMm: number
  uprightWidthMm: number
  holePitchXMm: number
  holePitchYMm: number
  firstHoleLeftMm: number
  firstHoleTopMm: number
  holeCols: number
  holeRows: number
}

/** 板面可用尺寸：宽 = 节宽 − 立柱宽，高 = 总高 − 底板面离地高（与后端自动换算同一口径）。 */
export function boardSize(shelf: Pick<ShelfGeometryLike, 'bayWidthMm' | 'uprightWidthMm' | 'totalHeightMm' | 'baseTopHeightMm'>) {
  return {
    widthMm: shelf.bayWidthMm - shelf.uprightWidthMm,
    heightMm: shelf.totalHeightMm - shelf.baseTopHeightMm,
  }
}

/**
 * 沿一个方向自动换算孔数：⌊(板面长 − 2 × 首孔边距) ÷ 孔距⌋ + 1。
 * 两端边距取同一个首孔边距；板面放不下一个孔、孔距不是正数时返回 0。
 */
export function autoHoleCount(boardLengthMm: number, firstHoleMm: number, pitchMm: number): number {
  if (!Number.isFinite(boardLengthMm) || !Number.isFinite(firstHoleMm) || !Number.isFinite(pitchMm)) return 0
  if (pitchMm <= 0 || firstHoleMm < 0) return 0
  const span = boardLengthMm - 2 * firstHoleMm
  if (span < 0) return 0
  return Math.floor(span / pitchMm + EPSILON) + 1
}

export function autoHoleGrid(shelf: ShelfGeometryLike): { cols: number; rows: number } {
  const board = boardSize(shelf)
  return {
    cols: autoHoleCount(board.widthMm, shelf.firstHoleLeftMm, shelf.holePitchXMm),
    rows: autoHoleCount(board.heightMm, shelf.firstHoleTopMm, shelf.holePitchYMm),
  }
}

/** 孔心坐标（mm，板面左上为原点，col / row 从 1 起）。 */
export function holeCenter(
  shelf: Pick<ShelfGeometryLike, 'holePitchXMm' | 'holePitchYMm' | 'firstHoleLeftMm' | 'firstHoleTopMm'>,
  col: number,
  row: number,
) {
  return {
    x: shelf.firstHoleLeftMm + (col - 1) * shelf.holePitchXMm,
    y: shelf.firstHoleTopMm + (row - 1) * shelf.holePitchYMm,
  }
}

/** 孔阵最后一个孔到板面右 / 下边缘的余量（mm）；负数表示孔阵超出了板面。 */
export function holeGridMargins(shelf: ShelfGeometryLike) {
  const board = boardSize(shelf)
  const last = holeCenter(shelf, Math.max(1, shelf.holeCols), Math.max(1, shelf.holeRows))
  return { rightMm: board.widthMm - last.x, bottomMm: board.heightMm - last.y }
}

/** 手填孔数与自动换算值不一致的提示：返回 null 表示一致（或手填值缺失，由后端按换算值补）。 */
export function holeCountMismatch(manual: number | null | undefined, auto: number): { manual: number; auto: number } | null {
  if (typeof manual !== 'number' || !Number.isFinite(manual) || manual <= 0) return null
  return manual === auto ? null : { manual, auto }
}

/** 预览缩放：把一节货架按 maxWidth × maxHeight 的框等比缩放，不放大超过 1:1。 */
export function previewScale(shelf: Pick<ShelfGeometryLike, 'bayWidthMm' | 'totalHeightMm'>, maxWidthPx: number, maxHeightPx: number): number {
  if (!(shelf.bayWidthMm > 0) || !(shelf.totalHeightMm > 0)) return 0
  return Math.min(maxWidthPx / shelf.bayWidthMm, maxHeightPx / shelf.totalHeightMm, 1)
}
