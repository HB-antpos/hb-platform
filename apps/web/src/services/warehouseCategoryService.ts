import request from '../utils/request'

export interface WarehouseCategoryNode {
  categoryGUID: string
  parentGUID?: string
  categoryName: string
  chineseName?: string
  isActive: boolean
  sortOrder?: number
  remarks?: string
  createdAt?: string
  updatedAt?: string
  children?: WarehouseCategoryNode[]
}

export interface SaveWarehouseCategoryPayload {
  parentGUID?: string
  categoryName: string
  chineseName?: string
  isActive?: boolean
  remarks?: string
}

export interface WarehouseCategoryProductQuery {
  categoryGuid: string
  page: number
  pageSize: number
  productName?: string
  itemNumber?: string
  supplierCode?: string
  isActive?: boolean
}

export interface WarehouseCategoryProductItem {
  productCode: string
  productBaseName?: string
  itemNumber?: string
  domesticSupplierCode?: string
  domesticSupplierName?: string
  localSupplierCode?: string
  localSupplierName?: string
  productCategoryGUID?: string
  productCategoryName?: string
  productBarcode?: string
  productImage?: string
  domesticPrice?: number
  volume?: number
  isActive: boolean
  createdAt?: string
  updatedAt?: string
}

export interface WarehouseCategoryProductResult {
  items: WarehouseCategoryProductItem[]
  total: number
  page: number
  pageSize: number
}

interface ReactApiResponse<T> {
  success?: boolean
  isSuccess?: boolean
  data?: T
  message?: string
}

type WarehouseCategoryApiItem = Record<string, unknown>
type WarehouseCategoryProductApiItem = Record<string, unknown>
type WarehouseCategoryProductPagedApi = {
  items?: WarehouseCategoryProductApiItem[]
  total?: number
  totalCount?: number
  page?: number
  pageIndex?: number
  pageNumber?: number
  pageSize?: number
}

function unwrapReactData<T>(payload: ReactApiResponse<T> | T, fallbackMessage: string): T {
  if (payload && typeof payload === 'object' && 'success' in payload) {
    const result = payload as ReactApiResponse<T>
    if (result.success === false) {
      throw new Error(result.message || fallbackMessage)
    }
    if (result.data === undefined) {
      throw new Error(result.message || fallbackMessage)
    }
    return result.data
  }

  return payload as T
}

function ensureReactSuccess(payload: ReactApiResponse<unknown> | unknown, fallbackMessage: string) {
  if (payload && typeof payload === 'object' && ('success' in payload || 'isSuccess' in payload)) {
    const result = payload as ReactApiResponse<unknown>
    if ((result.success ?? result.isSuccess) === false) {
      throw new Error(result.message || fallbackMessage)
    }
  }
}

function extractAffectedCount(payload: ReactApiResponse<unknown> | unknown) {
  if (!payload || typeof payload !== 'object') {
    return 0
  }

  const data = 'data' in payload ? (payload as ReactApiResponse<unknown>).data : payload
  if (!data || typeof data !== 'object') {
    return 0
  }

  const affected = (data as Record<string, unknown>).affected ?? (data as Record<string, unknown>).Affected
  return typeof affected === 'number' && Number.isFinite(affected) ? affected : 0
}

function transformCategory(raw: WarehouseCategoryApiItem): WarehouseCategoryNode {
  const childrenRaw = raw.children ?? raw.Children

  return {
    categoryGUID: String(raw.categoryGUID ?? raw.CategoryGUID ?? raw.CategoryGuid ?? ''),
    parentGUID: String(raw.parentGUID ?? raw.ParentGUID ?? raw.ParentGuid ?? '') || undefined,
    categoryName: String(raw.categoryName ?? raw.CategoryName ?? ''),
    chineseName: String(raw.chineseName ?? raw.ChineseName ?? '') || undefined,
    isActive: Boolean(raw.isActive ?? raw.IsActive),
    sortOrder:
      raw.sortOrder === undefined || raw.sortOrder === null ? undefined : Number(raw.sortOrder ?? raw.SortOrder),
    remarks: String(raw.remarks ?? raw.Remarks ?? '') || undefined,
    createdAt: String(raw.createdAt ?? raw.CreatedAt ?? '') || undefined,
    updatedAt: String(raw.updatedAt ?? raw.UpdatedAt ?? '') || undefined,
    children: Array.isArray(childrenRaw)
      ? childrenRaw
          .filter((item): item is WarehouseCategoryApiItem => !!item && typeof item === 'object')
          .map(transformCategory)
      : [],
  }
}

function toPayload(data: SaveWarehouseCategoryPayload) {
  return {
    parentGUID: data.parentGUID,
    categoryName: data.categoryName,
    chineseName: data.chineseName,
    isActive: data.isActive ?? true,
    remarks: data.remarks,
  }
}

function transformProduct(raw: WarehouseCategoryProductApiItem): WarehouseCategoryProductItem {
  return {
    productCode: String(raw.productCode ?? raw.ProductCode ?? ''),
    productBaseName:
      String(raw.productBaseName ?? raw.ProductBaseName ?? raw.productName ?? raw.ProductName ?? '') || undefined,
    itemNumber: String(raw.itemNumber ?? raw.ItemNumber ?? '') || undefined,
    domesticSupplierCode:
      String(raw.domesticSupplierCode ?? raw.DomesticSupplierCode ?? raw.supplierCode ?? raw.SupplierCode ?? '')
      || undefined,
    domesticSupplierName:
      String(raw.domesticSupplierName ?? raw.DomesticSupplierName ?? raw.supplierName ?? raw.SupplierName ?? '')
      || undefined,
    localSupplierCode: String(raw.localSupplierCode ?? raw.LocalSupplierCode ?? '') || undefined,
    localSupplierName: String(raw.localSupplierName ?? raw.LocalSupplierName ?? '') || undefined,
    productCategoryGUID:
      String(raw.productCategoryGUID ?? raw.ProductCategoryGUID ?? '') || undefined,
    productCategoryName:
      String(raw.productCategoryName ?? raw.ProductCategoryName ?? raw.categoryName ?? raw.CategoryName ?? '')
      || undefined,
    productBarcode: String(raw.productBarcode ?? raw.ProductBarcode ?? raw.barcode ?? raw.Barcode ?? '') || undefined,
    productImage: String(raw.productImage ?? raw.ProductImage ?? '') || undefined,
    domesticPrice:
      raw.domesticPrice === undefined || raw.domesticPrice === null
        ? undefined
        : Number(raw.domesticPrice ?? raw.DomesticPrice),
    volume:
      raw.volume === undefined || raw.volume === null ? undefined : Number(raw.volume ?? raw.Volume),
    isActive: Boolean(raw.isActive ?? raw.IsActive),
    createdAt: String(raw.createdAt ?? raw.CreatedAt ?? '') || undefined,
    updatedAt: String(raw.updatedAt ?? raw.UpdatedAt ?? '') || undefined,
  }
}

export async function getCategoryTree(): Promise<WarehouseCategoryNode[]> {
  const response = await request.get<ReactApiResponse<WarehouseCategoryApiItem[]> | WarehouseCategoryApiItem[]>(
    '/api/react/v1/warehouse-categories/tree',
  )
  const result = unwrapReactData(response, '加载分类树失败')
  return Array.isArray(result)
    ? result
        .filter((item): item is WarehouseCategoryApiItem => !!item && typeof item === 'object')
        .map(transformCategory)
    : []
}

export async function createWarehouseCategory(data: SaveWarehouseCategoryPayload): Promise<WarehouseCategoryNode> {
  const response = await request.post<ReactApiResponse<WarehouseCategoryApiItem> | WarehouseCategoryApiItem>(
    '/api/react/v1/warehouse-categories',
    toPayload(data),
  )
  return transformCategory(unwrapReactData(response, '创建分类失败') as WarehouseCategoryApiItem)
}

export async function updateWarehouseCategory(
  categoryGuid: string,
  data: SaveWarehouseCategoryPayload,
): Promise<WarehouseCategoryNode> {
  const response = await request.put<ReactApiResponse<WarehouseCategoryApiItem> | WarehouseCategoryApiItem>(
    `/api/react/v1/warehouse-categories/${categoryGuid}`,
    // 后端 UpdateWarehouseCategoryDto 把 CategoryGUID 标为必填，模型校验先于控制器执行；
    // 请求体不带它会直接 400。控制器仍以路由里的 GUID 为准覆盖该字段。
    { ...toPayload(data), categoryGuid },
  )
  return transformCategory(unwrapReactData(response, '更新分类失败') as WarehouseCategoryApiItem)
}

export async function deleteWarehouseCategory(categoryGuid: string): Promise<boolean> {
  const response = await request.delete<ReactApiResponse<boolean> | boolean>(
    `/api/react/v1/warehouse-categories/${categoryGuid}`,
  )
  // 删除接口成功时只返回 success，没有 data；不能用需要 data 的通用解包逻辑误报失败。
  ensureReactSuccess(response, '删除分类失败')
  if (response && typeof response === 'object' && ('success' in response || 'isSuccess' in response)) {
    const result = response as ReactApiResponse<boolean>
    return Boolean(result.success ?? result.isSuccess)
  }
  return Boolean(response)
}

export async function batchAssignProducts(categoryGuid: string, productCodes: string[]): Promise<number> {
  const response = await request.post<ReactApiResponse<unknown> | unknown>(
    `/api/react/v1/warehouse-categories/${categoryGuid}/products/batch-assign`,
    {
      CategoryGuid: categoryGuid,
      ProductCodes: productCodes,
    },
  )
  // 批量更新属于强业务操作，HTTP 200 但 success/isSuccess=false 也必须阻断成功提示。
  ensureReactSuccess(response, '批量更新商品分类失败')
  // 后端返回实际影响行数，调用方可据此避免“接口成功但没有商品被更新”的假成功。
  return extractAffectedCount(response)
}

export async function getWarehouseCategoryProducts(
  params: WarehouseCategoryProductQuery,
): Promise<WarehouseCategoryProductResult> {
  const response = await request.get<ReactApiResponse<WarehouseCategoryProductPagedApi> | WarehouseCategoryProductPagedApi>(
    `/api/react/v1/warehouse-categories/${params.categoryGuid}/products`,
    {
      params: {
        pageNumber: params.page,
        pageSize: params.pageSize,
        productName: params.productName,
        itemNumber: params.itemNumber,
        supplierCode: params.supplierCode,
        isActive: params.isActive,
      },
    },
  )

  const result = unwrapReactData(response, '加载分类商品失败')

  return {
    items: Array.isArray(result.items)
      ? result.items
          .filter((item): item is WarehouseCategoryProductApiItem => !!item && typeof item === 'object')
          .map(transformProduct)
      : [],
    total: Number(result.total ?? result.totalCount ?? 0),
    page: Number(result.page ?? result.pageNumber ?? result.pageIndex ?? params.page),
    pageSize: Number(result.pageSize ?? params.pageSize),
  }
}
