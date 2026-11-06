import { parseAppStoreUrl } from '../pages/AppDownload/appDownloadLogic'

/**
 * 下载短链页读取 iOS 商店链接：匿名接口 GET /api/app-updates/mobile-ios 的 data.appStoreUrl。
 * 公开页不带登录态：不走 utils/request（它会带 cookie，401 还会跳登录、失败时上报请求地址）。
 * 取不到或不是合法 https 链接时抛错，页面显示重试。
 */
export async function fetchIosAppStoreUrl(signal?: AbortSignal): Promise<string> {
  const baseUrl = (import.meta.env.VITE_API_BASE_URL || '').trim()
  const response = await fetch(`${baseUrl}/api/app-updates/mobile-ios`, {
    credentials: 'omit',
    cache: 'no-store',
    signal,
  })
  if (!response.ok) {
    throw new Error(`Failed to load the App Store link (${response.status})`)
  }
  const payload = (await response.json()) as { data?: { appStoreUrl?: unknown }; appStoreUrl?: unknown } | null
  const url = parseAppStoreUrl(payload?.data?.appStoreUrl ?? payload?.appStoreUrl)
  if (!url) {
    throw new Error('The App Store link is not available')
  }
  return url
}
