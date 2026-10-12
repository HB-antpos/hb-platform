import type { FaceCaptureImageMeta, FaceCaptureImageRect } from '../../../types/faceCamera'
import { RequestError } from '../../../utils/request'

// 抓拍图片查看的纯逻辑：行内按钮判定、错误分类、人脸框坐标换算、objectURL 生命周期、按需逐张加载会话。
// 图片是敏感的生物识别信息，这里的约束（由 sourceContract / imageLogic 测试守住）：
// 1. 只在用户点击「查看图片」打开弹框后才请求，请求按顺序逐张发出，没有任何预加载或缩略图列表；
// 2. 图片字节只以 Blob → objectURL 的形式存在，关闭 / 切换 / 卸载（dispose）时一律 revoke 并清空引用；
// 3. 不写任何浏览器存储、地址栏、控制台。

// ───────────── 行内入口 ─────────────

export type RowImageAction = 'none' | 'view' | 'local'

export function isCosImage(image: Pick<FaceCaptureImageMeta, 'storage'>) {
  return image.storage === 'cos'
}

/**
 * 抓拍行的图片入口：
 * - 后端没返回 canViewImages=true（无权限）或该行没有图片元数据：不显示任何入口；
 * - 至少一张在 COS：显示「查看图片」按钮；
 * - 图片都还在接收服务本地（storage=local）：显示灰色「图片在本地」，不可点击。
 */
export function getRowImageAction(
  canViewImages: boolean,
  images: ReadonlyArray<Pick<FaceCaptureImageMeta, 'storage'>>,
): RowImageAction {
  if (!canViewImages || images.length === 0) return 'none'
  return images.some(isCosImage) ? 'view' : 'local'
}

// ───────────── 错误分类 ─────────────

export const FACE_IMAGE_ERROR_CODES = [
  'FACE_IMAGE_FORBIDDEN',
  'FACE_IMAGE_NOT_FOUND',
  'FACE_IMAGE_NOT_IN_COS',
  'FACE_IMAGE_OBJECT_MISSING',
  'FACE_IMAGE_STORE_NOT_CONFIGURED',
  'FACE_IMAGE_STORE_DENIED',
  'FACE_IMAGE_STORE_TIMEOUT',
  'FACE_IMAGE_STORE_ERROR',
  'FACE_IMAGE_TOO_LARGE',
  'FACE_IMAGE_TYPE_UNSUPPORTED',
] as const

export type FaceImageErrorCode = (typeof FACE_IMAGE_ERROR_CODES)[number]

/** 页面上区分展示的几类失败：未配置 / 图片在本地 / 无权限 / 登录过期 / 图片不存在 / 其余读取失败。 */
export type FaceImageErrorKind = 'notConfigured' | 'local' | 'forbidden' | 'unauthorized' | 'missing' | 'failed'

/** 文案键写成字面量映射而不是模板拼接：契约测试按字面量扫描键。 */
export const FACE_IMAGE_ERROR_MESSAGE_KEYS: Record<FaceImageErrorKind, string> = {
  notConfigured: 'faceCameras.images.errors.notConfigured',
  local: 'faceCameras.images.errors.local',
  forbidden: 'faceCameras.images.errors.forbidden',
  unauthorized: 'faceCameras.images.errors.unauthorized',
  missing: 'faceCameras.images.errors.missing',
  failed: 'faceCameras.images.errors.failed',
}

/** 只读取统一信封里的 errorCode / code；不读取、不返回响应体的其他内容。 */
export function getFaceImageErrorCode(error: unknown): FaceImageErrorCode | null {
  if (!(error instanceof RequestError)) return null
  const payload = error.payload
  if (!payload || typeof payload !== 'object') return null
  const raw = payload as Record<string, unknown>
  const code = raw.errorCode ?? raw.code
  return FACE_IMAGE_ERROR_CODES.find((known) => known === code) ?? null
}

export function classifyFaceImageError(error: unknown): FaceImageErrorKind {
  const code = getFaceImageErrorCode(error)
  if (code === 'FACE_IMAGE_STORE_NOT_CONFIGURED') return 'notConfigured'
  if (code === 'FACE_IMAGE_NOT_IN_COS') return 'local'
  if (code === 'FACE_IMAGE_FORBIDDEN') return 'forbidden'
  if (code === 'FACE_IMAGE_NOT_FOUND' || code === 'FACE_IMAGE_OBJECT_MISSING') return 'missing'
  if (error instanceof RequestError) {
    if (error.status === 403) return 'forbidden'
    if (error.status === 401) return 'unauthorized'
    if (error.status === 404) return 'missing'
  }
  return 'failed'
}

/** 这几类失败对同一张抓拍里的其余图片也必然成立，不必再逐张请求。 */
export function isSharedFailure(kind: FaceImageErrorKind) {
  return kind === 'notConfigured' || kind === 'forbidden' || kind === 'unauthorized'
}

// ───────────── 图片分组 ─────────────

export interface ImageGroups {
  backgrounds: FaceCaptureImageMeta[]
  faces: FaceCaptureImageMeta[]
  others: FaceCaptureImageMeta[]
}

/** 按种类分组：背景图（kind=1）、人脸图（kind=0，按人脸序号排）、其他。 */
export function groupImages(images: ReadonlyArray<FaceCaptureImageMeta>): ImageGroups {
  const faces = images.filter((image) => image.kind === 0)
  faces.sort((a, b) => (a.faceIndex ?? Number.MAX_SAFE_INTEGER) - (b.faceIndex ?? Number.MAX_SAFE_INTEGER))
  return {
    backgrounds: images.filter((image) => image.kind === 1),
    faces,
    others: images.filter((image) => image.kind !== 0 && image.kind !== 1),
  }
}

/** 请求顺序：背景图 → 人脸图 → 其他；只含存放在 COS 的图片（本地图片后台读不了，不发请求）。 */
export function getLoadOrder(images: ReadonlyArray<FaceCaptureImageMeta>): FaceCaptureImageMeta[] {
  const { backgrounds, faces, others } = groupImages(images)
  return [...backgrounds, ...faces, ...others].filter(isCosImage)
}

// ───────────── 人脸框坐标换算 ─────────────

export interface ImageSize {
  width: number
  height: number
}

/** 相对背景图的百分比定位（CSS 绝对定位用，不读取像素，不用 canvas）。 */
export interface BoxStyle {
  left: string
  top: string
  width: string
  height: string
}

export interface FaceBox {
  key: string
  /** 显示在框角上的人脸序号（从 1 开始）。 */
  label: number
  style: BoxStyle
}

function clamp01(value: number) {
  return Math.min(1, Math.max(0, value))
}

function toPercent(fraction: number) {
  return `${Math.round(fraction * 1_000_000) / 10_000}%`
}

function isPositiveSize(size: ImageSize | null | undefined): size is ImageSize {
  return !!size && Number.isFinite(size.width) && Number.isFinite(size.height) && size.width > 0 && size.height > 0
}

/**
 * 把人脸框换算成相对背景图的百分比：
 * - rectNormalized 为 true：数值本身就是 0~1 的比例；
 * - 否则按像素处理：除以背景图原始宽高；没有可用的宽高就画不出框，返回 null（宁可不画也不画错位置）。
 * 结果夹在图片范围内；宽或高不是正数、数值非有限数时返回 null。
 */
export function computeFaceBox(
  rect: FaceCaptureImageRect | null | undefined,
  rectNormalized: boolean | null | undefined,
  reference: ImageSize | null | undefined,
): BoxStyle | null {
  if (!rect) return null
  const values = [rect.x, rect.y, rect.w, rect.h]
  if (!values.every((value) => Number.isFinite(value))) return null
  let x = rect.x
  let y = rect.y
  let w = rect.w
  let h = rect.h
  if (rectNormalized !== true) {
    if (!isPositiveSize(reference)) return null
    x /= reference.width
    y /= reference.height
    w /= reference.width
    h /= reference.height
  }
  const left = clamp01(x)
  const top = clamp01(y)
  const width = Math.min(clamp01(x + w), 1) - left
  const height = Math.min(clamp01(y + h), 1) - top
  if (!(width > 0) || !(height > 0)) return null
  return { left: toPercent(left), top: toPercent(top), width: toPercent(width), height: toPercent(height) }
}

/**
 * 背景图的参照尺寸：优先用后端记录的原始宽高；没有时用图片加载后浏览器报告的自然尺寸。
 */
export function pickReferenceSize(
  background: Pick<FaceCaptureImageMeta, 'width' | 'height'> | null | undefined,
  natural: ImageSize | null | undefined,
): ImageSize | null {
  if (background && background.width && background.height) {
    const size = { width: background.width, height: background.height }
    if (isPositiveSize(size)) return size
  }
  return isPositiveSize(natural) ? natural : null
}

/** 背景图上要画的人脸框：取所有带 rect 的人脸图 / 背景图记录（人脸框坐标都是相对背景图的）。 */
export function collectFaceBoxes(
  images: ReadonlyArray<FaceCaptureImageMeta>,
  reference: ImageSize | null | undefined,
): FaceBox[] {
  const boxes: FaceBox[] = []
  for (const image of images) {
    if ((image.kind !== 0 && image.kind !== 1) || !image.rect) continue
    const style = computeFaceBox(image.rect, image.rectNormalized, reference)
    if (style) boxes.push({ key: image.imageId, label: (image.faceIndex ?? 0) + 1, style })
  }
  return boxes
}

// ───────────── objectURL 生命周期 ─────────────

export interface ObjectUrlApi {
  createObjectURL: (blob: Blob) => string
  revokeObjectURL: (url: string) => void
}

function browserUrlApi(): ObjectUrlApi {
  return {
    createObjectURL: (blob) => URL.createObjectURL(blob),
    revokeObjectURL: (url) => URL.revokeObjectURL(url),
  }
}

/**
 * objectURL 登记表：每个 key（imageId）至多一个存活的 URL；
 * 同 key 再登记会先 revoke 旧的；dispose 后拒绝再登记（返回 null），避免关闭后才返回的响应泄漏 URL。
 */
export function createObjectUrlRegistry(api: ObjectUrlApi = browserUrlApi()) {
  const urls = new Map<string, string>()
  let disposed = false

  function release(key: string) {
    const url = urls.get(key)
    if (url === undefined) return
    urls.delete(key)
    api.revokeObjectURL(url)
  }

  function releaseAll() {
    for (const key of [...urls.keys()]) release(key)
  }

  return {
    set(key: string, blob: Blob): string | null {
      if (disposed) return null
      release(key)
      const url = api.createObjectURL(blob)
      urls.set(key, url)
      return url
    },
    release,
    releaseAll,
    dispose() {
      disposed = true
      releaseAll()
    },
    liveCount: () => urls.size,
  }
}

// ───────────── 按需逐张加载会话 ─────────────

export type ImageLoadState =
  | { status: 'loading' }
  | { status: 'loaded'; url: string }
  | { status: 'local' }
  | { status: 'error'; kind: FaceImageErrorKind }

export type ImageLoadStates = ReadonlyMap<string, ImageLoadState>

export interface ImageSession {
  /** 开始加载；按顺序逐张请求，全部结束（或 dispose）后返回。 */
  start: () => Promise<void>
  /** 关闭 / 切换 / 卸载：中止在途请求，revoke 全部 objectURL，清空状态，此后不再回调。 */
  dispose: () => void
  getStates: () => ImageLoadStates
}

export interface ImageSessionOptions {
  images: ReadonlyArray<FaceCaptureImageMeta>
  fetchImage: (image: FaceCaptureImageMeta, signal: AbortSignal) => Promise<Blob>
  onChange: (states: ImageLoadStates) => void
  urlApi?: ObjectUrlApi
}

/**
 * 一次「查看图片」弹框对应一个会话。图片按 getLoadOrder 的顺序逐张请求（不并发、不预取）；
 * storage 为 local 的图片标记为「在本地」且不发请求；
 * 遇到未配置 / 无权限 / 登录过期这类对其余图片同样成立的失败，剩余图片直接标记同样的失败，不再逐张重试。
 */
export function createImageSession(options: ImageSessionOptions): ImageSession {
  const registry = createObjectUrlRegistry(options.urlApi)
  const controller = new AbortController()
  let disposed = false
  let states = new Map<string, ImageLoadState>()

  function update(imageId: string, state: ImageLoadState) {
    if (disposed) return
    states = new Map(states).set(imageId, state)
    options.onChange(states)
  }

  async function start() {
    if (disposed) return
    const order = getLoadOrder(options.images)
    const initial = new Map<string, ImageLoadState>()
    for (const image of options.images) {
      initial.set(image.imageId, isCosImage(image) ? { status: 'loading' } : { status: 'local' })
    }
    states = initial
    options.onChange(states)

    let sharedFailure: FaceImageErrorKind | null = null
    for (const image of order) {
      if (disposed) return
      if (sharedFailure) {
        update(image.imageId, { status: 'error', kind: sharedFailure })
        continue
      }
      try {
        const blob = await options.fetchImage(image, controller.signal)
        // 关闭后才返回的响应：registry 已 dispose，拿不到 URL，blob 随之丢弃。
        const url = registry.set(image.imageId, blob)
        if (disposed || url === null) return
        update(image.imageId, { status: 'loaded', url })
      } catch (error) {
        if (disposed || controller.signal.aborted) return
        const kind = classifyFaceImageError(error)
        if (isSharedFailure(kind)) sharedFailure = kind
        update(image.imageId, { status: 'error', kind })
      }
    }
  }

  return {
    start,
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

/** 弹框标题里显示的抓拍编号前 8 位（完整编号没有展示价值）。 */
export function shortCaptureId(captureId: string) {
  return captureId.slice(0, 8)
}
