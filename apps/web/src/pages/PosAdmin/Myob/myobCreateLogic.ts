import type {
  MyobCreateDraft,
  MyobCreateFromMyobRequest,
  MyobCreateWarning,
  MyobEmploymentType,
} from '../../../types/myob'
import {
  buildNewStaffPayloadFields,
  getNewStaffErrorField,
  isMinorBirthday,
  normalizeAustralianMobile,
} from '../../System/Users/newStaffProfileRules'

/**
 * 「我方建档」纯逻辑：草稿 → 表单初值、表单 → 建档请求、核对页每行的「MYOB 原值 / 将写入 / 来源」。
 * 不依赖 React / antd / dayjs，便于在 node 下单测；生日在这里一律是 YYYY-MM-DD 字符串。
 */

export type MyobCreatePasswordMode = 'email' | 'manual'

export interface MyobCreateFormFields {
  givenName?: string
  familyName?: string
  username?: string
  email?: string
  phone?: string
  /** YYYY-MM-DD。 */
  birthday?: string
  employmentType?: MyobEmploymentType
  storeCode?: string
  passwordMode?: MyobCreatePasswordMode
  password?: string
  guardianName?: string
  guardianRelationship?: string
  guardianPhone?: string
  guardianEmail?: string
}

export const MYOB_EMPLOYMENT_TYPES: readonly MyobEmploymentType[] = ['FullTime', 'PartTime', 'Temporary']

function trimmed(value: string | null | undefined) {
  return (value ?? '').trim()
}

/** 草稿 → 表单初值：MYOB 有的直接填上，没有的留空等人工补；分店只在恰好一家映射到本公司时预选。 */
export function buildInitialCreateFields(draft: MyobCreateDraft): MyobCreateFormFields {
  return {
    givenName: draft.givenName,
    familyName: draft.familyName,
    username: draft.username,
    email: draft.email ?? '',
    phone: draft.phone ?? '',
    birthday: draft.birthday ?? undefined,
    employmentType: draft.employmentType ?? undefined,
    storeCode: draft.suggestedStoreCode ?? undefined,
    // 默认由系统把初始密码发到员工邮箱：管理员不经手密码；员工首次登录必须先改密。
    passwordMode: 'email',
  }
}

/** 表单 → 建档请求。监护人只在未满 16 岁时带（生日改回 16 岁以上后残留的监护人值不提交）；手动密码只在选「我来设置」时带。 */
export function buildCreateRequest(
  draft: MyobCreateDraft,
  fields: MyobCreateFormFields,
  todayIso: string,
): MyobCreateFromMyobRequest {
  const staff = buildNewStaffPayloadFields(
    {
      email: fields.email,
      phone: fields.phone,
      birthday: fields.birthday,
      guardianName: fields.guardianName,
      guardianRelationship: fields.guardianRelationship,
      guardianPhone: fields.guardianPhone,
      guardianEmail: fields.guardianEmail,
    },
    todayIso,
  )
  const manual = fields.passwordMode === 'manual'
  return {
    connectionGuid: draft.connectionGuid,
    myobEmployeeUid: draft.card.uid,
    username: trimmed(fields.username),
    givenName: trimmed(fields.givenName),
    familyName: trimmed(fields.familyName),
    email: trimmed(fields.email),
    phone: staff.phone,
    birthday: staff.birthday,
    ...(staff.guardian ? { guardian: staff.guardian } : {}),
    storeCode: trimmed(fields.storeCode),
    // 没选就传空串让后端拒绝：建员工服务遇到认不出的值会默认成临时工，前端不能替人兜底。
    employmentType: fields.employmentType ?? '',
    sendPasswordSetupEmail: !manual,
    ...(manual ? { password: fields.password ?? '' } : {}),
  }
}

/** 表单当前是否未满 16 岁（按悉尼今天）。 */
export function isCreateFormMinor(fields: MyobCreateFormFields, todayIso: string) {
  return isMinorBirthday(trimmed(fields.birthday), todayIso)
}

// ───────────────────────── 核对页 ─────────────────────────

/**
 * 每个字段的来源：
 * - myob：与 MYOB 原值一致（电话按规范化后的号码比，邮箱忽略大小写）；
 * - edited：MYOB 有值，但人工改了；
 * - filled：MYOB 没有，人工补的（监护人、MYOB 缺的邮箱 / 电话等）；
 * - system：系统给的（建议用户名、按公司映射预选的分店、邮件发初始密码），人工没改。
 */
export type MyobCreateFieldSource = 'myob' | 'edited' | 'filled' | 'system'

export type MyobCreateReviewKey =
  | 'givenName'
  | 'familyName'
  | 'username'
  | 'email'
  | 'phone'
  | 'birthday'
  | 'employmentType'
  | 'store'
  | 'guardianName'
  | 'guardianRelationship'
  | 'guardianPhone'
  | 'guardianEmail'
  | 'password'

export interface MyobCreateReviewRow {
  key: MyobCreateReviewKey
  /** MYOB 里的原值（展示用）；MYOB 没有这一项为 null。 */
  myobValue: string | null
  /** 将写入我方的值（展示用）。 */
  value: string
  source: MyobCreateFieldSource
}

/** 展示值的格式化（由组件按当前语言提供，纯逻辑里不碰 i18n）。 */
export interface MyobCreateReviewFormatters {
  employmentType: (type: MyobEmploymentType) => string
  store: (storeCode: string) => string
  relationship: (value: string) => string
  passwordMode: (mode: MyobCreatePasswordMode) => string
}

function sourceOf(myob: string | null | undefined, value: string, equals: (a: string, b: string) => boolean): MyobCreateFieldSource {
  const original = trimmed(myob)
  if (!original) {
    return 'filled'
  }
  return equals(original, value) ? 'myob' : 'edited'
}

const exact = (a: string, b: string) => a === b
const caseInsensitive = (a: string, b: string) => a.toLowerCase() === b.toLowerCase()

/** 两个电话是否同一号码：都是澳洲手机时按规范化后的号码比，否则只比数字。 */
export function isSamePhone(a: string | null | undefined, b: string | null | undefined) {
  const left = normalizeAustralianMobile(a)
  const right = normalizeAustralianMobile(b)
  if (left || right) {
    return left === right
  }
  const digits = (value: string | null | undefined) => (value ?? '').replace(/\D/g, '')
  return digits(a) !== '' && digits(a) === digits(b)
}

export function buildCreateReviewRows(
  draft: MyobCreateDraft,
  fields: MyobCreateFormFields,
  todayIso: string,
  format: MyobCreateReviewFormatters,
): MyobCreateReviewRow[] {
  const username = trimmed(fields.username)
  const phone = trimmed(fields.phone)
  const storeCode = trimmed(fields.storeCode)
  const rows: MyobCreateReviewRow[] = [
    { key: 'givenName', myobValue: draft.givenName || null, value: trimmed(fields.givenName), source: sourceOf(draft.givenName, trimmed(fields.givenName), exact) },
    { key: 'familyName', myobValue: draft.familyName || null, value: trimmed(fields.familyName), source: sourceOf(draft.familyName, trimmed(fields.familyName), exact) },
    // 用户名 MYOB 没有：沿用建议值算「系统」，改过算「人工修改」。
    { key: 'username', myobValue: null, value: username, source: username === draft.username ? 'system' : 'edited' },
    { key: 'email', myobValue: draft.email, value: trimmed(fields.email), source: sourceOf(draft.email, trimmed(fields.email), caseInsensitive) },
    // 电话原值显示 MYOB 里的写法（如 +61…），规范成 04… 不算人工修改。
    { key: 'phone', myobValue: draft.myobPhone, value: phone, source: sourceOf(draft.myobPhone, phone, isSamePhone) },
    { key: 'birthday', myobValue: draft.birthday, value: trimmed(fields.birthday), source: sourceOf(draft.birthday, trimmed(fields.birthday), exact) },
  ]

  const type = fields.employmentType
  rows.push({
    key: 'employmentType',
    // 原值显示 MYOB 的 EmploymentStatus 原文（FullTime / Casual / Other…）。
    myobValue: draft.card.employmentStatus,
    value: type ? format.employmentType(type) : '',
    source: draft.employmentType === null ? 'filled' : type === draft.employmentType ? 'myob' : 'edited',
  })
  rows.push({
    key: 'store',
    myobValue: null,
    value: storeCode ? format.store(storeCode) : '',
    source: storeCode && storeCode === draft.suggestedStoreCode ? 'system' : 'filled',
  })

  if (isCreateFormMinor(fields, todayIso)) {
    rows.push(
      { key: 'guardianName', myobValue: null, value: trimmed(fields.guardianName), source: 'filled' },
      {
        key: 'guardianRelationship',
        myobValue: null,
        value: fields.guardianRelationship ? format.relationship(fields.guardianRelationship) : '',
        source: 'filled',
      },
      { key: 'guardianPhone', myobValue: null, value: trimmed(fields.guardianPhone), source: 'filled' },
      { key: 'guardianEmail', myobValue: null, value: trimmed(fields.guardianEmail), source: 'filled' },
    )
  }

  const mode = fields.passwordMode ?? 'email'
  rows.push({ key: 'password', myobValue: null, value: format.passwordMode(mode), source: mode === 'email' ? 'system' : 'filled' })
  return rows
}

export function countEditedRows(rows: MyobCreateReviewRow[]) {
  return rows.filter((row) => row.source === 'edited').length
}

// ───────────────────────── 提醒与错误 ─────────────────────────

const WARNING_KEYS: Record<string, string> = {
  EMAIL_IN_USE: 'myob.create.warnings.emailInUse',
  PHONE_IN_USE: 'myob.create.warnings.phoneInUse',
  CARD_INACTIVE: 'myob.create.warnings.cardInactive',
  NO_MAPPED_STORE: 'myob.create.warnings.noMappedStore',
  MISSING_FIELDS: 'myob.create.warnings.missingFields',
}

/** 已知提醒码 → 文案键；未知码返回 null，界面直接显示后端文案。邮箱被已删除账号占用用单独的文案。 */
export function getCreateWarningKey(warning: MyobCreateWarning): string | null {
  if (warning.code === 'EMAIL_IN_USE' && warning.userDeleted) {
    return 'myob.create.warnings.emailInUseDeleted'
  }
  return WARNING_KEYS[warning.code] ?? null
}

/** 邮箱被我方在职账号占用：可以改为直接把卡绑定给他。 */
export function getBindExistingCandidate(warnings: MyobCreateWarning[]): MyobCreateWarning | null {
  return warnings.find((item) => item.code === 'EMAIL_IN_USE' && !item.userDeleted && item.userGuid) ?? null
}

/**
 * 请求层把业务失败拼成「CODE: 文案」；extractMyobError 只剥 MYOB_ 开头的码，
 * 建档会透传建员工服务的 USERNAME_EXISTS / NEW_STAFF_* 等，这里按本条错误码去掉前缀再给人看。
 */
export function stripErrorCodePrefix(message: string, code: string | null | undefined) {
  const prefix = code ? `${code}:` : ''
  return prefix && message.startsWith(prefix) ? message.slice(prefix.length).trim() : message
}

export type MyobCreateErrorField = 'username' | 'email' | 'phone' | 'birthday' | 'guardianName' | 'guardianPhone' | 'guardianEmail' | 'employmentType'

/** 建档失败时要退回第 1 步并标红的字段；与该字段无关的错误返回 null（停在核对页显示错误）。 */
export function getCreateErrorField(code: string | undefined, serverMessage?: string): MyobCreateErrorField | null {
  switch (code) {
    case 'USERNAME_EXISTS':
      return 'username'
    case 'EMAIL_EXISTS':
      return 'email'
    case 'MYOB_CREATE_INVALID_EMPLOYMENT_TYPE':
      return 'employmentType'
    default:
      return getNewStaffErrorField(code, serverMessage)
  }
}
