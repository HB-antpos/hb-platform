import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 人脸抓拍摄像机页的文案全部在页面级消息文件里（随页面懒加载，不进首屏 i18n 包），
// 全局 zh.json / en.json 只允许有一个菜单键 menu.faceCameras。拆开后键名写错不会有任何报错，
// 界面会直接显示原始键名，所以在这里静态核对：
// 源码里出现的每个字面量键中英文都必须存在；两份消息文件键集合一致；没有无人使用的孤立键；插值占位符一致。

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

function getLeaf(resource: Json, path: string): string {
  let node: Json | string | undefined = resource
  for (const segment of path.split('.')) {
    if (typeof node !== 'object' || node === null) return ''
    node = node[segment]
  }
  return typeof node === 'string' ? node : ''
}

function flattenKeys(resource: Json, prefix = ''): string[] {
  return Object.entries(resource).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : flattenKeys(value, `${prefix}${key}.`),
  )
}

function placeholders(text: string): string {
  return [...text.matchAll(/\{\{\s*([A-Za-z0-9_]+)\s*\}\}/g)].map((match) => match[1]).sort().join(',')
}

/** 去掉注释，避免注释里的示例键或中文被当成代码。 */
function stripComments(source: string) {
  return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`])\/\/[^\n]*/g, '$1')
}

const directory = 'src/pages/System/FaceCameras'
const NAMESPACE = 'faceCameras'
const sources = [
  'index.tsx',
  'CameraFormModal.tsx',
  'SecretRevealModal.tsx',
  'CapturesDrawer.tsx',
  'FaceImageViewerModal.tsx',
  'logic.ts',
  'imageLogic.ts',
].map((name) => ({
  name,
  text: stripComments(readFileSync(resolve(directory, name), 'utf8')),
}))

const pageMessages = { zh: readJson(`${directory}/messages.zh.json`), en: readJson(`${directory}/messages.en.json`) }
const globalMessages = { zh: readJson('src/i18n/locales/zh.json'), en: readJson('src/i18n/locales/en.json') }

// 1) 中英文页面消息键集合一致，且都在 faceCameras 命名空间下
const zhKeys = flattenKeys(pageMessages.zh).sort()
const enKeys = flattenKeys(pageMessages.en).sort()
if (zhKeys.join('|') !== enKeys.join('|')) {
  const onlyZh = zhKeys.filter((key) => !enKeys.includes(key))
  const onlyEn = enKeys.filter((key) => !zhKeys.includes(key))
  throw new Error(`页面消息文件中英文键集合不一致。仅中文: ${onlyZh.join(',')}；仅英文: ${onlyEn.join(',')}`)
}
if (zhKeys.length === 0) throw new Error('页面消息文件为空')
if (!zhKeys.every((key) => key.startsWith(`${NAMESPACE}.`))) throw new Error('页面消息必须都在 faceCameras 命名空间下')

// 2) 源码里的字面量键（任意引号包起来的 faceCameras.xxx）在页面消息里中英文都存在
const usedKeys = new Set<string>()
const keyPattern = new RegExp(`['"\`](${NAMESPACE}\\.[A-Za-z0-9_.]+)['"\`]`, 'g')
for (const { text } of sources) {
  for (const match of text.matchAll(keyPattern)) usedKeys.add(match[1])
}
if (usedKeys.size === 0) throw new Error('没有扫描到任何文案键，检查正则或源码路径是否失效')
for (const key of usedKeys) {
  for (const language of ['zh', 'en'] as const) {
    if (!hasLeaf(pageMessages[language], key)) {
      throw new Error(`源码使用了不存在的${language === 'zh' ? '中文' : '英文'}页面文案键: ${key}`)
    }
  }
}

// 3) 页面消息文件里的每个键都被源码用到（没有孤立键，也没有靠模板拼接才能用到的键）
for (const key of zhKeys) {
  if (!usedKeys.has(key)) throw new Error(`页面消息键没有任何源码引用，请删除或补上引用: ${key}`)
}

// 4) 同一个键的中英文插值占位符必须一致（{{n}} 写成 {{count}} 之类的错误不会有任何报错）
for (const key of zhKeys) {
  const zh = getLeaf(pageMessages.zh, key)
  const en = getLeaf(pageMessages.en, key)
  if (placeholders(zh) !== placeholders(en)) {
    throw new Error(`页面消息 ${key} 的中英文插值占位符不一致: zh=[${placeholders(zh)}] en=[${placeholders(en)}]`)
  }
}

// 5) 页面注册了页面级消息文件（懒注册，不进首屏包）
const pageSource = sources[0].text
if (
  !pageSource.includes('registerPageMessages({ zh, en })') ||
  !pageSource.includes("from './messages.zh.json'") ||
  !pageSource.includes("from './messages.en.json'")
) {
  throw new Error('页面没有注册页面级消息文件')
}

// 6) 全局语言包只新增菜单键，不能重复定义页面文案
for (const language of ['zh', 'en'] as const) {
  if (!hasLeaf(globalMessages[language], 'menu.faceCameras')) throw new Error(`全局${language}语言包缺少 menu.faceCameras`)
  if (NAMESPACE in globalMessages[language]) throw new Error(`页面文案不允许写进全局${language}语言包`)
}

// 7) 页面代码里没有硬编码的中文文案（注释除外）：全部走消息文件
for (const { name, text } of sources) {
  const literal = text.match(/[一-鿿]+/)
  if (literal) throw new Error(`${name} 含有硬编码中文文案「${literal[0]}」，请移入 messages.zh.json`)
}

// 8) 状态文案不得写成「在线 / 离线」：海康摄像机只有检测到人脸才推送，没人经过镜头时没有任何推送也没有心跳，
//    「暂无推送」不等于断电断网，用户已经因为「离线」标签误以为摄像机坏了。
{
  const zh = pageMessages.zh as { faceCameras: { subtitle: string; status: Record<string, string>; notice: Record<string, string> } }
  const en = pageMessages.en as { faceCameras: { subtitle: string; status: Record<string, string>; notice: Record<string, string> } }
  const expectEqual = (actual: string, expected: string, what: string) => {
    if (actual !== expected) throw new Error(`${what} 应为「${expected}」，实际是「${actual}」`)
  }
  expectEqual(zh.faceCameras.status.recentlyActive, '近期有推送', '中文状态标签')
  expectEqual(zh.faceCameras.status.noRecentPush, '暂无推送', '中文状态标签')
  expectEqual(zh.faceCameras.status.disabled, '已停用', '中文状态标签')
  expectEqual(en.faceCameras.status.recentlyActive, 'Recently active', '英文状态标签')
  expectEqual(en.faceCameras.status.noRecentPush, 'No recent pushes', '英文状态标签')
  expectEqual(en.faceCameras.status.disabled, 'Disabled', '英文状态标签')
  expectEqual(
    zh.faceCameras.subtitle,
    '共 {{total}} 台 · 近期有推送 {{active}} · 暂无推送 {{quiet}} · 已停用 {{disabled}}',
    '中文页头计数',
  )
  const labelTexts = [
    zh.faceCameras.subtitle,
    ...Object.values(zh.faceCameras.status),
    en.faceCameras.subtitle,
    en.faceCameras.status.recentlyActive,
    en.faceCameras.status.noRecentPush,
    en.faceCameras.status.disabled,
  ]
  for (const text of labelTexts) {
    if (/在线|离线|\b(online|offline)\b/i.test(text)) throw new Error(`状态标签 / 页头计数不得出现在线 / 离线字样: ${text}`)
  }
  for (const text of [zh.faceCameras.notice.pushStatus, zh.faceCameras.status.noRecentPushTip]) {
    if (!text.includes('检测到人脸') || !text.includes('不代表断电或断网')) {
      throw new Error(`说明文案必须写清「只有检测到人脸才推送」与「不代表断电或断网」: ${text}`)
    }
  }
  if (!zh.faceCameras.notice.pushStatus.includes('近期有推送') || !zh.faceCameras.notice.pushStatus.includes('{{minutes}}')) {
    throw new Error('顶部说明要给出「近期有推送」的判定窗口')
  }
  for (const text of [en.faceCameras.notice.pushStatus, en.faceCameras.status.noRecentPushTip]) {
    if (!/only pushes when it detects a face/.test(text) || !/does not mean/.test(text)) {
      throw new Error(`英文说明文案必须写清只有检测到人脸才推送、且不代表离线: ${text}`)
    }
  }
}

// 9) 抓拍图片查看的文案：敏感提示必须逐字包含约定内容；各类失败文案都要有；全局语言包里没有任何图片相关键。
{
  const zh = pageMessages.zh as { faceCameras: { images: Record<string, string | Record<string, string>> } }
  const en = pageMessages.en as { faceCameras: { images: Record<string, string | Record<string, string>> } }
  if (zh.faceCameras.images.sensitiveNotice !== '这是敏感的生物识别信息。查看会被记录，请勿截图外传。') {
    throw new Error('中文敏感提示文案必须是约定的原文')
  }
  if (!/sensitive biometric/i.test(String(en.faceCameras.images.sensitiveNotice)) || !/logged/i.test(String(en.faceCameras.images.sensitiveNotice))) {
    throw new Error('英文敏感提示必须写清敏感生物识别信息与查看会被记录')
  }
  if (zh.faceCameras.images.local !== '图片在本地') throw new Error('中文「图片在本地」文案不对')
  const zhErrors = zh.faceCameras.images.errors as Record<string, string>
  if (zhErrors.notConfigured !== '图片读取尚未配置') throw new Error('中文「图片读取尚未配置」文案不对')
  if (zhErrors.local !== '图片仍在接收服务本地，暂不可在后台查看') throw new Error('中文「图片在本地」错误文案不对')
  for (const kind of ['notConfigured', 'local', 'forbidden', 'unauthorized', 'missing', 'failed']) {
    if (!zhErrors[kind] || !(en.faceCameras.images.errors as Record<string, string>)[kind]) throw new Error(`缺少图片失败文案: ${kind}`)
  }
  for (const language of ['zh', 'en'] as const) {
    if (JSON.stringify(globalMessages[language]).includes('FaceImage') || JSON.stringify(globalMessages[language]).includes('faceCameras.images')) {
      throw new Error(`图片相关文案不允许进全局${language}语言包`)
    }
  }
}

console.log('FaceCameras messagesContract.test: ok')
