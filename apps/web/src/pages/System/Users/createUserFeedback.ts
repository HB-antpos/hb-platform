import type { TFunction } from 'i18next'

export type CreateUserFeedbackField = 'username' | 'email'

export interface CreateUserErrorFeedback {
  message: string
  field?: CreateUserFeedbackField
  resultUnconfirmed?: true
}

type UnknownRecord = Record<string, unknown>

function asRecord(value: unknown): UnknownRecord | undefined {
  return typeof value === 'object' && value !== null ? value as UnknownRecord : undefined
}

function getErrorCode(error: unknown): string | undefined {
  const errorRecord = asRecord(error)
  const payload = asRecord(errorRecord?.payload)
  const code = payload?.errorCode ?? payload?.code
  return typeof code === 'string' ? code.trim().toUpperCase() : undefined
}

function getErrorStatus(error: unknown): number | undefined {
  const status = asRecord(error)?.status
  return typeof status === 'number' && Number.isFinite(status) ? status : undefined
}

function getUnknownResultFeedback(t: TFunction): CreateUserErrorFeedback {
  return {
    resultUnconfirmed: true,
    message: t('system.users.createUserResultUnknown', '暂时无法确认创建结果，请先在用户列表中确认该用户是否已创建，再决定是否重试。'),
  }
}

export interface UserFieldErrorFeedback {
  field: CreateUserFeedbackField
  message: string
}

/**
 * 「资料 → 保存基本信息」失败时，把后端的用户名 / 邮箱重复错误码映射到对应字段的中文提示；
 * 其他错误返回 null，由调用方沿用原来的通用提示。
 */
export function getUpdateUserFieldError(error: unknown, t: TFunction): UserFieldErrorFeedback | null {
  const code = getErrorCode(error)
  if (code === 'USERNAME_EXISTS') {
    return {
      field: 'username',
      message: t('system.users.updateUserUsernameExists', '该用户名已被使用，请更换用户名后再保存。'),
    }
  }
  if (code === 'EMAIL_EXISTS') {
    return {
      field: 'email',
      message: t('system.users.updateUserEmailExists', '该邮箱已被其他账号使用，请更换邮箱后再保存。'),
    }
  }
  return null
}

export function getCreateUserErrorFeedback(error: unknown, t: TFunction): CreateUserErrorFeedback | null {
  const code = getErrorCode(error)
  const status = getErrorStatus(error)

  // 401 已由请求层触发重新登录，页面即将卸载，不再显示弹窗内反馈。
  if (status === 401) return null

  if (code === 'EMAIL_EXISTS') {
    return {
      message: t('system.users.createUserEmailExists', '该邮箱已被使用，请更换邮箱后重试。'),
      field: 'email',
    }
  }

  if (code === 'USERNAME_EXISTS') {
    return {
      message: t('system.users.createUserUsernameExists', '该用户名已被使用，请更换用户名后重试。'),
      field: 'username',
    }
  }

  if (code === 'STORE_SCOPE_DENIED') {
    return {
      message: t('system.users.createUserStoreScopeDenied', '所选分店不在您的管理范围内，请在“分店”页签调整选择后重试。'),
    }
  }

  if (code === 'ADMIN_REQUIRED' || status === 403) {
    return {
      message: t('system.users.createUserPermissionDenied', '当前账号没有创建用户的权限，请联系系统管理员。'),
    }
  }

  if (code === 'VALIDATION_ERROR' || status === 400) {
    return {
      message: t('system.users.createUserValidationFailed', '用户信息未通过校验，请检查填写内容后重试。'),
    }
  }

  if (status === 429) {
    return {
      message: t('system.users.createUserRateLimited', '操作过于频繁，请稍后再试。'),
    }
  }

  if (code === 'CREATE_USER_FAILED') {
    return {
      message: t('system.users.createUserRejected', '暂时无法创建用户，请稍后重试。如仍无法完成，请联系系统管理员。'),
    }
  }

  if (status !== undefined && status >= 500 && status <= 599) {
    return getUnknownResultFeedback(t)
  }

  // 请求超时、网络中断和服务端错误都无法证明用户是否已写入，提示先查询列表以避免重复提交。
  return getUnknownResultFeedback(t)
}
