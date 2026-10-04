// 监护人签署页"现场修改资料"的纯函数：快照 → 表单值、比对改了哪些分区、生成提交给后端的修改内容。
// 后端会再逐项比对，只记录真正变化的；这里只负责把监护人动过的分区完整带上。

type Raw = Record<string, unknown>

export const AMENDABLE_SECTIONS = {
  childDetails: '孩子资料',
  guardianDetails: '监护人资料',
  education: '就读情况',
  schoolCalendar: '学校安排',
  otherWork: '其他工作',
  commute: '通勤与接送',
  backupContact: '备用联系人',
} as const
export type AmendableSection = keyof typeof AMENDABLE_SECTIONS

export const WEEK_DAY_KEYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'] as const
export const WEEK_DAY_LABELS = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'] as const
// 上学时间只让监护人核对周一到周五；周末等其他记录原样保留。
const SCHOOL_DAYS = [1, 2, 3, 4, 5]

export interface DateRangeValue { start?: string; end?: string; label?: string }
export interface WeeklyValue { dayOfWeek: number; mustAttend: boolean; start?: string; end?: string }
export interface EmployerValue {
  companyName?: string; tradingName?: string; address?: string; postcode?: string; phone?: string; email?: string
  hours: Partial<Record<(typeof WEEK_DAY_KEYS)[number], number | null>>
}
export interface ContactValue { fullName?: string; relationship?: string; phone?: string; mobile?: string; email?: string; address?: string; postcode?: string }

export interface GuardianReviewValues {
  child: { givenName?: string; familyName?: string; address?: string; postcode?: string; phone?: string; email?: string }
  guardian: { name?: string; relationship?: string; phone?: string; address?: string; postcode?: string }
  education: { schoolName?: string; yearLevel?: string; completedYear10?: boolean | null; flexibleSchoolingQualifiedTeacher?: boolean | null }
  school: {
    provider?: string; contactName?: string; contactPosition?: string; contactPhone?: string; contactMobile?: string; contactEmail?: string
    weekly: WeeklyValue[]; holidays: DateRangeValue[]; pupilFreeDays: DateRangeValue[]
  }
  otherWork: { hasOtherWork: boolean; hoursUnknown: boolean; employers: EmployerValue[] }
  commute: {
    afterSchoolToStoreMinutes?: number | null; homewardMinutes?: number | null; transportMode?: string; pickupPerson?: string
    latestTransportLocalTime?: string; latestWorkEndLocalTime?: string
  }
  contacts: ContactValue[]
}

const record = (value: unknown): Raw => (value && typeof value === 'object' && !Array.isArray(value) ? value as Raw : {})
const get = (value: Raw, key: string) => value[key] ?? value[key[0].toUpperCase() + key.slice(1)]
const str = (value: unknown) => (typeof value === 'string' ? value : typeof value === 'number' ? String(value) : '')
const list = (value: unknown): Raw[] => (Array.isArray(value) ? value.map(record) : [])
const bool = (value: unknown) => (typeof value === 'boolean' ? value : null)
const num = (value: unknown) => (typeof value === 'number' && Number.isFinite(value) ? value : null)
const day = (value: unknown) => (typeof value === 'number' ? value : WEEK_DAY_KEYS.indexOf(str(value) as (typeof WEEK_DAY_KEYS)[number]))
const ranges = (value: unknown): DateRangeValue[] =>
  list(value).map((x) => ({ start: str(get(x, 'startDate')).slice(0, 10), end: str(get(x, 'endDate')).slice(0, 10), label: str(get(x, 'label')) || undefined }))

/** 后端快照（EmployeeMinorComplianceDto 的 JSON）→ 签署页表单初始值。 */
export function toReviewValues(snapshot: Raw): GuardianReviewValues {
  const form = record(get(snapshot, 'formData'))
  const school = record(get(snapshot, 'schoolCalendar'))
  const work = record(get(snapshot, 'otherWork'))
  const commute = record(get(snapshot, 'commute'))
  const weeklyRaw = list(get(school, 'weeklySchedule'))
  return {
    child: {
      givenName: str(get(form, 'childGivenName')), familyName: str(get(form, 'childFamilyName')), address: str(get(form, 'childAddress')),
      postcode: str(get(form, 'childPostcode')), phone: str(get(form, 'childPhone')), email: str(get(form, 'childEmail')),
    },
    guardian: {
      name: str(get(snapshot, 'guardianName')), relationship: str(get(snapshot, 'guardianRelationship')), phone: str(get(snapshot, 'guardianPhone')),
      address: str(get(form, 'guardianAddress')), postcode: str(get(form, 'guardianPostcode')),
    },
    education: {
      schoolName: str(get(snapshot, 'schoolName')), yearLevel: str(get(snapshot, 'yearLevel')),
      completedYear10: bool(get(snapshot, 'completedYear10')), flexibleSchoolingQualifiedTeacher: bool(get(form, 'flexibleSchoolingQualifiedTeacher')),
    },
    school: {
      provider: str(get(school, 'schoolProvider')), contactName: str(get(school, 'schoolContactName')), contactPosition: str(get(school, 'schoolContactPosition')),
      contactPhone: str(get(school, 'schoolContactPhone')), contactMobile: str(get(school, 'schoolContactMobile')), contactEmail: str(get(school, 'schoolContactEmail')),
      weekly: SCHOOL_DAYS.map((dayOfWeek) => {
        const found = weeklyRaw.find((x) => day(get(x, 'dayOfWeek')) === dayOfWeek)
        return { dayOfWeek, mustAttend: get(found ?? {}, 'mustAttend') === true, start: str(get(found ?? {}, 'startLocalTime')), end: str(get(found ?? {}, 'endLocalTime')) }
      }),
      holidays: ranges(get(school, 'holidays')),
      pupilFreeDays: ranges(get(school, 'pupilFreeDays')),
    },
    otherWork: {
      hasOtherWork: get(work, 'hasOtherWork') === true,
      hoursUnknown: get(work, 'hoursUnknown') === true,
      employers: list(get(work, 'employers')).map((x) => {
        const hoursRaw = record(get(x, 'weeklyHours'))
        const hours: EmployerValue['hours'] = {}
        WEEK_DAY_KEYS.forEach((key, index) => {
          const value = num(hoursRaw[key] ?? hoursRaw[String(index)])
          if (value != null) hours[key] = value
        })
        return {
          companyName: str(get(x, 'companyName')), tradingName: str(get(x, 'tradingName')), address: str(get(x, 'address')),
          postcode: str(get(x, 'postcode')), phone: str(get(x, 'phone')), email: str(get(x, 'email')), hours,
        }
      }),
    },
    commute: {
      afterSchoolToStoreMinutes: num(get(commute, 'afterSchoolToStoreMinutes')), homewardMinutes: num(get(commute, 'homewardMinutes')),
      transportMode: str(get(commute, 'transportMode')), pickupPerson: str(get(commute, 'pickupPerson')),
      latestTransportLocalTime: str(get(commute, 'latestTransportLocalTime')), latestWorkEndLocalTime: str(get(commute, 'latestWorkEndLocalTime')),
    },
    contacts: list(get(snapshot, 'contacts')).map((x) => ({
      fullName: str(get(x, 'fullName')), relationship: str(get(x, 'relationship')), phone: str(get(x, 'phone')), mobile: str(get(x, 'mobile')),
      email: str(get(x, 'email')), address: str(get(x, 'address')), postcode: str(get(x, 'postcode')),
    })),
  }
}

// 比对前统一去空白、把 undefined / '' / null 视作同一个"空"，避免没动过的输入框被误判为已修改。
function normalize(value: unknown): unknown {
  if (typeof value === 'string') return value.trim() || null
  if (value === undefined || value === '') return null
  if (Array.isArray(value)) return value.map(normalize)
  if (value && typeof value === 'object') {
    // 值为空的键直接丢弃：表单里"没有这个键"和"键为空"是同一回事。
    return Object.fromEntries(Object.entries(value as Raw).map(([k, v]) => [k, normalize(v)] as const).filter(([, v]) => v !== null).sort(([a], [b]) => a.localeCompare(b)))
  }
  return value
}
const same = (a: unknown, b: unknown) => JSON.stringify(normalize(a)) === JSON.stringify(normalize(b))

const SECTION_FIELDS: Record<AmendableSection, (v: GuardianReviewValues) => unknown> = {
  childDetails: (v) => v.child,
  guardianDetails: (v) => v.guardian,
  education: (v) => v.education,
  schoolCalendar: (v) => v.school,
  otherWork: (v) => v.otherWork,
  commute: (v) => v.commute,
  backupContact: (v) => v.contacts,
}

/** 监护人动过的分区（与初始值逐项比对）。 */
export function changedSections(initial: GuardianReviewValues, current: GuardianReviewValues): AmendableSection[] {
  return (Object.keys(SECTION_FIELDS) as AmendableSection[]).filter((key) => !same(SECTION_FIELDS[key](initial), SECTION_FIELDS[key](current)))
}

const clean = (value?: string) => (value ?? '').trim()
const filledRange = (x: DateRangeValue) => clean(x.start) || clean(x.end)

/** 生成签署请求里的 amendments：只带改过的分区；没有任何修改时返回 undefined。 */
export function toAmendments(initial: GuardianReviewValues, current: GuardianReviewValues, snapshot: Raw): Raw | undefined {
  const sections = changedSections(initial, current)
  if (!sections.length) return undefined
  const out: Raw = {}
  if (sections.includes('childDetails')) Object.assign(out, {
    childGivenName: clean(current.child.givenName), childFamilyName: clean(current.child.familyName), childAddress: clean(current.child.address),
    childPostcode: clean(current.child.postcode), childPhone: clean(current.child.phone), childEmail: clean(current.child.email),
  })
  if (sections.includes('guardianDetails')) Object.assign(out, {
    guardianName: clean(current.guardian.name), guardianRelationship: clean(current.guardian.relationship), guardianPhone: clean(current.guardian.phone),
    guardianAddress: clean(current.guardian.address), guardianPostcode: clean(current.guardian.postcode),
  })
  if (sections.includes('education')) Object.assign(out, {
    schoolName: clean(current.education.schoolName), yearLevel: clean(current.education.yearLevel),
    completedYear10: current.education.completedYear10 ?? undefined, flexibleSchoolingQualifiedTeacher: current.education.flexibleSchoolingQualifiedTeacher ?? undefined,
  })
  if (sections.includes('schoolCalendar')) {
    const raw = record(get(snapshot, 'schoolCalendar'))
    // 周一到周五以外的记录、学期区间、时区不在签署页编辑范围，原样带回。
    const otherDays = list(get(raw, 'weeklySchedule'))
      .filter((x) => !SCHOOL_DAYS.includes(day(get(x, 'dayOfWeek'))))
      .map((x) => ({ dayOfWeek: day(get(x, 'dayOfWeek')), mustAttend: get(x, 'mustAttend') === true, startLocalTime: get(x, 'startLocalTime') ?? null, endLocalTime: get(x, 'endLocalTime') ?? null }))
    out.schoolCalendar = {
      schoolProvider: clean(current.school.provider), schoolContactName: clean(current.school.contactName), schoolContactPosition: clean(current.school.contactPosition),
      schoolContactPhone: clean(current.school.contactPhone), schoolContactMobile: clean(current.school.contactMobile), schoolContactEmail: clean(current.school.contactEmail),
      timeZoneId: get(raw, 'timeZoneId') ?? null,
      termRanges: list(get(raw, 'termRanges')).map((x) => ({ startDate: get(x, 'startDate'), endDate: get(x, 'endDate'), label: get(x, 'label') ?? null })),
      weeklySchedule: [
        ...current.school.weekly.map((x) => ({ dayOfWeek: x.dayOfWeek, mustAttend: x.mustAttend, startLocalTime: x.mustAttend ? clean(x.start) : null, endLocalTime: x.mustAttend ? clean(x.end) : null })),
        ...otherDays,
      ],
      holidays: current.school.holidays.filter(filledRange).map((x) => ({ startDate: clean(x.start), endDate: clean(x.end), label: clean(x.label) || null })),
      pupilFreeDays: current.school.pupilFreeDays.filter(filledRange).map((x) => ({ startDate: clean(x.start), endDate: clean(x.end), label: clean(x.label) || null })),
    }
  }
  if (sections.includes('otherWork')) out.otherWork = {
    hasOtherWork: current.otherWork.hasOtherWork,
    hoursUnknown: current.otherWork.hoursUnknown,
    employers: current.otherWork.hasOtherWork ? current.otherWork.employers.map((x) => ({
      companyName: clean(x.companyName), tradingName: clean(x.tradingName), address: clean(x.address), postcode: clean(x.postcode),
      phone: clean(x.phone), email: clean(x.email),
      weeklyHours: Object.fromEntries(Object.entries(x.hours ?? {}).filter(([, value]) => typeof value === 'number' && value > 0)),
    })) : [],
  }
  if (sections.includes('commute')) out.commute = {
    afterSchoolToStoreMinutes: current.commute.afterSchoolToStoreMinutes ?? 0, homewardMinutes: current.commute.homewardMinutes ?? 0,
    transportMode: clean(current.commute.transportMode), pickupPerson: clean(current.commute.pickupPerson),
    latestTransportLocalTime: clean(current.commute.latestTransportLocalTime), latestWorkEndLocalTime: clean(current.commute.latestWorkEndLocalTime),
  }
  if (sections.includes('backupContact')) out.contacts = current.contacts
    .filter((x) => clean(x.fullName) || clean(x.phone))
    .map((x) => ({
      fullName: clean(x.fullName), relationship: clean(x.relationship), phone: clean(x.phone), mobile: clean(x.mobile),
      email: clean(x.email) || null, address: clean(x.address), postcode: clean(x.postcode),
    }))
  return out
}
