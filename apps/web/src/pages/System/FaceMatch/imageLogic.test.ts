import assert from 'node:assert/strict'

import { RequestError } from '../../../utils/request'
import type { ObjectUrlApi } from '../FaceCameras/imageLogic'
import {
  CAPTURE_SLOT,
  createHitImageSession,
  createHitThumbnailSession,
  decodeThumbnailBase64,
  type HitThumbnailStates,
  RESULT_IMAGE_ERROR_KEYS,
  REVIEW_IMAGE_ERROR_KEYS,
  classifyResultImageError,
  createResultImageSession,
  isSharedResultImageFailure,
  type ResultImageStates,
  classifyReviewImageError,
  createPreviewManager,
  createReferenceImageSession,
  createReviewImageSession,
  isSharedImageFailure,
  nextPreviewUid,
  referenceSlot,
  type ReviewImageStates,
} from './imageLogic'

// 复核图片与登记照预览的纯逻辑测试：全部用假 URL API、假 fetch、假数据，不连任何服务，也不碰真实图片。

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

const blob = (n: number) => new Blob([new Uint8Array([n])], { type: 'image/jpeg' })
const flush = () => new Promise((resolve) => setTimeout(resolve, 0))

function failure(status: number, errorCode?: string) {
  return new RequestError('x', status, errorCode ? { success: false, errorCode } : null)
}

// ───────── 错误分类（后端真实错误码）─────────
assert.equal(classifyReviewImageError(failure(503, 'FACE_MATCH_STORE_NOT_CONFIGURED')), 'notConfigured')
assert.equal(classifyReviewImageError(failure(503, 'FACE_MATCH_DISABLED')), 'notConfigured', '功能未开启也按未配置展示')
assert.equal(classifyReviewImageError(failure(403, 'FACE_MATCH_FORBIDDEN')), 'forbidden')
assert.equal(classifyReviewImageError(failure(403, 'FACE_MATCH_STORE_FORBIDDEN')), 'forbidden', '门店范围外')
assert.equal(classifyReviewImageError(failure(403)), 'forbidden')
assert.equal(classifyReviewImageError(failure(401)), 'unauthorized')
assert.equal(classifyReviewImageError(failure(404, 'FACE_MATCH_IMAGE_GONE')), 'gone', 'IMAGE_GONE：已清理或不存在')
assert.equal(classifyReviewImageError(failure(410)), 'gone')
assert.equal(classifyReviewImageError(failure(404, 'FACE_MATCH_NOT_FOUND')), 'missing', 'NOT_FOUND：任务不存在或越权共用')
assert.equal(classifyReviewImageError(failure(404)), 'missing')
// 图片存储拒绝 / 超时 / 出错、图片过大 / 格式不支持、审计写入失败：都是读取失败
for (const [status, code] of [
  [502, 'FACE_MATCH_STORE_DENIED'],
  [502, 'FACE_MATCH_STORE_TIMEOUT'],
  [502, 'FACE_MATCH_STORE_ERROR'],
  [502, 'FACE_MATCH_IMAGE_TOO_LARGE'],
  [415, 'FACE_MATCH_IMAGE_UNSUPPORTED'],
  [500, 'FACE_MATCH_AUDIT_FAILED'],
] as const) {
  assert.equal(classifyReviewImageError(failure(status, code)), 'failed', `${code} 按读取失败处理`)
}
assert.equal(classifyReviewImageError(failure(403, 'FACE_MATCH_SOMETHING_NEW')), 'forbidden', '不认识的码按状态码兜底')
assert.equal(classifyReviewImageError(failure(500)), 'failed')
assert.equal(classifyReviewImageError(failure(503)), 'failed', '没有错误码的 503 不当成未配置')
assert.equal(classifyReviewImageError(new Error('network')), 'failed')
assert.equal(classifyReviewImageError(undefined), 'failed')
for (const kind of ['notConfigured', 'forbidden', 'unauthorized', 'gone', 'missing', 'failed'] as const) {
  assert.ok(REVIEW_IMAGE_ERROR_KEYS[kind].startsWith('faceMatch.review.imageErrors.'))
}
assert.equal(isSharedImageFailure('notConfigured'), true)
assert.equal(isSharedImageFailure('forbidden'), true)
assert.equal(isSharedImageFailure('unauthorized'), true)
assert.equal(isSharedImageFailure('gone'), false, '单张被清理不代表其余图片也没了')
assert.equal(isSharedImageFailure('missing'), false)
assert.equal(isSharedImageFailure('failed'), false)

// ───────── 复核会话：先抓拍再第一张参考照片，其余切换时才取 ─────────
{
  const fake = createFakeUrlApi()
  const order: string[] = []
  let last: ReviewImageStates = new Map()
  const session = createReviewImageSession({
    referenceCount: 3,
    fetchCapture: async () => {
      order.push('capture')
      return blob(1)
    },
    fetchReference: async (index) => {
      order.push(`reference-${index}`)
      return blob(10 + index)
    },
    onChange: (states) => {
      last = states
    },
    urlApi: fake.api,
  })
  await session.start()
  assert.deepEqual(order, ['capture', 'reference-0'], '开始只取抓拍与第一张参考照片，没有预加载其余参考照片')
  assert.equal(last.get(CAPTURE_SLOT)?.status, 'loaded')
  assert.equal(last.get(referenceSlot(0))?.status, 'loaded')
  assert.equal(last.has(referenceSlot(1)), false)
  assert.equal(fake.live.size, 2)

  await session.loadReference(2)
  assert.deepEqual(order, ['capture', 'reference-0', 'reference-2'], '切换到第 3 张才请求它')
  await session.loadReference(2)
  await session.loadReference(0)
  assert.equal(order.length, 3, '已加载的不重复请求')
  // 越界序号不请求
  await session.loadReference(3)
  await session.loadReference(-1)
  await session.loadReference(1.5)
  assert.equal(order.length, 3)

  // 只有 URL 字符串进入状态，不含 Blob
  const state = last.get(CAPTURE_SLOT)
  assert.ok(state && state.status === 'loaded' && typeof state.url === 'string' && state.url.startsWith('blob:fake/'))

  session.dispose()
  assert.equal(fake.live.size, 0, 'dispose 撤销全部 objectURL')
  assert.equal(fake.revoked.length, 3)
  assert.equal(session.getStates().size, 0, 'dispose 清空状态')
  await session.loadReference(1)
  assert.equal(order.length, 3, 'dispose 后不再请求')
  session.dispose() // 幂等
  assert.equal(fake.revoked.length, 3)
}

// ───────── 名单目标图会话（抓拍详情抽屉）：不带现场抓拍，开始只取第 1 张，切换才取对应那张，dispose 撤销全部 ─────────
{
  const fake = createFakeUrlApi()
  const order: string[] = []
  const signals: AbortSignal[] = []
  let last: ReviewImageStates = new Map()
  const session = createReferenceImageSession({
    referenceCount: 3,
    fetchReference: async (index, signal) => {
      order.push(`reference-${index}`)
      signals.push(signal)
      return blob(20 + index)
    },
    onChange: (states) => {
      last = states
    },
    urlApi: fake.api,
  })
  await session.start()
  assert.deepEqual(order, ['reference-0'], '开始只取第 1 张目标图：没有现场抓拍请求，也没有预取其余几张')
  assert.equal(last.has(CAPTURE_SLOT), false, '目标图会话里没有现场抓拍槽位')
  assert.equal(last.get(referenceSlot(0))?.status, 'loaded')
  assert.equal(last.has(referenceSlot(1)), false)
  assert.equal(fake.live.size, 1)

  await session.loadReference(2)
  assert.deepEqual(order, ['reference-0', 'reference-2'], '切换到第 3 张才请求它')
  await session.loadReference(2)
  await session.loadReference(0)
  assert.equal(order.length, 2, '已加载的不重复请求')
  await session.loadReference(3)
  await session.loadReference(-1)
  assert.equal(order.length, 2, '越界下标不请求')

  // 状态里只有 URL 字符串，不含 Blob
  const state = last.get(referenceSlot(2))
  assert.ok(state && state.status === 'loaded' && typeof state.url === 'string' && state.url.startsWith('blob:fake/'))
  assert.equal(fake.live.size, 2)

  session.dispose()
  assert.equal(fake.live.size, 0, 'dispose 撤销全部目标图 objectURL')
  assert.equal(fake.revoked.length, 2)
  assert.equal(session.getStates().size, 0)
  assert.ok(signals.every((signal) => signal.aborted), 'dispose 中止请求信号')
  await session.loadReference(1)
  assert.equal(order.length, 2, 'dispose 后不再请求')
  session.dispose()
  assert.equal(fake.revoked.length, 2, '重复 dispose 幂等')
}
// 目标图会话：张数为 0 时什么也不请求；第 1 张还没返回就 dispose（用户关了抽屉 / 换了抓拍）：不泄漏 URL
{
  const fake = createFakeUrlApi()
  let calls = 0
  const empty = createReferenceImageSession({
    referenceCount: 0,
    fetchReference: async () => {
      calls += 1
      return blob(1)
    },
    onChange: () => undefined,
    urlApi: fake.api,
  })
  await empty.start()
  assert.equal(calls, 0, '没有目标图时不请求')

  let resolveFirst: (value: Blob) => void = () => undefined
  const pending = createReferenceImageSession({
    referenceCount: 2,
    fetchReference: () => new Promise<Blob>((resolve) => {
      resolveFirst = resolve
    }),
    onChange: () => undefined,
    urlApi: fake.api,
  })
  const started = pending.start()
  pending.dispose()
  resolveFirst(blob(1))
  await started
  await flush()
  assert.equal(fake.created.length, 0, 'dispose 后才返回的目标图不得创建 objectURL')
  assert.equal(fake.live.size, 0)
}
// 目标图会话：无权限（403）这类对其余张同样成立的失败，后续各张直接标同样的失败，不再逐张重试；错误按复核弹框同一套分类
{
  const fake = createFakeUrlApi()
  let calls = 0
  let last: ReviewImageStates = new Map()
  const session = createReferenceImageSession({
    referenceCount: 3,
    fetchReference: async () => {
      calls += 1
      throw failure(403, 'FACE_MATCH_FORBIDDEN')
    },
    onChange: (states) => {
      last = states
    },
    urlApi: fake.api,
  })
  await session.start()
  await session.loadReference(1)
  await session.loadReference(2)
  assert.equal(calls, 1, '共享失败只请求一次')
  assert.deepEqual(last.get(referenceSlot(0)), { status: 'error', kind: 'forbidden' })
  assert.deepEqual(last.get(referenceSlot(2)), { status: 'error', kind: 'forbidden' })
  assert.equal(fake.created.length, 0)
  session.dispose()
}

// ───────── 没有参考照片：只取抓拍 ─────────
{
  const fake = createFakeUrlApi()
  const order: string[] = []
  const session = createReviewImageSession({
    referenceCount: 0,
    fetchCapture: async () => {
      order.push('capture')
      return blob(1)
    },
    fetchReference: async () => {
      order.push('reference')
      return blob(2)
    },
    onChange: () => undefined,
    urlApi: fake.api,
  })
  await session.start()
  assert.deepEqual(order, ['capture'])
  await session.loadReference(0)
  assert.deepEqual(order, ['capture'], '没有参考照片时不会请求')
  session.dispose()
}

// ───────── 关闭后才返回的响应：拿不到 URL，不泄漏 ─────────
{
  const fake = createFakeUrlApi()
  let resolveCapture: (value: Blob) => void = () => undefined
  let calls = 0
  const session = createReviewImageSession({
    referenceCount: 2,
    fetchCapture: () => new Promise<Blob>((resolve) => {
      resolveCapture = resolve
    }),
    fetchReference: async () => {
      calls += 1
      return blob(2)
    },
    onChange: () => undefined,
    urlApi: fake.api,
  })
  const started = session.start()
  session.dispose() // 用户在图片返回前关闭了弹框
  resolveCapture(blob(1))
  await started
  await flush()
  assert.equal(fake.created.length, 0, '关闭后才到达的响应不得创建 objectURL')
  assert.equal(fake.live.size, 0)
  assert.equal(calls, 0, '关闭后不再继续请求参考照片')
}

// ───────── 中止信号：dispose 会中止在途请求 ─────────
{
  const fake = createFakeUrlApi()
  let seenSignal: AbortSignal | null = null
  const session = createReviewImageSession({
    referenceCount: 0,
    fetchCapture: (signal) => {
      seenSignal = signal
      return new Promise<Blob>((_, reject) => signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError'))))
    },
    fetchReference: async () => blob(1),
    onChange: () => undefined,
    urlApi: fake.api,
  })
  const started = session.start()
  assert.equal((seenSignal as AbortSignal | null)?.aborted, false)
  session.dispose()
  assert.equal((seenSignal as AbortSignal | null)?.aborted, true, 'dispose 中止在途请求')
  await started
}

// ───────── 失败：共享失败不再逐张重试；单张失败不影响别的图片 ─────────
{
  const fake = createFakeUrlApi()
  const order: string[] = []
  let last: ReviewImageStates = new Map()
  const session = createReviewImageSession({
    referenceCount: 3,
    fetchCapture: async () => {
      order.push('capture')
      throw failure(403, 'FACE_MATCH_FORBIDDEN')
    },
    fetchReference: async (index) => {
      order.push(`reference-${index}`)
      return blob(index)
    },
    onChange: (states) => {
      last = states
    },
    urlApi: fake.api,
  })
  await session.start()
  assert.deepEqual(order, ['capture'], '无权限对其余图片同样成立：不再请求')
  assert.deepEqual(last.get(CAPTURE_SLOT), { status: 'error', kind: 'forbidden' })
  assert.deepEqual(last.get(referenceSlot(0)), { status: 'error', kind: 'forbidden' })
  await session.loadReference(1)
  assert.deepEqual(last.get(referenceSlot(1)), { status: 'error', kind: 'forbidden' }, '之后切换到的参考照片同样直接标记失败')
  assert.equal(order.length, 1)
  session.dispose()
}
{
  const fake = createFakeUrlApi()
  let last: ReviewImageStates = new Map()
  const session = createReviewImageSession({
    referenceCount: 2,
    fetchCapture: async () => blob(1),
    fetchReference: async (index) => {
      if (index === 0) throw failure(404, 'FACE_MATCH_NOT_FOUND')
      return blob(2)
    },
    onChange: (states) => {
      last = states
    },
    urlApi: fake.api,
  })
  await session.start()
  assert.deepEqual(last.get(referenceSlot(0)), { status: 'error', kind: 'missing' })
  assert.equal(last.get(CAPTURE_SLOT)?.status, 'loaded', '一张不存在不影响抓拍')
  await session.loadReference(1)
  assert.equal(last.get(referenceSlot(1))?.status, 'loaded', '不存在不是共享失败，下一张照常加载')
  session.dispose()
  assert.equal(fake.live.size, 0)
}

// ───────── 登记照预览：新增创建 URL、移除立即撤销、卸载撤销全部、卸载后拒绝登记 ─────────
{
  const fake = createFakeUrlApi()
  const manager = createPreviewManager(fake.api)
  const a = { uid: nextPreviewUid(), file: blob(1) }
  const b = { uid: nextPreviewUid(), file: blob(2) }
  const c = { uid: nextPreviewUid(), file: blob(3) }
  assert.notEqual(a.uid, b.uid, '临时编号互不相同')

  let urls = manager.sync([a, b])
  assert.equal(urls.size, 2)
  assert.equal(fake.created.length, 2)
  const urlOfA = urls.get(a.uid)
  const urlOfB = urls.get(b.uid)
  assert.ok(urlOfA && urlOfB && urlOfA !== urlOfB)

  // 同一批再同步：不重复创建
  urls = manager.sync([a, b])
  assert.equal(fake.created.length, 2)
  assert.equal(urls.get(a.uid), urlOfA)

  // 移除一张：立即撤销它的 URL，其余不动
  urls = manager.sync([a, c])
  assert.equal(urls.has(b.uid), false)
  assert.deepEqual(fake.revoked, [urlOfB], '移除的照片立即撤销 objectURL')
  assert.equal(fake.created.length, 3)
  assert.equal(manager.liveCount(), 2)

  // 全部移除
  urls = manager.sync([])
  assert.equal(urls.size, 0)
  assert.equal(fake.live.size, 0)

  // 卸载：撤销全部，并拒绝之后的登记
  manager.sync([a, b])
  assert.equal(fake.live.size, 2)
  manager.dispose()
  assert.equal(fake.live.size, 0, '卸载撤销全部 objectURL')
  assert.equal(manager.liveCount(), 0)
  const afterDispose = manager.sync([c])
  assert.equal(afterDispose.size, 0, '卸载后不再创建 URL')
  assert.equal(fake.live.size, 0)
  manager.dispose() // 幂等
}

// ───────── 抓拍与比对：图片错误分类（沿用 FACE_IMAGE_* 错误码）─────────
assert.equal(classifyResultImageError(failure(403, 'FACE_IMAGE_FORBIDDEN')), 'forbidden')
assert.equal(classifyResultImageError(failure(403)), 'forbidden')
assert.equal(classifyResultImageError(failure(401)), 'unauthorized')
assert.equal(classifyResultImageError(failure(404, 'FACE_IMAGE_NOT_FOUND')), 'missing')
assert.equal(classifyResultImageError(failure(404, 'FACE_IMAGE_OBJECT_MISSING')), 'missing')
assert.equal(classifyResultImageError(failure(409, 'FACE_IMAGE_NOT_IN_COS')), 'local', '图片还在接收服务本地：暂未转存')
assert.equal(classifyResultImageError(failure(503, 'FACE_IMAGE_STORE_NOT_CONFIGURED')), 'notConfigured')
assert.equal(classifyResultImageError(failure(502, 'FACE_IMAGE_STORE_ERROR')), 'failed')
assert.equal(classifyResultImageError(failure(500)), 'failed')
assert.equal(classifyResultImageError(new Error('boom')), 'failed')
assert.deepEqual(Object.keys(RESULT_IMAGE_ERROR_KEYS).sort(), ['failed', 'forbidden', 'local', 'missing', 'notConfigured', 'unauthorized'])
assert.equal(isSharedResultImageFailure('forbidden'), true)
assert.equal(isSharedResultImageFailure('notConfigured'), true)
assert.equal(isSharedResultImageFailure('unauthorized'), true)
assert.equal(isSharedResultImageFailure('missing'), false, '单张不存在不影响另一张')
assert.equal(isSharedResultImageFailure('failed'), false)

// ───────── 抓拍与比对：图片会话（先人脸小图再背景图、按需、dispose 撤销）─────────
{
  const fake = createFakeUrlApi()
  const order: string[] = []
  let last: ResultImageStates = new Map()
  const session = createResultImageSession({
    fetchers: {
      scene: async () => {
        order.push('scene')
        return blob(2)
      },
      face: async () => {
        order.push('face')
        return blob(1)
      },
    },
    onChange: (states) => {
      last = states
    },
    urlApi: fake.api,
  })
  assert.equal(order.length, 0, '创建会话本身不请求任何图片')
  await session.start()
  assert.deepEqual(order, ['face', 'scene'], '先人脸小图，再背景图')
  assert.equal(last.get('face')?.status, 'loaded')
  assert.equal(last.get('scene')?.status, 'loaded')
  assert.equal(fake.live.size, 2)
  session.dispose()
  assert.equal(fake.live.size, 0, 'dispose 撤销全部 objectURL')
  assert.equal(fake.revoked.length, 2)
  assert.equal(session.getStates().size, 0, 'dispose 清空状态')
  session.dispose() // 幂等
  assert.equal(fake.revoked.length, 2)
}

// 没有传 fetcher 的槽位不请求（后端没给图片编号）
{
  const fake = createFakeUrlApi()
  const order: string[] = []
  const session = createResultImageSession({
    fetchers: { face: async () => (order.push('face'), blob(1)) },
    onChange: () => undefined,
    urlApi: fake.api,
  })
  await session.start()
  assert.deepEqual(order, ['face'])
  assert.equal(session.getStates().has('scene'), false)
  session.dispose()
  const empty = createResultImageSession({ fetchers: {}, onChange: () => undefined, urlApi: fake.api })
  await empty.start()
  assert.equal(empty.getStates().size, 0)
  empty.dispose()
}

// 关闭后才返回的响应：拿不到 URL，不泄漏；并且不再请求下一张
{
  const fake = createFakeUrlApi()
  let resolveFace: (value: Blob) => void = () => undefined
  let sceneCalls = 0
  const session = createResultImageSession({
    fetchers: {
      face: () => new Promise<Blob>((resolve) => {
        resolveFace = resolve
      }),
      scene: async () => {
        sceneCalls += 1
        return blob(2)
      },
    },
    onChange: () => undefined,
    urlApi: fake.api,
  })
  const started = session.start()
  session.dispose()
  resolveFace(blob(1))
  await started
  await flush()
  assert.equal(fake.created.length, 0, '关闭后才到达的响应不得创建 objectURL')
  assert.equal(fake.live.size, 0)
  assert.equal(sceneCalls, 0, '关闭后不再继续请求背景图')
}

// dispose 会中止在途请求
{
  const fake = createFakeUrlApi()
  let seenSignal: AbortSignal | null = null
  const session = createResultImageSession({
    fetchers: {
      face: (signal) => {
        seenSignal = signal
        return new Promise<Blob>((_, reject) => signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError'))))
      },
    },
    onChange: () => undefined,
    urlApi: fake.api,
  })
  const started = session.start()
  assert.equal((seenSignal as AbortSignal | null)?.aborted, false)
  session.dispose()
  assert.equal((seenSignal as AbortSignal | null)?.aborted, true)
  await started
}

// 失败：共享失败（无权限等）不再请求另一张；单张失败不影响另一张
{
  const fake = createFakeUrlApi()
  const order: string[] = []
  let last: ResultImageStates = new Map()
  const forbidden = createResultImageSession({
    fetchers: {
      face: async () => {
        order.push('face')
        throw failure(403, 'FACE_IMAGE_FORBIDDEN')
      },
      scene: async () => {
        order.push('scene')
        return blob(2)
      },
    },
    onChange: (states) => {
      last = states
    },
    urlApi: fake.api,
  })
  await forbidden.start()
  assert.deepEqual(order, ['face'], '无权限：不再请求背景图（也不多写一次审计）')
  assert.deepEqual(last.get('face'), { status: 'error', kind: 'forbidden' })
  assert.deepEqual(last.get('scene'), { status: 'error', kind: 'forbidden' })
  forbidden.dispose()

  order.length = 0
  const partial = createResultImageSession({
    fetchers: {
      face: async () => {
        order.push('face')
        throw failure(404, 'FACE_IMAGE_OBJECT_MISSING')
      },
      scene: async () => {
        order.push('scene')
        return blob(2)
      },
    },
    onChange: (states) => {
      last = states
    },
    urlApi: fake.api,
  })
  await partial.start()
  assert.deepEqual(order, ['face', 'scene'])
  assert.deepEqual(last.get('face'), { status: 'error', kind: 'missing' })
  assert.equal(last.get('scene')?.status, 'loaded')
  partial.dispose()
  assert.equal(fake.live.size, 0)
}

// ───────── 命中图（名单命中视图的详情抽屉）─────────
{
  const urls = createFakeUrlApi()
  let states: ReviewImageStates = new Map()
  const session = createHitImageSession({ fetchHit: async () => blob(7), onChange: (next) => (states = next), urlApi: urls.api })
  await session.start()
  assert.equal(states.get(CAPTURE_SLOT)?.status, 'loaded', '命中图放在唯一的槽位里')
  assert.equal(urls.live.size, 1)
  session.dispose()
  assert.equal(urls.live.size, 0, 'dispose 撤销命中图 objectURL')

  const gone = createHitImageSession({ fetchHit: async () => { throw failure(404, 'FACE_MATCH_IMAGE_GONE') }, onChange: (next) => (states = next), urlApi: urls.api })
  await gone.start()
  const goneState = states.get(CAPTURE_SLOT)
  assert.equal(goneState?.status === 'error' ? goneState.kind : null, 'gone', '命中图已清理按复核口径分类')
}

// ───────── 命中图缩略图 ─────────
assert.deepEqual(Array.from(decodeThumbnailBase64('AQID') ?? []), [1, 2, 3])
assert.equal(decodeThumbnailBase64(''), null)
assert.equal(decodeThumbnailBase64(null), null)
assert.equal(decodeThumbnailBase64('data:image/jpeg;base64,AQID'), null, '只认裸 base64，不接受 data: 地址')
{
  const urls = createFakeUrlApi()
  let states: HitThumbnailStates = new Map()
  const batches: string[][] = []
  const session = createHitThumbnailSession({
    taskIds: ['t1', 't2', 't3'],
    fetchBatch: async (ids) => {
      batches.push([...ids])
      return [
        { taskId: 't1', status: 'ok', imageBase64: 'AQID' },
        { taskId: 't2', status: 'gone', imageBase64: null },
        { taskId: 't3', status: 'ok', imageBase64: '%%%' },
      ]
    },
    onChange: (next) => (states = next),
    urlApi: urls.api,
  })
  await session.start()
  assert.deepEqual(batches, [['t1', 't2', 't3']], '一页只发一次批量请求')
  assert.equal(states.get('t1')?.status, 'loaded')
  assert.equal(states.get('t2')?.status, 'none', '过期 / 读不到的按占位处理')
  assert.equal(states.get('t3')?.status, 'none', '非法 base64 不登记 objectURL')
  assert.equal(urls.live.size, 1)
  session.dispose()
  assert.equal(urls.live.size, 0, 'dispose 撤销全部缩略图 objectURL')
  assert.equal(session.getStates().size, 0)

  // dispose 之后才到的响应：不登记、不回调
  let resolveLate: (items: { taskId: string; status: string; imageBase64: string | null }[]) => void = () => undefined
  let lateCalls = 0
  const late = createHitThumbnailSession({
    taskIds: ['t9'],
    fetchBatch: () => new Promise((resolve) => (resolveLate = resolve)),
    onChange: () => (lateCalls += 1),
    urlApi: urls.api,
  })
  const pending = late.start()
  late.dispose()
  const callsBefore = lateCalls
  resolveLate([{ taskId: 't9', status: 'ok', imageBase64: 'AQID' }])
  await pending
  assert.equal(lateCalls, callsBefore, 'dispose 后不再回调')
  assert.equal(urls.live.size, 0, '晚到的缩略图不会留下 objectURL')

  // 整批失败：全部退回占位
  const failed = createHitThumbnailSession({ taskIds: ['a'], fetchBatch: async () => { throw failure(500) }, onChange: (next) => (states = next), urlApi: urls.api })
  await failed.start()
  assert.equal(states.get('a')?.status, 'none')

  // 空列表不发请求
  let emptyCalls = 0
  await createHitThumbnailSession({ taskIds: [], fetchBatch: async () => { emptyCalls += 1; return [] }, onChange: () => undefined, urlApi: urls.api }).start()
  assert.equal(emptyCalls, 0)
}

console.log('FaceMatch imageLogic.test: ok')
