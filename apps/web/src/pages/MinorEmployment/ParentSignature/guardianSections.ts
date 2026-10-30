// 家长签署页"分区完整性"的纯函数与英文标签：决定每个分区显示「已完整 / 必填缺失 / 建议填写」、进度条与签字按钮是否可用。
// 必填规则与后端 SignGuardianAsync 的三道校验保持一致，不额外发明后端没有的必填项：
//   ValidateFormCompleteness  —— 孩子名、姓；requiredToBeEnrolled !== false 时学校（SchoolCalendar.SchoolProvider）；
//   ValidateGuardianContact   —— 监护人姓名、有效电话（邮箱由门店录入，家长不能改，这里不校验）；
//   ValidateBackupContacts    —— 至少一名备用联系人，姓名 + 有效电话，电话/手机/邮箱不能与监护人相同；
//   ValidateSchoolCalendar    —— 需上学的日子起止时间 HH:mm 且开始早于结束，日期区间开始不晚于结束。
// 其他工作、通勤后端没有必填项：空着时只标「建议填写」，不阻止签字。

import type { AmendableSection, DateRangeValue, GuardianReviewValues } from './guardianAmendments'

export type GuardianSectionKey = 'child' | 'guardian' | 'school' | 'otherWork' | 'commute' | 'backupContact'
/** complete：必填齐全；required：缺必填（不能签字）；suggested：没有必填缺失，但空着建议家长补上。 */
export type GuardianSectionStatus = 'complete' | 'required' | 'suggested'

export interface GuardianSectionState {
  key: GuardianSectionKey
  status: GuardianSectionStatus
  /** 缺失的必填项（英文，直接展示给家长）。 */
  missing: string[]
  /** 建议补充的内容（英文）。 */
  suggestions: string[]
}

export interface GuardianCompletenessContext {
  /** 后端 RequiredToBeEnrolled：只有明确为 false 时学校才不是必填。 */
  requiredToBeEnrolled?: boolean | null
  /** 门店录入的监护人邮箱（家长不能改），用于判断备用联系人邮箱是否与监护人相同。 */
  guardianEmail?: string | null
}

/** 家长页显示的 6 个分区；"就读情况"和"学校安排"合成一项「School & timetable」。 */
export const GUARDIAN_SECTIONS: { key: GuardianSectionKey; label: string; amendable: AmendableSection[] }[] = [
  { key: 'child', label: 'Child details', amendable: ['childDetails'] },
  { key: 'guardian', label: 'Guardian details', amendable: ['guardianDetails'] },
  { key: 'school', label: 'School & timetable', amendable: ['education', 'schoolCalendar'] },
  { key: 'otherWork', label: 'Other work', amendable: ['otherWork'] },
  { key: 'commute', label: 'Travel & pick-up', amendable: ['commute'] },
  { key: 'backupContact', label: 'Backup contact', amendable: ['backupContact'] },
]

export const GUARDIAN_SECTION_LABELS: Record<GuardianSectionKey, string> = Object.fromEntries(
  GUARDIAN_SECTIONS.map((section) => [section.key, section.label]),
) as Record<GuardianSectionKey, string>

/** 家长页用的英文分区名（AMENDABLE_SECTIONS 的中文标签留给 HR 审核页）。 */
export const AMENDABLE_SECTION_LABELS_EN: Record<AmendableSection, string> = {
  childDetails: 'Child details',
  guardianDetails: 'Guardian details',
  education: 'School',
  schoolCalendar: 'School timetable',
  otherWork: 'Other work',
  commute: 'Travel & pick-up',
  backupContact: 'Backup contact',
}

/** 与 WEEK_DAY_KEYS 同序（0 = Sunday）。 */
export const WEEK_DAY_SHORT_EN = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'] as const

const clean = (value?: string | null) => (value ?? '').trim()
const TIME = /^([01]\d|2[0-3]):[0-5]\d$/
// 与后端 PhonePattern + 位数判断一致：6~30 个允许字符，数字 8~15 位。
const PHONE = /^[0-9+() .-]{6,30}$/

export function isValidPhone(value?: string | null) {
  const text = clean(value)
  const digits = text.replace(/\D/g, '').length
  return Boolean(text) && PHONE.test(text) && digits >= 8 && digits <= 15
}

/** 与后端 NormalizePhone 一致：只留数字，+61 开头的 11 位换成 0 开头，便于比较是不是同一个号码。 */
export function normalizePhone(value?: string | null) {
  const digits = clean(value).replace(/\D/g, '')
  return digits.startsWith('61') && digits.length === 11 ? `0${digits.slice(2)}` : digits
}

const rangeHasBothDates = (x: DateRangeValue) => Boolean(clean(x.start) && clean(x.end))
const rangeIsFilled = (x: DateRangeValue) => Boolean(clean(x.start) || clean(x.end))

/** 学校安排格式问题（英文）；没有问题返回 undefined。规则对应后端 ValidateSchoolCalendar。 */
export function schoolCalendarProblem(school: GuardianReviewValues['school']): string | undefined {
  for (const day of school.weekly ?? []) {
    if (!day.mustAttend) continue
    const start = clean(day.start)
    const end = clean(day.end)
    if (!TIME.test(start) || !TIME.test(end)) return 'School hours (start and end time for each school day)'
    if (start >= end) return 'School hours (start time must be before end time)'
  }
  const ranges = [...(school.holidays ?? []), ...(school.pupilFreeDays ?? [])].filter(rangeIsFilled)
  // 只填了一边的区间后端无法解析日期，也算格式问题。
  if (ranges.some((x) => !rangeHasBothDates(x))) return 'School holidays / pupil-free days (each needs a start and end date)'
  if (ranges.some((x) => clean(x.start) > clean(x.end))) return 'School holidays / pupil-free days (start date must not be after end date)'
  return undefined
}

/** 和 toAmendments 一致：姓名、电话都空的联系人行视为空白行，提交时会被丢弃。 */
const meaningfulContacts = (values: GuardianReviewValues) => (values.contacts ?? []).filter((x) => clean(x.fullName) || clean(x.phone))

function evaluate(key: GuardianSectionKey, values: GuardianReviewValues, context: GuardianCompletenessContext): Omit<GuardianSectionState, 'key' | 'status'> {
  const missing: string[] = []
  const suggestions: string[] = []
  switch (key) {
    case 'child':
      if (!clean(values.child?.givenName)) missing.push("Child's first name")
      if (!clean(values.child?.familyName)) missing.push("Child's last name")
      break
    case 'guardian':
      if (!clean(values.guardian?.name)) missing.push('Parent / guardian name')
      if (!isValidPhone(values.guardian?.phone)) missing.push('Parent / guardian phone number')
      if (!clean(values.guardian?.relationship)) suggestions.push('Relationship to the child')
      break
    case 'school': {
      if (context.requiredToBeEnrolled !== false && !clean(values.school?.provider)) missing.push('School name')
      const problem = values.school ? schoolCalendarProblem(values.school) : undefined
      if (problem) missing.push(problem)
      if (context.requiredToBeEnrolled !== false) {
        if (!clean(values.education?.yearLevel)) suggestions.push('Year level')
        if (!(values.school?.weekly ?? []).some((x) => x.mustAttend)) suggestions.push('School hours')
      }
      break
    }
    case 'otherWork':
      // null = 家长还没回答"是否在其他地方工作"。
      if (values.otherWork?.hasOtherWork == null) suggestions.push('Does your child work anywhere else?')
      else if (values.otherWork.hasOtherWork && !(values.otherWork.employers ?? []).some((x) => clean(x.companyName) || clean(x.tradingName))) suggestions.push('Other employer details')
      break
    case 'commute': {
      const c = values.commute ?? {}
      const empty = !c.afterSchoolToStoreMinutes && !c.homewardMinutes && !clean(c.transportMode) && !clean(c.pickupPerson)
        && !clean(c.latestTransportLocalTime) && !clean(c.latestWorkEndLocalTime)
      if (empty) suggestions.push('How your child gets to and from work')
      break
    }
    case 'backupContact': {
      const contacts = meaningfulContacts(values)
      if (contacts.length === 0) { missing.push('At least one backup contact'); break }
      if (contacts.some((x) => !clean(x.fullName))) missing.push('Backup contact name')
      if (contacts.some((x) => !isValidPhone(x.phone))) missing.push('Backup contact phone number')
      const guardianPhone = normalizePhone(values.guardian?.phone)
      const guardianEmail = clean(context.guardianEmail).toLowerCase()
      const sameAsGuardian = contacts.some((x) =>
        (guardianPhone && (normalizePhone(x.phone) === guardianPhone || (clean(x.mobile) && normalizePhone(x.mobile) === guardianPhone)))
        // 只在两边都填了邮箱时比较：监护人只登记手机（短信渠道）时邮箱为空，不能算"相同"。
        || (guardianEmail && clean(x.email).toLowerCase() === guardianEmail))
      if (sameAsGuardian) missing.push('A backup contact different from the parent / guardian (phone, mobile and email must not match yours)')
      break
    }
  }
  return { missing, suggestions }
}

/** 逐个分区评估完整性。 */
export function evaluateGuardianSections(values: GuardianReviewValues, context: GuardianCompletenessContext = {}): GuardianSectionState[] {
  return GUARDIAN_SECTIONS.map(({ key }) => {
    const { missing, suggestions } = evaluate(key, values, context)
    const status: GuardianSectionStatus = missing.length ? 'required' : suggestions.length ? 'suggested' : 'complete'
    return { key, status, missing, suggestions }
  })
}

/** 进度条："N of M complete"，只有完全齐全（含建议项）的分区才算完成。 */
export function guardianProgress(states: GuardianSectionState[]) {
  return { complete: states.filter((x) => x.status === 'complete').length, total: states.length }
}

/** 缺必填的分区（签字按钮因此禁用）。 */
export function sectionsMissingRequired(states: GuardianSectionState[]) {
  return states.filter((x) => x.status === 'required')
}

type Raw = Record<string, unknown>
const read = (value: Raw, key: string) => value[key] ?? value[key[0].toUpperCase() + key.slice(1)]

/** 从后端快照取完整性判断需要的上下文（字段名大小写都兼容）。 */
export function completenessContext(snapshot: Raw): GuardianCompletenessContext {
  const enrolled = read(snapshot, 'requiredToBeEnrolled')
  const email = read(snapshot, 'guardianEmail')
  return { requiredToBeEnrolled: typeof enrolled === 'boolean' ? enrolled : null, guardianEmail: typeof email === 'string' ? email : null }
}

/**
 * 表单初始值：家长页只显示一个「School name」（对应后端必填的 SchoolCalendar.SchoolProvider）。
 * 旧资料只填了就读情况里的学校名、学校安排里的学校为空时，用学校名预填，提交时会作为修改一并带上。
 */
export function prefillReviewValues(values: GuardianReviewValues): GuardianReviewValues {
  if (clean(values.school.provider) || !clean(values.education.schoolName)) return values
  return { ...values, school: { ...values.school, provider: clean(values.education.schoolName) } }
}
