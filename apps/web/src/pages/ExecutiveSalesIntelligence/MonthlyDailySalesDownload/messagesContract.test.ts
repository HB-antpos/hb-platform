import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 月度日销售下载的文案全部在页面级消息文件里（随页面懒加载，不进首屏 i18n 包），
// 全局 zh.json / en.json 只允许有一个菜单键。拆开后键名写错不会有任何报错，界面会直接显示原始键名，
// 所以在这里静态核对：源码里出现的每个字面量键，中英文都必须存在；两份消息文件键集合一致；没有无人使用的孤立键。

type Json = { [key: string]: Json | string }

function readJson(path: string): Json {
  return JSON.parse(readFileSync(resolve(path), 'utf8')) as Json
}

function hasLeaf(resource: Json, path: string) {
  let node: Json | string | undefined = resource
  for (const segment of path.split('.')) {
    if (typeof node !== 'object' || node === null) return false
    node = node[segment]
  }
  return typeof node === 'string'
}

function flattenKeys(resource: Json, prefix = ''): string[] {
  return Object.entries(resource).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : flattenKeys(value, `${prefix}${key}.`),
  )
}

/** 去掉注释，避免注释里的示例键或中文被当成代码。 */
function stripComments(source: string) {
  return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`])\/\/[^\n]*/g, '$1')
}

const directory = 'src/pages/ExecutiveSalesIntelligence/MonthlyDailySalesDownload'
const NAMESPACE = 'monthlyDailySalesDownload'
const sources = ['index.tsx', 'export.ts', 'logic.ts'].map(name => ({
  name,
  text: stripComments(readFileSync(resolve(directory, name), 'utf8')),
}))

const pageMessages = { zh: readJson(`${directory}/messages.zh.json`), en: readJson(`${directory}/messages.en.json`) }
const globalMessages = { zh: readJson('src/i18n/locales/zh.json'), en: readJson('src/i18n/locales/en.json') }

// 1) 中英文页面消息键集合一致
const zhKeys = flattenKeys(pageMessages.zh).sort()
const enKeys = flattenKeys(pageMessages.en).sort()
if (zhKeys.join('|') !== enKeys.join('|')) {
  const onlyZh = zhKeys.filter(key => !enKeys.includes(key))
  const onlyEn = enKeys.filter(key => !zhKeys.includes(key))
  throw new Error(`页面消息文件中英文键集合不一致。仅中文: ${onlyZh.join(',')}；仅英文: ${onlyEn.join(',')}`)
}
if (zhKeys.length === 0) throw new Error('页面消息文件为空')
if (!zhKeys.every(key => key.startsWith(`${NAMESPACE}.`))) throw new Error('页面消息必须都在 monthlyDailySalesDownload 命名空间下')

// 2) 源码里的字面量键：tr('xxx')（页面里的缩写，自动补命名空间）与完整写法 'monthlyDailySalesDownload.xxx'
const usedKeys = new Set<string>()
for (const { text } of sources) {
  for (const match of text.matchAll(/\btr\(\s*['"`]([A-Za-z0-9_.]+)['"`]/g)) usedKeys.add(`${NAMESPACE}.${match[1]}`)
  for (const match of text.matchAll(new RegExp(`['"\`](${NAMESPACE}\\.[A-Za-z0-9_.]+)['"\`]`, 'g'))) usedKeys.add(match[1])
}
if (usedKeys.size === 0) throw new Error('没有扫描到任何文案键，检查正则或源码路径是否失效')
for (const key of usedKeys) {
  for (const language of ['zh', 'en'] as const) {
    if (!hasLeaf(pageMessages[language], key)) {
      throw new Error(`源码使用了不存在的${language === 'zh' ? '中文' : '英文'}页面文案键: ${key}`)
    }
  }
}

// 3) 页面消息文件里的每个键都被源码用到（没有孤立键，也没有靠动态拼接才能用到的键）
for (const key of zhKeys) {
  if (!usedKeys.has(key)) throw new Error(`页面消息键没有任何源码引用，请删除或补上引用: ${key}`)
}

// 4) 页面注册了页面级消息文件（懒注册，不进首屏包）
const pageSource = sources[0].text
if (!pageSource.includes('registerPageMessages({ zh, en })') || !pageSource.includes("from './messages.zh.json'") || !pageSource.includes("from './messages.en.json'")) {
  throw new Error('页面没有注册页面级消息文件')
}

// 5) 全局语言包只新增菜单键，不能重复定义页面文案
for (const language of ['zh', 'en'] as const) {
  if (!hasLeaf(globalMessages[language], 'menu.monthlyDailySalesDownload')) throw new Error(`全局${language}语言包缺少 menu.monthlyDailySalesDownload`)
  if (NAMESPACE in globalMessages[language]) throw new Error(`页面文案不允许写进全局${language}语言包`)
}

// 6) 页面与导出代码里没有硬编码的中文文案（注释除外）：全部走消息文件
for (const { name, text } of sources) {
  const literal = text.match(/[一-鿿]+/)
  if (literal) throw new Error(`${name} 含有硬编码中文文案「${literal[0]}」，请移入 messages.zh.json`)
}

console.log('monthlyDailySalesDownload messagesContract.test: ok')
