export interface StoreDto {
  storeGUID: string
  storeName: string
  storeCode: string
  description?: string
  address?: string
  contactPhone?: string
  contactEmail?: string
  abn?: string
  brandName?: string
  timeZoneId?: string
  returnPolicy?: string
  /** 代金券使用说明（券面「VOUCHER TERMS」下方正文，一行一条）；未定制时服务端不返回，收银端按内置默认文案打印。 */
  voucherTerms?: string
  /** 分期条款（分期小票「INSTALLMENT TERMS」下方正文，一行一条）；同上。 */
  installmentTerms?: string
  isActive: boolean
  createdAt: string
  updatedAt: string
  totalUsers?: number
  activeUsers?: number
}

export interface StoreQueryDto {
  page?: number
  pageSize?: number
  search?: string
  isActive?: boolean
  brandName?: string
  timeZoneId?: string
  userGUID?: string
  sortField?: string
  sortOrder?: string
}

export interface CreateStoreDto {
  storeName: string
  storeCode: string
  description?: string
  address?: string
  contactPhone?: string
  contactEmail?: string
  abn?: string
  brandName?: string
  timeZoneId?: string
  returnPolicy?: string
  /** 代金券使用说明（券面「VOUCHER TERMS」下方正文，一行一条）；未定制时服务端不返回，收银端按内置默认文案打印。 */
  voucherTerms?: string
  /** 分期条款（分期小票「INSTALLMENT TERMS」下方正文，一行一条）；同上。 */
  installmentTerms?: string
  isActive?: boolean
}

export interface UpdateStoreDto {
  storeName: string
  storeCode: string
  description?: string
  address?: string
  contactPhone?: string
  contactEmail?: string
  abn?: string
  brandName?: string
  timeZoneId?: string
  returnPolicy?: string
  /** 代金券使用说明（券面「VOUCHER TERMS」下方正文，一行一条）；未定制时服务端不返回，收银端按内置默认文案打印。 */
  voucherTerms?: string
  /** 分期条款（分期小票「INSTALLMENT TERMS」下方正文，一行一条）；同上。 */
  installmentTerms?: string
  isActive?: boolean
}

export type StoreBatchUpdateField =
  | 'timeZoneId'
  | 'abn'
  | 'brandName'
  | 'isActive'
  | 'returnPolicy'
  | 'voucherTerms'
  | 'installmentTerms'

export interface BatchUpdateStoresRequest {
  storeGuids: string[]
  fields: StoreBatchUpdateField[]
  timeZoneId?: string
  abn?: string | null
  brandName?: string | null
  isActive?: boolean
  returnPolicy?: string | null
  voucherTerms?: string | null
  installmentTerms?: string | null
}

export interface BatchUpdateStoresResult {
  requestedCount: number
  updatedCount: number
  updatedStoreGuids: string[]
}

export interface StoreUserDto {
  userGUID: string
  username: string
  fullName?: string
  realName?: string
  email: string
  roles: string[]
  isManageable: boolean
  isActive: boolean
  assignedAt: string
}

export interface StoreDetailDto extends StoreDto {
  users?: StoreUserDto[]
}

export interface AddUserToStoreDto {
  userGUID: string
  isManageable?: boolean
}

export interface StoreUserQueryDto {
  page?: number
  pageSize?: number
  search?: string
  roleGuid?: string
  isActive?: boolean
  sortBy?: string
  sortDirection?: string
}
