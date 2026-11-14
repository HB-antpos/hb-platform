import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// 订单「装箱情况」与完成出库提醒的文案放在 packProgress/messages.{zh,en}.json（storeOrders.packProgress.*），
// 随懒加载代码块注册，不进首屏包。拆成全局 + 页面两处后，键名写错不会报错、界面直接显示原始键名，所以这里做静态核对；
// 同时守住包体预算：详情页只能用动态 import 引入这个代码块。

type Json = { [key: string]: Json | string }

const ROOT = resolve('src/pages/Warehouse/StoreOrders/packProgress')
const PREFIX = 'storeOrders.packProgress.'

function readJson(path: string): Json {
  return JSON.parse(readFileSync(path, 'utf8')) as Json
}

function flatten(resource: Json, prefix = ''): Record<string, string> {
  return Object.entries(resource).reduce<Record<string, string>>((result, [key, value]) => {
    if (typeof value === 'string') {
      result[`${prefix}${key}`] = value
    } else {
      Object.assign(result, flatten(value, `${prefix}${key}.`))
    }
    return result
  }, {})
}

function assert(condition: boolean, message: string) {
  if (!condition) {
    throw new Error(message)
  }
}

const placeholders = (text: string) => [...text.matchAll(/\{\{\s*(\w+)\s*\}\}/g)].map((match) => match[1]).sort().join(',')

const zh = flatten(readJson(join(ROOT, 'messages.zh.json')))
const en = flatten(readJson(join(ROOT, 'messages.en.json')))

// 1) 中英文键集合一致、没有空文案、插值变量一致，且都在 storeOrders.packProgress 命名空间下。
assert(Object.keys(zh).sort().join('|') === Object.keys(en).sort().join('|'), '中英文键集合不一致')
for (const [language, resource] of [['zh', zh], ['en', en]] as const) {
  for (const [key, value] of Object.entries(resource)) {
    assert(value.trim().length > 0, `${language} 文案为空: ${key}`)
    assert(key.startsWith('storeOrders.packProgress.'), `${language} 文案不在 storeOrders.packProgress 命名空间: ${key}`)
  }
}
for (const key of Object.keys(zh)) {
  assert(placeholders(zh[key]) === placeholders(en[key]), `中英文插值变量不一致: ${key}`)
}

// 2) 全局语言包（在首屏包里）不得定义这个命名空间。
for (const language of ['zh', 'en']) {
  const globalMessages = readJson(resolve(`src/i18n/locales/${language}.json`))
  const storeOrders = (globalMessages.storeOrders ?? {}) as Json
  assert(!('packProgress' in storeOrders), `全局 ${language} 语言包不应定义 storeOrders.packProgress`)
}

// 3) 懒加载代码块里注册了这两份文案，且组件引入了它。
const messagesSource = readFileSync(join(ROOT, 'messages.ts'), 'utf8')
assert(
  messagesSource.includes('registerPageMessages(') && messagesSource.includes("'./messages.zh.json'") && messagesSource.includes("'./messages.en.json'"),
  'messages.ts 必须用 registerPageMessages 注册中英文文案',
)
const componentSource = readFileSync(join(ROOT, 'PackProgressSection.tsx'), 'utf8')
assert(componentSource.includes("import './messages'"), 'PackProgressSection 必须引入 ./messages 注册文案')

// 4) 源码里出现的每个字面量键中英文都存在；动态拼接的键（箱状态）单独核对。
const usedKeys = new Set<string>()
for (const match of componentSource.matchAll(/['"`]storeOrders\.packProgress\.([A-Za-z0-9_.]*[A-Za-z0-9_])['"`]/g)) {
  usedKeys.add(`${PREFIX}${match[1]}`)
}
assert(usedKeys.size > 20, `源码里的文案键太少（${usedKeys.size}），检查命名空间或源码路径`)
for (const key of usedKeys) {
  assert(key in zh && key in en, `缺少中英文文案键: ${key}`)
}
for (const kind of ['packing', 'sealed', 'transit', 'unknown']) {
  assert(`${PREFIX}status.${kind}` in zh && `${PREFIX}status.${kind}` in en, `缺少箱状态文案: status.${kind}`)
}
// 每个定义了的键都被用到（避免留下死键增加包体）。
for (const key of Object.keys(zh)) {
  assert(usedKeys.has(key) || key.startsWith(`${PREFIX}status.`), `文案键没有被使用: ${key}`)
}

// 5) 详情页只能用动态 import 引入装箱块：静态 import 会把它并进详情页主代码块（首屏包体预算很紧）。
const detailSource = readFileSync(resolve('src/pages/Warehouse/StoreOrders/Detail.tsx'), 'utf8')
assert(
  /const loadPackProgress = \(\) => import\('\.\/packProgress\/PackProgressSection'\)/.test(detailSource) &&
    detailSource.includes('lazy(loadPackProgress)'),
  '详情页必须用 lazy + 动态 import 引入 PackProgressSection',
)
// 每处动态 import 都会在详情页代码块里内联一份预加载依赖清单（约 1KB），所以只允许一处。
assert((detailSource.match(/import\('\.\/packProgress\//g) ?? []).length === 1, '详情页对 ./packProgress 只能有一处动态 import')
assert(!/^import .*from '\.\/packProgress\//m.test(detailSource), '详情页不能静态 import ./packProgress 下的模块')
assert(!/^import .*from '\.\/packProgress\//m.test(readFileSync(resolve('src/pages/Warehouse/StoreOrders/index.tsx'), 'utf8')), '订单列表页不引用装箱块')

console.log(`packProgressMessages.contract.test: ok（${Object.keys(zh).length} 个键，源码引用 ${usedKeys.size} 个）`)
