import type { StoreOrderDetail, StoreOrderDetailLine } from '../../../types/storeOrder'
import fs from 'node:fs'
import path from 'node:path'
import { buildBarcodeSvgPath, encodeBarcodeModules, isValidEAN13 } from '../../../utils/barcode'
import {
  PICKING_ORDER_BARCODE_HEIGHT,
  PICKING_ORDER_BARCODE_MODULE_WIDTH,
  buildPickingListExcelData,
  buildPickingListPdfPages,
  buildPickingOrderBarcode,
  buildPickingOrderBarcodeValue,
  formatInnerPackCount,
  formatPickingOrderQuantity,
} from './pickingListLogic'
import { formatStoreOrderVolume } from './volumeFormat'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualText = JSON.stringify(actual)
  const expectedText = JSON.stringify(expected)
  if (actualText !== expectedText) {
    throw new Error(`${label}。Expected: ${expectedText}, received: ${actualText}`)
  }
}

function runTest(name: string, execute: () => void) {
  execute()
  console.log(`ok - ${name}`)
}

// 这里锁定配货单“内包装数量”的业务规则，避免组件里再次出现临时兜底逻辑。
runTest('minOrderQuantity 有效时应返回纯数字格式的内包装数量', () => {
  assertEqual(formatInnerPackCount(12, undefined, 12), '1', '整除时应显示整数且不带小数')
  assertEqual(formatInnerPackCount(18, undefined, 12), '1.5', '非整除时应保留 1 位小数')
  assertEqual(formatInnerPackCount(0, 12, 12), '1', '订货数量为 0 时应使用发货数兜底计算包数')
  assertEqual(formatInnerPackCount(0, 18, 12), '1.5', '发货数兜底后非整除时应保留 1 位小数')
})

runTest('minOrderQuantity 为空 0 1 或无效时应返回空字符串', () => {
  assertEqual(formatInnerPackCount(24, undefined, undefined), '', 'minOrderQuantity 为空时应显示空字符串')
  assertEqual(formatInnerPackCount(24, undefined, 0), '', 'minOrderQuantity 为 0 时应显示空字符串')
  assertEqual(formatInnerPackCount(24, undefined, 1), '', 'minOrderQuantity 为 1 时应显示空字符串')
  assertEqual(formatInnerPackCount(24, undefined, Number.NaN), '', 'minOrderQuantity 非法时应显示空字符串')
})

runTest('分店订货体积应统一保留两位小数', () => {
  assertEqual(formatStoreOrderVolume(7.648), '7.65', '体积应四舍五入到两位小数')
  assertEqual(formatStoreOrderVolume(0), '0.00', '零体积也应显示两位小数')
  assertEqual(formatStoreOrderVolume(undefined), '--', '缺失体积应显示占位符')
})

const excelTexts = {
  sheetName: 'Picking List',
  orderNoLabel: 'Order No.',
  storeLabel: 'Store',
  orderDateLabel: 'Order Date',
  printTimeLabel: 'Print Time',
  remarksLabel: 'Remarks',
  totalSKULabel: 'Total SKU',
  totalOrderQtyLabel: 'Total Order Qty',
  totalShipQtyLabel: 'Total Ship Qty',
  totalOrderVolumeLabel: 'Order Volume',
  detailHeaders: {
    index: '#',
    itemNumber: '货号',
    location: '货位',
    productName: '商品名称',
    importPrice: '进口价',
    rrp: 'RRP',
    innerPackCount: '内包装数量',
    orderQuantity: '订货数量',
  },
}

const excelItems: StoreOrderDetailLine[] = [
  {
    detailGUID: 'detail-1',
    productCode: 'P-001',
    itemNumber: 'A-001',
    barcode: '111',
    productName: '商品 A',
    quantity: 12,
    allocQuantity: 10,
    price: 0,
    amount: 0,
    importPrice: 3.5,
    importAmount: 0,
    minOrderQuantity: 12,
    isActive: true,
    locationCode: 'L-01',
    rrp: 5.5,
  },
  {
    detailGUID: 'detail-2',
    productCode: 'P-002',
    itemNumber: 'A-002',
    barcode: '222',
    productName: '商品 B',
    quantity: 18,
    allocQuantity: 9,
    price: 0,
    amount: 0,
    importPrice: 4,
    importAmount: 0,
    minOrderQuantity: 12,
    isActive: true,
    locationCode: 'L-02',
    rrp: 6,
  },
  {
    detailGUID: 'detail-3',
    productCode: 'P-003',
    itemNumber: 'A-003',
    barcode: '333',
    productName: '商品 C',
    quantity: 0,
    allocQuantity: 15,
    price: 0,
    amount: 0,
    importPrice: 8,
    importAmount: 0,
    minOrderQuantity: 0,
    isActive: true,
    locationCode: 'L-03',
  },
  {
    detailGUID: 'detail-4',
    productCode: 'P-004',
    itemNumber: 'A-004',
    barcode: '444',
    productName: '商品 D',
    quantity: 24,
    allocQuantity: 24,
    price: 0,
    amount: 0,
    importPrice: 2,
    importAmount: 0,
    minOrderQuantity: 1,
    isActive: true,
    locationCode: 'L-04',
    rrp: 3,
  },
]

const excelOrder: StoreOrderDetail = {
  orderGUID: 'order-1',
  orderNo: 'SO-001',
  storeCode: 'ST-01',
  totalAmount: 0,
  totalQuantity: 37,
  totalImportAmount: 0,
  totalVolume: 0,
  totalOrderVolume: 12.3456,
  remarks: '请优先处理',
  totalAllocQuantity: 19,
  totalSKU: 3,
  itemsTotal: 4,
  orderDate: '2026-06-01T00:00:00.000Z',
  items: excelItems,
}

runTest('配货单 Excel 数据应包含固定列顺序、备注和总计信息', () => {
  const excelData = buildPickingListExcelData(excelOrder, excelItems, excelTexts)
  assertEqual(excelData.sheetName, 'Picking List', 'sheet 名称应来自传入文案')
  assertDeepEqual(
    excelData.overviewRows,
    [
      ['Order No.', 'SO-001'],
      ['Store', 'ST-01'],
      ['Order Date', '2026-06-01T00:00:00.000Z'],
      ['Print Time', ''],
    ],
    'Excel 概览仍应保留订单日期和打印时间元数据',
  )
  assertDeepEqual(
    excelData.detailHeader,
    ['#', '货号', '货位', '商品名称', '进口价', 'RRP', '内包装数量', '订货数量'],
    '明细列顺序应隐藏发货数列',
  )
  assertEqual(excelData.detailRows.every((row) => row.length === 8), true, 'Excel 明细行应保持 8 列')
  assertDeepEqual(excelData.detailRows.map((row) => row[6]), ['1', '1.5', '', ''], '内包装数量应复用统一格式化逻辑')
  assertDeepEqual(excelData.detailRows.map((row) => row[7]), [12, 18, 15, 24], '订货数为 0 时应使用发货数兜底')
  assertDeepEqual(excelData.remarksRow, ['Remarks', '请优先处理'], '备注行应保留原始备注')
  assertDeepEqual(
    excelData.totalRows,
    [
      ['Total SKU', 3],
      ['Total Order Qty', 37],
      ['Total Ship Qty', 19],
      ['Order Volume', '12.35'],
    ],
    '总计行应包含 SKU、订货数、发货数和两位小数订货体积',
  )
})

runTest('配货单订单条码内容应为 HBSO:订单号，订单号为空时不生成条码', () => {
  assertEqual(buildPickingOrderBarcodeValue('2026-0418'), 'HBSO:2026-0418', '条码内容应为 HBSO: 前缀加订单号')
  assertEqual(buildPickingOrderBarcodeValue(' 2026-0418 '), 'HBSO:2026-0418', '订单号首尾空白不应进入条码')
  for (const emptyOrderNo of ['', '   ', undefined, null]) {
    assertEqual(buildPickingOrderBarcodeValue(emptyOrderNo), null, `订单号为 ${JSON.stringify(emptyOrderNo)} 时不应生成条码内容`)
    assertEqual(buildPickingOrderBarcode(emptyOrderNo), null, `订单号为 ${JSON.stringify(emptyOrderNo)} 时表头不应渲染条码`)
  }
  assertEqual(buildPickingOrderBarcode('订单'), null, 'CODE128 无法编码的订单号不应渲染条码')

  const excelData = buildPickingListExcelData(excelOrder, excelItems, excelTexts)
  assertEqual(JSON.stringify(excelData).includes('HBSO:'), false, 'Excel 导出不需要订单条码内容')
})

runTest('配货单订单条码应强制 CODE128 并按 1px 模块输出单个矢量路径', () => {
  const modules = encodeBarcodeModules('HBSO:2026-0418', 'CODE128')
  assertEqual(modules?.length, 178, 'HBSO:2026-0418 按 CODE128 自动分段应为 178 个模块')
  assertEqual(modules?.startsWith('11010010000'), true, '条码应以 CODE128 Start B 开头')
  assertEqual(modules?.endsWith('1100011101011'), true, '条码应以 CODE128 Stop 结尾')

  // 合法 EAN13 内容强制 CODE128 时也必须输出 CODE128（Start C），不能被自动判断改成 EAN13。
  assertEqual(isValidEAN13('9310000000001'), true, '样例应是合法 EAN13')
  assertEqual(encodeBarcodeModules('9310000000001', 'CODE128')?.startsWith('11010011100'), true, '强制 CODE128 不应走 EAN13 自动判断')
  assertEqual(encodeBarcodeModules('HBSO:订单', 'CODE128'), null, 'CODE128 无法编码时应返回 null 而不是抛异常')
  assertEqual(buildBarcodeSvgPath('1101', 1, 36), 'M0 0h2v36h-2zM3 0h1v36h-1z', '相邻黑条模块应合并为一个矩形子路径')

  const barcode = buildPickingOrderBarcode('2026-0418')
  assertEqual(barcode?.value, 'HBSO:2026-0418', '表头条码应使用 HBSO:订单号')
  assertEqual(PICKING_ORDER_BARCODE_MODULE_WIDTH, 1, '条码模块宽应为 1px')
  assertEqual(PICKING_ORDER_BARCODE_HEIGHT >= 36 && PICKING_ORDER_BARCODE_HEIGHT <= 40, true, '条高应在 36-40px 之间')
  assertEqual(barcode?.width, 178, '条码宽度应等于模块数乘以 1px 模块宽')
  assertEqual(barcode?.height, PICKING_ORDER_BARCODE_HEIGHT, '条码高度应使用统一条高常量')

  // 把矢量路径还原成模块序列，确认绘制结果与 CODE128 编码逐模块一致。
  const redrawnModules = Array.from({ length: barcode?.width ?? 0 }, () => '0')
  for (const match of (barcode?.path ?? '').matchAll(/M(\d+) 0h(\d+)v(\d+)h-\2z/g)) {
    assertEqual(Number(match[3]), PICKING_ORDER_BARCODE_HEIGHT, '每根黑条都应画满条高')
    redrawnModules.fill('1', Number(match[1]), Number(match[1]) + Number(match[2]))
  }
  assertEqual(redrawnModules.join(''), modules, '矢量路径还原后应与 CODE128 模块序列完全一致')
})

runTest('配货单订货数为空或为 0 时应使用发货数兜底', () => {
  assertEqual(formatPickingOrderQuantity(12, 9), 12, '订货数有效时应优先显示订货数')
  assertEqual(formatPickingOrderQuantity(0, 12), 12, '订货数为 0 时应显示发货数')
  assertEqual(formatPickingOrderQuantity(undefined, 8), 8, '订货数缺失时应显示发货数')
  assertEqual(formatPickingOrderQuantity(0, 0), '', '订货数和发货数都为空时应显示空字符串')
})

runTest('配货单包数应使用订货数列同口径数量作为分子', () => {
  assertEqual(formatInnerPackCount(0, 12, 12), '1', 'MC020-16 主动配货 12 且中包数 12 时应显示 1 包')
  assertEqual(formatInnerPackCount(0, 18, 12), '1.5', '主动配货 18 且中包数 12 时应显示 1.5 包')
  assertEqual(formatInnerPackCount(undefined, 12, 12), '1', '订货数缺失时也应使用发货数兜底计算包数')
  assertEqual(formatInnerPackCount(0, 0, 12), '', '订货数和发货数都为空时包数应显示空白')
})

runTest('配货单打印应取消固定 30 行分页并交给 A4 打印流填满页面', () => {
  const pickingListSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/PickingList.tsx'), 'utf8')
  assertEqual(pickingListSource.includes('PICKING_PRINT_ROWS_PER_PAGE'), false, '打印组件不应保留固定 30 行分页常量')
  assertEqual(pickingListSource.includes('buildPickingPrintPages'), false, '打印组件不应按固定行数预切页')
})

runTest('配货单 PDF 分页应按 A4 可用高度计算并为最后一页保留汇总区域', () => {
  const items = Array.from({ length: 8 }, (_, index) => ({
    ...excelItems[0],
    detailGUID: `pdf-detail-${index}`,
    itemNumber: `PDF-${index}`,
  }))
  const pages = buildPickingListPdfPages(items, true, {
    pageHeightMm: 100,
    pagePaddingTopMm: 5,
    pagePaddingBottomMm: 5,
    headerHeightMm: 10,
    tableHeaderHeightMm: 5,
    footerHeightMm: 5,
    rowHeightMm: 10,
    finalSummaryHeightMm: 20,
  })

  assertDeepEqual(
    pages.map((page) => page.items.length),
    [7, 1],
    '尾页需要汇总时应优先让倒数第二页满排，最后一页可以少',
  )
  assertEqual(pages[0].showSummary, false, '非末页不应显示备注和汇总')
  assertEqual(pages[1].showSummary, true, '最后一页应显示备注和汇总')
})

runTest('配货单 PDF 每页应带页头元数据且页脚只承载页码', () => {
  const pages = buildPickingListPdfPages(excelItems, true, {
    pageHeightMm: 70,
    pagePaddingTopMm: 5,
    pagePaddingBottomMm: 5,
    headerHeightMm: 10,
    tableHeaderHeightMm: 5,
    footerHeightMm: 5,
    rowHeightMm: 10,
    finalSummaryHeightMm: 10,
  })

  pages.forEach((page) => {
    assertEqual(page.hasHeader, true, '每个 PDF 分页都应渲染业务页头')
    assertEqual(page.footerKind, 'pageNumber', 'PDF 页脚只能显示页码')
  })
})

runTest('配货单 PDF 默认分页每页应按 9mm 明细行放 26 行并保留页码空间', () => {
  const items = Array.from({ length: 31 }, (_, index) => ({
    ...excelItems[0],
    detailGUID: `footer-safe-${index}`,
    itemNumber: `SAFE-${index}`,
  }))
  const pages = buildPickingListPdfPages(items, false)

  assertEqual(pages[0].items.length, 26, '默认 PDF 分页首张 A4 应按 9mm 明细行容纳 26 行')
  assertEqual(pages.length >= 2, true, '31 行明细不应继续挤在同一页导致页码重叠')
})

runTest('配货单 PDF 带汇总的尾页应优先让倒数第二页满排', () => {
  const cases: Array<[number, number[]]> = [
    [23, [23]],
    [24, [24]],
    [25, [25]],
    [26, [25, 1]],
    [27, [26, 1]],
    [28, [26, 2]],
    [29, [26, 3]],
    [49, [26, 23]],
    [50, [26, 24]],
    [51, [26, 25]],
    [52, [26, 25, 1]],
    [75, [26, 26, 23]],
    [76, [26, 26, 24]],
    [77, [26, 26, 25]],
    [78, [26, 26, 25, 1]],
  ]

  for (const [itemCount, expectedPageSizes] of cases) {
    const items = Array.from({ length: itemCount }, (_, index) => ({
      ...excelItems[0],
      detailGUID: `summary-safe-${itemCount}-${index}`,
      itemNumber: `SUMMARY-${itemCount}-${index}`,
    }))
    const pages = buildPickingListPdfPages(items, true)
    const summaryPage = pages[pages.length - 1]

    assertDeepEqual(pages.map((page) => page.items.length), expectedPageSizes, `${itemCount} 行时应优先让倒数第二页满排`)
    assertEqual(summaryPage.showSummary, true, `${itemCount} 行时最后一页应显示汇总`)
    assertEqual(summaryPage.items.length <= 25, true, `${itemCount} 行时短尾合并后的汇总页不应超过 25 行`)
  }
})

runTest('配货单 PDF 打印路径应使用分页 PDF 且不再直接打印 HTML 页面', () => {
  const pickingListSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/PickingList.tsx'), 'utf8')
  assertEqual(pickingListSource.includes('printElementPagesAsPdf'), true, '打印按钮应走分页 PDF 打印')
  assertEqual(pickingListSource.includes('downloadElementPagesAsPdf'), true, '下载按钮应走分页 PDF 下载')
  assertEqual(pickingListSource.includes('window.print()'), false, '配货单不应再直接调用浏览器 HTML 打印')
})

function readCssRule(source: string, selector: string) {
  const escapedSelector = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const match = source.match(new RegExp(`${escapedSelector}\\s*\\{([\\s\\S]*?)\\}`))
  return match?.[1] ?? ''
}

function readCssWidth(rule: string) {
  return Number(rule.match(/width:\s*(\d+(?:\.\d+)?)%/)?.[1] ?? Number.NaN)
}

function readCssNumber(rule: string, property: string) {
  return Number(rule.match(new RegExp(`${property}:\\s*(\\d+(?:\\.\\d+)?)`))?.[1] ?? Number.NaN)
}

runTest('配货单页头应将店名和单号同一行居中放大显示', () => {
  const pickingListSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/PickingList.tsx'), 'utf8')
  const printCssSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/print.css'), 'utf8')
  const headerSource = pickingListSource.slice(
    pickingListSource.indexOf('const renderPickingHeader'),
    pickingListSource.indexOf('return (', pickingListSource.indexOf('const renderPickingHeader')),
  )
  const headerRule = readCssRule(printCssSource, '.store-order-picking-header')
  const primaryRule = readCssRule(printCssSource, '.store-order-picking-primary')
  const primaryLineRule = readCssRule(printCssSource, '.store-order-picking-primary-line')
  const storeRule = readCssRule(printCssSource, '.store-order-picking-store')
  const orderNoRule = readCssRule(printCssSource, '.store-order-picking-order-no')
  const metaRule = readCssRule(printCssSource, '.store-order-picking-meta')

  assertEqual(headerSource.includes('className="store-order-picking-primary-line"'), true, '页头应有店名和单号同一行主信息容器')
  assertEqual(headerSource.includes('className="store-order-picking-store"'), true, '店名应挂载主字号样式')
  assertEqual(headerSource.includes('{displayStoreText}'), true, '主信息行应显示店名')
  assertEqual(headerSource.includes('className="store-order-picking-order-no"'), true, '单号应挂载主字号样式')
  assertEqual(headerSource.includes('className="store-order-picking-meta"'), true, '页头右栏应保留辅助信息容器（订单条码）')
  assertEqual(headerSource.includes('className="store-order-picking-order-date"'), true, '订货日期应移到标题下方的左栏')
  assertEqual(headerSource.includes("t('warehouse.pickingList.printTime')"), false, '页头不应继续显示打印时间')
  assertEqual(headerSource.includes("t('warehouse.pickingList.orderDate')"), true, '页头应显示订货日期')
  assertEqual(headerSource.includes('formatPrintDate(order.orderDate, false, printLocale)'), true, '页头应格式化订单日期')
  assertEqual(headerSource.includes('formatPrintDate(undefined, true, printLocale)'), false, '页头不应继续计算打印时间')
  assertEqual(printCssSource.includes('.store-order-picking-meta'), true, '打印样式应保留右栏辅助信息样式')
  assertEqual(headerSource.includes("t('warehouse.pickingList.orderNoLabel')"), false, '主信息行不应继续显示订单号文字标签')
  assertEqual(headerSource.includes('#{orderNoText}'), true, '主信息行应使用 # 前缀显示单号')
  assertEqual(headerSource.includes("t('warehouse.pickingList.storeLabel')"), false, '店名不应继续作为右侧小号元数据显示')
  assertEqual(/grid-template-columns:\s*minmax\(80px,\s*1fr\)\s*minmax\(0,\s*2fr\)\s*minmax\(max-content,\s*1fr\)/.test(headerRule), true, '页头应使用三栏布局承载居中主信息，右栏至少容纳订单条码（五位流水号时条码也不溢出右栏）')
  assertEqual(/text-align:\s*center/.test(primaryRule), true, '主信息区应居中')
  assertEqual(/display:\s*inline-flex/.test(primaryLineRule), true, '店名和单号应水平排列')
  assertEqual(/white-space:\s*nowrap/.test(primaryLineRule), false, '主信息行不应强制店名和单号整体单行显示')
  assertEqual(/gap:\s*18px/.test(primaryLineRule), true, '店名和单号之间应有清晰间距')
  assertEqual(/display:\s*-webkit-box/.test(storeRule), true, '店名应启用多行截断容器')
  assertEqual(/white-space:\s*normal/.test(storeRule), true, '店名应允许自动换行')
  assertEqual(/-webkit-line-clamp:\s*2/.test(storeRule), true, '店名最多显示两行')
  assertEqual(/-webkit-box-orient:\s*vertical/.test(storeRule), true, '店名两行截断应使用纵向 box')
  assertEqual(/text-overflow:\s*ellipsis/.test(storeRule), false, '店名不应继续使用单行省略逻辑')
  assertEqual(readCssNumber(storeRule, 'font-size'), 22, '店名字号应放大到 22px')
  assertEqual(readCssNumber(orderNoRule, 'font-size'), 28, '单号字号应放大到 28px')
  assertEqual(/flex:\s*0\s+0\s+auto/.test(orderNoRule), true, '单号不应被长店名挤压收缩')
  assertEqual(/font-weight:\s*800/.test(orderNoRule), true, '单号应加粗突出显示')
  assertEqual(readCssNumber(storeRule, 'font-size') > readCssNumber(metaRule, 'font-size'), true, '店名字号应大于右侧辅助信息')
  assertEqual(/flex-direction:\s*column/.test(metaRule), true, '右侧辅助信息应保持单列显示')
  assertEqual(/align-items:\s*flex-end/.test(metaRule), true, '右侧辅助信息应右对齐')
})

runTest('配货单页头右上角应渲染 SVG 订单条码且不额外占用表头行', () => {
  const pickingListSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/PickingList.tsx'), 'utf8')
  const printCssSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/print.css'), 'utf8')
  const zhLocale = JSON.parse(fs.readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/zh.json'), 'utf8'))
  const enLocale = JSON.parse(fs.readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/en.json'), 'utf8'))
  const headerStart = pickingListSource.indexOf('const renderPickingHeader')
  const headerSource = pickingListSource.slice(headerStart, pickingListSource.indexOf('return (', headerStart))
  const metaSource = headerSource.slice(headerSource.indexOf('className="store-order-picking-meta"'))
  const headingRule = readCssRule(printCssSource, '.store-order-picking-heading')
  const barcodeBarsRule = readCssRule(printCssSource, '.store-order-picking-barcode-bars')
  const captionRule = readCssRule(printCssSource, '.store-order-picking-barcode-caption')

  assertEqual(pickingListSource.includes('buildPickingOrderBarcode(order?.orderNo)'), true, '条码只能用真实订单号生成，不能拿 orderGUID 兜底')
  assertEqual(headerSource.includes('{orderBarcode ? ('), true, '订单号为空时页头不应渲染条码')
  assertEqual(metaSource.includes('className="store-order-picking-barcode"'), true, '条码应放在页头右栏（与标题、店名同一行右侧）')
  assertEqual(metaSource.includes('<svg'), true, '条码应用 SVG 矢量绘制，保证 html2canvas 2 倍截图仍然锐利')
  assertEqual(headerSource.includes('<canvas'), false, '页头条码不应使用 1 倍位图 canvas')
  assertEqual(metaSource.includes("{orderBarcode.value} · {t('warehouse.pickingList.scanToPick')}"), true, '条码下方应显示 HBSO:订单号 · 扫码拣货')
  assertEqual(/align-self:\s*stretch/.test(headingRule), true, '左栏应随表头行高拉伸')
  assertEqual(/justify-content:\s*space-between/.test(headingRule), true, '订货日期应与右栏条码说明文字底边对齐')
  assertEqual(/max-width:\s*none/.test(barcodeBarsRule), true, '条码不能被容器按比例压缩')
  assertEqual(/flex:\s*0 0 auto/.test(barcodeBarsRule), true, '条码应保持 1px 模块原尺寸')
  assertEqual(readCssNumber(captionRule, 'font-size'), 10, '条码说明文字应使用 10px 小字')
  assertEqual(/white-space:\s*nowrap/.test(captionRule), true, '条码说明文字应保持单行')
  assertEqual(zhLocale.warehouse.pickingList.scanToPick, '扫码拣货', '中文条码说明应存在')
  assertEqual(enLocale.warehouse.pickingList.scanToPick, 'Scan to pick', '英文条码说明应存在')
})

runTest('配货单打印行高和字体应按 9mm 明细行稳定分页', () => {
  const printCssSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/print.css'), 'utf8')
  const tableRule = readCssRule(printCssSource, '.store-order-picking-table')
  const rowRule = readCssRule(printCssSource, '.store-order-picking-table tr')
  const bodyRowRule = readCssRule(printCssSource, '.store-order-picking-table tbody tr')
  const bodyCellRule = readCssRule(printCssSource, '.store-order-picking-table td')
  const headerCellRule = readCssRule(printCssSource, '.store-order-picking-table th')
  const indexRule = readCssRule(printCssSource, '.store-order-picking-table .col-index')
  const itemRule = readCssRule(printCssSource, '.store-order-picking-table .col-item')
  const locationRule = readCssRule(printCssSource, '.store-order-picking-table .col-location')
  const productRule = readCssRule(printCssSource, '.store-order-picking-table .col-product')
  const priceRule = readCssRule(printCssSource, '.store-order-picking-table .col-price')
  const innerPackRule = readCssRule(printCssSource, '.store-order-picking-table .col-inner-pack')
  const quantityRule = readCssRule(printCssSource, '.store-order-picking-table .col-qty')
  const headerTopBorderRule = readCssRule(printCssSource, '.store-order-picking-table thead tr:last-child th')
  const firstColumnBorderRule = readCssRule(printCssSource, '.store-order-picking-table th:first-child,\n.store-order-picking-table td:first-child')
  const zebraRule = readCssRule(printCssSource, '.store-order-picking-table tbody tr:nth-child(even) td')

  assertEqual(readCssNumber(tableRule, 'font-size'), 15, '表格基础字体应为 15px')
  assertEqual(readCssNumber(tableRule, 'line-height'), 1.35, '表格基础行高应约为 1.35')
  assertEqual(/border-collapse:\s*separate/.test(tableRule), true, '配货单表格应使用 separate 边框避免内线叠加变粗')
  assertEqual(/border-spacing:\s*0/.test(tableRule), true, '配货单表格 separate 边框应保持无间距')
  assertEqual(/print-color-adjust:\s*exact/.test(tableRule), true, '表格打印应保留背景色')
  assertEqual(/-webkit-print-color-adjust:\s*exact/.test(tableRule), true, '表格打印应兼容 Chromium 背景色保留')
  assertEqual(/break-inside:\s*avoid-page/.test(rowRule), true, '行分页应使用 avoid-page 防止行中间切断')
  assertEqual(/break-inside:\s*avoid/.test(rowRule), true, '行分页应保留通用 break-inside 兼容规则')
  assertEqual(/page-break-inside:\s*avoid/.test(rowRule), true, '行分页应保留旧版 page-break-inside 兼容规则')
  assertEqual(/height:\s*9mm/.test(bodyRowRule), true, '明细行应固定为 9mm 高')
  assertEqual(/height:\s*9mm/.test(bodyCellRule), true, '明细单元格应固定为 9mm 高')
  assertEqual(/box-sizing:\s*border-box/.test(bodyCellRule), true, '明细单元格固定高度应包含边框和内边距')
  assertEqual(/padding:\s*0\.7mm 0\.6mm/.test(bodyCellRule), true, '明细单元格 padding 应适配 9mm 行高')
  assertEqual(/padding:\s*0\.7mm 0\.6mm/.test(headerCellRule), true, '表头单元格 padding 应适配打印行高')
  assertEqual(/text-align:\s*center/.test(bodyCellRule), true, '明细单元格文本应水平居中')
  assertEqual(/text-align:\s*center/.test(headerCellRule), true, '表头单元格文本应水平居中')
  assertEqual(/vertical-align:\s*middle/.test(bodyCellRule), true, '明细单元格文本应垂直居中')
  assertEqual(/vertical-align:\s*middle/.test(headerCellRule), true, '表头单元格文本应垂直居中')
  assertEqual(/border:\s*1px solid #000/.test(bodyCellRule), false, '明细单元格不应使用四边 border，避免内框线叠加变粗')
  assertEqual(/border:\s*1px solid #000/.test(headerCellRule), false, '表头单元格不应使用四边 border，避免内框线叠加变粗')
  assertEqual(/border-right:\s*1px solid #000/.test(bodyCellRule), true, '明细单元格应绘制右侧黑色实线')
  assertEqual(/border-bottom:\s*1px solid #000/.test(bodyCellRule), true, '明细单元格应绘制底部黑色实线')
  assertEqual(/border-right:\s*1px solid #000/.test(headerCellRule), true, '表头单元格应绘制右侧黑色实线')
  assertEqual(/border-bottom:\s*1px solid #000/.test(headerCellRule), true, '表头单元格应绘制底部黑色实线')
  assertEqual(/border-top:\s*1px solid #000/.test(headerTopBorderRule), true, '列头行应补顶部黑色实线作为表格上外框')
  assertEqual(/border-left:\s*1px solid #000/.test(firstColumnBorderRule), true, '第一列应补左侧黑色实线作为表格左外框')
  assertEqual(readCssNumber(indexRule, 'font-size'), 14.5, '行号字体应为 14.5px')
  assertEqual(readCssNumber(itemRule, 'font-size'), 14.5, '货号字体应为 14.5px')
  assertEqual(readCssNumber(locationRule, 'font-size'), 14.5, '货位字体应为 14.5px')
  assertEqual(readCssNumber(productRule, 'font-size'), 12.5, '商品名称字体应为 12.5px')
  assertEqual(/padding-left:\s*1px/.test(indexRule), true, '行号列左侧间距应压缩到 1px')
  assertEqual(/padding-right:\s*2px/.test(indexRule), true, '行号列右侧间距应压缩到 2px')
  assertEqual(/text-align:\s*right/.test(indexRule), false, '行号列不应覆盖基础居中对齐')
  assertEqual(/text-align:\s*right/.test(priceRule), false, '价格列不应覆盖基础居中对齐')
  assertEqual(/white-space:\s*nowrap/.test(indexRule), true, '三位行号不应换行')
  assertEqual(/font-weight:\s*700/.test(indexRule), true, '行号列应加粗')
  assertEqual(/font-variant-numeric:\s*tabular-nums/.test(indexRule), true, '行号应使用等宽数字稳定对齐')
  assertEqual(/font-weight:\s*700/.test(quantityRule), true, '订货数列应加粗')
  assertEqual(zebraRule, '', '明细行不应保留斑马纹背景规则')
  assertEqual(printCssSource.includes('.col-send-qty'), false, '发货数明细列应从打印样式中移除')
  for (const rule of [priceRule, innerPackRule, quantityRule]) {
    assertEqual(readCssNumber(rule, 'font-size'), 13, '数字列字体应为 13px')
  }
})

runTest('配货单打印列宽应按草图重新分配并保留 colgroup', () => {
  const printCssSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/print.css'), 'utf8')
  const pickingListSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/PickingList.tsx'), 'utf8')

  assertEqual(readCssWidth(readCssRule(printCssSource, '.store-order-picking-table .col-index')), 6, '行号列应为 6%')
  assertEqual(readCssWidth(readCssRule(printCssSource, '.store-order-picking-table .col-item')), 18, '货号列应为 18%')
  assertEqual(readCssWidth(readCssRule(printCssSource, '.store-order-picking-table .col-location')), 17, '货位列应为 17%')
  assertEqual(readCssWidth(readCssRule(printCssSource, '.store-order-picking-table .col-product')), 25, '商品名称列应为 25%')
  assertEqual(readCssWidth(readCssRule(printCssSource, '.store-order-picking-table .col-price')), 7, '价格列应为 7%')
  assertEqual(readCssWidth(readCssRule(printCssSource, '.store-order-picking-table .col-inner-pack')), 6.5, '包数列应为 6.5%')
  assertEqual(readCssWidth(readCssRule(printCssSource, '.store-order-picking-table .col-qty')), 13.5, '订货数量列应合并发货数列宽度为 13.5%')
  assertEqual(printCssSource.includes('.col-send-qty'), false, '发货数明细列样式应移除')
  assertEqual(pickingListSource.includes('<colgroup>'), true, '固定表格布局应使用 colgroup 明确列宽')
  assertDeepEqual(
    Array.from(pickingListSource.matchAll(/<col className="([^"]+)" \/>/g), (match) => match[1]).slice(0, 8),
    ['col-index', 'col-item', 'col-location', 'col-product', 'col-price', 'col-price', 'col-inner-pack', 'col-qty'],
    'colgroup 应按表头顺序定义 8 列，并包含两列价格列',
  )
  assertEqual(pickingListSource.includes('colSpan={8}'), true, '标题行应跨 8 列')
  assertEqual(pickingListSource.includes('colSpan={9}'), false, '标题行不应继续跨 9 列')
  assertEqual(pickingListSource.includes('col-send-qty'), false, '配货单不应再渲染独立发货数列')
})

runTest('配货单打印货号货位应保留间隔且名称价格区域更紧凑', () => {
  const printCssSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/print.css'), 'utf8')
  const locationRule = readCssRule(printCssSource, '.store-order-picking-table .col-location')
  const productRule = readCssRule(printCssSource, '.store-order-picking-table .col-product')
  const priceRule = readCssRule(printCssSource, '.store-order-picking-table .col-price')
  const innerPackRule = readCssRule(printCssSource, '.store-order-picking-table .col-inner-pack')
  const quantityRule = readCssRule(printCssSource, '.store-order-picking-table .col-qty')

  assertEqual(/padding-left:\s*5px/.test(locationRule), true, '货位列左侧间隔应为 5px')
  assertEqual(/padding-right:\s*5px/.test(locationRule), true, '货位列右侧间隔应为 5px')
  assertEqual(readCssWidth(productRule), 25, '商品名称列应收窄到 25%')
  assertEqual(readCssWidth(priceRule), 7, '价格列应为 7%')
  assertEqual(readCssWidth(quantityRule), 13.5, '订货数量列应吸收发货数列宽度')

  for (const rule of [priceRule, innerPackRule, quantityRule]) {
    assertEqual(/padding-left:\s*1px/.test(rule), true, '数字列左侧 padding 应压缩到 1px')
    assertEqual(/padding-right:\s*1px/.test(rule), true, '数字列右侧 padding 应压缩到 1px')
  }
})

runTest('配货单打印表头应将内包装数量显示为包数', () => {
  const pickingListSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/PickingList.tsx'), 'utf8')
  assertEqual(
    pickingListSource.includes('<th className="col-inner-pack">{t(\'warehouse.pickingList.innerPackShort\')}</th>'),
    true,
    '打印表头应使用配货单专用包数翻译',
  )
  assertEqual(
    pickingListSource.includes("innerPackCount: t('warehouse.pickingList.innerPackShort')"),
    true,
    'Excel 表头应复用配货单专用包数翻译',
  )
  assertEqual(pickingListSource.includes('<th className="col-inner-pack">包数</th>'), false, '打印表头不应硬编码“包数”')
  assertEqual(pickingListSource.includes("<th className=\"col-inner-pack\">{t('column.innerPackCount')}</th>"), false, '打印表头不应继续显示“内包装数量”翻译')
  assertEqual(pickingListSource.includes("innerPackCount: t('column.innerPackCount')"), false, 'Excel 表头不应继续显示“内包装数量”翻译')
})

runTest('配货单打印表头应将订货数量显示为订货数', () => {
  const pickingListSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/PickingList.tsx'), 'utf8')
  assertEqual(
    pickingListSource.includes('<th className="col-qty">{t(\'warehouse.pickingList.orderQtyShort\')}</th>'),
    true,
    '打印表头应使用配货单专用订货数翻译',
  )
  assertEqual(pickingListSource.includes('<th className="col-qty">订货数</th>'), false, '打印表头不应硬编码“订货数”')
  assertEqual(pickingListSource.includes("<th className=\"col-qty\">{t('column.orderQuantity')}</th>"), false, '打印表头不应继续显示“订货数量”翻译')
  assertEqual(
    pickingListSource.includes('<td className="col-qty">{formatPickingOrderQuantity(item.quantity, item.allocQuantity)}</td>'),
    true,
    '订货数单元格应使用发货数兜底显示函数',
  )
  assertEqual(
    pickingListSource.includes('{formatInnerPackCount(item.quantity, item.allocQuantity, item.minOrderQuantity)}'),
    true,
    '包数单元格应传入发货数作为兜底分子',
  )
  assertEqual(pickingListSource.includes('<th className="col-send-qty">'), false, '打印表头不应继续显示发货数列')
  assertEqual(pickingListSource.includes('<td className="col-send-qty">'), false, '明细行不应继续显示发货数单元格')
})

runTest('配货单打印短表头应包含中英文翻译', () => {
  const zhLocale = JSON.parse(fs.readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/zh.json'), 'utf8'))
  const enLocale = JSON.parse(fs.readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/en.json'), 'utf8'))

  assertEqual(zhLocale.warehouse.pickingList.innerPackShort, '包数', '中文包数短表头应存在')
  assertEqual(zhLocale.warehouse.pickingList.orderQtyShort, '订货数', '中文订货数短表头应存在')
  assertEqual(enLocale.warehouse.pickingList.innerPackShort, 'INNER Pack', '英文包数短表头应显示 INNER Pack')
  assertEqual(enLocale.warehouse.pickingList.orderQtyShort, 'Order Qty', '英文订货数短表头应存在')
})

runTest('配货单打印货号货位应单行完整显示且不能被省略隐藏', () => {
  const printCssSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/print.css'), 'utf8')
  const itemRule = readCssRule(printCssSource, '.store-order-picking-table .col-item')
  assertEqual(/padding-left:\s*10px/.test(itemRule), true, '货号列左侧间距应为 10px')
  assertEqual(/text-align:\s*center/.test(itemRule), true, '货号列应居中显示')

  for (const selector of ['.store-order-picking-table .col-item', '.store-order-picking-table .col-location']) {
    const rule = readCssRule(printCssSource, selector)
    assertEqual(rule.includes('white-space: nowrap'), true, `${selector} 应保持单行显示`)
    assertEqual(/overflow:\s*hidden/.test(rule), false, `${selector} 不应隐藏溢出内容`)
    assertEqual(/text-overflow:\s*ellipsis/.test(rule), false, `${selector} 不应使用省略号截断`)
  }
})

runTest('配货单打印商品名称应自动换行并最多显示两行', () => {
  const printCssSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/print.css'), 'utf8')
  const productRule = readCssRule(printCssSource, '.store-order-picking-table .col-product')
  const nameRule = readCssRule(printCssSource, '.store-order-picking-name')

  assertEqual(productRule.includes('white-space: normal'), true, '商品名称列应允许自动换行')
  assertEqual(/text-overflow:\s*ellipsis/.test(productRule), false, '商品名称列不应继续使用单行省略号')
  assertEqual(/display:\s*-webkit-box/.test(nameRule), true, '商品名称内容应启用两行截断容器')
  assertEqual(/text-align:\s*center/.test(nameRule), true, '商品名称内容应在单元格内水平居中')
  assertEqual(nameRule.includes('white-space: normal'), true, '商品名称内容应允许自动换行')
  assertEqual(/overflow:\s*hidden/.test(nameRule), true, '商品名称超过两行时应隐藏')
  assertEqual(/-webkit-line-clamp:\s*2/.test(nameRule), true, '商品名称最多显示两行')
  assertEqual(/-webkit-box-orient:\s*vertical/.test(nameRule), true, '商品名称两行截断应使用纵向 box')
  assertEqual(readCssNumber(nameRule, 'line-height'), 1.15, '商品名称内容行高应控制在 9mm 明细行内')
  assertEqual(/text-overflow:\s*ellipsis/.test(nameRule), false, '商品名称内容不应继续使用单行省略号')
})

runTest('配货单打印应绑定专用 A4 页面边距', () => {
  const printCssSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/print.css'), 'utf8')
  assertEqual(printCssSource.includes('@page store-order-picking'), true, '应保留配货单专用命名页面')
  assertEqual(
    /\.store-order-picking-paper\s*\{[\s\S]*?page:\s*store-order-picking/.test(printCssSource),
    true,
    '配货单纸张元素应绑定命名页面',
  )
})

runTest('配货单 PDF 页码区域应有独立底部留白', () => {
  const pickingListSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/PickingList.tsx'), 'utf8')
  const printCssSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/print.css'), 'utf8')
  const pageRule = readCssRule(printCssSource, '.store-order-pdf-page.store-order-picking-paper')
  const bodyRule = readCssRule(printCssSource, '.store-order-pdf-page-body')
  const pageNumberRule = readCssRule(printCssSource, '.store-order-pdf-page-number')

  assertEqual(/padding:\s*6mm 5mm 12mm/.test(pageRule), true, 'PDF 页面底部应预留 12mm')
  assertEqual(/padding-bottom:\s*12mm/.test(bodyRule), true, 'PDF 内容区底部应避开页码')
  assertEqual(/bottom:\s*4mm/.test(pageNumberRule), true, '页码应固定在底部留白区域内')
  assertEqual(
    pickingListSource.includes("t('warehouse.pickingList.pageNumber', { current: pageIndex + 1, total: pdfPages.length })"),
    true,
    'PDF 页码应使用配货单专用国际化文案',
  )
  assertEqual(pickingListSource.includes('第 ${pageIndex + 1} / ${pdfPages.length} 页'), false, 'PDF 页码不应硬编码中文模板')
})

runTest('配货单 PDF 页码应包含中英文翻译', () => {
  const zhLocale = JSON.parse(fs.readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/zh.json'), 'utf8'))
  const enLocale = JSON.parse(fs.readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/en.json'), 'utf8'))

  assertEqual(zhLocale.warehouse.pickingList.pageNumber, '第 {{current}} / {{total}} 页', '中文 PDF 页码应显示第 x / y 页')
  assertEqual(enLocale.warehouse.pickingList.pageNumber, 'Page {{current}} / {{total}}', '英文 PDF 页码应显示 Page x / y')
  assertEqual(zhLocale.warehouse.pickingList.layoutNotReady, '打印内容尚未准备完成，请稍后重试', '中文应提示打印布局未就绪')
  assertEqual(enLocale.warehouse.pickingList.layoutNotReady, 'The print content is not ready yet. Please try again.', '英文应提示打印布局未就绪')
})

console.log('pickingListLogic.test: ok')
