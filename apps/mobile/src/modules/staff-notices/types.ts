/**
 * 「员工通知」接口类型，字段与后端 BlazorApp.Shared/DTOs/StaffNotificationDtos.cs 一一对应。
 * 模块叫 staff-notices：工作台已有站内通知收件箱 notifications，两者不是一回事。
 */

export type StaffNoticeRecipientStatus = "ready" | "confirm" | "blocked";

export type StaffNoticeRecipientReason =
  | "already_logged_in"
  | "suspicious_email"
  | "no_email"
  | "account_inactive"
  | "login_disabled"
  | "forbidden"
  | "not_found"
  | "self"
  | "no_channel"
  | "in_progress";

export type StaffNoticeSmsSkipReason = "sms_off" | "sms_unavailable" | "no_phone" | "not_mobile";

export interface StaffNoticeTemplate {
  key: string;
  name: string;
  kind: string;
  description: string;
  isEnabled: boolean;
  /** 这个模板必须发邮件（账号已开通要把随机密码发到邮箱），邮件开关锁定为开。 */
  requiresEmail: boolean;
  /** 发送会重置密码并让旧登录失效。 */
  issuesPassword: boolean;
  /** 空字符串表示这个模板没有短信版本。 */
  smsBody: string;
}

export interface StaffNoticePreflightRecipient {
  userGuid: string;
  displayName: string;
  loginName: string;
  storeName: string | null;
  email: string | null;
  phone: string | null;
  isActive: boolean;
  hasLoggedIn: boolean;
  lastLoginAtUtc: string | null;
  status: StaffNoticeRecipientStatus;
  reasons: StaffNoticeRecipientReason[];
  willSendEmail: boolean;
  willSendSms: boolean;
  smsSkipReason: StaffNoticeSmsSkipReason | null;
  smsSegments: number;
}

/** 短信可选发件名（ClickSend 的 Alpha Tag）。默认项排第一，恰有一项 isDefault。 */
export interface StaffNoticeSmsSender {
  id: string;
  isDefault: boolean;
}

export interface StaffNoticePreflight {
  smsAvailable: boolean;
  /** 可选发件名；短信不可用时为空。旧后端没有这个字段，所以按可选处理。 */
  smsSenders?: StaffNoticeSmsSender[];
  smsUsedLast24h: number;
  smsDailyLimit: number;
  smsUnitCostAud: number;
  recipients: StaffNoticePreflightRecipient[];
}

export interface StaffNoticePreflightRequest {
  templateKey: string;
  userGuids: string[];
  sendEmail: boolean;
  sendSms: boolean;
}

export interface StaffNoticePreview {
  emailSubject: string;
  /** 纯文本版（发信时的 text 部分）。App 里没有 WebView，不用 emailHtml。 */
  emailText: string;
  smsText: string;
  smsLength: number;
  smsSegments: number;
  smsValid: boolean;
  smsError: string | null;
}

export interface StaffNoticeSendRequest extends StaffNoticePreflightRequest {
  /** 本次短信用的发件名；不传 = 默认项。服务端会校验必须在白名单里。 */
  smsSenderId?: string;
  confirmReissue: boolean;
}

export interface StaffNoticeSendResult {
  batchId: string;
  status: string;
  totalCount: number;
}

export type StaffNoticeBatchStatus = "queued" | "running" | "completed" | "interrupted";
export type StaffNoticeBatchResult = "running" | "all_sent" | "partial_failed" | "all_failed";
export type StaffNoticeChannelStatus = "none" | "skipped" | "pending" | "sent" | "failed";

export interface StaffNoticeBatch {
  id: string;
  templateKey: string;
  templateName: string;
  createdBy: string;
  createdAtUtc: string;
  finishedAtUtc: string | null;
  status: StaffNoticeBatchStatus;
  result: StaffNoticeBatchResult;
  totalCount: number;
  emailRequested: number;
  emailSent: number;
  emailFailed: number;
  smsRequested: number;
  smsSent: number;
  smsFailed: number;
  /** 这批短信用的发件名；没发短信的批次与改动前的历史批次为空。 */
  smsSenderId?: string | null;
  canRetry: boolean;
}

export interface StaffNoticeRecipientResult {
  id: string;
  userGuid: string;
  displayName: string;
  emailStatus: StaffNoticeChannelStatus;
  smsStatus: StaffNoticeChannelStatus;
  /** 失败 / 跳过的原因，服务端写好的文案（中文）；成功为空串。 */
  note: string;
}

export interface StaffNoticeBatchDetail extends StaffNoticeBatch {
  recipients: StaffNoticeRecipientResult[];
}

export interface StaffNoticeBatchPage {
  items: StaffNoticeBatch[];
  total: number;
  page: number;
  pageSize: number;
}
