import type { ApiResponse, PagedResult } from '../types/api'
import type {
  AddUserToStoreDto,
  BatchUpdateStoresRequest,
  BatchUpdateStoresResult,
  CreateStoreDto,
  StoreDetailDto,
  StoreDto,
  StoreQueryDto,
  StoreUserDto,
  StoreUserQueryDto,
  UpdateStoreDto,
} from '../types/store'
import request, { RequestError, unwrapApiData, unwrapPagedResult } from '../utils/request'
import { sortStoreOptionsByName } from '../utils/managedStoreScope'

export interface StoreOption {
  label: string
  value: string
}

type StoreUserApiDto = Omit<StoreUserDto, 'isManageable'> & {
  isManageable?: boolean
  isPrimary?: boolean
}

type StoreDetailApiDto = Omit<StoreDetailDto, 'users'> & {
  users?: StoreUserApiDto[]
}

const mapStoreUser = (user: StoreUserApiDto): StoreUserDto => {
  const { isPrimary, isManageable, ...rest } = user
  return {
    ...rest,
    isManageable: isManageable ?? isPrimary ?? false,
  }
}

const mapStoreDetail = (store: StoreDetailApiDto): StoreDetailDto => ({
  ...store,
  users: store.users?.map(mapStoreUser),
})

export async function getStores(params: StoreQueryDto): Promise<PagedResult<StoreDto>> {
  const response = await request.get<ApiResponse<PagedResult<StoreDto>>>('/api/stores', {
    params: params as Record<string, unknown>,
  })
  return unwrapPagedResult(response)
}

export async function getActiveStores(): Promise<StoreOption[]> {
  const response = await request.get<ApiResponse<StoreDto[]> | StoreDto[]>('/api/stores/active')
  const stores = Array.isArray(response)
    ? response
    : Array.isArray(response.data)
      ? response.data
      : []

  return sortStoreOptionsByName(
    stores.map((store) => ({
      label: store.storeName || store.storeCode,
      value: store.storeCode,
    })),
  )
}

export interface BrandedStoreOption extends StoreOption {
  /** 分店品牌（Hot Bargain / Discount General 等），未设置时为 undefined。 */
  brandName?: string
}

/**
 * 与 getActiveStores 同源，但额外带上品牌名，供需要按品牌筛选分店的页面使用。
 * 单独成函数而不是给 getActiveStores 加字段：后者有十几个调用方，且部分测试做整对象比较。
 */
export async function getActiveStoresWithBrand(): Promise<BrandedStoreOption[]> {
  const response = await request.get<ApiResponse<StoreDto[]> | StoreDto[]>('/api/stores/active')
  const stores = Array.isArray(response)
    ? response
    : Array.isArray(response.data)
      ? response.data
      : []

  return sortStoreOptionsByName(
    stores.map((store) => ({
      label: store.storeName || store.storeCode,
      value: store.storeCode,
      brandName: store.brandName?.trim() || undefined,
    })),
  )
}

export async function getStoreByGuid(guid: string): Promise<StoreDetailDto> {
  const response = await request.get<ApiResponse<StoreDetailApiDto>>(`/api/stores/guid/${guid}`)
  return mapStoreDetail(unwrapApiData(response))
}

export async function getNextStoreCode(): Promise<string> {
  const response = await request.get<ApiResponse<string>>('/api/stores/next-code')
  if (response.success === false) {
    throw new RequestError(response.message || '获取下一个分店编码失败', 400, response)
  }
  return unwrapApiData(response)
}

export async function createStore(payload: CreateStoreDto): Promise<StoreDetailDto> {
  const response = await request.post<ApiResponse<StoreDetailApiDto>>('/api/stores', payload)
  if (response.success === false) {
    throw new RequestError(response.message || '创建分店失败', 400, response)
  }
  return mapStoreDetail(unwrapApiData(response))
}

export async function updateStore(guid: string, payload: UpdateStoreDto): Promise<StoreDetailDto> {
  const response = await request.put<ApiResponse<StoreDetailApiDto>>(`/api/stores/guid/${guid}`, payload)
  return mapStoreDetail(unwrapApiData(response))
}

export async function batchUpdateStores(
  payload: BatchUpdateStoresRequest,
): Promise<BatchUpdateStoresResult> {
  const response = await request.patch<ApiResponse<BatchUpdateStoresResult>>(
    '/api/stores/batch',
    payload,
  )
  return unwrapApiData(response)
}

export async function syncStoreToHq(guid: string): Promise<boolean> {
  const response = await request.post<ApiResponse<boolean>>(`/api/stores/guid/${guid}/sync-hq`)
  if (response.success === false) {
    throw new RequestError(response.message || '同步HQ分店失败', 400, response)
  }
  return unwrapApiData(response)
}

export async function getStoreUsers(params: {
  storeGuid: string
  query?: StoreUserQueryDto
}): Promise<PagedResult<StoreUserDto>> {
  const response = await request.get<ApiResponse<PagedResult<StoreUserApiDto>>>(
    `/api/stores/guid/${params.storeGuid}/users`,
    {
      params: params.query as Record<string, unknown> | undefined,
    },
  )
  const result = unwrapPagedResult(response)
  return {
    ...result,
    items: result.items.map(mapStoreUser),
  }
}

export async function addUserToStore(storeGuid: string, payload: AddUserToStoreDto): Promise<boolean> {
  const response = await request.post<ApiResponse<boolean>>(`/api/stores/guid/${storeGuid}/users`, {
    UserGUID: payload.userGUID,
    IsPrimary: payload.isManageable ?? false,
  })
  return unwrapApiData(response)
}

export async function removeUserFromStore(storeGuid: string, userGuid: string): Promise<boolean> {
  const response = await request.delete<ApiResponse<boolean>>(`/api/stores/guid/${storeGuid}/users/${userGuid}`)
  return unwrapApiData(response)
}

export async function setStoreUserManageable(storeGuid: string, userGuid: string, isManageable: boolean): Promise<boolean> {
  const response = await request.put<ApiResponse<boolean>>(
    `/api/stores/guid/${storeGuid}/users/${userGuid}/primary`,
    isManageable,
  )
  return unwrapApiData(response)
}
