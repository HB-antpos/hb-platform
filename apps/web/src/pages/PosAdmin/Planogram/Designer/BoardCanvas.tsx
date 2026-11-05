import { CopyOutlined, DeleteOutlined, ColumnWidthOutlined } from '@ant-design/icons'
import {
  forwardRef,
  memo,
  useCallback,
  useEffect,
  useId,
  useImperativeHandle,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
  type PointerEvent as ReactPointerEvent,
} from 'react'
import type { ProductSpecListItemDto } from '../../../../types/planogram'
import { toProductThumbnailUrl } from '../../../../utils/productImageThumbnail'
import { boardSize } from '../engine/holeGrid'
import {
  bayBoardOriginX,
  computePlacement,
  resolvePerHookQty,
  type LayoutContext,
  type LayoutHook,
  type LayoutSpec,
  type PlacedItem,
  type PlacementGeometry,
} from '../engine/geometry'
import {
  buildDropHint,
  estimateTextWidth,
  itemFootLabel,
  itemFootLabelPrint,
  primaryConflict,
  productColor,
  wrapLabel,
  type DesignerItem,
  type DropHint,
  type Slot,
} from './designerLogic'
import {
  arrangeFadedGuids,
  arrangePreviewBoxes,
  planDragArrange,
  type ArrangeCommitPlan,
  type ArrangeLine,
  type ArrangePlan,
  type ArrangeRejectPlan,
  type CanvasPreviewBox,
  type PreviewTone,
} from './dragArrange'
import type { DisplayConflict } from './useDesignerLayout'

/**
 * 设计器画布（纯 SVG，按毫米等比绘制）：节 / 立柱 / 底板 / 孔阵 / 标尺 / 挂钩两孔点 / 价签 / 商品包装 / 冲突红框与徽标 / 选中手柄与浮动工具条。
 * 几何一律来自 engine/geometry.ts（与后端校验器同一套公式）；渲染方式借鉴设计稿 docs/design/planogram-20261104/plano.js：
 * 1 mm = zoom/100 px，价签顶 = 孔心 + 钩高 − 价签底边高于挂货臂 − 价签高。
 * 画布内拖动已放商品用 pointer capture（同 PricingStrategies/IntervalEditor 的写法）；从左栏拖入由外层 dnd-kit 驱动，经 externalHint 显示幽灵。
 */

// ---------------------------------------------------------------------------
// 尺寸与颜色
// ---------------------------------------------------------------------------

const RULER_LEFT = 34
const RULER_TOP = 40
const PLAIN_LEFT = 10
const PLAIN_TOP = 24
const PAD_RIGHT = 30
const PAD_BOTTOM = 14
/** 指针移动超过这个距离才算拖动（否则只是点选）。 */
const DRAG_THRESHOLD_PX = 4

const COLOR_STROKE = '#8c8c8c'
/** 打印描边（设计稿 print 模式）：比屏幕稍深，黑白打印也清晰。 */
const COLOR_PRINT_STROKE = '#595959'
const COLOR_SELECTED = '#1677ff'
const COLOR_ERROR = '#ff4d4f'
const COLOR_WARN = '#fa8c16'
const COLOR_OK = '#52c41a'

/** 商品图：屏幕上方块高度不足这个像素数时不加载图片（缩小到看不清时退回色块，也省流量）。 */
export const CANVAS_IMAGE_MIN_PX = 28
/** 商品图缩略图档位（COS 实时缩放的边长）：按方块显示尺寸 × 像素密度取最小够用的一档。 */
export const CANVAS_IMAGE_TIERS = [72, 160, 320] as const

export function canvasImageTier(displayPx: number, devicePixelRatio = 1): number {
  const need = displayPx * Math.min(2, Math.max(1, devicePixelRatio))
  return CANVAS_IMAGE_TIERS.find((tier) => tier >= need) ?? CANVAS_IMAGE_TIERS[CANVAS_IMAGE_TIERS.length - 1]
}

/** 预览框配色：拖动虚影 / 新位置（后移、对调）/ 换行。 */
const PREVIEW_TONES: Record<PreviewTone, { stroke: string; fill: string }> = {
  ghost: { stroke: COLOR_OK, fill: '#f6ffed' },
  shift: { stroke: COLOR_SELECTED, fill: '#e6f4ff' },
  wrap: { stroke: COLOR_WARN, fill: '#fff7e6' },
}

const NO_PLAN: ArrangePlan = { kind: 'none' }

export interface CanvasMetrics {
  /** 1 mm 对应的像素数（= 缩放 / 100）。 */
  scale: number
  /** 货架整体左上角（左端立柱外缘、板顶）在 SVG 里的位置。 */
  ox: number
  oy: number
  width: number
  height: number
}

export function canvasMetrics(shelf: { bayWidthMm: number; totalHeightMm: number }, bayCount: number, zoom: number, showRulers: boolean): CanvasMetrics {
  const scale = zoom / 100
  const ox = showRulers ? RULER_LEFT : PLAIN_LEFT
  const oy = showRulers ? RULER_TOP : PLAIN_TOP
  return { scale, ox, oy, width: ox + shelf.bayWidthMm * bayCount * scale + PAD_RIGHT, height: oy + shelf.totalHeightMm * scale + PAD_BOTTOM }
}

/**
 * 打印（PDF）模式的画布尺寸：左侧留标尺 / 行号栏、上方留标尺 + 节标题；1 mm = scale px（scale 由导出页按纸张与比例算出）。
 * 数值与设计稿 plano.js 的 print 渲染一致（ox 36、oy 44、右下各留 10）。visibleBays = 本页画几节。
 */
export const PRINT_RULER_LEFT = 36
export const PRINT_RULER_TOP = 44
export const PRINT_PAD = 10

export function printMetrics(shelf: { bayWidthMm: number; totalHeightMm: number }, visibleBays: number, scale: number): CanvasMetrics {
  return {
    scale,
    ox: PRINT_RULER_LEFT,
    oy: PRINT_RULER_TOP,
    width: PRINT_RULER_LEFT + shelf.bayWidthMm * visibleBays * scale + PRINT_PAD,
    height: PRINT_RULER_TOP + shelf.totalHeightMm * scale + PRINT_PAD,
  }
}

/** 适应窗口：按可视区域算出能放下整个货架的最大缩放（取整百分比，夹在 25–100）。 */
export function fitZoomFor(shelf: { bayWidthMm: number; totalHeightMm: number }, bayCount: number, viewport: { width: number; height: number }, showRulers: boolean): number {
  const ox = showRulers ? RULER_LEFT : PLAIN_LEFT
  const oy = showRulers ? RULER_TOP : PLAIN_TOP
  const byWidth = (viewport.width - ox - PAD_RIGHT) / (shelf.bayWidthMm * bayCount)
  const byHeight = (viewport.height - oy - PAD_BOTTOM) / shelf.totalHeightMm
  const percent = Math.floor(Math.min(byWidth, byHeight) * 100)
  return Math.min(100, Math.max(25, Number.isFinite(percent) ? percent : 25))
}

// ---------------------------------------------------------------------------
// 对外类型
// ---------------------------------------------------------------------------

export interface CanvasHintText {
  ok: boolean
  line1: string
  line2?: string
}

export interface BoardCanvasLabels {
  group: string
  bayTitle: (bay: number, widthMm: number) => string
  base: string
  specChanged: string
  float: { aria: string; copyRight: string; distribute: string; remove: string }
  itemTitle: (args: { name: string; code: string; qty: number; bay: number; col: number; row: number; conflicts: string[]; specChanged: boolean }) => string
}

export interface BoardCanvasHandle {
  /** 屏幕坐标 → 货架整体坐标（mm，左端立柱外缘 / 板顶为 0）；指针在画布可视区域外时返回 null。 */
  clientToShelfPoint: (clientX: number, clientY: number) => { xMm: number; yMm: number } | null
  /** 适应窗口：返回能放下整个货架的缩放百分比。 */
  fitZoom: () => number
  scrollToItem: (guid: string) => void
}

export type CanvasSnapMode = 'hole' | 'free'

/** 打印语言：中文 / 英文 / 双语（双语时商品名两行：中文一行、英文一行）。 */
export type PrintLang = 'zh' | 'en' | 'bi'

/** 打印模式下一件商品的名称：primary 是主显示名；secondary 仅双语时有（英文名，缺失时回退中文名并带 * 号）。 */
export interface PrintName {
  primary: string
  secondary: string | null
}

/** 打印模式的内容开关（导出弹窗里的勾选项）。 */
export interface BoardPrintOptions {
  /** 序号气泡（与执行清单行号一一对应）。 */
  bubbles: boolean
  /** 价签上印价格。 */
  prices: boolean
  /** 孔位列号（每 5 列）/ 行号（每 5 行）。 */
  coords: boolean
}

export interface BoardPrintLabels {
  bayTitle: (bay: number, widthMm: number) => string
  base: string
}

interface BoardCanvasProps {
  ctx: LayoutContext | null
  items: readonly DesignerItem[]
  placed: readonly PlacedItem[]
  specs: Readonly<Record<string, ProductSpecListItemDto>>
  conflictsByGuid: ReadonlyMap<string, DisplayConflict[]>
  selectedGuid: string | null
  zoom: number
  showHoles: boolean
  showRulers: boolean
  showTags: boolean
  snapMode: CanvasSnapMode
  readOnly: boolean
  language: string
  labels: BoardCanvasLabels
  conflictText: (conflict: DisplayConflict) => string
  /** 从左栏拖入时由外层算出的落点提示（画布内拖动用自己的提示）。 */
  externalHint: DropHint | null
  /** 从左栏拖入时外层算出的对调 / 插入预览（与 externalHint 成对）。 */
  externalPlan?: ArrangePlan | null
  /** 外层给的额外虚线预览框（多选「添加到方案」的落点预览）。 */
  previewBoxes?: readonly CanvasPreviewBox[] | null
  /** 商品方块里显示商品图（缺省不显示；设计器默认打开，历史版本预览不显示）。 */
  showImages?: boolean
  describeHint: (hint: DropHint, plan: ArrangePlan) => CanvasHintText
  onSelect: (guid: string | null) => void
  onMoveItem: (guid: string, slot: Slot) => void
  /** 松手时落点不可放（严格吸附模式）：外层提示原因，条目回到原位。 */
  onRejectMove: (hint: DropHint) => void
  /** 画布内拖动触发对调 / 插入：外层一次提交整份新明细（一个撤销快照）。 */
  onArrange?: (plan: ArrangeCommitPlan, draggedGuid: string) => void
  /** 对调 / 插入放不下或会冲突：外层提示原因，什么都不改。 */
  onRejectArrange?: (plan: ArrangeRejectPlan) => void
  onCopyRight: () => void
  onDistribute: () => void
  onRemove: () => void
  onZoomWheel: (direction: 1 | -1) => void
  /** Tab / Shift+Tab 在画布里依次选中条目（到头后放行默认的焦点移动）。 */
  onTabSelect: (direction: 1 | -1) => boolean
  emptyText: string
}

// ---------------------------------------------------------------------------
// 条目渲染
// ---------------------------------------------------------------------------

/** 价签、孔点、包装的几何（px 换算前的 mm 值）；缺参数 / 缺挂钩时用占位尺寸，让它在画布上仍然看得见、点得到。 */
const FALLBACK_SPEC: LayoutSpec = { widthMm: 60, heightMm: 100, depthMm: 10, hangHoleOffsetMm: 10, unitWeightG: null, hookQtyOverrides: {}, retailPrice: 0 }
const FALLBACK_HOOK: LayoutHook = {
  hasPriceTagArm: false,
  armLengthMm: 0,
  dropMm: 50,
  tipReserveMm: 0,
  tagWidthMm: 0,
  tagHeightMm: 0,
  tagBottomAboveArmMm: 0,
  holeSpanX: 2,
  holeSpanY: 1,
}

export interface ItemView {
  item: DesignerItem
  geometry: PlacementGeometry
  hook: LayoutHook
  specWidthMm: number
  specHeightMm: number
  /** 第 N 节板面左缘在货架整体坐标里的 x（mm）。 */
  boardOriginMm: number
  holePitchXMm: number
  holeSpanX: number
  name: string
  /** 双语打印的第二行（英文名）；其余情况为 null。 */
  secondaryName: string | null
  code: string
  price: number
  qty: number
  missing: boolean
  conflicts: DisplayConflict[]
  hasError: boolean
  hasWarn: boolean
  /** 红色冲突标签文字（选中时显示在包装上方）。 */
  primaryText: string
  conflictTexts: string[]
  fill: string
}

function displayName(info: ProductSpecListItemDto | undefined, code: string, language: string): string {
  if (!info) return code
  if (language.toLowerCase().startsWith('en') && info.englishName) return info.englishName
  return info.productName || code
}

export interface BuildItemViewsArgs {
  ctx: LayoutContext | null
  items: readonly DesignerItem[]
  specs: Readonly<Record<string, ProductSpecListItemDto>>
  conflictsByGuid: ReadonlyMap<string, DisplayConflict[]>
  conflictText: (conflict: DisplayConflict) => string
  /** 商品显示名：交互画布按界面语言取一个；打印按导出语言取（双语时带第二行）。 */
  nameOf: (info: ProductSpecListItemDto | undefined, code: string) => PrintName
}

/**
 * 条目视图模型：几何（computePlacement，与后端校验器同一套公式）+ 显示名 / 冲突 / 填充色。
 * 设计器画布与 PDF 打印共用这一个函数，所以两边的占位几何、冲突标记完全一致，不存在第二份几何。
 */
export function buildItemViews({ ctx, items, specs, conflictsByGuid, conflictText, nameOf }: BuildItemViewsArgs): ItemView[] {
  if (!ctx) return []
  const result: ItemView[] = []
  for (const item of items) {
    if (item.bayIndex < 1 || item.bayIndex > ctx.bayCount) continue
    const info = specs[item.productCode]
    const spec = ctx.specs[item.productCode]
    const hook = ctx.hooks[item.hookModelGuid]
    const missing = !spec || !hook
    const usedSpec = spec ?? FALLBACK_SPEC
    const usedHook = hook ?? FALLBACK_HOOK
    const geometry = computePlacement(ctx.shelf, usedHook, usedSpec, item.holeCol, item.holeRow)
    const conflicts = conflictsByGuid.get(item.guid) ?? []
    const errors = conflicts.filter((entry) => entry.severity === 'error')
    const primary = primaryConflict(conflicts)
    const name = nameOf(info, item.productCode)
    result.push({
      item,
      geometry,
      hook: usedHook,
      specWidthMm: usedSpec.widthMm,
      specHeightMm: usedSpec.heightMm,
      boardOriginMm: bayBoardOriginX(ctx.shelf, item.bayIndex),
      holePitchXMm: ctx.shelf.holePitchXMm,
      holeSpanX: usedHook.holeSpanX,
      name: name.primary,
      secondaryName: name.secondary,
      code: info?.itemNumber || item.productCode,
      price: info?.retailPrice ?? 0,
      qty: spec && hook ? resolvePerHookQty(item, hook, spec) : 0,
      missing,
      conflicts,
      hasError: errors.length > 0,
      hasWarn: errors.length === 0 && conflicts.length > 0,
      primaryText: primary ? conflictText(primary) : '',
      conflictTexts: conflicts.map((entry) => conflictText(entry)),
      fill: missing ? '#f5f5f5' : productColor(item.productCode),
    })
  }
  return result
}

/** 打印模式下一件商品的附加信息（序号气泡、价格开关、实拍图）；不传 = 交互模式。 */
interface ItemPrintOptions {
  /** 清单序号；null = 不画气泡。 */
  number: number | null
  showPrice: boolean
  /** 商品实拍图（data URL，已经加载完成）；null = 用白底。 */
  image: string | null
  lang: PrintLang
}

interface ItemShapeProps {
  view: ItemView
  selected: boolean
  dragging: boolean
  m: CanvasMetrics
  showTags: boolean
  labels: Pick<BoardCanvasLabels, 'itemTitle' | 'specChanged'>
  onPointerDown: (guid: string, event: ReactPointerEvent<SVGGElement>) => void
  /** 打印（PDF）模式：白底、描边略深、无选中手柄 / 交互 / 悬浮提示，价格与序号气泡按开关，冲突仍标红。 */
  print?: ItemPrintOptions
  /** 交互画布的商品图（缩略图地址）；null = 画色块 + 名称。 */
  image?: string | null
  /** 商品图加载失败：外层记下这个地址，之后退回色块。 */
  onImageError?: (href: string) => void
}

const ItemShape = memo(function ItemShape({ view, selected, dragging, m, showTags, labels, onPointerDown, print, image, onImageError }: ItemShapeProps) {
  const S = m.scale
  const { geometry: g, hook } = view
  const boardLeftPx = m.ox + view.boardOriginMm * S
  const cx = boardLeftPx + g.centerX * S
  const w = view.specWidthMm * S
  const h = view.specHeightMm * S
  const x = cx - w / 2
  const y = m.oy + g.productTop * S
  // 打印字号取设计稿 plano.js 的值（中文 8.5 / 英文 7.5，纸上 1 px ≈ 0.34 mm，与缩放无关）；交互画布随缩放变化
  const fs = print ? (print.lang === 'zh' ? 8.5 : 7.5) : Math.min(13, Math.max(6.5, 18.75 * S))

  const stroke = selected ? COLOR_SELECTED : view.hasError ? COLOR_ERROR : view.hasWarn ? COLOR_WARN : print ? COLOR_PRINT_STROKE : COLOR_STROKE
  const strokeWidth = selected || view.hasError ? 2 : view.hasWarn ? 1.5 : print ? 0.9 : 1

  // 交互画布显示商品图时名称收起（悬浮提示 / 右栏里有名称），只留底部「货号·×每钩」；图片加载失败由外层退回色块 + 名称
  const interactiveImage = !print && Boolean(image) && !view.missing
  // 名称：按宽度折行，最多 3 行（高度不够就少画）；双语打印时先中文（最多 2 行）再英文（最多 2 行，小一号）
  const lineHeight = fs + 2
  const maxLines = Math.max(1, Math.min(3, Math.floor((h - fs * 2.6) / (fs + 2))))
  let lines: string[] = []
  let secondaryLines: string[] = []
  if (!view.missing && !interactiveImage) {
    if (print && view.secondaryName) {
      // 双语：先中文（最多一半行数）再英文（小一号）；框太矮放不下两行时只印主名称，不硬挤（挤了会压住货号标签）
      const budget = Math.min(4, Math.floor((h - fs * 2.6) / (fs + 2)))
      if (budget >= 2) {
        const primaryBudget = Math.max(1, Math.ceil(budget / 2))
        lines = wrapLabel(view.name, w - 4, fs, primaryBudget)
        secondaryLines = wrapLabel(view.secondaryName, w - 4, fs * 0.85, Math.max(1, budget - lines.length))
      } else {
        lines = wrapLabel(view.name, w - 4, fs, Math.max(1, budget))
      }
    } else {
      lines = wrapLabel(view.name, w - 4, fs, maxLines)
    }
  }
  const allLineCount = lines.length + secondaryLines.length
  const firstLineY = y + h / 2 + fs * 0.15 - ((allLineCount - 1) * lineHeight) / 2

  const holeRadius = Math.min(3.2, Math.max(1.6, 2.2 * (S / 0.4)))
  const hangW = Math.max(4, 15 * S)
  const hangH = Math.max(1.8, 5.5 * S)

  const tagVisible = (print ? true : showTags) && hook.hasPriceTagArm && g.tagTop !== null && !view.missing
  const tagW = hook.tagWidthMm * S
  const tagH = hook.tagHeightMm * S
  const tagY = m.oy + (g.tagTop ?? 0) * S
  const tagFont = print ? Math.min(11, Math.max(6.5, tagH * 0.62)) : Math.min(12, Math.max(5.5, tagH * 0.62))

  const badgeX = x + w
  const badgeY = y

  // 悬浮 / 读屏标题只有交互画布用（打印是静态图形）
  const title = print
    ? ''
    : labels.itemTitle({
        name: view.name,
        code: view.code,
        qty: view.qty,
        bay: view.item.bayIndex,
        col: view.item.holeCol,
        row: view.item.holeRow,
        conflicts: view.conflictTexts,
        specChanged: view.item.specChanged,
      })
  const footText = print ? itemFootLabelPrint(view.code, view.qty) : itemFootLabel(view.code, view.qty)
  // 有实拍图时名称 / 货号底下垫一层半透明白条，保证文字压在图上也看得清
  const hasImage = Boolean(print?.image)
  const labelBackdropH = allLineCount * lineHeight + 4

  const body = (
    <>
      {print ? null : <title>{title}</title>}
      {/* 挂钩两孔点（双钩两个，单钩一个） */}
      {Array.from({ length: view.holeSpanX }, (_, k) => (
        <circle key={k} cx={boardLeftPx + (g.holeX + k * view.holePitchXMm) * S} cy={m.oy + g.holeY * S} r={holeRadius} fill="#595959" />
      ))}
      {/* 商品包装 */}
      <rect x={x} y={y} width={w} height={h} rx={1.5} fill={print ? (view.hasError ? '#fff1f0' : '#fff') : interactiveImage ? '#fff' : view.fill} stroke={stroke} strokeWidth={strokeWidth} strokeDasharray={view.missing ? '4 3' : undefined} />
      {print?.image ? <image href={print.image} x={x + 1} y={y + 1} width={Math.max(0, w - 2)} height={Math.max(0, h - 2)} preserveAspectRatio="xMidYMid meet" /> : null}
      {interactiveImage && image ? (
        <image
          href={image}
          x={x + 1}
          y={y + 1}
          width={Math.max(0, w - 2)}
          height={Math.max(0, h - 2)}
          preserveAspectRatio="xMidYMid meet"
          pointerEvents="none"
          // React 18 会给 SVG <image> 监听 error 事件（react-dom 对 img / image / link 都挂了非冒泡监听），此规则的白名单没列 image
          // eslint-disable-next-line react/no-unknown-property
          onError={() => onImageError?.(image)}
        />
      ) : null}
      {view.hasError ? <rect x={x - 1} y={y - 1} width={w + 2} height={h + 2} fill={COLOR_ERROR} opacity={0.12} pointerEvents="none" /> : null}
      <rect x={cx - hangW / 2} y={y + 1.5} width={hangW} height={hangH} rx={1} fill="#fff" stroke="#999" strokeWidth={0.6} pointerEvents="none" />
      {hasImage && allLineCount > 0 ? (
        <rect x={x + 1} y={firstLineY - fs - 1} width={Math.max(0, w - 2)} height={labelBackdropH} fill="#fff" opacity={0.78} />
      ) : null}
      {lines.length > 0 ? (
        <text fontSize={fs} textAnchor="middle" fill="rgba(0,0,0,.8)" pointerEvents="none">
          {lines.map((line, index) => (
            <tspan key={index} x={cx} y={firstLineY + index * lineHeight}>{line}</tspan>
          ))}
        </text>
      ) : null}
      {secondaryLines.length > 0 ? (
        <text fontSize={fs * 0.85} textAnchor="middle" fill="rgba(0,0,0,.65)" pointerEvents="none">
          {secondaryLines.map((line, index) => (
            <tspan key={index} x={cx} y={firstLineY + (lines.length + index) * lineHeight}>{line}</tspan>
          ))}
        </text>
      ) : null}
      {/* 商品图上的货号标签垫一条半透明白底，压在图上也看得清 */}
      {interactiveImage && w >= 26 ? (
        <rect x={x + 1} y={y + h - fs * 0.85 - 6} width={Math.max(0, w - 2)} height={fs * 0.85 + 5} fill="#fff" opacity={0.82} pointerEvents="none" />
      ) : null}
      {view.missing ? (
        <text x={cx} y={y + h / 2 + 3} fontSize={Math.max(9, fs)} textAnchor="middle" fill={COLOR_ERROR} pointerEvents="none">?</text>
      ) : w >= 26 ? (
        // 打印时序号气泡贴在左下角：窄框里居中的货号标签会被气泡盖住开头，改成靠右对齐并抬到气泡上方
        <text
          x={print && print.number !== null && w < 80 ? x + w - 2 : cx}
          y={print && print.number !== null && w < 80 ? y + h - 9 : y + h - 3}
          fontSize={print ? 7 : fs * 0.85}
          textAnchor={print && print.number !== null && w < 80 ? 'end' : 'middle'}
          fill="rgba(0,0,0,.55)"
          fontFamily="Menlo, Consolas, monospace"
          pointerEvents="none"
        >
          {footText}
        </text>
      ) : null}
      {/* 价签：挂钩的固定部件，压在商品顶边上（价签顶 = 孔心 + 钩高 − 价签底边高于挂货臂 − 价签高） */}
      {tagVisible ? (
        <g pointerEvents="none">
          <rect x={cx - tagW / 2} y={tagY} width={tagW} height={tagH} rx={1} fill="#fff" stroke="#8c8c8c" strokeWidth={0.7} />
          {tagW >= 18 && (!print || print.showPrice) ? (
            <text x={cx} y={tagY + tagH - Math.max(1.5, tagH * 0.2)} fontSize={tagFont} textAnchor="middle" fontWeight={700} fill="#1f1f1f">
              {`$${view.price.toFixed(2)}`}
            </text>
          ) : null}
        </g>
      ) : null}
      {/* 序号气泡（仅打印）：与执行清单行号一一对应，贴在商品包装左下角 */}
      {print && print.number !== null ? <NumberBubble cx={x + 1} cy={y + h - 1} value={print.number} /> : null}
      {/* 冲突 / 参数已变徽标 */}
      {view.hasError || view.hasWarn ? (
        <g pointerEvents="none">
          <circle cx={badgeX} cy={badgeY} r={7} fill={view.hasError ? COLOR_ERROR : COLOR_WARN} stroke="#fff" strokeWidth={1} />
          <text x={badgeX} y={badgeY + 3.6} fontSize={10} fontWeight={700} textAnchor="middle" fill="#fff">!</text>
        </g>
      ) : null}
      {!print && view.item.specChanged ? (
        <g pointerEvents="none">
          <circle cx={x} cy={badgeY} r={7} fill="#faad14" stroke="#fff" strokeWidth={1} />
          <text x={x} y={badgeY + 3.6} fontSize={10} fontWeight={700} textAnchor="middle" fill="#fff">↻</text>
          <title>{labels.specChanged}</title>
        </g>
      ) : null}
    </>
  )

  // 打印模式没有交互：不带 role / 读屏标签 / 指针事件，只是一组静态图形
  if (print) return <g opacity={dragging ? 0.4 : 1}>{body}</g>
  return (
    <g
      data-item-guid={view.item.guid}
      role="button"
      aria-label={title}
      aria-pressed={selected}
      className="planogram-designer-item"
      opacity={dragging ? 0.4 : 1}
      onPointerDown={(event) => onPointerDown(view.item.guid, event)}
    >
      {body}
    </g>
  )
})

/** 序号气泡：黑底白字的圆（设计稿 r=6.5、字号 7）；三位数时放大一点、字号缩小。 */
function NumberBubble({ cx, cy, value }: { cx: number; cy: number; value: number }) {
  const digits = String(value).length
  const r = digits >= 3 ? 8 : 6.5
  const font = digits >= 3 ? 6 : 7
  return (
    <g pointerEvents="none">
      <circle cx={cx} cy={cy} r={r} fill="#1f1f1f" />
      <text x={cx} y={cy + font * 0.35} fontSize={font} textAnchor="middle" fill="#fff" fontWeight={600}>{value}</text>
    </g>
  )
}

/**
 * 选中项的手柄与冲突说明（画在所有商品之上的独立顶层，不参与点击）。
 * 选中状态不改变商品自身的 DOM 位置：如果为了「选中的画在最上面」去调整商品的绘制顺序，React 会移动 DOM 节点，
 * 浏览器会因此丢掉刚设置的 pointer capture，按下就拖会失效。
 */
function SelectionOverlay({ view, m }: { view: ItemView; m: CanvasMetrics }) {
  const S = m.scale
  const g = view.geometry
  const cx = m.ox + (view.boardOriginMm + g.centerX) * S
  const w = view.specWidthMm * S
  const h = view.specHeightMm * S
  const x = cx - w / 2
  const y = m.oy + g.productTop * S
  const pillText = view.primaryText
  const pillW = estimateTextWidth(pillText, 8.5) + 14
  return (
    <g pointerEvents="none" aria-hidden="true">
      <rect x={x} y={y} width={w} height={h} rx={1.5} fill="none" stroke={COLOR_SELECTED} strokeWidth={2} />
      {[[x, y], [x + w, y], [x, y + h], [x + w, y + h]].map(([hx, hy], index) => (
        <rect key={index} x={hx - 2.5} y={hy - 2.5} width={5} height={5} fill="#fff" stroke={COLOR_SELECTED} />
      ))}
      {pillText ? (
        <g transform={`translate(${Math.max(2, x - 2)}, ${Math.max(2, Math.min(y, m.oy + g.top * S) - 18)})`}>
          <rect width={pillW} height={14} rx={3} fill={view.hasError ? COLOR_ERROR : COLOR_WARN} />
          <text x={pillW / 2} y={10} fontSize={8.5} textAnchor="middle" fill="#fff">{pillText}</text>
        </g>
      ) : null}
    </g>
  )
}

// ---------------------------------------------------------------------------
// 货架本体（节 / 立柱 / 底板 / 孔阵 / 标尺）
// ---------------------------------------------------------------------------

interface ShelfLayerProps {
  shelf: LayoutContext['shelf'] & { baseDepthMm?: number }
  bayCount: number
  /** 只画 bayFrom..bayTo 这几节（PDF 一页画几节）；m.ox 是第 bayFrom 节左缘的位置。缺省 = 全部（设计器画布）。 */
  bayFrom?: number
  bayTo?: number
  m: CanvasMetrics
  showHoles: boolean
  showRulers: boolean
  labels: Pick<BoardCanvasLabels, 'bayTitle' | 'base'>
  /** 打印模式：孔位列号 / 行号（行号按货架型号的行号起算方向转成显示编号）。 */
  printCoords?: { rowLabel: (row: number) => number }
}

const ShelfLayer = memo(function ShelfLayer({ shelf, bayCount, bayFrom = 1, bayTo, m, showHoles, showRulers, labels, printCoords }: ShelfLayerProps) {
  const rawId = useId()
  const patternId = `planogram-designer-holes-${rawId.replace(/[^a-zA-Z0-9_-]/g, '')}`
  const S = m.scale
  const board = boardSize(shelf)
  const bayW = shelf.bayWidthMm
  const lastBay = Math.min(bayCount, bayTo ?? bayCount)
  const visibleBays = Math.max(0, lastBay - bayFrom + 1)
  // 本页第一节左缘在货架整体坐标里的 x（mm）：标尺刻度显示整体坐标，翻到第 2 页时仍从该页起点的真实位置开始
  const originMm = (bayFrom - 1) * bayW
  const totalW = bayW * visibleBays
  const pitchXPx = shelf.holePitchXMm * S
  const pitchYPx = shelf.holePitchYMm * S
  // 孔太密（缩放后孔距小于 4 px）不画孔阵，避免整块变成灰糊
  const drawHoles = showHoles && pitchXPx >= 4 && pitchYPx >= 4 && shelf.holeCols > 0 && shelf.holeRows > 0
  const holeR = Math.min(1.8, Math.max(0.9, pitchXPx * (printCoords ? 0.09 : 0.12)))
  const baseY = m.oy + board.heightMm * S
  const uprightPx = shelf.uprightWidthMm * S

  const hTicks: number[] = []
  for (let mm = Math.ceil(originMm / 100) * 100; mm <= originMm + totalW; mm += 100) hTicks.push(mm)
  const vTicks: number[] = []
  for (let mm = 0; mm <= shelf.totalHeightMm; mm += 100) vTicks.push(mm)
  const leftOfBay = (bay: number) => m.ox + (bay - bayFrom) * bayW * S
  const rulerFill = printCoords ? '#fff' : '#fafafa'

  return (
    <g>
      <defs>
        {drawHoles ? (
          <pattern id={patternId} width={pitchXPx} height={pitchYPx} patternUnits="userSpaceOnUse">
            <circle cx={pitchXPx / 2} cy={pitchYPx / 2} r={holeR} fill={printCoords ? '#d0d0d0' : '#cfcfcf'} />
          </pattern>
        ) : null}
      </defs>
      {showRulers ? (
        <g aria-hidden="true">
          <rect x={0} y={0} width={m.width} height={m.oy - 14} fill={rulerFill} />
          <rect x={0} y={0} width={m.ox - 8} height={m.height} fill={rulerFill} />
          {hTicks.map((mm) => (
            <g key={`h${mm}`}>
              <line x1={m.ox + (mm - originMm) * S} y1={m.oy - 22} x2={m.ox + (mm - originMm) * S} y2={m.oy - 14} stroke="#bfbfbf" />
              {mm % 300 === 0 ? (
                // 右端的刻度标签放不下（超出画布宽度）时改成右对齐，避免被裁成「18」
                m.ox + (mm - originMm) * S + 2 + String(mm).length * 5.4 > m.width
                  ? <text x={m.ox + (mm - originMm) * S - 2} y={m.oy - 24} fontSize={9} textAnchor="end" fill="rgba(0,0,0,.45)">{mm}</text>
                  : <text x={m.ox + (mm - originMm) * S + 2} y={m.oy - 24} fontSize={9} fill="rgba(0,0,0,.45)">{mm}</text>
              ) : null}
            </g>
          ))}
          {vTicks.map((mm) => (
            <g key={`v${mm}`}>
              <line x1={m.ox - 16} y1={m.oy + mm * S} x2={m.ox - 8} y2={m.oy + mm * S} stroke="#bfbfbf" />
              {mm % 300 === 0 ? <text x={2} y={m.oy + mm * S + 3} fontSize={9} fill="rgba(0,0,0,.45)">{mm}</text> : null}
            </g>
          ))}
        </g>
      ) : null}
      {Array.from({ length: visibleBays }, (_, i) => {
        const bay = bayFrom + i
        return (
          <text key={`t${bay}`} x={leftOfBay(bay) + (bayW * S) / 2} y={m.oy - 4} fontSize={printCoords ? 12 : 11} fontWeight={600} textAnchor="middle" fill="rgba(0,0,0,.75)">
            {labels.bayTitle(bay, bayW)}
          </text>
        )
      })}
      <rect x={m.ox} y={m.oy} width={totalW * S} height={shelf.totalHeightMm * S} fill="#f7f7f7" stroke="#bfbfbf" />
      {Array.from({ length: visibleBays }, (_, i) => {
        const bay = bayFrom + i
        const left = leftOfBay(bay) + (shelf.uprightWidthMm / 2) * S
        return (
          <g key={`b${bay}`}>
            <rect x={left} y={m.oy} width={board.widthMm * S} height={board.heightMm * S} fill="#fff" stroke="#d9d9d9" />
            {drawHoles ? (
              <rect
                x={left + (shelf.firstHoleLeftMm - shelf.holePitchXMm / 2) * S}
                y={m.oy + (shelf.firstHoleTopMm - shelf.holePitchYMm / 2) * S}
                width={shelf.holeCols * pitchXPx}
                height={shelf.holeRows * pitchYPx}
                fill={`url(#${patternId})`}
              />
            ) : null}
          </g>
        )
      })}
      {/* 立柱：边上两根在货架内侧，节与节之间的跨在交界线上 */}
      {Array.from({ length: visibleBays + 1 }, (_, i) => {
        const boundary = bayFrom - 1 + i
        const x = m.ox + i * bayW * S - (boundary === bayCount ? uprightPx : boundary > 0 ? uprightPx / 2 : 0)
        return <rect key={`u${boundary}`} x={x} y={m.oy - 2} width={uprightPx} height={shelf.totalHeightMm * S + 2} fill="#e8e8e8" stroke="#bfbfbf" />
      })}
      <rect x={m.ox} y={baseY} width={totalW * S} height={shelf.baseTopHeightMm * S} fill="#ededed" stroke="#bfbfbf" />
      {/* 打印：孔位列号（每 5 列：1、6、11…）画在底板上沿（要画在底板之后，否则被底板的灰底盖住）；行号画在左侧立柱上（见下） */}
      {printCoords ? (
        <g aria-hidden="true">
          {Array.from({ length: visibleBays }, (_, i) => {
            const left = leftOfBay(bayFrom + i) + (shelf.uprightWidthMm / 2) * S
            return Array.from({ length: Math.ceil(shelf.holeCols / 5) }, (_, k) => {
              const col = k * 5 + 1
              return (
                <text key={`c${bayFrom + i}-${col}`} x={left + (shelf.firstHoleLeftMm + (col - 1) * shelf.holePitchXMm) * S} y={baseY + 10} fontSize={7} textAnchor="middle" fill="#1677ff">{col}</text>
              )
            })
          })}
        </g>
      ) : null}
      {printCoords ? (
        <g aria-hidden="true">
          {Array.from({ length: shelf.holeRows }, (_, k) => {
            const row = k + 1
            const label = printCoords.rowLabel(row)
            if ((label - 1) % 5 !== 0) return null
            const labelX = m.ox + (bayFrom > 1 ? uprightPx / 4 : uprightPx / 2)
            return (
              <text key={`r${row}`} x={labelX} y={m.oy + (shelf.firstHoleTopMm + (row - 1) * shelf.holePitchYMm) * S + 2.5} fontSize={7} textAnchor="middle" fill="#1677ff" fontWeight={600}>{label}</text>
            )
          })}
        </g>
      ) : null}
      <text x={m.ox + (totalW * S) / 2} y={baseY + (shelf.baseTopHeightMm * S) / 2 + 4} fontSize={10} textAnchor="middle" fill="rgba(0,0,0,.45)">
        {labels.base}
      </text>
    </g>
  )
})

// ---------------------------------------------------------------------------
// 拖拽幽灵（落点提示）
// ---------------------------------------------------------------------------

interface HintLayerProps {
  hint: DropHint
  text: CanvasHintText
  ctx: LayoutContext
  m: CanvasMetrics
  showRulers: boolean
  fill: string
  /** 画布宽度，气泡放不下时翻到幽灵左侧。 */
  canvasWidth: number
}

function HintLayer({ hint, text, ctx, m, showRulers, fill, canvasWidth }: HintLayerProps) {
  const g = hint.geometry
  if (!g) return null
  const S = m.scale
  const hookModel = ctx.hooks[hint.hookModelGuid]
  const spec = ctx.specs[hint.productCode]
  if (!hookModel || !spec) return null
  // 颜色跟着提示文字的结论走：对调 / 插入时虚影仍是绿色（松手有效），放不下时是红色
  const stroke = text.ok ? COLOR_OK : COLOR_ERROR
  const boardLeftPx = m.ox + bayBoardOriginX(ctx.shelf, hint.bayIndex) * S
  const cx = boardLeftPx + g.centerX * S
  const w = spec.widthMm * S
  const h = spec.heightMm * S
  const x = cx - w / 2
  const y = m.oy + g.productTop * S
  const holeY = m.oy + g.holeY * S
  const pitchPx = ctx.shelf.holePitchXMm * S

  const fs = 9
  const w1 = estimateTextWidth(text.line1, fs)
  const w2 = text.line2 ? estimateTextWidth(text.line2, fs - 0.5) : 0
  const bubbleW = Math.max(w1, w2) + 18
  const bubbleH = text.line2 ? 32 : 20
  const bubbleRight = x + w + 10 + bubbleW <= canvasWidth
  const bubbleX = bubbleRight ? x + w + 10 : Math.max(2, x - 10 - bubbleW)
  const bubbleY = Math.max(2, y + 8)

  return (
    <g pointerEvents="none" aria-hidden="true">
      {Array.from({ length: hookModel.holeSpanX }, (_, k) => (
        <circle key={k} cx={boardLeftPx + (g.holeX + k * ctx.shelf.holePitchXMm) * S} cy={holeY} r={4} fill="none" stroke={stroke} strokeWidth={1.5} />
      ))}
      <rect x={x} y={y} width={w} height={h} rx={1.5} fill={text.ok ? '#f6ffed' : '#fff1f0'} stroke={stroke} strokeWidth={1.5} strokeDasharray="4 3" opacity={0.92} />
      <rect x={x + 2} y={y + 2} width={Math.max(0, w - 4)} height={Math.max(0, h - 4)} rx={1} fill={fill} opacity={0.35} />
      {hookModel.hasPriceTagArm && g.tagTop !== null ? (
        <rect x={cx - (hookModel.tagWidthMm * S) / 2} y={m.oy + g.tagTop * S} width={hookModel.tagWidthMm * S} height={hookModel.tagHeightMm * S} fill="#fff" stroke={stroke} strokeWidth={0.8} strokeDasharray="2 2" />
      ) : null}
      {showRulers ? (
        <g opacity={0.7}>
          <rect x={boardLeftPx + (g.holeX - ctx.shelf.holePitchXMm / 2) * S} y={m.oy - 22} width={pitchPx * hookModel.holeSpanX} height={8} rx={2} fill={stroke} />
          <rect x={m.ox - 16} y={holeY - 4} width={8} height={8} rx={2} fill={stroke} />
        </g>
      ) : null}
      <g transform={`translate(${bubbleX}, ${bubbleY})`}>
        <rect width={bubbleW} height={bubbleH} rx={5} fill="#1f1f1f" opacity={0.92} />
        <text x={9} y={13} fontSize={fs} fontWeight={600} fill={text.ok ? '#73d13d' : '#ff7875'}>{text.line1}</text>
        {text.line2 ? <text x={9} y={26} fontSize={fs - 0.5} fill="rgba(255,255,255,.78)">{text.line2}</text> : null}
      </g>
    </g>
  )
}

/** 虚线预览框：对调 / 插入时被挪动商品的新位置（蓝）、换行的（橙）、多选添加的落点（绿）。 */
function PreviewBoxesLayer({ boxes, ctx, m }: { boxes: readonly CanvasPreviewBox[]; ctx: LayoutContext; m: CanvasMetrics }) {
  const S = m.scale
  return (
    <g pointerEvents="none" aria-hidden="true">
      {boxes.map((box) => {
        const tone = PREVIEW_TONES[box.tone]
        return (
          <rect
            key={box.key}
            x={m.ox + (bayBoardOriginX(ctx.shelf, box.bayIndex) + box.leftMm) * S}
            y={m.oy + box.topMm * S}
            width={box.widthMm * S}
            height={box.heightMm * S}
            rx={1.5}
            fill={tone.fill}
            fillOpacity={0.6}
            stroke={tone.stroke}
            strokeWidth={1.6}
            strokeDasharray="5 3"
          />
        )
      })}
    </g>
  )
}

/** 插入点竖线：插入有效时蓝色，放不下时红色。 */
function InsertLineLayer({ line, ctx, m, ok }: { line: ArrangeLine; ctx: LayoutContext; m: CanvasMetrics; ok: boolean }) {
  const S = m.scale
  const x = m.ox + (bayBoardOriginX(ctx.shelf, line.bayIndex) + line.xMm) * S
  const top = m.oy + line.topMm * S - 6
  const height = Math.max(12, (line.bottomMm - line.topMm) * S + 12)
  return <rect pointerEvents="none" aria-hidden="true" x={x - 1.5} y={top} width={3} height={height} rx={1.5} fill={ok ? COLOR_SELECTED : COLOR_ERROR} />
}

// ---------------------------------------------------------------------------
// 主组件
// ---------------------------------------------------------------------------

interface DragState {
  guid: string
  pointerId: number
  startClientX: number
  startClientY: number
  /** 抓取点相对锚点孔心的偏移（mm）：拖动时用「指针 − 偏移」去吸附，所以抓在包装中间也不会一松手就跳位。 */
  grabDxMm: number
  grabDyMm: number
  moved: boolean
  /** 上一次算预览时的落点（节 / 列 / 行）：落点没变就不重算对调 / 插入。 */
  lastKey: string | null
}

const BoardCanvas = forwardRef<BoardCanvasHandle, BoardCanvasProps>(function BoardCanvas(props, ref) {
  const {
    ctx, items, placed, specs, conflictsByGuid, selectedGuid, zoom, showHoles, showRulers, showTags, readOnly, language, labels, conflictText,
    externalHint, externalPlan, previewBoxes, showImages = false, describeHint, onSelect, onCopyRight, onDistribute, onRemove, emptyText,
  } = props
  const latest = useRef(props)
  latest.current = props

  const scrollRef = useRef<HTMLDivElement | null>(null)
  const svgRef = useRef<SVGSVGElement | null>(null)
  const dragRef = useRef<DragState | null>(null)
  /** 画布内拖动的落点与对调 / 插入预览（两者成对更新）。 */
  const [dragPreview, setDragPreview] = useState<{ hint: DropHint; plan: ArrangePlan } | null>(null)
  const [draggingGuid, setDraggingGuid] = useState<string | null>(null)
  // 加载失败的商品图地址：之后这些方块退回色块 + 名称（换缩放档位会换地址，再试一次）
  const [failedImages, setFailedImages] = useState<ReadonlySet<string>>(() => new Set())
  const handleImageError = useCallback((href: string) => {
    setFailedImages((current) => (current.has(href) ? current : new Set(current).add(href)))
  }, [])

  const metrics = useMemo(() => (ctx ? canvasMetrics(ctx.shelf, ctx.bayCount, zoom, showRulers) : null), [ctx, showRulers, zoom])
  const latestMetrics = useRef(metrics)
  latestMetrics.current = metrics

  // ---- 条目视图模型（不含选中 / 拖动状态，选中变化时只有两个 ItemShape 重绘） ----
  const views = useMemo<ItemView[]>(
    () => buildItemViews({ ctx, items, specs, conflictsByGuid, conflictText, nameOf: (info, code) => ({ primary: displayName(info, code, language), secondary: null }) }),
    [conflictText, conflictsByGuid, ctx, items, language, specs],
  )

  // 绘制顺序：普通 → 有冲突（后画的在上面，红框不被盖住）。只随冲突变化，选中不动 DOM 顺序（见 SelectionOverlay）
  const ordered = useMemo(() => {
    const rank = (view: ItemView) => (view.hasError || view.hasWarn ? 1 : 0)
    return [...views].sort((a, b) => rank(a) - rank(b))
  }, [views])
  const selectedView = useMemo(() => (selectedGuid ? views.find((view) => view.item.guid === selectedGuid) : undefined), [selectedGuid, views])

  // ---- 对外句柄 ----
  const clientToShelfPoint = useCallback((clientX: number, clientY: number) => {
    const scroll = scrollRef.current
    const svg = svgRef.current
    const m = latestMetrics.current
    if (!scroll || !svg || !m) return null
    const viewport = scroll.getBoundingClientRect()
    if (clientX < viewport.left || clientX > viewport.right || clientY < viewport.top || clientY > viewport.bottom) return null
    const rect = svg.getBoundingClientRect()
    return { xMm: (clientX - rect.left - m.ox) / m.scale, yMm: (clientY - rect.top - m.oy) / m.scale }
  }, [])

  useImperativeHandle(
    ref,
    () => ({
      clientToShelfPoint,
      fitZoom: () => {
        const scroll = scrollRef.current
        const current = latest.current
        if (!scroll || !current.ctx) return current.zoom
        return fitZoomFor(current.ctx.shelf, current.ctx.bayCount, { width: scroll.clientWidth, height: scroll.clientHeight }, current.showRulers)
      },
      scrollToItem: (guid: string) => {
        const scroll = scrollRef.current
        const current = latest.current
        const m = latestMetrics.current
        if (!scroll || !m || !current.ctx) return
        const entry = current.placed.find((candidate) => candidate.item.guid === guid)
        if (!entry?.geometry) return
        const boardLeft = m.ox + bayBoardOriginX(current.ctx.shelf, entry.item.bayIndex) * m.scale
        const left = boardLeft + entry.geometry.left * m.scale
        const right = boardLeft + entry.geometry.right * m.scale
        const top = m.oy + entry.geometry.top * m.scale
        const bottom = m.oy + entry.geometry.bottom * m.scale
        const inView = left >= scroll.scrollLeft && right <= scroll.scrollLeft + scroll.clientWidth && top >= scroll.scrollTop && bottom <= scroll.scrollTop + scroll.clientHeight
        if (inView) return
        // 瞬间滚动（不做平滑动画，尊重「减少动画」偏好）
        scroll.scrollTo({ left: Math.max(0, left - (scroll.clientWidth - (right - left)) / 2), top: Math.max(0, top - (scroll.clientHeight - (bottom - top)) / 2) })
      },
    }),
    [clientToShelfPoint],
  )

  // ---- Ctrl / ⌘ + 滚轮缩放（需要非被动监听才能阻止页面缩放 / 滚动） ----
  useEffect(() => {
    const element = scrollRef.current
    if (!element) return undefined
    const onWheel = (event: WheelEvent) => {
      if (!event.ctrlKey && !event.metaKey) return
      event.preventDefault()
      latest.current.onZoomWheel(event.deltaY < 0 ? 1 : -1)
    }
    element.addEventListener('wheel', onWheel, { passive: false })
    return () => element.removeEventListener('wheel', onWheel)
  }, [])

  // ---- 画布内拖动已放商品（pointer capture） ----
  const finishDrag = useCallback(() => {
    dragRef.current = null
    setDragPreview(null)
    setDraggingGuid(null)
  }, [])

  const handleItemPointerDown = useCallback((guid: string, event: ReactPointerEvent<SVGGElement>) => {
    event.stopPropagation()
    const current = latest.current
    current.onSelect(guid)
    scrollRef.current?.focus({ preventScroll: true })
    if (current.readOnly || event.button !== 0 || !current.ctx) return
    const entry = current.placed.find((candidate) => candidate.item.guid === guid)
    if (!entry?.geometry) return
    const point = clientToShelfPoint(event.clientX, event.clientY)
    if (!point) return
    const anchorX = bayBoardOriginX(current.ctx.shelf, entry.item.bayIndex) + entry.geometry.holeX
    try {
      event.currentTarget.setPointerCapture(event.pointerId)
    } catch {
      // 合成事件 / 指针已失效时没法捕获：放弃拖动，只保留选中
      return
    }
    dragRef.current = {
      guid,
      pointerId: event.pointerId,
      startClientX: event.clientX,
      startClientY: event.clientY,
      grabDxMm: point.xMm - anchorX,
      grabDyMm: point.yMm - entry.geometry.holeY,
      moved: false,
      lastKey: null,
    }
  }, [clientToShelfPoint])

  const handlePointerMove = useCallback((event: ReactPointerEvent<SVGSVGElement>) => {
    const drag = dragRef.current
    const current = latest.current
    if (!drag || !current.ctx || drag.pointerId !== event.pointerId) return
    if (!drag.moved) {
      if (Math.hypot(event.clientX - drag.startClientX, event.clientY - drag.startClientY) < DRAG_THRESHOLD_PX) return
      drag.moved = true
      setDraggingGuid(drag.guid)
    }
    const point = clientToShelfPoint(event.clientX, event.clientY)
    const item = current.items.find((candidate) => candidate.guid === drag.guid)
    if (!point || !item) {
      drag.lastKey = null
      setDragPreview(null)
      return
    }
    // 指针移出画布可视区域时没有提示（松手即取消）；在画布内用「指针 − 抓取偏移」吸附最近孔位
    const hint = buildDropHint(
      current.ctx,
      current.placed,
      { xMm: point.xMm - drag.grabDxMm, yMm: point.yMm - drag.grabDyMm },
      { productCode: item.productCode, hookModelGuid: item.hookModelGuid, perHookQty: item.perHookQty },
      item.guid,
    )
    const key = hint ? `${hint.bayIndex}/${hint.holeCol}/${hint.holeRow}` : null
    if (key === drag.lastKey) return
    drag.lastKey = key
    // 落点变了才重算对调 / 插入（重排要逐个校验被挪动的商品，指针每动一下都算没必要）
    setDragPreview(hint ? { hint, plan: planDragArrange({ ctx: current.ctx, items: current.items, hint, dragged: item, isNew: false }) } : null)
  }, [clientToShelfPoint])

  const handlePointerUp = useCallback((event: ReactPointerEvent<SVGSVGElement>) => {
    const drag = dragRef.current
    if (!drag || drag.pointerId !== event.pointerId) return
    const current = latest.current
    const item = current.items.find((candidate) => candidate.guid === drag.guid)
    // 用抬起那一刻的指针重新算一次落点与重排，不依赖上一次 move 的状态
    let hint: DropHint | null = null
    let plan: ArrangePlan = NO_PLAN
    if (drag.moved && current.ctx && item) {
      const point = clientToShelfPoint(event.clientX, event.clientY)
      if (point) {
        hint = buildDropHint(
          current.ctx,
          current.placed,
          { xMm: point.xMm - drag.grabDxMm, yMm: point.yMm - drag.grabDyMm },
          { productCode: item.productCode, hookModelGuid: item.hookModelGuid, perHookQty: item.perHookQty },
          item.guid,
        )
        if (hint) plan = planDragArrange({ ctx: current.ctx, items: current.items, hint, dragged: item, isNew: false })
      }
    }
    finishDrag()
    if (!hint || !item) return
    // 压住了别的商品：对调 / 插入（或放不下时整次拒绝）；没压住才按原来的普通移动处理
    if (plan.kind === 'noop') return
    if (plan.kind === 'swap' || plan.kind === 'insert') {
      current.onArrange?.(plan, item.guid)
      return
    }
    if (plan.kind === 'reject') {
      current.onRejectArrange?.(plan)
      return
    }
    const unchanged = hint.bayIndex === item.bayIndex && hint.holeCol === item.holeCol && hint.holeRow === item.holeRow
    if (unchanged) return
    // 严格吸附：不可放就回弹（条目没动）；「自由」模式放到最近孔位，冲突之后标红
    if (hint.ok || current.snapMode === 'free') current.onMoveItem(item.guid, { bayIndex: hint.bayIndex, holeCol: hint.holeCol, holeRow: hint.holeRow })
    else current.onRejectMove(hint)
  }, [clientToShelfPoint, finishDrag])

  // Esc 取消正在进行的拖动
  useEffect(() => {
    if (!draggingGuid) return undefined
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') finishDrag()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [draggingGuid, finishDrag])

  // Tab / Shift+Tab：在画布里依次选中条目，到头后放行默认的焦点移动
  const handleKeyDown = useCallback((event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Tab' || event.altKey || event.ctrlKey || event.metaKey || event.target !== event.currentTarget) return
    if (latest.current.onTabSelect(event.shiftKey ? -1 : 1)) event.preventDefault()
  }, [])

  // ---- 渲染 ----
  const hint = externalHint ?? dragPreview?.hint ?? null
  const plan: ArrangePlan = externalHint ? externalPlan ?? NO_PLAN : dragPreview?.plan ?? NO_PLAN
  const hintFill = hint ? productColor(hint.productCode) : '#fff'
  // 对调 / 插入预览：被挪动的商品原位置淡化，新位置画虚线框；插入点画竖线
  const arrangeBoxes = useMemo(() => (ctx ? arrangePreviewBoxes(ctx, items, plan) : []), [ctx, items, plan])
  const fadedGuids = useMemo(() => arrangeFadedGuids(plan), [plan])
  const insertLine = plan.kind === 'insert' || plan.kind === 'reject' ? plan.line : null

  // 商品图：按方块在屏幕上的尺寸取缩略图档位；太小、没有图、加载失败都退回色块
  const devicePixelRatio = typeof window !== 'undefined' ? window.devicePixelRatio || 1 : 1
  const imageOf = (view: ItemView): string | null => {
    if (!showImages || view.missing || !metrics) return null
    const raw = specs[view.item.productCode]?.productImage?.trim()
    if (!raw) return null
    const heightPx = view.specHeightMm * metrics.scale
    if (heightPx < CANVAS_IMAGE_MIN_PX) return null
    const href = toProductThumbnailUrl(raw, canvasImageTier(Math.max(heightPx, view.specWidthMm * metrics.scale), devicePixelRatio))
    return href && !failedImages.has(href) ? href : null
  }
  const selectedEntry = selectedGuid ? placed.find((entry) => entry.item.guid === selectedGuid) : undefined
  const toolbar = useMemo(() => {
    if (!selectedEntry?.geometry || !ctx || !metrics) return null
    const cx = metrics.ox + (bayBoardOriginX(ctx.shelf, selectedEntry.item.bayIndex) + selectedEntry.geometry.centerX) * metrics.scale
    const top = metrics.oy + selectedEntry.geometry.top * metrics.scale
    return { left: Math.max(4, Math.min(metrics.width - 190, cx - 90)), top: Math.max(2, top - 52) }
  }, [ctx, metrics, selectedEntry])

  return (
    <div
      ref={scrollRef}
      className="planogram-designer-canvas-scroll"
      tabIndex={0}
      role="group"
      aria-label={labels.group}
      onKeyDown={handleKeyDown}
      onPointerDown={(event) => {
        // 点在画布空白处：取消选中并把焦点放到画布（键盘方向键 / Delete 才作用于画布）
        if (event.target === event.currentTarget) onSelect(null)
      }}
    >
      {ctx && metrics ? (
        <div className="planogram-designer-canvas-inner" style={{ width: metrics.width, height: metrics.height }}>
          <svg
            ref={svgRef}
            className="planogram-designer-svg"
            width={metrics.width}
            height={metrics.height}
            viewBox={`0 0 ${metrics.width} ${metrics.height}`}
            onPointerDown={() => {
              onSelect(null)
              scrollRef.current?.focus({ preventScroll: true })
            }}
            onPointerMove={handlePointerMove}
            onPointerUp={handlePointerUp}
            onPointerCancel={finishDrag}
          >
            <ShelfLayer shelf={ctx.shelf} bayCount={ctx.bayCount} m={metrics} showHoles={showHoles} showRulers={showRulers} labels={labels} />
            <g>
              {ordered.map((view) => (
                <ItemShape
                  key={view.item.guid}
                  view={view}
                  selected={view.item.guid === selectedGuid}
                  dragging={view.item.guid === draggingGuid || fadedGuids.has(view.item.guid)}
                  m={metrics}
                  showTags={showTags}
                  labels={labels}
                  onPointerDown={handleItemPointerDown}
                  image={imageOf(view)}
                  onImageError={handleImageError}
                />
              ))}
            </g>
            {selectedView ? <SelectionOverlay view={selectedView} m={metrics} /> : null}
            {previewBoxes && previewBoxes.length > 0 ? <PreviewBoxesLayer boxes={previewBoxes} ctx={ctx} m={metrics} /> : null}
            {hint && arrangeBoxes.length > 0 ? <PreviewBoxesLayer boxes={arrangeBoxes} ctx={ctx} m={metrics} /> : null}
            {hint && insertLine ? <InsertLineLayer line={insertLine} ctx={ctx} m={metrics} ok={plan.kind === 'insert'} /> : null}
            {hint ? <HintLayer hint={hint} text={describeHint(hint, plan)} ctx={ctx} m={metrics} showRulers={showRulers} fill={hintFill} canvasWidth={metrics.width} /> : null}
          </svg>
          {/* 拖动中（画布内拖动或从商品库拖入）不显示浮动工具条，免得盖住落点提示 */}
          {toolbar && !readOnly && !draggingGuid && !externalHint ? (
            <div className="planogram-designer-float" style={{ left: toolbar.left, top: toolbar.top }} role="toolbar" aria-label={labels.float.aria}>
              <button type="button" onClick={onCopyRight}><CopyOutlined aria-hidden="true" /> {labels.float.copyRight}</button>
              <button type="button" onClick={onDistribute}><ColumnWidthOutlined aria-hidden="true" /> {labels.float.distribute}</button>
              <button type="button" className="is-danger" onClick={onRemove} aria-label={labels.float.remove} title={labels.float.remove}><DeleteOutlined aria-hidden="true" /></button>
            </div>
          ) : null}
        </div>
      ) : (
        <div className="planogram-designer-canvas-empty">{emptyText}</div>
      )}
    </div>
  )
})

export default BoardCanvas

// ---------------------------------------------------------------------------
// 打印（PDF）版面图：同一套 ShelfLayer / buildItemViews / ItemShape，只是换成打印模式
// ---------------------------------------------------------------------------

export interface BoardPrintProps {
  ctx: LayoutContext
  items: readonly DesignerItem[]
  specs: Readonly<Record<string, ProductSpecListItemDto>>
  /** 打印标红用：只要条目有冲突，就和设计器一样画红框 / 红色徽标。 */
  conflictsByGuid: ReadonlyMap<string, DisplayConflict[]>
  /** 条目 guid → 清单序号（节 → 行 → 列排序后的 1..N，与执行清单行号一致）。 */
  numbers: ReadonlyMap<string, number>
  /** 本页画第 bayFrom..bayTo 节。 */
  bayFrom: number
  bayTo: number
  /** 1 mm 对应的像素数（由导出页按纸张 / 比例算出）。 */
  scale: number
  options: BoardPrintOptions
  /** 商品货号 → 已加载好的实拍图（data URL）；没有的商品回退成白底。 */
  images: ReadonlyMap<string, string>
  lang: PrintLang
  nameOf: BuildItemViewsArgs['nameOf']
  labels: BoardPrintLabels
  /** 内部行号（自板顶）→ 显示行号（货架型号的「行号起算」可能是自底板）。 */
  rowLabel: (row: number) => number
}

/** 打印版面图 SVG：白底、孔位列 / 行号、序号气泡、价签价格、可选实拍图；冲突标红保留，选中手柄 / 浮动工具条 / 交互全部没有。 */
export function BoardPrintSvg(props: BoardPrintProps) {
  const { ctx, items, specs, conflictsByGuid, numbers, bayFrom, bayTo, scale, options, images, lang, nameOf, labels, rowLabel } = props
  const lastBay = Math.min(ctx.bayCount, bayTo)
  const visibleBays = Math.max(1, lastBay - bayFrom + 1)
  const metrics = printMetrics(ctx.shelf, visibleBays, scale)
  // 条目按「整个货架」的坐标算，所以条目的 ox 要整体左移到本页第一节的起点（ShelfLayer 的 ox 本来就是第一节左缘）
  const itemMetrics: CanvasMetrics = { ...metrics, ox: metrics.ox - (bayFrom - 1) * ctx.shelf.bayWidthMm * scale }
  const pageItems = useMemo(() => items.filter((item) => item.bayIndex >= bayFrom && item.bayIndex <= lastBay), [bayFrom, items, lastBay])
  const views = useMemo(
    () => buildItemViews({ ctx, items: pageItems, specs, conflictsByGuid, conflictText: () => '', nameOf }),
    [conflictsByGuid, ctx, nameOf, pageItems, specs],
  )
  // 绘制顺序：普通 → 有冲突（后画的在上面，红框不被盖住），与设计器一致
  const ordered = useMemo(() => {
    const rank = (view: ItemView) => (view.hasError || view.hasWarn ? 1 : 0)
    return [...views].sort((a, b) => rank(a) - rank(b))
  }, [views])
  const itemLabels = useMemo(() => ({ itemTitle: () => '', specChanged: '' }), [])
  return (
    <svg
      className="planogram-pdf-board-svg"
      width={metrics.width}
      height={metrics.height}
      viewBox={`0 0 ${metrics.width} ${metrics.height}`}
      xmlns="http://www.w3.org/2000/svg"
      // 截图时 SVG 被序列化成独立图片，页面 CSS 不会跟进去：字体与文字色必须写在 SVG 自身
      fontFamily={PRINT_FONT_FAMILY}
    >
      <ShelfLayer
        shelf={ctx.shelf}
        bayCount={ctx.bayCount}
        bayFrom={bayFrom}
        bayTo={lastBay}
        m={metrics}
        showHoles
        showRulers
        labels={labels}
        printCoords={options.coords ? { rowLabel } : undefined}
      />
      <g>
        {ordered.map((view) => (
          <ItemShape
            key={view.item.guid}
            view={view}
            selected={false}
            dragging={false}
            m={itemMetrics}
            showTags
            labels={itemLabels}
            onPointerDown={noopPointerDown}
            print={{
              number: options.bubbles ? numbers.get(view.item.guid) ?? null : null,
              showPrice: options.prices,
              image: images.get(view.item.productCode) ?? null,
              lang,
            }}
          />
        ))}
      </g>
    </svg>
  )
}

const noopPointerDown = () => undefined

/** 打印字体栈（设计稿 plano.js 同一串）：覆盖 macOS / Windows / Linux 的中文无衬线字体。 */
export const PRINT_FONT_FAMILY = '-apple-system, BlinkMacSystemFont, "PingFang SC", "Segoe UI", Roboto, "Helvetica Neue", Arial, "Microsoft YaHei", sans-serif'
