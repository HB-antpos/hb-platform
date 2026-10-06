import { readFileSync, readdirSync } from 'node:fs'
import { join, resolve } from 'node:path'

// 货号条码创建页的文案被拆成两处：既有键在全局 zh.json / en.json，
// 重设计新增的键在页面级消息文件里（随页面懒加载，不占首屏 i18n 包）。
// 拆开后键名写错不会有任何报错，界面会直接显示原始键名，所以在这里做静态核对。

type Json = { [key: string]: Json | string }

function readJson(path: string): Json {
  return JSON.parse(readFileSync(resolve(path), 'utf8')) as Json
}

function hasLeaf(resource: Json, path: string) {
  let node: Json | string | undefined = resource
  for (const segment of path.split('.')) {
    if (typeof node !== 'object' || node === null) {
      return false
    }
    node = node[segment]
  }
  return typeof node === 'string'
}

function flattenKeys(resource: Json, prefix = ''): string[] {
  return Object.entries(resource).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : flattenKeys(value, `${prefix}${key}.`),
  )
}

const pageDirectory = 'src/pages/DomesticPurchase/ProductCreation'
const globalMessages = { zh: readJson('src/i18n/locales/zh.json'), en: readJson('src/i18n/locales/en.json') }
const pageMessages = {
  zh: readJson(`${pageDirectory}/productCreationMessages.zh.json`),
  en: readJson(`${pageDirectory}/productCreationMessages.en.json`),
}

// 1) 中英文页面消息文件的键集合必须一致，否则切换语言时会有一边缺文案。
const zhKeys = flattenKeys(pageMessages.zh).sort()
const enKeys = flattenKeys(pageMessages.en).sort()
if (zhKeys.join('|') !== enKeys.join('|')) {
  const onlyZh = zhKeys.filter((key) => !enKeys.includes(key))
  const onlyEn = enKeys.filter((key) => !zhKeys.includes(key))
  throw new Error(`货号条码创建页面消息文件中英文键集合不一致。仅中文: ${onlyZh.join(',')}；仅英文: ${onlyEn.join(',')}`)
}

// 2) 页面里必须真的注册了这两份消息文件，否则新增文案在运行时全部缺失。
const indexSource = readFileSync(resolve(`${pageDirectory}/index.tsx`), 'utf8')
if (
  !indexSource.includes('registerPageMessages(')
  || !indexSource.includes("from './productCreationMessages.zh.json'")
  || !indexSource.includes("from './productCreationMessages.en.json'")
) {
  throw new Error('货号条码创建页面没有注册页面级消息文件')
}

// 3) 本页自己的源码（不含前缀管理弹窗，它另有归属）里出现的每个字面量键，
//    在「全局 ∪ 页面消息」里中英文都必须存在。既扫 t('x.y') 调用，也扫带已知命名空间的字符串字面量（如配置表里的键）。
const sourceFiles = readdirSync(resolve(pageDirectory))
  .filter((name) => /\.tsx?$/.test(name) && !/\.test\.tsx?$/.test(name) && name !== 'PrefixCodeManageModal.tsx')
  .map((name) => join(pageDirectory, name))

const translateCallPattern = /\bt\??\.?\(\s*['"`]([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)['"`]/g
const namespacedLiteralPattern = /['"`]((?:productCreation|common|domesticProducts|chinaSuppliers|productImport|menu|column)\.[A-Za-z0-9_.]+)['"`]/g

const usedKeys = new Set<string>()
for (const source of sourceFiles) {
  const text = readFileSync(resolve(source), 'utf8')
  for (const pattern of [translateCallPattern, namespacedLiteralPattern]) {
    for (const match of text.matchAll(pattern)) {
      usedKeys.add(match[1])
    }
  }
}
if (usedKeys.size < 50) {
  throw new Error(`货号条码创建只扫描到 ${usedKeys.size} 个文案键，检查正则或源码路径是否失效`)
}

for (const key of usedKeys) {
  for (const language of ['zh', 'en'] as const) {
    if (!hasLeaf(globalMessages[language], key) && !hasLeaf(pageMessages[language], key)) {
      throw new Error(`货号条码创建使用了不存在的${language === 'zh' ? '中文' : '英文'}文案键: ${key}`)
    }
  }
}

// 4) 页面消息文件不应与全局语言包重复定义同一个键（重复只会白白占体积，且已有键不会被覆盖，容易误导）。
for (const key of zhKeys) {
  for (const language of ['zh', 'en'] as const) {
    if (hasLeaf(globalMessages[language], key)) {
      throw new Error(`货号条码创建页面消息文件重复定义了全局已有的${language === 'zh' ? '中文' : '英文'}键: ${key}`)
    }
  }
}

// 5) 页面消息文件里的每个键都应被源码使用，避免改版后留下无人引用的死键。
for (const key of zhKeys) {
  if (!usedKeys.has(key)) {
    throw new Error(`货号条码创建页面消息文件里的键没有被任何源码引用: ${key}`)
  }
}

console.log('productCreationMessagesContract.test: ok')
