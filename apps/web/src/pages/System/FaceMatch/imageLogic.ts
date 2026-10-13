import { RequestError } from '../../../utils/request'
import { createObjectUrlRegistry, type ObjectUrlApi } from '../FaceCameras/imageLogic'
import { getFaceMatchErrorCode } from './logic'

// 复核图片与登记照预览的纯逻辑（objectURL 登记表直接复用 FaceCameras/imageLogic，不复制代码）。
// 图片是敏感的生物识别信息，这里的约束（由 sourceContract / imageLogic 测试守住）：
// 1. 只在用户打开复核弹框后才请求，现场抓拍与参考照片按需逐张取，没有预加载列表或缩略图；
// 2. 图片字节只以 Blob → objectURL 的形式存在，关闭 / 切换 / 卸载（dispose）时一律 revoke 并清空引用；
// 3. 不写任何浏览器存储、地址栏、控制台；<img src> 永远是 objectURL，不是后端地址。

// ───────────── 复核图片错误分类 ─────────────

/** 页面上区分展示的几类失败：未配置 / 无权限 / 登录过期 / 图片已过期删除 / 图片不存在 / 其余读取失败。 */
export type ReviewImageErrorKind = 'notConfigured' | 'forbidden' | 'unauthorized' | 'gone' | 'missing' | 'failed'

/** 文案键写成字面量映射而不是模板拼接：契约测试按字面量扫描键。 */
export const REVIEW_IMAGE_ERROR_KEYS: Record<ReviewImageErrorKind, string> = {
  notConfigured: 'faceMatch.review.imageErrors.notConfigured',
  forbidden: 'faceMatch.review.imageErrors.forbidden',
  unauthorized: 'faceMatch.review.imageErrors.unauthorized',
  gone: 'faceMatch.review.imageErrors.gone',
  missing: 'faceMatch.review.imageErrors.missing',
  failed: 'faceMatch.review.imageErrors.failed',
}

/** 只看错误码与 HTTP 状态码，不读取、不返回响应体的其他内容。 */
export function classifyReviewImageError(error: unknown): ReviewImageErrorKind {
  const code = getFaceMatchErrorCode(error)
  // 后端真实错误码：私有桶未配置 / 功能未开启 → 未配置；越权（含门店范围外）→ 无权限；
  // IMAGE_GONE = 命中图或参考照片已清理或不存在；NOT_FOUND = 任务不存在或越权共用。
  // 图片存储拒绝 / 超时 / 出错、图片过大 / 格式不支持、审计写入失败都归入「读取失败」（单张失败，不影响别的图片）。
  if (code === 'FACE_MATCH_STORE_NOT_CONFIGURED' || code === 'FACE_MATCH_DISABLED') return 'notConfigured'
  if (code === 'FACE_MATCH_FORBIDDEN' || code === 'FACE_MATCH_STORE_FORBIDDEN') return 'forbidden'
  if (code === 'FACE_MATCH_IMAGE_GONE') return 'gone'
  if (code === 'FACE_MATCH_NOT_FOUND') return 'missing'
  if (error instanceof RequestError) {
    if (error.status === 403) return 'forbidden'
    if (error.status === 401) return 'unauthorized'
    if (error.status === 410) return 'gone'
    if (error.status === 404) return 'missing'
  }
  return 'failed'
}

/** 这几类失败对同一任务的其余图片也必然成立，不必再逐张请求（单张被清理 / 不存在不算：其余图片可能还在）。 */
export function isSharedImageFailure(kind: ReviewImageErrorKind) {
  return kind === 'notConfigured' || kind === 'forbidden' || kind === 'unauthorized'
}

// ───────────── 复核图片会话 ─────────────

export const CAPTURE_SLOT = 'capture'

export function referenceSlot(index: number) {
  return `reference-${index}`
}

export type ReviewImageState =
  | { status: 'loading' }
  | { status: 'loaded'; url: string }
  | { status: 'error'; kind: ReviewImageErrorKind }

export type ReviewImageStates = ReadonlyMap<string, ReviewImageState>

export interface ReviewImageSession {
  /** 开始加载：先现场抓拍，再第一张参考照片（其余参考照片在切换时才取）。 */
  start: () => Promise<void>
  /** 按需加载某张参考照片；已加载 / 加载中的不重复请求。 */
  loadReference: (index: number) => Promise<void>
  /** 关闭 / 切换 / 卸载：中止在途请求，revoke 全部 objectURL，清空状态，此后不再回调。 */
  dispose: () => void
  getStates: () => ReviewImageStates
}

export interface ReviewImageSessionOptions {
  referenceCount: number
  fetchCapture: (signal: AbortSignal) => Promise<Blob>
  fetchReference: (index: number, signal: AbortSignal) => Promise<Blob>
  onChange: (states: ReviewImageStates) => void
  urlApi?: ObjectUrlApi
}

/**
 * 一次「复核弹框」对应一个会话。图片按需逐张请求；遇到未配置 / 无权限 / 登录过期 / 已过期这类对其余图片
 * 同样成立的失败，后续图片直接标记同样的失败，不再逐张重试。
 */
export function createReviewImageSession(options: ReviewImageSessionOptions): ReviewImageSession {
  const registry = createObjectUrlRegistry(options.urlApi)
  const controller = new AbortController()
  let disposed = false
  let states = new Map<string, ReviewImageState>()
  let sharedFailure: ReviewImageErrorKind | null = null
  const requested = new Set<string>()

  function update(slot: string, state: ReviewImageState) {
    if (disposed) return
    states = new Map(states).set(slot, state)
    options.onChange(states)
  }

  async function load(slot: string, fetcher: (signal: AbortSignal) => Promise<Blob>) {
    if (disposed || requested.has(slot)) return
    requested.add(slot)
    if (sharedFailure) {
      update(slot, { status: 'error', kind: sharedFailure })
      return
    }
    update(slot, { status: 'loading' })
    try {
      const blob = await fetcher(controller.signal)
      // 关闭后才返回的响应：registry 已 dispose，拿不到 URL，blob 随之丢弃。
      const url = registry.set(slot, blob)
      if (disposed || url === null) return
      update(slot, { status: 'loaded', url })
    } catch (error) {
      if (disposed || controller.signal.aborted) return
      const kind = classifyReviewImageError(error)
      if (isSharedImageFailure(kind)) sharedFailure = kind
      update(slot, { status: 'error', kind })
    }
  }

  function loadReference(index: number) {
    if (!Number.isInteger(index) || index < 0 || index >= options.referenceCount) return Promise.resolve()
    return load(referenceSlot(index), (signal) => options.fetchReference(index, signal))
  }

  async function start() {
    await load(CAPTURE_SLOT, options.fetchCapture)
    if (options.referenceCount > 0) await loadReference(0)
  }

  return {
    start,
    loadReference,
    getStates: () => states,
    dispose() {
      if (disposed) return
      disposed = true
      controller.abort()
      registry.dispose()
      states = new Map()
    },
  }
}

// ───────────── 登记照 / 名单照片上传预览 ─────────────

export interface PreviewItem {
  uid: string
  file: Blob
}

export interface PreviewManager {
  /** 按当前文件列表同步：新文件创建 objectURL，已移除的立即 revoke；返回 uid → URL。 */
  sync: (items: ReadonlyArray<PreviewItem>) => ReadonlyMap<string, string>
  /** 卸载：revoke 全部并拒绝再登记。 */
  dispose: () => void
  liveCount: () => number
}

/**
 * 待上传照片的本地预览：只用 objectURL（不转 dataURL、不读像素），移除一张或卸载时撤销对应 URL。
 * 与复核图片一样复用 createObjectUrlRegistry，objectURL 的创建与撤销只出现在登记表里。
 */
export function createPreviewManager(urlApi?: ObjectUrlApi): PreviewManager {
  const registry = createObjectUrlRegistry(urlApi)
  const urls = new Map<string, string>()
  return {
    sync(items) {
      const live = new Set(items.map((item) => item.uid))
      for (const uid of [...urls.keys()]) {
        if (!live.has(uid)) {
          registry.release(uid)
          urls.delete(uid)
        }
      }
      for (const item of items) {
        if (urls.has(item.uid)) continue
        const url = registry.set(item.uid, item.file)
        if (url !== null) urls.set(item.uid, url)
      }
      return new Map(urls)
    },
    dispose() {
      registry.dispose()
      urls.clear()
    },
    liveCount: () => urls.size,
  }
}

let previewSequence = 0

/** 给每个待上传文件一个稳定的临时编号（只在本页内存里用，不含文件名等内容）。 */
export function nextPreviewUid(): string {
  previewSequence += 1
  return `photo-${previewSequence}`
}
