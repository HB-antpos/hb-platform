import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

const sourceRoot = resolve(process.cwd(), 'src')
const read = (path: string) => readFileSync(resolve(sourceRoot, path), 'utf8')
const pageSource = read('pages/Warehouse/PriceUpdateTasks/index.tsx')
const settingsModalSource = read('pages/Warehouse/PriceUpdateTasks/SettingsModal.tsx')
const serviceSource = read('services/storePriceUpdateTaskService.ts')
const routesSource = read('router/routes.tsx')
const productsSource = read('pages/Warehouse/Products/index.tsx')
// 文案分三处：首屏包只放路由标题与入口；页面文案、价格通知文案分别随各自页面代码块懒注册。
function loadMessages(language: 'zh' | 'en') {
  const main = JSON.parse(read(`i18n/locales/${language}.json`)) as Record<string, any>
  const page = JSON.parse(read(`pages/Warehouse/PriceUpdateTasks/messages.${language}.json`)) as Record<string, any>
  const notification = JSON.parse(read(`pages/Warehouse/Products/priceNotificationMessages.${language}.json`)) as Record<string, any>
  return {
    warehouse: {
      priceUpdateTasks: { ...main.warehouse.priceUpdateTasks, ...page.warehouse.priceUpdateTasks },
      priceNotification: notification.warehouse.priceNotification,
    },
    mainKeys: Object.keys(main.warehouse.priceUpdateTasks ?? {}).sort(),
  }
}
const zh = loadMessages('zh')
const en = loadMessages('en')
assert(JSON.stringify(zh.mainKeys) === JSON.stringify(['entry', 'title']), '首屏 i18n 包只应保留价格变更任务的路由标题与入口文案')
assert(JSON.stringify(en.mainKeys) === JSON.stringify(['entry', 'title']), '首屏 i18n 英文包同样只保留标题与入口文案')
assert(pageSource.includes('registerPageMessages({ zh: messagesZh, en: messagesEn })'), '监控页必须懒注册本页文案')

const staticRouteIndex = routesSource.indexOf("path: '/warehouse/products/price-update-tasks'")
const dynamicRouteIndex = routesSource.indexOf("path: '/warehouse/products/:productCode/records'")
assert(staticRouteIndex >= 0, '必须注册价格变更任务静态路由')
assert(staticRouteIndex < dynamicRouteIndex, '价格变更任务静态路由必须位于动态商品路由之前')
assert(
  /path: '\/warehouse\/products\/price-update-tasks',[\s\S]{0,420}accessKey: 'canManageWarehouseProducts',[\s\S]{0,220}activeMenu: '\/warehouse\/products'/.test(routesSource),
  '价格变更任务路由必须继承仓库商品权限（Warehouse.ManageProducts）并保持父级菜单激活',
)
// 仓库商品页改版后，价格类入口收进页头「价格」菜单，与零售价变化入口并列。
assert(
  /key: 'priceUpdateTasks',[\s\S]{0,200}visible: access\.canManageWarehouseProducts,[\s\S]{0,120}\/warehouse\/products\/price-update-tasks/.test(productsSource) &&
    /key: 'retailPriceChanges',[\s\S]{0,400}key: 'priceUpdateTasks'/.test(productsSource),
  '仓库商品页必须在零售价变化入口旁提供价格变更任务入口，并复用 canManageWarehouseProducts 权限',
)

// 监控页不得代分店改价/标记完成：这些执行动作只发生在移动端，保证「谁处理的」记录真实。
// 允许的写操作只有「撤销未完成任务」与「保存超期天数设置」，分别走 service 里的 cancelStorePriceUpdateTasks /
// updateStorePriceUpdateTaskSettings，页面与弹窗本身不直接发写请求。
for (const forbidden of ['price-update-tasks/apply', 'price-update-tasks/keep', 'price-update-tasks/labels', 'request.post', 'request.put']) {
  assert(!pageSource.includes(forbidden), `监控页不应包含代分店执行的写接口 ${forbidden}`)
  assert(!settingsModalSource.includes(forbidden), `设置弹窗不应包含代分店执行的写接口 ${forbidden}`)
}
assert(pageSource.includes('cancelStorePriceUpdateTasks('), '监控页必须通过 service 提供撤销未完成任务入口')
assert(
  pageSource.includes("getCheckboxProps: (task) => ({ disabled: task.status !== 'Pending' })"),
  '任务明细只有未完成的任务可勾选撤销，已完成/已取消行必须置灰',
)
assert(pageSource.includes('summary.hqSyncEnabled ?'), '未启用总部同步时必须隐藏总部同步失败指标卡')
assert(pageSource.includes('taskHqSyncEnabled ? [{'), '未启用总部同步时必须隐藏任务明细的总部同步列')
assert(pageSource.includes('parsePriceUpdateTasksSearch(location.search)'), '页面必须支持通过 URL query 初始化页签与筛选')

// 任务超期天数设置：页头「设置」入口 + 弹窗 + service 的 /settings 接口。
assert(pageSource.includes("import SettingsModal from './SettingsModal'") && pageSource.includes('<SettingsModal'), '页面必须引用并渲染 SettingsModal')
const extraSection = /extra=\{\(([\s\S]*?)\n\s*\)\}\r?\n\s*>/.exec(pageSource)?.[1] ?? ''
assert(extraSection.includes('SettingOutlined') && extraSection.includes('setSettingsOpen(true)') && extraSection.includes('${I18N}.settings.button'), '页头 extra 区必须有带 SettingOutlined 图标、打开设置弹窗的「设置」按钮')
assert(extraSection.indexOf('${I18N}.export.button') >= 0 && extraSection.indexOf('${I18N}.export.button') < extraSection.indexOf('${I18N}.settings.button'), '「设置」按钮应排在「导出」之后')
assert(/<SettingsModal[\s\S]*?onSaved=\{\(\) => setRefreshToken\(/.test(pageSource), '保存设置后必须刷新页面数据，让「超期」统计按新天数重算')
assert(
  serviceSource.includes("const API_BASE = '/api/react/v1/store-price-update-tasks'") &&
    /getStorePriceUpdateTaskSettings\([\s\S]*?request\.get<[^(]*\(`\$\{API_BASE\}\/settings`/.test(serviceSource),
  'getStorePriceUpdateTaskSettings 必须 GET {前缀}/settings',
)
assert(
  /updateStorePriceUpdateTaskSettings\(overdueDays: number\)[\s\S]*?request\.put<[^(]*\(`\$\{API_BASE\}\/settings`, \{ overdueDays \}\)/.test(serviceSource),
  'updateStorePriceUpdateTaskSettings 必须 PUT {前缀}/settings，请求体为 { overdueDays }',
)
assert(
  settingsModalSource.includes('validateOverdueDays(') && settingsModalSource.includes('isOverdueDaysChanged(') && settingsModalSource.includes('onSaved()'),
  '设置弹窗必须用 validateOverdueDays / isOverdueDaysChanged 控制保存，并在保存成功后回调 onSaved',
)

// 仓库商品保存：建议折扣走独立接口，提示以最后一个响应头为准。
const saveSection = productsSource.slice(productsSource.indexOf('const handleSave = async'), productsSource.indexOf('const getInlineCellKey'))
assert(
  saveSection.indexOf('await updateWarehouseProductFull(') >= 0 &&
    saveSection.indexOf('await updateWarehouseProductFull(') < saveSection.indexOf('await setSuggestedDiscounts('),
  '必须先走原有保存，再保存建议折扣',
)
assert(saveSection.includes("source: 'WarehouseProducts'"), '单品编辑的建议折扣来源必须为 WarehouseProducts')
assert((saveSection.match(/createPriceNotificationCapture\(\)/g) ?? []).length === 1, '一次保存的两个请求必须共用同一个汇总捕获器（后者覆盖前者，不相加）')
assert(productsSource.includes('registerPageMessages({ zh: priceNotificationMessagesZh, en: priceNotificationMessagesEn })'), '仓库商品页必须懒注册价格通知文案')
assert(productsSource.includes("source: 'BatchUpdate'"), '批量改价的建议折扣来源必须为 BatchUpdate')
assert(productsSource.includes('priceNotificationCapture.accept(result.priceNotification)'), '批量后台任务必须读取任务快照里的 priceNotification')

// zh/en 文案键必须完全一致，避免某个语言缺文案。
function collectKeys(value: unknown, prefix = ''): string[] {
  if (typeof value !== 'object' || value === null) return [prefix]
  return Object.entries(value).flatMap(([key, child]) => collectKeys(child, prefix ? `${prefix}.${key}` : key))
}
for (const section of ['priceUpdateTasks', 'priceNotification'] as const) {
  const zhKeys = collectKeys(zh.warehouse[section]).sort()
  const enKeys = collectKeys(en.warehouse[section]).sort()
  assert(zhKeys.length > 0, `zh 缺少 warehouse.${section}`)
  assert(JSON.stringify(zhKeys) === JSON.stringify(enKeys), `warehouse.${section} 的 zh/en 文案键必须一致`)
}
// 设置弹窗用到的文案键（含按校验失败原因拼出的 error.*）在 zh/en 中都必须存在。
const settingsKeys = ['button', 'title', 'description', 'overdueDaysLabel', 'daysSuffix', 'defaultHint', 'lastModified', 'loadFailed', 'saved', 'saveFailed']
  .concat(['empty', 'notInteger', 'tooSmall', 'tooLarge'].map((reason) => `error.${reason}`))
for (const [language, messages] of [['zh', zh], ['en', en]] as const) {
  for (const key of settingsKeys) {
    const value = `settings.${key}`.split('.').reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], messages.warehouse.priceUpdateTasks)
    assert(typeof value === 'string' && value.length > 0, `${language} 缺少设置文案 warehouse.priceUpdateTasks.settings.${key}`)
  }
}
for (const match of settingsModalSource.matchAll(/\$\{I18N\}\.([A-Za-z]+(?:\.[A-Za-z]+)*)[`.]/g)) {
  const value = `settings.${match[1]}`.split('.').reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], zh.warehouse.priceUpdateTasks)
  assert(value !== undefined, `设置弹窗引用了不存在的文案 warehouse.priceUpdateTasks.settings.${match[1]}`)
}
assert(zh.warehouse.priceUpdateTasks.settings.description.includes('超过这个天数就算「超期」'), '设置说明必须写明超期的定义')
assert(
  zh.warehouse.priceUpdateTasks.settings.description.includes('报表中心') && zh.warehouse.priceUpdateTasks.settings.description.includes('管理员和仓库管理员不会被锁'),
  '设置说明必须写明店长会被锁报表中心、管理员与仓库管理员不锁',
)

// 页面与共用工具引用的文案键都必须存在。
const usedKeys = new Set<string>()
for (const source of [pageSource, read('pages/Warehouse/PriceUpdateTasks/logic.ts')]) {
  for (const match of source.matchAll(/\$\{I18N\}\.([A-Za-z]+(?:\.[A-Za-z]+)*)[`.]/g)) usedKeys.add(`priceUpdateTasks.${match[1]}`)
}
for (const source of [productsSource, read('utils/priceNotification.ts')]) {
  for (const match of source.matchAll(/'warehouse\.(priceNotification\.[A-Za-z]+)'/g)) usedKeys.add(match[1])
}
assert(usedKeys.size > 40, '应能从源码中提取到文案键')
for (const key of usedKeys) {
  const value = key.split('.').reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], zh.warehouse)
  assert(value !== undefined, `缺少文案 warehouse.${key}`)
}

console.log('priceUpdateTasks.sourceContract.test: ok')
