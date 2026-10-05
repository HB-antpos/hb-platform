import { RequestError } from '../../../utils/request'
import type {
  EmployeeProfileSensitiveChangeStatus,
  EmployeeProfileSensitiveField,
} from '../../../types/employeeProfile'

export const SENSITIVE_PROFILE_FIELDS = [
  'birthday',
  'bankBsb',
  'bankAccountNumber',
  'superannuationCompanyName',
  'superannuationCompanyCode',
  'superannuationAccountNumber',
  'identityType',
  'identityId',
  'identityPhotoUrl',
] as const

export type SensitiveProfileField = EmployeeProfileSensitiveField

export type SensitiveProfileSnapshot = Partial<Record<SensitiveProfileField, unknown>>

function normalizeSensitiveValue(field: SensitiveProfileField, value: unknown) {
  if (typeof value !== 'string') {
    return value ?? null
  }
  const trimmed = value.trim()
  // 生日来自后端是 1998-06-21T00:00:00，表单是 1998-06-21，只比较日期部分。
  return field === 'birthday' ? trimmed.slice(0, 10) || null : trimmed
}

export function getChangedSensitiveFields<
  TCurrent extends SensitiveProfileSnapshot,
  TNext extends SensitiveProfileSnapshot,
>(
  current: TCurrent,
  next: TNext,
): SensitiveProfileField[] {
  return SENSITIVE_PROFILE_FIELDS.filter(
    (field) => normalizeSensitiveValue(field, current[field]) !== normalizeSensitiveValue(field, next[field]),
  )
}

export function maskSensitiveSummary(value?: string | null) {
  const normalized = value?.trim()
  if (!normalized) {
    return '--'
  }

  return `****${normalized.slice(-4)}`
}

export type ProfileCompletionStatus = 'noProfile' | 'incomplete' | 'complete'
export type ProfileMissingPart = 'bank' | 'superannuation'

interface ProfileCompletionInput {
  hasProfile?: boolean
  bankBsb?: string
  bankAccountNumber?: string
  superannuationCompanyName?: string
  superannuationAccountNumber?: string
}

function hasText(value?: string | null) {
  return Boolean(value?.trim())
}

/**
 * 列表行的资料状态：
 * - 未建档：后端明确返回 hasProfile=false，该用户没有任何员工资料记录；
 * - 待补全：已建档，但银行（BSB + 账号）或公积金（公司 + 账号）缺失；
 * - 完整：银行与公积金都已填写。
 * 证件信息不在列表接口里返回，所以这里不能据此判断证件是否齐全。
 */
export function getProfileCompletion(profile: ProfileCompletionInput): {
  status: ProfileCompletionStatus
  missing: ProfileMissingPart[]
} {
  if (profile.hasProfile === false) {
    return { status: 'noProfile', missing: ['bank', 'superannuation'] }
  }

  const missing: ProfileMissingPart[] = []
  if (!hasText(profile.bankBsb) || !hasText(profile.bankAccountNumber)) {
    missing.push('bank')
  }
  if (!hasText(profile.superannuationCompanyName) || !hasText(profile.superannuationAccountNumber)) {
    missing.push('superannuation')
  }
  return { status: missing.length === 0 ? 'complete' : 'incomplete', missing }
}

/** 头像占位文字：中文取第一个字，西文取前两个单词首字母（大写）。 */
export function getProfileInitials(...names: Array<string | null | undefined>) {
  const source = names.map((name) => name?.trim()).find(Boolean)
  if (!source) {
    return '?'
  }

  const words = source.split(/\s+/).filter(Boolean)
  if (words.length > 1 && /^[A-Za-z]/.test(words[0])) {
    return `${words[0][0]}${words[1][0]}`.toUpperCase()
  }

  // 用 Array.from 按码点切分，避免把代理对（生僻字）拆成乱码。
  const first = Array.from(source)[0]
  return /^[A-Za-z]/.test(first) ? Array.from(source).slice(0, 2).join('').toUpperCase() : first
}

/** 按名称稳定取色：分页、刷新后同一个人的头像颜色不会跳变。 */
export function getStablePaletteIndex(seed: string, paletteSize: number) {
  const hash = Array.from(seed).reduce((sum, char) => (sum * 31 + char.charCodeAt(0)) >>> 0, 0)
  return paletteSize > 0 ? hash % paletteSize : 0
}

/** GUID 等长标识的展示缩写：保留头尾便于核对，完整值仍可复制。 */
export function shortenIdentifier(value?: string | null, head = 8, tail = 6) {
  const normalized = value?.trim()
  if (!normalized) {
    return ''
  }
  return normalized.length <= head + tail + 1 ? normalized : `${normalized.slice(0, head)}…${normalized.slice(-tail)}`
}

function normalizeFormValue(value: unknown) {
  // dayjs / moment 一类日期对象统一按日期字符串比较，其余按去空白后的字符串比较。
  if (value && typeof value === 'object' && 'format' in value && typeof value.format === 'function') {
    return String(value.format('YYYY-MM-DD'))
  }
  return typeof value === 'string' ? value.trim() : String(value ?? '')
}

/** 编辑抽屉底部「已修改 N 项」：对比打开时的初始值与当前表单值，只统计真正不同的字段。 */
export function countChangedFormFields(
  initial: object,
  current: object,
  fields: readonly string[],
) {
  // 入参用 object：表单值是 interface，TS 不会给接口隐式的字符串索引签名。
  const read = (source: object, field: string) => (source as Record<string, unknown>)[field]
  return fields.filter((field) => normalizeFormValue(read(initial, field)) !== normalizeFormValue(read(current, field))).length
}

export function isRejectReasonValid(reason?: string | null) {
  return Boolean(reason?.trim())
}

export function getReviewChangedFields(
  current: SensitiveProfileSnapshot,
  proposed: SensitiveProfileSnapshot,
  identityPhotoChanged: boolean,
) {
  const compared = new Set(getChangedSensitiveFields(current, proposed))
  // 私有证件照 URL 是短效签名，照片是否变化只能使用后端持久化的对象级字段快照。
  compared.delete('identityPhotoUrl')
  if (identityPhotoChanged) {
    compared.add('identityPhotoUrl')
  }
  return SENSITIVE_PROFILE_FIELDS.filter((field) => compared.has(field))
}

export function getExpectedSensitiveRevision(
  current: SensitiveProfileSnapshot & { sensitiveRevision?: unknown },
) {
  // 新后台始终回传打开详情时的 revision；后端仅在实际敏感差异存在时执行 CAS。
  return typeof current.sensitiveRevision === 'number'
    ? current.sensitiveRevision
    : undefined
}

export function isSensitiveRequestReviewable(status?: EmployeeProfileSensitiveChangeStatus) {
  return status === 'Pending'
}

export function createLatestRequestGuard() {
  let version = 0
  return {
    begin: () => {
      version += 1
      return version
    },
    isCurrent: (token: number) => token === version,
    invalidate: () => {
      version += 1
    },
  }
}

function readErrorCode(payload: unknown) {
  if (!payload || typeof payload !== 'object') {
    return undefined
  }

  const candidate = payload as { code?: unknown; errorCode?: unknown }
  return typeof candidate.errorCode === 'string'
    ? candidate.errorCode
    : typeof candidate.code === 'string'
      ? candidate.code
      : undefined
}

export function isPendingChangeConfirmationRequired(error: unknown) {
  return error instanceof RequestError
    && error.status === 409
    && readErrorCode(error.payload) === 'EMPLOYEE_PROFILE_PENDING_CHANGE_CONFIRMATION_REQUIRED'
}

export async function saveAdminProfileWithPendingConfirmation<
  TPayload extends object,
  TResult,
>(
  payload: TPayload,
  save: (
    nextPayload: TPayload & { confirmSupersedePendingSensitiveChangeRequest?: boolean }
  ) => Promise<TResult>,
  confirm: () => Promise<boolean>,
): Promise<{ status: 'saved'; data: TResult } | { status: 'cancelled' }> {
  try {
    return { status: 'saved', data: await save(payload) }
  } catch (error) {
    if (!isPendingChangeConfirmationRequired(error)) {
      throw error
    }
  }

  if (!await confirm()) {
    return { status: 'cancelled' }
  }

  // 确认标志只在服务端明确报告 Pending 冲突后发送，最终判断仍由事务内检查完成。
  const confirmedPayload = {
    ...payload,
    confirmSupersedePendingSensitiveChangeRequest: true,
  }
  return { status: 'saved', data: await save(confirmedPayload) }
}

export function isSensitiveVersionConflict(error: unknown) {
  return error instanceof RequestError
    && error.status === 409
    && readErrorCode(error.payload) === 'EMPLOYEE_PROFILE_SENSITIVE_VERSION_CONFLICT'
}

export type SensitiveReviewConflictKind = 'version' | 'terminal'

function getSensitiveReviewConflictKind(error: unknown): SensitiveReviewConflictKind | false {
  if (!(error instanceof RequestError) || error.status !== 409) {
    return false
  }
  const code = readErrorCode(error.payload)
  if (code === 'EMPLOYEE_PROFILE_SENSITIVE_VERSION_CONFLICT') {
    return 'version'
  }
  return code === 'REQUEST_NOT_PENDING' ? 'terminal' : false
}

export async function handleSensitiveReviewFailure(
  error: unknown,
  refreshDetail: () => Promise<unknown>,
  refreshList: () => Promise<unknown>,
  refreshPendingCount: () => Promise<unknown>,
) {
  const conflictKind = getSensitiveReviewConflictKind(error)
  if (!conflictKind) {
    return false
  }

  // 任一审核冲突都同步刷新三处状态，终态详情返回后会立即撤下操作按钮。
  await Promise.all([refreshDetail(), refreshList(), refreshPendingCount()])
  return conflictKind
}
