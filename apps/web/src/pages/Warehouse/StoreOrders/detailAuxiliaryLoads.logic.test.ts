import { readFileSync } from 'node:fs'
import path from 'node:path'
import { resolveIsMobileViewport } from '../../../hooks/useIsMobile'
import { shouldShowDetailInitialLoading, shouldSkipDetailAutoReload } from '../../../utils/detailLoadState'
import { shouldLoadStoreOrderDetailPage, shouldShowStoreOrderDetailInitialLoading } from './detailLoadState'

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

const detailFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/Detail.tsx')
const pickingListFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/PickingList.tsx')
const invoiceFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/Invoice.tsx')
const containerDetailFile = path.resolve(process.cwd(), 'src/pages/Warehouse/ContainerDetail/index.tsx')
const localSupplierInvoiceEditFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit/index.tsx')
const detailLoadStateFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/detailLoadState.ts')
const sharedDetailLoadStateFile = path.resolve(process.cwd(), 'src/utils/detailLoadState.ts')
const zhFile = path.resolve(process.cwd(), 'src/i18n/locales/zh.json')
const enFile = path.resolve(process.cwd(), 'src/i18n/locales/en.json')

function readSource(file: string) {
  // 统一换行，避免 Windows CRLF 让源码契约断言误判。
  return readFileSync(file, 'utf8').replace(/\r\n/g, '\n')
}

const detailSource = readSource(detailFile)
const pickingListSource = readSource(pickingListFile)
const invoiceSource = readSource(invoiceFile)
const containerDetailSource = readSource(containerDetailFile)
const localSupplierInvoiceEditSource = readSource(localSupplierInvoiceEditFile)
const detailLoadStateSource = readSource(detailLoadStateFile)
const sharedDetailLoadStateSource = readSource(sharedDetailLoadStateFile)
const zhSource = readSource(zhFile)
const enSource = readSource(enFile)

async function main() {
  const failures: string[] = []

  const auxiliaryWarningFailure = await runTest('分店下拉加载失败应降级为非阻断提示', () => {
    assert(
      detailSource.includes("message.warning(t('storeOrders.detail.loadStoreOptionsFailed'"),
      'loadStores 失败时应使用非阻断 warning 文案，避免误提示整张订货明细失败',
    )
    assert(
      !detailSource.includes("message.error(error instanceof Error ? error.message : t('storeOrders.loadStoresFailed'))"),
      'loadStores 失败时不应直接透传后端错误 message',
    )
  })
  if (auxiliaryWarningFailure) failures.push(auxiliaryWarningFailure)

  const warehouseStaffStoreSelectorFailure = await runTest('仓库员工明细页不应请求完整分店下拉', () => {
    assert(
      detailSource.includes('if (!canUseWarehouseManagerActions)') &&
        detailSource.includes('setStores([])') &&
        detailSource.includes('lastLoadedStoresQueryKeyRef.current = storesQueryKey') &&
        detailSource.includes('return\n    }\n\n    setStoresLoading(true)'),
      '非仓库管理员应跳过完整分店下拉接口，避免 WarehouseStaff 因 /api/stores 403 看到分店显示失败',
    )
    assert(
      detailSource.includes('if (headerForm.storeCode && !options.some((item) => item.value === headerForm.storeCode))') &&
        detailSource.includes('const currentStoreLabel = detail?.storeName') &&
        detailSource.includes('`${detail.storeName} (${headerForm.storeCode})`') &&
        detailSource.includes("`${headerForm.storeCode} (${t('column.currentStore')})`"),
      '分店下拉跳过后应优先使用明细接口返回的 storeName 显示当前订单分店',
    )
    assert(
      !detailSource.includes('userGUID: canViewAllStores ? undefined : currentUser?.userGUID'),
      '详情页不应继续为仓库员工请求按用户过滤的完整分店下拉',
    )
  })
  if (warehouseStaffStoreSelectorFailure) failures.push(warehouseStaffStoreSelectorFailure)

  const translationFailure = await runTest('分店下拉非阻断提示应有中英文文案', () => {
    assert(
      zhSource.includes('"loadStoreOptionsFailed": "分店下拉加载失败，订单明细可继续查看"'),
      '中文文案缺少分店下拉非阻断提示',
    )
    assert(
      enSource.includes('"loadStoreOptionsFailed": "Store selector failed to load. Order details remain available."'),
      '英文文案缺少分店下拉非阻断提示',
    )
  })
  if (translationFailure) failures.push(translationFailure)

  const detailSaveTranslationFailure = await runTest('订货明细保存和金额显示应有中英文文案', () => {
    assert(zhSource.includes('"saveEditedLines": "整单保存"'), '中文文案缺少整单保存')
    assert(enSource.includes('"saveEditedLines": "Save All Lines"'), '英文文案缺少整单保存')
    assert(zhSource.includes('"importPriceSyncConfirmTitle": "确认同步进口价"'), '中文文案缺少进口价同步确认标题')
    assert(enSource.includes('"importPriceSyncConfirmTitle": "Confirm Import Price Sync"'), '英文文案缺少进口价同步确认标题')
    assert(
      zhSource.includes('"importPriceSyncConfirmContent": "进口价保存后会同步写入仓库商品表和分店表，请确认是否继续。"'),
      '中文文案缺少进口价同步确认内容',
    )
    assert(
      enSource.includes('"importPriceSyncConfirmContent": "After saving, import prices will sync to warehouse products and store products. Continue?"'),
      '英文文案缺少进口价同步确认内容',
    )
    assert(zhSource.includes('"syncImportPriceCheckbox": "同步进口价到仓库商品表和分店表"'), '中文文案缺少同步进口价勾选项')
    assert(enSource.includes('"syncImportPriceCheckbox": "Sync import price to warehouse products and store products"'), '英文文案缺少同步进口价勾选项')
    assert(zhSource.includes('"orderAmountLabel": "预计销售额"'), '中文订单金额标签应改为预计销售额')
    assert(enSource.includes('"orderAmountLabel": "Estimated Sales"'), '英文订单金额标签应改为 Estimated Sales')
    assert(zhSource.includes('"importAmountLabel": "发货金额 ex GST"'), '中文进口金额标签应改为发货金额 ex GST')
    assert(enSource.includes('"importAmountLabel": "Allocated Amount ex GST"'), '英文进口金额标签应改为 Allocated Amount ex GST')
    assert(zhSource.includes('"gstAmountLabel": "GST 10%"'), '中文文案缺少 GST 10%')
    assert(enSource.includes('"gstAmountLabel": "GST 10%"'), '英文文案缺少 GST 10%')
  })
  if (detailSaveTranslationFailure) failures.push(detailSaveTranslationFailure)

  const editabilityStateFailure = await runTest('详情页应复用订单状态权限派生函数', () => {
    assert(
      detailSource.includes("import { deriveStoreOrderDetailPermissions } from './storeOrderDetailPermissions'") &&
        detailSource.includes('} = deriveStoreOrderDetailPermissions(detail?.flowStatus)'),
      '详情页尚未复用 deriveStoreOrderDetailPermissions 派生状态权限',
    )
  })
  if (editabilityStateFailure) failures.push(editabilityStateFailure)

  const editGuardFailure = await runTest('不可编辑订单的写入口应先走统一 guard', () => {
    assert(
      detailSource.includes('function ensureOrderEditable') &&
        detailSource.includes("message.warning(t('storeOrders.detail.orderReadonlyRefresh'))") &&
        detailSource.includes('if (!ensureOrderEditable())') &&
        detailSource.includes('handleSaveLine') &&
        detailSource.includes('handleConfirmPaste'),
      '详情页写操作尚未统一拦截不可编辑订单',
    )
  })
  if (editGuardFailure) failures.push(editGuardFailure)

  const flowGuardFailure = await runTest('状态流转写入口应有函数内二次门禁', () => {
    assert(
      detailSource.includes('if (!canUseWarehouseManagerActions || !canStartPicking)') &&
        detailSource.includes('if (!canUseWarehouseManagerActions || !canCompleteOrder)') &&
        detailSource.includes("message.warning(t('storeOrders.detail.orderReadonlyRefresh'))"),
      '开始配货/完成订单函数入口尚未按仓库管理员权限和状态二次拦截',
    )
  })
  if (flowGuardFailure) failures.push(flowGuardFailure)

  const completeOrderOutboundDateFailure = await runTest('详情页完成订单应只在出库日期为空时补当天', () => {
    const completeOrderSource = detailSource.slice(
      detailSource.indexOf('const handleCompleteOrder'),
      detailSource.indexOf('const handleChangeOrderStatus'),
    )

    assert(detailSource.includes('function formatLocalDateForInput'), '详情页应提供本地日期格式化 helper，避免 UTC 日期偏移')
    assert(!detailSource.includes('completeStoreOrder,'), '详情页完成订单不应再导入直接完成接口')
    assert(!completeOrderSource.includes('completeStoreOrder(detail.orderGUID)'), '详情页完成订单不应直接调用完成接口')
    assert(
      completeOrderSource.includes('const currentOutboundDate = headerForm.outboundDate?.slice(0, 10)') &&
        completeOrderSource.includes('const nextOutboundDate = currentOutboundDate || formatLocalDateForInput()') &&
        completeOrderSource.includes('updateStoreOrderOutboundDate({') &&
        completeOrderSource.includes('outboundDate: nextOutboundDate') &&
        completeOrderSource.includes('completeOrder: true'),
      '完成订单应复用出库日期接口：已有出库日期则保留，空出库日期才补当天并同步完成订单',
    )
  })
  if (completeOrderOutboundDateFailure) failures.push(completeOrderOutboundDateFailure)

  const disabledUiFailure = await runTest('非仓库管理员应禁用表头和明细写控件，仅保留 WarehouseStaff 只读配货单入口', () => {
    // 重设计：配货单/发票/状态流转按钮从订单头和明细卡挪到概况卡右侧，权限开关不变。
    const overviewActionsSource = detailSource.slice(
      detailSource.indexOf('<div className="wh-order-detail-overview-actions">'),
      detailSource.indexOf('<ol className="wh-order-detail-steps"'),
    )
    const pickingButtonPosition = overviewActionsSource.indexOf("t('storeOrders.pickingList')")
    const pickingButtonSource = overviewActionsSource.slice(
      overviewActionsSource.lastIndexOf('<Button', pickingButtonPosition),
      pickingButtonPosition,
    )
    const managerGuardPosition = overviewActionsSource.lastIndexOf('{canUseWarehouseManagerActions ? (', pickingButtonPosition)
    const managerGuardClosePosition = overviewActionsSource.lastIndexOf(') : null}', pickingButtonPosition)
    const extraGuardPosition = overviewActionsSource.lastIndexOf('{canUseStoreOrderDetailExtraActions ? (', pickingButtonPosition)

    assert(
      detailSource.includes('disabled={!canUseWarehouseManagerActions || isReadonlyOrder}') &&
        detailSource.includes('disabled={!canUseWarehouseManagerActions || isReadonlyOrder || validPastePreviewCount === 0}') &&
        detailSource.includes('disabled={isReadonlyOrder || !canStartPicking}') &&
        detailSource.includes('disabled={!canCompleteOrder}') &&
        // 开始配货 / 完成订单 / ⋯（更改状态）只给订货管理者：主操作由权限派生，⋯ 菜单在管理员开关内。
        detailSource.includes('const flowActions = resolveStoreOrderDetailFlowActions(detail?.flowStatus, canUseWarehouseManagerActions)') &&
        overviewActionsSource.includes("flowActions.primary === 'startPicking' ? (") &&
        overviewActionsSource.includes('{canUseWarehouseManagerActions ? (\n                  <Dropdown') &&
        detailSource.includes('const canUseWarehouseManagerActions = access.canManageWarehouseOrders && !isWarehouseStaffOnly') &&
        detailSource.includes('const canUseStoreOrderDocumentActions = access.isWarehouseStaff') &&
        detailSource.includes('const canUseStoreOrderDetailExtraActions = canUseWarehouseManagerActions || canUseStoreOrderDocumentActions') &&
        extraGuardPosition >= 0 &&
        extraGuardPosition < pickingButtonPosition &&
        pickingButtonSource.includes('navigate(`/warehouse/store-order/picking/${detail.orderGUID}`)') &&
        managerGuardPosition <= managerGuardClosePosition &&
        /rowSelection=\{\s*canUseWarehouseManagerActions/.test(detailSource),
      '详情页尚未按仓库管理员权限禁用写控件，或未为 WarehouseStaff 保留只读配货单入口',
    )
  })
  if (disabledUiFailure) failures.push(disabledUiFailure)

  const statusChangeFailure = await runTest('详情页应提供三状态订单状态更改入口', () => {
    assert(
      detailSource.includes('updateStoreOrderStatus') &&
        detailSource.includes('handleChangeOrderStatus') &&
        detailSource.includes('orderStatusChangeOptions') &&
        detailSource.includes('StoreOrderFlowStatus.Submitted') &&
        detailSource.includes('StoreOrderFlowStatus.Picking') &&
        detailSource.includes('StoreOrderFlowStatus.Completed') &&
        detailSource.includes("t('storeOrders.detail.changeOrderStatus'") &&
        detailSource.includes("t('storeOrders.detail.statusChangeSuccess'"),
      '详情页尚未提供三状态订单状态更改入口',
    )
  })
  if (statusChangeFailure) failures.push(statusChangeFailure)

  const keepAliveSkipAutoReloadFailure = await runTest('详情页 Tab 切回已有数据时应跳过自动刷新', () => {
    assert(
      detailSource.includes('loadedDetailIdRef') &&
        detailSource.includes('useKeepAliveContext') &&
        detailSource.includes('const { active } = useKeepAliveContext()') &&
        detailSource.includes("import { useIsMobile } from '../../../hooks/useIsMobile'") &&
        detailSource.includes('const isMobileLayout = useIsMobile()') &&
        detailSource.includes('const canLoadDetail = shouldLoadStoreOrderDetailPage({') &&
        detailSource.includes('if (!canLoadDetail) return') &&
        detailSource.includes('visibleDetailIdRef') &&
        detailSource.includes('lastLoadedDetailQueryKeyRef') &&
        detailSource.includes('shouldSkipDetailAutoReload({') &&
        detailSource.includes('shouldShowStoreOrderDetailInitialLoading({') &&
        detailSource.includes('canLoadDetail,') &&
        detailSource.includes('return () => {') &&
        detailSource.includes('detailRequestControllerRef.current?.abort()'),
      '详情页缺少移动布局加载门禁或桌面 KeepAlive active 守卫',
    )
    assert(
      detailSource.includes('loadedDetailIdRef.current = result.orderGUID || id') &&
        detailSource.includes('visibleDetailIdRef.current = result.orderGUID || id') &&
        detailSource.includes('lastLoadedDetailQueryKeyRef.current = detailQueryKey'),
      '详情页加载成功后应记录已加载订单 id 和查询参数，后续同订单同查询才能跳过自动刷新',
    )
  })
  if (keepAliveSkipAutoReloadFailure) failures.push(keepAliveSkipAutoReloadFailure)

  const mobileDetailLoadGateFailure = await runTest('390px 移动布局无 KeepAlive Provider 时也应加载订货明细', () => {
    const isMobileLayout = resolveIsMobileViewport({
      width: 390,
      height: 844,
      coarsePointer: true,
    })

    assert(
      shouldLoadStoreOrderDetailPage({
        keepAliveActive: false,
        isMobileLayout,
      }),
      '移动布局直接渲染页面时应忽略 KeepAlive 默认 active=false，避免永久停在 idle Spin',
    )
    assert(
      shouldLoadStoreOrderDetailPage({
        keepAliveActive: true,
        isMobileLayout: false,
      }),
      '桌面当前激活 Tab 应继续加载订货明细',
    )
    assert(
      !shouldLoadStoreOrderDetailPage({
        keepAliveActive: false,
        isMobileLayout: false,
      }),
      '桌面隐藏 KeepAlive Tab 仍应阻止详情请求',
    )
  })
  if (mobileDetailLoadGateFailure) failures.push(mobileDetailLoadGateFailure)

  const initialLoadingDecisionFailure = await runTest('详情页初始加载和自动刷新跳过判断应覆盖切回和换单边界', () => {
    assert(
      sharedDetailLoadStateSource.includes('loadedDetailId !== requestedDetailId || visibleDetailId !== requestedDetailId') &&
        sharedDetailLoadStateSource.includes('export function shouldSkipDetailAutoReload') &&
        detailLoadStateSource.includes('shouldShowDetailInitialLoading'),
      '初始加载和自动刷新跳过判断应沉到通用 helper，并同时检查已加载记录和当前可展示记录',
    )
    assert(
      !shouldShowDetailInitialLoading({
        requestedDetailId: 'detail-a',
        loadedDetailId: 'detail-a',
        visibleDetailId: 'detail-a',
      }) &&
      !shouldShowStoreOrderDetailInitialLoading({
        requestedOrderId: 'order-a',
        loadedOrderId: 'order-a',
        visibleDetailId: 'order-a',
      }),
      '同订单且当前仍有可展示明细时应静默刷新',
    )
    assert(
      shouldSkipDetailAutoReload({
        requestedDetailId: 'detail-a',
        loadedDetailId: 'detail-a',
        visibleDetailId: 'detail-a',
      }),
      '同详情且当前仍有可展示内容时应跳过自动刷新',
    )
    assert(
      shouldShowStoreOrderDetailInitialLoading({
        requestedOrderId: 'order-b',
        loadedOrderId: 'order-a',
        visibleDetailId: 'order-a',
      }),
      '切到新订单时应显示首次主加载',
    )
    assert(
      shouldShowStoreOrderDetailInitialLoading({
        requestedOrderId: 'order-a',
        loadedOrderId: 'order-a',
        visibleDetailId: null,
      }),
      '当前没有可展示明细时即使已加载标记命中也应显示主加载',
    )
    assert(
      shouldShowStoreOrderDetailInitialLoading({
        requestedOrderId: 'order-a',
        loadedOrderId: 'order-a',
        visibleDetailId: 'order-b',
      }),
      '当前可展示明细属于其他订单时应显示主加载，避免短暂展示错误订单状态',
    )
    assert(
      !shouldSkipDetailAutoReload({
        requestedDetailId: 'detail-b',
        loadedDetailId: 'detail-a',
        visibleDetailId: 'detail-a',
      }) &&
      !shouldSkipDetailAutoReload({
        requestedDetailId: '',
        loadedDetailId: 'detail-a',
        visibleDetailId: 'detail-a',
      }) &&
      !shouldSkipDetailAutoReload({
        requestedDetailId: 'detail-a',
        loadedDetailId: 'detail-a',
        visibleDetailId: null,
      }),
      '换详情、空 id 或没有可展示内容时不应跳过自动刷新',
    )
    assert(
      shouldSkipDetailAutoReload({
        requestedDetailId: 'detail-a',
        loadedDetailId: 'detail-a',
        visibleDetailId: 'detail-a',
        requestedDetailQueryKey: '{"pageNumber":1}',
        loadedDetailQueryKey: '{"pageNumber":1}',
      }) &&
      !shouldSkipDetailAutoReload({
        requestedDetailId: 'detail-a',
        loadedDetailId: 'detail-a',
        visibleDetailId: 'detail-a',
        requestedDetailQueryKey: '{"pageNumber":2}',
        loadedDetailQueryKey: '{"pageNumber":1}',
      }),
      '门店订单详情查询参数一致才应跳过自动刷新，分页搜索排序变化必须重新请求',
    )
  })
  if (initialLoadingDecisionFailure) failures.push(initialLoadingDecisionFailure)

  const silentFailurePreserveFailure = await runTest('详情页静默刷新失败不应清空当前明细', () => {
    assert(
      detailSource.includes("const errorMessage = error instanceof Error ? error.message : t('storeOrders.detail.loadDetailFailed')") &&
        detailSource.includes('if (showLoading)') &&
        detailSource.includes('setDetail(null)') &&
        detailSource.includes("setDetailLoadStatus('error')") &&
        detailSource.includes('setDetailErrorMessage(errorMessage)') &&
        detailSource.includes('message.error(errorMessage)'),
      '静默刷新失败时应保留旧 detail，只提示错误；首次加载失败才进入 error 空态',
    )
  })
  if (silentFailurePreserveFailure) failures.push(silentFailurePreserveFailure)

  const storeOrderPrintPagesKeepAliveFailure = await runTest('配货单和发票 Tab 切回已有数据时应跳过自动刷新', () => {
    for (const [pageName, source, loadFailureKey] of [
      ['配货单', pickingListSource, 'warehouse.pickingList.loadFailed'],
      ['发票', invoiceSource, 'warehouse.invoice.loadFailed'],
    ] as const) {
      assert(
        source.includes("import { shouldSkipDetailAutoReload } from '../../../utils/detailLoadState'") &&
          source.includes('loadedOrderIdRef') &&
          source.includes('visibleOrderIdRef') &&
          source.includes('const load = async (showLoading = true)') &&
          source.includes('if (showLoading) {') &&
          source.includes('setLoading(true)') &&
          source.includes('shouldSkipDetailAutoReload({') &&
          source.includes('return'),
        `${pageName}缺少同订单 Tab 恢复跳过自动刷新保护`,
      )
      assert(
        source.includes('loadedOrderIdRef.current = detail.orderGUID || id') &&
          source.includes('visibleOrderIdRef.current = detail.orderGUID || id') &&
          source.includes(`const errorMessage = error instanceof Error ? error.message : t('${loadFailureKey}')`) &&
          source.includes('if (showLoading) {') &&
          source.includes('setOrder(null)') &&
          source.includes('setStore(null)'),
        `${pageName}应在成功后记录可展示订单，且首次加载失败才清空当前内容`,
      )
    }
    assert(
      pickingListSource.includes("import { useKeepAliveContext } from 'keepalive-for-react'") &&
        pickingListSource.includes('const { active } = useKeepAliveContext()') &&
        pickingListSource.includes('const activeRef = useRef(active)') &&
        pickingListSource.includes('activeRef.current = active') &&
        pickingListSource.includes('if (!active) return') &&
        pickingListSource.includes('let cancelled = false') &&
        pickingListSource.includes('if (cancelled || !activeRef.current) return') &&
        pickingListSource.includes('cancelled = true') &&
        pickingListSource.includes('}, [active, id])'),
      '配货单页缺少 KeepAlive active/异步结果守卫，隐藏 Tab 不应跟随全局路由加载或继续打印',
    )
  })
  if (storeOrderPrintPagesKeepAliveFailure) failures.push(storeOrderPrintPagesKeepAliveFailure)

  const warehouseStaffPickingStoreLoadFailure = await runTest('配货单页 WarehouseStaff 不应请求完整分店下拉', () => {
    assert(
      pickingListSource.includes("import { useAuthStore } from '../../../store/auth'") &&
        pickingListSource.includes('const { access } = useAuthStore()') &&
        pickingListSource.includes('const canUseWarehouseManagerActions = access.canManageWarehouseOrders && !isWarehouseStaffOnly') &&
        pickingListSource.includes('if (detail.storeCode && canUseWarehouseManagerActions)') &&
        pickingListSource.includes('WarehouseStaff 无需加载完整分店下拉') &&
        pickingListSource.includes('store?.storeName || order.storeName || order.storeCode'),
      '配货单页应跳过 WarehouseStaff 的完整分店接口请求，并使用订单详情中的 storeName/storeCode 展示',
    )
  })
  if (warehouseStaffPickingStoreLoadFailure) failures.push(warehouseStaffPickingStoreLoadFailure)

  const warehouseStaffPickingPrintFailure = await runTest('配货单页 WarehouseStaff 打印下载默认不触发状态流转写接口，显式授予开始配货权限后才触发', () => {
    const beforePrintSource = pickingListSource.slice(
      pickingListSource.indexOf('const handleBeforePrint = async () => {'),
      pickingListSource.indexOf('const handlePrint = async () => {'),
    )

    assert(
      beforePrintSource.includes('WarehouseStaff 可打印/下载配货单') &&
        // 是否推进状态统一由 shouldStartPickingBeforePrint 决定：管理类账号沿用原行为，
        // 纯仓库员工必须被显式授予 Warehouse.StartPicking（见 pickingListLogic.test.ts）。
        beforePrintSource.includes('shouldStartPickingBeforePrint({') &&
        beforePrintSource.includes('canUseWarehouseManagerActions,') &&
        beforePrintSource.includes('hasStartPickingPermission,') &&
        pickingListSource.includes('access.hasPermission(P.Warehouse.StartPicking)') &&
        beforePrintSource.includes('await startPickingStoreOrder(order.orderGUID)') &&
        beforePrintSource.includes('if (!activeRef.current)') &&
        beforePrintSource.includes('loadedOrderIdRef.current = null') &&
        pickingListSource.includes('await handleBeforePrint()') &&
        pickingListSource.includes('await printElementPagesAsPdf') &&
        pickingListSource.includes('await downloadElementPagesAsPdf'),
      '配货单打印/下载前只有管理类账号或被显式授予开始配货权限的账号可自动开始配货，且切换 Tab 后不能继续生成旧页面 PDF',
    )
  })
  if (warehouseStaffPickingPrintFailure) failures.push(warehouseStaffPickingPrintFailure)

  const lowRiskDetailPagesKeepAliveFailure = await runTest('低风险详情页 Tab 切回应保留已有内容并跳过自动刷新', () => {
    assert(
      containerDetailSource.includes("import { shouldShowDetailInitialLoading, shouldSkipDetailAutoReload } from '../../../utils/detailLoadState'") &&
        containerDetailSource.includes('useKeepAliveContext') &&
        containerDetailSource.includes('const { active } = useKeepAliveContext()') &&
        containerDetailSource.includes('if (!active || !currentUserGuid) return') &&
        containerDetailSource.includes('loadedContainerGuidRef') &&
        containerDetailSource.includes('visibleContainerGuidRef') &&
        containerDetailSource.includes('lastLoadedContainerDetailSuccessRef') &&
        containerDetailSource.includes('const loadData = async (showLoading = true)') &&
        containerDetailSource.includes('shouldSkipDetailAutoReload({') &&
        containerDetailSource.includes("const activeLoadQueryKey = detailLoadMode === 'probe'") &&
        containerDetailSource.includes('lastLoadedContainerDetailSuccessRef.current?.containerGuid === containerGuid') &&
        containerDetailSource.includes('lastLoadedContainerDetailSuccessRef.current.queryKey === activeLoadQueryKey') &&
        containerDetailSource.includes('void loadHeader(shouldShowInitialLoading)') &&
        containerDetailSource.includes("void loadDetailRows(detailLoadMode === 'paged' ? 'paged' : 'probe')") &&
        containerDetailSource.includes('loadedContainerGuidRef.current = containerGuid') &&
        containerDetailSource.includes('visibleContainerGuidRef.current = containerGuid') &&
        /lastLoadedContainerDetailSuccessRef\.current = \{\s*containerGuid,\s*queryKey: pagedDetailQueryKey,\s*generation: currentRequestId,\s*\}/.test(containerDetailSource),
      '货柜详情缺少 KeepAlive active 守卫或明细查询条件缓存保护',
    )
    assert(
      containerDetailSource.includes('setDetailTableRenderKey((value) => value + 1)') &&
        containerDetailSource.includes('detailTableRef.current?.scrollTo?.({ top: scrollTop })') &&
        containerDetailSource.indexOf('setDetailTableRenderKey((value) => value + 1)') >
          containerDetailSource.indexOf('if (!active || wasActive || rows.length === 0)') &&
        containerDetailSource.indexOf("loadDetailChunk(1, 'reset')") <
          containerDetailSource.indexOf('setDetailTableRenderKey((value) => value + 1)'),
      '货柜明细 Tab 切回已有数据时应只恢复虚拟表格测量，不能通过重新加载明细修复空白',
    )
    // 原只读详情页已并入进货单明细页（同一页面按权限显示只读视图），下面一条断言覆盖两种身份。
    assert(
      localSupplierInvoiceEditSource.includes("import { shouldShowDetailInitialLoading, shouldSkipDetailAutoReload } from '../../../../utils/detailLoadState'") &&
        localSupplierInvoiceEditSource.includes('loadedInvoiceGuidRef') &&
        localSupplierInvoiceEditSource.includes('visibleInvoiceGuidRef') &&
        localSupplierInvoiceEditSource.includes('const loadInvoice = useCallback(async (showLoading = true)') &&
        localSupplierInvoiceEditSource.includes('shouldSkipDetailAutoReload({') &&
        localSupplierInvoiceEditSource.includes('loadedInvoiceGuidRef.current = invoiceGuid') &&
        localSupplierInvoiceEditSource.includes('visibleInvoiceGuidRef.current = invoiceGuid'),
      '进货单明细页（含只读视图）缺少同发票 Tab 恢复跳过自动刷新保护',
    )
  })
  if (lowRiskDetailPagesKeepAliveFailure) failures.push(lowRiskDetailPagesKeepAliveFailure)

  const readonlyCopyFailure = await runTest('只读状态应提供中英文提示文案', () => {
    assert(
      zhSource.includes('"orderReadonlyTitle": "当前订单为只读状态"') &&
        zhSource.includes('"orderReadonlyDescription": "已完成订单不可编辑，请更改状态后再操作。但仍可补录或修正出库日期。"') &&
        zhSource.includes('"orderReadonlyRefresh": "当前订单状态不可编辑，请刷新确认状态。"'),
      '中文文案缺少订单只读提示',
    )
    assert(
      enSource.includes('"orderReadonlyTitle": "Order is read-only"') &&
        enSource.includes('"orderReadonlyDescription": "Completed orders cannot be edited. Change the status before editing. The outbound date can still be corrected."') &&
        enSource.includes('"orderReadonlyRefresh": "The current order status is not editable. Please refresh and confirm the status."'),
      '英文文案缺少订单只读提示',
    )
  })
  if (readonlyCopyFailure) failures.push(readonlyCopyFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('detailAuxiliaryLoads.logic.test: ok')
}

await main()
