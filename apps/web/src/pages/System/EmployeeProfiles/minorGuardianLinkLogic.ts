import type {
  GuardianLinkExpiryDays,
  GuardianLinkSendPayload,
  GuardianLinkSendResult,
  MinorStaffComplianceItem,
  MinorStaffStage,
} from '../../../types/minorEmployment'

// 「未成年用工合规」页签的纯函数：阶段文案、发送开关默认值、澳洲手机判断、提交校验等，单测见同名 .test.ts。

export const STAGE_META: Record<MinorStaffStage, { label: string; color: string }> = {
  not_sent: { label: '未发送', color: 'orange' },
  link_expired: { label: '链接已过期', color: 'orange' },
  awaiting_guardian: { label: '待家长签字', color: 'blue' },
  pending_review: { label: '待审核', color: 'gold' },
  returned: { label: '退回修改', color: 'red' },
  approved: { label: '审核通过', color: 'green' },
}

export function getStageMeta(stage: MinorStaffStage) {
  return STAGE_META[stage] ?? STAGE_META.not_sent
}

/** 表格里用短名，弹窗里用全名。 */
export function getFormTypeLabel(formType: MinorStaffComplianceItem['formType'], full = false) {
  if (formType === 'QLD_CE1') return 'QLD CE1 家长同意表'
  if (formType === 'NSW_COMPANY_CONSENT') return full ? 'NSW 公司未成年员工同意书' : 'NSW 公司同意书'
  return '门店州未确认'
}

// 关系存英文值：会原样印在英文同意书上；界面显示中文。
export const RELATIONSHIP_OPTIONS = [
  { value: 'Mother', label: '母亲' },
  { value: 'Father', label: '父亲' },
  { value: 'Legal guardian', label: '法定监护人' },
  { value: 'Other', label: '其他' },
] as const

export function getRelationshipLabel(value?: string) {
  if (!value) return ''
  const matched = RELATIONSHIP_OPTIONS.find((option) => option.value.toLowerCase() === value.trim().toLowerCase())
  return matched ? matched.label : value
}

/** 已有值不在预设选项里时原样追加，避免打开弹窗就把旧值丢掉。 */
export function getRelationshipOptions(current?: string): Array<{ value: string; label: string }> {
  const options: Array<{ value: string; label: string }> = RELATIONSHIP_OPTIONS.map((option) => ({ ...option }))
  const trimmed = current?.trim()
  if (trimmed && !options.some((option) => option.value.toLowerCase() === trimmed.toLowerCase())) {
    options.push({ value: trimmed, label: trimmed })
  }
  return options
}

/** 与「待审核」退回补充弹窗的字段键一致。 */
const RETURN_FIELD_LABELS: Record<string, string> = {
  parent: '家长电话 / 邮箱',
  education: '学校时间与假期',
  backupContact: '备用联系人',
  otherWork: '其他工作',
  rosterEvidence: '通勤与排班事实',
}

export function getReturnFieldsText(fields: string[]) {
  return fields.map((field) => RETURN_FIELD_LABELS[field] ?? field).join('、')
}

/** 后端从库里读出的 *AtUtc 不带时区后缀（DateTimeKind 未指定），按 UTC 解析；带 Z / 偏移的原样解析。 */
export function parseUtcTimestamp(value?: string | null) {
  if (!value) return null
  const trimmed = value.trim()
  const normalized = /[zZ]$|[+-]\d{2}:?\d{2}$/.test(trimmed) || !/T\d{2}:/.test(trimmed) ? trimmed : `${trimmed}Z`
  const ms = Date.parse(normalized)
  return Number.isNaN(ms) ? null : ms
}

const sydneyFormatter = new Intl.DateTimeFormat('en-AU', {
  timeZone: 'Australia/Sydney',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
})

function getSydneyParts(ms: number) {
  return sydneyFormatter.formatToParts(new Date(ms)).reduce<Record<string, string>>((acc, part) => {
    if (part.type !== 'literal') acc[part.type] = part.value
    return acc
  }, {})
}

/** UTC 时间 → 悉尼「MM-DD HH:mm」；空值或无法解析返回 '—'。时区换算交给 Intl，不手算夏令时。 */
export function formatSydneyMonthDayTime(value?: string | null) {
  const ms = parseUtcTimestamp(value)
  if (ms === null) return '—'
  const parts = getSydneyParts(ms)
  return `${parts.month}-${parts.day} ${parts.hour}:${parts.minute}`
}

/** UTC 时间 → 悉尼「MM-DD」。 */
export function formatSydneyMonthDay(value?: string | null) {
  const ms = parseUtcTimestamp(value)
  if (ms === null) return '—'
  const parts = getSydneyParts(ms)
  return `${parts.month}-${parts.day}`
}

/** 澳洲手机号：04 开头 10 位，或 +614 / 614 开头（后跟 8 位）；空格、横线、括号忽略。 */
export function isAustralianMobile(phone?: string | null) {
  if (!phone) return false
  const digits = phone.replace(/[\s\-()]/g, '')
  return /^04\d{8}$/.test(digits) || /^\+?614\d{8}$/.test(digits)
}

export type SmsBlockReason = 'sms_unavailable' | 'not_mobile' | null

/** 短信开关是否可用：总开关没开 → 置灰；填了号码但不是澳洲手机 → 置灰；号码为空时先允许（电话必填会拦下）。 */
export function getSmsBlockReason(phone: string | undefined, smsAvailable: boolean): SmsBlockReason {
  if (!smsAvailable) return 'sms_unavailable'
  if (phone && phone.trim() && !isAustralianMobile(phone)) return 'not_mobile'
  return null
}

/** 默认开关：有邮箱则开邮件；短信可用且号码是澳洲手机则开短信。 */
export function getDefaultChannels(item: Pick<MinorStaffComplianceItem, 'guardianEmail' | 'guardianPhone'>, smsAvailable: boolean) {
  return {
    sendEmail: Boolean(item.guardianEmail?.trim()),
    sendSms: smsAvailable && isAustralianMobile(item.guardianPhone),
  }
}

export interface GuardianLinkFormValues {
  guardianName?: string
  guardianRelationship?: string
  guardianEmail?: string
  guardianPhone?: string
  sendEmail?: boolean
  sendSms?: boolean
  expiryDays?: GuardianLinkExpiryDays
}

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/

export function isValidEmail(value?: string) {
  return Boolean(value && EMAIL_PATTERN.test(value.trim()))
}

/** 实际生效的渠道：短信开关被置灰时，即使之前打开也按关闭处理。 */
export function getEffectiveChannels(values: GuardianLinkFormValues, smsAvailable: boolean) {
  return {
    sendEmail: values.sendEmail === true,
    sendSms: values.sendSms === true && getSmsBlockReason(values.guardianPhone, smsAvailable) === null,
  }
}

/** 是否可以提交：姓名、关系、电话必填；至少一个渠道；开邮件时邮箱必填且格式正确。 */
export function canSubmitGuardianLink(values: GuardianLinkFormValues, smsAvailable: boolean) {
  const channels = getEffectiveChannels(values, smsAvailable)
  if (!values.guardianName?.trim()) return false
  if (!values.guardianRelationship?.trim()) return false
  if (!values.guardianPhone?.trim()) return false
  if (!channels.sendEmail && !channels.sendSms) return false
  if (channels.sendEmail && !isValidEmail(values.guardianEmail)) return false
  return true
}

/** 验证码去向：开了短信走短信，否则走邮箱（与后端规则一致）。 */
export function getCodeChannel(values: GuardianLinkFormValues, smsAvailable: boolean): 'sms' | 'email' {
  return getEffectiveChannels(values, smsAvailable).sendSms ? 'sms' : 'email'
}

export function buildGuardianLinkPayload(
  item: Pick<MinorStaffComplianceItem, 'version' | 'revision'>,
  values: GuardianLinkFormValues,
  smsAvailable: boolean,
): GuardianLinkSendPayload {
  const channels = getEffectiveChannels(values, smsAvailable)
  const optional = (value?: string) => value?.trim() || undefined
  return {
    guardianName: values.guardianName?.trim() ?? '',
    guardianRelationship: optional(values.guardianRelationship),
    guardianEmail: optional(values.guardianEmail),
    guardianPhone: optional(values.guardianPhone),
    sendEmail: channels.sendEmail,
    sendSms: channels.sendSms,
    expiryDays: values.expiryDays ?? 3,
    // 并发保护：没有合规记录时传 null，由后端新建。
    expectedVersion: item.version ?? null,
    expectedRevision: item.revision ?? null,
  }
}

/** 全部渠道成功且没有返回链接 → 只弹成功提示；否则弹「发送结果」。 */
export function shouldShowSendResult(result: Pick<GuardianLinkSendResult, 'emailRequested' | 'emailSent' | 'smsRequested' | 'smsSent' | 'signingUrl'>) {
  const emailFailed = result.emailRequested && !result.emailSent
  const smsFailed = result.smsRequested && !result.smsSent
  return emailFailed || smsFailed || Boolean(result.signingUrl)
}

/** 发送结果弹窗里的黄色提示：按哪个渠道送达拼句子；只有验证码改走邮箱（短信失败）时才说最后一句。 */
export function getSendResultWarning(result: Pick<GuardianLinkSendResult, 'emailRequested' | 'emailSent' | 'smsRequested' | 'smsSent' | 'codeChannel'>) {
  const emailOk = result.emailRequested && result.emailSent
  const smsOk = result.smsRequested && result.smsSent
  const smsFailed = result.smsRequested && !result.smsSent
  let text: string
  if (emailOk && smsOk) {
    text = '链接仍然有效，家长可以用邮件或短信里的链接签字；也可以复制下面的链接手动转发。'
  } else if (emailOk) {
    text = '链接仍然有效，家长可以用邮件里的链接签字；也可以复制下面的链接手动转发。'
  } else if (smsOk) {
    text = '链接仍然有效，家长可以用短信里的链接签字；也可以复制下面的链接手动转发。'
  } else {
    text = '链接仍然有效，但邮件和短信都没有送达，请复制下面的链接手动转发给家长。'
  }
  if (result.codeChannel === 'email' && smsFailed) {
    text += '验证码会改发到邮箱。'
  }
  return text
}

export type MinorStaffAction = 'send' | 'resend' | 'revoke' | 'review' | 'document'

/** 按阶段给操作；approved 没有合规记录 Id 时无法打开原件。 */
export function getStaffActions(item: Pick<MinorStaffComplianceItem, 'stage' | 'complianceId'>): MinorStaffAction[] {
  switch (item.stage) {
    case 'not_sent':
    case 'link_expired':
      return ['send']
    case 'returned':
      return ['resend']
    case 'awaiting_guardian':
      return ['resend', 'revoke']
    case 'pending_review':
      return ['review']
    case 'approved':
      return item.complianceId ? ['document'] : []
    default:
      return []
  }
}

/** 最近发送的渠道结果：null 表示上次没选这个渠道，不显示。 */
export function getLastSentChannels(item: Pick<MinorStaffComplianceItem, 'lastSentEmail' | 'lastSentSms'>) {
  const channels: Array<{ label: string; ok: boolean }> = []
  if (typeof item.lastSentEmail === 'boolean') channels.push({ label: '邮件', ok: item.lastSentEmail })
  if (typeof item.lastSentSms === 'boolean') channels.push({ label: '短信', ok: item.lastSentSms })
  return channels
}
