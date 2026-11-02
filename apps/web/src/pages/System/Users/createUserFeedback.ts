import type { TFunction } from 'i18next'
import { getNewStaffErrorField, type NewStaffErrorField } from './newStaffProfileRules'

export type CreateUserFeedbackField = 'username' | 'email' | NewStaffErrorField

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

function getServerMessage(error: unknown): string | undefined {
  const payload = asRecord(asRecord(error)?.payload)
  return typeof payload?.message === 'string' ? payload.message : undefined
}

/**
 * 新建员工必填规则（后端 NEW_STAFF_*）的失败原因：定位到对应字段并给出可读提示。
 * 前端已提前校验，这里兜底处理旧页面缓存或前后端口径不一致的情况。
 */
function getNewStaffFeedback(code: string | undefined, error: unknown, t: TFunction): CreateUserErrorFeedback | null {
  const field = getNewStaffErrorField(code, getServerMessage(error))
  if (!field) return null
  const messages: Record<string, string> = {
    NEW_STAFF_BIRTHDAY_REQUIRED: t('system.usersNewStaff.errorBirthdayRequired', '请填写生日。'),
    NEW_STAFF_BIRTHDAY_INVALID: t('system.usersNewStaff.errorBirthdayInvalid', '生日无效：不能晚于今天，也不能早于 1940 年。'),
    NEW_STAFF_PHONE_REQUIRED: t('system.usersNewStaff.errorPhoneRequired', '请填写有效的电话（8–15 位数字）。'),
    NEW_STAFF_EMAIL_REQUIRED: t('system.usersNewStaff.errorEmailRequired', '请填写邮箱。'),
    NEW_STAFF_GUARDIAN_REQUIRED: t('system.usersNewStaff.errorGuardianRequired', '员工未满 18 岁，必须填写监护人姓名、关系、手机和邮箱。'),
    NEW_STAFF_GUARDIAN_PHONE_INVALID: t('system.usersNewStaff.errorGuardianPhoneInvalid', '监护人手机必须是 04 开头的澳洲手机号。'),
    NEW_STAFF_GUARDIAN_EMAIL_INVALID: t('system.usersNewStaff.errorGuardianEmailInvalid', '监护人邮箱格式无效。'),
  }
  const message = code === 'NEW_STAFF_GUARDIAN_DUPLICATE'
    ? field === 'guardianEmail'
      ? t('system.usersNewStaff.guardianEmailSameAsStaff', '监护人邮箱不能和员工本人的邮箱相同')
      : t('system.usersNewStaff.guardianPhoneSameAsStaff', '监护人手机不能和员工本人的电话相同')
    : messages[code ?? '']
  return { field, message }
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
  // 编辑用户只会定位到用户名 / 邮箱；新建员工字段只出现在「创建用户」。
  field: 'username' | 'email'
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

/**
 * 角色等级校验失败（后端 TARGET_LEVEL_DENIED / ROLE_LEVEL_DENIED）时的中文提示，
 * 让操作者知道是「等级不够」而不是笼统的保存失败；其他错误返回 null，由调用方沿用原提示。
 */
export function getLevelDeniedMessage(error: unknown, t: TFunction): string | null {
  const code = getErrorCode(error)
  if (code === 'TARGET_LEVEL_DENIED') {
    return t('system.users.levelDeniedTarget', '该账号的角色等级不低于你，无权修改。')
  }
  if (code === 'ROLE_LEVEL_DENIED') {
    return t('system.users.levelDeniedRole', '不能授予等级不低于你自己的角色。')
  }
  return null
}

export function getCreateUserErrorFeedback(error: unknown, t: TFunction): CreateUserErrorFeedback | null {
  const code = getErrorCode(error)
  const status = getErrorStatus(error)

  // 401 已由请求层触发重新登录，页面即将卸载，不再显示弹窗内反馈。
  if (status === 401) return null

  const newStaffFeedback = getNewStaffFeedback(code, error, t)
  if (newStaffFeedback) return newStaffFeedback

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
