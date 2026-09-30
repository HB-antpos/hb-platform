import { Image } from 'antd'
import type { CSSProperties } from 'react'
import { memo } from 'react'

import { useProductImageVersion } from '../hooks/useProductImageVersion'
import { toProductImagePreviewUrl, toProductThumbnailUrl } from '../utils/productImageThumbnail'

interface ProductListImageProps {
  /** 数据库里存的原图地址。 */
  src: string
  size: number
  fit?: CSSProperties['objectFit']
  radius?: number
  className?: string
  fallback?: string
  /** 预览遮罩文案；传空串隐藏遮罩文字。 */
  previewMask?: string
}

/**
 * 商品列表的图片单元格：
 * - 表格内显示 COS 缩略图，单张从 100KB 以上降到约 2KB；
 * - loading="lazy" 让虚拟滚动之外、尚未进入视口的图片不抢占带宽；
 * - 点开预览时才加载原图，保证放大查看的清晰度；
 * - COS 上换过的图带版本参数，避免缩略图 30 天强缓存继续显示旧图。
 */
function ProductListImage({ src, size, fit = 'cover', radius, className, fallback, previewMask }: ProductListImageProps) {
  // 首次渲染还没有版本号，先按原地址显示；查到该图最近换过后再切到带版本号的地址。
  const version = useProductImageVersion(src)

  const thumbnail = toProductThumbnailUrl(src, undefined, version) ?? src
  const previewSrc = toProductImagePreviewUrl(src, version)
  const preview = previewMask === undefined ? { src: previewSrc } : { src: previewSrc, mask: previewMask }

  return (
    <Image
      className={className}
      src={thumbnail}
      alt=""
      width={size}
      height={size}
      loading="lazy"
      decoding="async"
      style={{ objectFit: fit, ...(radius === undefined ? {} : { borderRadius: radius }) }}
      preview={preview}
      fallback={fallback}
    />
  )
}

export default memo(ProductListImage)
