/**
 * 「创建用户」新建员工的必填规则（与后端 NewStaffProfileRules.cs 同一套口径，前端只负责提前提示）：
 * - 电话、生日必填；生日不能晚于悉尼今天，也不能早于 1940-01-01；
 * - 生日未满 18 岁时，监护人姓名、关系、手机（04 开头澳洲手机）、邮箱四项必填，
 *   且监护人手机 / 邮箱不能和员工本人的电话 / 邮箱相同。
 * 这里只放纯函数，不依赖 React / antd / dayjs，便于在 node 下单测。
 */

export const EARLIEST_BIRTHDAY = '1940-01-01'
export const ADULT_AGE = 18

/** 关系选项：提交英文值（会印在英文同意书上），界面显示中文 / 英文文案。 */
export const GUARDIAN_RELATIONSHIP_OPTIONS = [
  { value: 'Mother', labelKey: 'system.usersNewStaff.relationshipMother', defaultLabel: '母亲' },
  { value: 'Father', labelKey: 'system.usersNewStaff.relationshipFather', defaultLabel: '父亲' },
  { value: 'Legal guardian', labelKey: 'system.usersNewStaff.relationshipLegalGuardian', defaultLabel: '法定监护人' },
  { value: 'Other', labelKey: 'system.usersNewStaff.relationshipOther', defaultLabel: '其他' },
] as const

export type GuardianRelationship = (typeof GUARDIAN_RELATIONSHIP_OPTIONS)[number]['value']

export interface NewStaffGuardianPayload {
  name: string
  relationship: string
  phone: string
  email: string
}

export interface NewStaffPayloadFields {
  phone: string
  birthday: string
  guardian?: NewStaffGuardianPayload
}

/** 表单里与新建员工相关的扁平字段（生日已由调用方格式化成 YYYY-MM-DD）。 */
export interface NewStaffFormFields {
  email?: string | null
  phone?: string | null
  birthday?: string | null
  guardianName?: string | null
  guardianRelationship?: string | null
  guardianPhone?: string | null
  guardianEmail?: string | null
}

const isoDatePattern = /^(\d{4})-(\d{2})-(\d{2})$/

function parseIsoDate(value: string | null | undefined) {
  const match = value?.trim().match(isoDatePattern)
  if (!match) return null
  const year = Number(match[1])
  const month = Number(match[2])
  const day = Number(match[3])
  // 用 UTC 构造只为校验日历合法性（如 2月30日），不涉及时区换算。
  const date = new Date(Date.UTC(year, month - 1, day))
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) return null
  return { year, month, day }
}

/** 生日是否在允许范围内：1940-01-01 ≤ 生日 ≤ 悉尼今天（都是 YYYY-MM-DD，可直接按字符串比较）。 */
export function isBirthdayInRange(birthday: string | null | undefined, todayIso: string) {
  if (!parseIsoDate(birthday) || !parseIsoDate(todayIso)) return false
  const value = birthday!.trim()
  return value >= EARLIEST_BIRTHDAY && value <= todayIso
}

/**
 * 按「今天」（悉尼日期）算周岁：今年还没到生日就减 1。
 * 2 月 29 日出生的人在平年 3 月 1 日才满岁，与后端 AgeOn（birthday > today.AddYears(-age)）结果一致，见单测。
 */
export function calculateAge(birthday: string | null | undefined, todayIso: string): number | null {
  const birth = parseIsoDate(birthday)
  const today = parseIsoDate(todayIso)
  if (!birth || !today) return null
  let age = today.year - birth.year
  if (today.month < birth.month || (today.month === birth.month && today.day < birth.day)) age -= 1
  return age
}

export function isMinorAge(age: number | null | undefined): boolean {
  return typeof age === 'number' && age >= 0 && age < ADULT_AGE
}

/** 生日在范围内且未满 18 岁。 */
export function isMinorBirthday(birthday: string | null | undefined, todayIso: string) {
  return isBirthdayInRange(birthday, todayIso) && isMinorAge(calculateAge(birthday, todayIso))
}

/**
 * 澳洲手机号规范化为 E.164（+614xxxxxxxx），逐条照搬后端 AustralianPhoneNumber.TryNormalize：
 * 只接受数字和空格、横线、括号、点、开头的加号；支持 04…、+61 4…、+61 04…、0061 4…、61 4…（11 位）。
 * 不是澳洲手机号返回 null。
 */
export function normalizeAustralianMobile(raw: string | null | undefined): string | null {
  if (!raw || !raw.trim()) return null
  const text = raw.trim()
  if (/[^0-9 \-().+]/.test(text)) return null
  if (text.slice(1).includes('+')) return null
  const hasPlus = text.startsWith('+')
  const digits = text.replace(/\D/g, '')

  let national: string
  if (hasPlus || digits.startsWith('0061')) {
    const withoutPrefix = digits.startsWith('0061') ? digits.slice(2) : digits
    if (!withoutPrefix.startsWith('61')) return null
    national = withoutPrefix.slice(2)
    if (national.startsWith('0')) national = national.slice(1)
  } else if (digits.startsWith('61') && digits.length === 11) {
    national = digits.slice(2)
  } else if (digits.startsWith('0')) {
    national = digits.slice(1)
  } else {
    return null
  }

  if (national.length !== 9 || national[0] !== '4') return null
  return `+61${national}`
}

export function isAustralianMobile(raw: string | null | undefined) {
  return normalizeAustralianMobile(raw) !== null
}

function digitsOf(value: string | null | undefined) {
  return (value ?? '').replace(/\D/g, '')
}

/** 员工电话：后端只要求 8~15 位数字（可以是座机）。 */
export function isStaffPhoneValid(raw: string | null | undefined) {
  const count = digitsOf(raw).length
  return count >= 8 && count <= 15
}

/** 监护人手机与员工电话是否相同：员工电话是澳洲手机时按规范化号码比，否则按纯数字比（同后端）。 */
export function isSameContactPhone(staffPhone: string | null | undefined, guardianPhone: string | null | undefined) {
  if (!staffPhone?.trim() || !guardianPhone?.trim()) return false
  const staff = normalizeAustralianMobile(staffPhone)
  if (staff) return staff === normalizeAustralianMobile(guardianPhone)
  return digitsOf(staffPhone) === digitsOf(guardianPhone)
}

export function isSameEmail(a: string | null | undefined, b: string | null | undefined) {
  const left = a?.trim().toLowerCase()
  const right = b?.trim().toLowerCase()
  return !!left && left === right
}

function trimmed(value: string | null | undefined) {
  return (value ?? '').trim()
}

/**
 * 组装提交给 POST /api/Users 的新增字段：phone、birthday，以及只在未成年时才带的 guardian。
 * 生日改回成年后，表单里残留的监护人值不会被提交。
 */
export function buildNewStaffPayloadFields(values: NewStaffFormFields, todayIso: string): NewStaffPayloadFields {
  const birthday = trimmed(values.birthday)
  const fields: NewStaffPayloadFields = { phone: trimmed(values.phone), birthday }
  if (isMinorBirthday(birthday, todayIso)) {
    fields.guardian = {
      name: trimmed(values.guardianName),
      relationship: trimmed(values.guardianRelationship),
      phone: trimmed(values.guardianPhone),
      email: trimmed(values.guardianEmail),
    }
  }
  return fields
}

/** 后端 NEW_STAFF_* 错误码对应的表单字段；GUARDIAN_DUPLICATE 按后端中文原因区分是手机还是邮箱重复。 */
export type NewStaffErrorField = 'birthday' | 'phone' | 'email' | 'guardianName' | 'guardianPhone' | 'guardianEmail'

export function getNewStaffErrorField(code: string | undefined, serverMessage?: string): NewStaffErrorField | null {
  switch (code) {
    case 'NEW_STAFF_BIRTHDAY_REQUIRED':
    case 'NEW_STAFF_BIRTHDAY_INVALID':
      return 'birthday'
    case 'NEW_STAFF_PHONE_REQUIRED':
      return 'phone'
    case 'NEW_STAFF_EMAIL_REQUIRED':
      return 'email'
    case 'NEW_STAFF_GUARDIAN_REQUIRED':
      return 'guardianName'
    case 'NEW_STAFF_GUARDIAN_PHONE_INVALID':
      return 'guardianPhone'
    case 'NEW_STAFF_GUARDIAN_EMAIL_INVALID':
      return 'guardianEmail'
    case 'NEW_STAFF_GUARDIAN_DUPLICATE':
      return /邮箱|email/i.test(serverMessage ?? '') ? 'guardianEmail' : 'guardianPhone'
    default:
      return null
  }
}
