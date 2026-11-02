import type { PosReceiptDocument } from '../types/posmSalesOrder'

/**
 * 客人扫码打开的公开小票。
 * 这里刻意不走 utils/request：它在非 2xx 时会把请求 URL 上报到中心日志，而 URL 里就是分享令牌（等同凭据）；
 * 也不能带登录态 cookie，更不能让 401 触发跳转登录。令牌不存在 / 过期 / 被撤销统一是 404，返回 null。
 */
export async function fetchPublicReceipt(token: string, signal?: AbortSignal): Promise<PosReceiptDocument | null> {
  const baseUrl = (import.meta.env.VITE_API_BASE_URL || '').trim()
  const response = await fetch(`${baseUrl}/api/public/pos-receipts/${encodeURIComponent(token)}`, {
    credentials: 'omit',
    referrerPolicy: 'no-referrer',
    cache: 'no-store',
    signal,
  })
  if (response.status === 404) {
    return null
  }
  if (!response.ok) {
    throw new Error(`Failed to load receipt (${response.status})`)
  }
  return (await response.json()) as PosReceiptDocument
}
