export type MinorEmploymentState =
  | 'Draft'
  | 'AwaitingParentSignature'
  | 'SignedAwaitingSubmission'
  | 'PendingReview'
  | 'Approved'
  | 'Returned'
  | 'Withdrawn'
  | 'Expired'

export type MinorEmploymentStateCode = 'NSW' | 'QLD'
export type MinorEmploymentReviewAction = 'approve' | 'return'

export interface MinorEmploymentContact {
  name: string
  relationship: string
  phone: string
  email?: string
  isParent?: boolean
  address?: string
  postcode?: string
  mobile?: string
}

export interface MinorEmploymentRisk {
  code: string
  level: 'info' | 'warning' | 'high'
  title: string
  detail: string
  source?: string
  canPublishRoster: true
}

export interface MinorEmploymentReviewSummary {
  id: string
  employeeId: string
  employeeName: string
  state: MinorEmploymentStateCode
  formType: 'QLD_CE1' | 'NSW_COMPANY_CONSENT'
  version: number
  status: MinorEmploymentState
  submittedAt?: string
  updatedAt?: string
  riskCount: number
  riskSummary?: string
}

export interface MinorEmploymentReviewDetail extends MinorEmploymentReviewSummary {
  snapshot: Record<string, unknown>
  age?: number
  storeName?: string
  roleName?: string
  employmentType?: string
  parent: MinorEmploymentContact
  backupContact: MinorEmploymentContact
  otherWork?: { hasOtherWork?: boolean; hoursUnknown?: boolean; employers?: Array<{ companyName?: string; tradingName?: string; address?: string; postcode?: string; phone?: string; email?: string; weeklyHours?: Record<string, number> }>; plannedIntervals?: unknown[]; actualIntervals?: unknown[] }
  education?: { schoolProvider?: string; schoolName?: string; timeZoneId?: string; schoolContactName?: string; schoolContactEmail?: string; schoolContactPhone?: string; schoolContactPosition?: string; schoolContactMobile?: string; termRanges?: unknown[]; holidays?: unknown[]; pupilFreeDays?: unknown[]; weeklySchedule?: unknown }
  rosterEvidence?: { afterSchoolToStoreMinutes?: number; homewardMinutes?: number; transportMode?: string; pickupPerson?: string; latestTransportLocalTime?: string; latestWorkEndLocalTime?: string }
  risks?: MinorEmploymentRisk[]
  signedAt?: string
  signedBy?: string
  signatureEvidenceId?: string
  documentUrl?: string
  documentSha256?: string
  reviewReason?: string
  reviewFields?: string[]
  updatedBy?: string
  revision: number
}

export interface MinorEmploymentReviewQuery {
  page?: number
  pageSize?: number
  keyword?: string
  state?: MinorEmploymentStateCode
  status?: MinorEmploymentState
}

export interface MinorEmploymentReviewDecision {
  expectedRevision: number
  version: number
  action: MinorEmploymentReviewAction
  reason?: string
  fields?: string[]
}

export interface MinorEmploymentHistoryEntry {
  id: string | number
  version?: number
  action: string
  actor?: string
  createdAt?: string
  comment?: string
}

export interface ParentSignatureDocument {
  id: string
  employeeName: string
  employeeAge?: number
  state: MinorEmploymentStateCode
  formType: 'QLD_CE1' | 'NSW_COMPANY_CONSENT'
  version: number
  parentName?: string
  relationship?: string
  parentPhone?: string
  parentEmail?: string
  backupContact?: MinorEmploymentContact
  education?: MinorEmploymentReviewDetail['education']
  employerName?: string
  roleName?: string
  consentText: string[]
  consentScope: string
  snapshot: Record<string, unknown>
  formData?: unknown
  schoolCalendar?: unknown
  otherWork?: unknown
  commute?: unknown
  contacts?: MinorEmploymentContact[]
  expiresAt?: string
  revision: number
}

/** 监护人邮箱验证会话：验证前只返回打码邮箱与验证码发送节奏。 */
export interface GuardianSession {
  maskedGuardianEmail: string
  emailVerified: boolean
  codeSentAt?: string
  codeExpiresAt?: string
  resendAvailableAt?: string
  remainingSends: number
}

export interface ParentSignatureSubmit {
  version: number
  consentScope: string
  revision: number
  parentName: string
  relationship: string
  parentPhone: string
  parentEmail: string
  signature: string
  confirmRelationship: boolean
  confirmBackupContact: boolean
  confirmConsent: boolean
}
