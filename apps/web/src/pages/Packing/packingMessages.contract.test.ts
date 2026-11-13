import { readFileSync, readdirSync } from 'node:fs'
import { join, resolve } from 'node:path'

// 扫码打包页的文案放在页面目录的 packingMessages.{zh,en}.json（命名空间 packingUi），随页面懒注册，不进首屏包。
// 拆成全局 + 页面两处后，键名写错不会报错、界面直接显示原始键名，所以这里做静态核对。

type Json = { [key: string]: Json | string }

const ROOT = resolve('src/pages/Packing')
const NAMESPACE = 'packingUi'

function readJson(path: string): Json {
  return JSON.parse(readFileSync(path, 'utf8')) as Json
}

function flattenKeys(resource: Json, prefix = ''): string[] {
  return Object.entries(resource).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : flattenKeys(value, `${prefix}${key}.`),
  )
}

function assert(condition: boolean, message: string) {
  if (!condition) {
    throw new Error(message)
  }
}

const zh = readJson(join(ROOT, 'packingMessages.zh.json'))
const en = readJson(join(ROOT, 'packingMessages.en.json'))

// 1) 只有一个 packingUi 命名空间，中英文键集合一致，没有空文案。
assert(Object.keys(zh).join() === NAMESPACE && Object.keys(en).join() === NAMESPACE, `页面文案只能有 ${NAMESPACE} 一个命名空间`)
const zhKeys = flattenKeys(zh[NAMESPACE] as Json).sort()
const enKeys = flattenKeys(en[NAMESPACE] as Json).sort()
assert(zhKeys.join('|') === enKeys.join('|'), '中英文键集合不一致')
const flatten = (resource: Json) =>
  Object.fromEntries(flattenKeys(resource).map((key) => [key, key.split('.').reduce<Json | string>((node, part) => (node as Json)[part], resource)]))
for (const [language, resource] of [['zh', flatten(zh[NAMESPACE] as Json)], ['en', flatten(en[NAMESPACE] as Json)]] as const) {
  for (const [key, value] of Object.entries(resource)) {
    assert(typeof value === 'string' && value.trim().length > 0, `${language} 文案为空: ${key}`)
  }
}

// 2) 全局语言包不得定义同名命名空间（它在首屏包里）。
for (const language of ['zh', 'en']) {
  const globalMessages = readJson(resolve(`src/i18n/locales/${language}.json`))
  assert(!(NAMESPACE in globalMessages), `全局 ${language} 语言包不应定义 ${NAMESPACE}`)
}

// 3) 页面入口里注册了这两份文件。
const entry = readFileSync(join(ROOT, 'index.tsx'), 'utf8')
assert(
  entry.includes('registerPageMessages(') && entry.includes("'./packingMessages.zh.json'") && entry.includes("'./packingMessages.en.json'"),
  '页面入口必须用 registerPageMessages 注册中英文文案',
)

// 4) 源码里出现的每个字面量键，中英文都必须存在；动态拼接的键（步骤、位置分组）单独列出来核对。
const sources = readdirSync(ROOT).filter((name) => /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name))
const usedKeys = new Set<string>()
for (const name of sources) {
  const text = readFileSync(join(ROOT, name), 'utf8')
  for (const match of text.matchAll(/['"`]packingUi\.([A-Za-z0-9_.]*[A-Za-z0-9_])['"`]/g)) {
    usedKeys.add(match[1])
  }
}
assert(usedKeys.size > 100, `源码里的文案键太少（${usedKeys.size}），检查命名空间或源码路径`)
for (const key of usedKeys) {
  assert(zhKeys.includes(key) && enKeys.includes(key), `缺少中英文文案键: ${key}`)
}
for (const step of ['build', 'seal', 'dispatch', 'receive']) {
  for (const suffix of ['', 'Pending']) {
    assert(zhKeys.includes(`step.${step}${suffix}`), `缺少托盘进度文案: step.${step}${suffix}`)
  }
}
for (const group of ['stored', 'transit', 'elsewhere', 'other']) {
  assert(zhKeys.includes(`locate.group.${group}`), `缺少位置分组文案: locate.group.${group}`)
}

// 5) 不再有旧命名空间的残留引用。
for (const name of sources) {
  assert(!readFileSync(join(ROOT, name), 'utf8').includes('warehouseUi.packing'), `${name} 仍引用旧文案命名空间 warehouseUi.packing`)
}

console.log(`packingMessages.contract.test: ok（${zhKeys.length} 个键，源码引用 ${usedKeys.size} 个）`)
