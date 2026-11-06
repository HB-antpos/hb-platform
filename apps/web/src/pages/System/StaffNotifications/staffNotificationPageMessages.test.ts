import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import i18next from 'i18next'

import { getTemplateCheckText } from './TemplateChecks'
import { validateTemplateDraft, type TemplateCheck } from './staffNotificationLogic'

// 员工通知页（模板 + 发送记录）的文案被拆成「全局菜单键 + 页面级消息文件」：
// 拆开后键名写错不会有任何报错，界面会直接显示原始键名，所以在这里做静态核对。
// 本文件只核对这一页自己的源码；发送抽屉（用户管理页）的键由它自己的契约测试核对。

type Json = { [key: string]: Json | string }

const dir = 'src/pages/System/StaffNotifications'
const readJson = (path: string): Json => JSON.parse(readFileSync(resolve(path), 'utf8')) as Json
const readSource = (name: string) => readFileSync(resolve(dir, name), 'utf8')

function flattenKeys(resource: Json, prefix = ''): string[] {
  return Object.entries(resource).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : flattenKeys(value, `${prefix}${key}.`),
  )
}

const pageZh = readJson(`${dir}/staffNotificationPageMessages.zh.json`)
const pageEn = readJson(`${dir}/staffNotificationPageMessages.en.json`)
const sharedZh = readJson(`${dir}/staffNotificationSharedMessages.zh.json`)
const sharedEn = readJson(`${dir}/staffNotificationSharedMessages.en.json`)
const globalZh = readJson('src/i18n/locales/zh.json')
const globalEn = readJson('src/i18n/locales/en.json')

// 中英文键集合一致。
assert.deepEqual(flattenKeys(pageZh).sort(), flattenKeys(pageEn).sort(), '页面级消息中英文键集合必须一致')
assert.deepEqual(flattenKeys(sharedZh).sort(), flattenKeys(sharedEn).sort(), '共享消息中英文键集合必须一致')

// 每个文案都有内容，英文里不能夹中文（员工通知页的英文界面是给英文用户看的）。
for (const [lang, resource] of [['zh', pageZh], ['en', pageEn]] as const) {
  for (const key of flattenKeys(resource)) {
    let node: Json | string = resource
    for (const segment of key.split('.')) node = (node as Json)[segment]
    assert.equal(typeof node === 'string' && node.trim().length > 0, true, `${lang} ${key} 不能为空`)
    if (lang === 'en') assert.equal(/[一-鿿]/.test(node as string), false, `en ${key} 不能含中文`)
  }
}

// 全局菜单键：中英文都有。
assert.equal((globalZh.menu as Json).staffNotifications, '员工通知')
assert.equal((globalEn.menu as Json).staffNotifications, 'Staff notifications')

// 源码里出现的字面量键必须存在（页面级 ∪ 共享，中英文都要有）。
const pageKeyPattern = /['"`](system\.staffNotifications\.(?:page|templates|logs|channelStatus|batchResult)\.[A-Za-z0-9_.]+)['"`]/g
const sources = ['index.tsx', 'TemplatesTab.tsx', 'LogsTab.tsx', 'TemplateChecks.tsx', 'StaffNotificationTags.tsx']
// flattenKeys 对完整文件给出的键本身已带 system.staffNotifications. 前缀。
const zhKeys = new Set([...flattenKeys(pageZh), ...flattenKeys(sharedZh)])
const enKeys = new Set([...flattenKeys(pageEn), ...flattenKeys(sharedEn)])
let referenced = 0
for (const source of sources) {
  for (const match of readSource(source).matchAll(pageKeyPattern)) {
    referenced += 1
    assert.equal(zhKeys.has(match[1]), true, `${source} 引用的 ${match[1]} 在中文消息里不存在`)
    assert.equal(enKeys.has(match[1]), true, `${source} 引用的 ${match[1]} 在英文消息里不存在`)
  }
}
assert.equal(referenced > 40, true, '应当扫到足够多的引用，防止正则失效后静默通过')

// 页面源码契约：用 MeasuredTable + 稳定 metricId；不直接导入 antd Table；「发测试给我」本期不做；不带设计图标注。
const tabs = `${readSource('TemplatesTab.tsx')}\n${readSource('LogsTab.tsx')}\n${readSource('index.tsx')}`
assert.equal(/import\s*\{[^}]*\bTable\b[^}]*\}\s*from\s*'antd'/.test(tabs), false, '不能直接从 antd 导入 Table')
assert.equal(tabs.includes('metricId="system.staff-notifications.batches"'), true)
assert.equal(tabs.includes('metricId="system.staff-notifications.batch-recipients"'), true)
assert.equal(tabs.includes('发测试给我'), false, '「发测试给我」本期不做')
assert.equal(tabs.includes('data-pin'), false)
assert.equal(tabs.includes('dangerouslySetInnerHTML'), false)
assert.equal(readSource('index.tsx').includes('registerPageMessages({ zh: pageMessagesZh, en: pageMessagesEn })'), true, '页面级消息必须在页面顶层懒注册')

// ===== 校验清单文案：真实走一遍 i18n，变量名要原样显示（不能被当成插值占位符吞掉） =====
for (const [language, page, shared] of [['zh', pageZh, sharedZh], ['en', pageEn, sharedEn]] as const) {
  const instance = i18next.createInstance()
  await instance.init({ lng: language, resources: {}, interpolation: { escapeValue: false } })
  // 与线上一致：两份消息分别用 registerPageMessages 的方式深度合并进同一个 translation 命名空间。
  instance.addResourceBundle(language, 'translation', page, true, false)
  instance.addResourceBundle(language, 'translation', shared, true, false)
  const t = instance.t.bind(instance)

  const good = validateTemplateDraft(
    {
      name: 'x',
      emailSubject: 'Hi',
      emailBody: 'Hi {{name}}\n{{login_box}}\n{{download_buttons}}',
      smsBody: 'Get the app: {{app_link}}',
    },
    { issuesPassword: true, checkDuplicateName: false, otherTemplateNames: [] },
  )
  const loginBox = getTemplateCheckText(good.email.find((check) => check.id === 'email.loginBox') as TemplateCheck, t)
  assert.equal(loginBox.includes('{{login_box}}'), true, `${language}：含 {{login_box}} 的提示要把变量名原样显示：${loginBox}`)
  const noPassword = getTemplateCheckText(good.sms.find((check) => check.id === 'sms.mailOnlyVariables') as TemplateCheck, t)
  assert.equal(noPassword.includes('{{login_box}}'), true, `${language}：${noPassword}`)
  const length = getTemplateCheckText(good.sms.find((check) => check.id === 'sms.length') as TemplateCheck, t)
  assert.equal(/\d+ \/ 160/.test(length), true, `${language}：短信长度提示应显示「长度 / 160」：${length}`)

  // 各种失败 / 提醒 / 空短信场景：每条都有文案、没有残留的 {{...}} 占位或原始键名。
  const drafts = [
    { draft: { name: '', emailSubject: '账号', emailBody: 'Hi {{nick}} 你好', smsBody: 'We’ve {{login_box}} {{phone}} ' + 'a'.repeat(500) }, issuesPassword: true },
    { draft: { name: 'n'.repeat(101), emailSubject: 's'.repeat(201), emailBody: '{{login_box}}', smsBody: '' }, issuesPassword: false },
    { draft: { name: 'Dup', emailSubject: 'Hi', emailBody: 'Hi', smsBody: '' }, issuesPassword: false },
  ]
  for (const { draft, issuesPassword } of drafts) {
    const validation = validateTemplateDraft(draft, { issuesPassword, checkDuplicateName: true, otherTemplateNames: ['dup'] })
    // 名称只显示出问题的（通过项不渲染），所以名称的 pass 没有文案是预期的。
    for (const check of [...validation.name.filter((item) => item.status !== 'pass'), ...validation.email, ...validation.sms]) {
      const text = getTemplateCheckText(check, t)
      assert.equal(text.length > 0, true, `${language}：${check.id}/${check.status} 没有文案`)
      assert.equal(text.startsWith('system.staffNotifications'), false, `${language}：${check.id}/${check.status} 显示了原始键名：${text}`)
      // 只有 {{login_box}} / {{download_buttons}} / {{app_link}} 这类「变量名本身」允许出现花括号。
      const leftovers = [...text.matchAll(/\{\{([^{}]*)\}\}/g)].map((match) => match[1]).filter((name) => !['login_box', 'download_buttons', 'app_link', 'name', 'email', 'store', 'nick', 'phone'].includes(name))
      assert.deepEqual(leftovers, [], `${language}：${check.id}/${check.status} 有没替换的占位符：${text}`)
    }
  }
}

console.log('staffNotificationPageMessages tests passed')
