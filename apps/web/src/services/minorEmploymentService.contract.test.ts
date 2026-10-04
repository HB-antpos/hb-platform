import { buildGuardianPreviewPayload, buildGuardianSignPayload, buildGuardianSignRequest, mapDetail, mapParentDocument, mapSummary } from './minorEmploymentService'

const fixture = { Id: 42, UserGUID: 'employee-42', EmployeeName: 'Alex Chen', StateCode: 'NSW', FormType: 'NSW_COMPANY_CONSENT', Version: 3, Status: 'awaiting_guardian_signature', GuardianSignedAtUtc: '2026-09-20T01:02:03Z', BackupContact: { FullName: 'Sam Chen', Relationship: 'Aunt', Phone: '0400000000', Email: 'sam@example.test' }, Contacts: [{ FullName: 'Sam Chen', Relationship: 'Aunt', Phone: '0400000000', Email: 'sam@example.test', Address: '1 Main St', Postcode: '4000', Mobile: '0400000000' }], SchoolCalendar: { SchoolProvider: 'Example High', Holidays: [{ StartDate: '2026-09-21', EndDate: '2026-10-02' }], WeeklySchedule: [{ Day: 'Mon', EndTime: '15:00' }] }, OtherWork: { HasOtherWork: true, HoursUnknown: false, Employers: [{ CompanyName: 'Other Co', WeeklyHours: { Mon: 4 } }], PlannedIntervals: [{ Start: '16:00', End: '18:00' }] }, Commute: { AfterSchoolToStoreMinutes: 25, HomewardMinutes: 30, TransportMode: 'Bus', PickupPerson: 'Sam Chen' }, FormData: { EmployerCompanyName: 'Hot Bargain Pty Ltd', RoleName: 'Shop assistant' } }
const assert = (condition: unknown, message: string) => { if (!condition) throw new Error(message) }
const summary = mapSummary(fixture)
assert(summary.id === '42', 'numeric DTO id must map to a string id')
assert(summary.status === 'AwaitingParentSignature', 'awaiting_guardian_signature must map to parent signature state')
const detail = mapDetail(fixture)
assert(detail.backupContact.name === 'Sam Chen', 'backup FullName must be shown')
assert(detail.education?.holidays?.length === 1, 'Pascal schoolCalendar fields must be mapped')
assert(detail.rosterEvidence?.afterSchoolToStoreMinutes === 25, 'Pascal commute fields must be mapped')
assert(detail.otherWork?.hasOtherWork === true, 'typed other work must not be replaced by zero-hour fallback')
assert(detail.risks === undefined, 'missing risks must remain unknown')
assert(detail.signedAt === fixture.GuardianSignedAtUtc, 'guardianSignedAtUtc must be used')
const parent = mapParentDocument(fixture)
assert(parent.employerName === 'Hot Bargain Pty Ltd', 'employerCompanyName must be displayed')
assert(parent.contacts?.[0]?.address === '1 Main St', 'contact address must be preserved')
const payload = buildGuardianSignPayload({ revision: 7, version: 3, consentScope: 'Server scope', parentName: 'Pat Chen', relationship: 'Parent', parentPhone: '0400000001', parentEmail: 'pat@example.test', signature: 'data:image/png;base64,sig', confirmRelationship: true, confirmBackupContact: false, confirmConsent: true })
assert(payload.confirmRelationship === true && payload.confirmBackupContact === false && payload.confirmConsent === true, 'signature confirmations must remain independent')
assert(!JSON.stringify(payload).includes('parentPhone') && !JSON.stringify(payload).includes('parentEmail'), 'signature API must use the immutable snapshot, not unsaved parent edits')
assert(JSON.stringify(buildGuardianPreviewPayload('opaque-token')) === JSON.stringify({ token: 'opaque-token' }), 'preview token must be sent only in the POST body')
assert(JSON.stringify(buildGuardianSignRequest('opaque-token', { revision: 7, version: 3, consentScope: 'Server scope', parentName: 'Pat Chen', relationship: 'Parent', parentPhone: '0400000001', parentEmail: 'pat@example.test', signature: 'sig', confirmRelationship: true, confirmBackupContact: true, confirmConsent: true })).includes('opaque-token'), 'sign token must be sent in the POST body')
console.log('minorEmploymentService.contract.test: ok')

assert(payload.version === 3 && payload.consentScope === 'Server scope', 'immutable version and server consent must not use draft revision or a different declaration')
