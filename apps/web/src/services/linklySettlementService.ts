import { normalizeLinklySettlementPage } from '../pages/PosAdmin/LinklySettlements/logic'
import type { ApiResponse, PagedResult } from '../types/api'
import type {
  LinklySettlementDetail,
  LinklySettlementDeviceOption,
  LinklySettlementExportResult,
  LinklySettlementFilters,
  LinklySettlementListItem,
  LinklySettlementListQuery,
} from '../types/linklySettlement'
import request, { RequestError, unwrapApiData } from '../utils/request'

const API_BASE = '/api/react/v1/linkly-settlements'
const API_BASE_URL = (((import.meta as ImportMeta & { env?: ImportMetaEnv }).env?.VITE_API_BASE_URL) || '').trim()

function buildExportUrl() {
  return `${API_BASE_URL}${API_BASE}/export`.replace(/([^:]\/)\/+/g, '$1')
}

export async function getLinklySettlements(
  params: LinklySettlementListQuery,
  signal?: AbortSignal,
) {
  const response = await request.get<ApiResponse<PagedResult<LinklySettlementListItem>>>(API_BASE, {
    params: params as unknown as Record<string, unknown>,
    signal,
  })
  return normalizeLinklySettlementPage(unwrapApiData(response))
}

export async function getLinklySettlementDevices(signal?: AbortSignal) {
  const response = await request.get<ApiResponse<LinklySettlementDeviceOption[]>>(
    `${API_BASE}/devices`,
    { signal },
  )
  return unwrapApiData(response) ?? []
}

export async function getLinklySettlementDetail(id: string, signal?: AbortSignal) {
  const response = await request.get<ApiResponse<LinklySettlementDetail>>(
    `${API_BASE}/${id}`,
    { signal },
  )
  return unwrapApiData(response)
}

function getJsonErrorMessage(payload: unknown, status: number) {
  if (payload && typeof payload === 'object') {
    const response = payload as ApiResponse<unknown>
    const code = response.code ?? response.errorCode
    const message = response.message || `导出失败 (${status})`
    return code ? `${code}: ${message}` : message
  }
  return `导出失败 (${status})`
}

export function parseContentDispositionFileName(
  contentDisposition: string | null,
  fallback = 'linkly-settlements.xlsx',
) {
  if (!contentDisposition) return fallback

  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(contentDisposition)?.[1]
  if (encoded) {
    try {
      return decodeURIComponent(encoded).replace(/[\\/]/g, '_')
    } catch {
      return fallback
    }
  }

  const plain = /filename="?([^";]+)"?/i.exec(contentDisposition)?.[1]?.trim()
  return plain ? plain.replace(/[\\/]/g, '_') : fallback
}

export async function exportLinklySettlements(
  filters: LinklySettlementFilters,
  signal?: AbortSignal,
): Promise<LinklySettlementExportResult> {
  const response = await fetch(buildExportUrl(), {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(filters),
    signal,
  })
  const contentType = response.headers.get('content-type') || ''

  if (contentType.toLowerCase().includes('json')) {
    const payload = await response.json() as ApiResponse<unknown>
    throw new RequestError(getJsonErrorMessage(payload, response.status), response.status, payload)
  }

  if (!response.ok) {
    const text = await response.text()
    throw new RequestError(text || `导出失败 (${response.status})`, response.status, text)
  }

  return {
    blob: await response.blob(),
    fileName: parseContentDispositionFileName(response.headers.get('content-disposition')),
  }
}

export function downloadLinklySettlementExport(result: LinklySettlementExportResult) {
  const objectUrl = URL.createObjectURL(result.blob)
  try {
    const link = document.createElement('a')
    link.href = objectUrl
    link.download = result.fileName
    link.style.display = 'none'
    document.body.appendChild(link)
    link.click()
    link.remove()
  } finally {
    URL.revokeObjectURL(objectUrl)
  }
}
