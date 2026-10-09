import { requestAdvertisementUploadSignature, uploadAdvertisementFile } from '../../../services/advertisementService'
import { toMediaSize, type MediaSize } from './orientation'

/** 封面图最大宽度（像素）：后台列表缩略图与预览弹窗封面足够清晰，JPEG 也只有几十 KB。 */
export const THUMBNAIL_MAX_WIDTH = 640
const THUMBNAIL_JPEG_QUALITY = 0.82
/** 抽帧超时：需要先解码到目标时间点，大视频给足时间但不能无限等。 */
const CAPTURE_TIMEOUT_MS = 12000

/**
 * 选抽帧时间点：取第 1 秒（避开多数视频开头的黑场/淡入），
 * 视频不足 2 秒时取一半位置；时长未知或为 0 时返回 0，表示直接取首帧。
 */
export function pickThumbnailTime(duration: number): number {
  if (!Number.isFinite(duration) || duration <= 0) return 0
  return Math.min(1, duration / 2)
}

/** 按最大宽度等比缩小；原图不超过最大宽度时保持原尺寸；尺寸非法返回 null。 */
export function computeThumbnailSize(
  width?: number | null,
  height?: number | null,
  maxWidth = THUMBNAIL_MAX_WIDTH,
): MediaSize | null {
  const size = toMediaSize(width, height)
  if (!size) return null
  if (size.width <= maxWidth) return size
  return { width: maxWidth, height: Math.max(1, Math.round((size.height * maxWidth) / size.width)) }
}

/** 封面文件名：沿用视频原名（去掉扩展名）加 -cover.jpg，方便在对象存储里对应到视频。 */
export function buildThumbnailFileName(originalFileName: string): string {
  const base = originalFileName.replace(/\.[^./\\]+$/, '').trim() || 'video'
  return `${base}-cover.jpg`
}

/**
 * 在浏览器端从本地视频文件抽一帧，返回缩小后的 JPEG（只读本地 File，不走网络，画布不会被跨域污染）。
 * 解码失败（例如浏览器不支持的编码）、超时或画布无法导出时返回 null；本函数永不抛错，不能阻断上传。
 * objectURL 与视频元素用完即释放。
 */
export function captureVideoThumbnail(file: File, timeoutMs = CAPTURE_TIMEOUT_MS): Promise<Blob | null> {
  if (typeof window === 'undefined' || typeof URL.createObjectURL !== 'function') {
    return Promise.resolve(null)
  }

  let objectUrl: string
  try {
    objectUrl = URL.createObjectURL(file)
  } catch {
    return Promise.resolve(null)
  }

  return new Promise((resolve) => {
    let settled = false
    const video = document.createElement('video')
    video.preload = 'auto'
    video.muted = true
    video.playsInline = true

    const finish = (blob: Blob | null) => {
      if (settled) return
      settled = true
      window.clearTimeout(timer)
      video.onloadedmetadata = null
      video.onloadeddata = null
      video.onseeked = null
      video.onerror = null
      video.removeAttribute('src')
      video.load()
      URL.revokeObjectURL(objectUrl)
      resolve(blob)
    }

    const timer = window.setTimeout(() => finish(null), timeoutMs)

    const drawCurrentFrame = () => {
      const size = computeThumbnailSize(video.videoWidth, video.videoHeight)
      const canvas = document.createElement('canvas')
      const context = size ? canvas.getContext('2d') : null
      if (!size || !context) {
        finish(null)
        return
      }
      canvas.width = size.width
      canvas.height = size.height
      try {
        context.drawImage(video, 0, 0, size.width, size.height)
        canvas.toBlob((blob) => finish(blob && blob.size > 0 ? blob : null), 'image/jpeg', THUMBNAIL_JPEG_QUALITY)
      } catch {
        finish(null)
      }
    }

    video.onerror = () => finish(null)
    video.onseeked = drawCurrentFrame
    video.onloadedmetadata = () => {
      const targetTime = pickThumbnailTime(video.duration)
      if (targetTime > 0) {
        // 跳到目标时间点，seeked 触发时该帧已可绘制。
        video.currentTime = targetTime
      } else {
        // 无需跳转：等首帧解码好再画。
        video.onloadeddata = drawCurrentFrame
      }
    }
    video.src = objectUrl
  })
}

/**
 * 上传视频时自动生成封面：抽帧 → 以图片身份走同一套上传签名 → 返回封面地址。
 * 任何一步失败都返回 null（由调用方提示并让用户手填），永不抛错，不影响视频本身的上传与保存。
 */
export async function createVideoThumbnailUrl(videoFile: File): Promise<string | null> {
  try {
    const blob = await captureVideoThumbnail(videoFile)
    if (!blob) return null

    const thumbnailFile = new File([blob], buildThumbnailFileName(videoFile.name), { type: 'image/jpeg' })
    const signature = await requestAdvertisementUploadSignature({
      fileName: thumbnailFile.name,
      contentType: 'image/jpeg',
      fileSize: thumbnailFile.size,
      mediaType: 'Image',
    })
    return await uploadAdvertisementFile(signature, thumbnailFile)
  } catch (error) {
    console.warn('Failed to create advertisement video thumbnail', error)
    return null
  }
}
