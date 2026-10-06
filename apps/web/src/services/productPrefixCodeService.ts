import type { ApiResponse, PagedResult } from '../types/api'
import type { ProductType } from '../types/domesticProduct'
import type {
  PrefixCodeListParams,
  PrefixCodeListResult,
  PrefixCodeProductItem,
  PrefixCodeProductsResult,
  ProductPrefixCodeItem,
  SavePrefixCodePayload,
} from '../types/productPrefixCode'
import request, { unwrapApiData, unwrapPagedResult } from '../utils/request'

const API_BASE = '/api/react/v1/product-prefix-codes'

type PrefixApiItem = Record<string, unknown>
type PrefixProductApiItem = Record<string, unknown>

function transformPrefixItem(raw: PrefixApiItem): ProductPrefixCodeItem {
  return {
    prefixCode: String(raw.prefixCode ?? raw.PrefixCode ?? ''),
    supplierCode: String(raw.supplierCode ?? raw.SupplierCode ?? ''),
    supplierName: String(raw.supplierName ?? raw.SupplierName ?? '') || undefined,
    prefixName: String(raw.prefixName ?? raw.PrefixName ?? ''),
    prefixDescription: String(raw.prefixDescription ?? raw.PrefixDescription ?? '') || undefined,
    isActive: Boolean(raw.isActive ?? raw.IsActive),
    sortOrder:
      raw.sortOrder === undefined || raw.sortOrder === null ? undefined : Number(raw.sortOrder ?? raw.SortOrder),
    createdAt: String(raw.createdAt ?? raw.CreatedAt ?? '') || undefined,
    updatedAt: String(raw.updatedAt ?? raw.UpdatedAt ?? '') || undefined,
  }
}

function transformPrefixProduct(raw: PrefixProductApiItem): PrefixCodeProductItem {
  return {
    productCode: String(raw.productCode ?? raw.ProductCode ?? ''),
    supplierCode: String(raw.supplierCode ?? raw.SupplierCode ?? '') || undefined,
    supplierName: String(raw.supplierName ?? raw.SupplierName ?? '') || undefined,
    productName: String(raw.productName ?? raw.ProductName ?? '') || undefined,
    englishProductName: String(raw.englishProductName ?? raw.EnglishProductName ?? '') || undefined,
    hbProductNo: String(raw.hbProductNo ?? raw.HBProductNo ?? '') || undefined,
    barcode: String(raw.barcode ?? raw.Barcode ?? '') || undefined,
    productSpecification: String(raw.productSpecification ?? raw.ProductSpecification ?? '') || undefined,
    productType: Number(raw.productType ?? raw.ProductType ?? 0) as ProductType,
    productTypeName: String(raw.productTypeName ?? raw.ProductTypeName ?? '') || undefined,
    domesticPrice:
      raw.domesticPrice === undefined || raw.domesticPrice === null ? undefined : Number(raw.domesticPrice ?? raw.DomesticPrice),
    oemPrice: raw.oemPrice === undefined || raw.oemPrice === null ? undefined : Number(raw.oemPrice ?? raw.OEMPrice),
    importPrice:
      raw.importPrice === undefined || raw.importPrice === null ? undefined : Number(raw.importPrice ?? raw.ImportPrice),
    packingQuantity:
      raw.packingQuantity === undefined || raw.packingQuantity === null
        ? undefined
        : Number(raw.packingQuantity ?? raw.PackingQuantity),
    unitVolume:
      raw.unitVolume === undefined || raw.unitVolume === null ? undefined : Number(raw.unitVolume ?? raw.UnitVolume),
    middlePackQuantity:
      raw.middlePackQuantity === undefined || raw.middlePackQuantity === null
        ? undefined
        : Number(raw.middlePackQuantity ?? raw.MiddlePackQuantity),
    packingSize: String(raw.packingSize ?? raw.PackingSize ?? '') || undefined,
    material: String(raw.material ?? raw.Material ?? '') || undefined,
    remarks: String(raw.remarks ?? raw.Remarks ?? '') || undefined,
    productImage: String(raw.productImage ?? raw.ProductImage ?? '') || undefined,
    isActive: Boolean(raw.isActive ?? raw.IsActive),
    createdAt: String(raw.createdAt ?? raw.CreatedAt ?? '') || undefined,
  }
}

function toPrefixPayload(data: SavePrefixCodePayload, includeSupplierCode: boolean) {
  return {
    ...(includeSupplierCode ? { supplierCode: data.supplierCode } : {}),
    prefixName: data.prefixName,
    prefixDescription: data.prefixDescription,
    isActive: data.isActive ?? true,
    // 排序号原样透传：表单有「排序」字段后不再补成 0（旧写法会让编辑/切换状态时悄悄把排序重置为 0）。
    // 未填时为 undefined，序列化时被丢弃，由后端按空值处理。
    sortOrder: data.sortOrder,
  }
}

export async function getPrefixCodeList(params: PrefixCodeListParams): Promise<PrefixCodeListResult> {
  const response = await request.get<ApiResponse<PagedResult<PrefixApiItem>>>(API_BASE, {
    params: {
      page: params.page ?? 1,
      pageSize: params.pageSize ?? 20,
      search: params.search,
      supplierCode: params.supplierCode,
      isActive: params.isActive,
      sortField: params.sortField,
      sortDirection: params.sortDirection,
    },
  })
  const result = unwrapPagedResult(response)
  return {
    items: result.items
      .filter((item): item is PrefixApiItem => !!item && typeof item === 'object')
      .map(transformPrefixItem),
    total: result.total,
    page: result.page,
    pageSize: result.pageSize,
  }
}

export async function createPrefixCode(data: SavePrefixCodePayload): Promise<ProductPrefixCodeItem> {
  const response = await request.post<ApiResponse<PrefixApiItem>>(API_BASE, toPrefixPayload(data, true))
  return transformPrefixItem(unwrapApiData(response) as PrefixApiItem)
}

export async function updatePrefixCode(prefixCode: string, data: SavePrefixCodePayload): Promise<ProductPrefixCodeItem> {
  const response = await request.put<ApiResponse<PrefixApiItem>>(`${API_BASE}/${encodeURIComponent(prefixCode)}`, toPrefixPayload(data, false))
  return transformPrefixItem(unwrapApiData(response) as PrefixApiItem)
}

export async function deletePrefixCode(prefixCode: string): Promise<boolean> {
  const response = await request.delete<ApiResponse<boolean>>(`${API_BASE}/${encodeURIComponent(prefixCode)}`)
  return unwrapApiData(response)
}

export async function getProductsByPrefix(
  prefixCode: string,
  params: { page?: number; pageSize?: number },
): Promise<PrefixCodeProductsResult> {
  const response = await request.get<ApiResponse<PagedResult<PrefixProductApiItem>>>(`${API_BASE}/${encodeURIComponent(prefixCode)}/products`, {
    params: {
      page: params.page ?? 1,
      pageSize: params.pageSize ?? 10,
    },
  })
  const result = unwrapPagedResult(response)
  return {
    items: result.items
      .filter((item): item is PrefixProductApiItem => !!item && typeof item === 'object')
      .map(transformPrefixProduct),
    total: result.total,
    page: result.page,
    pageSize: result.pageSize,
  }
}
