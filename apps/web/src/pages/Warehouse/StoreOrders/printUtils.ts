import type { StoreDto } from '../../../types/store'

export interface StorePrintInfo {
  storeName?: string
  storeCode?: string
  address?: string
  contactPhone?: string
  contactEmail?: string
}

interface DocumentFileNameFallbackTexts {
  unknownOrder: string
  unknownStore: string
}

interface DownloadPdfOptions {
  createCanvasContextErrorMessage?: string
  avoidBreakOffsets?: number[]
}

interface PagedPdfOptions {
  createCanvasContextErrorMessage?: string
  layoutNotReadyErrorMessage?: string
  pageSelector?: string
}

interface WaitForStablePdfPageElementsOptions {
  layoutNotReadyErrorMessage?: string
  maxFrames?: number
  pageSelector?: string
  waitForFrame?: () => Promise<void>
}

export const PDF_IMAGE_FORMAT = 'JPEG'
export const PDF_IMAGE_MIME_TYPE = 'image/jpeg'
export const PDF_IMAGE_QUALITY = 0.95
const A4_ASPECT_RATIO = 297 / 210
const A4_ASPECT_RATIO_TOLERANCE = 0.02
const DEFAULT_LAYOUT_STABILITY_FRAMES = 6

export interface PdfSlicePlanItem {
  offsetY: number
  height: number
}

function normalizePrintLocale(locale?: string) {
  // 打印只需要当前需求中的中英文格式，其他语种先按中文兜底，避免输出不一致。
  return locale?.toLowerCase().startsWith('en') ? 'en-US' : 'zh-CN'
}

function parseLeadingDateParts(value: string) {
  const dateParts = value.trim().match(/^(\d{4})[-/](\d{1,2})[-/](\d{1,2})/)
  if (!dateParts) {
    return null
  }

  return {
    year: Number(dateParts[1]),
    month: Number(dateParts[2]),
    day: Number(dateParts[3]),
  }
}

export function formatPrintDate(value?: string, withTime = true, locale?: string) {
  if (!withTime && typeof value === 'string') {
    const dateParts = parseLeadingDateParts(value)
    if (dateParts) {
      const localDate = new Date(dateParts.year, dateParts.month - 1, dateParts.day)
      const printLocale = normalizePrintLocale(locale)
      // date-only 字符串不能走 new Date('yyyy-MM-dd')，否则 UTC 负时区会显示成前一天。
      return localDate.toLocaleDateString(printLocale)
    }
  }

  const target = value ? new Date(value) : new Date()
  if (Number.isNaN(target.getTime())) {
    return value || '--'
  }

  const printLocale = normalizePrintLocale(locale)
  return withTime ? target.toLocaleString(printLocale, { hour12: false }) : target.toLocaleDateString(printLocale)
}

function formatDatePart(year: number, month: number, day: number) {
  return `${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`
}

export function formatDocumentFileDate(value?: string | Date) {
  if (typeof value === 'string') {
    const dateParts = parseLeadingDateParts(value)
    if (dateParts) {
      return formatDatePart(dateParts.year, dateParts.month, dateParts.day)
    }
  }

  const target = value ? new Date(value) : new Date()
  const safeTarget = Number.isNaN(target.getTime()) ? new Date() : target
  // 文件名日期固定用 yyyy-MM-dd，避免不同系统区域设置导出出不同文件名。
  return formatDatePart(safeTarget.getFullYear(), safeTarget.getMonth() + 1, safeTarget.getDate())
}

export function formatCurrency(value?: number) {
  const amount = Number(value ?? 0)
  const text = Math.abs(amount).toFixed(2)
  // 退货冲减 / 退款单金额为负数：符号写在 $ 前（-$7.50），不写成 $-7.50；不足 1 分的负数按 0 显示。
  return amount < 0 && text !== '0.00' ? `-$${text}` : `$${text}`
}

export function sanitizeFileNamePart(value: string) {
  const normalized = (value || '')
    .replace(/[\\/:*?"<>|]/g, '_')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/[\s_]+/g, '_')

  return normalized
}

export function buildDocumentFileName(
  prefix: string,
  storeName: string | undefined,
  orderNo: string | undefined,
  extension: string,
  fallbackTexts: DocumentFileNameFallbackTexts,
  datePart?: string,
) {
  // 文件名中的未知文案交给调用方注入翻译，工具函数只负责清洗与拼接。
  const unknownStoreText = fallbackTexts.unknownStore
  const unknownOrderText = fallbackTexts.unknownOrder
  const safePrefix = sanitizeFileNamePart(prefix)
  const safeStoreName = sanitizeFileNamePart(storeName || unknownStoreText)
  const safeOrderNo = sanitizeFileNamePart(orderNo || unknownOrderText)
  const safeDatePart = datePart ? sanitizeFileNamePart(datePart) : ''
  return [safePrefix, safeStoreName, safeOrderNo, safeDatePart].filter(Boolean).join('_') + `.${extension}`
}

export function resolveStorePrintInfo(storeCode?: string, store?: StoreDto | null): StorePrintInfo {
  return {
    storeName: store?.storeName || storeCode || '--',
    storeCode: storeCode || store?.storeCode,
    address: store?.address,
    contactPhone: store?.contactPhone,
    contactEmail: store?.contactEmail,
  }
}

export function buildPdfSlicePlan(imageHeight: number, pageHeightInPx: number, avoidBreakOffsets: number[] = []): PdfSlicePlanItem[] {
  const normalizedImageHeight = Math.max(0, Math.floor(imageHeight))
  if (!Number.isFinite(normalizedImageHeight) || normalizedImageHeight <= 0) {
    return []
  }

  // 切片高度必须是正整数像素，避免浮点高度造成空切片和损坏的图片数据。
  const normalizedPageHeight = Math.max(1, Math.floor(pageHeightInPx))
  const normalizedBreakOffsets = Array.from(
    new Set(
      avoidBreakOffsets
        .filter((offset) => Number.isFinite(offset))
        .map((offset) => Math.floor(offset))
        .filter((offset) => offset > 0 && offset <= normalizedImageHeight),
    ),
  ).sort((left, right) => left - right)
  const slices: PdfSlicePlanItem[] = []

  let offsetY = 0
  while (offsetY < normalizedImageHeight) {
    const defaultEndY = Math.min(offsetY + normalizedPageHeight, normalizedImageHeight)
    const candidateBreakOffsets = normalizedBreakOffsets.filter((breakOffset) => breakOffset > offsetY && breakOffset <= defaultEndY)
    const boundaryEndY = candidateBreakOffsets[candidateBreakOffsets.length - 1]
    const endY = boundaryEndY ?? defaultEndY
    const height = Math.max(1, endY - offsetY)
    slices.push({ offsetY, height })
    offsetY += height
  }

  return slices
}

export function paintPdfSlice(
  context: CanvasRenderingContext2D,
  sourceCanvas: HTMLCanvasElement,
  imageWidth: number,
  slice: PdfSlicePlanItem,
) {
  context.fillStyle = '#ffffff'
  context.fillRect(0, 0, imageWidth, slice.height)
  context.drawImage(
    sourceCanvas,
    0,
    slice.offsetY,
    imageWidth,
    slice.height,
    0,
    0,
    imageWidth,
    slice.height,
  )
}

export function getPdfSliceImageData(sliceCanvas: HTMLCanvasElement) {
  return sliceCanvas.toDataURL(PDF_IMAGE_MIME_TYPE, PDF_IMAGE_QUALITY)
}

export function collectElementBreakOffsets(root: HTMLElement, rowSelector: string, footerSelector?: string) {
  const rootTop = root.getBoundingClientRect().top
  const rows = Array.from(root.querySelectorAll<HTMLElement>(rowSelector))
  const footer = footerSelector ? root.querySelector<HTMLElement>(footerSelector) : null

  // 这些偏移会被换算到 canvas 像素，用来让 PDF 切页尽量落在完整内容块之间。
  const offsets = rows.map((row) => row.getBoundingClientRect().top - rootTop)
  if (footer) {
    offsets.push(footer.getBoundingClientRect().top - rootTop)
  }

  offsets.push(root.scrollHeight)
  return offsets.filter((offset) => Number.isFinite(offset) && offset > 0)
}

function waitForAnimationFrame() {
  return new Promise<void>((resolve) => {
    window.requestAnimationFrame(() => resolve())
  })
}

function getPdfPageElements(root: HTMLElement, pageSelector: string) {
  const pages = Array.from(root.querySelectorAll<HTMLElement>(pageSelector))
  return pages.length > 0 ? pages : [root]
}

function getPdfPageLayoutSignature(pageElements: HTMLElement[]) {
  const pageSignatures = pageElements.map((pageElement) => {
    const rect = pageElement.getBoundingClientRect()
    const aspectRatio = rect.width > 0 ? rect.height / rect.width : 0
    const hasA4Geometry =
      rect.width > 0 &&
      rect.height > 0 &&
      Math.abs(aspectRatio - A4_ASPECT_RATIO) <= A4_ASPECT_RATIO_TOLERANCE

    if (!pageElement.isConnected || !hasA4Geometry) {
      return null
    }

    const rowCount = pageElement.querySelectorAll('tbody tr').length
    return [
      Math.round(rect.width * 100) / 100,
      Math.round(rect.height * 100) / 100,
      pageElement.scrollWidth,
      pageElement.scrollHeight,
      rowCount,
    ].join(':')
  })

  return pageSignatures.every((signature): signature is string => Boolean(signature))
    ? pageSignatures.join('|')
    : null
}

export async function waitForStablePdfPageElements(
  root: HTMLElement,
  options: WaitForStablePdfPageElementsOptions = {},
) {
  const errorMessage = options.layoutNotReadyErrorMessage || '打印内容尚未准备完成，请稍后重试'
  const maxFrames = Math.max(2, Math.floor(options.maxFrames ?? DEFAULT_LAYOUT_STABILITY_FRAMES))
  const pageSelector = options.pageSelector || '.store-order-pdf-page'
  const waitForFrame = options.waitForFrame || waitForAnimationFrame
  let previousSignature: string | null = null

  if (!root.isConnected) {
    throw new Error(errorMessage)
  }

  for (let frameIndex = 0; frameIndex < maxFrames; frameIndex += 1) {
    await waitForFrame()
    if (!root.isConnected) {
      throw new Error(errorMessage)
    }

    // 每帧重新读取当前节点，避免 React/KeepAlive 更新后继续截图旧 DOM 引用。
    const pageElements = getPdfPageElements(root, pageSelector)
    const signature = getPdfPageLayoutSignature(pageElements)
    if (!signature) {
      previousSignature = null
      continue
    }

    if (signature === previousSignature) {
      return pageElements
    }
    previousSignature = signature
  }

  throw new Error(errorMessage)
}

export function getCurrentReadyPdfPageElement(
  root: HTMLElement,
  pageSelector: string,
  pageIndex: number,
  expectedPageCount: number,
  errorMessage = '打印内容尚未准备完成，请稍后重试',
) {
  if (!root.isConnected) {
    throw new Error(errorMessage)
  }

  // 按当前根节点重新查询，避免多页截图期间继续使用 React/KeepAlive 已替换的节点。
  const currentPageElements = getPdfPageElements(root, pageSelector)
  const currentPageElement = currentPageElements[pageIndex]
  if (
    currentPageElements.length !== expectedPageCount ||
    !currentPageElement ||
    !getPdfPageLayoutSignature([currentPageElement])
  ) {
    throw new Error(errorMessage)
  }

  return currentPageElement
}

async function createPdfDocumentFromElement(element: HTMLElement, options?: DownloadPdfOptions) {
  const [{ default: html2canvas }, { default: jsPDF }] = await Promise.all([import('html2canvas'), import('jspdf')])
  const canvas = await html2canvas(element, {
    scale: 2,
    useCORS: true,
    logging: false,
    backgroundColor: '#ffffff',
  })

  const pdf = new jsPDF('p', 'mm', 'a4')
  const pdfWidth = 210
  const pdfHeight = 297
  const imageWidth = canvas.width
  const imageHeight = canvas.height
  const canvasScaleY = element.scrollHeight > 0 ? canvas.height / element.scrollHeight : 1
  const avoidBreakOffsets = options?.avoidBreakOffsets?.map((offset) => offset * canvasScaleY) ?? []
  const slicePlan = buildPdfSlicePlan(imageHeight, (pdfHeight * imageWidth) / pdfWidth, avoidBreakOffsets)

  slicePlan.forEach((slice, pageIndex) => {
    const sliceCanvas = document.createElement('canvas')
    sliceCanvas.width = imageWidth
    sliceCanvas.height = slice.height

    const context = sliceCanvas.getContext('2d')
    if (!context) {
      // 这里允许页面传入国际化错误文案，避免工具层写死提示语言。
      throw new Error(options?.createCanvasContextErrorMessage || '创建 PDF 临时画布失败')
    }

    paintPdfSlice(context, canvas, imageWidth, slice)

    const imageData = getPdfSliceImageData(sliceCanvas)
    if (pageIndex > 0) {
      pdf.addPage()
    }

    const imageHeightInPdf = (slice.height * pdfWidth) / imageWidth
    pdf.addImage(imageData, PDF_IMAGE_FORMAT, 0, 0, pdfWidth, imageHeightInPdf)
  })

  return pdf
}

async function createPdfDocumentFromPages(root: HTMLElement, options?: PagedPdfOptions) {
  const pageSelector = options?.pageSelector || '.store-order-pdf-page'
  const [{ default: html2canvas }, { default: jsPDF }] = await Promise.all([import('html2canvas'), import('jspdf')])
  const pageElements = await waitForStablePdfPageElements(root, {
    layoutNotReadyErrorMessage: options?.layoutNotReadyErrorMessage,
    pageSelector,
  })
  const errorMessage = options?.layoutNotReadyErrorMessage || '打印内容尚未准备完成，请稍后重试'
  const expectedPageCount = pageElements.length
  const pdf = new jsPDF('p', 'mm', 'a4')

  for (let pageIndex = 0; pageIndex < expectedPageCount; pageIndex += 1) {
    const pageElement = getCurrentReadyPdfPageElement(
      root,
      pageSelector,
      pageIndex,
      expectedPageCount,
      errorMessage,
    )
    const canvas = await html2canvas(pageElement, {
      scale: 2,
      useCORS: true,
      logging: false,
      backgroundColor: '#ffffff',
    })
    const currentPageElement = getCurrentReadyPdfPageElement(
      root,
      pageSelector,
      pageIndex,
      expectedPageCount,
      errorMessage,
    )
    if (currentPageElement !== pageElement) {
      throw new Error(errorMessage)
    }

    const context = canvas.getContext('2d')
    if (!context) {
      // 分页 PDF 逐页渲染，任何一页拿不到画布上下文都要中断，避免输出坏 PDF。
      throw new Error(options?.createCanvasContextErrorMessage || '创建 PDF 临时画布失败')
    }

    if (pageIndex > 0) {
      pdf.addPage()
    }

    const imageData = getPdfSliceImageData(canvas)
    pdf.addImage(imageData, PDF_IMAGE_FORMAT, 0, 0, 210, 297)
  }

  return pdf
}

export function preparePdfPrintFrame(frame: HTMLIFrameElement, url: string) {
  frame.style.position = 'fixed'
  frame.style.left = '-10000px'
  frame.style.top = '0'
  frame.style.width = '210mm'
  frame.style.height = '297mm'
  frame.style.border = '0'
  frame.style.opacity = '0'
  frame.style.pointerEvents = 'none'
  frame.src = url
}

export async function printPdfFrameAfterLayout(
  frame: HTMLIFrameElement,
  waitForFrame: () => Promise<void> = waitForAnimationFrame,
) {
  await waitForFrame()
  await waitForFrame()

  // PDF viewer 需要先在非零 iframe 内完成布局，Edge 首次打开预览才不会使用未完成的页面几何。
  frame.contentWindow?.focus()
  frame.contentWindow?.print()
}

export async function createElementPdfBase64(element: HTMLElement, options?: DownloadPdfOptions) {
  const pdf = await createPdfDocumentFromElement(element, options)
  const pdfDataUri = pdf.output('datauristring') as string
  const separatorIndex = pdfDataUri.indexOf(',')
  return separatorIndex >= 0 ? pdfDataUri.slice(separatorIndex + 1) : pdfDataUri
}

export async function downloadElementAsPdf(element: HTMLElement, fileName: string, options?: DownloadPdfOptions) {
  const pdf = await createPdfDocumentFromElement(element, options)

  pdf.save(fileName)
}

export async function downloadElementPagesAsPdf(element: HTMLElement, fileName: string, options?: PagedPdfOptions) {
  const pdf = await createPdfDocumentFromPages(element, options)

  pdf.save(fileName)
}

export async function printElementPagesAsPdf(element: HTMLElement, options?: PagedPdfOptions) {
  const pdf = await createPdfDocumentFromPages(element, options)
  const blob = pdf.output('blob') as Blob
  const url = URL.createObjectURL(blob)
  const frame = document.createElement('iframe')
  let cleaned = false
  let cleanupTimer: number | undefined
  const cleanup = () => {
    if (cleaned) {
      return
    }
    cleaned = true
    if (cleanupTimer !== undefined) {
      window.clearTimeout(cleanupTimer)
    }
    URL.revokeObjectURL(url)
    frame.remove()
  }

  preparePdfPrintFrame(frame, url)
  frame.onload = () => {
    const printWindow = frame.contentWindow
    if (!printWindow) {
      cleanup()
      return
    }

    printWindow.addEventListener('afterprint', cleanup, { once: true })
    // 打印临时 PDF 而不是当前 HTML，避开浏览器自动 URL、标题和日期页脚。
    void printPdfFrameAfterLayout(frame)
      .then(() => {
        if (!cleaned) {
          cleanupTimer = window.setTimeout(cleanup, 60_000)
        }
      })
      .catch((error) => {
        console.error(error)
        cleanup()
      })
  }

  // afterprint 在部分 Chromium 版本中可能不触发；真正触发打印后再启动资源回收兜底。
  document.body.appendChild(frame)
}
