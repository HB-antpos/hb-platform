/**
 * 员工 App 下载短链页（/app）的纯逻辑：判断设备、拼下载地址、校验 App Store 链接。
 * 短信里永远只放 https://hotbargain.vip/app 一个链接，由这个页面按设备分流到最新版。
 */

export type AppPlatform = 'ios' | 'android' | 'desktop'

/** navigator 里判断设备要用的字段；单独声明方便测试传入假对象。 */
export interface NavigatorLike {
  userAgent?: string
  platform?: string
  maxTouchPoints?: number
}

/**
 * iPhone / iPad 判断：
 * - UA 含 iPhone 或 iPad；
 * - iPadOS 13 起默认伪装成桌面 Safari（UA 是 Macintosh，platform 为 MacIntel），只能靠触摸点区分：真正的 Mac 没有触摸屏。
 * Android 看 UA；其余（电脑）显示二维码页。
 */
export function detectAppPlatform(nav: NavigatorLike): AppPlatform {
  const userAgent = nav.userAgent ?? ''
  if (/iPhone|iPad/i.test(userAgent)) return 'ios'
  if (nav.platform === 'MacIntel' && (nav.maxTouchPoints ?? 0) > 1) return 'ios'
  if (/Android/i.test(userAgent)) return 'android'
  return 'desktop'
}

/** 安卓下载地址：服务端 302 到最新 APK，不用跟着版本改。 */
export const ANDROID_DOWNLOAD_PATH = '/api/mobile-app-builds/android-latest/download?profile=production'

/** 拼安卓下载的绝对地址（二维码里必须是绝对地址）；配置了 API 域名就用它，否则用当前站点。 */
export function buildAndroidDownloadUrl(apiBaseUrl: string | undefined, origin: string): string {
  const base = (apiBaseUrl ?? '').trim() || origin
  return `${base.replace(/\/+$/, '')}${ANDROID_DOWNLOAD_PATH}`
}

/**
 * 只接受 https 的 App Store 链接：页面会自动 location.replace 过去，
 * 不能让 javascript: / data: 之类的值经由接口进入跳转。取不到合法值返回 null。
 */
export function parseAppStoreUrl(value: unknown): string | null {
  if (typeof value !== 'string') return null
  const trimmed = value.trim()
  if (!trimmed) return null
  try {
    const url = new URL(trimmed)
    return url.protocol === 'https:' ? url.toString() : null
  } catch {
    return null
  }
}
