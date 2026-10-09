import type { AdvertisementMediaType } from '../../../types/advertisement'
import { toMediaSize, type MediaSize } from './orientation'

/** 读取素材尺寸的超时：大视频的元数据可能在文件尾部，给足时间但不能无限等。 */
const READ_DIMENSIONS_TIMEOUT_MS = 8000

/**
 * 在浏览器端读取本地素材的像素宽高（只读本地 File，不走网络）：
 * - 图片：Image 的 naturalWidth / naturalHeight
 * - 视频：<video> loadedmetadata 之后的 videoWidth / videoHeight
 * 超时、解码失败或尺寸非法时返回 null；本函数永不抛错，不能阻断上传。
 * objectURL 读完即 revoke，视频元素同时清掉 src 释放解码资源。
 */
export function readMediaDimensions(
  file: File,
  mediaType: AdvertisementMediaType,
  timeoutMs = READ_DIMENSIONS_TIMEOUT_MS,
): Promise<MediaSize | null> {
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
    let releaseElement = () => {}

    const finish = (width?: number, height?: number) => {
      if (settled) return
      settled = true
      window.clearTimeout(timer)
      releaseElement()
      URL.revokeObjectURL(objectUrl)
      resolve(toMediaSize(width, height))
    }

    const timer = window.setTimeout(() => finish(), timeoutMs)

    if (mediaType === 'Video') {
      const video = document.createElement('video')
      video.preload = 'metadata'
      video.muted = true
      releaseElement = () => {
        video.onloadedmetadata = null
        video.onerror = null
        video.removeAttribute('src')
        video.load()
      }
      video.onloadedmetadata = () => finish(video.videoWidth, video.videoHeight)
      video.onerror = () => finish()
      video.src = objectUrl
      return
    }

    const image = new window.Image()
    releaseElement = () => {
      image.onload = null
      image.onerror = null
    }
    image.onload = () => finish(image.naturalWidth, image.naturalHeight)
    image.onerror = () => finish()
    image.src = objectUrl
  })
}
