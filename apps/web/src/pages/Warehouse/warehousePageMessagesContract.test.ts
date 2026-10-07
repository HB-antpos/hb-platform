import { readdirSync, readFileSync, statSync } from 'node:fs'
import { basename, dirname, join, resolve } from 'node:path'

// 仓库管理 8 个列表页重设计新增的文案放在各页面目录的 `*Messages.zh.json` / `*Messages.en.json`，
// 统一挂在 `warehouseUi.<页面>` 命名空间下、随页面懒注册（全局语言包在首屏包里，体积预算很紧）。
// 拆成两处后键名写错不会报错、界面直接显示原始键名，所以这里按约定自动发现并做静态核对。

type Json = { [key: string]: Json | string }

const NAMESPACE = 'warehouseUi'
const ROOT = resolve('src/pages/Warehouse')

function readJson(path: string): Json {
  return JSON.parse(readFileSync(path, 'utf8')) as Json
}

function flattenKeys(resource: Json, prefix = ''): string[] {
  return Object.entries(resource).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : flattenKeys(value, `${prefix}${key}.`),
  )
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

function walk(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name)
    return statSync(path).isDirectory() ? walk(path) : [path]
  })
}

const globalMessages = { zh: readJson(resolve('src/i18n/locales/zh.json')), en: readJson(resolve('src/i18n/locales/en.json')) }

if (NAMESPACE in globalMessages.zh || NAMESPACE in globalMessages.en) {
  throw new Error(`全局语言包不应定义 ${NAMESPACE} 命名空间，它只用于页面级懒注册文案`)
}

const allFiles = walk(ROOT)
const messageFiles = allFiles.filter((file) => file.endsWith('Messages.zh.json')).filter((file) => {
  const resource = readJson(file)
  return NAMESPACE in resource
})

const usedNamespaces = new Set<string>()

for (const zhFile of messageFiles) {
  const enFile = zhFile.replace(/\.zh\.json$/, '.en.json')
  const directory = dirname(zhFile)
  const label = zhFile.slice(ROOT.length + 1)
  const pageMessages = { zh: readJson(zhFile), en: readJson(enFile) }

  // 1) 中英文键集合一致。
  const zhKeys = flattenKeys(pageMessages.zh).sort()
  const enKeys = flattenKeys(pageMessages.en).sort()
  if (zhKeys.join('|') !== enKeys.join('|')) {
    const onlyZh = zhKeys.filter((key) => !enKeys.includes(key))
    const onlyEn = enKeys.filter((key) => !zhKeys.includes(key))
    throw new Error(`${label} 中英文键集合不一致。仅中文: ${onlyZh.join(',')}；仅英文: ${onlyEn.join(',')}`)
  }

  // 2) 每个文件只占一个 warehouseUi.<页面> 子命名空间，且不与其他页面重名。
  const pageNamespaces = new Set(zhKeys.map((key) => key.split('.').slice(0, 2).join('.')))
  if (pageNamespaces.size !== 1) {
    throw new Error(`${label} 应只包含一个 ${NAMESPACE}.<页面> 子命名空间，实际: ${[...pageNamespaces].join(',')}`)
  }
  const [pageNamespace] = [...pageNamespaces]
  if (usedNamespaces.has(pageNamespace)) {
    throw new Error(`${pageNamespace} 被多个页面消息文件重复使用`)
  }
  usedNamespaces.add(pageNamespace)

  // 3) 同目录（含子目录）源码里必须真的注册了这份文件。
  const sources = allFiles.filter(
    (file) => file.startsWith(`${directory}/`) && /\.(ts|tsx)$/.test(file) && !/\.test\.tsx?$/.test(file),
  )
  const zhImport = `/${basename(zhFile)}'`
  const registered = sources.some((file) => {
    const text = readFileSync(file, 'utf8')
    return text.includes('registerPageMessages(') && text.includes(zhImport)
  })
  if (!registered) {
    throw new Error(`${label} 没有在页面源码里用 registerPageMessages 注册`)
  }

  // 4) 源码里出现的每个字面量键，中英文都必须存在。
  const pattern = new RegExp(`['"\`](${pageNamespace.replace('.', '\\.')}\\.[A-Za-z0-9_.]+)['"\`]`, 'g')
  const usedKeys = new Set<string>()
  for (const file of sources) {
    for (const match of readFileSync(file, 'utf8').matchAll(pattern)) {
      usedKeys.add(match[1])
    }
  }
  if (usedKeys.size === 0) {
    throw new Error(`${label} 的键在同目录源码里一次也没被使用，检查命名空间或源码路径`)
  }
  for (const key of usedKeys) {
    for (const language of ['zh', 'en'] as const) {
      if (!hasLeaf(pageMessages[language], key)) {
        throw new Error(`${label} 缺少${language === 'zh' ? '中文' : '英文'}文案键: ${key}`)
      }
    }
  }
}

console.log(`warehousePageMessagesContract.test: ok（${messageFiles.length} 个页面消息文件）`)
