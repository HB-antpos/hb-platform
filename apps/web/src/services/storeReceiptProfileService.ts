import type { ApiResponse } from '../types/api'
import type {
  StoreReceiptProfileDevicesResult,
  StoreReceiptProfilePublishResult,
  StoreReceiptProfileStatusItem,
} from '../types/storeReceiptProfile'
import request, { unwrapApiData } from '../utils/request'

// 路由与契约一致：HBweb 后端 StoreReceiptProfilesController，前缀 api/stores/receipt-profile。
const RECEIPT_PROFILE_BASE = '/api/stores/receipt-profile'

/** 契约限制：status / publish 每次 1–100 家。 */
export const RECEIPT_PROFILE_MAX_STORES_PER_REQUEST = 100

/**
 * 查询一批分店的小票下发状态（含当前值/最新快照/设备应用台数）。
 * 后端要求 1–100 个不重复且非空白的 guid，这里先去重去空白，再按 100 一批请求，结果按请求顺序拼接。
 * 后端不返回找不到/已软删的门店，调用方需自行处理缺项。
 */
export async function getStoreReceiptProfileStatuses(storeGuids: string[]): Promise<StoreReceiptProfileStatusItem[]> {
  const unique = Array.from(new Set(storeGuids.map((guid) => guid.trim()).filter(Boolean)))
  if (unique.length === 0) {
    return []
  }

  const batches: string[][] = []
  for (let index = 0; index < unique.length; index += RECEIPT_PROFILE_MAX_STORES_PER_REQUEST) {
    batches.push(unique.slice(index, index + RECEIPT_PROFILE_MAX_STORES_PER_REQUEST))
  }

  const results = await Promise.all(batches.map(async (batch) => {
    const response = await request.post<ApiResponse<StoreReceiptProfileStatusItem[]>>(
      `${RECEIPT_PROFILE_BASE}/status`,
      { storeGuids: batch },
    )
    // unwrapApiData 在响应体缺少 data 键时会原样返回整个响应对象，这里只接受数组，避免把它当成状态列表。
    const data = unwrapApiData(response)
    return Array.isArray(data) ? data : []
  }))
  return results.flat()
}

/** 某分店「设备应用情况」：该店启用的 POS 设备及其已应用的下发版本。 */
export async function getStoreReceiptProfileDevices(storeGuid: string): Promise<StoreReceiptProfileDevicesResult> {
  const response = await request.get<ApiResponse<StoreReceiptProfileDevicesResult>>(
    `${RECEIPT_PROFILE_BASE}/${encodeURIComponent(storeGuid)}/devices`,
  )
  const data = unwrapApiData(response)
  return { ...data, devices: Array.isArray(data?.devices) ? data.devices : [] }
}

/**
 * 批量下发（原子：任一店不可下发则整批不写入）。
 * 因为原子性，这里不做分批：调用方必须保证 1–100 家（页面的勾选上限即为 100）。
 * 整批失败时 request 会抛出带 payload（errorCode/details）的 RequestError，由调用方解析。
 */
export async function publishStoreReceiptProfiles(storeGuids: string[]): Promise<StoreReceiptProfilePublishResult> {
  const response = await request.post<ApiResponse<StoreReceiptProfilePublishResult>>(
    `${RECEIPT_PROFILE_BASE}/publish`,
    { storeGuids },
  )
  return unwrapApiData(response)
}
