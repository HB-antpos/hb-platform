/** 预览里要显示的文字，由调用方按当前语言传入。 */
export interface ProductDimPreviewLabels {
  /** 正视图中央，如「正视」 */
  front: string
  /** 侧视图下方，如「侧视」 */
  side: string
  /** 如「W 110」 */
  width: string
  /** 高度尺寸线的两行：{ name: 'H', value: '155' } */
  height: { name: string; value: string }
  /** 如「T 12」 */
  depth: string
  /** 如「吊孔 10」 */
  hangHole: string
  /** 正视图下方的一行说明，如「挂钩横向占位 = max(W, 价签 52)」 */
  footprint: string
}

interface ProductDimPreviewProps {
  widthMm: number | null | undefined
  heightMm: number | null | undefined
  depthMm: number | null | undefined
  hangHoleOffsetMm: number | null | undefined
  /** 吊孔类型 0 无 / 1 圆孔 / 2 欧式挂孔；0 时不画吊孔。 */
  hangHoleType: 0 | 1 | 2
  labels: ProductDimPreviewLabels
  ariaLabel: string
}

const SVG_WIDTH = 214
const SVG_HEIGHT = 220
const FRONT_X = 40
const FRONT_Y = 28
/** 正视图最大像素：宽 80、高 125（设计图 110 × 155 → 缩放约 0.7）。 */
const MAX_FRONT_W = 80
const MAX_FRONT_H = 125

function positive(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0
}

/** 包装尺寸示意：左正视（宽 × 高 + 吊孔）、右侧视（厚），随表单里输入的数字实时缩放。 */
export default function ProductDimPreview({ widthMm, heightMm, depthMm, hangHoleOffsetMm, hangHoleType, labels, ariaLabel }: ProductDimPreviewProps) {
  if (!positive(widthMm) || !positive(heightMm)) {
    return <svg className="planogram-preview-svg" width={SVG_WIDTH} height={SVG_HEIGHT} role="img" aria-label={ariaLabel} />
  }

  const s = Math.min(1, MAX_FRONT_W / widthMm, MAX_FRONT_H / heightMm)
  const w = widthMm * s
  const h = heightMm * s
  const t = positive(depthMm) ? Math.max(3, depthMm * s + 2) : 0
  const offset = typeof hangHoleOffsetMm === 'number' && hangHoleOffsetMm >= 0 ? hangHoleOffsetMm * s : 0
  const sideX = FRONT_X + w + 58

  return (
    <svg
      className="planogram-preview-svg"
      width={SVG_WIDTH}
      height={SVG_HEIGHT}
      viewBox={`0 0 ${SVG_WIDTH} ${SVG_HEIGHT}`}
      role="img"
      aria-label={ariaLabel}
      fontSize="9"
    >
      {/* 正视：外包装 + 吊孔 */}
      <rect x={FRONT_X} y={FRONT_Y} width={w} height={h} rx="2" fill="#fff1b8" stroke="#595959" />
      {hangHoleType === 1 ? (
        <circle cx={FRONT_X + w / 2} cy={FRONT_Y + offset} r="2.5" fill="#fff" stroke="#8c8c8c" />
      ) : hangHoleType === 2 ? (
        <rect x={FRONT_X + w / 2 - 6} y={FRONT_Y + offset - 2} width="12" height="4" rx="2" fill="#fff" stroke="#8c8c8c" />
      ) : null}
      <text x={FRONT_X + w / 2} y={FRONT_Y + h / 2 + 4} textAnchor="middle" fill="rgba(0,0,0,.55)">{labels.front}</text>
      {/* 尺寸线：宽（上）、高（左）、吊孔偏移（右上，橙色） */}
      <line x1={FRONT_X} y1={FRONT_Y - 10} x2={FRONT_X + w} y2={FRONT_Y - 10} stroke="#1677ff" strokeWidth=".8" />
      <text x={FRONT_X + w / 2} y={FRONT_Y - 13} textAnchor="middle" fill="#1677ff">{labels.width}</text>
      <line x1={FRONT_X - 10} y1={FRONT_Y} x2={FRONT_X - 10} y2={FRONT_Y + h} stroke="#1677ff" strokeWidth=".8" />
      <text x={FRONT_X - 13} y={FRONT_Y + h / 2} textAnchor="end" fill="#1677ff">{labels.height.name}</text>
      <text x={FRONT_X - 13} y={FRONT_Y + h / 2 + 10} textAnchor="end" fill="#1677ff">{labels.height.value}</text>
      {hangHoleType !== 0 && offset > 0 ? (
        <>
          <line x1={FRONT_X + w + 6} y1={FRONT_Y} x2={FRONT_X + w + 6} y2={FRONT_Y + offset} stroke="#fa8c16" strokeWidth=".8" />
          <text x={FRONT_X + w + 9} y={FRONT_Y + 8} fill="#fa8c16">{labels.hangHole}</text>
        </>
      ) : null}
      {/* 侧视：只看到厚度 */}
      {t > 0 ? (
        <>
          <rect x={sideX} y={FRONT_Y} width={t} height={h} fill="#fff1b8" stroke="#595959" />
          <line x1={sideX - 2} y1={FRONT_Y - 10} x2={sideX + t + 2} y2={FRONT_Y - 10} stroke="#1677ff" strokeWidth=".8" />
          <text x={sideX + t / 2 + 1} y={FRONT_Y - 13} textAnchor="middle" fill="#1677ff">{labels.depth}</text>
          <text x={sideX + t / 2 + 1} y={FRONT_Y + h + 14} textAnchor="middle" fill="rgba(0,0,0,.55)">{labels.side}</text>
        </>
      ) : null}
      {/* 说明另起一行靠左：包装很窄时居中会被画布裁掉 */}
      <text x={4} y={FRONT_Y + h + 30} fill="rgba(0,0,0,.55)" fontSize="8.5">{labels.footprint}</text>
    </svg>
  )
}
