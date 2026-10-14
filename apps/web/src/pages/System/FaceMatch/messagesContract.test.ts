import { readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'

// 人脸比对页的文案全部在页面级消息文件里（随页面懒加载，不进首屏 i18n 包），
// 全局 zh.json / en.json 只允许有一个菜单键 menu.faceMatch。拆开后键名写错不会有任何报错，
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

const directory = 'src/pages/System/FaceMatch'
const NAMESPACE = 'faceMatch'
// 目录下除测试外的全部源码都参与扫描：新增组件不会漏检。
const sourceNames = readdirSync(resolve(directory)).filter((name) => /\.(ts|tsx)$/.test(name) && !/\.test\.ts$/.test(name))
const sources = sourceNames.map((name) => ({ name, text: stripComments(readFileSync(resolve(directory, name), 'utf8')) }))
if (!sourceNames.includes('index.tsx') || !sourceNames.includes('logic.ts') || !sourceNames.includes('imageLogic.ts')) {
  throw new Error(`没有扫描到预期的源码文件: ${sourceNames.join(',')}`)
}

const pageMessages = { zh: readJson(`${directory}/messages.zh.json`), en: readJson(`${directory}/messages.en.json`) }
const globalMessages = { zh: readJson('src/i18n/locales/zh.json'), en: readJson('src/i18n/locales/en.json') }

// 1) 中英文页面消息键集合一致，且都在 faceMatch 命名空间下
const zhKeys = flattenKeys(pageMessages.zh).sort()
const enKeys = flattenKeys(pageMessages.en).sort()
if (zhKeys.join('|') !== enKeys.join('|')) {
  const onlyZh = zhKeys.filter((key) => !enKeys.includes(key))
  const onlyEn = enKeys.filter((key) => !zhKeys.includes(key))
  throw new Error(`页面消息文件中英文键集合不一致。仅中文: ${onlyZh.join(',')}；仅英文: ${onlyEn.join(',')}`)
}
if (zhKeys.length === 0) throw new Error('页面消息文件为空')
if (!zhKeys.every((key) => key.startsWith(`${NAMESPACE}.`))) throw new Error('页面消息必须都在 faceMatch 命名空间下')

// 2) 源码里的字面量键（任意引号包起来的 faceMatch.xxx）在页面消息里中英文都存在
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
const pageSource = sources.find((source) => source.name === 'index.tsx')!.text
if (
  !pageSource.includes('registerPageMessages({ zh, en })') ||
  !pageSource.includes("from './messages.zh.json'") ||
  !pageSource.includes("from './messages.en.json'")
) {
  throw new Error('页面没有注册页面级消息文件')
}

// 6) 全局语言包只新增菜单键，不能重复定义页面文案
for (const language of ['zh', 'en'] as const) {
  if (getLeaf(globalMessages[language], 'menu.faceMatch') === '') throw new Error(`全局${language}语言包缺少 menu.faceMatch`)
  if (NAMESPACE in globalMessages[language]) throw new Error(`页面文案不允许写进全局${language}语言包`)
}
if (getLeaf(globalMessages.zh, 'menu.faceMatch') !== '人脸比对') throw new Error('全局中文菜单名应为「人脸比对」')
if (getLeaf(globalMessages.en, 'menu.faceMatch') !== 'Face Matching') throw new Error('全局英文菜单名应为 Face Matching')

// 7) 页面代码里没有硬编码的中文文案（注释除外）：全部走消息文件
for (const { name, text } of sources) {
  const literal = text.match(/[一-鿿]+/)
  if (literal) throw new Error(`${name} 含有硬编码中文文案「${literal[0]}」，请移入 messages.zh.json`)
}

// 8) 用词与口径（用户已定）：
//    · 一律使用「不受欢迎人员名单」，不得出现「黑名单」/ blacklist；
//    · 相似度只能写成「不等于是同一个人的概率」（旧写法会被读成「不是同一个人」的概率，有歧义），疑似匹配的提示文案固定；
//    · 命中复核只需一个有复核权限的人判断一次，复核场景不得再出现双人复核、复核人序数、须换人确认的说法；
//    · 唯一访客数必须标注估算。
{
  const everything = [JSON.stringify(pageMessages.zh), JSON.stringify(pageMessages.en), JSON.stringify(globalMessages.zh), JSON.stringify(globalMessages.en)]
  for (const text of everything) {
    if (/黑名单/.test(text) || /black\s*-?\s*list/i.test(text)) throw new Error('文案不得出现「黑名单」/ blacklist，应使用「不受欢迎人员名单」')
  }
  const zh = pageMessages.zh as { faceMatch: Record<string, Record<string, unknown>> }
  const en = pageMessages.en as { faceMatch: Record<string, Record<string, unknown>> }
  const expectEqual = (actual: unknown, expected: string, what: string) => {
    if (actual !== expected) throw new Error(`${what} 应为「${expected}」，实际是「${String(actual)}」`)
  }
  expectEqual(zh.faceMatch.tabs.unwelcome, '不受欢迎人员名单', '中文页签名')
  expectEqual(
    (zh.faceMatch.review as Record<string, string>).suspectedNotice,
    '疑似匹配（相似度 {{percent}}%，不等于是同一个人的概率），需要人工核对确认',
    '中文疑似匹配提示',
  )
  expectEqual(
    (zh.faceMatch.review as Record<string, string>).similarity,
    '相似度 {{percent}}%（不等于是同一个人的概率）',
    '中文相似度展示',
  )
  expectEqual(
    (zh.faceMatch.review.columns as Record<string, string>).similarity,
    '相似度（不等于是同一个人的概率）',
    '中文相似度列头',
  )
  // 复核弹框确认「是同一个人」的文案同样要写明口径
  if (!String((zh.faceMatch.review.confirmMatch as Record<string, string>).content).includes('不等于是同一个人的概率')) {
    throw new Error('复核确认弹框必须写明「相似度不等于是同一个人的概率」')
  }
  // 有歧义的旧写法（字面量拆开写，避免全仓库检索旧文案时命中本测试自身）
  const ambiguousProbability = '不是同一个人' + '的概率'
  if (JSON.stringify(pageMessages.zh).includes(ambiguousProbability)) {
    throw new Error(`中文文案不得使用有歧义的旧写法，应写「不等于是同一个人的概率」: ${ambiguousProbability}`)
  }
  const enSuspected = String((en.faceMatch.review as Record<string, string>).suspectedNotice)
  if (!/Suspected match/.test(enSuspected) || !/not the probability/.test(enSuspected) || /two people|second/i.test(enSuspected)) {
    throw new Error(`英文疑似匹配提示必须含 Suspected match 与 not the probability，且不得再写双人确认: ${enSuspected}`)
  }
  const enSimilarity = String((en.faceMatch.review as Record<string, string>).similarity)
  if (!/not the probability/.test(enSimilarity)) throw new Error('英文相似度展示必须写明 not the probability')
  // 档位文案
  expectEqual((zh.faceMatch.review.band as Record<string, string>).high, '较高', '中文档位')
  expectEqual((zh.faceMatch.review.band as Record<string, string>).normal, '一般', '中文档位')
  // 复核按钮文案
  expectEqual((zh.faceMatch.review.verdict as Record<string, string>).match, '是同一个人', '中文复核按钮')
  expectEqual((zh.faceMatch.review.verdict as Record<string, string>).notMatch, '不是同一个人', '中文复核按钮')
  // 单人复核：复核状态与步骤文案固定，且复核场景（review 节点 + 门店档案名单说明 + 错误码文案）不再出现双人复核的说法。
  expectEqual((zh.faceMatch.review.status as Record<string, string>).confirmed, '已确认', '中文已确认状态')
  expectEqual((zh.faceMatch.review.status as Record<string, string>).pending, '待复核', '中文待复核状态')
  expectEqual((zh.faceMatch.review.step as Record<string, string>).first, '待复核', '中文待复核步骤')
  if ('second' in (zh.faceMatch.review.step as Record<string, string>) || 'second' in (en.faceMatch.review.step as Record<string, string>)) {
    throw new Error('复核步骤不应再有 second 键（旧的待二审步骤）')
  }
  if ('firstReviewer' in zh.faceMatch.review || 'firstReviewer' in en.faceMatch.review) {
    throw new Error('不应再有「前一位复核人已判断，请后一位确认」的提示横幅文案 firstReviewer')
  }
  expectEqual((zh.faceMatch.review as Record<string, string>).reviewerFirst, '复核人：{{name}}', '中文复核人')
  expectEqual((zh.faceMatch.review as Record<string, string>).reviewerSecond, '补充复核人：{{name}}', '中文补充复核人（仅旧遗留任务）')
  if (!String((zh.faceMatch.review as Record<string, string>).intro).includes('系统不会自动拒绝服务或通知警方')) {
    throw new Error('复核页说明必须写明系统不会自动拒绝服务或通知警方')
  }
  // 命中自动确认：阈值说明固定（用户定稿），必须写明相似度的口径、仍会提示店员、系统不会自动拒绝服务或通知警方、0 = 不自动确认、默认 90。
  const zhProfile = zh.faceMatch.profile as unknown as { fields: Record<string, string>; hints: Record<string, string> }
  const enProfile = en.faceMatch.profile as unknown as { fields: Record<string, string>; hints: Record<string, string> }
  expectEqual(zhProfile.fields.unwelcomeAutoConfirmScore, '命中自动确认阈值（%）', '中文自动确认阈值字段名')
  expectEqual(
    zhProfile.hints.unwelcomeAutoConfirmScore,
    '命中任务的相似度达到或超过这个值时，系统自动把任务标为“已确认”，不再需要人工复核（店员仍会收到提示；系统不会自动拒绝服务或通知警方）。填 0 表示不自动确认，默认 90。相似度不等于是同一个人的概率。',
    '中文自动确认阈值说明',
  )
  if (!zhProfile.hints.unwelcomeAutoConfirmScore.includes('不等于是同一个人的概率')) throw new Error('自动确认阈值说明必须写明「相似度不等于是同一个人的概率」')
  const enAutoHint = enProfile.hints.unwelcomeAutoConfirmScore
  if (!/not the probability of being the same person/.test(enAutoHint) || !/never refuses service or notifies police/.test(enAutoHint) || !/default is 90/.test(enAutoHint)) {
    throw new Error(`英文自动确认阈值说明必须写明 not the probability、不会自动拒绝服务或通知警方、默认 90: ${enAutoHint}`)
  }
  expectEqual(zh.faceMatch.review.autoConfirmedTag, '自动确认', '中文自动确认小标签')
  expectEqual(zh.faceMatch.review.autoConfirmedBy, '系统自动确认', '中文自动确认复核人列')
  expectEqual(en.faceMatch.review.autoConfirmedTag, 'Auto-confirmed', '英文自动确认小标签')
  expectEqual(en.faceMatch.review.autoConfirmedBy, 'Confirmed automatically by the system', '英文自动确认复核人列')
  // 误报统计：自动确认单独一列（总体 / 条目 / 相似度区间都有），原「已确认」列头改成「人工确认」，驳回率说明写明只统计人工判断的任务。
  const zhStats = zh.faceMatch.stats as Record<string, string>
  const enStats = en.faceMatch.stats as Record<string, string>
  expectEqual(zhStats.confirmedCount, '人工确认', '中文统计页「已确认」列头')
  expectEqual(zhStats.autoConfirmedCount, '自动确认', '中文统计页自动确认列头')
  expectEqual(enStats.confirmedCount, 'Manually confirmed', '英文统计页人工确认列头')
  expectEqual(enStats.autoConfirmedCount, 'Auto-confirmed', '英文统计页自动确认列头')
  if (!zhStats.intro.includes('驳回率只统计人工判断的任务，系统自动确认的不计入。')) throw new Error('中文统计说明必须写明「驳回率只统计人工判断的任务，系统自动确认的不计入。」')
  if (!zhStats.intro.includes('已驳回 ÷（已驳回 + 人工确认）')) throw new Error('中文统计说明里的驳回率公式应为「已驳回 ÷（已驳回 + 人工确认）」')
  if (!/counts only tasks decided by a person/.test(enStats.intro) || !/not included/.test(enStats.intro) || !/dismissed \+ manually confirmed/.test(enStats.intro)) {
    throw new Error(`英文统计说明必须写明驳回率只统计人工判断的任务、自动确认的不计入: ${enStats.intro}`)
  }
  // 自动确认新增的全部文案：沿用单人复核口径，不出现双人 / 序数 / 有歧义的概率写法
  const autoConfirmTexts = {
    zh: [
      zhProfile.fields.unwelcomeAutoConfirmScore, zhProfile.hints.unwelcomeAutoConfirmScore,
      (zh.faceMatch.validation as Record<string, unknown>).autoConfirmScoreRequired, (zh.faceMatch.validation as Record<string, unknown>).autoConfirmScoreInvalid,
      zh.faceMatch.review.autoConfirmedTag, zh.faceMatch.review.autoConfirmedBy,
      zhStats.confirmedCount, zhStats.autoConfirmedCount, zhStats.intro,
    ],
    en: [
      enProfile.fields.unwelcomeAutoConfirmScore, enProfile.hints.unwelcomeAutoConfirmScore,
      (en.faceMatch.validation as Record<string, unknown>).autoConfirmScoreRequired, (en.faceMatch.validation as Record<string, unknown>).autoConfirmScoreInvalid,
      en.faceMatch.review.autoConfirmedTag, en.faceMatch.review.autoConfirmedBy,
      enStats.confirmedCount, enStats.autoConfirmedCount, enStats.intro,
    ],
  }
  for (const text of autoConfirmTexts.zh.map(String)) {
    if (text === '' || text === 'undefined') throw new Error('自动确认的中文文案缺失')
    for (const banned of [new RegExp('不是同一个人' + '的概率'), /两人/, /两位/, /第[一二]复核/, /不同于/]) {
      if (banned.test(text)) throw new Error(`自动确认中文文案不得出现 ${banned}: ${text}`)
    }
  }
  for (const text of autoConfirmTexts.en.map(String)) {
    if (text === '' || text === 'undefined') throw new Error('自动确认的英文文案缺失')
    for (const banned of [/two people/i, /two[- ]person/i, /second reviewer/i, /first reviewer/i, /different person/i, /probability of not being/i]) {
      if (banned.test(text)) throw new Error(`自动确认英文文案不得出现 ${banned}: ${text}`)
    }
  }
  const reviewScope = [
    JSON.stringify(zh.faceMatch.review),
    JSON.stringify(en.faceMatch.review),
    // 旧后端兼容：错误码文案表里保留 FACE_MATCH_SAME_REVIEWER，但措辞不再出现复核人序数
    JSON.stringify((zh.faceMatch.errors as Record<string, unknown>).FACE_MATCH_SAME_REVIEWER),
    JSON.stringify((en.faceMatch.errors as Record<string, unknown>).FACE_MATCH_SAME_REVIEWER),
  ].join('\n')
  // 复核人序数的中文禁用词用「序数 + 复核」拼出来，同样是避免全仓库检索旧文案时命中本测试自身。
  const ordinalBanned = ['第二', '第一'].map((ordinal) => new RegExp(`${ordinal}复核`))
  for (const banned of [...ordinalBanned, /两人/, /两位/, /不同于/, /已由第二人/, /second reviewer/i, /first reviewer/i, /two[- ]person/i, /two people/i, /different person/i, /second person/i]) {
    if (banned.test(reviewScope)) throw new Error(`复核场景文案不得再出现双人复核的说法（命中 ${banned}）`)
  }
  // 门店档案里的名单说明：命中名单进入「人工复核」，不再写成双人复核
  const zhUnwelcomeList = JSON.stringify(zh).match(/命中名单时提示店员并进入[^。]*。/)?.[0] ?? ''
  if (zhUnwelcomeList !== '命中名单时提示店员并进入人工复核。') throw new Error(`门店档案名单说明应写「进入人工复核」，实际是「${zhUnwelcomeList}」`)
  if (/two[- ]person review/i.test(JSON.stringify(en))) throw new Error('英文文案不得再写 two-person review')
  // 唯一访客数必须标注估算（中英文）
  if (!String(zh.faceMatch.traffic.uniqueVisitorEstimate).includes('估算')) throw new Error('中文唯一访客数列头必须含「估算」')
  if (!String(zh.faceMatch.traffic.estimateNotice).includes('估算值')) throw new Error('中文客流说明必须写明估算值')
  if (!/estimate/i.test(String(en.faceMatch.traffic.uniqueVisitorEstimate)) || !/estimate/i.test(String(en.faceMatch.traffic.estimateNotice))) {
    throw new Error('英文唯一访客数列头与说明必须含 estimate')
  }
  // 常驻提示：闸门只是记录，不能替代法律判断
  if (!String(zh.faceMatch.legalNotice).includes('不能替代法律判断')) throw new Error('中文常驻提示必须写明不能替代法律判断')
  if (!/not replace legal judgement/i.test(String(en.faceMatch.legalNotice))) throw new Error('英文常驻提示必须写明不能替代法律判断')
  // 腾讯云侧删除待完成
  if (!String(zh.faceMatch.enrollment.pendingRemoteDeletion).includes('腾讯云侧删除待完成') || !String(zh.faceMatch.enrollment.pendingRemoteDeletion).includes('继续重试')) {
    throw new Error('登记撤回待完成提示必须写明「腾讯云侧删除待完成，系统会继续重试」')
  }
  // 敏感信息提示
  if (!String(zh.faceMatch.review.sensitiveNotice).includes('敏感的生物识别信息') || !String(zh.faceMatch.review.sensitiveNotice).includes('查看会被记录')) {
    throw new Error('复核页敏感提示必须写明敏感生物识别信息与查看会被记录')
  }
  // Other 类别需要两位批准人
  if (!String((zh.faceMatch.unwelcome.form as Record<string, string>).otherNeedsTwo).includes('两位不同的批准人')) throw new Error('选择「其他」时必须提示需两位不同批准人')
  // 事件类别里没有「一般偷窃」
  const categories = zh.faceMatch.unwelcome.category as Record<string, string>
  if (Object.keys(categories).length !== 3) throw new Error('事件类别只能有三项')
  if (JSON.stringify(categories).includes('一般偷窃')) throw new Error('事件类别里不得有「一般偷窃」')
}

// 8b) 抓拍与比对（FaceCapture.Images.View）与命中通知：口径与固定文案
{
  const zh = pageMessages.zh as { faceMatch: Record<string, Record<string, unknown>> }
  const en = pageMessages.en as { faceMatch: Record<string, Record<string, unknown>> }
  const expectEqual = (actual: unknown, expected: string, what: string) => {
    if (actual !== expected) throw new Error(`${what} 应为「${expected}」，实际是「${String(actual)}」`)
  }
  expectEqual(zh.faceMatch.tabs.results, '抓拍与比对', '中文页签名')
  expectEqual(en.faceMatch.tabs.results, 'Captures & matches', '英文页签名')
  const zhResults = zh.faceMatch.results as Record<string, unknown>
  const enResults = en.faceMatch.results as Record<string, unknown>
  const zhDrawer = zhResults.drawer as Record<string, unknown>
  const enDrawer = enResults.drawer as Record<string, unknown>
  // 相似度口径：页面说明与抽屉都要写明「相似度不等于是同一个人的概率」
  for (const [what, text] of [['页面说明', zhResults.intro], ['抽屉比对说明', zhDrawer.scoreNote]] as const) {
    if (!String(text).includes('相似度不等于是同一个人的概率')) throw new Error(`中文${what}必须写明「相似度不等于是同一个人的概率」`)
  }
  if (!String(zhDrawer.notProbability).includes('不等于是同一个人的概率')) throw new Error('中文抽屉名单命中旁必须写明「不等于是同一个人的概率」')
  for (const [what, text] of [['页面说明', enResults.intro], ['抽屉比对说明', enDrawer.scoreNote], ['名单命中旁', enDrawer.notProbability]] as const) {
    if (!/not the probability of being the same person/.test(String(text))) throw new Error(`英文${what}必须写明 not the probability of being the same person`)
  }
  // 页面说明要讲清：只显示 Capture 模式留存的抓拍，Match 模式处理完即删
  if (!String(zhResults.intro).includes('Capture') || !String(zhResults.intro).includes('Match') || !String(zhResults.intro).includes('处理完即删除')) {
    throw new Error('中文页面说明必须写明 Capture 模式留存、Match 模式处理完即删除')
  }
  if (!/Capture mode/.test(String(enResults.intro)) || !/Match mode/.test(String(enResults.intro)) || !/deleted right after processing/.test(String(enResults.intro))) {
    throw new Error('英文页面说明必须写明 Capture mode 留存、Match mode 处理完即删除')
  }
  // 照片是生物识别信息：列表脚注与抽屉提示都要写明每次查看会被记录
  if (!String(zhResults.footnote).includes('生物识别信息') || !String(zhResults.footnote).includes('被记录')) throw new Error('中文列表脚注必须写明生物识别信息与查看会被记录')
  if (!String(zhDrawer.sensitiveNotice).includes('生物识别信息') || !String(zhDrawer.sensitiveNotice).includes('被记录')) throw new Error('中文抽屉提示必须写明生物识别信息与查看会被记录')
  if (!/biometric/i.test(String(enResults.footnote)) || !/recorded/.test(String(enResults.footnote))) throw new Error('英文列表脚注必须写明 biometric 与 recorded')
  if (!/biometric/i.test(String(enDrawer.sensitiveNotice)) || !/recorded/.test(String(enDrawer.sensitiveNotice))) throw new Error('英文抽屉提示必须写明 biometric 与 recorded')
  // 判定与通知状态的固定中文文案（与设计一致）
  const zhDecision = zhResults.decision as Record<string, string>
  expectEqual(zhDecision.staff, '员工', '中文判定-员工')
  expectEqual(zhDecision.repeatVisitor, '回头客', '中文判定-回头客')
  expectEqual(zhDecision.newVisitor, '新访客', '中文判定-新访客')
  expectEqual(zhDecision.lowQuality, '质量不足', '中文判定-质量不足')
  expectEqual(zhDecision.notEvaluated, '未评估', '中文判定-未评估')
  const zhNotify = zh.faceMatch.notify as Record<string, string>
  expectEqual(zhNotify.sent, '已发送', '中文通知状态-已发送')
  expectEqual(zhNotify.pending, '排队中', '中文通知状态-排队中')
  expectEqual(zhNotify.skipped, '已跳过', '中文通知状态-已跳过')
  expectEqual(zhNotify.counts, '{{recipients}} 人 · 已读 {{read}}', '中文已发送人数与已读')
  expectEqual((zh.faceMatch.review.columns as Record<string, string>).notify, '命中通知', '中文复核任务「命中通知」列头')
  // 通知只显示人数不显示姓名；通知文案区说明「仅英文，不含姓名与照片」
  expectEqual(zhDrawer.notifyTextTitle, '通知文案（仅英文，不含姓名与照片）', '中文通知文案区标题')
  if (!String((zhDrawer as Record<string, unknown>).goReview).includes('去复核')) throw new Error('抽屉要有「去复核」按钮')
  const timeline = zhResults.timeline as Record<string, string>
  if (!timeline.readHint.includes('只显示人数，不显示姓名')) throw new Error('通知已读行必须写明只显示人数，不显示姓名')
  if (!/never names/.test(String((enResults.timeline as Record<string, string>).readHint))) throw new Error('英文已读说明必须写明 never names')
  // 通知文案是固定英文原文，由代码里的常量展示，不进消息文件（中英文界面都原样展示，也就不会被误翻译）
  for (const text of [JSON.stringify(zh), JSON.stringify(en)]) {
    if (/Possible match alert/.test(text) || /Possible match \(similarity/.test(text)) throw new Error('通知英文原文不得放进页面消息文件（固定英文，不翻译）')
  }
  // 新增文案同样不得有有歧义的概率写法、双人复核说法
  for (const text of [JSON.stringify(zhResults), JSON.stringify(zhNotify)]) {
    for (const banned of [new RegExp('不是同一个人' + '的概率'), /两人/, /两位/, /黑名单/]) {
      if (banned.test(text)) throw new Error(`抓拍与比对中文文案不得出现 ${banned}`)
    }
  }
  for (const text of [JSON.stringify(enResults), JSON.stringify(en.faceMatch.notify)]) {
    for (const banned of [/two people/i, /two[- ]person/i, /probability of not being/i, /black\s*-?\s*list/i]) {
      if (banned.test(text)) throw new Error(`抓拍与比对英文文案不得出现 ${banned}`)
    }
  }
}

// 9) 页面文案不得进全局语言包：除菜单键外，全局包里没有任何与本页相关的键
for (const language of ['zh', 'en'] as const) {
  const text = JSON.stringify(globalMessages[language])
  if (text.includes('faceMatch.') || text.includes('"FACE_MATCH_')) throw new Error(`全局${language}语言包里不应有本页文案`)
}

// 10) 错误码文案：与后端真实错误码一一对应（中英文都要有，已被替换的旧码不得残留）
{
  const realCodes = [
    'FACE_MATCH_DISABLED', 'FACE_MATCH_STORE_NOT_CONFIGURED', 'FACE_MATCH_FORBIDDEN', 'FACE_MATCH_STORE_FORBIDDEN',
    'FACE_MATCH_STORE_INVALID', 'FACE_MATCH_STORE_NOT_FOUND', 'FACE_MATCH_FIELD_INVALID', 'FACE_MATCH_NOT_FOUND',
    'FACE_MATCH_GATE_NOT_MET', 'FACE_MATCH_CONSENT_WITHDRAWN', 'FACE_MATCH_NSW_NOTICE_REQUIRED', 'FACE_MATCH_NSW_NOTICE_TOO_RECENT',
    'FACE_MATCH_ENROLLMENT_EXISTS', 'FACE_MATCH_PHOTO_UNUSABLE', 'FACE_MATCH_WITHDRAW_PENDING', 'FACE_MATCH_TENCENT_AUTH',
    'FACE_MATCH_TENCENT_BUSY', 'FACE_MATCH_TENCENT_LIMIT', 'FACE_MATCH_TENCENT_CONFLICT', 'FACE_MATCH_TENCENT_UNAVAILABLE',
    'FACE_MATCH_TENCENT_ERROR', 'FACE_MATCH_ENTRY_NOT_PENDING', 'FACE_MATCH_APPROVER_IS_SUBMITTER', 'FACE_MATCH_ALREADY_APPROVED_BY_YOU',
    'FACE_MATCH_ENTRY_EXPIRED', 'FACE_MATCH_UNWELCOME_NOT_ENABLED', 'FACE_MATCH_ENTRY_CHANGED', 'FACE_MATCH_TASK_CLOSED',
    'FACE_MATCH_SAME_REVIEWER', 'FACE_MATCH_TASK_CHANGED', 'FACE_MATCH_IMAGE_GONE', 'FACE_MATCH_IMAGE_TOO_LARGE',
    'FACE_MATCH_IMAGE_UNSUPPORTED', 'FACE_MATCH_STORE_DENIED', 'FACE_MATCH_STORE_TIMEOUT', 'FACE_MATCH_STORE_ERROR',
    'FACE_MATCH_AUDIT_FAILED', 'FACE_MATCH_TIMEZONE_UNAVAILABLE',
  ]
  for (const language of ['zh', 'en'] as const) {
    const errors = (pageMessages[language] as { faceMatch: { errors: Json } }).faceMatch.errors
    const codesInMessages = Object.keys(errors).filter((key) => key.startsWith('FACE_MATCH_')).sort()
    if (codesInMessages.join(',') !== [...realCodes].sort().join(',')) {
      throw new Error(`${language} 错误码文案与后端真实错误码不一致: ${codesInMessages.join(',')}`)
    }
    for (const code of realCodes) {
      if (getLeaf(pageMessages[language], `faceMatch.errors.${code}`).trim() === '') throw new Error(`${language} 缺少错误码文案 ${code}`)
    }
  }
  for (const status of ['status401', 'status403', 'status404', 'status409', 'status413', 'status503', 'generic']) {
    for (const language of ['zh', 'en'] as const) {
      if (!hasLeaf(pageMessages[language], `faceMatch.errors.${status}`)) throw new Error(`${language} 缺少按状态码兜底的文案 ${status}`)
    }
  }
  // 撤回待完成（502）要告诉用户「登记仍然有效，请稍后重试」
  const zhErrors = (pageMessages.zh as { faceMatch: { errors: Record<string, string> } }).faceMatch.errors
  if (!zhErrors.FACE_MATCH_WITHDRAW_PENDING.includes('登记仍然有效') || !zhErrors.FACE_MATCH_WITHDRAW_PENDING.includes('重试')) {
    throw new Error('FACE_MATCH_WITHDRAW_PENDING 文案必须写明登记仍然有效、请稍后重试撤回')
  }
  // 名单条目清理未完成提示
  if (!hasLeaf(pageMessages.zh, 'faceMatch.unwelcome.cleanupPending') || !hasLeaf(pageMessages.en, 'faceMatch.unwelcome.cleanupPendingTip')) {
    throw new Error('缺少名单条目「清理中」提示文案')
  }
}

console.log('FaceMatch messagesContract.test: ok')
