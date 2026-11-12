import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

import mobilePolicyEn from '../../content/mobile-privacy-policy.en.json'
import mobilePolicyZh from '../../content/mobile-privacy-policy.zh.json'
import policyEn from '../../content/pos-privacy-policy.en.json'
import policyZh from '../../content/pos-privacy-policy.zh.json'
import { isPublicAppPath } from '../MobilePrivacy/publicPath'

import {
  getPosPrivacyPolicy,
  resolvePosPrivacyLanguage,
  type PosPrivacyLanguage,
} from './posPrivacyPolicy'

// 测试在 apps/web 目录下运行（与 supportPage.test.ts 一致），按相对路径读源码做登记契约检查。
const appSource = readFileSync('src/App.tsx', 'utf8')
const pageSource = readFileSync('src/pages/PosPrivacy/index.tsx', 'utf8')

const EXPECTED_SECTION_IDS = [
  'account',
  'collection',
  'purposes',
  'permissions',
  'sharing',
  'overseas',
  'tracking',
  'retention',
  'rights',
  'changes',
]
const PUBLIC_URL = 'https://hotbargain.vip/privacy/pos'
const LANGUAGES: PosPrivacyLanguage[] = ['en', 'zh']

// 与 /privacy/mobile 完全一致的字段契约：多一个或少一个字段都视为漂移。
const POLICY_KEYS = [
  'policyVersion',
  'language',
  'title',
  'subtitle',
  'effectiveDateLabel',
  'effectiveDate',
  'summary',
  'organization',
  'sections',
  'footer',
]
const ORGANIZATION_KEYS = ['label', 'name', 'contactLabel', 'email']
const SECTION_KEYS = ['id', 'title', 'paragraphs', 'items']
const FOOTER_KEYS = [
  'backLabel',
  'publicCopy',
  'publicUrl',
  'emailLabel',
  'openFailedTitle',
  'openFailedMessage',
]

function assertKeys(value: object, keys: string[], name: string) {
  assert.deepEqual(Object.keys(value).sort(), [...keys].sort(), `${name} 字段必须完整且无多余字段`)
}

function assertNonEmpty(value: unknown, name: string) {
  assert.equal(typeof value, 'string', `${name} 必须是字符串`)
  assert.notEqual((value as string).trim(), '', `${name} 不能为空`)
}

for (const language of LANGUAGES) {
  const policy = getPosPrivacyPolicy(language)
  const name = `pos-privacy-policy.${language}`

  // 1. 两种语言都能经加载器取到，且结构完整。
  assert.equal(policy, language === 'en' ? policyEn : policyZh, `${name} 加载器必须返回对应语言的 JSON`)
  assert.equal(policy.language, language)
  assert.equal(policy.policyVersion, '2026-11-13')
  assertKeys(policy, POLICY_KEYS, name)
  assertKeys(policy.organization, ORGANIZATION_KEYS, `${name}.organization`)
  assertKeys(policy.footer, FOOTER_KEYS, `${name}.footer`)
  for (const key of ['title', 'subtitle', 'effectiveDateLabel', 'effectiveDate', 'summary']) {
    assertNonEmpty((policy as Record<string, unknown>)[key], `${name}.${key}`)
  }
  for (const key of ORGANIZATION_KEYS) {
    assertNonEmpty((policy.organization as Record<string, unknown>)[key], `${name}.organization.${key}`)
  }
  for (const key of FOOTER_KEYS) {
    assertNonEmpty((policy.footer as Record<string, unknown>)[key], `${name}.footer.${key}`)
  }

  // 2. section id 与顺序固定；每节必须有正文，且段落非空。
  assert.deepEqual(policy.sections.map((section) => section.id), EXPECTED_SECTION_IDS, `${name} section id 与顺序`)
  policy.sections.forEach((section, index) => {
    const sectionName = `${name}.sections[${index}]`
    assertKeys(section, SECTION_KEYS, sectionName)
    assertNonEmpty(section.title, `${sectionName}.title`)
    assert.ok(section.paragraphs.length > 0, `${sectionName} 必须有正文段落`)
    section.paragraphs.forEach((paragraph, paragraphIndex) =>
      assertNonEmpty(paragraph, `${sectionName}.paragraphs[${paragraphIndex}]`))
    section.items.forEach((item, itemIndex) => assertNonEmpty(item, `${sectionName}.items[${itemIndex}]`))
    // 标题前缀序号必须与顺序一致（1. ~ 10.）。
    assert.ok(section.title.startsWith(`${index + 1}. `), `${sectionName}.title 序号应为 ${index + 1}.`)
  })

  // 3. 公开地址、负责主体、联系邮箱。
  assert.equal(policy.footer.publicUrl, PUBLIC_URL)
  assert.equal(policy.organization.name, 'HOT BARGAIN INTERNATIONAL PTY LTD')
  assert.equal(policy.organization.email, 'inquiries@hotbargain.com.au')
  assert.ok(policy.footer.openFailedMessage.includes('inquiries@hotbargain.com.au'))

  // 4. 防止占位符上线：正文、标题、页脚等所有文本都不得出现待办标记。
  const text = JSON.stringify(policy)
  assert.ok(!text.includes('【'), `${name} 不得含 【`)
  assert.ok(!text.includes('待确认'), `${name} 不得含 待确认`)
  assert.ok(!/\btodo\b/i.test(text), `${name} 不得含 TODO`)
  assert.ok(!/\btbd\b/i.test(text), `${name} 不得含 TBD`)
}

// 5. 中英文骨架必须一致：段落数与条目数逐节对齐，避免一种语言漏译。
policyEn.sections.forEach((section, index) => {
  const zhSection = policyZh.sections[index]
  assert.equal(zhSection.paragraphs.length, section.paragraphs.length, `${section.id} 段落数中英一致`)
  assert.equal(zhSection.items.length, section.items.length, `${section.id} 条目数中英一致`)
})
assert.equal(policyEn.organization.email, policyZh.organization.email)
assert.equal(policyEn.organization.name, policyZh.organization.name)
assert.equal(policyEn.policyVersion, policyZh.policyVersion)

// 6. 负责主体与联系邮箱必须沿用 /privacy/mobile，避免两张隐私页对外口径不一致。
assert.deepEqual(
  { name: policyEn.organization.name, email: policyEn.organization.email },
  { name: mobilePolicyEn.organization.name, email: mobilePolicyEn.organization.email },
)
assert.deepEqual(
  { label: policyZh.organization.label, contactLabel: policyZh.organization.contactLabel },
  { label: mobilePolicyZh.organization.label, contactLabel: mobilePolicyZh.organization.contactLabel },
)
assert.deepEqual(policyEn.footer.openFailedMessage, mobilePolicyEn.footer.openFailedMessage)
assert.deepEqual(policyZh.footer.openFailedMessage, mobilePolicyZh.footer.openFailedMessage)

// 7. 关键声明（App Store 隐私问卷依赖）两种语言都必须在正文里。
const enText = JSON.stringify(policyEn)
const zhText = JSON.stringify(policyZh)
assert.match(enText, /does not request microphone access/)
assert.match(zhText, /不请求麦克风权限/)
assert.match(enText, /do not sell or rent personal information/)
assert.match(zhText, /不出售或出租个人信息/)
assert.match(enText, /Singapore/)
assert.match(zhText, /新加坡/)
// 境外处理不得只写新加坡：入口还经过内容分发 / 中继节点，支付服务商按自身环境处理（2026-11-12 核对结论）。
assert.match(enText, /content-delivery or relay infrastructure/)
assert.match(zhText, /内容分发或中继设施/)
// 本应用不保存完整卡号：iPad 代码与生产银行交易表近 30 天最多 10 位数字，均为掩码。
assert.match(enText, /does not store full card numbers, card security codes or PINs/)
assert.match(zhText, /不保存完整卡号、卡安全码或 PIN/)
// 诊断只发到我们自己的服务器（生产 EAS 未配置 Sentry DSN）；若以后启用第三方诊断，必须先改政策再发版。
assert.match(enText, /sent to our own servers/)
assert.match(zhText, /发送至我们自己的服务器/)
assert.match(enText, /OAIC/)
assert.match(zhText, /OAIC/)

// 8. 语言解析：中文系统语言显示中文，其余显示英文。
assert.equal(resolvePosPrivacyLanguage('zh-CN'), 'zh')
assert.equal(resolvePosPrivacyLanguage('ZH-hans'), 'zh')
assert.equal(resolvePosPrivacyLanguage('en-AU'), 'en')
assert.equal(resolvePosPrivacyLanguage(''), 'en')
assert.equal(resolvePosPrivacyLanguage(undefined), 'en')

// 9. 登记契约：lazy 路由、免登录白名单、页面品牌链接。
assert.ok(appSource.includes("const PosPrivacyPage = lazy(() => import('./pages/PosPrivacy'))"), 'App.tsx 必须 lazy 引入')
assert.ok(appSource.includes('path="/privacy/pos"'), 'App.tsx 必须注册 /privacy/pos 路由')
assert.ok(appSource.includes('resetKey="pos-privacy"'))
assert.ok(!/^import .*pages\/PosPrivacy/m.test(appSource), '页面不得被静态引入首屏包')
assert.equal(isPublicAppPath('/privacy/pos'), true)
assert.equal(isPublicAppPath('/privacy/pos/'), true)
assert.ok(pageSource.includes('href="/privacy/pos"'))
assert.ok(!pageSource.includes('href="/privacy/mobile"'), '页面品牌链接不得指向 /privacy/mobile')

console.log('pos privacy policy tests passed')
