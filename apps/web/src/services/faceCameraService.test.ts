import assert from 'node:assert/strict'

import { RequestError } from '../utils/request'
import {
  createFaceCamera,
  getFaceCameraCaptures,
  getFaceCameras,
  normalizeFaceCamera,
  normalizeFaceCameraSecretResult,
  rotateFaceCameraToken,
  updateFaceCamera,
} from './faceCameraService'

// 明显的假值：不是任何真实 token。
const FAKE_TOKEN = 'f'.repeat(8) + '0'.repeat(40)
const FAKE_URL = `https://example.invalid/face-ingest/hik/v1/${FAKE_TOKEN}`

const originalFetch = globalThis.fetch
const calls: Array<{ url: string; init?: RequestInit }> = []
let responseFactory: () => Response

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  calls.push({ url: String(input), init })
  return responseFactory()
}) as typeof fetch

// 监听所有 console 输出：token 绝不能出现在任何日志里。
const consoleOutput: string[] = []
const consoleMethods = ['log', 'info', 'warn', 'error', 'debug', 'trace'] as const
const originalConsole = new Map<string, (...args: unknown[]) => void>()
for (const method of consoleMethods) {
  originalConsole.set(method, console[method])
  console[method] = (...args: unknown[]) => {
    consoleOutput.push(args.map((arg) => (typeof arg === 'string' ? arg : JSON.stringify(arg))).join(' '))
  }
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
}

const cameraPayload = {
  cameraId: 'cam-1',
  storeCode: '1008',
  storeName: 'Bankstown',
  deviceModel: 'DS-2CD',
  macAddress: null,
  note: '',
  enabled: true,
  createdAtUtc: '2026-10-12T00:00:00Z',
  updatedAtUtc: '2026-10-12T01:00:00Z',
  lastSeenAtUtc: '2026-10-12T02:00:00Z',
  online: true,
  captureCount24h: 12,
  faceCount24h: 30,
  parseFailureCount24h: 1,
  lastEventAtUtc: '2026-10-12T01:59:00Z',
}

function lastCall() {
  return calls[calls.length - 1]
}

function bodyOf(call: { init?: RequestInit }) {
  return call.init?.body === undefined ? undefined : JSON.parse(String(call.init.body))
}

try {
  // ───────── 列表：路径、方法、no-store、解包与归一化 ─────────
  responseFactory = () =>
    json({
      success: true,
      data: { items: [cameraPayload], generatedAtUtc: '2026-10-12T02:01:00Z', onlineWindowMinutes: 10, statsWindowHours: 24 },
    })
  const list = await getFaceCameras()
  assert.equal(lastCall().url, '/api/react/v1/face-cameras')
  assert.equal(lastCall().init?.method, 'GET')
  assert.equal(lastCall().init?.cache, 'no-store', '请求带 cache: no-store')
  assert.equal(list.items.length, 1)
  assert.equal(list.items[0].cameraId, 'cam-1')
  assert.equal(list.items[0].note, null, '空串备注归一化为 null')
  assert.equal(list.items[0].online, true)
  assert.equal(list.onlineWindowMinutes, 10)
  assert.equal(list.statsWindowHours, 24)

  // 缺字段兜底，不崩
  const empty = normalizeFaceCamera(undefined)
  assert.equal(empty.cameraId, '')
  assert.equal(empty.enabled, false)
  assert.equal(empty.captureCount24h, 0)

  // ───────── 新增：POST 路径与请求体，解包 token / 地址 ─────────
  responseFactory = () =>
    json({ success: true, data: { camera: cameraPayload, token: FAKE_TOKEN, cameraUrl: FAKE_URL } }, 200)
  const created = await createFaceCamera({
    cameraId: 'cam-1',
    storeCode: '1008',
    note: null,
    deviceModel: 'DS-2CD',
    macAddress: null,
  })
  assert.equal(lastCall().url, '/api/react/v1/face-cameras')
  assert.equal(lastCall().init?.method, 'POST')
  assert.equal(lastCall().init?.cache, 'no-store')
  assert.deepEqual(bodyOf(lastCall()), { cameraId: 'cam-1', storeCode: '1008', note: null, deviceModel: 'DS-2CD', macAddress: null })
  assert.equal(created.token, FAKE_TOKEN)
  assert.equal(created.cameraUrl, FAKE_URL)
  assert.equal(created.camera.cameraId, 'cam-1')

  // ───────── 编辑：PUT /{cameraId}，cameraId 编码，不在请求体里 ─────────
  responseFactory = () => json({ success: true, data: { ...cameraPayload, enabled: false } })
  const updated = await updateFaceCamera('cam-1', {
    storeCode: '1008',
    note: '门口',
    deviceModel: null,
    macAddress: null,
    enabled: false,
  })
  assert.equal(lastCall().url, '/api/react/v1/face-cameras/cam-1')
  assert.equal(lastCall().init?.method, 'PUT')
  assert.deepEqual(bodyOf(lastCall()), { storeCode: '1008', note: '门口', deviceModel: null, macAddress: null, enabled: false })
  assert.equal(updated.enabled, false)

  await updateFaceCamera('a/b c', { storeCode: '1', note: null, deviceModel: null, macAddress: null, enabled: true })
  assert.equal(lastCall().url, '/api/react/v1/face-cameras/a%2Fb%20c', 'cameraId 要做 URL 编码')

  // ───────── 轮换：POST /{cameraId}/rotate-token，无请求体 ─────────
  responseFactory = () =>
    json({ success: true, data: { camera: cameraPayload, token: FAKE_TOKEN, cameraUrl: FAKE_URL } })
  const rotated = await rotateFaceCameraToken('cam-1')
  assert.equal(lastCall().url, '/api/react/v1/face-cameras/cam-1/rotate-token')
  assert.equal(lastCall().init?.method, 'POST')
  assert.equal(lastCall().init?.body, undefined)
  assert.equal(lastCall().init?.cache, 'no-store')
  assert.equal(rotated.cameraUrl, FAKE_URL)

  // ───────── 抓拍记录：GET captures?page&pageSize ─────────
  responseFactory = () =>
    json({
      success: true,
      data: {
        items: [
          {
            captureId: 101,
            receivedAtUtc: '2026-10-12T02:00:00Z',
            cameraEventTime: '2026-10-12T13:00:00+11:00',
            eventType: 'faceSnap',
            faceCount: 2,
            parseStatus: 1,
            parseNote: 'partial',
            isDuplicate: true,
          },
          { captureId: 'x', receivedAtUtc: '2026-10-12T02:00:00Z', parseStatus: 99 },
        ],
        page: 2,
        pageSize: 20,
        hasMore: true,
      },
    })
  const abort = new AbortController()
  const captures = await getFaceCameraCaptures('cam-1', 2, 20, abort.signal)
  assert.equal(lastCall().url, '/api/react/v1/face-cameras/cam-1/captures?page=2&pageSize=20')
  assert.equal(lastCall().init?.method, 'GET')
  assert.equal(lastCall().init?.signal, abort.signal)
  assert.equal(captures.hasMore, true)
  assert.equal(captures.page, 2)
  assert.equal(captures.items[0].captureId, '101')
  assert.equal(captures.items[0].parseStatus, 1)
  assert.equal(captures.items[0].isDuplicate, true)
  assert.equal(captures.items[0].cameraEventTime, '2026-10-12T13:00:00+11:00')
  assert.equal(captures.items[1].parseStatus, 2, '未知解析状态按未解析处理')
  assert.equal(captures.items[1].eventType, null)
  assert.equal(captures.items[1].isDuplicate, false)

  // ───────── 错误：业务错误码保留在 RequestError.payload 里供页面映射 ─────────
  responseFactory = () =>
    json({ success: false, message: '摄像机编号已存在', errorCode: 'FACE_CAMERA_ID_EXISTS', data: null }, 409)
  await assert.rejects(
    () => createFaceCamera({ cameraId: 'cam-1', storeCode: '1008', note: null, deviceModel: null, macAddress: null }),
    (error: unknown) =>
      error instanceof RequestError &&
      error.status === 409 &&
      (error.payload as { errorCode?: string }).errorCode === 'FACE_CAMERA_ID_EXISTS',
  )

  // ───────── 响应缺 token / 地址：抛固定标记，错误里不含响应内容 ─────────
  assert.throws(
    () => normalizeFaceCameraSecretResult({ camera: cameraPayload, token: FAKE_TOKEN }),
    (error: unknown) =>
      error instanceof Error &&
      error.message === 'FACE_CAMERA_SECRET_RESPONSE_INVALID' &&
      !JSON.stringify(error).includes(FAKE_TOKEN) &&
      !String(error.stack).includes(FAKE_TOKEN),
  )
  assert.throws(() => normalizeFaceCameraSecretResult(null), /FACE_CAMERA_SECRET_RESPONSE_INVALID/)

  // ───────── token 不被日志输出，也不会被发往任何别的请求 ─────────
  // 新增 / 轮换成功后：没有任何 console 输出，且除上面的接口调用外没有其他网络请求（例如中心日志上报）。
  const fetchedUrls = calls.map((call) => call.url)
  assert.equal(
    fetchedUrls.every((url) => url.startsWith('/api/react/v1/face-cameras')),
    true,
    '只有 face-cameras 接口请求，没有日志上报等额外请求',
  )
  for (const call of calls) {
    assert.equal(call.url.includes(FAKE_TOKEN), false, 'token 不在 URL 里')
    assert.equal(String(call.init?.body ?? '').includes(FAKE_TOKEN), false, 'token 不会作为请求体发出')
  }
  assert.deepEqual(consoleOutput, [], 'service 层不输出任何 console 日志')
} finally {
  globalThis.fetch = originalFetch
  for (const method of consoleMethods) {
    console[method] = originalConsole.get(method) as never
  }
}

console.log('faceCameraService.test: ok')
