import assert from 'node:assert/strict'
import { BROWSER_EXTENSION_PRIVACY_POLICY as policy } from './browserExtensionPrivacyPolicy'

// 扩展 1.5.0 起采集供应商分类：隐私政策必须如实披露，并与“不保存密码/refresh token”的现状一致。
const section = (id: string) => {
  const found = policy.sections.find((item) => item.id === id)
  assert.ok(found, `缺少隐私政策章节 ${id}`)
  return [...found.paragraphs, ...found.items].join('\n')
}

assert.equal(policy.policyVersion, '2026-09-24')
assert.equal(policy.effectiveDate, '24 September 2026')

const information = section('information-we-handle')
assert.match(information, /Supplier category information/)
assert.match(information, /item numbers listed on each category page/)
assert.match(information, /never collects, reads or stores usernames, passwords, website cookies or refresh tokens/)
assert.doesNotMatch(information, /password entered for sign-in/)

assert.match(section('how-information-is-used'), /full category capture runs only after the employee starts it/)

const permissions = section('browser-permissions')
assert.match(permissions, /one at a time at a limited rate/)
assert.match(permissions, /No additional browser permission is requested for category capture/)
assert.doesNotMatch(permissions, /employee sign-in/)

const retention = section('storage-and-retention')
assert.match(retention, /does not store passwords or refresh tokens/)
assert.match(retention, /expire after six hours/)
assert.doesNotMatch(retention, /refresh token and settings required to keep the employee signed in/)

assert.match(section('remote-code-and-tracking'), /records supplier category pages only/)

// 全文不得残留旧版“扩展内登录、保存 refresh token”的描述。
const fullText = policy.sections.map((item) => [...item.paragraphs, ...item.items].join('\n')).join('\n')
assert.doesNotMatch(fullText, /The password is transmitted/)
assert.doesNotMatch(fullText, /held in extension local storage until logout/)

console.log('browserExtensionPrivacyPolicy.test: ok')
