import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import i18next from 'i18next'

import { describeRecipientReason, getSmsSkipKey } from './staffNotificationLogic'

// 发送员工通知抽屉（用户管理页入口）的文案被拆成「页面级消息文件 + 用户页消息文件」：
// 拆开后键名写错不会有任何报错，界面会直接显示原始键名，所以在这里做静态核对。
// 员工通知页本身（模板 + 发送记录）的键由 staffNotificationPageMessages.test.ts 核对。

type Json = { [key: string]: Json | string }

const dir = 'src/pages/System/StaffNotifications'
const readJson = (path: string): Json => JSON.parse(readFileSync(resolve(path), 'utf8')) as Json
const readSource = (name: string) => readFileSync(resolve(dir, name), 'utf8')

function flattenKeys(resource: Json, prefix = ''): string[] {
  return Object.entries(resource).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : flattenKeys(value, `${prefix}${key}.`),
  )
}
function leaf(resource: Json, path: string): string | undefined {
  let node: Json | string | undefined = resource
  for (const segment of path.split('.')) {
    if (typeof node !== 'object' || node === null) return undefined
    node = node[segment]
  }
  return typeof node === 'string' ? node : undefined
}

const sendZh = readJson(`${dir}/staffNotificationSendMessages.zh.json`)
const sendEn = readJson(`${dir}/staffNotificationSendMessages.en.json`)
const sharedZh = readJson(`${dir}/staffNotificationSharedMessages.zh.json`)
const sharedEn = readJson(`${dir}/staffNotificationSharedMessages.en.json`)
const usersZh = readJson('src/pages/System/Users/usersPageMessages.zh.json')
const usersEn = readJson('src/pages/System/Users/usersPageMessages.en.json')

// 中英文键集合一致，每条都有内容，英文里不能夹中文。
assert.deepEqual(flattenKeys(sendZh).sort(), flattenKeys(sendEn).sort(), '发送抽屉消息中英文键集合必须一致')
for (const [lang, resource] of [['zh', sendZh], ['en', sendEn]] as const) {
  for (const key of flattenKeys(resource)) {
    const text = leaf(resource, key)
    assert.equal(typeof text === 'string' && text.trim().length > 0, true, `${lang} ${key} 不能为空`)
    if (lang === 'en') assert.equal(/[一-鿿]/.test(text as string), false, `en ${key} 不能含中文`)
  }
}

// 源码里出现的字面量键必须存在（页面级 ∪ 共享，中英文都要有）。
const keyPattern = /['"`](system\.staffNotifications\.(?:send|confirm|progress|result|channelStatus|batchResult)\.[A-Za-z0-9_.]+)['"`]/g
const sources = ['SendNotificationDrawer.tsx', 'SendFlowModals.tsx', 'SendPreviewPane.tsx', 'SendRecipientTable.tsx', 'StaffNotificationTags.tsx']
const zhKeys = new Set([...flattenKeys(sendZh), ...flattenKeys(sharedZh)])
const enKeys = new Set([...flattenKeys(sendEn), ...flattenKeys(sharedEn)])
let referenced = 0
for (const source of sources) {
  for (const match of readSource(source).matchAll(keyPattern)) {
    referenced += 1
    assert.equal(zhKeys.has(match[1]), true, `${source} 引用的 ${match[1]} 在中文消息里不存在`)
    assert.equal(enKeys.has(match[1]), true, `${source} 引用的 ${match[1]} 在英文消息里不存在`)
  }
}
assert.equal(referenced > 60, true, '应当扫到足够多的引用，防止正则失效后静默通过')

// 动态拼出来的键（原因标签 / 说明、短信跳过原因）：逻辑层可能给出的每个 key 都要有文案，已知原因与兜底都算。
const reasonCodes = [
  'already_logged_in', 'suspicious_email', 'no_email', 'account_inactive', 'login_disabled', 'forbidden', 'not_found',
  'self', 'no_channel', 'in_progress',
]
const reasonKeys = new Set<string>(reasonCodes.map((code) => describeRecipientReason(code, 'blocked').key))
reasonKeys.add(describeRecipientReason('some_future_reason', 'blocked').key)
reasonKeys.add(describeRecipientReason('some_future_reason', 'confirm').key)
assert.equal(reasonKeys.size, 12, '10 个已知原因 + 两个兜底都应各有一个 key')
for (const key of reasonKeys) {
  for (const [lang, bundle] of [['zh', sendZh], ['en', sendEn]] as const) {
    assert.equal(leaf(bundle, `system.staffNotifications.send.reason.${key}`) !== undefined, true, `${lang} 缺少原因标签 reason.${key}`)
    assert.equal(leaf(bundle, `system.staffNotifications.send.reason.${key}Hint`) !== undefined, true, `${lang} 缺少原因说明 reason.${key}Hint`)
  }
}
for (const skip of ['sms_unavailable', 'no_phone', 'not_mobile', 'sms_off'] as const) {
  const key = getSmsSkipKey(skip)
  assert.notEqual(key, null)
  for (const [lang, bundle] of [['zh', sendZh], ['en', sendEn]] as const) {
    assert.equal(leaf(bundle, `system.staffNotifications.send.smsSkip.${key}`) !== undefined, true, `${lang} 缺少短信跳过原因 smsSkip.${key}`)
  }
}
for (const key of ['alreadyLoggedIn', 'suspiciousEmail', 'noEmail', 'accountInactive', 'loginDisabled', 'forbidden', 'notFound', 'self', 'noChannel', 'inProgress']) {
  assert.equal(reasonKeys.has(key), true, `原因 key ${key} 应由逻辑层产生`)
}

// 用户管理页新增的入口文案（登录情况下拉、行菜单、批量按钮）。
for (const [lang, bundle] of [['zh', usersZh], ['en', usersEn]] as const) {
  for (const key of [
    'system.usersWorkspace.loginStateFilter',
    'system.usersWorkspace.loginStateAll',
    'system.usersWorkspace.loginStateNever',
    'system.usersWorkspace.loginStateLogged',
    'system.usersWorkspace.sendNotification',
    'system.usersBatch.sendNotification',
  ]) {
    assert.equal(leaf(bundle, key) !== undefined, true, `${lang} 用户页消息缺少 ${key}`)
  }
}
// 用户页源码里确实用到了这些键（防止加了文案却没接上）。
const usersSource = readFileSync(resolve('src/pages/System/Users/index.tsx'), 'utf8')
for (const key of ['system.usersWorkspace.loginStateFilter', 'system.usersWorkspace.sendNotification']) {
  assert.equal(usersSource.includes(`'${key}'`), true, `Users/index.tsx 应使用 ${key}`)
}
assert.equal(readFileSync(resolve('src/pages/System/Users/UserBatchActions.tsx'), 'utf8').includes("'system.usersBatch.sendNotification'"), true)

// 发送抽屉在页面顶层懒注册自己的消息；预览走 sandbox iframe，不用 dangerouslySetInnerHTML；表格用 MeasuredTable。
const drawer = readSource('SendNotificationDrawer.tsx')
assert.equal(drawer.includes('registerPageMessages({ zh: sendMessagesZh, en: sendMessagesEn })'), true)
const preview = readSource('SendPreviewPane.tsx')
assert.equal(preview.includes('dangerouslySetInnerHTML'), false, '邮件预览不能用 dangerouslySetInnerHTML')
assert.equal(/<iframe[\s\S]*?sandbox="[^"]*"[\s\S]*?srcDoc=/.test(preview), true, '邮件预览用 sandbox iframe + srcDoc')
assert.equal(/sandbox="[^"]*allow-scripts/.test(preview), false, 'sandbox 不能放开脚本')
for (const source of sources) {
  const text = readSource(source)
  assert.equal(/import\s*\{[^}]*\bTable\b[^}]*\}\s*from\s*'antd'/.test(text), false, `${source} 不能直接从 antd 导入 Table`)
  assert.equal(text.includes('data-pin'), false, `${source} 不能带设计图标注`)
}
assert.equal(readSource('SendRecipientTable.tsx').includes('metricId="system.staff-notifications.send-recipients"'), true)
assert.equal(readSource('SendFlowModals.tsx').includes('metricId="system.staff-notifications.send-result"'), true)

// ===== 真实走一遍 i18n：汇总文案里的 <b> 与插值、没有残留占位符 =====
for (const [language, send, shared] of [['zh', sendZh, sharedZh], ['en', sendEn, sharedEn]] as const) {
  const instance = i18next.createInstance()
  await instance.init({ lng: language, resources: {}, interpolation: { escapeValue: false } })
  instance.addResourceBundle(language, 'translation', send, true, false)
  instance.addResourceBundle(language, 'translation', shared, true, false)

  assert.equal(instance.t('system.staffNotifications.send.summary.lead', { count: 5 }).includes('<b>5</b>'), true, `${language}：汇总开头要保留 <b>${5}</b> 给 Trans 渲染`)
  assert.equal(instance.t('system.staffNotifications.send.summary.sms', { count: 3, cost: 'A$0.24' }).includes('A$0.24'), true)
  const title = instance.t('system.staffNotifications.confirm.title', { name: '账号已开通' })
  assert.equal(title.includes('账号已开通') && !title.includes('{{'), true, `${language}：${title}`)
  const when = instance.t('system.staffNotifications.send.reason.alreadyLoggedInHint', { when: instance.t('system.staffNotifications.send.lastLogin.days', { count: 3 }) })
  assert.equal(when.includes('3') && !when.includes('{{'), true, `${language}：${when}`)
  assert.equal(instance.t('system.staffNotifications.result.titleWithParts', { parts: '4 / 5' }).includes('4 / 5'), true)
  assert.equal(instance.t('system.staffNotifications.channelStatus.sent'), language === 'zh' ? '已发出' : 'Sent', '已发出不能写成「已送达」')
}
// 中文口径：不写「已送达」。
assert.equal(JSON.stringify(sendZh).includes('已送达'), false)
assert.equal(JSON.stringify(sharedZh).includes('已送达'), false)

console.log('staffNotificationSendMessages tests passed')
