import {
  buildGuardianLinkPayload,
  canSubmitGuardianLink,
  formatSydneyMonthDay,
  formatSydneyMonthDayTime,
  getCodeChannel,
  getDefaultChannels,
  getFormTypeLabel,
  getLastSentChannels,
  getRelationshipLabel,
  getRelationshipOptions,
  getReturnFieldsText,
  getSendResultWarning,
  getSmsBlockReason,
  getStageMeta,
  getStaffActions,
  isAustralianMobile,
  parseUtcTimestamp,
  shouldShowSendResult,
} from './minorGuardianLinkLogic'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

function assertJson(actual: unknown, expected: unknown, message: string) {
  const a = JSON.stringify(actual)
  const e = JSON.stringify(expected)
  if (a !== e) {
    throw new Error(`${message}: expected ${e}, got ${a}`)
  }
}

// 阶段 → 标签 / 颜色
assertJson(getStageMeta('not_sent'), { label: '未发送', color: 'orange' }, '未发送为橙色')
assertJson(getStageMeta('link_expired'), { label: '链接已过期', color: 'orange' }, '过期为橙色')
assertJson(getStageMeta('awaiting_guardian'), { label: '待家长签字', color: 'blue' }, '待签字为蓝色')
assertJson(getStageMeta('pending_review'), { label: '待审核', color: 'gold' }, '待审核为金色')
assertJson(getStageMeta('returned'), { label: '退回修改', color: 'red' }, '退回为红色')
assertJson(getStageMeta('approved'), { label: '审核通过', color: 'green' }, '通过为绿色')

assertEqual(getFormTypeLabel('QLD_CE1'), 'QLD CE1 家长同意表', 'QLD 同意书')
assertEqual(getFormTypeLabel('NSW_COMPANY_CONSENT'), 'NSW 公司同意书', 'NSW 短名')
assertEqual(getFormTypeLabel('NSW_COMPANY_CONSENT', true), 'NSW 公司未成年员工同意书', 'NSW 全名')

// 关系：英文存值、中文显示，旧值不在选项里时保留
assertEqual(getRelationshipLabel('Mother'), '母亲', '母亲')
assertEqual(getRelationshipLabel('legal guardian'), '法定监护人', '大小写不敏感')
assertEqual(getRelationshipLabel('Aunt'), 'Aunt', '未知关系原样显示')
assertEqual(getRelationshipOptions('Aunt').length, 5, '未知旧值追加为选项')
assertEqual(getRelationshipOptions('Father').length, 4, '已知值不重复追加')

assertEqual(getReturnFieldsText(['education', 'parent', 'custom']), '学校时间与假期、家长电话 / 邮箱、custom', '退回字段中文')

// 澳洲手机判断
assert(isAustralianMobile('0412 345 678'), '04 开头带空格')
assert(isAustralianMobile('+61412345678'), '+614 开头')
assert(isAustralianMobile('61412345678'), '614 开头')
assert(!isAustralianMobile('0298765432'), '固定电话不是手机')
assert(!isAustralianMobile('041234567'), '位数不够')
assert(!isAustralianMobile(''), '空值')

assertEqual(getSmsBlockReason('0412345678', false), 'sms_unavailable', '总开关未开')
assertEqual(getSmsBlockReason('0298765432', true), 'not_mobile', '固话置灰')
assertEqual(getSmsBlockReason('', true), null, '空号码先不拦')
assertEqual(getSmsBlockReason('0412345678', true), null, '手机可发')

// 默认开关
assertJson(getDefaultChannels({ guardianEmail: 'a@b.com', guardianPhone: '0412345678' }, true), { sendEmail: true, sendSms: true }, '邮箱+手机全开')
assertJson(getDefaultChannels({ guardianEmail: undefined, guardianPhone: '0412345678' }, false), { sendEmail: false, sendSms: false }, '短信不可用不开')
assertJson(getDefaultChannels({ guardianEmail: 'a@b.com', guardianPhone: '0298765432' }, true), { sendEmail: true, sendSms: false }, '固话不开短信')

// 是否可提交
const base = { guardianName: 'Linh', guardianRelationship: 'Mother', guardianEmail: 'l@gmail.com', guardianPhone: '0412345678', sendEmail: true, sendSms: false, expiryDays: 3 as const }
assert(canSubmitGuardianLink(base, true), '完整资料可提交')
assert(!canSubmitGuardianLink({ ...base, guardianName: '  ' }, true), '缺姓名')
assert(!canSubmitGuardianLink({ ...base, guardianPhone: '' }, true), '缺电话')
assert(!canSubmitGuardianLink({ ...base, guardianEmail: 'bad' }, true), '邮箱格式错误')
assert(!canSubmitGuardianLink({ ...base, sendEmail: false, sendSms: false }, true), '没开渠道')
assert(canSubmitGuardianLink({ ...base, guardianEmail: '', sendEmail: false, sendSms: true }, true), '只发短信不需要邮箱')
assert(!canSubmitGuardianLink({ ...base, sendEmail: false, sendSms: true }, false), '短信不可用时只开短信不能提交')
assert(!canSubmitGuardianLink({ ...base, guardianPhone: '0298765432', sendEmail: false, sendSms: true }, true), '固话只开短信不能提交')

assertEqual(getCodeChannel({ ...base, sendSms: true }, true), 'sms', '开短信验证码走短信')
assertEqual(getCodeChannel({ ...base, sendSms: true }, false), 'email', '短信不可用走邮箱')

// 提交载荷：置灰的短信按关闭处理；空串变 undefined；版本透传
const payload = buildGuardianLinkPayload({ version: 2, revision: 5 }, { ...base, guardianPhone: '0298765432', sendSms: true, guardianRelationship: ' ' }, true)
assertEqual(payload.sendSms, false, '固话时短信不发送')
assertEqual(payload.guardianRelationship, undefined, '空关系不传')
assertEqual(payload.expectedVersion, 2, '版本透传')
assertEqual(payload.expectedRevision, 5, '修订透传')
const fresh = buildGuardianLinkPayload({}, { ...base, expiryDays: undefined }, true)
assertEqual(fresh.expectedVersion, null, '无记录传 null')
assertEqual(fresh.expiryDays, 3, '有效期默认 3 天')

// 发送结果
const ok = { emailRequested: true, emailSent: true, smsRequested: true, smsSent: true, codeChannel: 'sms' as const }
assert(!shouldShowSendResult(ok), '全部成功只弹提示')
assert(shouldShowSendResult({ ...ok, smsSent: false }), '短信失败弹结果')
assert(shouldShowSendResult({ ...ok, signingUrl: 'https://x' }), '有链接弹结果')
assert(!shouldShowSendResult({ emailRequested: true, emailSent: true, smsRequested: false, smsSent: false }), '未请求的渠道不算失败')

const smsFailedWarning = getSendResultWarning({ ...ok, smsSent: false, codeChannel: 'email' })
assert(smsFailedWarning.includes('邮件里的链接') && smsFailedWarning.endsWith('验证码会改发到邮箱。'), '短信失败改发邮箱')
assert(!getSendResultWarning({ ...ok, emailSent: false }).includes('验证码'), '邮件失败不提验证码改发')
assert(getSendResultWarning({ ...ok, emailSent: false, smsSent: false, codeChannel: 'email' }).includes('都没有送达'), '全部失败')

// 操作
assertJson(getStaffActions({ stage: 'not_sent' }), ['send'], '未发送')
assertJson(getStaffActions({ stage: 'link_expired' }), ['send'], '过期')
assertJson(getStaffActions({ stage: 'returned' }), ['resend'], '退回')
assertJson(getStaffActions({ stage: 'awaiting_guardian' }), ['resend', 'revoke'], '待签字')
assertJson(getStaffActions({ stage: 'pending_review' }), ['review'], '待审核')
assertJson(getStaffActions({ stage: 'approved', complianceId: 9 }), ['document'], '通过')
assertJson(getStaffActions({ stage: 'approved' }), [], '通过但缺记录 Id')

assertJson(getLastSentChannels({ lastSentEmail: true, lastSentSms: null }), [{ label: '邮件', ok: true }], 'null 渠道不显示')
assertJson(getLastSentChannels({ lastSentEmail: false, lastSentSms: true }), [{ label: '邮件', ok: false }, { label: '短信', ok: true }], '两个渠道')

// 时间：不带时区后缀按 UTC，悉尼夏令时 +11
assertEqual(parseUtcTimestamp('2026-11-02T05:20:00'), Date.UTC(2026, 10, 2, 5, 20), '无后缀按 UTC')
assertEqual(parseUtcTimestamp('2026-11-02T05:20:00Z'), Date.UTC(2026, 10, 2, 5, 20), '带 Z')
assertEqual(formatSydneyMonthDayTime('2026-11-02T05:20:00'), '11-02 16:20', '夏令时 +11')
assertEqual(formatSydneyMonthDayTime('2026-06-30T14:30:00Z'), '07-01 00:30', '冬令时 +10 跨日')
assertEqual(formatSydneyMonthDay('2026-10-28T23:00:00Z'), '10-29', '悉尼日期')
assertEqual(formatSydneyMonthDayTime(undefined), '—', '空值')

console.log('minorGuardianLinkLogic.test: ok')
