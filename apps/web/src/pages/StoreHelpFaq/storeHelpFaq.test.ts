import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

import { STORE_HELP_FAQ_GROUPS, STORE_HELP_FAQ_PATH, type StoreHelpFaqLanguage, type StoreHelpFaqTab } from './faqContent'
import {
  buildStoreHelpFaqNumbering,
  filterStoreHelpFaqGroups,
  findStoreHelpFaqTabByQuestionId,
  fromStoreHelpFaqAnchor,
  isStoreHelpFaqHeading,
  parseStoreHelpFaqText,
  resolveStoreHelpFaqLanguage,
  resolveStoreHelpFaqTab,
  toStoreHelpFaqAnchor,
} from './faqText'

const appSource = readFileSync('src/App.tsx', 'utf8')
const publicPathSource = readFileSync('src/pages/MobilePrivacy/publicPath.ts', 'utf8')

// 路由：懒加载 + 免登录白名单
assert.equal(STORE_HELP_FAQ_PATH, '/support/faq')
assert.ok(appSource.includes("const StoreHelpFaqPage = lazy(() => import('./pages/StoreHelpFaq'))"))
assert.ok(appSource.includes('path="/support/faq"'))
assert.ok(publicPathSource.includes("'/support/faq'"))

// 记号解析：中文页主显中文并附英文小字，英文页只显示英文
assert.deepEqual(parseStoreHelpFaqText('点 [p:Pay Now|立即付款]。', 'zh'), [
  { kind: 'text', text: '点 ' },
  { kind: 'control', variant: 'p', label: '立即付款', secondary: 'Pay Now' },
  { kind: 'text', text: '。' },
])
assert.deepEqual(parseStoreHelpFaqText('Tap [p:Pay Now|立即付款].', 'en'), [
  { kind: 'text', text: 'Tap ' },
  { kind: 'control', variant: 'p', label: 'Pay Now' },
  { kind: 'text', text: '.' },
])
// 非字母开头的英文（数字、符号、中英混排）不重复附注
assert.deepEqual(parseStoreHelpFaqText('[k:10%]', 'zh'), [{ kind: 'control', variant: 'k', label: '10%' }])
assert.deepEqual(parseStoreHelpFaqText('[k:中 / EN]', 'zh'), [{ kind: 'control', variant: 'k', label: '中 / EN' }])
assert.deepEqual(parseStoreHelpFaqText('[tab:iOS]', 'zh'), [{ kind: 'control', variant: 'tab', label: 'iOS' }])
assert.deepEqual(parseStoreHelpFaqText('[tab:Lay-bys|分期订单]', 'zh'), [
  { kind: 'control', variant: 'tab', label: '分期订单', secondary: 'Lay-bys' },
])

// 内容完整性：中英齐全、id 唯一、记号全部可解析
const tabs = Object.keys(STORE_HELP_FAQ_GROUPS) as StoreHelpFaqTab[]
const languages: StoreHelpFaqLanguage[] = ['en', 'zh']
const seenIds = new Set<string>()
for (const tab of tabs) {
  for (const group of STORE_HELP_FAQ_GROUPS[tab]) {
    assert.ok(group.questions.length > 0, `${tab}/${group.id} 没有问题`)
    for (const question of group.questions) {
      assert.ok(!seenIds.has(question.id), `重复的问题 id: ${question.id}`)
      seenIds.add(question.id)
      assert.equal(question.steps.en.length, question.steps.zh.length, `${question.id} 中英步骤数不一致`)
      assert.equal(question.steps.en.filter(isStoreHelpFaqHeading).length, question.steps.zh.filter(isStoreHelpFaqHeading).length)
      if (question.tips) {
        assert.equal(question.tips.en.length, question.tips.zh.length, `${question.id} 中英提示数不一致`)
      }
      for (const language of languages) {
        assert.ok(question.title[language].trim(), `${question.id} 缺 ${language} 标题`)
        const texts = [
          question.where[language],
          ...question.steps[language],
          ...(question.tips?.[language] ?? []),
          ...(question.related?.[language].map((item) => item.body) ?? []),
        ]
        for (const text of texts) {
          for (const segment of parseStoreHelpFaqText(text, language)) {
            if (segment.kind === 'text') {
              assert.ok(!/\[(k|p|t|d|i|g|tab):/.test(segment.text), `${question.id} 有未解析的记号: ${text}`)
            }
          }
        }
      }
    }
  }
}
const countQuestions = (tab: StoreHelpFaqTab) => STORE_HELP_FAQ_GROUPS[tab].reduce((total, group) => total + group.questions.length, 0)
assert.equal(countQuestions('pos'), 13)
assert.equal(countQuestions('mobile'), 8)

// 编号跨分组连续，过滤后保持原编号
const posNumbering = buildStoreHelpFaqNumbering(STORE_HELP_FAQ_GROUPS.pos)
assert.equal(posNumbering.get('remove'), 1)
assert.equal(posNumbering.get('voucher'), 4)
assert.equal(posNumbering.get('return-scan'), 13)

// 搜索：中英按钮名都能命中，多词需全部命中，空词返回全部
const refundHits = filterStoreHelpFaqGroups(STORE_HELP_FAQ_GROUPS.pos, '退款', 'zh').flatMap((group) => group.questions.map((question) => question.id))
assert.ok(refundHits.includes('refund'))
assert.ok(refundHits.includes('no-receipt-refund'))
const englishLabelHits = filterStoreHelpFaqGroups(STORE_HELP_FAQ_GROUPS.pos, 'whole order', 'zh').flatMap((group) => group.questions.map((question) => question.id))
assert.ok(englishLabelHits.includes('order-discount'))
assert.deepEqual(filterStoreHelpFaqGroups(STORE_HELP_FAQ_GROUPS.mobile, 'zzz-no-match', 'en'), [])
assert.equal(filterStoreHelpFaqGroups(STORE_HELP_FAQ_GROUPS.mobile, '   ', 'en'), STORE_HELP_FAQ_GROUPS.mobile)

// 语言与标签解析
assert.equal(resolveStoreHelpFaqLanguage('en', 'zh-CN'), 'en')
assert.equal(resolveStoreHelpFaqLanguage(null, 'zh-CN'), 'zh')
assert.equal(resolveStoreHelpFaqLanguage('fr', 'en-AU'), 'en')
assert.equal(resolveStoreHelpFaqTab('mobile'), 'mobile')
assert.equal(resolveStoreHelpFaqTab('other'), 'pos')

// 锚点：分享链接能定位到问题所在标签
assert.equal(toStoreHelpFaqAnchor('daily-closes'), 'faq-daily-closes')
assert.equal(fromStoreHelpFaqAnchor('#faq-daily-closes'), 'daily-closes')
assert.equal(fromStoreHelpFaqAnchor('#other'), undefined)
assert.equal(findStoreHelpFaqTabByQuestionId(STORE_HELP_FAQ_GROUPS, 'daily-closes'), 'mobile')
assert.equal(findStoreHelpFaqTabByQuestionId(STORE_HELP_FAQ_GROUPS, 'lay-by-continue'), 'pos')
assert.equal(findStoreHelpFaqTabByQuestionId(STORE_HELP_FAQ_GROUPS, 'missing'), undefined)

console.log('store help FAQ tests passed')
