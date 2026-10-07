import assert from 'node:assert/strict'
import { parsePublishFailure } from '../pages/System/Stores/receiptProfileLogic'
import { RequestError } from '../utils/request'
import {
  getStoreReceiptProfileDevices,
  getStoreReceiptProfileStatuses,
  publishStoreReceiptProfiles,
} from './storeReceiptProfileService'

// 契约（receipt-profile-spec §2）：路由前缀 api/stores/receipt-profile；
// status / publish 为 POST + { storeGuids }（1–100），devices 为 GET /{storeGuid}/devices。

interface CapturedRequest {
  url: string
  method: string
  body: unknown
}

const originalFetch = globalThis.fetch
const captured: CapturedRequest[] = []

function respondWith(handler: (request: CapturedRequest) => { status?: number; body: unknown }) {
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const request: CapturedRequest = {
      url: String(input),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
    }
    captured.push(request)
    const { status = 200, body } = handler(request)
    return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
  }) as typeof fetch
}

function statusItem(storeGuid: string) {
  return {
    storeGuid,
    storeCode: storeGuid.toUpperCase(),
    storeName: `示例分店 ${storeGuid}`,
    status: 'never',
    latestVersion: 0,
    publishedAtUtc: null,
    publishedBy: null,
    current: { brandName: null, storeName: `示例分店 ${storeGuid}`, address: null, phone: null, abn: null, returnPolicy: null },
    latest: null,
    deviceTotal: 0,
    deviceApplied: 0,
  }
}

try {
  // status：POST 路由与请求体；先去重、去空白。
  respondWith((request) => ({
    body: { success: true, data: (request.body as { storeGuids: string[] }).storeGuids.map(statusItem) },
  }))
  const items = await getStoreReceiptProfileStatuses(['a', ' b ', 'a', ''])
  assert.equal(captured.length, 1)
  assert.equal(captured[0].method, 'POST')
  assert.equal(captured[0].url, '/api/stores/receipt-profile/status')
  assert.deepEqual(captured[0].body, { storeGuids: ['a', 'b'] }, '请求体只含字段 storeGuids，且已去重去空白')
  assert.deepEqual(items.map((item) => item.storeGuid), ['a', 'b'])

  // 空输入不发请求。
  captured.length = 0
  assert.deepEqual(await getStoreReceiptProfileStatuses([]), [])
  assert.deepEqual(await getStoreReceiptProfileStatuses(['', '  ']), [])
  assert.equal(captured.length, 0, '没有有效 guid 时不应请求后端（后端要求 1–100 个，否则 400）')

  // 超过 100 个按 100 一批请求，结果按请求顺序拼接。
  captured.length = 0
  const many = Array.from({ length: 250 }, (_, index) => `g${String(index).padStart(3, '0')}`)
  const manyItems = await getStoreReceiptProfileStatuses(many)
  assert.deepEqual(
    captured.map((request) => (request.body as { storeGuids: string[] }).storeGuids.length),
    [100, 100, 50],
    '每批不超过契约上限 100',
  )
  assert.deepEqual(manyItems.map((item) => item.storeGuid), many, '结果顺序与请求顺序一致')

  // 后端不返回已软删的门店：缺项原样透传，由调用方处理。
  captured.length = 0
  respondWith(() => ({ body: { success: true, data: [statusItem('a')] } }))
  assert.deepEqual((await getStoreReceiptProfileStatuses(['a', 'gone'])).map((item) => item.storeGuid), ['a'])

  // data 缺失时按空列表处理，页面不会因此崩溃。
  respondWith(() => ({ body: { success: true } }))
  assert.deepEqual(await getStoreReceiptProfileStatuses(['a']), [])

  // status 失败（后端未部署 404）：抛出带状态码的 RequestError，由页面降级。
  respondWith(() => ({ status: 404, body: { success: false, message: 'Not Found' } }))
  await assert.rejects(
    () => getStoreReceiptProfileStatuses(['a']),
    (error) => error instanceof RequestError && error.status === 404,
  )

  // devices：GET /{storeGuid}/devices。
  captured.length = 0
  respondWith(() => ({
    body: {
      success: true,
      data: {
        storeGuid: 'a',
        storeCode: '9001',
        latestVersion: 2,
        devices: [{
          deviceCode: 'POS_9001_0001', deviceSystem: 'Windows', clientKind: 'wpf', deviceStatus: 1,
          isOnline: true, lastHeartbeatAt: '2026-10-07T14:32:10', appliedVersion: 2, appliedAtUtc: '2026-10-07T04:33:00', upToDate: true,
        }],
      },
    },
  }))
  const devices = await getStoreReceiptProfileDevices('a/b c')
  assert.equal(captured[0].method, 'GET')
  assert.equal(captured[0].url, '/api/stores/receipt-profile/a%2Fb%20c/devices', 'storeGuid 作为路径段需编码')
  assert.equal(devices.latestVersion, 2)
  assert.equal(devices.devices[0].clientKind, 'wpf')
  respondWith(() => ({ body: { success: true, data: { storeGuid: 'a', storeCode: '9001', latestVersion: 0 } } }))
  assert.deepEqual((await getStoreReceiptProfileDevices('a')).devices, [], 'devices 缺失时回退为空数组')

  // publish：POST 路由与请求体；原子下发不在服务层拆批。
  captured.length = 0
  respondWith(() => ({
    body: {
      success: true,
      data: {
        requestedCount: 2, publishedCount: 1, unchangedCount: 1,
        items: [
          { storeGuid: 'a', storeCode: '9001', outcome: 'published', version: 3 },
          { storeGuid: 'b', storeCode: '9002', outcome: 'unchanged', version: 2 },
        ],
      },
    },
  }))
  const publishResult = await publishStoreReceiptProfiles(['a', 'b'])
  assert.equal(captured[0].method, 'POST')
  assert.equal(captured[0].url, '/api/stores/receipt-profile/publish')
  assert.deepEqual(captured[0].body, { storeGuids: ['a', 'b'] })
  assert.equal(publishResult.publishedCount, 1)
  assert.equal(publishResult.unchangedCount, 1)
  assert.equal(publishResult.items[0].outcome, 'published')

  // publish 整批失败（400）：RequestError 保留 payload，页面据此展示逐店 message。
  respondWith(() => ({
    status: 400,
    body: {
      success: false,
      message: '部分分店不可下发',
      errorCode: 'RECEIPT_PROFILE_NOT_PUBLISHABLE',
      details: [{ storeGuid: 'a', storeCode: '9001', errorCode: 'STORE_NAME_REQUIRED', message: '分店名称为空' }],
    },
  }))
  let rejectedError: unknown
  await publishStoreReceiptProfiles(['a']).catch((error) => { rejectedError = error })
  assert.ok(rejectedError instanceof RequestError)
  assert.equal(rejectedError.status, 400)
  const failure = parsePublishFailure(rejectedError)
  assert.equal(failure.kind, 'rejected')
  assert.equal(failure.items[0].message, '分店名称为空')
  assert.equal(failure.items[0].storeGuid, 'a')

  // publish 409：并发冲突。
  respondWith(() => ({
    status: 409,
    body: { success: false, message: '请重试', errorCode: 'RECEIPT_PROFILE_PUBLISH_CONFLICT' },
  }))
  let conflictError: unknown
  await publishStoreReceiptProfiles(['a']).catch((error) => { conflictError = error })
  assert.equal(parsePublishFailure(conflictError).kind, 'conflict')

  // HTTP 200 但 success=false 也必须当作失败，不能误报「已下发」。
  respondWith(() => ({ body: { success: false, errorCode: 'RECEIPT_PROFILE_NOT_PUBLISHABLE', message: '不可下发' } }))
  await assert.rejects(() => publishStoreReceiptProfiles(['a']), RequestError)
} finally {
  globalThis.fetch = originalFetch
}

console.log('storeReceiptProfileService.test: ok')
