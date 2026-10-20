import request from '../utils/request'

// 后端 /api/react/v1/product-descriptions/from-images 单次最多 20 条（每条都要看图，耗时远高于文字翻译）。
export const DESCRIBE_FROM_IMAGES_MAX_ITEMS = 20

export type ProductDescriptionSource = 'image' | 'text' | 'failed'

export interface ProductImageDescriptionItem {
  /** 前端自定的行标识（hguid、行 key 等），结果按它回填 */
  key: string
  imageUrl?: string | null
  /** 中文商品名：看图时作参考，没图或看图失败时后端用它走文字翻译 */
  chineseName?: string | null
}

export interface ProductImageDescriptionResult {
  key: string
  description?: string | null
  source: ProductDescriptionSource
  reason?: string | null
}

export interface ProductImageDescriptionSummary {
  /** 按 key 索引的结果；失败条目也在内（description 为空） */
  results: Record<string, ProductImageDescriptionResult>
  imageCount: number
  textCount: number
  failedCount: number
}

interface DescribeResponse {
  success?: boolean
  message?: string
  data?: {
    results?: ProductImageDescriptionResult[]
  }
}

/**
 * 看图生成英文商品名。超过单次上限时按 20 条一批串行请求再合并，
 * 每批完成后回调进度，便于按钮显示「已完成 x / y」。
 */
export async function describeProductsFromImages(
  items: ProductImageDescriptionItem[],
  onProgress?: (done: number, total: number) => void,
): Promise<ProductImageDescriptionSummary> {
  const summary: ProductImageDescriptionSummary = { results: {}, imageCount: 0, textCount: 0, failedCount: 0 }
  if (!items.length) {
    return summary
  }

  for (let start = 0; start < items.length; start += DESCRIBE_FROM_IMAGES_MAX_ITEMS) {
    const batch = items.slice(start, start + DESCRIBE_FROM_IMAGES_MAX_ITEMS)
    const response = await request<DescribeResponse>('/api/react/v1/product-descriptions/from-images', {
      method: 'POST',
      data: {
        items: batch.map((item) => ({
          key: item.key,
          imageUrl: item.imageUrl?.trim() || undefined,
          chineseName: item.chineseName?.trim() || undefined,
        })),
      },
    })
    if (response.success === false) {
      throw new Error(response.message || '生成英文名失败')
    }

    for (const result of response.data?.results ?? []) {
      summary.results[result.key] = result
    }
    onProgress?.(Math.min(start + batch.length, items.length), items.length)
  }

  for (const result of Object.values(summary.results)) {
    if (result.source === 'image') summary.imageCount += 1
    else if (result.source === 'text') summary.textCount += 1
    else summary.failedCount += 1
  }
  return summary
}

/** 取某个 key 的有效英文名：失败或空结果返回 undefined。 */
export function getDescribedEnglishName(summary: ProductImageDescriptionSummary, key: string): string | undefined {
  const result = summary.results[key]
  const description = result?.source === 'failed' ? undefined : result?.description?.trim()
  return description || undefined
}
