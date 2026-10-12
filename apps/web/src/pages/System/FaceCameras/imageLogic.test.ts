import assert from 'node:assert/strict'

import type { FaceCaptureImageMeta } from '../../../types/faceCamera'
import { RequestError } from '../../../utils/request'
import {
  FACE_IMAGE_ERROR_CODES,
  FACE_IMAGE_ERROR_MESSAGE_KEYS,
  classifyFaceImageError,
  collectFaceBoxes,
  computeFaceBox,
  createImageSession,
  createObjectUrlRegistry,
  getFaceImageErrorCode,
  getLoadOrder,
  getRowImageAction,
  groupImages,
  isSharedFailure,
  pickReferenceSize,
  shortCaptureId,
  type ImageLoadStates,
  type ObjectUrlApi,
} from './imageLogic'

// 图片查看的纯逻辑测试：全部用假 URL API、假 fetch、假数据，不连任何服务，也不碰真实图片。

function image(overrides: Partial<FaceCaptureImageMeta> & { imageId: string }): FaceCaptureImageMeta {
  return {
    kind: 0,
    storage: 'cos',
    width: null,
    height: null,
    byteSize: 100,
    faceIndex: null,
    rect: null,
    rectNormalized: null,
    faceScore: null,
    ...overrides,
  }
}

/** 假的 URL.createObjectURL / revokeObjectURL：记录创建、撤销与当前存活的 URL。 */
function createFakeUrlApi() {
  let counter = 0
  const live = new Set<string>()
  const created: string[] = []
  const revoked: string[] = []
  const api: ObjectUrlApi = {
    createObjectURL: () => {
      counter += 1
      const url = `blob:fake/${counter}`
      created.push(url)
      live.add(url)
      return url
    },
    revokeObjectURL: (url) => {
      revoked.push(url)
      live.delete(url)
    },
  }
  return { api, live, created, revoked }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

const tick = () => new Promise<void>((resolve) => setTimeout(resolve, 0))
const blobOf = (text = 'x') => new Blob([text], { type: 'image/jpeg' })

function envelopeError(status: number, errorCode: string) {
  return new RequestError('固定文案', status, { success: false, errorCode })
}

// ───────── 行内入口：只有后端返回 canViewImages=true 且该行有图片元数据才显示 ─────────
assert.equal(getRowImageAction(false, [{ storage: 'cos' }]), 'none', '无权限：不显示任何入口')
assert.equal(getRowImageAction(true, []), 'none', '该行没有图片：不显示')
assert.equal(getRowImageAction(false, []), 'none')
assert.equal(getRowImageAction(true, [{ storage: 'cos' }]), 'view')
assert.equal(getRowImageAction(true, [{ storage: 'cos' }, { storage: 'local' }]), 'view', '混合：有 COS 图片就可查看')
assert.equal(getRowImageAction(true, [{ storage: 'local' }, { storage: 'local' }]), 'local', '全在本地：灰色提示')
assert.equal(getRowImageAction(true, [{ storage: 's3' }]), 'local', '未知存储位置也不能请求')

// ───────── 错误码到文案的映射 ─────────
assert.equal(classifyFaceImageError(envelopeError(503, 'FACE_IMAGE_STORE_NOT_CONFIGURED')), 'notConfigured')
assert.equal(classifyFaceImageError(envelopeError(409, 'FACE_IMAGE_NOT_IN_COS')), 'local')
assert.equal(classifyFaceImageError(envelopeError(403, 'FACE_IMAGE_FORBIDDEN')), 'forbidden')
assert.equal(classifyFaceImageError(envelopeError(404, 'FACE_IMAGE_NOT_FOUND')), 'missing')
assert.equal(classifyFaceImageError(envelopeError(404, 'FACE_IMAGE_OBJECT_MISSING')), 'missing')
for (const code of ['FACE_IMAGE_STORE_DENIED', 'FACE_IMAGE_STORE_TIMEOUT', 'FACE_IMAGE_STORE_ERROR', 'FACE_IMAGE_TOO_LARGE', 'FACE_IMAGE_TYPE_UNSUPPORTED']) {
  assert.equal(classifyFaceImageError(envelopeError(502, code)), 'failed', `${code} 归为读取失败`)
}
// 没有错误码时按 HTTP 状态判断
assert.equal(classifyFaceImageError(new RequestError('x', 403, null)), 'forbidden')
assert.equal(classifyFaceImageError(new RequestError('x', 401, null)), 'unauthorized')
assert.equal(classifyFaceImageError(new RequestError('x', 404, null)), 'missing')
assert.equal(classifyFaceImageError(new RequestError('x', 502, null)), 'failed')
assert.equal(classifyFaceImageError(new RequestError('x', 500, { errorCode: 'SOMETHING_ELSE' })), 'failed', '不认识的错误码不信任')
assert.equal(classifyFaceImageError(new TypeError('Failed to fetch')), 'failed', '网络错误')
assert.equal(classifyFaceImageError(undefined), 'failed')
assert.equal(getFaceImageErrorCode(new RequestError('x', 400, { code: 'FACE_IMAGE_FORBIDDEN' })), 'FACE_IMAGE_FORBIDDEN', '兼容 code 字段')
assert.equal(getFaceImageErrorCode(new RequestError('x', 400, 'plain text')), null)
assert.equal(getFaceImageErrorCode(new Error('FACE_IMAGE_FORBIDDEN')), null, '不是请求错误不读')
assert.equal(FACE_IMAGE_ERROR_CODES.length, 10)
// 每一类失败都有一条文案键，且键指向 faceCameras.images.errors.*
assert.deepEqual(Object.keys(FACE_IMAGE_ERROR_MESSAGE_KEYS).sort(), ['failed', 'forbidden', 'local', 'missing', 'notConfigured', 'unauthorized'])
for (const key of Object.values(FACE_IMAGE_ERROR_MESSAGE_KEYS)) assert.match(key, /^faceCameras\.images\.errors\.[A-Za-z]+$/)
assert.equal(isSharedFailure('notConfigured'), true)
assert.equal(isSharedFailure('forbidden'), true)
assert.equal(isSharedFailure('unauthorized'), true)
assert.equal(isSharedFailure('missing'), false)
assert.equal(isSharedFailure('failed'), false)
assert.equal(isSharedFailure('local'), false)

// ───────── 分组与加载顺序 ─────────
{
  const images = [
    image({ imageId: 'f1', kind: 0, faceIndex: 1 }),
    image({ imageId: 'o1', kind: 2 }),
    image({ imageId: 'b1', kind: 1 }),
    image({ imageId: 'f0', kind: 0, faceIndex: 0 }),
    image({ imageId: 'l1', kind: 0, faceIndex: 2, storage: 'local' }),
    image({ imageId: 'fn', kind: 0, faceIndex: null }),
  ]
  const groups = groupImages(images)
  assert.deepEqual(groups.backgrounds.map((item) => item.imageId), ['b1'])
  assert.deepEqual(groups.faces.map((item) => item.imageId), ['f0', 'f1', 'l1', 'fn'], '人脸图按人脸序号排，无序号的排最后')
  assert.deepEqual(groups.others.map((item) => item.imageId), ['o1'])
  assert.deepEqual(getLoadOrder(images).map((item) => item.imageId), ['b1', 'f0', 'f1', 'fn', 'o1'], '背景 → 人脸 → 其他；本地图片不请求')
  assert.equal(shortCaptureId('0123456789abcdef'), '01234567')
}

// ───────── 人脸框坐标换算 ─────────
{
  // 归一化：数值本身就是比例
  assert.deepEqual(computeFaceBox({ x: 0.25, y: 0.1, w: 0.2, h: 0.3 }, true, null), {
    left: '25%', top: '10%', width: '20%', height: '30%',
  })
  // 像素：除以背景图原始宽高（1920×1080）
  assert.deepEqual(computeFaceBox({ x: 480, y: 108, w: 192, h: 324 }, false, { width: 1920, height: 1080 }), {
    left: '25%', top: '10%', width: '10%', height: '30%',
  })
  // 归一化未知（null）按像素处理，与后端约定一致
  assert.deepEqual(computeFaceBox({ x: 480, y: 108, w: 192, h: 324 }, null, { width: 1920, height: 1080 }), {
    left: '25%', top: '10%', width: '10%', height: '30%',
  })
  // 像素但没有可用的参照尺寸：画不出，宁可不画
  assert.equal(computeFaceBox({ x: 480, y: 108, w: 192, h: 324 }, false, null), null)
  assert.equal(computeFaceBox({ x: 480, y: 108, w: 192, h: 324 }, false, { width: 0, height: 1080 }), null)
  assert.equal(computeFaceBox({ x: 480, y: 108, w: 192, h: 324 }, null, undefined), null)
  // 超出图片的部分被夹住
  assert.deepEqual(computeFaceBox({ x: 0.9, y: 0.9, w: 0.3, h: 0.3 }, true, null), {
    left: '90%', top: '90%', width: '10%', height: '10%',
  })
  assert.deepEqual(computeFaceBox({ x: -0.1, y: 0, w: 0.3, h: 0.5 }, true, null), {
    left: '0%', top: '0%', width: '20%', height: '50%',
  })
  // 像素坐标在小数下的精度：1/3 保留 4 位小数
  assert.deepEqual(computeFaceBox({ x: 100, y: 0, w: 100, h: 1080 }, false, { width: 300, height: 1080 }), {
    left: '33.3333%', top: '0%', width: '33.3333%', height: '100%',
  })
  // 非法数值
  assert.equal(computeFaceBox({ x: 0.1, y: 0.1, w: 0, h: 0.2 }, true, null), null, '宽为 0')
  assert.equal(computeFaceBox({ x: 0.1, y: 0.1, w: 0.2, h: -0.2 }, true, null), null, '高为负')
  assert.equal(computeFaceBox({ x: Number.NaN, y: 0.1, w: 0.2, h: 0.2 }, true, null), null)
  assert.equal(computeFaceBox({ x: 0.1, y: 0.1, w: Number.POSITIVE_INFINITY, h: 0.2 }, true, null), null)
  assert.equal(computeFaceBox({ x: 1.2, y: 0.1, w: 0.2, h: 0.2 }, true, null), null, '完全在图片右侧外')
  assert.equal(computeFaceBox(null, true, null), null)

  // 参照尺寸：优先后端记录的原始宽高，其次浏览器报告的自然尺寸
  assert.deepEqual(pickReferenceSize({ width: 1920, height: 1080 }, { width: 640, height: 360 }), { width: 1920, height: 1080 })
  assert.deepEqual(pickReferenceSize({ width: null, height: null }, { width: 640, height: 360 }), { width: 640, height: 360 })
  assert.deepEqual(pickReferenceSize({ width: 0, height: 1080 }, { width: 640, height: 360 }), { width: 640, height: 360 })
  assert.deepEqual(pickReferenceSize(null, { width: 640, height: 360 }), { width: 640, height: 360 })
  assert.equal(pickReferenceSize({ width: null, height: null }, null), null)
  assert.equal(pickReferenceSize({ width: null, height: null }, { width: 0, height: 0 }), null)

  // 背景图上画哪些框：带 rect 的人脸图 / 背景图记录；标签是人脸序号 + 1
  const boxes = collectFaceBoxes(
    [
      image({ imageId: 'f0', kind: 0, faceIndex: 0, rect: { x: 0.1, y: 0.1, w: 0.1, h: 0.1 }, rectNormalized: true }),
      image({ imageId: 'f1', kind: 0, faceIndex: 1, rect: { x: 960, y: 540, w: 96, h: 108 }, rectNormalized: false }),
      image({ imageId: 'o', kind: 2, faceIndex: 2, rect: { x: 0.5, y: 0.5, w: 0.1, h: 0.1 }, rectNormalized: true }),
      image({ imageId: 'norect', kind: 0, faceIndex: 3 }),
    ],
    { width: 1920, height: 1080 },
  )
  assert.deepEqual(boxes.map((box) => [box.key, box.label]), [['f0', 1], ['f1', 2]])
  assert.deepEqual(boxes[1].style, { left: '50%', top: '50%', width: '5%', height: '10%' })
  // 像素框在没有参照尺寸时被略过，归一化框照常
  assert.deepEqual(
    collectFaceBoxes(
      [
        image({ imageId: 'f0', kind: 0, faceIndex: 0, rect: { x: 0.1, y: 0.1, w: 0.1, h: 0.1 }, rectNormalized: true }),
        image({ imageId: 'f1', kind: 0, faceIndex: 1, rect: { x: 960, y: 540, w: 96, h: 108 }, rectNormalized: false }),
      ],
      null,
    ).map((box) => box.key),
    ['f0'],
  )
}

// ───────── objectURL 登记表 ─────────
{
  const fake = createFakeUrlApi()
  const registry = createObjectUrlRegistry(fake.api)
  const first = registry.set('a', blobOf())
  assert.equal(first, 'blob:fake/1')
  assert.equal(registry.liveCount(), 1)
  // 同一个 key 再登记：先撤销旧的
  const second = registry.set('a', blobOf())
  assert.equal(second, 'blob:fake/2')
  assert.deepEqual(fake.revoked, ['blob:fake/1'])
  registry.set('b', blobOf())
  registry.release('a')
  assert.deepEqual(fake.revoked, ['blob:fake/1', 'blob:fake/2'])
  registry.release('a') // 重复释放无副作用
  assert.equal(fake.revoked.length, 2)
  registry.dispose()
  assert.equal(fake.live.size, 0, 'dispose 撤销全部')
  assert.equal(registry.liveCount(), 0)
  // dispose 之后不再登记：晚到的响应拿不到 URL，不会泄漏
  assert.equal(registry.set('c', blobOf()), null)
  assert.equal(fake.created.length, 3)
  assert.equal(fake.live.size, 0)
}

// ───────── 会话：按需逐张加载 → 关闭 / 卸载时全部撤销 ─────────
{
  const fake = createFakeUrlApi()
  const requested: string[] = []
  let inFlight = 0
  let maxInFlight = 0
  const changes: ImageLoadStates[] = []
  const images = [
    image({ imageId: 'f1', kind: 0, faceIndex: 1 }),
    image({ imageId: 'b1', kind: 1 }),
    image({ imageId: 'local1', kind: 0, faceIndex: 2, storage: 'local' }),
    image({ imageId: 'f0', kind: 0, faceIndex: 0 }),
  ]
  const session = createImageSession({
    images,
    urlApi: fake.api,
    onChange: (states) => changes.push(states),
    fetchImage: async (item) => {
      requested.push(item.imageId)
      inFlight += 1
      maxInFlight = Math.max(maxInFlight, inFlight)
      await tick()
      inFlight -= 1
      return blobOf(item.imageId)
    },
  })
  assert.equal(requested.length, 0, '创建会话本身不发任何请求')
  assert.equal(fake.created.length, 0)
  await session.start()
  assert.deepEqual(requested, ['b1', 'f0', 'f1'], '背景 → 人脸（按序号）；本地图片不请求')
  assert.equal(maxInFlight, 1, '逐张请求，不并发')
  const states = session.getStates()
  assert.deepEqual(states.get('local1'), { status: 'local' })
  assert.equal(states.get('b1')?.status, 'loaded')
  assert.equal(fake.live.size, 3)
  assert.deepEqual(
    [...states.values()].filter((state) => state.status === 'loaded').map((state) => (state as { url: string }).url).sort(),
    [...fake.live].sort(),
  )
  const changeCount = changes.length
  // 关闭 / 切换行 / 卸载 → dispose：全部撤销，引用清空，此后不再回调
  session.dispose()
  assert.equal(fake.live.size, 0, '关闭后没有存活的 objectURL')
  assert.deepEqual([...fake.revoked].sort(), [...fake.created].sort(), '创建过的每个 URL 都被撤销过')
  assert.equal(session.getStates().size, 0, '状态里不再持有任何 URL')
  assert.equal(changes.length, changeCount, 'dispose 不回调（组件已卸载，避免状态更新）')
  session.dispose() // 幂等
  assert.equal(fake.revoked.length, fake.created.length)
  await session.start() // dispose 之后 start 不做任何事
  assert.equal(requested.length, 3)
}

// ───────── 会话：请求在途时关闭 → 中止请求，晚到的响应不会泄漏 URL ─────────
{
  const fake = createFakeUrlApi()
  const gates = new Map<string, ReturnType<typeof deferred<Blob>>>()
  const signals = new Map<string, AbortSignal>()
  let changeCount = 0
  const session = createImageSession({
    images: [image({ imageId: 'b1', kind: 1 }), image({ imageId: 'f0', kind: 0, faceIndex: 0 }), image({ imageId: 'f1', kind: 0, faceIndex: 1 })],
    urlApi: fake.api,
    onChange: () => {
      changeCount += 1
    },
    fetchImage: (item, signal) => {
      signals.set(item.imageId, signal)
      const gate = deferred<Blob>()
      gates.set(item.imageId, gate)
      return gate.promise
    },
  })
  const started = session.start()
  await tick()
  assert.deepEqual([...gates.keys()], ['b1'], '只发出第一张的请求')
  gates.get('b1')!.resolve(blobOf())
  await tick()
  assert.deepEqual([...gates.keys()], ['b1', 'f0'], '第一张完成后才发第二张')
  assert.equal(fake.live.size, 1)

  const before = changeCount
  session.dispose() // 此时第二张请求还在途
  assert.equal(signals.get('f0')?.aborted, true, '在途请求被中止')
  assert.equal(fake.live.size, 0, '已拿到的图片 URL 被撤销')
  // 响应在关闭之后才到：不创建 URL，也不再发第三张
  gates.get('f0')!.resolve(blobOf())
  await started
  assert.equal(fake.created.length, 1, '关闭后晚到的响应不会生成新的 objectURL')
  assert.equal(fake.live.size, 0)
  assert.deepEqual([...gates.keys()], ['b1', 'f0'], '关闭后不再发后续请求')
  assert.equal(changeCount, before, '关闭后不再回调')
}

// ───────── 会话：在途请求被中止后的 AbortError 不当成失败 ─────────
{
  const fake = createFakeUrlApi()
  let changeCount = 0
  const gate = deferred<Blob>()
  const session = createImageSession({
    images: [image({ imageId: 'b1', kind: 1 })],
    urlApi: fake.api,
    onChange: () => {
      changeCount += 1
    },
    fetchImage: () => gate.promise,
  })
  const started = session.start()
  await tick()
  const before = changeCount
  session.dispose()
  gate.reject(new DOMException('aborted', 'AbortError'))
  await started
  assert.equal(changeCount, before)
  assert.equal(fake.created.length, 0)
}

// ───────── 会话：切换到另一条抓拍 = 旧会话 dispose + 新会话 ─────────
{
  const fake = createFakeUrlApi()
  const make = (id: string) =>
    createImageSession({
      images: [image({ imageId: id, kind: 1 })],
      urlApi: fake.api,
      onChange: () => {},
      fetchImage: async () => blobOf(id),
    })
  const first = make('first')
  await first.start()
  const firstUrl = [...fake.live][0]
  first.dispose()
  const second = make('second')
  await second.start()
  assert.equal(fake.live.has(firstUrl), false, '切换行时上一条的 URL 已撤销')
  assert.equal(fake.live.size, 1)
  second.dispose()
  assert.equal(fake.live.size, 0)
}

// ───────── 会话：共享失败（未配置 / 无权限 / 登录过期）后不再逐张重试 ─────────
for (const [status, code, kind] of [
  [503, 'FACE_IMAGE_STORE_NOT_CONFIGURED', 'notConfigured'],
  [403, 'FACE_IMAGE_FORBIDDEN', 'forbidden'],
] as const) {
  const fake = createFakeUrlApi()
  let calls = 0
  const session = createImageSession({
    images: [image({ imageId: 'b1', kind: 1 }), image({ imageId: 'f0', kind: 0, faceIndex: 0 }), image({ imageId: 'f1', kind: 0, faceIndex: 1 })],
    urlApi: fake.api,
    onChange: () => {},
    fetchImage: async () => {
      calls += 1
      throw envelopeError(status, code)
    },
  })
  await session.start()
  assert.equal(calls, 1, `${code}：只请求一次`)
  assert.deepEqual([...session.getStates().values()], [
    { status: 'error', kind },
    { status: 'error', kind },
    { status: 'error', kind },
  ])
  assert.equal(fake.created.length, 0)
  session.dispose()
}
{
  // 单张失败（不存在 / 读取失败）不影响其余图片
  const fake = createFakeUrlApi()
  const session = createImageSession({
    images: [image({ imageId: 'b1', kind: 1 }), image({ imageId: 'f0', kind: 0, faceIndex: 0 }), image({ imageId: 'f1', kind: 0, faceIndex: 1 })],
    urlApi: fake.api,
    onChange: () => {},
    fetchImage: async (item) => {
      if (item.imageId === 'b1') throw envelopeError(404, 'FACE_IMAGE_OBJECT_MISSING')
      if (item.imageId === 'f0') throw new TypeError('Failed to fetch')
      return blobOf()
    },
  })
  await session.start()
  assert.deepEqual(session.getStates().get('b1'), { status: 'error', kind: 'missing' })
  assert.deepEqual(session.getStates().get('f0'), { status: 'error', kind: 'failed' })
  assert.equal(session.getStates().get('f1')?.status, 'loaded')
  assert.equal(fake.live.size, 1)
  session.dispose()
  assert.equal(fake.live.size, 0)
}
{
  // 全是本地图片：不发任何请求，状态全是「在本地」
  let calls = 0
  const session = createImageSession({
    images: [image({ imageId: 'l1', kind: 1, storage: 'local' }), image({ imageId: 'l2', kind: 0, storage: 'local' })],
    urlApi: createFakeUrlApi().api,
    onChange: () => {},
    fetchImage: async () => {
      calls += 1
      return blobOf()
    },
  })
  await session.start()
  assert.equal(calls, 0)
  assert.deepEqual([...session.getStates().values()], [{ status: 'local' }, { status: 'local' }])
  session.dispose()
}

console.log('FaceCameras imageLogic.test: ok')
