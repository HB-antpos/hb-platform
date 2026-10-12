import type { ApiResponse } from '../types/api'
import type {
  CreateFaceCameraRequest,
  FaceCamera,
  FaceCameraList,
  FaceCameraSecretResult,
  FaceCapture,
  FaceCapturePage,
  FaceCaptureParseStatus,
  UpdateFaceCameraRequest,
} from '../types/faceCamera'
import request, { unwrapApiData } from '../utils/request'

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

export function normalizeFaceCapture(value: unknown): FaceCapture {
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
  }
}

export function normalizeFaceCapturePage(value: unknown): FaceCapturePage {
  const raw = asRecord(value)
  return {
    items: Array.isArray(raw.items) ? raw.items.map(normalizeFaceCapture) : [],
    page: asCount(raw.page) || 1,
    pageSize: asCount(raw.pageSize) || 20,
    hasMore: raw.hasMore === true,
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
