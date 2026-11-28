/**
 * 订货前台（/shop）嵌在大屏收银 App「订货」模式里的识别与约定。
 *
 * 中文注释：
 * 1. App 在页面脚本执行前写入 window.__HB_EMBED_MODE__ = 'pos-ipad'（apps/pos-ipad 的 ordering-shop-view.tsx）。
 *    站内路由跳转不重新加载页面，这个标记一直在；普通浏览器里没有它，行为不变。
 * 2. 嵌入时退出登录、进入后台、浏览器插件推广都由 App 自己处理，网页里隐藏这些入口。
 * 3. App 扫到的条码通过 POS_APP_SCAN_EVENT 事件交给网页，网页按关键字搜索。
 */
export const POS_APP_EMBED_MODE = 'pos-ipad'

/** 与 apps/pos-ipad/src/features/ordering/ordering-config.ts 的 ORDERING_SCAN_EVENT 保持一致。 */
export const POS_APP_SCAN_EVENT = 'hb-pos-app:scan'

export function isPosAppEmbed(): boolean {
  if (typeof window === 'undefined') return false
  return (window as Window & { __HB_EMBED_MODE__?: unknown }).__HB_EMBED_MODE__ === POS_APP_EMBED_MODE
}

/** 从扫码事件里取条码；不是字符串或为空时返回 null。 */
export function readPosAppScanBarcode(event: Event): string | null {
  const detail = (event as CustomEvent<unknown>).detail
  if (!detail || typeof detail !== 'object') return null
  const barcode = (detail as { barcode?: unknown }).barcode
  if (typeof barcode !== 'string') return null
  const trimmed = barcode.trim()
  return trimmed ? trimmed : null
}
