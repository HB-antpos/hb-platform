import { ShoppingCartOutlined } from '@ant-design/icons'
import { Breadcrumb, Button, Empty, Pagination, Select, Space, Spin, Tag, Tooltip, Typography, message } from 'antd'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom'
import {
  shouldIgnoreShopBarcodeSubmit,
  type ShopBarcodeSubmitSource,
} from '../../components/shopBarcodeSubmitGate'
import { withShopBarcodeRequestTimeout } from '../../components/shopBarcodeRequestTimeout'
import ShopScanBar from '../../components/ShopScanBar'
import SupplyStatusCard from '../../components/SupplyNotice/SupplyStatusCard'
import { useSupplyStatusLookup } from '../../components/SupplyNotice/useSupplyStatusLookup'
import type { ShopCameraSubmitOutcome } from '../../components/ShopCameraScanner'
import ShopScanResultPicker from '../../components/ShopScanResultPicker'
import { PRODUCT_GRADE_CONFIG } from '../../types/productGrade'
import { useBarcodeScanner } from '../../hooks/useBarcodeScanner'
import ProductCard from './components/ProductCard'
import ProductActivityHistoryModal from './components/ProductActivityHistoryModal'
import {
  buildShopHomeDynamicDataRequestIdentity,
  createShopHomeDynamicDataRequestCoordinator,
  createShopHomeDynamicDataStoreScopeCoordinator,
  createShopHomeSalesSummaryRequestCoordinator,
  mergeShopHomeBaseDynamicDataMap,
  mergeShopHomeCartDynamicData,
  readShopHomeDynamicDataRequestProductCodes,
  runShopHomeDynamicDataRequest,
  runShopHomeSalesSummaryRequest,
  runShopHomeStoreScopedDynamicDataRequest,
} from './shopHomeCartDynamicData'
import {
  addStoreOrderCartItem,
  getActiveStoreOrderCart,
  getStoreOrderProducts,
  getStoreOrderProductsDynamicData,
  getStoreOrderProductSalesSummary,
  lookupStoreOrderProductsByBarcode,
  removeStoreOrderCartItem,
  updateStoreOrderCartItem,
} from '../../services/storeOrderService'
import { getCategoryTree, type WarehouseCategoryNode } from '../../services/warehouseCategoryService'
import {
  buildWarehouseCategoryLookup,
  getWarehouseProductCategoryTooltip,
  type WarehouseCategoryLookup,
} from '../Warehouse/Products/categoryPath'
import { useShopStore } from '../../store/shop'
import type {
  StoreOrderDynamicData,
  StoreOrderProductItem,
  StoreOrderProductSalesSummaryItem,
  StoreOrderScanStatus,
} from '../../types/storeOrder'
import {
  isScanFeedbackUnlocked,
  playScanFeedback,
  unlockScanFeedback,
} from '../../utils/scanFeedback'

function getShopHomePerfNow() {
  return typeof performance !== 'undefined' ? performance.now() : Date.now()
}

function logShopHomePerf(stage: string, payload: Record<string, unknown>) {
  if (typeof console === 'undefined') {
    return
  }

  const isDebugEnabled =
    import.meta.env.DEV ||
    (typeof window !== 'undefined' && window.localStorage.getItem('shopHomePerf') === '1')

  if (!isDebugEnabled) {
    return
  }

  console.info('[shop-home-perf]', stage, payload)
}

// 商城列表页大小必须与顶部筛选和底部分页保持一致。
const SHOP_HOME_PAGE_SIZE_OPTIONS = [50, 100, 200, 500]
// 商品卡连续点击时只在用户短暂停顿后提交最终数量，避免并发请求覆盖最新输入。
const SHOP_PRODUCT_QUANTITY_UPDATE_DEBOUNCE_MS = 300

export default function ShopHomePage() {
  const { t, i18n } = useTranslation()
  const navigate = useNavigate()
  const [searchParams] = useSearchParams()
  const categoryId = searchParams.get('category')
  const keyword = searchParams.get('keyword')
  // 每次导航都会生成新的 location.key：再次搜索同一关键字、重复点当前分类或首页时 URL 不变，
  // 靠它重新请求商品，避免上架、改价后回来复查仍看到旧结果。
  const { key: navigationKey } = useLocation()

  const [products, setProducts] = useState<StoreOrderProductItem[]>([])
  const [dynamicDataMap, setDynamicDataMap] = useState<Record<string, StoreOrderDynamicData>>({})
  const [loading, setLoading] = useState(false)
  const [total, setTotal] = useState(0)
  const [currentPage, setCurrentPage] = useState(1)
  const [pageSize, setPageSize] = useState(200)
  const [categoryName, setCategoryName] = useState('')
  const [categoryTree, setCategoryTree] = useState<WarehouseCategoryNode[]>([])
  const [parentChain, setParentChain] = useState<WarehouseCategoryNode[]>([])
  const [scanStatus, setScanStatus] = useState<StoreOrderScanStatus>('ready')
  const [scanEnabled, setScanEnabled] = useState(false)
  const [scanBusy, setScanBusy] = useState(false)
  const [cameraEnabled, setCameraEnabled] = useState(false)
  const [cameraSessionKey, setCameraSessionKey] = useState('')
  const [scanMessage, setScanMessage] = useState(t('shop.scan.waiting'))
  const [lastScannedCode, setLastScannedCode] = useState('')
  const [lastScannedProduct, setLastScannedProduct] = useState('')
  const [lastProductImage, setLastProductImage] = useState('')
  const [lastItemNumber, setLastItemNumber] = useState('')
  const [lastAddedQuantity, setLastAddedQuantity] = useState<number>()
  const [lastCartTotalQuantity, setLastCartTotalQuantity] = useState<number>()
  const [soundEnabled, setSoundEnabled] = useState(isScanFeedbackUnlocked())
  const [scanCandidates, setScanCandidates] = useState<StoreOrderProductItem[]>([])
  const [pickerOpen, setPickerOpen] = useState(false)
  const [pickerLoading, setPickerLoading] = useState(false)
  const [pickerDynamicDataMap, setPickerDynamicDataMap] = useState<Record<string, StoreOrderDynamicData>>({})
  const [gradeFilter, setGradeFilter] = useState<string[]>([])
  const [quantityLoadingMap, setQuantityLoadingMap] = useState<Record<string, boolean>>({})
  const [optimisticCartQuantityMap, setOptimisticCartQuantityMap] = useState<Record<string, number>>({})
  const [removingCartProductMap, setRemovingCartProductMap] = useState<Record<string, boolean>>({})
  const [cartOnlyFilter, setCartOnlyFilter] = useState(false)
  const [activityModalProduct, setActivityModalProduct] = useState<StoreOrderProductItem | null>(null)
  const [activityModalOpen, setActivityModalOpen] = useState(false)
  const quantityUpdateTimersRef = useRef<Record<string, number>>({})
  const quantityUpdateVersionRef = useRef<Record<string, number>>({})
  const dynamicDataMapRef = useRef<Record<string, StoreOrderDynamicData>>({})
  const dynamicDataRequestCoordinatorRef = useRef(createShopHomeDynamicDataRequestCoordinator())
  const salesSummaryRequestCoordinatorRef = useRef(createShopHomeSalesSummaryRequestCoordinator())
  const dynamicDataStoreScopeCoordinatorRef = useRef(createShopHomeDynamicDataStoreScopeCoordinator())
  const scanBusyRef = useRef(false)
  const cameraActiveRef = useRef(false)
  const pickerOpenRef = useRef(false)
  const pickerLoadingRef = useRef(false)
  const selectedStore = useShopStore((state) => state.selectedStore)
  // 搜索 / 扫码零结果时补查“暂停供货”的商品，让分店知道是下架了而不是扫错码。
  const supplyLookup = useSupplyStatusLookup(selectedStore?.storeCode ?? null)
  const selectedStoreCodeRef = useRef<string | null>(null)
  const previousSelectedStoreCodeRef = useRef(selectedStore?.storeCode ?? null)
  const cart = useShopStore((state) => state.cart)
  const setCart = useShopStore((state) => state.setCart)
  const shouldShowCategoryPath = Boolean(keyword)
  selectedStoreCodeRef.current = selectedStore?.storeCode ?? null

  const cameraIsActive = cameraEnabled && cameraSessionKey === (selectedStore?.storeCode ?? '')
  cameraActiveRef.current = cameraIsActive

  useEffect(() => {
    const nextStoreCode = selectedStore?.storeCode ?? null
    if (previousSelectedStoreCodeRef.current === nextStoreCode) {
      return
    }

    previousSelectedStoreCodeRef.current = nextStoreCode
    Object.values(quantityUpdateTimersRef.current).forEach((timer) => window.clearTimeout(timer))
    quantityUpdateTimersRef.current = {}
    quantityUpdateVersionRef.current = {}
    setOptimisticCartQuantityMap({})
    setRemovingCartProductMap({})
    setQuantityLoadingMap({})
    setCameraEnabled(false)
    setCameraSessionKey('')
    cameraActiveRef.current = false
    pickerOpenRef.current = false
    pickerLoadingRef.current = false
    setPickerOpen(false)
    setPickerLoading(false)
    setScanCandidates([])
    // 切店立即关闭并清空商品活动历史弹窗，旧请求由弹窗内部 version/identity 守卫丢弃。
    setActivityModalOpen(false)
    setActivityModalProduct(null)
  }, [selectedStore?.storeCode])

  useEffect(() => {
    return () => {
      Object.values(quantityUpdateTimersRef.current).forEach((timer) => window.clearTimeout(timer))
      quantityUpdateTimersRef.current = {}
      quantityUpdateVersionRef.current = {}
    }
  }, [])

  const categoryLookup = useMemo<WarehouseCategoryLookup | null>(() => {
    if (!categoryTree.length) {
      return null
    }

    return buildWarehouseCategoryLookup(categoryTree)
  }, [categoryTree])

  const categoryPathMap = useMemo<Record<string, string>>(() => {
    if (!shouldShowCategoryPath || !categoryLookup) {
      return {}
    }

    return products.reduce<Record<string, string>>((acc, product) => {
      // 搜索结果需要展示完整分类路径，优先用 GUID，缺失时沿用分类名称兜底。
      const categoryPath = getWarehouseProductCategoryTooltip(product, categoryLookup, i18n.language)
      if (categoryPath) {
        acc[product.productCode] = categoryPath
      }
      return acc
    }, {})
  }, [categoryLookup, i18n.language, products, shouldShowCategoryPath])

  const cartProductItems = useMemo<StoreOrderProductItem[]>(() => {
    return (cart?.items ?? []).map((item) => ({
      productCode: item.productCode,
      itemNumber: item.itemNumber,
      barcode: item.barcode,
      productName: item.productName,
      productImage: item.productImage,
      // 购物车明细没有完整商品 DTO；复用购物车价格和最小订量即可驱动商品卡展示与数量更新。
      oemPrice: item.price,
      importPrice: item.importPrice,
      minOrderQuantity: item.minOrderQuantity || 1,
      stockQuantity: 0,
      isInStock: item.isActive,
    }))
  }, [cart?.items])

  const cartQuantityByProductCode = useMemo<Record<string, number>>(() => {
    return (cart?.items ?? []).reduce<Record<string, number>>((acc, item) => {
      acc[item.productCode] = item.quantity
      return acc
    }, {})
  }, [cart?.items])

  const cartProductPageItems = useMemo(() => {
    const startIndex = (currentPage - 1) * pageSize
    return cartProductItems.slice(startIndex, startIndex + pageSize)
  }, [cartProductItems, currentPage, pageSize])

  const displayProducts = cartOnlyFilter ? cartProductPageItems : products
  const displayTotal = cartOnlyFilter ? cartProductItems.length : total
  const cartProductCount = (cart?.items.length || cart?.totalSKU) ?? 0
  const displayProductCodes = useMemo(
    () => displayProducts.map((product) => product.productCode),
    [displayProducts],
  )
  const dynamicDataRequestIdentity = buildShopHomeDynamicDataRequestIdentity({
    active: Boolean(selectedStore?.storeCode && displayProductCodes.length),
    storeCode: selectedStore?.storeCode,
    productCodes: displayProductCodes,
  })

  const pageTitle = useMemo(() => {
    if (cartOnlyFilter) {
      return t('shop.cartProductsTitle')
    }

    if (keyword) {
      return t('shop.searchTitle', { keyword })
    }

    if (categoryId) {
      return categoryName || t('shop.categoryProducts')
    }

    return t('shop.allProducts')
  }, [cartOnlyFilter, categoryId, categoryName, keyword, t])

  useLayoutEffect(() => {
    const coordinator = dynamicDataStoreScopeCoordinatorRef.current
    const storeCode = selectedStore?.storeCode ?? null
    // 门店 scope 只能在已提交生命周期切换，不能让候选 render 污染 ABA generation。
    coordinator.activate(storeCode)

    return () => {
      coordinator.deactivate(storeCode)
    }
  }, [selectedStore?.storeCode])

  useLayoutEffect(() => {
    dynamicDataMapRef.current = dynamicDataMap
  }, [dynamicDataMap])

  useLayoutEffect(() => {
    const coordinator = dynamicDataRequestCoordinatorRef.current
    const salesCoordinator = salesSummaryRequestCoordinatorRef.current
    // 只有已提交的 render 才能切换请求身份，避免并发候选 render 污染当前 token。
    coordinator.activate(dynamicDataRequestIdentity)
    salesCoordinator.activate(dynamicDataRequestIdentity)

    return () => {
      coordinator.deactivate(dynamicDataRequestIdentity)
      salesCoordinator.deactivate(dynamicDataRequestIdentity)
    }
  }, [dynamicDataRequestIdentity])

  useEffect(() => {
    setCurrentPage(1)
  }, [categoryId, keyword, navigationKey])

  useEffect(() => {
    if (!cartOnlyFilter) {
      return
    }

    const totalPages = Math.max(1, Math.ceil(cartProductItems.length / pageSize))
    if (currentPage > totalPages) {
      setCurrentPage(totalPages)
    }
  }, [cartOnlyFilter, cartProductItems.length, currentPage, pageSize])

  useEffect(() => {
    if (cart?.isSummaryOnly || cart?.items.length) {
      return
    }

    Object.values(quantityUpdateTimersRef.current).forEach((timer) => window.clearTimeout(timer))
    quantityUpdateTimersRef.current = {}
    quantityUpdateVersionRef.current = {}
    setOptimisticCartQuantityMap({})
    setRemovingCartProductMap({})
    setQuantityLoadingMap({})
  }, [cart?.isSummaryOnly, cart?.items.length])

  const loadDynamicDataMap = useCallback(
    async (productCodes: string[]) => {
      if (!selectedStore?.storeCode || !productCodes.length) {
        return {}
      }

      const result = await getStoreOrderProductsDynamicData({
        storeCode: selectedStore.storeCode,
        productCodes,
        includeSales: false,
      })

      return result.reduce<Record<string, StoreOrderDynamicData>>((acc, item) => {
        acc[item.productCode] = item
        return acc
      }, {})
    },
    [selectedStore?.storeCode],
  )

  const loadSalesSummaryMap = useCallback(
    async (productCodes: string[]) => {
      if (!selectedStore?.storeCode || !productCodes.length) {
        return {}
      }

      const result = await getStoreOrderProductSalesSummary({
        storeCode: selectedStore.storeCode,
        productCodes,
      })
      return result.reduce<Record<string, StoreOrderProductSalesSummaryItem>>((salesMap, item) => {
        salesMap[item.productCode] = item
        return salesMap
      }, {})
    },
    [selectedStore?.storeCode],
  )

  useEffect(() => {
    let cancelled = false

    const fetchProducts = async () => {
      if (!selectedStore?.storeCode) {
        setProducts([])
        setTotal(0)
        setLoading(false)
        return
      }

      setLoading(true)
      const startedAt = getShopHomePerfNow()
      try {
        const result = await getStoreOrderProducts({
          storeCode: selectedStore.storeCode,
          pageNumber: currentPage,
          pageSize,
          categoryGUID: categoryId || undefined,
          itemNumber: keyword || undefined,
          productName: keyword || undefined,
          sortBy: 'Default',
          grade: gradeFilter.length ? [...gradeFilter].sort().join(',') : undefined,
        })

        if (cancelled) {
          return
        }

        setProducts(result.items)
        setTotal(result.total)
        if (!result.items.length && keyword && !cartOnlyFilter) {
          void supplyLookup.lookup(keyword)
        } else {
          supplyLookup.clear()
        }
        logShopHomePerf('products.done', {
          storeCode: selectedStore.storeCode,
          pageNumber: currentPage,
          pageSize,
          itemCount: result.items.length,
          total: result.total,
          elapsedMs: Math.round(getShopHomePerfNow() - startedAt),
        })

        if (categoryId) {
          setCategoryName(result.items[0]?.categoryName || t('shop.categoryProducts'))
        } else {
          setCategoryName('')
        }
      } catch (error) {
        if (!cancelled) {
          logShopHomePerf('products.error', {
            storeCode: selectedStore.storeCode,
            pageNumber: currentPage,
            pageSize,
            elapsedMs: Math.round(getShopHomePerfNow() - startedAt),
          })
          message.error(t('shop.loadProductsFailed'))
          setProducts([])
          setTotal(0)
        }
      } finally {
        if (!cancelled) {
          setLoading(false)
        }
      }
    }

    void fetchProducts()

    return () => {
      cancelled = true
    }
  }, [categoryId, currentPage, keyword, navigationKey, pageSize, selectedStore?.storeCode, gradeFilter, t])

  useEffect(() => {
    let cancelled = false

    const loadCategoryTree = async () => {
      if (!categoryId && !keyword) {
        setCategoryTree([])
        return
      }

      try {
        const tree = await getCategoryTree()
        if (!cancelled) {
          setCategoryTree(tree)
        }
      } catch (error) {
        if (!cancelled) {
          setCategoryTree([])
        }
      }
    }

    void loadCategoryTree()

    return () => {
      cancelled = true
    }
  }, [categoryId, keyword])

  useEffect(() => {
    if (!categoryId) {
      setParentChain([])
      return
    }

    const path: WarehouseCategoryNode[] = []

    const dfs = (nodes: WarehouseCategoryNode[]): boolean => {
      for (const node of nodes) {
        path.push(node)
        if (node.categoryGUID === categoryId) {
          return true
        }
        if (node.children?.length && dfs(node.children)) {
          return true
        }
        path.pop()
      }
      return false
    }

    dfs(categoryTree)
    setParentChain(path)
  }, [categoryId, categoryTree])

  useEffect(() => {
    const identity = dynamicDataRequestIdentity
    if (!identity) {
      setDynamicDataMap({})
      return
    }

    const coordinator = dynamicDataRequestCoordinatorRef.current
    const token = coordinator.begin(identity)
    const requestProductCodes = readShopHomeDynamicDataRequestProductCodes(identity)
    const prioritySalesProductCodes = requestProductCodes.slice(0, 50)
    const remainderSalesProductCodes = requestProductCodes.slice(50)
    const startedAt = getShopHomePerfNow()
    void runShopHomeDynamicDataRequest({
      coordinator,
      token,
      productCodes: requestProductCodes,
      request: loadDynamicDataMap,
      onSuccess: (nextMap) => {
        setDynamicDataMap(nextMap)
        logShopHomePerf('dynamic-base.done', {
          storeCode: selectedStore?.storeCode,
          requestCount: requestProductCodes.length,
          resultCount: Object.keys(nextMap).length,
          elapsedMs: Math.round(getShopHomePerfNow() - startedAt),
        })

        const salesCoordinator = salesSummaryRequestCoordinatorRef.current
        const mergeSalesSummary = (salesMap: Record<string, StoreOrderProductSalesSummaryItem>) => {
          setDynamicDataMap((previousMap) => {
            let hasChanged = false
            const nextMap = { ...previousMap }
            Object.entries(salesMap).forEach(([
              productCode,
              {
                salesQuantitySinceLastArrival,
                lastArrivalDate,
                lastArrivalQuantity,
                lastArrivalOrderQuantity,
                lastArrivalOrderDate,
                salesStartDate,
              },
            ]) => {
              const previousData = previousMap[productCode]
              // 销量与来货日期、来货数量同批回填；都没变时保留原引用，商品卡才能跳过重渲染。
              if (
                !previousData
                || (previousData.salesQuantitySinceLastArrival === salesQuantitySinceLastArrival
                  && previousData.lastArrivalDate === lastArrivalDate
                  && previousData.lastArrivalQuantity === lastArrivalQuantity
                  && previousData.lastArrivalOrderQuantity === lastArrivalOrderQuantity
                  && previousData.lastArrivalOrderDate === lastArrivalOrderDate
                  && previousData.salesStartDate === salesStartDate)
              ) {
                return
              }

              hasChanged = true
              nextMap[productCode] = {
                ...previousData,
                salesQuantitySinceLastArrival,
                lastArrivalDate,
                lastArrivalQuantity,
                lastArrivalOrderQuantity,
                lastArrivalOrderDate,
                salesStartDate,
              }
            })
            return hasChanged ? nextMap : previousMap
          })
        }
        const requestSalesBatch = (productCodes: string[], stage: 'sales-priority.done' | 'sales-remainder.done') => {
          const salesStartedAt = getShopHomePerfNow()
          const requestRemainderSales = () => {
            if (stage === 'sales-priority.done' && remainderSalesProductCodes.length > 0) {
              void requestSalesBatch(remainderSalesProductCodes, 'sales-remainder.done')
            }
          }
          return runShopHomeSalesSummaryRequest({
            coordinator: salesCoordinator,
            token: salesCoordinator.begin(identity),
            productCodes,
            request: loadSalesSummaryMap,
            onSuccess: (salesMap) => {
              mergeSalesSummary(salesMap)
              logShopHomePerf(stage, {
                storeCode: selectedStore?.storeCode,
                requestCount: productCodes.length,
                resultCount: Object.keys(salesMap).length,
                elapsedMs: Math.round(getShopHomePerfNow() - salesStartedAt),
              })
              requestRemainderSales()
            },
            // 优先批失败不清空基础数据，并继续尝试余量；旧 token 不会进入该回调。
            onError: requestRemainderSales,
          })
        }

        void requestSalesBatch(prioritySalesProductCodes, 'sales-priority.done')
      },
      onError: () => {
        setDynamicDataMap({})
      },
    })

    return () => {
      coordinator.invalidate(token)
    }
    // identity 已编码门店和当前展示页商品；购物车数量变化不应重复请求同一批商品。
  }, [dynamicDataRequestIdentity, loadDynamicDataMap, loadSalesSummaryMap])

  useEffect(() => {
    let cancelled = false

    const loadPickerDynamicData = async () => {
      if (!pickerOpen || !selectedStore?.storeCode || !scanCandidates.length) {
        setPickerDynamicDataMap({})
        return
      }

      try {
        const nextMap = await loadDynamicDataMap(scanCandidates.map((item) => item.productCode))
        if (!cancelled) {
          setPickerDynamicDataMap(nextMap)
        }
      } catch (error) {
        if (!cancelled) {
          setPickerDynamicDataMap({})
        }
      }
    }

    void loadPickerDynamicData()

    return () => {
      cancelled = true
    }
  }, [loadDynamicDataMap, pickerOpen, scanCandidates, selectedStore?.storeCode])

  const refreshCart = useCallback(async () => {
    const storeCode = selectedStore?.storeCode ?? null
    if (!storeCode) {
      setCart(null)
      return true
    }

    const coordinator = dynamicDataStoreScopeCoordinatorRef.current
    const token = coordinator.capture(storeCode)
    if (!token) {
      return false
    }

    const nextCart = await getActiveStoreOrderCart(storeCode)
    if (!coordinator.isCurrent(token)) {
      return false
    }

    setCart(nextCart)
    return true
  }, [selectedStore?.storeCode, setCart])

  const ensureFullCart = useCallback(async () => {
    const storeCode = selectedStore?.storeCode ?? null
    if (!storeCode) {
      setCart(null)
      return null
    }

    if (cart && !cart.isSummaryOnly && cart.storeCode === storeCode) {
      return cart
    }

    // summary 只服务首屏；需要明细交互时再补 full cart，避免登录和切店拖慢。
    const nextCart = await getActiveStoreOrderCart(storeCode)
    if (selectedStoreCodeRef.current !== storeCode) {
      return null
    }

    setCart(nextCart)
    return nextCart
  }, [cart, selectedStore?.storeCode, setCart])

  const refreshDynamicDataForProducts = useCallback(
    async (productCodes: string[]) => {
      const normalizedCodes = productCodes
        .filter((code) => Boolean(code))
        .filter((code, index, allCodes) => allCodes.indexOf(code) === index)

      const storeCode = selectedStore?.storeCode ?? null
      const coordinator = dynamicDataStoreScopeCoordinatorRef.current
      const token = coordinator.capture(storeCode)
      if (!token || !normalizedCodes.length) {
        return
      }

      await runShopHomeStoreScopedDynamicDataRequest({
        coordinator,
        token,
        productCodes: normalizedCodes,
        request: loadDynamicDataMap,
        onSuccess: (nextMap) => {
          setDynamicDataMap((prev) => mergeShopHomeBaseDynamicDataMap(prev, nextMap))
          setPickerDynamicDataMap((prev) => ({ ...prev, ...nextMap }))
        },
      })
    },
    [loadDynamicDataMap, selectedStore?.storeCode],
  )

  const updateScanFeedback = useCallback(
    (
      nextStatus: StoreOrderScanStatus,
      nextMessage: string,
      options?: {
        barcode?: string
        productName?: string
        productImage?: string
        itemNumber?: string
        quantity?: number
        cartTotalQuantity?: number
        tone?: Parameters<typeof playScanFeedback>[0]
      },
    ) => {
      setScanStatus(nextStatus)
      setScanMessage(nextMessage)
      setLastScannedCode(options?.barcode ?? '')
      setLastScannedProduct(options?.productName ?? '')
      setLastProductImage(options?.productImage ?? '')
      setLastItemNumber(options?.itemNumber ?? '')
      setLastAddedQuantity(options?.quantity)
      setLastCartTotalQuantity(options?.cartTotalQuantity)

      if (options?.tone) {
        playScanFeedback(options.tone)
      }
    },
    [],
  )

  const addScannedProductToCart = useCallback(
    async (
      product: StoreOrderProductItem,
      barcode: string,
      storeCode: string | null = selectedStoreCodeRef.current,
    ): Promise<ShopCameraSubmitOutcome> => {
      if (!storeCode) {
        updateScanFeedback('blocked', t('shop.scan.selectStoreFirst'), {
          barcode,
          tone: 'blocked',
        })
        return 'blocked'
      }
      const quantity = product.minOrderQuantity > 0 ? product.minOrderQuantity : 1

      try {
        const nextCart = await addStoreOrderCartItem({
          storeCode,
          productCode: product.productCode,
          quantity,
        })

        if (selectedStoreCodeRef.current !== storeCode) {
          return 'ignored'
        }

        setCart(nextCart)

        const cartTotalQty = nextCart?.items.find((ci) => ci.productCode === product.productCode)?.quantity

        updateScanFeedback('added', t('shop.scan.addedProduct', { name: product.productName || product.productCode }), {
          barcode,
          productName: product.productName || product.productCode,
          productImage: product.productImage,
          itemNumber: product.itemNumber,
          quantity,
          cartTotalQuantity: cartTotalQty,
          tone: 'success',
        })
        refreshDynamicDataForProducts([product.productCode]).catch(() => {})
        return 'added'
      } catch {
        if (selectedStoreCodeRef.current !== storeCode) {
          return 'ignored'
        }

        updateScanFeedback('error', t('shop.scan.addItemFailed'), {
          barcode,
          productName: product.productName || product.productCode,
          tone: 'error',
        })
        return 'error'
      }
    },
    [refreshDynamicDataForProducts, setCart, t, updateScanFeedback],
  )

  const handleBarcodeSubmit = useCallback(
    async (
      rawBarcode: string,
      source: ShopBarcodeSubmitSource,
    ): Promise<ShopCameraSubmitOutcome> => {
      const barcode = rawBarcode.trim()
      if (shouldIgnoreShopBarcodeSubmit({
        barcode,
        busy: scanBusyRef.current,
        cameraActive: cameraActiveRef.current,
        pickerOpen: pickerOpenRef.current,
        source,
      })) {
        return 'ignored'
      }

      // state 更新前先占用同步门闩，连续解码回调不得穿过同一渲染帧并发提交。
      scanBusyRef.current = true
      setScanBusy(true)
      setScanStatus('scanning')
      setScanMessage(t('shop.scan.lookingUp', { barcode }))

      const storeCode = selectedStoreCodeRef.current
      try {
        if (!storeCode) {
          updateScanFeedback('blocked', t('shop.scan.selectStoreFirst'), {
            barcode,
            tone: 'blocked',
          })
          return 'blocked'
        }

        const result = await withShopBarcodeRequestTimeout(
          (signal) => lookupStoreOrderProductsByBarcode(barcode, storeCode, signal),
        )
        if (selectedStoreCodeRef.current !== storeCode) {
          return 'ignored'
        }

        if (!result.items.length) {
          const paused = await supplyLookup.lookup(barcode)
          if (selectedStoreCodeRef.current !== storeCode) {
            return 'ignored'
          }
          updateScanFeedback('not_found', paused.length ? t('supplyStatusCard.pausedHeading') : t('shop.scan.barcodeNotFound'), {
            barcode,
            tone: 'not-found',
          })
          return 'not_found'
        }

        if (result.items.length === 1) {
          return await addScannedProductToCart(result.items[0], barcode, storeCode)
        }

        setScanCandidates(result.items)
        pickerOpenRef.current = true
        setPickerOpen(true)
        updateScanFeedback('multiple', t('shop.scan.multipleMatchesFound'), {
          barcode,
          tone: 'multiple',
        })
        return 'multiple'
      } catch {
        if (selectedStoreCodeRef.current !== storeCode) {
          return 'ignored'
        }

        updateScanFeedback('error', t('shop.scan.lookupFailed'), {
          barcode,
          tone: 'error',
        })
        return 'error'
      } finally {
        scanBusyRef.current = false
        setScanBusy(false)
      }
    },
    [addScannedProductToCart, t, updateScanFeedback],
  )

  const handleToggleCamera = useCallback(() => {
    if (cameraEnabled) {
      cameraActiveRef.current = false
      setCameraEnabled(false)
      setCameraSessionKey('')
      return
    }

    if (pickerOpenRef.current) {
      return
    }

    const storeCode = selectedStoreCodeRef.current
    if (!storeCode) {
      updateScanFeedback('blocked', t('shop.scan.selectStoreFirst'), {
        tone: 'blocked',
      })
      return
    }

    cameraActiveRef.current = true
    setCameraSessionKey(storeCode)
    setCameraEnabled(true)
  }, [cameraEnabled, t, updateScanFeedback])

  const handleCameraBarcodeSubmit = useCallback(
    (barcode: string) => handleBarcodeSubmit(barcode, 'camera'),
    [handleBarcodeSubmit],
  )

  const handleUnlockSound = useCallback(async () => {
    const success = await unlockScanFeedback()
    setSoundEnabled(success)
    setScanMessage(success ? t('shop.scan.soundFeedbackReady') : t('shop.scan.soundUnavailable'))
  }, [t])

  useBarcodeScanner({
    enabled: scanEnabled && !scanBusy && !cameraIsActive && !pickerOpen,
    idleMs: 300,
    resetMs: 2000,
    onScan: (value) => {
      void handleBarcodeSubmit(value, 'hid')
    },
  })

  const handleCategoryPathClick = useCallback(
    (product: StoreOrderProductItem) => {
      if (!product.warehouseCategoryGUID) {
        return
      }

      // 点击搜索结果里的分类路径时进入对应分类页，同时清除当前搜索关键词。
      navigate(`/shop?category=${encodeURIComponent(product.warehouseCategoryGUID)}`)
    },
    [navigate],
  )

  const handleOpenActivity = useCallback((product: StoreOrderProductItem) => {
    setActivityModalProduct(product)
    setActivityModalOpen(true)
  }, [])

  const handleCloseActivity = useCallback(() => {
    setActivityModalOpen(false)
    setActivityModalProduct(null)
  }, [])

  const handleCartOnlyFilterToggle = useCallback(async () => {
    if (cartOnlyFilter) {
      setCartOnlyFilter(false)
      setCurrentPage(1)
      return
    }

    if (!selectedStore?.storeCode) {
      message.warning(t('shop.selectStoreFirst'))
      return
    }

    try {
      await ensureFullCart()
      setCartOnlyFilter(true)
      setCurrentPage(1)
    } catch (error) {
      message.error(t('shop.loadCartFailed', 'Failed to load cart'))
    }
  }, [cartOnlyFilter, ensureFullCart, selectedStore?.storeCode, t])

  const handleAddToCart = useCallback(
    async (product: StoreOrderProductItem, quantity: number) => {
      if (!selectedStore?.storeCode) {
        message.warning(t('shop.selectStoreFirst'))
        return
      }
      const addQuantity = Math.max(1, Math.floor(Number.isFinite(quantity) ? quantity : 0))
      setOptimisticCartQuantityMap((prev) => ({ ...prev, [product.productCode]: addQuantity }))
      setQuantityLoadingMap((prev) => ({ ...prev, [product.productCode]: true }))
      try {
        const nextCart = await addStoreOrderCartItem({
          storeCode: selectedStore.storeCode,
          productCode: product.productCode,
          quantity: addQuantity,
        })
        setCart(nextCart)
        await refreshDynamicDataForProducts([product.productCode]).catch(() => {})
        setOptimisticCartQuantityMap((prev) => {
          const next = { ...prev }
          delete next[product.productCode]
          return next
        })
      } catch {
        setOptimisticCartQuantityMap((prev) => {
          const next = { ...prev }
          delete next[product.productCode]
          return next
        })
        message.error(t('shop.addToCartFailed'))
      } finally {
        setQuantityLoadingMap((prev) => {
          const next = { ...prev }
          delete next[product.productCode]
          return next
        })
      }
    },
    [refreshDynamicDataForProducts, selectedStore?.storeCode, setCart, t],
  )

  const handleProductQuantityChange = useCallback(
    (product: StoreOrderProductItem, quantity: number) => {
      if (!selectedStore?.storeCode) {
        message.warning(t('shop.selectStoreFirst'))
        return
      }
      const productCode = product.productCode
      const normalizedQuantity = Math.max(0, Math.floor(Number.isFinite(quantity) ? quantity : 0))
      const currentCartQuantity = cart?.isSummaryOnly
        ? (dynamicDataMapRef.current[productCode]?.cartQuantity ?? 0)
        : (cartQuantityByProductCode[productCode] ?? dynamicDataMapRef.current[productCode]?.cartQuantity ?? 0)
      const hasPendingQuantityWork =
        optimisticCartQuantityMap[productCode] !== undefined ||
        quantityUpdateTimersRef.current[productCode] !== undefined ||
        quantityLoadingMap[productCode]

      if (normalizedQuantity === currentCartQuantity && !hasPendingQuantityWork) {
        return
      }

      if (normalizedQuantity <= 0 && currentCartQuantity <= 0 && !hasPendingQuantityWork) {
        return
      }

      if (quantityUpdateTimersRef.current[productCode]) {
        window.clearTimeout(quantityUpdateTimersRef.current[productCode])
      }

      const updateVersion = (quantityUpdateVersionRef.current[productCode] ?? 0) + 1
      quantityUpdateVersionRef.current[productCode] = updateVersion
      setOptimisticCartQuantityMap((prev) => ({ ...prev, [productCode]: normalizedQuantity }))
      setQuantityLoadingMap((prev) => ({ ...prev, [productCode]: true }))

      quantityUpdateTimersRef.current[productCode] = window.setTimeout(() => {
        delete quantityUpdateTimersRef.current[productCode]
        void (async () => {
          if (quantityUpdateVersionRef.current[productCode] !== updateVersion) return

          try {
            // 商品卡数量就是购物车数量，连续点击时只提交最终数量，避免旧请求覆盖新数量。
            const nextCart = await updateStoreOrderCartItem({
              storeCode: selectedStore.storeCode,
              productCode,
              quantity: normalizedQuantity,
            })

            if (quantityUpdateVersionRef.current[productCode] !== updateVersion) return

            setCart(nextCart)
            await refreshDynamicDataForProducts([productCode]).catch(() => {})

            if (quantityUpdateVersionRef.current[productCode] !== updateVersion) return

            // 后端确认保存后再提示；同一商品复用 message key，连续点击只保留最终数量提示。
            message.success({
              content: t('shop.cartQuantityUpdated', { quantity: normalizedQuantity }),
              key: `shop-product-quantity-${productCode}`,
            })

            setOptimisticCartQuantityMap((prev) => {
              const next = { ...prev }
              delete next[productCode]
              return next
            })
          } catch (error) {
            if (quantityUpdateVersionRef.current[productCode] === updateVersion) {
              setOptimisticCartQuantityMap((prev) => {
                const next = { ...prev }
                delete next[productCode]
                return next
              })
              message.error(t('shop.cartUpdateFailed', 'Failed to update quantity'))
            }
          } finally {
            if (quantityUpdateVersionRef.current[productCode] === updateVersion) {
              setQuantityLoadingMap((prev) => {
                const next = { ...prev }
                delete next[productCode]
                return next
              })
            }
          }
        })()
      }, SHOP_PRODUCT_QUANTITY_UPDATE_DEBOUNCE_MS)
    },
    [
      cartQuantityByProductCode,
      cart?.isSummaryOnly,
      optimisticCartQuantityMap,
      quantityLoadingMap,
      refreshDynamicDataForProducts,
      selectedStore?.storeCode,
      setCart,
      t,
    ],
  )

  const handleRemoveFromCart = useCallback(async (product: StoreOrderProductItem) => {
    if (!selectedStore?.storeCode) {
      message.warning(t('shop.selectStoreFirst'))
      return
    }

    const productCode = product.productCode
    if (removingCartProductMap[productCode]) {
      return
    }

    // 删除按钮点击后先让卡片退出已入车状态，避免重复点击触发第二次删除。
    setRemovingCartProductMap((prev) => ({ ...prev, [productCode]: true }))
    setOptimisticCartQuantityMap((prev) => ({ ...prev, [productCode]: 0 }))

    try {
      const fullCart = await ensureFullCart()
      const cartItem = fullCart?.items.find((item) => item.productCode === productCode)

      if (!cartItem) {
        setOptimisticCartQuantityMap((prev) => {
          const next = { ...prev }
          delete next[productCode]
          return next
        })
        message.warning(t('shop.itemNotFoundInCart'))
        return
      }

      await removeStoreOrderCartItem({
        storeCode: selectedStore.storeCode,
        detailGUID: cartItem.detailGUID,
      })
      message.success(t('shop.removedFromCart', { name: product.productName }))
      const [cartRefreshResult] = await Promise.allSettled([
        refreshCart(),
        refreshDynamicDataForProducts([productCode]),
      ])
      if (cartRefreshResult.status === 'fulfilled' && cartRefreshResult.value) {
        setOptimisticCartQuantityMap((prev) => {
          const next = { ...prev }
          delete next[productCode]
          return next
        })
      }
    } catch (error) {
      setOptimisticCartQuantityMap((prev) => {
        const next = { ...prev }
        delete next[productCode]
        return next
      })
      message.error(t('shop.cartRemoveFailed'))
    } finally {
      setRemovingCartProductMap((prev) => {
        const next = { ...prev }
        delete next[productCode]
        return next
      })
    }
  }, [
    ensureFullCart,
    refreshCart,
    refreshDynamicDataForProducts,
    removingCartProductMap,
    selectedStore?.storeCode,
    t,
  ])

  return (
    <div className="shop-home-page">
      <ShopScanBar
        status={scanStatus}
        lastScannedCode={lastScannedCode}
        lastProductName={lastScannedProduct}
        lastProductImage={lastProductImage}
        lastItemNumber={lastItemNumber}
        lastQuantity={lastAddedQuantity}
        lastCartTotalQuantity={lastCartTotalQuantity}
        lastMessage={scanMessage}
        enabled={scanEnabled}
        soundEnabled={soundEnabled}
        busy={scanBusy}
        cameraEnabled={cameraIsActive}
        cameraPaused={pickerOpen || pickerLoading}
        cameraSessionKey={cameraSessionKey}
        onToggleEnabled={() => setScanEnabled((current) => !current)}
        onToggleCamera={handleToggleCamera}
        onUnlockSound={() => {
          void handleUnlockSound()
        }}
        onManualSubmit={(barcode) => {
          return handleBarcodeSubmit(barcode, 'manual')
        }}
        onCameraSubmit={handleCameraBarcodeSubmit}
      />

      <div className="shop-home-header">
        {categoryId && parentChain.length ? (
          <Breadcrumb
            items={parentChain.map((item) => ({
              title: <Link to={`/shop?category=${item.categoryGUID}`}>{item.categoryName}</Link>,
            }))}
          />
        ) : null}

        <h2>{pageTitle}</h2>

        <div className="shop-home-controls">
          <div className="shop-home-pagination-info">
            {t('shop.paginationInfo', { total: displayTotal, page: currentPage })}
          </div>
          <div className="shop-home-filters">
            <Space size={4} wrap>
              <span className="shop-home-filter-label">{t('shop.grade')}:</span>
              <Tag.CheckableTag
                checked={gradeFilter.length === 0}
                onChange={() => {
                  setGradeFilter([])
                  setCurrentPage(1)
                }}
                style={{ padding: '2px 8px', borderRadius: 4 }}
              >
                {t('common.all')}
              </Tag.CheckableTag>
              {(Object.entries(PRODUCT_GRADE_CONFIG) as [string, typeof PRODUCT_GRADE_CONFIG[keyof typeof PRODUCT_GRADE_CONFIG]][]).map(([key, cfg]) => (
                <Tooltip key={key} title={t(`shop.gradeTooltip.${key}`)}>
                  <Tag.CheckableTag
                    checked={gradeFilter.includes(key)}
                    onChange={(checked) => {
                      setGradeFilter((prev) =>
                        checked
                          ? [...prev, key]
                          : prev.filter((g) => g !== key),
                      )
                      setCurrentPage(1)
                    }}
                    style={{
                      padding: '2px 8px',
                      borderRadius: 4,
                      border: `1px solid ${cfg.color}`,
                      color: gradeFilter.includes(key) ? '#fff' : cfg.color,
                      background: gradeFilter.includes(key) ? cfg.color : 'transparent',
                    }}
                  >
                    {key} - {t(`shop.gradeLabel.${key}`)}
                  </Tag.CheckableTag>
                </Tooltip>
              ))}
              <Button
                size="small"
                type={cartOnlyFilter ? 'primary' : 'default'}
                icon={<ShoppingCartOutlined />}
                onClick={() => {
                  void handleCartOnlyFilterToggle()
                }}
              >
                {t('shop.cartProductsFilter', { count: cartProductCount })}
              </Button>
            </Space>
          </div>
          <div className="shop-home-filters">
            <span className="shop-home-filter-label">{t('shop.itemsPerPage')}:</span>
            <Select
              value={pageSize}
              className="shop-home-filter-select"
              onChange={(value) => {
                setCurrentPage(1)
                setPageSize(value)
              }}
              options={SHOP_HOME_PAGE_SIZE_OPTIONS.map((value) => ({ value, label: String(value) }))}
            />
          </div>
        </div>
      </div>

      {loading && !cartOnlyFilter ? (
        <div className="shop-home-loading">
          <Spin size="large" />
        </div>
      ) : displayProducts.length ? (
        <>
          <div className="shop-home-grid">
            {displayProducts.map((product) => {
              const dynamicData = dynamicDataMap[product.productCode]
              const syncedDynamicData: StoreOrderDynamicData = mergeShopHomeCartDynamicData({
                dynamicData,
                productCode: product.productCode,
                // summary-only 没有明细；full cart 时始终以购物车真实数量覆盖动态接口值。
                cartQuantity: cart?.isSummaryOnly
                  ? undefined
                  : cartQuantityByProductCode[product.productCode],
              })
              const isRemovingFromCart = Boolean(removingCartProductMap[product.productCode])
              const optimisticCartQuantity = isRemovingFromCart
                ? 0
                : optimisticCartQuantityMap[product.productCode]
              const cardDynamicData =
                optimisticCartQuantity === undefined
                  ? syncedDynamicData
                  : {
                    ...syncedDynamicData,
                    cartQuantity: optimisticCartQuantity,
                  }

              return (
                <ProductCard
                  key={product.productCode}
                  product={product}
                  dynamicData={cardDynamicData}
                  categoryPath={categoryPathMap[product.productCode]}
                  onCategoryPathClick={
                    shouldShowCategoryPath && product.warehouseCategoryGUID
                      ? handleCategoryPathClick
                      : undefined
                  }
                  onAddToCart={handleAddToCart}
                  onQuantityChange={handleProductQuantityChange}
                  onRemoveFromCart={handleRemoveFromCart}
                  onActivityClick={handleOpenActivity}
                  loading={Boolean(quantityLoadingMap[product.productCode] || isRemovingFromCart)}
                  removing={isRemovingFromCart}
                />
              )
            })}
          </div>

          <div className="shop-home-pagination">
            <Pagination
              current={currentPage}
              total={displayTotal}
              pageSize={pageSize}
              onChange={(page, size) => {
                setCurrentPage(page)
                if (size && size !== pageSize) {
                  setPageSize(size)
                }
              }}
              pageSizeOptions={SHOP_HOME_PAGE_SIZE_OPTIONS.map(String)}
              showSizeChanger
            />
          </div>
        </>
      ) : (
        supplyLookup.items.length ? (
          <div className="shop-supply-status-list" data-testid="shop-supply-status-list">
            <Typography.Text strong style={{ display: 'block', marginBottom: 4 }}>{t('supplyStatusCard.pausedHeading')}</Typography.Text>
            <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 12 }}>{t('supplyStatusCard.pausedHint')}</Typography.Text>
            <Space direction="vertical" size={8} style={{ width: '100%', maxWidth: 560 }}>
              {supplyLookup.items.map((status) => (
                <SupplyStatusCard
                  key={status.productCode}
                  status={status}
                  busy={supplyLookup.busyCode === status.productCode}
                  onWatch={(item) => void supplyLookup.toggleWatch(item, true)}
                  onUnwatch={(item) => void supplyLookup.toggleWatch(item, false)}
                />
              ))}
            </Space>
          </div>
        ) : (
          <Empty description={cartOnlyFilter ? t('shop.noCartProductsFound') : t('shop.noProductsFound')} />
        )
      )}

      <ShopScanResultPicker
        open={pickerOpen}
        loading={pickerLoading}
        barcode={lastScannedCode}
        items={scanCandidates}
        dynamicDataMap={pickerDynamicDataMap}
        onCancel={() => {
          if (pickerLoadingRef.current) {
            return
          }
          pickerOpenRef.current = false
          setPickerOpen(false)
          setPickerLoading(false)
        }}
        onSelect={(product) => {
          if (pickerLoadingRef.current) {
            return
          }
          pickerLoadingRef.current = true
          setPickerLoading(true)
          const storeCode = selectedStoreCodeRef.current
          void addScannedProductToCart(product, lastScannedCode, storeCode).finally(() => {
            pickerLoadingRef.current = false
            pickerOpenRef.current = false
            setPickerLoading(false)
            setPickerOpen(false)
          })
        }}
      />

      <ProductActivityHistoryModal
        open={activityModalOpen}
        product={activityModalProduct}
        storeCode={selectedStore?.storeCode ?? null}
        storeName={selectedStore?.storeName ?? null}
        onClose={handleCloseActivity}
      />
    </div>
  )
}
