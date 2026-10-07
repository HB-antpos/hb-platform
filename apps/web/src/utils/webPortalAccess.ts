import type { AccessControl } from '../types/auth'
import { P } from '../types/permissions'

type WebPortalAccess = Pick<
  AccessControl,
  | 'isAdmin'
  | 'onlyOrder'
  | 'canAccessDashboard'
  | 'canAccessOrderFront'
  | 'canViewShopBatchProductSales'
  | 'canManageWarehouse'
  | 'canManageWarehouseOrders'
  | 'canManageStoreOrderImportPriceVariance'
  | 'canViewContainers'
  | 'canViewProductMovementReport'
  | 'canManageLocalPurchase'
  | 'canEditLocalPurchase'
  | 'canManageSystemSettings'
  | 'canViewAppDownloads'
  | 'canViewPerformanceBaseline'
  | 'canViewOperationAudits'
  | 'canViewDeviceRegistration'
  | 'hasPermission'
>

export const WEB_NO_ACCESS_PATH = '/web-access-denied'

type AdminEntryRule = {
  defaultPath: string
  targetPrefixes: readonly string[]
  canAccess: (access: BackendNavigationAccess) => boolean
}

// 前台货号销量权限只影响订货前台，不参与后台入口判定。
export type BackendNavigationAccess = Omit<WebPortalAccess, 'canAccessOrderFront' | 'onlyOrder' | 'canViewShopBatchProductSales'>

// 与后端 NavigationService.HasBackendNavigationAccess 的权限集合保持同一入口语义。
const ADMIN_ENTRY_RULES: readonly AdminEntryRule[] = [
  {
    defaultPath: '/dashboard',
    targetPrefixes: ['/dashboard'],
    canAccess: (access) => access.canAccessDashboard,
  },
  {
    defaultPath: '/warehouse/store-orders',
    targetPrefixes: [
      '/warehouse/store-orders',
      '/warehouse/preorders',
      '/warehouse/store-order',
    ],
    canAccess: (access) => access.canManageWarehouseOrders,
  },
  {
    defaultPath: '/warehouse/store-order-import-price-variance',
    targetPrefixes: ['/warehouse/store-order-import-price-variance'],
    canAccess: (access) => access.canManageStoreOrderImportPriceVariance,
  },
  {
    defaultPath: '/warehouse/store-orders',
    // 旧 Warehouse.Manage 只覆盖其实际派生的仓库业务页，不能绕过价差或货柜叶子权限。
    targetPrefixes: [
      '/warehouse/products',
      '/warehouse/categories',
      '/warehouse/locations',
      '/warehouse/product-grade-management',
    ],
    canAccess: (access) => access.canManageWarehouse,
  },
  {
    defaultPath: '/warehouse/containers',
    targetPrefixes: [
      '/warehouse/containers',
      '/warehouse/container/detail',
      '/warehouse/container/allocation-sales',
    ],
    canAccess: (access) => access.canViewContainers,
  },
  // 每个销售页面独立授权；仅有单页权限时，登录后直接落到该页。
  // 进货销量分析合并了批量货号销量与分店进货销量分析：任一标签权限即可落地，
  // 两个旧地址仍视为本页入口，由路由重定向到对应标签后再按权限选标签。
  ...([
    ['overview', [P.SalesDashboard.SalesDataView]],
    ['sales-detail-v2', [P.SalesDashboard.SalesDetailView]],
    ['compact-sales-board', [P.SalesDashboard.CompactBoardView]],
    ['monthly-daily-sales-download', [P.SalesDashboard.MonthlyDailySalesDownloadView]],
    ['product-movement-report', [P.SalesDashboard.ProductMovementView]],
    [
      'purchase-sales-analysis',
      [P.SalesDashboard.BatchProductSalesView, P.SalesDashboard.LocalSupplierPurchaseSalesView],
      ['batch-product-sales-analysis', 'local-supplier-purchase-sales-analysis'],
    ],
    ['warehouse-product-flow-analysis', [P.SalesDashboard.WarehouseFlowView], ['product-sales-analysis']],
    ['local-product-sales-analysis', [P.SalesDashboard.LocalProductAnalysisView]],
    ['purchase-amount-dashboard', [P.SalesDashboard.PurchaseAmountView]],
  ] as [string, string[], string[]?][]).map(([path, permissions, legacyPaths = []]) => ({
    defaultPath: `/executive-sales-intelligence/${path}`,
    targetPrefixes: [path, ...legacyPaths].map((item) => `/executive-sales-intelligence/${item}`),
    canAccess: (access: BackendNavigationAccess) => permissions.some((permission) => access.hasPermission(permission)),
  })),
  {
    defaultPath: '/pos-admin/local-supplier-invoices',
    targetPrefixes: [
      '/pos-admin/local-supplier-invoices',
      '/pos-admin/invoice-detail',
    ],
    canAccess: (access) => access.canManageLocalPurchase,
  },
  {
    defaultPath: '/system/invoice-email-settings',
    targetPrefixes: [
      '/system/invoice-email-settings',
      '/system/payment-terminal-settings',
      '/system/emergency-login-keys',
    ],
    canAccess: (access) => access.canManageSystemSettings,
  },
  {
    defaultPath: '/system/app-downloads',
    targetPrefixes: ['/system/app-downloads', '/system/wpf-versions'],
    canAccess: (access) => access.canViewAppDownloads,
  },
  {
    defaultPath: '/system/device-registration',
    targetPrefixes: ['/system/device-registration'],
    canAccess: (access) => access.canViewDeviceRegistration,
  },
  {
    // 员工操作日志合并页：新收银或老收银任一查看权限；旧老系统地址重定向到这里。
    defaultPath: '/pos-admin/operation-logs',
    targetPrefixes: ['/pos-admin/operation-logs', '/pos-admin/legacy-employee-logs'],
    canAccess: (access) => access.canViewOperationAudits || access.hasPermission(P.LegacyEmployeeLogs.View),
  },
  {
    // 日结记录：独立权限码，只有它的账号也要能进入后台；放在既有入口之后，组合权限用户继续沿用原默认入口。
    defaultPath: '/pos-admin/daily-closes',
    targetPrefixes: ['/pos-admin/daily-closes'],
    canAccess: (access) => access.hasPermission(P.DailyCloseRecords.View),
  },
  {
    // 放在既有入口之后，组合权限用户继续沿用原默认入口。
    defaultPath: '/system/performance-baseline',
    targetPrefixes: ['/system/performance-baseline'],
    canAccess: (access) => access.canViewPerformanceBaseline,
  },
]

function matchesRoutePrefix(target: string, prefix: string) {
  return (
    target === prefix ||
    target.startsWith(`${prefix}/`) ||
    target.startsWith(`${prefix}?`) ||
    target.startsWith(`${prefix}#`)
  )
}

export function hasBackendNavigationAccess(access: BackendNavigationAccess) {
  return access.isAdmin || ADMIN_ENTRY_RULES.some((rule) => rule.canAccess(access))
}

export function getDefaultWebPath(access: WebPortalAccess) {
  if (access.onlyOrder) {
    return '/shop'
  }
  const adminEntry = ADMIN_ENTRY_RULES.find((rule) => rule.canAccess(access))
  if (adminEntry) {
    return adminEntry.defaultPath
  }
  if (access.canAccessOrderFront) {
    return '/shop'
  }
  return WEB_NO_ACCESS_PATH
}

export function resolveAuthorizedWebTarget(target: string | null | undefined, access: WebPortalAccess) {
  if (!target || !target.startsWith('/') || target.startsWith('//') || target === '/login') {
    return undefined
  }
  // 旧货号销量地址（现重定向到进货销量分析的「粘贴数据查看」标签）需单独授权；未授权时不保留历史地址，由默认落点（纯订货角色为 /shop）接管。
  if (target.startsWith('/shop/batch-product-sales')) {
    return access.canAccessOrderFront && access.canViewShopBatchProductSales ? target : undefined
  }
  // 纯订货角色不保留任何后台历史地址，只允许回到订货前台及其现有子路由。
  if (access.onlyOrder) {
    return /^\/shop(?:\/|[?#]|$)/.test(target) ? target : '/shop'
  }
  if (target === '/') {
    return target
  }
  if (target === WEB_NO_ACCESS_PATH) {
    return !hasBackendNavigationAccess(access) && !access.canAccessOrderFront ? target : undefined
  }
  if (/^\/shop(?:\/|[?#]|$)/.test(target)) {
    return access.canAccessOrderFront ? target : undefined
  }

  // 编辑页与只读详情共用进货单路径前缀，必须在父级查看权限匹配前单独核验编辑权限。
  if (/^\/pos-admin\/local-supplier-invoices\/[^/?#]+\/?(?:[?#]|$)/.test(target)) {
    return access.canEditLocalPurchase ? target : undefined
  }

  // 后台壳只负责放行入口，具体重定向目标仍按对应叶子权限核验。
  const authorizedRule = ADMIN_ENTRY_RULES.find(
    (rule) =>
      rule.canAccess(access) &&
      rule.targetPrefixes.some((prefix) => matchesRoutePrefix(target, prefix)),
  )
  if (authorizedRule) {
    return target
  }
  return access.isAdmin ? target : undefined
}
