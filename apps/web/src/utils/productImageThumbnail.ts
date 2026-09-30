// 商品图片缩略图地址。
//
// 列表里的图片只显示 34~36px，但数据库里存的是原图地址（单张 100~150KB）。
// 腾讯云 COS 桶已开通图片处理，给地址追加 imageMogr2 参数即可由 COS 实时缩放并转 WebP，
// 实测单张降到约 2KB。供应商官网等外链图片无法处理，保持原地址。

// 腾讯云 COS 默认域名：<bucket>-<appid>.cos.<region>.myqcloud.com
const COS_HOST_PATTERN = /^[a-z0-9-]+\.cos\.[a-z0-9-]+\.myqcloud\.com$/i

/** 列表缩略图边长：按 2 倍像素密度覆盖 36px 的显示尺寸。 */
export const PRODUCT_LIST_THUMBNAIL_SIZE = 72

export function isCosImageUrl(url: string): boolean {
  try {
    const parsed = new URL(url)
    return (parsed.protocol === 'https:' || parsed.protocol === 'http:') && COS_HOST_PATTERN.test(parsed.hostname)
  } catch {
    return false
  }
}

/**
 * 返回 COS 图片的缩略图地址；非 COS 地址、已带查询参数的地址原样返回。
 * 已带查询参数时不追加：COS 要求图片处理参数位于查询串开头，拼接容易生成无效地址。
 *
 * version 是图片在 COS 上的修改时间（见 productImageVersions）。COS 缩略图响应带 30 天强缓存，
 * 图片按原文件名覆盖后浏览器仍显示旧缩略图；换过的图追加 &v= 换一个缓存键，COS 会忽略这个参数。
 */
export function toProductThumbnailUrl(
  url: string | null | undefined,
  size: number = PRODUCT_LIST_THUMBNAIL_SIZE,
  version?: string,
): string | undefined {
  const trimmed = url?.trim()
  if (!trimmed) {
    return undefined
  }
  if (trimmed.includes('?') || !isCosImageUrl(trimmed)) {
    return trimmed
  }
  const versionSuffix = version ? `&v=${encodeURIComponent(version)}` : ''
  return `${trimmed}?imageMogr2/thumbnail/${size}x${size}/format/webp${versionSuffix}`
}

/**
 * 预览原图的地址：换过的 COS 图追加 ?v=，避免浏览器按启发式缓存继续显示旧原图。
 * 没有版本号、非 COS 或已带查询参数的地址原样返回。
 */
export function toProductImagePreviewUrl(url: string, version?: string): string {
  const trimmed = url.trim()
  if (!version || trimmed.includes('?') || !isCosImageUrl(trimmed)) {
    return url
  }
  return `${trimmed}?v=${encodeURIComponent(version)}`
}
