import assert from 'node:assert/strict'

import {
  ANDROID_DOWNLOAD_PATH,
  buildAndroidDownloadUrl,
  detectAppPlatform,
  parseAppStoreUrl,
} from './appDownloadLogic'

// ===== 设备判断：iPhone / iPad / iPadOS 伪装 Mac / Android / 桌面 =====
const IPHONE_UA = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1'
const IPAD_UA = 'Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1'
const MAC_UA = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15'
const ANDROID_UA = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36'
const ANDROID_TABLET_UA = 'Mozilla/5.0 (Linux; Android 13; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36'
const WINDOWS_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36'
const LINUX_UA = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36'

assert.equal(detectAppPlatform({ userAgent: IPHONE_UA, platform: 'iPhone', maxTouchPoints: 5 }), 'ios')
assert.equal(detectAppPlatform({ userAgent: IPAD_UA, platform: 'iPad', maxTouchPoints: 5 }), 'ios')
// iPadOS 13+ 默认请求桌面网站：UA 是 Macintosh、platform 是 MacIntel，只能靠触摸点判断。
assert.equal(detectAppPlatform({ userAgent: MAC_UA, platform: 'MacIntel', maxTouchPoints: 5 }), 'ios')
// 真正的 Mac 没有触摸屏：显示二维码页，不能被当成 iPad 跳 App Store。
assert.equal(detectAppPlatform({ userAgent: MAC_UA, platform: 'MacIntel', maxTouchPoints: 0 }), 'desktop')
assert.equal(detectAppPlatform({ userAgent: MAC_UA, platform: 'MacIntel' }), 'desktop')
assert.equal(detectAppPlatform({ userAgent: ANDROID_UA, platform: 'Linux armv81', maxTouchPoints: 5 }), 'android')
assert.equal(detectAppPlatform({ userAgent: ANDROID_TABLET_UA, platform: 'Linux armv81', maxTouchPoints: 5 }), 'android')
assert.equal(detectAppPlatform({ userAgent: WINDOWS_UA, platform: 'Win32', maxTouchPoints: 0 }), 'desktop')
// 带触摸屏的 Windows 笔记本不是手机。
assert.equal(detectAppPlatform({ userAgent: WINDOWS_UA, platform: 'Win32', maxTouchPoints: 10 }), 'desktop')
assert.equal(detectAppPlatform({ userAgent: LINUX_UA, platform: 'Linux x86_64', maxTouchPoints: 0 }), 'desktop')
assert.equal(detectAppPlatform({}), 'desktop', '什么信息都没有时显示二维码页')

// ===== 安卓下载地址 =====
assert.equal(ANDROID_DOWNLOAD_PATH, '/api/mobile-app-builds/android-latest/download?profile=production')
assert.equal(
  buildAndroidDownloadUrl('', 'https://hotbargain.vip'),
  'https://hotbargain.vip/api/mobile-app-builds/android-latest/download?profile=production',
  '没有配置 API 域名时用当前站点',
)
assert.equal(
  buildAndroidDownloadUrl('https://api.example.test/', 'https://hotbargain.vip'),
  'https://api.example.test/api/mobile-app-builds/android-latest/download?profile=production',
  '配置了 API 域名就用它，并去掉末尾斜杠',
)
assert.equal(buildAndroidDownloadUrl(undefined, 'https://hotbargain.vip').startsWith('https://hotbargain.vip/api/'), true)

// ===== App Store 链接：只接受 https，不让奇怪的值进入自动跳转 =====
assert.equal(
  parseAppStoreUrl('https://apps.apple.com/au/app/hbgroup/id6786073002?uo=4'),
  'https://apps.apple.com/au/app/hbgroup/id6786073002?uo=4',
)
assert.equal(parseAppStoreUrl('  https://apps.apple.com/au/app/id1  '), 'https://apps.apple.com/au/app/id1')
assert.equal(parseAppStoreUrl('http://apps.apple.com/au/app/id1'), null)
assert.equal(parseAppStoreUrl('javascript:alert(1)'), null)
assert.equal(parseAppStoreUrl('data:text/html,<b>x</b>'), null)
assert.equal(parseAppStoreUrl('itms-apps://apps.apple.com/app/id1'), null)
assert.equal(parseAppStoreUrl(''), null)
assert.equal(parseAppStoreUrl('not a url'), null)
assert.equal(parseAppStoreUrl(null), null)
assert.equal(parseAppStoreUrl(42), null)

console.log('appDownloadLogic tests passed')
