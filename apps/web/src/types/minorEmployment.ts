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
  /** 监护人在签署页现场修改过的分区键（guardianDetails / schoolCalendar / backupContact 等）。 */
  guardianAmendedFields: string[]
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
  /** 验证码发到哪里：email（默认）或 sms；sms 时页面提示手机号而不是邮箱。 */
  codeChannel?: 'email' | 'sms'
  /** 打码后的监护人手机号，只在 codeChannel 为 sms 时有值。 */
  maskedGuardianPhone?: string
  emailVerified: boolean
  codeSentAt?: string
  codeExpiresAt?: string
  resendAvailableAt?: string
  remainingSends: number
}

/** 员工维度的合规阶段（后端 /api/minor-employment/staff 计算），决定列表状态与可用操作。 */
export type MinorStaffStage =
  | 'not_sent'
  | 'awaiting_guardian'
  | 'link_expired'
  | 'pending_review'
  | 'returned'
  | 'approved'

/** 店长 / HR 视角的未满 18 岁员工一行：没有任何合规记录的员工也会列出（stage=not_sent）。 */
export interface MinorStaffComplianceItem {
  userGUID: string
  employeeName: string
  username?: string
  storeCode?: string
  storeName?: string
  stateCode: 'NSW' | 'QLD' | 'UNKNOWN'
  formType: 'QLD_CE1' | 'NSW_COMPANY_CONSENT' | 'UNSUPPORTED_STATE'
  birthday?: string
  age: number
  complianceId?: number
  version?: number
  revision?: number
  complianceStatus?: string
  stage: MinorStaffStage
  needsAction: boolean
  canSend: boolean
  canRevoke: boolean
  guardianName?: string
  guardianRelationship?: string
  guardianEmail?: string
  guardianPhone?: string
  maskedGuardianEmail?: string
  maskedGuardianPhone?: string
  linkExpiresAtUtc?: string
  lastSentAtUtc?: string
  /** null = 上次没选这个渠道；true = 送达；false = 失败。 */
  lastSentEmail?: boolean | null
  lastSentSms?: boolean | null
  guardianSignedAtUtc?: string
  reviewComment?: string
  returnFields: string[]
}

export interface MinorStaffComplianceList {
  items: MinorStaffComplianceItem[]
  /** 按调用者门店范围计算，不受筛选影响，用作页签红点。 */
  needsActionCount: number
  /** 服务器短信总开关是否已开。 */
  smsAvailable: boolean
}

export interface MinorStaffComplianceQuery {
  storeCode?: string
  stateCode?: 'NSW' | 'QLD' | 'UNKNOWN'
  keyword?: string
  needsActionOnly?: boolean
}

export type GuardianLinkExpiryDays = 1 | 3 | 7

export interface GuardianLinkSendPayload {
  guardianName: string
  guardianRelationship?: string
  guardianEmail?: string
  guardianPhone?: string
  sendEmail: boolean
  sendSms: boolean
  expiryDays: GuardianLinkExpiryDays
  expectedVersion: number | null
  expectedRevision: number | null
}

export interface GuardianLinkSendResult {
  item: MinorStaffComplianceItem
  expiresAtUtc: string
  emailRequested: boolean
  emailSent: boolean
  emailError?: string
  smsRequested: boolean
  smsSent: boolean
  smsError?: string
  codeChannel: 'sms' | 'email'
  /** 只在有渠道失败时返回，供 HR 手动转发。 */
  signingUrl?: string
  /** 后端 ApiResponse.message：中文结果摘要。 */
  message?: string
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
