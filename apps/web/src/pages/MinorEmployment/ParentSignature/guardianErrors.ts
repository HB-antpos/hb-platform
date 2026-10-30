// 家长签署页的错误提示：后端 message 是中文，不能直接给家长看，一律按 errorCode 换成英文；未知错误码用调用方给的英文兜底。
// 纯函数，不依赖 service / request，便于单测；errorCode 由调用方用 guardianErrorCode(error) 取出后传入。

const CONTACT_STORE = 'Please contact the store.'

const MESSAGES: Record<string, string> = {
  // 链接与版本
  GUARDIAN_TOKEN_INVALID: 'This signing link is invalid or has expired. Please ask the store to send you a new link.',
  NOT_FOUND: 'This signing link is invalid or has expired. Please ask the store to send you a new link.',
  MINOR_COMPLIANCE_VERSION_CONFLICT: 'This signing link has been replaced by a newer one. Please use the latest link the store sent you.',
  GUARDIAN_TOKEN_CONFLICT: 'This form has already been signed or was just updated by the store. Please refresh the page, or ask the store for a new link.',
  // 验证码
  GUARDIAN_EMAIL_NOT_VERIFIED: 'Your verification has expired. Please verify again with a new code.',
  GUARDIAN_CODE_EXPIRED: 'This code has expired or has already been used. Please request a new code.',
  GUARDIAN_CODE_LOCKED: 'Too many incorrect attempts. Please request a new code.',
  GUARDIAN_CODE_INVALID: 'That code is incorrect. Please check it and try again.',
  GUARDIAN_CODE_COOLDOWN: 'A code was just sent. Please wait a moment before requesting another one.',
  GUARDIAN_CODE_SEND_LIMIT: "You've reached the limit for sending codes. Please ask the store to send you a new signing link.",
  GUARDIAN_CODE_CONFLICT: 'Something changed while sending your code. Please refresh the page and try again.',
  // 邮件 / 短信通道
  GUARDIAN_EMAIL_NOT_CONFIGURED: `We can't send emails right now. ${CONTACT_STORE}`,
  GUARDIAN_EMAIL_SEND_FAILED: "We couldn't send the email. Please try again in a few minutes.",
  GUARDIAN_SMS_NOT_CONFIGURED: `We can't send text messages right now. ${CONTACT_STORE}`,
  GUARDIAN_SMS_PHONE_INVALID: "The mobile number on file can't receive text messages. Please ask the store to update it and send you a new link.",
  SMS_DISABLED: `We can't send text messages right now. ${CONTACT_STORE}`,
  SMS_INVALID_PHONE: "The mobile number on file can't receive text messages. Please ask the store to update it and send you a new link.",
  SMS_BODY_NOT_GSM7: `We couldn't send the text message. ${CONTACT_STORE}`,
  SMS_PHONE_RATE_LIMITED: 'Too many text messages have been sent to this number. Please try again later.',
  SMS_DAILY_BUDGET_EXCEEDED: `We can't send text messages right now. Please try again later or ${CONTACT_STORE.toLowerCase()}`,
  SMS_SEND_FAILED: "We couldn't send the text message. Please try again in a few minutes.",
  // 资料完整性（与签署页分区校验对应）
  MINOR_COMPLIANCE_FORM_INCOMPLETE: "Some required details are missing: the child's first and last name and the school name. Please complete them and try again. If they are all filled in, please contact the store.",
  MINOR_COMPLIANCE_SCHOOL_CALENDAR_INVALID: 'Please check the school timetable: each school day needs a start and end time (start before end), and each date range must start on or before its end date.',
  MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED: 'Please add at least one backup contact: an adult other than the parent or guardian, with a valid phone number.',
  MINOR_COMPLIANCE_GUARDIAN_CONTACT_REQUIRED: "The parent or guardian's name and a valid phone number are required. Please check Guardian details, or contact the store.",
  // 签字
  SIGNER_NAME_MISMATCH: 'The signing name must match the parent / guardian name in Guardian details.',
  SIGNATURE_CONFIRMATION_REQUIRED: 'Please tick all three confirmations and sign before submitting. If this keeps happening, refresh the page and try again.',
  SIGNATURE_REQUIRED: 'Please sign in the box before submitting.',
}

// 原件生成 / 保存失败（DOCUMENT_*）统一提示：签字没有提交成功，可以稍后重试。
const DOCUMENT_FAILED = "We couldn't save the signed form, so it has not been submitted. Please try again in a few minutes."

/** 从后端中文提示里抽"还可尝试 N 次"的 N；抽不到返回 undefined。 */
export function remainingAttempts(rawMessage?: string) {
  // 后端原文形如 "验证码不正确，还可尝试 3 次"，里面只有这一个数字。
  const match = /(\d+)/.exec(rawMessage ?? '')
  if (!match) return undefined
  const value = Number(match[1])
  return Number.isInteger(value) && value >= 0 ? value : undefined
}

/** 备用联系人错误按中文提示里的关键词细分（后端同一个错误码有三种原因）。 */
// 注意"至少一名独立备用紧急联系人"里也有"独立"，所以先判断"至少"。
function backupContactMessage(rawMessage: string) {
  if (rawMessage.includes('至少')) return MESSAGES.MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED
  if (rawMessage.includes('独立')) return "The backup contact must be a different person from the parent or guardian. Their phone, mobile and email can't be the same as yours."
  if (rawMessage.includes('有效电话')) return 'Each backup contact needs a name and a valid phone number.'
  return MESSAGES.MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED
}

/**
 * errorCode → 英文提示。
 * @param code   后端 errorCode（可为空，为空时尝试从 "CODE: 中文" 形式的 message 前缀里取）
 * @param rawMessage 后端原始 message，只用来抽数字 / 区分细分原因，绝不原样展示
 * @param status HTTP 状态码（429 = 限流）
 * @param fallback 未知错误码时的英文兜底
 */
export function describeGuardianError(code: string | undefined, rawMessage: string | undefined, status: number | undefined, fallback: string) {
  const message = rawMessage ?? ''
  const resolved = code || /^([A-Z][A-Z0-9_]+):/.exec(message)?.[1]
  if (resolved === 'GUARDIAN_CODE_INVALID') {
    const left = remainingAttempts(message.replace(/^[A-Z0-9_]+:\s*/, ''))
    if (left !== undefined && left > 0) return `That code is incorrect. You have ${left} ${left === 1 ? 'attempt' : 'attempts'} left.`
    return MESSAGES.GUARDIAN_CODE_INVALID
  }
  if (resolved === 'MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED') return backupContactMessage(message)
  if (resolved && MESSAGES[resolved]) return MESSAGES[resolved]
  if (resolved?.startsWith('DOCUMENT_')) return DOCUMENT_FAILED
  if (status === 429) return 'Too many requests. Please wait a minute and try again.'
  return fallback
}

/** 从异常对象取 message / status（鸭子类型，不依赖 RequestError），再换成英文。 */
export function guardianErrorText(error: unknown, code: string | undefined, fallback: string) {
  const raw = error && typeof error === 'object' ? error as { message?: unknown; status?: unknown } : {}
  return describeGuardianError(
    code,
    typeof raw.message === 'string' ? raw.message : undefined,
    typeof raw.status === 'number' ? raw.status : undefined,
    fallback,
  )
}
