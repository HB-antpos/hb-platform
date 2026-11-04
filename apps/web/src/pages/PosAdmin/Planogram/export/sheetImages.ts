/**
 * PDF 里的商品实拍图加载：勾选「商品实拍图」时，截图之前先把每张图取成 data URL（嵌进 SVG），
 * 这样 SVG 被 html2canvas 序列化成独立图片时图片还在（独立 SVG 图片不能再去加载外链），也不会污染 canvas（跨域）。
 * - 地址经 exportService.getImageDownloadCandidates 换成「同源直连 / image-proxy / 带缓存参数直连」候选，依次尝试；
 * - 每张图总共最多等 timeoutMs（默认 8 秒），超时 / 失败就放弃这张（页面回退成色块），不拖垮整个导出；
 * - 并发 concurrency 张（默认 6）同时取。
 */

export const PDF_IMAGE_TIMEOUT_MS = 8000
export const PDF_IMAGE_CONCURRENCY = 6
/** 单张图超过这个大小就不嵌（几 MB 的原图嵌进 SVG 会让截图慢得离谱）。 */
export const PDF_IMAGE_MAX_BYTES = 4 * 1024 * 1024

export interface PdfImageSource {
  productCode: string
  productImage?: string | null
}

export interface PdfImageLoadOptions {
  timeoutMs?: number
  concurrency?: number
  /** 调用方取消（关闭弹窗）：未开始的不再取，进行中的中止。 */
  signal?: AbortSignal
  /** 图片地址 → 候选下载地址（默认用 exportService.getImageDownloadCandidates，测试时注入）。 */
  candidatesOf?: (url: string) => string[] | Promise<string[]>
  fetcher?: typeof fetch
  /** Blob → data URL（默认 FileReader，测试时注入）。 */
  readBlob?: (blob: Blob) => Promise<string>
  onProgress?: (done: number, total: number) => void
}

export interface PdfImageLoadResult {
  /** 商品货号 → data URL。 */
  images: Map<string, string>
  /** 加载失败 / 超时的商品货号。 */
  failed: string[]
}

async function defaultCandidates(url: string): Promise<string[]> {
  // 动态引入：exportService 体积不小，只在勾选实拍图时才需要
  const { getImageDownloadCandidates } = await import('../../../../services/exportService')
  return getImageDownloadCandidates(url)
}

function defaultReadBlob(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onloadend = () => (typeof reader.result === 'string' ? resolve(reader.result) : reject(new Error('read failed')))
    reader.onerror = () => reject(reader.error ?? new Error('read failed'))
    reader.readAsDataURL(blob)
  })
}

/** 取一张图（总时限 timeoutMs，含所有候选地址）；失败返回 null。 */
export async function loadOnePdfImage(url: string, options: PdfImageLoadOptions = {}): Promise<string | null> {
  const timeoutMs = options.timeoutMs ?? PDF_IMAGE_TIMEOUT_MS
  const fetcher = options.fetcher ?? fetch
  const readBlob = options.readBlob ?? defaultReadBlob
  const candidates = await (options.candidatesOf ?? defaultCandidates)(url)
  if (candidates.length === 0) return null

  const deadline = Date.now() + timeoutMs
  for (const candidate of candidates) {
    const remaining = deadline - Date.now()
    if (remaining <= 0 || options.signal?.aborted) return null
    const controller = new AbortController()
    const timer = setTimeout(() => controller.abort(), remaining)
    const onOuterAbort = () => controller.abort()
    options.signal?.addEventListener('abort', onOuterAbort, { once: true })
    try {
      const response = await fetcher(candidate, { signal: controller.signal })
      if (!response.ok) continue
      const blob = await response.blob()
      if (blob.size === 0 || blob.size > PDF_IMAGE_MAX_BYTES) continue
      if (blob.type && !blob.type.startsWith('image/')) continue
      // 读 Blob 也受同一个时限约束：超时 abort 之后 reader 仍可能卡住，所以和超时竞速
      const dataUrl = await Promise.race([
        readBlob(blob),
        new Promise<never>((_resolve, reject) => controller.signal.addEventListener('abort', () => reject(new Error('timeout')), { once: true })),
      ])
      if (dataUrl) return dataUrl
    } catch {
      // 这一个候选失败（网络 / 超时 / 中止）：换下一个，时限用完就整体放弃
    } finally {
      clearTimeout(timer)
      options.signal?.removeEventListener('abort', onOuterAbort)
    }
  }
  return null
}

/** 并发取一批商品图；没有图片地址的商品直接跳过（不算失败）。 */
export async function loadPdfImages(products: readonly PdfImageSource[], options: PdfImageLoadOptions = {}): Promise<PdfImageLoadResult> {
  const targets = products.filter((product): product is PdfImageSource & { productImage: string } => Boolean(product.productImage?.trim()))
  const images = new Map<string, string>()
  const failed: string[] = []
  let cursor = 0
  let done = 0
  const concurrency = Math.max(1, Math.min(options.concurrency ?? PDF_IMAGE_CONCURRENCY, targets.length || 1))

  const worker = async () => {
    while (cursor < targets.length) {
      const target = targets[cursor]
      cursor += 1
      if (options.signal?.aborted) return
      const dataUrl = await loadOnePdfImage(target.productImage.trim(), options)
      if (dataUrl) images.set(target.productCode, dataUrl)
      else failed.push(target.productCode)
      done += 1
      options.onProgress?.(done, targets.length)
    }
  }
  await Promise.all(Array.from({ length: concurrency }, worker))
  return { images, failed }
}
