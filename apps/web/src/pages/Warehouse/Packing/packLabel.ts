import { Ecc, QrCode } from '@rc-component/qrcode/es/libs/qrcodegen'
import { generatePath } from '@rc-component/qrcode/es/utils'

import type { PackCartonSummary, PackPalletDetail, PackPalletSummary } from '../../../types/warehousePacking'
import { PACK_DESTINATION_TYPE } from '../../../types/warehousePacking'
import { buildBarcodeSvgPath, encodeBarcodeModules } from '../../../utils/barcode'

import { parsePackUtc } from './packingLogic'

// 箱 / 托盘标签：英文版 70×26 mm（与移动端同一设计，设计稿 l4.html）。
// 标签本体只出现英文和数字、只用纯黑纯白，热敏打印不会糊。
// 版式按设计稿的 570×208 打印点排版，打印时整体缩放到 70 mm 宽。

export const PACK_LABEL_WIDTH_PX = 570
export const PACK_LABEL_HEIGHT_PX = 208
/** 打印缩放：70 mm（CSS 固定 96 px/inch → 264.567 px）÷ 570 点。 */
export const PACK_LABEL_PRINT_SCALE = Number((((70 / 25.4) * 96) / PACK_LABEL_WIDTH_PX).toFixed(5))
/** 托盘标签固定打 2 张，贴相邻两侧。 */
export const PALLET_LABEL_COPIES = 2

export interface PackLabelFields {
  kind: 'carton' | 'pallet'
  /** 左侧黑条大字：BOX / PLT。 */
  kindText: 'BOX' | 'PLT'
  kindCaption: 'CARTON' | 'PALLET'
  /** 发出地（大写）。 */
  origin: string
  /** 托盘经运输公司时的承运商（大写）；有值时打印 · VIA <承运商>。 */
  via: string | null
  /** 托盘直送分店：打印 · DIRECT。 */
  direct: boolean
  /** 去向：分店名大写；未指定为 UNASSIGNED。 */
  to: string
  /** 完整编号：二维码和 Code128 的内容。 */
  code: string
  /** 编号前 8 位（小字）。 */
  codePrefix: string
  /** 流水尾号（大字）。 */
  codeTail: string
  /** 数量：箱 `N SKU · M PCS`，托盘 `N CTN · M PCS`。 */
  stat: string
  /** 右下第一行：订货单号 `ORDER …` 或运单号 `CN …`；没有则为空。 */
  reference: string | null
  /** 右下第二行：时间 + 操作人，如 `13 NOV 14:32 LILY`。 */
  stamp: string
  /** 托盘第几张：1/2、2/2；箱标签为空。 */
  copy: string | null
}

const MONTHS = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC']
const CODE_PREFIX_LENGTH = 8

/** 标签上只留可打印的 ASCII 字符并转大写；中文等字符去掉后为空时返回空串，由调用方决定兜底。 */
export function toLabelText(value: string | null | undefined): string {
  return (value ?? '')
    .replace(/[^\x20-\x7e]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .toUpperCase()
}

/** 标签时间：`13 NOV 14:32`，按浏览器本地时区（打包工位就在仓库）。 */
export function formatLabelTime(value: string | null | undefined, now: Date = new Date()): string {
  const date = parsePackUtc(value) ?? now
  const pad = (part: number) => String(part).padStart(2, '0')
  return `${pad(date.getDate())} ${MONTHS[date.getMonth()]} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

function splitCode(code: string) {
  return { codePrefix: code.slice(0, CODE_PREFIX_LENGTH), codeTail: code.slice(CODE_PREFIX_LENGTH) }
}

/** 去向大字：分店名（无英文名时用分店编码）；没有收货分店一律 UNASSIGNED。 */
function resolveTo(destination: PackCartonSummary['destination']): string {
  if (destination.destinationType === PACK_DESTINATION_TYPE.Unassigned) {
    return 'UNASSIGNED'
  }
  return toLabelText(destination.storeName) || toLabelText(destination.storeCode) || 'UNASSIGNED'
}

function buildStamp(sealedAtUtc: string | null | undefined, operatorName: string | null | undefined, now: Date) {
  return [formatLabelTime(sealedAtUtc, now), toLabelText(operatorName)].filter(Boolean).join(' ')
}

/** 箱 DTO → 标签字段。封箱前打印（补打）时时间取当前时间、操作人取建箱人。 */
export function buildCartonLabelFields(carton: PackCartonSummary, now: Date = new Date()): PackLabelFields {
  const orderNo = toLabelText(carton.orderNo)
  return {
    kind: 'carton',
    kindText: 'BOX',
    kindCaption: 'CARTON',
    origin: toLabelText(carton.originName),
    via: null,
    direct: false,
    to: resolveTo(carton.destination),
    code: carton.cartonNo,
    ...splitCode(carton.cartonNo),
    stat: `${carton.skuCount} SKU · ${carton.pieceCount} PCS`,
    reference: orderNo ? `ORDER ${orderNo}` : null,
    stamp: buildStamp(carton.sealedAtUtc, carton.sealedByName || carton.createdByName, now),
    copy: null,
  }
}

/**
 * 托盘 DTO → 标签字段（固定 2 张，带 1/2、2/2）。
 * 经运输公司：FROM 行加 VIA，右下打运单号（CN）；直送分店：FROM 行加 DIRECT，
 * 托盘上的箱都关联同一张订货单时右下打该订货单号。
 */
export function buildPalletLabelFields(
  pallet: PackPalletSummary & Partial<Pick<PackPalletDetail, 'cartons'>>,
  now: Date = new Date(),
): PackLabelFields[] {
  const { destination } = pallet
  const viaCarrier = destination.destinationType === PACK_DESTINATION_TYPE.Carrier
  const consignmentNo = toLabelText(pallet.consignmentNo)
  const orderNos = new Set((pallet.cartons ?? []).map((carton) => toLabelText(carton.orderNo)).filter(Boolean))
  const singleOrderNo = orderNos.size === 1 ? [...orderNos][0] : ''
  const reference = consignmentNo ? `CN ${consignmentNo}` : singleOrderNo ? `ORDER ${singleOrderNo}` : null

  const base: PackLabelFields = {
    kind: 'pallet',
    kindText: 'PLT',
    kindCaption: 'PALLET',
    origin: toLabelText(pallet.originName),
    via: viaCarrier ? toLabelText(destination.carrierName) || null : null,
    direct: destination.destinationType === PACK_DESTINATION_TYPE.Store,
    to: resolveTo(destination),
    code: pallet.palletNo,
    ...splitCode(pallet.palletNo),
    stat: `${pallet.cartonCount} CTN · ${pallet.pieceCount} PCS`,
    reference,
    stamp: buildStamp(pallet.sealedAtUtc, pallet.sealedByName || pallet.createdByName, now),
    copy: null,
  }
  return Array.from({ length: PALLET_LABEL_COPIES }, (_, index) => ({
    ...base,
    copy: `${index + 1}/${PALLET_LABEL_COPIES}`,
  }))
}

/** 估算大写文字的相对宽度：M / W 偏宽，I / 1 / 空格偏窄，其余按 1 个字宽。 */
function estimateLabelTextWidth(text: string): number {
  let width = 0
  for (const char of text) {
    width += char === 'M' || char === 'W' ? 1.4 : char === 'I' || char === '1' || char === ' ' ? 0.55 : 1
  }
  return width
}

/** 去向大字按估算宽度缩号：不换行、尽量不截断。返回字号（打印点）。 */
export function resolveToFontSize(to: string): number {
  const width = estimateLabelTextWidth(to)
  if (width <= 9.2) return 40
  if (width <= 11.5) return 33
  if (width <= 14.5) return 27
  return 21
}

function escapeHtml(value: string) {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

/** 底部一维码（Code128）：一个 path 的 SVG，横向拉伸到固定宽度。内容无法编码时不出条码。 */
export function buildLabelBarcodeSvg(code: string): string {
  const modules = encodeBarcodeModules(code, 'CODE128')
  if (!modules) {
    return ''
  }
  return (
    `<svg class="pkl-bc" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${modules.length} 44" preserveAspectRatio="none" shape-rendering="crispEdges">` +
    `<path d="${buildBarcodeSvgPath(modules, 1, 44)}" fill="#000"/></svg>`
  )
}

/** 右上二维码：内容为完整编号，纠错等级 M，不留边（标签自身留白）。 */
export function buildLabelQrSvg(code: string): string {
  const cells = QrCode.encodeText(code, Ecc.MEDIUM).getModules()
  return (
    `<svg class="pkl-qr" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${cells.length} ${cells.length}" shape-rendering="crispEdges">` +
    `<path d="${generatePath(cells, 0)}" fill="#000"/></svg>`
  )
}

/** 单张标签的 HTML（570×208 打印点）。所有文字先转义。 */
export function buildPackLabelHtml(fields: PackLabelFields): string {
  const fromSuffix = fields.via
    ? ` · VIA <b>${escapeHtml(fields.via)}</b>`
    : fields.direct
      ? ' · DIRECT'
      : ''
  const metaLines = [fields.reference, fields.stamp].filter((line): line is string => Boolean(line)).map(escapeHtml)
  const copy = fields.copy ? `<b class="pkl-copy">${escapeHtml(fields.copy)}</b>` : ''
  return (
    `<div class="pkl pkl-${fields.kind}">` +
    `<div class="pkl-kind"><b>${fields.kindText}</b><i>${fields.kindCaption}</i></div>` +
    '<div class="pkl-main"><div class="pkl-top"><div class="pkl-txt">' +
    `<div class="pkl-from">FROM <b>${escapeHtml(fields.origin)}</b>${fromSuffix}</div>` +
    `<div class="pkl-to" style="font-size:${resolveToFontSize(fields.to)}px"><em>TO</em>${escapeHtml(fields.to)}</div>` +
    `<div class="pkl-code"><span>${escapeHtml(fields.codePrefix)}</span><b>${escapeHtml(fields.codeTail)}</b>` +
    `<span class="pkl-stat">${escapeHtml(fields.stat)}</span></div>` +
    `</div>${buildLabelQrSvg(fields.code)}</div>` +
    `<div class="pkl-bot">${buildLabelBarcodeSvg(fields.code)}<div class="pkl-meta">${metaLines.join('<br>')}${copy}</div></div>` +
    '</div></div>'
  )
}

/** 标签本体样式：预览和打印共用（类名统一 pkl- 前缀）。 */
export const PACK_LABEL_CSS = `
.pkl,.pkl *{box-sizing:border-box;margin:0;padding:0}
.pkl{width:${PACK_LABEL_WIDTH_PX}px;height:${PACK_LABEL_HEIGHT_PX}px;background:#fff;color:#000;overflow:hidden;display:flex;font-family:"Helvetica Neue",Arial,sans-serif;-webkit-print-color-adjust:exact;print-color-adjust:exact}
.pkl-pallet{border:6px solid #000}
.pkl-kind{width:70px;background:#000;color:#fff;display:flex;flex-direction:column;align-items:center;justify-content:center;flex:none}
.pkl-kind b{font-size:27px;font-weight:900;letter-spacing:1px;line-height:1}
.pkl-kind i{font-style:normal;font-size:10.5px;font-weight:700;letter-spacing:1px;margin-top:6px}
.pkl-main{flex:1;min-width:0;display:flex;flex-direction:column;padding:7px 10px 7px 12px}
.pkl-pallet .pkl-main{padding:3px 6px 3px 10px}
.pkl-top{display:flex;gap:8px;flex:1;min-height:0}
.pkl-txt{flex:1;min-width:0;white-space:nowrap;overflow:hidden}
.pkl-from{font-size:13px;font-weight:600;line-height:17px}
.pkl-from b{font-weight:900}
.pkl-to{font-weight:900;line-height:42px;letter-spacing:.3px}
.pkl-to em{font-style:normal;font-size:13px;font-weight:800;letter-spacing:1px;margin-right:6px}
.pkl-code{display:flex;align-items:baseline;gap:6px;font-family:Menlo,Consolas,"Courier New",monospace;font-weight:800;line-height:40px}
.pkl-code span{font-size:19px}
.pkl-code b{font-size:40px;font-weight:800}
.pkl-code .pkl-stat{font-family:"Helvetica Neue",Arial,sans-serif;font-size:15px;font-weight:800;margin-left:6px}
.pkl-qr{width:112px;height:112px;flex:none;align-self:flex-start;margin-top:4px;display:block}
.pkl-bot{display:flex;align-items:flex-end;gap:10px;height:48px;flex:none}
.pkl-bc{width:232px;height:44px;flex:none;display:block}
.pkl-meta{flex:1;min-width:0;font-size:13px;font-weight:600;line-height:1.3;text-align:right;white-space:nowrap}
.pkl-copy{font-size:19px;font-weight:900;font-family:Menlo,Consolas,"Courier New",monospace;margin-left:6px}
`

/** 打印文档：一张标签一页，纸张 70×26 mm、无页边距；标签按 570 点 → 70 mm 等比缩放。 */
export function buildPackLabelDocument(labels: PackLabelFields[]): string {
  const pages = labels.map((fields) => `<div class="pkl-page"><div class="pkl-fit">${buildPackLabelHtml(fields)}</div></div>`)
  return (
    '<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Labels</title><style>' +
    '@page{size:70mm 26mm;margin:0}' +
    'html,body{margin:0;padding:0;background:#fff}' +
    '.pkl-page{width:70mm;height:26mm;overflow:hidden;break-after:page;page-break-after:always}' +
    '.pkl-page:last-child{break-after:auto;page-break-after:auto}' +
    `.pkl-fit{width:${PACK_LABEL_WIDTH_PX}px;height:${PACK_LABEL_HEIGHT_PX}px;transform-origin:0 0;transform:scale(${PACK_LABEL_PRINT_SCALE})}` +
    PACK_LABEL_CSS +
    `</style></head><body>${pages.join('')}</body></html>`
  )
}

function waitForAnimationFrame() {
  return new Promise<void>((resolve) => {
    window.requestAnimationFrame(() => resolve())
  })
}

/**
 * 用隐藏 iframe 打印标签（沿用仓库里 iframe 打印的时序：等两帧布局完成再 focus + print）。
 * 不 import StoreOrders/printUtils：它与发票 / 拣货单共用 print.css 分块，多一个引用方会破坏包体门禁。
 */
export function printPackLabels(labels: PackLabelFields[]) {
  if (!labels.length) {
    return
  }
  const frame = document.createElement('iframe')
  let cleaned = false
  const cleanup = () => {
    if (!cleaned) {
      cleaned = true
      frame.remove()
    }
  }
  frame.setAttribute('aria-hidden', 'true')
  frame.style.cssText = 'position:fixed;left:-10000px;top:0;width:70mm;height:26mm;border:0;opacity:0;pointer-events:none'
  frame.onload = () => {
    const printWindow = frame.contentWindow
    if (!printWindow) {
      cleanup()
      return
    }
    printWindow.addEventListener('afterprint', cleanup, { once: true })
    void waitForAnimationFrame()
      .then(waitForAnimationFrame)
      .then(() => {
        printWindow.focus()
        printWindow.print()
        // afterprint 在部分 Chromium 版本不触发：打印触发后兜底回收。
        window.setTimeout(cleanup, 60_000)
      })
  }
  frame.srcdoc = buildPackLabelDocument(labels)
  document.body.appendChild(frame)
}
