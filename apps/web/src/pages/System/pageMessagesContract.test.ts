import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 分店管理 / 员工个人信息维护两个页面的文案被拆成两处：既有键在全局 zh.json / en.json，
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

const globalMessages = { zh: readJson('src/i18n/locales/zh.json'), en: readJson('src/i18n/locales/en.json') }

const pages = [
  {
    name: '分店管理',
    keyPattern: /['"`](system\.stores\.[A-Za-z0-9_.]+)['"`]/g,
    sources: ['src/pages/System/Stores/index.tsx', 'src/pages/System/Stores/StoreFormFields.tsx'],
    messages: {
      zh: 'src/pages/System/Stores/storesMessages.zh.json',
      en: 'src/pages/System/Stores/storesMessages.en.json',
    },
    registerImport: "from './storesMessages.zh.json'",
    registerSource: 'src/pages/System/Stores/index.tsx',
  },
  {
    name: '员工个人信息维护',
    keyPattern: /['"`](system\.employeeProfiles\.[A-Za-z0-9_.]+)['"`]/g,
    sources: ['src/pages/System/EmployeeProfiles/index.tsx'],
    messages: {
      zh: 'src/pages/System/EmployeeProfiles/employeeProfilesMessages.zh.json',
      en: 'src/pages/System/EmployeeProfiles/employeeProfilesMessages.en.json',
    },
    registerImport: "from './employeeProfilesMessages.zh.json'",
    registerSource: 'src/pages/System/EmployeeProfiles/index.tsx',
  },
]

for (const page of pages) {
  const pageMessages = { zh: readJson(page.messages.zh), en: readJson(page.messages.en) }

  // 1) 中英文页面消息文件的键集合必须一致，否则切换语言时会有一边缺文案。
  const zhKeys = flattenKeys(pageMessages.zh).sort()
  const enKeys = flattenKeys(pageMessages.en).sort()
  if (zhKeys.join('|') !== enKeys.join('|')) {
    const onlyZh = zhKeys.filter((key) => !enKeys.includes(key))
    const onlyEn = enKeys.filter((key) => !zhKeys.includes(key))
    throw new Error(`${page.name} 页面消息文件中英文键集合不一致。仅中文: ${onlyZh.join(',')}；仅英文: ${onlyEn.join(',')}`)
  }

  // 2) 页面里必须真的注册了这两份消息文件，否则新增文案在运行时全部缺失。
  const registerSource = readFileSync(resolve(page.registerSource), 'utf8')
  if (!registerSource.includes('registerPageMessages(') || !registerSource.includes(page.registerImport)) {
    throw new Error(`${page.name} 页面没有注册页面级消息文件`)
  }

  // 3) 源码里出现的每个字面量键，在「全局 ∪ 页面消息」里中英文都必须存在。
  const usedKeys = new Set<string>()
  for (const source of page.sources) {
    const text = readFileSync(resolve(source), 'utf8')
    for (const match of text.matchAll(page.keyPattern)) {
      usedKeys.add(match[1])
    }
  }
  if (usedKeys.size === 0) {
    throw new Error(`${page.name} 没有扫描到任何文案键，检查正则或源码路径是否失效`)
  }

  for (const key of usedKeys) {
    for (const language of ['zh', 'en'] as const) {
      if (!hasLeaf(globalMessages[language], key) && !hasLeaf(pageMessages[language], key)) {
        throw new Error(`${page.name} 使用了不存在的${language === 'zh' ? '中文' : '英文'}文案键: ${key}`)
      }
    }
  }

  // 4) 页面消息文件不应与全局语言包重复定义同一个键（重复只会白白占首屏之外的体积，且后注册的不会覆盖前者，容易误导）。
  for (const key of zhKeys.map((item) => `system.${page.name === '分店管理' ? 'stores' : 'employeeProfiles'}.${item.split('.').slice(2).join('.')}`)) {
    if (hasLeaf(globalMessages.zh, key)) {
      throw new Error(`${page.name} 页面消息文件重复定义了全局已有的键: ${key}`)
    }
  }
}

console.log('pageMessagesContract.test: ok')
