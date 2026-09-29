// 内置默认供应商 profile（DATS 列表页）
export const DEFAULT_PROFILES = {
  configVersion: '3',
  profiles: [
    {
      // DATS 是显示名称；HB 的供应商业务代码是 240。
      supplierCode: '240',
      displayName: 'DATS',
      enabled: true,
      origins: ['https://www.dats.com.au/*'],
      listPagePatterns: ['https://www.dats.com.au/*'],
      cardSelector: '.product[data-product-code]',
      itemNumber: {
        source: 'attribute',
        selector: null,
        attribute: 'data-product-code',
        transforms: ['trim', 'uppercase'],
      },
      mountSelector: '.widget-productlist-code',
      mountPosition: 'afterend',
      // 供应商分类采集（1.5.0+）：离线回退时使用；联网后以后端下发的 category 块为准。
      // 以下选择器已于 2026-09-23 对公开页 /、/office-stationery、/office-stationery/adhesives-and-tape 核实；
      // 登录后的页面结构待登录核实，若不同由后端配置热更新修正（递增 ConfigVersion，无需发版）。
      category: {
        enabled: true,
        passiveEnabled: true,
        crawlEnabled: true,
        categoryPagePatterns: ['https://www.dats.com.au/*'],
        categoryExcludePatterns: [],
        // 面包屑：首项 Home 只有图标，名称在 meta[itemprop=name]；末项无链接，URL 在 meta/data-url。
        breadcrumbSelector: '.widget-breadcrumb li[itemprop="itemListElement"]',
        breadcrumbSkip: 1,
        titleSelector: 'h1.page-title, h1',
        keySource: 'pathname',
        keyQueryParams: [],
        navRootUrl: 'https://www.dats.com.au/',
        // 顶部 mega menu（标题链接 + 兄弟 ul）与分类页侧栏分类树（li 嵌套）两个候选，按 key 去重合并。
        navSelector: '.widget-navigation-menu .dropdown-area a[href], .widget-product-category-list a.box-title',
        // 分类页侧栏会列出整棵树；采集器只接受当前分类路径下的子链接，其余忽略。
        subcategoryLinkSelector: '.widget-product-category-list a.box-title',
        // DATS 分页写在 <head> 的 link[rel=next]（?PageProduct=2&PageSizeProduct=24）。
        paginationNextSelector: 'link[rel="next"], a[rel="next"]',
        maxPages: 20,
        maxDepth: 4,
        maxCategories: 400,
        crawlDelayMs: 1500,
        promotionalPatterns: [],
      },
    },
  ],
};
