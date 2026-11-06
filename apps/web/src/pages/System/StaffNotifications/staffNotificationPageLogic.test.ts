import assert from 'node:assert/strict'

import type { StaffNotificationBatch, StaffNotificationBatchDetail, StaffNotificationTemplate } from '../../../types/staffNotification'
import {
  BATCH_LOG_DEFAULT_PAGE_SIZE,
  buildBatchQuery,
  canInsertVariable,
  createEmptyTemplateDraft,
  formatBatchSmsCost,
  formatSydneyDateTime,
  formatSydneyMonthDay,
  getDefaultLogDateRange,
  getTemplateCardMeta,
  getTemplateVariableChips,
  isTemplateDraftDirty,
  replaceBatchInList,
  shiftIsoDate,
  toTemplateDraft,
  toTemplateSavePayload,
} from './staffNotificationPageLogic'

// ===== 悉尼时间：UTC → 悉尼，夏令时由 Intl 处理 =====
// 2026-11-06 05:42 UTC：悉尼已进夏令时（UTC+11），当地 16:42，与设计图第一行一致。
assert.equal(formatSydneyDateTime('2026-11-06T05:42:00Z'), '2026-11-06 16:42')
// 冬令时（UTC+10）：2026-07-01 00:30 UTC → 悉尼 10:30。
assert.equal(formatSydneyDateTime('2026-07-01T00:30:00Z'), '2026-07-01 10:30')
// 跨日：UTC 15:30 在悉尼已是第二天。
assert.equal(formatSydneyDateTime('2026-11-05T15:30:00Z'), '2026-11-06 02:30')
// 缺 Z 的 UTC 时间戳（SQL 读出）仍按 UTC 解析；带偏移的按偏移解析。
assert.equal(formatSydneyDateTime('2026-11-06T05:42:00'), '2026-11-06 16:42')
assert.equal(formatSydneyDateTime('2026-11-06 05:42:00'), '2026-11-06 16:42')
assert.equal(formatSydneyDateTime('2026-11-06T16:42:00+11:00'), '2026-11-06 16:42')
assert.equal(formatSydneyDateTime(null), '—')
assert.equal(formatSydneyDateTime(''), '—')
assert.equal(formatSydneyDateTime('not-a-date'), '—')
assert.equal(formatSydneyMonthDay('2026-11-05T15:30:00Z'), '11-06')
assert.equal(formatSydneyMonthDay(null), '')

// ===== 默认日期范围：最近 7 天（含今天） =====
assert.equal(shiftIsoDate('2026-11-06', -6), '2026-10-31')
assert.equal(shiftIsoDate('2026-03-02', -6), '2026-02-24')
assert.equal(shiftIsoDate('2026-01-03', -6), '2025-12-28')
assert.equal(shiftIsoDate('2026-02-27', 3), '2026-03-02')
assert.deepEqual(getDefaultLogDateRange('2026-11-06'), { from: '2026-10-31', to: '2026-11-06' })

// ===== 记录筛选 → 查询参数：空值不传，关键字去空白 =====
assert.deepEqual(buildBatchQuery({}, 1, BATCH_LOG_DEFAULT_PAGE_SIZE), { page: 1, pageSize: 20 })
assert.deepEqual(
  buildBatchQuery(
    { from: '2026-10-31', to: '2026-11-06', templateKey: 'account-activated', channel: 'sms', result: 'partial_failed', keyword: '  emma ' },
    3,
    50,
  ),
  { page: 3, pageSize: 50, from: '2026-10-31', to: '2026-11-06', templateKey: 'account-activated', channel: 'sms', result: 'partial_failed', keyword: 'emma' },
)
assert.deepEqual(buildBatchQuery({ keyword: '   ', templateKey: '' }, 1, 20), { page: 1, pageSize: 20 })

// ===== 批次行：短信费用、替换 =====
function batch(overrides: Partial<StaffNotificationBatch>): StaffNotificationBatch {
  return {
    id: 'b1', templateKey: 'account-activated', templateName: '账号已开通', createdBy: 'admin', createdAtUtc: '2026-11-06T05:42:00Z', finishedAtUtc: null,
    status: 'completed', result: 'partial_failed', totalCount: 5, emailRequested: 5, emailSent: 4, emailFailed: 1, smsRequested: 3, smsSent: 1, smsFailed: 1,
    smsSegments: 1, smsCostAud: 0.0792, canRetry: true, ...overrides,
  }
}
assert.equal(formatBatchSmsCost(batch({})), 'A$0.08')
assert.equal(formatBatchSmsCost(batch({ smsSegments: 0, smsCostAud: 0 })), null, '一条短信也没发出显示「—」')
assert.equal(formatBatchSmsCost(batch({ smsSegments: 10, smsCostAud: 0.792 })), 'A$0.79')

{
  const list = [batch({ id: 'b1' }), batch({ id: 'b2', createdBy: 'emma.t' })]
  const updatedDetail: StaffNotificationBatchDetail = { ...batch({ id: 'b1', status: 'running', result: 'running', canRetry: false }), recipients: [] }
  const next = replaceBatchInList(list, updatedDetail)
  assert.equal(next[0].status, 'running')
  assert.equal(next[0].canRetry, false)
  assert.equal('recipients' in next[0], false, '列表行不保留逐人明细')
  assert.equal(next[1], list[1], '其它行保持原引用')
}

// ===== 模板草稿 =====
const systemTemplate: StaffNotificationTemplate = {
  key: 'account-activated', name: '账号已开通', kind: 'system', description: '', isEnabled: true, requiresEmail: true, issuesPassword: true,
  emailSubject: 'Your Hot Bargain staff app account is ready', emailBody: 'Hi {{name}},\n{{login_box}}', smsBody: 'Get the app: {{app_link}}', isCustomized: false,
  updatedAtUtc: '2026-11-05T15:30:00Z',
}
const baseline = toTemplateDraft(systemTemplate)
assert.deepEqual(baseline, { name: '账号已开通', isEnabled: true, emailSubject: systemTemplate.emailSubject, emailBody: systemTemplate.emailBody, smsBody: systemTemplate.smsBody })
assert.equal(isTemplateDraftDirty(baseline, baseline), false)
assert.equal(isTemplateDraftDirty({ ...baseline, name: '账号已开通 ' }, baseline), false, '名称尾部空白不算修改')
assert.equal(isTemplateDraftDirty({ ...baseline, smsBody: 'x' }, baseline), true)
assert.equal(isTemplateDraftDirty({ ...baseline, isEnabled: false }, baseline), true)
assert.equal(isTemplateDraftDirty(createEmptyTemplateDraft(), createEmptyTemplateDraft()), false)
assert.deepEqual(toTemplateSavePayload({ ...baseline, name: ' 新名称 ' }), { name: '新名称', isEnabled: true, emailSubject: baseline.emailSubject, emailBody: baseline.emailBody, smsBody: baseline.smsBody })
// 保存体只含契约里的五个字段。
assert.deepEqual(Object.keys(toTemplateSavePayload(baseline)).sort(), ['emailBody', 'emailSubject', 'isEnabled', 'name', 'smsBody'])

// 变量条：发放密码的模板六个；自建模板没有 {{login_box}}。
assert.deepEqual(getTemplateVariableChips(true).map((chip) => chip.variable), ['name', 'email', 'store', 'app_link', 'login_box', 'download_buttons'])
assert.deepEqual(getTemplateVariableChips(false).map((chip) => chip.variable), ['name', 'email', 'store', 'app_link', 'download_buttons'])
assert.deepEqual(getTemplateVariableChips(true).filter((chip) => chip.mailOnly).map((chip) => chip.variable), ['login_box', 'download_buttons'])
// 整块内容只能放进邮件正文。
assert.equal(canInsertVariable('login_box', 'emailBody'), true)
assert.equal(canInsertVariable('login_box', 'smsBody'), false)
assert.equal(canInsertVariable('download_buttons', 'emailSubject'), false)
assert.equal(canInsertVariable('name', 'smsBody'), true)
assert.equal(canInsertVariable('app_link', 'emailSubject'), true)

// 模板卡片小字：发放密码 / 普通通知 + 悉尼更新日期（2026-11-05 15:30 UTC = 悉尼 11-06 02:30）。
assert.deepEqual(getTemplateCardMeta(systemTemplate), { kind: 'issuesPassword', updatedOn: '11-06' })
assert.deepEqual(getTemplateCardMeta({ issuesPassword: false, updatedAtUtc: null }), { kind: 'general', updatedOn: '' })

console.log('staffNotificationPageLogic tests passed')
