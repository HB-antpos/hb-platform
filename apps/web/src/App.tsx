import { App as AntdApp, ConfigProvider, Result, Spin, theme } from 'antd'
import enUS from 'antd/locale/en_US'
import zhCN from 'antd/locale/zh_CN'
import 'dayjs/locale/zh-cn'
import { lazy, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { createBrowserRouter, Navigate, Route, RouterProvider, Routes, useLocation } from 'react-router-dom'
import GlobalErrorBoundary from './components/GlobalErrorBoundary'
import RouteLoadBoundary from './components/RouteLoadBoundary'
import LoginPage from './pages/Login'
import { isPublicAppPath } from './pages/MobilePrivacy/publicPath'
import { ShopPreorderLeaveProvider } from './pages/ShopPreorder/preorderLeaveContext'
import ForbiddenPage from './pages/Forbidden'
import WebAccessDeniedPage from './pages/WebAccessDenied'
import { useAuthStore } from './store/auth'
import { AUTH_EXPIRED_EVENT } from './utils/request'
import { getDefaultWebPath, WEB_NO_ACCESS_PATH } from './utils/webPortalAccess'

const loadAdminLayout = () => import('./layout/AdminLayout')
const loadShopLayout = () => import('./layout/ShopLayout')

const AdminLayout = lazy(loadAdminLayout)
const ShopLayout = lazy(loadShopLayout)
const BrowserExtensionPrivacyPage = lazy(() => import('./pages/BrowserExtensionPrivacy'))
const HbSupplierOrderSupportPage = lazy(() => import('./pages/HbSupplierOrderSupport'))
const MobilePrivacyPage = lazy(() => import('./pages/MobilePrivacy'))
const MinorEmploymentParentSignaturePage = lazy(() => import('./pages/MinorEmployment/ParentSignature'))
const ShopBestSellersPage = lazy(() => import('./pages/ShopBestSellers'))
const ShopComingSoonPage = lazy(() => import('./pages/ShopComingSoon'))
const ShopSupplyWatchesPage = lazy(() => import('./pages/ShopSupplyWatches'))
const ShopHomePage = lazy(() => import('./pages/ShopHome'))
const ShopLocalSupplierInvoiceDetailPage = lazy(() => import('./pages/ShopLocalSupplierInvoiceDetail'))
const ShopLocalSupplierInvoicesPage = lazy(() => import('./pages/ShopLocalSupplierInvoices'))
const ShopOrderDetailPage = lazy(() => import('./pages/ShopOrderDetail'))
const ShopOrdersPage = lazy(() => import('./pages/ShopOrders'))
const ShopPreorderPage = lazy(() => import('./pages/ShopPreorder'))
const ShopPurchaseSalesAnalysisPage = lazy(() => import('./pages/ShopPurchaseSalesAnalysis'))

function AppBootstrap() {
  const { t } = useTranslation()
  const { initialized, loading, currentUser, access, fetchCurrentUser, clearAuth } = useAuthStore()
  const location = useLocation()
  const isPublicPath = isPublicAppPath(location.pathname)

  useEffect(() => {
    if (!initialized && !loading && !isPublicPath) {
      void fetchCurrentUser()
    }
  }, [fetchCurrentUser, initialized, isPublicPath, loading])

  useEffect(() => {
    if ((initialized && !loading) || isPublicPath || location.pathname === WEB_NO_ACCESS_PATH) {
      return
    }

    // 认证等待期间只预热当前 URL 所属外壳，避免后台与 Shop 互相进入首屏闭包。
    const shellLoader = /^\/shop(?:\/|$)/.test(location.pathname)
      ? loadShopLayout
      : (location.pathname === '/' ? undefined : loadAdminLayout)
    void shellLoader?.()
  }, [initialized, isPublicPath, loading, location.pathname])

  useEffect(() => {
    window.addEventListener(AUTH_EXPIRED_EVENT, clearAuth)
    return () => window.removeEventListener(AUTH_EXPIRED_EVENT, clearAuth)
  }, [clearAuth])

  const homePage = getDefaultWebPath(access)
  const portalDeniedPage = homePage === WEB_NO_ACCESS_PATH
    ? <Navigate to={WEB_NO_ACCESS_PATH} replace />
    : <ForbiddenPage />

  if ((!initialized || loading) && !isPublicPath) {
    return (
      <div className="app-loading">
        <Spin size="large" fullscreen />
      </div>
    )
  }

  return (
    <Routes>
      <Route path="/" element={<Navigate to={homePage} replace />} />
      <Route
        path="/login"
        element={currentUser ? <Navigate to={homePage} replace /> : <LoginPage />}
      />
      <Route
        path="/privacy/browser-extension"
        element={<RouteLoadBoundary resetKey="browser-extension-privacy"><BrowserExtensionPrivacyPage /></RouteLoadBoundary>}
      />
      <Route
        path="/privacy/mobile"
        element={<RouteLoadBoundary resetKey="mobile-privacy"><MobilePrivacyPage /></RouteLoadBoundary>}
      />
      <Route
        path="/minor-employment/sign"
        element={<RouteLoadBoundary resetKey="minor-employment-sign"><MinorEmploymentParentSignaturePage /></RouteLoadBoundary>}
      />
      <Route
        path="/minor-employment/guardian/:token"
        element={<RouteLoadBoundary resetKey="minor-employment-guardian"><MinorEmploymentParentSignaturePage /></RouteLoadBoundary>}
      />
      <Route
        path="/support/hb-supplier-order"
        element={<RouteLoadBoundary resetKey="hb-supplier-order-support"><HbSupplierOrderSupportPage /></RouteLoadBoundary>}
      />
      <Route
        path={WEB_NO_ACCESS_PATH}
        element={
          currentUser
            ? (homePage === WEB_NO_ACCESS_PATH ? <WebAccessDeniedPage /> : <Navigate to={homePage} replace />)
            : <Navigate to="/login" replace />
        }
      />
      <Route
        path="/shop"
        element={
          currentUser
            ? (access.canAccessOrderFront
                ? (
                    <ShopPreorderLeaveProvider>
                      <RouteLoadBoundary resetKey="shop-layout"><ShopLayout /></RouteLoadBoundary>
                    </ShopPreorderLeaveProvider>
                  )
                : portalDeniedPage)
            : <Navigate to="/login" replace />
        }
      >
        <Route index element={<ShopHomePage />} />
        <Route path="best-sellers" element={<ShopBestSellersPage />} />
        {/* 货号销量已并入进货销量分析的「粘贴数据查看」标签；旧地址重定向过去，未授权时页面自动回落到「选择分店查看」。 */}
        <Route path="batch-product-sales" element={<Navigate replace to="/shop/purchase-sales-analysis?tab=paste" />} />
        <Route path="coming-soon" element={<ShopComingSoonPage />} />
        <Route path="supply-watches" element={<ShopSupplyWatchesPage />} />
        <Route path="orders" element={<ShopOrdersPage />} />
        <Route path="orders/:id" element={<ShopOrderDetailPage />} />
        <Route path="local-supplier-invoices" element={<ShopLocalSupplierInvoicesPage />} />
        <Route path="local-supplier-invoices/:invoiceGuid" element={<ShopLocalSupplierInvoiceDetailPage />} />
        <Route path="purchase-sales-analysis" element={<ShopPurchaseSalesAnalysisPage />} />
        <Route path="preorders/:activationGuid" element={<ShopPreorderPage />} />
      </Route>
      <Route
        path="/*"
        element={
          currentUser
            ? (access.canAccessAdminShell
                ? <RouteLoadBoundary resetKey="admin-layout"><AdminLayout /></RouteLoadBoundary>
                : portalDeniedPage)
            : <Navigate to="/login" replace />
        }
      />
      <Route
        path="*"
        element={<Result status="404" title="404" subTitle={t('menu.pageNotFound', '页面不存在')} />}
      />
    </Routes>
  )
}

// 使用 Data Router 承载现有 Routes，页面才能使用官方 useBlocker 在站内离页前保护未保存数据。
const router = createBrowserRouter([{ path: '*', element: <AppBootstrap /> }])

export default function App() {
  const { i18n } = useTranslation()
  const antdLocale = i18n.resolvedLanguage === 'en' ? enUS : zhCN

  return (
    <ConfigProvider
      locale={antdLocale}
      theme={{
        algorithm: theme.defaultAlgorithm,
        token: {
          colorPrimary: '#1677ff',
          borderRadius: 10,
        },
      }}
    >
      <AntdApp>
        <GlobalErrorBoundary>
          <RouterProvider router={router} />
        </GlobalErrorBoundary>
      </AntdApp>
    </ConfigProvider>
  )
}
