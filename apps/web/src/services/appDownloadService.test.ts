import assert from 'node:assert/strict'

import { fetchIosAppStoreUrl } from './appDownloadService'

// 下载短链页读 iOS 商店链接：匿名接口、不带登录态，只接受 https 链接。
const originalFetch = globalThis.fetch
const calls: Array<{ url: string; init?: RequestInit }> = []
let nextStatus = 200
let nextPayload: unknown = { success: true, data: { appStoreUrl: 'https://apps.apple.com/au/app/hbgroup/id6786073002?uo=4' } }

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  calls.push({ url: String(input), init })
  return new Response(JSON.stringify(nextPayload), { status: nextStatus, headers: { 'Content-Type': 'application/json' } })
}) as typeof fetch

try {
  assert.equal(await fetchIosAppStoreUrl(), 'https://apps.apple.com/au/app/hbgroup/id6786073002?uo=4')
  assert.equal(new URL(calls[0].url, 'http://localhost').pathname, '/api/app-updates/mobile-ios')
  assert.equal(calls[0].init?.credentials, 'omit', '公开页不带登录态 cookie')
  assert.equal(calls[0].init?.cache, 'no-store')

  // 兼容没有 data 信封的写法。
  nextPayload = { appStoreUrl: 'https://apps.apple.com/au/app/id1' }
  assert.equal(await fetchIosAppStoreUrl(), 'https://apps.apple.com/au/app/id1')

  // 链接缺失 / 不是 https：抛错，页面显示重试，不会自动跳转到奇怪的地址。
  nextPayload = { success: true, data: { appStoreUrl: '' } }
  await assert.rejects(() => fetchIosAppStoreUrl())
  nextPayload = { success: true, data: { appStoreUrl: 'javascript:alert(1)' } }
  await assert.rejects(() => fetchIosAppStoreUrl())

  nextStatus = 500
  nextPayload = { success: false }
  await assert.rejects(() => fetchIosAppStoreUrl())
} finally {
  globalThis.fetch = originalFetch
}

console.log('appDownloadService tests passed')
