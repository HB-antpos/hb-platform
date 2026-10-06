import { readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'

// 国内商品页的文案被拆成两处：既有键在全局 zh.json / en.json（domesticProducts.* 与 common.*），
// 重设计新增的键在页面级消息文件里（随页面懒加载，不占首屏 i18n 包）。
// 拆开后键名写错不会有任何报错，界面会直接显示原始键名，所以在这里做静态核对。

type Json = { [key: string]: Json | string }

const pageDirectory = 'src/pages/DomesticPurchase/DomesticProducts'

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

function flatten(resource: Json, prefix = ''): Record<string, string> {
  const result: Record<string, string> = {}
  for (const [key, value] of Object.entries(resource)) {
    if (typeof value === 'string') {
      result[`${prefix}${key}`] = value
    } else {
      Object.assign(result, flatten(value, `${prefix}${key}.`))
    }
  }
  return result
}

function placeholders(text: string) {
  return Array.from(text.matchAll(/\{\{\s*([A-Za-z0-9_]+)\s*\}\}/g), (match) => match[1]).sort().join(',')
}

const globalMessages = { zh: readJson('src/i18n/locales/zh.json'), en: readJson('src/i18n/locales/en.json') }
const pageMessages = {
  zh: readJson(`${pageDirectory}/domesticProductsMessages.zh.json`),
  en: readJson(`${pageDirectory}/domesticProductsMessages.en.json`),
}
const pageFlat = { zh: flatten(pageMessages.zh), en: flatten(pageMessages.en) }

// 1) 中英文页面消息文件的键集合必须一致，否则切换语言时会有一边缺文案。
const zhKeys = Object.keys(pageFlat.zh).sort()
const enKeys = Object.keys(pageFlat.en).sort()
if (zhKeys.join('|') !== enKeys.join('|')) {
  const onlyZh = zhKeys.filter((key) => !enKeys.includes(key))
  const onlyEn = enKeys.filter((key) => !zhKeys.includes(key))
  throw new Error(`国内商品页面消息文件中英文键集合不一致。仅中文: ${onlyZh.join(',')}；仅英文: ${onlyEn.join(',')}`)
}

// 2) 同一个键中英文占位符（{{count}} 之类）必须一致，否则一种语言会显示出原始占位符。
for (const key of zhKeys) {
  if (placeholders(pageFlat.zh[key]) !== placeholders(pageFlat.en[key])) {
    throw new Error(`页面消息 ${key} 中英文占位符不一致`)
  }
}

// 3) 页面里必须真的注册了这两份消息文件，否则新增文案在运行时全部缺失。
const indexSource = readFileSync(resolve(`${pageDirectory}/index.tsx`), 'utf8')
if (
  !indexSource.includes('registerPageMessages(') ||
  !indexSource.includes("from './domesticProductsMessages.zh.json'") ||
  !indexSource.includes("from './domesticProductsMessages.en.json'")
) {
  throw new Error('国内商品页面没有注册页面级消息文件')
}

// 4) 源码里 t('...') 的每个字面量键，在「全局 ∪ 页面消息」里中英文都必须存在。
const sourceFiles = readdirSync(resolve(pageDirectory)).filter(
  (name) => /\.tsx?$/.test(name) && !/\.test\.tsx?$/.test(name),
)
const keyPattern = /\bt\(\s*['"`]([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)['"`]/g
const usedKeys = new Set<string>()
for (const file of sourceFiles) {
  const text = readFileSync(resolve(`${pageDirectory}/${file}`), 'utf8')
  for (const match of text.matchAll(keyPattern)) {
    usedKeys.add(match[1])
  }
}
if (usedKeys.size === 0) {
  throw new Error('国内商品页没有扫描到任何文案键，检查正则或源码路径是否失效')
}
for (const key of usedKeys) {
  for (const language of ['zh', 'en'] as const) {
    if (getLeaf(globalMessages[language], key) === undefined && pageFlat[language][key] === undefined) {
      throw new Error(`国内商品页使用了不存在的${language === 'zh' ? '中文' : '英文'}文案键: ${key}`)
    }
  }
}

// 5) 页面消息文件不应与全局语言包重复定义同一个键（后注册的不会覆盖前者，重复只会误导）。
for (const key of zhKeys) {
  for (const language of ['zh', 'en'] as const) {
    if (getLeaf(globalMessages[language], key) !== undefined) {
      throw new Error(`国内商品页面消息文件重复定义了全局已有的${language === 'zh' ? '中文' : '英文'}键: ${key}`)
    }
  }
}

// 6) 页面消息里每个键都必须被源码引用，避免留下死键白白占体积。
const allSource = sourceFiles.map((file) => readFileSync(resolve(`${pageDirectory}/${file}`), 'utf8')).join('\n')
for (const key of zhKeys) {
  if (!allSource.includes(`'${key}'`) && !allSource.includes(`"${key}"`) && !allSource.includes(`\`${key}\``)) {
    throw new Error(`国内商品页面消息文件里的键 ${key} 没有被任何源码引用`)
  }
}

console.log('domesticProductsMessagesContract.test: ok')
