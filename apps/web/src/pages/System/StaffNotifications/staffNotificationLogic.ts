import type {
  StaffNotificationBatch,
  StaffNotificationBatchDetail,
  StaffNotificationBatchResult,
  StaffNotificationBatchStatus,
  StaffNotificationChannelStatus,
  StaffNotificationPreflightRecipient,
  StaffNotificationRecipientReason,
  StaffNotificationRecipientStatus,
  StaffNotificationSmsSender,
  StaffNotificationSmsSkipReason,
} from '../../../types/staffNotification'

/**
 * 员工通知的纯逻辑：收件人默认勾选与汇总、短信字符 / 条数估算、模板实时校验、批次结果文案、批次轮询。
 * 不依赖 React 与 i18n（文案由组件按这里返回的 key 翻译），方便单测。
 */

// ===== 收件人：默认勾选、筛选、汇总 =====

export type RecipientFilter = 'all' | 'ready' | 'confirm' | 'blocked'

/** 默认勾选：只有 ready；confirm（已登录过、邮箱存疑）默认不勾但可手动勾，blocked 不可勾。 */
export function getDefaultSelectedGuids(recipients: readonly StaffNotificationPreflightRecipient[]): string[] {
  return recipients.filter((recipient) => recipient.status === 'ready').map((recipient) => recipient.userGuid)
}

/** blocked 的人不能被勾选。 */
export function isRecipientSelectable(recipient: Pick<StaffNotificationPreflightRecipient, 'status'>): boolean {
  return recipient.status !== 'blocked'
}

/**
 * 重新预检（切换渠道开关、换模板）后的勾选：
 * - 上一次预检里已有的人沿用用户的勾选，不因为预检刷新被重置；
 * - 新出现的人按默认规则（只勾 ready）；
 * - blocked 的人一律取消勾选。
 */
export function reconcileSelection(
  previousSelected: ReadonlySet<string>,
  previousRecipients: readonly StaffNotificationPreflightRecipient[] | null,
  nextRecipients: readonly StaffNotificationPreflightRecipient[],
): string[] {
  const known = new Set((previousRecipients ?? []).map((recipient) => recipient.userGuid))
  return nextRecipients
    .filter((recipient) => {
      if (!isRecipientSelectable(recipient)) return false
      return known.has(recipient.userGuid) ? previousSelected.has(recipient.userGuid) : recipient.status === 'ready'
    })
    .map((recipient) => recipient.userGuid)
}

export function filterRecipients(
  recipients: readonly StaffNotificationPreflightRecipient[],
  filter: RecipientFilter,
): StaffNotificationPreflightRecipient[] {
  return filter === 'all' ? [...recipients] : recipients.filter((recipient) => recipient.status === filter)
}

export function countRecipientsByStatus(
  recipients: readonly StaffNotificationPreflightRecipient[],
): Record<RecipientFilter, number> {
  const counts: Record<RecipientFilter, number> = { all: recipients.length, ready: 0, confirm: 0, blocked: 0 }
  for (const recipient of recipients) {
    if (recipient.status in counts) counts[recipient.status] += 1
  }
  return counts
}

export interface SendSummary {
  /** 勾选且可发送的人数：「将发送给 N 人」。 */
  selectedCount: number
  /** 邮件封数。 */
  emailCount: number
  /** 会收到短信的人数。 */
  smsRecipientCount: number
  /** 短信计费条数合计（一人长短信可能占多条）。 */
  smsSegments: number
  /** 短信估价（澳元）。 */
  smsCostAud: number
  /** 跳过的人数 = 预检名单里没发的人（没勾选、blocked）。 */
  skippedCount: number
  /** 勾选里已登录过的人：发送会让他们的旧密码失效，要求二次确认。 */
  reissueRecipients: StaffNotificationPreflightRecipient[]
}

/** 用预检结果现算底部汇总；只统计当前勾选里的人，blocked 即使被塞进勾选集合也不算。 */
export function summarizeSelection(
  recipients: readonly StaffNotificationPreflightRecipient[],
  selectedGuids: ReadonlySet<string>,
  smsUnitCostAud: number,
): SendSummary {
  const chosen = recipients.filter((recipient) => selectedGuids.has(recipient.userGuid) && isRecipientSelectable(recipient))
  const smsChosen = chosen.filter((recipient) => recipient.willSendSms)
  const smsSegments = smsChosen.reduce((sum, recipient) => sum + Math.max(0, recipient.smsSegments), 0)
  return {
    selectedCount: chosen.length,
    emailCount: chosen.filter((recipient) => recipient.willSendEmail).length,
    smsRecipientCount: smsChosen.length,
    smsSegments,
    smsCostAud: smsSegments * smsUnitCostAud,
    skippedCount: recipients.length - chosen.length,
    reissueRecipients: chosen.filter((recipient) => recipient.reasons.includes('already_logged_in')),
  }
}

/** 澳元金额：A$0.24。 */
export function formatAud(value: number): string {
  const safe = Number.isFinite(value) ? value : 0
  return `A$${safe.toFixed(2)}`
}

/** 今日 / 24 小时短信额度：本次发送后是否会超过全站上限（只做提示，是否拒绝以服务端为准）。 */
export function isSmsQuotaExceeded(used: number, limit: number, plannedSegments: number): boolean {
  return limit > 0 && plannedSegments > 0 && used + plannedSegments > limit
}

// ===== 短信发件名 =====

/**
 * 本次短信实际使用的发件名：
 * - 用户选的那项仍在预检返回的可选列表里就用它；
 * - 已不在列表里（重新预检后白名单变了）回落到默认项，没有标默认项就取第一项，绝不把失效值传给后端；
 * - 列表为空（短信不可用）返回 undefined，调用方据此不传 smsSenderId。
 */
export function resolveSmsSenderId(
  senders: readonly StaffNotificationSmsSender[],
  chosenId: string | undefined,
): string | undefined {
  if (senders.length === 0) return undefined
  if (chosenId !== undefined && senders.some((sender) => sender.id === chosenId)) return chosenId
  return (senders.find((sender) => sender.isDefault) ?? senders[0]).id
}

// ===== 收件人原因：文案 key 与兜底 =====

export type RecipientReasonTone = 'warning' | 'error' | 'default'

export interface RecipientReasonDescription {
  /** 文案 key 后缀：system.staffNotifications.send.reason.<key>（标签）与 <key>Hint（说明）。 */
  key: string
  tone: RecipientReasonTone
  /** 不认识的原因原文，兜底文案里显示。 */
  raw?: string
}

const KNOWN_REASON_TONES: Record<StaffNotificationRecipientReason, RecipientReasonTone> = {
  already_logged_in: 'warning',
  suspicious_email: 'warning',
  no_email: 'error',
  account_inactive: 'default',
  login_disabled: 'default',
  forbidden: 'default',
  not_found: 'default',
  self: 'default',
  no_channel: 'default',
  in_progress: 'default',
}

/** 后端以后新增的原因不能让界面白屏：未知原因按收件人状态给通用文案（confirm 提示确认，blocked 不可发送）。 */
export function describeRecipientReason(
  reason: string,
  status: StaffNotificationRecipientStatus,
): RecipientReasonDescription {
  if (Object.prototype.hasOwnProperty.call(KNOWN_REASON_TONES, reason)) {
    const known = reason as StaffNotificationRecipientReason
    return { key: reasonKeyOf(known), tone: KNOWN_REASON_TONES[known] }
  }
  return status === 'blocked'
    ? { key: 'unknownBlocked', tone: 'default', raw: reason }
    : { key: 'unknownConfirm', tone: 'warning', raw: reason }
}

/** snake_case 原因码 → camelCase 文案 key。 */
function reasonKeyOf(reason: StaffNotificationRecipientReason): string {
  return reason.replace(/_([a-z])/g, (_, letter: string) => letter.toUpperCase())
}

export type SmsSkipKey = 'smsOff' | 'smsUnavailable' | 'noPhone' | 'notMobile'

/** 短信跳过原因 → 文案 key 后缀（system.staffNotifications.send.smsSkip.<key>）；sms_off 是用户自己关的，不算「跳过」。 */
export function getSmsSkipKey(reason: StaffNotificationSmsSkipReason | undefined | null): SmsSkipKey | null {
  switch (reason) {
    case 'sms_unavailable':
      return 'smsUnavailable'
    case 'no_phone':
      return 'noPhone'
    case 'not_mobile':
      return 'notMobile'
    case 'sms_off':
      return 'smsOff'
    default:
      return null
  }
}

export type RecipientChannelPlan = 'both' | 'emailOnly' | 'emailOnlySmsSkipped' | 'smsOnly' | 'none'

/** 可发送的人走哪些渠道：用来写「邮件 + 短信 / 只发邮件（没有手机号）」。 */
export function getRecipientChannelPlan(recipient: StaffNotificationPreflightRecipient): RecipientChannelPlan {
  if (recipient.willSendEmail && recipient.willSendSms) return 'both'
  if (recipient.willSendEmail) {
    const skip = getSmsSkipKey(recipient.smsSkipReason)
    return skip && skip !== 'smsOff' ? 'emailOnlySmsSkipped' : 'emailOnly'
  }
  if (recipient.willSendSms) return 'smsOnly'
  return 'none'
}

/** 邮箱域名（小写）；用于「123.com 不像真实邮箱」的提示，取不到返回空串。 */
export function getEmailDomain(email: string | null | undefined): string {
  const value = (email ?? '').trim()
  const at = value.lastIndexOf('@')
  return at >= 0 ? value.slice(at + 1).toLowerCase() : ''
}

// ===== 短信：字符与条数估算 =====

/** 一条短信最多 160 字符；超过后每条按 153 计（多条拼接要占 7 个字符的头）。 */
export const SMS_SINGLE_LIMIT = 160
export const SMS_MULTI_SEGMENT_LIMIT = 153
/** 最多 3 条（≤ 459 字符），超出服务端拒绝保存。 */
export const SMS_MAX_SEGMENTS = 3
export const SMS_MAX_LENGTH = SMS_MAX_SEGMENTS * SMS_MULTI_SEGMENT_LIMIT

/** 模板变量。邮件可用全部六个；短信只能用前四个。 */
export const TEMPLATE_VARIABLES = ['name', 'email', 'store', 'app_link', 'login_box', 'download_buttons'] as const
export type TemplateVariable = (typeof TEMPLATE_VARIABLES)[number]
export const SMS_ALLOWED_VARIABLES: readonly TemplateVariable[] = ['name', 'email', 'store', 'app_link']
/** 只能放进邮件的整块内容。 */
export const MAIL_ONLY_VARIABLES: readonly TemplateVariable[] = ['login_box', 'download_buttons']

/** 估算短信长度时变量按这些字符数计：下载短链 26（https://hotbargain.vip/app）、姓名 12、邮箱 24、分店 12。 */
export const SMS_VARIABLE_ESTIMATE_LENGTH: Partial<Record<TemplateVariable, number>> = {
  app_link: 26,
  name: 12,
  email: 24,
  store: 12,
}

/** 匹配 {{xxx}}；变量名必须和模板里写的完全一致（含大小写、不带空格），多余空格视为未知变量。 */
const VARIABLE_PATTERN = /\{\{([^{}]*)\}\}/g

/** 文本里出现的所有 {{变量}} 名（按出现顺序，含重复）。 */
export function extractVariableNames(text: string): string[] {
  return Array.from(text.matchAll(VARIABLE_PATTERN), (match) => match[1])
}

/** 不在允许集合里的变量名（去重）。 */
export function findUnknownVariables(text: string, allowed: readonly string[] = TEMPLATE_VARIABLES): string[] {
  return Array.from(new Set(extractVariableNames(text).filter((name) => !allowed.includes(name))))
}

/** 把已知变量换成估算字符数的占位，未知变量原样保留（它们本身会被校验拒绝）。 */
function expandSmsVariables(body: string): string {
  return body.replace(VARIABLE_PATTERN, (whole, name: string) => {
    const estimate = SMS_VARIABLE_ESTIMATE_LENGTH[name as TemplateVariable]
    return estimate === undefined ? whole : 'x'.repeat(estimate)
  })
}

/** 短信估算长度（字符数）。 */
export function estimateSmsLength(body: string): number {
  return Array.from(expandSmsVariables(body.replace(/\r\n/g, '\n'))).length
}

/** 计费条数：空 0；≤ 160 一条；超过按每条 153 向上取整。 */
export function estimateSmsSegments(length: number): number {
  if (length <= 0) return 0
  if (length <= SMS_SINGLE_LIMIT) return 1
  return Math.ceil(length / SMS_MULTI_SEGMENT_LIMIT)
}

/** 当前条数对应的字符上限：1 条 160，多条 条数 × 153。 */
export function getSmsLengthLimit(segments: number): number {
  return segments <= 1 ? SMS_SINGLE_LIMIT : segments * SMS_MULTI_SEGMENT_LIMIT
}

/** 「139 / 160 字符 · 1 条」所需的数字。 */
export function describeSmsLength(length: number, segments: number = estimateSmsSegments(length)) {
  return { length, limit: getSmsLengthLimit(segments), segments }
}

/** 保守 GSM-7 子集：字母数字、空格、! " # % & ' ( ) * + , - . / : ; < = > ? @ _ $ 与换行。 */
const SMS_SAFE_CHAR = /^[A-Za-z0-9 !"#%&'()*+,\-./:;<=>?@_$\n]$/

/** 短信里不在保守 GSM-7 子集的字符（去重，按出现顺序）。 */
export function findInvalidSmsChars(body: string): string[] {
  // 所有 {{...}} 先换成中性占位：未知变量、邮件专用变量各有专门的校验提示，不能再被当成非法字符重复报。
  const expanded = body.replace(/\r\n/g, '\n').replace(VARIABLE_PATTERN, 'x')
  const bad: string[] = []
  for (const character of expanded) {
    if (!SMS_SAFE_CHAR.test(character) && !bad.includes(character)) bad.push(character)
  }
  return bad
}

// ===== 模板实时校验（与服务端规则一致，服务端为准） =====

export const TEMPLATE_NAME_MAX_LENGTH = 100
export const TEMPLATE_EMAIL_SUBJECT_MAX_LENGTH = 200
export const TEMPLATE_EMAIL_BODY_MAX_LENGTH = 4000

/** 中日韩文字、标点、全角字符：邮件只能写英文。 */
const CJK_PATTERN = /[⺀-鿿가-힯豈-﫿︰-﹏＀-￯]|[\u{20000}-\u{2FA1F}]/u

export function containsCjk(text: string): boolean {
  return CJK_PATTERN.test(text)
}

export interface TemplateDraft {
  name: string
  emailSubject: string
  emailBody: string
  smsBody: string
}

export interface TemplateValidationContext {
  /** 发放随机密码的模板：邮件正文必须含 {{login_box}}。 */
  issuesPassword: boolean
  /** 自建模板的名称不能和其他模板重名；系统模板不查重。 */
  checkDuplicateName: boolean
  /** 其他模板的名称（不含当前模板自己）。 */
  otherTemplateNames: readonly string[]
}

export type TemplateCheckStatus = 'pass' | 'fail' | 'warn'

export type TemplateCheckId =
  | 'name.length'
  | 'name.duplicate'
  | 'email.loginBox'
  | 'email.loginBoxNotIssued'
  | 'email.download'
  | 'email.english'
  | 'email.variables'
  | 'email.length'
  | 'sms.length'
  | 'sms.mailOnlyVariables'
  | 'sms.charset'
  | 'sms.variables'

export interface TemplateCheck {
  id: TemplateCheckId
  status: TemplateCheckStatus
  /** 文案插值：长度、条数、出现问题的字符 / 变量等。 */
  params?: Record<string, string | number>
}

export interface TemplateValidation {
  name: TemplateCheck[]
  email: TemplateCheck[]
  sms: TemplateCheck[]
  smsLength: number
  smsSegments: number
  /** 没有任何 fail 才能保存；warn 只是提示。 */
  canSave: boolean
}

export function validateTemplateDraft(draft: TemplateDraft, context: TemplateValidationContext): TemplateValidation {
  const name: TemplateCheck[] = []
  const trimmedName = draft.name.trim()
  const nameLength = Array.from(trimmedName).length
  name.push({
    id: 'name.length',
    status: nameLength >= 1 && nameLength <= TEMPLATE_NAME_MAX_LENGTH ? 'pass' : 'fail',
    params: { length: nameLength, max: TEMPLATE_NAME_MAX_LENGTH },
  })
  if (context.checkDuplicateName && trimmedName) {
    const lowered = trimmedName.toLowerCase()
    if (context.otherTemplateNames.some((other) => other.trim().toLowerCase() === lowered)) {
      name.push({ id: 'name.duplicate', status: 'fail' })
    }
  }

  const email: TemplateCheck[] = []
  if (context.issuesPassword) {
    email.push({
      id: 'email.loginBox',
      status: draft.emailBody.includes('{{login_box}}') ? 'pass' : 'fail',
    })
  } else if (draft.emailBody.includes('{{login_box}}')) {
    // 普通通知模板不发放密码，{{login_box}} 没有意义；是否允许保存以服务端为准，这里只提醒。
    email.push({ id: 'email.loginBoxNotIssued', status: 'warn' })
  }
  email.push({
    id: 'email.download',
    status: draft.emailBody.includes('{{download_buttons}}') || draft.emailBody.includes('{{app_link}}') ? 'pass' : 'warn',
  })
  email.push({
    id: 'email.english',
    status: containsCjk(draft.emailSubject) || containsCjk(draft.emailBody) ? 'fail' : 'pass',
  })
  const emailUnknown = findUnknownVariables(`${draft.emailSubject}\n${draft.emailBody}`)
  if (emailUnknown.length) {
    email.push({ id: 'email.variables', status: 'fail', params: { variables: emailUnknown.map((item) => `{{${item}}}`).join('、') } })
  }
  const subjectLength = Array.from(draft.emailSubject).length
  const bodyLength = Array.from(draft.emailBody).length
  if (subjectLength > TEMPLATE_EMAIL_SUBJECT_MAX_LENGTH || bodyLength > TEMPLATE_EMAIL_BODY_MAX_LENGTH) {
    email.push({
      id: 'email.length',
      status: 'fail',
      params: {
        subject: subjectLength,
        subjectMax: TEMPLATE_EMAIL_SUBJECT_MAX_LENGTH,
        body: bodyLength,
        bodyMax: TEMPLATE_EMAIL_BODY_MAX_LENGTH,
      },
    })
  }

  const sms: TemplateCheck[] = []
  const smsLength = estimateSmsLength(draft.smsBody)
  const smsSegments = estimateSmsSegments(smsLength)
  sms.push({
    id: 'sms.length',
    status: smsSegments <= SMS_MAX_SEGMENTS ? 'pass' : 'fail',
    params: { length: smsLength, limit: getSmsLengthLimit(smsSegments), segments: smsSegments, maxLength: SMS_MAX_LENGTH },
  })
  const mailOnlyUsed = MAIL_ONLY_VARIABLES.filter((variable) => draft.smsBody.includes(`{{${variable}}}`))
  sms.push({
    id: 'sms.mailOnlyVariables',
    status: mailOnlyUsed.length ? 'fail' : 'pass',
    params: { variables: mailOnlyUsed.map((item) => `{{${item}}}`).join('、') },
  })
  const invalidChars = findInvalidSmsChars(draft.smsBody)
  sms.push({
    id: 'sms.charset',
    status: invalidChars.length ? 'fail' : 'pass',
    params: { chars: invalidChars.map(describeInvalidChar).join(' ') },
  })
  // 邮件专用变量已有专门的一条提示，这里只报真正不认识的变量。
  const smsUnknown = findUnknownVariables(draft.smsBody)
  if (smsUnknown.length) {
    sms.push({ id: 'sms.variables', status: 'fail', params: { variables: smsUnknown.map((item) => `{{${item}}}`).join('、') } })
  }

  const all = [...name, ...email, ...sms]
  return { name, email, sms, smsLength, smsSegments, canSave: all.every((check) => check.status !== 'fail') }
}

/** 把不可见 / 易混的字符显示成可读形式：弯引号、全角空格等。 */
function describeInvalidChar(character: string): string {
  if (character === '\t') return '[Tab]'
  if (character === ' ' || character === '　') return '[空格]'
  return character
}

/** 在光标处插入文本：返回新文本和插入后光标位置。选区非空时替换选区。 */
export function insertTextAtSelection(
  value: string,
  selectionStart: number | null | undefined,
  selectionEnd: number | null | undefined,
  insertion: string,
): { value: string; caret: number } {
  const start = clampIndex(selectionStart ?? value.length, value.length)
  const end = clampIndex(selectionEnd ?? start, value.length)
  const from = Math.min(start, end)
  const to = Math.max(start, end)
  return { value: value.slice(0, from) + insertion + value.slice(to), caret: from + insertion.length }
}

function clampIndex(index: number, length: number): number {
  return Math.max(0, Math.min(index, length))
}

// ===== 批次：状态、进度、结果文案 =====

export function isBatchFinished(status: StaffNotificationBatchStatus): boolean {
  return status === 'completed' || status === 'interrupted'
}

export interface BatchProgress {
  done: number
  total: number
}

/**
 * 已处理 / 总数：逐人明细里邮件与短信都不再是 pending 才算处理完；
 * 明细还没返回时退回按渠道计数估算，取两个渠道里进度更慢的那个。
 */
export function getBatchProgress(batch: StaffNotificationBatchDetail): BatchProgress {
  const total = Math.max(0, batch.totalCount)
  if (isBatchFinished(batch.status)) return { done: total, total }
  if (batch.recipients.length > 0) {
    const done = batch.recipients.filter((recipient) => recipient.emailStatus !== 'pending' && recipient.smsStatus !== 'pending').length
    return { done: Math.min(done, total), total }
  }
  const ratios: number[] = []
  if (batch.emailRequested > 0) ratios.push((batch.emailSent + batch.emailFailed) / batch.emailRequested)
  if (batch.smsRequested > 0) ratios.push((batch.smsSent + batch.smsFailed) / batch.smsRequested)
  const ratio = ratios.length ? Math.min(...ratios) : 0
  return { done: Math.min(total, Math.floor(ratio * total)), total }
}

export function getBatchProgressPercent(progress: BatchProgress): number {
  return progress.total > 0 ? Math.min(100, Math.round((progress.done / progress.total) * 100)) : 0
}

/** 有失败渠道的人数：「重试失败项（N）」里的 N。 */
export function countRetryRecipients(batch: Pick<StaffNotificationBatchDetail, 'recipients'>): number {
  return batch.recipients.filter((recipient) => recipient.emailStatus === 'failed' || recipient.smsStatus === 'failed').length
}

export interface BatchResultMeta {
  /** 文案 key 后缀：system.staffNotifications.batchResult.<key>。 */
  key: 'running' | 'allSent' | 'partialFailed' | 'allFailed'
  /** antd Tag 颜色。 */
  color: 'processing' | 'success' | 'warning' | 'error'
}

export function getBatchResultMeta(result: StaffNotificationBatchResult | string): BatchResultMeta {
  switch (result) {
    case 'all_sent':
      return { key: 'allSent', color: 'success' }
    case 'partial_failed':
      return { key: 'partialFailed', color: 'warning' }
    case 'all_failed':
      return { key: 'allFailed', color: 'error' }
    default:
      return { key: 'running', color: 'processing' }
  }
}

/** 「邮件 4 / 5」这种计数；这个渠道本批没勾（requested 为 0）返回 null，界面显示「—」。 */
export function formatChannelRatio(sent: number, requested: number): string | null {
  return requested > 0 ? `${sent} / ${requested}` : null
}

export interface ChannelStatusDisplay {
  /** 文案 key 后缀：system.staffNotifications.channelStatus.<key>。 */
  key: 'sent' | 'failed' | 'pending' | 'notSent' | 'unfinished'
  /** antd Tag 颜色；notSent / unfinished 用灰色文字，不用 Tag。 */
  color: 'success' | 'error' | 'processing' | 'default'
}

/**
 * 逐人渠道状态的展示：sent 写「已发出」而不是「已送达」（邮件服务器接收就算已发出，退信目前收不到回执）。
 * none / skipped 都显示「未发送」，原因在说明列；批次已结束但仍是 pending（被中断）显示「未完成」。
 */
export function getChannelStatusDisplay(status: StaffNotificationChannelStatus, batchFinished: boolean): ChannelStatusDisplay {
  switch (status) {
    case 'sent':
      return { key: 'sent', color: 'success' }
    case 'failed':
      return { key: 'failed', color: 'error' }
    case 'pending':
      return batchFinished ? { key: 'unfinished', color: 'default' } : { key: 'pending', color: 'processing' }
    default:
      return { key: 'notSent', color: 'default' }
  }
}

/** 发送结果弹窗标题的计数：邮件 4 / 5，短信 1 / 3（没勾的渠道不出现）。 */
export function getResultTitleCounts(batch: Pick<StaffNotificationBatch, 'emailRequested' | 'emailSent' | 'smsRequested' | 'smsSent'>) {
  return {
    email: formatChannelRatio(batch.emailSent, batch.emailRequested),
    sms: formatChannelRatio(batch.smsSent, batch.smsRequested),
  }
}

/** 服务端拒绝的人：reason 可能是预检原因码，也可能是现成的中文说明；认得的走翻译，不认得的原样显示。 */
export function describeRejectedReason(reason: string): { known: boolean; key: string; raw: string } {
  const trimmed = (reason ?? '').trim()
  if (Object.prototype.hasOwnProperty.call(KNOWN_REASON_TONES, trimmed)) {
    return { known: true, key: reasonKeyOf(trimmed as StaffNotificationRecipientReason), raw: trimmed }
  }
  return { known: false, key: 'unknownBlocked', raw: trimmed }
}

// ===== 批次轮询 =====

/** 发送后每 1.5 秒查一次批次详情，直到 completed / interrupted。 */
export const BATCH_POLL_INTERVAL_MS = 1500
/** 连续失败这么多次就不再自动重试，提示用户去发送记录里看（后台仍在发送）。 */
export const BATCH_POLL_MAX_CONSECUTIVE_FAILURES = 5

export interface BatchPollerScheduler {
  setTimeout: (callback: () => void, ms: number) => unknown
  clearTimeout: (handle: unknown) => void
}

export interface BatchPollerOptions {
  batchId: string
  fetchBatch: (batchId: string, signal: AbortSignal) => Promise<StaffNotificationBatchDetail>
  /** 每次拿到最新详情（含最后一次）。 */
  onUpdate: (detail: StaffNotificationBatchDetail) => void
  /** 批次结束（completed / interrupted）。 */
  onFinished: (detail: StaffNotificationBatchDetail) => void
  /** 连续失败达到上限后放弃；单次失败只会静默重试。 */
  onGiveUp?: (error: unknown) => void
  intervalMs?: number
  maxConsecutiveFailures?: number
  scheduler?: BatchPollerScheduler
}

export interface BatchPoller {
  /** 停止轮询：清掉待执行的定时器、取消在途请求，之后不再触发任何回调。 */
  stop: () => void
}

const defaultScheduler: BatchPollerScheduler = {
  setTimeout: (callback, ms) => globalThis.setTimeout(callback, ms),
  clearTimeout: (handle) => globalThis.clearTimeout(handle as ReturnType<typeof setTimeout>),
}

/**
 * 立即查一次，之后每次响应回来再隔 intervalMs 查下一次（不会叠加请求）。
 * 组件卸载时必须调用 stop()，否则会留下悬挂的定时器。
 */
export function startBatchPolling(options: BatchPollerOptions): BatchPoller {
  const scheduler = options.scheduler ?? defaultScheduler
  const interval = options.intervalMs ?? BATCH_POLL_INTERVAL_MS
  const maxFailures = options.maxConsecutiveFailures ?? BATCH_POLL_MAX_CONSECUTIVE_FAILURES
  let stopped = false
  let timer: unknown
  let controller: AbortController | null = null
  let failures = 0

  const schedule = () => {
    timer = scheduler.setTimeout(() => {
      timer = undefined
      void tick()
    }, interval)
  }

  const tick = async () => {
    if (stopped) return
    controller = new AbortController()
    const { signal } = controller
    try {
      const detail = await options.fetchBatch(options.batchId, signal)
      if (stopped) return
      failures = 0
      options.onUpdate(detail)
      if (isBatchFinished(detail.status)) {
        stopped = true
        options.onFinished(detail)
        return
      }
    } catch (error) {
      if (stopped) return
      failures += 1
      if (failures >= maxFailures) {
        stopped = true
        options.onGiveUp?.(error)
        return
      }
    } finally {
      controller = null
    }
    if (!stopped) schedule()
  }

  void tick()

  return {
    stop: () => {
      stopped = true
      if (timer !== undefined) {
        scheduler.clearTimeout(timer)
        timer = undefined
      }
      controller?.abort()
    },
  }
}

// ===== 错误 =====

/** 业务错误码（ApiResponse.errorCode / code），没有返回 undefined。 */
export function getStaffNotificationErrorCode(error: unknown): string | undefined {
  if (error && typeof error === 'object' && 'payload' in error) {
    const payload = (error as { payload?: unknown }).payload
    if (payload && typeof payload === 'object') {
      const record = payload as { errorCode?: unknown; code?: unknown }
      const code = record.errorCode ?? record.code
      if (typeof code === 'string' && code) return code
    }
  }
  return undefined
}

/** 服务端给的中文原因（payload.message）；没有就用兜底文案。 */
export function getStaffNotificationErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object' && 'payload' in error) {
    const payload = (error as { payload?: unknown }).payload
    if (payload && typeof payload === 'object' && 'message' in payload) {
      const text = (payload as { message?: unknown }).message
      if (typeof text === 'string' && text.trim()) return text
    }
  }
  return fallback
}
