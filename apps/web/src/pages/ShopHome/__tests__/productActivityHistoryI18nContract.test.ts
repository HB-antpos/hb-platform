import { readFileSync } from 'node:fs'
import path from 'node:path'
import en from '../../../i18n/locales/en.json'
import zh from '../../../i18n/locales/zh.json'
import productCardEn from '../components/productCardMessages.en.json'
import productCardZh from '../components/productCardMessages.zh.json'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

const requiredKeys = [
  'title',
  'productName',
  'itemNumber',
  'store',
  'lastArrival',
  'latestOrder',
  'latestShipment',
  'salesSinceArrival',
  'notRealtime',
  'filter',
  'filterAll',
  'filterOrder',
  'filterSales',
  'date',
  'type',
  'typeOrder',
  'typeSales',
  'typeSalesDetails',
  'typeSubtotal',
  'orderNo',
  'orderQuantity',
  'shipQuantity',
  'outboundDate',
  'salesQuantity',
  'averagePrice',
  'status',
  'lastOrder',
  'entryTitle',
  'entryAria',
  'empty',
  'loadFailed',
  'retry',
]

for (const [locale, messages] of Object.entries({ en, zh })) {
  const namespace = messages.shop.productActivityHistory as unknown as Record<string, unknown>
  assertEqual(typeof namespace, 'object', `${locale} 商品活动历史必须使用独立命名空间`)

  for (const key of requiredKeys) {
    assertEqual(
      typeof namespace[key],
      'string',
      `${locale} shop.productActivityHistory.${key} 必须是字符串`,
    )
  }
}

// 卡片文案拆在全局与页面级两处：源码里引用的每个键都必须在「全局 ∪ 卡片页面消息」中英文都存在，
// 且两份页面消息键集合一致，否则界面会直接显示原始键名。
const cardZh = productCardZh.shop.productActivityHistory as Record<string, unknown>
const cardEn = productCardEn.shop.productActivityHistory as Record<string, unknown>
assertEqual(
  JSON.stringify(Object.keys(cardZh).sort()),
  JSON.stringify(Object.keys(cardEn).sort()),
  '卡片页面消息中英文键集合必须一致',
)
const cardSource = readFileSync(
  path.resolve(process.cwd(), 'src/pages/ShopHome/components/ProductCard.tsx'),
  'utf8',
)
const cardKeys = [...cardSource.matchAll(/'shop\.productActivityHistory\.([A-Za-z]+)'/g)].map((match) => match[1])
assertEqual(cardKeys.length > 0, true, '卡片源码应引用 shop.productActivityHistory 文案')
for (const [locale, globalMessages, cardMessages] of [
  ['zh', zh, cardZh],
  ['en', en, cardEn],
] as const) {
  const globalNamespace = globalMessages.shop.productActivityHistory as unknown as Record<string, unknown>
  for (const key of cardKeys) {
    assertEqual(
      typeof (cardMessages[key] ?? globalNamespace[key]),
      'string',
      `${locale} 卡片引用的 shop.productActivityHistory.${key} 缺少文案`,
    )
  }
}

console.log('productActivityHistoryI18nContract.test: ok')
