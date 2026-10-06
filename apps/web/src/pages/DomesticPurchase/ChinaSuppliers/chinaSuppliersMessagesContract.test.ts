import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 国内供应商页的文案被拆成两处：既有键在全局 zh.json / en.json，
// 重设计新增的键在页面级消息文件里（随页面懒加载，不占首屏 i18n 包）。
// 拆开后键名写错不会有任何报错，界面会直接显示原始键名，所以在这里做静态核对。

type Json = { [key: string]: Json | string }

function readJson(path: string): Json {
  return JSON.parse(readFileSync(resolve(path), 'utf8')) as Json
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

function placeholders(text: string): string {
  return [...text.matchAll(/\{\{\s*([A-Za-z0-9_]+)\s*\}\}/g)].map((match) => match[1]).sort().join(',')
}

const pageDirectory = 'src/pages/DomesticPurchase/ChinaSuppliers'
const sources = ['index.tsx', 'SupplierFormModal.tsx', 'SyncResultModal.tsx']
const registerSource = `${pageDirectory}/index.tsx`
const messageFiles = {
  zh: `${pageDirectory}/chinaSuppliersMessages.zh.json`,
  en: `${pageDirectory}/chinaSuppliersMessages.en.json`,
}

const globalMessages = { zh: readJson('src/i18n/locales/zh.json'), en: readJson('src/i18n/locales/en.json') }
const pageMessages = { zh: readJson(messageFiles.zh), en: readJson(messageFiles.en) }

// 1) 中英文页面消息文件的键集合必须一致，否则切换语言时会有一边缺文案。
const zhKeys = flattenKeys(pageMessages.zh).sort()
const enKeys = flattenKeys(pageMessages.en).sort()
if (zhKeys.join('|') !== enKeys.join('|')) {
  const onlyZh = zhKeys.filter((key) => !enKeys.includes(key))
  const onlyEn = enKeys.filter((key) => !zhKeys.includes(key))
  throw new Error(`国内供应商页面消息文件中英文键集合不一致。仅中文: ${onlyZh.join(',')}；仅英文: ${onlyEn.join(',')}`)
}
if (zhKeys.length === 0) {
  throw new Error('国内供应商页面消息文件是空的')
}

// 2) 页面里必须真的注册了这两份消息文件，否则新增文案在运行时全部缺失。
const registerText = readFileSync(resolve(registerSource), 'utf8')
for (const expected of [
  'registerPageMessages({ zh: chinaSuppliersMessagesZh, en: chinaSuppliersMessagesEn })',
  "from './chinaSuppliersMessages.zh.json'",
  "from './chinaSuppliersMessages.en.json'",
]) {
  if (!registerText.includes(expected)) {
    throw new Error(`国内供应商页面没有注册页面级消息文件，缺少: ${expected}`)
  }
}

// 3) 源码里出现的每个字面量键（t('a.b') 形式），在「全局 ∪ 页面消息」里中英文都必须存在。
//    不限命名空间：连 common.* 之类的全局键也一起核对，写错一个字母就能立刻发现。
const keyPattern = /\bt\(\s*['"`]([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)['"`]/g
const usedKeys = new Set<string>()
for (const source of sources) {
  const text = readFileSync(resolve(`${pageDirectory}/${source}`), 'utf8')
  for (const match of text.matchAll(keyPattern)) {
    usedKeys.add(match[1])
  }
}
if (usedKeys.size === 0) {
  throw new Error('国内供应商页面没有扫描到任何文案键，检查正则或源码路径是否失效')
}

for (const key of usedKeys) {
  for (const language of ['zh', 'en'] as const) {
    if (getLeaf(globalMessages[language], key) === undefined && getLeaf(pageMessages[language], key) === undefined) {
      throw new Error(`国内供应商页面使用了不存在的${language === 'zh' ? '中文' : '英文'}文案键: ${key}`)
    }
  }
}

// 4) 页面消息文件不应与全局语言包重复定义同一个键（后注册的不会覆盖前者，重复只会误导）。
for (const key of zhKeys) {
  for (const language of ['zh', 'en'] as const) {
    if (getLeaf(globalMessages[language], key) !== undefined) {
      throw new Error(`国内供应商页面消息文件（${language}）重复定义了全局已有的键: ${key}`)
    }
  }
}

// 5) 页面消息文件里不留没人用的键，避免文案越堆越多。
for (const key of zhKeys) {
  if (!usedKeys.has(key)) {
    throw new Error(`国内供应商页面消息文件里的键没有被任何源码引用: ${key}`)
  }
}

// 6) 同一个键的中英文插值占位符必须一致（{{count}} 写成 {{total}} 之类的错误不会有任何报错）。
for (const key of zhKeys) {
  const zh = getLeaf(pageMessages.zh, key) ?? ''
  const en = getLeaf(pageMessages.en, key) ?? ''
  if (placeholders(zh) !== placeholders(en)) {
    throw new Error(`国内供应商页面消息 ${key} 的中英文插值占位符不一致: zh=[${placeholders(zh)}] en=[${placeholders(en)}]`)
  }
}

console.log('ChinaSuppliers chinaSuppliersMessagesContract.test: ok')
