import {
  AppstoreOutlined,
  BarChartOutlined,
  BellOutlined,
  DashboardOutlined,
  DownOutlined,
  FileTextOutlined,
  GiftOutlined,
  HomeOutlined,
  LogoutOutlined,
  MenuOutlined,
  MoreOutlined,
  OrderedListOutlined,
  ScanOutlined,
  ShoppingCartOutlined,
  UserOutlined,
} from '@ant-design/icons'
import { Alert, Badge, Button, Drawer, Dropdown, Input, Menu, Modal, Select, Space, Spin, message } from 'antd'
import type { MenuProps } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, Outlet, useLocation, useNavigate } from 'react-router-dom'
import shopBrandCart from '../assets/shop-brand-cart.png'
import './ShopWorkspace.css'
import LanguageSwitch from '../components/LanguageSwitch'
import RouteLoadBoundary from '../components/RouteLoadBoundary'
import ShopCartDrawer from '../components/ShopCartDrawer'
import ShopCartSummary from '../components/ShopCartSummary'
import SupplierOrderingExtensionEntry from '../components/SupplierOrderingExtensionEntry'
import { getCategoryTree, type WarehouseCategoryNode } from '../services/warehouseCategoryService'
import { getActiveStoreOrderCart, getActiveStoreOrderCartSummary } from '../services/storeOrderService'
import {
  canBypassPreorderGate,
  getActivePreorders,
  resolveEffectivePreorderGateBlocked,
} from '../services/preorderService'
import { useAuthStore } from '../store/auth'
import { useShopStore } from '../store/shop'
import { resolveShopBannerCopy } from './shopBannerCopy'
import { shopNavMessages } from './shopNavMessages'
import { useShopUserStores } from './useShopUserStores'
import { registerPageMessages } from '../i18n/registerPageMessages'
import {
  resolvePreorderPromptPresentation,
  resolveShopPreorderNavigation,
} from '../pages/ShopPreorder/preorderNavigation'
import { getPreorderDateDisplay } from '../pages/ShopPreorder/preorderDate'
import { changeStoreAfterDurableLeave, runAfterDurableLeave, usePreorderLeave } from '../pages/ShopPreorder/preorderLeaveContext'

// 导航增量文案在布局代码块加载时注册，早于首次渲染。
registerPageMessages(shopNavMessages)

const { Search } = Input
const PREORDER_GATE_TIMEOUT_MS = 8_000
const SHOP_MOBILE_LAYOUT_QUERY = '(max-width: 768px)'

function supportsHover() {
  if (typeof window !== 'undefined' && window.matchMedia) {
    return window.matchMedia('(hover: hover)').matches
  }

  return true
}

function useShopMobileLayout() {
  const [isMobileShopLayout, setIsMobileShopLayout] = useState(() => {
    if (typeof window === 'undefined' || !window.matchMedia) {
      return false
    }
    return window.matchMedia(SHOP_MOBILE_LAYOUT_QUERY).matches
  })

  useEffect(() => {
    if (!window.matchMedia) {
      return undefined
    }

    // 必须与商城 CSS 使用同一个断点，确保任一视口只挂载一个扩展握手实例。
    const mediaQuery = window.matchMedia(SHOP_MOBILE_LAYOUT_QUERY)
    const update = () => setIsMobileShopLayout(mediaQuery.matches)
    mediaQuery.addEventListener('change', update)
    update()
    return () => mediaQuery.removeEventListener('change', update)
  }, [])

  return isMobileShopLayout
}

function useShopFavicon() {
  useEffect(() => {
    const favicon = document.querySelector<HTMLLinkElement>('link[rel="icon"]')
    if (!favicon) {
      return undefined
    }

    const previousHref = favicon.getAttribute('href')
    const previousSizes = favicon.getAttribute('sizes')
    favicon.setAttribute('href', shopBrandCart)
    favicon.setAttribute('sizes', '384x384')

    // Shop 路由树卸载时还原全局 HB favicon，避免影响登录页和后台页面。
    return () => {
      if (previousHref === null) {
        favicon.removeAttribute('href')
      } else {
        favicon.setAttribute('href', previousHref)
      }

      if (previousSizes === null) {
        favicon.removeAttribute('sizes')
      } else {
        favicon.setAttribute('sizes', previousSizes)
      }
    }
  }, [])
}

function ShopBrandMark() {
  return (
    <span className="shop-brand-mark" aria-hidden="true">
      <img className="shop-brand-mark__image" src={shopBrandCart} alt="" draggable={false} />
    </span>
  )
}

export default function ShopLayout() {
  useShopFavicon()
  const navigate = useNavigate()
  const location = useLocation()
  const { currentUser, access, logout } = useAuthStore()
  const { requestPreorderDurableLeave } = usePreorderLeave()
  const { t, i18n } = useTranslation()
  const isMobileShopLayout = useShopMobileLayout()
  const isShopHomePage = location.pathname === '/shop'
  const isPreorderPage = location.pathname.startsWith('/shop/preorders/')
  const isBestSellersPage = location.pathname.startsWith('/shop/best-sellers')
  const isComingSoonPage = location.pathname.startsWith('/shop/coming-soon')
  const isOrdersPage = location.pathname.startsWith('/shop/orders')
  const isLocalSupplierInvoicesPage = location.pathname.startsWith('/shop/local-supplier-invoices')
  const isPurchaseSalesAnalysisPage = location.pathname.startsWith('/shop/purchase-sales-analysis')
  const isSupplyWatchesPage = location.pathname.startsWith('/shop/supply-watches')
  const isMorePage = isPreorderPage || isBestSellersPage || isComingSoonPage || isLocalSupplierInvoicesPage || isPurchaseSalesAnalysisPage || isSupplyWatchesPage
  const shopBannerCopy = useMemo(() => resolveShopBannerCopy(location.pathname), [location.pathname])
  const preorderDateTimeFormatter = useMemo(
    () => new Intl.DateTimeFormat(i18n.resolvedLanguage || i18n.language, { dateStyle: 'medium', timeStyle: 'short' }),
    [i18n.language, i18n.resolvedLanguage],
  )

  const userStores = useShopStore((state) => state.userStores)
  const selectedStore = useShopStore((state) => state.selectedStore)
  const cart = useShopStore((state) => state.cart)
  const setSelectedStore = useShopStore((state) => state.setSelectedStore)
  const setCart = useShopStore((state) => state.setCart)
  const preorderActivations = useShopStore((state) => state.preorderActivations)
  const supplyWatchSummary = useShopStore((state) => state.supplyWatchSummary)
  const refreshSupplyWatchSummary = useShopStore((state) => state.refreshSupplyWatchSummary)
  // 进入商城、切换分店、切换页面时刷新关注汇总；商品恢复订货靠这次刷新露出提示。
  useEffect(() => {
    void refreshSupplyWatchSummary()
  }, [selectedStore?.storeCode, location.pathname, refreshSupplyWatchSummary])
  const showSupplyRestockedAlert = supplyWatchSummary.restockedCount > 0 && !isSupplyWatchesPage
  const preorderBlocked = useShopStore((state) => state.preorderBlocked)
  const preorderGateLoading = useShopStore((state) => state.preorderGateLoading)
  const preorderGateError = useShopStore((state) => state.preorderGateError)
  const setPreorderGate = useShopStore((state) => state.setPreorderGate)
  const beginPreorderGateRequest = useShopStore((state) => state.beginPreorderGateRequest)
  const isPreorderGateRequestCurrent = useShopStore((state) => state.isPreorderGateRequestCurrent)
  const resetShop = useShopStore((state) => state.reset)
  const preorderGateBypassed = canBypassPreorderGate(access)
  const effectivePreorderBlocked = resolveEffectivePreorderGateBlocked(
    preorderBlocked,
    preorderGateBypassed,
  )
  const preorderPrompt = resolvePreorderPromptPresentation({
    storeCode: selectedStore?.storeCode,
    activationGuids: preorderActivations.map((item) => item.activationGuid),
    loading: preorderGateLoading,
    error: preorderGateError,
    bypassed: preorderGateBypassed,
    onPreorderPage: isPreorderPage,
  })
  const showPreorderGateAlert = Boolean(
    selectedStore &&
    effectivePreorderBlocked &&
    !preorderGateLoading &&
    !preorderGateError &&
    preorderActivations.length > 0,
  )

  const [categories, setCategories] = useState<WarehouseCategoryNode[]>([])
  const [loadingCategories, setLoadingCategories] = useState(false)
  const [cartDrawerOpen, setCartDrawerOpen] = useState(false)
  const [cartDrawerLoading, setCartDrawerLoading] = useState(false)
  const [mobileCategoryVisible, setMobileCategoryVisible] = useState(false)
  const [mobileMoreVisible, setMobileMoreVisible] = useState(false)
  const [isHoverSupported, setIsHoverSupported] = useState(true)
  const [dismissedPreorderPromptKey, setDismissedPreorderPromptKey] = useState<string | null>(null)
  const preorderPromptOpen = preorderPrompt.mode === 'pending'
    && dismissedPreorderPromptKey !== preorderPrompt.key
  const selectedStoreCodeRef = useRef<string | null>(null)
  const cartDrawerOpenRef = useRef(false)
  const fullCartRequestVersionRef = useRef(0)
  selectedStoreCodeRef.current = selectedStore?.storeCode ?? null
  cartDrawerOpenRef.current = cartDrawerOpen

  const selectedCategory = useMemo(() => {
    return new URLSearchParams(location.search).get('category') || ''
  }, [location.search])

  useEffect(() => {
    setIsHoverSupported(supportsHover())
  }, [])

  useShopUserStores(currentUser?.userGUID)

  useEffect(() => {
    let cancelled = false

    const fetchCategories = async () => {
      setLoadingCategories(true)
      try {
        const tree = await getCategoryTree()
        if (cancelled) {
          return
        }

        const allNode = tree.find((item) => item.categoryName.toLowerCase().includes('all'))
        const displayCategories = allNode?.children?.length ? allNode.children : tree
        setCategories(displayCategories)
      } catch (error) {
        if (!cancelled) {
          setCategories([])
        }
      } finally {
        if (!cancelled) {
          setLoadingCategories(false)
        }
      }
    }

    void fetchCategories()

    return () => {
      cancelled = true
    }
  }, [])

  const refreshCartSummary = useCallback(async () => {
    const storeCode = selectedStore?.storeCode ?? null
    if (!storeCode) {
      setCart(null)
      setCartDrawerLoading(false)
      return
    }

    try {
      const nextCart = await getActiveStoreOrderCartSummary(storeCode)
      if (selectedStoreCodeRef.current === storeCode) {
        setCart(nextCart)
      }
    } catch (error) {
      if (selectedStoreCodeRef.current === storeCode) {
        setCart(null)
      }
    }
  }, [selectedStore?.storeCode, setCart])

  const refreshPreorderGate = useCallback(async () => {
    const storeCode = selectedStore?.storeCode ?? null
    const requestToken = beginPreorderGateRequest()
    if (!storeCode) {
      if (isPreorderGateRequestCurrent(requestToken) && selectedStoreCodeRef.current === null) {
        setPreorderGate({ preorderActivations: [], preorderBlocked: false, preorderGateLoading: false, preorderGateError: false })
      }
      return
    }

    // 请求开始即清除旧分店的阻塞结果；检查中允许提交，最终以后端写入门禁为准。
    if (!isPreorderGateRequestCurrent(requestToken) || selectedStoreCodeRef.current !== storeCode) return
    setPreorderGate({ preorderActivations: [], preorderBlocked: false, preorderGateLoading: true, preorderGateError: false })
    const controller = new AbortController()
    // 远端异常时及时结束检查；失败时保持 fail-open，不沿用旧门禁。
    const timeoutId = window.setTimeout(() => controller.abort(), PREORDER_GATE_TIMEOUT_MS)
    try {
      const result = await getActivePreorders(storeCode, controller.signal)
      if (isPreorderGateRequestCurrent(requestToken) && selectedStoreCodeRef.current === storeCode) {
        setPreorderGate({
          preorderActivations: result.activations,
          preorderBlocked: result.normalOrderBlocked,
          preorderGateLoading: false,
          preorderGateError: false,
        })
      }
    } catch {
      if (isPreorderGateRequestCurrent(requestToken) && selectedStoreCodeRef.current === storeCode) {
        setPreorderGate({ preorderActivations: [], preorderBlocked: false, preorderGateLoading: false, preorderGateError: true })
      }
    } finally {
      window.clearTimeout(timeoutId)
    }
  }, [beginPreorderGateRequest, isPreorderGateRequestCurrent, selectedStore?.storeCode, setPreorderGate])

  const handleOpenPreorder = useCallback(() => {
    const resolution = resolveShopPreorderNavigation({
      storeCode: selectedStore?.storeCode,
      activationGuid: preorderActivations[0]?.activationGuid,
      loading: preorderGateLoading,
      error: preorderGateError,
    })

    if (resolution.action === 'open') {
      navigate(`/shop/preorders/${resolution.activationGuid}`)
      return
    }
    if (resolution.action === 'select-store') {
      message.warning(t('shop.preorder.selectStoreFirst'))
      return
    }
    if (resolution.action === 'refresh') {
      // 手动点击 Preorder 时只触发后台刷新，不再弹出检查中的干扰提示。
      void refreshPreorderGate()
      return
    }
    message.info(t('shop.preorder.noActive'))
  }, [navigate, preorderActivations, preorderGateError, preorderGateLoading, refreshPreorderGate, selectedStore?.storeCode, t])

  const refreshFullCart = useCallback(async () => {
    const storeCode = selectedStore?.storeCode ?? null
    if (!storeCode) {
      setCart(null)
      setCartDrawerLoading(false)
      return
    }

    const requestVersion = fullCartRequestVersionRef.current + 1
    fullCartRequestVersionRef.current = requestVersion
    setCartDrawerLoading(true)
    try {
      const nextCart = await getActiveStoreOrderCart(storeCode)
      if (selectedStoreCodeRef.current === storeCode) {
        setCart(nextCart)
      }
    } catch (error) {
      if (selectedStoreCodeRef.current === storeCode) {
        setCart(null)
      }
    } finally {
      if (fullCartRequestVersionRef.current === requestVersion) {
        setCartDrawerLoading(false)
      }
    }
  }, [selectedStore?.storeCode, setCart])

  useEffect(() => {
    // 切换分店先清掉旧购物车；抽屉已打开时直接补新门店明细，否则只拉摘要。
    setCart(null)
    void (cartDrawerOpenRef.current ? refreshFullCart() : refreshCartSummary())
    void refreshPreorderGate()
  }, [refreshCartSummary, refreshFullCart, refreshPreorderGate, setCart])

  useEffect(() => {
    if (!selectedStore?.storeCode) {
      return
    }

    const refreshVisibleCart = () => {
      if (document.visibilityState !== 'visible') {
        return
      }

      // 抽屉已打开时保留明细视图；否则只刷新顶部摘要，避免前台切回拖慢首屏。
      void (cartDrawerOpen ? refreshFullCart() : refreshCartSummary())
      void refreshPreorderGate()
    }

    const refreshFocusedCart = () => {
      void (cartDrawerOpen ? refreshFullCart() : refreshCartSummary())
      void refreshPreorderGate()
    }

    window.addEventListener('focus', refreshFocusedCart)
    document.addEventListener('visibilitychange', refreshVisibleCart)

    return () => {
      window.removeEventListener('focus', refreshFocusedCart)
      document.removeEventListener('visibilitychange', refreshVisibleCart)
    }
  }, [cartDrawerOpen, refreshCartSummary, refreshFullCart, refreshPreorderGate, selectedStore?.storeCode])

  const openCartDrawer = () => {
    setCartDrawerOpen(true)
    void refreshFullCart()
  }

  const buildMenuItems = (nodes: WarehouseCategoryNode[]): MenuProps['items'] => {
    return nodes.map((node) => {
      if (node.children?.length) {
        return {
          key: node.categoryGUID,
          label: node.categoryName,
          children: buildMenuItems(node.children),
        }
      }

      return {
        key: node.categoryGUID,
        label: node.categoryName,
        onClick: () => {
          navigate(`/shop?category=${node.categoryGUID}`)
          setMobileCategoryVisible(false)
        },
      }
    })
  }

  const handleLogout = async () => {
    await runAfterDurableLeave(requestPreorderDurableLeave, async () => {
      await logout()
      resetShop()
      navigate('/login', { replace: true })
    })
  }

  const handleSearch = (value: string) => {
    const keyword = value.trim()
    if (!keyword) {
      navigate('/shop')
      return
    }
    navigate(`/shop?keyword=${encodeURIComponent(keyword)}`)
  }

  const handleStoreChange = async (value?: string) => {
    await changeStoreAfterDurableLeave(value, requestPreorderDurableLeave, (storeCode) => {
      const nextStore = userStores.find((item) => item.storeCode === storeCode) ?? null
      setSelectedStore(nextStore)
    })
  }

  const openShopScanner = () => {
    if (!isShopHomePage) {
      navigate('/shop?scan=1')
      return
    }

    window.dispatchEvent(new Event('shop:open-scanner'))
  }

  return (
    <div
      className={`shop-layout${isComingSoonPage || isOrdersPage || isLocalSupplierInvoicesPage || isPurchaseSalesAnalysisPage ? ' shop-workspace-layout' : ''}`}
    >
      <header className="shop-main-header">
        <div className="shop-shell">
          <button
            type="button"
            className="shop-brand"
            onClick={() => navigate('/shop')}
            aria-label={t('shop.shopHome', 'Shop Home')}
          >
            <ShopBrandMark />
            <span className="shop-brand-copy">
              <strong>HB SHOP</strong>
              <span>{t('shop.storeOrdering', 'Store Ordering')}</span>
            </span>
          </button>

          <nav className="shop-primary-nav" aria-label={t('shop.primaryNavigation', 'Shop navigation')}>
            <Link
              to="/shop"
              className={`shop-primary-nav__item${isShopHomePage ? ' active' : ''}`}
              aria-current={isShopHomePage ? 'page' : undefined}
            >
              {t('shop.shopHome', 'Shop Home')}
            </Link>
            <button
              type="button"
              className={`shop-primary-nav__item${isPreorderPage ? ' active' : ''}`}
              onClick={handleOpenPreorder}
              aria-current={isPreorderPage ? 'page' : undefined}
            >
              {t('shop.preorder.navigation', 'Preorder')}
            </button>
            <Link
              to="/shop/best-sellers"
              className={`shop-primary-nav__item${isBestSellersPage ? ' active' : ''}`}
              aria-current={isBestSellersPage ? 'page' : undefined}
            >
              {t('shop.bestSellers', 'Best Sellers')}
            </Link>
            <Link
              to="/shop/coming-soon"
              className={`shop-primary-nav__item${isComingSoonPage ? ' active' : ''}`}
              aria-current={isComingSoonPage ? 'page' : undefined}
            >
              {t('shop.comingSoon', 'Coming Soon')}
            </Link>
            <Link
              to="/shop/orders"
              className={`shop-primary-nav__item${isOrdersPage ? ' active' : ''}`}
              aria-current={isOrdersPage ? 'page' : undefined}
            >
              {t('shop.orderHistory', 'Orders')}
            </Link>
            <Link
              to="/shop/local-supplier-invoices"
              className={`shop-primary-nav__item${isLocalSupplierInvoicesPage ? ' active' : ''}`}
              aria-current={isLocalSupplierInvoicesPage ? 'page' : undefined}
            >
              {t('shop.localSupplierInvoices', 'Local Invoices')}
            </Link>
            <Link
              to="/shop/purchase-sales-analysis"
              className={`shop-primary-nav__item${isPurchaseSalesAnalysisPage ? ' active' : ''}`}
              aria-current={isPurchaseSalesAnalysisPage ? 'page' : undefined}
            >
              {t('shop.purchaseSalesAnalysis', 'Purchase & Sales')}
            </Link>
            <Link
              to="/shop/supply-watches"
              className={`shop-primary-nav__item${isSupplyWatchesPage ? ' active' : ''}`}
              aria-current={isSupplyWatchesPage ? 'page' : undefined}
            >
              {t('shop.supplyWatches', 'Watched')}
              {supplyWatchSummary.restockedCount > 0 ? <Badge count={supplyWatchSummary.restockedCount} size="small" offset={[6, -2]} /> : null}
            </Link>
          </nav>

          <div className="shop-header-account">
            {isShopHomePage && !isMobileShopLayout
              ? <SupplierOrderingExtensionEntry presentation="desktop" />
              : null}
            <LanguageSwitch className="shop-header-language" size="small" compact />
            {currentUser ? (
              <Dropdown
                trigger={['click']}
                placement="bottomRight"
                menu={{
                  items: [
                    ...(access.canAccessDashboard ? [{
                      key: 'dashboard',
                      icon: <DashboardOutlined />,
                      label: t('menu.dashboard', 'Dashboard'),
                      onClick: () => window.open('/dashboard', '_blank'),
                    }] : []),
                    {
                      key: 'logout',
                      icon: <LogoutOutlined />,
                      label: t('layout.logout', 'Log Out'),
                      danger: true,
                      onClick: () => void handleLogout(),
                    },
                  ],
                }}
              >
                <button type="button" className="shop-account-button" aria-label={`${t('shop.account', 'Account')}: ${currentUser.username}`}>
                  <UserOutlined />
                </button>
              </Dropdown>
            ) : (
              <Link className="shop-account-button" to="/login" aria-label={t('login.submit', 'Login')}>
                <UserOutlined />
              </Link>
            )}
          </div>
        </div>
      </header>

      <div className="shop-ordering-toolbar">
        <div className="shop-shell">
          <Search
            placeholder={t('shop.searchOrScan', 'Search products or scan barcode')}
            onSearch={handleSearch}
            className="shop-ordering-search"
            enterButton
          />
          <Button
            className="shop-ordering-scan"
            icon={<ScanOutlined />}
            onClick={openShopScanner}
            data-shop-scan-trigger
          >
            {t('shop.scan.barcodeScan', 'Scan Barcode')}
          </Button>
          <div className="shop-ordering-store">
            <span>{t('shop.orderingFor', 'Ordering for:')}</span>
            <Select
              placeholder={t('shop.selectStore', 'Select Store')}
              className="shop-selector"
              value={selectedStore?.storeCode}
              onChange={(value) => void handleStoreChange(value)}
              allowClear
              options={userStores.map((item) => ({ value: item.storeCode, label: item.storeName }))}
            />
          </div>
          <button type="button" className="shop-ordering-cart" onClick={openCartDrawer}>
            <ShoppingCartOutlined aria-hidden="true" />
            <ShopCartSummary cart={cart} />
          </button>
          <Button type="primary" className="shop-ordering-review" onClick={openCartDrawer}>
            {t('shop.reviewOrder', 'Review Order')}
          </Button>
        </div>
      </div>

      <header className="shop-mobile-header">
        <div className="shop-mobile-top-row">
          <button type="button" className="shop-mobile-logo" onClick={() => navigate('/shop')} aria-label={t('shop.shopHome', 'Shop Home')}>
            <ShopBrandMark />
            <span className="shop-brand-copy"><strong>HB SHOP</strong><span>{t('shop.storeOrdering', 'Store Ordering')}</span></span>
          </button>
          <div className="shop-mobile-store">
            <span>{t('shop.orderingFor', 'Ordering for:')}</span>
            <Select
              placeholder={t('shop.selectStore', 'Select Store')}
              className="shop-mobile-store-select"
              value={selectedStore?.storeCode}
              onChange={(value) => void handleStoreChange(value)}
              allowClear
              options={userStores.map((item) => ({ value: item.storeCode, label: item.storeName }))}
              variant="borderless"
            />
          </div>
          <button type="button" className="shop-mobile-cart" onClick={openCartDrawer} aria-label={t('shop.shoppingCart', 'Shopping Cart')}>
            <Badge count={cart?.totalQuantity ?? 0} size="small" offset={[4, -2]}>
              <ShoppingCartOutlined />
            </Badge>
          </button>
          <button type="button" className="shop-mobile-menu" onClick={() => setMobileMoreVisible(true)} aria-label={t('shop.more', 'More')}>
            <MenuOutlined />
          </button>
        </div>
        {isShopHomePage ? (
          <div className="shop-mobile-ordering-tools">
            <Search
              placeholder={t('shop.searchOrScan', 'Search or scan barcode')}
              onSearch={handleSearch}
              className="shop-mobile-search"
              enterButton
            />
            <Button type="primary" icon={<ScanOutlined />} onClick={openShopScanner} data-shop-scan-trigger>
              {t('shop.scan.shortAction', 'Scan')}
            </Button>
          </div>
        ) : null}
      </header>

      <nav className="shop-mobile-bottom-nav" aria-label={t('shop.mobileNavigation', 'Shop mobile navigation')}>
        <button
          type="button"
          className={`shop-mobile-bottom-nav__item shop-mobile-bottom-nav__shop${isShopHomePage ? ' active' : ''}`}
          onClick={() => navigate('/shop')}
          aria-current={isShopHomePage ? 'page' : undefined}
        >
          <HomeOutlined /><span>{t('shop.mobileShop', 'Shop')}</span>
        </button>
        <button
          type="button"
          className="shop-mobile-bottom-nav__item shop-mobile-bottom-nav__categories"
          onClick={() => setMobileCategoryVisible(true)}
          aria-expanded={mobileCategoryVisible}
        >
          <AppstoreOutlined /><span>{t('shop.allCategories', 'All Categories')}</span>
        </button>
        <button
          type="button"
          className="shop-mobile-bottom-nav__item shop-mobile-bottom-nav__scan"
          onClick={openShopScanner}
          data-shop-scan-trigger
        >
          <span className="shop-mobile-bottom-nav__scan-icon"><ScanOutlined /></span>
          <span>{t('shop.scan.shortAction', 'Scan')}</span>
        </button>
        <button
          type="button"
          className={`shop-mobile-bottom-nav__item shop-mobile-bottom-nav__orders${isOrdersPage ? ' active' : ''}`}
          onClick={() => navigate('/shop/orders')}
          aria-current={isOrdersPage ? 'page' : undefined}
        >
          <OrderedListOutlined /><span>{t('shop.orderHistory', 'Orders')}</span>
        </button>
        <button
          type="button"
          className={`shop-mobile-bottom-nav__item shop-mobile-bottom-nav__more${isMorePage ? ' active' : ''}`}
          onClick={() => setMobileMoreVisible(true)}
          aria-current={isMorePage ? 'page' : undefined}
          aria-expanded={mobileMoreVisible}
        >
          <MoreOutlined /><span>{t('shop.more', 'More')}</span>
        </button>
      </nav>

      <div className={`shop-nav-bar${isShopHomePage ? '' : ' shop-nav-bar--secondary'}`}>
        {isShopHomePage ? (
          <div className="shop-blue-menu shop-category-nav">
            <div className="shop-shell">
              <button type="button" className="shop-category-trigger" onClick={() => setMobileCategoryVisible(true)}>
                <MenuOutlined /> {t('shop.categories', 'Categories')}
              </button>
              <button
                type="button"
                className={`shop-category-item${selectedCategory ? '' : ' active'}`}
                onClick={() => navigate('/shop')}
              >
                {t('shop.allProducts', 'All Products')}
              </button>
              {loadingCategories ? (
                <div className="shop-category-loading">
                  <Spin size="small" /> {t('shop.loadingCategories', 'Loading categories...')}
                </div>
              ) : (
                categories.map((category) => {
                  const childMenus = category.children?.length
                    ? (buildMenuItems(category.children) ?? [])
                    : []
                  const content = (
                    <button
                      type="button"
                      className={`shop-category-item${selectedCategory === category.categoryGUID ? ' active' : ''}`}
                      onClick={() => navigate(`/shop?category=${category.categoryGUID}`)}
                    >
                      {category.categoryName}
                      {childMenus.length ? <DownOutlined style={{ fontSize: 10, marginLeft: 4 }} /> : null}
                    </button>
                  )

                  if (!childMenus.length) {
                    return <span key={category.categoryGUID}>{content}</span>
                  }

                  return (
                    <Dropdown
                      key={category.categoryGUID}
                      menu={{
                        items: childMenus,
                        triggerSubMenuAction: isHoverSupported ? 'hover' : 'click',
                      }}
                      overlayClassName="shop-category-dropdown"
                      trigger={isHoverSupported ? ['hover'] : ['click']}
                    >
                      {content}
                    </Dropdown>
                  )
                })
              )}
            </div>
          </div>
        ) : (
          <div className="shop-orders-banner">
            <div className="shop-shell">
              <div className="shop-orders-banner-title">{t(shopBannerCopy.titleKey, shopBannerCopy.titleFallback)}</div>
              <div className="shop-orders-banner-subtitle">
                {t(shopBannerCopy.subtitleKey, shopBannerCopy.subtitleFallback)}
              </div>
            </div>
          </div>
        )}
      </div>

      <div className="shop-content">
        {showPreorderGateAlert ? (
          <Alert
            className="shop-preorder-gate-alert"
            type="warning"
            showIcon
            message={t('shop.preorder.gateBlocked', { count: preorderActivations.length })}
            description={t('shop.preorder.gateBlockedDescription')}
            action={<Space>{preorderActivations[0] ? <Button size="small" type="primary" onClick={() => navigate(`/shop/preorders/${preorderActivations[0].activationGuid}`)}>{t('shop.preorder.enterPreorder')}</Button> : null}</Space>}
          />
        ) : null}
        {showSupplyRestockedAlert ? (
          <Alert
            className="shop-supply-restocked-alert"
            type="success"
            showIcon
            message={t('shop.supplyRestockedBanner', { count: supplyWatchSummary.restockedCount })}
            action={<Button size="small" type="primary" onClick={() => navigate('/shop/supply-watches')}>{t('shop.supplyRestockedBannerAction', 'View')}</Button>}
          />
        ) : null}
        <RouteLoadBoundary resetKey={location.pathname}>
          <Outlet />
        </RouteLoadBoundary>
      </div>

      <div className="shop-footer">{t('shop.footer', '© 2026 Hotbargain International. All rights reserved.')}</div>

      <Modal
        open={preorderPromptOpen}
        title={(
          <Space>
            <GiftOutlined />
            <span>{t('shop.preorder.pendingTitle', { count: preorderActivations.length })}</span>
          </Space>
        )}
        onCancel={() => setDismissedPreorderPromptKey(preorderPrompt.key)}
        footer={[
          <Button key="later" onClick={() => setDismissedPreorderPromptKey(preorderPrompt.key)}>
            {t('shop.preorder.later')}
          </Button>,
          <Button
            key="action"
            type="primary"
            onClick={handleOpenPreorder}
          >
            {t('shop.preorder.enterPreorder')}
          </Button>,
        ]}
      >
        <Space direction="vertical" size={8}>
          {preorderActivations.map((item) => {
            const estimatedArrivalDate = getPreorderDateDisplay(item.estimatedArrivalDate)
            return (
              <div key={item.activationGuid}>
                <strong>{item.templateName} · {t('shop.preorder.period', { sequence: item.sequenceNumber })}</strong>
                <br />
                <span>{t('shop.preorder.deadline', { date: preorderDateTimeFormatter.format(new Date(item.endAtUtc)) })}</span>
                {estimatedArrivalDate ? <><br /><span>{t('shop.preorder.estimatedArrivalDate', { date: estimatedArrivalDate })}</span></> : null}
              </div>
            )
          })}
        </Space>
      </Modal>

      <ShopCartDrawer
        open={cartDrawerOpen}
        onClose={() => setCartDrawerOpen(false)}
        cart={cart}
        loading={cartDrawerLoading}
        preorderBlocked={effectivePreorderBlocked}
        onOpenPreorder={preorderActivations[0] ? () => navigate(`/shop/preorders/${preorderActivations[0].activationGuid}`) : undefined}
        onPreorderRequired={refreshPreorderGate}
        onCartChanged={refreshFullCart}
      />

      <Drawer
        title={t('shop.categories', 'Categories')}
        placement="left"
        onClose={() => setMobileCategoryVisible(false)}
        open={mobileCategoryVisible}
        width="85%"
        className="shop-mobile-category-drawer"
      >
        <Button
          type={selectedCategory ? 'default' : 'primary'}
          block
          className="shop-mobile-category-all"
          onClick={() => {
            navigate('/shop')
            setMobileCategoryVisible(false)
          }}
        >
          {t('shop.allProducts', 'All Products')}
        </Button>
        <Menu mode="inline" items={buildMenuItems(categories)} selectedKeys={selectedCategory ? [selectedCategory] : []} />
      </Drawer>

      <Drawer
        title={t('shop.more', 'More')}
        placement="right"
        onClose={() => setMobileMoreVisible(false)}
        open={mobileMoreVisible}
        width="min(86vw, 360px)"
        className="shop-mobile-more-drawer"
      >
        <div className="shop-mobile-more-menu">
          <button
            type="button"
            className={isPreorderPage ? 'active' : ''}
            onClick={() => {
              setMobileMoreVisible(false)
              handleOpenPreorder()
            }}
          >
            <GiftOutlined /><span>{t('shop.preorder.navigation', 'Preorder')}</span>
          </button>
          <Link
            to="/shop/best-sellers"
            className={isBestSellersPage ? 'active' : ''}
            onClick={() => setMobileMoreVisible(false)}
            aria-current={isBestSellersPage ? 'page' : undefined}
          >
            <AppstoreOutlined /><span>{t('shop.bestSellers', 'Best Sellers')}</span>
          </Link>
          <Link
            to="/shop/coming-soon"
            className={isComingSoonPage ? 'active' : ''}
            onClick={() => setMobileMoreVisible(false)}
            aria-current={isComingSoonPage ? 'page' : undefined}
          >
            <AppstoreOutlined /><span>{t('shop.comingSoon', 'Coming Soon')}</span>
          </Link>
          <Link
            to="/shop/local-supplier-invoices"
            className={isLocalSupplierInvoicesPage ? 'active' : ''}
            onClick={() => setMobileMoreVisible(false)}
            aria-current={isLocalSupplierInvoicesPage ? 'page' : undefined}
          >
            <FileTextOutlined /><span>{t('shop.localSupplierInvoices', 'Local Invoices')}</span>
          </Link>
          <Link
            to="/shop/purchase-sales-analysis"
            className={isPurchaseSalesAnalysisPage ? 'active' : ''}
            onClick={() => setMobileMoreVisible(false)}
            aria-current={isPurchaseSalesAnalysisPage ? 'page' : undefined}
          >
            <BarChartOutlined /><span>{t('shop.purchaseSalesAnalysis', 'Purchase & Sales')}</span>
          </Link>
          <Link
            to="/shop/supply-watches"
            className={isSupplyWatchesPage ? 'active' : ''}
            onClick={() => setMobileMoreVisible(false)}
            aria-current={isSupplyWatchesPage ? 'page' : undefined}
          >
            <BellOutlined /><span>{t('shop.supplyWatches', 'Watched')}</span>
            {supplyWatchSummary.restockedCount > 0 ? <Badge count={supplyWatchSummary.restockedCount} size="small" /> : null}
          </Link>
          <div className="shop-mobile-more-menu__separator" />
          {isShopHomePage && isMobileShopLayout
            ? <SupplierOrderingExtensionEntry presentation="mobile-nav" />
            : null}
          <div className="shop-mobile-more-menu__utility">
            <span>{t('shop.language', 'Language')}</span>
            <LanguageSwitch size="small" compact />
          </div>
          {currentUser ? (
            <div className="shop-mobile-more-menu__utility">
              <span>{t('shop.account', 'Account')}</span>
              <strong>{currentUser.username}</strong>
            </div>
          ) : (
            <Link to="/login" onClick={() => setMobileMoreVisible(false)}>
              <UserOutlined /><span>{t('login.submit', 'Login')}</span>
            </Link>
          )}
          {access.canAccessDashboard ? (
            <button type="button" onClick={() => window.open('/dashboard', '_blank')}>
              <DashboardOutlined /><span>{t('menu.dashboard', 'Dashboard')}</span>
            </button>
          ) : null}
          {currentUser ? (
            <button type="button" className="danger" onClick={() => void handleLogout()}>
              <LogoutOutlined /><span>{t('layout.logout', 'Log Out')}</span>
            </button>
          ) : null}
        </div>
      </Drawer>
    </div>
  )
}
