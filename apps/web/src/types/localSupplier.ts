export interface LocalSupplierDto {
  guid: string
  localSupplierCode: string
  name: string
  status: number
  contactPerson?: string
  phone?: string
  email?: string
  remark?: string
  imageBaseUrl?: string
  /** 最近一张进货单的订单日期（按当前账号可见分店计算；只有分页列表返回）。 */
  lastPurchaseDate?: string | null
  lastPurchaseStoreCode?: string | null
  lastPurchaseStoreName?: string | null
}

/** 商品图片模板筛选：configured = 已单独配置；missing = 未配置（可能在用内置预设）。 */
export type LocalSupplierImageTemplateFilter = 'configured' | 'missing'

export interface LocalSupplierQueryDto {
  pageIndex?: number
  pageSize?: number
  keyword?: string
  status?: string
  sortBy?: string
  sortOrder?: 'ascend' | 'descend'
  imageTemplate?: LocalSupplierImageTemplateFilter
}

export interface CreateLocalSupplierDto {
  name: string
  status?: number
  contactPerson?: string
  phone?: string
  email?: string
  remark?: string
  imageBaseUrl?: string
}

export interface UpdateLocalSupplierDto extends CreateLocalSupplierDto {}

export interface SyncLocalSupplierResult {
  createdCount: number
  updatedCount: number
  deactivatedCount: number
  skippedCount: number
  errors: string[]
}
