import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 前缀管理页、共用前缀表单、展开行面板和创建页里的「管理前缀」弹窗的文案被拆成两处：
// 既有键在全局 zh.json / en.json，重设计新增的键在页面级消息文件里（随页面懒加载，不占首屏 i18n 包）。
// 拆开后键名写错不会有任何报错，界面会直接显示原始键名，所以在这里做静态核对。

type Json = { [key: string]: Json | string }

function readJson(path: string): Json {
  return JSON.parse(readFileSync(resolve(path), 'utf8')) as Json
}

function hasLeaf(resource: Json, path: string) {
  return getLeaf(resource, path) !== undefined
}

function getLeaf(resource: Json, path: string): string | undefined {
  let node: Json | string | undefined = resource
  for (const segment of path.split('.')) {
    if (typeof node !== 'object' || node === null) {
      return undefined
    }
    node = node[segment]
  }
  return typeof node === 'string' ? node : undefined
}

function flattenKeys(resource: Json, prefix = ''): string[] {
  return Object.entries(resource).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : flattenKeys(value, `${prefix}${key}.`),
  )
}

const pageDirectory = 'src/pages/DomesticPurchase/ProductPrefixCodeManagement'
const sources = [
  `${pageDirectory}/index.tsx`,
  `${pageDirectory}/PrefixCodeFormModal.tsx`,
  `${pageDirectory}/PrefixProductsPanel.tsx`,
  // 创建页里的「管理前缀」弹窗复用同一份表单与文案。
  'src/pages/DomesticPurchase/ProductCreation/PrefixCodeManageModal.tsx',
]
// 共用表单组件负责注册页面级消息：页面与创建页弹窗都经由它拿到文案。
const registerSource = `${pageDirectory}/PrefixCodeFormModal.tsx`

const globalMessages = { zh: readJson('src/i18n/locales/zh.json'), en: readJson('src/i18n/locales/en.json') }
const pageMessages = {
  zh: readJson(`${pageDirectory}/prefixCodeMessages.zh.json`),
  en: readJson(`${pageDirectory}/prefixCodeMessages.en.json`),
}

// 1) 中英文页面消息文件的键集合必须一致，否则切换语言时会有一边缺文案。
const zhKeys = flattenKeys(pageMessages.zh).sort()
const enKeys = flattenKeys(pageMessages.en).sort()
if (zhKeys.join('|') !== enKeys.join('|')) {
  const onlyZh = zhKeys.filter((key) => !enKeys.includes(key))
  const onlyEn = enKeys.filter((key) => !zhKeys.includes(key))
  throw new Error(`前缀页面消息文件中英文键集合不一致。仅中文: ${onlyZh.join(',')}；仅英文: ${onlyEn.join(',')}`)
}

// 2) 共用表单里必须真的注册了这两份消息文件，且页面与创建页弹窗都依赖这个共用表单（否则文案运行时缺失）。
const registerText = readFileSync(resolve(registerSource), 'utf8')
if (
  !registerText.includes('registerPageMessages(') ||
  !registerText.includes("from './prefixCodeMessages.zh.json'") ||
  !registerText.includes("from './prefixCodeMessages.en.json'")
) {
  throw new Error('PrefixCodeFormModal 没有注册前缀页面级消息文件')
}
const pageText = readFileSync(resolve(sources[0]), 'utf8')
if (!pageText.includes("from './PrefixCodeFormModal'")) {
  throw new Error('前缀管理页没有引用共用的 PrefixCodeFormModal，页面级消息不会被注册')
}
const manageModalText = readFileSync(resolve(sources[3]), 'utf8')
if (!manageModalText.includes("from '../ProductPrefixCodeManagement/PrefixCodeFormModal'")) {
  throw new Error('创建页的 PrefixCodeManageModal 没有复用共用的 PrefixCodeFormModal')
}

// 3) 源码里出现的每个字面量键，在「全局 ∪ 页面消息」里中英文都必须存在。
const keyPattern = /\bt\(\s*['"`]([A-Za-z0-9_.]+)['"`]/g
const usedKeys = new Set<string>()
for (const source of sources) {
  const text = readFileSync(resolve(source), 'utf8')
  for (const match of text.matchAll(keyPattern)) {
    usedKeys.add(match[1])
  }
}
if (usedKeys.size === 0) {
  throw new Error('没有扫描到任何文案键，检查正则或源码路径是否失效')
}

for (const key of usedKeys) {
  for (const language of ['zh', 'en'] as const) {
    if (!hasLeaf(globalMessages[language], key) && !hasLeaf(pageMessages[language], key)) {
      throw new Error(`使用了不存在的${language === 'zh' ? '中文' : '英文'}文案键: ${key}`)
    }
  }
}

// 4) 页面消息文件不应与全局语言包重复定义同一个键（后注册的不会覆盖前者，重复只会误导）。
for (const key of zhKeys) {
  if (hasLeaf(globalMessages.zh, key) || hasLeaf(globalMessages.en, key)) {
    throw new Error(`前缀页面消息文件重复定义了全局已有的键: ${key}`)
  }
}

// 5) 页面消息里不能有孤立键：源码已不再使用的键应当删掉，避免消息文件越积越多。
for (const key of zhKeys) {
  if (!usedKeys.has(key)) {
    throw new Error(`前缀页面消息文件里的键没有被任何源码引用: ${key}`)
  }
}

// 6) 英文界面不得出现中文：旧的全局 prefixCode.* 英文值曾经全是中文。
const hasHan = /[一-鿿]/
const globalPrefixEnglish = (globalMessages.en.prefixCode ?? {}) as Json
for (const key of flattenKeys(globalPrefixEnglish)) {
  const value = getLeaf(globalPrefixEnglish, key)
  if (value && hasHan.test(value)) {
    throw new Error(`全局英文文案 prefixCode.${key} 仍是中文: ${value}`)
  }
}
for (const key of enKeys) {
  const value = getLeaf(pageMessages.en, key)
  if (value && hasHan.test(value)) {
    throw new Error(`页面英文文案 ${key} 含中文: ${value}`)
  }
}

// 7) 状态文案统一用「停用」，不再出现「禁用」。
for (const key of zhKeys) {
  if (getLeaf(pageMessages.zh, key)?.includes('禁用')) {
    throw new Error(`页面中文文案 ${key} 应使用「停用」而不是「禁用」`)
  }
}
for (const key of usedKeys) {
  if (getLeaf(globalMessages.zh, key)?.includes('禁用')) {
    throw new Error(`前缀页面引用的全局键 ${key} 中文值含「禁用」，应统一为「停用」`)
  }
}

console.log('prefixCodeMessagesContract.test: ok')
