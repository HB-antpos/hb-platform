// 商品图片版本号。
//
// 商品图常在 COS 上按原文件名直接覆盖（不经过本系统、数据库也不变），而 COS 缩略图响应带
// Cache-Control: max-age=2592000，浏览器会在 30 天内继续显示旧缩略图。
// 这里把当前渲染的图片地址攒成一批，问后端哪些图最近在 COS 上改过，拿到修改时间当版本号，
// 由 toProductThumbnailUrl 追加 &v= 换掉缓存键。没改过的图拿不到版本号，地址不变、照常命中缓存。

import type { ApiResponse } from '../types/api'

import { isCosImageUrl } from './productImageThumbnail'
import request, { unwrapApiData } from './request'

/** 与后端 ProductImageVersionService.MaxUrlsPerRequest 保持一致。 */
export const PRODUCT_IMAGE_VERSION_BATCH_SIZE = 200
/** 同一地址查询后多久内不再重复查询；与后端探测结果缓存时长一致。 */
export const PRODUCT_IMAGE_VERSION_TTL_MS = 10 * 60 * 1000
/** 攒批窗口：表格一次渲染几十个单元格，合成一个请求。 */
const COLLECT_DELAY_MS = 30

export type ProductImageVersionFetcher = (urls: string[]) => Promise<Record<string, string>>

const versions = new Map<string, string>()
const queriedAt = new Map<string, number>()
const pending = new Set<string>()
const listeners = new Set<() => void>()
let flushTimer: ReturnType<typeof setTimeout> | undefined
// 版本号表每次有变化加 1，供一次渲染整页图片的组件用 useSyncExternalStore 订阅。
let revision = 0
let fetcher: ProductImageVersionFetcher = defaultFetcher
let now: () => number = () => Date.now()

async function defaultFetcher(urls: string[]): Promise<Record<string, string>> {
  const response = await request.post<ApiResponse<{ versions?: Record<string, string> }>>(
    '/api/react/v1/product-images/versions',
    { urls },
  )
  return unwrapApiData(response)?.versions ?? {}
}

function notify() {
  revision += 1
  listeners.forEach((listener) => listener())
}

async function flush() {
  flushTimer = undefined
  const urls = [...pending]
  pending.clear()
  let changed = false

  for (let start = 0; start < urls.length; start += PRODUCT_IMAGE_VERSION_BATCH_SIZE) {
    const batch = urls.slice(start, start + PRODUCT_IMAGE_VERSION_BATCH_SIZE)
    try {
      const result = await fetcher(batch)
      for (const url of batch) {
        const version = result[url]
        if (version && versions.get(url) !== version) {
          versions.set(url, version)
          changed = true
        }
      }
    } catch {
      // 版本号只是缓存优化：查询失败时按原地址显示，并允许下次渲染重新查询。
      batch.forEach((url) => queriedAt.delete(url))
    }
  }

  if (changed) {
    notify()
  }
}

/** 登记一个需要版本号的图片地址；非 COS 地址与近期已查过的地址直接忽略。 */
export function requestProductImageVersion(url: string | null | undefined) {
  if (!url || !isCosImageUrl(url.trim())) {
    return
  }
  const lastQueriedAt = queriedAt.get(url)
  if (lastQueriedAt !== undefined && now() - lastQueriedAt < PRODUCT_IMAGE_VERSION_TTL_MS) {
    return
  }
  queriedAt.set(url, now())
  pending.add(url)
  flushTimer ??= setTimeout(() => void flush(), COLLECT_DELAY_MS)
}

export function getProductImageVersion(url: string | null | undefined): string | undefined {
  return url ? versions.get(url) : undefined
}

export function getProductImageVersionsRevision(): number {
  return revision
}

export function subscribeProductImageVersions(listener: () => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/** 仅供测试：替换请求函数与时钟，并清空内部状态。 */
export function resetProductImageVersionsForTest(options: {
  fetcher?: ProductImageVersionFetcher
  now?: () => number
} = {}) {
  if (flushTimer !== undefined) {
    clearTimeout(flushTimer)
    flushTimer = undefined
  }
  revision = 0
  versions.clear()
  queriedAt.clear()
  pending.clear()
  listeners.clear()
  fetcher = options.fetcher ?? defaultFetcher
  now = options.now ?? (() => Date.now())
}
