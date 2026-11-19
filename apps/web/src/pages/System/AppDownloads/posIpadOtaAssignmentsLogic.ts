import type {
  PosIpadOtaAssignment,
  PosIpadOtaAssignmentCreateRequest,
  PosIpadOtaAssignmentTargetType,
  PosIpadOtaAssignmentUpdateRequest,
  PosIpadOtaRelease,
} from '../../../types/appUpdatePolicy'
import { isAppUpdatePolicyVersionConflict } from './appUpdatePolicyLogic'

/** 带 toISOString 的最小日期形态，dayjs 实例直接满足，逻辑层不依赖 dayjs。 */
export interface IsoDateLike {
  toISOString(): string
}

export interface PosIpadOtaAssignmentFormValue {
  targetType: PosIpadOtaAssignmentTargetType
  /** 分店覆盖：目标分店；设备覆盖：用来筛选候选设备的分店（不随请求提交）。 */
  storeGuid?: string | null
  deviceRegistrationId?: number | null
  releaseId?: string | null
  forceUpdate?: boolean
  releaseMessage?: string | null
  note?: string | null
  expiresAt?: IsoDateLike | null
  enabled?: boolean
}

export const OTA_ASSIGNMENT_RELEASE_MESSAGE_MAX_LENGTH = 1000
export const OTA_ASSIGNMENT_NOTE_MAX_LENGTH = 200

const TIMEZONE_SUFFIX_PATTERN = /(Z|[+-]\d{2}:?\d{2})$/i

function normalizeText(value?: string | null) {
  const normalized = value?.trim()
  return normalized || null
}

/** 后端时间语义是 UTC，SQL / JSON 可能返回不带 Z 的字符串，缺时区时补 Z。 */
export function parseUtcTimestamp(value?: string | null): Date | null {
  const trimmed = value?.trim()
  if (!trimmed) {
    return null
  }

  const normalized = TIMEZONE_SUFFIX_PATTERN.test(trimmed)
    ? trimmed
    : `${trimmed.replace(' ', 'T')}Z`
  const parsed = new Date(normalized)
  return Number.isNaN(parsed.getTime()) ? null : parsed
}

export function buildPosIpadOtaAssignmentFormValue(
  assignment: PosIpadOtaAssignment | null,
): PosIpadOtaAssignmentFormValue {
  if (!assignment) {
    return {
      targetType: 'store',
      storeGuid: null,
      deviceRegistrationId: null,
      releaseId: null,
      forceUpdate: false,
      releaseMessage: null,
      note: null,
      expiresAt: null,
      enabled: true,
    }
  }

  return {
    targetType: assignment.targetType,
    storeGuid: assignment.storeGuid,
    deviceRegistrationId: assignment.deviceRegistrationId,
    releaseId: assignment.releaseId,
    forceUpdate: assignment.forceUpdate,
    releaseMessage: assignment.releaseMessage,
    note: assignment.note,
    // expiresAt 由组件按 dayjs 转换，这里不构造日期对象。
    expiresAt: null,
    enabled: assignment.enabled,
  }
}

/** 创建请求：设备覆盖只带设备注册 ID，分店覆盖只带分店 GUID，避免把筛选用的分店当成目标。 */
export function buildCreatePosIpadOtaAssignmentRequest(
  value: PosIpadOtaAssignmentFormValue,
): PosIpadOtaAssignmentCreateRequest {
  const isDevice = value.targetType === 'device'
  return {
    targetType: value.targetType,
    storeGuid: isDevice ? null : normalizeText(value.storeGuid),
    deviceRegistrationId: isDevice ? value.deviceRegistrationId ?? null : null,
    releaseId: value.releaseId ?? '',
    forceUpdate: Boolean(value.forceUpdate),
    releaseMessage: normalizeText(value.releaseMessage),
    note: normalizeText(value.note),
    expiresAtUtc: value.expiresAt ? value.expiresAt.toISOString() : null,
    enabled: value.enabled ?? true,
  }
}

/** 修改请求：目标不可变，只带版本与投放字段；expectedPolicyVersion 必须是该行当前版本。 */
export function buildUpdatePosIpadOtaAssignmentRequest(
  value: PosIpadOtaAssignmentFormValue,
  expectedPolicyVersion: number,
): PosIpadOtaAssignmentUpdateRequest {
  return {
    expectedPolicyVersion,
    releaseId: value.releaseId ?? '',
    forceUpdate: Boolean(value.forceUpdate),
    releaseMessage: normalizeText(value.releaseMessage),
    note: normalizeText(value.note),
    expiresAtUtc: value.expiresAt ? value.expiresAt.toISOString() : null,
    enabled: value.enabled ?? true,
  }
}

/** 一键启停：沿用原行的全部投放字段，只翻转 enabled。 */
export function buildToggleEnabledRequest(
  assignment: PosIpadOtaAssignment,
  enabled: boolean,
): PosIpadOtaAssignmentUpdateRequest {
  return {
    expectedPolicyVersion: assignment.policyVersion,
    releaseId: assignment.releaseId,
    forceUpdate: assignment.forceUpdate,
    releaseMessage: assignment.releaseMessage,
    note: assignment.note,
    expiresAtUtc: parseUtcTimestamp(assignment.expiresAtUtc)?.toISOString() ?? null,
    enabled,
  }
}

/**
 * 到期时间校验：与后端一致，只有「启用」的覆盖要求到期时间晚于当前；
 * 停用的覆盖允许保留已过去的到期时间（例如把过期覆盖先停用）。
 */
export function isExpiresAtValid(
  enabled: boolean,
  expiresAt: IsoDateLike | null | undefined,
  nowMs: number,
) {
  if (!enabled || !expiresAt) {
    return true
  }

  const expiresMs = Date.parse(expiresAt.toISOString())
  return Number.isFinite(expiresMs) && expiresMs > nowMs
}

/**
 * 可选发布：runtime 与全局基线发布一致的登记发布（OTA 只能下发给同 runtime 的客户端）。
 * 全局基线没有指定发布时无从判断，列出全部；已选中的发布（编辑场景）始终保留，避免回显丢失。
 */
export function filterAssignableReleases(
  releases: readonly PosIpadOtaRelease[],
  baselineRuntimeVersion: string | null | undefined,
  keepReleaseId?: string | null,
) {
  const runtime = normalizeText(baselineRuntimeVersion)
  return [...releases]
    .filter((release) =>
      !runtime || release.runtimeVersion === runtime || release.id === keepReleaseId)
    .sort((left, right) => {
      const rightMs = parseUtcTimestamp(right.publishedAtUtc)?.getTime() ?? 0
      const leftMs = parseUtcTimestamp(left.publishedAtUtc)?.getTime() ?? 0
      return rightMs - leftMs
    })
}

/** 表格排序：设备覆盖在前（优先级最高），其次按分店编号、设备编号。 */
export function sortPosIpadOtaAssignments(assignments: readonly PosIpadOtaAssignment[]) {
  const typeRank = (item: PosIpadOtaAssignment) => (item.targetType === 'device' ? 0 : 1)
  return [...assignments].sort((left, right) =>
    typeRank(left) - typeRank(right)
    || (left.storeCode ?? '').localeCompare(right.storeCode ?? '')
    || (left.deviceNumber ?? '').localeCompare(right.deviceNumber ?? ''))
}

export type PosIpadOtaAssignmentErrorKind =
  | 'version-conflict'
  | 'target-exists'
  | 'not-found'
  | 'expires-invalid'
  | 'release-invalid'
  | 'release-required'
  | 'message-too-long'
  | 'note-too-long'
  | 'target-invalid'
  | 'unknown'

const ERROR_CODE_KINDS: Record<string, PosIpadOtaAssignmentErrorKind> = {
  APP_UPDATE_POLICY_VERSION_CONFLICT: 'version-conflict',
  APP_UPDATE_POLICY_VERSION_REQUIRED: 'version-conflict',
  OTA_ASSIGNMENT_TARGET_EXISTS: 'target-exists',
  OTA_ASSIGNMENT_NOT_FOUND: 'not-found',
  OTA_ASSIGNMENT_EXPIRES_INVALID: 'expires-invalid',
  OTA_RELEASE_INVALID: 'release-invalid',
  OTA_RELEASE_REQUIRED: 'release-required',
  RELEASE_MESSAGE_TOO_LONG: 'message-too-long',
  OTA_ASSIGNMENT_NOTE_TOO_LONG: 'note-too-long',
  TARGET_STORES_REQUIRED: 'target-invalid',
  TARGET_STORES_INVALID: 'target-invalid',
  TARGET_DEVICES_REQUIRED: 'target-invalid',
  TARGET_DEVICES_INVALID: 'target-invalid',
  TARGET_SCOPE_INVALID: 'target-invalid',
}

function readErrorCode(payload: unknown): string | null {
  if (!payload || typeof payload !== 'object') {
    return null
  }

  const raw = payload as Record<string, unknown>
  const code = raw.errorCode ?? raw.code
  return typeof code === 'string' && code ? code : null
}

/**
 * 从请求异常里取后端错误码：
 * HTTP 409 / 4xx 带 ApiResponse 响应体，HTTP 200 的业务失败则被 unwrapApiData 转成「CODE: message」。
 */
export function readPosIpadOtaAssignmentErrorCode(error: unknown): string | null {
  if (!error || typeof error !== 'object') {
    return null
  }

  const candidate = error as { payload?: unknown; message?: unknown }
  const fromPayload = readErrorCode(candidate.payload)
  if (fromPayload) {
    return fromPayload
  }

  const match = typeof candidate.message === 'string'
    ? /^([A-Z][A-Z0-9_]{2,}):/.exec(candidate.message)
    : null
  return match?.[1] ?? null
}

export function classifyPosIpadOtaAssignmentError(error: unknown): PosIpadOtaAssignmentErrorKind {
  // 版本冲突沿用页面既有判定（409 + 版本错误码），其余按错误码映射。
  if (isAppUpdatePolicyVersionConflict(error)) {
    return 'version-conflict'
  }

  const code = readPosIpadOtaAssignmentErrorCode(error)
  return (code && ERROR_CODE_KINDS[code]) || 'unknown'
}

/** 这几类错误说明本地列表已过期，处理后必须重新拉取权威状态。 */
export function shouldReloadAfterAssignmentError(kind: PosIpadOtaAssignmentErrorKind) {
  return kind === 'version-conflict' || kind === 'not-found' || kind === 'target-exists'
}
