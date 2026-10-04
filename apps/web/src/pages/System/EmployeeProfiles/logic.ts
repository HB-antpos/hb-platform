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
