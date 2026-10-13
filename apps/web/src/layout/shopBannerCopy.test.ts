import { resolveShopBannerCopy } from './shopBannerCopy'
import { shopNavMessages } from './shopNavMessages'
import en from '../i18n/locales/en.json'
import zh from '../i18n/locales/zh.json'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

const bestSellersCopy = resolveShopBannerCopy('/shop/best-sellers')
assertEqual(bestSellersCopy.titleKey, 'shop.bestSellers', '热销商品页标题应使用热销商品文案')
assertEqual(
  bestSellersCopy.subtitleKey,
  'shop.bestSellersBannerSubtitle',
  '热销商品页副标题不应复用历史订单文案',
)

const comingSoonCopy = resolveShopBannerCopy('/shop/coming-soon')
assertEqual(comingSoonCopy.titleKey, 'shop.comingSoon', '即将上新页标题应使用即将上新文案')
assertEqual(
  comingSoonCopy.subtitleKey,
  'shop.comingSoonBannerSubtitle',
  '即将上新页副标题不应复用历史订单文案',
)
// 即将上新按货柜到仓库日期（过去 1 周～未来 3 周）统一口径：兜底文案须与中文资源一致，两种语言都要点明「到仓库 / warehouse」
assertEqual(comingSoonCopy.subtitleFallback, zh.shop.comingSoonBannerSubtitle, '即将上新副标题兜底文案应与中文资源一致')
assertEqual(zh.shop.comingSoonBannerSubtitle.includes('到仓库日期'), true, '中文副标题应说明按到仓库日期')
assertEqual(en.shop.comingSoonBannerSubtitle.includes('warehouse arrival'), true, '英文副标题应说明按 warehouse arrival')

// 货号销量已并入进货销量分析的「粘贴数据查看」标签，独立导航文案不应再留在首屏主文案包。
assertEqual((en.shop as Record<string, unknown>).batchProductSales, undefined, '英文主文案包不应保留货号销量导航文案')
assertEqual((zh.shop as Record<string, unknown>).batchProductSales, undefined, '中文主文案包不应保留货号销量导航文案')

const purchaseSalesCopy = resolveShopBannerCopy('/shop/purchase-sales-analysis')
assertEqual(purchaseSalesCopy.titleKey, 'shop.purchaseSalesAnalysis', '进货销量分析页标题应使用专属文案')
assertEqual(purchaseSalesCopy.subtitleKey, 'shop.purchaseSalesAnalysisSubtitle', '进货销量分析页副标题不应复用历史订单文案')
assertEqual(shopNavMessages.en.shop.purchaseSalesAnalysis, 'Purchase & Sales', '英文进货销量分析导航文案应随布局懒注册')
assertEqual(shopNavMessages.zh.shop.purchaseSalesAnalysis, '进货销量分析', '中文进货销量分析导航文案应随布局懒注册')
assertEqual((zh.shop as Record<string, unknown>).purchaseSalesAnalysis, undefined, '导航增量文案不得进入首屏主文案包')

// 我关注的商品：入口文案随布局懒注册，横幅用专属标题与副标题。
const supplyWatchesCopy = resolveShopBannerCopy('/shop/supply-watches')
assertEqual(supplyWatchesCopy.titleKey, 'shop.supplyWatches', '我关注的商品页标题应使用专属文案')
assertEqual(supplyWatchesCopy.subtitleKey, 'shop.supplyWatchesSubtitle', '我关注的商品页副标题不应复用历史订单文案')
assertEqual(shopNavMessages.zh.shop.supplyWatches, '我关注的商品', '中文关注导航文案应随布局懒注册')
assertEqual(shopNavMessages.en.shop.supplyWatches, 'Watched', '英文关注导航文案应随布局懒注册')
assertEqual((zh.shop as Record<string, unknown>).supplyWatches, undefined, '关注导航文案不得进入首屏主文案包')

const ordersCopy = resolveShopBannerCopy('/shop/orders')
assertEqual(ordersCopy.titleKey, 'shop.orderHistory', '历史订单页标题应保持历史订单文案')
assertEqual(ordersCopy.subtitleKey, 'shop.ordersBannerSubtitle', '历史订单页副标题应保持订单汇总文案')

const orderDetailCopy = resolveShopBannerCopy('/shop/orders/order-1')
assertEqual(orderDetailCopy.titleKey, 'shop.orderHistory', '历史订单详情页标题应保持历史订单文案')
assertEqual(orderDetailCopy.subtitleKey, 'shop.ordersBannerSubtitle', '历史订单详情页副标题应保持订单汇总文案')

const localInvoicesCopy = resolveShopBannerCopy('/shop/local-supplier-invoices')
assertEqual(
  localInvoicesCopy.titleKey,
  'shop.localSupplierInvoices',
  '澳洲本地进货单列表页应使用专属标题',
)
assertEqual(
  localInvoicesCopy.subtitleKey,
  'shop.localSupplierInvoicesBannerSubtitle',
  '澳洲本地进货单列表页应使用专属副标题',
)

const localInvoiceDetailCopy = resolveShopBannerCopy('/shop/local-supplier-invoices/invoice-1')
assertEqual(
  localInvoiceDetailCopy.titleKey,
  'shop.localSupplierInvoices',
  '澳洲本地进货单明细页应保持专属标题',
)
assertEqual(
  localInvoiceDetailCopy.subtitleKey,
  'shop.localSupplierInvoicesBannerSubtitle',
  '澳洲本地进货单明细页应保持专属副标题',
)

const preorderCopy = resolveShopBannerCopy('/shop/preorders/preorder-1')
assertEqual(preorderCopy.titleKey, 'shop.preorderTitle', '预订货页标题应使用预订货文案')
assertEqual(
  preorderCopy.subtitleKey,
  'shop.preorderBannerSubtitle',
  '预订货页副标题应使用预订货说明文案',
)
assertEqual(en.shop.preorderTitle, 'Preorder', '英文预订货标题资源应存在')
assertEqual(
  en.shop.preorderBannerSubtitle,
  "Enter pack quantities for this period based on each item's MOQ, then submit to continue to the next period.",
  '英文预订货副标题资源应完整',
)
assertEqual(zh.shop.preorderTitle, '预订货', '中文预订货标题资源应存在')
assertEqual(
  zh.shop.preorderBannerSubtitle,
  '按最小订货量填写本期份数，提交后继续处理下一期。',
  '中文预订货副标题资源应与页面语义一致',
)
assertEqual(en.shop.localSupplierInvoices, 'Australian Local Invoices', '英文澳洲本地进货单标题资源应存在')
assertEqual(
  en.shop.localSupplierInvoicesBannerSubtitle,
  'View local purchase invoices, supplier details, product lines, and receiving status by store.',
  '英文澳洲本地进货单副标题资源应完整',
)
assertEqual(zh.shop.localSupplierInvoices, '澳洲本地进货单', '中文澳洲本地进货单标题资源应存在')
assertEqual(
  zh.shop.localSupplierInvoicesBannerSubtitle,
  '按分店和本地供应商查看进货单、商品明细及入库状态。',
  '中文澳洲本地进货单副标题资源应与页面语义一致',
)

console.log('shopBannerCopy.test: ok')
