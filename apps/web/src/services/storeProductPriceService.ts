import type { ApiResponse, PagedResult } from '../types/api'
import type {
  BatchUpdateStoreRetailPriceDto,
  CopyProgressDto,
  CopyStoreDataDto,
  CopyStoreDataResultDto,
  StoreProductPriceListDto,
  StoreProductPriceQueryDto,
  SyncFromHqRequest,
  SyncFromHqResult,
  SyncToOtherStoresDto,
} from '../types/storeProductPrice'
import request, { RequestError, unwrapApiData, unwrapPagedResult } from '../utils/request'

const API_BASE = '/api/react/v1/store-product-prices'

export async function getStoreProductPriceGrid(data: StoreProductPriceQueryDto) {
  const response = await request.post<ApiResponse<PagedResult<StoreProductPriceListDto>> | PagedResult<StoreProductPriceListDto>>(
    `${API_BASE}/grid`,
    data,
  )
  return unwrapPagedResult(response)
}

export async function batchUpdateStoreRetailPrices(data: BatchUpdateStoreRetailPriceDto): Promise<number> {
  const response = await request.post<ApiResponse<number>>(`${API_BASE}/batch-update`, data)
  return unwrapApiData(response)
}

export async function syncToOtherStores(data: SyncToOtherStoresDto): Promise<number> {
  const response = await request.post<ApiResponse<number>>(`${API_BASE}/sync-to-other-stores`, data)
  return unwrapApiData(response)
}

export async function copyStoreData(data: CopyStoreDataDto): Promise<CopyStoreDataResultDto> {
  const response = await request.post<ApiResponse<CopyStoreDataResultDto>>(`${API_BASE}/copy-store-data`, data)
  return unwrapApiData(response)
}

export function subscribeCopyProgress(
  params: {
    sourceStoreCode: string
    targetStoreCodes: string[]
    mode: string
    syncMultiCode: boolean
  },
  onProgress: (progress: CopyProgressDto) => void,
  onError: (error: Event) => void,
  onComplete: () => void,
): EventSource {
  const query = new URLSearchParams({
    sourceStoreCode: params.sourceStoreCode,
    targetStoreCodes: params.targetStoreCodes.join(','),
    mode: params.mode,
    syncMultiCode: String(params.syncMultiCode),
  })

  const baseUrl = (import.meta.env.VITE_API_BASE_URL || '').trim()
  const url = `${baseUrl}${API_BASE}/copy-store-data/stream?${query.toString()}`
  const eventSource = new EventSource(url, { withCredentials: true })

  eventSource.onmessage = (event) => {
    try {
      const data = JSON.parse(event.data) as CopyProgressDto
      onProgress(data)
    } catch {
      // ignore parse errors
    }
  }

  eventSource.onerror = (error) => {
    onError(error)
    eventSource.close()
  }

  const checkComplete = (event: MessageEvent) => {
    try {
      const data = JSON.parse(event.data) as CopyProgressDto
      if (data.eventType === 'completed' || data.eventType === 'error') {
        onComplete()
        eventSource.close()
      }
    } catch {
      // ignore
    }
  }
  eventSource.addEventListener('message', checkComplete)

  return eventSource
}

export async function syncFromHq(data: SyncFromHqRequest): Promise<SyncFromHqResult> {
  const response = await request.post<ApiResponse<SyncFromHqResult>>(`${API_BASE}/sync-from-hq`, data)

  // 这里显式校验业务成功标记，避免把失败响应误当成成功数据展示。
  if (response.success === false || response.isSuccess === false) {
    throw new RequestError(response.message || '从HQ同步失败', 200, response)
  }

  return unwrapApiData(response)
}
