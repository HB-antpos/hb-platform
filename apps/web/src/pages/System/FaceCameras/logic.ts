import dayjs from 'dayjs'

import type {
  CreateFaceCameraRequest,
  FaceCamera,
  FaceCameraFields,
  FaceCaptureParseStatus,
  UpdateFaceCameraRequest,
} from '../../../types/faceCamera'
import { RequestError } from '../../../utils/request'

// 门店人脸抓拍摄像机管理页的纯逻辑：状态判定、表单校验（与后端字段规则一致）、请求体规范化、
// 摄像机地址脱敏、一次性 token 展示状态机、复制到剪贴板、分页。页面与测试共用。

// ───────────── 字段规则（与后端一致） ─────────────

/** CameraId：小写字母数字横线，不以横线开头，最长 64（后端 ^[a-z0-9][a-z0-9-]{0,63}\z）。 */
export const CAMERA_ID_PATTERN = /^[a-z0-9][a-z0-9-]{0,63}$/
/** StoreCode 只允许字母数字下划线横线。 */
export const STORE_CODE_PATTERN = /^[A-Za-z0-9_-]+$/
export const STORE_CODE_MAX_LENGTH = 32
export const NOTE_MAX_LENGTH = 200
export const DEVICE_MODEL_MAX_LENGTH = 64
export const MAC_ADDRESS_MAX_LENGTH = 32
export const CAMERA_ID_MAX_LENGTH = 64

/** 抓拍记录分页：单页默认 20（后端最大 50），页码最大 100。 */
export const CAPTURE_PAGE_SIZE = 20
export const CAPTURE_MAX_PAGE = 100

// ───────────── 状态判定 ─────────────

export type CameraStatus = 'disabled' | 'online' | 'offline'

/** 停用优先于在线：已停用的摄像机即使仍有推送也显示「已停用」；其余按服务端算好的 online 判定。 */
export function getCameraStatus(camera: Pick<FaceCamera, 'enabled' | 'online'>): CameraStatus {
  if (!camera.enabled) return 'disabled'
  return camera.online ? 'online' : 'offline'
}

/** antd Tag 颜色：在线绿、离线橙（需要关注）、停用灰。 */
export const CAMERA_STATUS_COLORS: Record<CameraStatus, string> = {
  online: 'green',
  offline: 'orange',
  disabled: 'default',
}

export interface CameraStatusCounts {
  total: number
  online: number
  offline: number
  disabled: number
}

export function countCameraStatuses(cameras: ReadonlyArray<Pick<FaceCamera, 'enabled' | 'online'>>): CameraStatusCounts {
  const counts: CameraStatusCounts = { total: cameras.length, online: 0, offline: 0, disabled: 0 }
  for (const camera of cameras) counts[getCameraStatus(camera)] += 1
  return counts
}

/** 近 24 小时解析失败数大于 0 时用警示色。 */
export function hasParseFailures(camera: Pick<FaceCamera, 'parseFailureCount24h'>) {
  return camera.parseFailureCount24h > 0
}

// ───────────── 表单校验与请求体规范化 ─────────────

export interface FaceCameraFormValues {
  cameraId?: string | null
  storeCode?: string | null
  note?: string | null
  deviceModel?: string | null
  macAddress?: string | null
}

/** 去首尾空白；空串（含全空白）规范成 null，后端把 null 当作「未填写」。 */
export function normalizeOptionalText(value: string | null | undefined): string | null {
  const trimmed = (value ?? '').trim()
  return trimmed ? trimmed : null
}

export function validateCameraId(value: string | null | undefined): string | null {
  const text = (value ?? '').trim()
  if (!text) return 'faceCameras.validation.cameraIdRequired'
  if (!CAMERA_ID_PATTERN.test(text)) return 'faceCameras.validation.cameraIdInvalid'
  return null
}

export function validateStoreCode(value: string | null | undefined): string | null {
  const text = (value ?? '').trim()
  if (!text) return 'faceCameras.validation.storeCodeRequired'
  if (text.length > STORE_CODE_MAX_LENGTH) return 'faceCameras.validation.storeCodeTooLong'
  if (!STORE_CODE_PATTERN.test(text)) return 'faceCameras.validation.storeCodeInvalid'
  return null
}

export function validateNote(value: string | null | undefined): string | null {
  return (value ?? '').trim().length > NOTE_MAX_LENGTH ? 'faceCameras.validation.noteTooLong' : null
}

export function validateDeviceModel(value: string | null | undefined): string | null {
  return (value ?? '').trim().length > DEVICE_MODEL_MAX_LENGTH ? 'faceCameras.validation.deviceModelTooLong' : null
}

export function validateMacAddress(value: string | null | undefined): string | null {
  return (value ?? '').trim().length > MAC_ADDRESS_MAX_LENGTH ? 'faceCameras.validation.macAddressTooLong' : null
}

export type FaceCameraFormErrors = Partial<Record<keyof FaceCameraFormValues, string>>

/** 整表校验；编辑模式不校验 cameraId（不可改）。返回「字段 → 文案键」，空对象表示通过。 */
export function validateFaceCameraForm(values: FaceCameraFormValues, mode: 'create' | 'edit'): FaceCameraFormErrors {
  const errors: FaceCameraFormErrors = {}
  const checks: Array<[keyof FaceCameraFormValues, string | null]> = [
    ['cameraId', mode === 'create' ? validateCameraId(values.cameraId) : null],
    ['storeCode', validateStoreCode(values.storeCode)],
    ['note', validateNote(values.note)],
    ['deviceModel', validateDeviceModel(values.deviceModel)],
    ['macAddress', validateMacAddress(values.macAddress)],
  ]
  for (const [field, error] of checks) {
    if (error) errors[field] = error
  }
  return errors
}

function buildFields(values: FaceCameraFormValues): FaceCameraFields {
  return {
    storeCode: (values.storeCode ?? '').trim(),
    note: normalizeOptionalText(values.note),
    deviceModel: normalizeOptionalText(values.deviceModel),
    macAddress: normalizeOptionalText(values.macAddress),
  }
}

export function buildCreateRequest(values: FaceCameraFormValues): CreateFaceCameraRequest {
  return { cameraId: (values.cameraId ?? '').trim(), ...buildFields(values) }
}

export function buildUpdateRequest(values: FaceCameraFormValues, enabled: boolean): UpdateFaceCameraRequest {
  return { ...buildFields(values), enabled }
}

/** 编辑表单的初始值：null 展示成空串。 */
export function toFormValues(camera: FaceCamera): Required<FaceCameraFormValues> {
  return {
    cameraId: camera.cameraId,
    storeCode: camera.storeCode,
    note: camera.note ?? '',
    deviceModel: camera.deviceModel ?? '',
    macAddress: camera.macAddress ?? '',
  }
}

// ───────────── 摄像机地址脱敏 ─────────────

const MASK = '••••••••'

/**
 * 脱敏摄像机地址：保留 协议 + 域名 + 固定路径前缀，最后一段（token）整体替换为掩码，不保留任何 token 字符。
 * 不是合法地址（缺最后一段）时整体返回掩码，宁可多藏。
 */
export function maskCameraUrl(url: string | null | undefined): string {
  const text = (url ?? '').trim()
  const cut = text.lastIndexOf('/')
  if (!text || cut < 0 || cut === text.length - 1) return MASK
  return `${text.slice(0, cut + 1)}${MASK}`
}

// ───────────── 一次性 token 展示状态机 ─────────────

/**
 * 新增 / 轮换成功后的一次性展示状态。刻意只保留摄像机地址一份（token 已包含在地址里，不单独存），
 * 关闭即整体替换成 CLOSED 常量，内存里不留任何引用。
 */
export type SecretRevealState =
  | { open: false }
  | { open: true; kind: 'created' | 'rotated'; cameraId: string; cameraUrl: string }

export const SECRET_REVEAL_CLOSED: SecretRevealState = Object.freeze({ open: false })

/** 打开展示框：只从结果里取地址，不接收也不保存 token 本身。 */
export function openSecretReveal(
  kind: 'created' | 'rotated',
  result: { camera: { cameraId: string }; cameraUrl: string },
): SecretRevealState {
  return { open: true, kind, cameraId: result.camera.cameraId, cameraUrl: result.cameraUrl }
}

/** 关闭展示框：无论当前状态，一律回到已清空状态。 */
export function closeSecretReveal(): SecretRevealState {
  return SECRET_REVEAL_CLOSED
}

/** 状态里是否还持有地址（测试与页面卸载自检用）。 */
export function holdsSecret(state: SecretRevealState): boolean {
  return state.open && state.cameraUrl.length > 0
}

// ───────────── 复制到剪贴板 ─────────────

export interface ClipboardLike {
  writeText?: (text: string) => Promise<void>
}

/**
 * 复制文本：成功返回 true；剪贴板不可用（非 https、权限被拒、旧浏览器）返回 false，
 * 由界面降级成「已选中文本，请手动复制」。吞掉异常，不记录异常对象（避免文本进入日志）。
 */
export async function copyTextToClipboard(
  text: string,
  clipboard: ClipboardLike | undefined = typeof navigator === 'undefined' ? undefined : navigator.clipboard,
): Promise<boolean> {
  if (!clipboard || typeof clipboard.writeText !== 'function') return false
  try {
    await clipboard.writeText(text)
    return true
  } catch {
    return false
  }
}

// ───────────── 错误码 ─────────────

export const FACE_CAMERA_ERROR_CODES = [
  'FACE_CAMERA_ID_INVALID',
  'FACE_CAMERA_STORE_CODE_INVALID',
  'FACE_CAMERA_STORE_NOT_FOUND',
  'FACE_CAMERA_FIELD_INVALID',
  'FACE_CAMERA_ID_EXISTS',
  'FACE_CAMERA_TOKEN_CONFLICT',
  'FACE_CAMERA_NOT_FOUND',
] as const

export type FaceCameraErrorCode = (typeof FACE_CAMERA_ERROR_CODES)[number]

/**
 * 从请求错误里取已知业务错误码；没有或不认识返回 null。
 * 只读 errorCode/code，不读取、不返回响应体的其他内容。
 */
export function getFaceCameraErrorCode(error: unknown): FaceCameraErrorCode | null {
  if (!(error instanceof RequestError)) return null
  const payload = error.payload
  if (!payload || typeof payload !== 'object') return null
  const raw = payload as Record<string, unknown>
  const code = raw.errorCode ?? raw.code
  return FACE_CAMERA_ERROR_CODES.find((known) => known === code) ?? null
}

export function isForbiddenError(error: unknown) {
  return error instanceof RequestError && error.status === 403
}

// ───────────── 时间与解析状态 ─────────────

/** UTC ISO（带 Z 或偏移）按浏览器本地时区显示；空值或非法值显示 -。 */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '-'
  const parsed = dayjs(value)
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm:ss') : '-'
}

/** 解析状态的展示元数据：颜色（不只靠颜色，页面同时显示中英文标签）。 */
export const PARSE_STATUS_COLORS: Record<FaceCaptureParseStatus, string> = {
  0: 'green',
  1: 'orange',
  2: 'red',
  3: 'default',
}

// ───────────── 抓拍记录分页 ─────────────

export function canGoPrevPage(page: number) {
  return page > 1
}

/** 后端没有 total：下一页由 hasMore 控制，且不超过最大页码。 */
export function canGoNextPage(page: number, hasMore: boolean) {
  return hasMore && page < CAPTURE_MAX_PAGE
}

export function clampPage(page: number) {
  if (!Number.isFinite(page)) return 1
  return Math.min(CAPTURE_MAX_PAGE, Math.max(1, Math.trunc(page)))
}

/** 请求乱序保护：只认最后一次发起的请求。 */
export function createLatestGuard() {
  let latest = 0
  return {
    begin: () => ++latest,
    isLatest: (id: number) => id === latest,
    invalidate: () => {
      latest += 1
    },
  }
}
