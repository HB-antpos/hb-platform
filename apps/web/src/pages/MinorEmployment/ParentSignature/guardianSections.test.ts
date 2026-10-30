import { toAmendments, toReviewValues } from './guardianAmendments'
import type { GuardianReviewValues } from './guardianAmendments'
import {
  completenessContext, evaluateGuardianSections, guardianProgress, isValidPhone, normalizePhone, prefillReviewValues, schoolCalendarProblem, sectionsMissingRequired,
} from './guardianSections'
import type { GuardianSectionKey } from './guardianSections'

const assert = (condition: unknown, message: string) => { if (!condition) throw new Error(message) }
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T
const statusOf = (values: GuardianReviewValues, key: GuardianSectionKey, context = {}) => evaluateGuardianSections(values, context).find((x) => x.key === key)!

// 门店刚发出的草稿：只有孩子姓名（来自员工资料）、雇主、监护人姓名与手机；学校、通勤、其他工作、备用联系人都空着。
const storeDraft = {
  guardianName: 'Linh Nguyen', guardianPhone: '0412 345 678', guardianEmail: null, requiredToBeEnrolled: null,
  formData: { childGivenName: 'Mia', childFamilyName: 'Nguyen', employerCompanyName: 'Hot Bargain' },
  schoolCalendar: null, otherWork: null, commute: null, contacts: [],
}
const draft = toReviewValues(storeDraft)
const draftStates = evaluateGuardianSections(draft, completenessContext(storeDraft))

// 门店预填的孩子与监护人资料是齐全的；学校与备用联系人是后端必填 → 缺；其他工作、通勤只是建议。
assert(draftStates.find((x) => x.key === 'child')?.status === 'complete', 'child prefilled by the store must be complete')
assert(draftStates.find((x) => x.key === 'guardian')?.status === 'suggested', 'guardian without relationship is only a suggestion, not required')
assert(draftStates.find((x) => x.key === 'school')?.status === 'required', 'school name is required when enrolment is not explicitly false')
assert(draftStates.find((x) => x.key === 'backupContact')?.status === 'required', 'at least one backup contact is required')
assert(draftStates.find((x) => x.key === 'otherWork')?.status === 'suggested', 'unanswered other work must be a suggestion, not a required field')
assert(draftStates.find((x) => x.key === 'commute')?.status === 'suggested', 'empty commute must be a suggestion, not a required field')
assert(draft.otherWork.hasOtherWork === null, 'missing other work data means the question is unanswered')
assert(JSON.stringify(sectionsMissingRequired(draftStates).map((x) => x.key)) === JSON.stringify(['school', 'backupContact']), 'only school and backup contact block signing')
assert(guardianProgress(draftStates).complete === 1 && guardianProgress(draftStates).total === 6, 'progress counts only fully complete sections')

// 家长补全后：学校、备用联系人齐全 → 不再缺必填；amendments 带上补全的分区。
const filled = clone(draft)
filled.guardian.relationship = 'Mother'
filled.school.provider = 'Example High School'
filled.education.schoolName = 'Example High School'
filled.education.yearLevel = 'Year 10'
filled.school.weekly[0] = { dayOfWeek: 1, mustAttend: true, start: '08:40', end: '15:10' }
filled.otherWork.hasOtherWork = false
filled.commute.transportMode = 'Bus'
filled.contacts = [{ fullName: 'An Tran', phone: '0498 765 432', relationship: 'Aunt' }]
const filledStates = evaluateGuardianSections(filled, completenessContext(storeDraft))
assert(sectionsMissingRequired(filledStates).length === 0, 'completed form must not block signing')
assert(guardianProgress(filledStates).complete === 6, 'all six sections complete')
const amend = toAmendments(draft, filled, storeDraft)!
assert((amend.schoolCalendar as { schoolProvider: string }).schoolProvider === 'Example High School', 'school provider must be sent so the backend completeness check passes')
assert(Array.isArray(amend.contacts) && (amend.contacts as unknown[]).length === 1, 'new backup contact must be sent')
assert((amend.otherWork as { hasOtherWork: boolean }).hasOtherWork === false, 'answering "No" for other work must be sent')

// 不需要入学（requiredToBeEnrolled === false）时学校名不是必填。
assert(statusOf(draft, 'school', { requiredToBeEnrolled: false }).missing.length === 0, 'school name is optional when the child is not required to be enrolled')

// 孩子姓名缺失 → 必填。
const noChild = clone(filled); noChild.child.familyName = ' '
assert(statusOf(noChild, 'child').status === 'required', "child's last name is required")

// 监护人电话：必填且格式与后端一致。
const badGuardianPhone = clone(filled); badGuardianPhone.guardian.phone = '12345'
assert(statusOf(badGuardianPhone, 'guardian').status === 'required', 'guardian phone must be a valid phone number')
assert(isValidPhone('+61 412 345 678') && isValidPhone('(02) 9000 0000') && !isValidPhone('1234567') && !isValidPhone('0412-ABC-678'), 'phone validation must follow backend rules')
assert(normalizePhone('+61 412 345 678') === '0412345678', '+61 numbers must normalise to 0-prefixed')

// 备用联系人：电话 / 手机与监护人相同（含 +61 写法）不行；邮箱只在两边都填时比较。
const samePhone = clone(filled); samePhone.contacts = [{ fullName: 'Linh', phone: '+61 412 345 678' }]
assert(statusOf(samePhone, 'backupContact').status === 'required', 'backup phone equal to the guardian phone must be rejected')
const sameMobile = clone(filled); sameMobile.contacts = [{ fullName: 'An', phone: '0498 765 432', mobile: '0412345678' }]
assert(statusOf(sameMobile, 'backupContact').status === 'required', 'backup mobile equal to the guardian phone must be rejected')
const sameEmail = clone(filled); sameEmail.contacts = [{ fullName: 'An', phone: '0498 765 432', email: 'Linh@Example.test' }]
assert(statusOf(sameEmail, 'backupContact', { guardianEmail: 'linh@example.test' }).status === 'required', 'backup email equal to the guardian email must be rejected')
assert(statusOf(filled, 'backupContact', { guardianEmail: null }).status === 'complete', 'empty guardian email (SMS only) must not count as a match')
const noPhone = clone(filled); noPhone.contacts = [{ fullName: 'An', phone: '' }]
assert(statusOf(noPhone, 'backupContact').missing.includes('Backup contact phone number'), 'backup contact needs a valid phone')
const blankRow = clone(filled); blankRow.contacts = [{ fullName: ' ', phone: '' }]
assert(statusOf(blankRow, 'backupContact').missing.includes('At least one backup contact'), 'blank contact rows do not count')

// 学校安排格式：起止时间、开始早于结束、日期区间。
const school = clone(filled.school)
assert(schoolCalendarProblem(school) === undefined, 'valid timetable has no problem')
school.weekly[0] = { dayOfWeek: 1, mustAttend: true, start: '15:00', end: '09:00' }
assert(Boolean(schoolCalendarProblem(school)), 'start after end must be a problem')
school.weekly[0] = { dayOfWeek: 1, mustAttend: true, start: '', end: '15:00' }
assert(Boolean(schoolCalendarProblem(school)), 'missing start time on a school day must be a problem')
school.weekly[0] = { dayOfWeek: 1, mustAttend: false, start: '', end: '' }
school.holidays = [{ start: '2026-12-20', end: '2026-12-10' }]
assert(Boolean(schoolCalendarProblem(school)), 'holiday ending before it starts must be a problem')
school.holidays = [{ start: '2026-12-20', end: '' }]
assert(Boolean(schoolCalendarProblem(school)), 'half-filled date range must be a problem')
school.holidays = [{ start: '', end: '' }]
assert(schoolCalendarProblem(school) === undefined, 'empty date rows are ignored (dropped on submit)')

// 其他工作：选了"有"但没填雇主 → 建议，不阻止签字。
const otherYes = clone(filled); otherYes.otherWork.hasOtherWork = true; otherYes.otherWork.employers = []
assert(statusOf(otherYes, 'otherWork').status === 'suggested', 'other work without employer details is a suggestion')

// 学校名预填：旧资料只有就读学校名、学校安排为空时预填，且作为修改提交。
const legacy = toReviewValues({ ...storeDraft, schoolName: 'Old College' })
const prefilled = prefillReviewValues(legacy)
assert(prefilled.school.provider === 'Old College' && legacy.school.provider === '', 'school provider must be prefilled from the school name without mutating the baseline')
assert((toAmendments(legacy, prefilled, storeDraft)!.schoolCalendar as { schoolProvider: string }).schoolProvider === 'Old College', 'prefilled provider must be sent as an amendment')
assert(prefillReviewValues(filled) === filled, 'values with a provider are returned unchanged')

// 快照字段大小写兼容。
const pascal = completenessContext({ RequiredToBeEnrolled: false, GuardianEmail: 'a@b.test' })
assert(pascal.requiredToBeEnrolled === false && pascal.guardianEmail === 'a@b.test', 'context must read PascalCase snapshot keys')

console.log('guardianSections.test: ok')
