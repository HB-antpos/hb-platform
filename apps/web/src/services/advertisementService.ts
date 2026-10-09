import type { ApiResponse } from '../types/api'
import type {
  AdvertisementDetailDto,
  AdvertisementGridResult,
  AdvertisementListDto,
  AdvertisementMediaType,
  AdvertisementOrientation,
  AdvertisementPayloadInput,
  AdvertisementUploadSignatureRequest,
  AdvertisementUploadSignatureResponse,
  CreateAdvertisementDto,
  UpdateAdvertisementDto,
} from '../types/advertisement'
import { reportExternalFetchError } from '../utils/centerLogClient'
import request, { unwrapApiData } from '../utils/request'

const API_BASE = '/api/react/v1/advertisements'

function toIsoString(value: string | { toISOString: () => string }) {
  return typeof value === 'string' ? value : value.toISOString()
}

export function stripAdvertisementMediaUrlQuery(rawUrl: string) {
  if (!rawUrl) {
    return ''
  }

  try {
    const parsedUrl = new URL(rawUrl)
    return `${parsedUrl.origin}${parsedUrl.pathname}`
  } catch {
    return rawUrl.split('?')[0]
  }
}

export function resolveAdvertisementMediaType(file: { type?: string; name?: string }): AdvertisementMediaType {
  const normalizedType = (file.type || '').toLowerCase()
  if (normalizedType.startsWith('video/')) {
    return 'Video'
  }

  const normalizedName = (file.name || '').toLowerCase()
  if (/\.(mp4|mov|m4v|webm|ogg)$/i.test(normalizedName)) {
    return 'Video'
  }

  return 'Image'
}

const ADVERTISEMENT_ORIENTATIONS: readonly AdvertisementOrientation[] = ['Landscape', 'Portrait', 'Any']

/**
 * 版式兜底：旧数据、缺省或未知值一律按 Any（通用）处理，保持与改版前相同的播放行为。
 * 大小写不敏感，避免服务端序列化风格差异导致整列显示错。
 */
export function normalizeAdvertisementOrientation(value: unknown): AdvertisementOrientation {
  if (typeof value !== 'string') {
    return 'Any'
  }
  const normalized = value.trim().toLowerCase()
  return ADVERTISEMENT_ORIENTATIONS.find((item) => item.toLowerCase() === normalized) ?? 'Any'
}

/** 合法像素尺寸（正整数）才保留；否则返回 null。 */
function toMediaDimension(value: unknown): number | null {
  const numeric = typeof value === 'number' ? value : Number(value)
  return value != null && Number.isFinite(numeric) && numeric > 0 ? Math.round(numeric) : null
}

/**
 * 素材宽高要么都有、要么都为 null（服务端契约）：任一缺失或非法时两者一起置 null。
 */
export function normalizeAdvertisementMediaSize(
  mediaWidth: unknown,
  mediaHeight: unknown,
): { mediaWidth: number | null; mediaHeight: number | null } {
  const width = toMediaDimension(mediaWidth)
  const height = toMediaDimension(mediaHeight)
  return width != null && height != null
    ? { mediaWidth: width, mediaHeight: height }
    : { mediaWidth: null, mediaHeight: null }
}

/** 列表 / 详情返回统一做版式与尺寸兜底，页面层只处理三种合法版式。 */
function normalizeAdvertisementDto<T extends AdvertisementListDto>(item: T): T {
  return {
    ...item,
    orientation: normalizeAdvertisementOrientation(item.orientation),
    ...normalizeAdvertisementMediaSize(item.mediaWidth, item.mediaHeight),
  }
}

export function buildAdvertisementUpsertPayload(
  input: AdvertisementPayloadInput,
): CreateAdvertisementDto {
  return {
    title: input.title.trim(),
    description: input.description?.trim() || undefined,
    mediaType: input.mediaType,
    mediaUrl: stripAdvertisementMediaUrlQuery(input.mediaUrl),
    thumbnailUrl: input.thumbnailUrl?.trim() || undefined,
    objectKey: input.objectKey?.trim() || undefined,
    originalFileName: input.originalFileName?.trim() || undefined,
    contentType: input.contentType?.trim() || undefined,
    fileSize: input.fileSize == null ? undefined : Number(input.fileSize),
    orientation: normalizeAdvertisementOrientation(input.orientation),
    ...normalizeAdvertisementMediaSize(input.mediaWidth, input.mediaHeight),
    effectiveStart: toIsoString(input.effectiveStart),
    effectiveEnd: toIsoString(input.effectiveEnd),
    isEnabled: input.isEnabled ?? true,
    sortOrder: Number(input.sortOrder ?? 0),
    stores: (input.stores ?? []).map((store) =>
      typeof store === 'string' ? { storeCode: store } : { storeCode: store.storeCode },
    ),
  }
}

export async function getAdvertisementGrid(data: Record<string, unknown>) {
  const response = await request.post<ApiResponse<AdvertisementGridResult>>(`${API_BASE}/grid`, data)
  const result = unwrapApiData(response)
  return result
    ? { ...result, items: (result.items ?? []).map(normalizeAdvertisementDto) }
    : result
}

export async function getAdvertisementById(id: string): Promise<AdvertisementDetailDto> {
  const response = await request.get<ApiResponse<AdvertisementDetailDto>>(`${API_BASE}/${id}`)
  const detail = unwrapApiData(response)
  return detail ? normalizeAdvertisementDto(detail) : detail
}

export async function createAdvertisement(data: CreateAdvertisementDto): Promise<AdvertisementDetailDto> {
  const response = await request.post<ApiResponse<AdvertisementDetailDto>>(API_BASE, data)
  return unwrapApiData(response)
}

export async function updateAdvertisement(id: string, data: UpdateAdvertisementDto): Promise<AdvertisementDetailDto> {
  const response = await request.put<ApiResponse<AdvertisementDetailDto>>(`${API_BASE}/${id}`, data)
  return unwrapApiData(response)
}

export async function deleteAdvertisement(id: string): Promise<void> {
  await request.delete(`${API_BASE}/${id}`)
}

export async function enableAdvertisement(id: string, enable: boolean): Promise<void> {
  await request.post(`${API_BASE}/${id}/enable?enable=${enable}`)
}

export async function requestAdvertisementUploadSignature(
  data: AdvertisementUploadSignatureRequest,
): Promise<AdvertisementUploadSignatureResponse> {
  const response = await request.post<ApiResponse<AdvertisementUploadSignatureResponse>>(
    `${API_BASE}/upload-signature`,
    data,
  )
  return unwrapApiData(response)
}

export async function uploadAdvertisementFile(
  signature: AdvertisementUploadSignatureResponse,
  file: File,
): Promise<string> {
  const uploadUrl = signature.url || signature.uploadUrl
  if (!uploadUrl) {
    throw new Error('Upload URL is empty')
  }

  const response = await fetch(uploadUrl, {
    method: 'PUT',
    headers: signature.headers,
    body: file,
  }).catch((error) => {
    reportExternalFetchError({
      url: uploadUrl,
      method: 'PUT',
      error,
    })
    throw error
  })

  if (!response.ok) {
    const uploadError = new Error(`Upload failed: ${response.status}`)
    // 上传失败日志必须旁路发送，不能等待、更不能影响原始上传报错。
    reportExternalFetchError({
      url: uploadUrl,
      method: 'PUT',
      statusCode: response.status,
      error: uploadError,
      responsePayload: {
        message: response.statusText || `HTTP ${response.status}`,
      },
    })
    throw uploadError
  }

  return stripAdvertisementMediaUrlQuery(signature.mediaUrl || uploadUrl)
}
