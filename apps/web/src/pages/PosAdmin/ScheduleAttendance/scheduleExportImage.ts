// 导出排班图片：把导出模型排成一张 SVG，再转成 PNG。
// 为什么不直接截屏现有看板（html2canvas）：卡片样式用了 color-mix()，html2canvas 1.4.1 解析不了；
// 而且用样式隐藏管理者信息不可靠。这里从模型直接排版，模型里没有的东西（如年龄）不可能被画出来。
// 全程只用 SVG 基本图形和文字（不用 foreignObject、不引外部资源），转 canvas 时不会污染画布，也不需要新依赖。
// 版式与手机端导出图一致（按天分列的看板）；排版 / 文字测量分离，便于在 Node 里单测。

import { MEAL_ICON_PATH, MEAL_SLASH_PATH } from './mealMarkLogic'
import type { MealMark } from './mealMarkLogic'
import { formatHours } from './weekSummaryLogic'
import type { ExportCard, ExportModel } from './scheduleExportLogic'

export const EXPORT_FONT_FAMILY = '-apple-system, BlinkMacSystemFont, "PingFang SC", "Hiragino Sans GB", "Microsoft YaHei", "Segoe UI", Roboto, sans-serif'

/** 排版常量（px，出图时按 scale 放大）。 */
export const EXPORT_LAYOUT = {
  padding: 24,
  titleHeight: 62,
  colWidth: 150,
  headHeight: 44,
  colPadX: 6,
  colPadTop: 7,
  colPadBottom: 8,
  cardHeight: 40,
  cardGap: 5,
  minBodyHeight: 56,
  footerHeight: 34,
  /** 导出画布任意一边的上限（像素），超过就降低放大倍数，避免浏览器画布过大失败。 */
  maxCanvasSide: 8192,
} as const

const COLORS = {
  title: '#101828',
  sub: '#475467',
  line: '#e4e7ec',
  outline: '#d0d5dd',
  headBg: '#f2f4f7',
  weekendBg: '#f9fafb',
  muted: '#98a2b3',
  danger: '#b42318',
  mealNoneInk: '#ad4e00',
  mealNoneBg: '#fff7e6',
  mealNoneLine: '#ffa940',
} as const

/** 测量文字宽度（px）。浏览器里用 canvas，单测里传假的。 */
export type TextMeasure = (text: string, fontSize: number, weight: number) => number

export interface ExportLayout {
  width: number
  height: number
  bodyTop: number
  bodyHeight: number
  footerTop: number
}

export function layoutScheduleExport(model: ExportModel): ExportLayout {
  const L = EXPORT_LAYOUT
  const width = L.padding * 2 + L.colWidth * model.days.length
  const tallest = Math.max(0, ...model.days.map((day) => day.cards.length))
  const bodyHeight = Math.max(
    L.minBodyHeight,
    tallest === 0 ? 0 : L.colPadTop + tallest * L.cardHeight + (tallest - 1) * L.cardGap + L.colPadBottom,
  )
  const bodyTop = L.padding + L.titleHeight + L.headHeight
  const footerTop = bodyTop + bodyHeight
  return { width, height: footerTop + L.footerHeight + L.padding, bodyTop, bodyHeight, footerTop }
}

/** XML 转义：名字、店名都是用户数据，必须转义，不能拼进 SVG 就当真。 */
export function escapeXml(value: string): string {
  return value.replace(/[&<>"']/g, (char) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' }[char] as string))
}

/** 文字太宽就截断并补「…」。 */
export function fitText(text: string, maxWidth: number, fontSize: number, weight: number, measure: TextMeasure): string {
  if (maxWidth <= 0) return ''
  if (measure(text, fontSize, weight) <= maxWidth) return text
  const chars = Array.from(text)
  let end = chars.length
  while (end > 0 && measure(`${chars.slice(0, end).join('')}…`, fontSize, weight) > maxWidth) end -= 1
  return end > 0 ? `${chars.slice(0, end).join('')}…` : ''
}

const n = (value: number) => Number(value.toFixed(2))

/** 午饭小标识：圆角底 + 叉勺（长班次无午饭叠斜杠、橙色虚线框；手动指定为虚线框；含 2 次写 ×2）。返回 SVG 片段与占用宽度。 */
function mealChip(mark: MealMark, ink: string, right: number, midY: number, measure: TextMeasure): { svg: string; width: number } {
  if (mark.kind === 'hidden') return { svg: '', width: 0 }
  const none = mark.kind === 'none'
  const countText = mark.count > 1 ? `×${mark.count}` : ''
  const countWidth = countText ? measure(countText, 10, 700) + 1 : 0
  const iconSize = 10
  const width = 6 + iconSize + countWidth
  const x = right - width
  const y = midY - 7
  const stroke = none ? COLORS.mealNoneLine : ink
  const dashed = none || mark.manual
  const inkColor = none ? COLORS.mealNoneInk : ink
  const scale = iconSize / 24
  const parts = [
    `<rect x="${n(x)}" y="${n(y)}" width="${n(width)}" height="14" rx="3" fill="${none ? COLORS.mealNoneBg : '#ffffff'}" fill-opacity="${none ? 1 : 0.72}"${dashed ? ` stroke="${stroke}" stroke-width="1" stroke-dasharray="2 1.5"` : ''}/>`,
    `<g transform="translate(${n(x + 3)} ${n(y + 2)}) scale(${n(scale)})" fill="none" stroke-linecap="round" stroke-linejoin="round">`,
    `<path d="${MEAL_ICON_PATH}" stroke="${inkColor}" stroke-width="2.4"/>`,
    none ? `<path d="${MEAL_SLASH_PATH}" stroke="${COLORS.mealNoneInk}" stroke-width="2.6"/>` : '',
    '</g>',
    countText ? `<text x="${n(x + 3 + iconSize + 1)}" y="${n(midY + 3.5)}" font-size="10" font-weight="700" fill="${inkColor}">${escapeXml(countText)}</text>` : '',
  ]
  return { svg: parts.join(''), width }
}

function cardSvg(card: ExportCard, x: number, y: number, width: number, texts: ExportModel['texts'], measure: TextMeasure): string {
  const H = EXPORT_LAYOUT.cardHeight
  const { bg, bar, ink } = card.color
  const out: string[] = []
  out.push(`<rect x="${n(x)}" y="${n(y)}" width="${n(width)}" height="${H}" rx="6" fill="${bg}" stroke="${bar}" stroke-opacity="0.35" stroke-width="1"${card.draft ? ' stroke-dasharray="4 3"' : ''}/>`)
  out.push(`<rect x="${n(x + 2)}" y="${n(y + 4)}" width="3" height="${H - 8}" rx="1.5" fill="${bar}"/>`)

  // 第一行：名字（太长截断）+ 草稿标记 ……… 工时
  const hoursText = card.paidHours !== null && card.paidHours > 0 ? formatHours(card.paidHours) : ''
  const hoursWidth = hoursText ? measure(hoursText, 10.5, 400) : 0
  const draftWidth = card.draft ? measure(texts.draft, 10, 400) + 8 : 0
  const nameMax = width - 10 - 6 - (hoursWidth ? hoursWidth + 6 : 0) - (draftWidth ? draftWidth + 4 : 0)
  const name = fitText(card.name, nameMax, 12.5, 600, measure)
  out.push(`<text x="${n(x + 10)}" y="${n(y + 16)}" font-size="12.5" font-weight="600" fill="${ink}">${escapeXml(name)}</text>`)
  if (draftWidth) {
    const tagX = x + 10 + measure(name, 12.5, 600) + 4
    out.push(`<rect x="${n(tagX)}" y="${n(y + 6)}" width="${n(draftWidth)}" height="13" rx="3" fill="#ffffff" fill-opacity="0.7" stroke="${COLORS.mealNoneLine}" stroke-dasharray="2 1.5"/>`)
    out.push(`<text x="${n(tagX + draftWidth / 2)}" y="${n(y + 15.5)}" font-size="10" fill="${COLORS.mealNoneInk}" text-anchor="middle">${escapeXml(texts.draft)}</text>`)
  }
  if (hoursText) out.push(`<text x="${n(x + width - 6)}" y="${n(y + 16)}" font-size="10.5" fill="${ink}" fill-opacity="0.75" text-anchor="end">${escapeXml(hoursText)}</text>`)

  // 第二行：时间 ……… 午饭标识
  out.push(`<text x="${n(x + 10)}" y="${n(y + 32)}" font-size="11.5" font-weight="500" fill="${ink}" style="font-variant-numeric:tabular-nums">${escapeXml(`${card.startTime}–${card.endTime}`)}</text>`)
  if (card.meal) out.push(mealChip(card.meal, ink, x + width - 6, y + 28, measure).svg)
  return out.join('')
}

/** 把模型排成完整的 SVG 文档（自带 width / height，作为图片加载时有固有尺寸）。 */
export function renderScheduleExportSvg(model: ExportModel, measure: TextMeasure): { svg: string; width: number; height: number } {
  const L = EXPORT_LAYOUT
  const layout = layoutScheduleExport(model)
  const { width, height } = layout
  const tableWidth = L.colWidth * model.days.length
  const tableLeft = L.padding
  const tableTop = L.padding + L.titleHeight
  const { texts } = model
  const out: string[] = []

  out.push(`<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" font-family='${EXPORT_FONT_FAMILY}'>`)
  out.push(`<rect width="${width}" height="${height}" fill="#ffffff"/>`)
  out.push(`<text x="${L.padding}" y="${L.padding + 24}" font-size="22" font-weight="700" fill="${COLORS.title}">${escapeXml(fitText(texts.title, tableWidth, 22, 700, measure))}</text>`)
  out.push(`<text x="${L.padding}" y="${L.padding + 46}" font-size="13" fill="${COLORS.sub}">${escapeXml(fitText(texts.subtitle, tableWidth, 13, 400, measure))}</text>`)

  // 每天一列：周末浅底、表头、竖线、卡片
  model.days.forEach((day, index) => {
    const colX = tableLeft + index * L.colWidth
    out.push(`<rect x="${colX}" y="${tableTop}" width="${L.colWidth}" height="${L.headHeight + layout.bodyHeight}" fill="${day.weekend ? COLORS.weekendBg : '#ffffff'}"/>`)
    out.push(`<rect x="${colX}" y="${tableTop}" width="${L.colWidth}" height="${L.headHeight}" fill="${COLORS.headBg}"/>`)
    out.push(`<text x="${colX + L.colWidth / 2}" y="${tableTop + 19}" font-size="13" font-weight="700" fill="${COLORS.title}" text-anchor="middle">${escapeXml(`${day.weekdayLabel} ${day.dateLabel}`)}</text>`)
    out.push(`<text x="${colX + L.colWidth / 2}" y="${tableTop + 35}" font-size="11" fill="${day.headcount === 0 ? COLORS.danger : COLORS.sub}" text-anchor="middle">${escapeXml(texts.onDuty(day.headcount))}</text>`)
    if (index > 0) out.push(`<line x1="${colX}" y1="${tableTop}" x2="${colX}" y2="${tableTop + L.headHeight + layout.bodyHeight}" stroke="${COLORS.line}"/>`)
    day.cards.forEach((card, row) => {
      const y = layout.bodyTop + L.colPadTop + row * (L.cardHeight + L.cardGap)
      out.push(cardSvg(card, colX + L.colPadX, y, L.colWidth - 2 * L.colPadX, texts, measure))
    })
  })
  out.push(`<line x1="${tableLeft}" y1="${tableTop + L.headHeight}" x2="${tableLeft + tableWidth}" y2="${tableTop + L.headHeight}" stroke="${COLORS.line}"/>`)
  out.push(`<rect x="${tableLeft}" y="${tableTop}" width="${tableWidth}" height="${L.headHeight + layout.bodyHeight}" fill="none" stroke="${COLORS.outline}"/>`)

  // 页脚：共 N 人 + 午饭图例（只列图里真的出现过的）……… 生成时间
  const footY = layout.footerTop + 21
  let cursor = tableLeft
  const total = texts.total(model.peopleCount)
  out.push(`<text x="${cursor}" y="${footY}" font-size="11" fill="${COLORS.muted}">${escapeXml(total)}</text>`)
  cursor += measure(total, 11, 400) + 14
  const kinds = new Set(model.days.flatMap((day) => day.cards.map((card) => card.meal?.kind)))
  ;([['meal', texts.mealLegendMeal], ['none', texts.mealLegendNone]] as const).forEach(([kind, label]) => {
    if (!kinds.has(kind)) return
    const chip = mealChip({ kind, count: 1, manual: false, deductMinutes: 0, durationMinutes: 0 }, '#475467', cursor + 16, footY - 4, measure)
    out.push(chip.svg)
    out.push(`<text x="${n(cursor + chip.width + 4)}" y="${footY}" font-size="11" fill="${COLORS.muted}">${escapeXml(label)}</text>`)
    cursor += chip.width + 4 + measure(label, 11, 400) + 12
  })
  out.push(`<text x="${tableLeft + tableWidth}" y="${footY}" font-size="11" fill="${COLORS.muted}" text-anchor="end">${escapeXml(texts.generatedAt)}</text>`)
  out.push('</svg>')
  return { svg: out.join('\n'), width, height }
}

// ---------- 浏览器端：测量、转 PNG、下载、复制 ----------

/** 用 canvas 量文字宽度（与最终出图用同一套字体栈）。 */
export function createCanvasMeasure(): TextMeasure {
  const ctx = document.createElement('canvas').getContext('2d')
  return (text, fontSize, weight) => {
    if (!ctx) return text.length * fontSize * 0.6
    ctx.font = `${weight} ${fontSize}px ${EXPORT_FONT_FAMILY}`
    return ctx.measureText(text).width
  }
}

export function svgToDataUrl(svg: string): string {
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`
}

/** 放大倍数：默认 2 倍，画布任意一边超过上限就降倍数。 */
export function pickScale(width: number, height: number, preferred = 2): number {
  const limit = EXPORT_LAYOUT.maxCanvasSide / Math.max(width, height)
  return Math.max(1, Math.min(preferred, limit))
}

export async function renderSvgToPngBlob(svg: string, width: number, height: number, preferredScale = 2): Promise<Blob> {
  const image = new Image()
  await new Promise<void>((resolve, reject) => {
    image.onload = () => resolve()
    image.onerror = () => reject(new Error('导出图片加载失败'))
    image.src = svgToDataUrl(svg)
  })
  const scale = pickScale(width, height, preferredScale)
  const canvas = document.createElement('canvas')
  canvas.width = Math.round(width * scale)
  canvas.height = Math.round(height * scale)
  const ctx = canvas.getContext('2d')
  if (!ctx) throw new Error('当前浏览器不支持画布')
  ctx.fillStyle = '#ffffff'
  ctx.fillRect(0, 0, canvas.width, canvas.height)
  ctx.scale(scale, scale)
  ctx.drawImage(image, 0, 0, width, height)
  return new Promise<Blob>((resolve, reject) => {
    canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error('PNG 编码失败'))), 'image/png')
  })
}

export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.appendChild(link)
  link.click()
  link.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 4000)
}

/** 浏览器是否支持把 PNG 写进剪贴板（需要 https / localhost 与 ClipboardItem）。 */
export function canCopyImage(): boolean {
  return typeof ClipboardItem !== 'undefined' && typeof navigator !== 'undefined' && Boolean(navigator.clipboard?.write)
}

export async function copyPngToClipboard(blob: Blob): Promise<void> {
  await navigator.clipboard.write([new ClipboardItem({ 'image/png': blob })])
}
