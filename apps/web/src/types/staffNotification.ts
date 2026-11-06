/**
 * 员工通知（后台给员工发邮件 / 短信，带模板）的接口类型。
 * 字段、状态、校验规则以后端契约为准；时间字段一律是 UTC ISO 字符串，页面按悉尼时间显示。
 */

/** 系统模板固定的 key：「账号已开通」，会给员工发放系统生成的随机密码。 */
export const STAFF_NOTIFICATION_ACCOUNT_ACTIVATED_KEY = 'account-activated'

/** 一次发送最多的收件人数，与用户管理页的批量勾选上限一致。 */
export const STAFF_NOTIFICATION_MAX_RECIPIENTS = 100

export type StaffNotificationTemplateKind = 'system' | 'general'

export interface StaffNotificationTemplate {
  /** 系统模板固定为 account-activated；自建模板是服务端生成的 GUID 字符串。 */
  key: string
  name: string
  kind: StaffNotificationTemplateKind
  /** 一句话说明，发送抽屉里模板下拉下方显示。 */
  description: string
  isEnabled: boolean
  /** true = 这个模板必须发邮件（账号已开通会发放随机密码）。 */
  requiresEmail: boolean
  /** true = 发送会重置密码并让旧登录失效（账号已开通）。 */
  issuesPassword: boolean
  emailSubject: string
  /** 纯文本，可含 {{name}} {{email}} {{store}} {{app_link}} {{login_box}} {{download_buttons}}。 */
  emailBody: string
  /** 纯文本，只能含 {{name}} {{email}} {{store}} {{app_link}}。 */
  smsBody: string
  /** 系统模板是否被改过（决定「恢复默认文案」是否可点）。 */
  isCustomized: boolean
  updatedAtUtc: string | null
}

export interface StaffNotificationTemplateSave {
  name: string
  isEnabled: boolean
  emailSubject: string
  emailBody: string
  smsBody: string
}

// ===== 预检 =====

export type StaffNotificationRecipientStatus = 'ready' | 'confirm' | 'blocked'

/** 预检给出的原因。后端以后可能新增，界面遇到不认识的原因要有兜底文案（见 staffNotificationLogic）。 */
export type StaffNotificationRecipientReason =
  | 'already_logged_in' // confirm：登录过，发放密码会让旧密码失效、全部设备被踢下线
  | 'suspicious_email' // confirm：邮箱域名像占位（如 123.com）
  | 'no_email' // blocked
  | 'account_inactive' // blocked
  | 'login_disabled' // blocked：账号被限制登录
  | 'forbidden' // blocked：不在当前操作人的管理范围内
  | 'not_found' // blocked
  | 'self' // blocked：不能给自己发（发放密码会把自己踢下线）；仅对 issuesPassword 模板
  | 'no_channel' // blocked：勾选的渠道对这个人都发不出去
  | 'in_progress' // blocked：这个人已经在另一个进行中的批次里

export type StaffNotificationSmsSkipReason = 'sms_off' | 'sms_unavailable' | 'no_phone' | 'not_mobile' | null

export interface StaffNotificationPreflightRequest {
  templateKey: string
  userGuids: string[]
  sendEmail: boolean
  sendSms: boolean
}

export interface StaffNotificationPreflightRecipient {
  userGuid: string
  displayName: string
  loginName: string
  storeName: string | null
  /** 管理员本来就能看到完整邮箱，这里返回完整值。 */
  email: string | null
  /** 员工资料里的手机号原文。 */
  phone: string | null
  isActive: boolean
  hasLoggedIn: boolean
  lastLoginAtUtc: string | null
  /** ready 默认勾选；confirm 默认不勾选但可手动勾选；blocked 不可勾选。 */
  status: StaffNotificationRecipientStatus
  reasons: StaffNotificationRecipientReason[]
  willSendEmail: boolean
  willSendSms: boolean
  smsSkipReason: StaffNotificationSmsSkipReason
  /** willSendSms 时的计费条数，否则 0。 */
  smsSegments: number
}

export interface StaffNotificationPreflight {
  /** 短信总闸 + 配置齐全。 */
  smsAvailable: boolean
  smsUsedLast24h: number
  smsDailyLimit: number
  /** 单条估价（澳元），如 0.0792。 */
  smsUnitCostAud: number
  /** 与请求 userGuids 同序。 */
  recipients: StaffNotificationPreflightRecipient[]
}

// ===== 预览 =====

export interface StaffNotificationPreviewRequest {
  templateKey: string
  userGuid: string
}

export interface StaffNotificationPreview {
  emailSubject: string
  /** 服务端渲染好的完整 HTML 片段（密码位置是 ••••），只能放进 sandbox iframe 的 srcDoc。 */
  emailHtml: string
  smsText: string
  smsLength: number
  smsSegments: number
  smsValid: boolean
  smsError: string | null
}

// ===== 发送、批次 =====

export interface StaffNotificationSendRequest {
  templateKey: string
  userGuids: string[]
  sendEmail: boolean
  sendSms: boolean
  confirmReissue: boolean
}

export type StaffNotificationBatchStatus = 'queued' | 'running' | 'completed' | 'interrupted'

/** none = 本批没勾这个渠道；skipped = 勾了但这个人发不了。 */
export type StaffNotificationChannelStatus = 'none' | 'skipped' | 'pending' | 'sent' | 'failed'

export type StaffNotificationBatchResult = 'running' | 'all_sent' | 'partial_failed' | 'all_failed'

export interface StaffNotificationRejected {
  userGuid: string
  reason: string
}

export interface StaffNotificationSendResult {
  batchId: string
  status: StaffNotificationBatchStatus
  totalCount: number
  /** 服务端重新预检后拒绝的人（blocked）。 */
  rejected: StaffNotificationRejected[]
}

export interface StaffNotificationBatch {
  id: string
  templateKey: string
  templateName: string
  /** 操作人登录名。 */
  createdBy: string
  createdAtUtc: string
  finishedAtUtc: string | null
  status: StaffNotificationBatchStatus
  result: StaffNotificationBatchResult
  totalCount: number
  emailRequested: number
  emailSent: number
  emailFailed: number
  smsRequested: number
  smsSent: number
  smsFailed: number
  /** 实际发出的条数合计。 */
  smsSegments: number
  /** smsSegments * 单价。 */
  smsCostAud: number
  /** 有失败项且批次已结束。 */
  canRetry: boolean
}

export interface StaffNotificationRecipientResult {
  id: string
  userGuid: string
  displayName: string
  storeName: string | null
  /** 如 m***@example.com。 */
  emailMask: string | null
  /** 如 04** *** 156。 */
  phoneMask: string | null
  emailStatus: StaffNotificationChannelStatus
  smsStatus: StaffNotificationChannelStatus
  /** 失败 / 跳过的中文原因，成功为空串。 */
  note: string
}

export interface StaffNotificationBatchDetail extends StaffNotificationBatch {
  recipients: StaffNotificationRecipientResult[]
}

export type StaffNotificationBatchChannel = 'email' | 'sms'

export interface StaffNotificationBatchQuery {
  page?: number
  pageSize?: number
  templateKey?: string
  result?: StaffNotificationBatchResult
  channel?: StaffNotificationBatchChannel
  /** 匹配操作人或员工姓名。 */
  keyword?: string
  /** YYYY-MM-DD 悉尼日期。 */
  from?: string
  to?: string
}

/** 后端业务错误码（ApiResponse.errorCode）。 */
export const STAFF_NOTIFICATION_ERROR_CODES = {
  /** 模板保存校验失败，message 是用「；」拼好的中文原因。 */
  templateInvalid: 'TEMPLATE_INVALID',
  /** 选中的人里有已登录过的，但没带 confirmReissue。 */
  confirmRequired: 'CONFIRM_REQUIRED',
  /** 服务端重新预检后，选中的人全部被拦下（没有可发送的收件人）；不会建批次，details 里是被拒名单。 */
  noRecipients: 'NO_RECIPIENTS',
  /** 重试时所有失败项都不能自动重试（给自己、员工已登录、之后被重新发放过等）；原因已写进每个员工的「说明」，要重新拉取才能看到。 */
  nothingToRetry: 'NOTHING_TO_RETRY',
} as const
