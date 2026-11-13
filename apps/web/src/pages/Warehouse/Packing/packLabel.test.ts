import type { PackCartonSummary, PackPalletDetail } from '../../../types/warehousePacking'

import {
  PACK_LABEL_PRINT_SCALE,
  buildCartonLabelFields,
  buildLabelBarcodeSvg,
  buildLabelQrSvg,
  buildPackLabelDocument,
  buildPackLabelHtml,
  buildPalletLabelFields,
  formatLabelTime,
  resolveToFontSize,
  toLabelText,
} from './packLabel'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertIncludes(text: string, part: string, message: string) {
  if (!text.includes(part)) {
    throw new Error(`${message}。缺少: ${part}`)
  }
}

// 本地时间 2026-11-13 14:32：标签时间按浏览器本地时区显示，用本地构造避免依赖运行环境时区。
const NOW = new Date(2026, 10, 13, 14, 32, 0)
const SEALED_AT = NOW.toISOString()

function carton(overrides: Partial<PackCartonSummary> = {}): PackCartonSummary {
  return {
    cartonGuid: 'carton-1',
    cartonNo: 'BX2611130042',
    status: 2,
    originName: 'HB Warehouse',
    destination: { destinationType: 1, storeCode: '1008', storeName: 'Sunnybank' },
    skuCount: 5,
    pieceCount: 86,
    orderGuid: 'order-1',
    orderNo: '2026-0412',
    createdByName: 'Tom',
    createdAtUtc: SEALED_AT,
    sealedByName: 'Lily',
    sealedAtUtc: SEALED_AT,
    updatedAtUtc: SEALED_AT,
    ...overrides,
  }
}

function pallet(overrides: Partial<PackPalletDetail> = {}): PackPalletDetail {
  return {
    palletGuid: 'pallet-1',
    palletNo: 'PL261113007',
    status: 2,
    originName: 'HB Warehouse',
    destination: {
      destinationType: 2,
      storeCode: '1020',
      storeName: 'Cairns',
      carrierGuid: 'carrier-1',
      carrierName: 'Border Express',
    },
    consignmentNo: 'BEX 4471 0932 18',
    cartonCount: 5,
    skuCount: 20,
    pieceCount: 366,
    createdByName: 'Tom',
    createdAtUtc: SEALED_AT,
    sealedByName: 'Lily',
    sealedAtUtc: SEALED_AT,
    updatedAtUtc: SEALED_AT,
    cartons: [],
    products: [],
    ...overrides,
  }
}

// ---- 文字清洗与时间 ----
assertEqual(toLabelText('  Sunny  bank '), 'SUNNY BANK', '大写并压缩空白')
assertEqual(toLabelText('总仓'), '', '纯中文清洗后为空')
assertEqual(toLabelText('Inala 分店'), 'INALA', '中英混排只留英文')
assertEqual(toLabelText(null), '', '空值')
assertEqual(formatLabelTime(SEALED_AT), '13 NOV 14:32', '标签时间格式')
assertEqual(formatLabelTime(null, NOW), '13 NOV 14:32', '没有封箱时间用当前时间')

// ---- 箱标签字段 ----
const cartonFields = buildCartonLabelFields(carton(), NOW)
assertEqual(cartonFields.kindText, 'BOX', '左侧黑条 BOX')
assertEqual(cartonFields.kindCaption, 'CARTON', '黑条小字 CARTON')
assertEqual(cartonFields.origin, 'HB WAREHOUSE', '发出地大写')
assertEqual(cartonFields.via, null, '箱标签不打 VIA')
assertEqual(cartonFields.direct, false, '箱标签不打 DIRECT')
assertEqual(cartonFields.to, 'SUNNYBANK', '去向为分店名大写')
assertEqual(cartonFields.code, 'BX2611130042', '完整编号')
assertEqual(cartonFields.codePrefix, 'BX261113', '编号前 8 位')
assertEqual(cartonFields.codeTail, '0042', '流水尾号')
assertEqual(cartonFields.stat, '5 SKU · 86 PCS', '箱数量')
assertEqual(cartonFields.reference, 'ORDER 2026-0412', '右下订货单号')
assertEqual(cartonFields.stamp, '13 NOV 14:32 LILY', '时间 + 封箱人')
assertEqual(cartonFields.copy, null, '箱标签不带张数')

const unassigned = buildCartonLabelFields(
  carton({ destination: { destinationType: 0 }, orderGuid: null, orderNo: null, sealedAtUtc: null, sealedByName: null }),
  NOW,
)
assertEqual(unassigned.to, 'UNASSIGNED', '未指定去向打 UNASSIGNED')
assertEqual(unassigned.reference, null, '没关联订货单时不打订货单号')
assertEqual(unassigned.stamp, '13 NOV 14:32 TOM', '未封箱时用当前时间和建箱人')

assertEqual(
  buildCartonLabelFields(carton({ destination: { destinationType: 1, storeCode: '1008', storeName: '阳光岸分店' } }), NOW).to,
  '1008',
  '分店名没有英文时用分店编码',
)
assertEqual(
  buildCartonLabelFields(carton({ destination: { destinationType: 2, carrierGuid: 'c', carrierName: 'Toll' } }), NOW).to,
  'UNASSIGNED',
  '经运输公司但没填最终分店时为 UNASSIGNED',
)
assertEqual(buildCartonLabelFields(carton({ sealedByName: '莉莉' }), NOW).stamp, '13 NOV 14:32', '操作人没有英文名时只打时间')

// ---- 托盘标签字段：2 张，1/2、2/2 ----
const palletLabels = buildPalletLabelFields(pallet(), NOW)
assertEqual(palletLabels.length, 2, '托盘打 2 张')
assertEqual(palletLabels.map((label) => label.copy).join(','), '1/2,2/2', '张数标记')
const palletFields = palletLabels[0]
assertEqual(palletFields.kindText, 'PLT', '左侧黑条 PLT')
assertEqual(palletFields.kindCaption, 'PALLET', '黑条小字 PALLET')
assertEqual(palletFields.via, 'BORDER EXPRESS', '经运输公司打 VIA')
assertEqual(palletFields.direct, false, '经运输公司不打 DIRECT')
assertEqual(palletFields.to, 'CAIRNS', '去向为最终收货分店')
assertEqual(palletFields.codePrefix, 'PL261113', '托盘编号前 8 位')
assertEqual(palletFields.codeTail, '007', '托盘流水尾号')
assertEqual(palletFields.stat, '5 CTN · 366 PCS', '托盘数量')
assertEqual(palletFields.reference, 'CN BEX 4471 0932 18', '右下运单号')
assertEqual(palletFields.stamp, '13 NOV 14:32 LILY', '时间 + 封托人')

const direct = buildPalletLabelFields(
  pallet({
    destination: { destinationType: 1, storeCode: '1011', storeName: 'Inala' },
    consignmentNo: null,
    cartons: [carton({ orderNo: '2026-0398' }), carton({ cartonGuid: 'carton-2', orderNo: '2026-0398' })],
  }),
  NOW,
)[1]
assertEqual(direct.direct, true, '直送分店打 DIRECT')
assertEqual(direct.via, null, '直送没有承运商')
assertEqual(direct.reference, 'ORDER 2026-0398', '直送且各箱同一订货单时右下打订货单号')
assertEqual(direct.copy, '2/2', '第二张')

assertEqual(
  buildPalletLabelFields(
    pallet({
      destination: { destinationType: 1, storeCode: '1011', storeName: 'Inala' },
      consignmentNo: null,
      cartons: [carton({ orderNo: '2026-0398' }), carton({ cartonGuid: 'carton-2', orderNo: '2026-0401' })],
    }),
    NOW,
  )[0].reference,
  null,
  '各箱订货单不同就不打订货单号',
)
assertEqual(
  buildPalletLabelFields(pallet({ destination: { destinationType: 0 }, consignmentNo: null }), NOW)[0].to,
  'UNASSIGNED',
  '托盘未指定去向',
)

// ---- 字号分档：长名字缩号，不换行不截断 ----
assertEqual(resolveToFontSize('INALA'), 40, '短名用最大字')
assertEqual(resolveToFontSize('SUNNYBANK'), 40, '9 个普通宽度字母仍用最大字（与设计稿一致）')
assertEqual(resolveToFontSize('TOOWOOMBA'), 33, '含 W / M 的长名缩一号（与设计稿一致）')
assertEqual(resolveToFontSize('UNASSIGNED'), 33, 'UNASSIGNED 缩一号')
assertEqual(resolveToFontSize('SURFERS PARADISE'), 21, '很长的名字用最小字')

// ---- 条码、二维码 ----
const barcode = buildLabelBarcodeSvg('BX2611130042')
assertIncludes(barcode, 'preserveAspectRatio="none"', '一维码横向拉伸到固定宽度')
assertEqual(/<path d="M[\dMhvz. -]+" fill="#000"\/>/.test(barcode), true, '一维码是单个纯黑 path')
assertEqual(buildLabelBarcodeSvg('中文'), '', 'Code128 无法编码时不出条码')
const qr = buildLabelQrSvg('BX2611130042')
assertEqual(/viewBox="0 0 (\d+) \1"/.test(qr), true, '二维码是正方形')
assertIncludes(qr, 'fill="#000"', '二维码纯黑')
assertEqual(buildLabelQrSvg('BX2611130042') === buildLabelQrSvg('BX2611130043'), false, '二维码内容随编号变化')

// ---- 标签 HTML ----
const cartonHtml = buildPackLabelHtml(cartonFields)
assertIncludes(cartonHtml, 'class="pkl pkl-carton"', '箱标签样式类')
assertIncludes(cartonHtml, '<b>BOX</b><i>CARTON</i>', '黑条文字')
assertIncludes(cartonHtml, 'FROM <b>HB WAREHOUSE</b></div>', '箱标签 FROM 行没有后缀')
assertIncludes(cartonHtml, '<em>TO</em>SUNNYBANK', 'TO 行')
assertIncludes(cartonHtml, '<span>BX261113</span><b>0042</b>', '编号前缀小字 + 尾号大字')
assertIncludes(cartonHtml, '5 SKU · 86 PCS', '数量')
assertIncludes(cartonHtml, 'ORDER 2026-0412<br>13 NOV 14:32 LILY', '右下两行')
assertEqual(cartonHtml.includes('pkl-copy'), false, '箱标签没有张数')

const palletHtml = buildPackLabelHtml(palletFields)
assertIncludes(palletHtml, 'class="pkl pkl-pallet"', '托盘标签样式类（整圈粗黑框）')
assertIncludes(palletHtml, 'FROM <b>HB WAREHOUSE</b> · VIA <b>BORDER EXPRESS</b>', '托盘 FROM 行带 VIA')
assertIncludes(palletHtml, '<b class="pkl-copy">1/2</b>', '托盘张数')
assertIncludes(buildPackLabelHtml(direct), 'FROM <b>HB WAREHOUSE</b> · DIRECT', '直送托盘 FROM 行带 DIRECT')
assertIncludes(
  buildPackLabelHtml({ ...cartonFields, to: 'A&B <X>' }),
  '<em>TO</em>A&amp;B &lt;X&gt;',
  '文字必须转义',
)

// 标签本体只出现英文和数字：去掉 SVG 路径后不应有非 ASCII 字符（· 分隔符除外）
for (const html of [cartonHtml, palletHtml]) {
  const text = html.replace(/<svg[\s\S]*?<\/svg>/g, '').replace(/<[^>]+>/g, ' ').replace(/·/g, '')
  assertEqual(/^[\x20-\x7e]*$/.test(text), true, '标签文字应全部是英文和数字')
}

// ---- 打印文档：70×26 mm，一张标签一页 ----
const documentHtml = buildPackLabelDocument([cartonFields, ...palletLabels])
assertIncludes(documentHtml, '@page{size:70mm 26mm;margin:0}', '纸张 70×26 mm 且无页边距')
assertEqual(documentHtml.split('<div class="pkl-page">').length - 1, 3, '一张标签一页')
assertIncludes(documentHtml, '.pkl-page{width:70mm;height:26mm;overflow:hidden;break-after:page', '每页固定标签尺寸并分页')
assertIncludes(documentHtml, `transform:scale(${PACK_LABEL_PRINT_SCALE})`, '570 点缩放到 70 mm')
assertEqual(Math.abs(PACK_LABEL_PRINT_SCALE * 570 - (70 / 25.4) * 96) < 0.01, true, '缩放后宽度等于 70 mm')
assertEqual(PACK_LABEL_PRINT_SCALE * 208 <= (26 / 25.4) * 96, true, '缩放后高度不超过 26 mm')
// 只用纯黑纯白：样式和内容里不应出现其他颜色
const colors = new Set((documentHtml.match(/#[0-9a-fA-F]{3,6}\b/g) ?? []).map((color) => color.toLowerCase()))
assertEqual([...colors].sort().join(','), '#000,#fff', '只允许纯黑纯白')
assertEqual(/rgba?\(|gray|grey/i.test(documentHtml), false, '不应出现灰色或半透明')

console.log('packLabel.test: ok')
