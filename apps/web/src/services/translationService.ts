import type { ApiResponse } from '../types/api'
import request from '../utils/request'

// 后端 /api/Translation/batch-translate 单次最多 100 个文本，超过直接返回 400。
export const BATCH_TRANSLATE_MAX_TEXTS = 100

export async function batchTranslate(texts: string[]): Promise<Record<string, string>> {
  if (!texts.length) {
    return {}
  }

  // 超过上限时按 100 个一批串行请求再合并；100 个以内仍是一次请求，与原行为一致。
  const translations: Record<string, string> = {}
  for (let start = 0; start < texts.length; start += BATCH_TRANSLATE_MAX_TEXTS) {
    Object.assign(translations, await requestBatchTranslate(texts.slice(start, start + BATCH_TRANSLATE_MAX_TEXTS)))
  }
  return translations
}

async function requestBatchTranslate(texts: string[]): Promise<Record<string, string>> {
  const response = await request<ApiResponse<{ translations?: Record<string, string> }> | { success?: boolean; isSuccess?: boolean; message?: string; data?: { translations?: Record<string, string> } }>(
    '/api/Translation/batch-translate',
    {
      method: 'POST',
      data: { texts },
    },
  )

  if (response.success === false || response.isSuccess === false) {
    throw new Error(response.message || '翻译失败')
  }

  return response.data?.translations ?? {}
}
