import assert from 'node:assert/strict'

import type {
  StaffNotificationBatchDetail,
  StaffNotificationPreflightRecipient,
  StaffNotificationRecipientResult,
} from '../../../types/staffNotification'
import {
  BATCH_POLL_INTERVAL_MS,
  countRecipientsByStatus,
  countRetryRecipients,
  describeRecipientReason,
  describeRejectedReason,
  estimateSmsLength,
  estimateSmsSegments,
  filterRecipients,
  findInvalidSmsChars,
  findUnknownVariables,
  formatAud,
  getBatchProgress,
  getBatchResultMeta,
  getChannelStatusDisplay,
  getDefaultSelectedGuids,
  getRecipientChannelPlan,
  getResultTitleCounts,
  getSmsLengthLimit,
  getStaffNotificationErrorCode,
  getStaffNotificationErrorMessage,
  insertTextAtSelection,
  isSmsQuotaExceeded,
  reconcileSelection,
  resolveSmsSenderId,
  startBatchPolling,
  summarizeSelection,
  validateTemplateDraft,
  type BatchPollerScheduler,
} from './staffNotificationLogic'

function recipient(overrides: Partial<StaffNotificationPreflightRecipient> & { userGuid: string }): StaffNotificationPreflightRecipient {
  return {
    displayName: overrides.userGuid,
    loginName: overrides.userGuid,
    storeName: 'Sunnybank',
    email: `${overrides.userGuid}@example.com`,
    phone: '0491 570 156',
    isActive: true,
    hasLoggedIn: false,
    lastLoginAtUtc: null,
    status: 'ready',
    reasons: [],
    willSendEmail: true,
    willSendSms: true,
    smsSkipReason: null,
    smsSegments: 1,
    ...overrides,
  }
}

// 设计图 2 的 9 个人：4 个可发送、3 个需确认、2 个不可发送。
const people: StaffNotificationPreflightRecipient[] = [
  recipient({ userGuid: 'mia' }),
  recipient({ userGuid: 'jack' }),
  recipient({ userGuid: 'sophie', willSendSms: false, smsSegments: 0, smsSkipReason: 'no_phone', phone: null }),
  recipient({ userGuid: 'liam', willSendSms: false, smsSegments: 0, smsSkipReason: 'not_mobile', phone: '07 5550 1234' }),
  recipient({ userGuid: 'emma', status: 'confirm', reasons: ['already_logged_in'], hasLoggedIn: true }),
  recipient({ userGuid: 'noah', status: 'confirm', reasons: ['already_logged_in'], hasLoggedIn: true }),
  recipient({ userGuid: 'olivia', status: 'confirm', reasons: ['suspicious_email'], email: 'olivia@123.com' }),
  recipient({ userGuid: 'ava', status: 'blocked', reasons: ['no_email'], email: null, willSendEmail: false, willSendSms: false, smsSegments: 0 }),
  recipient({ userGuid: 'ethan', status: 'blocked', reasons: ['account_inactive'], isActive: false, willSendEmail: false, willSendSms: false, smsSegments: 0 }),
]

// ===== 默认勾选：只勾 ready；confirm 默认不勾；blocked 不可勾 =====
assert.deepEqual(getDefaultSelectedGuids(people), ['mia', 'jack', 'sophie', 'liam'])
assert.deepEqual(countRecipientsByStatus(people), { all: 9, ready: 4, confirm: 3, blocked: 2 })
assert.deepEqual(filterRecipients(people, 'confirm').map((item) => item.userGuid), ['emma', 'noah', 'olivia'])
assert.deepEqual(filterRecipients(people, 'blocked').map((item) => item.userGuid), ['ava', 'ethan'])
assert.equal(filterRecipients(people, 'all').length, 9)

// 重新预检：用户手动勾上的 confirm（emma）保留、手动取消的 ready（liam）保留；blocked 一律取消。
{
  const previous = new Set(['mia', 'jack', 'sophie', 'emma'])
  const next = people.map((item) => (item.userGuid === 'jack' ? { ...item, status: 'blocked' as const, reasons: ['no_channel' as const] } : item))
  assert.deepEqual(reconcileSelection(previous, people, next), ['mia', 'sophie', 'emma'])
  // 第一次预检（没有上一次名单）等同默认勾选。
  assert.deepEqual(reconcileSelection(new Set(), null, people), ['mia', 'jack', 'sophie', 'liam'])
  // 新出现的 ready 默认勾选，新出现的 confirm 不勾。
  const extended = [...people, recipient({ userGuid: 'zoe' }), recipient({ userGuid: 'max', status: 'confirm', reasons: ['already_logged_in'] })]
  assert.deepEqual(reconcileSelection(new Set(['mia']), people, extended), ['mia', 'zoe'])
}

// ===== 汇总：设计图的「将发送给 5 人：邮件 5 封 · 短信 3 条（约 A$0.24）· 跳过 4 人」 =====
{
  const selected = new Set(['mia', 'jack', 'sophie', 'liam', 'emma'])
  const summary = summarizeSelection(people, selected, 0.0792)
  assert.equal(summary.selectedCount, 5)
  assert.equal(summary.emailCount, 5)
  assert.equal(summary.smsRecipientCount, 3)
  assert.equal(summary.smsSegments, 3)
  assert.equal(formatAud(summary.smsCostAud), 'A$0.24')
  assert.equal(summary.skippedCount, 4)
  assert.deepEqual(summary.reissueRecipients.map((item) => item.userGuid), ['emma'], '只有勾选里已登录过的人才需要二次确认')

  // 默认勾选（4 个 ready）里没有已登录过的人：不出确认。
  const defaults = summarizeSelection(people, new Set(getDefaultSelectedGuids(people)), 0.0792)
  assert.equal(defaults.reissueRecipients.length, 0)
  assert.equal(defaults.selectedCount, 4)
  assert.equal(defaults.smsSegments, 2)

  // blocked 被塞进勾选集合也不计入；多条短信的人按 smsSegments 计费。
  const tricky = summarizeSelection(
    [recipient({ userGuid: 'a', smsSegments: 2 }), recipient({ userGuid: 'b', status: 'blocked', reasons: ['self'] })],
    new Set(['a', 'b']),
    0.0792,
  )
  assert.equal(tricky.selectedCount, 1)
  assert.equal(tricky.smsSegments, 2)
  assert.equal(tricky.skippedCount, 1)
  assert.equal(summarizeSelection(people, new Set(), 0.0792).selectedCount, 0)
}
assert.equal(formatAud(0.0792), 'A$0.08')
assert.equal(formatAud(Number.NaN), 'A$0.00')

// 每日额度提示：用量 + 本次计划条数超过上限才提示；上限未知（0）不提示。
assert.equal(isSmsQuotaExceeded(298, 300, 3), true)
assert.equal(isSmsQuotaExceeded(12, 300, 3), false)
assert.equal(isSmsQuotaExceeded(12, 0, 3), false)
assert.equal(isSmsQuotaExceeded(300, 300, 0), false)

// ===== 收件人原因：已知原因配文案 key，不认识的原因有兜底 =====
assert.deepEqual(describeRecipientReason('already_logged_in', 'confirm'), { key: 'alreadyLoggedIn', tone: 'warning' })
assert.deepEqual(describeRecipientReason('self', 'blocked'), { key: 'self', tone: 'default' })
assert.deepEqual(describeRecipientReason('no_channel', 'blocked'), { key: 'noChannel', tone: 'default' })
assert.deepEqual(describeRecipientReason('in_progress', 'blocked'), { key: 'inProgress', tone: 'default' })
assert.deepEqual(describeRecipientReason('no_email', 'blocked'), { key: 'noEmail', tone: 'error' })
assert.deepEqual(describeRecipientReason('brand_new_reason', 'blocked'), { key: 'unknownBlocked', tone: 'default', raw: 'brand_new_reason' })
assert.deepEqual(describeRecipientReason('brand_new_reason', 'confirm'), { key: 'unknownConfirm', tone: 'warning', raw: 'brand_new_reason' })
// 原型链上的名字不能被误认成已知原因。
assert.equal(describeRecipientReason('constructor', 'blocked').key, 'unknownBlocked')
assert.deepEqual(describeRejectedReason('self'), { known: true, key: 'self', raw: 'self' })
assert.deepEqual(describeRejectedReason('该账号已在另一个批次里'), { known: false, key: 'unknownBlocked', raw: '该账号已在另一个批次里' })

// 渠道计划：邮件 + 短信 / 只发邮件（短信被跳过）/ 仅邮件（用户自己关了短信）
assert.equal(getRecipientChannelPlan(people[0]), 'both')
assert.equal(getRecipientChannelPlan(people[2]), 'emailOnlySmsSkipped')
assert.equal(getRecipientChannelPlan({ ...people[0], willSendSms: false, smsSkipReason: 'sms_off', smsSegments: 0 }), 'emailOnly')
assert.equal(getRecipientChannelPlan({ ...people[0], willSendEmail: false }), 'smsOnly')
assert.equal(getRecipientChannelPlan(people[7]), 'none')

// ===== 短信字符与条数（契约：{{app_link}} 26、{{name}} 12、{{email}} 24、{{store}} 12） =====
{
  const defaultSms = "Hot Bargain: your staff app account is ready. Sign in with your email. We've emailed your password. Get the app: {{app_link}}"
  // 设计图：139 / 160 字符，1 条。原文 113 个字符 + 26 的短链 = 139。
  assert.equal(estimateSmsLength(defaultSms), 139)
  assert.equal(estimateSmsSegments(139), 1)
  assert.equal(estimateSmsLength('{{app_link}}'), 26)
  assert.equal(estimateSmsLength('{{name}}'), 12)
  assert.equal(estimateSmsLength('{{email}}'), 24)
  assert.equal(estimateSmsLength('{{store}}'), 12)
  assert.equal(estimateSmsLength('a\r\nb'), 3, 'CRLF 按一个换行计')
  assert.equal(estimateSmsSegments(0), 0)
  assert.equal(estimateSmsSegments(160), 1)
  assert.equal(estimateSmsSegments(161), 2, '超过 160 就按每条 153 拆')
  assert.equal(estimateSmsSegments(306), 2)
  assert.equal(estimateSmsSegments(307), 3)
  assert.equal(estimateSmsSegments(459), 3)
  assert.equal(estimateSmsSegments(460), 4)
  assert.equal(getSmsLengthLimit(1), 160)
  assert.equal(getSmsLengthLimit(2), 306)
  assert.equal(getSmsLengthLimit(3), 459)
}

// GSM-7 保守子集：弯引号、中文、emoji 都不行；常用标点和换行可以；变量本身不算非法字符。
assert.deepEqual(findInvalidSmsChars("Hi {{name}}, it's ready: $5 (see {{app_link}})!\nBye"), [])
assert.deepEqual(findInvalidSmsChars('It’s ready'), ['’'])
assert.deepEqual(findInvalidSmsChars('Hi 你好 😀 你'), ['你', '好', '😀'])
assert.deepEqual(findInvalidSmsChars('a{b}c'), ['{', '}'], '落单的花括号不在子集里')
assert.deepEqual(findInvalidSmsChars('Hi {{name}} {{unknown}}'), [], '未知变量由变量校验报，不重复当非法字符')
assert.deepEqual(findUnknownVariables('Hi {{name}} {{ name }} {{foo}} {{foo}}'), [' name ', 'foo'], '带空格的写法和未知变量一样被拒绝')

// ===== 模板校验 =====
{
  const goodEmailBody = 'Hi {{name}},\n\n{{login_box}}\n\nGet the app:\n{{download_buttons}}'
  const ok = validateTemplateDraft(
    {
      name: '账号已开通',
      emailSubject: 'Your Hot Bargain staff app account is ready',
      emailBody: goodEmailBody,
      smsBody: "Hot Bargain: your staff app account is ready. Sign in with your email. We've emailed your password. Get the app: {{app_link}}",
    },
    { issuesPassword: true, checkDuplicateName: false, otherTemplateNames: [] },
  )
  assert.equal(ok.canSave, true)
  assert.equal(ok.smsLength, 139)
  assert.equal(ok.smsSegments, 1)
  assert.deepEqual(ok.email.map((check) => [check.id, check.status]), [['email.loginBox', 'pass'], ['email.download', 'pass'], ['email.english', 'pass']])
  assert.deepEqual(ok.sms.map((check) => [check.id, check.status]), [['sms.length', 'pass'], ['sms.mailOnlyVariables', 'pass'], ['sms.charset', 'pass']])

  // 发放密码的模板缺 {{login_box}}：不能保存。
  const missingBox = validateTemplateDraft(
    { name: '账号已开通', emailSubject: 'Hi', emailBody: 'Hi {{name}}', smsBody: '' },
    { issuesPassword: true, checkDuplicateName: false, otherTemplateNames: [] },
  )
  assert.equal(missingBox.canSave, false)
  assert.equal(missingBox.email.find((check) => check.id === 'email.loginBox')?.status, 'fail')

  // 普通模板不要求 {{login_box}}；写了只提醒，不阻止保存（以服务端为准）。
  const general = validateTemplateDraft(
    { name: '通知', emailSubject: 'Hi', emailBody: 'Hi {{name}}', smsBody: 'Hi' },
    { issuesPassword: false, checkDuplicateName: true, otherTemplateNames: ['账号已开通'] },
  )
  assert.equal(general.canSave, true)
  assert.equal(general.email.some((check) => check.id === 'email.loginBox'), false)
  assert.equal(general.email.find((check) => check.id === 'email.download')?.status, 'warn')
  const generalWithBox = validateTemplateDraft(
    { name: '通知', emailSubject: 'Hi', emailBody: '{{login_box}}', smsBody: '' },
    { issuesPassword: false, checkDuplicateName: true, otherTemplateNames: [] },
  )
  assert.equal(generalWithBox.email.find((check) => check.id === 'email.loginBoxNotIssued')?.status, 'warn')
  assert.equal(generalWithBox.canSave, true)

  // 邮件只能英文：主题或正文有中文 / 全角标点都拒绝。
  const cjk = validateTemplateDraft(
    { name: 'x', emailSubject: '账号已开通', emailBody: 'Hello，world', smsBody: '' },
    { issuesPassword: false, checkDuplicateName: false, otherTemplateNames: [] },
  )
  assert.equal(cjk.email.find((check) => check.id === 'email.english')?.status, 'fail')
  assert.equal(cjk.canSave, false)

  // 出现别的 {{...}} 变量：邮件、短信都拒绝。
  const unknown = validateTemplateDraft(
    { name: 'x', emailSubject: 'Hi {{nick}}', emailBody: 'Hi', smsBody: 'Hi {{phone}}' },
    { issuesPassword: false, checkDuplicateName: false, otherTemplateNames: [] },
  )
  assert.equal(unknown.email.find((check) => check.id === 'email.variables')?.status, 'fail')
  assert.equal(unknown.sms.find((check) => check.id === 'sms.variables')?.status, 'fail')

  // 短信不能放 login_box / download_buttons。
  const smsBox = validateTemplateDraft(
    { name: 'x', emailSubject: 'Hi', emailBody: 'Hi', smsBody: 'Pw {{login_box}}' },
    { issuesPassword: false, checkDuplicateName: false, otherTemplateNames: [] },
  )
  assert.equal(smsBox.sms.find((check) => check.id === 'sms.mailOnlyVariables')?.status, 'fail')
  assert.equal(smsBox.canSave, false)

  // 短信超过 3 条（> 459 字符）拒绝；2 条仍可保存。
  const longSms = validateTemplateDraft(
    { name: 'x', emailSubject: 'Hi', emailBody: 'Hi', smsBody: 'a'.repeat(460) },
    { issuesPassword: false, checkDuplicateName: false, otherTemplateNames: [] },
  )
  assert.equal(longSms.sms.find((check) => check.id === 'sms.length')?.status, 'fail')
  assert.equal(longSms.smsSegments, 4)
  assert.equal(longSms.canSave, false)
  const twoSegments = validateTemplateDraft(
    { name: 'x', emailSubject: 'Hi', emailBody: 'Hi', smsBody: 'a'.repeat(200) },
    { issuesPassword: false, checkDuplicateName: false, otherTemplateNames: [] },
  )
  assert.equal(twoSegments.smsSegments, 2)
  assert.equal(twoSegments.canSave, true)

  // 弯引号让短信无法保存；变量展开后的长度参与估算（{{email}} 24 字符）。
  const curly = validateTemplateDraft(
    { name: 'x', emailSubject: 'Hi', emailBody: 'Hi', smsBody: 'We’ve emailed {{email}}' },
    { issuesPassword: false, checkDuplicateName: false, otherTemplateNames: [] },
  )
  assert.equal(curly.sms.find((check) => check.id === 'sms.charset')?.status, 'fail')
  assert.equal(curly.smsLength, 'We’ve emailed '.length + 24)

  // 名称：1–100 字符，自建模板不能重名（不分大小写）。
  const emptyName = validateTemplateDraft(
    { name: '  ', emailSubject: 'Hi', emailBody: 'Hi', smsBody: '' },
    { issuesPassword: false, checkDuplicateName: true, otherTemplateNames: [] },
  )
  assert.equal(emptyName.canSave, false)
  const dup = validateTemplateDraft(
    { name: ' Account Activated ', emailSubject: 'Hi', emailBody: 'Hi', smsBody: '' },
    { issuesPassword: false, checkDuplicateName: true, otherTemplateNames: ['account activated'] },
  )
  assert.equal(dup.name.find((check) => check.id === 'name.duplicate')?.status, 'fail')
  const tooLong = validateTemplateDraft(
    { name: 'n'.repeat(101), emailSubject: 'Hi', emailBody: 'Hi', smsBody: '' },
    { issuesPassword: false, checkDuplicateName: false, otherTemplateNames: [] },
  )
  assert.equal(tooLong.canSave, false)

  // 长度上限：主题 200 / 正文 4000。
  const longMail = validateTemplateDraft(
    { name: 'x', emailSubject: 's'.repeat(201), emailBody: 'b'.repeat(4001), smsBody: '' },
    { issuesPassword: false, checkDuplicateName: false, otherTemplateNames: [] },
  )
  assert.equal(longMail.email.find((check) => check.id === 'email.length')?.status, 'fail')
}

// 变量条点击插入到光标处；有选区时替换选区；没有光标信息时追加到末尾。
assert.deepEqual(insertTextAtSelection('Hi , bye', 3, 3, '{{name}}'), { value: 'Hi {{name}}, bye', caret: 11 })
assert.deepEqual(insertTextAtSelection('Hi XX bye', 3, 5, '{{name}}'), { value: 'Hi {{name}} bye', caret: 11 })
assert.deepEqual(insertTextAtSelection('abc', null, null, '!'), { value: 'abc!', caret: 4 })
assert.deepEqual(insertTextAtSelection('abc', 99, 99, '!'), { value: 'abc!', caret: 4 })

// ===== 批次：结果文案、进度、重试数 =====
function resultRow(id: string, emailStatus: StaffNotificationRecipientResult['emailStatus'], smsStatus: StaffNotificationRecipientResult['smsStatus'], note = ''): StaffNotificationRecipientResult {
  return { id, userGuid: id, displayName: id, storeName: null, emailMask: null, phoneMask: null, emailStatus, smsStatus, note }
}
function batch(overrides: Partial<StaffNotificationBatchDetail>): StaffNotificationBatchDetail {
  return {
    id: 'b1', templateKey: 'account-activated', templateName: '账号已开通', createdBy: 'admin', createdAtUtc: '2026-11-06T06:42:00Z', finishedAtUtc: null,
    status: 'running', result: 'running', totalCount: 5, emailRequested: 5, emailSent: 0, emailFailed: 0, smsRequested: 3, smsSent: 0, smsFailed: 0,
    smsSegments: 0, smsCostAud: 0, canRetry: false, recipients: [], ...overrides,
  }
}

// 设计图 3 的结果：邮件 4 / 5，短信 1 / 3；失败项 2 人（Emma 邮件失败，Jack 短信失败）。
{
  const finished = batch({
    status: 'completed', result: 'partial_failed', emailSent: 4, emailFailed: 1, smsSent: 1, smsFailed: 1, canRetry: true,
    recipients: [
      resultRow('mia', 'sent', 'sent'),
      resultRow('jack', 'sent', 'failed', '该号码 1 小时内已发满 5 条，稍后再试'),
      resultRow('sophie', 'sent', 'skipped', '没有手机号'),
      resultRow('liam', 'sent', 'skipped', '固定电话收不到短信'),
      resultRow('emma', 'failed', 'skipped', '邮件发送失败，密码没有改动，短信也不发'),
    ],
  })
  assert.deepEqual(getResultTitleCounts(finished), { email: '4 / 5', sms: '1 / 3' })
  assert.equal(countRetryRecipients(finished), 2)
  assert.deepEqual(getBatchProgress(finished), { done: 5, total: 5 }, '批次结束后进度补满')
  // 没勾短信的批次：短信计数为空，界面不显示。
  assert.deepEqual(getResultTitleCounts(batch({ smsRequested: 0, emailRequested: 2, emailSent: 2 })), { email: '2 / 2', sms: null })
}

// 进行中：按逐人明细算（邮件与短信都不再 pending 才算处理完）；没有明细时按渠道计数估算。
assert.deepEqual(
  getBatchProgress(batch({ recipients: [resultRow('a', 'sent', 'sent'), resultRow('b', 'sent', 'pending'), resultRow('c', 'pending', 'none')] })),
  { done: 1, total: 5 },
)
assert.deepEqual(getBatchProgress(batch({ emailSent: 2, emailFailed: 1, smsSent: 1 })), { done: 1, total: 5 }, '取进度更慢的渠道：邮件 3/5、短信 1/3')
assert.deepEqual(getBatchProgress(batch({ totalCount: 0 })), { done: 0, total: 0 })

// 结果标签与「已发出」口径（不写「已送达」）。
assert.deepEqual(getBatchResultMeta('all_sent'), { key: 'allSent', color: 'success' })
assert.deepEqual(getBatchResultMeta('partial_failed'), { key: 'partialFailed', color: 'warning' })
assert.deepEqual(getBatchResultMeta('all_failed'), { key: 'allFailed', color: 'error' })
assert.deepEqual(getBatchResultMeta('running'), { key: 'running', color: 'processing' })
assert.deepEqual(getChannelStatusDisplay('sent', true), { key: 'sent', color: 'success' })
assert.deepEqual(getChannelStatusDisplay('failed', true), { key: 'failed', color: 'error' })
assert.deepEqual(getChannelStatusDisplay('skipped', true), { key: 'notSent', color: 'default' })
assert.deepEqual(getChannelStatusDisplay('none', false), { key: 'notSent', color: 'default' })
assert.deepEqual(getChannelStatusDisplay('pending', false), { key: 'pending', color: 'processing' })
assert.deepEqual(getChannelStatusDisplay('pending', true), { key: 'unfinished', color: 'default' })

// ===== 错误码 / 服务端原因 =====
const businessError = Object.assign(new Error('TEMPLATE_INVALID: x'), { payload: { success: false, errorCode: 'TEMPLATE_INVALID', message: '邮件正文必须包含 {{login_box}}；短信超长' } })
assert.equal(getStaffNotificationErrorCode(businessError), 'TEMPLATE_INVALID')
assert.equal(getStaffNotificationErrorMessage(businessError, '保存失败'), '邮件正文必须包含 {{login_box}}；短信超长')
assert.equal(getStaffNotificationErrorCode(new Error('network')), undefined)
assert.equal(getStaffNotificationErrorMessage(new Error('network'), '保存失败'), '保存失败')

// ===== 批次轮询：每 1.5 秒查一次、结束即停、停止后不留悬挂定时器 =====
function createFakeScheduler() {
  let nextId = 1
  const timers = new Map<number, { callback: () => void; ms: number }>()
  const scheduler: BatchPollerScheduler = {
    setTimeout: (callback, ms) => {
      const id = nextId++
      timers.set(id, { callback, ms })
      return id
    },
    clearTimeout: (handle) => {
      timers.delete(handle as number)
    },
  }
  return {
    scheduler,
    pending: () => timers.size,
    delays: () => [...timers.values()].map((timer) => timer.ms),
    /** 触发所有到期定时器。 */
    fire: () => {
      const due = [...timers.entries()]
      timers.clear()
      due.forEach(([, timer]) => timer.callback())
    },
  }
}
const flush = () => new Promise<void>((resolve) => setImmediate(resolve))

{
  const fake = createFakeScheduler()
  const updates: string[] = []
  const finished: string[] = []
  const statuses: StaffNotificationBatchDetail['status'][] = ['queued', 'running', 'completed']
  let calls = 0
  const poller = startBatchPolling({
    batchId: 'b1',
    scheduler: fake.scheduler,
    fetchBatch: async () => batch({ status: statuses[Math.min(calls++, statuses.length - 1)] }),
    onUpdate: (detail) => updates.push(detail.status),
    onFinished: (detail) => finished.push(detail.status),
  })
  await flush()
  assert.equal(calls, 1, '启动后立即查一次')
  assert.deepEqual(fake.delays(), [BATCH_POLL_INTERVAL_MS], '间隔 1.5 秒')
  fake.fire()
  await flush()
  fake.fire()
  await flush()
  assert.deepEqual(updates, ['queued', 'running', 'completed'])
  assert.deepEqual(finished, ['completed'])
  assert.equal(fake.pending(), 0, '批次结束后不再排下一次')
  poller.stop()
}

// 卸载（stop）：清掉待执行的定时器，之后迟到的响应也不再触发回调。
{
  const fake = createFakeScheduler()
  const updates: string[] = []
  let release: ((detail: StaffNotificationBatchDetail) => void) | undefined
  let aborted = false
  const poller = startBatchPolling({
    batchId: 'b1',
    scheduler: fake.scheduler,
    fetchBatch: (_id, signal) => new Promise<StaffNotificationBatchDetail>((resolve) => {
      signal.addEventListener('abort', () => { aborted = true })
      release = resolve
    }),
    onUpdate: (detail) => updates.push(detail.status),
    onFinished: () => updates.push('finished'),
  })
  poller.stop()
  assert.equal(aborted, true, '在途请求被取消')
  release?.(batch({ status: 'completed' }))
  await flush()
  assert.deepEqual(updates, [], 'stop 之后迟到的响应不触发回调')
  assert.equal(fake.pending(), 0)
}
{
  const fake = createFakeScheduler()
  let calls = 0
  const poller = startBatchPolling({
    batchId: 'b1', scheduler: fake.scheduler,
    fetchBatch: async () => { calls += 1; return batch({ status: 'running' }) },
    onUpdate: () => undefined, onFinished: () => undefined,
  })
  await flush()
  assert.equal(fake.pending(), 1)
  poller.stop()
  assert.equal(fake.pending(), 0, '卸载时必须清掉悬挂的定时器')
  fake.fire()
  await flush()
  assert.equal(calls, 1, 'stop 之后不再发请求')
}

// 单次失败静默重试；连续失败到上限放弃并通知；成功一次后失败计数清零。
{
  const fake = createFakeScheduler()
  let calls = 0
  const gaveUp: unknown[] = []
  startBatchPolling({
    batchId: 'b1', scheduler: fake.scheduler, maxConsecutiveFailures: 3,
    fetchBatch: async () => { calls += 1; throw new Error('boom') },
    onUpdate: () => undefined, onFinished: () => undefined, onGiveUp: (error) => gaveUp.push(error),
  })
  for (let round = 0; round < 4; round += 1) {
    await flush()
    fake.fire()
  }
  await flush()
  assert.equal(calls, 3)
  assert.equal(gaveUp.length, 1)
  assert.equal(fake.pending(), 0)
}
{
  const fake = createFakeScheduler()
  const outcomes = ['fail', 'fail', 'ok', 'fail', 'fail', 'done']
  let index = 0
  const gaveUp: unknown[] = []
  const finished: string[] = []
  startBatchPolling({
    batchId: 'b1', scheduler: fake.scheduler, maxConsecutiveFailures: 3,
    fetchBatch: async () => {
      const outcome = outcomes[index++]
      if (outcome === 'fail') throw new Error('boom')
      return batch({ status: outcome === 'done' ? 'completed' : 'running' })
    },
    onUpdate: () => undefined, onFinished: (detail) => finished.push(detail.status), onGiveUp: (error) => gaveUp.push(error),
  })
  for (let round = 0; round < 7; round += 1) {
    await flush()
    fake.fire()
  }
  await flush()
  assert.deepEqual(finished, ['completed'], '中间成功过一次，失败计数清零，不会提前放弃')
  assert.equal(gaveUp.length, 0)
}

// ===== 短信发件名：选择仍有效就沿用，失效回落默认项，空名单不传 =====
{
  const senders = [{ id: 'HotBargain', isDefault: true }, { id: 'ANTPOS', isDefault: false }]
  assert.equal(resolveSmsSenderId(senders, undefined), 'HotBargain', '没选过用默认项')
  assert.equal(resolveSmsSenderId(senders, 'ANTPOS'), 'ANTPOS', '选择仍在名单里就沿用')
  assert.equal(resolveSmsSenderId(senders, 'HotBargain'), 'HotBargain')
  assert.equal(resolveSmsSenderId(senders, 'Removed'), 'HotBargain', '重新预检后选的项已不在名单里：回落默认项，不能把失效值传给后端')
  assert.equal(resolveSmsSenderId(senders, ''), 'HotBargain', '空串不是有效选择')
  assert.equal(resolveSmsSenderId(senders.slice(0, 1), 'ANTPOS'), 'HotBargain', '名单缩成只剩默认项')
  // 默认项不在第一位也按 isDefault 找；整个名单都没标默认项时退而取第一项。
  assert.equal(resolveSmsSenderId([{ id: 'ANTPOS', isDefault: false }, { id: 'HotBargain', isDefault: true }], undefined), 'HotBargain')
  assert.equal(resolveSmsSenderId([{ id: 'A', isDefault: false }, { id: 'B', isDefault: false }], 'gone'), 'A')
  // 短信不可用 / 名单为空：没有可传的发件名。
  assert.equal(resolveSmsSenderId([], undefined), undefined)
  assert.equal(resolveSmsSenderId([], 'ANTPOS'), undefined)
}

console.log('staffNotificationLogic tests passed')
