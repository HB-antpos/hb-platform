import {
  AppstoreOutlined,
  BankOutlined,
  BarChartOutlined,
  BuildOutlined,
  CalendarOutlined,
  DashboardOutlined,
  DatabaseOutlined,
  DollarOutlined,
  DownloadOutlined,
  EnvironmentOutlined,
  FileDoneOutlined,
  FileTextOutlined,
  GiftOutlined,
  InboxOutlined,
  IdcardOutlined,
  KeyOutlined,
  MailOutlined,
  MoneyCollectOutlined,
  NumberOutlined,
  PictureOutlined,
  QrcodeOutlined,
  ReconciliationOutlined,
  ScheduleOutlined,
  SettingOutlined,
  ShopOutlined,
  ShoppingCartOutlined,
  TagsOutlined,
  TeamOutlined,
  TrophyOutlined,
  UserOutlined,
  WalletOutlined,
} from '@ant-design/icons'
import type { MenuProps } from 'antd'
import { isValidElement, lazy } from 'react'
import { matchPath, Navigate } from 'react-router-dom'
import i18n from '../i18n'
import {
  ADMIN_PURCHASE_SALES_PATH,
  buildAdminPurchaseSalesTabPath,
} from '../pages/ExecutiveSalesIntelligence/PurchaseSalesAnalysis/tabs'
import ForbiddenPage from '../pages/Forbidden'
import NotFoundPage from '../pages/NotFound'
import type { AccessControl } from '../types/auth'
import type { NavigationMenuDto } from '../types/auth'
import type { AppRouteItem, AppRouteMeta, TabItem } from '../types/router'
import { chooseNavigationMenus } from './menuFallback'
import { shouldIncludeLocalMenuRoute } from './menuVisibility'

const DashboardPage = lazy(() => import('../pages/Dashboard'))
const DomesticChinaSuppliersPage = lazy(() => import('../pages/DomesticPurchase/ChinaSuppliers'))
const DomesticProductsPage = lazy(() => import('../pages/DomesticPurchase/DomesticProducts'))
const ProductPrefixCodeManagementPage = lazy(() => import('../pages/DomesticPurchase/ProductPrefixCodeManagement'))
const ProductCreationPage = lazy(() => import('../pages/DomesticPurchase/ProductCreation'))
const ProductImportPage = lazy(() => import('../pages/DomesticPurchase/ProductImport'))
const ProductGradeManagementPage = lazy(() => import('../pages/Warehouse/ProductGradeManagement'))
const ExecutiveSalesIntelligencePage = lazy(() => import('../pages/ExecutiveSalesIntelligence'))
const CompactSalesBoardPage = lazy(() => import('../pages/ExecutiveSalesIntelligence/CompactSalesBoard'))
const SalesDetailAnalysisPage = lazy(() => import('../pages/ExecutiveSalesIntelligence/SalesDetailAnalysisV2'))
const ProductMovementReportPage = lazy(() => import('../pages/ExecutiveSalesIntelligence/ProductMovementReport'))
const WarehouseProductFlowAnalysisPage = lazy(() => import('../pages/ExecutiveSalesIntelligence/WarehouseProductFlowAnalysis'))
const PurchaseSalesAnalysisPage = lazy(() => import('../pages/ExecutiveSalesIntelligence/PurchaseSalesAnalysis'))
const LocalProductSalesAnalysisPage = lazy(() => import('../pages/ExecutiveSalesIntelligence/LocalProductSalesAnalysis'))
const PurchaseAmountDashboardPage = lazy(() => import('../pages/ExecutiveSalesIntelligence/PurchaseAmountDashboard'))
const PosmSalesOrdersPage = lazy(() => import('../pages/PosmSalesOrders'))
const PosAdminCashRegisterUsersPage = lazy(() => import('../pages/PosAdmin/CashRegisterUsers'))
const PosAdminPricingStrategiesPage = lazy(() => import('../pages/PosAdmin/PricingStrategies'))
const PosAdminPromotionsPage = lazy(() => import('../pages/PosAdmin/Promotions'))
const PosAdminScheduleAttendancePage = lazy(() => import('../pages/PosAdmin/ScheduleAttendance'))
const PosAdminDeviceRegistrationPage = lazy(() => import('../pages/PosAdmin/DeviceRegistration'))
const PosAdminSupplierManagementPage = lazy(() => import('../pages/PosAdmin/SupplierManagement'))
const PosAdminProductManagementPage = lazy(() => import('../pages/PosAdmin/ProductManagement'))
const PosAdminStoreProductPricePage = lazy(() => import('../pages/PosAdmin/StoreProductPrice'))
const PosAdminAdvertisementsPage = lazy(() => import('../pages/PosAdmin/Advertisements'))
// 员工操作日志合并页：老收银 / 新收银两个来源在页内切换。
const PosAdminEmployeeLogsPage = lazy(() => import('../pages/PosAdmin/EmployeeLogs'))
const LinklySettlementsPage = lazy(() => import('../pages/PosAdmin/LinklySettlements'))
const LinklySettlementDetailPage = lazy(() => import('../pages/PosAdmin/LinklySettlementDetail'))
const LocalSupplierInvoicesPage = lazy(() => import('../pages/PosAdmin/LocalSupplierInvoices'))
const LocalSupplierInvoiceDetailPage = lazy(() => import('../pages/PosAdmin/LocalSupplierInvoiceDetailPage'))
const LocalSupplierInvoiceSalesAnalysisPage = lazy(() => import('../pages/PosAdmin/LocalSupplierInvoiceSalesAnalysis'))
const InvoiceEditPage = lazy(() => import('../pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit'))
const SystemAppDownloadsPage = lazy(() => import('../pages/System/AppDownloads'))
const SystemRemoteMaintenancePage = lazy(() => import('../pages/System/RemoteMaintenance'))
const SystemCenterLogsPage = lazy(() => import('../pages/System/CenterLogs'))
const SystemPerformanceBaselinePage = lazy(() => import('../pages/System/PerformanceBaseline'))
const EmergencyLoginKeysPage = lazy(() => import('../pages/System/EmergencyLoginKeys'))
const InvoiceEmailSettingsPage = lazy(() => import('../pages/System/InvoiceEmailSettings'))
const PaymentTerminalSettingsPage = lazy(() => import('../pages/System/PaymentTerminalSettings'))
const SystemScheduledStatisticsPage = lazy(() => import('../pages/System/ScheduledStatistics'))
const SystemRolesPage = lazy(() => import('../pages/System/Roles'))
const SystemStoresPage = lazy(() => import('../pages/System/Stores'))
const SystemEmployeeProfilesPage = lazy(() => import('../pages/System/EmployeeProfiles'))
const SystemUsersPage = lazy(() => import('../pages/System/Users'))
const SystemPermissionsPage = lazy(() => import('../pages/System/Permissions'))
const WarehouseCategoriesPage = lazy(() => import('../pages/Warehouse/Categories'))
const ContainerDetailPage = lazy(() => import('../pages/Warehouse/ContainerDetail'))
const ContainerAllocationSalesPage = lazy(() => import('../pages/Warehouse/ContainerAllocationSales'))
const ContainersPage = lazy(() => import('../pages/Warehouse/Containers'))
const WarehouseLocationsPage = lazy(() => import('../pages/Warehouse/Locations'))
const WarehouseProductsPage = lazy(() => import('../pages/Warehouse/Products'))
const RetailPriceChangesPage = lazy(() => import('../pages/Warehouse/RetailPriceChanges'))
const PriceUpdateTasksPage = lazy(() => import('../pages/Warehouse/PriceUpdateTasks'))
const WarehouseProductRecordsPage = lazy(() => import('../pages/Warehouse/ProductRecords'))
const StoreOrderDetailPage = lazy(() => import('../pages/Warehouse/StoreOrders/Detail'))
const StoreOrderInvoicePage = lazy(() => import('../pages/Warehouse/StoreOrders/Invoice'))
const StoreOrderPickingListPage = lazy(() => import('../pages/Warehouse/StoreOrders/PickingList'))
const StoreOrderPickingSlipsPage = lazy(() => import('../pages/Warehouse/StoreOrders/pickingAssignment/PickingSlipsPage'))
const StoreOrdersPage = lazy(() => import('../pages/Warehouse/StoreOrders'))
const StoreOrderImportPriceVariancePage = lazy(() => import('../pages/Warehouse/StoreOrderImportPriceVariance'))
const PreordersPage = lazy(() => import('../pages/Warehouse/Preorders'))
const PreorderActivationDetailPage = lazy(() => import('../pages/Warehouse/Preorders/ActivationDetail'))

export interface ResolvedRoute extends AppRouteItem {
  parentPaths: string[]
  params: Record<string, string>
}

interface LeafEntry {
  route: AppRouteItem
  parentPaths: string[]
}

const iconMap = {
  DashboardOutlined: <DashboardOutlined />,
  AppstoreOutlined: <AppstoreOutlined />,
  BankOutlined: <BankOutlined />,
  BarChartOutlined: <BarChartOutlined />,
  BuildOutlined: <BuildOutlined />,
  CalendarOutlined: <CalendarOutlined />,
  DatabaseOutlined: <DatabaseOutlined />,
  DollarOutlined: <DollarOutlined />,
  DownloadOutlined: <DownloadOutlined />,
  EnvironmentOutlined: <EnvironmentOutlined />,
  FileDoneOutlined: <FileDoneOutlined />,
  FileTextOutlined: <FileTextOutlined />,
  GiftOutlined: <GiftOutlined />,
  InboxOutlined: <InboxOutlined />,
  IdcardOutlined: <IdcardOutlined />,
  MailOutlined: <MailOutlined />,
  UserOutlined: <UserOutlined />,
  TeamOutlined: <TeamOutlined />,
  NumberOutlined: <NumberOutlined />,
  ReconciliationOutlined: <ReconciliationOutlined />,
  ScheduleOutlined: <ScheduleOutlined />,
  SettingOutlined: <SettingOutlined />,
  ShopOutlined: <ShopOutlined />,
  ShoppingCartOutlined: <ShoppingCartOutlined />,
  TagsOutlined: <TagsOutlined />,
  MoneyCollectOutlined: <MoneyCollectOutlined />,
  KeyOutlined: <KeyOutlined />,
  PictureOutlined: <PictureOutlined />,
  QrcodeOutlined: <QrcodeOutlined />,
  TrophyOutlined: <TrophyOutlined />,
  WalletOutlined: <WalletOutlined />,
}

export const appRoutes: AppRouteItem[] = [
  {
    path: '/dashboard',
    meta: {
      title: 'menu.dashboard',
      icon: 'DashboardOutlined',
      affix: true,
      closable: false,
      keepAlive: true,
      accessKey: 'canAccessDashboard',
    },
    element: <DashboardPage />,
  },
  {
    path: '/system',
    meta: {
      title: 'menu.system',
      icon: 'SettingOutlined',
    },
    children: [
      {
        path: '/system/stores',
        meta: {
          title: 'menu.systemStores',
          icon: 'ShopOutlined',
          keepAlive: true,
          accessKey: 'canReadStore',
        },
        element: <SystemStoresPage />,
      },
      {
        path: '/system/employee-profiles',
        meta: {
          title: 'menu.systemEmployeeProfiles',
          icon: 'IdcardOutlined',
          keepAlive: true,
          accessKey: 'canViewEmployeeProfiles',
        },
        element: <SystemEmployeeProfilesPage />,
      },
      {
        path: '/system/center-logs',
        meta: {
          title: 'menu.systemCenterLogs',
          icon: 'FileTextOutlined',
          keepAlive: true,
          accessKey: 'canViewSystemLogs',
        },
        element: <SystemCenterLogsPage />,
      },
      {
        path: '/system/performance-baseline',
        meta: {
          title: 'menu.performanceBaseline',
          icon: 'DashboardOutlined',
          keepAlive: true,
          accessKey: 'canViewPerformanceBaseline',
        },
        element: <SystemPerformanceBaselinePage />,
      },
      {
        path: '/system/scheduled-statistics',
        meta: {
          title: 'menu.scheduledStatistics',
          icon: 'ScheduleOutlined',
          keepAlive: true,
          accessKey: 'canManageScheduledTasks',
        },
        element: <SystemScheduledStatisticsPage />,
      },
      {
        path: '/system/invoice-email-settings',
        meta: {
          title: 'menu.invoiceEmailSettings',
          icon: 'MailOutlined',
          keepAlive: true,
          accessKey: 'canManageSystemSettings',
        },
        element: <InvoiceEmailSettingsPage />,
      },
      {
        path: '/system/payment-terminal-settings',
        meta: {
          title: 'menu.paymentTerminalSettings',
          icon: 'WalletOutlined',
          keepAlive: true,
          accessKey: 'canManageSystemSettings',
        },
        element: <PaymentTerminalSettingsPage />,
      },
      {
        path: '/system/emergency-login-keys',
        meta: {
          title: 'menu.emergencyLoginKeys',
          icon: 'KeyOutlined',
          keepAlive: true,
          accessKey: 'canManageSystemSettings',
        },
        element: <EmergencyLoginKeysPage />,
      },
      {
        path: '/system/users',
        meta: {
          title: 'menu.systemUsers',
          icon: 'UserOutlined',
          keepAlive: true,
          accessKey: 'canReadUserConsole',
        },
        element: <SystemUsersPage />,
      },
      {
        path: '/system/roles',
        meta: {
          title: 'menu.systemRoles',
          icon: 'TeamOutlined',
          keepAlive: true,
          accessKey: 'canReadRole',
        },
        element: <SystemRolesPage />,
      },
      {
        path: '/system/permissions',
        meta: {
          title: 'menu.systemPermissions',
          icon: 'KeyOutlined',
          keepAlive: true,
          accessKey: 'canReadRole',
        },
        element: <SystemPermissionsPage />,
      },
      {
        path: '/system/device-registration',
        meta: {
          title: 'menu.deviceRegistration',
          icon: 'BuildOutlined',
          keepAlive: true,
          accessKey: 'canViewDeviceRegistration',
        },
        element: <PosAdminDeviceRegistrationPage />,
      },
      {
        path: '/system/app-downloads',
        meta: {
          title: 'menu.appDownloads',
          icon: 'QrcodeOutlined',
          keepAlive: true,
          accessKey: 'canViewAppDownloads',
        },
        element: <SystemAppDownloadsPage />,
      },
      {
        path: '/system/remote-maintenance',
        meta: {
          title: 'menu.remoteMaintenance',
          icon: 'BuildOutlined',
          // 离开含临时凭据的页面时卸载，及时清空内存密码与轮询。
          keepAlive: false,
          accessKey: 'isAdmin',
        },
        element: <SystemRemoteMaintenancePage />,
      },
      {
        // 旧地址兼容：WPF 版本已并入版本发布中心，书签与历史链接跳到 WPF 终端。
        path: '/system/wpf-versions',
        meta: {
          title: 'menu.appDownloads',
          hidden: true,
          accessKey: 'canViewAppDownloads',
          activeMenu: '/system/app-downloads',
        },
        element: <Navigate replace to="/system/app-downloads?view=wpf" />,
      },
    ],
  },
  {
    path: '/domestic-purchase',
    meta: {
      title: 'menu.domesticPurchase',
      icon: 'ShoppingCartOutlined',
    },
    children: [
      {
        path: '/domestic-purchase/china-suppliers',
        meta: {
          title: 'menu.chinaSuppliers',
          icon: 'BankOutlined',
          keepAlive: true,
          accessKey: 'canManageDomesticSuppliers',
        },
        element: <DomesticChinaSuppliersPage />,
      },
      {
        path: '/domestic-purchase/domestic-products',
        meta: {
          title: 'menu.domesticProducts',
          icon: 'AppstoreOutlined',
          keepAlive: true,
          accessKey: 'canReadProduct',
        },
        element: <DomesticProductsPage />,
      },
      {
        path: '/domestic-purchase/prefix-code-management',
        meta: {
          title: 'menu.prefixCodeManagement',
          icon: 'NumberOutlined',
          keepAlive: true,
          accessKey: 'canManageDomesticPrefixCodes',
        },
        element: <ProductPrefixCodeManagementPage />,
      },
      {
        path: '/domestic-purchase/product-creation',
        meta: {
          title: 'menu.productCreation',
          icon: 'BuildOutlined',
          keepAlive: true,
          accessKey: 'canManageDomesticProducts',
        },
        element: <ProductCreationPage />,
      },
      {
        path: '/domestic-purchase/product-import',
        meta: {
          title: 'menu.productImport',
          icon: 'InboxOutlined',
          keepAlive: true,
          accessKey: 'canManageDomesticProducts',
        },
        element: <ProductImportPage />,
      },
    ],
  },
  {
    path: '/warehouse',
    meta: {
      title: 'menu.warehouse',
      icon: 'DatabaseOutlined',
    },
    children: [
      {
        path: '/warehouse/store-orders',
        meta: {
          title: 'menu.storeOrders',
          icon: 'ReconciliationOutlined',
          keepAlive: true,
          accessKey: 'canManageWarehouseOrders',
        },
        element: <StoreOrdersPage />,
      },
      {
        path: '/warehouse/preorders',
        meta: {
          title: 'menu.preorders',
          icon: 'GiftOutlined',
          keepAlive: true,
          accessKey: 'canManageWarehouseOrders',
        },
        element: <PreordersPage />,
      },
      {
        path: '/warehouse/preorders/activations/:activationGuid',
        meta: {
          title: 'menu.preorderActivationDetail',
          hidden: true,
          accessKey: 'canManageWarehouseOrders',
          activeMenu: '/warehouse/preorders',
        },
        element: <PreorderActivationDetailPage />,
      },
      {
        path: '/warehouse/store-order-import-price-variance',
        meta: {
          title: 'menu.storeOrderImportPriceVariance',
          icon: 'BarChartOutlined',
          keepAlive: true,
          accessKey: 'canManageStoreOrderImportPriceVariance',
        },
        element: <StoreOrderImportPriceVariancePage />,
      },
      {
        path: '/warehouse/store-order/detail/:id',
        meta: {
          title: 'menu.storeOrderDetail',
          hidden: true,
          keepAlive: true,
          accessKey: 'canManageWarehouseOrders',
          activeMenu: '/warehouse/store-orders',
          dynamicTitle: () => i18n.t('menu.storeOrderDetail'),
        },
        element: <StoreOrderDetailPage />,
      },
      {
        path: '/warehouse/store-order/picking/:id',
        meta: {
          title: 'menu.pickingList',
          hidden: true,
          keepAlive: true,
          accessKey: 'canManageWarehouseOrders',
          activeMenu: '/warehouse/store-orders',
          dynamicTitle: () => i18n.t('menu.pickingList'),
        },
        element: <StoreOrderPickingListPage />,
      },
      {
        // 分单拣货单（A4 每人一页）：?orders=订单GUID[,…]&segment=段号，由拣货分配卡片与批量分配打开。
        path: '/warehouse/store-order/picking-slips',
        meta: {
          title: 'menu.pickingSlips',
          hidden: true,
          accessKey: 'canManageWarehouseOrders',
          activeMenu: '/warehouse/store-orders',
          dynamicTitle: () => i18n.t('menu.pickingSlips'),
        },
        element: <StoreOrderPickingSlipsPage />,
      },
      {
        path: '/warehouse/store-order/invoice/:id',
        meta: {
          title: 'menu.invoice',
          hidden: true,
          keepAlive: true,
          accessKey: 'canManageWarehouseOrders',
          activeMenu: '/warehouse/store-orders',
          dynamicTitle: () => i18n.t('menu.invoice'),
        },
        element: <StoreOrderInvoicePage />,
      },
      {
        path: '/warehouse/containers',
        meta: {
          title: 'menu.containers',
          icon: 'InboxOutlined',
          keepAlive: true,
          accessKey: 'canViewContainers',
        },
        element: <ContainersPage />,
      },
      {
        path: '/warehouse/container/detail/:containerGuid',
        meta: {
          title: 'menu.containerDetail',
          hidden: true,
          keepAlive: true,
          accessKey: 'canViewContainers',
          activeMenu: '/warehouse/containers',
          dynamicTitle: () => i18n.t('menu.containerDetail'),
        },
        element: <ContainerDetailPage />,
      },
      {
        path: '/warehouse/container/allocation-sales/:containerGuid',
        meta: {
          title: '货柜配销数据',
          hidden: true,
          keepAlive: true,
          accessKey: 'canViewContainers',
          activeMenu: '/warehouse/containers',
          dynamicTitle: () => '货柜配销数据',
        },
        element: <ContainerAllocationSalesPage />,
      },
      {
        path: '/warehouse/products',
        meta: {
          title: 'menu.warehouseProducts',
          icon: 'AppstoreOutlined',
          keepAlive: true,
          accessKey: 'canManageWarehouseProducts',
        },
        element: <WarehouseProductsPage />,
      },
      {
        path: '/warehouse/products/retail-price-changes',
        meta: {
          title: 'warehouse.retailPriceChanges.title',
          hidden: true,
          keepAlive: true,
          accessKey: 'canManageWarehouseProducts',
          activeMenu: '/warehouse/products',
        },
        element: <RetailPriceChangesPage />,
      },
      {
        // 静态路径必须排在 :productCode 动态路由之前，否则会被当成商品编码匹配。
        path: '/warehouse/products/price-update-tasks',
        meta: {
          title: 'warehouse.priceUpdateTasks.title',
          hidden: true,
          keepAlive: true,
          accessKey: 'canManageWarehouseProducts',
          activeMenu: '/warehouse/products',
        },
        element: <PriceUpdateTasksPage />,
      },
      {
        path: '/warehouse/products/:productCode/records',
        meta: {
          title: 'menu.warehouseProductRecords',
          hidden: true,
          keepAlive: true,
          accessKey: 'canManageWarehouseProducts',
          activeMenu: '/warehouse/products',
          dynamicTitle: () => i18n.t('menu.warehouseProductRecords'),
        },
        element: <WarehouseProductRecordsPage />,
      },
      {
        path: '/warehouse/categories',
        meta: {
          title: 'menu.categories',
          icon: 'TagsOutlined',
          keepAlive: true,
          accessKey: 'canManageWarehouseCategories',
        },
        element: <WarehouseCategoriesPage />,
      },
      {
        path: '/warehouse/locations',
        meta: {
          title: 'menu.warehouseLocations',
          icon: 'EnvironmentOutlined',
          keepAlive: true,
          accessKey: 'canManageWarehouseLocations',
        },
        element: <WarehouseLocationsPage />,
      },
      {
        path: '/warehouse/product-grade-management',
        meta: {
          title: 'menu.productGradeManagement',
          icon: 'TrophyOutlined',
          keepAlive: true,
          accessKey: 'canManageWarehouseProducts',
        },
        element: <ProductGradeManagementPage />,
      },
    ],
  },
  {
    path: '/executive-sales-intelligence',
    meta: {
      title: 'menu.executiveSalesIntelligence',
      icon: 'BarChartOutlined',
      accessKey: 'canViewSalesIntelligence',
    },
    children: [
      {
        path: '/executive-sales-intelligence/overview',
        meta: {
          title: 'menu.salesData',
          icon: 'DashboardOutlined',
          keepAlive: true,
          accessKey: 'canViewSalesData',
        },
        element: <ExecutiveSalesIntelligencePage />,
      },
      {
        path: '/executive-sales-intelligence/sales-detail-v2',
        meta: {
          title: 'menu.salesDetail',
          icon: 'FileTextOutlined',
          keepAlive: true,
          accessKey: 'canViewSalesDetail',
        },
        element: <SalesDetailAnalysisPage />,
      },
      {
        path: '/executive-sales-intelligence/compact-sales-board',
        meta: {
          title: 'menu.compactSalesBoard',
          icon: 'BarChartOutlined',
          keepAlive: true,
          accessKey: 'canViewCompactSalesBoard',
        },
        element: <CompactSalesBoardPage />,
      },
      {
        path: '/executive-sales-intelligence/product-movement-report',
        meta: {
          title: 'menu.productMovementReport',
          icon: 'ReconciliationOutlined',
          keepAlive: true,
          accessKey: 'canViewProductMovementReport',
        },
        element: <ProductMovementReportPage />,
      },
      {
        // 进货销量分析：批量货号销量与分店进货销量分析合为一页两个标签，任一页权限即可进入。
        path: ADMIN_PURCHASE_SALES_PATH,
        meta: {
          title: 'menu.purchaseSalesAnalysis',
          icon: 'BarChartOutlined',
          keepAlive: true,
          accessKey: 'canViewPurchaseSalesAnalysis',
        },
        element: <PurchaseSalesAnalysisPage />,
      },
      {
        // 旧地址兼容：批量货号销量已并入进货销量分析，书签与历史链接跳到对应标签。
        path: '/executive-sales-intelligence/batch-product-sales-analysis',
        meta: {
          title: 'menu.batchProductSalesAnalysis',
          hidden: true,
          accessKey: 'canViewPurchaseSalesAnalysis',
        },
        element: <Navigate replace to={buildAdminPurchaseSalesTabPath('batch')} />,
      },
      {
        path: '/executive-sales-intelligence/warehouse-product-flow-analysis',
        meta: {
          title: 'menu.warehouseProductFlowAnalysis',
          icon: 'BarChartOutlined',
          keepAlive: true,
          accessKey: 'canViewWarehouseProductFlowAnalysis',
        },
        element: <WarehouseProductFlowAnalysisPage />,
      },
      {
        path: '/executive-sales-intelligence/local-product-sales-analysis',
        meta: {
          title: 'menu.localProductSalesAnalysis',
          icon: 'BarChartOutlined',
          keepAlive: true,
          accessKey: 'canViewLocalProductSalesAnalysis',
        },
        element: <LocalProductSalesAnalysisPage />,
      },
      {
        // 旧地址兼容：分店进货销量分析已并入进货销量分析，书签与历史链接跳到对应标签。
        path: '/executive-sales-intelligence/local-supplier-purchase-sales-analysis',
        meta: {
          title: 'menu.localSupplierPurchaseSalesAnalysis',
          hidden: true,
          accessKey: 'canViewPurchaseSalesAnalysis',
        },
        element: <Navigate replace to={buildAdminPurchaseSalesTabPath('store')} />,
      },
      {
        path: '/executive-sales-intelligence/product-sales-analysis',
        meta: {
          title: 'menu.warehouseProductFlowAnalysis',
          hidden: true,
          accessKey: 'canViewWarehouseProductFlowAnalysis',
        },
        element: <Navigate replace to="/executive-sales-intelligence/warehouse-product-flow-analysis" />,
      },
      {
        path: '/executive-sales-intelligence/purchase-amount-dashboard',
        meta: {
          title: 'menu.purchaseAmountDashboard',
          icon: 'DollarOutlined',
          keepAlive: true,
          accessKey: 'canViewPurchaseAmountDashboard',
        },
        element: <PurchaseAmountDashboardPage />,
      },
    ],
  },
  {
    path: '/pos-admin',
    meta: {
      title: 'menu.posAdmin',
      icon: 'WalletOutlined',
    },
    children: [
      {
        path: '/pos-admin/suppliers',
        meta: {
          title: 'menu.suppliers',
          icon: 'ShopOutlined',
          keepAlive: true,
          accessKey: 'canViewAustralianSuppliers',
        },
        element: <PosAdminSupplierManagementPage />,
      },
      {
        path: '/pos-admin/products',
        meta: {
          title: 'menu.productManagement',
          icon: 'AppstoreOutlined',
          keepAlive: true,
          accessKey: 'canViewPosProducts',
        },
        element: <PosAdminProductManagementPage />,
      },
      {
        path: '/pos-admin/store-product-price',
        meta: {
          title: 'menu.storeProductPrice',
          icon: 'DollarOutlined',
          keepAlive: true,
          accessKey: 'canManageStoreProducts',
        },
        element: <PosAdminStoreProductPricePage />,
      },
      {
        path: '/pos-admin/pricing-strategies',
        meta: {
          title: 'menu.pricingStrategies',
          icon: 'FileTextOutlined',
          keepAlive: true,
          accessKey: 'canManagePricing',
        },
        element: <PosAdminPricingStrategiesPage />,
      },
      {
        path: '/pos-admin/promotions',
        meta: {
          title: 'menu.promotions',
          icon: 'GiftOutlined',
          keepAlive: true,
          accessKey: 'canManagePromotions',
        },
        element: <PosAdminPromotionsPage />,
      },
      {
        path: '/pos-admin/advertisements',
        meta: {
          title: 'menu.advertisements',
          icon: 'PictureOutlined',
          keepAlive: true,
          accessKey: 'canManageAdvertisements',
        },
        element: <PosAdminAdvertisementsPage />,
      },
      {
        path: '/pos-admin/schedule-attendance',
        meta: {
          title: 'menu.scheduleAttendance',
          icon: 'CalendarOutlined',
          keepAlive: true,
          accessKey: 'canViewAttendanceSchedule',
        },
        element: <PosAdminScheduleAttendancePage />,
      },
      {
        path: '/pos-admin/cash-register-users',
        meta: {
          title: 'menu.cashRegisterUsers',
          icon: 'UserOutlined',
          keepAlive: true,
          accessKey: 'canManageStoreOps',
        },
        element: <PosAdminCashRegisterUsersPage />,
      },
      {
        path: '/pos-admin/operation-logs',
        meta: {
          title: 'menu.operationLogs',
          icon: 'FileTextOutlined',
          keepAlive: true,
          accessKey: 'canViewEmployeeOperationLogs',
        },
        element: <PosAdminEmployeeLogsPage />,
      },
      {
        // 旧「老系统操作日志」地址（书签、分享链接）转到合并页的老收银来源。
        path: '/pos-admin/legacy-employee-logs',
        meta: {
          title: 'menu.operationLogs',
          hidden: true,
          accessKey: 'canViewLegacyEmployeeLogs',
          activeMenu: '/pos-admin/operation-logs',
        },
        element: <Navigate to="/pos-admin/operation-logs?source=legacy" replace />,
      },
      {
        path: '/pos-admin/linkly-settlements',
        meta: {
          title: 'menu.linklySettlements',
          icon: 'ReconciliationOutlined',
          keepAlive: true,
          accessKey: 'isAdmin',
        },
        element: <LinklySettlementsPage />,
      },
      {
        path: '/pos-admin/linkly-settlements/:id',
        meta: {
          title: 'menu.linklySettlementDetail',
          hidden: true,
          accessKey: 'isAdmin',
          activeMenu: '/pos-admin/linkly-settlements',
          dynamicTitle: () => i18n.t('menu.linklySettlementDetail'),
        },
        element: <LinklySettlementDetailPage />,
      },
      {
        path: '/pos-admin/sales-orders',
        meta: {
          title: 'menu.salesOrders',
          icon: 'FileDoneOutlined',
          keepAlive: true,
          accessKey: 'canReadOrder',
        },
        element: <PosmSalesOrdersPage />,
      },
      {
        path: '/pos-admin/local-supplier-invoices',
        meta: {
          title: 'menu.localSupplierInvoices',
          icon: 'ReconciliationOutlined',
          keepAlive: true,
          accessKey: 'canManageLocalPurchase',
        },
        element: <LocalSupplierInvoicesPage />,
      },
      {
        // 旧地址兼容：页面已挪到销售看板，书签与历史链接重定向到新路径。
        path: '/pos-admin/local-supplier-purchase-sales-analysis',
        meta: {
          title: 'menu.localSupplierPurchaseSalesAnalysis',
          hidden: true,
          accessKey: 'canViewPurchaseSalesAnalysis',
        },
        element: <Navigate replace to={buildAdminPurchaseSalesTabPath('store')} />,
      },
      {
        path: '/pos-admin/invoice-detail/:id',
        meta: {
          title: 'menu.invoiceDetail',
          hidden: true,
          keepAlive: true,
          accessKey: 'canManageLocalPurchase',
          activeMenu: '/pos-admin/local-supplier-invoices',
          dynamicTitle: () => i18n.t('menu.invoiceDetail'),
        },
        element: <LocalSupplierInvoiceDetailPage />,
      },
      {
        path: '/pos-admin/local-supplier-invoices/:id/sales-analysis',
        meta: {
          title: 'menu.localSupplierInvoiceSalesAnalysis',
          hidden: true,
          keepAlive: true,
          accessKey: 'canManageLocalPurchase',
          activeMenu: '/pos-admin/local-supplier-invoices',
          dynamicTitle: () => i18n.t('menu.localSupplierInvoiceSalesAnalysis', '进货单销量分析'),
        },
        element: <LocalSupplierInvoiceSalesAnalysisPage />,
      },
      {
        path: '/pos-admin/local-supplier-invoices/:id',
        meta: {
          title: 'menu.editInvoice',
          hidden: true,
          keepAlive: true,
          accessKey: 'canEditLocalPurchase',
          activeMenu: '/pos-admin/local-supplier-invoices',
          dynamicTitle: () => i18n.t('menu.editInvoice'),
        },
        element: <InvoiceEditPage />,
      },
    ],
  },
]

function flattenAllRoutes(routes: AppRouteItem[]): AppRouteItem[] {
  return routes.flatMap((route) => {
    if (route.children?.length) {
      return [route, ...flattenAllRoutes(route.children)]
    }
    return [route]
  })
}

function flattenLeafRoutes(routes: AppRouteItem[], parentPaths: string[] = []): LeafEntry[] {
  return routes.flatMap((route) => {
    const currentPaths = [...parentPaths, route.path]
    if (route.children?.length) {
      return flattenLeafRoutes(route.children, currentPaths)
    }
    return [{ route, parentPaths }]
  })
}

const leafEntries = flattenLeafRoutes(appRoutes)
const allRoutes = flattenAllRoutes(appRoutes)
const titleMap = new Map(allRoutes.map((route) => [route.path, route.meta.title]))

function canAccessRoute(meta: AppRouteMeta, access: AccessControl) {
  if (!meta.accessKey) {
    return true
  }
  return access[meta.accessKey] === true
}

export function resolveRoute(pathname: string) {
  for (const entry of leafEntries) {
    const matched = matchPath({ path: entry.route.path, end: true }, pathname)
    if (matched) {
      return {
        ...entry.route,
        parentPaths: entry.parentPaths,
        params: matched.params as Record<string, string>,
      } satisfies ResolvedRoute
    }
  }
  return null
}

export function getCurrentRoute(pathname: string, access: AccessControl) {
  const route = resolveRoute(pathname)
  if (!route) {
    return null
  }

  if (!canAccessRoute(route.meta, access)) {
    return {
      ...route,
      element: <ForbiddenPage />,
    }
  }

  return route
}

export function getCurrentElement(pathname: string, access: AccessControl) {
  return getCurrentRoute(pathname, access)?.element ?? <NotFoundPage />
}

export function getOpenMenuKeys(pathname: string, access: AccessControl) {
  return getCurrentRoute(pathname, access)?.parentPaths ?? []
}

export function getSelectedMenuKeys(pathname: string, access: AccessControl) {
  const route = getCurrentRoute(pathname, access)
  if (!route) {
    return []
  }
  return [route.meta.activeMenu || route.path]
}

function buildMenusInternal(routes: AppRouteItem[], access: AccessControl): MenuProps['items'] {
  return routes
    .map((route) => {
      if (route.meta.hidden) {
        return null
      }

      const hasRouteChildren = Boolean(route.children?.length)
      const children = hasRouteChildren ? buildMenusInternal(route.children!, access) : undefined
      const hasChildren = Boolean(children?.length)
      const hasSelfAccess = canAccessRoute(route.meta, access)

      if (
        !shouldIncludeLocalMenuRoute({
          hasRouteChildren,
          hasVisibleChildren: hasChildren,
          hasSelfAccess,
        })
      ) {
        return null
      }

      return {
        key: route.path,
        icon: route.meta.icon ? iconMap[route.meta.icon as keyof typeof iconMap] : undefined,
        label: i18n.t(route.meta.title),
        children,
      }
    })
    .filter(Boolean) as MenuProps['items']
}

function isWarehouseStaffNavigationLimited(access: AccessControl) {
  return (
    access.isWarehouseStaff &&
    !access.isAdmin &&
    !access.isWarehouseManager &&
    (access.hasRole('WarehouseStaff') || access.hasRole('仓库员工'))
  )
}

function buildWarehouseStaffMenus(access: AccessControl): MenuProps['items'] {
  if (!access.canManageWarehouseOrders) {
    return []
  }

  // 仓库员工侧边栏只保留分店订货列表；首柜价差异属于仓库管理员报表，不跟随只读订货入口开放。
  return [
    {
      key: '/warehouse',
      icon: iconMap.DatabaseOutlined,
      label: i18n.t('menu.warehouse'),
      children: [
        {
          key: '/warehouse/store-orders',
          icon: iconMap.ReconciliationOutlined,
          label: i18n.t('menu.storeOrders'),
        },
      ],
    },
  ]
}

export function buildMenus(access: AccessControl, navigationMenu?: NavigationMenuDto[]) {
  const localMenus = isWarehouseStaffNavigationLimited(access)
    ? [
        ...(buildWarehouseStaffMenus(access) ?? []),
        // 显式授权的销售页补充到仓库员工菜单，其他业务仍沿用原有白名单。
        ...(buildMenusInternal(appRoutes.filter(route => route.path === '/executive-sales-intelligence'), access) ?? []),
      ]
    : buildMenusInternal(appRoutes, access)
  if (navigationMenu !== undefined) {
    return chooseNavigationMenus(localMenus, buildMenusFromBackend(navigationMenu))
  }
  return localMenus
}

function buildMenusFromBackend(nodes: NavigationMenuDto[]): MenuProps['items'] {
  return nodes.map((node) => ({
    key: node.path,
    icon: node.icon ? iconMap[node.icon as keyof typeof iconMap] : undefined,
    label: i18n.t(node.titleKey),
    children: node.children?.length ? buildMenusFromBackend(node.children) : undefined,
  }))
}

export function getBreadcrumbItems(pathname: string, access: AccessControl, currentTabTitle?: string) {
  const route = getCurrentRoute(pathname, access)
  if (!route) {
    return [{ title: i18n.t('menu.pageNotFound') }]
  }

  const titles = [...route.parentPaths]
    .map((path) => titleMap.get(path))
    .filter((item): item is string => Boolean(item))
    .map((key) => i18n.t(key))

  const currentTitle = currentTabTitle || route.meta.dynamicTitle?.(route.params) || i18n.t(route.meta.title)
  return [...titles, currentTitle].map((title) => ({ title }))
}

export function toTabItem(pathname: string, access: AccessControl): TabItem | null {
  const route = getCurrentRoute(pathname, access)
  if (!route) {
    return null
  }
  // 旧地址兼容路由只做跳转：不生成页签，否则跳转后会留下一个与目标页同名的多余页签。
  if (isValidElement(route.element) && route.element.type === Navigate) {
    return null
  }

  return {
    key: route.meta.affix ? route.path : pathname,
    path: pathname,
    routePath: route.path,
    title: route.meta.dynamicTitle?.(route.params) || i18n.t(route.meta.title),
    affix: route.meta.affix,
    closable: route.meta.closable !== false,
    keepAlive: route.meta.keepAlive,
  }
}

export function getAffixTabs(): TabItem[] {
  return leafEntries
    .filter((entry) => entry.route.meta.affix)
    .map((entry) => ({
      key: entry.route.path,
      path: entry.route.path,
      routePath: entry.route.path,
      title: i18n.t(entry.route.meta.title),
      affix: true,
      closable: false,
      keepAlive: entry.route.meta.keepAlive,
    }))
}
