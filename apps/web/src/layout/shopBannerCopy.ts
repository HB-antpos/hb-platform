export interface ShopBannerCopy {
  titleKey: string
  titleFallback: string
  subtitleKey: string
  subtitleFallback: string
}

const orderHistoryBannerCopy: ShopBannerCopy = {
  titleKey: 'shop.orderHistory',
  titleFallback: '历史订单',
  subtitleKey: 'shop.ordersBannerSubtitle',
  subtitleFallback: '查看分店提交过的订单、状态、数量和金额汇总。',
}

export function resolveShopBannerCopy(pathname: string): ShopBannerCopy {
  if (pathname.startsWith('/shop/local-supplier-invoices')) {
    return {
      titleKey: 'shop.localSupplierInvoices',
      titleFallback: '澳洲本地进货单',
      subtitleKey: 'shop.localSupplierInvoicesBannerSubtitle',
      subtitleFallback: '按分店和本地供应商查看进货单、商品明细及入库状态。',
    }
  }

  if (pathname.startsWith('/shop/purchase-sales-analysis')) {
    // 副标题键随页面代码块懒注册；横幅在商城布局下隐藏，这里仅保证标题语义正确。
    return {
      titleKey: 'shop.purchaseSalesAnalysis',
      titleFallback: '进货销量分析',
      subtitleKey: 'shop.purchaseSalesAnalysisSubtitle',
      subtitleFallback: '按供应商和订单日期范围查看本店商品最近进货与进货后的每日销量。',
    }
  }

  if (pathname.startsWith('/shop/supply-watches')) {
    return {
      titleKey: 'shop.supplyWatches',
      titleFallback: '我关注的商品',
      subtitleKey: 'shop.supplyWatchesSubtitle',
      subtitleFallback: '关注暂停供货的商品，恢复订货时在这里提醒你。',
    }
  }

  if (pathname.startsWith('/shop/preorders/')) {
    return {
      titleKey: 'shop.preorderTitle',
      titleFallback: 'Preorder 预订货',
      subtitleKey: 'shop.preorderBannerSubtitle',
      subtitleFallback: '按最小订货量填写本期份数，提交后继续处理下一期。',
    }
  }

  if (pathname.startsWith('/shop/best-sellers')) {
    return {
      titleKey: 'shop.bestSellers',
      titleFallback: '热销商品',
      subtitleKey: 'shop.bestSellersBannerSubtitle',
      subtitleFallback: '查看热销商品销量、销售额和排名汇总。',
    }
  }

  if (pathname.startsWith('/shop/coming-soon')) {
    return {
      titleKey: 'shop.comingSoon',
      titleFallback: '即将上新',
      subtitleKey: 'shop.comingSoonBannerSubtitle',
      subtitleFallback: '按货柜到仓库日期：过去 1 周至未来 3 周，快速筛选补货和新品。',
    }
  }

  return orderHistoryBannerCopy
}
