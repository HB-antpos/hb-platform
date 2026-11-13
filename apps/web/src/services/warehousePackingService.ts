import type { ApiResponse } from '../types/api'
import type {
  PackCarrier,
  PackCarrierSaveRequest,
  PackCartonDetail,
  PackCartonFilter,
  PackCartonHeaderRequest,
  PackCartonList,
  PackDestinationRequest,
  PackEvent,
  PackLocateResult,
  PackOrderOption,
  PackPalletDetail,
  PackPalletFilter,
  PackPalletHeaderRequest,
  PackPalletList,
  PackResolveResult,
} from '../types/warehousePacking'
import request, { RequestError, unwrapApiData } from '../utils/request'

const API_BASE = '/api/react/v1/warehouse-packing'

const segment = (value: string) => encodeURIComponent(value)
const cartonUrl = (cartonGuid: string, suffix = '') => `${API_BASE}/cartons/${segment(cartonGuid)}${suffix}`
const palletUrl = (palletGuid: string, suffix = '') => `${API_BASE}/pallets/${segment(palletGuid)}${suffix}`

export interface PackingErrorInfo<TData = unknown> {
  /** HTTP 状态码；非接口错误（如断网）为 0。 */
  status: number
  errorCode?: string
  message: string
  /** 后端随错误带回的数据：一码多品的候选商品、被拦下的箱等。 */
  data?: TData
}

/** 把接口异常拆成状态码 / 错误码 / 提示 / 附带数据；提示原样取后端 message（403 等照实显示）。 */
export function readPackingError<TData = unknown>(error: unknown): PackingErrorInfo<TData> {
  if (error instanceof RequestError) {
    const payload = (error.payload ?? {}) as ApiResponse<TData>
    return {
      status: error.status,
      errorCode: typeof payload.errorCode === 'string' ? payload.errorCode : undefined,
      message: error.message,
      data: payload.data,
    }
  }
  return { status: 0, message: error instanceof Error ? error.message : String(error) }
}

// ---- 箱 ----

export async function listPackCartons(params: { filter?: PackCartonFilter; keyword?: string; mine?: boolean }) {
  const response = await request.get<ApiResponse<PackCartonList>>(`${API_BASE}/cartons`, {
    params: { filter: params.filter, keyword: params.keyword?.trim(), mine: params.mine ? true : undefined },
  })
  return unwrapApiData(response)
}

export async function createPackCarton(payload: PackDestinationRequest & { orderGuid?: string | null }) {
  return unwrapApiData(await request.post<ApiResponse<PackCartonDetail>>(`${API_BASE}/cartons`, payload))
}

export async function getPackCarton(cartonGuid: string) {
  return unwrapApiData(await request.get<ApiResponse<PackCartonDetail>>(cartonUrl(cartonGuid)))
}

/** 扫商品进箱；quantity 为本次增加的件数（按中包 / 箱规时由调用方算好）。 */
export async function scanIntoPackCarton(
  cartonGuid: string,
  payload: { code?: string; productCode?: string; quantity?: number },
) {
  return unwrapApiData(await request.post<ApiResponse<PackCartonDetail>>(cartonUrl(cartonGuid, '/scan'), payload))
}

/** 直接设置某商品在箱里的总数量；0 表示移除。 */
export async function setPackCartonItemQuantity(cartonGuid: string, productCode: string, quantity: number) {
  return unwrapApiData(
    await request.put<ApiResponse<PackCartonDetail>>(cartonUrl(cartonGuid, `/items/${segment(productCode)}`), { quantity }),
  )
}

export async function updatePackCartonHeader(cartonGuid: string, payload: PackCartonHeaderRequest) {
  return unwrapApiData(await request.put<ApiResponse<PackCartonDetail>>(cartonUrl(cartonGuid, '/header'), payload))
}

export async function sealPackCarton(cartonGuid: string, weightKg?: number | null) {
  return unwrapApiData(
    await request.post<ApiResponse<PackCartonDetail>>(cartonUrl(cartonGuid, '/seal'), { weightKg: weightKg ?? null }),
  )
}

export async function reopenPackCarton(cartonGuid: string) {
  return unwrapApiData(await request.post<ApiResponse<PackCartonDetail>>(cartonUrl(cartonGuid, '/reopen')))
}

export async function voidPackCarton(cartonGuid: string) {
  return unwrapApiData(await request.post<ApiResponse<PackCartonDetail>>(cartonUrl(cartonGuid, '/void')))
}

export async function listPackCartonEvents(cartonGuid: string) {
  return unwrapApiData(await request.get<ApiResponse<PackEvent[]>>(cartonUrl(cartonGuid, '/events'))) ?? []
}

// ---- 托盘 ----

export async function listPackPallets(params: { filter?: PackPalletFilter; keyword?: string }) {
  const response = await request.get<ApiResponse<PackPalletList>>(`${API_BASE}/pallets`, {
    params: { filter: params.filter, keyword: params.keyword?.trim() },
  })
  return unwrapApiData(response)
}

export async function createPackPallet(payload: PackDestinationRequest) {
  return unwrapApiData(await request.post<ApiResponse<PackPalletDetail>>(`${API_BASE}/pallets`, payload))
}

export async function getPackPallet(palletGuid: string) {
  return unwrapApiData(await request.get<ApiResponse<PackPalletDetail>>(palletUrl(palletGuid)))
}

/** 把箱放上托盘；code 为箱号或箱 GUID。去向不一致时仓库经理可带 allowDestinationMismatch 放行。 */
export async function addCartonToPackPallet(palletGuid: string, code: string, allowDestinationMismatch = false) {
  return unwrapApiData(
    await request.post<ApiResponse<PackPalletDetail>>(palletUrl(palletGuid, '/cartons'), { code, allowDestinationMismatch }),
  )
}

export async function removeCartonFromPackPallet(palletGuid: string, cartonGuid: string) {
  return unwrapApiData(
    await request.delete<ApiResponse<PackPalletDetail>>(palletUrl(palletGuid, `/cartons/${segment(cartonGuid)}`)),
  )
}

export async function updatePackPalletHeader(palletGuid: string, payload: PackPalletHeaderRequest) {
  return unwrapApiData(await request.put<ApiResponse<PackPalletDetail>>(palletUrl(palletGuid, '/header'), payload))
}

export async function sealPackPallet(palletGuid: string, weightKg?: number | null) {
  return unwrapApiData(
    await request.post<ApiResponse<PackPalletDetail>>(palletUrl(palletGuid, '/seal'), { weightKg: weightKg ?? null }),
  )
}

export async function reopenPackPallet(palletGuid: string) {
  return unwrapApiData(await request.post<ApiResponse<PackPalletDetail>>(palletUrl(palletGuid, '/reopen')))
}

export async function voidPackPallet(palletGuid: string) {
  return unwrapApiData(await request.post<ApiResponse<PackPalletDetail>>(palletUrl(palletGuid, '/void')))
}

export async function listPackPalletEvents(palletGuid: string) {
  return unwrapApiData(await request.get<ApiResponse<PackEvent[]>>(palletUrl(palletGuid, '/events'))) ?? []
}

// ---- 扫码识别、位置查询、主数据 ----

/** 识别箱号 / 托盘号；都不是时后端返回 404，这里转成 null，调用方按商品码处理。 */
export async function resolvePackCode(code: string): Promise<PackResolveResult | null> {
  try {
    return unwrapApiData(
      await request.get<ApiResponse<PackResolveResult>>(`${API_BASE}/resolve`, { params: { code: code.trim() } }),
    )
  } catch (error) {
    if (error instanceof RequestError && error.status === 404) {
      return null
    }
    throw error
  }
}

export async function locatePackProduct(code: string) {
  return unwrapApiData(
    await request.get<ApiResponse<PackLocateResult>>(`${API_BASE}/locate`, { params: { code: code.trim() } }),
  )
}

export async function listPackCarriers(includeInactive = false) {
  const response = await request.get<ApiResponse<PackCarrier[]>>(`${API_BASE}/carriers`, {
    params: { includeInactive: includeInactive ? true : undefined },
  })
  return unwrapApiData(response) ?? []
}

/** 新增（不传 carrierGuid）或修改运输公司；停用即 isActive=false 保存。 */
export async function savePackCarrier(carrierGuid: string | null, payload: PackCarrierSaveRequest) {
  const response = carrierGuid
    ? await request.put<ApiResponse<PackCarrier>>(`${API_BASE}/carriers/${segment(carrierGuid)}`, payload)
    : await request.post<ApiResponse<PackCarrier>>(`${API_BASE}/carriers`, payload)
  return unwrapApiData(response)
}

/** 装箱时可关联的订货单：已提交和配货中的。 */
export async function searchPackOrders(keyword?: string) {
  const response = await request.get<ApiResponse<PackOrderOption[]>>(`${API_BASE}/orders`, {
    params: { keyword: keyword?.trim() },
  })
  return unwrapApiData(response) ?? []
}
