const PUBLIC_APP_PATHS = new Set([
  '/login',
  '/privacy/browser-extension',
  '/privacy/mobile',
  '/minor-employment/sign',
  '/support/hb-supplier-order',
  '/support/faq',
])

export function isPublicAppPath(pathname: string) {
  const normalizedPath = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname
  return (
    PUBLIC_APP_PATHS.has(normalizedPath) ||
    normalizedPath.startsWith('/minor-employment/guardian/') ||
    // 客人扫码打开的在线小票：/r/<令牌>，免登录。
    normalizedPath.startsWith('/r/')
  )
}
