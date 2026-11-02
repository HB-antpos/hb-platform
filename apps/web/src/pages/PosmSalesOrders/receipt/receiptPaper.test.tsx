import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { renderToStaticMarkup } from 'react-dom/server'

import type { PosReceiptDocument } from '../../../types/posmSalesOrder'

import ReceiptPaper from './ReceiptPaper'

const sample: PosReceiptDocument = {
  orderGuid: '7e4c91d2-0b5a-4f3e-9a21-5c8d6e0f13ab',
  shortCode: '6E0F13AB',
  statusKind: 'paid',
  lines: [
    { kind: 'text', text: 'HotBargain', align: 'center', bold: true },
    { kind: 'blank', text: '', align: 'left', bold: false },
    { kind: 'barcode', text: '6E0F13AB', align: 'center', bold: false },
    { kind: 'text', text: '6E0F13AB', align: 'center', bold: true },
    { kind: 'separator', text: '-'.repeat(42), align: 'left', bold: false },
    { kind: 'text', text: 'Total(inc GST)                      $23.85', align: 'left', bold: true },
    { kind: 'qr', text: 'https://hotbargain.vip/r/7K2M9QXD4TBH', align: 'center', bold: false },
  ],
}

const markup = renderToStaticMarkup(<ReceiptPaper document={sample} />)

// 逐行按后端给的内容绘制：居中 / 加粗标记、空行占位、分隔线整行 42 个减号，金额列的空格原样保留。
assert.ok(/class="posm-receipt-row is-center is-bold">HotBargain</.test(markup), '品牌行居中加粗')
assert.ok(markup.includes('class="posm-receipt-row"> </div>'), '空行用不间断空格占位，保持行高')
assert.ok(markup.includes('-'.repeat(42)), '分隔线 42 列')
assert.ok(markup.includes('Total(inc GST)                      $23.85'), '合计行空格原样保留')
assert.ok(/class="posm-receipt-row is-bold">Total\(inc GST\)/.test(markup), '合计行加粗、左对齐')

// 单号条码：Code128 画成 SVG，宽按 3 点 / 模块换算（123 模块 × 3 点 ≈ 纸宽的 64%），可访问名称是单号。
const barcode = markup.match(/<svg class="posm-receipt-barcode"[^>]*>/)?.[0] ?? ''
assert.ok(barcode.includes('aria-label="6E0F13AB"'), '条码 aria-label 是单号')
assert.ok(barcode.includes('viewBox="0 0 123 1"'), '123 个模块')
assert.ok(/width:26\.9\d*ch/.test(barcode), '条码宽度按 369/576 的纸宽比例换算（约 26.9ch）')

// 二维码存在，且纸面本身固定 42ch，不随容器折行。
assert.ok(markup.includes('QR code'), '二维码已渲染')
const css = readFileSync(resolve('src/pages/PosmSalesOrders/receipt/receiptPaper.css'), 'utf8')
assert.ok(/\.posm-receipt-paper \{[^}]*width: 42ch;/s.test(css), '纸宽固定 42 个等宽字符')
assert.ok(/white-space: pre;/.test(css), '行内空格不折叠、不换行')
assert.ok(css.includes('#posm-receipt-print-root'), '打印只输出小票根节点')
assert.ok(css.includes('size: 80mm auto'), '打印纸宽 80mm')

// 打印变体：去掉阴影 / 锯齿由样式负责，标记上区分 is-print。
assert.ok(renderToStaticMarkup(<ReceiptPaper document={sample} variant="print" />).includes('is-print'), '打印变体')

// 退款单：状态只通过 data-status 暴露给纸外着色，纸面文字不变。
const refund = renderToStaticMarkup(<ReceiptPaper document={{ ...sample, statusKind: 'refund' }} />)
assert.ok(refund.includes('data-status="refund"'))

// 公开页的请求不能走 utils/request：它出错时会把带令牌的 URL 上报到中心日志，也会在 401 时跳登录。
const publicService = readFileSync(resolve('src/services/publicReceiptService.ts'), 'utf8')
assert.ok(!/from '\.\.\/utils\/request'/.test(publicService), '公开小票请求不走 request 工具')
assert.ok(publicService.includes("credentials: 'omit'") && publicService.includes("referrerPolicy: 'no-referrer'"))

// 订单详情抽屉：默认停在「小票」页签，页脚有在线二维码与打印。
const drawer = readFileSync(resolve('src/pages/PosmSalesOrders/OrderDetailDrawer.tsx'), 'utf8')
assert.ok(drawer.includes("useState<'receipt' | 'items'>('receipt')"), '默认页签是小票')
assert.ok(drawer.includes('<ReceiptQrPopover') && drawer.includes('window.print()'), '页脚有二维码气泡与打印')
