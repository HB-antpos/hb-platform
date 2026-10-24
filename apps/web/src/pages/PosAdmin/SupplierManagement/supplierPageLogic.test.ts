import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import {
  buildRecentInvoicesRequest,
  buildSupplierInvoicesLink,
  describeSupplierImageTemplate,
  formatPurchaseDate,
  parsePreviewItemNumbers,
  validateSupplierImageTemplateInput,
} from './supplierPageLogic'
import messagesEn from './supplierMessages.en.json'
import messagesZh from './supplierMessages.zh.json'
import globalEn from '../../../i18n/locales/en.json'
import globalZh from '../../../i18n/locales/zh.json'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

// 商品图片列：单独配置 → 域名；没配但有内置预设 → 预设；都没有 → 未配置
assertEqual(
  JSON.stringify(describeSupplierImageTemplate({ localSupplierCode: 'X1', imageBaseUrl: 'https://www.dats.com.au/images/{itemNumber}.jpg' })),
  '{"kind":"configured","host":"dats.com.au"}',
  '配置了模板显示去掉 www 的域名',
)
assertEqual(
  describeSupplierImageTemplate({
    localSupplierCode: '250',
    imageBaseUrl: 'https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/{supplierCode}/{itemNumber}.jpg',
  }).kind,
  'cos',
  '公司 COS 图床单独标出',
)
assertEqual(
  describeSupplierImageTemplate({ localSupplierCode: 'X1', imageBaseUrl: 'not a url {itemNumber}' }).kind,
  'configured',
  '模板不是合法 URL 也算已配置（域名为空）',
)
assertEqual(
  JSON.stringify(describeSupplierImageTemplate({ localSupplierCode: 'Malmar', imageBaseUrl: '  ' })),
  '{"kind":"preset","name":"MALMAR"}',
  '空白模板 + 代码命中预设（不区分大小写）',
)
assertEqual(describeSupplierImageTemplate({ localSupplierCode: 'GFA', imageBaseUrl: undefined }).kind, 'missing', '没有模板也没有预设')

// 模板校验：可不填；填了必须含 {itemNumber}；不超过 512
assertEqual(validateSupplierImageTemplateInput(''), null, '空模板允许')
assertEqual(validateSupplierImageTemplateInput('   '), null, '空白视为不填')
assertEqual(validateSupplierImageTemplateInput('https://a.com/{itemNumber}.jpg'), null, '合法模板')
assertEqual(validateSupplierImageTemplateInput('https://a.com/img.jpg'), 'missingPlaceholder', '缺 {itemNumber}')
assertEqual(validateSupplierImageTemplateInput(`https://a.com/${'x'.repeat(520)}{itemNumber}`), 'tooLong', '超长')

// 预览货号：空白 / 逗号分隔、去重、最多 3 个
assertEqual(parsePreviewItemNumbers(' DA1, DA2  DA1，DA3 DA4').join('|'), 'DA1|DA2|DA3', '去重并截到 3 个')
assertEqual(parsePreviewItemNumbers('   ').length, 0, '空输入没有货号')

assertEqual(formatPurchaseDate('2026-10-21T00:00:00'), '2026-10-21', '只显示到天')
assertEqual(formatPurchaseDate(null), null, '没有进货')
assertEqual(buildSupplierInvoicesLink('A&B 1'), '/pos-admin/local-supplier-invoices?supplierCode=A%26B%201', '代码要编码')

const request = buildRecentInvoicesRequest('DATS', 3)
assertEqual(request.endRow, 3, '只取前 3 张')
assertEqual(request.sortModel[0].colId, 'orderDate', '按订单日期')
assertEqual(request.sortModel[0].sort, 'desc', '倒序')
assertEqual(request.filterModel.supplierCode.filter, 'DATS', '按供应商代码精确过滤')

// 文案契约：源码里的 t('键') 必须在「全局 ∪ 页面」中英文都存在；两份页面文案键集合一致
type Messages = Record<string, unknown>
function hasKey(messages: Messages, key: string): boolean {
  let node: unknown = messages
  for (const part of key.split('.')) {
    if (!node || typeof node !== 'object' || !(part in (node as Messages))) return false
    node = (node as Messages)[part]
  }
  return typeof node === 'string'
}
function collectKeys(messages: Messages, prefix = ''): string[] {
  return Object.entries(messages).flatMap(([key, value]) =>
    value && typeof value === 'object'
      ? collectKeys(value as Messages, `${prefix}${key}.`)
      : [`${prefix}${key}`])
}
assertEqual(
  collectKeys(messagesZh).sort().join('\n'),
  collectKeys(messagesEn).sort().join('\n'),
  '中英文页面文案键集合必须一致',
)
for (const file of ['index.tsx', 'SupplierDrawer.tsx']) {
  const source = readFileSync(resolve(`src/pages/PosAdmin/SupplierManagement/${file}`), 'utf8')
  for (const match of source.matchAll(/\bt\(\s*'([A-Za-z0-9_.]+)'/g)) {
    const key = match[1]
    for (const [language, page, global] of [['zh', messagesZh, globalZh], ['en', messagesEn, globalEn]] as const) {
      assertEqual(
        hasKey(page as Messages, key) || hasKey(global as Messages, key),
        true,
        `${file} 使用的文案键 ${key} 在 ${language} 中缺失`,
      )
    }
  }
}

console.log('supplierPageLogic.test: ok')
