import type { ApiResponse } from '../types/api'
import type {
  CreateFaceCameraRequest,
  FaceCamera,
  FaceCameraList,
  FaceCameraSecretResult,
  FaceCapture,
  FaceCaptureImageKind,
  FaceCaptureImageMeta,
  FaceCaptureImageRect,
  FaceCapturePage,
  FaceCaptureParseStatus,
  UpdateFaceCameraRequest,
} from '../types/faceCamera'
import request, { RequestError, unwrapApiData } from '../utils/request'

// 门店人脸抓拍摄像机管理接口。返回 ApiResponse 信封，字段 camelCase；权限 FaceCapture.Cameras.Manage。
const API_BASE = '/api/react/v1/face-cameras'

/**
 * 安全约定（新增 / 轮换 token 两个接口）：
 * 1. 响应体里的 token / cameraUrl 只经本文件的归一化函数原样返回给调用方，不写日志、不进缓存、不拼进任何错误信息。
 *    utils/request.ts 对成功响应不做任何上报；失败响应只上报 success/message/code 摘要，且失败响应里本来就没有 token。
 * 2. 请求统一带 cache: 'no-store'，不让浏览器 HTTP 缓存保留响应（服务端同样返回 Cache-Control: no-store）。
 * 3. 本文件禁止出现 console.*，由 sourceContract 测试静态守住。
 */
const NO_STORE = { cache: 'no-store' } as const
const API_BASE_URL = (((import.meta as ImportMeta & { env?: ImportMetaEnv }).env?.VITE_API_BASE_URL) || '').trim()

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : {}
}

function asString(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback
}

function asNullableString(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value : null
}

function asCount(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

/** 解析状态只认 0~3；未知值按「未解析」处理，界面不崩。 */
function asParseStatus(value: unknown): FaceCaptureParseStatus {
  return value === 0 || value === 1 || value === 2 || value === 3 ? value : 2
}

export function normalizeFaceCamera(value: unknown): FaceCamera {
  const raw = asRecord(value)
  return {
    cameraId: asString(raw.cameraId),
    storeCode: asString(raw.storeCode),
    storeName: asNullableString(raw.storeName),
    deviceModel: asNullableString(raw.deviceModel),
    macAddress: asNullableString(raw.macAddress),
    note: asNullableString(raw.note),
    enabled: raw.enabled === true,
    createdAtUtc: asString(raw.createdAtUtc),
    updatedAtUtc: asString(raw.updatedAtUtc),
    lastSeenAtUtc: asNullableString(raw.lastSeenAtUtc),
    online: raw.online === true,
    captureCount24h: asCount(raw.captureCount24h),
    faceCount24h: asCount(raw.faceCount24h),
    parseFailureCount24h: asCount(raw.parseFailureCount24h),
    lastEventAtUtc: asNullableString(raw.lastEventAtUtc),
  }
}

export function normalizeFaceCameraList(value: unknown): FaceCameraList {
  const raw = asRecord(value)
  return {
    items: Array.isArray(raw.items) ? raw.items.map(normalizeFaceCamera) : [],
    generatedAtUtc: asString(raw.generatedAtUtc),
    onlineWindowMinutes: asCount(raw.onlineWindowMinutes) || 10,
    statsWindowHours: asCount(raw.statsWindowHours) || 24,
  }
}

/**
 * 归一化新增 / 轮换响应。缺 token 或地址时抛固定文案的普通错误：
 * 绝不把响应体、已收到的任何字段放进错误信息或错误属性里。
 */
export function normalizeFaceCameraSecretResult(value: unknown): FaceCameraSecretResult {
  const raw = asRecord(value)
  const token = asString(raw.token)
  const cameraUrl = asString(raw.cameraUrl)
  if (!token || !cameraUrl) {
    throw new Error('FACE_CAMERA_SECRET_RESPONSE_INVALID')
  }
  return { camera: normalizeFaceCamera(raw.camera), token, cameraUrl }
}

function asNullableNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function asImageKind(value: unknown): FaceCaptureImageKind {
  return value === 0 || value === 1 ? value : 2
}

function normalizeImageRect(value: unknown): FaceCaptureImageRect | null {
  const raw = asRecord(value)
  const x = asNullableNumber(raw.x)
  const y = asNullableNumber(raw.y)
  const w = asNullableNumber(raw.w)
  const h = asNullableNumber(raw.h)
  return x === null || y === null || w === null || h === null ? null : { x, y, w, h }
}

export function normalizeFaceCaptureImage(value: unknown): FaceCaptureImageMeta {
  const raw = asRecord(value)
  return {
    imageId: asString(raw.imageId),
    kind: asImageKind(raw.kind),
    storage: asString(raw.storage).trim().toLowerCase(),
    width: asNullableNumber(raw.width),
    height: asNullableNumber(raw.height),
    byteSize: asCount(raw.byteSize),
    faceIndex: asNullableNumber(raw.faceIndex),
    rect: normalizeImageRect(raw.rect),
    rectNormalized: typeof raw.rectNormalized === 'boolean' ? raw.rectNormalized : null,
    faceScore: asNullableNumber(raw.faceScore),
  }
}

export function normalizeFaceCapture(value: unknown, canViewImages = false): FaceCapture {
  const raw = asRecord(value)
  return {
    captureId: String(raw.captureId ?? ''),
    receivedAtUtc: asString(raw.receivedAtUtc),
    cameraEventTime: asNullableString(raw.cameraEventTime),
    eventType: asNullableString(raw.eventType),
    faceCount: asCount(raw.faceCount),
    parseStatus: asParseStatus(raw.parseStatus),
    parseNote: asNullableString(raw.parseNote),
    isDuplicate: raw.isDuplicate === true,
    // 没有查看权限时即使响应里带了 images 也丢弃（后端本就返回空数组，这里再保险一层）。
    images:
      canViewImages && Array.isArray(raw.images)
        ? raw.images.map(normalizeFaceCaptureImage).filter((image) => image.imageId)
        : [],
  }
}

export function normalizeFaceCapturePage(value: unknown): FaceCapturePage {
  const raw = asRecord(value)
  const canViewImages = raw.canViewImages === true
  return {
    items: Array.isArray(raw.items) ? raw.items.map((item) => normalizeFaceCapture(item, canViewImages)) : [],
    page: asCount(raw.page) || 1,
    pageSize: asCount(raw.pageSize) || 20,
    hasMore: raw.hasMore === true,
    canViewImages,
    // 只认三个已知取值；缺失 / 未知（老后端、没有档案行）一律 null，页面不显示模式提示。
    storeMode: raw.storeMode === 'off' || raw.storeMode === 'capture' || raw.storeMode === 'match' ? raw.storeMode : null,
    lastSeenAtUtc: asNullableString(raw.lastSeenAtUtc),
  }
}

function cameraPath(cameraId: string) {
  return `${API_BASE}/${encodeURIComponent(cameraId)}`
}

export async function getFaceCameras(signal?: AbortSignal): Promise<FaceCameraList> {
  const response = await request.get<ApiResponse<unknown> | unknown>(API_BASE, { signal, ...NO_STORE })
  return normalizeFaceCameraList(unwrapApiData(response))
}

export async function createFaceCamera(payload: CreateFaceCameraRequest): Promise<FaceCameraSecretResult> {
  const response = await request.post<ApiResponse<unknown> | unknown>(API_BASE, payload, NO_STORE)
  return normalizeFaceCameraSecretResult(unwrapApiData(response))
}

export async function updateFaceCamera(cameraId: string, payload: UpdateFaceCameraRequest): Promise<FaceCamera> {
  const response = await request.put<ApiResponse<unknown> | unknown>(cameraPath(cameraId), payload, NO_STORE)
  return normalizeFaceCamera(unwrapApiData(response))
}

export async function rotateFaceCameraToken(cameraId: string): Promise<FaceCameraSecretResult> {
  const response = await request.post<ApiResponse<unknown> | unknown>(
    `${cameraPath(cameraId)}/rotate-token`,
    undefined,
    NO_STORE,
  )
  return normalizeFaceCameraSecretResult(unwrapApiData(response))
}

export async function getFaceCameraCaptures(
  cameraId: string,
  page: number,
  pageSize: number,
  signal?: AbortSignal,
): Promise<FaceCapturePage> {
  const response = await request.get<ApiResponse<unknown> | unknown>(`${cameraPath(cameraId)}/captures`, {
    params: { page, pageSize },
    signal,
    ...NO_STORE,
  })
  return normalizeFaceCapturePage(unwrapApiData(response))
}

// 路径里的编号：摄像机编号为小写字母数字横线，抓拍 / 图片编号为 1~32 位字母数字。
// 发请求前先校验，避免 ".." 之类被浏览器当成路径穿越而指向别的接口（编号来自服务端数据，这是第二道防线）。
const IMAGE_PATH_CAMERA_ID = /^[a-z0-9][a-z0-9-]{0,63}$/
const IMAGE_PATH_ID = /^[A-Za-z0-9]{1,32}$/
const IMAGE_CONTENT_TYPES = ['image/jpeg', 'image/png']

/**
 * 按需读取一张抓拍图片（敏感生物识别信息）。
 * 1. 请求层 request 只解析 JSON / 文本，会把二进制弄坏，所以这里单独用 fetch（带登录 Cookie），其余约定一致：no-store；
 * 2. 成功时只认 image/jpeg、image/png，返回 Blob 由调用方转 objectURL 显示；响应体绝不写入缓存、存储、日志；
 * 3. 失败时抛 RequestError（status + 统一信封 payload），由 imageLogic.classifyFaceImageError 按错误码分类，不读取其他内容。
 */
export async function fetchFaceCaptureImage(
  cameraId: string,
  captureId: string,
  imageId: string,
  signal?: AbortSignal,
): Promise<Blob> {
  if (!IMAGE_PATH_CAMERA_ID.test(cameraId) || !IMAGE_PATH_ID.test(captureId) || !IMAGE_PATH_ID.test(imageId)) {
    throw new RequestError('FACE_IMAGE_NOT_FOUND', 404, { success: false, errorCode: 'FACE_IMAGE_NOT_FOUND' })
  }
  const url = `${API_BASE_URL}${cameraPath(cameraId)}/captures/${captureId}/images/${imageId}/content`.replace(/([^:]\/)\/+/g, '$1')
  const response = await fetch(url, {
    method: 'GET',
    credentials: 'include',
    headers: { Accept: IMAGE_CONTENT_TYPES.join(', ') },
    signal,
    ...NO_STORE,
  })
  const contentType = (response.headers.get('content-type') || '').toLowerCase()
  if (response.ok && IMAGE_CONTENT_TYPES.some((type) => contentType.startsWith(type))) {
    return await response.blob()
  }
  const payload: unknown = contentType.includes('json') ? await response.json().catch(() => null) : null
  const message = asString(asRecord(payload).message) || `请求失败 (${response.status})`
  throw new RequestError(message, response.ok ? 502 : response.status, payload)
}
