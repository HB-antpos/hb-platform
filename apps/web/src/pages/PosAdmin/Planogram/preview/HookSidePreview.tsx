import { suggestHookQty } from '../engine/hookQty'

/** 示意用的样品包装厚度（mm）：与设计图一致，用来演示「每钩挂数 = ⌊(L − 预留) ÷ 厚⌋」。 */
export const HOOK_PREVIEW_SAMPLE_DEPTH_MM = 20

export interface HookSidePreviewGeometry {
  hasPriceTagArm: boolean
  armLengthMm: number
  dropMm: number
  wireDiameterMm: number
  tipReserveMm: number
  tagWidthMm: number
  tagHeightMm: number
  tagBottomAboveArmMm: number
}

/** 预览里要显示的文字，由调用方按当前语言传入。 */
export interface HookSidePreviewLabels {
  /** 如「价签 52×26」 */
  tag: string
  /** 如「底边高 10」 */
  tagBottom: string
  /** 如「臂长 L = 200」 */
  armLength: string
  /** 如「H 50」 */
  drop: string
  /** 如「预留 20」 */
  reserve: string
  /** 如「每钩挂数 = ⌊(L − 预留) ÷ 包装厚⌋ = ⌊180 ÷ 20⌋ = 9」 */
  formula: string
}

interface HookSidePreviewProps {
  hook: HookSidePreviewGeometry
  labels: HookSidePreviewLabels
  ariaLabel: string
}

const WIDTH = 340
const BOARD_X = 286
const BOARD_W = 10
/** 挂钩插进洞洞板的孔位轴线。 */
const ANCHOR_X = 291
/** 一根上臂（价签臂）所在的孔行位置（画面上方留出尺寸线与板面顶端）。 */
const UPPER_ARM_Y = 44
/** 样品包装在画面里的固定高度（px，仅示意）。 */
const SAMPLE_HEIGHT_PX = 66
/** 示意图里臂长最多占的像素宽度；臂更长时整体等比缩小。 */
const MAX_ARM_PX = 215
/** 吊孔距顶边的示意值（mm，默认 10）：商品顶边 = 挂货臂 − 吊孔偏移。 */
const SAMPLE_HANG_OFFSET_MM = 10

function isValid(hook: HookSidePreviewGeometry): boolean {
  return [hook.armLengthMm, hook.dropMm, hook.wireDiameterMm].every((v) => typeof v === 'number' && Number.isFinite(v) && v > 0) &&
    [hook.tipReserveMm, hook.tagWidthMm, hook.tagHeightMm, hook.tagBottomAboveArmMm].every((v) => typeof v === 'number' && Number.isFinite(v) && v >= 0)
}

/**
 * 挂钩侧视图（纯 SVG）：洞洞板侧面在右，上臂带价签架、下臂挂货，货品沿下臂从板面一侧排开，臂端预留不挂货。
 * 价签架底边比挂货臂高 tagBottomAboveArmMm，所以默认值下正好压在吊孔偏移 10 的商品顶边上。
 */
export default function HookSidePreview({ hook, labels, ariaLabel }: HookSidePreviewProps) {
  if (!isValid(hook)) {
    return <svg className="planogram-preview-svg" width={WIDTH} height={200} role="img" aria-label={ariaLabel} />
  }

  const s = Math.min(1, MAX_ARM_PX / hook.armLengthMm)
  const arm = hook.armLengthMm * s
  const tipX = ANCHOR_X - arm
  const y0 = UPPER_ARM_Y
  const y1 = y0 + hook.dropMm * s
  const wire = Math.min(5, Math.max(2, hook.wireDiameterMm * s * 0.7))

  // 样品包装：厚度 20 mm，沿下臂从板面一侧往臂端排；数量 = 自动换算值
  // 示意最多画 60 张，避免臂特别长时生成成百上千个节点
  const sampleCount = Math.min(60, suggestHookQty(hook, HOOK_PREVIEW_SAMPLE_DEPTH_MM))
  const pitchPx = HOOK_PREVIEW_SAMPLE_DEPTH_MM * s
  const cardW = Math.max(2, pitchPx * 0.62)
  const productTop = y1 - SAMPLE_HANG_OFFSET_MM * s
  const productBottom = productTop + SAMPLE_HEIGHT_PX
  const dimY = productBottom + 16
  const height = dimY + 48

  // 价签架：从上臂前端垂下的薄片（侧视只看到厚度），底边高于挂货臂 tagBottomAboveArmMm
  const tagBottom = y1 - hook.tagBottomAboveArmMm * s
  const tagTop = tagBottom - hook.tagHeightMm * s
  const tagX = tipX - 12

  return (
    <svg
      className="planogram-preview-svg"
      width={WIDTH}
      height={height}
      viewBox={`0 0 ${WIDTH} ${height}`}
      role="img"
      aria-label={ariaLabel}
      fontSize="9"
    >
      {/* 洞洞板侧面与孔位 */}
      <rect x={BOARD_X} y={10} width={BOARD_W} height={productBottom - 2} fill="#f0f0f0" stroke="#bfbfbf" />
      {Array.from({ length: Math.floor((productBottom - 20) / 15) }, (_, i) => (
        <circle key={i} cx={ANCHOR_X} cy={20 + i * 15} r="1.6" fill="#bfbfbf" />
      ))}

      {/* 挂钩本体：板面处一根竖线连接上下臂；无价签臂时只剩挂货臂 */}
      <g fill="none" stroke="#1f1f1f" strokeLinecap="round" strokeLinejoin="round">
        {hook.hasPriceTagArm ? (
          <>
            <path d={`M${ANCHOR_X} ${y0} L${tipX} ${y0}`} strokeWidth={wire} />
            <path d={`M${ANCHOR_X} ${y0} L${ANCHOR_X} ${y1}`} strokeWidth={wire} />
          </>
        ) : (
          <path d={`M${ANCHOR_X} ${y1 - 12} L${ANCHOR_X} ${y1}`} strokeWidth={wire} />
        )}
        {/* 挂货臂：臂端向上翘起，防止货品滑落 */}
        <path d={`M${ANCHOR_X} ${y1} L${tipX} ${y1} L${tipX - 10} ${y1 - 10}`} strokeWidth={wire} />
      </g>

      {/* 价签架 */}
      {hook.hasPriceTagArm && hook.tagHeightMm > 0 ? (
        <g>
          {tagTop > y0 ? <path d={`M${tipX} ${y0} L${tagX + 4} ${tagTop}`} stroke="#1f1f1f" strokeWidth="2" strokeLinecap="round" fill="none" /> : null}
          <rect x={tagX} y={tagTop} width="8" height={Math.max(1, tagBottom - tagTop)} rx="1.5" fill="#fff" stroke="#1f1f1f" strokeWidth="1.2" />
          <text x={tagX - 3} y={tagTop + 9} textAnchor="end" fill="rgba(0,0,0,.45)">{labels.tag}</text>
          <line x1={tagX - 6} y1={tagBottom} x2={tagX - 6} y2={y1} stroke="#fa8c16" strokeWidth=".8" />
          <text x={tagX - 9} y={(tagBottom + y1) / 2 + 3} textAnchor="end" fill="#fa8c16" fontSize="8">{labels.tagBottom}</text>
        </g>
      ) : null}

      {/* 样品包装：从板面一侧往臂端排开，臂端预留不挂货 */}
      {Array.from({ length: sampleCount }, (_, i) => (
        <rect
          key={i}
          x={ANCHOR_X - (i + 1) * pitchPx + (pitchPx - cardW) / 2}
          y={productTop}
          width={cardW}
          height={SAMPLE_HEIGHT_PX}
          fill="#fff1b8"
          stroke="#8c8c8c"
          strokeWidth=".8"
          opacity=".9"
        />
      ))}

      {/* 钩高 H：挂孔到挂货臂 */}
      <line x1={305} y1={y0} x2={305} y2={y1} stroke="#1677ff" strokeWidth=".8" />
      <text x={308} y={(y0 + y1) / 2 + 3} fill="#1677ff">{labels.drop}</text>

      {/* 臂长 L 与臂端预留 */}
      <line x1={tipX} y1={dimY} x2={ANCHOR_X} y2={dimY} stroke="#1677ff" strokeWidth=".8" strokeDasharray="3 2" />
      <text x={(tipX + ANCHOR_X) / 2} y={dimY + 11} textAnchor="middle" fill="#1677ff">{labels.armLength}</text>
      <line x1={tipX} y1={dimY + 16} x2={tipX + hook.tipReserveMm * s} y2={dimY + 16} stroke="#fa8c16" strokeWidth="1.2" />
      <text x={tipX + (hook.tipReserveMm * s) / 2} y={dimY + 27} textAnchor="middle" fill="#fa8c16">{labels.reserve}</text>
      <text x={Math.max(4, tipX + hook.tipReserveMm * s + 14)} y={dimY + 27} fontSize="8.5" fill="rgba(0,0,0,.55)">{labels.formula}</text>
    </svg>
  )
}
