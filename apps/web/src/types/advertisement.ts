export type AdvertisementMediaType = 'Image' | 'Video'

/**
 * 广告版式：决定客显在哪个广告位播放。
 * - Landscape 横版：只在空闲全屏播放
 * - Portrait 竖版：只在收银时右侧广告位播放
 * - Any 通用：两处都播（旧广告一律为 Any）
 */
export type AdvertisementOrientation = 'Landscape' | 'Portrait' | 'Any'

export interface AdvertisementStoreItemDto {
  storeCode: string
  storeName?: string
}

/** 广告后台分店选择器的选项（GET /api/react/v1/advertisements/store-options）。 */
export interface AdvertisementStoreOptionDto {
  storeCode: string
  storeName?: string | null
  brandName?: string | null
}

export interface AdvertisementListDto {
  id: string
  title: string
  description?: string
  mediaType: AdvertisementMediaType
  mediaUrl: string
  thumbnailUrl?: string
  objectKey?: string
  originalFileName?: string
  contentType?: string
  fileSize?: number
  /** 服务端对旧数据也返回 Any；service 层对缺省/未知值统一兜底为 Any。 */
  orientation: AdvertisementOrientation
  /** 素材像素宽高，旧数据或读取失败时为 null。 */
  mediaWidth?: number | null
  mediaHeight?: number | null
  effectiveStart: string
  effectiveEnd: string
  isEnabled: boolean
  sortOrder: number
  stores: AdvertisementStoreItemDto[]
}

export type AdvertisementDetailDto = AdvertisementListDto

export interface CreateAdvertisementDto {
  title: string
  description?: string
  mediaType: AdvertisementMediaType
  mediaUrl: string
  thumbnailUrl?: string
  objectKey?: string
  originalFileName?: string
  contentType?: string
  fileSize?: number
  orientation: AdvertisementOrientation
  /** 素材像素宽高：两者要么都有、要么都为 null。 */
  mediaWidth: number | null
  mediaHeight: number | null
  effectiveStart: string
  effectiveEnd: string
  isEnabled: boolean
  sortOrder: number
  stores: AdvertisementStoreItemDto[]
}

export type UpdateAdvertisementDto = CreateAdvertisementDto

export interface AdvertisementGridResult {
  items: AdvertisementListDto[]
  total: number
}

export interface AdvertisementUploadSignatureRequest {
  fileName: string
  contentType: string
  fileSize: number
  mediaType: AdvertisementMediaType
}

export interface AdvertisementUploadSignatureResponse {
  url: string
  mediaUrl?: string
  uploadUrl?: string
  objectKey: string
  headers?: Record<string, string>
}

export interface AdvertisementPayloadInput {
  title: string
  description?: string | null
  mediaType: AdvertisementMediaType
  mediaUrl: string
  thumbnailUrl?: string | null
  objectKey?: string | null
  originalFileName?: string | null
  contentType?: string | null
  fileSize?: number | null
  orientation?: AdvertisementOrientation | string | null
  mediaWidth?: number | null
  mediaHeight?: number | null
  effectiveStart: string | { toISOString: () => string }
  effectiveEnd: string | { toISOString: () => string }
  isEnabled?: boolean
  sortOrder?: number | null
  stores?: Array<string | AdvertisementStoreItemDto>
}
