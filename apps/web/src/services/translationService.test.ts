import { BATCH_TRANSLATE_MAX_TEXTS, batchTranslate } from './translationService'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

const originalFetch = globalThis.fetch
const requestSizes: number[] = []

try {
  // 模拟后端：超过 100 个文本返回 400，其余逐个回显「EN-原文」。
  globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
    const texts = (JSON.parse(String(init?.body ?? '{}')).texts ?? []) as string[]
    requestSizes.push(texts.length)
    if (texts.length > BATCH_TRANSLATE_MAX_TEXTS) {
      return new Response(JSON.stringify({ success: false, message: '批量翻译最多支持100个文本' }), {
        status: 400,
        headers: { 'Content-Type': 'application/json' },
      })
    }
    return new Response(JSON.stringify({
      success: true,
      data: { translations: Object.fromEntries(texts.map((text) => [text, `EN-${text}`])) },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  const texts = Array.from({ length: 250 }, (_, index) => `名称${index}`)
  const translations = await batchTranslate(texts)
  assertEqual(requestSizes.join(','), '100,100,50', '超过 100 个文本应按 100 个一批分批请求')
  assertEqual(Object.keys(translations).length, 250, '分批结果应合并为完整译文')
  assertEqual(translations['名称249'], 'EN-名称249', '最后一批的译文也应合并进结果')

  requestSizes.length = 0
  await batchTranslate(texts.slice(0, 100))
  assertEqual(requestSizes.join(','), '100', '100 个以内仍只发一次请求')

  requestSizes.length = 0
  assertEqual(Object.keys(await batchTranslate([])).length, 0, '空列表不发请求')
  assertEqual(requestSizes.length, 0, '空列表不应请求后端')
} finally {
  globalThis.fetch = originalFetch
}

console.log('translationService.test: ok')
