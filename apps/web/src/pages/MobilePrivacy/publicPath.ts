const PUBLIC_APP_PATHS = new Set([
  '/login',
  '/privacy/browser-extension',
  '/privacy/mobile',
  '/minor-employment/sign',
  '/support/hb-supplier-order',
  '/support/faq',
  // 员工 App 下载短链：短信 / 邮件里的链接，免登录，按设备分流到 App Store / 最新 APK。
  '/app',
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
