import { memo, useMemo } from 'react'
import ProductListImage from '../../../components/ProductListImage'
import { buildLetterTileDataUri, productThumbColor, productThumbLetter } from './domesticProductsLogic'

interface ProductThumbProps {
  /** 商品图地址（原图）；缺失时直接画首字方块。 */
  src?: string
  name?: string
  /** 底色种子，传货号保证同一商品永远同色。 */
  seed?: string
  size?: number
}

/**
 * 商品缩略图：
 * - 有图片时复用项目的列表缩略图组件（COS 缩略图 + 懒加载 + 版本号防旧缓存，点击可预览原图）；
 * - 没有图片，或图片加载失败（后端会按货号推演默认地址，常常 404）时，退化为「首字 + 稳定底色」方块。
 */
function ProductThumb({ src, name, seed, size = 40 }: ProductThumbProps) {
  const letter = productThumbLetter(name)
  const color = productThumbColor(seed || name)
  const fallback = useMemo(() => buildLetterTileDataUri(letter, color), [letter, color])
  const trimmed = src?.trim()

  if (!trimmed) {
    return (
      <span
        className="dp-thumb"
        style={{ width: size, height: size, background: color, fontSize: Math.round(size * 0.36) }}
        aria-hidden="true"
      >
        {letter}
      </span>
    )
  }

  return (
    <span className="dp-thumb dp-thumb-image" style={{ width: size, height: size }}>
      <ProductListImage src={trimmed} size={size} radius={8} previewMask="" fallback={fallback} />
    </span>
  )
}

export default memo(ProductThumb)
