import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 节日销售报告的文案全部在页面级消息文件里（随页面懒加载，不进首屏 i18n 包），
// 全局 zh.json / en.json 只允许有一个菜单键。拆开后键名写错不会有任何报错，界面会直接显示原始键名，
// 所以在这里静态核对：tr('xxx') 里的字面量键中英文都必须存在；两份消息文件键集合一致；没有无人使用的孤立键。
// 少数键由字面量映射表（CATEGORY_LABEL_KEY 等）间接使用，按「源码里出现过的任何字符串字面量」认定为已使用。

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

const directory = 'src/pages/ExecutiveSalesIntelligence/HolidaySalesReport'
const NAMESPACE = 'holidaySalesReport'
const sources = ['index.tsx', 'Sections.tsx', 'Drawers.tsx', 'shared.tsx', 'logic.ts'].map(name => ({
  name,
  text: stripComments(readFileSync(resolve(directory, name), 'utf8')),
}))

const pageMessages = { zh: readJson(`${directory}/messages.zh.json`), en: readJson(`${directory}/messages.en.json`) }
const globalMessages = { zh: readJson('src/i18n/locales/zh.json'), en: readJson('src/i18n/locales/en.json') }

// 1) 中英文页面消息键集合一致，且都在命名空间下
const zhKeys = flattenKeys(pageMessages.zh).sort()
const enKeys = flattenKeys(pageMessages.en).sort()
if (zhKeys.join('|') !== enKeys.join('|')) {
  const onlyZh = zhKeys.filter(key => !enKeys.includes(key))
  const onlyEn = enKeys.filter(key => !zhKeys.includes(key))
  throw new Error(`页面消息文件中英文键集合不一致。仅中文: ${onlyZh.join(',')}；仅英文: ${onlyEn.join(',')}`)
}
if (zhKeys.length === 0) throw new Error('页面消息文件为空')
if (!zhKeys.every(key => key.startsWith(`${NAMESPACE}.`))) throw new Error(`页面消息必须都在 ${NAMESPACE} 命名空间下`)

// 2) tr('xxx') 里的字面量键必须存在（页面里的缩写，自动补命名空间）
const trKeys = new Set<string>()
for (const { text } of sources) {
  for (const match of text.matchAll(/\btr\(\s*['"`]([A-Za-z0-9_.]+)['"`]/g)) trKeys.add(`${NAMESPACE}.${match[1]}`)
}
if (trKeys.size === 0) throw new Error('没有扫描到任何文案键，检查正则或源码路径是否失效')
for (const key of trKeys) {
  for (const language of ['zh', 'en'] as const) {
    if (!hasLeaf(pageMessages[language], key)) {
      throw new Error(`源码使用了不存在的${language === 'zh' ? '中文' : '英文'}页面文案键: ${key}`)
    }
  }
}
// tr(`xxx.${变量}`) 这种动态拼接会让静态核对失效，必须改成字面量映射表
for (const { name, text } of sources) {
  if (/\btr\(\s*`[^`]*\$\{/.test(text)) throw new Error(`${name} 用模板字符串动态拼接了文案键，请改用字面量映射表`)
}

// 3) 每个页面消息键都被源码用到（直接 tr('…')，或作为字面量出现在映射表里）
const literals = new Set<string>()
for (const { text } of sources) {
  for (const match of text.matchAll(/['"`]([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)['"`]/g)) literals.add(`${NAMESPACE}.${match[1]}`)
}
for (const key of zhKeys) {
  if (!trKeys.has(key) && !literals.has(key)) throw new Error(`页面消息键没有任何源码引用，请删除或补上引用: ${key}`)
}

// 4) 页面注册了页面级消息文件（懒注册，不进首屏包）
const pageSource = sources[0].text
if (!pageSource.includes('registerPageMessages({ zh, en })') || !pageSource.includes("from './messages.zh.json'") || !pageSource.includes("from './messages.en.json'")) {
  throw new Error('页面没有注册页面级消息文件')
}

// 5) 全局语言包只新增菜单键，不能重复定义页面文案
for (const language of ['zh', 'en'] as const) {
  if (!hasLeaf(globalMessages[language], 'menu.holidaySalesReport')) throw new Error(`全局${language}语言包缺少 menu.holidaySalesReport`)
  if (NAMESPACE in globalMessages[language]) throw new Error(`页面文案不允许写进全局${language}语言包`)
}

// 6) 页面代码里没有硬编码的中文文案（注释除外）：全部走消息文件
for (const { name, text } of sources) {
  const literal = text.match(/[一-鿿]+/)
  if (literal) throw new Error(`${name} 含有硬编码中文文案「${literal[0]}」，请移入 messages.zh.json`)
}

console.log('holidaySalesReport messagesContract.test: ok')
