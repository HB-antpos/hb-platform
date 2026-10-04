import type { ApiResponse, PagedResult } from '../types/api'
import type {
  GuardianSession,
  MinorEmploymentReviewDecision,
  MinorEmploymentReviewDetail,
  MinorEmploymentReviewQuery,
  MinorEmploymentReviewSummary,
  MinorEmploymentHistoryEntry,
  MinorEmploymentRisk,
  ParentSignatureDocument,
  ParentSignatureSubmit,
} from '../types/minorEmployment'
import request, { RequestError, unwrapApiData, unwrapPagedResult } from '../utils/request'

// 后端契约集中在本文件；页面不拼接 token，也不把 token 写入日志或埋点。
const ADMIN_PATH = '/api/minor-employment/hr'
const PUBLIC_SIGNATURE_PATH = '/api/minor-employment/guardian'

type Raw = Record<string, unknown>
function record(value: unknown): Raw { return value && typeof value === 'object' && !Array.isArray(value) ? value as Raw : {} }
function stateCode(value: unknown): MinorEmploymentReviewSummary['state'] { return value === 'NSW' ? 'NSW' : 'QLD' }
function formType(value: unknown): MinorEmploymentReviewSummary['formType'] { return value === 'NSW_COMPANY_CONSENT' ? 'NSW_COMPANY_CONSENT' : 'QLD_CE1' }
const text = (value: unknown) => (typeof value === 'string' && value.trim() ? value.trim() : typeof value === 'number' && Number.isFinite(value) ? String(value) : undefined)
const number = (value: unknown) => typeof value === 'number' && Number.isFinite(value) ? value : undefined
function mapStatus(value: unknown): MinorEmploymentReviewSummary['status'] {
  const status = text(value)?.toLowerCase()
  if (status === 'pending_hr_review' || status === 'pendingreview') return 'PendingReview'
  if (status === 'signed_pending_employee_submit' || status === 'signedawaitingsubmission') return 'SignedAwaitingSubmission'
  if (status === 'awaiting_parent_signature' || status === 'awaiting_guardian_signature' || status === 'awaitingparentsignature' || status === 'awaitingguardiansignature') return 'AwaitingParentSignature'
  if (status === 'approved') return 'Approved'
  if (status === 'returned' || status === 'returned_for_revision') return 'Returned'
  if (status === 'withdrawn') return 'Withdrawn'
  if (status === 'expired') return 'Expired'
  return 'Draft'
}
function backendStatus(value: MinorEmploymentReviewQuery['status']) {
  if (value === 'PendingReview') return 'pending_hr_review'
  if (value === 'SignedAwaitingSubmission') return 'signed_pending_employee_submit'
  if (value === 'AwaitingParentSignature') return 'awaiting_guardian_signature'
  if (value === 'Returned') return 'returned'
  return value?.toLowerCase()
}
function mapSchoolCalendar(value: unknown): MinorEmploymentReviewDetail['education'] {
  const raw = record(value)
  return { schoolProvider: text(raw.schoolProvider ?? raw.SchoolProvider), schoolName: text(raw.schoolName ?? raw.SchoolName), timeZoneId: text(raw.timeZoneId ?? raw.TimeZoneId), schoolContactName: text(raw.schoolContactName ?? raw.SchoolContactName), schoolContactEmail: text(raw.schoolContactEmail ?? raw.SchoolContactEmail), schoolContactPhone: text(raw.schoolContactPhone ?? raw.SchoolContactPhone), schoolContactPosition: text(raw.schoolContactPosition ?? raw.SchoolContactPosition), schoolContactMobile: text(raw.schoolContactMobile ?? raw.SchoolContactMobile), termRanges: (raw.termRanges ?? raw.TermRanges) as unknown[], holidays: (raw.holidays ?? raw.Holidays) as unknown[], pupilFreeDays: (raw.pupilFreeDays ?? raw.PupilFreeDays) as unknown[], weeklySchedule: raw.weeklySchedule ?? raw.WeeklySchedule }
}
function mapCommute(value: unknown): MinorEmploymentReviewDetail['rosterEvidence'] {
  const raw = record(value)
  return { afterSchoolToStoreMinutes: number(raw.afterSchoolToStoreMinutes ?? raw.AfterSchoolToStoreMinutes), homewardMinutes: number(raw.homewardMinutes ?? raw.HomewardMinutes), transportMode: text(raw.transportMode ?? raw.TransportMode), pickupPerson: text(raw.pickupPerson ?? raw.PickupPerson), latestTransportLocalTime: text(raw.latestTransportLocalTime ?? raw.LatestTransportLocalTime), latestWorkEndLocalTime: text(raw.latestWorkEndLocalTime ?? raw.LatestWorkEndLocalTime) }
}
function mapOtherWork(value: unknown): MinorEmploymentReviewDetail['otherWork'] {
  const raw = record(value)
  const employerValues = raw.employers ?? raw.Employers
  const employers = Array.isArray(employerValues) ? employerValues.map((item: unknown) => { const employer = record(item); return { companyName: text(employer.companyName ?? employer.CompanyName), tradingName: text(employer.tradingName ?? employer.TradingName), address: text(employer.address ?? employer.Address), postcode: text(employer.postcode ?? employer.Postcode), phone: text(employer.phone ?? employer.Phone), email: text(employer.email ?? employer.Email), weeklyHours: (employer.weeklyHours ?? employer.WeeklyHours) as Record<string, number> | undefined } }) : undefined
  const hasOtherWork = raw.hasOtherWork ?? raw.HasOtherWork
  const hoursUnknown = raw.hoursUnknown ?? raw.HoursUnknown
  return { hasOtherWork: typeof hasOtherWork === 'boolean' ? hasOtherWork : undefined, hoursUnknown: typeof hoursUnknown === 'boolean' ? hoursUnknown : undefined, employers, plannedIntervals: (raw.plannedIntervals ?? raw.PlannedIntervals) as unknown[] | undefined, actualIntervals: (raw.actualIntervals ?? raw.ActualIntervals) as unknown[] | undefined }
}

export function mapSummary(raw: Raw): MinorEmploymentReviewSummary {
  const riskValues = raw.risks ?? raw.Risks
  return {
    id: text(raw.id ?? raw.Id) ?? '',
    employeeId: text(raw.employeeId ?? raw.EmployeeId ?? raw.userGUID ?? raw.UserGUID) ?? '',
    employeeName: text(raw.employeeName ?? raw.EmployeeName ?? raw.userGUID ?? raw.UserGUID) ?? '',
    state: stateCode(text(raw.state ?? raw.State ?? raw.stateCode ?? raw.StateCode)),
    formType: formType(text(raw.formType ?? raw.FormType)),
    version: number(raw.version ?? raw.Version) ?? 0,
    status: mapStatus(raw.status ?? raw.Status),
    submittedAt: text(raw.submittedAtUtc ?? raw.SubmittedAtUtc ?? raw.submittedAt ?? raw.SubmittedAt),
    updatedAt: text(raw.updatedAt ?? raw.UpdatedAt),
    riskCount: number(raw.riskCount ?? raw.RiskCount) ?? (Array.isArray(riskValues) ? riskValues.length : 0),
    riskSummary: text(raw.riskSummary ?? raw.RiskSummary),
  }
}

export function mapDetail(raw: Raw): MinorEmploymentReviewDetail {
  const parent = record(raw.parent ?? raw.Parent)
  const contactValues = raw.contacts ?? raw.Contacts
  const contacts = Array.isArray(contactValues) ? contactValues.map((contact: unknown) => record(contact)) : []
  const backup = record(raw.backupContact ?? raw.BackupContact ?? contacts[0])
  const summary = mapSummary(raw)
  return {
    ...summary,
    snapshot: raw,
    age: number(raw.age ?? raw.Age),
    storeName: text(raw.storeName ?? raw.StoreName ?? raw.storeCode ?? raw.StoreCode),
    roleName: text(raw.roleName ?? raw.RoleName),
    employmentType: text(raw.employmentType ?? raw.EmploymentType),
    parent: {
      name: text(parent.name ?? parent.Name ?? raw.guardianName ?? raw.GuardianName) ?? '', relationship: text(parent.relationship ?? parent.Relationship ?? raw.guardianRelationship ?? raw.GuardianRelationship) ?? '',
      phone: text(parent.phone ?? parent.Phone ?? raw.guardianPhone ?? raw.GuardianPhone) ?? '', email: text(parent.email ?? parent.Email ?? raw.guardianEmail ?? raw.GuardianEmail), isParent: true,
    },
    backupContact: {
      name: text(backup.name ?? backup.Name ?? backup.fullName ?? backup.FullName) ?? '', relationship: text(backup.relationship ?? backup.Relationship) ?? '',
      phone: text(backup.phone ?? backup.Phone) ?? '', email: text(backup.email ?? backup.Email),
    },
    otherWork: mapOtherWork(raw.otherWork ?? raw.OtherWork),
    education: mapSchoolCalendar(raw.education ?? raw.schoolCalendar ?? raw.SchoolCalendar),
    rosterEvidence: mapCommute(raw.rosterEvidence ?? raw.commute ?? raw.Commute),
    risks: Array.isArray(raw.risks ?? raw.Risks) ? (raw.risks ?? raw.Risks) as MinorEmploymentRisk[] : undefined,
    signedAt: text(raw.guardianSignedAtUtc ?? raw.GuardianSignedAtUtc ?? raw.signedAt ?? raw.SignedAt), signedBy: text(raw.guardianSignedName ?? raw.GuardianSignedName ?? raw.signedBy ?? raw.SignedBy),
    signatureEvidenceId: text(raw.signatureEvidenceId ?? raw.SignatureEvidenceId),
    documentUrl: text(raw.documentUrl ?? raw.DocumentUrl), documentSha256: text(raw.documentSha256 ?? raw.DocumentSha256),
    reviewReason: text(raw.reviewComment ?? raw.ReviewComment ?? raw.reviewReason ?? raw.ReviewReason), reviewFields: Array.isArray(raw.returnFields ?? raw.ReturnFields) ? (raw.returnFields ?? raw.ReturnFields) as string[] : [],
    updatedBy: text(raw.updatedBy ?? raw.UpdatedBy), revision: number(raw.revision ?? raw.Revision ?? raw.version ?? raw.Version) ?? 0,
  }
}

export function mapParentDocument(raw: Raw): ParentSignatureDocument {
  const mapped = mapDetail(raw)
  const formData = record(raw.formData ?? raw.FormData)
  return {
    id: mapped.id,
    snapshot: raw,
    consentScope: text(raw.consentScope ?? raw.ConsentScope) ?? '',
    employeeName: mapped.employeeName,
    employeeAge: mapped.age,
    state: mapped.state,
    formType: mapped.formType,
    version: mapped.version,
    parentName: mapped.parent.name,
    relationship: mapped.parent.relationship,
    parentPhone: mapped.parent.phone,
    parentEmail: mapped.parent.email,
    backupContact: mapped.backupContact,
    education: mapped.education,
    employerName: text(formData.employerCompanyName ?? formData.EmployerCompanyName ?? formData.employerName ?? formData.EmployerName),
    roleName: text(formData.roleName ?? formData.RoleName),
    consentText: [
      mapped.formType === 'QLD_CE1' ? '我确认本表按 QLD CE1 字段填写，学校时间和假期安排真实有效。' : '我确认这是 Hot Bargain 的 NSW 公司未成年员工同意书。',
      '我同意员工只在适用法律、Award、学校安排和公司政策允许范围内工作。家长同意不会解除法定工时、休息或安全限制。',
      '我确认指定的备用联系人与家长是不同的人，并同意其仅用于必要的安全联系。',
    ],
    formData: raw.formData ?? raw.FormData,
    schoolCalendar: raw.schoolCalendar ?? raw.SchoolCalendar,
    otherWork: raw.otherWork ?? raw.OtherWork,
    commute: raw.commute ?? raw.Commute,
    contacts: contactsFromRaw(raw),
    expiresAt: text(raw.expiresAt ?? raw.ExpiresAt),
    revision: mapped.revision,
  }
}

function contactsFromRaw(raw: Raw) {
  const values = Array.isArray(raw.contacts ?? raw.Contacts) ? (raw.contacts ?? raw.Contacts) as unknown[] : []
  return values.map((value) => {
    const contact = record(value)
    return { name: text(contact.fullName ?? contact.FullName) ?? '', relationship: text(contact.relationship ?? contact.Relationship) ?? '', phone: text(contact.phone ?? contact.Phone) ?? '', email: text(contact.email ?? contact.Email), address: text(contact.address ?? contact.Address), postcode: text(contact.postcode ?? contact.Postcode), mobile: text(contact.mobile ?? contact.Mobile) }
  })
}

export async function getMinorEmploymentDocument(id: string) {
  const response = await request.get<ApiResponse<{ downloadUrl: string; fileName: string; sha256: string }>>(`${ADMIN_PATH}/${encodeURIComponent(id)}/document`)
  return unwrapApiData(response)
}

const API_BASE_URL = (((import.meta as ImportMeta & { env?: ImportMetaEnv }).env?.VITE_API_BASE_URL) || '').trim()

export async function fetchMinorEmploymentDocument(id: string) {
  const metadata = await getMinorEmploymentDocument(id)
  // request 封装只解析 JSON / 文本，PDF 原件用 fetch 读二进制；后端代理下载，依赖登录 Cookie。
  const url = /^https?:\/\//i.test(metadata.downloadUrl)
    ? metadata.downloadUrl
    : `${API_BASE_URL}${metadata.downloadUrl}`.replace(/([^:]\/)\/+/g, '$1')
  const response = await fetch(url, { credentials: 'include', headers: { Accept: 'application/pdf, application/json' } })
  const contentType = (response.headers.get('content-type') || '').toLowerCase()
  if (!response.ok || !contentType.includes('pdf')) {
    const payload = contentType.includes('json') ? await response.json().catch(() => null) : null
    const message = payload && typeof payload === 'object' && typeof (payload as ApiResponse<unknown>).message === 'string'
      ? (payload as ApiResponse<unknown>).message as string
      : `下载原件失败 (${response.status})`
    throw new RequestError(message, response.status, payload)
  }
  const blob = await response.blob()
  return { metadata, blob }
}

export async function getMinorEmploymentHistory(id: string) {
  const response = await request.get<ApiResponse<MinorEmploymentHistoryEntry[]>>(`${ADMIN_PATH}/${encodeURIComponent(id)}/history`)
  const value = unwrapApiData(response)
  return mapHistory(value)
}

export async function getMinorEmploymentReviews(params: MinorEmploymentReviewQuery = {}) {
  const response = await request.get<ApiResponse<PagedResult<MinorEmploymentReviewSummary>>>(ADMIN_PATH, { params: { page: params.page, pageSize: params.pageSize, keyword: params.keyword, stateCode: params.state, status: backendStatus(params.status) } })
  const result = unwrapPagedResult(response)
  return { ...result, items: result.items.map((item) => mapSummary(item as unknown as Raw)) }
}

export async function getMinorEmploymentReview(id: string) {
  const response = await request.get<ApiResponse<MinorEmploymentReviewDetail>>(`${ADMIN_PATH}/${encodeURIComponent(id)}`)
  return mapDetail(unwrapApiData(response) as unknown as Raw)
}

export async function decideMinorEmploymentReview(id: string, payload: MinorEmploymentReviewDecision) {
  const response = await request.post<ApiResponse<MinorEmploymentReviewDetail>>(
    `${ADMIN_PATH}/${encodeURIComponent(id)}/${payload.action === 'return' ? 'return' : 'approve'}`,
    { version: payload.version, comment: payload.reason, returnFields: payload.fields ?? [] },
  )
  return mapDetail(unwrapApiData(response) as unknown as Raw)
}

export async function getParentSignatureDocument(token: string, sessionKey?: string) {
  // token 只放在 POST body，不加入 URL 或埋点；未通过邮箱验证码时后端不返回任何资料。
  const response = await request.post<ApiResponse<ParentSignatureDocument>>(`${PUBLIC_SIGNATURE_PATH}/preview`, buildGuardianPreviewPayload(token, sessionKey), { skipAuthRedirect: true })
  return mapParentDocument(unwrapApiData(response) as unknown as Raw)
}

export async function submitParentSignature(token: string, payload: ParentSignatureSubmit, sessionKey?: string) {
  const response = await request.post<ApiResponse<ParentSignatureDocument>>(
    `${PUBLIC_SIGNATURE_PATH}/sign`, buildGuardianSignRequest(token, payload, sessionKey), { skipAuthRedirect: true },
  )
  return mapParentDocument(unwrapApiData(response) as unknown as Raw)
}

/** 监护人会话：只含打码邮箱与验证码节奏，验证前不暴露孩子资料。 */
export async function getGuardianSession(token: string, sessionKey?: string) {
  const response = await request.post<ApiResponse<Raw>>(`${PUBLIC_SIGNATURE_PATH}/session`, buildGuardianPreviewPayload(token, sessionKey), { skipAuthRedirect: true })
  return mapGuardianSession(unwrapApiData(response) as unknown as Raw)
}

export async function sendGuardianCode(token: string) {
  const response = await request.post<ApiResponse<Raw>>(`${PUBLIC_SIGNATURE_PATH}/send-code`, { token }, { skipAuthRedirect: true })
  return mapGuardianSession(unwrapApiData(response) as unknown as Raw)
}

/** 校验通过返回会话密钥，查看与签署都要带上。 */
export async function verifyGuardianCode(token: string, code: string) {
  const response = await request.post<ApiResponse<string>>(`${PUBLIC_SIGNATURE_PATH}/verify`, { token, code: code.replace(/\D/g, '') }, { skipAuthRedirect: true })
  return String(unwrapApiData(response) ?? '')
}

export function mapGuardianSession(raw: Raw): GuardianSession {
  return {
    maskedGuardianEmail: text(raw.maskedGuardianEmail ?? raw.MaskedGuardianEmail) ?? '',
    emailVerified: (raw.emailVerified ?? raw.EmailVerified) === true,
    codeSentAt: text(raw.codeSentAtUtc ?? raw.CodeSentAtUtc),
    codeExpiresAt: text(raw.codeExpiresAtUtc ?? raw.CodeExpiresAtUtc),
    resendAvailableAt: text(raw.resendAvailableAtUtc ?? raw.ResendAvailableAtUtc),
    remainingSends: Number(raw.remainingSends ?? raw.RemainingSends ?? 0) || 0,
  }
}

/** 从请求异常里取后端 errorCode（如 GUARDIAN_EMAIL_NOT_VERIFIED、GUARDIAN_CODE_COOLDOWN）。 */
export function guardianErrorCode(error: unknown) {
  if (!(error instanceof RequestError)) return undefined
  const payload = error.payload as Raw | undefined
  return text(payload?.errorCode ?? payload?.ErrorCode)
}

export function buildGuardianSignPayload(payload: ParentSignatureSubmit) {
  return {
    version: payload.version,
    signedName: payload.parentName,
    signatureData: payload.signature,
    confirmRelationship: payload.confirmRelationship,
    confirmConsent: payload.confirmConsent,
    confirmBackupContact: payload.confirmBackupContact,
    consentScope: payload.consentScope,
  }
}

export function buildGuardianPreviewPayload(token: string, sessionKey?: string) { return sessionKey ? { token, sessionKey } : { token } }
export function buildGuardianSignRequest(token: string, payload: ParentSignatureSubmit, sessionKey?: string) { return { token, ...(sessionKey ? { sessionKey } : {}), ...buildGuardianSignPayload(payload) } }

export function mapHistory(value: unknown): MinorEmploymentHistoryEntry[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((item) => {
    const version = record(item)
    const audits = version.audits ?? version.Audits
    const entries = Array.isArray(audits) && audits.length ? audits : [{ action: version.status ?? version.Status, createdAt: version.createdAt ?? version.CreatedAt }]
    return entries.map((entry, index) => {
      const audit = record(entry), meta = record(audit.metadata ?? audit.Metadata)
      return { id: `${version.id ?? version.Id}-${index}`, version: number(version.version ?? version.Version), action: text(audit.action ?? audit.Action) ?? '', actor: text(audit.actorLabel ?? audit.ActorLabel), createdAt: text(audit.createdAt ?? audit.CreatedAt), comment: text(meta.comment ?? meta.Comment) }
    })
  })
}
