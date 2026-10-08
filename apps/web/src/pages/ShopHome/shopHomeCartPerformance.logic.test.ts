import { readFileSync } from 'node:fs'
import path from 'node:path'
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

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

async function runTest(name: string, execute: () => void | Promise<void>): Promise<string | null> {
  try {
    await execute()
    console.log(`ok - ${name}`)
    return null
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error)
    console.error(`not ok - ${name}`)
    console.error(reason)
    return `${name}: ${reason}`
  }
}

function extractFunctionBody(source: string, marker: string, endMarker: string) {
  const start = source.indexOf(marker)
  const end = source.indexOf(endMarker, start)
  assert(start >= 0 && end > start, `找不到 ${marker} 对应的函数代码块`)
  return source.slice(start, end)
}

function createDeferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })

  return { promise, resolve, reject }
}

const shopHomeFile = path.resolve(process.cwd(), 'src/pages/ShopHome/index.tsx')
const shopHomeSource = readFileSync(shopHomeFile, 'utf8')
const productCardFile = path.resolve(process.cwd(), 'src/pages/ShopHome/components/ProductCard.tsx')
const productCardSource = readFileSync(productCardFile, 'utf8')
const bestSellersFile = path.resolve(process.cwd(), 'src/pages/ShopHome/components/BestSellersSection.tsx')
const bestSellersSource = readFileSync(bestSellersFile, 'utf8')
const globalCssFile = path.resolve(process.cwd(), 'src/styles/global.css')
const globalCssSource = readFileSync(globalCssFile, 'utf8')
const zhLocaleSource = readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/zh.json'), 'utf8')
const enLocaleSource = readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/en.json'), 'utf8')

async function main() {
  const failures: string[] = []

  const localBaseRefreshPreservesSalesFailure = await runTest(
    '单商品基础动态刷新应保留已经加载的 Sales',
    () => {
      const previousMap = {
        P1: {
          productCode: 'P1',
          cartQuantity: 1,
          salesQuantitySinceLastArrival: -2,
          lastArrivalDate: '2026-09-30T00:00:00',
          lastArrivalQuantity: 24,
          lastArrivalOrderQuantity: 0,
          lastArrivalOrderDate: '2026-09-10T09:00:00',
        },
        P2: {
          productCode: 'P2',
          cartQuantity: 2,
          salesQuantitySinceLastArrival: null,
        },
      }
      const nextMap = mergeShopHomeBaseDynamicDataMap(previousMap, {
        P1: { productCode: 'P1', cartQuantity: 3 },
        P2: { productCode: 'P2', cartQuantity: 4 },
      })

      assert(nextMap.P1.cartQuantity === 3, '基础动态字段必须使用最新值')
      assert(nextMap.P1.salesQuantitySinceLastArrival === -2, '负 Sales 必须在局部刷新后保留')
      assert(nextMap.P1.lastArrivalDate === '2026-09-30T00:00:00', '来货日期必须与 Sales 一起在局部刷新后保留')
      assert(nextMap.P1.lastArrivalQuantity === 24, '来货数量必须与 Sales 一起在局部刷新后保留')
      assert(nextMap.P1.lastArrivalOrderQuantity === 0, '来货对应订货数量（含 0）必须在局部刷新后保留')
      assert(nextMap.P1.lastArrivalOrderDate === '2026-09-10T09:00:00', '来货订单的订货日期必须在局部刷新后保留')
      assert(nextMap.P2.salesQuantitySinceLastArrival === null, '不可用 Sales 的 null 语义必须保留')
      assert(
        shopHomeSource.includes('mergeShopHomeBaseDynamicDataMap(prev, nextMap)'),
        '主商品动态 map 的局部刷新必须复用保留 Sales 的字段级合并',
      )
    },
  )
  if (localBaseRefreshPreservesSalesFailure) failures.push(localBaseRefreshPreservesSalesFailure)

  const productQuantityUpdateFailure = await runTest('商品卡数量按钮应直接设置购物车数量并复用返回购物车', () => {
    const body = extractFunctionBody(
      shopHomeSource,
      'const handleProductQuantityChange = useCallback',
      'const handleRemoveFromCart = useCallback',
    )

    assert(
      shopHomeSource.includes('updateStoreOrderCartItem,') &&
        shopHomeSource.includes('const SHOP_PRODUCT_QUANTITY_UPDATE_DEBOUNCE_MS = 300') &&
        shopHomeSource.includes('const quantityUpdateTimersRef = useRef<Record<string, number>>({})') &&
        shopHomeSource.includes('const quantityUpdateVersionRef = useRef<Record<string, number>>({})') &&
        shopHomeSource.includes('Object.values(quantityUpdateTimersRef.current).forEach((timer) => window.clearTimeout(timer))') &&
        body.includes('const nextCart = await updateStoreOrderCartItem({') &&
        body.includes('quantity: normalizedQuantity') &&
        body.includes('setCart(nextCart)') &&
        body.includes('refreshDynamicDataForProducts([productCode])') &&
        body.includes('setOptimisticCartQuantityMap((prev) => ({ ...prev, [productCode]: normalizedQuantity }))') &&
        body.includes('delete next[productCode]') &&
        body.includes('quantityUpdateTimersRef.current[productCode] = window.setTimeout') &&
        body.includes('window.clearTimeout(quantityUpdateTimersRef.current[productCode])') &&
        body.includes('quantityUpdateVersionRef.current[productCode] = updateVersion') &&
        body.includes('quantityUpdateVersionRef.current[productCode] !== updateVersion') &&
        body.includes('setQuantityLoadingMap') &&
        body.includes('normalizedQuantity <= 0 && currentCartQuantity <= 0') &&
        !body.includes('refreshCart()'),
      '商品卡数量更新未直接调用 cart/update、未复用返回购物车，或缺少乐观更新/回滚/单商品刷新',
    )
  })
  if (productQuantityUpdateFailure) failures.push(productQuantityUpdateFailure)

  const productQuantityUpdateSuccessMessageFailure = await runTest('商品卡数量保存成功后应只提示最新数量', () => {
    const body = extractFunctionBody(
      shopHomeSource,
      'const handleProductQuantityChange = useCallback',
      'const handleRemoveFromCart = useCallback',
    )
    const refreshIndex = body.indexOf('await refreshDynamicDataForProducts([productCode])')
    const latestVersionCheckIndex = body.indexOf('if (quantityUpdateVersionRef.current[productCode] !== updateVersion) return', refreshIndex)
    const successMessageIndex = body.indexOf("message.success({", latestVersionCheckIndex)

    assert(
      refreshIndex >= 0 &&
        latestVersionCheckIndex > refreshIndex &&
        successMessageIndex > latestVersionCheckIndex &&
        body.includes("content: t('shop.cartQuantityUpdated', { quantity: normalizedQuantity })") &&
        body.includes('key: `shop-product-quantity-${productCode}`') &&
        zhLocaleSource.includes('"cartQuantityUpdated": "数量已保存：{{quantity}}"') &&
        enLocaleSource.includes('"cartQuantityUpdated": "Quantity saved: {{quantity}}"'),
      '商品卡数量保存成功后缺少最新版本校验后的成功提示、message key，或 zh/en 文案不同步',
    )
  })
  if (productQuantityUpdateSuccessMessageFailure) failures.push(productQuantityUpdateSuccessMessageFailure)

  const productCardAddFailure = await runTest('商品卡未入车商品应保留 Add 首次加购并乐观更新', () => {
    const body = extractFunctionBody(
      shopHomeSource,
      'const handleAddToCart = useCallback',
      'const handleProductQuantityChange = useCallback',
    )

    assert(
      body.includes('const addQuantity = Math.max(1, Math.floor(Number.isFinite(quantity) ? quantity : 0))') &&
        body.includes('setOptimisticCartQuantityMap((prev) => ({ ...prev, [product.productCode]: addQuantity }))') &&
        body.includes('const nextCart = await addStoreOrderCartItem({') &&
        body.includes('quantity: addQuantity') &&
        body.includes('setCart(nextCart)') &&
        body.includes('refreshDynamicDataForProducts([product.productCode])') &&
        body.includes('delete next[product.productCode]') &&
        shopHomeSource.includes('onAddToCart={handleAddToCart}'),
      '商品卡 Add 没有走 cart/add，或缺少乐观数量、成功刷新、失败回滚',
    )
  })
  if (productCardAddFailure) failures.push(productCardAddFailure)

  const scanAddFailure = await runTest('扫码 Add 应复用 cart/add 返回的购物车并避免二次拉整车', () => {
    const body = extractFunctionBody(
      shopHomeSource,
      'const addScannedProductToCart = useCallback',
      'const handleBarcodeSubmit = useCallback',
    )

    assert(
      body.includes('const nextCart = await addStoreOrderCartItem({') &&
        body.includes('setCart(nextCart)') &&
        !body.includes('getActiveStoreOrderCart('),
      '扫码 Add 仍然在 addStoreOrderCartItem 后额外调用 getActiveStoreOrderCart',
    )
  })
  if (scanAddFailure) failures.push(scanAddFailure)

  const dynamicDataFailure = await runTest('商品卡 Add、数量更新和扫码加购后应只刷新当前商品动态数据', () => {
    const productAddBody = extractFunctionBody(
      shopHomeSource,
      'const handleAddToCart = useCallback',
      'const handleProductQuantityChange = useCallback',
    )
    const productQuantityBody = extractFunctionBody(
      shopHomeSource,
      'const handleProductQuantityChange = useCallback',
      'const handleRemoveFromCart = useCallback',
    )
    const scanAddBody = extractFunctionBody(
      shopHomeSource,
      'const addScannedProductToCart = useCallback',
      'const handleBarcodeSubmit = useCallback',
    )

    assert(
      shopHomeSource.includes('const refreshDynamicDataForProducts = useCallback') &&
        productAddBody.includes('refreshDynamicDataForProducts([product.productCode])') &&
        productQuantityBody.includes('refreshDynamicDataForProducts([productCode])') &&
        scanAddBody.includes('refreshDynamicDataForProducts([product.productCode])') &&
        !productAddBody.includes('refreshDynamicData()') &&
        !productQuantityBody.includes('refreshDynamicData()') &&
        !scanAddBody.includes('refreshDynamicData()'),
      '加购后仍在刷新整页 dynamic-data 或购物车，而不是只刷新当前商品',
    )
  })
  if (dynamicDataFailure) failures.push(dynamicDataFailure)

  const defaultSortFailure = await runTest('商城首页默认商品查询应命中后端首页缓存排序', () => {
    const fetchProductsBody = extractFunctionBody(
      shopHomeSource,
      'const fetchProducts = async',
      'void fetchProducts()',
    )

    assert(
      fetchProductsBody.includes("sortBy: 'Default'") &&
        !fetchProductsBody.includes("sortBy: 'productName'"),
      '商城首页默认商品查询未使用后端首页缓存对应的 Default 排序',
    )
  })
  if (defaultSortFailure) failures.push(defaultSortFailure)

  const storeScopeFailure = await runTest('商城首页商品查询必须带当前分店并等待分店选中', () => {
    const fetchProductsBody = extractFunctionBody(
      shopHomeSource,
      'const fetchProducts = async',
      'void fetchProducts()',
    )

    assert(
      fetchProductsBody.includes('if (!selectedStore?.storeCode)') &&
        fetchProductsBody.includes('setProducts([])') &&
        fetchProductsBody.includes('setTotal(0)') &&
        fetchProductsBody.includes('storeCode: selectedStore.storeCode'),
      '商城首页商品查询未等待当前分店，或未把 selectedStore.storeCode 传给后端',
    )
  })
  if (storeScopeFailure) failures.push(storeScopeFailure)

  const storeScopeDependencyFailure = await runTest('商城首页切换分店后应重新加载商品列表', () => {
    assert(
      shopHomeSource.includes('selectedStore?.storeCode, gradeFilter, t') ||
        shopHomeSource.includes('gradeFilter, selectedStore?.storeCode, t'),
      '商品加载 effect 依赖缺少 selectedStore.storeCode，切换分店后不会重新按门店加载',
    )
  })
  if (storeScopeDependencyFailure) failures.push(storeScopeDependencyFailure)

  const pageSizeOptionsFailure = await runTest('商城首页默认每页 200 且支持 50/100/200/500', () => {
    assert(
      shopHomeSource.includes('const SHOP_HOME_PAGE_SIZE_OPTIONS = [50, 100, 200, 500]') &&
        shopHomeSource.includes('const [pageSize, setPageSize] = useState(200)') &&
        shopHomeSource.includes('options={SHOP_HOME_PAGE_SIZE_OPTIONS.map((value) => ({ value, label: String(value) }))}') &&
        shopHomeSource.includes('pageSizeOptions={SHOP_HOME_PAGE_SIZE_OPTIONS.map(String)}'),
      '商城首页默认每页数量或顶部/底部分页选项未统一为 50/100/200/500',
    )
  })
  if (pageSizeOptionsFailure) failures.push(pageSizeOptionsFailure)

  const cartOnlyFilterFailure = await runTest('商城首页购物车商品按钮应显示当前分店全部购物车商品', () => {
    assert(
      shopHomeSource.includes("import { ShoppingCartOutlined } from '@ant-design/icons'") &&
        shopHomeSource.includes('const [cartOnlyFilter, setCartOnlyFilter] = useState(false)') &&
        shopHomeSource.includes('const cart = useShopStore((state) => state.cart)') &&
        shopHomeSource.includes('const cartProductItems = useMemo<StoreOrderProductItem[]>(() => {') &&
        shopHomeSource.includes('return (cart?.items ?? []).map((item) => ({') &&
        shopHomeSource.includes('const cartProductPageItems = useMemo(() => {') &&
        shopHomeSource.includes('return cartProductItems.slice(startIndex, startIndex + pageSize)') &&
        shopHomeSource.includes('const displayProducts = cartOnlyFilter ? cartProductPageItems : products') &&
        shopHomeSource.includes('const displayTotal = cartOnlyFilter ? cartProductItems.length : total') &&
        shopHomeSource.includes('const cartProductCount = (cart?.items.length || cart?.totalSKU) ?? 0') &&
        shopHomeSource.includes('type={cartOnlyFilter ?') &&
        shopHomeSource.includes('icon={<ShoppingCartOutlined />}') &&
        shopHomeSource.includes('const handleCartOnlyFilterToggle = useCallback') &&
        shopHomeSource.includes('await ensureFullCart()') &&
        shopHomeSource.includes("t('shop.cartProductsFilter', { count: cartProductCount })") &&
        shopHomeSource.includes('cartOnlyFilter ? t(') &&
        shopHomeSource.includes("t('shop.noCartProductsFound')"),
      '商城首页购物车商品过滤没有读取 cart.items、没有本地分页，或没有独立按钮/空状态文案',
    )
  })
  if (cartOnlyFilterFailure) failures.push(cartOnlyFilterFailure)

  const cartOnlyDynamicDataFailure = await runTest('购物车商品过滤应保留完整动态数据并以真实购物车数量覆盖', async () => {
    const salesValues = [12, 0, -3, null, undefined] as const
    for (const salesQuantitySinceLastArrival of salesValues) {
      const merged = mergeShopHomeCartDynamicData({
        dynamicData: {
          productCode: 'P-CART',
          cartQuantity: 999,
          lastOrderDate: '2026-08-01',
          salesQuantitySinceLastArrival,
        },
        productCode: 'P-CART',
        cartQuantity: 7,
      })

      assert(merged.cartQuantity === 7, '购物车真实数量必须覆盖 dynamic-data 中的旧数量')
      assert(merged.lastOrderDate === '2026-08-01', '购物车模式必须保留最近来货等完整动态数据')
      assert(
        Object.is(merged.salesQuantitySinceLastArrival, salesQuantitySinceLastArrival),
        `Sales 值 ${String(salesQuantitySinceLastArrival)} 在合并后发生变化`,
      )
      assert(
        (typeof merged.salesQuantitySinceLastArrival === 'number') ===
          (salesQuantitySinceLastArrival !== null && salesQuantitySinceLastArrival !== undefined),
        'Sales 正数、0、负数应显示，null/undefined 应隐藏',
      )
    }

    const stableDynamicData = {
      productCode: 'P-STABLE',
      cartQuantity: 7,
      lastOrderDate: '2026-08-01',
      salesQuantitySinceLastArrival: 0,
    }
    assert(
      Object.is(
        mergeShopHomeCartDynamicData({
          dynamicData: stableDynamicData,
          productCode: 'P-STABLE',
          cartQuantity: 7,
        }),
        stableDynamicData,
      ),
      '动态数据内容未变时必须返回原引用，避免 Sales 分批触发未变化卡片重渲染',
    )
    assert(productCardSource.includes('export default memo(ProductCard)'), 'ProductCard 必须使用 React.memo')
    const quantityHandler = extractFunctionBody(
      shopHomeSource,
      'const handleProductQuantityChange = useCallback',
      'const handleRemoveFromCart = useCallback',
    )
    assert(
      shopHomeSource.includes('const dynamicDataMapRef = useRef<Record<string, StoreOrderDynamicData>>({})') &&
        quantityHandler.includes('dynamicDataMapRef.current[productCode]?.cartQuantity') &&
        !quantityHandler.includes('\n      dynamicDataMap,\n'),
      '数量回调不得依赖整个 dynamicDataMap，否则每批 Sales 都会让所有 ProductCard 重新渲染',
    )

    const hiddenIdentity = buildShopHomeDynamicDataRequestIdentity({
      active: false,
      storeCode: 'S1',
      productCodes: ['P1', 'P2'],
    })
    const hiddenCoordinator = createShopHomeDynamicDataRequestCoordinator()
    hiddenCoordinator.activate(hiddenIdentity)
    let requestCount = 0
    await runShopHomeDynamicDataRequest({
      coordinator: hiddenCoordinator,
      token: hiddenCoordinator.begin(hiddenIdentity),
      productCodes: ['P1', 'P2'],
      request: async () => {
        requestCount += 1
        return {}
      },
      onSuccess: () => undefined,
      onError: () => undefined,
    })
    assert(requestCount === 0, '无有效展示上下文时不得发起动态数据请求')

    const pageOneIdentity = buildShopHomeDynamicDataRequestIdentity({
      active: true,
      storeCode: 'S1',
      productCodes: ['P1', 'P2'],
    })
    const pageTwoIdentity = buildShopHomeDynamicDataRequestIdentity({
      active: true,
      storeCode: 'S1',
      productCodes: ['P3', 'P4'],
    })
    const storeTwoIdentity = buildShopHomeDynamicDataRequestIdentity({
      active: true,
      storeCode: 'S2',
      productCodes: ['P3', 'P4'],
    })
    assert(pageOneIdentity !== pageTwoIdentity, '分页后请求身份必须变化')
    assert(pageTwoIdentity !== storeTwoIdentity, '切店后请求身份必须变化')

    const renderSafeCoordinator = createShopHomeDynamicDataRequestCoordinator()
    renderSafeCoordinator.activate(pageOneIdentity)
    const committedPageOneToken = renderSafeCoordinator.begin(pageOneIdentity)
    assert(committedPageOneToken, '已提交的页 1 应能开始请求')

    // render 只计算候选 identity；未提交的页 2 不得触碰 coordinator。
    const uncommittedPageTwoIdentity = pageTwoIdentity
    assert(uncommittedPageTwoIdentity !== pageOneIdentity, '未提交候选身份应与当前身份不同')
    assert(
      renderSafeCoordinator.isCurrent(committedPageOneToken),
      '未提交的候选身份不应改变已提交页面的当前 token',
    )

    renderSafeCoordinator.deactivate(pageOneIdentity)
    renderSafeCoordinator.activate(pageTwoIdentity)
    // B 已提交 layout，但 passive effect 还未 begin；紧接着回 A 必须能开始新代次。
    renderSafeCoordinator.deactivate(pageTwoIdentity)
    renderSafeCoordinator.activate(pageOneIdentity)
    const returnedPageOneToken = renderSafeCoordinator.begin(pageOneIdentity)
    assert(returnedPageOneToken, 'B 未 begin 就回 A 时，A 应能开始新请求')
    assert(
      returnedPageOneToken.version > committedPageOneToken.version,
      '回到 A 的请求必须使用更新的单调 version',
    )
    assert(
      !renderSafeCoordinator.isCurrent(committedPageOneToken) &&
        renderSafeCoordinator.isCurrent(returnedPageOneToken),
      'B 未 begin 就回 A 时，旧 A token 应失效且新 A token 应生效',
    )

    const requestedBatches: string[][] = []

    const runAbaScenario = async (
      label: string,
      firstIdentity: string | null,
      middleIdentity: string | null,
      firstProductCodes: string[],
      middleProductCodes: string[],
      firstOutcome: 'success' | 'error',
    ) => {
      const coordinator = createShopHomeDynamicDataRequestCoordinator()
      const committed: string[] = []
      const errors: string[] = []
      const firstDeferred = createDeferred<Record<string, { productCode: string; cartQuantity: number }>>()
      const middleDeferred = createDeferred<Record<string, { productCode: string; cartQuantity: number }>>()
      const currentDeferred = createDeferred<Record<string, { productCode: string; cartQuantity: number }>>()

      coordinator.activate(firstIdentity)
      const firstRequest = runShopHomeDynamicDataRequest({
        coordinator,
        token: coordinator.begin(firstIdentity),
        productCodes: firstProductCodes,
        request: (productCodes) => {
          requestedBatches.push(productCodes)
          return firstDeferred.promise
        },
        onSuccess: () => committed.push(`${label}-first`),
        onError: () => errors.push(`${label}-first`),
      })

      coordinator.activate(middleIdentity)
      const middleRequest = runShopHomeDynamicDataRequest({
        coordinator,
        token: coordinator.begin(middleIdentity),
        productCodes: middleProductCodes,
        request: (productCodes) => {
          requestedBatches.push(productCodes)
          return middleDeferred.promise
        },
        onSuccess: () => committed.push(`${label}-middle`),
        onError: () => errors.push(`${label}-middle`),
      })

      coordinator.activate(firstIdentity)
      const currentRequest = runShopHomeDynamicDataRequest({
        coordinator,
        token: coordinator.begin(firstIdentity),
        productCodes: firstProductCodes,
        request: (productCodes) => {
          requestedBatches.push(productCodes)
          return currentDeferred.promise
        },
        onSuccess: () => committed.push(`${label}-current`),
        onError: () => errors.push(`${label}-current`),
      })

      currentDeferred.resolve({ CURRENT: { productCode: 'CURRENT', cartQuantity: 3 } })
      await currentRequest
      if (firstOutcome === 'success') {
        firstDeferred.resolve({ STALE: { productCode: 'STALE', cartQuantity: 1 } })
        middleDeferred.reject(new Error(`${label} stale middle error`))
      } else {
        firstDeferred.reject(new Error(`${label} stale first error`))
        middleDeferred.resolve({ STALE: { productCode: 'STALE', cartQuantity: 2 } })
      }
      await Promise.all([firstRequest, middleRequest])

      assert(
        JSON.stringify(committed) === JSON.stringify([`${label}-current`]),
        `${label} ABA 后旧成功响应不得提交`,
      )
      assert(errors.length === 0, `${label} ABA 后旧错误不得污染新状态`)
    }

    // 删除购物车末页商品会让 displayProducts 从 P1/P2 收缩并翻到 P3/P4；旧页响应不得覆盖新页。
    await runAbaScenario(
      'cart-delete-page',
      pageOneIdentity,
      pageTwoIdentity,
      ['P1', 'P2'],
      ['P3', 'P4'],
      'success',
    )
    await runAbaScenario(
      'store',
      pageTwoIdentity,
      storeTwoIdentity,
      ['P3', 'P4'],
      ['P3', 'P4'],
      'error',
    )
    assert(
      requestedBatches.every((batch) => batch.length === 2) && requestedBatches.length === 6,
      '每页动态数据必须一次批量请求，不得按商品产生 N+1',
    )

    assert(
      shopHomeSource.includes('const cartQuantityByProductCode = useMemo<Record<string, number>>(() => {') &&
        shopHomeSource.includes('acc[item.productCode] = item.quantity') &&
        shopHomeSource.includes('const displayProductCodes = useMemo(') &&
        shopHomeSource.includes('buildShopHomeDynamicDataRequestIdentity({') &&
        shopHomeSource.includes('createShopHomeDynamicDataRequestCoordinator()') &&
        shopHomeSource.includes('runShopHomeDynamicDataRequest({') &&
        !shopHomeSource.includes('const cartProductDynamicDataMap = useMemo') &&
        shopHomeSource.includes('const currentCartQuantity = cart?.isSummaryOnly') &&
        shopHomeSource.includes('mergeShopHomeCartDynamicData({') &&
        productCardSource.includes("const hasSalesQuantity = typeof salesQuantity === 'number'") &&
        shopHomeSource.includes('dynamicData={cardDynamicData}'),
      'ShopHome 未批量加载当前购物车页动态数据、未按真实购物车数量合并，或 ProductCard 的 Sales 显示契约回归',
    )
  })
  if (cartOnlyDynamicDataFailure) failures.push(cartOnlyDynamicDataFailure)

  const dynamicDataCommitPhaseFailure = await runTest('动态数据身份只应在 layout 提交阶段激活', () => {
    const identityStart = shopHomeSource.indexOf('const dynamicDataRequestIdentity =')
    const pageTitleStart = shopHomeSource.indexOf('const pageTitle = useMemo', identityStart)
    const layoutEffectStart = shopHomeSource.indexOf('useLayoutEffect(() => {', identityStart)
    const requestEffectStart = shopHomeSource.indexOf(
      'useEffect(() => {\n    const identity = dynamicDataRequestIdentity',
      identityStart,
    )
    assert(identityStart >= 0 && pageTitleStart > identityStart, '找不到动态数据 identity render 区段')
    assert(
      !shopHomeSource.slice(identityStart, pageTitleStart).includes('.activate('),
      'render 阶段不得直接 activate coordinator',
    )
    assert(
      shopHomeSource.includes("import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'") &&
        layoutEffectStart > pageTitleStart &&
        requestEffectStart > layoutEffectStart,
      'coordinator activate 必须使用 useLayoutEffect，且先于 passive request effect',
    )
    const layoutEffectBody = shopHomeSource.slice(layoutEffectStart, requestEffectStart)
    assert(
      layoutEffectBody.includes('coordinator.activate(dynamicDataRequestIdentity)') &&
        layoutEffectBody.includes('coordinator.deactivate(dynamicDataRequestIdentity)'),
      'layout effect 必须在提交时激活 identity，并在 cleanup 定向失效该 identity',
    )
  })
  if (dynamicDataCommitPhaseFailure) failures.push(dynamicDataCommitPhaseFailure)

  const progressiveSalesSummaryFailure = await runTest(
    'Sales summary 应先提交基础动态数据、优先 50 个且拒绝旧响应与余量失败',
    async () => {
      const coordinator = createShopHomeSalesSummaryRequestCoordinator()
      const identity = buildShopHomeDynamicDataRequestIdentity({
        active: true,
        storeCode: 'S1',
        productCodes: ['P1', 'P2', 'P3'],
      })
      assert(
        JSON.stringify(readShopHomeDynamicDataRequestProductCodes(identity)) ===
          JSON.stringify(['P1', 'P2', 'P3']),
        'Sales 分批商品码必须从稳定 identity 还原，不能依赖购物车重算产生的新数组引用',
      )
      const priorityDeferred = createDeferred<Record<string, number | null>>()
      const remainderDeferred = createDeferred<Record<string, number | null>>()
      const requestedBatches: string[][] = []
      const committed: Record<string, number | null> = {}
      let remainderRequest: Promise<void> | null = null

      coordinator.activate(identity)
      const priorityRequest = runShopHomeSalesSummaryRequest({
        coordinator,
        token: coordinator.begin(identity),
        productCodes: ['P1', 'P2'],
        request: (productCodes) => {
          requestedBatches.push(productCodes)
          return priorityDeferred.promise
        },
        onSuccess: (result) => {
          Object.assign(committed, result)
          remainderRequest = runShopHomeSalesSummaryRequest({
            coordinator,
            token: coordinator.begin(identity),
            productCodes: ['P3'],
            request: (productCodes) => {
              requestedBatches.push(productCodes)
              return remainderDeferred.promise
            },
            onSuccess: (nextResult) => Object.assign(committed, nextResult),
            // 余量失败不得清掉已经可见的优先批 Sales。
            onError: () => undefined,
          })
        },
        onError: () => undefined,
      })

      assert(requestedBatches.length === 1, '基础动态数据提交前不应预取余量 Sales')
      priorityDeferred.resolve({ P1: 12, P2: 0 })
      await priorityRequest
      assert(
        JSON.stringify(requestedBatches) === JSON.stringify([['P1', 'P2'], ['P3']]),
        'Sales 必须先请求优先批，成功后才请求余量',
      )
      assert(committed.P1 === 12 && committed.P2 === 0, '优先批成功后必须先提交其 Sales')
      remainderDeferred.reject(new Error('remainder failed'))
      await remainderRequest
      assert(committed.P1 === 12 && committed.P2 === 0 && !('P3' in committed), '余量失败不得清空优先批')

      const abaCoordinator = createShopHomeSalesSummaryRequestCoordinator()
      const staleDeferred = createDeferred<Record<string, number | null>>()
      const currentDeferred = createDeferred<Record<string, number | null>>()
      const abaCommitted: string[] = []
      const abaErrors: string[] = []
      const filterIdentity = buildShopHomeDynamicDataRequestIdentity({
        active: true,
        storeCode: 'S1',
        productCodes: ['FILTERED'],
      })

      abaCoordinator.activate(identity)
      const staleRequest = runShopHomeSalesSummaryRequest({
        coordinator: abaCoordinator,
        token: abaCoordinator.begin(identity),
        productCodes: ['P1'],
        request: () => staleDeferred.promise,
        onSuccess: () => abaCommitted.push('stale'),
        onError: () => abaErrors.push('stale'),
      })
      // 切店、分页或筛选后又回到相同 identity 时，旧 token 也必须失效。
      abaCoordinator.activate(filterIdentity)
      abaCoordinator.activate(identity)
      const currentRequest = runShopHomeSalesSummaryRequest({
        coordinator: abaCoordinator,
        token: abaCoordinator.begin(identity),
        productCodes: ['P1'],
        request: () => currentDeferred.promise,
        onSuccess: () => abaCommitted.push('current'),
        onError: () => abaErrors.push('current'),
      })
      staleDeferred.reject(new Error('stale error'))
      currentDeferred.resolve({ P1: -3 })
      await Promise.all([staleRequest, currentRequest])
      assert(
        JSON.stringify(abaCommitted) === JSON.stringify(['current']) && abaErrors.length === 0,
        '筛选/切店 ABA 后旧 Sales 成功或错误不得污染当前页',
      )

      const dynamicDataEffect = extractFunctionBody(
        shopHomeSource,
        'useEffect(() => {\n    const identity = dynamicDataRequestIdentity',
        'useEffect(() => {\n    let cancelled = false',
      )
      const removeBody = extractFunctionBody(
        shopHomeSource,
        'const handleRemoveFromCart = useCallback',
        '  return (\n    <div className="shop-home-page">',
      )
      assert(
        shopHomeSource.includes('includeSales: false') &&
          dynamicDataEffect.includes('const requestProductCodes = readShopHomeDynamicDataRequestProductCodes(identity)') &&
          dynamicDataEffect.includes('const prioritySalesProductCodes = requestProductCodes.slice(0, 50)') &&
          dynamicDataEffect.includes('const remainderSalesProductCodes = requestProductCodes.slice(50)') &&
          !dynamicDataEffect.includes('\n    prioritySalesProductCodes,\n') &&
          !dynamicDataEffect.includes('\n    remainderSalesProductCodes,\n') &&
          shopHomeSource.includes("logShopHomePerf('dynamic-base.done'") &&
          shopHomeSource.includes("stage: 'sales-priority.done' | 'sales-remainder.done'") &&
          dynamicDataEffect.includes('const requestRemainderSales = () => {') &&
          dynamicDataEffect.includes('onError: requestRemainderSales') &&
          dynamicDataEffect.includes('runShopHomeSalesSummaryRequest({') &&
          !removeBody.includes('refreshDynamicData()') &&
          removeBody.includes('const [cartRefreshResult] = await Promise.allSettled([') &&
          removeBody.includes('refreshCart(),') &&
          removeBody.includes('refreshDynamicDataForProducts([productCode]),'),
        'ShopHome 未使用稳定 identity 分批、优先失败后继续余量、性能日志或删除局部刷新',
      )
    },
  )
  if (progressiveSalesSummaryFailure) failures.push(progressiveSalesSummaryFailure)

  const storeScopedRefreshFailure = await runTest('单商品动态数据刷新应拒绝切店 ABA 的旧响应，并保留并发商品各自结果', async () => {
    const coordinator = createShopHomeDynamicDataStoreScopeCoordinator()
    const staleResponse = createDeferred<Record<string, { productCode: string; cartQuantity: number }>>()
    const productAResponse = createDeferred<Record<string, { productCode: string; cartQuantity: number }>>()
    const productBResponse = createDeferred<Record<string, { productCode: string; cartQuantity: number }>>()
    const committed: Record<string, { productCode: string; cartQuantity: number }> = {}

    coordinator.activate('S1')
    const staleToken = coordinator.capture('S1')
    const staleRequest = runShopHomeStoreScopedDynamicDataRequest({
      coordinator,
      token: staleToken,
      productCodes: ['STALE'],
      request: () => staleResponse.promise,
      onSuccess: (result) => Object.assign(committed, result),
    })

    // S1 -> S2 -> S1 必须产生新代次，旧 S1 响应即使最后返回也不能合并 Sales。
    coordinator.activate('S2')
    coordinator.activate('S1')
    const currentToken = coordinator.capture('S1')
    const productARequest = runShopHomeStoreScopedDynamicDataRequest({
      coordinator,
      token: currentToken,
      productCodes: ['A'],
      request: () => productAResponse.promise,
      onSuccess: (result) => Object.assign(committed, result),
    })
    const productBRequest = runShopHomeStoreScopedDynamicDataRequest({
      coordinator,
      token: currentToken,
      productCodes: ['B'],
      request: () => productBResponse.promise,
      onSuccess: (result) => Object.assign(committed, result),
    })

    productBResponse.resolve({ B: { productCode: 'B', cartQuantity: 2 } })
    staleResponse.resolve({ STALE: { productCode: 'STALE', cartQuantity: 99 } })
    productAResponse.resolve({ A: { productCode: 'A', cartQuantity: 1 } })
    await Promise.all([staleRequest, productARequest, productBRequest])

    assert(!('STALE' in committed), '切店 ABA 后旧单商品响应不得合并')
    assert(committed.A?.cartQuantity === 1 && committed.B?.cartQuantity === 2, '不同商品的局部刷新不得互相覆盖')

    const refreshDynamicDataForProductsBody = extractFunctionBody(
      shopHomeSource,
      'const refreshDynamicDataForProducts = useCallback',
      'const updateScanFeedback = useCallback',
    )
    assert(
      refreshDynamicDataForProductsBody.includes('const coordinator = dynamicDataStoreScopeCoordinatorRef.current') &&
        refreshDynamicDataForProductsBody.includes('const token = coordinator.capture(storeCode)') &&
        refreshDynamicDataForProductsBody.includes('runShopHomeStoreScopedDynamicDataRequest({'),
      '单商品刷新没有接入可测试的门店范围请求协调逻辑',
    )
  })
  if (storeScopedRefreshFailure) failures.push(storeScopedRefreshFailure)

  const cartRefreshScopeFailure = await runTest('删除后的购物车刷新应拒绝切店与 ABA 的旧响应', () => {
    const body = extractFunctionBody(
      shopHomeSource,
      'const refreshCart = useCallback',
      'const ensureFullCart = useCallback',
    )
    assert(
      body.includes('const coordinator = dynamicDataStoreScopeCoordinatorRef.current') &&
        body.includes('const token = coordinator.capture(storeCode)') &&
        body.includes('if (!coordinator.isCurrent(token))') &&
        body.indexOf('if (!coordinator.isCurrent(token))') < body.indexOf('setCart(nextCart)'),
      '购物车刷新必须在 setCart 前校验门店 generation，阻止 S1→S2→S1 的旧响应覆盖',
    )
  })
  if (cartRefreshScopeFailure) failures.push(cartRefreshScopeFailure)

  const storeSwitchMutationStateFailure = await runTest('切店应清理旧门店商品操作状态和待提交定时器', () => {
    const body = extractFunctionBody(
      shopHomeSource,
      'useEffect(() => {\n    const nextStoreCode = selectedStore?.storeCode ?? null',
      'useEffect(() => {\n    return () => {',
    )
    assert(
      body.includes('Object.values(quantityUpdateTimersRef.current).forEach((timer) => window.clearTimeout(timer))') &&
        body.includes('quantityUpdateTimersRef.current = {}') &&
        body.includes('quantityUpdateVersionRef.current = {}') &&
        body.includes('setOptimisticCartQuantityMap({})') &&
        body.includes('setRemovingCartProductMap({})') &&
        body.includes('setQuantityLoadingMap({})'),
      '切店必须清空旧门店乐观数量、删除/加载状态并失效待提交数量更新',
    )
  })
  if (storeSwitchMutationStateFailure) failures.push(storeSwitchMutationStateFailure)

  const summaryCartFailure = await runTest('商城首页 summary-only 不应阻断当前页商品卡购物车状态', () => {
    assert(
      shopHomeSource.includes('const ensureFullCart = useCallback') &&
        shopHomeSource.includes('selectedStoreCodeRef.current !== storeCode') &&
        shopHomeSource.includes('cart && !cart.isSummaryOnly && cart.storeCode === storeCode') &&
        shopHomeSource.includes('cart?.isSummaryOnly || cart?.items.length') &&
        shopHomeSource.includes('summary 只服务首屏；需要明细交互时再补 full cart') &&
        shopHomeSource.includes('const fullCart = await ensureFullCart()') &&
        shopHomeSource.includes('fullCart?.items.find((item) => item.productCode === productCode)'),
      'summary-only 下缺少按需 full cart、防陈旧门店保护，或删除商品没有先补 detailGUID 明细',
    )
  })
  if (summaryCartFailure) failures.push(summaryCartFailure)

  const cartClearSyncFailure = await runTest('清空购物车后应清理卡片乐观状态和未提交数量更新', () => {
    assert(
      shopHomeSource.includes('if (cart?.isSummaryOnly || cart?.items.length) {\n      return\n    }') &&
        shopHomeSource.includes('Object.values(quantityUpdateTimersRef.current).forEach((timer) => window.clearTimeout(timer))') &&
        shopHomeSource.includes('quantityUpdateTimersRef.current = {}') &&
        shopHomeSource.includes('quantityUpdateVersionRef.current = {}') &&
        shopHomeSource.includes('setOptimisticCartQuantityMap({})') &&
        shopHomeSource.includes('setRemovingCartProductMap({})') &&
        shopHomeSource.includes('setQuantityLoadingMap({})') &&
        shopHomeSource.includes('[cart?.isSummaryOnly, cart?.items.length]'),
      '购物车清空后没有取消未提交数量更新，或没有清理商品卡乐观/删除中状态',
    )
  })
  if (cartClearSyncFailure) failures.push(cartClearSyncFailure)

  const cartOnlyI18nFailure = await runTest('购物车商品过滤文案应保持中英文同步', () => {
    assert(
      zhLocaleSource.includes('"cartProductsFilter": "购物车商品 ({{count}})"') &&
        zhLocaleSource.includes('"cartProductsTitle": "购物车商品"') &&
        zhLocaleSource.includes('"noCartProductsFound": "购物车暂无商品"') &&
        enLocaleSource.includes('"cartProductsFilter": "Cart Products ({{count}})"') &&
        enLocaleSource.includes('"cartProductsTitle": "Cart Products"') &&
        enLocaleSource.includes('"noCartProductsFound": "No products in cart"'),
      '购物车商品过滤缺少 zh/en 同步文案',
    )
  })
  if (cartOnlyI18nFailure) failures.push(cartOnlyI18nFailure)

  const searchCategoryPathFailure = await runTest('搜索商品卡片应显示分类完整路径', () => {
    assert(
      shopHomeSource.includes('buildWarehouseCategoryLookup') &&
        shopHomeSource.includes('getWarehouseProductCategoryTooltip') &&
        shopHomeSource.includes('const shouldShowCategoryPath = Boolean(keyword)') &&
        shopHomeSource.includes('categoryPath={categoryPathMap[product.productCode]}'),
      '搜索结果商品卡片未复用分类树路径工具，或未把分类完整路径传给 ProductCard',
    )
  })
  if (searchCategoryPathFailure) failures.push(searchCategoryPathFailure)

  const searchOnlyCategoryPathFailure = await runTest('分类路径只应显示在搜索结果卡片', () => {
    assert(
      shopHomeSource.includes('if (!shouldShowCategoryPath || !categoryLookup)') &&
        shopHomeSource.includes('return {}'),
      '分类路径缺少 keyword 开关，分类页或全部商品页可能也会显示路径',
    )
  })
  if (searchOnlyCategoryPathFailure) failures.push(searchOnlyCategoryPathFailure)

  const productCardCategoryPathFailure = await runTest('商品卡片应在货号下方以两行省略显示分类路径', () => {
    assert(
      productCardSource.includes('categoryPath?: string') &&
        productCardSource.includes('Tooltip') &&
        productCardSource.includes('shop-product-category-path') &&
        productCardSource.includes('ellipsis={{ rows: 2 }}') &&
        globalCssSource.includes('.shop-product-category-path'),
      'ProductCard 未声明 categoryPath，或分类路径没有 Tooltip/两行省略/稳定样式',
    )
  })
  if (productCardCategoryPathFailure) failures.push(productCardCategoryPathFailure)

  const productCardQuantityStepperFailure = await runTest('商品卡数量应默认 0 并使用 INNER 步进直接更新购物车', () => {
    assert(
      productCardSource.includes('onQuantityChange: (product: StoreOrderProductItem, quantity: number) => Promise<void> | void') &&
        productCardSource.includes('onAddToCart: (product: StoreOrderProductItem, quantity: number) => Promise<void> | void') &&
        productCardSource.includes('ShoppingCartOutlined') &&
        productCardSource.includes('const stepQuantity = product.minOrderQuantity > 0 ? product.minOrderQuantity : 1') &&
        productCardSource.includes('const cartQuantity = dynamicData?.cartQuantity ?? 0') &&
        productCardSource.includes('const [quantity, setQuantity] = useState<number>(0)') &&
        productCardSource.includes('setQuantity(cartQuantity)') &&
        productCardSource.includes('applyQuantityChange(quantity - stepQuantity)') &&
        productCardSource.includes('applyQuantityChange(quantity + stepQuantity)') &&
        productCardSource.includes('min={0}') &&
        productCardSource.includes('controls={false}') &&
        productCardSource.includes('disabled={removing || quantity <= 0}') &&
        productCardSource.includes('disabled={removing}') &&
        !productCardSource.includes('disabled={loading || quantity <= 0}') &&
        !productCardSource.includes('disabled={loading}\n                className="shop-product-quantity-input"') &&
        productCardSource.includes('onBlur={() => applyQuantityChange(quantity)}') &&
        productCardSource.includes('onPressEnter={() => applyQuantityChange(quantity)}'),
      '商品卡数量控件没有默认 0、没有按 INNER 步进、没有保留 Add，或没有直接提交数量变化',
    )
  })
  if (productCardQuantityStepperFailure) failures.push(productCardQuantityStepperFailure)

  const productCardAddVisibilityFailure = await runTest('商品卡应以固定槽位切换首次加购和删除', () => {
    assert(
      productCardSource.includes('cartQuantity > 0 ? (') &&
        productCardSource.includes('cartQuantity <= 0 ? (') &&
        productCardSource.includes('className="shop-product-card-action-slot shop-product-card-action-slot--left"') &&
        productCardSource.includes('className="shop-product-card-action-slot shop-product-card-action-slot--right"') &&
        productCardSource.includes('const addQuantity = quantity > 0 ? quantity : stepQuantity') &&
        productCardSource.includes('void onAddToCart(product, addQuantity)') &&
        productCardSource.includes('className="shop-product-cart-button"') &&
        productCardSource.includes('aria-label="Add product to cart"') &&
        productCardSource.includes('title="Add product to cart"') &&
        !productCardSource.includes('shop-product-card-actions--in-cart') &&
        !productCardSource.includes('>\n                  Add\n                </Button>'),
      '商品卡没有保持左右固定槽位，首次加购仍显示 Add 文字，或未补齐可访问性说明',
    )
  })
  if (productCardAddVisibilityFailure) failures.push(productCardAddVisibilityFailure)

  const productCardQuickPackFailure = await runTest('商品卡 2/3/4 份快捷按钮应设置总数量', () => {
    const quickPackCases = [
      { packCount: 2, stepQuantity: 1, currentQuantity: 9, expected: 2 },
      { packCount: 3, stepQuantity: 12, currentQuantity: 60, expected: 36 },
      { packCount: 4, stepQuantity: 12, currentQuantity: 0, expected: 48 },
    ]

    assert(
      quickPackCases.every(({ packCount, stepQuantity, expected }) => packCount * stepQuantity === expected) &&
        quickPackCases.some(({ currentQuantity, expected }) => currentQuantity > expected) &&
        productCardSource.includes('const handleQuickPackQuantity = (packCount: number) => {') &&
        productCardSource.includes('const quickQuantity = packCount * stepQuantity') &&
        productCardSource.includes('applyQuantityChange(quickQuantity)') &&
        productCardSource.includes('[2, 3, 4].map((packCount) =>') &&
        productCardSource.includes('onClick={() => handleQuickPackQuantity(packCount)}') &&
        productCardSource.includes('className="shop-product-quick-pack-button"') &&
        productCardSource.includes('aria-label={`Set total quantity to ${packCount} packs (${quickQuantity})`}') &&
        productCardSource.includes('title={`Set total quantity to ${packCount} packs (${quickQuantity})`}'),
      '2/3/4 份按钮未按 packCount * stepQuantity 设置总量、未连接点击处理，或缺少可访问性说明',
    )
  })
  if (productCardQuickPackFailure) failures.push(productCardQuickPackFailure)

  const productCardRemoveOptimisticFailure = await runTest('商品卡删除应先乐观退出已入车状态并防重复点击', () => {
    const body = extractFunctionBody(
      shopHomeSource,
      'const handleRemoveFromCart = useCallback',
      '  return (\n    <div className="shop-home-page">',
    )

    assert(
      productCardSource.includes('removing?: boolean') &&
        productCardSource.includes('removing = false') &&
        productCardSource.includes('if (removing)') &&
        shopHomeSource.includes('const [removingCartProductMap, setRemovingCartProductMap] = useState<Record<string, boolean>>({})') &&
        body.includes('if (removingCartProductMap[productCode])') &&
        body.includes('setRemovingCartProductMap((prev) => ({ ...prev, [productCode]: true }))') &&
        body.includes('setOptimisticCartQuantityMap((prev) => ({ ...prev, [productCode]: 0 }))') &&
        body.includes('const [cartRefreshResult] = await Promise.allSettled') &&
        body.includes("cartRefreshResult.status === 'fulfilled' && cartRefreshResult.value") &&
        body.includes('delete next[productCode]') &&
        shopHomeSource.includes('const isRemovingFromCart = Boolean(removingCartProductMap[product.productCode])') &&
        shopHomeSource.includes('const optimisticCartQuantity = isRemovingFromCart') &&
        shopHomeSource.includes('? 0') &&
        shopHomeSource.includes('removing={isRemovingFromCart}'),
      '商品卡删除没有先乐观置 0、缺少删除中去重 guard，或 ProductCard 未收到 removing 状态',
    )
  })
  if (productCardRemoveOptimisticFailure) failures.push(productCardRemoveOptimisticFailure)

  const optimisticDynamicDataFailure = await runTest('商城首页应把乐观购物车数量覆盖到商品卡 dynamicData', () => {
    assert(
        shopHomeSource.includes('const [optimisticCartQuantityMap, setOptimisticCartQuantityMap] = useState<Record<string, number>>({})') &&
        shopHomeSource.includes('const optimisticCartQuantity = isRemovingFromCart') &&
        shopHomeSource.includes(': optimisticCartQuantityMap[product.productCode]') &&
        shopHomeSource.includes('const syncedDynamicData: StoreOrderDynamicData =') &&
        shopHomeSource.includes('const cardDynamicData =') &&
        shopHomeSource.includes('? syncedDynamicData') &&
        shopHomeSource.includes('...syncedDynamicData') &&
        shopHomeSource.includes('cartQuantity: optimisticCartQuantity') &&
        shopHomeSource.includes('dynamicData={cardDynamicData}'),
      '商城首页没有把 optimisticCartQuantityMap 覆盖到 ProductCard dynamicData',
    )
  })
  if (optimisticDynamicDataFailure) failures.push(optimisticDynamicDataFailure)

  const productCardQuantityStyleFailure = await runTest('商品卡数量操作区应使用单一固定网格布局', () => {
    assert(
      productCardSource.includes('shop-product-quantity-stepper') &&
        productCardSource.includes('shop-product-quantity-button') &&
        productCardSource.includes('shop-product-quantity-input') &&
        globalCssSource.includes('grid-template-columns: 20px 20px minmax(24px, 1fr) 20px repeat(3, 20px) 20px') &&
        globalCssSource.includes('box-sizing: border-box') &&
        globalCssSource.includes('white-space: nowrap') &&
        globalCssSource.includes('.shop-product-quantity-stepper') &&
        globalCssSource.includes('display: contents') &&
        globalCssSource.includes('.shop-product-quantity-button') &&
        globalCssSource.includes('.shop-product-quantity-input') &&
        globalCssSource.includes('.shop-product-quick-pack-button') &&
        globalCssSource.includes('.shop-product-cart-button') &&
        !globalCssSource.includes('.shop-product-card-actions--in-cart') &&
        !globalCssSource.includes('.shop-product-add-button'),
      '商品卡数量操作区仍使用入车前后动态列宽，或缺少快捷按钮/图标槽样式',
    )
  })
  if (productCardQuantityStyleFailure) failures.push(productCardQuantityStyleFailure)

  const categoryPathClickFailure = await runTest('搜索商品分类路径点击后应进入对应分类并清除搜索词', () => {
    assert(
      shopHomeSource.includes('useNavigate') &&
        shopHomeSource.includes('const navigate = useNavigate()') &&
        shopHomeSource.includes('const handleCategoryPathClick = useCallback') &&
        shopHomeSource.includes('product.warehouseCategoryGUID') &&
        shopHomeSource.includes('navigate(`/shop?category=${encodeURIComponent(product.warehouseCategoryGUID)}`)') &&
        shopHomeSource.includes('shouldShowCategoryPath && product.warehouseCategoryGUID') &&
        shopHomeSource.includes('? handleCategoryPathClick') &&
        shopHomeSource.includes(': undefined'),
      '搜索商品分类路径没有点击进入分类、跳转没有清除 keyword，或缺少分类 GUID 时仍会显示可点击状态',
    )
  })
  if (categoryPathClickFailure) failures.push(categoryPathClickFailure)

  const productCardCategoryPathA11yFailure = await runTest('可点击分类路径应支持鼠标和键盘触发', () => {
    assert(
      productCardSource.includes('onCategoryPathClick?: (product: StoreOrderProductItem) => void') &&
        productCardSource.includes('const canClickCategoryPath = Boolean(categoryPath && onCategoryPathClick)') &&
        productCardSource.includes("role={canClickCategoryPath ? 'button' : undefined}") &&
        productCardSource.includes('tabIndex={canClickCategoryPath ? 0 : undefined}') &&
        productCardSource.includes("event.key === 'Enter' || event.key === ' '") &&
        productCardSource.includes('onCategoryPathClick(product)') &&
        globalCssSource.includes('.shop-product-category-path--clickable'),
      'ProductCard 分类路径缺少可点击 prop、键盘触发或可点击样式',
    )
  })
  if (productCardCategoryPathA11yFailure) failures.push(productCardCategoryPathA11yFailure)

  const lazyImageFailure = await runTest('商品卡图片应懒加载避免拖慢首屏', () => {
    assert(
      productCardSource.includes('loading="lazy"'),
      '商品卡图片未设置 loading="lazy"，首屏外图片会抢占首页加载资源',
    )
  })
  if (lazyImageFailure) failures.push(lazyImageFailure)

  const bestSellerLazyImageFailure = await runTest('热销商品表格图片应懒加载避免拖慢首屏', () => {
    assert(
      bestSellersSource.includes('loading="lazy"') &&
        bestSellersSource.includes('decoding="async"') &&
        bestSellersSource.includes('className="shop-best-sellers-image"') &&
        bestSellersSource.includes('shop-best-sellers-image-placeholder'),
      '热销商品表格图片缺少 lazy loading、异步解码或稳定占位，首屏外图片仍可能抢占加载资源',
    )
  })
  if (bestSellerLazyImageFailure) failures.push(bestSellerLazyImageFailure)

  const bestSellerVirtualFailure = await runTest('热销商品列表应使用 AntD Table 虚拟滚动', () => {
    assert(
        bestSellersSource.includes('import type { ColumnsType }') &&
        bestSellersSource.includes('Table') &&
        bestSellersSource.includes('virtual') &&
        bestSellersSource.includes('scroll={{ x: 1080, y: 560 }}') &&
        bestSellersSource.includes('className="shop-best-sellers-table"') &&
        bestSellersSource.includes('rowKey={(record) => record.productCode || record.itemNumber || String(record.rank)}') &&
        !bestSellersSource.includes('virtualWindow.visibleProducts.map') &&
        !bestSellersSource.includes('ResizeObserver') &&
        !bestSellersSource.includes('Badge.Ribbon'),
      '热销商品列表未切换到 AntD Table virtual，或仍保留手写 Card 虚拟列表',
    )
  })
  if (bestSellerVirtualFailure) failures.push(bestSellerVirtualFailure)

  const bestSellerAbortFailure = await runTest('热销商品切换筛选分页时应取消旧请求', () => {
    assert(
      bestSellersSource.includes('const controller = new AbortController()') &&
        bestSellersSource.includes('controller.signal') &&
        bestSellersSource.includes('controller.abort()') &&
        bestSellersSource.includes("fetchError instanceof DOMException && fetchError.name === 'AbortError'"),
      '热销商品请求缺少 AbortController，慢请求仍可能覆盖最新筛选结果',
    )
  })
  if (bestSellerAbortFailure) failures.push(bestSellerAbortFailure)

  const bestSellerDateRangeFailure = await runTest('热销商品默认日期范围应从昨天开始且使用本地业务日期', () => {
    assert(
      bestSellersSource.includes("import { buildBestSellerDateRange } from '../../../utils/bestSellerDateRange'") &&
        bestSellersSource.includes('buildBestSellerDateRange(timeRange)') &&
        !bestSellersSource.includes('toISOString().slice(0, 10)') &&
        !bestSellersSource.includes('start.setDate(start.getDate() - timeRange)'),
      '热销商品日期范围未复用本地业务日期工具，或仍用 toISOString/今天作为结束日期',
    )
  })
  if (bestSellerDateRangeFailure) failures.push(bestSellerDateRangeFailure)

  const bestSellerStatusNoticeFailure = await runTest('热销商品非 Fresh 状态应提示统计未就绪', () => {
    assert(
      bestSellersSource.includes('const isBestSellerStatisticFresh = statisticStatus ===') &&
        bestSellersSource.includes('bestSellerStatusNotice') &&
        bestSellersSource.includes('商品统计未就绪，请先生成商品统计。') &&
        bestSellersSource.includes('getBestSellerEmptyText()') &&
        bestSellersSource.includes('统计未就绪，暂未返回热销商品。'),
      '热销商品缺少非 Fresh 状态提示，或空数据文案没有区分统计未就绪和真无数据',
    )
  })
  if (bestSellerStatusNoticeFailure) failures.push(bestSellerStatusNoticeFailure)

  const bestSellerFailedRowsNoticeFailure = await runTest('热销商品 Failed 状态应同时保留商品行和格式化警告', () => {
    assert(
      bestSellersSource.includes("import { formatStatisticMessageAmounts } from '../../../utils/statisticMessage'") &&
        bestSellersSource.includes('const formattedStatisticMessage = useMemo(') &&
        bestSellersSource.includes('formatStatisticMessageAmounts(statisticMessage)') &&
        bestSellersSource.includes('formattedStatisticMessage ||') &&
        bestSellersSource.includes('<Tooltip title={formattedStatisticMessage}>') &&
        bestSellersSource.includes("type={statisticStatus === 'Failed' ? 'error' : 'warning'}") &&
        bestSellersSource.includes('message={bestSellerStatusNotice}') &&
        bestSellersSource.includes('dataSource={products}'),
      'Failed 状态没有同时保留商品行、红色警告，或 Tooltip/Alert 未使用精确格式化消息',
    )
  })
  if (bestSellerFailedRowsNoticeFailure) failures.push(bestSellerFailedRowsNoticeFailure)

  const bestSellerTableColumnsFailure = await runTest('热销商品表格应显示条码、复制、状态、分店销量和加购操作', () => {
    assert(
      bestSellersSource.includes('BarcodePreview') &&
        bestSellersSource.includes('CopyOutlined') &&
        bestSellersSource.includes('ShoppingCartOutlined') &&
        bestSellersSource.includes('Popover') &&
        bestSellersSource.includes("title: 'Gross Profit'") &&
        bestSellersSource.includes("title: 'Gross Margin'") &&
        bestSellersSource.includes("title: 'Stats'") &&
        bestSellersSource.includes('addStoreOrderCartItem') &&
        bestSellersSource.includes('setCart(nextCart)') &&
        !bestSellersSource.includes("title: 'Product Code'"),
      '热销商品表格列未包含条码、货号复制、分店销量弹层或加购操作，或仍显示 Product Code 列',
    )
  })
  if (bestSellerTableColumnsFailure) failures.push(bestSellerTableColumnsFailure)

  const bestSellerStatsAlwaysVisibleFailure = await runTest('热销商品 Stats 列应保留给所有订货前台用户', () => {
    assert(
      bestSellersSource.includes("title: 'Stats'") &&
        !bestSellersSource.includes("import { useAuthStore } from '../../../store/auth'") &&
        !bestSellersSource.includes('const isAdmin = useAuthStore') &&
        !bestSellersSource.includes('...(isAdmin ? ['),
      '热销商品 Stats 列不应按管理员条件隐藏，普通订货前台用户也要保留完整列',
    )
  })
  if (bestSellerStatsAlwaysVisibleFailure) failures.push(bestSellerStatsAlwaysVisibleFailure)

  const bestSellerBranchSalesFailure = await runTest('热销商品分店销量明细应按销量倒序展示', () => {
    assert(
        bestSellersSource.includes('function getBranchSalesRows(product: BestSellerProduct)') &&
        bestSellersSource.includes('function getBranchSalesCount(product: BestSellerProduct)') &&
        bestSellersSource.includes('return product.branchSalesCount ?? product.branchSales?.length ?? 0') &&
        bestSellersSource.includes('].sort((a, b) => (b.quantity ?? 0) - (a.quantity ?? 0))') &&
        bestSellersSource.includes("defaultSortOrder: 'descend'") &&
        bestSellersSource.includes("dataIndex: 'salesAmount'") &&
        bestSellersSource.includes("dataIndex: 'grossProfit'") &&
        bestSellersSource.includes("dataIndex: 'grossMarginRate'") &&
        bestSellersSource.includes('const count = getBranchSalesCount(record)') &&
        bestSellersSource.includes('shop-best-sellers-branch-sales-popover'),
      '分店销量明细缺少默认销量倒序排序、branchSalesCount 优先级或紧凑弹层',
    )
  })
  if (bestSellerBranchSalesFailure) failures.push(bestSellerBranchSalesFailure)

  const bestSellerAddGuardFailure = await runTest('热销商品加购只允许上架且已选分店的商品', () => {
    assert(
      bestSellersSource.includes('record.isActive !== true || !selectedStore?.storeCode') &&
        bestSellersSource.includes('disabled={disabled}') &&
        bestSellersSource.includes('getAddQuantity(product)') &&
        bestSellersSource.includes('message.warning(t(') &&
        bestSellersSource.includes('setCart(nextCart)'),
      '热销商品加购未限制上下架状态和分店，或未复用 cart/add 返回结果',
    )
  })
  if (bestSellerAddGuardFailure) failures.push(bestSellerAddGuardFailure)

  const bestSellerTableLayoutFailure = await runTest('热销商品表格应保留全列并使用紧凑列宽', () => {
    assert(
      bestSellersSource.includes("title: 'Rank'") &&
        bestSellersSource.includes("title: 'Image'") &&
        bestSellersSource.includes("title: 'Barcode'") &&
        bestSellersSource.includes("title: 'Item No.'") &&
        bestSellersSource.includes("title: 'Product Name'") &&
        bestSellersSource.includes("title: 'Units Sold'") &&
        bestSellersSource.includes("title: 'Sales Amount'") &&
        bestSellersSource.includes("title: 'Gross Profit'") &&
        bestSellersSource.includes("title: 'Gross Margin'") &&
        bestSellersSource.includes("title: 'Stats'") &&
        bestSellersSource.includes("title: 'Status'") &&
        bestSellersSource.includes("title: 'Stores Sold'") &&
        bestSellersSource.includes("title: 'Action'") &&
        bestSellersSource.includes('scroll={{ x: 1080, y: 560 }}') &&
        bestSellersSource.includes('width: 44') &&
        bestSellersSource.includes('width: 50') &&
        bestSellersSource.includes('width: 104') &&
        bestSellersSource.includes('width: 155') &&
        globalCssSource.includes('.shop-best-sellers-table') &&
        globalCssSource.includes('.shop-best-sellers-image-cell') &&
        globalCssSource.includes('width: 42px') &&
        globalCssSource.includes('height: 42px') &&
        globalCssSource.includes('.shop-best-sellers-barcode-cell') &&
        globalCssSource.includes('max-width: 98px') &&
        globalCssSource.includes('.shop-best-sellers-item-number') &&
        globalCssSource.includes('.shop-best-sellers-store-count') &&
        globalCssSource.includes('width: min(560px, calc(100vw - 32px))') &&
        globalCssSource.includes('max-width: calc(100vw - 32px)') &&
        globalCssSource.includes('.shop-best-sellers-product-name') &&
        globalCssSource.includes('max-height: calc(1.3em * 2)') &&
        globalCssSource.includes('-webkit-line-clamp: 2') &&
        globalCssSource.includes('.shop-best-sellers-rank') &&
        globalCssSource.includes('.shop-best-sellers-table .ant-table-cell') &&
        globalCssSource.includes('padding: 7px 6px !important') &&
        !globalCssSource.includes('.shop-best-sellers-virtual-list') &&
        !globalCssSource.includes('.shop-best-seller-card'),
      '热销商品表格未保留全列、紧凑列宽、响应式门店销量弹层和固定图片条码尺寸，或仍保留旧卡片样式',
    )
  })
  if (bestSellerTableLayoutFailure) failures.push(bestSellerTableLayoutFailure)

  const perfLogGateFailure = await runTest('首页性能 console 日志生产环境默认关闭', () => {
    const helperBody = extractFunctionBody(
      shopHomeSource,
      'function logShopHomePerf',
      'export default function ShopHomePage',
    )

    assert(
      helperBody.includes('import.meta.env.DEV') &&
        helperBody.includes("window.localStorage.getItem('shopHomePerf') === '1'") &&
        helperBody.includes('if (!isDebugEnabled)'),
      '首页性能 console 日志缺少开发环境或显式开关保护',
    )
  })
  if (perfLogGateFailure) failures.push(perfLogGateFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('shopHomeCartPerformance.logic.test: ok')
}

await main()
