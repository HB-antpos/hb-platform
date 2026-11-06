import assert from 'node:assert/strict'

import { getStaffNotificationErrorCode, getStaffNotificationErrorMessage } from '../pages/System/StaffNotifications/staffNotificationLogic'
import {
  createStaffNotificationTemplate,
  getStaffNotificationBatch,
  getStaffNotificationBatches,
  getStaffNotificationTemplates,
  preflightStaffNotification,
  previewStaffNotification,
  resetStaffNotificationTemplate,
  retryStaffNotificationBatch,
  saveStaffNotificationTemplate,
  sendStaffNotification,
} from './staffNotificationService'
import { getUsers } from './userService'

// 员工通知接口契约：路径、方法、请求体、查询参数、解包与错误码。

const originalFetch = globalThis.fetch
interface Call {
  method: string
  pathname: string
  search: URLSearchParams
  body: Record<string, unknown> | undefined
}
const calls: Call[] = []
let nextStatus = 200
let nextPayload: unknown = { success: true, data: null }

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = new URL(String(input), 'http://localhost')
  calls.push({
    method: init?.method ?? 'GET',
    pathname: url.pathname,
    search: url.searchParams,
    body: typeof init?.body === 'string' ? (JSON.parse(init.body) as Record<string, unknown>) : undefined,
  })
  return new Response(JSON.stringify(nextPayload), { status: nextStatus, headers: { 'Content-Type': 'application/json' } })
}) as typeof fetch

/** 只取指向目标路径的调用：请求层在非 2xx 时可能额外上报日志，不能干扰断言。 */
const callsTo = (pathname: string) => calls.filter((call) => call.pathname === pathname)
const reset = (payload: unknown, status = 200) => {
  calls.length = 0
  nextStatus = status
  nextPayload = payload
}

const templateSave = { name: '账号已开通', isEnabled: true, emailSubject: 'Hi', emailBody: 'Hi {{name}}\n{{login_box}}', smsBody: 'Get the app: {{app_link}}' }

try {
  // ===== 模板 =====
  reset({ success: true, data: [{ key: 'account-activated', name: '账号已开通' }] })
  const templates = await getStaffNotificationTemplates()
  assert.equal(templates.length, 1)
  assert.equal(calls[0]?.method, 'GET')
  assert.equal(calls[0]?.pathname, '/api/staff-notifications/templates')
  reset({ success: true, data: null })
  assert.deepEqual(await getStaffNotificationTemplates(), [], '空数据按空列表处理')

  // 保存：PUT，key 做 URL 编码，body 只有契约里的五个字段（不回传 key / kind / isCustomized 等只读字段）。
  reset({ success: true, data: { key: 'account-activated' } })
  await saveStaffNotificationTemplate('account-activated', { ...templateSave, key: 'x', kind: 'system', isCustomized: true } as typeof templateSave)
  assert.equal(calls[0]?.method, 'PUT')
  assert.equal(calls[0]?.pathname, '/api/staff-notifications/templates/account-activated')
  assert.deepEqual(calls[0]?.body, templateSave)
  reset({ success: true, data: {} })
  await saveStaffNotificationTemplate('a/b c', templateSave)
  assert.equal(calls[0]?.pathname, '/api/staff-notifications/templates/a%2Fb%20c')

  // 新建：POST /templates；恢复默认：POST /templates/{key}/reset 无 body。
  reset({ success: true, data: {} })
  await createStaffNotificationTemplate(templateSave)
  assert.equal(calls[0]?.method, 'POST')
  assert.equal(calls[0]?.pathname, '/api/staff-notifications/templates')
  assert.deepEqual(calls[0]?.body, templateSave)
  reset({ success: true, data: {} })
  await resetStaffNotificationTemplate('account-activated')
  assert.equal(calls[0]?.method, 'POST')
  assert.equal(calls[0]?.pathname, '/api/staff-notifications/templates/account-activated/reset')

  // 模板校验失败：success=false + TEMPLATE_INVALID，服务端 message 能原样取出给 message.error。
  reset({ success: false, errorCode: 'TEMPLATE_INVALID', message: '邮件正文必须包含 {{login_box}}；短信超长' })
  const invalid = await saveStaffNotificationTemplate('account-activated', templateSave).then(() => undefined, (error: unknown) => error)
  assert.ok(invalid, '业务失败必须抛错，不能让页面误报保存成功')
  assert.equal(getStaffNotificationErrorCode(invalid), 'TEMPLATE_INVALID')
  assert.equal(getStaffNotificationErrorMessage(invalid, '保存失败'), '邮件正文必须包含 {{login_box}}；短信超长')

  // ===== 预检 =====
  reset({
    success: true,
    data: {
      smsAvailable: true, smsUsedLast24h: 12, smsDailyLimit: 300, smsUnitCostAud: 0.0792,
      recipients: [{ userGuid: 'u1', status: 'ready', reasons: [], willSendEmail: true, willSendSms: true, smsSkipReason: null, smsSegments: 1 }],
    },
  })
  const preflight = await preflightStaffNotification({ templateKey: 'account-activated', userGuids: ['u1', 'u2'], sendEmail: true, sendSms: false })
  assert.equal(calls[0]?.method, 'POST')
  assert.equal(calls[0]?.pathname, '/api/staff-notifications/preflight')
  assert.deepEqual(calls[0]?.body, { templateKey: 'account-activated', userGuids: ['u1', 'u2'], sendEmail: true, sendSms: false })
  assert.equal(preflight.smsAvailable, true)
  assert.equal(preflight.smsUsedLast24h, 12)
  assert.equal(preflight.smsUnitCostAud, 0.0792)
  assert.equal(preflight.recipients.length, 1)
  // 缺字段时短信按不可用处理，名单为空数组，不会白屏。
  reset({ success: true, data: {} })
  const emptyPreflight = await preflightStaffNotification({ templateKey: 'k', userGuids: [], sendEmail: true, sendSms: true })
  assert.deepEqual(emptyPreflight, { smsAvailable: false, smsUsedLast24h: 0, smsDailyLimit: 0, smsUnitCostAud: 0, recipients: [] })

  // ===== 预览 =====
  reset({ success: true, data: { emailSubject: 'S', emailHtml: '<p>x</p>', smsText: 'T', smsLength: 1, smsSegments: 1, smsValid: true, smsError: null } })
  const preview = await previewStaffNotification({ templateKey: 'account-activated', userGuid: 'u1' })
  assert.equal(calls[0]?.pathname, '/api/staff-notifications/preview')
  assert.deepEqual(calls[0]?.body, { templateKey: 'account-activated', userGuid: 'u1' })
  assert.equal(preview.emailHtml, '<p>x</p>')

  // ===== 发送 =====
  reset({ success: true, data: { batchId: 'b1', status: 'queued', totalCount: 3 } })
  const sent = await sendStaffNotification({ templateKey: 'account-activated', userGuids: ['u1'], sendEmail: true, sendSms: true, confirmReissue: true })
  assert.equal(calls[0]?.pathname, '/api/staff-notifications/send')
  assert.deepEqual(calls[0]?.body, { templateKey: 'account-activated', userGuids: ['u1'], sendEmail: true, sendSms: true, confirmReissue: true })
  assert.equal(sent.batchId, 'b1')
  assert.deepEqual(sent.rejected, [], 'rejected 缺省按空数组')
  reset({ success: true, data: { batchId: 'b2', status: 'queued', totalCount: 1, rejected: [{ userGuid: 'u2', reason: 'self' }] } })
  assert.deepEqual((await sendStaffNotification({ templateKey: 'k', userGuids: ['u1', 'u2'], sendEmail: true, sendSms: false, confirmReissue: false })).rejected, [{ userGuid: 'u2', reason: 'self' }])
  // 选中里有已登录过的人却没确认：整个请求失败，CONFIRM_REQUIRED 能被识别。
  reset({ success: false, errorCode: 'CONFIRM_REQUIRED', message: '需要确认重新发放密码' })
  const confirmRequired = await sendStaffNotification({ templateKey: 'k', userGuids: ['u1'], sendEmail: true, sendSms: false, confirmReissue: false }).then(() => undefined, (error: unknown) => error)
  assert.equal(getStaffNotificationErrorCode(confirmRequired), 'CONFIRM_REQUIRED')

  // ===== 批次记录 =====
  reset({ success: true, data: { items: [{ id: 'b1' }], total: 1, page: 1, pageSize: 20 } })
  const page = await getStaffNotificationBatches({ page: 1, pageSize: 20, templateKey: '', result: 'partial_failed', channel: 'sms', keyword: 'emma', from: '2026-10-31', to: '2026-11-06' })
  assert.equal(calls[0]?.method, 'GET')
  assert.equal(calls[0]?.pathname, '/api/staff-notifications/batches')
  assert.equal(calls[0]?.search.get('page'), '1')
  assert.equal(calls[0]?.search.get('pageSize'), '20')
  assert.equal(calls[0]?.search.get('result'), 'partial_failed')
  assert.equal(calls[0]?.search.get('channel'), 'sms')
  assert.equal(calls[0]?.search.get('keyword'), 'emma')
  assert.equal(calls[0]?.search.get('from'), '2026-10-31')
  assert.equal(calls[0]?.search.get('to'), '2026-11-06')
  assert.equal(calls[0]?.search.has('templateKey'), false, '空筛选不拼进查询串')
  assert.equal(page.total, 1)
  assert.equal(page.items.length, 1)

  reset({ success: true, data: { id: 'b 1', totalCount: 1 } })
  const detail = await getStaffNotificationBatch('b 1')
  assert.equal(calls[0]?.pathname, '/api/staff-notifications/batches/b%201')
  assert.deepEqual(detail.recipients, [], '明细缺省按空数组')

  reset({ success: true, data: { id: 'b1', status: 'queued', recipients: [{ id: 'r1' }] } })
  const retried = await retryStaffNotificationBatch('b1')
  assert.equal(calls[0]?.method, 'POST')
  assert.equal(calls[0]?.pathname, '/api/staff-notifications/batches/b1/retry')
  assert.equal(retried.status, 'queued')
  assert.equal(retried.recipients.length, 1)

  // ===== 用户列表新增筛选 loginState：随其它筛选一起提交，不传 = 全部 =====
  reset({ success: true, data: { items: [], total: 0, page: 1, pageSize: 10 } })
  await getUsers({ page: 2, pageSize: 10, isActive: true, loginState: 'never', sortBy: 'username', sortDirection: 'asc' })
  assert.equal(calls[0]?.pathname, '/api/Users/optimized')
  assert.equal(calls[0]?.search.get('loginState'), 'never')
  assert.equal(calls[0]?.search.get('page'), '2')
  assert.equal(calls[0]?.search.get('isActive'), 'true')
  assert.equal(calls[0]?.search.get('sortBy'), 'username')
  reset({ success: true, data: { items: [], total: 0, page: 1, pageSize: 10 } })
  await getUsers({ page: 1, pageSize: 10, loginState: undefined })
  assert.equal(calls[0]?.search.has('loginState'), false, '「全部」不传 loginState')

  // 其他调用不应落到别的路径上。
  assert.equal(callsTo('/api/staff-notifications/templates').length, 0)
} finally {
  globalThis.fetch = originalFetch
}

console.log('staffNotificationService.contract tests passed')
