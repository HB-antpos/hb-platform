import { createElementPagesPdfBlob, downloadElementPagesAsPdf, printElementPagesAsPdf } from '../../../Warehouse/StoreOrders/printUtils'
import { PDF_PAGE_SELECTOR, type PdfPaperSpec } from './sheetLogic'

/**
 * 商品陈列图 PDF 出图：把已经渲染好的页面 DOM（.planogram-pdf-page）交给分店订单 printUtils 的分页 PDF 能力
 * （html2canvas 2× → jsPDF，按纸张规格逐页写入；html2canvas / jspdf 在 printUtils 里动态 import，不进首屏，也不新增依赖）。
 */

export interface GeneratePdfArgs {
  /** 装着全部页面的容器（已经挂到 document 里，页面渲染完成）。 */
  stage: HTMLElement
  paper: PdfPaperSpec
  layoutNotReadyErrorMessage: string
  createCanvasContextErrorMessage: string
}

function pagedOptions({ paper, layoutNotReadyErrorMessage, createCanvasContextErrorMessage }: GeneratePdfArgs) {
  return {
    pageSelector: PDF_PAGE_SELECTOR,
    pageFormat: paper.format,
    layoutNotReadyErrorMessage,
    createCanvasContextErrorMessage,
  }
}

/** 等两帧布局 + 字体就绪，再截图（图片已在 SVG 里是 data URL，不需要再等网络）。 */
export async function waitForPrintStageReady(): Promise<void> {
  const nextFrame = () => new Promise<void>((resolve) => window.requestAnimationFrame(() => resolve()))
  await nextFrame()
  await nextFrame()
  try {
    await document.fonts?.ready
  } catch {
    // 个别浏览器没有字体就绪 Promise：忽略，按已有字体出图
  }
}

export async function downloadPlanogramPdf(args: GeneratePdfArgs & { fileName: string }): Promise<void> {
  await downloadElementPagesAsPdf(args.stage, args.fileName, pagedOptions(args))
}

export async function printPlanogramPdf(args: GeneratePdfArgs): Promise<void> {
  await printElementPagesAsPdf(args.stage, pagedOptions(args))
}

/** 输出 PDF Blob（不下载 / 不打印）：自动化验证用。 */
export async function createPlanogramPdfBlob(args: GeneratePdfArgs): Promise<Blob> {
  return createElementPagesPdfBlob(args.stage, pagedOptions(args))
}
