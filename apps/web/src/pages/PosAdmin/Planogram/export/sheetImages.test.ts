import assert from 'node:assert/strict'
import { loadOnePdfImage, loadPdfImages, PDF_IMAGE_MAX_BYTES, PDF_IMAGE_TIMEOUT_MS } from './sheetImages'

// PDF 商品实拍图加载：候选地址依次尝试、每张图总时限（默认 8 秒）、并发、失败 / 超时回退（不拖垮整个导出）、调用方取消。

assert.equal(PDF_IMAGE_TIMEOUT_MS, 8000, '单张图超时 8 秒')

const png = (size = 10) => new Blob([new Uint8Array(size)], { type: 'image/png' })
const toDataUrl = async (blob: Blob) => `data:${blob.type};base64,${blob.size}`
const sameCandidates = (url: string) => [url]

/** 一个会响应 abort 的假 fetch：delayMs 毫秒后返回；被中止时抛 AbortError。 */
function slowFetch(delayMs: number, make: () => Response) {
  return (async (_input: RequestInfo | URL, init?: RequestInit) =>
    new Promise<Response>((resolve, reject) => {
      const timer = setTimeout(() => resolve(make()), delayMs)
      init?.signal?.addEventListener('abort', () => {
        clearTimeout(timer)
        reject(new DOMException('Aborted', 'AbortError'))
      })
    })) as typeof fetch
}

// 1) 成功：返回 data URL
assert.equal(
  await loadOnePdfImage('https://img.example/a.png', { candidatesOf: sameCandidates, fetcher: (async () => new Response(png(7), { status: 200 })) as typeof fetch, readBlob: toDataUrl }),
  'data:image/png;base64,7',
)

// 2) 第一个候选失败（404）→ 换第二个候选
const seen: string[] = []
const fallbackFetch = (async (input: RequestInfo | URL) => {
  seen.push(String(input))
  return String(input).includes('proxy') ? new Response('nope', { status: 404 }) : new Response(png(3), { status: 200 })
}) as typeof fetch
assert.equal(
  await loadOnePdfImage('https://img.example/a.png', { candidatesOf: (url) => [`/api/react/v1/image-proxy?url=${url}`, url], fetcher: fallbackFetch, readBlob: toDataUrl }),
  'data:image/png;base64,3',
)
assert.equal(seen.length, 2, '依次尝试候选地址')

// 3) 超时：慢于时限的图放弃（返回 null），不会一直等
const started = Date.now()
assert.equal(
  await loadOnePdfImage('https://img.example/slow.png', { candidatesOf: sameCandidates, timeoutMs: 80, fetcher: slowFetch(5000, () => new Response(png(), { status: 200 })), readBlob: toDataUrl }),
  null,
  '超时放弃该图',
)
assert.ok(Date.now() - started < 1500, '超时后立即返回，而不是等 5 秒')

// 4) 总时限跨候选：两个候选共用同一个时限（第一个耗尽时限后不再试第二个）
let attempts = 0
const countingSlow = (async (_input: RequestInfo | URL, init?: RequestInit) => {
  attempts += 1
  return new Promise<Response>((_resolve, reject) => init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError'))))
}) as typeof fetch
assert.equal(await loadOnePdfImage('u', { candidatesOf: () => ['a', 'b', 'c'], timeoutMs: 60, fetcher: countingSlow, readBlob: toDataUrl }), null)
assert.equal(attempts, 1, '时限耗尽后不再尝试后面的候选')

// 5) 不是图片 / 空文件 / 过大：放弃
assert.equal(await loadOnePdfImage('u', { candidatesOf: sameCandidates, fetcher: (async () => new Response(new Blob(['<html>'], { type: 'text/html' }), { status: 200 })) as typeof fetch, readBlob: toDataUrl }), null, '内容不是图片')
assert.equal(await loadOnePdfImage('u', { candidatesOf: sameCandidates, fetcher: (async () => new Response(new Blob([], { type: 'image/png' }), { status: 200 })) as typeof fetch, readBlob: toDataUrl }), null, '空文件')
assert.equal(await loadOnePdfImage('u', { candidatesOf: sameCandidates, fetcher: (async () => new Response(png(PDF_IMAGE_MAX_BYTES + 1), { status: 200 })) as typeof fetch, readBlob: toDataUrl }), null, '超过大小上限')
assert.equal(await loadOnePdfImage('u', { candidatesOf: () => [], fetcher: (async () => new Response(png(), { status: 200 })) as typeof fetch, readBlob: toDataUrl }), null, '没有可用候选（跨域未知主机等）')
assert.equal(await loadOnePdfImage('u', { candidatesOf: sameCandidates, fetcher: (async () => { throw new TypeError('network') }) as typeof fetch, readBlob: toDataUrl }), null, '网络错误')

// 6) 批量：并发限制、失败的货号记入 failed、无图地址跳过、进度回调
let running = 0
let maxRunning = 0
const tracking = (async (input: RequestInfo | URL) => {
  running += 1
  maxRunning = Math.max(maxRunning, running)
  await new Promise((resolve) => setTimeout(resolve, 15))
  running -= 1
  return String(input).includes('bad') ? new Response('x', { status: 500 }) : new Response(png(2), { status: 200 })
}) as typeof fetch
const progress: number[] = []
const result = await loadPdfImages(
  [
    ...Array.from({ length: 10 }, (_, index) => ({ productCode: `P${index}`, productImage: `https://img.example/${index === 4 ? 'bad' : index}.png` })),
    { productCode: 'NOIMG', productImage: null },
    { productCode: 'BLANK', productImage: '  ' },
  ],
  { concurrency: 3, candidatesOf: sameCandidates, fetcher: tracking, readBlob: toDataUrl, onProgress: (done) => progress.push(done) },
)
assert.equal(result.images.size, 9, '9 张成功')
assert.deepEqual(result.failed, ['P4'], '只有失败的货号进 failed；没有图片地址的不算失败')
assert.ok(maxRunning <= 3 && maxRunning >= 2, `并发不超过 3，实际 ${maxRunning}`)
assert.equal(progress.length, 10, '每张图完成一次进度回调')
assert.equal(progress[progress.length - 1], 10)
assert.equal(result.images.has('NOIMG'), false)

// 7) 取消：signal 中止后不再开始新的图
const controller = new AbortController()
let started2 = 0
const cancelFetch = (async () => {
  started2 += 1
  controller.abort()
  return new Response(png(), { status: 200 })
}) as typeof fetch
const cancelled = await loadPdfImages(Array.from({ length: 6 }, (_, index) => ({ productCode: `C${index}`, productImage: `u${index}` })), { concurrency: 1, signal: controller.signal, candidatesOf: sameCandidates, fetcher: cancelFetch, readBlob: toDataUrl })
assert.ok(started2 <= 2, `取消后不再取剩下的图，实际开始了 ${started2} 张`)
assert.ok(cancelled.images.size <= 1)

assert.deepEqual((await loadPdfImages([], {})).failed, [], '没有商品：空结果')

console.log('sheetImages.test: ok')
