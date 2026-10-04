import { changedSections, toAmendments, toReviewValues } from './guardianAmendments'

const assert = (condition: unknown, message: string) => { if (!condition) throw new Error(message) }
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T

// 后端快照：混用 PascalCase / camelCase，周几为数字，其他雇主工时字典键为英文星期名。
const snapshot = {
  GuardianName: 'Casey Morgan', GuardianPhone: '07 3000 0000', GuardianEmail: 'casey@example.test', GuardianRelationship: 'Parent',
  SchoolName: 'Example College', YearLevel: 'Year 9', CompletedYear10: false, DateOfBirth: '2011-05-01T00:00:00',
  FormData: { ChildGivenName: 'Alex', ChildFamilyName: 'Morgan', GuardianAddress: '1 Example Street', GuardianPostcode: '4000', EmployerCompanyName: 'Example Retail Pty Ltd' },
  SchoolCalendar: {
    SchoolProvider: 'Example College', TimeZoneId: 'Australia/Brisbane',
    TermRanges: [{ StartDate: '2026-07-13T00:00:00', EndDate: '2026-09-18T00:00:00' }],
    WeeklySchedule: [
      { DayOfWeek: 1, MustAttend: true, StartLocalTime: '09:00', EndLocalTime: '15:00' },
      { DayOfWeek: 3, MustAttend: true, StartLocalTime: '12:00', EndLocalTime: '13:00' },
      { DayOfWeek: 6, MustAttend: true, StartLocalTime: '09:00', EndLocalTime: '10:00' },
    ],
    Holidays: [{ StartDate: '2026-12-10T00:00:00', EndDate: '2026-12-31T00:00:00' }],
    PupilFreeDays: [],
  },
  OtherWork: { HasOtherWork: true, Employers: [{ CompanyName: 'First Cafe Pty Ltd', WeeklyHours: { Saturday: 3 } }] },
  Commute: { AfterSchoolToStoreMinutes: 25, HomewardMinutes: 30, TransportMode: 'Parent pickup' },
  Contacts: [{ FullName: 'Jordan Taylor', Phone: '0400 000 000', Relationship: 'Aunt', Email: 'jordan@example.test' }],
}

const initial = toReviewValues(snapshot)
assert(initial.guardian.name === 'Casey Morgan' && initial.guardian.address === '1 Example Street', 'guardian fields must map from snapshot and formData')
assert(initial.child.givenName === 'Alex', 'child name must map from formData')
assert(initial.school.weekly.length === 5 && initial.school.weekly[0].dayOfWeek === 1, 'school week must list Monday to Friday')
assert(initial.school.weekly[0].mustAttend && initial.school.weekly[0].start === '09:00', 'Monday attendance must be prefilled')
assert(!initial.school.weekly[1].mustAttend, 'days without a record must default to not attending')
assert(initial.school.holidays[0].start === '2026-12-10', 'holiday dates must be trimmed to yyyy-MM-dd')
assert(initial.otherWork.employers[0].hours.Saturday === 3, 'other employer hours must map by weekday name')
assert(initial.contacts[0].fullName === 'Jordan Taylor', 'backup contact must map')

// 没改或只多了空格：不算修改，不发送 amendments。
assert(toAmendments(initial, clone(initial), snapshot) === undefined, 'unchanged values must not produce amendments')
const spaced = clone(initial); spaced.guardian.name = ' Casey Morgan '
assert(changedSections(initial, spaced).length === 0, 'whitespace-only edits must not count as changes')

// 只改监护人电话：只带监护人分区。
const phone = clone(initial); phone.guardian.phone = '0499 888 777'
assert(JSON.stringify(changedSections(initial, phone)) === JSON.stringify(['guardianDetails']), 'only the guardian section must be reported')
const phoneAmend = toAmendments(initial, phone, snapshot)!
assert(phoneAmend.guardianPhone === '0499 888 777' && phoneAmend.guardianName === 'Casey Morgan', 'guardian section must be sent in full')
assert(!('schoolCalendar' in phoneAmend) && !('contacts' in phoneAmend), 'untouched sections must not be sent')

// 改学校时间：学期区间、时区、周末记录原样带回。
const school = clone(initial); school.school.weekly[1] = { dayOfWeek: 2, mustAttend: true, start: '08:30', end: '15:10' }
const schoolAmend = toAmendments(initial, school, snapshot)!
const calendar = schoolAmend.schoolCalendar as { timeZoneId: string; termRanges: unknown[]; weeklySchedule: { dayOfWeek: number; mustAttend: boolean; startLocalTime: string | null }[] }
assert(calendar.timeZoneId === 'Australia/Brisbane' && calendar.termRanges.length === 1, 'time zone and term ranges must pass through')
assert(calendar.weeklySchedule.some((x) => x.dayOfWeek === 2 && x.startLocalTime === '08:30'), 'edited Tuesday must be sent')
assert(calendar.weeklySchedule.some((x) => x.dayOfWeek === 6), 'weekend records outside the form must be preserved')
assert(calendar.weeklySchedule.find((x) => x.dayOfWeek === 4)?.startLocalTime === null, 'non-attending days must not carry times')

// 备用联系人：空邮箱发 null（后端 EmailAddress 校验不接受空串），空白行丢弃。
const contacts = clone(initial)
contacts.contacts = [{ fullName: 'Uncle Sam', phone: '0400 555 666', email: '' }, { fullName: ' ', phone: '' }]
const contactAmend = toAmendments(initial, contacts, snapshot)!
const sent = contactAmend.contacts as { fullName: string; email: string | null }[]
assert(sent.length === 1 && sent[0].fullName === 'Uncle Sam' && sent[0].email === null, 'blank rows must be dropped and empty email sent as null')

// 没有其他工作时不发送雇主列表。
const noWork = clone(initial); noWork.otherWork.hasOtherWork = false
assert(JSON.stringify((toAmendments(initial, noWork, snapshot)!.otherWork as { employers: unknown[] }).employers) === '[]', 'employers must be cleared when there is no other work')

console.log('guardianAmendments.test: ok')
