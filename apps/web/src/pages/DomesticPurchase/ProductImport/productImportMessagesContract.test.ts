import { readdirSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 商品导入页的文案分成两处：既有键在全局 zh.json / en.json，重设计新增的键在页面级消息文件里
// （随页面代码块懒加载，不占首屏 i18n 包）。拆开后键名写错不会有任何报错，界面会直接显示原始键名，
// 所以在这里做静态核对。

type Json = { [key: string]: Json | string }

const pageDirectory = 'src/pages/DomesticPurchase/ProductImport'

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

function flattenEntries(resource: Json, prefix = ''): Array<[string, string]> {
  return Object.entries(resource).flatMap(([key, value]) =>
    typeof value === 'string' ? [[`${prefix}${key}`, value] as [string, string]] : flattenEntries(value, `${prefix}${key}.`),
  )
}

const globalMessages = { zh: readJson('src/i18n/locales/zh.json'), en: readJson('src/i18n/locales/en.json') }
const pageMessages = {
  zh: readJson(`${pageDirectory}/productImportMessages.zh.json`),
  en: readJson(`${pageDirectory}/productImportMessages.en.json`),
}

// 1) 中英文页面消息文件的键集合必须一致，否则切换语言时会有一边缺文案。
const zhEntries = flattenEntries(pageMessages.zh)
const enEntries = flattenEntries(pageMessages.en)
const zhKeys = zhEntries.map(([key]) => key).sort()
const enKeys = enEntries.map(([key]) => key).sort()
if (zhKeys.join('|') !== enKeys.join('|')) {
  const onlyZh = zhKeys.filter((key) => !enKeys.includes(key))
  const onlyEn = enKeys.filter((key) => !zhKeys.includes(key))
  throw new Error(`商品导入页面消息文件中英文键集合不一致。仅中文: ${onlyZh.join(',')}；仅英文: ${onlyEn.join(',')}`)
}
if (zhKeys.length === 0) {
  throw new Error('商品导入页面消息文件是空的')
}

// 英文界面不能混入中文（字面量漏翻时用户会在英文界面看到中文）
for (const [key, value] of enEntries) {
  if (/[㐀-鿿]/.test(value)) {
    throw new Error(`英文页面消息含中文字符: ${key}`)
  }
}

// 2) 页面里必须真的注册了这两份消息文件，否则新增文案在运行时全部缺失。
const pageSource = readFileSync(resolve(`${pageDirectory}/index.tsx`), 'utf8')
if (
  !pageSource.includes('registerPageMessages({ zh: productImportMessagesZh, en: productImportMessagesEn })')
  || !pageSource.includes("from './productImportMessages.zh.json'")
  || !pageSource.includes("from './productImportMessages.en.json'")
) {
  throw new Error('商品导入页面没有注册页面级消息文件')
}

// 3) 源码里出现的每个字面量键，在「全局 ∪ 页面消息」里中英文都必须存在。
//    扫描目录下全部非测试源码：既匹配 t('xxx.yyy') 调用，也匹配 productImport. / common. / domesticProducts. 打头的字符串字面量
//    （utils.ts 里的确认文案键、步骤条 / 统计条里的键表都是以字面量形式给出的）。
const sourceFiles = readdirSync(resolve(pageDirectory)).filter((name) => /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name))
const usedKeys = new Set<string>()
const literalPageKeys = new Set<string>()
const callPattern = /\bt\(\s*['"`]([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)['"`]/g
const literalPattern = /['"`]((?:productImport|common|domesticProducts)\.[A-Za-z0-9_.]+)['"`]/g
for (const name of sourceFiles) {
  const text = readFileSync(resolve(`${pageDirectory}/${name}`), 'utf8')
  for (const match of text.matchAll(callPattern)) usedKeys.add(match[1])
  for (const match of text.matchAll(literalPattern)) {
    usedKeys.add(match[1])
    literalPageKeys.add(match[1])
  }
}
if (usedKeys.size === 0) {
  throw new Error('商品导入页没有扫描到任何文案键，检查正则或源码路径是否失效')
}

for (const key of usedKeys) {
  for (const language of ['zh', 'en'] as const) {
    if (!hasLeaf(globalMessages[language], key) && !hasLeaf(pageMessages[language], key)) {
      throw new Error(`商品导入页使用了不存在的${language === 'zh' ? '中文' : '英文'}文案键: ${key}`)
    }
  }
}

// 4) 页面消息文件不应与全局语言包重复定义同一个键（重复只会白白占体积，且后注册的不会覆盖前者，容易误导）。
for (const key of zhKeys) {
  for (const language of ['zh', 'en'] as const) {
    if (hasLeaf(globalMessages[language], key)) {
      throw new Error(`商品导入页面消息文件重复定义了全局已有的键: ${key}`)
    }
  }
}

// 5) 页面消息文件里的每个键都必须在源码里被引用（否则是没人用的死文案，白占体积）。
for (const key of zhKeys) {
  if (!literalPageKeys.has(key) && !usedKeys.has(key)) {
    throw new Error(`商品导入页面消息文件里的键没有任何引用: ${key}`)
  }
}

console.log('productImportMessagesContract.test: ok')
