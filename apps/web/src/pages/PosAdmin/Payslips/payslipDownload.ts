import { downloadMyobPayslipPdf } from '../../../services/myobPayslipService'
import type { MyobPayslipKey } from '../../../types/myobPayslip'

/** 把 Blob 交给浏览器保存（临时 a 标签 + objectURL；延迟释放，避免个别浏览器还没读完就被回收）。 */
export function saveBlobAsFile(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = filename
  document.body.appendChild(link)
  link.click()
  document.body.removeChild(link)
  window.setTimeout(() => URL.revokeObjectURL(url), 10_000)
}

/**
 * 下载工资单 PDF 并保存到本机。接口需要登录态，所以先用统一请求封装以 Blob 取回，不能用新窗口直链；
 * 文件名优先取响应头 Content-Disposition，取不到用 fallbackFilename。失败原样抛出，由调用方给提示。
 */
export async function downloadAndSavePayslipPdf(key: MyobPayslipKey, fallbackFilename: string): Promise<string> {
  const file = await downloadMyobPayslipPdf(key, fallbackFilename)
  saveBlobAsFile(file.blob, file.filename)
  return file.filename
}
