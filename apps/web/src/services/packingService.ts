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
  PackIncoming,
  PackLocateResult,
  PackLocation,
  PackLocationSaveRequest,
  PackOwner,
  PackPalletDetail,
  PackPalletFilter,
  PackPalletHeaderRequest,
  PackPalletList,
  PackReceiveLookup,
  PackReceivePalletRequest,
  PackResolveResult,
  PackScope,
} from '../types/packing'
import request, { RequestError, unwrapApiData } from '../utils/request'

const API_BASE = '/api/react/v1/packing'

const segment = (value: string) => encodeURIComponent(value)
const cartonUrl = (cartonGuid: string, suffix = '') => `${API_BASE}/cartons/${segment(cartonGuid)}${suffix}`
const palletUrl = (palletGuid: string, suffix = '') => `${API_BASE}/pallets/${segment(palletGuid)}${suffix}`

export interface PackingErrorInfo<TData = unknown> {
  /** HTTP 状态码；非接口错误（如断网）为 0。 */
  status: number
  errorCode?: string
  message: string
  /** 后端随错误带回的数据：一码多品的候选商品、被拦下的箱、实到箱数等。 */
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

// ---- 所属地点 ----

/** 当前账号能操作的地点（分店 / 总仓）以及是不是店长 / 管理员。 */
export async function getPackScope() {
  return unwrapApiData(await request.get<ApiResponse<PackScope>>(`${API_BASE}/scope`))
}

/**
 * 选「其他分店 / 总仓」时的候选地点：启用的分店（不含发出地点自己）加总仓（发出地点是总仓时不含总仓）。
 * 店员没有「查看分店」权限，用这个接口而不是通用分店列表。
 */
export async function listPackDestinations(ownerCode?: string) {
  const response = await request.get<ApiResponse<PackOwner[]>>(`${API_BASE}/destinations`, { params: { ownerCode } })
  return unwrapApiData(response) ?? []
}

// ---- 箱 ----

export async function listPackCartons(params: {
  ownerCode?: string
  filter?: PackCartonFilter
  keyword?: string
  mine?: boolean
}) {
  const response = await request.get<ApiResponse<PackCartonList>>(`${API_BASE}/cartons`, {
    params: {
      ownerCode: params.ownerCode,
      filter: params.filter,
      keyword: params.keyword?.trim(),
      mine: params.mine ? true : undefined,
    },
  })
  return unwrapApiData(response)
}

export async function createPackCarton(payload: PackDestinationRequest & { ownerCode?: string | null }) {
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

/** 设置箱的存放位置（位置名称或位置条码）；传空清除。 */
export async function setPackCartonLocation(cartonGuid: string, locationCode: string | null) {
  return unwrapApiData(
    await request.put<ApiResponse<PackCartonDetail>>(cartonUrl(cartonGuid, '/location'), { locationCode: locationCode || null }),
  )
}

/** 发出散箱：去向必须是其他分店或总仓，发出后由对方扫码签收。 */
export async function dispatchPackCarton(cartonGuid: string) {
  return unwrapApiData(await request.post<ApiResponse<PackCartonDetail>>(cartonUrl(cartonGuid, '/dispatch')))
}

/** 签收一只箱（散箱，或托盘上「只签收这一箱」）。 */
export async function receivePackCarton(cartonGuid: string, locationCode?: string | null) {
  return unwrapApiData(
    await request.post<ApiResponse<PackCartonDetail>>(cartonUrl(cartonGuid, '/receive'), { locationCode: locationCode || null }),
  )
}

export async function listPackCartonEvents(cartonGuid: string) {
  return unwrapApiData(await request.get<ApiResponse<PackEvent[]>>(cartonUrl(cartonGuid, '/events'))) ?? []
}

// ---- 托盘 ----

export async function listPackPallets(params: { ownerCode?: string; filter?: PackPalletFilter; keyword?: string }) {
  const response = await request.get<ApiResponse<PackPalletList>>(`${API_BASE}/pallets`, {
    params: { ownerCode: params.ownerCode, filter: params.filter, keyword: params.keyword?.trim() },
  })
  return unwrapApiData(response)
}

export async function createPackPallet(payload: PackDestinationRequest & { ownerCode?: string | null }) {
  return unwrapApiData(await request.post<ApiResponse<PackPalletDetail>>(`${API_BASE}/pallets`, payload))
}

export async function getPackPallet(palletGuid: string) {
  return unwrapApiData(await request.get<ApiResponse<PackPalletDetail>>(palletUrl(palletGuid)))
}

/** 把箱放上托盘；code 为箱号或箱 GUID。去向不一致时店长可带 allowDestinationMismatch 放行。 */
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

export async function setPackPalletLocation(palletGuid: string, locationCode: string | null) {
  return unwrapApiData(
    await request.put<ApiResponse<PackPalletDetail>>(palletUrl(palletGuid, '/location'), { locationCode: locationCode || null }),
  )
}

/** 发出整个托盘：托盘上的箱一起变成在路上，由对方扫码签收。 */
export async function dispatchPackPallet(palletGuid: string) {
  return unwrapApiData(await request.post<ApiResponse<PackPalletDetail>>(palletUrl(palletGuid, '/dispatch')))
}

/**
 * 签收托盘：整托签收必须带实到箱数（和系统记录不一致返回 409 COUNT_MISMATCH）；
 * 带 cartonGuids 表示只签收这几只箱（逐箱核对，不要求箱数）。
 */
export async function receivePackPallet(palletGuid: string, payload: PackReceivePalletRequest) {
  return unwrapApiData(await request.post<ApiResponse<PackPalletDetail>>(palletUrl(palletGuid, '/receive'), payload))
}

export async function listPackPalletEvents(palletGuid: string) {
  return unwrapApiData(await request.get<ApiResponse<PackEvent[]>>(palletUrl(palletGuid, '/events'))) ?? []
}

// ---- 签收 ----

/** 待签收（发给本地点、还没签完）和最近签收。 */
export async function listPackIncoming(ownerCode?: string) {
  return unwrapApiData(await request.get<ApiResponse<PackIncoming>>(`${API_BASE}/incoming`, { params: { ownerCode } }))
}

/** 扫托盘标签或箱标签找到要签收的东西；扫在路上的托盘上的箱，同样返回整个托盘。 */
export async function lookupPackReceive(code: string) {
  return unwrapApiData(
    await request.get<ApiResponse<PackReceiveLookup>>(`${API_BASE}/receive/lookup`, { params: { code: code.trim() } }),
  )
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

/** 商品位置查询；allStores 只有店长 / 管理员可用（否则 403）。 */
export async function locatePackProduct(params: { ownerCode?: string; code: string; allStores?: boolean }) {
  return unwrapApiData(
    await request.get<ApiResponse<PackLocateResult>>(`${API_BASE}/locate`, {
      params: { ownerCode: params.ownerCode, code: params.code.trim(), allStores: params.allStores ? true : undefined },
    }),
  )
}

export async function listPackCarriers(includeInactive = false) {
  const response = await request.get<ApiResponse<PackCarrier[]>>(`${API_BASE}/carriers`, {
    params: { includeInactive: includeInactive ? true : undefined },
  })
  return unwrapApiData(response) ?? []
}

/** 新增（不传 carrierGuid，任何人可现场新增）或修改运输公司（只有管理员）；停用即 isActive=false 保存。 */
export async function savePackCarrier(carrierGuid: string | null, payload: PackCarrierSaveRequest) {
  const response = carrierGuid
    ? await request.put<ApiResponse<PackCarrier>>(`${API_BASE}/carriers/${segment(carrierGuid)}`, payload)
    : await request.post<ApiResponse<PackCarrier>>(`${API_BASE}/carriers`, payload)
  return unwrapApiData(response)
}

/** 存放位置：分店返回本店自己维护的位置，总仓返回现有货位。 */
export async function listPackLocations(ownerCode?: string, includeInactive = false) {
  const response = await request.get<ApiResponse<PackLocation[]>>(`${API_BASE}/locations`, {
    params: { ownerCode, includeInactive: includeInactive ? true : undefined },
  })
  return unwrapApiData(response) ?? []
}

/** 新增（不传 locationGuid）或修改存放位置（只有店长）；停用即 isActive=false 保存。 */
export async function savePackLocation(locationGuid: string | null, payload: PackLocationSaveRequest) {
  const response = locationGuid
    ? await request.put<ApiResponse<PackLocation>>(`${API_BASE}/locations/${segment(locationGuid)}`, payload)
    : await request.post<ApiResponse<PackLocation>>(`${API_BASE}/locations`, payload)
  return unwrapApiData(response)
}
