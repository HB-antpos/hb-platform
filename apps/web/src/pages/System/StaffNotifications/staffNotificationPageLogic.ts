import type {
  StaffNotificationBatch,
  StaffNotificationBatchChannel,
  StaffNotificationBatchQuery,
  StaffNotificationBatchResult,
  StaffNotificationTemplate,
  StaffNotificationTemplateSave,
} from '../../../types/staffNotification'

import { formatAud, type TemplateVariable } from './staffNotificationLogic'

/**
 * 「员工通知」页（模板 + 发送记录）的纯逻辑：悉尼时间格式化、记录筛选 → 查询参数、模板草稿。
 * 与 staffNotificationLogic 分开放，避免发送抽屉（用户管理页）把页面专用的东西一起打进它的代码块。
 */

// ===== 悉尼时间 =====

const SYDNEY_TIME_ZONE = 'Australia/Sydney'

const sydneyPartsFormatter = new Intl.DateTimeFormat('en-CA', {
  timeZone: SYDNEY_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
})

const TIMEZONE_SUFFIX = /(Z|[+-]\d{2}:?\d{2})$/i

/** 后端时间字段都是 UTC，但 SQL 读出来可能缺 Z：缺时区后缀的一律按 UTC 解析，不能让浏览器当本地时间。 */
function parseUtcMs(value: string | null | undefined): number | null {
  const text = (value ?? '').trim()
  if (!text) return null
  const ms = Date.parse(TIMEZONE_SUFFIX.test(text) ? text : `${text.replace(' ', 'T')}Z`)
  return Number.isNaN(ms) ? null : ms
}

function getSydneyParts(ms: number): Record<string, string> {
  return sydneyPartsFormatter.formatToParts(new Date(ms)).reduce<Record<string, string>>((result, part) => {
    if (part.type !== 'literal') result[part.type] = part.value
    return result
  }, {})
}

/** UTC → 悉尼「YYYY-MM-DD HH:mm」；空值 / 无法解析返回「—」。时区与夏令时交给 Intl，不手算。 */
export function formatSydneyDateTime(value: string | null | undefined): string {
  const ms = parseUtcMs(value)
  if (ms === null) return '—'
  const parts = getSydneyParts(ms)
  return `${parts.year}-${parts.month}-${parts.day} ${parts.hour}:${parts.minute}`
}

/** UTC → 悉尼「MM-DD」，用在模板卡片的「11-06 更新」。 */
export function formatSydneyMonthDay(value: string | null | undefined): string {
  const ms = parseUtcMs(value)
  if (ms === null) return ''
  const parts = getSydneyParts(ms)
  return `${parts.month}-${parts.day}`
}

/** 悉尼日历日 YYYY-MM-DD 加减若干天（纯日期运算，不涉及时区）。 */
export function shiftIsoDate(isoDate: string, days: number): string {
  const [year, month, day] = isoDate.split('-').map(Number)
  const shifted = new Date(Date.UTC(year, month - 1, day + days))
  return shifted.toISOString().slice(0, 10)
}

/** 发送记录默认筛选「最近 7 天」：以悉尼今天为止，含今天共 7 天（设计图里 10-31 → 11-06）。 */
export function getDefaultLogDateRange(sydneyToday: string): { from: string; to: string } {
  return { from: shiftIsoDate(sydneyToday, -6), to: sydneyToday }
}

// ===== 发送记录：筛选 → 查询参数 =====

export const BATCH_LOG_DEFAULT_PAGE_SIZE = 20

export interface BatchLogFilters {
  from?: string
  to?: string
  templateKey?: string
  channel?: StaffNotificationBatchChannel
  result?: StaffNotificationBatchResult
  keyword?: string
}

/** 筛选条件 → 接口查询参数：空值一律不传，关键字去首尾空白。 */
export function buildBatchQuery(filters: BatchLogFilters, page: number, pageSize: number): StaffNotificationBatchQuery {
  const keyword = filters.keyword?.trim()
  return {
    page,
    pageSize,
    ...(filters.from ? { from: filters.from } : {}),
    ...(filters.to ? { to: filters.to } : {}),
    ...(filters.templateKey ? { templateKey: filters.templateKey } : {}),
    ...(filters.channel ? { channel: filters.channel } : {}),
    ...(filters.result ? { result: filters.result } : {}),
    ...(keyword ? { keyword } : {}),
  }
}

/** 短信费用列：只统计实际发出的条数；一条没发出显示「—」。 */
export function formatBatchSmsCost(batch: Pick<StaffNotificationBatch, 'smsSegments' | 'smsCostAud'>): string | null {
  return batch.smsSegments > 0 ? formatAud(batch.smsCostAud) : null
}

/** 用新详情替换列表里同 id 的批次（重试轮询时刷新那一行），其余行保持引用不变。 */
export function replaceBatchInList(items: readonly StaffNotificationBatch[], updated: StaffNotificationBatch): StaffNotificationBatch[] {
  return items.map((item) => (item.id === updated.id ? { ...item, ...stripRecipients(updated) } : item))
}

/** 详情带 recipients，列表行只要批次本身。 */
function stripRecipients(batch: StaffNotificationBatch): StaffNotificationBatch {
  const { recipients: _recipients, ...rest } = batch as StaffNotificationBatch & { recipients?: unknown }
  return rest
}

// ===== 模板：编辑草稿 =====

/** 新建模板在列表里的占位 key（真实模板 key 是 account-activated 或 GUID，不会撞）。 */
export const NEW_TEMPLATE_KEY = '__new__'

export interface TemplateEditorDraft {
  name: string
  isEnabled: boolean
  emailSubject: string
  emailBody: string
  smsBody: string
}

export function createEmptyTemplateDraft(): TemplateEditorDraft {
  return { name: '', isEnabled: true, emailSubject: '', emailBody: '', smsBody: '' }
}

export function toTemplateDraft(template: StaffNotificationTemplate): TemplateEditorDraft {
  return {
    name: template.name,
    isEnabled: template.isEnabled,
    emailSubject: template.emailSubject,
    emailBody: template.emailBody,
    smsBody: template.smsBody,
  }
}

export function toTemplateSavePayload(draft: TemplateEditorDraft): StaffNotificationTemplateSave {
  return {
    name: draft.name.trim(),
    isEnabled: draft.isEnabled,
    emailSubject: draft.emailSubject,
    emailBody: draft.emailBody,
    smsBody: draft.smsBody,
  }
}

/** 草稿与已保存版本是否有差异（名称只比去空白后的值，避免多敲一个空格就算「未保存」）。 */
export function isTemplateDraftDirty(draft: TemplateEditorDraft, baseline: TemplateEditorDraft): boolean {
  return (
    draft.name.trim() !== baseline.name.trim() ||
    draft.isEnabled !== baseline.isEnabled ||
    draft.emailSubject !== baseline.emailSubject ||
    draft.emailBody !== baseline.emailBody ||
    draft.smsBody !== baseline.smsBody
  )
}

export interface TemplateVariableChip {
  variable: TemplateVariable
  /** 整块内容，只能放进邮件正文。 */
  mailOnly: boolean
}

/**
 * 变量条上显示的变量：发放密码的模板六个全有；自建（不发放密码）模板没有 {{login_box}}，
 * 因为它不发放密码，放了也没有意义。
 */
export function getTemplateVariableChips(issuesPassword: boolean): TemplateVariableChip[] {
  const chips: TemplateVariableChip[] = [
    { variable: 'name', mailOnly: false },
    { variable: 'email', mailOnly: false },
    { variable: 'store', mailOnly: false },
    { variable: 'app_link', mailOnly: false },
  ]
  if (issuesPassword) chips.push({ variable: 'login_box', mailOnly: true })
  chips.push({ variable: 'download_buttons', mailOnly: true })
  return chips
}

export type TemplateEditorField = 'emailSubject' | 'emailBody' | 'smsBody'

/** 整块内容（login_box / download_buttons）只能放进邮件正文；其它变量哪里都能放。 */
export function canInsertVariable(variable: TemplateVariable, field: TemplateEditorField): boolean {
  const mailOnly = variable === 'login_box' || variable === 'download_buttons'
  return !mailOnly || field === 'emailBody'
}

/** 模板卡片的「模板在新建时没有 key」之外的一行小字：发放密码的写「发放随机密码」，其它写「普通通知」，后面接悉尼更新日期。 */
export function getTemplateCardMeta(template: Pick<StaffNotificationTemplate, 'issuesPassword' | 'updatedAtUtc'>): {
  kind: 'issuesPassword' | 'general'
  updatedOn: string
} {
  return {
    kind: template.issuesPassword ? 'issuesPassword' : 'general',
    updatedOn: formatSydneyMonthDay(template.updatedAtUtc),
  }
}
