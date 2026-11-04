import {
  PDF_IMAGE_FORMAT,
  PDF_IMAGE_MIME_TYPE,
  PDF_IMAGE_QUALITY,
  buildDocumentFileName,
  buildPdfSlicePlan,
  collectElementBreakOffsets,
  formatDocumentFileDate,
  formatPrintDate,
  getCurrentReadyPdfPageElement,
  getPdfSliceImageData,
  paintPdfSlice,
  preparePdfPrintFrame,
  printPdfFrameAfterLayout,
  resolvePdfPageGeometry,
  waitForStablePdfPageElements,
} from './printUtils'
import fs from 'node:fs'
import path from 'node:path'

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualText = JSON.stringify(actual)
  const expectedText = JSON.stringify(expected)
  if (actualText !== expectedText) {
    throw new Error(`${label}。Expected: ${expectedText}, received: ${actualText}`)
  }
}

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function runTest(name: string, execute: () => void) {
  execute()
  console.log(`ok - ${name}`)
}

async function runAsyncTest(name: string, execute: () => Promise<void>) {
  await execute()
  console.log(`ok - ${name}`)
}

async function assertRejects(execute: () => Promise<unknown>, expectedMessage: string, label: string) {
  try {
    await execute()
  } catch (error) {
    assertEqual(error instanceof Error ? error.message : String(error), expectedMessage, label)
    return
  }

  throw new Error(`${label}。Expected promise to reject`)
}

// 这里锁定 PDF 切片规则，避免浮点高度和空白切片重新引入 wrong PNG signature。
runTest('切片高度应向下取整为整数像素，并完整覆盖剩余高度', () => {
  const slices = buildPdfSlicePlan(250, 104.7)
  assertDeepEqual(slices, [
    { offsetY: 0, height: 104 },
    { offsetY: 104, height: 104 },
    { offsetY: 208, height: 42 },
  ], '浮点页高应切成整数像素切片')
})

runTest('页高小于 1 像素时也不应生成空切片', () => {
  const slices = buildPdfSlicePlan(3, 0.4)
  assertDeepEqual(slices, [
    { offsetY: 0, height: 1 },
    { offsetY: 1, height: 1 },
    { offsetY: 2, height: 1 },
  ], '极小页高时应回退到 1 像素切片')
  assertEqual(slices.reduce((sum, item) => sum + item.height, 0), 3, '所有切片高度之和应等于原图高度')
})

runTest('PDF 切片应优先贴合行边界，避免把表格行切成两半', () => {
  const slices = buildPdfSlicePlan(260, 100, [30, 90, 150, 210, 260])
  assertDeepEqual(slices, [
    { offsetY: 0, height: 90 },
    { offsetY: 90, height: 60 },
    { offsetY: 150, height: 60 },
    { offsetY: 210, height: 50 },
  ], '切片结束位置应优先选择当前页内容范围内的最后一个行边界')
})

runTest('没有可用行边界时 PDF 切片应回退到标准页高', () => {
  const slices = buildPdfSlicePlan(260, 100, [140, 260])
  assertDeepEqual(slices, [
    { offsetY: 0, height: 100 },
    { offsetY: 100, height: 40 },
    { offsetY: 140, height: 100 },
    { offsetY: 240, height: 20 },
  ], '行边界不在当前页范围内时应保持前进，避免死循环')
})

runTest('PDF 避免切断偏移应从明细行、页脚和滚动高度收集', () => {
  const createElement = (top: number) => ({
    getBoundingClientRect: () => ({ top }),
  })
  const root = {
    scrollHeight: 260,
    getBoundingClientRect: () => ({ top: 10 }),
    querySelectorAll: (selector: string) => selector === 'tbody tr' ? [createElement(20), createElement(80), createElement(140)] : [],
    querySelector: (selector: string) => selector === '.footer' ? createElement(220) : null,
  } as unknown as HTMLElement

  assertDeepEqual(
    collectElementBreakOffsets(root, 'tbody tr', '.footer'),
    [10, 70, 130, 210, 260],
    '应返回相对根元素的行顶部、页脚顶部和根滚动高度',
  )
})

runTest('PDF 图片输出应锁定 JPEG 格式，避免回退到 PNG', () => {
  const calls: Array<[string, number]> = []
  const fakeCanvas = {
    toDataURL: (mimeType: string, quality: number) => {
      calls.push([mimeType, quality])
      return 'data:image/jpeg;base64,abc'
    },
  } as HTMLCanvasElement

  const imageData = getPdfSliceImageData(fakeCanvas)
  assertEqual(PDF_IMAGE_FORMAT, 'JPEG', '写入 jsPDF 的图片格式应为 JPEG')
  assertEqual(PDF_IMAGE_MIME_TYPE, 'image/jpeg', 'canvas 输出 MIME 应为 image/jpeg')
  assertEqual(PDF_IMAGE_QUALITY, 0.95, 'JPEG 质量应保持 0.95')
  assertEqual(imageData, 'data:image/jpeg;base64,abc', '应返回 canvas 输出的 JPEG data URL')
  assertDeepEqual(calls, [['image/jpeg', 0.95]], 'toDataURL 应按 JPEG 参数调用')
})

runTest('文件名日期应固定格式化为 yyyy-MM-dd', () => {
  assertEqual(formatDocumentFileDate('2026-06-04T12:30:00'), '2026-06-04', 'ISO 日期应直接提取年月日')
  assertEqual(formatDocumentFileDate('2026/6/4'), '2026-06-04', '斜杠日期也应补零')
})

runTest('date-only 发票日期显示不应受 UTC 负时区影响', () => {
  const originalTimezone = process.env.TZ
  process.env.TZ = 'America/Los_Angeles'
  try {
    assertEqual(formatPrintDate('2026-06-05', false, 'en-US'), '6/5/2026', 'date-only 字符串应按本地日期组件显示')
  } finally {
    process.env.TZ = originalTimezone
  }
})

runTest('文档文件名只有传入日期时才追加日期后缀', () => {
  const fallbackTexts = { unknownStore: 'UnknownStore', unknownOrder: 'UnknownOrder' }
  assertEqual(
    buildDocumentFileName('INVOICE', 'Bankstown', '2026-1049', 'xlsx', fallbackTexts, '2026-06-17'),
    'INVOICE_Bankstown_2026-1049_2026-06-17.xlsx',
    '发票文件名应追加 invoice 日期',
  )
  assertEqual(
    buildDocumentFileName('PICKING', 'Bankstown', '2026-1049', 'xlsx', fallbackTexts),
    'PICKING_Bankstown_2026-1049.xlsx',
    '未传日期时应保持原文件名格式',
  )
})

runTest('PDF 切片绘制应先铺白底再绘制原图切片', () => {
  const calls: unknown[] = []
  const context = {
    set fillStyle(value: string) {
      calls.push(['fillStyle', value])
    },
    fillRect: (...args: number[]) => calls.push(['fillRect', ...args]),
    drawImage: (...args: unknown[]) => calls.push(['drawImage', ...args]),
  } as unknown as CanvasRenderingContext2D
  const sourceCanvas = { width: 120, height: 300 } as HTMLCanvasElement

  paintPdfSlice(context, sourceCanvas, 120, { offsetY: 40, height: 80 })

  assertDeepEqual(
    calls,
    [
      ['fillStyle', '#ffffff'],
      ['fillRect', 0, 0, 120, 80],
      ['drawImage', sourceCanvas, 0, 40, 120, 80, 0, 0, 120, 80],
    ],
    '绘制顺序和参数应锁住白底 JPEG 切片逻辑',
  )
})

runTest('分页 PDF 应逐页渲染 A4 页面并写入 jsPDF', () => {
  const printUtilsSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/printUtils.ts'), 'utf8')
  assertEqual(printUtilsSource.includes('createPdfDocumentFromPages'), true, '应提供分页 PDF 生成函数')
  assertEqual(printUtilsSource.includes("querySelectorAll<HTMLElement>(pageSelector)"), true, '分页 PDF 应按页面选择器逐页读取 DOM')
  assertEqual(printUtilsSource.includes('waitForStablePdfPageElements'), true, '分页 PDF 应等待当前 DOM 布局稳定后再截图')
  assertEqual(printUtilsSource.includes('getCurrentReadyPdfPageElement'), true, '分页 PDF 每页截图前后应重新取得当前 DOM 节点')
  assertEqual(printUtilsSource.includes('currentPageElement !== pageElement'), true, '截图期间页面节点被替换时应停止生成 PDF')
  assertEqual(printUtilsSource.includes('pdf.addPage()'), true, '分页 PDF 多页时应追加 PDF 页面')
  assertEqual(printUtilsSource.includes('pdf.addImage(imageData, PDF_IMAGE_FORMAT, 0, 0, geometry.widthMm, geometry.heightMm)'), true, '每个分页应按纸张尺寸写入 PDF（不指定纸张 = A4 纵向 210 × 297）')
  assertEqual(printUtilsSource.includes("new jsPDF(geometry.orientation, 'mm', geometry.paper)"), true, 'jsPDF 的方向与纸张取自纸张规格')
})

runTest('纸张规格：不指定 = A4 纵向（订单 / 拣货单既有行为），陈列图另有 A3 横向 / A4 横向 / A4 纵向', () => {
  assertDeepEqual(resolvePdfPageGeometry(), { orientation: 'p', paper: 'a4', widthMm: 210, heightMm: 297, aspectRatio: 297 / 210 }, '默认 A4 纵向')
  assertDeepEqual(resolvePdfPageGeometry({ paper: 'a3', orientation: 'landscape' }), { orientation: 'l', paper: 'a3', widthMm: 420, heightMm: 297, aspectRatio: 297 / 420 }, 'A3 横向')
  assertDeepEqual(resolvePdfPageGeometry({ paper: 'a4', orientation: 'landscape' }), { orientation: 'l', paper: 'a4', widthMm: 297, heightMm: 210, aspectRatio: 210 / 297 }, 'A4 横向')
  assertDeepEqual(resolvePdfPageGeometry({ paper: 'a4', orientation: 'portrait' }), { orientation: 'p', paper: 'a4', widthMm: 210, heightMm: 297, aspectRatio: 297 / 210 }, 'A4 纵向')
})

await runAsyncTest('横向纸张的页面按对应宽高比判定布局就绪，A4 纵向的老调用方不受影响', async () => {
  const landscape = {
    isConnected: true,
    scrollWidth: 1218,
    scrollHeight: 861,
    getBoundingClientRect: () => ({ width: 1218, height: 861 }),
    querySelectorAll: () => Array.from({ length: 3 }),
  } as unknown as HTMLElement
  const root = { isConnected: true, querySelectorAll: () => [landscape] } as unknown as HTMLElement
  const pages = await waitForStablePdfPageElements(root, { pageFormat: { paper: 'a3', orientation: 'landscape' }, waitForFrame: async () => {}, maxFrames: 3 })
  assertEqual(pages[0], landscape, 'A3 横向页面应通过布局检查')
  // 不传纸张规格仍按 A4 纵向判断：横向页面应被拒绝（老行为）
  await assertRejects(
    () => waitForStablePdfPageElements(root, { layoutNotReadyErrorMessage: '布局未就绪', waitForFrame: async () => {}, maxFrames: 3 }),
    '布局未就绪',
    '默认 A4 纵向时横向页面不应通过',
  )
  assertEqual(
    getCurrentReadyPdfPageElement(root, '.x', 0, 1, '布局未就绪', { paper: 'a4', orientation: 'landscape' }),
    landscape,
    '逐页取节点时同样按指定纸张判断',
  )
})

await runAsyncTest('分页 PDF 应等待连续两帧布局一致后返回当前页面节点', async () => {
  let frameIndex = -1
  const widths = [780, 794, 794]
  const rowCounts = [25, 26, 26]
  const page = {
    isConnected: true,
    get scrollWidth() {
      return widths[Math.max(0, frameIndex)]
    },
    scrollHeight: 1123,
    getBoundingClientRect: () => ({
      width: widths[Math.max(0, frameIndex)],
      height: 1123,
    }),
    querySelectorAll: () => Array.from({ length: rowCounts[Math.max(0, frameIndex)] }),
  } as unknown as HTMLElement
  const root = {
    isConnected: true,
    querySelectorAll: () => [page],
  } as unknown as HTMLElement

  const pages = await waitForStablePdfPageElements(root, {
    maxFrames: 6,
    waitForFrame: async () => {
      frameIndex += 1
    },
  })

  assertEqual(frameIndex + 1, 3, '布局变化后应再等待一帧确认稳定')
  assertEqual(pages[0], page, '应返回稳定后的当前页面节点')
})

runTest('分页 PDF 应按索引重新取得当前页面节点并拒绝页数变化', () => {
  const createPage = () => ({
    isConnected: true,
    scrollWidth: 794,
    scrollHeight: 1123,
    getBoundingClientRect: () => ({ width: 794, height: 1123 }),
    querySelectorAll: () => Array.from({ length: 26 }),
  } as unknown as HTMLElement)
  const firstPage = createPage()
  const replacementPage = createPage()
  let currentPages = [firstPage]
  const root = {
    isConnected: true,
    querySelectorAll: () => currentPages,
  } as unknown as HTMLElement

  assertEqual(
    getCurrentReadyPdfPageElement(root, '.store-order-pdf-page', 0, 1, '布局未就绪'),
    firstPage,
    '首次应取得当前页面节点',
  )
  currentPages = [replacementPage]
  assertEqual(
    getCurrentReadyPdfPageElement(root, '.store-order-pdf-page', 0, 1, '布局未就绪'),
    replacementPage,
    'React 替换页面后应重新取得新节点，不能继续持有旧引用',
  )
  currentPages = [replacementPage, createPage()]
  try {
    getCurrentReadyPdfPageElement(root, '.store-order-pdf-page', 0, 1, '布局未就绪')
  } catch (error) {
    assertEqual(error instanceof Error ? error.message : String(error), '布局未就绪', '页数变化应返回布局未就绪错误')
    return
  }

  throw new Error('页数变化时应拒绝继续生成 PDF')
})

await runAsyncTest('分页 PDF 遇到断开或零尺寸节点时应停止生成', async () => {
  const errorMessage = '打印内容尚未准备完成'
  const disconnectedRoot = {
    isConnected: false,
    querySelectorAll: () => [],
  } as unknown as HTMLElement
  await assertRejects(
    () => waitForStablePdfPageElements(disconnectedRoot, {
      layoutNotReadyErrorMessage: errorMessage,
      waitForFrame: async () => {},
    }),
    errorMessage,
    '打印根节点断开时应返回布局未就绪错误',
  )

  const zeroSizePage = {
    isConnected: true,
    scrollWidth: 0,
    scrollHeight: 0,
    getBoundingClientRect: () => ({ width: 0, height: 0 }),
    querySelectorAll: () => [],
  } as unknown as HTMLElement
  const zeroSizeRoot = {
    isConnected: true,
    querySelectorAll: () => [zeroSizePage],
  } as unknown as HTMLElement
  await assertRejects(
    () => waitForStablePdfPageElements(zeroSizeRoot, {
      layoutNotReadyErrorMessage: errorMessage,
      maxFrames: 2,
      waitForFrame: async () => {},
    }),
    errorMessage,
    '零尺寸页面在限定帧数内未恢复时应返回布局未就绪错误',
  )
})

await runAsyncTest('PDF 打印 iframe 应使用屏幕外 A4 尺寸并等待两帧再打印', async () => {
  const events: string[] = []
  const frame = {
    style: {},
    contentWindow: {
      focus: () => events.push('focus'),
      print: () => events.push('print'),
    },
  } as unknown as HTMLIFrameElement

  preparePdfPrintFrame(frame, 'blob:test')
  assertEqual(frame.style.left, '-10000px', '打印 iframe 应移出可视区域')
  assertEqual(frame.style.width, '210mm', '打印 iframe 应使用非零 A4 宽度')
  assertEqual(frame.style.height, '297mm', '打印 iframe 应使用非零 A4 高度')
  assertEqual(frame.src, 'blob:test', '打印 iframe 应加载临时 PDF URL')

  await printPdfFrameAfterLayout(frame, async () => {
    events.push('frame')
  })
  assertDeepEqual(events, ['frame', 'frame', 'focus', 'print'], 'PDF viewer 应得到两帧布局时间后再触发打印')
})

runTest('分页 PDF 打印应使用临时 Blob URL 并清理资源', () => {
  const printUtilsSource = fs.readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/printUtils.ts'), 'utf8')
  const printFunctionSource = printUtilsSource.slice(printUtilsSource.indexOf('export async function printElementPagesAsPdf'))
  assertEqual(printUtilsSource.includes("pdf.output('blob')"), true, '打印 PDF 应输出 Blob')
  assertEqual(printUtilsSource.includes('URL.createObjectURL(blob)'), true, '打印 PDF 应创建临时 URL')
  assertEqual(printUtilsSource.includes('printPdfFrameAfterLayout'), true, '打印 PDF 应等待 iframe 布局稳定后再打印')
  assertEqual(printUtilsSource.includes("addEventListener('afterprint'"), true, '打印完成后应通过 afterprint 清理 iframe')
  assertEqual(printUtilsSource.includes('URL.revokeObjectURL(url)'), true, '打印完成后应清理临时 URL')
  assertEqual(printUtilsSource.includes('frame.remove()'), true, '打印完成后应移除临时 iframe')
  assertEqual(
    printFunctionSource.includes(
      "void printPdfFrameAfterLayout(frame)\n      .then(() => {\n        if (!cleaned) {\n          cleanupTimer = window.setTimeout(cleanup, 60_000)",
    ),
    true,
    '超时清理应在触发打印后才开始计时，避免 PDF 加载或打印对话框停留期间提前移除 iframe',
  )
})

console.log('printUtils.test: ok')
