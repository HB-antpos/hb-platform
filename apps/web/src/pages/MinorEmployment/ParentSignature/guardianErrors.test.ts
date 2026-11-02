import { describeGuardianError, guardianErrorText, remainingAttempts } from './guardianErrors'

const assert = (condition: unknown, message: string) => { if (!condition) throw new Error(message) }
const FALLBACK = 'Fallback.'
const CJK = /[一-鿿]/

// 已知错误码一律换成英文，绝不带出后端中文。
const codes = [
  'GUARDIAN_TOKEN_INVALID', 'GUARDIAN_EMAIL_NOT_VERIFIED', 'GUARDIAN_CODE_EXPIRED', 'GUARDIAN_CODE_LOCKED', 'GUARDIAN_CODE_INVALID', 'GUARDIAN_CODE_COOLDOWN',
  'GUARDIAN_CODE_SEND_LIMIT', 'GUARDIAN_CODE_CONFLICT', 'GUARDIAN_SMS_NOT_CONFIGURED', 'GUARDIAN_SMS_PHONE_INVALID', 'GUARDIAN_EMAIL_NOT_CONFIGURED',
  'GUARDIAN_EMAIL_SEND_FAILED', 'MINOR_COMPLIANCE_FORM_INCOMPLETE', 'MINOR_COMPLIANCE_SCHOOL_CALENDAR_INVALID', 'MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED',
  'MINOR_COMPLIANCE_GUARDIAN_CONTACT_REQUIRED', 'SIGNER_NAME_MISMATCH', 'SIGNATURE_CONFIRMATION_REQUIRED', 'GUARDIAN_TOKEN_CONFLICT',
  'MINOR_COMPLIANCE_VERSION_CONFLICT', 'DOCUMENT_STORAGE_UNAVAILABLE', 'DOCUMENT_STORAGE_FAILED', 'DOCUMENT_BUILD_FAILED', 'SMS_SEND_FAILED', 'SMS_PHONE_RATE_LIMITED',
]
for (const code of codes) {
  const text = describeGuardianError(code, `${code}: 这是一段中文提示`, 200, FALLBACK)
  assert(text !== FALLBACK && !CJK.test(text), `${code} must map to English`)
}

// 未知错误码 → 英文兜底，不显示后端中文。
assert(describeGuardianError('SOMETHING_NEW', '未知错误', 200, FALLBACK) === FALLBACK, 'unknown codes must use the fallback')
assert(describeGuardianError(undefined, '请求失败 (500)', 500, FALLBACK) === FALLBACK, 'plain server errors must use the fallback')
assert(describeGuardianError(undefined, undefined, 429, FALLBACK).startsWith('Too many requests'), 'rate limiting must have its own message')

// 验证码错误：抽出剩余次数；抽不到就用不带次数的英文。
assert(describeGuardianError('GUARDIAN_CODE_INVALID', 'GUARDIAN_CODE_INVALID: 验证码不正确，还可尝试 3 次', 200, FALLBACK) === 'That code is incorrect. You have 3 attempts left.', 'remaining attempts must be shown')
assert(describeGuardianError('GUARDIAN_CODE_INVALID', '验证码不正确，还可尝试 1 次', 200, FALLBACK).endsWith('1 attempt left.'), 'singular attempt wording')
assert(describeGuardianError('GUARDIAN_CODE_INVALID', '验证码不正确', 200, FALLBACK) === 'That code is incorrect. Please check it and try again.', 'no number → generic wording')
assert(remainingAttempts('还可尝试 4 次') === 4 && remainingAttempts('无') === undefined, 'remaining attempts parser')

// 错误码只在 message 前缀里时也能识别（unwrapApiData 会拼成 "CODE: 中文"）。
assert(describeGuardianError(undefined, 'GUARDIAN_CODE_EXPIRED: 验证码已过期，请重新发送', 200, FALLBACK).startsWith('This code has expired'), 'code prefix in the message must be recognised')

// 备用联系人三种原因分别给出英文。
assert(describeGuardianError('MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED', '备用联系人必须独立于家长联系人', 200, FALLBACK).includes('different person'), 'independence reason')
assert(describeGuardianError('MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED', '备用联系人必须填写有效电话', 200, FALLBACK).includes('valid phone number'), 'phone reason')
assert(describeGuardianError('MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED', '必须填写至少一名独立备用紧急联系人（家长之外）', 200, FALLBACK).includes('at least one backup contact'), 'missing contact reason (also contains 独立, must not be read as independence)')

// guardianErrorText 从异常对象取 message / status。
const error = Object.assign(new Error('GUARDIAN_CODE_LOCKED: 验证码错误次数过多，请重新发送'), { status: 200 })
assert(guardianErrorText(error, 'GUARDIAN_CODE_LOCKED', FALLBACK).startsWith('Too many incorrect attempts'), 'error objects must be described')
assert(guardianErrorText('oops', undefined, FALLBACK) === FALLBACK, 'non-error values use the fallback')

console.log('guardianErrors.test: ok')
