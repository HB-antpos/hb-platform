import { useId } from 'react'
import { boardSize, previewScale, type ShelfGeometryLike } from '../engine/holeGrid'

/** 预览里要显示的文字，由调用方按当前语言传入（组件本身不碰 i18n，批 2 设计器可直接复用）。 */
export interface ShelfFrontPreviewLabels {
  /** 上方节宽尺寸线，如「节宽 900」。 */
  bayWidth: string
  /** 右侧总高尺寸线：名称与数值分两行，如 { name: '总高', value: '1200' }。 */
  totalHeight: { name: string; value: string }
  /** 右侧板面高尺寸线（绿色）。 */
  board: { name: string; value: string }
  /** 底板条上的文字，如「底板 深 300 · 面高 150」。 */
  base: string
}

interface ShelfFrontPreviewProps {
  shelf: ShelfGeometryLike & { baseDepthMm?: number }
  labels: ShelfFrontPreviewLabels
  /** 预览框最大宽高（px），货架按它等比缩小；默认与设计图一致（900 × 1200 → 缩放 0.13）。 */
  maxWidthPx?: number
  maxHeightPx?: number
  ariaLabel: string
}

/** 孔太密（缩放后孔距小于这个像素）时不画孔阵，避免整块变成灰糊。 */
const MIN_HOLE_PITCH_PX = 1.6

function isValid(shelf: ShelfGeometryLike): boolean {
  const numbers = [
    shelf.bayWidthMm, shelf.totalHeightMm, shelf.baseTopHeightMm, shelf.uprightWidthMm,
    shelf.holePitchXMm, shelf.holePitchYMm, shelf.firstHoleLeftMm, shelf.firstHoleTopMm,
  ]
  return numbers.every((value) => typeof value === 'number' && Number.isFinite(value)) &&
    shelf.bayWidthMm > 0 && shelf.totalHeightMm > 0 &&
    shelf.baseTopHeightMm >= 0 && shelf.baseTopHeightMm < shelf.totalHeightMm &&
    shelf.uprightWidthMm >= 0 && shelf.uprightWidthMm < shelf.bayWidthMm
}

/**
 * 一节货架的正视图（纯 SVG）：灰色立柱与底板、白色板面、按孔距 / 首孔边距铺开的孔阵、尺寸线。
 * 几何都来自 engine/holeGrid.ts 的同一套公式；板面放不下的孔阵被裁剪在板面内（孔数填多了的差异由表单里的提示文字说明）。
 */
export default function ShelfFrontPreview({ shelf, labels, maxWidthPx = 130, maxHeightPx = 156, ariaLabel }: ShelfFrontPreviewProps) {
  const rawId = useId()
  const patternId = `planogram-holes-${rawId.replace(/[^a-zA-Z0-9_-]/g, '')}`
  const clipId = `planogram-board-${rawId.replace(/[^a-zA-Z0-9_-]/g, '')}`
  if (!isValid(shelf)) {
    return <svg className="planogram-preview-svg" width={maxWidthPx + 90} height={maxHeightPx + 40} role="img" aria-label={ariaLabel} />
  }

  const s = previewScale(shelf, maxWidthPx, maxHeightPx)
  const ox = 8
  const oy = 20
  const bayW = shelf.bayWidthMm * s
  const bayH = shelf.totalHeightMm * s
  const upright = shelf.uprightWidthMm * s
  const board = boardSize(shelf)
  // 板面在立柱之间：左右各让出半根立柱（与设计图一致：900 节宽、30 立柱 → 板面左边 15）
  const boardLeft = ox + (shelf.uprightWidthMm / 2) * s
  const boardTop = oy
  const boardW = board.widthMm * s
  const boardH = board.heightMm * s
  const baseTop = oy + boardH
  const baseH = shelf.baseTopHeightMm * s

  const pitchXPx = shelf.holePitchXMm * s
  const pitchYPx = shelf.holePitchYMm * s
  const drawHoles = pitchXPx >= MIN_HOLE_PITCH_PX && pitchYPx >= MIN_HOLE_PITCH_PX && shelf.holeCols > 0 && shelf.holeRows > 0
  const holesX = boardLeft + (shelf.firstHoleLeftMm - shelf.holePitchXMm / 2) * s
  const holesY = boardTop + (shelf.firstHoleTopMm - shelf.holePitchYMm / 2) * s

  const dimX = ox + bayW + 10
  const width = dimX + 62
  const height = oy + bayH + 14

  return (
    <svg
      className="planogram-preview-svg"
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      role="img"
      aria-label={ariaLabel}
      fontSize="9"
    >
      <defs>
        {drawHoles ? (
          <pattern id={patternId} x={holesX} y={holesY} width={pitchXPx} height={pitchYPx} patternUnits="userSpaceOnUse">
            <circle cx={pitchXPx / 2} cy={pitchYPx / 2} r={Math.min(0.9, pitchXPx / 4)} fill="#bfbfbf" />
          </pattern>
        ) : null}
        <clipPath id={clipId}>
          <rect x={boardLeft} y={boardTop} width={boardW} height={boardH} />
        </clipPath>
      </defs>
      {/* 一节货架外框 */}
      <rect x={ox} y={oy} width={bayW} height={bayH} fill="#f5f5f5" stroke="#bfbfbf" />
      {/* 板面 + 孔阵 */}
      <rect x={boardLeft} y={boardTop} width={boardW} height={boardH} fill="#fff" stroke="#d9d9d9" />
      {drawHoles ? (
        <rect
          x={holesX}
          y={holesY}
          width={shelf.holeCols * pitchXPx}
          height={shelf.holeRows * pitchYPx}
          fill={`url(#${patternId})`}
          clipPath={`url(#${clipId})`}
        />
      ) : null}
      {/* 立柱与底板 */}
      <rect x={ox} y={oy} width={upright} height={bayH} fill="#e8e8e8" stroke="#bfbfbf" />
      <rect x={ox + bayW - upright} y={oy} width={upright} height={bayH} fill="#e8e8e8" stroke="#bfbfbf" />
      <rect x={ox} y={baseTop} width={bayW} height={baseH} fill="#ededed" stroke="#bfbfbf" />
      <text x={ox + bayW / 2} y={baseTop + baseH / 2 + 3} textAnchor="middle" fill="rgba(0,0,0,.45)">
        {labels.base}
      </text>
      {/* 尺寸线：节宽（上）、总高与板面高（右） */}
      <line x1={ox} y1={oy - 8} x2={ox + bayW} y2={oy - 8} stroke="#1677ff" strokeWidth=".8" />
      <text x={ox + bayW / 2} y={oy - 11} textAnchor="middle" fill="#1677ff">{labels.bayWidth}</text>
      <line x1={dimX} y1={oy} x2={dimX} y2={oy + bayH} stroke="#1677ff" strokeWidth=".8" />
      <text x={dimX + 4} y={oy + bayH / 2 - 2} fill="#1677ff">{labels.totalHeight.name}</text>
      <text x={dimX + 4} y={oy + bayH / 2 + 9} fill="#1677ff">{labels.totalHeight.value}</text>
      <line x1={dimX + 26} y1={oy} x2={dimX + 26} y2={oy + boardH} stroke="#52c41a" strokeWidth=".8" />
      <text x={dimX + 30} y={oy + boardH / 2 - 2} fill="#52c41a">{labels.board.name}</text>
      <text x={dimX + 30} y={oy + boardH / 2 + 9} fill="#52c41a">{labels.board.value}</text>
    </svg>
  )
}
